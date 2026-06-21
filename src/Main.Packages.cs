using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Elements;
using Newtonsoft.Json;
using ExtendedLSC.ManualTransmission;
using ExtendedLSC.WheelFitment;
using ExtendedLSC.WindowTint;

namespace ExtendedLSC
{
    public partial class Main : Script
    {
        #region Vehicle Packages (Side Course)

        private string PackagesDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "Packages");

        /// <summary>
        /// Create menu for saving/loading vehicle mod packages (Side Course feature)
        /// </summary>
        private NativeMenu CreatePackagesMenu()
        {
            var menu = CreateMenu("PACKAGES");

            // Save current mods option
            var saveItem = new NativeItem("Save Current Mods");
            saveItem.Description = "Save all current modifications to a package file (you'll be asked to name it)";
            saveItem.Activated += (s, e) =>
            {
                BeginNamePackage();
            };
            menu.Add(saveItem);

            menu.Add(new NativeSeparatorItem());

            // Load saved packages
            LoadPackageItems(menu);

            // Preview-on-hover: snapshot the car's mods when the menu opens, live-preview the highlighted package,
            // commit on select, and revert to the snapshot on close (unless a package was committed).
            menu.Shown += (s, e) => { CaptureVehicleModSnapshot(); _packageCommitted = false; PreviewSelectedPackage(); };
            menu.SelectedIndexChanged += (s, e) => PreviewSelectedPackage();
            menu.Closed += (s, e) => { if (!_packageCommitted) RestoreVehicleModSnapshot(); };

            return menu;
        }

        /// <summary>
        /// Load package files and add them to the menu
        /// </summary>
        private void LoadPackageItems(NativeMenu menu)
        {
            _packageItemPaths.Clear();   // rebuilding the list -> drop stale item->path mappings
            try
            {
                if (!Directory.Exists(PackagesDirectory))
                {
                    var noPackagesItem = new NativeItem("No saved packages");
                    noPackagesItem.Enabled = false;
                    menu.Add(noPackagesItem);
                    return;
                }

                string vehicleModel = currentVehicle?.Model.ToString() ?? "";
                string[] packageFiles = Directory.GetFiles(PackagesDirectory, "*.json");

                int packagesFound = 0;
                foreach (string file in packageFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);

                    // Package format: VehicleModel_PackageName.json
                    // Only show packages for this vehicle model (or universal packages starting with "All_")
                    if (!fileName.StartsWith(vehicleModel + "_") && !fileName.StartsWith("All_"))
                        continue;

                    packagesFound++;
                    string displayName = fileName.Contains("_") ? fileName.Substring(fileName.IndexOf("_") + 1) : fileName;
                    string filePath = file; // Capture for closure

                    var packageItem = new NativeItem(displayName);
                    // Status, mirroring other ELSC items: this car's last-applied package -> equipped (garage) icon;
                    // else if you own every part in it (offset $0) -> owned tick; else show the cost you'd pay.
                    string equippedPkg = currentVehicle != null ? VehicleSaveData.GetEquippedPackage(VehicleKey(currentVehicle)) : null;
                    bool isEquipped = string.Equals(equippedPkg, displayName, StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        var pkg = LoadPackageData(filePath);
                        if (pkg == null) { packageItem.AltTitle = ">>"; packageItem.Description = $"Couldn't read {displayName}."; }
                        else
                        {
                            int offset = ComputePackageOffset(pkg);
                            if (isEquipped)
                            {
                                packageItem.AltTitle = ""; itemOwnershipStatus[packageItem] = STATUS_INSTALLED;
                                packageItem.Description = $"Equipped. Hover to preview; select to re-apply.";
                            }
                            else if (offset > 0)
                            {
                                packageItem.AltTitle = $"${offset:N0}";
                                packageItem.Description = $"Hover to preview; select to buy & apply (${offset:N0} for parts you don't own).";
                            }
                            else
                            {
                                packageItem.AltTitle = ""; itemOwnershipStatus[packageItem] = STATUS_OWNED;
                                packageItem.Description = $"Owned. Hover to preview; select to apply (free).";
                            }
                        }
                    }
                    catch { packageItem.AltTitle = ">>"; }
                    _packageItemPaths[packageItem] = filePath;   // for hover preview + X-to-delete
                    packageItem.Activated += (s, e) =>
                    {
                        LoadVehiclePackage(filePath);
                    };
                    menu.Add(packageItem);
                }

                if (packagesFound == 0)
                {
                    var noPackagesItem = new NativeItem("No packages for this vehicle");
                    noPackagesItem.Enabled = false;
                    menu.Add(noPackagesItem);
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading packages: {ex.Message}");
                var errorItem = new NativeItem("Error loading packages");
                errorItem.Enabled = false;
                menu.Add(errorItem);
            }
        }

        /// <summary>
        /// Save current vehicle mods to a package file
        /// </summary>
        // Prompt the user to name the package (on-screen keyboard), then SaveVehiclePackage(name).
        private void BeginNamePackage()
        {
            if (isNamingPackage) return;
            if (currentVehicle == null || !currentVehicle.Exists())
            {
                ShowNotification("~r~No vehicle to save!");
                return;
            }
            isNamingPackage = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this package (same name overwrites)");
        }

