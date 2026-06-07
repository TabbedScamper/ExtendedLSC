using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Elements;

namespace ExtendedLSC
{
    /// <summary>
    /// Extended Los Santos Customs - Main entry point
    /// Overlays a custom menu while blocking game input
    /// </summary>
    public class Main : Script
    {
        // Menu system
        private ObjectPool menuPool;
        private NativeMenu mainMenu;

        // Dynamic mod menus (rebuilt per vehicle) - flat structure
        private Dictionary<int, NativeMenu> modMenusByIndex = new Dictionary<int, NativeMenu>();
        private NativeMenu resprayMenu;
        private NativeMenu wheelsMenu;
        private List<NativeMenu> dynamicCategoryMenus = new List<NativeMenu>(); // User-created custom categories

        // Special menus that need refresh capability
        private NativeMenu hornMenu;
        private NativeMenu windowTintMenu;
        private NativeMenu plateMenu;
        private NativeMenu turboMenu;
        private NativeMenu headlightsMenu;

        // Config-based menus: Key = category path, Value = menu
        private Dictionary<string, NativeMenu> configMenusByPath = new Dictionary<string, NativeMenu>();

        // Track item ownership status for icon display (key = NativeItem, value = 1=installed, 2=owned-not-installed)
        private Dictionary<NativeItem, int> itemOwnershipStatus = new Dictionary<NativeItem, int>();
        private const int STATUS_INSTALLED = 1;
        private const int STATUS_OWNED = 2;

        // Track config item metadata for refresh: Key = NativeItem, Value = (categoryPath, itemValue)
        private Dictionary<NativeItem, (string categoryPath, int itemValue, int price)> configItemMetadata = new Dictionary<NativeItem, (string, int, int)>();

        // State
        private bool isMenuActive = false;
        private Vehicle currentVehicle = null;

        // Transaction display (custom drawn to match GTA style)
        private int transactionAmount = 0;
        private int transactionStartTime = 0;
        private const int TRANSACTION_DISPLAY_DURATION = 4000; // 4 seconds

        // Consolidated Debug Menu (F7 to open, select tool, B to exit)
        private enum DebugMode { None, Menu, TextSize, SpriteBrowser, MenuPosition, InputTiming }
        private DebugMode activeDebugMode = DebugMode.None;
        private int debugMenuSelection = 0;
        private readonly string[] debugMenuOptions = { "Text Size", "Sprite Browser", "Menu Position", "Input Timing", "Exit Debug" };

        // Text size debug settings
        private int textSizeDebugFieldIndex = 0;
        private float transactionTextScale = 0.5f;
        private float descriptionTextScale = 0.38f;
        private float spriteScale = 1.8f; // Multiplier for tick/ownership icons
        private List<string> activeDebugFields = new List<string>(); // Built dynamically

        // "Not enough cash" message (replaces description temporarily)
        private bool showNotEnoughCash = false;
        private int notEnoughCashStartTime = 0;
        private const int NOT_ENOUGH_CASH_DURATION = 5000; // 5 seconds

        // Preview system - temporarily apply mods when hovering
        private bool isPreviewingMod = false;
        private int previewModIndex = -1;
        private int previewOriginalValue = -1;

        // Config
        private Keys menuKey = Keys.F5;
        // debugLogging is now in ModSettings

        // LSC detection
        private int lastInterior = 0;
        private static readonly int[] LSC_INTERIORS = { 39938 };
        private bool isInLSC = false;
        private bool waitingForVehicleStop = false;
        private Vector3 lastVehiclePos = Vector3.Zero;

        // Mechanic management
        private const bool ENABLE_MECHANIC_REPLACEMENT = false; // Disabled for UI alignment
        private Ped customMechanic = null;
        private Vector3 customMechanicPos = Vector3.Zero;

        // Walk-around camera
        private bool isWalkAroundActive = false;
        private Vehicle walkAroundVehicle = null;
        private Vector3 cameraOrbitPos = Vector3.Zero;  // Target camera orbit position
        private Vector3 smoothCamPos = Vector3.Zero;    // Smoothed/actual camera position
        private float smoothCamHeight = 1.5f;           // Smoothed camera height
        private Camera walkAroundCam = null;
        private float cameraHeight = 1.5f;
        private float lookOffsetH = 0f;  // Horizontal look offset (left/right from car center)
        private int cameraModeCooldown = 0;  // Prevent immediate re-entry
        private float vehicleCamMinDist = 1f;  // Dynamic based on vehicle size
        private float vehicleCamMaxDist = 4f;
        private float vehicleCamStartDist = 3f;
        private float vehicleHalfLength = 2.5f;  // For box path clamping
        private float vehicleHalfWidth = 1f;
        private const float CAM_SMOOTH_SPEED = 0.1f;  // Smoothing factor (0-1, lower = smoother)

        // Preset camera positions for mod categories (built dynamically per vehicle)
        private int currentPresetIndex = -1;  // -1 = free roam
        private bool isInPresetMode = false;
        private bool isNavigatingMenu = false;  // Flag to prevent CloseMenu during menu navigation
        private List<ModCameraPreset> availablePresets = new List<ModCameraPreset>();

        // Custom menu input handling (native GTA-style acceleration)
        private int inputHeldSince = 0;           // Game time when direction was first held
        private int inputLastRepeat = 0;          // Game time of last repeat action
        private int inputRepeatCount = 0;         // How many times input has repeated
        private GTA.Control inputHeldDirection = GTA.Control.FrontendPause;  // Which direction is being held (FrontendPause = none)

        // Tunable input timing values (use debug mode to adjust)
        private int inputInitialDelay = 82;       // ms before first repeat
        private int inputSlowRepeat = 255;        // ms between repeats for first few
        private int inputFastRepeat = 83;         // ms between repeats after acceleration
        private int inputAccelThreshold = 3;      // repeats before switching to fast

        // Input timing debug settings
        private int inputTimingDebugSetting = 0;  // 0=InitialDelay, 1=SlowRepeat, 2=FastRepeat, 3=AccelThreshold
        private readonly string[] inputTimingSettingNames = { "Initial Delay", "Slow Repeat", "Fast Repeat", "Accel Threshold" };

        // Description editor state (editorModeEnabled is in ModSettings)
        private bool isEditingDescription = false;
        private string editingCategoryName = null;
        private int keyboardCheckCooldown = 0;

        // Sprite browser debug settings
        private int spriteBrowserPage = 0;
        private int spriteBrowserDictIndex = 0;
        private static readonly (string dict, string[] sprites)[] SpriteDictionaries = new[]
        {
            ("CommonMenu", new[] {
                "arrowleft", "arrowright", "shop_arrows_upANDdown", "shop_box_tick", "shop_box_tickb",
                "shop_box_cross", "shop_box_crossb", "shop_box_blank", "shop_box_blankb", "shop_lock",
                "shop_ammo_icon_a", "shop_ammo_icon_b", "shop_armour_icon_a", "shop_armour_icon_b",
                "shop_clothing_icon_a", "shop_clothing_icon_b", "shop_franklin_icon_a", "shop_franklin_icon_b",
                "shop_garage_icon_a", "shop_garage_icon_b", "shop_gunclub_icon_a", "shop_gunclub_icon_b",
                "shop_health_icon_a", "shop_health_icon_b", "shop_makeup_icon_a", "shop_makeup_icon_b",
                "shop_mask_icon_a", "shop_mask_icon_b", "shop_michael_icon_a", "shop_michael_icon_b",
                "shop_new_star", "shop_tattoos_icon_a", "shop_tattoos_icon_b", "shop_trevor_icon_a", "shop_trevor_icon_b",
                "gradient_bgd", "gradient_nav", "header_gradient", "header_gradient_script"
            }),
            ("mpcarhud", new[] {
                "transport_car_icon", "transport_bike_icon", "transport_boat_icon", "transport_heli_icon",
                "transport_plane_icon", "mp_rankbarfill", "albany", "annis", "banshee", "benefactor",
                "bf", "bollokan", "bravado", "brute", "buckingham", "canis", "cheval", "classique",
                "coil", "declasse", "dewbauchee", "dinka", "dundreary", "emperor", "enus", "fathom",
                "gallilvanter", "grotti", "hijak", "hvy", "imponte", "invetero", "jacksheepe", "jobuilt",
                "karin", "lampadati", "lcc", "maibatsu", "mammoth", "maxwell", "mtl", "nagasaki",
                "obey", "ocelot", "overflod", "pedal_and_metal", "pegassi", "pfister", "principe",
                "progen", "schyster", "shitzu", "speedophile", "stanley", "steel_horse", "truffade",
                "ubermacht", "vapid", "vulcar", "weeny", "western", "western_motorcycle_company", "willard", "ztype"
            }),
            ("mpinventory", new[] {
                "mp_specitem_coke", "mp_specitem_heroin", "mp_specitem_meth", "mp_specitem_weed",
                "mp_specitem_cash", "mp_specitem_weapons", "mp_specitem_crates"
            }),
            ("mpmissionend", new[] {
                "tickedtickbox", "emptytickbox"
            }),
            ("shopui_title_carmod", new[] { "shopui_title_carmod" }),
            ("shopui_title_carmod2", new[] { "shopui_title_carmod2" })
        };
        private DateTime lbPressStart = DateTime.MinValue;
        private DateTime xPressStart = DateTime.MinValue;
        private DateTime aPressStart = DateTime.MinValue;
        private DateTime bPressStart = DateTime.MinValue;
        private DateTime yPressStart = DateTime.MinValue;
        private bool wasLbPressed = false;
        private bool wasXPressed = false;
        private bool wasAPressed = false;
        private bool wasBPressed = false;
        private bool wasYPressed = false;
        private int debugTickCounter = 0;
        private DateTime lastDebugStatusLog = DateTime.MinValue;
        private DateTime lastLbHeldTime = DateTime.MinValue; // Track when LB was last held (for combo detection)

        // Struct to hold preset info
        private struct ModCameraPreset
        {
            public string Name;
            public VehicleModType ModType;
            public Vector3 LocalCameraOffset;  // X = left/right, Y = front/back, Z = up/down (relative to vehicle dimensions)

            public ModCameraPreset(string name, VehicleModType modType, Vector3 offset)
            {
                Name = name;
                ModType = modType;
                LocalCameraOffset = offset;
            }
        }

        // All possible mod camera positions (will filter to available ones per vehicle)
        private static readonly ModCameraPreset[] AllModPresets = new ModCameraPreset[]
        {
            new ModCameraPreset("Front Bumper", VehicleModType.FrontBumper, new Vector3(0, 1f, -0.2f)),
            new ModCameraPreset("Rear Bumper", VehicleModType.RearBumper, new Vector3(0, -1f, -0.1f)),
            new ModCameraPreset("Hood", VehicleModType.Hood, new Vector3(0.4f, 0.8f, 0.3f)),
            new ModCameraPreset("Spoiler", VehicleModType.Spoilers, new Vector3(-0.4f, -0.8f, 0.4f)),
            new ModCameraPreset("Roof", VehicleModType.Roof, new Vector3(-0.7f, 0.2f, 0.8f)),
            new ModCameraPreset("Side Skirts", VehicleModType.SideSkirt, new Vector3(-1f, 0, -0.2f)),
            new ModCameraPreset("Grille", VehicleModType.Grille, new Vector3(0, 0.9f, 0)),
            new ModCameraPreset("Exhaust", VehicleModType.Exhaust, new Vector3(0.4f, -0.9f, -0.3f)),
            new ModCameraPreset("Fender", VehicleModType.Fender, new Vector3(-0.9f, 0.3f, 0.1f)),
            new ModCameraPreset("Roll Cage", VehicleModType.Frame, new Vector3(-0.6f, 0, 0.5f)),
            new ModCameraPreset("Engine", VehicleModType.Engine, new Vector3(0.3f, 0.7f, 0.4f)),
        };

        public Main()
        {
            menuPool = new ObjectPool();

            // Initialize settings first (needed for logging)
            ModSettings.Log = Log;
            ModSettings.Load();

            // Initialize folder-based menu config
            MenuConfig.Log = Log;
            MenuConfig.Initialize();

            // Initialize vehicle save data (tracks purchased mods)
            VehicleSaveData.Log = Log;
            VehicleSaveData.Initialize();

            // Build static menus
            BuildMainMenu();

            // Hook events
            Tick += OnTick;
            KeyDown += OnKeyDown;

            Log("ExtendedLSC initialized");
        }

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
            menu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like Menyoo/native GTA
            menu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            menu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            menu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
            menuPool.Add(menu);
            return menu;
        }

