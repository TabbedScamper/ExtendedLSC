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
        #region Utility

        private void ShowNotification(string message)
        {
            GTA.UI.Notification.Show(message);
        }

        /// <summary>
        /// Draw debug text at a screen position (used for debug overlays that need to avoid subtitle conflicts)
        /// </summary>
        /// <summary>Show the native help bubble WITHOUT a beep, via the raw natives (SHVDN's ShowHelpTextThisFrame
        /// re-triggers the appear-sound every frame even with beep:false). Called per-frame, it persists and
        /// overrides the game's own help bubble in the same slot — same look as before, no constant beep.</summary>
        private void ShowSilentHelp(string text)
        {
            if (!ModSettings.ShowHints) return;   // INI: suppress ELSC's hint bubbles everywhere
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);   // loop=false, BEEP=false, shape=none
        }

        private void DrawDebugText(string text, float x, float y, int r = 255, int g = 255, int b = 255)
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.3f);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, 255);
            Function.Call(Hash.SET_TEXT_DROPSHADOW, 1, 0, 0, 0, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y);
        }

        /// <summary>
        /// Draw a 50% transparent yellow overlay on a UI element
        /// </summary>
        private void DrawElementHighlight(UIElement element)
        {
            var visibleMenu = GetVisibleMenu();
            if (visibleMenu == null) return;

            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = ActualAspect();  // real resolution, not GTA.UI.Screen (fixed 1280x720)
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = visibleMenu.Banner.Position;
            var bannerSize = visibleMenu.Banner.Size;

            float menuLeftX = bannerPos.X / lemonXBase;
            float menuTopY = bannerPos.Y / lemonYBase;
            float menuWidth = bannerSize.Width / lemonXBase;
            float bannerHeight = bannerSize.Height / lemonYBase;

            int totalItems = visibleMenu.Items.Count;
            int maxVisible = visibleMenu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            float subtitleHeight = 38f / lemonYBase;
            float itemHeight = 38f / lemonYBase;
            float visibleItemsHeight = itemHeight * Math.Min(totalItems, maxVisible);
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            // 50% transparent yellow (128 alpha out of 255)
            int yellow_r = 255, yellow_g = 255, yellow_b = 0, yellow_a = 128;

            switch (element)
            {
                case UIElement.Menu:
                    // Highlight entire menu with yellow overlay
                    float menuTotalHeight = bannerHeight + subtitleHeight + visibleItemsHeight + (hasScrollIndicator ? scrollIndicatorHeight : 0f) + 0.05f;
                    DrawHighlightOverlay(menuLeftX, menuTopY, menuWidth, menuTotalHeight, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;

                case UIElement.ScrollArrows:
                    if (hasScrollIndicator)
                    {
                        float arrowY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight + scrollArrowsOffsetY;
                        float arrowX = menuLeftX + scrollArrowsOffsetX;
                        float arrowW = menuWidth * scrollArrowsScaleW;
                        float arrowH = scrollIndicatorHeight * scrollArrowsScaleH;
                        DrawHighlightOverlay(arrowX, arrowY, arrowW, arrowH, yellow_r, yellow_g, yellow_b, yellow_a);
                    }
                    break;

                case UIElement.Description:
                    float descY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight + descriptionOffsetY;
                    if (hasScrollIndicator) descY += scrollIndicatorHeight + 0.01f;
                    float descX = menuLeftX + descriptionOffsetX;
                    float descW = menuWidth * descriptionScaleW;
                    float descH = GetDescriptionHeight(visibleMenu, menuWidth, menuLeftX);
                    if (descH == 0f) descH = 0.045f * descriptionScaleH;
                    DrawHighlightOverlay(descX, descY, descW, descH, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;

                case UIElement.StatsPanel:
                    float statsDescY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight;
                    if (hasScrollIndicator) statsDescY += scrollIndicatorHeight + 0.01f;
                    float dynDescH = GetDescriptionHeight(visibleMenu, menuWidth, menuLeftX);
                    if (dynDescH == 0f) dynDescH = 0.045f * descriptionScaleH;
                    float statsY = statsDescY + dynDescH + descriptionOffsetY + 0.005f + statsPanelOffsetY;
                    float statsX = menuLeftX + statsPanelOffsetX;
                    float statsPanelW = menuWidth * statsPanelScaleW;
                    float statsPanelH = (statsRowHeight * 4 + statsPanelPadding) * statsPanelScaleH;
                    DrawHighlightOverlay(statsX, statsY, statsPanelW, statsPanelH, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;

                case UIElement.Banner:
                    // customBanner.Position/Size are in CustomSprite ~1280x720 space -> normalize to 0..1 for the overlay.
                    if (customBanner != null)
                    {
                        float bw = customBanner.Size.Width / 1280f;
                        float bh = customBanner.Size.Height / 720f;
                        float bx = customBanner.Position.X / 1280f;
                        float by = customBanner.Position.Y / 720f;
                        DrawHighlightOverlay(bx, by, bw, bh, yellow_r, yellow_g, yellow_b, yellow_a);
                    }
                    break;
            }
        }

        /// <summary>
        /// Draw a filled semi-transparent rectangle overlay
        /// </summary>
        private void DrawHighlightOverlay(float x, float y, float width, float height, int r, int g, int b, int a)
        {
            float centerX = x + width / 2f;
            float centerY = y + height / 2f;
            Function.Call(Hash.DRAW_RECT, centerX, centerY, width, height, r, g, b, a);
        }

        /// <summary>
        /// Get current value for a resizer field
        /// </summary>
        private float GetResizerValue(string fieldName)
        {
            switch (fieldName)
            {
                case "Transaction Text": return transactionTextScale;
                case "Sprite Scale": return spriteScale;
                case "Arrow Sprite": return arrowSpriteScale;
                case "Description Text": return descriptionTextScale;
                case "Stats Row Height": return statsRowHeight;
                case "Stats Bar Height": return statsSegmentHeight;
                case "Stats Bar Gap": return statsSegmentGap;
                case "Stats Label Scale": return statsLabelScale;
                case "Stats Padding": return statsPanelPadding;
                case "Stats Content Y": return statsContentOffsetY;
                case "Stats Bar Inset": return statsBarInset;
                case "Stats Text Left": return statsTextLeftPadding;
                case "Stats Bar Y": return statsBarOffsetY;
                default: return 0f;
            }
        }

        /// <summary>
        /// Log all resizer values to debug log
        /// </summary>
        private void LogResizerValues()
        {
            string log = "\n========== RESIZER VALUES ==========\n";
            log += $"transactionTextScale = {transactionTextScale:F3}f;\n";
            log += $"descriptionTextScale = {descriptionTextScale:F3}f;\n";
            log += $"spriteScale = {spriteScale:F2}f;\n";
            log += $"arrowSpriteScale = {arrowSpriteScale:F2}f;\n";
            log += $"statsRowHeight = {statsRowHeight:F4}f;\n";
            log += $"statsSegmentHeight = {statsSegmentHeight:F4}f;\n";
            log += $"statsSegmentGap = {statsSegmentGap:F4}f;\n";
            log += $"statsLabelScale = {statsLabelScale:F3}f;\n";
            log += $"statsPanelPadding = {statsPanelPadding:F4}f;\n";
            log += $"statsContentOffsetY = {statsContentOffsetY:F4}f;\n";
            log += $"statsBarInset = {statsBarInset:F4}f;\n";
            log += $"statsTextLeftPadding = {statsTextLeftPadding:F4}f;\n";
            log += $"statsBarOffsetY = {statsBarOffsetY:F4}f;\n";
            log += "=====================================\n";

            System.IO.File.AppendAllText("scripts/ExtendedLSC_debug.log", log);
        }

        /// <summary>
        /// Get offset string for a UI element
        /// </summary>
        private string GetElementOffsetString(UIElement element)
        {
            switch (element)
            {
                case UIElement.Menu: return $"({mainMenu.Offset.X:F0}, {mainMenu.Offset.Y:F0})";
                case UIElement.ScrollArrows: return $"({scrollArrowsOffsetX:F3}, {scrollArrowsOffsetY:F3})";
                case UIElement.Description: return $"({descriptionOffsetX:F3}, {descriptionOffsetY:F3})";
                case UIElement.StatsPanel: return $"({statsPanelOffsetX:F3}, {statsPanelOffsetY:F3})";
                case UIElement.Banner: return $"({bannerOffsetX:F1}, {bannerOffsetY:F1})";
                default: return "(0, 0)";
            }
        }

        private string GetElementScaleString(UIElement element)
        {
            switch (element)
            {
                case UIElement.Menu: return "N/A";
                case UIElement.ScrollArrows: return $"W:{scrollArrowsScaleW:F2} H:{scrollArrowsScaleH:F2}";
                case UIElement.Description: return $"W:{descriptionScaleW:F2} H:{descriptionScaleH:F2}";
                case UIElement.StatsPanel: return $"W:{statsPanelScaleW:F2} H:{statsPanelScaleH:F2}";
                case UIElement.Banner: return $"W:{bannerScaleW:F2} H:{bannerScaleH:F2}";
                default: return "N/A";
            }
        }

        /// <summary>
        /// Log all menu position values to debug log
        /// </summary>
        private void LogMenuPositionValues()
        {
            string log = "\n========== MENU POSITION VALUES ==========\n";
            log += $"// Menu (LemonUI pixel offset)\n";
            log += $"mainMenu.Offset = new PointF({mainMenu.Offset.X:F0}f, {mainMenu.Offset.Y:F0}f);\n\n";
            log += $"// Scroll Arrows (normalized screen coords)\n";
            log += $"scrollArrowsOffsetX = {scrollArrowsOffsetX:F4}f;\n";
            log += $"scrollArrowsOffsetY = {scrollArrowsOffsetY:F4}f;\n";
            log += $"scrollArrowsScaleW = {scrollArrowsScaleW:F2}f;\n";
            log += $"scrollArrowsScaleH = {scrollArrowsScaleH:F2}f;\n\n";
            log += $"// Description (normalized screen coords)\n";
            log += $"descriptionOffsetX = {descriptionOffsetX:F4}f;\n";
            log += $"descriptionOffsetY = {descriptionOffsetY:F4}f;\n";
            log += $"descriptionScaleW = {descriptionScaleW:F2}f;\n";
            log += $"descriptionScaleH = {descriptionScaleH:F2}f;\n\n";
            log += $"// Stats Panel (normalized screen coords)\n";
            log += $"statsPanelOffsetX = {statsPanelOffsetX:F4}f;\n";
            log += $"statsPanelOffsetY = {statsPanelOffsetY:F4}f;\n";
            log += $"statsPanelScaleW = {statsPanelScaleW:F2}f;\n";
            log += $"statsPanelScaleH = {statsPanelScaleH:F2}f;\n\n";
            log += $"// Banner (CustomSprite ~1280x720 space) @ resolution {cachedScreenW:F0}x{cachedScreenH:F0}\n";
            log += $"bannerOffsetX = {bannerOffsetX:F1}f;\n";
            log += $"bannerOffsetY = {bannerOffsetY:F1}f;\n";
            log += $"bannerScaleW = {bannerScaleW:F2}f;\n";
            log += $"bannerScaleH = {bannerScaleH:F2}f;\n\n";
            // RAW menu geometry diagnostic — what LemonUI actually reports for the visible menu's banner,
            // so the per-resolution element formula can be derived from real numbers (not assumed conventions).
            NativeMenu _vm = null;
            foreach (var _o in menuPool) if (_o is NativeMenu _m && _m.Visible) { _vm = _m; break; }
            if (_vm != null)
            {
                float _sw = GTA.UI.Screen.Width, _sh = GTA.UI.Screen.Height, _asp = _sw / _sh;
                var _bp = _vm.Banner.Position; var _bs = _vm.Banner.Size;
                float _lxb = 1080f * _asp;
                log += $"// RAW @ {_sw:F0}x{_sh:F0}  aspect={_asp:F3}\n";
                log += $"bannerPos=({_bp.X:F1}, {_bp.Y:F1})  bannerSize=({_bs.Width:F1}, {_bs.Height:F1})\n";
                log += $"lemonXBase={_lxb:F1}  menuLeftX={_bp.X / _lxb:F4}  menuWidth={_bs.Width / _lxb:F4}  menuCenterX={(_bp.X + _bs.Width / 2f) / _lxb:F4}\n";
            }
            log += "===========================================\n";

            System.IO.File.AppendAllText("scripts/ExtendedLSC_debug.log", log);
        }

        /// <summary>
        /// Get the player's current cash
        /// </summary>
        private int GetPlayerCash()
        {
            return Game.Player.Money;
        }

        /// <summary>
        /// Check if player can afford the price
        /// </summary>
        private bool CanAfford(int price)
        {
            return Game.Player.Money >= price;
        }

        /// <summary>
        /// Subtract cash from player. Returns true if successful.
        /// Sets transaction display to show -$XXX on screen.
        /// </summary>
        private bool SubtractCash(int amount)
        {
            if (amount <= 0) return true; // Free items always succeed
            if (Game.Player.Money < amount) return false;

            Game.Player.Money -= amount;

            // Set transaction display
            transactionAmount = amount;
            transactionStartTime = Game.GameTime;

            return true;
        }

        /// <summary>
        /// Try to purchase - checks if owned or can afford, subtracts cash if needed
        /// Returns true if purchase successful (owned or paid)
        /// </summary>
        // Edit mode blanket guard for commit (apply/install) handlers. Returns true (and notifies) when the
        // modder is editing, so the caller bails before changing anything. Preview-on-hover never calls this,
        // so previews + name/price editing still work. TryPurchase also calls it as a backstop.
        private bool EditBlockApply()
        {
            if (!editModeActive) return false;
            ShowNotification("~y~Edit mode: preview only (nothing applied)");
            return true;
        }

        private bool TryPurchase(int price, bool alreadyOwned)
        {
            // Backstop: every commit that flows through here is blocked while editing (paid OR free).
            if (EditBlockApply()) return false;

            if (ModSettings.AllItemsFree) return true;   // INI: everything's free
            if (alreadyOwned || price <= 0) return true;

            if (!CanAfford(price))
            {
                // Show "not enough cash" in description area for 5 seconds
                showNotEnoughCash = true;
                notEnoughCashStartTime = Game.GameTime;
                return false;
            }

            SubtractCash(price);
            return true;
        }

        private void Log(string message)
        {
            if (!ModSettings.DebugLogging) return;

            try
            {
                // NOTE: do NOT use Assembly.Location here — SHVDN loads script DLLs from bytes (so the
                // file can be hot-swapped), which makes Location an empty string and the write throw.
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC.log");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                System.IO.File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
            catch { }
        }

        // --- Live menu-state publishing (for bridge-driven, feedback-based navigation) ---
        private string _lastMenuStateJson = "";
        private void DumpMenuState()
        {
            if (!ModSettings.DebugLogging) return; // dev/automation only — keeps release builds from writing the file
            try
            {
                NativeMenu vis = null;
                foreach (var obj in menuPool)
                    if (obj is NativeMenu m && m.Visible) vis = m; // last visible = topmost open submenu

                string json;
                if (vis == null)
                {
                    json = "{\"open\":false}";
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("{\"open\":true,\"menu\":").Append(JsonStr(vis.Name));
                    sb.Append(",\"selectedIndex\":").Append(vis.SelectedIndex);
                    sb.Append(",\"count\":").Append(vis.Items.Count);
                    sb.Append(",\"selected\":").Append(JsonStr(vis.SelectedItem != null ? vis.SelectedItem.Title : ""));
                    sb.Append(",\"selectedValue\":").Append(JsonStr(TryGetListValue(vis.SelectedItem)));
                    sb.Append(",\"items\":[");
                    for (int i = 0; i < vis.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonStr(vis.Items[i].Title));
                    }
                    sb.Append("]}");
                    json = sb.ToString();
                }

                if (json != _lastMenuStateJson)
                {
                    _lastMenuStateJson = json;
                    string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC_menustate.json");
                    System.IO.File.WriteAllText(p, json);
                }
            }
            catch { }
        }

        private static string TryGetListValue(NativeItem item)
        {
            if (item == null) return "";
            try
            {
                // NativeListItem<T> exposes SelectedItem (the T value); read it generically.
                var prop = item.GetType().GetProperty("SelectedItem");
                if (prop != null && prop.PropertyType != typeof(NativeItem))
                {
                    var val = prop.GetValue(item);
                    if (val != null) return val.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string JsonStr(string v)
        {
            if (v == null) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in v)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ---- Collision wireframe overlay (F8) ----
        // GREEN = body collision box (model extents). BLUE = each wheel's PHYSICAL collider cylinder, built
        // from the live collision radius/width in CWheel memory — so you can see collision vs the rendered
        // wheel (float = visual below collider, sink = visual above it) while re-tuning the raw fitment.
        private void DrawCollisionDebug()
        {
            try
            {
                Vehicle v = currentVehicle;
                if (v == null || !v.Exists())
                {
                    Ped pl = Game.Player.Character;
                    v = (pl != null && pl.Exists() && pl.IsInVehicle()) ? pl.CurrentVehicle : null;
                }
                if (v == null || !v.Exists()) return;

                // GREEN body collision — the ACTUAL collision surface, found by raycasting a grid from all
                // six sides inward and keeping where the rays hit this vehicle. Scanned once per vehicle (the
                // body collision is static) and cached in vehicle-local space, so it tracks the car as it
                // moves. Drawn as a point cloud (small crosses).
                if (_bodyScanHandle != v.Handle)
                {
                    ScanBodyCollision(v);
                    _bodyScanHandle = v.Handle;
                }
                Color green = Color.FromArgb(255, 0, 255, 0);
                foreach (Vector3 lp in _bodyHits)
                {
                    Vector3 w = v.GetOffsetPosition(lp);
                    World.DrawLine(w + new Vector3(-0.03f, 0, 0), w + new Vector3(0.03f, 0, 0), green);
                    World.DrawLine(w + new Vector3(0, -0.03f, 0), w + new Vector3(0, 0.03f, 0), green);
                    World.DrawLine(w + new Vector3(0, 0, -0.03f), w + new Vector3(0, 0, 0.03f), green);
                }
                // Extents box from the body-only hits (lighter green) so the bounds are clear.
                if (_bodyBoxValid)
                    DrawLocalBox(v, _bodyMin, _bodyMax, Color.FromArgb(255, 150, 255, 150));


                // BLUE wheel collider cylinders (actual physical collision).
                string[] bones = { "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr" };
                Color blue = Color.FromArgb(255, 40, 130, 255);
                int n = WheelMemory.GetWheelCount(v);
                for (int i = 0; i < bones.Length && i < n; i++)
                {
                    int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, bones[i]);
                    if (bi < 0) continue;
                    Vector3 c = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi);
                    float r = WheelMemory.GetTyreColliderRadius(v, i);
                    float w = WheelMemory.GetTyreColliderWidth(v, i);
                    if (r < 0.05f || r > 2f) continue;
                    DrawWheelCylinder(v, c, r, w, blue);
                }
            }
            catch { }
        }

        // Wireframe box from a local-space min/max, transformed to world via the entity matrix.
        private void DrawLocalBox(Entity e, Vector3 min, Vector3 max, Color col)
        {
            Vector3 C(float x, float y, float z) =>
                Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, e, x, y, z);
            Vector3[] p =
            {
                C(min.X, min.Y, min.Z), C(max.X, min.Y, min.Z), C(max.X, max.Y, min.Z), C(min.X, max.Y, min.Z),
                C(min.X, min.Y, max.Z), C(max.X, min.Y, max.Z), C(max.X, max.Y, max.Z), C(min.X, max.Y, max.Z)
            };
            int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
            for (int i = 0; i < 12; i++)
                World.DrawLine(p[edges[i, 0]], p[edges[i, 1]], col);
        }

        // Wireframe cylinder for a wheel: two radius circles in the wheel's roll plane (vehicle fwd/up),
        // offset along the axle (vehicle right) by the collider width, plus a few connecting struts.
        private void DrawWheelCylinder(Entity v, Vector3 center, float radius, float width, Color col)
        {
            Vector3 axle = v.RightVector;
            Vector3 fwd = v.ForwardVector;
            Vector3 up = v.UpVector;
            float half = width * 0.5f;
            Vector3 cA = center + axle * half;
            Vector3 cB = center - axle * half;
            const int seg = 18;
            Vector3 prevA = Vector3.Zero, prevB = Vector3.Zero;
            for (int s = 0; s <= seg; s++)
            {
                float a = (float)(s * 2.0 * Math.PI / seg);
                Vector3 dir = fwd * (float)Math.Cos(a) + up * (float)Math.Sin(a);
                Vector3 pA = cA + dir * radius;
                Vector3 pB = cB + dir * radius;
                if (s > 0)
                {
                    World.DrawLine(prevA, pA, col);
                    World.DrawLine(prevB, pB, col);
                    if (s % 3 == 0) World.DrawLine(pA, pB, col);   // sparse cylinder struts
                }
                prevA = pA; prevB = pB;
            }
        }

        // Scan the vehicle's ACTUAL collision surface by raycasting a grid from all six faces of the model
        // box inward; keep the points where the ray hits THIS vehicle. Stored in vehicle-local space so the
        // cloud stays glued to the car. One-shot per vehicle (a brief hitch on first enable is expected).
        private void ScanBodyCollision(Vehicle v)
        {
            _bodyHits.Clear();
            try
            {
                var oMin = new OutputArgument();
                var oMax = new OutputArgument();
                Function.Call(Hash.GET_MODEL_DIMENSIONS, v.Model.Hash, oMin, oMax);
                Vector3 mn = oMin.GetResult<Vector3>();
                Vector3 mx = oMax.GetResult<Vector3>();

                // Build wheel exclusion cylinders (local center + tyre radius + half-width) so we can drop
                // ray hits that landed on a wheel — those are shown separately in blue.
                var wCenter = new System.Collections.Generic.List<Vector3>();
                var wRad = new System.Collections.Generic.List<float>();
                var wHalf = new System.Collections.Generic.List<float>();
                string[] bones = { "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr" };
                int nW = WheelMemory.GetWheelCount(v);
                for (int i = 0; i < bones.Length && i < nW; i++)
                {
                    int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, bones[i]);
                    if (bi < 0) continue;
                    Vector3 c = v.GetPositionOffset(Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi));
                    float r = WheelMemory.GetTyreColliderRadius(v, i);
                    float w = WheelMemory.GetTyreColliderWidth(v, i);
                    wCenter.Add(c);
                    wRad.Add(r > 0.05f && r < 2f ? r : 0.4f);
                    wHalf.Add(w > 0.02f && w < 1f ? w * 0.5f : 0.2f);
                }

                // The body can never sit below the tyre contact line, so anything lower is the ground or a
                // stray low hit — reject it. (No wheels found -> no floor cut.)
                float floorZ = float.MinValue;
                for (int k = 0; k < wCenter.Count; k++)
                {
                    float bottom = wCenter[k].Z - wRad[k];
                    floorZ = (k == 0) ? bottom : Math.Min(floorZ, bottom);
                }

                // In LOCAL space: X = axle (lateral), Y = forward, Z = up. A point is "on a wheel" if it's
                // inside that wheel's cylinder (axially within half-width, radially within tyre radius).
                bool IsWheel(Vector3 p)
                {
                    for (int k = 0; k < wCenter.Count; k++)
                    {
                        Vector3 d = p - wCenter[k];
                        float axial = Math.Abs(d.X);
                        float radial = (float)Math.Sqrt(d.Y * d.Y + d.Z * d.Z);
                        // Generous margins: tyres can poke past the bone-centered collider (stance/camber),
                        // so over-exclude a little rather than leave wheel points in the body cloud.
                        if (axial < wHalf[k] + 0.16f && radial < wRad[k] + 0.14f) return true;
                    }
                    return false;
                }

                void Cast(Vector3 ls, Vector3 le)
                {
                    RaycastResult r = World.Raycast(v.GetOffsetPosition(ls), v.GetOffsetPosition(le), IntersectFlags.Vehicles);
                    if (r.DidHit && r.HitEntity != null && r.HitEntity.Handle == v.Handle)
                    {
                        Vector3 lp = v.GetPositionOffset(r.HitPosition);
                        if (lp.Z < floorZ + 0.02f) return;   // below tyre contact = ground / stray
                        if (!IsWheel(lp)) _bodyHits.Add(lp);
                    }
                }

                const int G = 11;        // grid resolution per face
                const float pad = 0.4f;  // start the ray this far outside the box
                for (int i = 0; i < G; i++)
                {
                    float ti = (float)i / (G - 1);
                    float x = mn.X + (mx.X - mn.X) * ti;
                    float yi = mn.Y + (mx.Y - mn.Y) * ti;
                    for (int j = 0; j < G; j++)
                    {
                        float tj = (float)j / (G - 1);
                        float y = mn.Y + (mx.Y - mn.Y) * tj;
                        float z = mn.Z + (mx.Z - mn.Z) * tj;
                        Cast(new Vector3(x, y, mx.Z + pad), new Vector3(x, y, mn.Z - pad));   // top-down
                        Cast(new Vector3(x, y, mn.Z - pad), new Vector3(x, y, mx.Z + pad));   // bottom-up
                        Cast(new Vector3(mn.X - pad, yi, z), new Vector3(mx.X + pad, yi, z));  // left->right
                        Cast(new Vector3(mx.X + pad, yi, z), new Vector3(mn.X - pad, yi, z));  // right->left
                        Cast(new Vector3(x, mn.Y - pad, z), new Vector3(x, mx.Y + pad, z));    // front->back
                        Cast(new Vector3(x, mx.Y + pad, z), new Vector3(x, mn.Y - pad, z));    // back->front
                    }
                }

                // Dedicated DENSE underside pass (bottom-up) — the floor pan is large and flat, so the main
                // grid under-samples it. Finer grid catches the underside cleanly.
                const int GB = 19;
                for (int i = 0; i < GB; i++)
                {
                    float x = mn.X + (mx.X - mn.X) * i / (GB - 1);
                    for (int j = 0; j < GB; j++)
                    {
                        float y = mn.Y + (mx.Y - mn.Y) * j / (GB - 1);
                        Cast(new Vector3(x, y, mn.Z - pad), new Vector3(x, y, mx.Z + pad));
                    }
                }

                // Extents box (AABB) of the body-only hits, in local space.
                if (_bodyHits.Count > 0)
                {
                    Vector3 lo = _bodyHits[0], hi = _bodyHits[0];
                    foreach (Vector3 p in _bodyHits)
                    {
                        lo = new Vector3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                        hi = new Vector3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                    }
                    _bodyMin = lo; _bodyMax = hi; _bodyBoxValid = true;
                }
                else _bodyBoxValid = false;

                Log($"[Collision] body scan: {_bodyHits.Count} body hits (wheels excluded) for {v.DisplayName}");
            }
            catch { }
        }

        #endregion
    }
}