        private void UpdateNamePackage()
        {
            if (!isNamingPackage) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingPackage = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Package not saved (empty name)."); return; }
                SaveVehiclePackage(result);
            }
            else if (status == 2) { isNamingPackage = false; }   // cancelled
        }

        // Turn a user-typed package name into a safe filename token (so the same name maps to the same file).
        private string SanitizePackageName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append((char.IsLetterOrDigit(c) || c == ' ' || c == '-') ? c : '_');
            string clean = sb.ToString().Trim();
            return string.IsNullOrEmpty(clean) ? "Package" : clean;
        }

        private void SaveVehiclePackage(string packageName)
        {
            if (currentVehicle == null || !currentVehicle.Exists())
            {
                ShowNotification("~r~No vehicle to save!");
                return;
            }

            try
            {
                // Create packages directory if needed
                if (!Directory.Exists(PackagesDirectory))
                    Directory.CreateDirectory(PackagesDirectory);

                string vehicleModel = currentVehicle.Model.ToString();
                string safeName = SanitizePackageName(packageName);
                // Same vehicle + same name => identical path => File.WriteAllText overwrites the old package.
                string fileName = $"{vehicleModel}_{safeName}.json";
                string filePath = Path.Combine(PackagesDirectory, fileName);
                bool overwrite = File.Exists(filePath);

                // Capture a complete, shareable snapshot of EVERYTHING ELSC can track, then serialize with Json.NET.
                var pkg = CaptureCurrentPackage();
                File.WriteAllText(filePath, JsonConvert.SerializeObject(pkg, Formatting.Indented));

                // You built this car, so you OWN everything in the package you just saved — mark it owned for THIS
                // car (so re-applying it is free, $0 offset) and flag it as the car's equipped build (the car
                // currently IS this snapshot). Without this, parts not bought through ELSC's buy flow (e.g. a
                // externally-modded or spawned car) would read as un-owned and wrongly charge on re-apply.
                MarkPackageOwned(pkg);
                VehicleSaveData.SetEquippedPackage(VehicleKey(currentVehicle), safeName);

                ShowNotification(overwrite ? $"~g~Package overwritten: {safeName}" : $"~g~Package saved: {safeName}");
                Log($"Package saved to: {filePath}");

                // Refresh the Packages submenu in place so the new/updated package shows in the load list.
                RefreshPackagesMenu();
            }
            catch (Exception ex)
            {
                ShowNotification("~r~Failed to save package!");
                Log($"Error saving package: {ex.Message}");
            }
        }

        // Rebuild the Packages submenu's items (Save + separator + saved packages) without re-creating the menu.
        private void RefreshPackagesMenu()
        {
            if (packagesMenu == null) return;
            packagesMenu.Clear();

            var saveItem = new NativeItem("Save Current Mods");
            saveItem.Description = "Save all current modifications to a package file (you'll be asked to name it)";
            saveItem.Activated += (s, e) => { BeginNamePackage(); };
            packagesMenu.Add(saveItem);

            packagesMenu.Add(new NativeSeparatorItem());
            LoadPackageItems(packagesMenu);
        }

        // Toggle-type mod slots are driven by TOGGLE_VEHICLE_MOD, not SET_VEHICLE_MOD.
        private static bool IsToggleModSlot(int slot) => slot >= 17 && slot <= 22;

        // Neon natives (raw hashes, mirroring the ones used in CreateNeonColorMenuFromConfig).
        private const ulong NEON_IS_ENABLED   = 0x8C4B92553E4766A5;  // _IS_VEHICLE_NEON_LIGHT_ENABLED(veh, index)
        private const ulong NEON_GET_COLOUR   = 0x7619EEE8C886757F;  // _GET_VEHICLE_NEON_LIGHTS_COLOUR(veh, &r,&g,&b)
        private const ulong NEON_SET_ENABLED  = 0x2AA720E4287BF269;  // _SET_VEHICLE_NEON_LIGHT_ENABLED(veh, index, on)
        private const ulong NEON_SET_COLOUR   = 0x8E0A582209A62695;  // _SET_VEHICLE_NEON_LIGHTS_COLOUR(veh, r,g,b)

        #region Vehicle Package — data model

        /// <summary>
        /// A complete, shareable build snapshot of everything ELSC can track for one vehicle.
        /// VISUAL fields are applied for hover-preview AND restore; ELSC fields are applied only on commit.
        /// </summary>
        private class VehiclePackageData
        {
            public string vehicle;          // model token
            public string created;          // timestamp

            // ---- VISUAL (native, applied on preview + restore) ----
            public Dictionary<int, int> mods = new Dictionary<int, int>();  // non-toggle slots 0-48, value>=0
            public int wheelType;
            public bool turbo, xenon, tireSmoke;
            public int primaryColor, secondaryColor, pearlColor, rimColor;
            public int primaryCustomArgb, secondaryCustomArgb;  // 0 = not a custom colour
            public int tireSmokeArgb;
            public bool neonLeft, neonRight, neonFront, neonBack;
            public int neonArgb;
            public int windowTint;
            public int livery = -1;
            public string plateText;
            public int plateStyle;
            public List<int> extras = new List<int>();   // extra ids that are ON (1-14)

            // ---- ELSC (applied only on commit) ----
            public VehicleSaveData.FitmentData fitment;
            public bool wheelFitmentOwned, customSuspensionEquipped;
            public VehicleSaveData.TuningData tuning;
            public bool mtOwned, mtEquipped;
            public int nosOwnedTiers, nosEquippedTier = -1;
            public string engineSwapId;
            public int customWindowColor;   // AARRGGBB (0 = none)
            public string speedoConfig;     // global speedo look (style/units/per-style colours) JSON, from Speedo.ExportConfig
        }

        #endregion

        #region Vehicle Package — capture

        /// <summary>Build a complete package snapshot from the live currentVehicle. Each native/getter group is
        /// wrapped so one failure can't abort the rest.</summary>
        private VehiclePackageData CaptureCurrentPackage()
        {
            var p = new VehiclePackageData
            {
                vehicle = currentVehicle != null ? currentVehicle.Model.ToString() : "",
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            if (currentVehicle == null || !currentVehicle.Exists()) return p;
            var v = currentVehicle;
            string vn = VehicleKey(v);

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch (Exception ex) { Log($"[Pkg] mod kit: {ex.Message}"); }

            // Mods (non-toggle slots only)
            try
            {
                for (int i = 0; i <= 48; i++)
                {
                    if (IsToggleModSlot(i)) continue;
                    int val = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, i);
                    if (val >= 0) p.mods[i] = val;
                }
            }
            catch (Exception ex) { Log($"[Pkg] mods: {ex.Message}"); }

            try { p.wheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, v); } catch (Exception ex) { Log($"[Pkg] wheelType: {ex.Message}"); }

            // Toggle mods
            try { p.turbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 18); } catch (Exception ex) { Log($"[Pkg] turbo: {ex.Message}"); }
            try { p.xenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 22); } catch (Exception ex) { Log($"[Pkg] xenon: {ex.Message}"); }
            try { p.tireSmoke = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 20); } catch (Exception ex) { Log($"[Pkg] tireSmoke: {ex.Message}"); }

            // Colours
            try { unsafe { int a, b; Function.Call(Hash.GET_VEHICLE_COLOURS, v, &a, &b); p.primaryColor = a; p.secondaryColor = b; } }
            catch (Exception ex) { Log($"[Pkg] colours: {ex.Message}"); }
            try { unsafe { int pe, rm; Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, v, &pe, &rm); p.pearlColor = pe; p.rimColor = rm; } }
            catch (Exception ex) { Log($"[Pkg] extraColours: {ex.Message}"); }

            // Custom RGB colours
            try
            {
                if (Function.Call<bool>(Hash.GET_IS_VEHICLE_PRIMARY_COLOUR_CUSTOM, v))
                    unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, &r, &g, &b); p.primaryCustomArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] customPrimary: {ex.Message}"); }
            try
            {
                if (Function.Call<bool>(Hash.GET_IS_VEHICLE_SECONDARY_COLOUR_CUSTOM, v))
                    unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, &r, &g, &b); p.secondaryCustomArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] customSecondary: {ex.Message}"); }

            // Tyre smoke colour
            try { unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, v, &r, &g, &b); p.tireSmokeArgb = PackRgb(r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] tyreSmokeColor: {ex.Message}"); }

            // Neon
            try
            {
                p.neonLeft  = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 0);
                p.neonRight = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 1);
                p.neonFront = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 2);
                p.neonBack  = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 3);
                unsafe { int r, g, b; Function.Call((Hash)NEON_GET_COLOUR, v, &r, &g, &b); p.neonArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] neon: {ex.Message}"); }

            try { p.windowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, v); } catch (Exception ex) { Log($"[Pkg] windowTint: {ex.Message}"); }
            try { p.livery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, v); } catch (Exception ex) { Log($"[Pkg] livery: {ex.Message}"); }
            try { p.plateText = Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v); } catch (Exception ex) { Log($"[Pkg] plateText: {ex.Message}"); }
            try { p.plateStyle = Function.Call<int>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v); } catch (Exception ex) { Log($"[Pkg] plateStyle: {ex.Message}"); }

            // Extras (1-14)
            try
            {
                for (int i = 1; i <= 14; i++)
                    if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v, i) && Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, v, i))
                        p.extras.Add(i);
            }
            catch (Exception ex) { Log($"[Pkg] extras: {ex.Message}"); }

            // ---- ELSC state (from save data) ----
            try
            {
                // Prefer the LIVE stance — so the snapshot restores the actual current stance and a save captures
                // exactly what's on the car — falling back to saved fitment data when the controller isn't bound.
                if (wheelFitment != null && wheelFitment.IsInitialized && lastFitmentVehicle == currentVehicle)
                    p.fitment = new VehicleSaveData.FitmentData
                    {
                        FrontCamber = wheelFitment.FrontCamber, RearCamber = wheelFitment.RearCamber,
                        FrontTrackWidth = wheelFitment.FrontTrackWidth, RearTrackWidth = wheelFitment.RearTrackWidth,
                        FrontHeight = wheelFitment.FrontHeight, RearHeight = wheelFitment.RearHeight,
                        Rake = wheelFitment.Rake, StockFake = wheelFitment.StockFake,
                        VisualSize = wheelFitment.VisualSize, VisualWidth = wheelFitment.VisualWidth
                    };
                else p.fitment = VehicleSaveData.GetFitmentData(vn);
            }
            catch (Exception ex) { Log($"[Pkg] fitment: {ex.Message}"); }
            try { p.wheelFitmentOwned = VehicleSaveData.IsWheelFitmentOwned(vn); } catch (Exception ex) { Log($"[Pkg] fitmentOwned: {ex.Message}"); }
            try { p.customSuspensionEquipped = VehicleSaveData.IsCustomSuspensionEquipped(vn); } catch (Exception ex) { Log($"[Pkg] customSusp: {ex.Message}"); }
            try { p.tuning = VehicleSaveData.GetTuningData(vn); } catch (Exception ex) { Log($"[Pkg] tuning: {ex.Message}"); }
            try { p.mtOwned = VehicleSaveData.IsManualTransmissionOwned(vn); } catch (Exception ex) { Log($"[Pkg] mtOwned: {ex.Message}"); }
            try { p.mtEquipped = VehicleSaveData.IsManualTransmissionEquipped(vn); } catch (Exception ex) { Log($"[Pkg] mtEquipped: {ex.Message}"); }
            try { p.nosOwnedTiers = VehicleSaveData.GetNosOwnedTiers(vn); } catch (Exception ex) { Log($"[Pkg] nosOwned: {ex.Message}"); }
            try { p.nosEquippedTier = VehicleSaveData.GetNosEquippedTier(vn); } catch (Exception ex) { Log($"[Pkg] nosEquipped: {ex.Message}"); }
            try { p.engineSwapId = VehicleSaveData.GetEngineSwapId(vn); } catch (Exception ex) { Log($"[Pkg] engineSwap: {ex.Message}"); }
            try { p.customWindowColor = VehicleSaveData.GetCustomWindowColor(vn); } catch (Exception ex) { Log($"[Pkg] customWindow: {ex.Message}"); }
            try { p.speedoConfig = Speedo.ExportConfig(); } catch (Exception ex) { Log($"[Pkg] speedo: {ex.Message}"); }   // global speedo look (style/units/colours)

            return p;
        }

        // ARGB pack with full alpha; RGB unpackers.
        private static int PackRgb(int r, int g, int b) => unchecked((int)(0xFF000000u | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF)));
        private static void UnpackRgb(int argb, out int r, out int g, out int b) { r = (argb >> 16) & 0xFF; g = (argb >> 8) & 0xFF; b = argb & 0xFF; }

        /// <summary>Deserialize a package file. Returns null on failure (e.g. an old hand-rolled JSON file).</summary>
        private VehiclePackageData LoadPackageData(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<VehiclePackageData>(json);
            }
            catch (Exception ex) { Log($"[Pkg] load '{filePath}': {ex.Message}"); return null; }
        }

        #endregion

        #region Vehicle Package — apply visual

        /// <summary>Apply ONLY the visual (native) fields of a package to the current vehicle. Used for hover
        /// preview AND restore. Each group is wrapped so one failure can't abort the rest.</summary>
        private void ApplyPackageVisual(VehiclePackageData p, bool livePreview = false)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            var v = currentVehicle;

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch (Exception ex) { Log($"[Pkg] apply mod kit: {ex.Message}"); }

            try { if (p.wheelType >= 0) Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, v, p.wheelType); } catch (Exception ex) { Log($"[Pkg] apply wheelType: {ex.Message}"); }

            // Mods: write every non-toggle slot. Slots absent from the package are reset to stock (-1) so a
            // preview/restore doesn't leave parts from the previous state behind.
            try
            {
                for (int i = 0; i <= 48; i++)
                {
                    if (IsToggleModSlot(i)) continue;
                    int val = (p.mods != null && p.mods.TryGetValue(i, out int mv)) ? mv : -1;
                    Function.Call(Hash.SET_VEHICLE_MOD, v, i, val, false);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply mods: {ex.Message}"); }

            try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 18, p.turbo); } catch (Exception ex) { Log($"[Pkg] apply turbo: {ex.Message}"); }
            try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 22, p.xenon); } catch (Exception ex) { Log($"[Pkg] apply xenon: {ex.Message}"); }

            try
            {
                Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 20, p.tireSmoke);
                if (p.tireSmoke && p.tireSmokeArgb != 0)
                {
                    UnpackRgb(p.tireSmokeArgb, out int r, out int g, out int b);
                    Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v, r, g, b);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply tireSmoke: {ex.Message}"); }

            try { Function.Call(Hash.SET_VEHICLE_COLOURS, v, p.primaryColor, p.secondaryColor); } catch (Exception ex) { Log($"[Pkg] apply colours: {ex.Message}"); }
            try { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v, p.pearlColor, p.rimColor); } catch (Exception ex) { Log($"[Pkg] apply extraColours: {ex.Message}"); }

            try { if (p.primaryCustomArgb != 0) { UnpackRgb(p.primaryCustomArgb, out int r, out int g, out int b); Function.Call(Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] apply customPrimary: {ex.Message}"); }
            try { if (p.secondaryCustomArgb != 0) { UnpackRgb(p.secondaryCustomArgb, out int r, out int g, out int b); Function.Call(Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] apply customSecondary: {ex.Message}"); }

            // Neon
            try
            {
                if (p.neonArgb != 0) { UnpackRgb(p.neonArgb, out int r, out int g, out int b); Function.Call((Hash)NEON_SET_COLOUR, v, r, g, b); }
                Function.Call((Hash)NEON_SET_ENABLED, v, 0, p.neonLeft);
                Function.Call((Hash)NEON_SET_ENABLED, v, 1, p.neonRight);
                Function.Call((Hash)NEON_SET_ENABLED, v, 2, p.neonFront);
                Function.Call((Hash)NEON_SET_ENABLED, v, 3, p.neonBack);
            }
            catch (Exception ex) { Log($"[Pkg] apply neon: {ex.Message}"); }

            // Window tint — incl. the ELSC custom glass colour. During a live hover preview, drive it through the
            // tint manager's exclusive PREVIEW slot so its per-frame AssertCar loop doesn't fight us; on restore/
            // commit, release the preview so AssertCar re-asserts the car's real (saved) colour.
            try
            {
                if (livePreview && windowTint != null && windowTint.CanPreview)
                {
                    windowTint.BeginPreview(v);
                    if (p.customWindowColor != 0) windowTint.PreviewColor(v, p.customWindowColor);
                    else Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, p.windowTint);
                }
                else
                {
                    if (windowTint != null) windowTint.EndPreview();
                    Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, p.windowTint);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply windowTint: {ex.Message}"); }
            try { if (p.livery >= 0) Function.Call(Hash.SET_VEHICLE_LIVERY, v, p.livery); } catch (Exception ex) { Log($"[Pkg] apply livery: {ex.Message}"); }
            // NOTE: deliberately do NOT apply p.plateText. The plate TEXT is the car's per-instance identity (the
            // VehicleKey). Cloning the package author's plate onto the car would change its identity and orphan the
            // ownership of any OTHER packages bought for this car. We keep the car's own plate; only the cosmetic
            // plate STYLE (background) travels with the package.
            try { Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v, p.plateStyle); } catch (Exception ex) { Log($"[Pkg] apply plateStyle: {ex.Message}"); }

            // Extras (1-14): turn on the ones in the package, off otherwise. SET_VEHICLE_EXTRA's flag is INVERTED.
            try
            {
                for (int i = 1; i <= 14; i++)
                {
                    if (!Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v, i)) continue;
                    bool on = p.extras != null && p.extras.Contains(i);
                    Function.Call(Hash.SET_VEHICLE_EXTRA, v, i, !on);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply extras: {ex.Message}"); }

            // Stance/fitment (camber, ride height, rake, track, wheel size/width) — push the package's values onto
            // the live fitment controller so the preview/restore shows the actual stance, not just the wheel design.
            // Done after the wheel mod/type above so VisualSize/Width have the right rims to scale. The per-frame
            // wheelFitment.Update() applies these; restore writes the snapshot's values back the same way.
            try
            {
                if (p.fitment != null && wheelFitment != null)
                {
                    if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle) InitFitmentForVehicle();
                    if (wheelFitment.IsInitialized)
                    {
                        wheelFitment.FrontCamber = p.fitment.FrontCamber;
                        wheelFitment.RearCamber = p.fitment.RearCamber;
                        wheelFitment.FrontTrackWidth = p.fitment.FrontTrackWidth;
                        wheelFitment.RearTrackWidth = p.fitment.RearTrackWidth;
                        wheelFitment.FrontHeight = p.fitment.FrontHeight;
                        wheelFitment.RearHeight = p.fitment.RearHeight;
                        wheelFitment.Rake = p.fitment.Rake;
                        wheelFitment.StockFake = p.fitment.StockFake;
                        wheelFitment.VisualSize = p.fitment.VisualSize;
                        wheelFitment.VisualWidth = p.fitment.VisualWidth;
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply fitment: {ex.Message}"); }

            // Speedometer look (global: style/units/colours). Apply IN-MEMORY (no file write) so a hover preview /
            // restore can show it without persisting; on a live preview, set Speedo.Preview to the package's active
            // style so the menu's demo gauge renders it. Restore (livePreview=false, p=snapshot) reverts + clears
            // the preview; the actual persist happens in ApplyPackageElsc on commit.
            try
            {
                if (!string.IsNullOrEmpty(p.speedoConfig))
                {
                    Speedo.ImportConfig(p.speedoConfig, persist: false);
                    Speedo.Preview = livePreview ? Speedo.ActiveStyleOf(p.speedoConfig) : null;
                }
                else if (!livePreview) Speedo.Preview = null;
            }
            catch (Exception ex) { Log($"[Pkg] apply speedo: {ex.Message}"); }
        }

        #endregion

        #region Vehicle Package — apply ELSC features (commit only)

        /// <summary>Apply the package's ELSC features (tuning, MT, custom suspension/fitment, NOS, engine swap,
        /// custom window glass). Writes into VehicleSaveData via its setters then re-uses the existing apply
        /// paths. Each feature wrapped so one failure can't abort the rest.</summary>
        private void ApplyPackageElsc(VehiclePackageData p)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);

            // Wheel fitment / custom suspension
            try
            {
                if (p.fitment != null && (p.wheelFitmentOwned || p.customSuspensionEquipped))
                {
                    VehicleSaveData.SetFitmentData(vn, p.fitment);
                    VehicleSaveData.SetWheelFitmentOwned(vn, true);
                    // Mirror InitFitmentForVehicle: push the saved values onto the live fitment controller.
                    if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle)
                        InitFitmentForVehicle();
                    if (wheelFitment.IsInitialized)
                    {
                        wheelFitment.FrontCamber = p.fitment.FrontCamber;
                        wheelFitment.RearCamber = p.fitment.RearCamber;
                        wheelFitment.FrontTrackWidth = p.fitment.FrontTrackWidth;
                        wheelFitment.RearTrackWidth = p.fitment.RearTrackWidth;
                        wheelFitment.FrontHeight = p.fitment.FrontHeight;
                        wheelFitment.RearHeight = p.fitment.RearHeight;
                        wheelFitment.Rake = p.fitment.Rake;
                        wheelFitment.StockFake = p.fitment.StockFake;
                        wheelFitment.VisualSize = p.fitment.VisualSize;
                        wheelFitment.VisualWidth = p.fitment.VisualWidth;
                    }
                    if (p.customSuspensionEquipped) EquipCustomSuspension();   // drops vanilla level, flags equipped, re-applies ride height
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc fitment: {ex.Message}"); }

            // Engine swap (resolve id -> EngineSwap)
            try
            {
                if (!string.IsNullOrEmpty(p.engineSwapId))
                {
                    var swap = EngineSwaps.Lookup(p.engineSwapId);
                    if (swap != null) { ApplyEngineSwap(swap); VehicleSaveData.SetEngineSwapId(vn, swap.Id); }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc engineSwap: {ex.Message}"); }

            // Manual transmission
            try
            {
                if (p.mtOwned)
                {
                    VehicleSaveData.SetManualTransmissionOwned(vn, true);
                    if (p.mtEquipped && ELSCTransmission.IsAvailable) EquipManualTransmission();   // sets equipped + enables shifting + rebuilds
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc mt: {ex.Message}"); }

            // Nitrous
            try
            {
                if (p.nosOwnedTiers != 0)
                {
                    VehicleSaveData.SetNosOwnedTiers(vn, p.nosOwnedTiers);
                    VehicleSaveData.SetNosEquippedTier(vn, p.nosEquippedTier);
                    if (elscTransmission != null)
                    {
                        elscTransmission.NosInstalled = p.nosEquippedTier >= 0;
                        if (p.nosEquippedTier >= 0) elscTransmission.ActiveFxIndex = p.nosEquippedTier;
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc nos: {ex.Message}"); }

            // Handling tune
            try
            {
                if (p.tuning != null)
                {
                    VehicleSaveData.SetTuningData(vn, p.tuning);
                    currentTuning = p.tuning;
                    InitTuningForVehicle();   // caches stock + applies the tune
                    ApplyTuning();
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc tuning: {ex.Message}"); }

            // Custom window glass colour
            try
            {
                if (p.customWindowColor != 0 && windowTint != null && windowTint.Ready)
                    windowTint.ApplyCustomColor(currentVehicle, p.customWindowColor);
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc windowGlass: {ex.Message}"); }

            // Speedometer look (global) — persist it now (the active style is applied only if owned). Also record
            // the package's style PER-CAR so the gauge sticks on this car (and only this car).
            try
            {
                if (!string.IsNullOrEmpty(p.speedoConfig))
                {
                    Speedo.Preview = null;
                    Speedo.ImportConfig(p.speedoConfig, persist: true);
                    if (currentVehicle != null && currentVehicle.Exists())
                    {
                        VehicleSaveData.SetSpeedoStyle(VehicleKey(currentVehicle), (int)Speedo.Active);
                        SyncSpeedoToCar(currentVehicle);
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc speedo: {ex.Message}"); }

            try { VehicleSaveData.Save(); } catch (Exception ex) { Log($"[Pkg] applyElsc save: {ex.Message}"); }
        }

        #endregion

        #region Vehicle Package — cost offset

        /// <summary>Total cost of every part in the package that the player does NOT already own for this
        /// vehicle (owned => 0). Approximate where the buy flow doesn't track per-part ownership.</summary>
        private int ComputePackageOffset(VehiclePackageData p)
        {
            if (ModSettings.AllItemsFree) return 0;   // INI: everything's free
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return 0;
            string vn = VehicleKey(currentVehicle);
            int total = 0;

            // Mods (cosmetic + performance). value 0 on a cosmetic slot is usually "stock part" — only charge
            // for installed (>=0) parts the player doesn't already own.
            try
            {
                if (p.mods != null)
                    foreach (var kv in p.mods)
                    {
                        int slot = kv.Key, val = kv.Value;
                        if (val < 0) continue;
                        if (!VehicleSaveData.IsModOwned(vn, slot, val))
                            total += ModPricing.GetModPriceByIndex(slot, val);
                    }
            }
            catch (Exception ex) { Log($"[Pkg] offset mods: {ex.Message}"); }

            // Wheels (slot 23 value) — charge per the wheel category if a non-stock design is set and unowned.
            try
            {
                if (p.mods != null && p.mods.TryGetValue(23, out int wIdx) && wIdx >= 0)
                {
                    int ownKey = WheelOwnKey(p.wheelType, wIdx);
                    if (!VehicleSaveData.IsModOwned(vn, 23, ownKey))
                        total += ModPricing.GetWheelPrice(p.wheelType, wIdx);
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset wheels: {ex.Message}"); }

            // Toggle mods
            try { if (p.turbo && !VehicleSaveData.IsTurboOwned(vn)) total += ModPricing.TurboPrice; } catch (Exception ex) { Log($"[Pkg] offset turbo: {ex.Message}"); }
            try { if (p.xenon && !VehicleSaveData.IsXenonOwned(vn)) total += ModPricing.XenonLightsPrice; } catch (Exception ex) { Log($"[Pkg] offset xenon: {ex.Message}"); }

            // Window tint (preset index)
            try
            {
                if (p.windowTint > 0 && !VehicleSaveData.IsTintOwned(vn, p.windowTint))
                {
                    int ti = p.windowTint;
                    total += (ti >= 0 && ti < ModPricing.WindowTintPrices.Length) ? ModPricing.WindowTintPrices[ti] : 0;
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset tint: {ex.Message}"); }

            // Plate style
            try
            {
                if (p.plateStyle >= 0 && !VehicleSaveData.IsPlateOwned(vn, p.plateStyle))
                    total += (p.plateStyle < ModPricing.PlatePrices.Length) ? ModPricing.PlatePrices[p.plateStyle] : 0;
            }
            catch (Exception ex) { Log($"[Pkg] offset plate: {ex.Message}"); }

            // Livery (slot 48 in ELSC's ownership map)
            try
            {
                if (p.livery >= 0 && !VehicleSaveData.IsModOwned(vn, 48, p.livery))
                    total += ModPricing.GetModPriceByIndex(48, p.livery);
            }
            catch (Exception ex) { Log($"[Pkg] offset livery: {ex.Message}"); }

            // ---- ELSC features ----
            try { if ((p.wheelFitmentOwned || p.customSuspensionEquipped) && !VehicleSaveData.IsWheelFitmentOwned(vn)) total += ModPricing.CustomSuspensionPrice; } catch (Exception ex) { Log($"[Pkg] offset susp: {ex.Message}"); }
            try { if (p.mtOwned && !VehicleSaveData.IsManualTransmissionOwned(vn)) total += ModPricing.ManualTransmissionPrice; } catch (Exception ex) { Log($"[Pkg] offset mt: {ex.Message}"); }

            // NOS tiers: charge each owned tier in the package the player doesn't already own.
            try
            {
                int have = VehicleSaveData.GetNosOwnedTiers(vn);
                for (int tier = 0; tier < ModPricing.NosTierPrices.Length; tier++)
                {
                    bool inPkg = (p.nosOwnedTiers & (1 << tier)) != 0;
                    bool owned = (have & (1 << tier)) != 0;
                    if (inPkg && !owned) total += ModPricing.NosTierPrices[tier];
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset nos: {ex.Message}"); }

            // Engine swap (no per-vehicle ownership tracking — charge unless this swap is already installed here).
            try
            {
                if (!string.IsNullOrEmpty(p.engineSwapId))
                {
                    var swap = EngineSwaps.Lookup(p.engineSwapId);
                    if (swap != null && VehicleSaveData.GetEngineSwapId(vn) != swap.Id) total += swap.Price;
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset engineSwap: {ex.Message}"); }

            return total;
        }

        #endregion

        #region Vehicle Package — commit, snapshot, preview

        /// <summary>Commit a package: charge the cost offset (parts not yet owned), mark everything owned, then
        /// apply the full visual + ELSC build. Aborts (and reverts to snapshot) if the player can't afford it.</summary>
        private void LoadVehiclePackage(string filePath)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) { ShowNotification("~r~No vehicle!"); return; }

            var p = LoadPackageData(filePath);
            if (p == null) { ShowNotification("~r~Failed to load package!"); RestoreVehicleModSnapshot(); return; }

            string vn = VehicleKey(currentVehicle);
            int offset = ComputePackageOffset(p);

            // Charge (or block). TryPurchase returns false if it can't afford OR we're in edit mode — abort cleanly.
            if (offset > 0 && !TryPurchase(offset, false))
            {
                RestoreVehicleModSnapshot();   // undo any hover preview
                return;
            }
            else if (offset == 0 && EditBlockApply())   // free package still blocked while editing
            {
                RestoreVehicleModSnapshot();
                return;
            }

            _packageCommitted = true;   // keep it on close (don't revert the preview)

            // The car KEEPS its own plate (identity). We do NOT apply the package author's plate text (see
            // ApplyPackageVisual) and we no longer randomize it — so the car's per-instance key stays STABLE and
            // ownership ACCUMULATES across every package you buy for this car (buying a second leaves the first
            // owned, re-equippable for free). Apply ELSC features before deriving the final key, since the custom
            // window glass can re-plate the car on a collision; write ownership/equipped under that same final key.
            ApplyPackageVisual(p);
            ApplyPackageElsc(p);
            vn = VehicleKey(currentVehicle);   // FINAL per-instance key (after any window-glass re-plate)
            MarkPackageOwned(p);

            // Re-seat the wheels ONCE so the fitment re-derives a clean natural baseline before the stance settles
            // (prevents the apply double-stacking into an oscillating suspension). Only when the package has a
            // non-stock stance — a plain visual package doesn't need it.
            bool hasStance = p.fitment != null && (p.wheelFitmentOwned || p.customSuspensionEquipped);
            if (hasStance) ReseatWheelsForFitment();

            // Remember it as this car's equipped package (drives the garage marker) + refresh the list markers.
            VehicleSaveData.SetEquippedPackage(vn, PackageDisplayName(filePath));
            RefreshPackagesMenu();

            if (offset > 0) ShowNotification($"~g~Package applied~w~ — ${offset:N0} charged");
            else ShowNotification("~g~Package applied~w~ — all parts owned");
            Log($"Package committed from: {filePath} (offset ${offset})");
        }

        // Friendly package name from its file path: "{model}_{Name}.json" -> "Name".
        private string PackageDisplayName(string filePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            return fileName.Contains("_") ? fileName.Substring(fileName.IndexOf("_") + 1) : fileName;
        }

        // ---- Delete a package: press X on a package row -> "are you sure?" prompt -> delete the file ----
        private NativeMenu _pkgDeleteMenu;
        private NativeItem _pkgDeleteItem;       // the "Delete" row (its title is re-stamped per target)
        private string _pkgDeleteTarget, _pkgDeleteName;

        // Called every frame while the LSC menu is up: X on a highlighted package opens the confirm prompt.
        private void CheckPackageDeleteInput()
        {
            if (packagesMenu == null || !packagesMenu.Visible) return;
            if (isWalkAroundActive) return;   // X opens doors in walk-around mode — don't let it delete a package
            if (_pkgDeleteMenu != null && _pkgDeleteMenu.Visible) return;   // already confirming
            var sel = packagesMenu.SelectedItem as NativeItem;
            if (sel == null || !_packageItemPaths.TryGetValue(sel, out string path)) return;
            bool xPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.FrontendX)
                         || Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.FrontendX);
            if (xPressed) OpenPackageDeleteConfirm(path, PackageDisplayName(path));
        }

        private void OpenPackageDeleteConfirm(string filePath, string displayName)
        {
            _pkgDeleteTarget = filePath; _pkgDeleteName = displayName;
            if (_pkgDeleteMenu == null)
            {
                _pkgDeleteMenu = CreateMenu("DELETE PACKAGE");
                var cancel = new NativeItem("Cancel", "Keep this package. (Back also cancels.)");
                cancel.Activated += (s, e) => ClosePackageDeleteConfirm();
                _pkgDeleteMenu.Add(cancel);
                _pkgDeleteItem = new NativeItem("Delete", "Permanently delete this package file. This cannot be undone.");
                _pkgDeleteItem.Activated += (s, e) =>
                {
                    try
                    {
                        if (File.Exists(_pkgDeleteTarget)) File.Delete(_pkgDeleteTarget);
                        if (currentVehicle != null && string.Equals(VehicleSaveData.GetEquippedPackage(VehicleKey(currentVehicle)), _pkgDeleteName, StringComparison.OrdinalIgnoreCase))
                            VehicleSaveData.SetEquippedPackage(VehicleKey(currentVehicle), null);
                        ShowNotification($"~g~Deleted package: {_pkgDeleteName}");
                    }
                    catch (Exception ex) { ShowNotification("~r~Failed to delete package!"); Log($"Delete package: {ex.Message}"); }
                    RefreshPackagesMenu();
                    ClosePackageDeleteConfirm();
                };
                _pkgDeleteMenu.Add(_pkgDeleteItem);
                _pkgDeleteMenu.Closed += (s, e) => { if (!isNavigatingMenu && packagesMenu != null) packagesMenu.Visible = true; };
            }
            _pkgDeleteItem.Title = $"Delete \"{displayName}\"";
            _pkgDeleteMenu.SelectedIndex = 0;   // default to Cancel (safer)
            isNavigatingMenu = true; packagesMenu.Visible = false; _pkgDeleteMenu.Visible = true; isNavigatingMenu = false;
        }

        private void ClosePackageDeleteConfirm()
        {
            if (_pkgDeleteMenu == null) return;
            isNavigatingMenu = true; _pkgDeleteMenu.Visible = false; if (packagesMenu != null) packagesMenu.Visible = true; isNavigatingMenu = false;
        }

        /// <summary>Mark every part in the package as owned for this vehicle (so re-applying is free).</summary>
        private void MarkPackageOwned(VehiclePackageData p)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            try
            {
                if (p.mods != null)
                    foreach (var kv in p.mods)
                        if (kv.Value >= 0)
                        {
                            if (kv.Key == 23) VehicleSaveData.SetModOwned(vn, 23, WheelOwnKey(p.wheelType, kv.Value));
                            else VehicleSaveData.SetModOwned(vn, kv.Key, kv.Value);
                        }
            }
            catch (Exception ex) { Log($"[Pkg] own mods: {ex.Message}"); }
            try { if (p.turbo) VehicleSaveData.SetTurboOwned(vn); } catch (Exception ex) { Log($"[Pkg] own turbo: {ex.Message}"); }
            try { if (p.xenon) VehicleSaveData.SetXenonOwned(vn); } catch (Exception ex) { Log($"[Pkg] own xenon: {ex.Message}"); }
            try { if (p.windowTint > 0) VehicleSaveData.SetTintOwned(vn, p.windowTint); } catch (Exception ex) { Log($"[Pkg] own tint: {ex.Message}"); }
            try { if (p.plateStyle >= 0) VehicleSaveData.SetPlateOwned(vn, p.plateStyle); } catch (Exception ex) { Log($"[Pkg] own plate: {ex.Message}"); }
            try { if (p.livery >= 0) VehicleSaveData.SetModOwned(vn, 48, p.livery); } catch (Exception ex) { Log($"[Pkg] own livery: {ex.Message}"); }
            try { if (p.wheelFitmentOwned || p.customSuspensionEquipped) VehicleSaveData.SetWheelFitmentOwned(vn, true); } catch (Exception ex) { Log($"[Pkg] own susp: {ex.Message}"); }
            try { if (p.mtOwned) VehicleSaveData.SetManualTransmissionOwned(vn, true); } catch (Exception ex) { Log($"[Pkg] own mt: {ex.Message}"); }
            try { if (p.nosOwnedTiers != 0) VehicleSaveData.SetNosOwnedTiers(vn, VehicleSaveData.GetNosOwnedTiers(vn) | p.nosOwnedTiers); } catch (Exception ex) { Log($"[Pkg] own nos: {ex.Message}"); }
            try { VehicleSaveData.Save(); } catch (Exception ex) { Log($"[Pkg] own save: {ex.Message}"); }
        }

        /// <summary>Snapshot the car's current visual build (so a package preview can be reverted exactly).</summary>
        private void CaptureVehicleModSnapshot()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) { _packageSnapshot = null; return; }
            _packageSnapshot = CaptureCurrentPackage();
        }

        /// <summary>Put the car's visuals back to the snapshot taken when the Packages menu opened. (Functional
        /// ELSC features aren't touched during preview, so restoring visuals is sufficient.)</summary>
        private void RestoreVehicleModSnapshot()
        {
            if (_packageSnapshot == null || currentVehicle == null || !currentVehicle.Exists()) return;
            ApplyPackageVisual(_packageSnapshot);
        }

        /// <summary>Revert to the snapshot, then (if hovering a package row) apply that package's visuals as a
        /// silent live preview.</summary>
        private void PreviewSelectedPackage()
        {
            if (_packageSnapshot == null || packagesMenu == null) return;
            RestoreVehicleModSnapshot();   // clean baseline so previews don't stack
            var sel = packagesMenu.SelectedItem as NativeItem;
            if (sel != null && _packageItemPaths.TryGetValue(sel, out string path))
            {
                var p = LoadPackageData(path);
                if (p != null) ApplyPackageVisual(p, livePreview: true);
            }
        }

        #endregion

        #endregion
    }
}
