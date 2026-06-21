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
        #region Menu Building

        // Custom banner from PNG file
        private CustomSprite customBanner = null;
        private bool bannerLoadAttempted = false;

        /// <summary>
        /// Creates a NativeMenu with consistent settings.
        /// </summary>
        private NativeMenu CreateMenu(string title, string subtitle = null)
        {
            // If no subtitle provided, use the title as subtitle (since banner covers title area)
            var menu = new NativeMenu(title, subtitle ?? title);
            // We draw our OWN custom banner image over LemonUI's banner, and the subtitle strip already shows the menu
            // label. LemonUI's banner TITLE text is redundant and, on long titles (e.g. "Custom Suspension and Camber"),
            // it's centred and large so it spills out past the banner/menu edges. Make it transparent so it never shows.
            if (menu.BannerText != null) menu.BannerText.Color = System.Drawing.Color.FromArgb(0, 255, 255, 255);
            menu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like native GTA
            menu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            menu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            menu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
            // LemonUI mouse OFF: its click activates the highlighted item, but hover-to-highlight doesn't track the
            // cursor over ELSC's custom-drawn menu, so a click anywhere fired the wrong item. Keyboard/controller only.
            menu.UseMouse = false;
            menu.CloseOnInvalidClick = false;  // (moot with mouse off) never let a stray click close the menu
            menuPool.Add(menu);
            allSubMenus.Add(menu);   // tracked so rebuild can hide+remove every submenu (no stale/overlapping menus)

            // Track scroll position to match LemonUI's internal firstItem
            menu.Shown += (s, e) =>
            {
                menuFirstItem[menu] = 0;
                menuLastSelectedIndex[menu] = menu.SelectedIndex;
            };

            menu.SelectedIndexChanged += (s, e) =>
            {
                // Clear "not enough cash" message and restore original description
                if (showNotEnoughCash && notEnoughCashItem != null)
                {
                    notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                    notEnoughCashItem = null;
                    notEnoughCashOriginalDesc = null;
                }
                showNotEnoughCash = false;
                notEnoughCashStartTime = 0;

                // Replicate LemonUI's scroll logic
                int maxItems = menu.MaxItems;
                int totalItems = menu.Items.Count;
                int newIndex = e.Index;

                if (!menuFirstItem.TryGetValue(menu, out int firstItem)) firstItem = 0;
                if (!menuLastSelectedIndex.TryGetValue(menu, out int lastIndex)) lastIndex = 0;

                if (totalItems > maxItems)
                {
                    int lower = firstItem;
                    int upper = firstItem + maxItems;

                    if (newIndex >= lower && newIndex < upper)
                    {
                        // Still in visible range, no scroll
                    }
                    else if (newIndex == upper)
                    {
                        // Scrolled down past visible
                        firstItem++;
                    }
                    else if (newIndex == lower - 1)
                    {
                        // Scrolled up past visible
                        firstItem--;
                    }
                    else
                    {
                        // Jumped (e.g., wrap around)
                        if (newIndex < maxItems)
                            firstItem = 0;
                        else
                            firstItem = newIndex - maxItems + 1;
                    }

                    firstItem = Math.Max(0, Math.Min(firstItem, totalItems - maxItems));
                }
                else
                {
                    firstItem = 0;
                }

                menuFirstItem[menu] = firstItem;
                menuLastSelectedIndex[menu] = newIndex;
            };

            // Clear "not enough cash" message and restore original description when menu closes
            menu.Closed += (s, e) =>
            {
                if (showNotEnoughCash && notEnoughCashItem != null)
                {
                    notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                    notEnoughCashItem = null;
                    notEnoughCashOriginalDesc = null;
                }
                showNotEnoughCash = false;
                notEnoughCashStartTime = 0;
            };

            return menu;
        }

        private void BuildMainMenu()
        {
            // Create menu - empty title (custom banner), "CATEGORIES" subtitle
            mainMenu = new NativeMenu("", "CATEGORIES");
            mainMenu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like native GTA
            mainMenu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            mainMenu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            mainMenu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
            mainMenu.UseMouse = false;
            mainMenu.CloseOnInvalidClick = false;
            menuPool.Add(mainMenu);

            // Load custom banner PNG
            LoadCustomBannerFromFile();

            // Close event
            mainMenu.Closed += (sender, args) =>
            {
                if (!isNavigatingMenu)
                {
                    CloseMenu();
                }
            };
        }

        private void LoadCustomBannerFromFile()
        {
            if (bannerLoadAttempted) return;
            bannerLoadAttempted = true;

            try
            {
                string scriptsDir = AppDomain.CurrentDomain.BaseDirectory;
                string bannerPath = Path.Combine(scriptsDir, "ExtendedLSC", "banner.png");

                if (File.Exists(bannerPath))
                {
                    customBanner = new CustomSprite(bannerPath, new SizeF(100, 100), new PointF(0, 0), Color.White, 0f, false);
                    Log($"Custom banner loaded from: {bannerPath}");
                }
                else
                {
                    Log($"Banner not found at: {bannerPath}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading custom banner: {ex.Message}");
            }
        }

        // Menu position adjustment
        // (activeDebugMode == DebugMode.MenuPosition) replaced by activeDebugMode == DebugMode.MenuPosition

        // ============== LEMONUI MENU ITEM LAYOUT REFERENCE ==============
        // NativeItem:
        //   - Title property      -> renders on LEFT side of item
        //   - AltTitle property   -> renders on RIGHT side of item
        //   - Description         -> renders at bottom when item is selected
        //
        // NativeSubmenuItem:
        //   - Title (3rd param)   -> renders on RIGHT side (NOT left!)
        //   - Has arrow ">" on right
        //   - DO NOT USE if you want left-aligned category names
        //
        // Solution: Use NativeItem + manual Activated handler for submenus
        //   var item = new NativeItem("Category Name");  // LEFT side
        //   item.AltTitle = "$500";                      // RIGHT side (price)
        //   item.Activated += (s,e) => { parentMenu.Visible = false; submenu.Visible = true; };
        // ================================================================

        // Auto-offset constants for LEFT-aligned menu (text left-justified)
        // Menu appears on LEFT side of screen (native LSC position)
        private const float BASE_ASPECT = 1.7778f;   // 16:9
        private const float BASE_OFFSET_X = 0f;      // Left side of screen, minimal offset
        private const float OFFSET_SCALE_X = 0f;     // TODO: derive from ultrawide testing

        // Capture + clear the visible menu's selected-item native description before LemonUI's Process() draws
        // it (so the un-offset native description never shows). We redraw it scroll-aware in DrawCustomBanner.
        private void PreClearVisibleDescription()
        {
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    if (menu.SelectedIndex >= 0 && menu.SelectedIndex < menu.Items.Count)
                        GetItemDescription(menu.Items[menu.SelectedIndex] as NativeItem);
                    break;
                }
            }
        }

        // ---- Bottom button-hint bar (context-sensitive; real device glyphs that auto-switch Xbox/PS/keyboard) ----
        private LemonUI.Scaleform.InstructionalButtons hintBar;
        private string hintBarSig = "";   // rebuild the scaleform only when the hint set changes (not every frame)
        private LemonUI.Elements.ScaledText editKeyHint;   // yellow keyboard-hotkey strip shown in edit mode

        private LemonUI.Scaleform.InstructionalButton HintBtn(string text, int control)
            => new LemonUI.Scaleform.InstructionalButton(text, (GTA.Control)control);

        /// <summary>Build + draw the context-sensitive hint bar. menu = the visible menu (null in walk-around).</summary>
        private void DrawHintBar(NativeMenu visibleMenu, bool walkAround)
        {
            // Build the menu hint-button set, then draw it on our OWN instanced scaleform at GFX order 7
            // (same technique as the walk-around bar) so carmod_shop's shared bar can't overwrite it.
            bool editing = editModeActive;
            bool onHorn = hornMenu != null && visibleMenu == hornMenu;
            bool onNos = nosMenu != null && visibleMenu == nosMenu;
            // On a package row (Packages menu, highlighted item is a saved package): offer X = Delete.
            bool onPackage = packagesMenu != null && visibleMenu == packagesMenu
                && visibleMenu.SelectedItem is NativeItem pkgSel && _packageItemPaths.ContainsKey(pkgSel);

            var glyphs = new System.Collections.Generic.List<string>();
            var labels = new System.Collections.Generic.List<string>();

            glyphs.Add(Glyph((int)GTA.Control.FrontendAccept)); labels.Add(editing ? "Edit Name / Price" : "Select");
            glyphs.Add(Glyph((int)GTA.Control.FrontendCancel)); labels.Add("Back");
            if (onHorn) { glyphs.Add(Glyph(ModSettings.HornPreviewButton)); labels.Add("Preview"); }
            if (onNos) { glyphs.Add(Glyph(ModSettings.NosButton)); labels.Add("Preview"); }
            if (onPackage) { glyphs.Add(Glyph((int)GTA.Control.FrontendX)); labels.Add("Delete"); }
            if (ModSettings.CustomCamera) { glyphs.Add(Glyph(ModSettings.WalkAroundButton)); labels.Add(isFirstPersonActive ? "Camera: First-Person" : "Camera"); }

            DrawHintsOwn(glyphs.ToArray(), labels.ToArray());

            // Edit mode keyboard hotkeys (keyboard-only, no controller glyph) — a tidy yellow strip above the bar.
            if (editModeActive && !walkAround)
            {
                string txt = $"~y~EDIT MODE   ~w~{ModSettings.EditModeKey}~y~ Exit    ~w~{ModSettings.RenameCategoryKey}~y~ Rename Category    ~w~{ModSettings.DeleteCategoryKey}~y~ Delete / Restore";
                var pos = new System.Drawing.PointF(1080f * GTA.UI.Screen.AspectRatio / 2f, 962f);   // center, just above the hint bar
                if (editKeyHint == null)
                {
                    editKeyHint = new LemonUI.Elements.ScaledText(pos, txt, 0.32f)
                    { Alignment = GTA.UI.Alignment.Center, Color = System.Drawing.Color.White, Outline = true };
                }
                else { editKeyHint.Text = txt; editKeyHint.Position = pos; }
                editKeyHint.Draw();
            }
        }

        // ── Walk-around hint bar on our OWN instanced scaleform ──────────────────────────────
        // Inside the vanilla LSC, carmod_shop owns the SHARED "instructional_buttons" movie and
        // overwrites LemonUI's hint bar (same handle → last SET_DATA_SLOT wins → flicker). We
        // request a SEPARATE instance so our content can't be clobbered, then draw it at GFX
        // order 7 so it renders on top of the game's bar. Fully self-managed, no LemonUI.
        private int _hintSf = -1;   // one instance, reused by the menu + walk-around hint bars (mutually exclusive)

        // Glyph token(s) for a control as shown in instructional-button boxes (current input device).
        private string Glyph(int control)
            => Function.Call<string>(Hash.GET_CONTROL_INSTRUCTIONAL_BUTTONS_STRING, 2, control, true);

        // Draw a button list (pre-built glyph strings; a slot may concatenate two glyphs, e.g. LB+RB) on OUR
        // instanced scaleform at GFX order 7 (on top of carmod_shop's bar).
        private void DrawHintsOwn(string[] glyphs, string[] labels)
        {
            // carmod_shop draws ITS OWN instructional_buttons instance (a private handle, not the shared movie), so we
            // can't blank it. It renders FULLSCREEN → stretched glyphs on ultrawide. We can't reach its handle, so we
            // MASK it: a full-width dark band (DRAW_RECT, normalized 0-1 → always full-width on any aspect) painted at
            // GFX order 7 hides carmod's stretched bar, then our compact aspect-correct bar draws on top. (See below.)

            if (_hintSf <= 0)
            {
                _hintSf = Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE_INSTANCE, "instructional_buttons");
                return;   // give it a frame to load
            }
            if (!Function.Call<bool>(Hash.HAS_SCALEFORM_MOVIE_LOADED, _hintSf)) return;

            int sf = _hintSf;

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "CLEAR_ALL");
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "TOGGLE_MOUSE_BUTTONS");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_BOOL, false);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            for (int i = 0; i < glyphs.Length; i++)
                SfButton(sf, i, glyphs[i], labels[i]);

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "DRAW_INSTRUCTIONAL_BUTTONS");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 0);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            // Render on top of carmod_shop's bar (layer 7), then restore the default layer.
            // The button movie's stage is 16:9; DRAW_SCALEFORM_MOVIE_FULLSCREEN stretched it across the whole screen,
            // so the glyphs looked wide on ultrawide. Draw it in an aspect-correct region instead, right-anchored
            // (where the buttons sit), so on 16:9 it's identical (full screen) and on wider screens it stays 16:9.
            float hw = Math.Min(1f, (1280f / 720f) / ActualAspect());   // 1.0 at 16:9, narrower on wider screens
            Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, 7);
            // Full-width dark band masks carmod_shop's stretched default bar (DRAW_RECT is normalized 0-1, so it's
            // full-width on any aspect — same coverage the old fullscreen scaleform gave). Then our compact, aspect-
            // correct bar draws on top. No-op-looking at 16:9 (our bar already covers it), needed on wider screens.
            if (hw < 0.999f)
                Function.Call(Hash.DRAW_RECT, 0.5f, 0.967f, 1f, 0.052f, 0, 0, 0, 255, 0);
            Function.Call(Hash.DRAW_SCALEFORM_MOVIE, sf, 1f - hw / 2f, 0.5f, hw, 1f, 255, 255, 255, 255, 0);
            Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, 0);
        }

        private void DrawWalkAroundHints()
        {
            // Rev is RT on controller (VehicleAccelerate) but the keyboard 'R' key (W moves now), so the glyph is
            // device-specific — the VehicleAccelerate glyph would wrongly show "W" on keyboard.
            bool kbm = Game.LastInputMethod == InputMethod.MouseAndKeyboard;
            string revGlyph = kbm ? "R" : Glyph((int)GTA.Control.VehicleAccelerate);

            // "Cycle View" shows BOTH bumpers (LB prev / RB next) as two adjacent slots — concatenating the
            // two control-glyph blobs into one slot renders blank, so each gets its own slot, label on RB.
            // Steering hint (D-pad Left/Right) only when the inspect-steering feature is enabled.
            if (ModSettings.KeepSteeringAngle)
            {
                // D-pad Left/Right steers the wheels only when UNLOCKED (does nothing when locked). The hint shows the
                // wheel label only in that mode; the R3 toggle label reflects the current state.
                string dpadLabel = walkWheelsLocked ? "" : "Turn Wheels";
                string lockLabel = walkWheelsLocked ? "Unlock Wheels" : "Lock Wheels";
                DrawHintsOwn(
                    new[] { Glyph(ModSettings.CamPrevButton),
                            Glyph(ModSettings.CamNextButton),
                            revGlyph,
                            Glyph((int)GTA.Control.VehicleMoveLeftRight),
                            Glyph((int)GTA.Control.LookLeftRight),
                            Glyph((int)GTA.Control.FrontendLeft),
                            Glyph((int)GTA.Control.FrontendRight),
                            Glyph((int)GTA.Control.FrontendRs),
                            Glyph(ModSettings.DoorButton),
                            Glyph(ModSettings.WalkAroundButton) },
                    new[] { "", "Cycle View", "Rev", "Move", "Look / Height", "", dpadLabel, lockLabel, "Open Door", "Next Cam" });
                return;
            }

            DrawHintsOwn(
                new[] { Glyph(ModSettings.CamPrevButton),
                        Glyph(ModSettings.CamNextButton),
                        revGlyph,
                        Glyph((int)GTA.Control.VehicleMoveLeftRight),
                        Glyph((int)GTA.Control.LookLeftRight),
                        Glyph(ModSettings.DoorButton),
                        Glyph(ModSettings.WalkAroundButton) },
                new[] { "", "Cycle View", "Rev", "Move", "Look / Height", "Open Door", "Next Cam" });
        }

        private void SfButton(int sf, int index, string glyph, string label)
        {
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "SET_DATA_SLOT");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, index);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_PLAYER_NAME_STRING, glyph);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_SCALEFORM_STRING, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, label);
            Function.Call(Hash.END_TEXT_COMMAND_SCALEFORM_STRING);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
        }

        private void DrawCustomBanner()
        {
            // Find any visible menu in the pool to get banner position
            NativeMenu visibleMenu = null;
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    visibleMenu = menu;
                    break;
                }
            }

            if (visibleMenu == null) return;

            // Draw custom banner on all menus
            if (customBanner != null)
            {
                // Get LemonUI banner position to align our custom sprite
                var menuBanner = visibleMenu.Banner;
                if (menuBanner != null)
                {
                    // Convert LemonUI coordinates to CustomSprite coordinates
                    var spritePos = LemonUIToCustomSprite(menuBanner.Position);
                    var spriteSize = LemonUIToCustomSpriteSize(menuBanner.Size);

                    customBanner.Position = new PointF(spritePos.X + bannerOffsetX, spritePos.Y + bannerOffsetY);
                    customBanner.Size = new SizeF(spriteSize.Width * bannerScaleW, spriteSize.Height * bannerScaleH);
                    customBanner.Draw();
                }
            }

            // Draw scroll indicators (for all menus)
            DrawScrollIndicators(visibleMenu);

            // Draw tick icons for installed items
            DrawTicks(visibleMenu);

            // Draw custom description below scroll indicator (or below items if no scroll indicator)
            DrawCustomDescription(visibleMenu);

            // Draw vehicle stats bars below description (LSC style)
            DrawVehicleStats(visibleMenu);

            // Context-sensitive button-hint bar at the bottom of the screen — drawn LAST because DrawHintsOwn
            // sets the script GFX draw order to 7 (to sit on top of carmod_shop's bar) and resets it to 0.
            // Doing it before the icons/stats would draw THEM at order 0 too, dimming them (the greyed-icon bug).
            // In walk-around, UpdateWalkAround draws its own hint bar; drawing one here too would flip-flop the
            // scaleform signature every frame.
            // Skip the hint bar while the idle cinematic is active — its instructional-button scaleform draws above a
            // plain DRAW_RECT, so it would poke through the fake-fade overlay.
            if (!isWalkAroundActive && _idlePhase == 0) DrawHintBar(visibleMenu, false);

            // Note: Menu position adjustment is handled in OnTick for MenuPosition debug mode
            // with element-specific controls (see switch on selectedUIElement)
        }

        /// <summary>
        /// Draw dark overlay with text for manual transmission key binding
        /// </summary>
        private void DrawMTBindingOverlay()
        {
            if (mtBindingState <= 0 && nosBindingState <= 0) return;
            bool nosBind = nosBindingState > 0;

            // Draw semi-transparent dark background covering most of screen
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.0f, 1.0f, 0, 0, 0, 200);

            // Draw title text
            string title = nosBind ? "NITROUS SETUP" : "MANUAL TRANSMISSION SETUP";
            Function.Call(Hash.SET_TEXT_FONT, 4); // Pricedown font
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.8f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, title);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.35f);

            // Draw instruction text
            string instruction = nosBind
                ? "Press a key or button to SPRAY NITROUS"
                : (mtBindingState == 1 ? "Press a key or button for SHIFT UP" : "Press a key or button for SHIFT DOWN");

            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.5f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 200, 0, 255); // Yellow
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, instruction);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.45f);

            // Draw hint text
            string hint = "Press ESC / B to cancel";
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.35f);
            Function.Call(Hash.SET_TEXT_COLOUR, 150, 150, 150, 255); // Gray
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, hint);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.55f);
        }

        private void DrawScrollIndicators(NativeMenu menu)
        {
            if (!ModSettings.ShowScrollIndicators) return;
            if (menu == null || !menu.Visible) return;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;

            // No need for scroll indicators if all items fit
            if (totalItems <= maxVisible) return;

            // Request the CommonMenu texture dictionary (needed for arrows sprite)
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, "CommonMenu"))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, "CommonMenu", false);
                return; // Wait for next frame
            }

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)

            // LemonUI uses 1080p base coordinates
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            // Menu banner gives us the top-left position
            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            // Calculate center X position of menu (in normalized 0-1 coords)
            float menuCenterX = (bannerPos.X + bannerSize.Width / 2f) / lemonXBase;

            // Calculate Y position below the visible items (for the scroller indicator)
            // Each item is about 38 pixels in 1080p base, plus subtitle area
            float subtitleHeight = 38f;
            float itemHeight = 38f;

            // Calculate bottom of visible items area
            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;
            float indicatorY = menuTopY + visibleItemsHeight + 0.0173f; // Center of indicator bar

            // Calculate menu width in normalized coords
            float menuWidth = bannerSize.Width / lemonXBase;

            // Apply scroll arrows offset and scale for independent positioning
            float drawCenterX = menuCenterX + scrollArrowsOffsetX;
            float drawIndicatorY = indicatorY + scrollArrowsOffsetY;
            float drawWidth = menuWidth * scrollArrowsScaleW;
            float drawHeight = 0.035f * scrollArrowsScaleH;

            // Draw background rect for scroll indicator (for the scroller indicator rect)
            Function.Call(Hash.DRAW_RECT, drawCenterX, drawIndicatorY, drawWidth, drawHeight, 0, 0, 0, 200);

            // Get texture resolution for proper scaling
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_arrows_upANDdown");
            // Width base must be 1080*actualAspect (not a fixed 1920) or the sprite stretches wide on non-16:9.
            float spriteW = (textureRes.X / (1080f * ActualAspect() * 2f)) * arrowSpriteScale;
            float spriteH = (textureRes.Y / (1080f * 2f)) * arrowSpriteScale;

            // Draw the up/down arrows sprite twice for bolder appearance
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                drawCenterX,
                drawIndicatorY,
                spriteW,
                spriteH,
                0f,
                255, 255, 255, 255);

            // Second pass for bolder look
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                drawCenterX,
                drawIndicatorY,
                spriteW,
                spriteH,
                0f,
                255, 255, 255, 255);
        }

        /// <summary>
        /// Draw tick icons for installed items (replaces "" text)
        /// </summary>
        private void DrawTicks(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (menu.Items.Count == 0) return;

            // Request the CommonMenu texture dictionary (has garage icons)
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, "CommonMenu"))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, "CommonMenu", false);
                return;
            }

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            float subtitleHeight = 38f;
            float itemHeight = LEMON_ITEM_HEIGHT; // matches LemonUI exactly (fallback only; we use lastPosition)

            // Menu right edge X position (normalized) - offset to keep sprite inside menu
            float menuRightX = (bannerPos.X + bannerSize.Width - 28f) / lemonXBase;

            // First visible item Y position (use 38 for initial offset, 37.5 for per-item)
            float firstItemY = (bannerPos.Y + bannerSize.Height + subtitleHeight + 38f / 2f) / lemonYBase;

            // Get garage icon sprite size
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_garage_icon_a");
            // Width base must be 1080*actualAspect (not a fixed 1920) or the icon stretches wide on non-16:9.
            float spriteW = textureRes.X / (1080f * ActualAspect() * 2.5f);
            float spriteH = textureRes.Y / (1080f * 2.5f);

            // Calculate which items are visible
            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            int selectedIndex = menu.SelectedIndex;

            // Use tracked scroll position (matches LemonUI's internal firstItem)
            int firstVisibleIndex = 0;
            if (menuFirstItem.TryGetValue(menu, out int tracked))
                firstVisibleIndex = tracked;
            else if (totalItems > maxVisible)
            {
                // Fallback: approximate if not tracked yet
                firstVisibleIndex = Math.Max(0, selectedIndex - maxVisible + 1);
                firstVisibleIndex = Math.Min(firstVisibleIndex, totalItems - maxVisible);
            }

            // Draw icons for each visible item based on ownership/installation status
            int visibleCount = Math.Min(maxVisible, totalItems);
            for (int i = 0; i < visibleCount; i++)
            {
                int itemIndex = firstVisibleIndex + i;
                if (itemIndex >= totalItems) break;

                var item = menu.Items[itemIndex] as NativeItem;
                if (item == null) continue;

                // Check ownership status from dictionary
                if (itemOwnershipStatus.TryGetValue(item, out int status))
                {
                    // Prefer LemonUI's EXACT drawn position (no drift); fall back to the row estimate.
                    float itemY = TryGetItemCenterY(item, lemonYBase, out float exactY)
                        ? exactY
                        : firstItemY + (i * itemHeight / lemonYBase);
                    bool isHovered = (itemIndex == selectedIndex);

                    // Determine sprite based on status and hover state
                    // Installed + hovered: shop_garage_icon_b (more visible)
                    // Installed + not hovered: shop_garage_icon_a
                    // Owned but not installed: shop_tick_icon (tinted black when hovered for visibility)
                    string spriteName;
                    int r = 255, g = 255, b = 255; // Default white tint

                    if (status == STATUS_INSTALLED)
                    {
                        spriteName = isHovered ? "shop_garage_icon_b" : "shop_garage_icon_a";
                    }
                    else // STATUS_OWNED
                    {
                        spriteName = "shop_tick_icon";
                        if (isHovered)
                        {
                            r = 0; g = 0; b = 0; // Black tint when hovered
                        }
                    }

                    Function.Call(Hash.DRAW_SPRITE,
                        "CommonMenu",
                        spriteName,
                        menuRightX,
                        itemY,
                        spriteW * spriteScale,
                        spriteH * spriteScale,
                        0f,
                        r, g, b, 255);
                }
            }
        }

        /// <summary>
        /// Draw custom description below the scroll indicator
        /// </summary>
        private void DrawCustomDescription(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (menu.SelectedIndex < 0 || menu.SelectedIndex >= menu.Items.Count) return;

            // Check if "not enough cash" message should be shown
            bool showingNotEnoughCash = false;
            if (showNotEnoughCash)
            {
                int elapsed = Game.GameTime - notEnoughCashStartTime;
                if (elapsed < NOT_ENOUGH_CASH_DURATION)
                {
                    showingNotEnoughCash = true;
                }
                else
                {
                    // Timer expired, reset state
                    showNotEnoughCash = false;
                    notEnoughCashStartTime = 0;
                }
            }

            var selectedItem = menu.Items[menu.SelectedIndex];

            // Resolve the text to draw via the SINGLE renderer so it is always positioned below the scroll
            // arrows: a transient "not enough cash" message overrides; otherwise the item's own description
            // (GetItemDescription captures + clears the native one so LemonUI doesn't draw it un-offset).
            string itemDesc = GetItemDescription(selectedItem);
            string description = showingNotEnoughCash ? "Sorry - you cannot afford this item." : itemDesc;

            // If we have a description OR debugging description, draw our custom box
            bool isDebugEditingDesc = (activeDebugMode == DebugMode.Resizer) && resizerFieldIndex < activeResizerFields.Count && activeResizerFields[resizerFieldIndex] == "Description Text";
            if (string.IsNullOrEmpty(description) && !isDebugEditingDesc) return;

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            // Calculate positions
            float subtitleHeight = 38f;
            float itemHeight = 38f;
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;

            // Description starts below scroll indicator (or below items if no scroll indicator)
            float descY = menuTopY + visibleItemsHeight;
            if (hasScrollIndicator)
            {
                descY += 0.0173f + scrollIndicatorHeight / 2f + 0.01f; // Below scroll indicator
            }
            else
            {
                descY += 0.01f; // Small gap below items
            }

            // Calculate menu left edge and width
            float menuLeftX = bannerPos.X / lemonXBase;
            float menuWidth = bannerSize.Width / lemonXBase;
            float menuCenterX = menuLeftX + menuWidth / 2f;

            // Apply description offset and scale for independent positioning
            float drawDescCenterX = menuCenterX + descriptionOffsetX;
            float drawDescLeftX = menuLeftX + descriptionOffsetX;
            float drawDescY = descY + descriptionOffsetY;
            float drawDescWidth = menuWidth * descriptionScaleW;

            // Calculate dynamic height based on text content
            float drawDescHeight = GetDescriptionHeight(menu, menuWidth, menuLeftX);
            // Fallback to standard single-line height (matches the calculated formula: padding + lineHeight)
            if (drawDescHeight == 0f) drawDescHeight = (0.012f + 0.018f) * descriptionScaleH;

            // Draw description background
            Function.Call(Hash.DRAW_RECT, drawDescCenterX, drawDescY + drawDescHeight / 2f, drawDescWidth, drawDescHeight, 0, 0, 0, 200);

            // Determine text and color
            string displayText;
            int textR, textG, textB;

            // Check if we're in debug mode editing the description text (reuse local var)

            if (showingNotEnoughCash)
            {
                displayText = "Sorry - you cannot afford this item.";
                textR = 255; textG = 255; textB = 255; // White
            }
            else
            {
                // Use actual description, but yellow if in debug mode editing description
                displayText = string.IsNullOrEmpty(description) ? "No description available." : description;
                if (isDebugEditingDesc)
                {
                    textR = 255; textG = 255; textB = 0; // Bright yellow when editing
                }
                else
                {
                    textR = 255; textG = 255; textB = 255; // White
                }
            }

            // Draw description text
            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, descriptionTextScale);
            Function.Call(Hash.SET_TEXT_COLOUR, textR, textG, textB, 255);
            Function.Call(Hash.SET_TEXT_WRAP, drawDescLeftX + 0.005f, drawDescLeftX + drawDescWidth - 0.005f);
            Function.Call(Hash.SET_TEXT_LEADING, 0);

            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, displayText);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, drawDescLeftX + 0.005f, drawDescY + 0.005f);
        }

        /// <summary>
        /// Get the actual height of the description box based on text content
        /// </summary>
        private float GetDescriptionHeight(NativeMenu menu, float menuWidth, float menuLeftX)
        {
            if (menu == null || menu.SelectedIndex < 0 || menu.SelectedIndex >= menu.Items.Count)
                return 0.045f; // Default single line height

            // Get the description text (checks item.Description first, then MenuConfig)
            var selectedItem = menu.Items[menu.SelectedIndex];
            string description = GetItemDescription(selectedItem);

            // For "not enough cash", use standard single-line height (don't resize box)
            if (showNotEnoughCash)
            {
                int elapsed = Game.GameTime - notEnoughCashStartTime;
                if (elapsed < NOT_ENOUGH_CASH_DURATION)
                {
                    // Use standard single-line height for error message
                    float errorLineHeight = 0.018f * descriptionScaleH;
                    float errorPadding = 0.012f * descriptionScaleH;
                    return errorPadding + errorLineHeight;
                }
            }

            if (string.IsNullOrEmpty(description))
                return 0f; // No description, no height

            // Estimate line count based on text length and available width
            // At descriptionTextScale ~0.35, roughly 38 chars fit per line in menu width
            int charsPerLine = 38;
            int lineCount = (int)Math.Ceiling((double)description.Length / charsPerLine);
            lineCount = Math.Max(1, Math.Min(lineCount, 5)); // Clamp between 1-5 lines

            // Calculate height: base padding + line height per line
            float lineHeight = 0.018f * descriptionScaleH;
            float padding = 0.012f * descriptionScaleH;
            return padding + (lineHeight * lineCount);
        }

        /// <summary>
        /// Draw vehicle performance stats bars below the description (LSC style)
        /// </summary>
        private void DrawVehicleStats(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            // Track engine-swap preview purely from the swap menu's live visibility (no sticky flags to get
            // stranded). When that menu isn't the one on screen, there is no swap preview.
            isPreviewingSwap = engineSwapMenu != null && menu == engineSwapMenu;
            if (isPreviewingSwap) UpdateSwapPreview(menu.SelectedIndex);

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            // Calculate menu layout
            float subtitleHeight = 38f;
            float itemHeight = 38f;
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;

            // Calculate Y position below description
            // Menu dimensions (apply position offsets from debug mode)
            float menuLeftX = bannerPos.X / lemonXBase + statsPanelOffsetX;
            float menuWidth = bannerSize.Width / lemonXBase;
            float menuCenterX = menuLeftX + menuWidth / 2f;

            float descY = menuTopY + visibleItemsHeight;
            if (hasScrollIndicator)
                descY += 0.0173f + scrollIndicatorHeight / 2f + 0.01f;
            else
                descY += 0.01f;

            // Get dynamic description height based on text content
            float descHeight = GetDescriptionHeight(menu, menuWidth, menuLeftX - statsPanelOffsetX);

            // Position stats below description if it exists, otherwise directly below menu/arrows
            float statsStartY;
            if (descHeight > 0f)
            {
                // Has description - position below it
                statsStartY = descY + descHeight + descriptionOffsetY + 0.005f + statsPanelOffsetY;
            }
            else
            {
                // No description - position directly below menu items/arrows
                statsStartY = descY + statsPanelOffsetY;
            }

            // Stats panel dimensions (use resizer debug values and scale)
            // Footer band holds the performance class chip below the 4 stat rows.
            float chipBandHeight = statsRowHeight * 1.05f * statsPanelScaleH;
            float statPanelHeight = (statsRowHeight * 4 + statsPanelPadding) * statsPanelScaleH + chipBandHeight;
            float statPanelWidth = menuWidth * statsPanelScaleW;

            // Draw stats background panel
            Function.Call(Hash.DRAW_RECT, menuCenterX, statsStartY + statPanelHeight / 2f, statPanelWidth, statPanelHeight, 0, 0, 0, 200);

            // Get vehicle stats
            // SPEED: raw fInitialDriveMaxFlatVel from memory (engine/brake/trans mods don't change top speed —
            // verified). ACCEL/BRAKING/TRACTION: the NATIVES return the base handling field multiplied by
            // installed performance-mod effects (and reflect live base-handling tuning too), so the bars MOVE and
            // the blue/red purchase preview works. At stock the natives EQUAL the raw fields (verified), so the
            // empirically-derived formula is calibrated for them.
            float hMaxVel = WheelFitment.WheelMemory.GetHandlingFloat(currentVehicle, WheelFitment.WheelMemory.HOFF_MAX_FLAT_VEL);
            float nAccel = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
            float nBrake = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
            float nTraction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);

            // Installed engine-swap display bonuses (EnginePowerMultiplier physics isn't read by the natives).
            float instAcc = activeEngineSwap != null ? activeEngineSwap.AccelBonus : 0f;
            float instSpd = activeEngineSwap != null ? activeEngineSwap.SpeedBonus : 0f;

            // RAW (uncapped) fractions. base* = the white "installed" bars; live* = the blue/red preview bars.
            float baseSpeed = RawStatPct(LSCStat.TopSpeed, hMaxVel) + instSpd;
            float baseAccel = RawStatPct(LSCStat.Accel, nAccel) + instAcc;
            float baseBrake = RawStatPct(LSCStat.Braking, nBrake);
            float baseTrac = RawStatPct(LSCStat.Traction, nTraction);
            float liveSpeed = baseSpeed, liveAccel = baseAccel, liveBrake = baseBrake, liveTrac = baseTrac;

            bool statPreviewing = false;
            if (isPreviewingSwap)
            {
                // White = currently installed swap (base*), blue/red = hovered swap. Brakes/traction unchanged.
                statPreviewing = true;
                float nsSpeed = RawStatPct(LSCStat.TopSpeed, hMaxVel);
                float nsAccel = RawStatPct(LSCStat.Accel, nAccel);
                liveSpeed = nsSpeed + swapPreviewSpeedBonus;
                liveAccel = nsAccel + swapPreviewAccelBonus;
            }
            else if (isPreviewingMod && previewModIndex >= 0 && hasStoredOriginalStats)
            {
                // Performance mod indices: Engine=11, Brakes=12, Transmission=13, Suspension=15, Armor=16, Turbo=18
                bool isPerformanceMod = previewModIndex == 11 || previewModIndex == 12 ||
                                         previewModIndex == 13 || previewModIndex == 15 ||
                                         previewModIndex == 16 || previewModIndex == 18;
                if (isPerformanceMod)
                {
                    statPreviewing = true;
                    // live (blue/red) = current natives (with the previewed mod) + installed swap (already base*)
                    // base (white) = stored originals + installed swap bonus
                    baseSpeed = RawStatPct(LSCStat.TopSpeed, originalTopSpeed) + instSpd;
                    baseAccel = RawStatPct(LSCStat.Accel, originalAcceleration) + instAcc;
                    baseBrake = RawStatPct(LSCStat.Braking, originalBraking);
                    baseTrac = RawStatPct(LSCStat.Traction, originalTraction);
                }
            }

            float topSpeedPct = Clamp01(baseSpeed);
            float accelPct = Clamp01(baseAccel);
            float brakingPct = Clamp01(baseBrake);
            float tractionPct = Clamp01(baseTrac);
            float previewTopSpeedPct = statPreviewing ? Clamp01(liveSpeed) : -1f;
            float previewAccelPct = statPreviewing ? Clamp01(liveAccel) : -1f;
            float previewBrakingPct = statPreviewing ? Clamp01(liveBrake) : -1f;
            float previewTractionPct = statPreviewing ? Clamp01(liveTrac) : -1f;

            // Draw each stat row (apply width scale to internal elements)
            float currentY = statsStartY + statsContentOffsetY;
            // The 0.095/0.105 label gap + insets below were tuned at the 16:9 menu width (~0.2255). menuWidth is
            // now aspect-correct (smaller on ultrawide), so scale these X constants by wScale (=1.0 at 16:9) or the
            // bars get crushed into a sliver on the right of the panel.
            float wScale = 1920f / lemonXBase;
            float labelX = menuLeftX + statsTextLeftPadding * wScale;  // Adjustable left padding for text
            float barStartX = menuLeftX + 0.095f * wScale * statsPanelScaleW + statsBarInset * wScale; // Bar starts after label, plus inset
            float barWidth = (menuWidth - 0.105f * wScale) * statsPanelScaleW - (statsBarInset * wScale * 2); // Remaining width minus inset from both sides
            float rowHeight = statsRowHeight * statsPanelScaleH;

            DrawStatRow("Top Speed", topSpeedPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewTopSpeedPct);
            currentY += rowHeight;
            DrawStatRow("Acceleration", accelPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewAccelPct);
            currentY += rowHeight;
            DrawStatRow("Braking", brakingPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewBrakingPct);
            currentY += rowHeight;
            DrawStatRow("Traction", tractionPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewTractionPct);

            // performance class chip (footer). PI uses RAW uncapped stats (incl. engine-swap bonuses) so
            // upgrades/swaps can bump the class. base* = installed, live* = previewed → chip shows "B -> A".
            int basePI = ComputePI(baseSpeed, baseAccel, baseBrake, baseTrac);
            int livePI = statPreviewing ? ComputePI(liveSpeed, liveAccel, liveBrake, liveTrac) : basePI;
            float chipCenterY = statsStartY + statPanelHeight - chipBandHeight / 2f;
            DrawClassChip(menuLeftX, statPanelWidth, menuCenterX, chipCenterY, chipBandHeight,
                          basePI, livePI, statPreviewing);
        }

        /// <summary>
        /// Draw the performance class chip: a coloured class-letter badge + "PERFORMANCE" label on the left and
        /// the PI number on the right. When previewing a performance mod it shows the class/PI transition (the
        /// resulting class colours the badge; the PI is drawn blue for a gain / red for a loss).
        /// </summary>
        private void DrawClassChip(float panelLeftX, float panelWidth, float panelCenterX, float centerY,
                                   float bandHeight, int basePI, int newPI, bool previewing)
        {
            bool changed = previewing && newPI != basePI;
            int shownPI = previewing ? newPI : basePI;

            string baseName; int br, bg, bb; GetVehicleClassInfo(basePI, out baseName, out br, out bg, out bb);
            string newName; int nr, ng, nb; GetVehicleClassInfo(shownPI, out newName, out nr, out ng, out nb);

            float pad = 0.007f * statsPanelScaleW;

            // --- Class badge (coloured square with the class letter) ---
            float boxSize = bandHeight * 0.62f;
            float boxCenterX = panelLeftX + pad + boxSize / 2f;
            Function.Call(Hash.DRAW_RECT, boxCenterX, centerY, boxSize, boxSize, nr, ng, nb, 255);
            // class letter centred in the badge (GTA text Y is the glyph top, so raise it ~0.62*box to centre)
            Function.Call(Hash.SET_TEXT_FONT, 1);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, bandHeight * 11.5f);
            Function.Call(Hash.SET_TEXT_COLOUR, 10, 10, 10, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, newName);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, boxCenterX, centerY - boxSize * 0.62f);

            // --- "CLASS" / transition label to the right of the badge ---
            float textX = boxCenterX + boxSize / 2f + pad;
            float labelY = centerY - bandHeight * 0.30f;
            string classLabel = changed ? (baseName + " → " + newName) : (newName + "-CLASS");
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, statsLabelScale);
            if (changed) Function.Call(Hash.SET_TEXT_COLOUR, nr, ng, nb, 255);
            else Function.Call(Hash.SET_TEXT_COLOUR, 235, 235, 235, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, false);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, classLabel);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, textX, labelY);

            // --- PI number, right-aligned in the band ---
            int piColR = 235, piColG = 235, piColB = 235;
            if (changed) { if (newPI > basePI) { piColR = 0; piColG = 150; piColB = 255; } else { piColR = 255; piColG = 50; piColB = 50; } }
            string piText = (changed ? (basePI + " → " + newPI) : shownPI.ToString()) + " PI";
            float rightX = panelLeftX + panelWidth - pad;
            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, statsLabelScale);
            Function.Call(Hash.SET_TEXT_COLOUR, piColR, piColG, piColB, 255);
            Function.Call(Hash.SET_TEXT_WRAP, 0f, rightX);
            Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, piText);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0f, labelY);
            Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, false);
        }

        /// <summary>
        /// Draw a single stat row with label and segmented bar (supports partial segment fills)
        /// </summary>
        private void DrawStatRow(string label, float percentage, float labelX, float barStartX, float barWidth, float y, float barYOffset, float previewPercentage = -1f)
        {
            // Draw label text (use resizer debug value for scale)
            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, statsLabelScale);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, label);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, labelX, y);

            // Draw segmented bar (5 segments like real LSC, with partial fill support)
            int totalSegments = 5;
            float fillAmount = percentage * totalSegments; // e.g., 3.65 means 3 full + 65% of 4th
            float previewFillAmount = previewPercentage >= 0 ? previewPercentage * totalSegments : -1f;

            // Use resizer debug values for segment dimensions
            float segmentWidth = barWidth / totalSegments;
            float segmentY = y + 0.007f + barYOffset; // Center vertically with text, plus adjustable offset

            for (int i = 0; i < totalSegments; i++)
            {
                float segLeftX = barStartX + (i * segmentWidth);
                float segCenterX = segLeftX + segmentWidth / 2f;
                float actualSegWidth = segmentWidth - statsSegmentGap;

                // Calculate how much of this segment is filled (0.0 to 1.0)
                float segmentFill = Math.Max(0f, Math.Min(1f, fillAmount - i));
                float previewSegmentFill = previewFillAmount >= 0 ? Math.Max(0f, Math.Min(1f, previewFillAmount - i)) : -1f;

                // Draw empty background first (gray)
                Function.Call(Hash.DRAW_RECT, segCenterX, segmentY, actualSegWidth, statsSegmentHeight, 100, 100, 100, 200);

                if (previewFillAmount >= 0)
                {
                    // Preview mode - show current fill, then overlay preview changes
                    float currentFill = segmentFill;
                    float newFill = previewSegmentFill;

                    if (currentFill > 0)
                    {
                        // Draw current fill (white) - partial width from left
                        float fillWidth = actualSegWidth * currentFill;
                        float fillX = segLeftX + (statsSegmentGap / 2f) + fillWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, fillX, segmentY, fillWidth, statsSegmentHeight, 255, 255, 255, 255);
                    }

                    if (newFill > currentFill)
                    {
                        // Gaining - draw blue for the gain portion
                        float gainStart = currentFill;
                        float gainEnd = newFill;
                        float gainWidth = actualSegWidth * (gainEnd - gainStart);
                        float gainX = segLeftX + (statsSegmentGap / 2f) + (actualSegWidth * gainStart) + gainWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, gainX, segmentY, gainWidth, statsSegmentHeight, 0, 150, 255, 255);
                    }
                    else if (newFill < currentFill)
                    {
                        // Losing - draw red for the loss portion (replacing part of white)
                        float lossStart = newFill;
                        float lossEnd = currentFill;
                        float lossWidth = actualSegWidth * (lossEnd - lossStart);
                        float lossX = segLeftX + (statsSegmentGap / 2f) + (actualSegWidth * lossStart) + lossWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, lossX, segmentY, lossWidth, statsSegmentHeight, 255, 50, 50, 255);
                    }
                }
                else
                {
                    // Normal mode - draw partial white fill
                    if (segmentFill > 0)
                    {
                        float fillWidth = actualSegWidth * segmentFill;
                        float fillX = segLeftX + (statsSegmentGap / 2f) + fillWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, fillX, segmentY, fillWidth, statsSegmentHeight, 255, 255, 255, 255);
                    }
                }
            }
        }

        /// <summary>
        /// Sprite browser for finding icons - toggle with F6
        /// </summary>
        private void DrawSpriteBrowser()
        {
            if (!(activeDebugMode == DebugMode.SpriteBrowser)) return;

            var (dictName, sprites) = SpriteDictionaries[spriteBrowserDictIndex];

            // Request texture dictionary
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, dictName))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, dictName, false);
            }

            // Draw background
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 0.5f, 0.6f, 0, 0, 0, 200);

            // Draw title
            Function.Call(Hash.SET_TEXT_FONT, 1);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.6f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 100, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"SPRITE BROWSER - {dictName} ({spriteBrowserDictIndex + 1}/{SpriteDictionaries.Length})");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.22f);

            // Draw controls hint
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.35f);
            Function.Call(Hash.SET_TEXT_COLOUR, 200, 200, 200, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "PgUp/PgDn: pages | Home/End: dictionaries | F6: close");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.26f);

            // Calculate page info
            int startIdx = spriteBrowserPage * 10;
            int endIdx = Math.Min(startIdx + 10, sprites.Length);
            int totalPages = (sprites.Length + 9) / 10;

            // Draw page info
            Function.Call(Hash.SET_TEXT_COLOUR, 150, 150, 150, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"Page {spriteBrowserPage + 1}/{totalPages}");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.74f);

            // Draw sprites in a grid (2 columns, 5 rows)
            float startY = 0.32f;
            float rowHeight = 0.08f;
            float col1X = 0.35f;
            float col2X = 0.65f;

            for (int i = startIdx; i < endIdx; i++)
            {
                int localIdx = i - startIdx;
                int row = localIdx / 2;
                int col = localIdx % 2;
                float x = col == 0 ? col1X : col2X;
                float y = startY + row * rowHeight;

                string spriteName = sprites[i];

                // Draw sprite
                Function.Call(Hash.DRAW_SPRITE,
                    dictName,
                    spriteName,
                    x - 0.08f,
                    y,
                    0.04f,
                    0.04f,
                    0f,
                    255, 255, 255, 255);

                // Draw index and name
                Function.Call(Hash.SET_TEXT_FONT, 0);
                Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.3f);
                Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
                Function.Call(Hash.SET_TEXT_CENTRE, false);
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"[{i}] {spriteName}");
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x - 0.05f, y - 0.012f);
            }
        }

        private void AdjustMenuPosition()
        {
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)

            var offset = mainMenu.Offset;

            // Movement controls only in debug mode
            if ((activeDebugMode == DebugMode.MenuPosition))
            {
                float fastSpeed = 2f;   // Left stick
                float fineSpeed = 0.5f; // D-pad

                // Left stick - fast movement
                float lsX = Game.GetControlValueNormalized(GTA.Control.ScriptLeftAxisX);
                float lsY = Game.GetControlValueNormalized(GTA.Control.ScriptLeftAxisY);
                if (Math.Abs(lsX) > 0.1f) offset.X += lsX * fastSpeed;
                if (Math.Abs(lsY) > 0.1f) offset.Y += lsY * fastSpeed;

                // D-pad - fine tuning
                if (Game.IsControlPressed(GTA.Control.FrontendUp)) offset.Y -= fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendDown)) offset.Y += fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendLeft)) offset.X -= fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendRight)) offset.X += fineSpeed;

                mainMenu.Offset = offset;
            }

            // Y button - log position
            if (Game.IsControlJustPressed(GTA.Control.FrontendY))
            {
                var bannerPos = mainMenu.Banner?.Position ?? PointF.Empty;
                var bannerSize = mainMenu.Banner?.Size ?? SizeF.Empty;

                // Get actual screen resolution via native
                var resW = new OutputArgument();
                var resH = new OutputArgument();
                Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
                int actualW = resW.GetResult<int>();
                int actualH = resH.GetResult<int>();
                float actualAspect = actualH > 0 ? (float)actualW / actualH : 0;

                // Calculate base position (Banner - Offset)
                float baseX = bannerPos.X - offset.X;

                Log($"========== MENU POSITION LOG ==========");
                Log($"UI Screen: {screenW}x{screenH} (aspect: {aspectRatio:F4})");
                Log($"Actual Screen: {actualW}x{actualH} (aspect: {actualAspect:F4})");
                Log($"Menu Offset: X={offset.X:F2}, Y={offset.Y:F2}");
                Log($"Banner Position: X={bannerPos.X:F2}, Y={bannerPos.Y:F2}");
                Log($"Banner Base (no offset): X={baseX:F2}");
                Log($"Banner Size: W={bannerSize.Width:F2}, H={bannerSize.Height:F2}");
                Log($"========================================");

                GTA.UI.Notification.Show($"Logged! Offset: X={offset.X:F1} Y={offset.Y:F1}");
            }

            // RB button - reset offset to 0
            if (Game.IsControlJustPressed(GTA.Control.FrontendRb))
            {
                offset = PointF.Empty;
                mainMenu.Offset = offset;
                GTA.UI.Notification.Show("Offset reset to 0,0");
            }

            // Position adjustment is now handled via Debug Menu (F7)
        }

        // Cached actual screen resolution
        private int cachedScreenW = 0;
        private int cachedScreenH = 0;

        private void UpdateCachedScreenResolution()
        {
            var resW = new OutputArgument();
            var resH = new OutputArgument();
            Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
            cachedScreenW = resW.GetResult<int>();
            cachedScreenH = resH.GetResult<int>();
        }

        /// <summary>
        /// Converts LemonUI position coordinates to CustomSprite's 1280x720 base.
        /// Works on any resolution including ultrawide.
        /// </summary>
        /// <summary>Real screen aspect from the ACTUAL resolution (cachedScreenW/H). GTA.UI.Screen.Width/Height
        /// return a fixed 1280x720, so deriving aspect from them pins every menu-relative element to 16:9 and
        /// breaks alignment on ultrawide/non-16:9. Use this for lemonXBase so the custom UI tracks the real menu.</summary>
        private float ActualAspect() => (cachedScreenW > 0 ? (float)cachedScreenW : 1920f) / (cachedScreenH > 0 ? cachedScreenH : 1080);

        private PointF LemonUIToCustomSprite(PointF lemonPos)
        {
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;
            float actualHeight = cachedScreenH > 0 ? cachedScreenH : 1080f;

            // X/width: LemonUI is on a 1080-tall base (X spans 1080*aspect). Convert 1080-base -> screen px
            // (×H/1080) -> CustomSprite 1280-base (×1280/W). The old code used just 1280/W, which only equals
            // this at 16:9 and stretched the banner off-aspect at every other resolution. Y stays 720/1080.
            float widthRatio = (actualHeight / 1080f) * (1280f / actualWidth);
            float heightRatio = 720f / 1080f;

            return new PointF(lemonPos.X * widthRatio, lemonPos.Y * heightRatio);
        }

        /// <summary>
        /// Converts LemonUI size to CustomSprite's 1280x720 base.
        /// Works on any resolution including ultrawide.
        /// </summary>
        private SizeF LemonUIToCustomSpriteSize(SizeF lemonSize)
        {
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;
            float actualHeight = cachedScreenH > 0 ? cachedScreenH : 1080f;

            // Same correction as LemonUIToCustomSprite: width must scale by (H/1080)*(1280/W), not just 1280/W,
            // so the banner keeps its aspect at non-16:9 / non-1080p resolutions. Height stays 720/1080.
            float widthRatio = (actualHeight / 1080f) * (1280f / actualWidth);
            float heightRatio = 720f / 1080f;

            return new SizeF(lemonSize.Width * widthRatio, lemonSize.Height * heightRatio);
        }

        /// <summary>
        /// Calculates and applies the menu offset to align with native LSC menu
        /// based on actual screen aspect ratio. Derived from testing at 16:9 and 32:9.
        /// </summary>
        private void ApplyAutoOffset()
        {
            if ((activeDebugMode == DebugMode.MenuPosition)) return; // Don't auto-offset in debug mode

            // Get actual screen resolution
            var resW = new OutputArgument();
            var resH = new OutputArgument();
            Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
            int actualW = resW.GetResult<int>();
            int actualH = resH.GetResult<int>();

            if (actualW <= 0 || actualH <= 0) return;

            float aspectRatio = (float)actualW / actualH;

            // Calculate offset using derived formula
            float offsetX = BASE_OFFSET_X + OFFSET_SCALE_X * (aspectRatio - BASE_ASPECT);

            mainMenu.Offset = new PointF(offsetX, 0);

            Log($"Auto-offset applied: X={offsetX:F2} for aspect {aspectRatio:F4} ({actualW}x{actualH})");
        }

        // ---- Keep the user where they were across a menu rebuild ----------------------------------------
        // A rebuild throws away every NativeMenu and makes new ones, so we can't keep object references.
        // Instead we snapshot the OPEN menu's subtitle (stable identifier) + highlighted index before the
        // rebuild, then re-open the matching new menu at that index afterwards. Entering/leaving edit mode,
        // editing names/prices, deleting a category, etc. all go through RebuildMenusForVehicle, so they all
        // stay put instead of bouncing back to the root category list.
        private string restoreMenuSubtitle = null;
        private int restoreMenuIndex = -1;
        private bool restorePending = false;

        // Snapshot the currently-open menu. indexOverride = -2 keeps the live highlight; pass an explicit
        // index to land somewhere else (e.g. deleting a category -> highlight shifts up one).
        private void CaptureMenuPosition(int indexOverride = -2)
        {
            restoreMenuSubtitle = null; restoreMenuIndex = -1; restorePending = true;
            NativeMenu open = (mainMenu != null && mainMenu.Visible) ? mainMenu : null;
            if (open == null)
            {
                foreach (var sm in allSubMenus)
                {
                    try { if (sm.Visible) { open = sm; break; } } catch { }
                }
            }
            if (open == null) return;   // nothing open (e.g. menu being opened fresh) -> no restore, leave display alone
            restoreMenuSubtitle = open.Name;
            restoreMenuIndex = indexOverride == -2 ? open.SelectedIndex : indexOverride;
        }

        private void RestoreMenuPosition()
        {
            string wantSub = restoreMenuSubtitle; int wantIdx = restoreMenuIndex;
            restoreMenuSubtitle = null; restoreMenuIndex = -1; restorePending = false;
            if (wantSub == null) return;   // nothing was open -> don't force any menu visible

            NativeMenu target = (mainMenu != null && mainMenu.Name == wantSub) ? mainMenu : null;
            if (target == null)
            {
                foreach (var sm in allSubMenus)
                {
                    try { if (sm.Name == wantSub) { target = sm; break; } } catch { }
                }
            }
            if (target == null) { if (mainMenu != null) mainMenu.Visible = true; return; }   // menu gone -> fall back to root

            isNavigatingMenu = true;
            if (mainMenu != null && target != mainMenu) mainMenu.Visible = false;
            target.Visible = true;
            if (target.Items.Count > 0)
            {
                int idx = wantIdx < 0 ? 0 : (wantIdx > target.Items.Count - 1 ? target.Items.Count - 1 : wantIdx);
                target.SelectedIndex = idx;
            }
            isNavigatingMenu = false;
        }

        private void RebuildMenusForVehicle()
        {
            if (currentVehicle == null) { restorePending = false; restoreMenuSubtitle = null; restoreMenuIndex = -1; return; }
            if (!restorePending) CaptureMenuPosition();   // auto-snapshot unless a caller already set an override

            // CRITICAL: Install mod kit before any mod operations
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);

            // Set current vehicle for MenuConfig (used for vehicle-specific overrides)
            // Resolve the per-vehicle config folder by model name (stable) or display name (back-compat).
            MenuConfig.SetCurrentVehicle(currentVehicle.DisplayName, unchecked((uint)currentVehicle.Model.Hash));

            // Load + re-assert this vehicle's saved engine swap (EnginePowerMultiplier/forced audio reset when
            // the entity restreams, so re-apply whenever we rebuild the menu for this car).
            activeEngineSwap = EngineSwaps.Lookup(VehicleSaveData.GetEngineSwapId(VehicleKey(currentVehicle)));
            if (activeEngineSwap != null) ApplyEngineSwap(activeEngineSwap);

            // Capture factory handling (once per model) + load/apply this vehicle's saved handling tune.
            if (ModSettings.VehicleTuning) InitTuningForVehicle();

            try
            {
                // Hide + remove EVERY submenu first so no stale menu lingers (was overlapping after edit-mode
                // rebuilds, e.g. the old Wheel Type menu staying drawn behind the new one).
                foreach (var sm in allSubMenus)
                {
                    try { sm.Visible = false; } catch { }
                    menuPool.Remove(sm);
                }
                allSubMenus.Clear();

                // Clear main menu
                mainMenu.Clear();

                // Clear dynamic menus
                foreach (var menu in modMenusByIndex.Values)
                {
                    menuPool.Remove(menu);
                }
                modMenusByIndex.Clear();
                itemOwnershipStatus.Clear();
                customDescriptions.Clear();

                // Clear config-based menus
                foreach (var menu in configMenusByPath.Values)
                {
                    menuPool.Remove(menu);
                }
                configMenusByPath.Clear();
                configItemMetadata.Clear();

                // Clear special menus
                if (hornMenu != null) { menuPool.Remove(hornMenu); hornMenu = null; }
                if (plateMenu != null) { menuPool.Remove(plateMenu); plateMenu = null; }
                if (turboMenu != null) { menuPool.Remove(turboMenu); turboMenu = null; }
                if (headlightsMenu != null) { menuPool.Remove(headlightsMenu); headlightsMenu = null; }
                if (windowTintMenu != null) { menuPool.Remove(windowTintMenu); windowTintMenu = null; }

                // Clear and remove respray/wheels if they exist
                if (resprayMenu != null)
                {
                    menuPool.Remove(resprayMenu);
                    resprayMenu = null;
                }
                if (wheelsMenu != null)
                {
                    menuPool.Remove(wheelsMenu);
                    wheelsMenu = null;
                }

                // Clear dynamic category menus (user-created custom categories)
                foreach (var menu in dynamicCategoryMenus)
                {
                    menuPool.Remove(menu);
                }
                dynamicCategoryMenus.Clear();

                // Clear any part-picker menus left from edit mode
                foreach (var menu in partPickerMenus)
                {
                    menuPool.Remove(menu);
                }
                partPickerMenus.Clear();
                mainCatItems.Clear();

                // Clear universal wheel-category menus
                foreach (var menu in universalWheelMenus)
                {
                    menuPool.Remove(menu);
                }
                universalWheelMenus.Clear();
                wheelInspectMenus.Clear();   // repopulated as wheel menus are rebuilt below

                // Build menu with grouped categories like native LSC
                BuildGroupedMainMenu();

                // Add any custom user-created categories from folders
                BuildDynamicMainMenu();

                Log($"Built menu with {mainMenu.Items.Count} categories for {currentVehicle.DisplayName}");

                // Re-open the menu the user was on (if any) at the same spot, instead of dumping to the root.
                RestoreMenuPosition();
            }
            catch (Exception ex)
            {
                Log($"ERROR in RebuildMenusForVehicle: {ex.Message}\n{ex.StackTrace}");
                restorePending = false; restoreMenuSubtitle = null; restoreMenuIndex = -1;
            }
        }

        /// <summary>
        /// Builds the main menu with grouped categories like native LSC.
        /// Groups like "Bumpers" contain sub-items "Front Bumper" and "Rear Bumper".
        /// </summary>
        private void BuildGroupedMainMenu()
        {
            // Helper to add a submenu item with proper back navigation and description
            // When editor mode is enabled: Hold LB/L1 (or Shift) while selecting to edit the description
            // Note: We don't use LemonUI's built-in description - we draw our own below the scroll indicator
            void AddSubmenuItem(string title, NativeMenu submenu)
            {
                // Per-vehicle hide of a default category. Hidden in normal play; in edit mode it still shows
                // (marked) so the modder can press Delete to restore it.
                bool hidden = MenuConfig.IsCategoryHidden(title);
                if (hidden && !editModeActive) return;

                // Apply this vehicle's rename override for built-in categories (e.g. Skirts -> Default Skirts).
                string display = MenuConfig.GetCategoryRename(title) ?? title;
                var item = new NativeItem(display);
                mainCatItems[item] = (title, false);   // key = ORIGINAL title so rename keeps working after override
                item.AltTitle = hidden ? "~c~(hidden)" : ">>";
                // Don't set Description here - we draw our own in DrawCustomDescription()
                // Note: Edit mode uses LB+X (checked in OnTick), not LB+A
                item.Activated += (s, e) =>
                {
                    // Normal mode - open submenu
                    isNavigatingMenu = true;
                    mainMenu.Visible = false;
                    submenu.Visible = true;
                    isNavigatingMenu = false;
                };
                submenu.Closed += (s, e) => { if (!isNavigatingMenu) mainMenu.Visible = true; };
                mainMenu.Add(item);
            }

            // Helper to check if mod type has options
            int GetModCount(int modIndex) => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, modIndex);

            // Count of parts STILL visible in this slot (i.e. not re-shelved into a custom category). When this
            // hits 0, the built-in category is hidden entirely — "remove all parts -> category disappears".
            int VisibleModCount(int modIndex)
            {
                int total = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, modIndex);
                int vis = 0;
                for (int i = 0; i < total; i++) if (!MenuConfig.IsPartHidden(modIndex, i)) vis++;
                return vis;
            }

            // Customize Radio: no in-menu toggle — it follows the game's Music volume (turn music down in GTA
            // settings to quiet/mute it). Just rescan here (when not already playing) so files added before
            // opening the menu are picked up.
            try { if (customizeRadio != null && !customizeRadio.IsActive) customizeRadio.Rescan(); } catch { }

            // Armor (16)
            if (GetModCount(16) > 0)
            {
                var menu = CreateModMenuByIndex(16, GetModCount(16), "Armor");
                AddSubmenuItem("Armor", menu);
            }

            // Brakes (12)
            if (GetModCount(12) > 0)
            {
                var menu = CreateModMenuByIndex(12, GetModCount(12), "Brakes");
                AddSubmenuItem("Brakes", menu);
            }

            // Bumpers (group: Front 1, Rear 2)
            int frontBumperCount = GetModCount(1);
            int rearBumperCount = GetModCount(2);
            int frontBumperVis = VisibleModCount(1);
            int rearBumperVis = VisibleModCount(2);
            if (frontBumperVis > 0 || rearBumperVis > 0)
            {
                var bumpersMenu = CreateMenu("Bumpers");

                if (frontBumperVis > 0)
                {
                    var frontMenu = CreateModMenuByIndex(1, frontBumperCount, "Front Bumper");
                    frontMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var frontItem = new NativeItem(SlotName(1, "Front Bumper"));
                    frontItem.AltTitle = ">>";
                    frontItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; frontMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(frontItem);
                }
                if (rearBumperVis > 0)
                {
                    var rearMenu = CreateModMenuByIndex(2, rearBumperCount, "Rear Bumper");
                    rearMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var rearItem = new NativeItem(SlotName(2, "Rear Bumper"));
                    rearItem.AltTitle = ">>";
                    rearItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; rearMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(rearItem);
                }
                AddSubmenuItem("Bumpers", bumpersMenu);
            }

            // Engine (11)
            if (GetModCount(11) > 0)
            {
                var menu = CreateModMenuByIndex(11, GetModCount(11), "Engine");
                AddSubmenuItem("Engine", menu);
            }

            // Engine Swap (custom feature: engine sound + power packages)
            {
                var swapMenu = CreateEngineSwapMenu();
                AddSubmenuItem("Engine Swap", swapMenu);
            }

            // Vehicle Tuning (live handling fine-tune; Side Course feature, toggleable in INI)
            if (ModSettings.VehicleTuning && WheelFitment.WheelMemory.HasHandling(currentVehicle))
            {
                var tm = CreateTuningMenu();
                AddSubmenuItem("Vehicle Tuning", tm);
            }

            // Exhaust (4)
            if (VisibleModCount(4) > 0)
            {
                var menu = CreateModMenuByIndex(4, GetModCount(4), "Exhaust");
                AddSubmenuItem(SlotName(4, "Exhaust"), menu);
            }

            // Extras (Side Course feature - toggle vehicle extras)
            if (ModSettings.ExtendedCategories)
            {
                var extrasMenu = CreateExtrasMenu();
                if (extrasMenu != null && extrasMenu.Items.Count > 0)
                {
                    AddSubmenuItem("Extras", extrasMenu);
                }
            }

            // Fenders (index 8 - left fender, index 9 - right fender)
            // Most vehicles only have left fender mods, show directly as "FENDERS"
            int leftFenderCount = GetModCount(8);
            int rightFenderCount = GetModCount(9);
            if (VisibleModCount(8) > 0)
            {
                // Create fender menu directly with left fender mods (most common case)
                var menu = CreateModMenuByIndex(8, leftFenderCount, "FENDERS");
                AddSubmenuItem(SlotName(8, "Fender"), menu);
            }
            // Right fender is rare - add as separate category if it exists
            if (VisibleModCount(9) > 0)
            {
                var menu = CreateModMenuByIndex(9, rightFenderCount, "FENDERS (RIGHT)");
                AddSubmenuItem(SlotName(9, "Fender (Right)"), menu);
            }

            // Grille (6)
            if (VisibleModCount(6) > 0)
            {
                var menu = CreateModMenuByIndex(6, GetModCount(6), "Grille");
                AddSubmenuItem(SlotName(6, "Grille"), menu);
            }

            // Hood (7)
            if (VisibleModCount(7) > 0)
            {
                var menu = CreateModMenuByIndex(7, GetModCount(7), "Hood");
                AddSubmenuItem(SlotName(7, "Hood"), menu);
            }

            // Horn (14)
            hornMenu = CreateHornMenu();
            if (hornMenu.Items.Count > 0)
            {
                AddSubmenuItem("Horn", hornMenu);
            }

            // Lights - full submenu structure like native LSC
            {
                var lightsMenu = CreateMenu("Lights");

                // Light Mode selector - cycles through different lighting states
                // SET_VEHICLE_LIGHTS 3=on, 4=off
                // SET_VEHICLE_INDICATOR_LIGHTS: index 0=right, 1=left
                var lightModes = new List<string> { "Off", "Headlights", "High Beams", "Left Indicator", "Right Indicator", "Hazards", "Brake Lights", "Interior" };
                var lightModeItem = new NativeListItem<string>("Light Mode", lightModes.ToArray());
                lightModeItem.SelectedIndex = 0; // Start at Off

                lightModeItem.ItemChanged += (s, e) =>
                {
                    if (currentVehicle == null || !currentVehicle.Exists()) return;

                    // Track current mode for continuous application
                    currentLightMode = e.Index;

                    // Reset all lights first
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4); // Off
                    Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, false); // Left off
                    Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, false); // Right off
                    Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, currentVehicle, false);

                    // Apply the selected mode (high beams/brake applied continuously in OnTick)
                    switch (e.Index)
                    {
                        case 0: // Off
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4);
                            break;
                        case 1: // Headlights
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 3); // On
                            break;
                        case 2: // High Beams - applied every frame in OnTick
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 3);
                            Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, true);
                            break;
                        case 3: // Left Indicator
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, true);
                            break;
                        case 4: // Right Indicator
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, true);
                            break;
                        case 5: // Hazards
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, true);
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, true);
                            break;
                        case 6: // Brake Lights - applied every frame in OnTick
                            break;
                        case 7: // Interior
                            Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, true);
                            break;
                    }
                };
                lightsMenu.Add(lightModeItem);

                // Headlights submenu
                headlightsMenu = CreateMenu("Headlights");
                headlightsMenu.Closed += (s, e) => { if (!isNavigatingMenu) lightsMenu.Visible = true; };

                bool hasXenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22);

                var stockLightsItem = new NativeItem("Stock Lights");
                if (!hasXenon)
                {
                    stockLightsItem.AltTitle = "";
                    itemOwnershipStatus[stockLightsItem] = STATUS_INSTALLED;
                }
                else
                {
                    stockLightsItem.AltTitle = "Free";
                }
                stockLightsItem.Activated += (s, e) => { if (EditBlockApply()) return; SetXenon(false); };
                headlightsMenu.Add(stockLightsItem);

                var xenonLightsItem = new NativeItem("Xenon Lights");
                bool xenonOwned = VehicleSaveData.IsXenonOwned(VehicleKey(currentVehicle));
                if (hasXenon)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_INSTALLED;
                    if (!xenonOwned) VehicleSaveData.SetXenonOwned(VehicleKey(currentVehicle));
                }
                else if (xenonOwned)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_OWNED;
                }
                else
                {
                    xenonLightsItem.AltTitle = $"${ModPricing.XenonLightsPrice}";
                }
                xenonLightsItem.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsXenonOwned(VehicleKey(currentVehicle));
                    if (!TryPurchase(ModPricing.XenonLightsPrice, owned)) return;

                    SetXenon(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetXenonOwned(VehicleKey(currentVehicle));
                        VehicleSaveData.Save();
                    }
                };
                headlightsMenu.Add(xenonLightsItem);

                // Headlight Color submenu (Side Course feature - requires xenon)
                if (ModSettings.LightCustomization)
                {
                    var headlightColorMenu = CreateHeadlightColorMenu();
                    headlightColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) headlightsMenu.Visible = true; };

                    var headlightColorNavItem = new NativeItem("Headlight Color");
                    headlightColorNavItem.AltTitle = ">>";
                    headlightColorNavItem.Description = "Requires Xenon Lights";
                    headlightColorNavItem.Activated += (s, e) =>
                    {
                        // Check if xenon is installed
                        if (!Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22))
                        {
                            ShowNotification("~r~Install Xenon Lights first!");
                            return;
                        }
                        isNavigatingMenu = true;
                        headlightsMenu.Visible = false;
                        headlightColorMenu.Visible = true;
                        isNavigatingMenu = false;
                    };
                    headlightsMenu.Add(headlightColorNavItem);
                }

                var headlightsNavItem = new NativeItem("Headlights");
                headlightsNavItem.AltTitle = ">>";
                headlightsNavItem.Activated += (s, e) => { isNavigatingMenu = true; lightsMenu.Visible = false; headlightsMenu.Visible = true; isNavigatingMenu = false; };
                lightsMenu.Add(headlightsNavItem);

                // Neon Kits submenu
                var neonKitsMenu = CreateMenu("Neon Kits");
                neonKitsMenu.Closed += (s, e) => { if (!isNavigatingMenu) lightsMenu.Visible = true; };

                // Neon Layout submenu - Individual side toggles
                var neonLayoutMenu = CreateMenu("NEON LAYOUT");
                neonLayoutMenu.Closed += (s, e) => { if (!isNavigatingMenu) neonKitsMenu.Visible = true; };
                // Default the underglow to white (only if currently black/unset) so enabled sides are actually visible.
                neonLayoutMenu.Shown += (s, e) => EnsureNeonColorVisible();

                // Neon indices: 0=Left, 1=Right, 2=Front, 3=Back
                var neonSides = new[] {
                    (name: "Front", index: 2),
                    (name: "Back", index: 3),
                    (name: "Left", index: 0),
                    (name: "Right", index: 1)
                };

                foreach (var (sideName, sideIndex) in neonSides)
                {
                    bool isEnabled = GetNeonEnabled(sideIndex);
                    var sideItem = new NativeItem(sideName);

                    // Show sprite if enabled, price if not
                    if (isEnabled)
                    {
                        sideItem.AltTitle = "";
                        itemOwnershipStatus[sideItem] = STATUS_INSTALLED;
                    }
                    else
                    {
                        sideItem.AltTitle = "$500";
                    }

                    int idx = sideIndex; // Capture for closure
                    string name = sideName;
                    sideItem.Activated += (s, e) =>
                    {
                        if (EditBlockApply()) return;
                        bool currentState = GetNeonEnabled(idx);

                        // Only charge when turning ON
                        if (!currentState && !TryPurchase(500, false)) return;

                        // Turning a side ON: make sure the underglow colour is visible (white) if it's black/unset.
                        if (!currentState) EnsureNeonColorVisible();

                        // Toggle the neon
                        SetNeonEnabled(idx, !currentState);

                        // Update the item display
                        bool newState = !currentState;
                        sideItem.AltTitle = newState ? "" : "$500";
                        if (newState)
                            itemOwnershipStatus[sideItem] = STATUS_INSTALLED;
                        else
                            itemOwnershipStatus.Remove(sideItem);

                        if (activeDebugMode != DebugMode.None)
                            ShowNotification($"~g~{name} neon {(newState ? "installed" : "removed")}!");
                        MechanicSpeak();
                    };
                    neonLayoutMenu.Add(sideItem);
                }

                var neonLayoutNavItem = new NativeItem("Neon Layout");
                neonLayoutNavItem.AltTitle = ">>";
                neonLayoutNavItem.Activated += (s, e) => { isNavigatingMenu = true; neonKitsMenu.Visible = false; neonLayoutMenu.Visible = true; isNavigatingMenu = false; };
                neonKitsMenu.Add(neonLayoutNavItem);

                // Neon Color submenu - uses MenuConfig
                var neonColorMenu = CreateNeonColorMenuFromConfig();
                if (neonColorMenu != null)
                {
                    neonColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) neonKitsMenu.Visible = true; };
                    var neonColorNavItem = new NativeItem("Neon Color");
                    neonColorNavItem.AltTitle = ">>";
                    neonColorNavItem.Activated += (s, e) => { isNavigatingMenu = true; neonKitsMenu.Visible = false; neonColorMenu.Visible = true; isNavigatingMenu = false; };
                    neonKitsMenu.Add(neonColorNavItem);
                }

                var neonKitsNavItem = new NativeItem("Neon Kits");
                neonKitsNavItem.AltTitle = ">>";
                neonKitsNavItem.Activated += (s, e) => { isNavigatingMenu = true; lightsMenu.Visible = false; neonKitsMenu.Visible = true; isNavigatingMenu = false; };
                lightsMenu.Add(neonKitsNavItem);

                // Turn off all lights when leaving the Lights menu
                lightsMenu.Closed += (s, e) =>
                {
                    currentLightMode = 0; // Reset light mode tracking
                    if (currentVehicle != null && currentVehicle.Exists())
                    {
                        Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4); // Off
                        Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, false);
                        Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, false); // Left off
                        Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, false); // Right off
                        Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, false);
                        Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, currentVehicle, false);
                    }
                };

                AddSubmenuItem("Lights", lightsMenu);
            }

            // Speedometer (always available — it's a HUD, not a per-mod category). Wired manually so the
            // preview follows the highlighted style on open/scroll.
            {
                var speedoMenu = CreateSpeedometerMenu();
                speedoMenu.Closed += (s, e) => { if (!isNavigatingMenu) { mainMenu.Visible = true; Speedo.Preview = null; } };
                var speedoNav = new NativeItem("Speedometer") { AltTitle = ">>" };
                speedoNav.Activated += (s, e) => { isNavigatingMenu = true; mainMenu.Visible = false; Speedo.Preview = Speedo.Active; speedoMenu.Visible = true; isNavigatingMenu = false; };
                mainMenu.Add(speedoNav);
            }

            // Livery - check both native livery system AND mod index 48
            int nativeLiveryCount = Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, currentVehicle);
            int modLiveryCount = GetModCount(48); // Mod-based liveries
            int liveryCount = Math.Max(nativeLiveryCount, modLiveryCount);
            if (liveryCount > 0)
            {
                var liveryMenu = CreateLiveryMenu(liveryCount, nativeLiveryCount > 0);
                AddSubmenuItem("Livery", liveryMenu);
            }

            // Plate
            plateMenu = CreatePlateMenu();
            AddSubmenuItem("Plate", plateMenu);

            // Packages (Side Course feature - save/load mod presets)
            if (ModSettings.VehiclePackages)
            {
                packagesMenu = CreatePackagesMenu();
                AddSubmenuItem("Packages", packagesMenu);
            }

            // Respray
            resprayMenu = BuildResprayMenu();
            AddSubmenuItem("Respray", resprayMenu);

            // Roll Cage (5)
            if (VisibleModCount(5) > 0)
            {
                var menu = CreateModMenuByIndex(5, GetModCount(5), "Roll Cage");
                AddSubmenuItem(SlotName(5, "Roll Cage"), menu);
            }

            // Roof (10)
            if (VisibleModCount(10) > 0)
            {
                var menu = CreateModMenuByIndex(10, GetModCount(10), "Roof");
                AddSubmenuItem(SlotName(10, "Roof"), menu);
            }

            // Skirts (3)
            if (VisibleModCount(3) > 0)
            {
                var menu = CreateModMenuByIndex(3, GetModCount(3), "Skirts");
                AddSubmenuItem(SlotName(3, "Skirts"), menu);
            }

            // Spoiler (0)
            if (VisibleModCount(0) > 0)
            {
                var menu = CreateModMenuByIndex(0, GetModCount(0), "Spoiler");
                AddSubmenuItem(SlotName(0, "Spoiler"), menu);
            }

            // Suspension (15) — native lowering levels PLUS the custom Camber & Ride Height (alignment),
            // because in real life camber + ride height ARE suspension. Always shown so alignment is
            // available even on vehicles without native suspension mods.
            {
                var menu = GetModCount(15) > 0
                    ? CreateModMenuByIndex(15, GetModCount(15), "Suspension")
                    : CreateMenu("Suspension");
                if (menu != null)
                {
                    alignmentParent = menu;   // BuildAlignmentMenu restores to this on close
                    var alignNav = new NativeItem("Custom Suspension and Camber", FitmentSummary());
                    // Three states: NOT owned -> price; owned but NOT the active suspension -> owned tick +
                    // "Select to equip"; owned AND equipped -> equipped garage icon + "Select to edit". This makes
                    // the equip mutually exclusive with the vanilla levels (only one shows the garage icon).
                    void RefreshSuspNav()
                    {
                        string vn = VehicleKey(currentVehicle);
                        bool owned = VehicleSaveData.IsWheelFitmentOwned(vn);
                        bool equipped = owned && VehicleSaveData.IsCustomSuspensionEquipped(vn);
                        if (!owned)
                        {
                            itemOwnershipStatus.Remove(alignNav);
                            alignNav.AltTitle = $"${ModPricing.CustomSuspensionPrice}";
                            alignNav.Description = FitmentSummary();
                        }
                        else if (equipped)
                        {
                            alignNav.AltTitle = "";
                            itemOwnershipStatus[alignNav] = STATUS_INSTALLED;
                            alignNav.Description = FitmentSummary() + "  ·  Select to edit";
                        }
                        else
                        {
                            alignNav.AltTitle = "";
                            itemOwnershipStatus[alignNav] = STATUS_OWNED;
                            alignNav.Description = "Select to equip your custom suspension setup.";
                        }
                    }
                    RefreshSuspNav();
                    // Refresh the saved-value summary + owned/equipped state each time the Suspension menu opens.
                    menu.Shown += (s, e) => { RefreshSuspNav(); _hoveringCustomSuspension = false; };
                    // Hovering our custom item should show OUR stance, not a game-level preview: undo the
                    // generic preview (back to the original installed level) and let our ride-height re-apply.
                    menu.SelectedIndexChanged += (s, e) =>
                    {
                        bool onNav = menu.SelectedItem == alignNav;
                        _hoveringCustomSuspension = onNav;
                        if (onNav && currentVehicle != null && currentVehicle.Exists())
                        {
                            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 15, previewOriginalValue, false);
                            // Re-apply our stance on THIS frame so there's no one-frame gap at the base.
                            wheelFitment.SuspendRideHeight = false;
                            wheelFitment.ApplyRideHeightNow();
                        }
                    };
                    // Buy once per car to unlock the camber/ride-height sliders (also unlocks the Tire Grip /
                    // Steering Lock tuning sliders). Rebuild fresh on open so it reflects wheels / pro-mode state.
                    // Double-select: first select buys-and-equips (or, if already owned, equips); only once it's
                    // the equipped suspension does a select open the editor. Equipping rebuilds the menu so the
                    // garage icon moves to Custom and off whatever vanilla level was equipped.
                    alignNav.Activated += (s, e) =>
                    {
                        if (currentVehicle == null) return;
                        string vn = VehicleKey(currentVehicle);
                        bool owned = VehicleSaveData.IsWheelFitmentOwned(vn);
                        bool equipped = owned && VehicleSaveData.IsCustomSuspensionEquipped(vn);

                        if (!owned)
                        {
                            if (!TryPurchase(ModPricing.CustomSuspensionPrice, false)) return;
                            VehicleSaveData.SetWheelFitmentOwned(vn, true);
                            EquipCustomSuspension();   // buys + equips; select again to edit
                            return;
                        }
                        if (!equipped)
                        {
                            EquipCustomSuspension();   // equip; select again to edit
                            return;
                        }
                        var sub = BuildAlignmentMenu();
                        if (sub == null) return;
                        isNavigatingMenu = true; menu.Visible = false; sub.Visible = true; isNavigatingMenu = false;
                    };
                    menu.Add(alignNav);
                    AddSubmenuItem("Suspension", menu);
                }
            }

            // Transmission (13)
            if (GetModCount(13) > 0)
            {
                var menu = CreateModMenuByIndex(13, GetModCount(13), "Transmission");

                // Add Manual Transmission option at the end
                AddManualTransmissionOption(menu);

                AddSubmenuItem("Transmission", menu);
            }

            // Turbo (18) - submenu like native LSC
            {
                turboMenu = CreateMenu("Turbo");

                bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
                bool turboOwned = VehicleSaveData.IsTurboOwned(VehicleKey(currentVehicle));

                var noneItem = new NativeItem("None");
                if (!hasTurbo)
                {
                    noneItem.AltTitle = "";
                    itemOwnershipStatus[noneItem] = STATUS_INSTALLED;
                }
                else
                {
                    noneItem.AltTitle = "Free";
                }
                noneItem.Activated += (s, e) => { if (EditBlockApply()) return; SetTurbo(false); };
                turboMenu.Add(noneItem);

                var turboTuningItem = new NativeItem("Turbo Tuning");
                if (hasTurbo)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_INSTALLED;
                    if (!turboOwned) VehicleSaveData.SetTurboOwned(VehicleKey(currentVehicle));
                }
                else if (turboOwned)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_OWNED;
                }
                else
                {
                    turboTuningItem.AltTitle = $"${ModPricing.TurboPrice}";
                }
                turboTuningItem.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsTurboOwned(VehicleKey(currentVehicle));
                    if (!TryPurchase(ModPricing.TurboPrice, owned)) return;

                    SetTurbo(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetTurboOwned(VehicleKey(currentVehicle));
                        VehicleSaveData.Save();
                    }
                };
                turboMenu.Add(turboTuningItem);

                // Nitrous FX — each tier (NOS 1-4) is bought here, rising in cost. Buying any tier installs NOS
                // and (first time) prompts for the spray button. Hold the NOS button while open to preview.
                {
                    var nosFxMenu = CreateNosColorMenu();
                    nosMenu = nosFxMenu;   // for the context "Preview" hint in the hint bar
                    var nosFxNav = new NativeItem("Nitrous (NOS)");
                    nosFxNav.Description = "Buy & equip a nitrous tier (NOS 1-4), then hold Preview to see it on the car.";
                    // Equipped (a tier active) -> garage icon; owned but Off -> owned tick; nothing owned -> ">>".
                    void RefreshNosNav()
                    {
                        string vn = VehicleKey(currentVehicle);
                        int eq = VehicleSaveData.GetNosEquippedTier(vn);
                        int ownedMask = VehicleSaveData.GetNosOwnedTiers(vn);
                        if (eq >= 0) { nosFxNav.AltTitle = ""; itemOwnershipStatus[nosFxNav] = STATUS_INSTALLED; }
                        else if (ownedMask != 0) { nosFxNav.AltTitle = ""; itemOwnershipStatus[nosFxNav] = STATUS_OWNED; }
                        else { nosFxNav.AltTitle = ">>"; itemOwnershipStatus.Remove(nosFxNav); }
                    }
                    RefreshNosNav();
                    nosFxMenu.Closed += (s, e) => { if (!isNavigatingMenu) { turboMenu.Visible = true; nosColorMenuOpen = false; elscTransmission?.StopExhaustFlames(); RefreshNosNav(); } };
                    nosFxNav.Activated += (s, e) => { isNavigatingMenu = true; turboMenu.Visible = false; nosColorMenuOpen = true; nosFxMenu.Visible = true; isNavigatingMenu = false; };
                    turboMenu.Add(nosFxNav);
                }

                AddSubmenuItem("Turbo", turboMenu);
            }

            // Wheels
            wheelsMenu = BuildWheelsMenu();
            AddSubmenuItem("Wheels", wheelsMenu);

            // Windows (tint) - uses MenuConfig
            windowTintMenu = CreateWindowTintMenuFromConfig();
            if (windowTintMenu != null)
            {
                AddSubmenuItem("Windows", windowTintMenu);
            }

            // Benny's / interior / engine-bay categories (Engine Block, Air Filter, Struts, Arch Cover, Seats,
            // Dashboard, Door Speakers, Steering Wheel, Hydraulics, Trunk, etc.). These have ModCategories
            // entries but weren't built by the hardcoded blocks above, so the car's Benny's parts never showed.
            // Add any the current car actually has. (46 Windows is skipped — the tint item already uses that
            // name; 48 Livery is already built above.)
            int[] bennysIndices = { 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45 };
            foreach (int mi in bennysIndices)
            {
                if (mi == 24 && !currentVehicle.Model.IsBike) continue;     // rear wheels: motorcycles only
                // VisibleModCount (not raw count): when every part in this slot is re-shelved into a custom
                // category, the built-in one would show only "Stock" — so hide it, same as slots 0-10.
                if (VisibleModCount(mi) <= 0) continue;
                if (!ModCategories.AllCategories.TryGetValue(mi, out var cat)) continue;
                var bm = CreateModMenuByIndex(mi, GetModCount(mi), cat.DisplayName);
                if (bm != null) AddSubmenuItem(cat.DisplayName, bm);
            }
        }

        /// <summary>True for motorcycles/bikes — they use the bike wheel type (6) and can't be stanced.</summary>
        private bool IsBike(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            try { return Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, v.Model.Hash); } catch { return false; }
        }

        /// <summary>Stancing is only allowed on 4-wheel vehicles (excludes bikes, trikes, 6-wheelers, etc.).</summary>
        private bool CanStance(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            try { return WheelFitment.WheelMemory.GetWheelCount(v) == 4; } catch { return false; }
        }

        private NativeMenu BuildWheelsMenu()
        {
            var menu = CreateMenu("Wheels");

            // Wheel Type submenu
            var wheelTypeMenu = CreateMenu("Wheel Types");
            wheelTypeMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Stock Wheels — revert to the car's factory wheels (the per-type lists have no -1 "stock"
            // entry like generic mod menus do). Sits at the top of the wheel-type list.
            var stockWheelsItem = new NativeItem("Stock Wheels", "Revert to the factory wheels");
            stockWheelsItem.Activated += (s, e) =>
            {
                if (EditBlockApply()) return;
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, -1, false); // front -> stock
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, -1, false); // back -> stock
                    ShowNotification("~g~Reverted to stock wheels");
                }
            };
            wheelTypeMenu.Add(stockWheelsItem);

            // Apply-To axle selector — staggered front/rear wheels. ONLY motorcycles have a real separate rear
            // wheel slot (mod 24); cars share one slot (mod 23 = all four), so on a car "Rear Only" would change
            // nothing. So the selector is bike-only, and the axle resets to Front+Rear for everything else.
            wheelApplyAxle = 0;
            if (IsBike(currentVehicle))
            {
                var axleItem = new NativeListItem<string>("Apply To", "Front + Rear", "Front Only", "Rear Only")
                {
                    SelectedIndex = wheelApplyAxle,
                    Description = "Choose which wheel a design applies to (for staggered front/rear setups)"
                };
                axleItem.ItemChanged += (s, e) => { wheelApplyAxle = e.Index; };
                NoWrap(axleItem);
                wheelTypeMenu.Add(axleItem);
            }

            // Wheel types (indices match the GTA enum). High End is 7 (NOT 6 — 6 is BikeWheels). Benny's
            // (8/9) + Street/Track/Open Wheel (10-12) were missing entirely. Empty types return null and are
            // skipped, so a car without a given type just won't list it.
            // Motorcycles only have the Bike Wheels type (6); the car types are empty for them, so show ONLY
            // bike wheels on a bike and ONLY the car types on a car.
            var wheelTypes = IsBike(currentVehicle)
                ? new[] { (6, "Bike Wheels") }
                : new[] {
                    (7, "High End"),
                    (2, "Lowrider"),
                    (1, "Muscle"),
                    (4, "Off-Road"),
                    (0, "Sport"),
                    (3, "SUV"),
                    (5, "Tuner"),
                    (8, "Benny's Originals"),
                    (9, "Benny's Bespoke"),
                    (11, "Street"),
                    (12, "Track"),
                    (10, "Open Wheel")
                };

            foreach (var (typeIndex, typeName) in wheelTypes)
            {
                var typeWheelsMenu = CreateWheelTypeMenu(typeIndex);
                if (typeWheelsMenu != null)
                {
                    typeWheelsMenu.Name = typeName;
                    typeWheelsMenu.Closed += (s, e) => { if (!isNavigatingMenu) wheelTypeMenu.Visible = true; };
                    var typeItem = new NativeItem(typeName);
                    typeItem.AltTitle = ">>";
                    int idx = typeIndex;
                    typeItem.Activated += (s, e) => { isNavigatingMenu = true; wheelTypeMenu.Visible = false; typeWheelsMenu.Visible = true; isNavigatingMenu = false; };
                    wheelTypeMenu.Add(typeItem);
                }
            }

            var wheelTypeNavItem = new NativeItem("Wheel Type");
            wheelTypeNavItem.AltTitle = ">>";
            wheelTypeNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelTypeMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelTypeNavItem);

            // Universal custom wheel categories live INSIDE Wheel Type (shipped by wheel packs; same on every car).
            foreach (var wcat in MenuConfig.GetUniversalWheelCategories())
            {
                var wcMenu = BuildWheelCategoryMenu(wcat, wheelTypeMenu);
                var nav = new NativeItem(wcat.DisplayName) { AltTitle = ">>" };
                if (!string.IsNullOrEmpty(wcat.Description)) nav.Description = wcat.Description;
                var capMenu = wcMenu;
                nav.Activated += (s, e) => { isNavigatingMenu = true; wheelTypeMenu.Visible = false; capMenu.Visible = true; isNavigatingMenu = false; };
                wheelTypeMenu.Add(nav);
            }
            if (editModeActive)
            {
                var addWheelCat = new NativeItem("~y~+ Add a Wheel Category") { AltTitle = "EDIT" };
                addWheelCat.Description = "Create a custom wheel category (applies to every car).";
                addWheelCat.Activated += (s, e) => StartAddWheelCategory();
                wheelTypeMenu.Add(addWheelCat);
            }

            // Wheel Color submenu
            var wheelColorMenu = CreateWheelColorMenu();
            wheelColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var wheelColorNavItem = new NativeItem("Wheel Color");
            wheelColorNavItem.AltTitle = ">>";
            wheelColorNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelColorMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelColorNavItem);

            // Tires submenu
            var tiresMenu = CreateMenu("Tires");
            tiresMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Tire Design submenu - the aftermarket tire profile (SET_VEHICLE_MOD "variation" flag): Stock vs
            // Custom. Priced + ownership-tracked like the other tire categories (replaces the old dead nav item).
            var tireDesignMenu = CreateTireDesignMenu();
            tireDesignMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
            var tireDesignNavItem = new NativeItem("Tire Design");
            tireDesignNavItem.AltTitle = ">>";
            tireDesignNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireDesignMenu.Visible = true; isNavigatingMenu = false; };
            tiresMenu.Add(tireDesignNavItem);

            // Tire Enhancements submenu - uses MenuConfig
            var tireEnhancementsMenu = CreateTireEnhancementsMenuFromConfig();
            if (tireEnhancementsMenu != null)
            {
                tireEnhancementsMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
                var tireEnhancementsNavItem = new NativeItem("Tire Enhancements");
                tireEnhancementsNavItem.AltTitle = ">>";
                tireEnhancementsNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireEnhancementsMenu.Visible = true; isNavigatingMenu = false; };
                tiresMenu.Add(tireEnhancementsNavItem);
            }

            // Tire Smoke submenu - uses MenuConfig
            var tireSmokeMenu = CreateTireSmokeMenuFromConfig();
            if (tireSmokeMenu != null)
            {
                tireSmokeMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
                var tireSmokeNavItem = new NativeItem("Tire Smoke");
                tireSmokeNavItem.AltTitle = ">>";
                tireSmokeNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireSmokeMenu.Visible = true; isNavigatingMenu = false; };
                tiresMenu.Add(tireSmokeNavItem);
            }

            var tiresNavItem = new NativeItem("Tires");
            tiresNavItem.AltTitle = ">>";
            tiresNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; tiresMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(tiresNavItem);

            // Wheel Fitment submenu (wheel-fitment adjustments). Stancing is 4-wheel only — bikes and other
            // non-4-wheel vehicles don't get the Fitment menu at all.
            if (ModSettings.AutoApplyStances && CanStance(currentVehicle))
            {
                wheelFitmentParent = menu;   // so BuildWheelFitmentMenu can restore this parent on close
                var fitmentMenu = BuildWheelFitmentMenu();
                if (fitmentMenu != null)
                {
                    // NOTE: the parent-restore Closed handler is wired INSIDE BuildWheelFitmentMenu (using
                    // wheelFitmentParent) so that rebuilt instances (Reset/Purchase) restore the parent too.
                    var fitmentNavItem = new NativeItem("Wheel Fitment");
                    fitmentNavItem.AltTitle = ">>";
                    fitmentNavItem.Description = "Adjust camber, track width, and ride height";
                    // REBUILD on open so the size/width sliders unlock the moment aftermarket wheels are on
                    // (HasVisualWheels is read at build time; the menu was previously built once with stock
                    // wheels and never refreshed).
                    fitmentNavItem.Activated += (s, e) =>
                    {
                        isNavigatingMenu = true;
                        menu.Visible = false;
                        var fresh = BuildWheelFitmentMenu() ?? fitmentMenu;
                        fresh.Visible = true;
                        isNavigatingMenu = false;
                    };
                    menu.Add(fitmentNavItem);
                }
                else
                {
                    GTA.UI.Notification.Show("~r~BuildWheelFitmentMenu returned null!");
                }
            }

            return menu;
        }

        private NativeMenu BuildWheelFitmentMenu()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return null;

            // Simplified: titled "Fitment Service", Pro toggle on top, then Wheel Width / Size / Poke,
            // then Reset. No purchase gate, no Save button — it auto-saves when you leave the menu.
            var menu = CreateMenu("Fitment Service");
            menu.Closed += (s, e) =>
            {
                SaveCurrentFitment();   // auto-save on leave
                if (!isNavigatingMenu && wheelFitmentParent != null) wheelFitmentParent.Visible = true;
            };

            _fitmentResync.Clear();
            InitFitmentForVehicle();
            AddProModeToggle(menu, BuildWheelFitmentMenu);
            AddSizeWidthItems(menu);   // Wheel Width, Wheel Size (wheel-specific fitment)
            AddPokeItems(menu);        // Wheel Poke (or Front/Rear Poke in Pro)
            AddResetItem(menu, BuildWheelFitmentMenu);
            return menu;
        }

        // ============================================================================================
        // Fitment slider GROUPS — shared by the Wheel Fitment menu (Wheels) and the Camber & Ride Height
        // submenu (Suspension). Each group respects Pro Mode internally (combined vs per-axle).
        // ============================================================================================

        // Generic per-axle float slider (radians/metres) used by the Pro-mode camber/height/poke controls.
        // Make a value slider STOP at its ends instead of wrapping (+max -> -min). Reverts a user left/right
        // that wrapped the index past an end. Direction is Unknown for programmatic SelectedIndex sets (e.g.
        // the auto-recovery resync), so those are ignored. Use on numeric value sliders, not choice-lists.
        private void NoWrap<T>(NativeListItem<T> item)
        {
            item.ItemChanged += (s, e) =>
            {
                int last = item.Items.Count - 1;
                if (last <= 0) return;
                if (e.Direction == Direction.Right && e.Index == 0) item.SelectedIndex = last;
                else if (e.Direction == Direction.Left && e.Index == last) item.SelectedIndex = 0;
            };
        }

        private NativeListItem<float> MakeFitSlider(string label, float min, float max, float step,
                                                    Func<float> get, Action<float> set, string desc)
        {
            var vals = new List<float>();
            for (float v = min; v <= max + 0.001f; v += step) vals.Add((float)Math.Round(v, 2));
            int idx = vals.FindIndex(v => Math.Abs(v - get()) < step * 0.55f);
            if (idx < 0) idx = vals.FindIndex(v => Math.Abs(v) < step * 0.55f);
            if (idx < 0) idx = vals.Count / 2;
            var item = new NativeListItem<float>(label, vals.ToArray()) { SelectedIndex = idx };
            item.Description = desc;
            item.ItemChanged += (s, e) => set(e.Object);
            _fitmentResync.Add(() =>
            {
                int j = vals.FindIndex(v => Math.Abs(v - get()) < step * 0.55f);
                if (j < 0) j = vals.FindIndex(v => Math.Abs(v) < step * 0.55f);
                if (j < 0) j = vals.Count / 2;
                item.SelectedIndex = j;
            });
            NoWrap(item);
            return item;
        }

        // ---- Wheel Width + Wheel Size (custom rims only). Width first, per the requested order. ----
        private void AddSizeWidthItems(NativeMenu menu)
        {
            if (wheelFitment.HasVisualWheels)
            {
                // Wheel Width (%) — up to 500% for wide-tire / deep-dish stance looks.
                var widthMults = new List<float>();
                var widthLabels = new List<string>();
                for (float m = 0.3f; m <= 5.001f; m += 0.05f)
                {
                    float mult = (float)Math.Round(m, 2);
                    widthMults.Add(mult);
                    widthLabels.Add(Math.Abs(mult - 1f) < 0.001f ? "100% (stock)" : $"{mult * 100f:F0}%");
                }
                int wIdx = 0;
                for (int i = 0; i < widthMults.Count; i++)
                    if (Math.Abs(widthMults[i] - wheelFitment.VisualWidth) < 0.026f) { wIdx = i; break; }
                var widthItem = new NativeListItem<string>("Wheel Width", widthLabels.ToArray()) { SelectedIndex = wIdx };
                widthItem.Description = "Fat meats or stretched rubber";
                widthItem.ItemChanged += (s, e) => { wheelFitment.VisualWidth = widthMults[e.Index]; };
                NoWrap(widthItem);
                menu.Add(widthItem);
                _fitmentResync.Add(() =>
                {
                    int j = 0;
                    for (int i = 0; i < widthMults.Count; i++)
                        if (Math.Abs(widthMults[i] - wheelFitment.VisualWidth) < 0.026f) { j = i; break; }
                    widthItem.SelectedIndex = j;
                });

                // Wheel Size (inches)
                float maxMult = wheelFitment.MaxSizeMult;
                var sizeMults = new List<float>();
                var sizeLabels = new List<string>();
                for (float m = 0.7f; m <= maxMult + 0.001f; m += 0.05f)
                {
                    float mult = (float)Math.Round(Math.Min(m, maxMult), 3);
                    if (sizeMults.Count > 0 && mult <= sizeMults[sizeMults.Count - 1]) break;
                    sizeMults.Add(mult);
                    float inches = wheelFitment.BaseVisualDiameter * mult / 0.0254f;
                    sizeLabels.Add(Math.Abs(mult - 1f) < 0.001f ? $"{inches:F1}\" (stock)" : $"{inches:F1}\"");
                }
                int sIdx = 0;
                for (int i = 0; i < sizeMults.Count; i++)
                    if (Math.Abs(sizeMults[i] - wheelFitment.VisualSize) < 0.026f) { sIdx = i; break; }
                var sizeItem = new NativeListItem<string>("Wheel Size", sizeLabels.ToArray()) { SelectedIndex = sIdx };
                sizeItem.Description = "Overall wheel diameter";
                sizeItem.ItemChanged += (s, e) => { wheelFitment.VisualSize = sizeMults[e.Index]; };
                NoWrap(sizeItem);
                menu.Add(sizeItem);
                _fitmentResync.Add(() =>
                {
                    int j = 0;
                    for (int i = 0; i < sizeMults.Count; i++)
                        if (Math.Abs(sizeMults[i] - wheelFitment.VisualSize) < 0.026f) { j = i; break; }
                    sizeItem.SelectedIndex = j;
                });
            }
            else
            {
                var hint = new NativeItem("Wheel Size / Width", "Fit custom wheels") { Enabled = false };
                hint.Description = "Install aftermarket wheels to unlock wheel sizing";
                menu.Add(hint);
            }
        }

        // ---- Camber (alignment — Suspension) ----
        private void AddCamberItems(NativeMenu menu)
        {
            if (ModSettings.WheelFitmentProMode)
            {
                menu.Add(MakeFitSlider("Front Camber", WheelFitment.WheelFitment.MIN_CAMBER, WheelFitment.WheelFitment.MAX_CAMBER, 0.01f,
                    () => wheelFitment.FrontCamber, v => wheelFitment.FrontCamber = v, "Tilt the front wheels in/out"));
                menu.Add(MakeFitSlider("Rear Camber", WheelFitment.WheelFitment.MIN_CAMBER, WheelFitment.WheelFitment.MAX_CAMBER, 0.01f,
                    () => wheelFitment.RearCamber, v => wheelFitment.RearCamber = v, "Tilt the rear wheels in/out"));
            }
            else
            {
                var degs = new List<int>(); var labels = new List<string>();
                for (int d = -45; d <= 45; d++) { degs.Add(d); labels.Add(d == 0 ? "0° (stock)" : $"{d}°"); }
                int idx = 45; float cur = WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber);
                for (int i = 0; i < degs.Count; i++) if (Math.Abs(degs[i] - cur) < 0.51f) { idx = i; break; }
                var item = new NativeListItem<string>("Camber", labels.ToArray()) { SelectedIndex = idx };
                item.Description = "Tilt the wheels in for that stanced look";
                item.ItemChanged += (s, e) =>
                {
                    float rad = WheelFitment.WheelFitment.ToRadians(degs[e.Index]);
                    wheelFitment.FrontCamber = rad; wheelFitment.RearCamber = rad;
                };
                NoWrap(item);
                menu.Add(item);
                _fitmentResync.Add(() =>
                {
                    int j = degs.IndexOf(0); float c = WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber);
                    for (int i = 0; i < degs.Count; i++) if (Math.Abs(degs[i] - c) < 0.51f) { j = i; break; }
                    item.SelectedIndex = j;
                });
            }
        }

        // ---- Ride Height (Suspension). Now drives the STABLE handling body-lift (fSuspensionRaise) — a single
        // whole-body value, so there is no front/rear split even in Pro mode. Range matches the raise clamp. ----
        private void AddHeightItems(NativeMenu menu)
        {
            int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_RAISE * 100f);   // e.g. -30 cm (slam)
            int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_RAISE * 100f);   // e.g. +45 cm (lift)
            var cms = new List<int>(); var labels = new List<string>();
            for (int cm = lo; cm <= hi; cm++) { cms.Add(cm); labels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm")); }
            int idx = cms.IndexOf(0); float cur = -wheelFitment.FrontHeight * 100f;
            for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
            var item = new NativeListItem<string>("Ride Height", labels.ToArray()) { SelectedIndex = idx };
            item.Description = "Lift (+) or slam (-) the whole car";
            item.ItemChanged += (s, e) =>
            {
                float h = -cms[e.Index] / 100f;
                wheelFitment.FrontHeight = h; wheelFitment.RearHeight = h;
            };
            NoWrap(item);
            menu.Add(item);
            _fitmentResync.Add(() =>
            {
                int j = cms.IndexOf(0); float c = -wheelFitment.FrontHeight * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                item.SelectedIndex = j;
            });
        }

        // ---- Rake (front/rear tilt — Suspension). + lowers the front, - lowers the rear. ----
        private void AddRakeItems(NativeMenu menu)
        {
            int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_RAKE * 100f);   // - = rear low
            int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_RAKE * 100f);   // + = front low
            var cms = new List<int>(); var labels = new List<string>();
            for (int cm = lo; cm <= hi; cm++)
                cms.Add(cm);
            foreach (int cm in cms)
                labels.Add(cm == 0 ? "0 (level)" : (cm > 0 ? $"+{cm} (front low)" : $"{cm} (rear low)"));
            int idx = cms.IndexOf(0); float cur = wheelFitment.Rake * 100f;
            for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
            var item = new NativeListItem<string>("Rake", labels.ToArray()) { SelectedIndex = idx };
            item.Description = "Tilt the car front/back: + drops the front, - drops the rear";
            item.ItemChanged += (s, e) => { wheelFitment.Rake = cms[e.Index] / 100f; };
            NoWrap(item);
            menu.Add(item);
            _fitmentResync.Add(() =>
            {
                int j = cms.IndexOf(0); float c = wheelFitment.Rake * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                item.SelectedIndex = j;
            });
        }

        // ---- Wheel Poke / track width (wheel fitment — Wheels) ----
        private void AddPokeItems(NativeMenu menu)
        {
            if (ModSettings.WheelFitmentProMode)
            {
                menu.Add(MakeFitSlider("Front Poke", WheelFitment.WheelFitment.MIN_TRACK_WIDTH, WheelFitment.WheelFitment.MAX_TRACK_WIDTH, 0.01f,
                    () => wheelFitment.FrontTrackWidth, v => wheelFitment.FrontTrackWidth = v, "Push front wheels out"));
                menu.Add(MakeFitSlider("Rear Poke", WheelFitment.WheelFitment.MIN_TRACK_WIDTH, WheelFitment.WheelFitment.MAX_TRACK_WIDTH, 0.01f,
                    () => wheelFitment.RearTrackWidth, v => wheelFitment.RearTrackWidth = v, "Push rear wheels out"));
            }
            else
            {
                int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_TRACK_WIDTH * 100f);   // inward (tuck)
                int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_TRACK_WIDTH * 100f);   // outward (poke)
                var cms = new List<int>(); var labels = new List<string>();
                for (int cm = lo; cm <= hi; cm++) { cms.Add(cm); labels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm")); }
                int idx = cms.IndexOf(0); float cur = wheelFitment.FrontTrackWidth * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
                var item = new NativeListItem<string>("Wheel Poke", labels.ToArray()) { SelectedIndex = idx };
                item.Description = "Push the wheels out toward the fenders";
                item.ItemChanged += (s, e) =>
                {
                    float t = cms[e.Index] / 100f;
                    wheelFitment.FrontTrackWidth = t; wheelFitment.RearTrackWidth = t;
                };
                NoWrap(item);
                menu.Add(item);
                _fitmentResync.Add(() =>
                {
                    int j = cms.IndexOf(0); float c = wheelFitment.FrontTrackWidth * 100f;
                    for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                    item.SelectedIndex = j;
                });
            }
        }

        // Init + load saved fitment for the current vehicle (shared by both fitment menus).
        private void InitFitmentForVehicle()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle)
            {
                // Hand off cleanly: restore the OLD car's model-shared suspension raise before rebinding.
                if (wheelFitment.IsInitialized && lastFitmentVehicle != currentVehicle)
                    try { wheelFitment.RestoreSuspension(); } catch { }
                wheelFitment.Initialize(currentVehicle);
                lastFitmentVehicle = currentVehicle;
                string vehName = VehicleKey(currentVehicle);
                if (VehicleSaveData.IsWheelFitmentOwned(vehName))
                {
                    var saved = VehicleSaveData.GetFitmentData(vehName);
                    wheelFitment.FrontCamber = saved.FrontCamber;
                    wheelFitment.RearCamber = saved.RearCamber;
                    wheelFitment.FrontTrackWidth = saved.FrontTrackWidth;
                    wheelFitment.RearTrackWidth = saved.RearTrackWidth;
                    wheelFitment.FrontHeight = saved.FrontHeight;
                    wheelFitment.RearHeight = saved.RearHeight;
                    wheelFitment.Rake = saved.Rake;
                    wheelFitment.StockFake = saved.StockFake;
                    wheelFitment.VisualSize = saved.VisualSize;
                    wheelFitment.VisualWidth = saved.VisualWidth;
                }
            }
        }

        // Shared "service" controls (Pro Mode toggle + Save + Reset). rebuild() rebuilds the host menu.
        // Pro Mode toggle (top of each fitment menu) — rebuilds the host menu so the item set matches.
        private void AddProModeToggle(NativeMenu menu, Func<NativeMenu> rebuild)
        {
            var proItem = new NativeCheckboxItem("Pro Mode", "Per-axle (front/rear separate) controls", ModSettings.WheelFitmentProMode);
            proItem.CheckboxChanged += (s, e) =>
            {
                ModSettings.WheelFitmentProMode = proItem.Checked;
                ModSettings.Save();
                isNavigatingMenu = true; menu.Visible = false;
                var nm = rebuild(); if (nm != null) nm.Visible = true;
                isNavigatingMenu = false;
            };
            menu.Add(proItem);
        }

        // Reset-to-stock (bottom of each fitment menu). No separate Save — menus auto-save on close.
        private void AddResetItem(NativeMenu menu, Func<NativeMenu> rebuild)
        {
            var resetItem = new NativeItem("Reset to Stock", "Reset all fitment to factory settings");
            resetItem.Activated += (s, e) =>
            {
                wheelFitment.Reset();
                SaveCurrentFitment();
                GTA.UI.Notification.Show("Fitment reset to stock");
                isNavigatingMenu = true; menu.Visible = false;
                var nm = rebuild(); if (nm != null) nm.Visible = true;
                isNavigatingMenu = false;
            };
            menu.Add(resetItem);
        }

        // Camber & Ride Height submenu — lives under the SUSPENSION category (alignment + springs are
        // suspension in real life). Pro toggle on top, then sliders, then Reset; auto-saves on close.
        private NativeMenu BuildAlignmentMenu()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return null;
            var menu = CreateMenu("Custom Suspension and Camber");
            menu.Closed += (s, e) =>
            {
                SaveCurrentFitment();   // auto-save on leave
                if (!isNavigatingMenu && alignmentParent != null) alignmentParent.Visible = true;
            };
            _fitmentResync.Clear();
            InitFitmentForVehicle();
            AddProModeToggle(menu, BuildAlignmentMenu);
            AddCamberItems(menu);
            AddHeightItems(menu);
            AddRakeItems(menu);
            AddResetItem(menu, BuildAlignmentMenu);
            return menu;
        }

        // Resync callbacks for the currently-built fitment sliders. Each one re-reads its wheelFitment value
        // and corrects its SelectedIndex in place. Rebuilt whenever a fitment menu is built.
        private readonly List<Action> _fitmentResync = new List<Action>();

        // When the stability monitor reverts an unstable setup, snap the OPEN fitment sliders to the reverted
        // values IN PLACE (correct each item's SelectedIndex). We don't rebuild the menu — that left the old
        // menu object still receiving input, so the slider would jump back to its pre-revert index on the next
        // press. Resyncing the live items keeps the player on the same menu they're navigating.
        private void RebuildOpenFitmentMenu()
        {
            try { foreach (var a in _fitmentResync) a(); }
            catch { }
        }

        // One-line summary of the current custom suspension/camber values, shown as the nav-item description.
        private string FitmentSummary()
        {
            try
            {
                if (!wheelFitment.IsInitialized) return "Ride height, camber, rake & poke";
                var parts = new List<string>();
                int h = (int)Math.Round(-wheelFitment.FrontHeight * 100f);   // +cm = lift, -cm = slam
                if (Math.Abs(h) >= 1) parts.Add($"Height {(h > 0 ? "+" : "")}{h}cm");
                int camDeg = (int)Math.Round(WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber));
                if (Math.Abs(camDeg) >= 1) parts.Add($"Camber {(camDeg > 0 ? "+" : "")}{camDeg}°");
                int rake = (int)Math.Round(wheelFitment.Rake * 100f);
                if (Math.Abs(rake) >= 1) parts.Add($"Rake {(rake > 0 ? "+" : "")}{rake}");
                int poke = (int)Math.Round(wheelFitment.FrontTrackWidth * 100f);
                if (Math.Abs(poke) >= 1) parts.Add($"Poke {(poke > 0 ? "+" : "")}{poke}cm");
                return parts.Count == 0 ? "Stock — nothing set" : string.Join("  ·  ", parts);
            }
            catch { return "Ride height, camber, rake & poke"; }
        }

        // The player chose one of the game's native Suspension levels (Stock/Lowered/Street/Sport/Competition).
        // Hand ride height over to the game: re-base our fake-lowering off the new level and zero our custom
        // offset so the two don't stack. Camber/poke/rake/size are independent and stay.
        private void OnGameSuspensionChosen()
        {
            try
            {
                // A vanilla suspension (Stock or a level) is now the active suspension, so Custom is no longer
                // equipped (it stays OWNED). Rebuild so the equipped icon moves off Custom onto the vanilla pick.
                if (currentVehicle != null && currentVehicle.Exists() &&
                    VehicleSaveData.IsCustomSuspensionEquipped(VehicleKey(currentVehicle)))
                {
                    VehicleSaveData.SetCustomSuspensionEquipped(VehicleKey(currentVehicle), false);
                    VehicleSaveData.Save();
                    CaptureMenuPosition();
                    RebuildMenusForVehicle();
                }

                if (!wheelFitment.IsInitialized) return;
                wheelFitment.SuspendRideHeight = false;
                wheelFitment.RefreshStockFake();   // the game just set the new level's ride height
                wheelFitment.FrontHeight = 0f;     // disable our custom ride-height (game's level takes over)
                wheelFitment.RearHeight = 0f;
                SaveCurrentFitment();
                RebuildOpenFitmentMenu();           // reflect the zeroed ride-height slider if it's open
            }
            catch { }
        }

        // A vanilla transmission (Stock or a level) was chosen, so Manual Transmission is no longer the active
        // transmission (it stays OWNED). Disable it + rebuild so the equipped icon moves off MT onto the pick.
        private void OnGameTransmissionChosen()
        {
            try
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!VehicleSaveData.IsManualTransmissionEquipped(VehicleKey(currentVehicle))) return;
                elscTransmission.Disable();
                VehicleSaveData.SetManualTransmissionEquipped(VehicleKey(currentVehicle), false);
                VehicleSaveData.Save();
                CaptureMenuPosition();
                RebuildMenusForVehicle();
            }
            catch { }
        }

        // Equip Manual Transmission as the ACTIVE transmission: drop any vanilla level, flag it equipped, enable
        // manual shifting, then rebuild so the equipped icon lands on MT (vanilla levels drop to owned-tick).
        private void EquipManualTransmission()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            isPreviewingMod = false; previewModIndex = -1;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.REMOVE_VEHICLE_MOD, currentVehicle, 13);   // drop vanilla transmission (mutually exclusive)
            previewOriginalValue = -1;
            VehicleSaveData.SetManualTransmissionOwned(vn, true);
            VehicleSaveData.SetManualTransmissionEquipped(vn, true);
            VehicleSaveData.Save();
            elscTransmission.SetVehicle(currentVehicle);
            elscTransmission.Enable();
            // Manual Transmission needs a gauge (gear/RPM readout) — turn one on for THIS car if it has none.
            if (VehicleSaveData.GetSpeedoStyle(vn) == 0)
            {
                VehicleSaveData.SetSpeedoStyle(vn, (int)SpeedoStyle.Simple);
                Speedo.Active = SpeedoStyle.Simple;
                ShowNotification("~g~Speedometer enabled for Manual Transmission");
            }
            ShowNotification("~g~Manual Transmission equipped~w~ — select again to edit keys");
            CaptureMenuPosition();
            RebuildMenusForVehicle();
        }

        // Equip Custom Suspension & Camber as the ACTIVE suspension: drop any vanilla level, flag it equipped,
        // re-apply the saved custom ride-height/camber, then rebuild so the equipped icon lands on Custom.
        private void EquipCustomSuspension()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            isPreviewingMod = false; previewModIndex = -1;   // don't let a menu-close revert undo the removal
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.REMOVE_VEHICLE_MOD, currentVehicle, 15);   // strip vanilla Suspension lowering
            previewOriginalValue = -1;
            VehicleSaveData.SetCustomSuspensionEquipped(vn, true);
            VehicleSaveData.Save();
            if (wheelFitment.IsInitialized) { wheelFitment.SuspendRideHeight = false; wheelFitment.ApplyRideHeightNow(); }
            ShowNotification("~g~Custom Suspension equipped~w~ — select again to edit");
            CaptureMenuPosition();
            RebuildMenusForVehicle();
        }

        private void SaveCurrentFitment()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            var fitmentData = new VehicleSaveData.FitmentData
            {
                FrontCamber = wheelFitment.FrontCamber,
                RearCamber = wheelFitment.RearCamber,
                FrontTrackWidth = wheelFitment.FrontTrackWidth,
                RearTrackWidth = wheelFitment.RearTrackWidth,
                FrontHeight = wheelFitment.FrontHeight,
                RearHeight = wheelFitment.RearHeight,
                Rake = wheelFitment.Rake,
                StockFake = wheelFitment.StockFake,
                VisualSize = wheelFitment.VisualSize,
                VisualWidth = wheelFitment.VisualWidth
            };
            VehicleSaveData.SetFitmentData(VehicleKey(currentVehicle), fitmentData);
            // Mark "owned" so InitFitmentForVehicle re-loads this on re-entry (the purchase gate that used
            // to set this was removed — fitment is now free, and saving is what flags a car as customised).
            VehicleSaveData.SetWheelFitmentOwned(VehicleKey(currentVehicle), true);
            VehicleSaveData.Save();

            // Also record this SPECIFIC car (model + plate + fingerprint + decorator tag) so the stance
            // auto-applies when the player later walks up to THIS car (and not other cars of the model).
            if (_stanceMgrInit)
            {
                if (stanceManager.RecordCar(currentVehicle, wheelFitment, out string stMsg))
                    Log($"[Stance] {stMsg}");
                else
                    GTA.UI.Notification.Show($"~o~Stance not saved per-car: {stMsg}");
            }
        }

        private NativeMenu BuildResprayMenu()
        {
            var menu = CreateMenu("Respray");
            // Primary color
            var primaryMenu = CreateColorMenu(true);
            primaryMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var primaryItem = new NativeItem("Primary Color");
            primaryItem.AltTitle = ">>";
            primaryItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; primaryMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(primaryItem);

            // Secondary color
            var secondaryMenu = CreateColorMenu(false);
            secondaryMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var secondaryItem = new NativeItem("Secondary Color");
            secondaryItem.AltTitle = ">>";
            secondaryItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; secondaryMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(secondaryItem);

            // Pearlescent
            var pearlMenu = CreatePearlescentMenu();
            pearlMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var pearlItem = new NativeItem("Pearlescent");
            pearlItem.AltTitle = ">>";
            pearlItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; pearlMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(pearlItem);

            // Wheel color
            var wheelColorMenu = CreateWheelColorMenu();
            wheelColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var wheelColorItem = new NativeItem("Wheel Color");
            wheelColorItem.AltTitle = ">>";
            wheelColorItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelColorMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelColorItem);

            return menu;
        }

        /// <summary>
        /// Recursively builds a menu from a MenuConfig Category.
        /// Handles arbitrary nesting depth with proper navigation (no stacking).
        /// </summary>
        /// <param name="category">The category to build a menu from</param>
        /// <param name="parentMenu">The parent menu to return to when closing</param>
        /// <returns>The built menu</returns>
        private NativeMenu BuildCategoryMenu(MenuConfig.Category category, NativeMenu parentMenu)
        {
            var menu = CreateMenu(category.DisplayName);
            dynamicCategoryMenus.Add(menu); // Track for cleanup on rebuild

            // Items to show = the configured items, plus an automatic "None" when this category controls a
            // single game slot (so the stock/remove option lives right here with the parts — universal, any mod).
            var displayItems = DisplayItemsFor(category);

            // Live preview + installed icon + pricing so custom categories behave like the built-in ones.
            // Items can span multiple game slots, so snapshot each slot's value on open and revert on close;
            // a purchase updates that baseline so the chosen part sticks.
            var previewOriginals = new Dictionary<int, int>();
            string vehicleName = VehicleKey(currentVehicle);

            menu.Shown += (s, e) =>
            {
                previewOriginals.Clear();
                if (currentVehicle != null && currentVehicle.Exists())
                    foreach (var it in displayItems)
                        if (it.SourceModType >= 0 && !previewOriginals.ContainsKey(it.SourceModType))
                            previewOriginals[it.SourceModType] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, it.SourceModType);
            };

            menu.SelectedIndexChanged += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < displayItems.Count && currentVehicle != null && currentVehicle.Exists())
                {
                    var it = displayItems[e.Index];
                    if (it.SourceModType >= 0)
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, it.SourceModType, it.Value, false);
                    }
                }
            };

            menu.Closed += (s, e) =>
            {
                // Revert any previewed slots to their (possibly purchase-updated) baseline.
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    foreach (var kv in previewOriginals)
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, kv.Key, kv.Value, false);
                }
                if (!isNavigatingMenu && parentMenu != null)
                    parentMenu.Visible = true;
            };

            // Track every part item so installing one MOVES the equipped icon off the previously-installed part in
            // the SAME game slot (a custom category can mix parts from several slots) instead of leaving it stuck on
            // every part ever bought. Re-evaluates each item against its slot's live value.
            var slotItems = new List<(NativeItem item, int slot, int value, int price)>();
            void RefreshCustomInstalled()
            {
                string vn = VehicleKey(currentVehicle);
                bool haveVeh = currentVehicle != null && currentVehicle.Exists();
                foreach (var (it, slot, value, pr) in slotItems)
                {
                    itemOwnershipStatus.Remove(it);
                    bool inst = slot >= 0 && haveVeh && Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, slot) == value;
                    bool own = inst || (value >= 0 && slot >= 0 && VehicleSaveData.IsModOwned(vn, slot, value));
                    if (inst) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_INSTALLED; }
                    else if (own) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_OWNED; }
                    else it.AltTitle = pr > 0 ? $"${pr:N0}" : "Free";
                }
            }

            // Add items if the category has any
            foreach (var item in displayItems)
            {
                var capturedItem = item;
                var menuItem = new NativeItem(item.Name);
                if (!string.IsNullOrEmpty(item.Description)) menuItem.Description = item.Description;
                slotItems.Add((menuItem, capturedItem.SourceModType, capturedItem.Value, capturedItem.Price));

                menuItem.Activated += (s, e) =>
                {
                    if (editModeActive)
                    {
                        int ei = category.Items.IndexOf(capturedItem);
                        if (ei >= 0) StartEditItem(category.Path, ei, false, capturedItem.Name, capturedItem.Price);
                        // synthetic "None" (ei < 0) isn't editable — just ignore in edit mode
                        return;
                    }
                    if (capturedItem.SourceModType < 0 || currentVehicle == null || !currentVehicle.Exists()) return;
                    bool own = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, capturedItem.SourceModType) == capturedItem.Value
                               || VehicleSaveData.IsModOwned(VehicleKey(currentVehicle), capturedItem.SourceModType, capturedItem.Value);
                    if (!TryPurchase(capturedItem.Price, own)) return;
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, capturedItem.SourceModType, capturedItem.Value, false);
                    previewOriginals[capturedItem.SourceModType] = capturedItem.Value;   // keep it on close
                    if (!own)
                    {
                        VehicleSaveData.SetModOwned(VehicleKey(currentVehicle), capturedItem.SourceModType, capturedItem.Value);
                        VehicleSaveData.Save();
                    }
                    RefreshCustomInstalled();   // move the equipped icon here, off any other part in the same slot
                    ShowNotification($"~g~Installed: {capturedItem.Name}");
                };

                menu.Add(menuItem);
            }
            RefreshCustomInstalled();   // initial equipped/owned icons

            // Add subcategory navigation items
            foreach (var subCategory in category.SubCategories)
            {
                if (!CategoryHasContent(subCategory)) continue;   // no empty sub-menus
                var subMenu = BuildCategoryMenu(subCategory, menu);

                var navItem = new NativeItem(subCategory.DisplayName);
                navItem.AltTitle = ">>";

                if (!string.IsNullOrEmpty(subCategory.Description))
                {
                    navItem.Description = subCategory.Description;
                }

                // Capture for closure
                var capturedSubMenu = subMenu;
                var capturedMenu = menu;
                navItem.Activated += (s, e) =>
                {
                    isNavigatingMenu = true;
                    capturedMenu.Visible = false;
                    capturedSubMenu.Visible = true;
                    isNavigatingMenu = false;
                };

                menu.Add(navItem);
            }

            // Edit mode: full modder toolset for this custom category (add sub-category / add part / edit
            // description / rename), all per-vehicle.
            if (!string.IsNullOrEmpty(category.Path)) AttachEditTools(menu, category.Path, true);

            return menu;
        }

        /// <summary>
        /// Items to show in a custom category = the configured items, plus an automatic "None" entry when the
        /// whole category drives ONE game slot and doesn't already have a stock option. Universal: any vehicle
        /// mod's single-slot category (Fenders, Bumpers, a Body's Seat...) gets a built-in revert-to-stock.
        /// </summary>
        private List<MenuConfig.MenuItem> DisplayItemsFor(MenuConfig.Category category)
        {
            var list = new List<MenuConfig.MenuItem>();
            if (category.Items != null) list.AddRange(category.Items);

            int slot = -1; bool single = false, hasNone = false;
            if (category.Items != null)
                foreach (var it in category.Items)
                {
                    if (it.SourceModType < 0) continue;
                    if (it.Value == -1) hasNone = true;
                    if (slot == -1) { slot = it.SourceModType; single = true; }
                    else if (it.SourceModType != slot) single = false;
                }

            if (single && slot >= 0 && !hasNone)
                list.Insert(0, new MenuConfig.MenuItem
                {
                    Name = "None",
                    Description = "Remove — revert this part to stock.",
                    Price = 0, Value = -1, SourceModType = slot
                });
            return list;
        }

        /// <summary>True if a custom category has any real items, or any descendant subcategory does. Used to
        /// skip rendering empty categories/sub-menus (no clutter). Universal.</summary>
        private bool CategoryHasContent(MenuConfig.Category category)
        {
            if (category == null) return false;
            if (category.Items != null && category.Items.Count > 0) return true;
            if (category.SubCategories != null)
                foreach (var sub in category.SubCategories)
                    if (CategoryHasContent(sub)) return true;
            return false;
        }

        // The body/visual slots a modder pulls custom parts from (VehicleModType, label).
        private static readonly (int slot, string name)[] PartSlots = new[]
        {
            (0, "Spoiler"), (1, "Front Bumper"), (2, "Rear Bumper"), (3, "Skirts"), (4, "Exhaust"),
            (5, "Roll Cage"), (6, "Grille"), (7, "Hood"), (8, "Fender"), (9, "Right Fender"), (10, "Roof")
        };
        private readonly List<NativeMenu> partPickerMenus = new List<NativeMenu>();
        private string pendingPartCategory = null;
        private int pendingPartSlot = -1, pendingPartIndex = -1;
        private bool isNamingPart = false;

        /// <summary>Open the part picker: every body slot that has parts -> its parts. Selecting a part previews
        /// it on the car, then opens the keyboard to name it before adding it to <paramref name="categoryPath"/>.</summary>
        // Batch session: parts the modder checkmarked across the slot submenus, committed together on Done.
        private readonly List<(int slot, int index, string name)> batchPartChecked = new List<(int, int, string)>();

        private void StartAddPart(string categoryPath, NativeMenu returnMenu)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            batchPartChecked.Clear();

            var picker = CreateMenu("Add Parts");
            partPickerMenus.Add(picker);
            picker.Closed += (s, e) => { if (!isNavigatingMenu) returnMenu.Visible = true; };

            var doneTop = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
            doneTop.Description = "Add every checked part to this category (free, default names).";
            doneTop.Activated += (s, e) => CommitBatchParts(categoryPath);
            picker.Add(doneTop);

            foreach (var (slot, name) in PartSlots)
            {
                int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, slot);
                if (count <= 0) continue;

                var slotMenu = CreateMenu(name);
                partPickerMenus.Add(slotMenu);
                int slotCap = slot, countCap = count;
                int origForSlot = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, slot);
                slotMenu.Shown += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists() && slotMenu.SelectedIndex < countCap)
                    { Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0); Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, slotMenu.SelectedIndex, false); }
                };
                slotMenu.SelectedIndexChanged += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists() && e.Index < countCap)
                    { Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0); Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, e.Index, false); }   // preview
                };
                slotMenu.Closed += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists())
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, origForSlot, false);   // revert preview
                    if (!isNavigatingMenu) picker.Visible = true;
                };

                for (int i = 0; i < count; i++)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, slot, i);
                    string pn = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                    if (string.IsNullOrEmpty(pn)) pn = $"{name} {i + 1}";
                    int s2 = slot, i2 = i; string nm = pn;
                    var cb = new NativeCheckboxItem(pn, batchPartChecked.Exists(x => x.slot == s2 && x.index == i2));
                    cb.CheckboxChanged += (s, e) =>
                    {
                        if (cb.Checked) { if (!batchPartChecked.Exists(x => x.slot == s2 && x.index == i2)) batchPartChecked.Add((s2, i2, nm)); }
                        else batchPartChecked.RemoveAll(x => x.slot == s2 && x.index == i2);
                    };
                    slotMenu.Add(cb);
                }
                var doneSub = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
                doneSub.Description = "Add every checked part (free, default names).";
                doneSub.Activated += (s, e) => CommitBatchParts(categoryPath);
                slotMenu.Add(doneSub);

                var nav = new NativeItem(name) { AltTitle = $"{count} >>" };
                nav.Activated += (s, e) => { isNavigatingMenu = true; picker.Visible = false; slotMenu.Visible = true; isNavigatingMenu = false; };
                picker.Add(nav);
            }

            isNavigatingMenu = true;
            returnMenu.Visible = false;
            picker.Visible = true;
            isNavigatingMenu = false;
        }

        private void CommitBatchParts(string categoryPath)
        {
            if (batchPartChecked.Count == 0) { ShowNotification("~y~Nothing checked yet."); return; }
            int added = 0;
            foreach (var (slot, idx, name) in batchPartChecked)
            {
                var item = new MenuConfig.MenuItem { Name = name, SourceModType = slot, Value = idx, Price = 0 };
                if (MenuConfig.AddItemToVehicleCategory(categoryPath, item)) added++;
            }
            batchPartChecked.Clear();
            ShowNotification($"~g~Added {added} part(s).");
            if (currentVehicle != null) RebuildMenusForVehicle();
        }

        private void BeginNamePart(string categoryPath, int slot, int index)
        {
            pendingPartCategory = categoryPath; pendingPartSlot = slot; pendingPartIndex = index;
            isNamingPart = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this part");
        }

        private void UpdateNamePart()
        {
            if (!isNamingPart) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingPart = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Part not added (empty name)."); return; }
                // Next step: ask for a price (blank/cancel = free).
                pendingPartName = result;
                isPricingPart = true; keyboardCheckCooldown = 5;
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 9);
                ShowNotification("~y~Set a price (leave blank for Free)");
            }
            else if (status == 2) { isNamingPart = false; }
        }

        private bool isPricingPart = false;
        private string pendingPartName = null;
        private void UpdatePricePart()
        {
            if (!isPricingPart) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1 || status == 2)   // finished or cancelled — cancel/blank = free
            {
                int price = 0;
                if (status == 1)
                {
                    string raw = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                    string digits = "";
                    foreach (char c in raw) if (char.IsDigit(c)) digits += c;
                    if (!string.IsNullOrEmpty(digits)) int.TryParse(digits, out price);
                }
                isPricingPart = false;
                var item = new MenuConfig.MenuItem { Name = pendingPartName, SourceModType = pendingPartSlot, Value = pendingPartIndex, Price = price };
                if (MenuConfig.AddItemToVehicleCategory(pendingPartCategory, item))
                {
                    ShowNotification($"~g~Added: {pendingPartName}" + (price > 0 ? $" (${price:N0})" : " (Free)"));
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't add the part.");
            }
        }

        /// <summary>
        /// Adds a dynamically loaded category to the main menu.
        /// </summary>
        /// <param name="category">The category to add</param>
        private void AddDynamicCategory(MenuConfig.Category category)
        {
            // Build the category's submenu
            var categoryMenu = BuildCategoryMenu(category, mainMenu);

            // Add navigation item to main menu
            var navItem = new NativeItem(category.DisplayName);
            navItem.AltTitle = ">>";

            if (!string.IsNullOrEmpty(category.Description))
            {
                navItem.Description = category.Description;
            }

            var capturedMenu = categoryMenu;
            navItem.Activated += (s, e) =>
            {
                isNavigatingMenu = true;
                mainMenu.Visible = false;
                capturedMenu.Visible = true;
                isNavigatingMenu = false;
            };

            categoryMenu.Closed += (s, e) =>
            {
                if (!isNavigatingMenu)
                    mainMenu.Visible = true;
            };

            mainMenu.Add(navItem);
            mainCatItems[navItem] = (category.Path, true);   // custom category; rename via the folder
        }

        /// <summary>
        /// Builds the main menu dynamically from MenuConfig folders.
        /// Custom categories appear alongside built-in ones.
        /// </summary>
        private void BuildDynamicMainMenu()
        {
            // Get all root categories from config
            var categories = MenuConfig.GetRootCategories();

            Log($"[Dynamic] Found {categories.Count} root categories");

            foreach (var category in categories)
            {
                // Skip categories that are handled specially by the hardcoded menu
                // These have special game logic (mod indices, colors, etc.)
                var specialCategories = new[] {
                    "Armor", "Brakes", "Bumpers", "Engine", "Exhaust", "Fenders",
                    "Grille", "Hood", "Horn", "Lights", "Livery", "Plate",
                    "Respray", "Roll Cage", "Roof", "Skirts", "Spoiler",
                    "Suspension", "Transmission", "Turbo", "Wheels", "Windows"
                };

                bool isSpecial = false;
                foreach (var special in specialCategories)
                {
                    if (string.Equals(category.Name, special, StringComparison.OrdinalIgnoreCase))
                    {
                        isSpecial = true;
                        break;
                    }
                }

                if (!isSpecial)
                {
                    if (!CategoryHasContent(category)) { Log($"[Dynamic] Skipping empty category: {category.Name}"); continue; }
                    // This is a user-created custom category
                    Log($"[Dynamic] Adding custom category: {category.Name}");
                    AddDynamicCategory(category);
                }
            }

            // Edit mode: modder tools at the bottom of the root menu (Add a Category).
            AttachEditTools(mainMenu, "", false);

            // Edit mode: in-game control remapper.
            if (editModeActive)
            {
                var controlsNav = new NativeItem("CONTROLS", "Remap keys & buttons. Changes save to settings.ini.") { AltTitle = ">>" };
                var cm = BuildControlsMenu();
                controlsNav.Activated += (s, e) => { isNavigatingMenu = true; mainMenu.Visible = false; cm.Visible = true; isNavigatingMenu = false; };
                cm.Closed += (s, e) => { if (!isNavigatingMenu) mainMenu.Visible = true; };
                mainMenu.Add(controlsNav);
            }
        }

        // ================= In-game control remapper (edit mode) =================
        private bool isRebindingKey = false, isRebindingButton = false;
        private string rebindLabel = null;
        private Action<Keys> rebindKeySetter = null;
        private Action<int> rebindButtonSetter = null;
        private NativeItem rebindItem = null;
        private Func<string> rebindItemValue = null;
        private LemonUI.Elements.ScaledText rebindPrompt;

        // Friendly names for controller control indices (display only; glyphs in the hint bar are device-accurate).
        private static readonly (int idx, string name)[] ControlButtonNames = new (int, string)[]
        {
            ((int)GTA.Control.FrontendAccept, "A / Cross"),
            ((int)GTA.Control.FrontendCancel, "B / Circle"),
            ((int)GTA.Control.FrontendX,      "X / Square"),
            ((int)GTA.Control.FrontendY,      "Y / Triangle"),
            ((int)GTA.Control.VehicleExit,    "Y / Triangle"),
            ((int)GTA.Control.FrontendLb,     "LB / L1"),
            ((int)GTA.Control.FrontendRb,     "RB / R1"),
            ((int)GTA.Control.Cover,          "RB / R1"),
            ((int)GTA.Control.FrontendLt,     "LT / L2"),
            ((int)GTA.Control.FrontendRt,     "RT / R2"),
            ((int)GTA.Control.FrontendLs,     "L3"),
            ((int)GTA.Control.FrontendRs,     "R3"),
            (37, "LB / L1"),
            (45, "RB / R1"),
            (76, "X / Square"),
        };
        private string ControlName(int idx)
        {
            foreach (var (i, n) in ControlButtonNames) if (i == idx) return n;
            return "#" + idx;
        }

        private NativeMenu BuildControlsMenu()
        {
            var menu = CreateMenu("CONTROLS", "CONTROLS");

            void AddKey(string label, Func<Keys> get, Action<Keys> set)
            {
                var it = new NativeItem(label, "Select to rebind. Saves to settings.ini.") { AltTitle = get().ToString() };
                Func<string> val = () => get().ToString();
                it.Activated += (s, e) => StartRebindKey(label, set, it, val);
                menu.Add(it);
            }
            void AddBtn(string label, Func<int> get, Action<int> set)
            {
                var it = new NativeItem(label, "Select to rebind. Saves to settings.ini.") { AltTitle = ControlName(get()) };
                Func<string> val = () => ControlName(get());
                it.Activated += (s, e) => StartRebindButton(label, set, it, val);
                menu.Add(it);
            }

            // --- Keyboard ---
            AddKey("Open / Close Menu",        () => ModSettings.MenuKey,            k => ModSettings.MenuKey = k);
            AddKey("Edit Mode Toggle",         () => ModSettings.EditModeKey,        k => ModSettings.EditModeKey = k);
            AddKey("Rename Category",          () => ModSettings.RenameCategoryKey,  k => ModSettings.RenameCategoryKey = k);
            AddKey("Delete / Restore Category",() => ModSettings.DeleteCategoryKey,  k => ModSettings.DeleteCategoryKey = k);
            AddKey("Debug Menu",               () => ModSettings.DebugMenuKey,       k => ModSettings.DebugMenuKey = k);
            AddKey("Shift Up (key)",           () => ModSettings.ShiftUpKey,         k => ModSettings.ShiftUpKey = k);
            AddKey("Shift Down (key)",         () => ModSettings.ShiftDownKey,       k => ModSettings.ShiftDownKey = k);
            AddKey("Neutral (key)",            () => ModSettings.NeutralKey,         k => ModSettings.NeutralKey = k);
            AddKey("Nitrous Spray (key)",      () => ModSettings.NosKey,             k => ModSettings.NosKey = k);

            // --- Controller (button glyphs auto-match the player's device) ---
            AddBtn("Walk-Around (button)",     () => ModSettings.WalkAroundButton,   v => ModSettings.WalkAroundButton = v);
            AddBtn("Camera Prev (button)",     () => ModSettings.CamPrevButton,      v => ModSettings.CamPrevButton = v);
            AddBtn("Camera Next (button)",     () => ModSettings.CamNextButton,      v => ModSettings.CamNextButton = v);
            AddBtn("Open Door (button)",       () => ModSettings.DoorButton,         v => ModSettings.DoorButton = v);
            AddBtn("Horn Preview (button)",    () => ModSettings.HornPreviewButton,  v => ModSettings.HornPreviewButton = v);
            AddBtn("Shift Up (button)",        () => ModSettings.ShiftUpButton,      v => ModSettings.ShiftUpButton = v);
            AddBtn("Shift Down (button)",      () => ModSettings.ShiftDownButton,    v => ModSettings.ShiftDownButton = v);
            AddBtn("Nitrous Spray (button)",   () => ModSettings.NosButton,          v => ModSettings.NosButton = v);

            return menu;
        }

        private void StartRebindKey(string label, Action<Keys> setter, NativeItem item, Func<string> value)
        {
            if (isRebindingKey || isRebindingButton) return;
            rebindLabel = label; rebindKeySetter = setter; rebindItem = item; rebindItemValue = value;
            isRebindingKey = true;
            ShowNotification($"~y~Press a key for: ~w~{label}");
        }
        private void StartRebindButton(string label, Action<int> setter, NativeItem item, Func<string> value)
        {
            if (isRebindingKey || isRebindingButton) return;
            rebindLabel = label; rebindButtonSetter = setter; rebindItem = item; rebindItemValue = value;
            isRebindingButton = true;
            ShowNotification($"~y~Press a controller button for: ~w~{label}");
        }
        private void CancelRebind(string why)
        {
            isRebindingKey = false; isRebindingButton = false;
            rebindKeySetter = null; rebindButtonSetter = null; rebindItem = null; rebindItemValue = null; rebindLabel = null;
            if (why != null) ShowNotification(why);
        }
        private void FinishRebind()
        {
            try { if (rebindItem != null && rebindItemValue != null) rebindItem.AltTitle = rebindItemValue(); } catch { }
            ModSettings.Save();
            CancelRebind(null);
        }
        // Controller capture — scanned each frame while the rebind prompt is up.
        private void CheckRebindButtonCapture()
        {
            if (!isRebindingButton) return;
            if (Game.IsControlJustPressed(GTA.Control.FrontendCancel)) { CancelRebind("~r~Rebind cancelled"); return; }
            foreach (var (idx, name) in ControlButtonNames)
            {
                if (idx == (int)GTA.Control.FrontendCancel) continue;   // reserved for cancel
                if (Game.IsControlJustPressed((GTA.Control)idx))
                {
                    rebindButtonSetter?.Invoke(idx);
                    ShowNotification($"~g~{rebindLabel} = {name}");
                    FinishRebind();
                    return;
                }
            }
        }
        private void DrawRebindPrompt()
        {
            string t = isRebindingButton
                ? $"~y~Press a controller button for~n~~w~{rebindLabel}~n~~y~(B / Circle to cancel)"
                : $"~y~Press a key for~n~~w~{rebindLabel}~n~~y~(Esc to cancel)";
            var pos = new System.Drawing.PointF(1080f * GTA.UI.Screen.AspectRatio / 2f, 470f);
            if (rebindPrompt == null) rebindPrompt = new LemonUI.Elements.ScaledText(pos, t, 0.5f) { Alignment = GTA.UI.Alignment.Center, Color = System.Drawing.Color.White, Outline = true };
            else { rebindPrompt.Text = t; rebindPrompt.Position = pos; }
            rebindPrompt.Draw();
        }

        private string addCategoryParent = "";
        private void StartAddCategory(string parentPath = "")
        {
            if (isEditingDescription || isAddingCategory) return;
            addCategoryParent = parentPath ?? "";
            isAddingCategory = true;
            keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name the new category");
        }

        // Reusable edit-mode footer: attach the modder tools to ANY menu, scoped to its config path. Root ("")
        // gets only Add-a-Category; a category path gets Add Category / Add Part / Edit Description (+ Rename if
        // it's a custom per-vehicle category). All changes are written under Vehicles/{model}/ — per-vehicle.
        private void AttachEditTools(NativeMenu menu, string configPath, bool isCustomCategory)
        {
            if (!editModeActive) return;
            bool isRoot = string.IsNullOrEmpty(configPath);

            var addCat = new NativeItem("~y~+ Add a Category") { AltTitle = "EDIT" };
            addCat.Description = isRoot ? "Create a new custom category for this vehicle."
                                       : "Create a custom sub-category here.";
            addCat.Activated += (s, e) => StartAddCategory(configPath);
            menu.Add(addCat);

            if (isRoot) return;

            var thisMenu = menu;
            // "Add a Part" only inside custom categories — parts render there. Built-in submenus get sub-categories
            // (which you then fill with parts), since the built-in part list itself is hardcoded.
            if (isCustomCategory)
            {
                var addPart = new NativeItem("~y~+ Add a Part") { AltTitle = "EDIT" };
                addPart.Description = "Pick a real body part and add it here under your own name.";
                addPart.Activated += (s, e) => StartAddPart(configPath, thisMenu);
                menu.Add(addPart);
            }

            var editDesc = new NativeItem("~y~Edit Description") { AltTitle = "EDIT" };
            editDesc.Description = "Edit this category's description (for this vehicle).";
            editDesc.Activated += (s, e) => StartEditDescriptionVeh(configPath);
            menu.Add(editDesc);

            if (isCustomCategory)
            {
                var rename = new NativeItem("~y~Rename Category") { AltTitle = "EDIT" };
                rename.Description = "Rename this custom category.";
                rename.Activated += (s, e) => StartRenameCategory(configPath);
                menu.Add(rename);
            }
        }

        // Config folder path for a built-in body slot, so custom sub-categories nest under the right place
        // (e.g. a custom category created in the Hood menu lives at Vehicles/{model}/Hood/...). null = not body.
        private string ConfigPathForSlot(int modIndex)
        {
            switch (modIndex)
            {
                case 0: return "Spoiler";
                case 1: return "Bumpers/Front Bumper";
                case 2: return "Bumpers/Rear Bumper";
                case 3: return "Skirts";
                case 4: return "Exhaust";
                case 5: return "Roll Cage";
                case 6: return "Grille";
                case 7: return "Hood";
                case 8: return "Fenders/Left Fender";
                case 9: return "Fenders/Right Fender";
                case 10: return "Roof";
                default: return null;
            }
        }

        // Render this vehicle's custom sub-categories created under <paramref name="parentPath"/> as nav items.
        private void RenderCustomChildren(NativeMenu menu, string parentPath)
        {
            foreach (var child in MenuConfig.GetVehicleSubCategories(parentPath))
            {
                var subMenu = BuildCategoryMenu(child, menu);
                var nav = new NativeItem(child.DisplayName) { AltTitle = ">>" };
                if (!string.IsNullOrEmpty(child.Description)) nav.Description = child.Description;
                var capturedSub = subMenu; var capturedMenu = menu;
                nav.Activated += (s, e) => { isNavigatingMenu = true; capturedMenu.Visible = false; capturedSub.Visible = true; isNavigatingMenu = false; };
                menu.Add(nav);
            }
        }

        // ===== Universal wheel categories (edit mode) =====
        private readonly List<NativeMenu> universalWheelMenus = new List<NativeMenu>();
        // Menus where you actually BROWSE wheels (per-type lists + custom wheel categories). D-pad Left/Right turns the
        // front wheels to inspect a rim ONLY while one of these is on screen — so suspension/tuning sliders, paint, etc.
        // (where Left/Right adjusts a value) never twitch the wheels. Rebuilt with the menu (cleared in the rebuild).
        private readonly HashSet<NativeMenu> wheelInspectMenus = new HashSet<NativeMenu>();
        private string pendingWheelCategory = null;
        private int pendingWheelType = -1, pendingWheelIndex = -1;
        private bool isNamingWheel = false;
        private bool isAddingWheelCategory = false;
        private static readonly (int type, string name)[] WheelPickerTypes = new[]
        {
            (0,"Sport"),(1,"Muscle"),(2,"Lowrider"),(3,"SUV"),(4,"Off-Road"),(5,"Tuner"),(6,"Bike"),
            (7,"High End"),(8,"Benny's Originals"),(9,"Benny's Bespoke"),(10,"Open Wheel"),(11,"Street"),(12,"Track")
        };

        private void PreviewWheel(int wheelType, int index)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, index, false);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, index, false);
        }

        // Menu for a universal wheel category: each item is a rim (WheelType+Value). Preview on hover, revert on
        // close, installed icon. Apply on select (preview-only in edit mode).
        private NativeMenu BuildWheelCategoryMenu(MenuConfig.Category category, NativeMenu parentMenu)
        {
            var menu = CreateMenu(category.DisplayName);
            universalWheelMenus.Add(menu);
            wheelInspectMenus.Add(menu);   // browsing rims here → D-pad Left/Right inspect-steers the wheels

            int[] orig = { -1, -1, -1 };   // wheelType, front(23), back(24)
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    orig[0] = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
                    orig[1] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
                    orig[2] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
                }
            };
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < category.Items.Count)
                {
                    var it = category.Items[e.Index];
                    if (it.WheelType >= 0) PreviewWheel(it.WheelType, it.Value);
                }
            };
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() && orig[0] >= 0)
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, orig[0]);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, orig[1], false);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, orig[2], false);
                }
                if (!isNavigatingMenu && parentMenu != null) parentMenu.Visible = true;
            };

            foreach (var item in category.Items)
            {
                var capturedItem = item;
                var mi = new NativeItem(item.Name);
                if (!string.IsNullOrEmpty(item.Description)) mi.Description = item.Description;
                bool installed = capturedItem.WheelType >= 0 && currentVehicle != null && currentVehicle.Exists()
                    && Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle) == capturedItem.WheelType
                    && Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23) == capturedItem.Value;
                if (installed) { mi.AltTitle = ""; itemOwnershipStatus[mi] = STATUS_INSTALLED; }
                else mi.AltTitle = capturedItem.Price > 0 ? $"${capturedItem.Price:N0}" : "Free";
                mi.Activated += (s, e) =>
                {
                    if (editModeActive) { StartEditItem(category.Name, category.Items.IndexOf(capturedItem), true, capturedItem.Name, capturedItem.Price); return; }
                    if (capturedItem.WheelType < 0 || currentVehicle == null || !currentVehicle.Exists()) return;
                    if (!TryPurchase(capturedItem.Price, false)) return;
                    PreviewWheel(capturedItem.WheelType, capturedItem.Value);
                    orig[0] = capturedItem.WheelType; orig[1] = capturedItem.Value; orig[2] = capturedItem.Value; // commit
                    itemOwnershipStatus[mi] = STATUS_INSTALLED; mi.AltTitle = "";
                    ShowNotification($"~g~Installed: {capturedItem.Name}");
                };
                menu.Add(mi);
            }

            if (editModeActive)
            {
                var thisMenu = menu; string catName = category.Name;
                var addWheel = new NativeItem("~y~+ Add a Wheel") { AltTitle = "EDIT" };
                addWheel.Description = "Pick a rim from any wheel type and add it to this category.";
                addWheel.Activated += (s, e) => StartAddWheel(catName, thisMenu);
                menu.Add(addWheel);
            }
            return menu;
        }

        // Batch session: rims the modder checkmarked across the wheel-type submenus, committed together on Done.
        private readonly List<(int type, int index, string name)> batchWheelChecked = new List<(int, int, string)>();

        // Wheel picker: every wheel type that has rims -> its rims (preview on hover). Checkmark the ones you want
        // and hit Done — they're all added (default names, free). Edit names/prices afterwards or in the config.
        private void StartAddWheel(string categoryName, NativeMenu returnMenu)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            batchWheelChecked.Clear();
            int saveType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
            int saveFront = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int saveBack = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);

            void Restore()
            {
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, saveType);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, saveFront, false);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, saveBack, false);
            }

            var picker = CreateMenu("Add Wheels");
            universalWheelMenus.Add(picker);
            picker.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists()) Restore();
                if (!isNavigatingMenu) returnMenu.Visible = true;
            };

            var doneTop = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
            doneTop.Description = "Add every checked wheel to this category (free, default names).";
            doneTop.Activated += (s, e) => CommitBatchWheels(categoryName);
            picker.Add(doneTop);

            foreach (var (wtype, wname) in WheelPickerTypes)
            {
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wtype);
                int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23);
                if (count <= 0) continue;

                var typeMenu = CreateMenu(wname);
                universalWheelMenus.Add(typeMenu);
                int typeCap = wtype, countCap = count;
                typeMenu.Shown += (s, e) => { if (currentVehicle != null && currentVehicle.Exists() && typeMenu.SelectedIndex < countCap) PreviewWheel(typeCap, typeMenu.SelectedIndex); };
                typeMenu.SelectedIndexChanged += (s, e) => { if (currentVehicle != null && currentVehicle.Exists() && e.Index < countCap) PreviewWheel(typeCap, e.Index); };
                typeMenu.Closed += (s, e) => { if (!isNavigatingMenu) picker.Visible = true; };
                for (int i = 0; i < count; i++)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 23, i);
                    string rn = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                    if (string.IsNullOrEmpty(rn)) rn = $"{wname} {i + 1}";
                    int wt = wtype, ii = i; string nm = rn;
                    var cb = new NativeCheckboxItem(rn, batchWheelChecked.Exists(x => x.type == wt && x.index == ii));
                    cb.CheckboxChanged += (s, e) =>
                    {
                        if (cb.Checked) { if (!batchWheelChecked.Exists(x => x.type == wt && x.index == ii)) batchWheelChecked.Add((wt, ii, nm)); }
                        else batchWheelChecked.RemoveAll(x => x.type == wt && x.index == ii);
                    };
                    typeMenu.Add(cb);
                }
                var doneSub = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
                doneSub.Description = "Add every checked wheel (free, default names).";
                doneSub.Activated += (s, e) => CommitBatchWheels(categoryName);
                typeMenu.Add(doneSub);

                var nav = new NativeItem(wname) { AltTitle = $"{count} >>" };
                nav.Activated += (s, e) => { isNavigatingMenu = true; picker.Visible = false; typeMenu.Visible = true; isNavigatingMenu = false; };
                picker.Add(nav);
            }
            Restore();   // leave the car as it was after enumerating types

            isNavigatingMenu = true; returnMenu.Visible = false; picker.Visible = true; isNavigatingMenu = false;
        }

        private void CommitBatchWheels(string categoryName)
        {
            if (batchWheelChecked.Count == 0) { ShowNotification("~y~Nothing checked yet."); return; }
            int added = 0;
            foreach (var (wt, idx, name) in batchWheelChecked)
            {
                var item = new MenuConfig.MenuItem { Name = name, WheelType = wt, Value = idx, Price = 0 };
                if (MenuConfig.AddWheelToUniversalCategory(categoryName, item)) added++;
            }
            batchWheelChecked.Clear();
            ShowNotification($"~g~Added {added} wheel(s) to {categoryName}.");
            if (currentVehicle != null) RebuildMenusForVehicle();
        }

        private void BeginNameWheel(string categoryName, int wheelType, int index)
        {
            pendingWheelCategory = categoryName; pendingWheelType = wheelType; pendingWheelIndex = index;
            isNamingWheel = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this wheel");
        }

        private void UpdateNameWheel()
        {
            if (!isNamingWheel) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingWheel = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Wheel not added (empty name)."); return; }
                var item = new MenuConfig.MenuItem { Name = result, WheelType = pendingWheelType, Value = pendingWheelIndex, Price = 0 };
                if (MenuConfig.AddWheelToUniversalCategory(pendingWheelCategory, item))
                {
                    ShowNotification($"~g~Added: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't add the wheel.");
            }
            else if (status == 2) { isNamingWheel = false; }
        }

        private void StartAddWheelCategory()
        {
            if (isAddingWheelCategory) return;
            isAddingWheelCategory = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name the new wheel category");
        }

        private void UpdateAddWheelCategory()
        {
            if (!isAddingWheelCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isAddingWheelCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Empty name."); return; }
                if (MenuConfig.CreateUniversalWheelCategory(result))
                {
                    ShowNotification($"~g~Wheel category created: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~That wheel category already exists.");
            }
            else if (status == 2) { isAddingWheelCategory = false; }
        }

        // ---- Edit an existing custom item's name + price (edit mode; A/Enter on the item) ----
        private bool isEditingItemName = false, isEditingItemPrice = false;
        private string editItemCategory = null, editItemName = null;
        private int editItemIndex = -1, editItemPrice = 0;
        private bool editItemIsWheel = false;

        private void StartEditItem(string category, int index, bool isWheel, string currentName, int currentPrice)
        {
            if (isEditingItemName || isEditingItemPrice) return;
            editItemCategory = category; editItemIndex = index; editItemIsWheel = isWheel;
            editItemName = currentName; editItemPrice = currentPrice;
            isEditingItemName = true; keyboardCheckCooldown = 5;
            // Pre-fill the current name so the modder can just press Enter to keep it and jump to the price.
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", currentName ?? "", "", "", "", 64);
            ShowNotification("~y~Edit name (Enter to keep), then price");
        }

        private void UpdateEditItemName()
        {
            if (!isEditingItemName) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                if (!string.IsNullOrEmpty(result)) editItemName = result;   // empty -> keep current name
                isEditingItemName = false;
                isEditingItemPrice = true; keyboardCheckCooldown = 5;
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", editItemPrice.ToString(), "", "", "", 9);
                ShowNotification("~y~Edit price (Enter to keep)");
            }
            else if (status == 2) { isEditingItemName = false; }   // cancel aborts the whole edit
        }

        private void UpdateEditItemPrice()
        {
            if (!isEditingItemPrice) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1 || status == 2)
            {
                int price = editItemPrice;
                if (status == 1)
                {
                    string raw = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                    string digits = ""; foreach (char c in raw) if (char.IsDigit(c)) digits += c;
                    if (!string.IsNullOrEmpty(digits)) int.TryParse(digits, out price);
                }
                isEditingItemPrice = false;
                bool ok = editItemIsWheel
                    ? MenuConfig.UpdateUniversalWheelItem(editItemCategory, editItemIndex, editItemName, price)
                    : MenuConfig.UpdateVehicleCategoryItem(editItemCategory, editItemIndex, editItemName, price);
                if (ok)
                {
                    ShowNotification($"~g~Updated: {editItemName}" + (price > 0 ? $" (${price:N0})" : " (Free)"));
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't update item.");
            }
        }

        private bool editDescVehicleSpecific = false;
        private void StartEditDescriptionVeh(string categoryPath)
        {
            if (isEditingDescription) return;
            editingCategoryName = categoryPath;
            editDescVehicleSpecific = true;
            isEditingDescription = true;
            keyboardCheckCooldown = 5;
            string cur = MenuConfig.GetDescription(categoryPath) ?? "";
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", cur, "", "", "", 200);
            ShowNotification("~y~Edit the description");
        }

        private bool isRenamingCategory = false;
        private string pendingRenamePath = null;
        private bool pendingRenameIsCustom = true;
        private void StartRenameCategory(string categoryKey, bool isCustom = true)
        {
            if (isRenamingCategory || isAddingCategory || isEditingDescription) return;
            pendingRenamePath = categoryKey;
            pendingRenameIsCustom = isCustom;
            isRenamingCategory = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~New category name");
        }

        private void UpdateRenameCategory()
        {
            if (!isRenamingCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isRenamingCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Rename cancelled (empty)."); return; }
                bool ok;
                if (pendingRenameIsCustom) ok = MenuConfig.RenameVehicleCategory(pendingRenamePath, result);
                else { MenuConfig.SetCategoryRename(pendingRenamePath, result); ok = true; }   // built-in override
                if (ok)
                {
                    ShowNotification($"~g~Renamed to: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't rename.");
            }
            else if (status == 2) { isRenamingCategory = false; }
        }

        private void UpdateAddCategory()
        {
            if (!isAddingCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isAddingCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Category not created (empty name)."); return; }
                if (MenuConfig.CreateVehicleCategory(addCategoryParent, result))
                {
                    ShowNotification($"~g~Category created: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~That category already exists.");
            }
            else if (status == 2) { isAddingCategory = false; }   // cancelled
        }

        #endregion
    }
}
