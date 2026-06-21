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
        #region Mod Application

        private void ApplyModByIndex(int modIndex, int valueIndex)
        {
            if (currentVehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, modIndex, valueIndex, false);
            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{ModCategories.GetDisplayName(modIndex)} installed!");
            MechanicSpeak();
            Log($"Applied mod index {modIndex} value {valueIndex}");

            // Refresh menu item statuses to show updated icons
            RefreshModMenuStatus(modIndex, valueIndex);
        }

        /// <summary>
        /// Refresh the ownership status icons for a mod menu after applying a mod
        /// </summary>
        private void RefreshModMenuStatus(int modIndex, int newInstalledValue)
        {
            if (!modMenusByIndex.TryGetValue(modIndex, out var menu)) return;

            string vehicleName = VehicleKey(currentVehicle);

            // Check if this mod type has no stock option (performance mods)
            bool noStockOption = false;
            if (ModCategories.AllCategories.TryGetValue(modIndex, out var category))
            {
                noStockOption = category.NoStockOption;
            }

            // Iterate through menu items and update their status
            // If has stock: Item 0 is "Stock" with value -1, rest are mods with values 0, 1, 2, etc.
            // If no stock: Item 0 is mod 0, Item 1 is mod 1, etc.
            for (int i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i] as NativeItem;
                if (item == null) continue;

                int itemValue = noStockOption ? i : i - 1;

                bool isNowInstalled = (itemValue == newInstalledValue);
                bool isOwned = itemValue >= 0 && VehicleSaveData.IsModOwned(vehicleName, modIndex, itemValue);

                // Remove old status
                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    // Show price for items that aren't owned
                    int price = ModPricing.GetModPriceByIndex(modIndex, itemValue);
                    item.AltTitle = itemValue == -1 ? "Free" : $"${price:N0}";
                }
            }
        }

        /// <summary>
        /// Refresh the horn menu ownership status after applying a horn
        /// </summary>
        private void RefreshHornMenuStatus(int newInstalledIndex)
        {
            if (hornMenu == null) return;
            string vehicleName = VehicleKey(currentVehicle);

            for (int i = 0; i < hornMenu.Items.Count; i++)
            {
                var item = hornMenu.Items[i] as NativeItem;
                if (item == null) continue;

                int hornIndex = i < _hornItemModIndex.Count ? _hornItemModIndex[i] : -1; // menu pos -> mod index
                bool isNowInstalled = (hornIndex == newInstalledIndex);
                bool isOwned = hornIndex >= 0 && VehicleSaveData.IsHornOwned(vehicleName, hornIndex);

                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = hornIndex == -1 ? "Free" : "$100";
                }
            }
        }

        /// <summary>
        /// Refresh the plate menu ownership status after applying a plate style
        /// </summary>
        private void RefreshPlateMenuStatus(int newInstalledIndex)
        {
            if (plateMenu == null) return;
            string vehicleName = VehicleKey(currentVehicle);

            for (int i = 0; i < plateMenu.Items.Count; i++)
            {
                var item = plateMenu.Items[i] as NativeItem;
                if (item == null) continue;

                // Menu items 0 and 1 are the "Custom Plate Text" / "Random Plate" headers; the plate STYLES
                // start at item 2. The style index = item index - 2 (matches CreatePlateMenu's layout note).
                int styleIdx = i - 2;
                if (styleIdx < 0) continue;   // skip the two header items

                bool isNowInstalled = (styleIdx == newInstalledIndex);
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, styleIdx);

                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = "$200";
                }
            }
        }

        /// <summary>
        /// Refresh the window tint menu ownership status after applying a tint
        /// </summary>
        private void RefreshWindowTintMenuStatus(int newInstalledTintValue)
        {
            // Window tint now uses config-based menu, delegate to config refresh
            RefreshConfigMenuStatus("Windows", newInstalledTintValue);
        }

        /// <summary>
        /// Refresh the turbo menu ownership status after toggling turbo
        /// </summary>
        private void RefreshTurboMenuStatus(bool turboEnabled)
        {
            if (turboMenu == null || turboMenu.Items.Count < 2) return;
            string vehicleName = VehicleKey(currentVehicle);
            bool turboOwned = VehicleSaveData.IsTurboOwned(vehicleName);

            // Item 0 is "None", Item 1 is "Turbo Tuning"
            var noneItem = turboMenu.Items[0] as NativeItem;
            var turboItem = turboMenu.Items[1] as NativeItem;

            if (noneItem != null)
            {
                itemOwnershipStatus.Remove(noneItem);
                if (!turboEnabled)
                {
                    noneItem.AltTitle = "";
                    itemOwnershipStatus[noneItem] = STATUS_INSTALLED;
                }
                else
                {
                    noneItem.AltTitle = "Free";
                }
            }

            if (turboItem != null)
            {
                itemOwnershipStatus.Remove(turboItem);
                if (turboEnabled)
                {
                    turboItem.AltTitle = "";
                    itemOwnershipStatus[turboItem] = STATUS_INSTALLED;
                }
                else if (turboOwned)
                {
                    turboItem.AltTitle = "";
                    itemOwnershipStatus[turboItem] = STATUS_OWNED;
                }
                else
                {
                    turboItem.AltTitle = $"${ModPricing.TurboPrice}";
                }
            }
        }

        /// <summary>
        /// Refresh the headlights menu ownership status after toggling xenon
        /// </summary>
        private void RefreshHeadlightsMenuStatus(bool xenonEnabled)
        {
            if (headlightsMenu == null || headlightsMenu.Items.Count < 2) return;
            string vehicleName = VehicleKey(currentVehicle);
            bool xenonOwned = VehicleSaveData.IsXenonOwned(vehicleName);

            // Item 0 is "Stock Lights", Item 1 is "Xenon Lights"
            var stockItem = headlightsMenu.Items[0] as NativeItem;
            var xenonItem = headlightsMenu.Items[1] as NativeItem;

            if (stockItem != null)
            {
                itemOwnershipStatus.Remove(stockItem);
                if (!xenonEnabled)
                {
                    stockItem.AltTitle = "";
                    itemOwnershipStatus[stockItem] = STATUS_INSTALLED;
                }
                else
                {
                    stockItem.AltTitle = "Free";
                }
            }

            if (xenonItem != null)
            {
                itemOwnershipStatus.Remove(xenonItem);
                if (xenonEnabled)
                {
                    xenonItem.AltTitle = "";
                    itemOwnershipStatus[xenonItem] = STATUS_INSTALLED;
                }
                else if (xenonOwned)
                {
                    xenonItem.AltTitle = "";
                    itemOwnershipStatus[xenonItem] = STATUS_OWNED;
                }
                else
                {
                    xenonItem.AltTitle = $"${ModPricing.XenonLightsPrice}";
                }
            }
        }

        private void ApplyMod(VehicleModType modType, int index)
        {
            ApplyModByIndex((int)modType, index);
        }

        // Ownership key for a wheel: encodes BOTH the wheel type and the rim index into one int, stored under
        // mod index 23. The same rim index means different wheels under different types, so the type must be
        // part of the key (otherwise buying "Sport #0" would mark "Muscle #0" as owned too).
        private static int WheelOwnKey(int wheelType, int wheelIndex) => wheelType * 1000 + wheelIndex;

        /// <summary>Apply a wheel design to the chosen axle(s): 0 = front+rear, 1 = front only, 2 = rear only.
        /// The wheel TYPE is shared across all wheels (GTA limit); only the design index differs per axle.</summary>
        private void ApplyWheelAxle(int wheelType, int wheelIndex, int axle)
        {
            if (currentVehicle == null) return;
            bool custom = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23); // keep custom tires
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            if (axle != 2) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, custom); // front
            if (axle != 1) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, custom); // rear
            ShowNotification(axle == 1 ? "~g~Front wheels installed!" : axle == 2 ? "~g~Rear wheels installed!" : "~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex} axle {axle}");
        }

        private void ApplyWheel(int wheelType, int wheelIndex)
        {
            if (currentVehicle == null) return;

            bool custom = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23); // keep custom tires
            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, custom); // Front wheels
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, custom); // Back wheels

            ShowNotification("~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex}");
        }

        /// <summary>Toggle the aftermarket tire profile (SET_VEHICLE_MOD variation flag) on both axles.</summary>
        private void ApplyCustomTires(bool custom)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            int front = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int back = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, front, custom);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, back, custom);
            ShowNotification(custom ? "~g~Custom tires installed!" : "~g~Stock tires installed!");
            MechanicSpeak();
        }

        private void ApplyLivery(int index, bool useNativeLivery)
        {
            if (currentVehicle == null) return;

            if (useNativeLivery)
            {
                Function.Call(Hash.SET_VEHICLE_LIVERY, currentVehicle, index);
            }
            else
            {
                // Mod-based livery (mod index 48)
                // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 48, index, false);
            }
            ShowNotification("~g~Livery applied!");
            MechanicSpeak();
        }

        private void ApplyColor(int colorIndex, bool isPrimary)
        {
            if (currentVehicle == null) return;

            int primary, secondary;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_COLOURS, currentVehicle, &primary, &secondary);
            }

            if (isPrimary)
                Function.Call(Hash.SET_VEHICLE_COLOURS, currentVehicle, colorIndex, secondary);
            else
                Function.Call(Hash.SET_VEHICLE_COLOURS, currentVehicle, primary, colorIndex);

            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{(isPrimary ? "Primary" : "Secondary")} color applied!");
            MechanicSpeak();
        }

        /// <summary>
        /// Apply paint color with proper paint type (Classic=0, Metallic=1, Pearl=2, Matte=3, Metal=4, Chrome=5)
        /// </summary>
        private void ApplyPaintColor(int colorIndex, int pearlescentSpec, int paintType, bool isPrimary)
        {
            if (currentVehicle == null) return;

            // Use direct color setting instead of SET_VEHICLE_MOD_COLOR_1
            // The raw color index already encodes the paint type in carcols.meta
            if (isPrimary)
            {
                currentVehicle.Mods.PrimaryColor = (VehicleColor)colorIndex;
                if (pearlescentSpec > 0)
                    currentVehicle.Mods.PearlescentColor = (VehicleColor)pearlescentSpec;
            }
            else
            {
                currentVehicle.Mods.SecondaryColor = (VehicleColor)colorIndex;
            }

            string paintTypeName = paintType switch
            {
                0 => "Classic",
                1 => "Metallic",
                2 => "Pearl",
                3 => "Matte",
                4 => "Metal",
                5 => "Chrome",
                _ => ""
            };
            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{paintTypeName} {(isPrimary ? "primary" : "secondary")} color applied!");
            MechanicSpeak();
        }

        private void ApplyPearlescent(int colorIndex)
        {
            if (currentVehicle == null) return;

            int pearl, wheel;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &wheel);
            }
            Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, colorIndex, wheel);

            ShowNotification("~g~Pearlescent applied!");
            MechanicSpeak();
        }

        private void ApplyWheelColor(int colorIndex)
        {
            if (currentVehicle == null) return;

            int pearl, wheel;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &wheel);
            }
            Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearl, colorIndex);

            ShowNotification("~g~Wheel color applied!");
            MechanicSpeak();
        }

        private void ApplyTireSmoke(int r, int g, int b)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);          // required before any mod op
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 20, true);    // Enable tire smoke
            Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, currentVehicle, r, g, b);

            ShowNotification("~g~Tire smoke applied!");
            MechanicSpeak();
        }

        private void ApplyHeadlightColor(int colorIndex)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, colorIndex);
            ShowNotification("~g~Headlight color applied!");
            MechanicSpeak();
        }

        private void ToggleNeon(int position)
        {
            if (currentVehicle == null) return;

            bool isOn = Function.Call<bool>((Hash)0x8C4B92553E4571EC, currentVehicle, position);
            Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, position, !isOn);

            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~Neon {(!isOn ? "enabled" : "disabled")}!");
            MechanicSpeak();
        }

        private void ApplyNeonColor(int r, int g, int b)
        {
            if (currentVehicle == null) return;

            Function.Call((Hash)0x8E0A582209A62695, currentVehicle, r, g, b);
            ShowNotification("~g~Neon color applied!");
            MechanicSpeak();
        }

        /// <summary>If the car's neon colour is black/unset (≈0,0,0) it emits NO visible glow — so the player can't see
        /// which sides they're enabling in the Neon Layout menu. Default it to white in that case so the layout is
        /// actually visible. Any non-black colour the player already chose is left untouched.</summary>
        private void EnsureNeonColorVisible()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            int r, g, b;
            unsafe { Function.Call((Hash)0x7619EEE8C886757F, currentVehicle, &r, &g, &b); } // GET neon colour
            if (r <= 8 && g <= 8 && b <= 8)   // effectively black -> invisible underglow
                Function.Call((Hash)0x8E0A582209A62695, currentVehicle, 255, 255, 255); // default to white
        }

        private void ApplyHorn(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods[VehicleModType.Horns].Index = index;
            ShowNotification("~g~Horn installed!");
            MechanicSpeak();
            RefreshHornMenuStatus(index);
        }

        private void ApplyWindowTint(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods.WindowTint = (VehicleWindowTint)index;
            ShowNotification("~g~Window tint applied!");
            MechanicSpeak();
            RefreshWindowTintMenuStatus(index);
        }

        private void ApplyPlateStyle(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods.LicensePlateStyle = (LicensePlateStyle)index;
            ShowNotification("~g~License plate style applied!");
            MechanicSpeak();
            RefreshPlateMenuStatus(index);
        }

        private void ToggleTurbo()
        {
            if (currentVehicle == null) return;

            bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 18, !hasTurbo);

            ShowNotification(hasTurbo ? "~r~Turbo removed!" : "~g~Turbo installed!");
            MechanicSpeak();
        }

        private void SetTurbo(bool enabled)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 18, enabled);
            ShowNotification(enabled ? "~g~Turbo Tuning installed!" : "~g~Turbo removed!");
            MechanicSpeak();
            RefreshTurboMenuStatus(enabled);
        }

        private void ToggleXenon()
        {
            if (currentVehicle == null) return;

            bool hasXenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22);
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 22, !hasXenon);

            ShowNotification(hasXenon ? "~r~Xenon removed!" : "~g~Xenon headlights installed!");
            MechanicSpeak();
        }

        private void SetXenon(bool enabled)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 22, enabled);
            ShowNotification(enabled ? "~g~Xenon Lights installed!" : "~g~Stock Lights installed!");
            MechanicSpeak();
            RefreshHeadlightsMenuStatus(enabled);
        }

        private bool GetNeonEnabled(int index)
        {
            if (currentVehicle == null) return false;
            try
            {
                // Use SHVDN's built-in neon check via Vehicle.Mods
                var neonLight = (VehicleNeonLight)index;
                return currentVehicle.Mods.IsNeonLightsOn(neonLight);
            }
            catch
            {
                return false;
            }
        }

        private void SetNeonEnabled(int index, bool enabled)
        {
            if (currentVehicle == null) return;
            try
            {
                var neonLight = (VehicleNeonLight)index;
                currentVehicle.Mods.SetNeonLightsOn(neonLight, enabled);
            }
            catch (Exception ex)
            {
                Log($"Error setting neon {index}: {ex.Message}");
            }
        }

        private void ApplyNeonLayout(bool front, bool back, bool left, bool right)
        {
            if (currentVehicle == null) return;

            // Neon indices: 0=left, 1=right, 2=front, 3=back
            SetNeonEnabled(0, left);
            SetNeonEnabled(1, right);
            SetNeonEnabled(2, front);
            SetNeonEnabled(3, back);

            ShowNotification("~g~Neon layout applied!");
            MechanicSpeak();
        }

        private void ToggleBulletproofTires()
        {
            if (currentVehicle == null) return;

            currentVehicle.CanTiresBurst = !currentVehicle.CanTiresBurst;
            ShowNotification(currentVehicle.CanTiresBurst ? "~r~Bulletproof tires removed!" : "~g~Bulletproof tires installed!");
            MechanicSpeak();
        }

        private void SetBulletproofTires(bool bulletproof)
        {
            if (currentVehicle == null) return;

            currentVehicle.CanTiresBurst = !bulletproof;
            ShowNotification(bulletproof ? "~g~Bulletproof Tires installed!" : "~g~Standard Tires installed!");
            MechanicSpeak();
        }

        private void RepairVehicle()
        {
            if (currentVehicle == null) return;

            currentVehicle.Repair();                                                   // SET_VEHICLE_FIXED (health + scuffs)
            try { Function.Call(Hash.SET_VEHICLE_DEFORMATION_FIXED, currentVehicle); } catch { }  // pop dents/nicks back out
            ShowNotification("~g~Vehicle repaired!");
            MechanicSpeak();
        }

        private void CleanVehicle()
        {
            if (currentVehicle == null) return;

            currentVehicle.Wash();
            ShowNotification("~g~Vehicle cleaned!");
            MechanicSpeak();
        }

        #endregion
    }
}