        private void BuildMainMenu()
        {
            // Create menu - empty title (custom banner), "CATEGORIES" subtitle
            mainMenu = new NativeMenu("", "CATEGORIES");
            mainMenu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like Menyoo/native GTA
            mainMenu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            mainMenu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            mainMenu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
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

                    customBanner.Position = spritePos;
                    customBanner.Size = spriteSize;
                    customBanner.Draw();
                }
            }

            // Draw scroll indicators (for all menus)
            DrawScrollIndicators(visibleMenu);

            // Draw tick icons for installed items
            DrawTicks(visibleMenu);

            // Draw custom description below scroll indicator (or below items if no scroll indicator)
            DrawCustomDescription(visibleMenu);

            // Position adjustment controls (only when (activeDebugMode == DebugMode.MenuPosition) = true)
            if ((activeDebugMode == DebugMode.MenuPosition))
            {
                AdjustMenuPosition();
            }
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
            float aspectRatio = screenW / screenH;

            // LemonUI uses 1080p base coordinates
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            // Menu banner gives us the top-left position
            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            // Calculate center X position of menu (in normalized 0-1 coords)
            float menuCenterX = (bannerPos.X + bannerSize.Width / 2f) / lemonXBase;

            // Calculate Y position below the visible items (like Menyoo's scroller indicator)
            // Each item is about 38 pixels in 1080p base, plus subtitle area
            float subtitleHeight = 38f;
            float itemHeight = 38f;

            // Calculate bottom of visible items area
            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;
            float indicatorY = menuTopY + visibleItemsHeight + 0.0173f; // Center of indicator bar

            // Calculate menu width in normalized coords
            float menuWidth = bannerSize.Width / lemonXBase;

            // Draw background rect for scroll indicator (like Menyoo's scroller indicator rect)
            Function.Call(Hash.DRAW_RECT, menuCenterX, indicatorY, menuWidth, 0.035f, 0, 0, 0, 200);

            // Get texture resolution for proper scaling (like Menyoo does)
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_arrows_upANDdown");
            float spriteW = textureRes.X / (1920f * 2f);
            float spriteH = textureRes.Y / (1080f * 2f);

            // Draw the up/down arrows sprite twice for bolder appearance
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                menuCenterX,
                indicatorY,
                spriteW,
                spriteH,
                0f,
                255, 255, 255, 255);

            // Second pass for bolder look
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                menuCenterX,
                indicatorY,
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
            float aspectRatio = screenW / screenH;

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            float subtitleHeight = 38f;
            float itemHeight = 38f;

            // Menu right edge X position (normalized) - offset to keep sprite inside menu
            float menuRightX = (bannerPos.X + bannerSize.Width - 28f) / lemonXBase;

            // First visible item Y position
            float firstItemY = (bannerPos.Y + bannerSize.Height + subtitleHeight + itemHeight / 2f) / lemonYBase;

            // Get garage icon sprite size
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_garage_icon_a");
            float spriteW = textureRes.X / (1920f * 2.5f);
            float spriteH = textureRes.Y / (1080f * 2.5f);

            // Calculate which items are visible
            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            int selectedIndex = menu.SelectedIndex;

            // Determine first visible index (LemonUI scrolls to keep selected item visible)
            int firstVisibleIndex = 0;
            if (totalItems > maxVisible)
            {
                // Approximate scroll position based on selected index
                if (selectedIndex >= maxVisible)
                {
                    firstVisibleIndex = Math.Min(selectedIndex - maxVisible + 1, totalItems - maxVisible);
                }
                firstVisibleIndex = Math.Max(0, Math.Min(firstVisibleIndex, totalItems - maxVisible));
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
                    float itemY = firstItemY + (i * itemHeight / lemonYBase);
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

            // Get selected item title to look up description
            var selectedItem = menu.Items[menu.SelectedIndex];
            string title = selectedItem.Title;
            string description = GetDescriptionFromConfig(title);

            // If showing "not enough cash" OR we have a normal description OR debugging description
            bool isDebugEditingDesc = (activeDebugMode == DebugMode.TextSize) && textSizeDebugFieldIndex < activeDebugFields.Count && activeDebugFields[textSizeDebugFieldIndex] == "Description";
            if (!showingNotEnoughCash && string.IsNullOrEmpty(description) && !isDebugEditingDesc) return;

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

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

            // Draw description background
            float descHeight = 0.045f; // Height for description box
            Function.Call(Hash.DRAW_RECT, menuCenterX, descY + descHeight / 2f, menuWidth, descHeight, 0, 0, 0, 200);

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
            Function.Call(Hash.SET_TEXT_WRAP, menuLeftX + 0.005f, menuLeftX + menuWidth - 0.005f);
            Function.Call(Hash.SET_TEXT_LEADING, 0);

            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, displayText);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, menuLeftX + 0.005f, descY + 0.005f);
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
            float aspectRatio = screenW / screenH;

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
        private PointF LemonUIToCustomSprite(PointF lemonPos)
        {
            // Use actual screen width for accurate conversion
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;

            float widthRatio = 1280f / actualWidth;
            float heightRatio = 720f / 1080f;

            return new PointF(lemonPos.X * widthRatio, lemonPos.Y * heightRatio);
        }

        /// <summary>
        /// Converts LemonUI size to CustomSprite's 1280x720 base.
        /// Works on any resolution including ultrawide.
        /// </summary>
        private SizeF LemonUIToCustomSpriteSize(SizeF lemonSize)
        {
            // Use actual screen width for accurate conversion
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;

            float widthRatio = 1280f / actualWidth;
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

        private void RebuildMenusForVehicle()
        {
            if (currentVehicle == null) return;

            // CRITICAL: Install mod kit before any mod operations
            // This is REQUIRED for SET_VEHICLE_MOD to work properly
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);

            // Set current vehicle for MenuConfig (used for vehicle-specific overrides)
            MenuConfig.CurrentVehicle = currentVehicle.DisplayName;

            try
            {
                // Clear main menu
                mainMenu.Clear();

                // Clear dynamic menus
                foreach (var menu in modMenusByIndex.Values)
                {
                    menuPool.Remove(menu);
                }
                modMenusByIndex.Clear();
                itemOwnershipStatus.Clear();

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

                // Build menu with grouped categories like native LSC
                BuildGroupedMainMenu();

                // Add any custom user-created categories from folders
                BuildDynamicMainMenu();

                Log($"Built menu with {mainMenu.Items.Count} categories for {currentVehicle.DisplayName}");
            }
            catch (Exception ex)
            {
                Log($"ERROR in RebuildMenusForVehicle: {ex.Message}\n{ex.StackTrace}");
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
                var item = new NativeItem(title);
                item.AltTitle = ">>";
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
            if (frontBumperCount > 0 || rearBumperCount > 0)
            {
                var bumpersMenu = CreateMenu("Bumpers");

                if (frontBumperCount > 0)
                {
                    var frontMenu = CreateModMenuByIndex(1, frontBumperCount, "Front Bumper");
                    frontMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var frontItem = new NativeItem("Front Bumper");
                    frontItem.AltTitle = ">>";
                    frontItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; frontMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(frontItem);
                }
                if (rearBumperCount > 0)
                {
                    var rearMenu = CreateModMenuByIndex(2, rearBumperCount, "Rear Bumper");
                    rearMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var rearItem = new NativeItem("Rear Bumper");
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

            // Exhaust (4)
            if (GetModCount(4) > 0)
            {
                var menu = CreateModMenuByIndex(4, GetModCount(4), "Exhaust");
                AddSubmenuItem("Exhaust", menu);
            }

            // Fenders (group: Left 8, Right 9)
            int leftFenderCount = GetModCount(8);
            int rightFenderCount = GetModCount(9);
            if (leftFenderCount > 0 || rightFenderCount > 0)
            {
                var fendersMenu = CreateMenu("Fenders");

                if (leftFenderCount > 0)
                {
                    var leftMenu = CreateModMenuByIndex(8, leftFenderCount, "Left Fender");
                    leftMenu.Closed += (s, e) => { if (!isNavigatingMenu) fendersMenu.Visible = true; };
                    var leftItem = new NativeItem("Left Fender");
                    leftItem.AltTitle = ">>";
                    leftItem.Activated += (s, e) => { isNavigatingMenu = true; fendersMenu.Visible = false; leftMenu.Visible = true; isNavigatingMenu = false; };
                    fendersMenu.Add(leftItem);
                }
                if (rightFenderCount > 0)
                {
                    var rightMenu = CreateModMenuByIndex(9, rightFenderCount, "Right Fender");
                    rightMenu.Closed += (s, e) => { if (!isNavigatingMenu) fendersMenu.Visible = true; };
                    var rightItem = new NativeItem("Right Fender");
                    rightItem.AltTitle = ">>";
                    rightItem.Activated += (s, e) => { isNavigatingMenu = true; fendersMenu.Visible = false; rightMenu.Visible = true; isNavigatingMenu = false; };
                    fendersMenu.Add(rightItem);
                }
                AddSubmenuItem("Fenders", fendersMenu);
            }

            // Grille (6)
            if (GetModCount(6) > 0)
            {
                var menu = CreateModMenuByIndex(6, GetModCount(6), "Grille");
                AddSubmenuItem("Grille", menu);
            }

            // Hood (7)
            if (GetModCount(7) > 0)
            {
                var menu = CreateModMenuByIndex(7, GetModCount(7), "Hood");
                AddSubmenuItem("Hood", menu);
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
                stockLightsItem.Activated += (s, e) => SetXenon(false);
                headlightsMenu.Add(stockLightsItem);

                var xenonLightsItem = new NativeItem("Xenon Lights");
                bool xenonOwned = VehicleSaveData.IsXenonOwned(currentVehicle.DisplayName);
                if (hasXenon)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_INSTALLED;
                    if (!xenonOwned) VehicleSaveData.SetXenonOwned(currentVehicle.DisplayName);
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
                    bool owned = VehicleSaveData.IsXenonOwned(currentVehicle.DisplayName);
                    if (!TryPurchase(ModPricing.XenonLightsPrice, owned)) return;

                    SetXenon(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetXenonOwned(currentVehicle.DisplayName);
                        VehicleSaveData.Save();
                    }
                };
                headlightsMenu.Add(xenonLightsItem);

                var headlightsNavItem = new NativeItem("Headlights");
                headlightsNavItem.AltTitle = ">>";
                headlightsNavItem.Activated += (s, e) => { isNavigatingMenu = true; lightsMenu.Visible = false; headlightsMenu.Visible = true; isNavigatingMenu = false; };
                lightsMenu.Add(headlightsNavItem);

                // Neon Kits submenu
                var neonKitsMenu = CreateMenu("Neon Kits");
                neonKitsMenu.Closed += (s, e) => { if (!isNavigatingMenu) lightsMenu.Visible = true; };

                // Neon Layout submenu
                var neonLayoutMenu = CreateMenu("Neon Layout");
                neonLayoutMenu.Closed += (s, e) => { if (!isNavigatingMenu) neonKitsMenu.Visible = true; };

                // Get current neon state using safe method
                bool neonFront = GetNeonEnabled(2);
                bool neonBack = GetNeonEnabled(3);
                bool neonLeft = GetNeonEnabled(0);
                bool neonRight = GetNeonEnabled(1);

                var neonLayouts = new[] {
                    ("None", false, false, false, false),
                    ("Front", true, false, false, false),
                    ("Back", false, true, false, false),
                    ("Sides", false, false, true, true),
                    ("Front and Back", true, true, false, false),
                    ("Front and Sides", true, false, true, true),
                    ("Back and Sides", false, true, true, true),
                    ("Front, Back and Sides", true, true, true, true)
                };

                foreach (var (name, front, back, left, right) in neonLayouts)
                {
                    bool isCurrentLayout = (front == neonFront && back == neonBack && left == neonLeft && right == neonRight);
                    bool isNone = !front && !back && !left && !right;
                    int layoutPrice = isNone ? 0 : 1500;
                    var layoutItem = new NativeItem(name);
                    if (isCurrentLayout)
                    {
                        layoutItem.AltTitle = "";
                        itemOwnershipStatus[layoutItem] = STATUS_INSTALLED;
                    }
                    else
                    {
                        layoutItem.AltTitle = isNone ? "Free" : "$1,500";
                    }
                    bool f = front, b = back, l = left, r = right;
                    bool isCurrent = isCurrentLayout;
                    int price = layoutPrice;
                    layoutItem.Activated += (s, e) =>
                    {
                        if (!isCurrent && !TryPurchase(price, false)) return;
                        ApplyNeonLayout(f, b, l, r);
                    };
                    neonLayoutMenu.Add(layoutItem);
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

                AddSubmenuItem("Lights", lightsMenu);
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

            // Respray
            resprayMenu = BuildResprayMenu();
            AddSubmenuItem("Respray", resprayMenu);

            // Roll Cage (5)
            if (GetModCount(5) > 0)
            {
                var menu = CreateModMenuByIndex(5, GetModCount(5), "Roll Cage");
                AddSubmenuItem("Roll Cage", menu);
            }

            // Roof (10)
            if (GetModCount(10) > 0)
            {
                var menu = CreateModMenuByIndex(10, GetModCount(10), "Roof");
                AddSubmenuItem("Roof", menu);
            }

            // Skirts (3)
            if (GetModCount(3) > 0)
            {
                var menu = CreateModMenuByIndex(3, GetModCount(3), "Skirts");
                AddSubmenuItem("Skirts", menu);
            }

            // Spoiler (0)
            if (GetModCount(0) > 0)
            {
                var menu = CreateModMenuByIndex(0, GetModCount(0), "Spoiler");
                AddSubmenuItem("Spoiler", menu);
            }

            // Suspension (15)
            if (GetModCount(15) > 0)
            {
                var menu = CreateModMenuByIndex(15, GetModCount(15), "Suspension");
                AddSubmenuItem("Suspension", menu);
            }

            // Transmission (13)
            if (GetModCount(13) > 0)
            {
                var menu = CreateModMenuByIndex(13, GetModCount(13), "Transmission");
                AddSubmenuItem("Transmission", menu);
            }

            // Turbo (18) - submenu like native LSC
            {
                turboMenu = CreateMenu("Turbo");

                bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
                bool turboOwned = VehicleSaveData.IsTurboOwned(currentVehicle.DisplayName);

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
                noneItem.Activated += (s, e) => { SetTurbo(false); };
                turboMenu.Add(noneItem);

                var turboTuningItem = new NativeItem("Turbo Tuning");
                if (hasTurbo)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_INSTALLED;
                    if (!turboOwned) VehicleSaveData.SetTurboOwned(currentVehicle.DisplayName);
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
                    bool owned = VehicleSaveData.IsTurboOwned(currentVehicle.DisplayName);
                    if (!TryPurchase(ModPricing.TurboPrice, owned)) return;

                    SetTurbo(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetTurboOwned(currentVehicle.DisplayName);
                        VehicleSaveData.Save();
                    }
                };
                turboMenu.Add(turboTuningItem);

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
        }

        private NativeMenu BuildWheelsMenu()
        {
            var menu = CreateMenu("Wheels");

            // Wheel Type submenu
            var wheelTypeMenu = CreateMenu("Wheel Types");
            wheelTypeMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Wheel types in LSC order: High End, Lowrider, Muscle, Off-Road, Sport, SUV, Tuner
            var wheelTypes = new[] {
                (6, "High End"),
                (2, "Lowrider"),
                (1, "Muscle"),
                (4, "Off-Road"),
                (0, "Sport"),
                (3, "SUV"),
                (5, "Tuner")
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

            // Tire Design - links to wheel type selection (same as above)
            var tireDesignNavItem = new NativeItem("Tire Design");
            tireDesignNavItem.AltTitle = ">>";
            tireDesignNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; wheelTypeMenu.Visible = true; isNavigatingMenu = false; };
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

            return menu;
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

            // Set up back navigation to parent
            menu.Closed += (s, e) =>
            {
                if (!isNavigatingMenu && parentMenu != null)
                    parentMenu.Visible = true;
            };

            // Add items if the category has any
            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);

                if (!string.IsNullOrEmpty(item.Description))
                {
                    menuItem.Description = item.Description;
                }

                // Handle pricing/installed status
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                var capturedItem = item;
                menuItem.Activated += (s, e) =>
                {
                    // Handle different item types
                    if (item.Type == "color" && item.R.HasValue && item.G.HasValue && item.B.HasValue)
                    {
                        // Color item - could be neon, tire smoke, etc.
                        Log($"[Dynamic] Color selected: {item.Name} ({item.R},{item.G},{item.B})");
                    }
                    else if (item.Type == "toggle")
                    {
                        // Toggle item
                        Log($"[Dynamic] Toggle selected: {item.Name} = {item.Value}");
                    }
                    else if (item.Value >= 0)
                    {
                        // Standard mod item with value
                        Log($"[Dynamic] Item selected: {item.Name} = {item.Value}");
                    }
                };

                menu.Add(menuItem);
            }

            // Add subcategory navigation items
            foreach (var subCategory in category.SubCategories)
            {
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

            return menu;
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
                    // This is a user-created custom category
                    Log($"[Dynamic] Adding custom category: {category.Name}");
                    AddDynamicCategory(category);
                }
            }
        }

        #endregion

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
            string vehicleName = currentVehicle?.DisplayName ?? "";

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

                    bool owned = capturedItem.Value >= 0 && VehicleSaveData.IsCustomItemOwned(currentVehicle.DisplayName, capturedCategoryPath, capturedItem.Value);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    onActivate(capturedItem);
                    // Mark as owned when purchased
                    if (capturedItem.Value >= 0 && !owned)
                    {
                        VehicleSaveData.SetCustomItemOwned(currentVehicle.DisplayName, capturedCategoryPath, capturedItem.Value);
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
            string vehicleName = currentVehicle?.DisplayName ?? "";

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
            return CreateMenuFromConfig(
                "Windows",
                () => (int)currentVehicle.Mods.WindowTint,
                (item) => ApplyWindowTint(item.Value)
            );
        }

        /// <summary>
        /// Create Neon Color menu from MenuConfig
        /// </summary>
        private NativeMenu CreateNeonColorMenuFromConfig()
        {
            var category = MenuConfig.LoadCategory("Lights/Neon Kits/Neon Color");
            if (category == null) return null;

            var menu = CreateMenu("Neon Color");

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    ApplyNeonColor(r, g, b);
                };
                menu.Add(menuItem);
            }

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

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    ApplyTireSmoke(r, g, b);
                };
                menu.Add(menuItem);
            }

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
        /// Get current scale value for debug field
        /// </summary>
        private float GetCurrentDebugScale(string fieldName)
        {
            if (fieldName == "Transaction (-$)") return transactionTextScale;
            if (fieldName == "Description") return descriptionTextScale;
            if (fieldName == "Sprites (Icons)") return spriteScale;
            return 0f;
        }

        /// <summary>
        /// Exit current debug mode and log any relevant settings
        /// </summary>
        private void ExitDebugMode()
        {
            // Log settings based on which mode was active
            if (activeDebugMode == DebugMode.TextSize)
            {
                Log($"=== TEXT SIZE SETTINGS ===");
                Log($"Transaction (-$) Scale: {transactionTextScale:F3}");
                Log($"Description Scale: {descriptionTextScale:F3}");
                Log($"Sprite Scale: {spriteScale:F3}");
                Log($"==========================");
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
                Log($"Offset: {mainMenu.Offset}");
                Log($"==============================");
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
            ShowNotification("Debug Mode ~r~OFF");
        }

        #endregion

        #region Dynamic Menu Creators

        private NativeMenu CreateModMenuByIndex(int modIndex, int count, string displayName)
        {
            var menu = CreateMenu(displayName);
            modMenusByIndex[modIndex] = menu;

            int currentMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, modIndex);
            string vehicleName = currentVehicle.DisplayName;
            int idx = modIndex; // Capture for closure

            // Preview system: store original when menu opens
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    previewModIndex = idx;
                    previewOriginalValue = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, idx);
                    isPreviewingMod = true;
                }
            };

            // Preview system: revert to original when menu closes
            menu.Closed += (s, e) =>
            {
                if (isPreviewingMod && currentVehicle != null && currentVehicle.Exists() && previewModIndex == idx)
                {
                    // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, idx, previewOriginalValue, false);
                    isPreviewingMod = false;
                    previewModIndex = -1;
                }
            };

            // Preview system: preview mod on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    // Index 0 = Stock (-1), Index 1+ = mod value (index - 1)
                    int previewValue = e.Index == 0 ? -1 : e.Index - 1;
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, idx, previewValue, false);
                }
            };

            // Stock/None option - different labels for different mod types
            string stockLabel;
            switch (modIndex)
            {
                case 16: stockLabel = "None"; break;           // Armor
                case 12: stockLabel = "Stock Brakes"; break;   // Brakes
                default: stockLabel = "Stock"; break;
            }
            var stockItem = new NativeItem(stockLabel);
            if (currentMod == -1)
            {
                stockItem.AltTitle = "";
                itemOwnershipStatus[stockItem] = STATUS_INSTALLED;
            }
            else
            {
                stockItem.AltTitle = "Free";
            }
            stockItem.Activated += (s, e) =>
            {
                // Purchasing stock - clear preview state first
                isPreviewingMod = false;
                previewModIndex = -1;
                ApplyModByIndex(idx, -1);
            };
            menu.Add(stockItem);

            // Mod options
            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetModPriceByIndex(modIndex, i);
                string name = ModPricing.IsPerformanceModIndex(modIndex)
                    ? ModPricing.GetPerformanceLevelNameByIndex(modIndex, i)
                    : GetModNameByIndex(modIndex, i);

                bool isInstalled = currentMod == i;
                bool isOwned = VehicleSaveData.IsModOwned(vehicleName, modIndex, i);

                var item = new NativeItem(name);

                // Set AltTitle and track ownership status for icon display
                if (isInstalled)
                {
                    item.AltTitle = ""; // Empty - icon will be drawn
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    // Mark as owned since it's installed
                    if (!isOwned) VehicleSaveData.SetModOwned(vehicleName, modIndex, i);
                }
                else if (isOwned)
                {
                    item.AltTitle = ""; // Empty - icon will be drawn
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = $"${price:N0}";
                }

                int modValue = i; // Capture for closure
                int capturedPrice = price;
                item.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsModOwned(currentVehicle.DisplayName, idx, modValue);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    // Purchasing - clear preview state first
                    isPreviewingMod = false;
                    previewModIndex = -1;

                    ApplyModByIndex(idx, modValue);
                    // Mark as owned when purchased
                    if (!owned)
                    {
                        VehicleSaveData.SetModOwned(currentVehicle.DisplayName, idx, modValue);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateModMenu(VehicleModType modType, int count)
        {
            return CreateModMenuByIndex((int)modType, count, ModPricing.GetModTypeName(modType));
        }

        private NativeMenu CreateWheelTypeMenu(int wheelType)
        {
            // Set wheel type temporarily to count wheels
            int origType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);

            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23); // 23 = front wheels

            // Restore original
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, origType);

            if (count <= 0) return null;

            var menu = CreateMenu(ModPricing.GetWheelTypeName(wheelType));

            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetWheelPrice(wheelType, i);
                var item = new NativeItem($"Wheel {i + 1}");
                item.AltTitle = $"${price:N0}";

                int wheelIndex = i;
                int wType = wheelType;
                int wheelPrice = price;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(wheelPrice, false)) return;
                    ApplyWheel(wType, wheelIndex);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateLiveryMenu(int count, bool useNativeLivery)
        {
            var menu = CreateMenu("Livery");

            // Get current livery based on system used
            int currentLivery;
            if (useNativeLivery)
            {
                currentLivery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, currentVehicle);
            }
            else
            {
                currentLivery = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 48);
            }

            // None/Stock option
            var noneItem = new NativeItem("None");
            noneItem.AltTitle = currentLivery == -1 ? "~g~Applied" : "Free";
            bool isNative = useNativeLivery;
            noneItem.Activated += (s, e) => ApplyLivery(-1, isNative);
            menu.Add(noneItem);

            for (int i = 0; i < count; i++)
            {
                int price = 500 + (i * 250);
                bool isApplied = currentLivery == i;

                // Try to get livery name from game
                string liveryName = $"Livery {i + 1}";
                if (!useNativeLivery)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 48, i);
                    if (!string.IsNullOrEmpty(label) && label != "NULL")
                    {
                        string name = Game.GetLocalizedString(label);
                        if (!string.IsNullOrEmpty(name))
                            liveryName = name;
                    }
                }

                var item = new NativeItem(liveryName);
                item.AltTitle = isApplied ? "~g~Applied" : $"${price:N0}";

                int liveryIndex = i;
                int liveryPrice = price;
                bool alreadyApplied = isApplied;
                item.Activated += (s, e) =>
                {
                    if (!alreadyApplied && !TryPurchase(liveryPrice, false)) return;
                    ApplyLivery(liveryIndex, isNative);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateColorMenu(bool isPrimary)
        {
            var menu = CreateMenu(isPrimary ? "Primary" : "Secondary");

            // Create category submenus
            var categories = new (string name, VehicleColors.ColorInfo[] colors, int paintType, int price)[]
            {
                ("Classic", VehicleColors.ClassicColors, 0, ModPricing.ClassicPrice),
                ("Metallic", VehicleColors.MetallicColors, 1, ModPricing.MetallicPrice),
                ("Matte", VehicleColors.MatteColors, 3, ModPricing.MattePrice),
                ("Metal", VehicleColors.MetalFinishes, 4, ModPricing.MetalPrice),
                ("Chrome", VehicleColors.ChromeColors, 5, ModPricing.ChromePrice),
            };

            foreach (var (categoryName, colors, paintType, basePrice) in categories)
            {
                var categoryMenu = CreateMenu(categoryName);
                categoryMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

                foreach (var color in colors)
                {
                    var item = new NativeItem(color.DisplayName);
                    item.AltTitle = $"${basePrice}";
                    var capturedColor = color;
                    int capturedPaintType = paintType;
                    int capturedPrice = basePrice;
                    item.Activated += (s, e) =>
                    {
                        if (!TryPurchase(capturedPrice, false)) return;
                        ApplyPaintColor(capturedColor.ColorIndex, capturedColor.PearlescentSpec, capturedPaintType, isPrimary);
                    };
                    categoryMenu.Add(item);
                }

                var navItem = new NativeItem(categoryName);
                navItem.AltTitle = ">>";
                navItem.Description = $"{colors.Length} colors";
                var capturedMenu = categoryMenu;
                navItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; capturedMenu.Visible = true; isNavigatingMenu = false; };
                menu.Add(navItem);
            }

            return menu;
        }

        private NativeMenu CreatePearlescentMenu()
        {
            var menu = CreateMenu("Pearlescent");

            foreach (var color in VehicleColors.PearlescentColors)
            {
                var item = new NativeItem(color.DisplayName);
                item.AltTitle = $"${ModPricing.PearlescentPrice}";
                int colorId = color.ColorIndex;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(ModPricing.PearlescentPrice, false)) return;
                    ApplyPearlescent(colorId);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateWheelColorMenu()
        {
            var menu = CreateMenu("Wheel Color");

            foreach (var color in VehicleColors.WheelColors)
            {
                var item = new NativeItem(color.DisplayName);
                item.AltTitle = $"${ModPricing.WheelColorPrice}";
                int colorId = color.ColorIndex;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(ModPricing.WheelColorPrice, false)) return;
                    ApplyWheelColor(colorId);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateTireSmokeMenu()
        {
            var menu = CreateMenu("Tire Smoke");

            var smokeColors = new[] {
                ("White", 255, 255, 255),
                ("Black", 20, 20, 20),
                ("Red", 255, 0, 0),
                ("Green", 0, 255, 0),
                ("Blue", 0, 0, 255),
                ("Yellow", 255, 255, 0),
                ("Orange", 255, 128, 0),
                ("Pink", 255, 0, 255),
                ("Purple", 128, 0, 255)
            };

            foreach (var (name, r, g, b) in smokeColors)
            {
                var item = new NativeItem(name);
                item.AltTitle = "$500";
                int cr = r, cg = g, cb = b;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(500, false)) return;
                    ApplyTireSmoke(cr, cg, cb);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateHeadlightColorMenu()
        {
            var menu = CreateMenu("Headlight Color");

            string[] colors = { "Default", "White", "Blue", "Electric Blue", "Mint Green", "Lime Green",
                               "Yellow", "Golden Shower", "Orange", "Red", "Pony Pink", "Hot Pink", "Purple" };

            for (int i = 0; i < colors.Length; i++)
            {
                var item = new NativeItem(colors[i]);
                int price = i == 0 ? 0 : 250;
                item.AltTitle = price == 0 ? "Free" : "$250";
                int colorIndex = i - 1; // -1 for default
                int capturedPrice = price;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(capturedPrice, false)) return;
                    ApplyHeadlightColor(colorIndex);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateNeonMenu()
        {
            var menu = CreateMenu("Neon");

            // Toggle positions
            string[] positions = { "Front", "Back", "Left", "Right" };
            for (int i = 0; i < 4; i++)
            {
                bool isOn = Function.Call<bool>((Hash)0x8C4B92553E4571EC, currentVehicle, i);
                var item = new NativeItem(positions[i]);
                item.AltTitle = isOn ? "~g~On" : "Off - $500";
                int pos = i;
                bool wasOn = isOn;
                item.Activated += (s, e) =>
                {
                    if (!wasOn && !TryPurchase(500, false)) return;
                    ToggleNeon(pos);
                };
                menu.Add(item);
            }

            menu.Add(new NativeSeparatorItem());

            // Neon colors - load from MenuConfig
            var neonCategory = MenuConfig.LoadCategory("Lights/Neon Kits/Neon Color");
            if (neonCategory != null)
            {
                foreach (var colorItem in neonCategory.Items)
                {
                    var item = new NativeItem(colorItem.Name);
                    item.AltTitle = colorItem.Price == 0 ? "Free" : $"${colorItem.Price}";
                    int r = colorItem.R ?? 255;
                    int g = colorItem.G ?? 255;
                    int b = colorItem.B ?? 255;
                    int price = colorItem.Price;
                    item.Activated += (s, e) =>
                    {
                        if (!TryPurchase(price, false)) return;
                        ApplyNeonColor(r, g, b);
                    };
                    menu.Add(item);
                }
            }

            return menu;
        }

        private NativeMenu CreateHornMenu()
        {
            var menu = CreateMenu("Horn");

            int currentHorn = currentVehicle.Mods[VehicleModType.Horns].Index;
            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, (int)VehicleModType.Horns);

            string[] hornNames = {
                "Stock Horn", "Star Spangled Banner", "Classical Horn 1", "Classical Horn 2",
                "Classical Horn 3", "Classical Horn 4", "Classical Horn 5", "Classical Horn 6",
                "Classical Horn 7", "Scale: Do", "Scale: Re", "Scale: Mi", "Scale: Fa",
                "Scale: Sol", "Scale: La", "Scale: Ti", "Scale: Do (High)",
                "Jazz Horn 1", "Jazz Horn 2", "Jazz Horn 3", "Jazz Loop",
                "Classical Loop", "San Andreas Loop", "Liberty City Loop"
            };

            string vehicleName = currentVehicle.DisplayName;
            for (int i = -1; i < count && i < hornNames.Length - 1; i++)
            {
                string name = i == -1 ? hornNames[0] : hornNames[Math.Min(i + 1, hornNames.Length - 1)];
                bool isInstalled = currentHorn == i;
                bool isOwned = i >= 0 && VehicleSaveData.IsHornOwned(vehicleName, i);
                var item = new NativeItem(name);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (i >= 0 && !isOwned) VehicleSaveData.SetHornOwned(vehicleName, i);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = i == -1 ? "Free" : "$100";
                }

                int hornIndex = i;
                int hornPrice = i == -1 ? 0 : 100; // Stock is free, others $100
                item.Activated += (s, e) =>
                {
                    bool owned = hornIndex >= 0 && VehicleSaveData.IsHornOwned(currentVehicle.DisplayName, hornIndex);
                    if (!TryPurchase(hornPrice, owned)) return;

                    ApplyHorn(hornIndex);
                    if (hornIndex >= 0 && !owned)
                    {
                        VehicleSaveData.SetHornOwned(currentVehicle.DisplayName, hornIndex);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateWindowTintMenu()
        {
            var menu = CreateMenu("Window Tint");

            // Order matches real LSC: None, Light Smoke, Dark Smoke, Limo
            // Values map to VehicleWindowTint enum
            var tintOptions = new (string Name, int Value)[]
            {
                ("None", 0),        // VehicleWindowTint.None
                ("Light Smoke", 3), // VehicleWindowTint.LightSmoke
                ("Dark Smoke", 2),  // VehicleWindowTint.DarkSmoke
                ("Limo", 5)         // VehicleWindowTint.Limo
            };

            int currentTint = (int)currentVehicle.Mods.WindowTint;

            string vehicleName = currentVehicle.DisplayName;
            for (int i = 0; i < tintOptions.Length; i++)
            {
                var (name, tintValue) = tintOptions[i];
                int price = i == 0 ? 0 : 500; // None is free, others cost $500
                bool isInstalled = currentTint == tintValue;
                bool isOwned = tintValue > 0 && VehicleSaveData.IsTintOwned(vehicleName, tintValue);
                var item = new NativeItem(name);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (tintValue > 0 && !isOwned) VehicleSaveData.SetTintOwned(vehicleName, tintValue);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = price == 0 ? "Free" : $"${price}";
                }

                int idx = tintValue;
                item.Activated += (s, e) =>
                {
                    ApplyWindowTint(idx);
                    if (idx > 0)
                    {
                        VehicleSaveData.SetTintOwned(currentVehicle.DisplayName, idx);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreatePlateMenu()
        {
            var menu = CreateMenu("License Plate");

            string[] plates = { "Blue on White 1", "Yellow on Black", "Yellow on Blue", "Blue on White 2", "Blue on White 3", "Yankton" };
            int currentPlate = (int)currentVehicle.Mods.LicensePlateStyle;

            string vehicleName = currentVehicle.DisplayName;
            for (int i = 0; i < plates.Length; i++)
            {
                bool isInstalled = currentPlate == i;
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, i);
                var item = new NativeItem(plates[i]);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (!isOwned) VehicleSaveData.SetPlateOwned(vehicleName, i);
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

                int plateIndex = i;
                int platePrice = 200;
                item.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsPlateOwned(currentVehicle.DisplayName, plateIndex);
                    if (!TryPurchase(platePrice, owned)) return;

                    ApplyPlateStyle(plateIndex);
                    if (!owned)
                    {
                        VehicleSaveData.SetPlateOwned(currentVehicle.DisplayName, plateIndex);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private string GetModName(VehicleModType modType, int index)
        {
            return GetModNameByIndex((int)modType, index);
        }

        private string GetModNameByIndex(int modIndex, int valueIndex)
        {
            // Try to get the localized name from game
            string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, modIndex, valueIndex);
            if (!string.IsNullOrEmpty(label) && label != "NULL")
            {
                string name = Game.GetLocalizedString(label);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            return $"{ModCategories.GetDisplayName(modIndex)} {valueIndex + 1}";
        }

        #endregion

        #region Mod Application

        private void ApplyModByIndex(int modIndex, int valueIndex)
        {
            if (currentVehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, modIndex, valueIndex, false);
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

            string vehicleName = currentVehicle?.DisplayName ?? "";

            // Iterate through menu items and update their status
            // Item 0 is always "Stock" with value -1, rest are mods with values 0, 1, 2, etc.
            for (int i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i] as NativeItem;
                if (item == null) continue;

                int itemValue = i - 1; // Stock is at index 0 with value -1

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
            string vehicleName = currentVehicle?.DisplayName ?? "";

            for (int i = 0; i < hornMenu.Items.Count; i++)
            {
                var item = hornMenu.Items[i] as NativeItem;
                if (item == null) continue;

                int hornIndex = i - 1; // Stock is at index 0 with value -1
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
            string vehicleName = currentVehicle?.DisplayName ?? "";

            for (int i = 0; i < plateMenu.Items.Count; i++)
            {
                var item = plateMenu.Items[i] as NativeItem;
                if (item == null) continue;

                bool isNowInstalled = (i == newInstalledIndex);
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, i);

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
            string vehicleName = currentVehicle?.DisplayName ?? "";
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
            string vehicleName = currentVehicle?.DisplayName ?? "";
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

        private void ApplyWheel(int wheelType, int wheelIndex)
        {
            if (currentVehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, false); // Front wheels
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, false); // Back wheels

            ShowNotification("~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex}");
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

            ShowNotification($"~g~{(isPrimary ? "Primary" : "Secondary")} color applied!");
            MechanicSpeak();
        }

        /// <summary>
        /// Apply paint color with proper paint type (Classic=0, Metallic=1, Pearl=2, Matte=3, Metal=4, Chrome=5)
        /// </summary>
        private void ApplyPaintColor(int colorIndex, int pearlescentSpec, int paintType, bool isPrimary)
        {
            if (currentVehicle == null) return;

            // Paint types: 0=Normal, 1=Metallic, 2=Pearl, 3=Matte, 4=Metal, 5=Chrome
            if (isPrimary)
            {
                // SET_VEHICLE_MOD_COLOR_1: vehicle, paintType, color, pearlescent
                Function.Call(Hash.SET_VEHICLE_MOD_COLOR_1, currentVehicle, paintType, colorIndex, pearlescentSpec);
            }
            else
            {
                // SET_VEHICLE_MOD_COLOR_2: vehicle, paintType, color
                Function.Call(Hash.SET_VEHICLE_MOD_COLOR_2, currentVehicle, paintType, colorIndex);
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
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 20, true); // Enable tire smoke
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

            currentVehicle.Repair();
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

        #region Menu Input Handling

        /// <summary>
        /// Custom menu input handling with native GTA-style acceleration.
        /// Initial delay before repeat, slow repeat for first few, then fast repeat.
        /// </summary>
        private void HandleMenuInput()
        {
            // Handle debug mode for timing adjustment (R3 to toggle, L3 to cycle settings)
            HandleInputTimingDebug();

            // Only handle input when a menu is visible
            NativeMenu visibleMenu = GetVisibleMenu();
            if (visibleMenu == null)
            {
                ResetInputState();
                return;
            }

            // In debug mode, left/right adjust timing instead of menu navigation
            if ((activeDebugMode == DebugMode.InputTiming))
            {
                return; // Debug mode handles its own input
            }

            int now = Game.GameTime;
            bool upHeld = Game.IsControlPressed(GTA.Control.FrontendUp) || Game.IsControlPressed(GTA.Control.PhoneUp);
            bool downHeld = Game.IsControlPressed(GTA.Control.FrontendDown) || Game.IsControlPressed(GTA.Control.PhoneDown);
            bool leftHeld = Game.IsControlPressed(GTA.Control.FrontendLeft) || Game.IsControlPressed(GTA.Control.PhoneLeft);
            bool rightHeld = Game.IsControlPressed(GTA.Control.FrontendRight) || Game.IsControlPressed(GTA.Control.PhoneRight);

            // Determine current held direction (using FrontendPause as "none" since there's no Control.None)
            GTA.Control currentDirection = GTA.Control.FrontendPause;
            if (upHeld) currentDirection = GTA.Control.FrontendUp;
            else if (downHeld) currentDirection = GTA.Control.FrontendDown;
            else if (leftHeld) currentDirection = GTA.Control.FrontendLeft;
            else if (rightHeld) currentDirection = GTA.Control.FrontendRight;

            // If direction changed or released, reset state
            if (currentDirection != inputHeldDirection)
            {
                inputHeldDirection = currentDirection;
                inputHeldSince = now;
                inputLastRepeat = 0;
                inputRepeatCount = 0;
                return; // First press is handled by LemonUI
            }

            // If no direction held, nothing to do
            if (currentDirection == GTA.Control.FrontendPause) return;

            // Check if we should trigger a repeat
            int holdDuration = now - inputHeldSince;

            // Haven't passed initial delay yet
            if (holdDuration < inputInitialDelay) return;

            // Determine repeat interval based on how many times we've repeated
            int repeatInterval = inputRepeatCount < inputAccelThreshold ? inputSlowRepeat : inputFastRepeat;

            // Check if enough time has passed since last repeat
            int timeSinceLastRepeat = now - inputLastRepeat;
            if (inputLastRepeat == 0)
            {
                // First repeat after initial delay
                timeSinceLastRepeat = holdDuration - inputInitialDelay;
            }

            if (timeSinceLastRepeat >= repeatInterval)
            {
                // Trigger repeat action by changing SelectedIndex
                int itemCount = visibleMenu.Items.Count;
                if (itemCount == 0) return;

                switch (currentDirection)
                {
                    case GTA.Control.FrontendUp:
                        visibleMenu.SelectedIndex = (visibleMenu.SelectedIndex - 1 + itemCount) % itemCount;
                        break;
                    case GTA.Control.FrontendDown:
                        visibleMenu.SelectedIndex = (visibleMenu.SelectedIndex + 1) % itemCount;
                        break;
                    case GTA.Control.FrontendLeft:
                    case GTA.Control.FrontendRight:
                        // Left/right handled by individual items (sliders, etc.)
                        // We don't need to handle these for basic navigation
                        break;
                }

                inputLastRepeat = now;
                inputRepeatCount++;
            }
        }

        /// <summary>
        /// Debug mode for adjusting input timing values in real-time.
        /// DISABLED - timing values have been tuned and saved.
        /// </summary>
        private void HandleInputTimingDebug()
        {
            // Input timing debug is now handled via the unified Debug Menu (F7)
        }

        private void ShowCurrentTimingSetting()
        {
            int value = GetCurrentTimingValue();
            string unit = inputTimingDebugSetting == 3 ? "\u200B" : "ms";
            ShowNotification($"~y~{inputTimingSettingNames[inputTimingDebugSetting]}: {value}{unit}");
        }

        private int GetCurrentTimingValue()
        {
            switch (inputTimingDebugSetting)
            {
                case 0: return inputInitialDelay;
                case 1: return inputSlowRepeat;
                case 2: return inputFastRepeat;
                case 3: return inputAccelThreshold;
                default: return 0;
            }
        }

        private void ShowTimingDebugOverlay()
        {
            // Draw debug info on screen
            string[] labels = { "Initial", "Slow", "Fast", "Accel" };
            int[] values = { inputInitialDelay, inputSlowRepeat, inputFastRepeat, inputAccelThreshold };

            string display = "~y~TIMING DEBUG~w~\n";
            for (int i = 0; i < 4; i++)
            {
                string marker = i == inputTimingDebugSetting ? "~g~> " : "  ";
                string unit = i == 3 ? "\u200B" : "ms";
                display += $"{marker}{labels[i]}: {values[i]}{unit}~w~\n";
            }
            display += "\n~c~L3=Cycle | Left/Right=Adjust | R3=Save~w~";

            GTA.UI.Screen.ShowSubtitle(display, 100);
        }

        private NativeMenu GetVisibleMenu()
        {
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    return menu;
                }
            }
            return null;
        }

        private void ResetInputState()
        {
            inputHeldDirection = GTA.Control.FrontendPause;
            inputHeldSince = 0;
            inputLastRepeat = 0;
            inputRepeatCount = 0;
        }

        #endregion

        #region Core Loop

        private void OnTick(object sender, EventArgs e)
        {
            // Handle description editor first (on-screen keyboard)
            UpdateDescriptionEditor();

            // Check for X to edit/cancel description (editor mode)
            CheckDescriptionEditInput();

            // Skip input processing while editing description (keyboard is open)
            // But still draw the menu and custom UI
            if (isEditingDescription)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner(); // Keep custom UI visible
                return;
            }

            // Debug Menu - tool selector (F7 to open)
            if (activeDebugMode == DebugMode.Menu)
            {
                // Disable menu input
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // Block vehicle controls
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
                Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);

                // B exits debug menu
                if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    ExitDebugMode();
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // D-pad navigation
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    debugMenuSelection = (debugMenuSelection - 1 + debugMenuOptions.Length) % debugMenuOptions.Length;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    debugMenuSelection = (debugMenuSelection + 1) % debugMenuOptions.Length;

                // A button selects
                if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
                {
                    switch (debugMenuSelection)
                    {
                        case 0: activeDebugMode = DebugMode.TextSize; break;
                        case 1: activeDebugMode = DebugMode.SpriteBrowser; spriteBrowserPage = 0; break;
                        case 2: activeDebugMode = DebugMode.MenuPosition; break;
                        case 3: activeDebugMode = DebugMode.InputTiming; break;
                        case 4: ExitDebugMode(); return;
                    }
                    ShowNotification($"~g~{debugMenuOptions[debugMenuSelection]}~w~ mode active");
                }

                // Draw menu
                menuPool.Process();
                DrawCustomBanner();

                // Draw debug menu overlay
                string menuText = "~y~DEBUG MENU~w~\n";
                for (int i = 0; i < debugMenuOptions.Length; i++)
                {
                    menuText += (i == debugMenuSelection ? "~g~> " : "  ") + debugMenuOptions[i] + (i == debugMenuSelection ? "~w~" : "") + "\n";
                }
                GTA.UI.Screen.ShowSubtitle(menuText.TrimEnd('\n'), 1);
                return;
            }

            // Text size debug mode
            if (activeDebugMode == DebugMode.TextSize)
            {
                // Disable input on all menus in the pool
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // Build list of available fields based on what's visible
                activeDebugFields.Clear();
                var visibleMenu = GetVisibleMenu();
                activeDebugFields.Add("Transaction (-$)");

                if (visibleMenu != null && visibleMenu.SelectedIndex >= 0 && visibleMenu.SelectedIndex < visibleMenu.Items.Count)
                {
                    var selectedItem = visibleMenu.Items[visibleMenu.SelectedIndex];
                    string desc = GetDescriptionFromConfig(selectedItem.Title);
                    if (!string.IsNullOrEmpty(desc))
                        activeDebugFields.Add("Description");
                }

                if (visibleMenu != null && itemOwnershipStatus.Count > 0)
                {
                    foreach (var item in visibleMenu.Items)
                    {
                        if (item is NativeItem ni && itemOwnershipStatus.ContainsKey(ni))
                        {
                            activeDebugFields.Add("Sprites (Icons)");
                            break;
                        }
                    }
                }

                if (textSizeDebugFieldIndex >= activeDebugFields.Count)
                    textSizeDebugFieldIndex = 0;

                string currentFieldName = activeDebugFields.Count > 0 ? activeDebugFields[textSizeDebugFieldIndex] : "None";

                // B button goes back to debug menu
                if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // Block all vehicle movement while in debug mode
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
                Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveUpDown);
                Game.DisableControlThisFrame(GTA.Control.VehicleHandbrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleExit);

                // Read D-pad input
                bool dpadLeft = Game.IsControlJustPressed(GTA.Control.FrontendLeft);
                bool dpadRight = Game.IsControlJustPressed(GTA.Control.FrontendRight);
                bool dpadUp = Game.IsControlJustPressed(GTA.Control.FrontendUp);
                bool dpadDown = Game.IsControlJustPressed(GTA.Control.FrontendDown);

                // Handle D-pad input for field cycling and scale adjustment
                float scaleStep = 0.02f;
                if (dpadLeft && activeDebugFields.Count > 0)
                {
                    textSizeDebugFieldIndex = (textSizeDebugFieldIndex - 1 + activeDebugFields.Count) % activeDebugFields.Count;
                    ShowNotification($"Editing: ~y~{activeDebugFields[textSizeDebugFieldIndex]}");
                }
                if (dpadRight && activeDebugFields.Count > 0)
                {
                    textSizeDebugFieldIndex = (textSizeDebugFieldIndex + 1) % activeDebugFields.Count;
                    ShowNotification($"Editing: ~y~{activeDebugFields[textSizeDebugFieldIndex]}");
                }
                if (dpadUp && activeDebugFields.Count > 0)
                {
                    if (currentFieldName == "Transaction (-$)") transactionTextScale += scaleStep;
                    else if (currentFieldName == "Description") descriptionTextScale += scaleStep;
                    else if (currentFieldName == "Sprites (Icons)") spriteScale += 0.1f;
                    float cs = GetCurrentDebugScale(currentFieldName);
                    Log($"{currentFieldName}: {cs:F3}");
                }
                if (dpadDown && activeDebugFields.Count > 0)
                {
                    if (currentFieldName == "Transaction (-$)") transactionTextScale = Math.Max(0.1f, transactionTextScale - scaleStep);
                    else if (currentFieldName == "Description") descriptionTextScale = Math.Max(0.1f, descriptionTextScale - scaleStep);
                    else if (currentFieldName == "Sprites (Icons)") spriteScale = Math.Max(0.5f, spriteScale - 0.1f);
                    float cs = GetCurrentDebugScale(currentFieldName);
                    Log($"{currentFieldName}: {cs:F3}");
                }

                // Draw menu (AcceptsInput=false so no navigation)
                menuPool.Process();
                DrawCustomBanner();

                // Show cash HUD
                Function.Call((Hash)0x96DEC8D5430208B7, true); // DISPLAY_CASH

                // Draw transaction text - yellow when editing that field
                bool editingTransaction = currentFieldName == "Transaction (-$)";
                {
                    string text = "-$1,234";
                    Function.Call(Hash.SET_TEXT_FONT, 7); // Pricedown
                    Function.Call(Hash.SET_TEXT_SCALE, 0.0f, transactionTextScale);
                    Function.Call(Hash.SET_TEXT_COLOUR, editingTransaction ? 255 : 224, editingTransaction ? 255 : 64, editingTransaction ? 0 : 64, 255);
                    Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                    Function.Call(Hash.SET_TEXT_WRAP, 0.0f, 0.985f);
                    Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                    Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                    Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.985f, 0.06f);
                }

                // Draw ticks (sprites) - they'll be tinted based on current edit mode
                if (visibleMenu != null)
                {
                    DrawTicks(visibleMenu);
                    DrawCustomDescription(visibleMenu);
                }

                // Draw debug overlay showing current field and scale
                float currentScale = GetCurrentDebugScale(currentFieldName);
                string debugText = $"~y~Editing: {currentFieldName}~w~  Scale: {currentScale:F2}  (D-Pad: L/R=field, U/D=size, B=exit)";
                GTA.UI.Screen.ShowSubtitle(debugText, 1);

                // Skip normal input handling
                return;
            }

            // Sprite Browser debug mode
            if (activeDebugMode == DebugMode.SpriteBrowser)
            {
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu
                if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // D-pad navigation for pages
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                {
                    spriteBrowserPage--;
                    var sprites = SpriteDictionaries[spriteBrowserDictIndex].sprites;
                    if (spriteBrowserPage < 0)
                        spriteBrowserPage = (sprites.Length - 1) / 10;
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                {
                    spriteBrowserPage++;
                    var sprites = SpriteDictionaries[spriteBrowserDictIndex].sprites;
                    int maxPage = (sprites.Length - 1) / 10;
                    if (spriteBrowserPage > maxPage) spriteBrowserPage = 0;
                }
                // L/R for dictionaries
                if (Game.IsControlJustPressed(GTA.Control.FrontendLeft))
                {
                    spriteBrowserDictIndex = (spriteBrowserDictIndex - 1 + SpriteDictionaries.Length) % SpriteDictionaries.Length;
                    spriteBrowserPage = 0;
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendRight))
                {
                    spriteBrowserDictIndex = (spriteBrowserDictIndex + 1) % SpriteDictionaries.Length;
                    spriteBrowserPage = 0;
                }

                menuPool.Process();
                DrawCustomBanner();
                DrawSpriteBrowser();

                GTA.UI.Screen.ShowSubtitle("~y~Sprite Browser~w~ - D-Pad: U/D=pages, L/R=dicts, B=back", 1);
                return;
            }

            // Menu Position debug mode
            if (activeDebugMode == DebugMode.MenuPosition)
            {
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu
                if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // D-pad to move menu
                var offset = mainMenu.Offset;
                float step = 5f;
                if (Game.IsControlPressed(GTA.Control.FrontendUp)) offset.Y -= step;
                if (Game.IsControlPressed(GTA.Control.FrontendDown)) offset.Y += step;
                if (Game.IsControlPressed(GTA.Control.FrontendLeft)) offset.X -= step;
                if (Game.IsControlPressed(GTA.Control.FrontendRight)) offset.X += step;
                mainMenu.Offset = offset;

                menuPool.Process();
                DrawCustomBanner();

                GTA.UI.Screen.ShowSubtitle($"~y~Menu Position~w~ - D-Pad to move | Offset: ({offset.X:F0}, {offset.Y:F0}) | B=back", 1);
                return;
            }

            // Input Timing debug mode
            if (activeDebugMode == DebugMode.InputTiming)
            {
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu
                if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // U/D to cycle settings
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    inputTimingDebugSetting = (inputTimingDebugSetting - 1 + 4) % 4;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    inputTimingDebugSetting = (inputTimingDebugSetting + 1) % 4;

                // L/R to adjust values
                int step = 10;
                if (Game.IsControlJustPressed(GTA.Control.FrontendLeft))
                {
                    switch (inputTimingDebugSetting)
                    {
                        case 0: inputInitialDelay = Math.Max(10, inputInitialDelay - step); break;
                        case 1: inputSlowRepeat = Math.Max(10, inputSlowRepeat - step); break;
                        case 2: inputFastRepeat = Math.Max(10, inputFastRepeat - step); break;
                        case 3: inputAccelThreshold = Math.Max(1, inputAccelThreshold - 1); break;
                    }
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendRight))
                {
                    switch (inputTimingDebugSetting)
                    {
                        case 0: inputInitialDelay += step; break;
                        case 1: inputSlowRepeat += step; break;
                        case 2: inputFastRepeat += step; break;
                        case 3: inputAccelThreshold++; break;
                    }
                }

                menuPool.Process();
                DrawCustomBanner();

                int[] values = { inputInitialDelay, inputSlowRepeat, inputFastRepeat, inputAccelThreshold };
                string debugText = $"~y~Input Timing~w~ - U/D=setting, L/R=adjust\n";
                for (int i = 0; i < 4; i++)
                {
                    debugText += (i == inputTimingDebugSetting ? "~g~> " : "  ") + inputTimingSettingNames[i] + ": " + values[i] + (i == inputTimingDebugSetting ? "~w~" : "") + "\n";
                }
                GTA.UI.Screen.ShowSubtitle(debugText.TrimEnd('\n'), 1);
                return;
            }

            // Debug: Log controller input (only when not editing)
            DebugLogControllerInput();

            // Handle custom menu input with native GTA-style acceleration
            HandleMenuInput();

            menuPool.Process();

            // Draw custom banner overlay
            DrawCustomBanner();

            // Draw sprite browser if enabled (F6 to toggle)
            DrawSpriteBrowser();

            // Keep handbrake on while menu is active (allows rev with just RT)
            if (isMenuActive && currentVehicle != null && currentVehicle.Exists() && !isWalkAroundActive)
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, true);

                // Show RPM when revving (outside camera mode)
                if (ModSettings.ShowRPMWhileRevving)
                {
                    float rpm = currentVehicle.CurrentRPM;
                    if (rpm > 0.3f)
                    {
                        GTA.UI.Screen.ShowSubtitle($"RPM: {rpm:F2}", 1);
                    }
                }
            }

            // Check mechanic cleanup
            CheckMechanicCleanup();

            // Make mechanic face player
            UpdateMechanicBehavior();

            // Walk-around camera mode
            if (isWalkAroundActive)
            {
                UpdateWalkAround();
            }

            // Cooldown timer for camera mode
            if (cameraModeCooldown > 0)
                cameraModeCooldown--;

            // Controller input for walk-around (Y button) - only when menu is open
            if (isMenuActive && !isWalkAroundActive && cameraModeCooldown == 0)
            {
                if (Game.IsControlJustPressed(GTA.Control.VehicleExit)) // Y button
                {
                    EnterWalkAround();
                }
            }

            // Controller input to open menu (RB + B)
            if (!isMenuActive && !isWalkAroundActive)
            {
                bool rbHeld = Game.IsControlPressed(GTA.Control.Cover);
                bool bPressed = Game.IsControlJustPressed(GTA.Control.FrontendCancel);

                if (rbHeld && bPressed)
                {
                    TryOpenMenu();
                }
            }

            // Block input while menu active (but allow movement in walk-around)
            if (isMenuActive)
            {
                // Show cash HUD every frame
                Function.Call((Hash)0x96DEC8D5430208B7, true); // DISPLAY_CASH

                // Draw transaction amount (-$XXX) if active
                if (transactionAmount > 0 && transactionStartTime > 0)
                {
                    int elapsed = Game.GameTime - transactionStartTime;
                    if (elapsed < TRANSACTION_DISPLAY_DURATION)
                    {
                        string text = $"-${transactionAmount:N0}";

                        Function.Call(Hash.SET_TEXT_FONT, 7);
                        Function.Call(Hash.SET_TEXT_SCALE, 0.0f, transactionTextScale);
                        Function.Call(Hash.SET_TEXT_COLOUR, 224, 64, 64, 255);
                        Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                        Function.Call(Hash.SET_TEXT_WRAP, 0.0f, 0.985f);
                        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.985f, 0.06f);
                    }
                    else
                    {
                        transactionAmount = 0;
                        transactionStartTime = 0;
                    }
                }

                if (isWalkAroundActive)
                {
                    // In walk-around: allow movement + menu controls
                    Game.EnableControlThisFrame(GTA.Control.FrontendUp);
                    Game.EnableControlThisFrame(GTA.Control.FrontendDown);
                    Game.EnableControlThisFrame(GTA.Control.FrontendLeft);
                    Game.EnableControlThisFrame(GTA.Control.FrontendRight);
                    Game.EnableControlThisFrame(GTA.Control.FrontendAccept);
                    Game.EnableControlThisFrame(GTA.Control.FrontendCancel);
                    Game.EnableControlThisFrame(GTA.Control.PhoneCancel);
                    // Movement and look controls stay enabled by default
                }
                else
                {
                    // In vehicle: block all except menu controls
                    Game.DisableAllControlsThisFrame();
                    Game.EnableControlThisFrame(GTA.Control.FrontendUp);
                    Game.EnableControlThisFrame(GTA.Control.FrontendDown);
                    Game.EnableControlThisFrame(GTA.Control.FrontendLeft);
                    Game.EnableControlThisFrame(GTA.Control.FrontendRight);
                    Game.EnableControlThisFrame(GTA.Control.FrontendAccept);
                    Game.EnableControlThisFrame(GTA.Control.FrontendCancel);
                    Game.EnableControlThisFrame(GTA.Control.PhoneCancel);
                    Game.EnableControlThisFrame(GTA.Control.LookLeftRight);
                    Game.EnableControlThisFrame(GTA.Control.LookUpDown);
                    Game.EnableControlThisFrame(GTA.Control.VehicleExit); // Y for walk-around
                    Game.EnableControlThisFrame(GTA.Control.Aim); // LB for edit mode
                    Game.EnableControlThisFrame(GTA.Control.Sprint); // Shift for edit mode (keyboard)
                    Game.EnableControlThisFrame(GTA.Control.VehicleDuck); // X for edit description
                }
            }

            // LSC detection
            CheckLSCEntry();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == menuKey && !isMenuActive)
            {
                TryOpenMenu();
            }

            // Debug Menu (F7 to open/close) - unified debug tool selector
            if (e.KeyCode == Keys.F7 && isMenuActive)
            {
                if (activeDebugMode == DebugMode.None)
                {
                    // Open debug menu
                    activeDebugMode = DebugMode.Menu;
                    debugMenuSelection = 0;
                    ShowNotification("~y~Debug Menu~w~ - D-Pad U/D: select, A: confirm, B: exit");
                }
                else
                {
                    // Exit any debug mode
                    ExitDebugMode();
                }
            }
        }

        #endregion

        #region Menu Control

        private void TryOpenMenu()
        {
            try
            {
                var player = Game.Player.Character;

                if (!player.IsInVehicle())
                {
                    ShowNotification("~r~You must be in a vehicle!");
                    return;
                }

                currentVehicle = player.CurrentVehicle;
                currentVehicle.Mods.InstallModKit();

                // Cache screen resolution for coordinate conversion
                UpdateCachedScreenResolution();

                // Rebuild menus for this vehicle
                RebuildMenusForVehicle();

                // Apply auto-offset for current resolution (unless in debug mode)
                ApplyAutoOffset();

                isMenuActive = true;
                mainMenu.Visible = true;

                // Force handbrake on so player can rev with just RT
                currentVehicle.IsEngineRunning = true;
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, true);

                Log($"Menu opened for vehicle: {currentVehicle.DisplayName}");
            }
            catch (Exception ex)
            {
                Log($"ERROR in TryOpenMenu: {ex.Message}\n{ex.StackTrace}");
                ShowNotification("~r~Menu error - check log");
            }
        }

        private void CloseMenu()
        {
            // Exit camera mode if active
            if (isWalkAroundActive)
            {
                ExitWalkAround();
            }

            // Release handbrake when menu closes
            if (currentVehicle != null && currentVehicle.Exists())
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, false);
            }

            // Save vehicle purchase data
            VehicleSaveData.Save();

            isMenuActive = false;
            currentVehicle = null;
            Log("Menu closed");
        }

        #endregion

        #region LSC Detection

        private void CheckLSCEntry()
        {
            var player = Game.Player.Character;
            int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);

            if (interior != lastInterior)
            {
                Log($"Interior changed: {lastInterior} -> {interior}");

                bool enteredLSC = Array.Exists(LSC_INTERIORS, id => id == interior);
                bool leftLSC = Array.Exists(LSC_INTERIORS, id => id == lastInterior);

                if (enteredLSC && player.IsInVehicle())
                {
                    Log("Entered Los Santos Customs - waiting for vehicle to stop");
                    isInLSC = true;
                    waitingForVehicleStop = true;
                }
                else if (leftLSC)
                {
                    Log("Left Los Santos Customs - closing menu");
                    isInLSC = false;

                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);

                    if (isMenuActive)
                    {
                        mainMenu.Visible = false;
                        CloseMenu();
                    }
                }

                lastInterior = interior;
            }

            // Detect service position
            if (waitingForVehicleStop && isInLSC)
            {
                var ped = Game.Player.Character;
                if (ped.IsInVehicle())
                {
                    var vehicle = ped.CurrentVehicle;
                    var pos = vehicle.Position;
                    float speed = vehicle.Speed;
                    float posDiff = lastVehiclePos != Vector3.Zero ? pos.DistanceTo(lastVehiclePos) : 0;

                    if (speed < 0.5f && posDiff > 0.3f && posDiff < 1.5f)
                    {
                        Log($"Service position teleport detected! Pos diff: {posDiff:F2}");
                        waitingForVehicleStop = false;
                        lastVehiclePos = Vector3.Zero;

                        DeleteAndRespawnMechanic(pos);
                        TryOpenMenu();

                        Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                        Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                    }

                    lastVehiclePos = pos;
                }
            }

            if (isInLSC && isMenuActive)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            }
        }

        #endregion

        #region Mechanic Management

        private void DeleteAndRespawnMechanic(Vector3 position)
        {
            if (!ENABLE_MECHANIC_REPLACEMENT) return;

            DeleteCustomMechanic();

            Ped[] nearbyPeds = World.GetNearbyPeds(position, 10f);

            foreach (var ped in nearbyPeds)
            {
                if (ped == Game.Player.Character) continue;

                Model model = ped.Model;
                Vector3 pos = ped.Position;
                float heading = ped.Heading;

                int[] drawables = new int[12];
                int[] textures = new int[12];
                for (int i = 0; i < 12; i++)
                {
                    drawables[i] = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, i);
                    textures[i] = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped, i);
                }

                Log($"Deleting mechanic - Model: {model.Hash}, Pos: {pos}");
                ped.Delete();

                model.Request(1000);
                if (model.IsLoaded)
                {
                    Ped newMechanic = World.CreatePed(model, pos, heading);
                    if (newMechanic != null)
                    {
                        for (int i = 0; i < 12; i++)
                        {
                            Function.Call(Hash.SET_PED_COMPONENT_VARIATION, newMechanic, i, drawables[i], textures[i], 0);
                        }

                        newMechanic.BlockPermanentEvents = true;
                        customMechanic = newMechanic;
                        customMechanicPos = pos;
                        Log($"Respawned mechanic with original outfit at {pos}");
                    }
                    model.MarkAsNoLongerNeeded();
                }

                break;
            }
        }

        private void DeleteCustomMechanic()
        {
            if (customMechanic != null && customMechanic.Exists())
            {
                Log("Deleting custom mechanic");
                customMechanic.Delete();
            }
            customMechanic = null;
            customMechanicPos = Vector3.Zero;
        }

        private void CheckMechanicCleanup()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;
            if (isInLSC) return;

            Ped[] nearbyPeds = World.GetNearbyPeds(customMechanicPos, 3f);
            foreach (var ped in nearbyPeds)
            {
                if (ped == Game.Player.Character) continue;
                if (ped == customMechanic) continue;

                float playerDistance = Game.Player.Character.Position.DistanceTo(customMechanicPos);
                Log($"New mechanic spawned at distance {playerDistance:F1}m - deleting custom mechanic");
                DeleteCustomMechanic();
                return;
            }
        }

        private void UpdateMechanicBehavior()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;

            Vector3 playerPos = Game.Player.Character.Position;
            Vector3 mechPos = customMechanic.Position;
            Vector3 direction = playerPos - mechPos;
            float heading = (float)(Math.Atan2(direction.Y, direction.X) * (180.0 / Math.PI)) - 90f;
            customMechanic.Heading = heading;
        }

        private void MechanicSpeak()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;

            string[] speeches = { "GENERIC_THANKS", "GENERIC_BYE", "CHAT_STATE", "CHAT_RESP" };
            string speech = speeches[new Random().Next(speeches.Length)];

            Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, customMechanic, speech, "SPEECH_PARAMS_FORCE_NORMAL");
        }

        #endregion

        #region Walk-Around Camera

        private void EnterWalkAround()
        {
            try
            {
                var player = Game.Player.Character;
                if (currentVehicle == null || !currentVehicle.Exists())
                {
                    Log("Cannot enter walk-around: no current vehicle");
                    return;
                }

                Log("Entering walk-around camera mode");
                isWalkAroundActive = true;
                walkAroundVehicle = currentVehicle;

                // Calculate camera distances based on vehicle size
                CalculateVehicleCameraDistances(walkAroundVehicle);

                // Build available presets based on vehicle's mod options
                BuildAvailablePresets();

                // Initialize camera orbit position (start at left side of vehicle)
                cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(new Vector3(-vehicleCamStartDist, 0, 0));
                smoothCamPos = cameraOrbitPos;  // Initialize smoothed position to starting position
                smoothCamHeight = cameraHeight;

                // Create scripted camera
                Vector3 camStartPos = new Vector3(cameraOrbitPos.X, cameraOrbitPos.Y, walkAroundVehicle.Position.Z + cameraHeight);
                walkAroundCam = World.CreateCamera(camStartPos, Vector3.Zero, 50f);
                walkAroundCam.PointAt(walkAroundVehicle);
                World.RenderingCamera = walkAroundCam;

                // Reset look offset and preset mode
                lookOffsetH = 0f;
                currentPresetIndex = 0;
                isInPresetMode = false;

                ShowNotification("~b~Camera Mode~w~\nLB/RB - Cycle Parts | A - Open/Close\nLS/RS - Free Roam | Y - Exit");
                Log("Walk-around camera mode activated");
            }
            catch (Exception ex)
            {
                Log($"ERROR in EnterWalkAround: {ex.Message}");
                ExitWalkAround();
            }
        }

        private void ExitWalkAround()
        {
            try
            {
                Log("Exiting walk-around camera mode");

                // Restore normal camera
                World.RenderingCamera = null;

                // Destroy scripted camera
                if (walkAroundCam != null && walkAroundCam.Exists())
                {
                    walkAroundCam.Delete();
                }
                walkAroundCam = null;

                // Release handbrake and close doors
                if (walkAroundVehicle != null && walkAroundVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, walkAroundVehicle, false);

                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, walkAroundVehicle, i, false);
                        }
                        catch { }
                    }
                    currentVehicle = walkAroundVehicle;
                }

                isWalkAroundActive = false;
                walkAroundVehicle = null;
                cameraOrbitPos = Vector3.Zero;
                cameraModeCooldown = 30; // Half second cooldown to prevent re-entry

                Log("Walk-around camera mode deactivated");
            }
            catch (Exception ex)
            {
                Log($"ERROR in ExitWalkAround: {ex.Message}");
                isWalkAroundActive = false;
                World.RenderingCamera = null;
            }
        }

        private void UpdateWalkAround()
        {
            if (walkAroundVehicle == null || !walkAroundVehicle.Exists())
            {
                ExitWalkAround();
                return;
            }

            if (walkAroundCam == null || !walkAroundCam.Exists())
            {
                ExitWalkAround();
                return;
            }

            // DISABLE vehicle controls (but NOT accelerate - allow revving)
            Game.DisableControlThisFrame(GTA.Control.VehicleExit);
            Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);
            Game.DisableControlThisFrame(GTA.Control.VehicleMoveUpDown);
            // VehicleAccelerate NOT disabled - player can rev with RT (handbrake is on)
            Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
            Game.DisableControlThisFrame(GTA.Control.VehicleHandbrake);
            Game.DisableControlThisFrame(GTA.Control.VehicleCinCam);
            Game.DisableControlThisFrame(GTA.Control.VehicleHorn);
            Game.DisableControlThisFrame(GTA.Control.VehicleLookBehind);

            // Y button to exit (read disabled control)
            bool yPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.VehicleExit);

            if (yPressed)
            {
                Log("Y pressed - exiting camera mode");
                ExitWalkAround();
                return;
            }

            // Walk around - movement relative to camera-to-car direction
            float moveSpeed = 0.04f; // Walking pace speed

            // Get direction from camera to car (this is our "forward")
            Vector3 toCar = walkAroundVehicle.Position - cameraOrbitPos;
            toCar.Z = 0;
            float dist = toCar.Length();
            if (dist > 0.01f)
            {
                toCar = toCar / dist; // Normalize
            }
            else
            {
                toCar = new Vector3(0, 1, 0);
            }

            // Right is perpendicular to forward (rotate 90 degrees)
            Vector3 right = new Vector3(toCar.Y, -toCar.X, 0);

            // LB/RB to cycle through preset camera positions
            bool lbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Aim); // LB
            bool rbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Cover); // RB

            if (lbPressed && availablePresets.Count > 0)
            {
                // Previous preset
                currentPresetIndex--;
                if (currentPresetIndex < 0) currentPresetIndex = availablePresets.Count - 1;
                GoToPreset(currentPresetIndex);

                // Safety check - GoToPreset might have triggered something that invalidated state
                if (walkAroundVehicle == null || !isWalkAroundActive) return;
            }
            else if (rbPressed && availablePresets.Count > 0)
            {
                // Next preset
                currentPresetIndex++;
                if (currentPresetIndex >= availablePresets.Count) currentPresetIndex = 0;
                GoToPreset(currentPresetIndex);

                // Safety check - GoToPreset might have triggered something that invalidated state
                if (walkAroundVehicle == null || !isWalkAroundActive) return;
            }

            // Left stick input (read disabled VEHICLE controls) - MOVEMENT
            float inputForward = -Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveUpDown);
            float inputRight = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveLeftRight);

            // Right stick input
            float lookInputH = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookLeftRight);
            float lookInputV = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookUpDown);

            // If any stick is moved, exit preset mode and return to free roam
            bool stickMoved = Math.Abs(inputForward) > 0.2f || Math.Abs(inputRight) > 0.2f ||
                              Math.Abs(lookInputH) > 0.2f || Math.Abs(lookInputV) > 0.2f;

            if (stickMoved && isInPresetMode)
            {
                isInPresetMode = false;
                // Camera will smoothly transition back due to lerp
            }

            // Only allow manual movement when not in preset mode (or transitioning out)
            if (!isInPresetMode || stickMoved)
            {
                if (Math.Abs(inputForward) > 0.1f || Math.Abs(inputRight) > 0.1f)
                {
                    Vector3 movement = (toCar * inputForward + right * inputRight) * moveSpeed;
                    cameraOrbitPos = cameraOrbitPos + movement;
                    isInPresetMode = false;
                }

                // Right stick left/right - horizontal look
                if (Math.Abs(lookInputH) > 0.1f)
                {
                    lookOffsetH -= lookInputH * 0.6f;
                    lookOffsetH = Math.Max(-45f, Math.Min(45f, lookOffsetH));
                }

                // Right stick up/down - adjust camera height
                if (Math.Abs(lookInputV) > 0.1f)
                {
                    cameraHeight -= lookInputV * 0.05f;
                    cameraHeight = Math.Max(0.3f, Math.Min(4f, cameraHeight));
                }
            }

            // Clamp to box path around vehicle (tighter on sides, more room front/back)
            // Get camera position in vehicle-local coordinates
            Vector3 localPos = walkAroundVehicle.GetPositionOffset(cameraOrbitPos);

            float clampedX = localPos.X;  // Side (+ = right of vehicle)
            float clampedY = localPos.Y;  // Front/back (+ = front of vehicle)

            // Clamp sides (width) - tighter
            if (Math.Abs(clampedX) > vehicleHalfWidth)
            {
                clampedX = Math.Sign(clampedX) * vehicleHalfWidth;
            }
            // Clamp front/back (length) - more room
            if (Math.Abs(clampedY) > vehicleHalfLength)
            {
                clampedY = Math.Sign(clampedY) * vehicleHalfLength;
            }

            // Minimum distance from center (don't get too close)
            float localDist = (float)Math.Sqrt(clampedX * clampedX + clampedY * clampedY);
            if (localDist < vehicleCamMinDist && localDist > 0.01f)
            {
                float scale = vehicleCamMinDist / localDist;
                clampedX *= scale;
                clampedY *= scale;
            }

            // Update orbit position with clamped values
            cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(new Vector3(clampedX, clampedY, 0));

            // Smooth the camera position using lerp
            smoothCamPos = Vector3.Lerp(smoothCamPos, cameraOrbitPos, CAM_SMOOTH_SPEED);
            smoothCamHeight = smoothCamHeight + (cameraHeight - smoothCamHeight) * CAM_SMOOTH_SPEED;

            // Calculate target camera position
            Vector3 camPos = new Vector3(smoothCamPos.X, smoothCamPos.Y, walkAroundVehicle.Position.Z + smoothCamHeight);

            // Simple collision check - ensure camera stays outside vehicle bounding box
            Vector3 localCamPos = walkAroundVehicle.GetPositionOffset(camPos);

            // Get minimum distance from vehicle center (half width/length with small margin)
            float minDistX = (vehicleHalfWidth - 1.5f) + 0.3f;  // Remove the margin we added, add small buffer
            float minDistY = (vehicleHalfLength - 2.0f) + 0.3f;

            // If camera is inside the vehicle bounds, push it out RADIALLY (not to nearest edge)
            if (Math.Abs(localCamPos.X) < minDistX && Math.Abs(localCamPos.Y) < minDistY)
            {
                // Calculate current angle from center
                float angle = (float)Math.Atan2(localCamPos.Y, localCamPos.X);

                // Find the distance to the edge of the bounding box at this angle
                // For a rectangle, the distance varies with angle
                float cosAngle = (float)Math.Abs(Math.Cos(angle));
                float sinAngle = (float)Math.Abs(Math.Sin(angle));

                // Distance to edge at this angle (rectangular boundary)
                float edgeDistX = cosAngle > 0.001f ? minDistX / cosAngle : float.MaxValue;
                float edgeDistY = sinAngle > 0.001f ? minDistY / sinAngle : float.MaxValue;
                float edgeDist = Math.Min(edgeDistX, edgeDistY);

                // Push camera out to the edge along the same angle
                localCamPos.X = (float)Math.Cos(angle) * edgeDist;
                localCamPos.Y = (float)Math.Sin(angle) * edgeDist;

                // Convert back to world position
                Vector3 pushedPos = walkAroundVehicle.GetOffsetPosition(localCamPos);
                camPos.X = pushedPos.X;
                camPos.Y = pushedPos.Y;
            }

            walkAroundCam.Position = camPos;

            // Calculate look target - automatically tilt based on camera height
            // Base target is vehicle center at a comfortable viewing height
            float vehicleCenterHeight = 0.6f; // Approximate center of most vehicles
            Vector3 vehicleCenter = walkAroundVehicle.Position;
            vehicleCenter.Z += vehicleCenterHeight;

            // Base direction to car center
            Vector3 baseLookDir = vehicleCenter - camPos;

            // Apply horizontal offset (rotate around vertical axis)
            float offsetRadH = lookOffsetH * (float)Math.PI / 180f;
            Vector3 lookDir = new Vector3(
                baseLookDir.X * (float)Math.Cos(offsetRadH) - baseLookDir.Y * (float)Math.Sin(offsetRadH),
                baseLookDir.X * (float)Math.Sin(offsetRadH) + baseLookDir.Y * (float)Math.Cos(offsetRadH),
                baseLookDir.Z // Vertical tilt is automatic based on height difference
            );

            Vector3 lookTarget = camPos + lookDir;
            walkAroundCam.PointAt(lookTarget);

            // Show help text based on mode
            if (isInPresetMode && currentPresetIndex >= 0 && currentPresetIndex < availablePresets.Count)
            {
                string presetName = availablePresets[currentPresetIndex].Name;
                int presetNum = currentPresetIndex + 1;
                int totalPresets = availablePresets.Count;
                GTA.UI.Screen.ShowHelpTextThisFrame($"~b~{presetName}~w~ ({presetNum}/{totalPresets})\nLB/RB - Cycle | Stick - Free Roam");
            }
            else
            {
                GTA.UI.Screen.ShowHelpTextThisFrame($"~w~Free Roam~w~\nLB/RB - Part Views ({availablePresets.Count}) | Y - Exit");
            }

            // Display RPM when revving
            float rpm = walkAroundVehicle.CurrentRPM;
            if (rpm > 0.3f)
            {
                GTA.UI.Screen.ShowSubtitle($"RPM: {rpm:F2}", 1);
            }

        }

        private int GetClosestDoorIndex(Vector3 localCamPos)
        {
            // localCamPos: X = side (negative = left, positive = right)
            //              Y = front/back (negative = back, positive = front)

            bool isLeft = localCamPos.X < 0;
            bool isFront = localCamPos.Y > 0;

            // Determine if we're at the very front (hood) or very back (trunk)
            float sideThreshold = 0.3f; // How centered we need to be for hood/trunk
            bool isCentered = Math.Abs(localCamPos.X) < Math.Abs(localCamPos.Y) * sideThreshold + 0.5f;

            // At the front and centered = hood
            if (isFront && isCentered && localCamPos.Y > 1.0f)
            {
                return 4; // Hood
            }

            // At the back and centered = trunk
            if (!isFront && isCentered && localCamPos.Y < -1.0f)
            {
                return 5; // Trunk
            }

            // Otherwise determine door based on quadrant
            if (isLeft && isFront)
                return 0; // Front Left Door
            if (!isLeft && isFront)
                return 1; // Front Right Door
            if (isLeft && !isFront)
                return 2; // Rear Left Door

            return 3; // Rear Right Door
        }

        private void ToggleDoor(int doorIndex, string doorName)
        {
            if (walkAroundVehicle == null) return;

            try
            {
                var door = walkAroundVehicle.Doors[(VehicleDoorIndex)doorIndex];
                if (door != null)
                {
                    if (door.IsOpen)
                    {
                        Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, walkAroundVehicle, doorIndex, false);
                        ShowNotification($"~w~Closed {doorName}");
                    }
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, walkAroundVehicle, doorIndex, false, false);
                        ShowNotification($"~w~Opened {doorName}");
                    }
                }
            }
            catch { }
        }

        private Vector3 GetPresetCameraPosition(int presetIndex)
        {
            if (walkAroundVehicle == null || presetIndex < 0 || presetIndex >= availablePresets.Count)
                return Vector3.Zero;

            // Get the preset's relative offset (normalized -1 to 1 range)
            Vector3 relOffset = availablePresets[presetIndex].LocalCameraOffset;

            // Scale by vehicle dimensions
            float x = relOffset.X * vehicleHalfWidth;
            float y = relOffset.Y * vehicleHalfLength;
            float z = relOffset.Z; // Z is absolute offset for height variation

            return new Vector3(x, y, z);
        }

        private void GoToPreset(int presetIndex)
        {
            if (walkAroundVehicle == null || availablePresets.Count == 0) return;

            currentPresetIndex = presetIndex;
            isInPresetMode = true;

            // Get target position in world space
            Vector3 localPos = GetPresetCameraPosition(presetIndex);
            cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(localPos);

            // Reset look offset to face the vehicle
            lookOffsetH = 0f;

            // Navigate menu to highlight the corresponding mod category
            NavigateMenuToModType(availablePresets[presetIndex].ModType);
        }

        private void NavigateMenuToModType(VehicleModType modType)
        {
            try
            {
                if (mainMenu == null || mainMenu.Items.Count == 0) return;

                // Get the display name for this mod type from ModCategories
                int modIndex = (int)modType;
                string targetName = ModCategories.GetDisplayName(modIndex);

                // Find the item with this display name in the flat alphabetical menu
                for (int i = 0; i < mainMenu.Items.Count; i++)
                {
                    if (mainMenu.Items[i].Title == targetName)
                    {
                        mainMenu.SelectedIndex = i;
                        Log($"Navigated to menu item: {targetName} (index {i})");
                        return;
                    }
                }

                Log($"Could not find menu item for: {targetName}");
            }
            catch (Exception ex)
            {
                Log($"Error navigating menu: {ex.Message}");
            }
        }

        private void BuildAvailablePresets()
        {
            availablePresets.Clear();

            if (walkAroundVehicle == null) return;

            // Check which mod types are available for this vehicle
            foreach (var preset in AllModPresets)
            {
                int modCount = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, walkAroundVehicle, (int)preset.ModType);
                if (modCount > 0)
                {
                    availablePresets.Add(preset);
                    Log($"Added preset: {preset.Name} ({modCount} mods available)");
                }
            }

            // Always add a general "Overview" preset if we have any mods
            if (availablePresets.Count > 0)
            {
                Log($"Built {availablePresets.Count} camera presets for vehicle");
            }
            else
            {
                // Fallback - add basic presets even if no mods detected
                availablePresets.Add(new ModCameraPreset("Front", VehicleModType.FrontBumper, new Vector3(0, 1f, 0)));
                availablePresets.Add(new ModCameraPreset("Side", VehicleModType.SideSkirt, new Vector3(-1f, 0, 0)));
                availablePresets.Add(new ModCameraPreset("Rear", VehicleModType.RearBumper, new Vector3(0, -1f, 0)));
                Log("No mods detected, using fallback presets");
            }
        }

        private void CalculateVehicleCameraDistances(Vehicle vehicle)
        {
            try
            {
                // GET_MODEL_DIMENSIONS returns min and max corners of bounding box
                OutputArgument min = new OutputArgument();
                OutputArgument max = new OutputArgument();
                Function.Call(Hash.GET_MODEL_DIMENSIONS, vehicle.Model.Hash, min, max);
                Vector3 minVec = min.GetResult<Vector3>();
                Vector3 maxVec = max.GetResult<Vector3>();

                // Calculate vehicle size (length and width)
                float length = maxVec.Y - minVec.Y;
                float width = maxVec.X - minVec.X;
                float height = maxVec.Z - minVec.Z;

                // Store half dimensions for box path clamping (with margin for camera)
                float sideMargin = 1.5f;   // How far from side of car
                float frontBackMargin = 2.0f; // How far from front/back of car
                vehicleHalfWidth = (width / 2f) + sideMargin;
                vehicleHalfLength = (length / 2f) + frontBackMargin;

                // Min distance is for when camera gets too close to center
                vehicleCamMinDist = Math.Max(1.2f, Math.Min(vehicleHalfWidth, vehicleHalfLength) * 0.8f);
                vehicleCamMaxDist = Math.Max(vehicleHalfLength, vehicleHalfWidth); // Not really used with box path
                vehicleCamStartDist = vehicleHalfWidth; // Start at side of vehicle
                cameraHeight = Math.Max(1.2f, height * 0.6f + 0.5f); // Half vehicle height + offset

                Log($"Vehicle dimensions: {length:F1}x{width:F1}x{height:F1} - Box path: halfW={vehicleHalfWidth:F1}, halfL={vehicleHalfLength:F1}, height={cameraHeight:F1}");
            }
            catch (Exception ex)
            {
                // Fall back to defaults if something goes wrong
                vehicleCamMinDist = 1.5f;
                vehicleCamMaxDist = 4f;
                vehicleCamStartDist = 3f;
                vehicleHalfWidth = 2f;
                vehicleHalfLength = 3f;
                cameraHeight = 1.5f;
                Log($"ERROR calculating vehicle camera distances: {ex.Message}");
            }
        }

        #endregion

        #region Utility

        private void ShowNotification(string message)
        {
            GTA.UI.Notification.Show(message);
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
        private bool TryPurchase(int price, bool alreadyOwned)
        {
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
                string logPath = $"scripts\\ExtendedLSC.log";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                System.IO.File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
            catch { }
        }

        #endregion

        #region Description Editor

        /// <summary>
        /// Start editing the description for a category using the on-screen keyboard
        /// </summary>
        private void StartEditDescription(string categoryName)
        {
            if (isEditingDescription) return;

            string currentDesc = MenuConfig.GetDescription(categoryName);
            editingCategoryName = categoryName;
            isEditingDescription = true;

            // Show on-screen keyboard
            // Parameters: type, windowTitle, defaultText, defaultText2, defaultText3, defaultText4, defaultText5, maxLength
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD,
                0,                          // KEYBOARD_TYPE_DEFAULT
                "FMMC_KEY_TIP8",            // Window title (generic "Enter Text")
                "",                         // Empty placeholder
                currentDesc,                // Default text (current description)
                "",                         // Empty
                "",                         // Empty
                "",                         // Empty
                256);                       // Max length

            Log($"[DescEditor] Started editing description for '{categoryName}'");
            ShowNotification($"~y~Editing: {categoryName}~n~Press A to save, B to cancel");
        }

        /// <summary>
        /// Check if the on-screen keyboard has finished and process the result
        /// </summary>
        private void UpdateDescriptionEditor()
        {
            if (!isEditingDescription) return;

            // Cooldown to prevent rapid checks
            if (keyboardCheckCooldown > 0)
            {
                keyboardCheckCooldown--;
                return;
            }
            keyboardCheckCooldown = 5; // Check every 5 frames

            // Check keyboard status
            // Returns: -1 = not active, 0 = typing, 1 = finished/accepted, 2 = cancelled
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);

            // Log status for debugging
            if (status != 0)
            {
                Log($"[DescEditor] Keyboard status: {status}");
            }

            if (status == 1) // Finished - user pressed Enter/Accept
            {
                string result = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);

                if (!string.IsNullOrEmpty(result))
                {
                    MenuConfig.SetDescription(editingCategoryName, result);
                    ShowNotification($"~g~Description saved for: {editingCategoryName}");
                    Log($"[DescEditor] Saved description for '{editingCategoryName}': {result}");

                    // Rebuild menus to show updated description
                    if (currentVehicle != null)
                    {
                        RebuildMenusForVehicle();
                        mainMenu.Visible = true;
                    }
                }
                else
                {
                    ShowNotification($"~r~Description not saved (empty text)");
                }

                isEditingDescription = false;
                editingCategoryName = null;
            }
            else if (status == 2) // Cancelled
            {
                ShowNotification($"~y~Description edit cancelled");
                isEditingDescription = false;
                editingCategoryName = null;
            }
        }

        /// <summary>
        /// Check for X input to edit category description (hold X for 500ms)
        /// </summary>
        private void CheckDescriptionEditInput()
        {
            // X button = FrontendX or Duck (depends on context)
            bool xHeld = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX) ||
                         Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);

            if (!ModSettings.EditorModeEnabled) return;

            // Don't process X input while keyboard is open (A=save, B=cancel handled by GTA)
            if (isEditingDescription) return;

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
