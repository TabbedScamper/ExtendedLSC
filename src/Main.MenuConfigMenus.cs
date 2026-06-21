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
        #region MenuConfig-Based Menu Creators

        /// <summary>
        /// Create a menu from MenuConfig items.json with full ownership tracking
        /// </summary>
        private NativeMenu CreateMenuFromConfig(string categoryPath, Func<int> getCurrentValue, Action<MenuConfig.MenuItem> onActivate)
        {
            var category = MenuConfig.LoadCategory(categoryPath);
            if (category == null)
            {
                Log($"[MenuConfig] Category not found: {categoryPath}");
                return null;
            }

            var menu = CreateMenu(category.DisplayName);
            int currentValue = getCurrentValue?.Invoke() ?? -1;
            string vehicleName = VehicleKey(currentVehicle);

            foreach (var item in category.Items)
            {
                bool isInstalled = item.Value == currentValue;
                bool isOwned = item.Value >= 0 && VehicleSaveData.IsCustomItemOwned(vehicleName, categoryPath, item.Value);
                var menuItem = new NativeItem(item.Name);

                if (!string.IsNullOrEmpty(item.Description))
                {
                    menuItem.Description = item.Description;
                }

                // Store metadata for refresh capability
                configItemMetadata[menuItem] = (categoryPath, item.Value, item.Price);

                if (isInstalled)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_INSTALLED;
                    // Mark as owned if currently installed
                    if (item.Value >= 0 && !isOwned)
                    {
                        VehicleSaveData.SetCustomItemOwned(vehicleName, categoryPath, item.Value);
                    }
                }
                else if (isOwned)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_OWNED;
                }
                else
                {
                    menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";
                }

                var capturedItem = item;
                var capturedCategoryPath = categoryPath;
                int capturedPrice = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (currentVehicle == null) return;

                    bool owned = capturedItem.Value >= 0 && VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), capturedCategoryPath, capturedItem.Value);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    onActivate(capturedItem);
                    // Mark as owned when purchased
                    if (capturedItem.Value >= 0 && !owned)
                    {
                        VehicleSaveData.SetCustomItemOwned(VehicleKey(currentVehicle), capturedCategoryPath, capturedItem.Value);
                        VehicleSaveData.Save();
                    }
                    // Refresh the menu to show updated icons
                    RefreshConfigMenuStatus(capturedCategoryPath, capturedItem.Value);
                };
                menu.Add(menuItem);
            }

            // Store menu reference for refresh
            configMenusByPath[categoryPath] = menu;

            return menu;
        }

        /// <summary>
        /// Refresh the ownership status icons for a config-based menu after applying an item
        /// </summary>
        private void RefreshConfigMenuStatus(string categoryPath, int newInstalledValue)
        {
            if (!configMenusByPath.TryGetValue(categoryPath, out var menu)) return;
            string vehicleName = VehicleKey(currentVehicle);

            foreach (var menuItemBase in menu.Items)
            {
                var menuItem = menuItemBase as NativeItem;
                if (menuItem == null) continue;

                if (!configItemMetadata.TryGetValue(menuItem, out var metadata)) continue;
                if (metadata.categoryPath != categoryPath) continue;

                bool isNowInstalled = (metadata.itemValue == newInstalledValue);
                bool isOwned = metadata.itemValue >= 0 && VehicleSaveData.IsCustomItemOwned(vehicleName, categoryPath, metadata.itemValue);

                itemOwnershipStatus.Remove(menuItem);

                if (isNowInstalled)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_OWNED;
                }
                else
                {
                    menuItem.AltTitle = metadata.price == 0 ? "Free" : $"${metadata.price}";
                }
            }
        }

        /// <summary>
        /// Create Window Tint menu from MenuConfig
        /// </summary>
        private NativeMenu CreateWindowTintMenuFromConfig()
        {
            // Custom Color follows the ELSC equip model (same as Custom Suspension / Manual Transmission):
            // NOT owned -> price; owned but NOT active -> owned tick + "Select to equip"; owned AND active ->
            // equipped garage icon + "Select to edit". Equipping applies the custom glass color (mutually
            // exclusive with the presets); selecting a preset un-equips it (the chosen color is preserved).
            NativeItem customColorItem = null;
            const int customColorPrice = 500;

            // Custom is "equipped" when a custom color is actively saved (CurrentColor != 0).
            bool CustomColorEquipped() =>
                currentVehicle != null && VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), "Windows", WINDOW_CUSTOM_COLOR_KEY)
                && windowTint.CurrentColor(currentVehicle) != 0;

            void RefreshCustomColorItem()
            {
                if (customColorItem == null || currentVehicle == null) return;
                bool owned = VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), "Windows", WINDOW_CUSTOM_COLOR_KEY);
                if (!owned)
                {
                    itemOwnershipStatus.Remove(customColorItem);
                    customColorItem.AltTitle = $"${customColorPrice}";
                    customColorItem.Description = "Buy a custom glass color you can fine-tune.";
                }
                else if (CustomColorEquipped())
                {
                    customColorItem.AltTitle = "";
                    itemOwnershipStatus[customColorItem] = STATUS_INSTALLED;
                    customColorItem.Description = "Select to edit your custom glass color.";
                }
                else
                {
                    customColorItem.AltTitle = "";
                    itemOwnershipStatus[customColorItem] = STATUS_OWNED;
                    customColorItem.Description = "Select to equip your custom glass color.";
                }
            }

            // Equip the custom glass color as the active tint (restores the last chosen color, or a default).
            void EquipCustomColor()
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!windowTint.Ready) { ShowNotification("~y~Window-color table initializing… try again in a moment."); return; }
                int last = windowTint.LastColor(currentVehicle);
                int argb = last != 0 ? last : WindowTintManager.PackArgb(64, 0, 0, 0);
                windowTint.ApplyCustomColor(currentVehicle, argb);
                windowTintPurchased = true;                 // don't revert on close
                RefreshConfigMenuStatus("Windows", -999);   // no preset shows the equipped icon now
                RefreshCustomColorItem();                   // Custom shows the equipped garage icon
                ShowNotification("~g~Custom glass color equipped~w~ — select again to edit");
            }

            var menu = CreateMenuFromConfig(
                "Windows",
                () => (int)currentVehicle.Mods.WindowTint,
                (item) =>
                {
                    // Selecting a preset un-equips Custom (its chosen color is preserved for re-equip).
                    windowTint.ClearCustomColor(currentVehicle);
                    ApplyWindowTint(item.Value);
                    windowTintPurchased = true;
                    RefreshCustomColorItem();   // Custom drops back to the owned tick
                }
            );
            if (menu == null) return null;

            var colorMenu = CreateCustomWindowColorMenu();

            // Hover preview of the custom glass color (uses the active or last-chosen color, never wipes it).
            void PreviewCustomColor()
            {
                if (currentVehicle == null || !currentVehicle.Exists() || !windowTint.CanPreview) return;
                int saved = windowTint.CurrentColor(currentVehicle);
                int last = windowTint.LastColor(currentVehicle);
                int argb = saved != 0 ? saved : (last != 0 ? last : WindowTintManager.PackArgb(64, 0, 0, 0));
                windowTint.PreviewColor(currentVehicle, argb);
            }

            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                origWindowTint = (int)currentVehicle.Mods.WindowTint;
                windowTintPurchased = false;
                RefreshCustomColorItem();
                // Park the car off per-tick assertion for the session so preset previews don't fight the
                // custom color; restore the real look immediately (BeginPreview moves it to the preview slot).
                windowTint.BeginPreview(currentVehicle);
                currentVehicle.Mods.WindowTint = (VehicleWindowTint)origWindowTint;
            };
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (e.Index < 0 || e.Index >= menu.Items.Count) return;
                var hovered = menu.Items[e.Index] as NativeItem;
                if (hovered != null && hovered == customColorItem) { PreviewCustomColor(); return; }
                if (hovered != null && configItemMetadata.TryGetValue(hovered, out var meta) && meta.Item2 >= 0)
                    currentVehicle.Mods.WindowTint = (VehicleWindowTint)meta.Item2;
            };
            menu.Closed += (s, e) =>
            {
                if (isNavigatingMenu) return;   // opening the color picker — don't revert / release the preview
                windowTint.EndPreview();
                if (!windowTintPurchased && origWindowTint >= 0 && currentVehicle != null && currentVehicle.Exists())
                    currentVehicle.Mods.WindowTint = (VehicleWindowTint)origWindowTint;
            };

            customColorItem = new NativeItem("Custom Color");
            RefreshCustomColorItem();
            customColorItem.Activated += (s, e) =>
            {
                if (currentVehicle == null) return;
                string vn = VehicleKey(currentVehicle);
                bool owned = VehicleSaveData.IsCustomItemOwned(vn, "Windows", WINDOW_CUSTOM_COLOR_KEY);

                if (!owned)
                {
                    if (!TryPurchase(customColorPrice, false)) return;
                    VehicleSaveData.SetCustomItemOwned(vn, "Windows", WINDOW_CUSTOM_COLOR_KEY);
                    VehicleSaveData.Save();
                    EquipCustomColor();   // buy + equip; select again to edit
                    return;
                }
                if (!CustomColorEquipped())
                {
                    EquipCustomColor();   // equip; select again to edit
                    return;
                }
                // Equipped -> open the RGB adjuster (edit).
                isNavigatingMenu = true; menu.Visible = false; colorMenu.Visible = true; isNavigatingMenu = false;
            };
            colorMenu.Closed += (s, e) => { if (!isNavigatingMenu) { menu.Visible = true; RefreshCustomColorItem(); } };
            menu.Add(customColorItem);
            return menu;
        }

        /// <summary>RGB picker for custom window glass color. Live-previews on change; saves on Apply/close.</summary>
        private NativeMenu CreateCustomWindowColorMenu()
        {
            var menu = CreateMenu("Custom Window Color");

            // R/G/B: 0..255 in steps of 5. Intensity: 5%..100% in steps of 5.
            var chVals = new List<int>(); for (int v = 0; v <= 255; v += 5) chVals.Add(v);
            var intList = new List<int>(); for (int v = 5; v <= 100; v += 5) intList.Add(v);
            var intVals = intList.ToArray();

            int saved = windowTint.CurrentColor(currentVehicle);
            // Fresh start: R/G/B = 0, intensity 25% (alpha ~64). Existing color loads its saved values.
            int def = saved != 0 ? saved : WindowTintManager.PackArgb(64, 0, 0, 0);
            WindowTintManager.UnpackArgb(def, out int da, out int dr, out int dg, out int db);

            int Nearest(List<int> list, int val)
            {
                int best = 0, bd = int.MaxValue;
                for (int i = 0; i < list.Count; i++) { int d = Math.Abs(list[i] - val); if (d < bd) { bd = d; best = i; } }
                return best;
            }
            int NearestArr(int[] list, int val)
            {
                int best = 0, bd = int.MaxValue;
                for (int i = 0; i < list.Length; i++) { int d = Math.Abs(list[i] - val); if (d < bd) { bd = d; best = i; } }
                return best;
            }

            var rItem = new NativeListItem<int>("Red", chVals.ToArray()) { SelectedIndex = Nearest(chVals, dr) };
            var gItem = new NativeListItem<int>("Green", chVals.ToArray()) { SelectedIndex = Nearest(chVals, dg) };
            var bItem = new NativeListItem<int>("Blue", chVals.ToArray()) { SelectedIndex = Nearest(chVals, db) };
            var iItem = new NativeListItem<int>("Intensity %", intVals) { SelectedIndex = NearestArr(intVals, (int)Math.Round(da / 2.55)) };

            Func<int> build = () =>
            {
                int a = (int)Math.Round(iItem.SelectedItem * 2.55);
                return WindowTintManager.PackArgb(a, rItem.SelectedItem, gItem.SelectedItem, bItem.SelectedItem);
            };
            Action preview = () =>
            {
                if (currentVehicle == null) return;
                if (!windowTint.CanPreview) { ShowNotification("~y~Initializing window-color table… try again in a moment."); return; }
                windowTint.PreviewColor(currentVehicle, build());
            };
            rItem.ItemChanged += (s, e) => preview();
            gItem.ItemChanged += (s, e) => preview();
            bItem.ItemChanged += (s, e) => preview();
            iItem.ItemChanged += (s, e) => preview();
            menu.Add(rItem); menu.Add(gItem); menu.Add(bItem); menu.Add(iItem);

            // While the picker is open, stop the per-tick assertion from re-stamping the saved color over the
            // live slider preview, and show the starting color immediately.
            menu.Shown += (s, e) => { windowTint.BeginPreview(currentVehicle); preview(); };

            // Backing out saves the previewed color (to clear a custom color, pick None in the parent menu).
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && windowTint.Ready) windowTint.ApplyCustomColor(currentVehicle, build());
                windowTint.EndPreview();
            };
            return menu;
        }

        /// <summary>
        /// Create Neon Color menu from MenuConfig
        /// </summary>
        private NativeMenu CreateNeonColorMenuFromConfig()
        {
            var category = MenuConfig.LoadCategory("Lights/Neon Kits/Neon Color");
            if (category == null) return null;

            var menu = CreateMenu("Neon Color");
            var neonColors = category.Items;

            // Live preview: temporarily turn the neon lights ON and show the highlighted colour so the player
            // can see it before buying. On close, restore the original colour + neon on/off state (unless bought).
            void PreviewNeon(int idx)
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (idx < 0 || idx >= neonColors.Count) return;
                for (int n = 0; n < 4; n++) Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, n, true); // enable all
                int pr = neonColors[idx].R ?? 255, pg = neonColors[idx].G ?? 255, pb = neonColors[idx].B ?? 255;
                Function.Call((Hash)0x8E0A582209A62695, currentVehicle, pr, pg, pb); // SET neon colour
            }
            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                neonColorPurchased = false;
                unsafe
                {
                    int r0, g0, b0;
                    Function.Call((Hash)0x7619EEE8C886757F, currentVehicle, &r0, &g0, &b0); // GET neon colour
                    origNeonRGB[0] = r0; origNeonRGB[1] = g0; origNeonRGB[2] = b0;
                }
                for (int n = 0; n < 4; n++)
                    origNeonOn[n] = Function.Call<bool>((Hash)0x8C4B92553E4766A5, currentVehicle, n); // IS enabled
                PreviewNeon(menu.SelectedIndex);
            };
            menu.SelectedIndexChanged += (s, e) => PreviewNeon(e.Index);
            menu.Closed += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!neonColorPurchased)
                    Function.Call((Hash)0x8E0A582209A62695, currentVehicle, origNeonRGB[0], origNeonRGB[1], origNeonRGB[2]);
                for (int n = 0; n < 4; n++)
                    Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, n, origNeonOn[n]); // restore on/off
            };

            // Read the car's CURRENT neon colour so the matching swatch shows the equipped icon.
            int curR = 255, curG = 255, curB = 255;
            if (currentVehicle != null && currentVehicle.Exists())
                unsafe { int cr, cg, cb; Function.Call((Hash)0x7619EEE8C886757F, currentVehicle, &cr, &cg, &cb); curR = cr; curG = cg; curB = cb; }

            var neonItems = new List<(NativeItem item, int r, int g, int b, int price)>();
            void RefreshNeonInstalled(int r, int g, int b)
            {
                foreach (var (it, ir, ig, ib, pr) in neonItems)
                {
                    itemOwnershipStatus.Remove(it);
                    if (ir == r && ig == g && ib == b) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_INSTALLED; }
                    else it.AltTitle = pr == 0 ? "Free" : $"${pr}";
                }
            }

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                neonItems.Add((menuItem, r, g, b, price));
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    neonColorPurchased = true; // keep the colour; the on/off state still restores to the player's kit
                    ApplyNeonColor(r, g, b);
                    RefreshNeonInstalled(r, g, b);   // move the equipped icon to the bought colour
                };
                menu.Add(menuItem);
            }
            RefreshNeonInstalled(curR, curG, curB);   // initial equipped icon

            return menu;
        }

        /// <summary>
        /// Create Tire Smoke menu from MenuConfig
        /// </summary>
        private NativeMenu CreateTireSmokeMenuFromConfig()
        {
            var category = MenuConfig.LoadCategory("Wheels/Tires/Tire Smoke");
            if (category == null) return null;

            var menu = CreateMenu("Tire Smoke");

            // Track each colour item so we can move the equipped icon (the installed "tick" sprite) onto whichever
            // colour is currently on the car — the neon-colour pattern; the menu had none of this before.
            var smokeItems = new List<(NativeItem item, int r, int g, int b, int price)>();
            NativeItem offItem = null;

            void RefreshInstalled()
            {
                bool on = false; int cr = 255, cg = 255, cb = 255;
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    try { on = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 20); } catch { }
                    try
                    {
                        var ra = new OutputArgument(); var ga = new OutputArgument(); var ba = new OutputArgument();
                        Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, currentVehicle, ra, ga, ba);
                        cr = ra.GetResult<int>(); cg = ga.GetResult<int>(); cb = ba.GetResult<int>();
                    }
                    catch { }
                }
                foreach (var (it, ir, ig, ib, pr) in smokeItems)
                {
                    itemOwnershipStatus.Remove(it);
                    if (on && ir == cr && ig == cg && ib == cb) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_INSTALLED; }
                    else it.AltTitle = pr == 0 ? "Free" : $"${pr}";
                }
                if (offItem != null)
                {
                    itemOwnershipStatus.Remove(offItem);
                    if (!on) { offItem.AltTitle = ""; itemOwnershipStatus[offItem] = STATUS_INSTALLED; }
                    else offItem.AltTitle = "Free";
                }
            }

            // Stock (off) — turn tyre smoke off (the config list has no "off" entry, like the wheel-type menus).
            offItem = new NativeItem("Stock (No Smoke)", "Remove coloured tyre smoke.");
            offItem.Activated += (s, e) =>
            {
                if (EditBlockApply()) return;
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 20, false);
                    ShowNotification("~g~Tyre smoke removed");
                }
                RefreshInstalled();
            };
            menu.Add(offItem);

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                smokeItems.Add((menuItem, r, g, b, price));
                menuItem.Activated += (s, e) =>
                {
                    if (EditBlockApply()) return;
                    if (!TryPurchase(price, false)) return;
                    ApplyTireSmoke(r, g, b);
                    RefreshInstalled();
                };
                menu.Add(menuItem);
            }

            // Refresh the equipped icon whenever the menu opens (the car's smoke may have changed elsewhere).
            menu.Shown += (s, e) => RefreshInstalled();
            RefreshInstalled();
            return menu;
        }

        /// <summary>
        /// Create Tire Enhancements menu from MenuConfig
        /// </summary>
        private NativeMenu CreateTireEnhancementsMenuFromConfig()
        {
            return CreateMenuFromConfig(
                "Wheels/Tires/Tire Enhancements",
                () => currentVehicle.CanTiresBurst == false ? 1 : 0, // 0 = standard, 1 = bulletproof
                (item) => SetBulletproofTires(item.Value == 1)
            );
        }

        /// <summary>
        /// Get description from MenuConfig
        /// </summary>
        private string GetDescriptionFromConfig(string categoryPath)
        {
            return MenuConfig.GetDescription(categoryPath);
        }

        /// <summary>
        /// Get the actual description to display for an item.
        /// Checks item's built-in Description first, then falls back to MenuConfig.
        /// </summary>
        // Single source of truth for an item's description text. CRITICAL: when an item carries a native
        // LemonUI Description we capture it into customDescriptions and CLEAR the native one — LemonUI's own
        // description rendering ignores the scroll-arrow row and overlaps it, so we suppress it and redraw the
        // text ourselves (scroll-aware) in DrawCustomDescription. Capturing every call also picks up dynamic
        // description updates. Cleared per vehicle in RebuildMenusForVehicle.
        private string GetItemDescription(NativeItem item)
        {
            if (item == null) return null;
            if (!string.IsNullOrEmpty(item.Description))
            {
                customDescriptions[item] = item.Description;
                item.Description = "";
            }
            if (customDescriptions.TryGetValue(item, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
            return GetDescriptionFromConfig(item.Title);
        }

        /// <summary>
        /// Exit current debug mode and log any relevant settings
        /// </summary>
        private void ExitDebugMode()
        {
            // Log settings based on which mode was active
            if (activeDebugMode == DebugMode.Resizer)
            {
                Log($"=== RESIZER SETTINGS ===");
                Log($"Transaction (-$) Scale: {transactionTextScale:F3}");
                Log($"Description Scale: {descriptionTextScale:F3}");
                Log($"Sprite Scale: {spriteScale:F3}");
                Log($"Stats Row Height: {statsRowHeight:F4}");
                Log($"Stats Segment Height: {statsSegmentHeight:F4}");
                Log($"Stats Segment Gap: {statsSegmentGap:F4}");
                Log($"Stats Label Scale: {statsLabelScale:F3}");
                Log($"Stats Panel Padding: {statsPanelPadding:F4}");
                Log($"========================");
            }
            else if (activeDebugMode == DebugMode.InputTiming)
            {
                Log($"=== INPUT TIMING SETTINGS ===");
                Log($"Initial Delay: {inputInitialDelay}ms");
                Log($"Slow Repeat: {inputSlowRepeat}ms");
                Log($"Fast Repeat: {inputFastRepeat}ms");
                Log($"Accel Threshold: {inputAccelThreshold}");
                Log($"=============================");
            }
            else if (activeDebugMode == DebugMode.MenuPosition)
            {
                Log($"=== MENU POSITION SETTINGS ===");
                Log($"Menu Offset: {mainMenu.Offset}");
                Log($"Scroll Arrows Offset: X={scrollArrowsOffsetX:F4}, Y={scrollArrowsOffsetY:F4}");
                Log($"Description Offset: X={descriptionOffsetX:F4}, Y={descriptionOffsetY:F4}");
                Log($"Stats Panel Offset: X={statsPanelOffsetX:F4}, Y={statsPanelOffsetY:F4}");
                Log($"===============================");
            }

            // Re-enable menu input if it was disabled
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu)
                {
                    menu.AcceptsInput = true;
                }
            }

            activeDebugMode = DebugMode.None;
            debugGameInputMode = false;  // Reset game input mode
            ShowNotification("Debug Mode ~r~OFF");
        }

        #endregion
    }
}
