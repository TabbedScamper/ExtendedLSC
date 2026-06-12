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

        /// <summary>Headlight and neon color/brightness customization (not yet implemented)</summary>
        public static bool LightCustomization { get; set; } = true;

        /// <summary>VStancer wheel fitment integration (not yet implemented)</summary>
        public static bool VStancerIntegration { get; set; } = true;

        /// <summary>Wheel Fitment Pro Mode: per-axle camber/track/height sliders. Off = simple menu.</summary>
        public static bool WheelFitmentProMode { get; set; } = false;

        /// <summary>NFS-style drag gauge HUD for the manual transmission (rev tach + gear + NOS).
        /// OFF by default — the simple corner HUD is the default; this is an opt-in option.</summary>
        public static bool DragHudEnabled { get; set; } = false;

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
        public static bool DebugLogging { get; set; } = true;

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
                    foreach (string line in lines)
                    {
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
                                case "draghudenabled":
                                case "drag_hud":
                                case "draghud":
                                    DragHudEnabled = ParseBool(value);
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

                    writer.WriteLine("; Headlight and neon color/brightness customization");
                    writer.WriteLine($"LightCustomization = {BoolToString(LightCustomization)}");
                    writer.WriteLine();

                    writer.WriteLine("; VStancer wheel fitment integration");
                    writer.WriteLine($"VStancerIntegration = {BoolToString(VStancerIntegration)}");
                    writer.WriteLine();

                    writer.WriteLine("; Wheel Fitment Pro Mode: per-axle camber/track/height sliders (off = simple menu)");
                    writer.WriteLine($"WheelFitmentProMode = {BoolToString(WheelFitmentProMode)}");
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
