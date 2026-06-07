using System;
using System.IO;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles mod settings stored in an INI-style config file.
    /// Users can edit this file to enable/disable features.
    /// </summary>
    public static class ModSettings
    {
        private static string ConfigPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ExtendedLSC",
            "settings.ini"
        );

        // Settings with defaults
        public static bool EditorModeEnabled { get; set; } = true;
        public static bool DebugLogging { get; set; } = true;
        public static bool ShowRPMWhileRevving { get; set; } = true;
        public static bool ShowScrollIndicators { get; set; } = true;

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
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                            continue;

                        // Parse key=value
                        int eqIndex = trimmed.IndexOf('=');
                        if (eqIndex > 0)
                        {
                            string key = trimmed.Substring(0, eqIndex).Trim().ToLowerInvariant();
                            string value = trimmed.Substring(eqIndex + 1).Trim().ToLowerInvariant();

                            switch (key)
                            {
                                case "editormode":
                                case "editor_mode":
                                case "editormodeenabled":
                                    EditorModeEnabled = ParseBool(value);
                                    break;
                                case "debuglogging":
                                case "debug_logging":
                                case "debug":
                                    DebugLogging = ParseBool(value);
                                    break;
                                case "showrpm":
                                case "show_rpm":
                                case "showrpmwhilerevving":
                                    ShowRPMWhileRevving = ParseBool(value);
                                    break;
                                case "scrollindicators":
                                case "scroll_indicators":
                                case "showscrollindicators":
                                    ShowScrollIndicators = ParseBool(value);
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
            Log?.Invoke($"[ModSettings] EditorMode={EditorModeEnabled}, DebugLogging={DebugLogging}, ShowRPM={ShowRPMWhileRevving}, ScrollIndicators={ShowScrollIndicators}");
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
                    writer.WriteLine("; ExtendedLSC Settings");
                    writer.WriteLine("; Edit this file to customize mod behavior");
                    writer.WriteLine(";");
                    writer.WriteLine("; Values: true/false, yes/no, 1/0");
                    writer.WriteLine();

                    writer.WriteLine("[Features]");
                    writer.WriteLine();

                    writer.WriteLine("; Enable Editor Mode to edit category descriptions in-game");
                    writer.WriteLine("; When enabled, hold LB (or Shift) + Select on a category to edit its description");
                    writer.WriteLine($"EditorMode = {(EditorModeEnabled ? "true" : "false")}");
                    writer.WriteLine();

                    writer.WriteLine("; Show scroll indicators (up/down arrows) when menu has more items");
                    writer.WriteLine($"ScrollIndicators = {(ShowScrollIndicators ? "true" : "false")}");
                    writer.WriteLine();

                    writer.WriteLine("; Show RPM display while revving engine in menu");
                    writer.WriteLine($"ShowRPM = {(ShowRPMWhileRevving ? "true" : "false")}");
                    writer.WriteLine();

                    writer.WriteLine("[Debug]");
                    writer.WriteLine();

                    writer.WriteLine("; Enable debug logging to ExtendedLSC.log");
                    writer.WriteLine($"DebugLogging = {(DebugLogging ? "true" : "false")}");
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
        /// Reload settings from file
        /// </summary>
        public static void Reload()
        {
            Load();
        }
    }
}
