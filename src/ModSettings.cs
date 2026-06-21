using System;
using System.IO;
using System.Windows.Forms;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles mod settings stored in an INI-style config file.
    /// Users can edit this file to enable/disable features.
    ///
    /// Architecture:
    /// - Main Course: Core LSC clone (always on)
    /// - Side Course: Optional features (toggleable, on by default)
    /// </summary>
    public static class ModSettings
    {
        private static string ConfigPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ExtendedLSC",
            "settings.ini"
        );

        #region Side Course Features (Optional, on by default)

        /// <summary>Custom camera system with part-focused presets and free roam</summary>
        public static bool CustomCamera { get; set; } = true;

        /// <summary>Condensed paint menu with color wheel UI (not yet implemented)</summary>
        public static bool CondensedPaintMenu { get; set; } = true;

        /// <summary>Extended mod categories: engine swaps, extras toggle, etc. (not yet implemented)</summary>
        public static bool ExtendedCategories { get; set; } = true;

        /// <summary>Save/load vehicle mod presets (not yet implemented)</summary>
        public static bool VehiclePackages { get; set; } = true;

        /// <summary>Live handling.meta editing with sliders (not yet implemented)</summary>
        public static bool VehicleTuning { get; set; } = true;

        /// <summary>Optional "Customize Radio": loop the player's OWN mp3/wav files (from scripts/ExtendedLSC/radio)
        /// while the LSC menu is open. No music is bundled — drop your own tracks in the folder.</summary>
        public static bool CustomizeRadio { get; set; } = true;
        public static float CustomizeRadioVolume { get; set; } = 0.19f;  // 0..1 ceiling (~-10 dB; quieter than in-game radio)

        /// <summary>Let the menu key open ELSC ANYWHERE (not just at a Los Santos Customs shop). Off by default.</summary>
        public static bool OpenAnywhere { get; set; } = false;

        /// <summary>Make every purchase free (skips all charges + zeroes package cost offsets). Off by default.</summary>
        public static bool AllItemsFree { get; set; } = false;

        /// <summary>Show ELSC's small contextual hint bubbles / action popups ANYWHERE they'd appear (door
        /// open/close, the free-roam help bubble, etc.). On by default; turn off to fully declutter the screen.</summary>
        public static bool ShowHints { get; set; } = true;

        /// <summary>Idle showcase cinematic: after ~10s of no input in the menu, fade to black, hide the HUD/menu and
        /// slowly orbit the car (fading on each side switch). Any input snaps back. On by default.</summary>
        public static bool IdleCinematic { get; set; } = true;

        /// <summary>Keep the front wheels turned where you left them instead of snapping back to center when you exit
        /// a parked vehicle (global steering auto-center fix). Also lets you turn the wheels in the walk-around camera
        /// (D-pad Left/Right) to inspect them. On by default.</summary>
        public static bool KeepSteeringAngle { get; set; } = true;

        /// <summary>While moving with Manual Transmission, restrict the player to melee (no drive-by shooting),
        /// which also frees the weapon controls so they can't interfere with shifting.</summary>
        public static bool MeleeOnlyInMotion { get; set; } = true;

        // ---- Manual Transmission feel (live-tunable; edit the INI then reload to apply) ----
        public static bool MtRumble { get; set; } = true;            // controller rumble on shifts + limiter
        public static bool MtExhaustPops { get; set; } = true;       // backfire pop on upshift + decel crackle
        public static float MtKickForce { get; set; } = 30f;         // perfect-shift forward lunge strength
        public static float MtLimiterRpm { get; set; } = 0.985f;     // rev-limiter engage point (0..1)
        public static float MtNosRefillPerSec { get; set; } = 0.015f; // passive NOS regen rate
        public static float MtNosPerfectBump { get; set; } = 0.06f;  // NOS added per great shift

        /// <summary>Headlight and neon color/brightness customization (not yet implemented)</summary>
        public static bool LightCustomization { get; set; } = true;

        /// <summary>VStancer wheel fitment integration (not yet implemented)</summary>
        public static bool VStancerIntegration { get; set; } = true;

        /// <summary>Wheel Fitment Pro Mode: per-axle camber/track/height sliders. Off = simple menu.</summary>
        public static bool WheelFitmentProMode { get; set; } = false;

        /// <summary>Install NOS/nitrous on the manual-transmission vehicle (temporary test toggle until
        /// it becomes a purchasable LSC upgrade). Off by default.</summary>
        public static bool NosEnabled { get; set; } = false;

        /// <summary>Per-vehicle manual transmission upgrade option</summary>
        public static bool ManualTransmission { get; set; } = true;

        /// <summary>Controller button for shift up (GTA control index). Default 45 = RB</summary>
        public static int ShiftUpButton { get; set; } = 45;

        /// <summary>Controller button for shift down (GTA control index). Default 37 = LB</summary>
        public static int ShiftDownButton { get; set; } = 37;

        /// <summary>Keyboard key for shift up. Default: E</summary>
        public static Keys ShiftUpKey { get; set; } = Keys.E;

        /// <summary>Keyboard key for shift down. Default: Q</summary>
        public static Keys ShiftDownKey { get; set; } = Keys.Q;

        /// <summary>Keyboard key for neutral toggle. Default: X</summary>
        public static Keys NeutralKey { get; set; } = Keys.X;

        /// <summary>Keyboard key to spray nitrous (NOS). Bound when NOS is purchased. Default: N</summary>
        public static Keys NosKey { get; set; } = Keys.N;

        /// <summary>Controller control index to spray nitrous. Default: 76 (Xbox X / handbrake).</summary>
        public static int NosButton { get; set; } = 76;

        /// <summary>Nitrous exhaust-flame colour (ARGB). Default: blue.</summary>
        public static int NosFlameColorArgb { get; set; } = unchecked((int)0xFF3C78FF);

        /// <summary>Which exhaust-FX catalog entry the nitrous spray uses (NOS 1 = index 0).</summary>
        public static int NosFxIndex { get; set; } = 0;

        /// <summary>Play the nitrous boost effect (whoosh + screen blur) on spray. Default: on.</summary>
        public static bool NosBoostFx { get; set; } = true;

        /// <summary>Bitmask of purchased nitrous tiers (bit 0 = NOS 1 … bit 3 = NOS 4). 0 = none owned.</summary>
        public static int NosOwnedTiers { get; set; } = 0;

        #endregion

        #region Controls (remappable — keyboard keys + controller GTA.Control indices)

        // ---- Keyboard ----
        /// <summary>Open / close the ELSC menu. Default: F5</summary>
        public static Keys MenuKey { get; set; } = Keys.F5;
        /// <summary>Open the debug tools menu (dev). Default: F7</summary>
        public static Keys DebugMenuKey { get; set; } = Keys.F7;
        /// <summary>Toggle modder edit mode (requires EditorMode). Default: F6</summary>
        public static Keys EditModeKey { get; set; } = Keys.F6;
        /// <summary>Rename the highlighted category in edit mode. Default: F2</summary>
        public static Keys RenameCategoryKey { get; set; } = Keys.F2;
        /// <summary>Delete / restore the highlighted category in edit mode. Default: Delete</summary>
        public static Keys DeleteCategoryKey { get; set; } = Keys.Delete;
        // (ShiftUpKey / ShiftDownKey / NeutralKey / NosKey live in the Manual Transmission region above.)

        // ---- Controller (stored as GTA.Control index so the on-screen glyphs auto-match the player's device) ----
        /// <summary>Enter / exit the walk-around camera. Default: Y (VehicleExit).</summary>
        public static int WalkAroundButton { get; set; } = (int)GTA.Control.VehicleExit;
        /// <summary>Walk-around: previous camera preset. Default: LB (FrontendLb).</summary>
        public static int CamPrevButton { get; set; } = (int)GTA.Control.FrontendLb;
        /// <summary>Walk-around: next camera preset. Default: RB (FrontendRb).</summary>
        public static int CamNextButton { get; set; } = (int)GTA.Control.FrontendRb;
        /// <summary>Walk-around: open / close the nearest door. Default: X (FrontendX).</summary>
        public static int DoorButton { get; set; } = (int)GTA.Control.FrontendX;
        /// <summary>Hold to preview the highlighted horn. Default: L3 (FrontendLs).</summary>
        public static int HornPreviewButton { get; set; } = (int)GTA.Control.FrontendLs;

        #endregion

        #region UI Settings

        /// <summary>Show scroll indicators (up/down arrows) when menu has more items</summary>
        public static bool ShowScrollIndicators { get; set; } = true;

        /// <summary>Show RPM display while revving engine in menu</summary>
        public static bool ShowRPMWhileRevving { get; set; } = true;

        #endregion

        #region Developer Settings (off by default for release)

        /// <summary>Enable editor mode for in-game editing of descriptions</summary>
        public static bool EditorMode { get; set; } = false;

        /// <summary>Enable debug logging to ExtendedLSC.log</summary>
        public static bool DebugLogging { get; set; } = false;

        #endregion

        // Legacy property names for compatibility
        public static bool EditorModeEnabled { get => EditorMode; set => EditorMode = value; }

        // Action for logging
        public static Action<string> Log { get; set; }

        /// <summary>
        /// Load settings from file, creating defaults if needed
        /// </summary>
        public static void Load()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(ConfigPath))
                {
                    string[] lines = File.ReadAllLines(ConfigPath);
                    bool hasControlsSection = false;
                    foreach (string line in lines)
                    {
                        if (line.Trim().Equals("[Controls]", StringComparison.OrdinalIgnoreCase)) hasControlsSection = true;
                        string trimmed = line.Trim();

                        // Skip comments and empty lines
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("["))
                            continue;

                        // Parse key=value
                        int eqIndex = trimmed.IndexOf('=');
                        if (eqIndex > 0)
                        {
                            string key = trimmed.Substring(0, eqIndex).Trim().ToLowerInvariant();
                            string value = trimmed.Substring(eqIndex + 1).Trim().ToLowerInvariant();

                            switch (key)
                            {
                                // Side Course Features
                                case "customcamera":
                                case "custom_camera":
                                    CustomCamera = ParseBool(value);
                                    break;
                                case "condensedpaintmenu":
                                case "condensed_paint_menu":
                                    CondensedPaintMenu = ParseBool(value);
                                    break;
                                case "extendedcategories":
                                case "extended_categories":
                                    ExtendedCategories = ParseBool(value);
                                    break;
                                case "vehiclepackages":
                                case "vehicle_packages":
                                    VehiclePackages = ParseBool(value);
                                    break;
                                case "vehicletuning":
                                case "vehicle_tuning":
                                    VehicleTuning = ParseBool(value);
                                    break;
                                case "customizeradio":
                                case "customize_radio":
                                    CustomizeRadio = ParseBool(value);
                                    break;
                                case "customizeradiovolume":
                                case "customize_radio_volume":
                                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float crv))
                                        CustomizeRadioVolume = crv < 0f ? 0f : (crv > 1f ? 1f : crv);
                                    break;
                                case "openanywhere":
                                case "open_anywhere":
                                    OpenAnywhere = ParseBool(value);
                                    break;
                                case "allitemsfree":
                                case "all_items_free":
                                    AllItemsFree = ParseBool(value);
                                    break;
                                case "showhints":
                                case "show_hints":
                                case "walkaroundhints":        // backward-compat alias (old name)
                                case "walk_around_hints":
                                    ShowHints = ParseBool(value);
                                    break;
                                case "idlecinematic":
                                case "idle_cinematic":
                                    IdleCinematic = ParseBool(value);
                                    break;
                                case "keepsteeringangle":
                                case "keep_steering_angle":
                                    KeepSteeringAngle = ParseBool(value);
                                    break;
                                case "meleeonlyinmotion":
                                case "melee_only_in_motion":
                                    MeleeOnlyInMotion = ParseBool(value);
                                    break;
                                case "mtrumble":
                                    MtRumble = ParseBool(value);
                                    break;
                                case "mtexhaustpops":
                                    MtExhaustPops = ParseBool(value);
                                    break;
                                case "mtkickforce":
                                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mtKf)) MtKickForce = mtKf;
                                    break;
                                case "mtlimiterrpm":
                                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mtLr)) MtLimiterRpm = mtLr;
                                    break;
                                case "mtnosrefillpersec":
                                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mtNr)) MtNosRefillPerSec = mtNr;
                                    break;
                                case "mtnosperfectbump":
                                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float mtNb)) MtNosPerfectBump = mtNb;
                                    break;
                                case "lightcustomization":
                                case "light_customization":
                                    LightCustomization = ParseBool(value);
                                    break;
                                case "vstancerintegration":
                                case "vstancer_integration":
                                case "vstancer":
                                    VStancerIntegration = ParseBool(value);
                                    break;
                                case "wheelfitmentpromode":
                                case "wheel_fitment_pro_mode":
                                case "promode":
                                    WheelFitmentProMode = ParseBool(value);
                                    break;
                                case "nosenabled":
                                case "nos":
                                    NosEnabled = ParseBool(value);
                                    break;
                                case "manualtransmission":
                                case "manual_transmission":
                                case "manual":
                                    ManualTransmission = ParseBool(value);
                                    break;
                                case "shiftupbutton":
                                case "shift_up_button":
                                    if (int.TryParse(value, out int shiftUp))
                                        ShiftUpButton = shiftUp;
                                    break;
                                case "shiftdownbutton":
                                case "shift_down_button":
                                    if (int.TryParse(value, out int shiftDown))
                                        ShiftDownButton = shiftDown;
                                    break;
                                case "shiftupkey":
                                case "shift_up_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys upKey))
                                        ShiftUpKey = upKey;
                                    break;
                                case "shiftdownkey":
                                case "shift_down_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys downKey))
                                        ShiftDownKey = downKey;
                                    break;
                                case "neutralkey":
                                case "neutral_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys neutralKey))
                                        NeutralKey = neutralKey;
                                    break;
                                case "noskey":
                                case "nos_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys nosKey))
                                        NosKey = nosKey;
                                    break;
                                case "nosbutton":
                                case "nos_button":
                                    if (int.TryParse(value, out int nosBtn))
                                        NosButton = nosBtn;
                                    break;
                                case "nosflamecolor":
                                case "nos_flame_color":
                                    if (int.TryParse(value, out int nosCol))
                                        NosFlameColorArgb = nosCol;
                                    break;
                                case "nosfx":
                                case "nos_fx":
                                    if (int.TryParse(value, out int nosFx))
                                        NosFxIndex = nosFx;
                                    break;
                                case "nosboostfx":
                                case "nos_boost_fx":
                                    NosBoostFx = ParseBool(value);
                                    break;
                                case "nosownedtiers":
                                case "nos_owned_tiers":
                                    if (int.TryParse(value, out int nosTiers))
                                        NosOwnedTiers = nosTiers;
                                    break;

                                // Controls (remappable)
                                case "menukey":
                                case "menu_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys mKey)) MenuKey = mKey;
                                    break;
                                case "debugmenukey":
                                case "debug_menu_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys dKey)) DebugMenuKey = dKey;
                                    break;
                                case "editmodekey":
                                case "edit_mode_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys eKey)) EditModeKey = eKey;
                                    break;
                                case "renamecategorykey":
                                case "rename_category_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys rKey)) RenameCategoryKey = rKey;
                                    break;
                                case "deletecategorykey":
                                case "delete_category_key":
                                    if (Enum.TryParse<Keys>(trimmed.Substring(eqIndex + 1).Trim(), true, out Keys delKey)) DeleteCategoryKey = delKey;
                                    break;
                                case "walkaroundbutton":
                                case "walk_around_button":
                                    if (int.TryParse(value, out int waBtn)) WalkAroundButton = waBtn;
                                    break;
                                case "camprevbutton":
                                case "cam_prev_button":
                                    if (int.TryParse(value, out int cpBtn)) CamPrevButton = cpBtn;
                                    break;
                                case "camnextbutton":
                                case "cam_next_button":
                                    if (int.TryParse(value, out int cnBtn)) CamNextButton = cnBtn;
                                    break;
                                case "doorbutton":
                                case "door_button":
                                    if (int.TryParse(value, out int doorBtn)) DoorButton = doorBtn;
                                    break;
                                case "hornpreviewbutton":
                                case "horn_preview_button":
                                    if (int.TryParse(value, out int hpBtn)) HornPreviewButton = hpBtn;
                                    break;

                                // UI Settings
                                case "scrollindicators":
                                case "scroll_indicators":
                                case "showscrollindicators":
                                    ShowScrollIndicators = ParseBool(value);
                                    break;
                                case "showrpm":
                                case "show_rpm":
                                case "showrpmwhilerevving":
                                    ShowRPMWhileRevving = ParseBool(value);
                                    break;

                                // Developer Settings
                                case "editormode":
                                case "editor_mode":
                                case "editormodeenabled":
                                    EditorMode = ParseBool(value);
                                    break;
                                case "debuglogging":
                                case "debug_logging":
                                case "debug":
                                case "enablelogging":
                                case "enable_logging":
                                    DebugLogging = ParseBool(value);
                                    break;
                            }
                        }
                    }
                    Log?.Invoke($"[ModSettings] Loaded settings from {ConfigPath}");

                    // Migrate older INIs: if they predate the [Controls] section, rewrite the file once so the
                    // new remappable bindings appear (all existing values were just loaded, so nothing is lost).
                    if (!hasControlsSection)
                    {
                        Save();
                        Log?.Invoke("[ModSettings] Upgraded settings.ini with the [Controls] section");
                    }
                }
                else
                {
                    // Create default config file
                    CreateDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[ModSettings] Error loading settings: {ex.Message}");
            }

            // Log current settings
            Log?.Invoke($"[ModSettings] Features: Camera={CustomCamera}, Paint={CondensedPaintMenu}, Extended={ExtendedCategories}");
            Log?.Invoke($"[ModSettings] Developer: EditorMode={EditorMode}, Debug={DebugLogging}");
        }

        /// <summary>
        /// Save current settings to file
        /// </summary>
        public static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var writer = new StreamWriter(ConfigPath))
                {
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; ExtendedLSC Settings");
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine(";");
                    writer.WriteLine("; This mod has two modes:");
                    writer.WriteLine(";   Main Course: Core LSC clone (always active)");
                    writer.WriteLine(";   Side Course: Optional enhanced features (toggle below)");
                    writer.WriteLine(";");
                    writer.WriteLine("; Set all Side Course features to 'false' for a pure LSC experience");
                    writer.WriteLine("; Values: true/false, yes/no, 1/0");
                    writer.WriteLine(";");
                    writer.WriteLine();

                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; SIDE COURSE FEATURES");
                    writer.WriteLine("; Optional enhancements (on by default, disable for pure LSC)");
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("[Features]");
                    writer.WriteLine();

                    writer.WriteLine("; Custom camera system with part-focused presets and free roam");
                    writer.WriteLine($"CustomCamera = {BoolToString(CustomCamera)}");
                    writer.WriteLine();

                    writer.WriteLine("; Condensed paint menu with color wheel UI");
                    writer.WriteLine($"CondensedPaintMenu = {BoolToString(CondensedPaintMenu)}");
                    writer.WriteLine();

                    writer.WriteLine("; Extended categories: engine swaps, extras toggle, etc.");
                    writer.WriteLine($"ExtendedCategories = {BoolToString(ExtendedCategories)}");
                    writer.WriteLine();

                    writer.WriteLine("; Save/load vehicle mod presets");
                    writer.WriteLine($"VehiclePackages = {BoolToString(VehiclePackages)}");
                    writer.WriteLine();

                    writer.WriteLine("; Live handling.meta editing with sliders");
                    writer.WriteLine($"VehicleTuning = {BoolToString(VehicleTuning)}");
                    writer.WriteLine();

                    writer.WriteLine("; Customize Radio: loop YOUR OWN mp3/wav files (put them in scripts\\ExtendedLSC\\radio)");
                    writer.WriteLine("; while the menu is open. Toggle also shown at the top of the menu (only if you have tracks).");
                    writer.WriteLine($"CustomizeRadio = {BoolToString(CustomizeRadio)}");
                    writer.WriteLine($"CustomizeRadioVolume = {CustomizeRadioVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    writer.WriteLine();

                    writer.WriteLine("; Let the menu key (MenuKey) open ELSC ANYWHERE, not just at a Los Santos Customs shop");
                    writer.WriteLine($"OpenAnywhere = {BoolToString(OpenAnywhere)}");
                    writer.WriteLine();

                    writer.WriteLine("; Make every purchase free (no charges, package costs show $0)");
                    writer.WriteLine($"AllItemsFree = {BoolToString(AllItemsFree)}");
                    writer.WriteLine();

                    writer.WriteLine("; Show ELSC's little hint bubbles / action popups anywhere (door open/close, free-roam help, etc.)");
                    writer.WriteLine($"ShowHints = {BoolToString(ShowHints)}");
                    writer.WriteLine();

                    writer.WriteLine("; Idle showcase cinematic: after ~10s of no menu input, fade out, hide HUD/menu and slowly orbit the car");
                    writer.WriteLine($"IdleCinematic = {BoolToString(IdleCinematic)}");
                    writer.WriteLine();

                    writer.WriteLine("; Keep the front wheels turned where you left them instead of snapping back to center on exit");
                    writer.WriteLine("; (also lets you turn the wheels in the walk-around camera with D-pad Left/Right to inspect them)");
                    writer.WriteLine($"KeepSteeringAngle = {BoolToString(KeepSteeringAngle)}");
                    writer.WriteLine();

                    writer.WriteLine("; Restrict to melee (no drive-by shooting) while moving with Manual Transmission");
                    writer.WriteLine($"MeleeOnlyInMotion = {BoolToString(MeleeOnlyInMotion)}");
                    writer.WriteLine();

                    writer.WriteLine("; Manual Transmission feel (live-tunable). KickForce ~30, LimiterRpm 0..1 (~0.985).");
                    writer.WriteLine($"MtRumble = {BoolToString(MtRumble)}");
                    writer.WriteLine($"MtExhaustPops = {BoolToString(MtExhaustPops)}");
                    writer.WriteLine($"MtKickForce = {MtKickForce.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    writer.WriteLine($"MtLimiterRpm = {MtLimiterRpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    writer.WriteLine($"MtNosRefillPerSec = {MtNosRefillPerSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    writer.WriteLine($"MtNosPerfectBump = {MtNosPerfectBump.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    writer.WriteLine();

                    writer.WriteLine("; Headlight and neon color/brightness customization");
                    writer.WriteLine($"LightCustomization = {BoolToString(LightCustomization)}");
                    writer.WriteLine();

                    writer.WriteLine("; VStancer wheel fitment integration");
                    writer.WriteLine($"VStancerIntegration = {BoolToString(VStancerIntegration)}");
                    writer.WriteLine();

                    writer.WriteLine("; Wheel Fitment Pro Mode: per-axle camber/track/height sliders (off = simple menu)");
                    writer.WriteLine($"WheelFitmentProMode = {BoolToString(WheelFitmentProMode)}");
                    writer.WriteLine();

                    writer.WriteLine("; Install NOS/nitrous on the manual-transmission car (test toggle; X on Xbox / N key to spray)");
                    writer.WriteLine($"Nos = {BoolToString(NosEnabled)}");
                    writer.WriteLine();

                    writer.WriteLine("; Per-vehicle manual transmission upgrade");
                    writer.WriteLine($"ManualTransmission = {BoolToString(ManualTransmission)}");
                    writer.WriteLine();

                    writer.WriteLine("; Controller button for shift up (GTA control index). Default 45 = RB");
                    writer.WriteLine($"ShiftUpButton = {ShiftUpButton}");
                    writer.WriteLine();

                    writer.WriteLine("; Controller button for shift down (GTA control index). Default 37 = LB");
                    writer.WriteLine($"ShiftDownButton = {ShiftDownButton}");
                    writer.WriteLine();

                    writer.WriteLine("; Keyboard key for shift up. Default: E");
                    writer.WriteLine($"ShiftUpKey = {ShiftUpKey}");
                    writer.WriteLine();

                    writer.WriteLine("; Keyboard key for shift down. Default: Q");
                    writer.WriteLine($"ShiftDownKey = {ShiftDownKey}");
                    writer.WriteLine();

                    writer.WriteLine("; Keyboard key for neutral toggle. Default: X");
                    writer.WriteLine($"NeutralKey = {NeutralKey}");
                    writer.WriteLine("; Nitrous (NOS) spray key/button — bound when NOS is purchased");
                    writer.WriteLine($"NosKey = {NosKey}");
                    writer.WriteLine($"NosButton = {NosButton}");
                    writer.WriteLine("; Nitrous exhaust-flame colour (ARGB integer)");
                    writer.WriteLine($"NosFlameColor = {NosFlameColorArgb}");
                    writer.WriteLine("; Nitrous exhaust effect (catalog index; NOS 1 = 0)");
                    writer.WriteLine($"NosFx = {NosFxIndex}");
                    writer.WriteLine("; Nitrous boost effect (whoosh + screen blur) on spray");
                    writer.WriteLine($"NosBoostFx = {BoolToString(NosBoostFx)}");
                    writer.WriteLine("; Purchased nitrous tiers (bitmask: bit0=NOS1 .. bit3=NOS4)");
                    writer.WriteLine($"NosOwnedTiers = {NosOwnedTiers}");
                    writer.WriteLine();

                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; CONTROLS  (remap any binding below)");
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; Keyboard keys: use .NET key names (F5, F6, Delete, Enter, NumPad0, OemTilde, D1...).");
                    writer.WriteLine("; Controller buttons: GTA control index numbers (the defaults below already map to the");
                    writer.WriteLine(";   right buttons). The on-screen button glyphs automatically match your device");
                    writer.WriteLine(";   (Xbox / PlayStation / keyboard) — change a number only if you know the index you want.");
                    writer.WriteLine("[Controls]");
                    writer.WriteLine();
                    writer.WriteLine("; --- Normal use ---");
                    writer.WriteLine("; Open / close the ELSC menu");
                    writer.WriteLine($"MenuKey = {MenuKey}");
                    writer.WriteLine("; Hold to enter the walk-around camera (controller)");
                    writer.WriteLine($"WalkAroundButton = {WalkAroundButton}");
                    writer.WriteLine("; Walk-around: cycle camera presets (controller)");
                    writer.WriteLine($"CamPrevButton = {CamPrevButton}");
                    writer.WriteLine($"CamNextButton = {CamNextButton}");
                    writer.WriteLine("; Walk-around: open / close nearest door (controller)");
                    writer.WriteLine($"DoorButton = {DoorButton}");
                    writer.WriteLine("; Hold to preview the highlighted horn (controller)");
                    writer.WriteLine($"HornPreviewButton = {HornPreviewButton}");
                    writer.WriteLine();
                    writer.WriteLine("; --- Manual Transmission / Nitrous (see [Features] above for the rest) ---");
                    writer.WriteLine($"; Shift Up: {ShiftUpKey} (key) / {ShiftUpButton} (button)   Shift Down: {ShiftDownKey} / {ShiftDownButton}");
                    writer.WriteLine($"; Neutral: {NeutralKey}    Nitrous: {NosKey} (key) / {NosButton} (button)");
                    writer.WriteLine();
                    writer.WriteLine("; --- Edit mode (modders; requires EditorMode = true in [Developer]) ---");
                    writer.WriteLine("; Toggle edit mode");
                    writer.WriteLine($"EditModeKey = {EditModeKey}");
                    writer.WriteLine("; Rename the highlighted category");
                    writer.WriteLine($"RenameCategoryKey = {RenameCategoryKey}");
                    writer.WriteLine("; Delete / restore the highlighted category");
                    writer.WriteLine($"DeleteCategoryKey = {DeleteCategoryKey}");
                    writer.WriteLine("; Open the debug tools menu");
                    writer.WriteLine($"DebugMenuKey = {DebugMenuKey}");
                    writer.WriteLine();

                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; UI SETTINGS");
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("[UI]");
                    writer.WriteLine();

                    writer.WriteLine("; Show scroll indicators (up/down arrows) when menu has more items");
                    writer.WriteLine($"ScrollIndicators = {BoolToString(ShowScrollIndicators)}");
                    writer.WriteLine();

                    writer.WriteLine("; Show RPM display while revving engine in menu");
                    writer.WriteLine($"ShowRPM = {BoolToString(ShowRPMWhileRevving)}");
                    writer.WriteLine();

                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("; DEVELOPER SETTINGS");
                    writer.WriteLine("; For mod development only - leave disabled for normal use");
                    writer.WriteLine("; ============================================================");
                    writer.WriteLine("[Developer]");
                    writer.WriteLine();

                    writer.WriteLine("; Enable editor mode for in-game editing of descriptions");
                    writer.WriteLine($"EditorMode = {BoolToString(EditorMode)}");
                    writer.WriteLine();

                    writer.WriteLine("; Enable debug logging to ExtendedLSC.log");
                    writer.WriteLine($"DebugLogging = {BoolToString(DebugLogging)}");
                }

                Log?.Invoke($"[ModSettings] Saved settings to {ConfigPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[ModSettings] Error saving settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Create default config file
        /// </summary>
        private static void CreateDefaultConfig()
        {
            Save();
            Log?.Invoke($"[ModSettings] Created default config at {ConfigPath}");
        }

        /// <summary>
        /// Parse boolean from various formats
        /// </summary>
        private static bool ParseBool(string value)
        {
            return value == "true" || value == "yes" || value == "1" || value == "on";
        }

        /// <summary>
        /// Convert bool to string for INI file
        /// </summary>
        private static string BoolToString(bool value)
        {
            return value ? "true" : "false";
        }

        /// <summary>
        /// Reload settings from file
        /// </summary>
        public static void Reload()
        {
            Load();
        }
    }
}
