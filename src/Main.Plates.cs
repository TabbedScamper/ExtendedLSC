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
        #region Custom Plate Text

        private string GetPlateText(Vehicle v)
        {
            try { return (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>
        /// Per-INSTANCE save key for a vehicle: "{DisplayName}|{PLATE}" (model + plate). This is what every
        /// VehicleSaveData call keys off, so two cars of the same model with different plates keep SEPARATE
        /// builds (a freshly-spawned stock car no longer inherits a modified car's saved build). Empty/missing
        /// plate -> "NOPLATE". VehicleSaveData lowercases keys internally, so case here doesn't matter.
        /// </summary>
        private string VehicleKey(Vehicle v)
        {
            if (v == null || !v.Exists()) return "";
            string plate;
            try { plate = (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim().ToUpperInvariant(); }
            catch (Exception ex) { Log($"[VehicleKey] plate read failed: {ex.Message}"); plate = ""; }
            if (string.IsNullOrEmpty(plate)) plate = "NOPLATE";
            return VehNameCache.Of(v) + "|" + plate;
        }

        /// <summary>An 8-char plate ("EL" + 6 random digits) not used by any spawned vehicle nor any active claim.</summary>
        private string UniquePlate()
        {
            // Plates already in use by a live vehicle or claimed this session.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var v in World.GetAllVehicles())
                    if (v != null && v.Exists()) used.Add(GetPlateText(v).ToUpperInvariant());
            }
            catch (Exception ex) { Log($"[UniquePlate] enumerate failed: {ex.Message}"); }
            foreach (var k in _plateClaims.Keys) used.Add(k);

            for (int attempt = 0; attempt < 64; attempt++)
            {
                string p = "EL" + _plateRng.Next(0, 1000000).ToString("D6");   // 8 chars, valid plate text
                if (!used.Contains(p)) return p;
            }
            // Extremely unlikely fallback (still 8 chars).
            return "EL" + _plateRng.Next(0, 1000000).ToString("D6");
        }

        /// <summary>
        /// Called once per newly-bound car (BEFORE ELSC reads/applies its saved build). If this car's plate is
        /// empty, or collides with a DIFFERENT live vehicle that already claimed it this session, assign a fresh
        /// unique plate so this instance gets its own per-instance key (and stays stock). Otherwise just record
        /// the claim. The first-claimed (already-built) car keeps its plate + data; the duplicate is the one
        /// re-plated. No data migration here — a fresh duplicate has no build to carry.
        /// </summary>
        private void EnsureUniquePlate(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            try
            {
                string plate = GetPlateText(v).ToUpperInvariant();

                bool collides = false;
                if (string.IsNullOrEmpty(plate) || IsPlaceholderPlate(plate))
                {
                    // No plate, or a shared spawner placeholder — every such car needs its OWN stable plate
                    // so they don't all collide on one ELSC identity (and snapshot/stance/tint apply to the wrong car).
                    collides = true;
                }
                else if (_plateClaims.TryGetValue(plate, out int owner) && owner != v.Handle)
                {
                    // Another handle claimed this plate. Only a conflict if that car is still a live, different vehicle.
                    bool ownerAlive = false;
                    foreach (var other in World.GetAllVehicles())
                        if (other != null && other.Exists() && other.Handle == owner) { ownerAlive = true; break; }
                    if (ownerAlive)
                        collides = true;
                    else
                        _plateClaims.Remove(plate);   // stale claim — free it
                }

                if (collides)
                {
                    string p = UniquePlate();
                    Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, v, p);
                    // This is a fresh/duplicate instance — clear any window tint that bled over from the car it used
                    // to share a plate with (the custom-glass manager applies saved colours by model+plate). A
                    // genuinely fresh spawn has no tint; if the player wants one they set it via ELSC (saves anew).
                    try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, 0); } catch { }
                    _plateClaims[p] = v.Handle;
                    Log($"[Plate] Duplicate/empty plate on '{v.DisplayName}' -> assigned unique '{p}'");
                }
                else
                {
                    _plateClaims[plate] = v.Handle;
                }
            }
            catch (Exception ex) { Log($"[Plate] EnsureUniquePlate failed: {ex.Message}"); }
        }

        /// <summary>
        /// Guard wrapper: detect a NEW car the player is driving and run EnsureUniquePlate exactly once per bind
        /// (tracked by Handle), before any save-data read/apply this frame. Called from OnTick.
        /// </summary>
        private void BindPlateForCurrentVehicle()
        {
            try
            {
                var ped = Game.Player.Character;
                if (ped == null || !ped.IsInVehicle()) { _lastPlateBoundHandle = 0; return; }
                var v = ped.CurrentVehicle;
                if (v == null || !v.Exists()) { _lastPlateBoundHandle = 0; return; }
                if (v.Handle == _lastPlateBoundHandle) return;   // already handled this car
                _lastPlateBoundHandle = v.Handle;
                EnsureUniquePlate(v);
            }
            catch (Exception ex) { Log($"[Plate] BindPlateForCurrentVehicle failed: {ex.Message}"); }
        }

        private int _lastPlateSweep = 0;

        /// <summary>Throttled world sweep: when two or more nearby cars share the SAME model AND plate (the classic
        /// "a whole spawned fleet shares one placeholder plate" case), give the extras a fresh unique plate so each owns its own
        /// ELSC save instead of overwriting the first one's. Only same-model + same-(non-empty)-plate collisions are
        /// touched, so ordinary traffic (unique plates) is never re-plated.</summary>
        private void DedupeWorldPlates()
        {
            if (Game.GameTime - _lastPlateSweep < 1500) return;
            _lastPlateSweep = Game.GameTime;
            try
            {
                var ped = Game.Player.Character;
                if (ped == null || !ped.Exists()) return;
                int curHandle = (ped.IsInVehicle() && ped.CurrentVehicle != null && ped.CurrentVehicle.Exists())
                    ? ped.CurrentVehicle.Handle : 0;

                var seen = new HashSet<string>();
                foreach (var v in World.GetNearbyVehicles(ped.Position, 120f))
                {
                    if (v == null || !v.Exists()) continue;
                    string plate = GetPlateText(v).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(plate)) continue;          // ignore blank-plate traffic
                    string key = v.Model.Hash + "|" + plate;
                    bool firstSeen = seen.Add(key);
                    // Re-plate duplicates always; ALSO re-plate every placeholder-plate car (even a lone one) so no
                    // car keeps the shared plate that collides on one ELSC identity.
                    if (firstSeen && !IsPlaceholderPlate(plate)) continue;       // unique non-placeholder -> keep it
                    if (v.Handle == curHandle) continue;                // player's car is handled by BindPlate

                    string p = UniquePlate();
                    Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, v, p);
                    try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, 0); } catch { }   // don't inherit shared-plate tint
                    _plateClaims[p] = v.Handle;
                    seen.Add(v.Model.Hash + "|" + p);
                    Log($"[Plate] Dedupe duplicate '{plate}' on '{v.DisplayName}' -> '{p}'");
                }
            }
            catch (Exception ex) { Log($"[Plate] DedupeWorldPlates error: {ex.Message}"); }
        }

        /// <summary>True if some OTHER live, spawned vehicle currently in the world already wears this plate text.</summary>
        private bool PlateUsedBySpawnedVehicle(Vehicle self, string plateText)
        {
            string want = (plateText ?? "").Trim();
            if (string.IsNullOrEmpty(want)) return false;
            try
            {
                foreach (var v in World.GetAllVehicles())
                {
                    if (v == null || !v.Exists()) continue;
                    if (self != null && v.Handle == self.Handle) continue;
                    if (string.Equals(GetPlateText(v), want, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex) { Log($"[Plate] PlateUsedBySpawnedVehicle failed: {ex.Message}"); }
            return false;
        }

        /// <summary>Open the on-screen keyboard to type a custom plate (max 8 chars), prefilled with the current.</summary>
        private void StartEditPlate()
        {
            if (isEditingPlate || currentVehicle == null || !currentVehicle.Exists()) return;
            isEditingPlate = true;
            plateBeforeEdit = GetPlateText(currentVehicle);
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", plateBeforeEdit, "", "", "", 8);
            ShowNotification("~y~Enter a plate (up to 8 characters)");
        }

        /// <summary>Poll the keyboard; on accept, validate uniqueness and apply (or raise the conflict prompt).</summary>
        private void UpdatePlateEditor()
        {
            if (!isEditingPlate) return;
            if (plateKbCooldown > 0) { plateKbCooldown--; return; }
            plateKbCooldown = 5;

            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1) // accepted
            {
                isEditingPlate = false;
                string text = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(text)) { ShowNotification("~r~Plate not changed (empty)"); return; }
                if (currentVehicle == null || !currentVehicle.Exists()) return;

                // Same as current -> nothing to do.
                if (string.Equals(text, plateBeforeEdit, StringComparison.OrdinalIgnoreCase)) return;

                // Reject if the plate is already used by another SAVED vehicle (window-tint check) OR by another
                // live SPAWNED vehicle currently in the world (so two cars can't share a plate -> identity collision).
                if (windowTint.PlateUsedBySavedVehicle(currentVehicle, text) || PlateUsedBySpawnedVehicle(currentVehicle, text))
                {
                    pendingPlateText = text;
                    ShowPlateConflict(text);
                }
                else
                {
                    ApplyCustomPlate(text, plateBeforeEdit);
                }
            }
            else if (status == 2) // cancelled
            {
                isEditingPlate = false;
                ShowNotification("~y~Plate edit cancelled");
            }
        }

        /// <summary>Set the plate text and migrate this car's ENTIRE saved build (and window color) so the whole
        /// build follows the rename instead of being orphaned under the old plate key.</summary>
        private void ApplyCustomPlate(string text, string oldPlate)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            string model = VehNameCache.Of(currentVehicle);
            // VehicleKey-style keys: empty plate -> "NOPLATE" so they line up with what VehicleKey() produces.
            string oldUp = (oldPlate ?? "").Trim().ToUpperInvariant(); if (string.IsNullOrEmpty(oldUp)) oldUp = "NOPLATE";
            string newUp = (text ?? "").Trim().ToUpperInvariant();     if (string.IsNullOrEmpty(newUp)) newUp = "NOPLATE";
            string oldFullKey = model + "|" + oldUp;
            string newFullKey = model + "|" + newUp;

            windowTint.MoveColorToPlate(model, oldPlate, text);  // keep the tint on the renamed car
            try { VehicleSaveData.MigrateVehicle(oldFullKey, newFullKey); }   // carry the WHOLE build to the new plate key
            catch (Exception ex) { Log($"[Plate] MigrateVehicle failed: {ex.Message}"); }

            Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, currentVehicle, text);

            // Re-point the session plate-claim from the old plate to the new one so dedupe stays consistent.
            try
            {
                if (!string.IsNullOrEmpty(oldUp) && _plateClaims.TryGetValue(oldUp, out int h) && h == currentVehicle.Handle)
                    _plateClaims.Remove(oldUp);
                _plateClaims[newUp] = currentVehicle.Handle;
                _lastPlateBoundHandle = currentVehicle.Handle;   // we just set this car's plate — don't re-dedupe it
            }
            catch (Exception ex) { Log($"[Plate] claim re-point failed: {ex.Message}"); }

            VehicleSaveData.Save();
            ShowNotification($"~g~Plate set to ~b~{text}");
            pendingPlateText = null;
            // Refresh the plate menu so the "Custom Plate Text" AltTitle shows the new value.
            if (plateMenu != null && plateMenu.Visible)
            {
                isNavigatingMenu = true; plateMenu.Visible = false;
                plateMenu = CreatePlateMenu();
                // The rebuilt instance didn't go through AddSubmenuItem, so it lacks the parent-restore handler —
                // without this, backing out of the (rebuilt) plate menu closed the whole ELSC menu instead of
                // returning to the main menu. Re-attach it.
                plateMenu.Closed += (s, e) => { if (!isNavigatingMenu) mainMenu.Visible = true; };
                plateMenu.Visible = true;
                isNavigatingMenu = false;
            }
        }

        /// <summary>Duplicate-plate prompt: choose a different name or overwrite the other vehicle's identity.</summary>
        private void ShowPlateConflict(string text)
        {
            if (plateConflictMenu == null)
            {
                plateConflictMenu = CreateMenu("Plate In Use");
                var diff = new NativeItem("Choose a Different Name", "Re-open the keyboard to pick a unique plate");
                diff.Activated += (s, e) =>
                {
                    isNavigatingMenu = true; plateConflictMenu.Visible = false; isNavigatingMenu = false;
                    StartEditPlate();
                };
                var over = new NativeItem("Overwrite Anyway", "Use this plate even though another saved vehicle has it");
                over.Activated += (s, e) =>
                {
                    isNavigatingMenu = true; plateConflictMenu.Visible = false;
                    if (plateMenu != null) plateMenu.Visible = true;
                    isNavigatingMenu = false;
                    if (!string.IsNullOrEmpty(pendingPlateText)) ApplyCustomPlate(pendingPlateText, plateBeforeEdit);
                };
                plateConflictMenu.Add(diff);
                plateConflictMenu.Add(over);
            }
            ShowNotification($"~o~Plate ~b~{text}~o~ is already used by another saved vehicle.");
            isNavigatingMenu = true;
            if (plateMenu != null) plateMenu.Visible = false;
            plateConflictMenu.Visible = true;
            isNavigatingMenu = false;
        }

        /// <summary>
        /// Check for X input to edit category description (hold X for 500ms)
        /// </summary>
        private void CheckDescriptionEditInput()
        {
            if (IsTypingText) return;   // never start an edit while the player is typing into another text box

            // X button = FrontendX or Duck (depends on context)
            bool xHeld = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX) ||
                         Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);

            if (!ModSettings.EditorModeEnabled) return;

            // Don't process X input while keyboard is open (A=save, B=cancel handled by GTA)
            if (isEditingDescription) return;

            // Some preview buttons share a physical button with the "hold X to edit description" gesture on
            // controller: L3 (horn preview = FrontendLs/Duck) and X (NOS preview = NosButton/VehicleDuck). Skip
            // the edit-description check while any of those preview buttons is held so previewing never edits.
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, ModSettings.HornPreviewButton) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLs) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, ModSettings.NosButton))
            {
                lastLbHeldTime = DateTime.MinValue;   // reset the hold timer so it can't carry over
                return;
            }

            // Find the currently visible menu
            var visibleMenu = GetVisibleMenu();
            if (visibleMenu == null || visibleMenu.Items.Count == 0) return;

            if (xHeld)
            {
                if (lastLbHeldTime == DateTime.MinValue)
                {
                    lastLbHeldTime = DateTime.Now;
                }
                else
                {
                    double elapsed = (DateTime.Now - lastLbHeldTime).TotalMilliseconds;
                    if (elapsed >= 500)
                    {
                        Log($"[DescEditor] X held for {elapsed:F0}ms - triggering edit!");
                        int selectedIndex = visibleMenu.SelectedIndex;
                        if (selectedIndex >= 0 && selectedIndex < visibleMenu.Items.Count)
                        {
                            string categoryName = visibleMenu.Items[selectedIndex].Title;
                            lastLbHeldTime = DateTime.MinValue;
                            StartEditDescription(categoryName);
                        }
                    }
                }
            }
            else
            {
                lastLbHeldTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Check if edit mode key is held (LB/L1 on controller, Left Shift on keyboard)
        /// </summary>
        private bool IsEditModeKeyHeld()
        {
            // LB on controller (FrontendLb/ScriptLB) or Left Shift (Sprint) for keyboard
            return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLb) ||
                   Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLB) ||
                   Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Sprint);
        }

        /// <summary>
        /// Debug: Log controller button inputs with timing
        /// </summary>
        private void DebugLogControllerInput()
        {
            debugTickCounter++;

            // Controller debug logging is disabled
            return;

            // Kept for reference - only log when menu is active
            if (!isMenuActive) return;

            // Check ALL frontend/script controls for complete button mapping
            bool frontLB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLb);
            bool frontRB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRb);
            bool frontLT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLt);
            bool frontRT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRt);
            bool frontLS = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLs);
            bool frontRS = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRs);
            bool frontAccept = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendAccept);
            bool frontCancel = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendCancel);
            bool frontX = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX);
            bool frontY = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendY);
            bool scriptLB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLB);
            bool scriptRB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptRB);
            bool scriptLT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLT);
            bool scriptRT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptRT);
            bool duck = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);
            bool jump = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Jump);
            bool context = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Context);
            bool detonate = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Detonate);
            bool vehicleExit = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.VehicleExit);

            // Log any button press with clear labels
            if (frontLB || frontRB || frontLT || frontRT || frontLS || frontRS || frontAccept || frontCancel ||
                frontX || frontY || scriptLB || scriptRB || scriptLT || scriptRT || duck || jump || context || detonate || vehicleExit)
            {
                Log($"[INPUT] FrontLB={frontLB} FrontRB={frontRB} FrontLT={frontLT} FrontRT={frontRT} FrontLS={frontLS} FrontRS={frontRS} Accept={frontAccept} Cancel={frontCancel} FrontX={frontX} FrontY={frontY} ScriptLB={scriptLB} ScriptRB={scriptRB} ScriptLT={scriptLT} ScriptRT={scriptRT} Duck={duck} Jump={jump} Context={context} Detonate={detonate} VehExit={vehicleExit}");
            }

            // Legacy tracking variables
            bool lbPressed = frontLB || scriptLB;
            bool xPressed = duck || frontX;
            bool aPressed = frontAccept || jump;
            bool bPressed = frontCancel;
            bool yPressed = vehicleExit || frontY;
            bool reloadPressed = false;
            bool coverPressed = false;
            bool contextPressed = context;
            bool sprintPressed = false;

            // LB button
            if (lbPressed && !wasLbPressed)
            {
                lbPressStart = DateTime.Now;
                Log($"[INPUT] LB PRESSED");
            }
            else if (!lbPressed && wasLbPressed)
            {
                var duration = DateTime.Now - lbPressStart;
                Log($"[INPUT] LB RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasLbPressed = lbPressed;

            // X button (VehicleDuck)
            if (xPressed && !wasXPressed)
            {
                xPressStart = DateTime.Now;
                Log($"[INPUT] X (VehicleDuck) PRESSED | LB={lbPressed} Sprint={sprintPressed}");
            }
            else if (!xPressed && wasXPressed)
            {
                var duration = DateTime.Now - xPressStart;
                Log($"[INPUT] X (VehicleDuck) RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasXPressed = xPressed;

            // A button
            if (aPressed && !wasAPressed)
            {
                aPressStart = DateTime.Now;
                Log($"[INPUT] A PRESSED");
            }
            else if (!aPressed && wasAPressed)
            {
                var duration = DateTime.Now - aPressStart;
                Log($"[INPUT] A RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasAPressed = aPressed;

            // B button
            if (bPressed && !wasBPressed)
            {
                bPressStart = DateTime.Now;
                Log($"[INPUT] B PRESSED");
            }
            else if (!bPressed && wasBPressed)
            {
                var duration = DateTime.Now - bPressStart;
                Log($"[INPUT] B RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasBPressed = bPressed;

            // Y button
            if (yPressed && !wasYPressed)
            {
                yPressStart = DateTime.Now;
                Log($"[INPUT] Y PRESSED");
            }
            else if (!yPressed && wasYPressed)
            {
                var duration = DateTime.Now - yPressStart;
                Log($"[INPUT] Y RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasYPressed = yPressed;

            // Log alternative controls if pressed (one-time per press cycle)
            if (reloadPressed && Game.IsControlJustPressed(GTA.Control.Reload))
                Log($"[INPUT] Reload control PRESSED");
            if (coverPressed && Game.IsControlJustPressed(GTA.Control.Cover))
                Log($"[INPUT] Cover (RB) control PRESSED");
            if (contextPressed && Game.IsControlJustPressed(GTA.Control.Context))
                Log($"[INPUT] Context control PRESSED");
        }

        #endregion
    }
}
