using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles loading and saving category descriptions and custom categories.
    ///
    /// Folder structure:
    ///   ExtendedLSC/
    ///     Vehicles/
    ///       Default/
    ///         categories.json    <- Default descriptions for all vehicles
    ///       BUFFALO02/           <- Vehicle-specific overrides (future)
    ///         categories.json
    ///
    /// When getting descriptions:
    ///   1. Check vehicle-specific folder first (if set)
    ///   2. Fall back to Default folder
    ///   3. Fall back to hardcoded defaults
    /// </summary>
    public static class CategoryConfig
    {
        private static string BasePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ExtendedLSC",
            "Vehicles"
        );

        private static string DefaultPath => Path.Combine(BasePath, "Default", "categories.json");

        private static string GetVehiclePath(string vehicleName) =>
            Path.Combine(BasePath, vehicleName, "categories.json");

        // Current vehicle being customized (for vehicle-specific overrides)
        public static string CurrentVehicle { get; set; }

        // Hardcoded default descriptions (fallback)
        private static readonly Dictionary<string, string> HardcodedDefaults = new Dictionary<string, string>
        {
            { "Armor", "Protect your car's occupants with military spec composite body panels." },
            { "Brakes", "Increase stopping power and eliminate brake fade." },
            { "Bumpers", "Customize front and rear bumpers." },
            { "Engine", "Increase brake horsepower." },
            { "Exhaust", "Customized sports exhausts." },
            { "Fenders", "Replace your fenders with custom aftermarket designs." },
            { "Grille", "Improved engine cooling" },
            { "Hood", "Enhance car engine cooling." },
            { "Horn", "Custom air horns" },
            { "Lights", "Improve night time visibility and decorative lighting." },
            { "Livery", "Apply a custom decal or livery to your vehicle." },
            { "Plate", "Customize license plate." },
            { "Respray", "Transforms vehicle appearance." },
            { "Roll Cage", "Stiffen your chassis with a roll cage." },
            { "Roof", "Lower your center of gravity with lightweight roof panels." },
            { "Skirts", "Enhance your vehicles look with custom side skirts." },
            { "Spoiler", "Increase downforce." },
            { "Suspension", "Upgrade to a sports oriented suspension setup." },
            { "Transmission", "Improved acceleration with close ratio transmission." },
            { "Turbo", "Reduce lag turbocharger." },
            { "Wheels", "Custom rim, tires, and colors." },
            { "Windows", "A selection of tinted windows." },
            // Sub-categories
            { "Front Bumper", "Replace your front bumper with custom designs." },
            { "Rear Bumper", "Replace your rear bumper with custom designs." },
            { "Left Fender", "Replace your left fender." },
            { "Right Fender", "Replace your right fender." },
            { "Headlights", "Choose between stock and Xenon headlights." },
            { "Neon Kits", "Add underglow neon lighting to your vehicle." },
            { "Neon Layout", "Choose which sides have neon lights." },
            { "Neon Color", "Select your neon light color." },
            { "Wheel Type", "Choose a wheel style category." },
            { "Wheel Color", "Change the color of your wheels." },
            { "Tires", "Customize your tire options." },
            { "Tire Design", "Choose custom tire lettering and designs." },
            { "Tire Enhancements", "Add bulletproof or other tire upgrades." },
            { "Tire Smoke", "Choose the color of your tire smoke." },
        };

        // Cached data
        private static Dictionary<string, CategoryData> _defaultCategories;
        private static Dictionary<string, CategoryData> _vehicleCategories;
        private static string _loadedVehicle;
        private static bool _defaultsLoaded = false;

        // Action for logging
        public static Action<string> Log { get; set; }

        /// <summary>
        /// Category data structure for JSON serialization
        /// </summary>
        public class CategoryData
        {
            public string Description { get; set; }
            public bool IsCustom { get; set; }
            public List<int> ModIndices { get; set; } // For custom categories - which mod indices to include
        }

        /// <summary>
        /// Config file root structure
        /// </summary>
        public class ConfigFile
        {
            public Dictionary<string, CategoryData> Categories { get; set; }
        }

        /// <summary>
        /// Ensure directories exist
        /// </summary>
        private static void EnsureDirectories()
        {
            string defaultDir = Path.GetDirectoryName(DefaultPath);
            if (!Directory.Exists(defaultDir))
            {
                Directory.CreateDirectory(defaultDir);
                Log?.Invoke($"[CategoryConfig] Created directory: {defaultDir}");
            }
        }

        /// <summary>
        /// Load default categories from Vehicles/Default/categories.json
        /// </summary>
        public static void LoadDefaults()
        {
            _defaultCategories = new Dictionary<string, CategoryData>();

            try
            {
                EnsureDirectories();

                if (File.Exists(DefaultPath))
                {
                    string json = File.ReadAllText(DefaultPath);
                    var config = JsonConvert.DeserializeObject<ConfigFile>(json);

                    if (config?.Categories != null)
                    {
                        _defaultCategories = config.Categories;
                        Log?.Invoke($"[CategoryConfig] Loaded {_defaultCategories.Count} default categories from {DefaultPath}");
                    }
                }
                else
                {
                    // Create default config file with hardcoded defaults
                    CreateDefaultConfig();
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[CategoryConfig] Error loading defaults: {ex.Message}");
                CreateDefaultsInMemory();
            }

            _defaultsLoaded = true;
        }

        /// <summary>
        /// Load vehicle-specific categories (if they exist)
        /// </summary>
        public static void LoadVehicleOverrides(string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName)) return;
            if (_loadedVehicle == vehicleName && _vehicleCategories != null) return; // Already loaded

            _vehicleCategories = new Dictionary<string, CategoryData>();
            _loadedVehicle = vehicleName;

            try
            {
                string vehiclePath = GetVehiclePath(vehicleName);
                if (File.Exists(vehiclePath))
                {
                    string json = File.ReadAllText(vehiclePath);
                    var config = JsonConvert.DeserializeObject<ConfigFile>(json);

                    if (config?.Categories != null)
                    {
                        _vehicleCategories = config.Categories;
                        Log?.Invoke($"[CategoryConfig] Loaded {_vehicleCategories.Count} overrides for {vehicleName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[CategoryConfig] Error loading vehicle overrides for {vehicleName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get description for a category
        /// Priority: Vehicle-specific > Default file > Hardcoded
        /// </summary>
        public static string GetDescription(string category)
        {
            if (!_defaultsLoaded) LoadDefaults();

            // 1. Check vehicle-specific override
            if (!string.IsNullOrEmpty(CurrentVehicle))
            {
                LoadVehicleOverrides(CurrentVehicle);
                if (_vehicleCategories != null &&
                    _vehicleCategories.TryGetValue(category, out var vehicleData) &&
                    !string.IsNullOrEmpty(vehicleData.Description))
                {
                    return vehicleData.Description;
                }
            }

            // 2. Check default file
            if (_defaultCategories.TryGetValue(category, out var defaultData) &&
                !string.IsNullOrEmpty(defaultData.Description))
            {
                return defaultData.Description;
            }

            // 3. Fall back to hardcoded defaults
            if (HardcodedDefaults.TryGetValue(category, out string hardcoded))
            {
                return hardcoded;
            }

            // Category not found
            Log?.Invoke($"[CategoryConfig] WARNING: No description found for category '{category}'");
            return $"Customize your {category.ToLower()}.";
        }

        /// <summary>
        /// Set description for a category (saves to Default folder)
        /// </summary>
        public static void SetDescription(string category, string description)
        {
            if (!_defaultsLoaded) LoadDefaults();

            if (_defaultCategories.ContainsKey(category))
            {
                _defaultCategories[category].Description = description;
            }
            else
            {
                _defaultCategories[category] = new CategoryData
                {
                    Description = description,
                    IsCustom = !HardcodedDefaults.ContainsKey(category)
                };
            }

            Log?.Invoke($"[CategoryConfig] Set description for '{category}': {description}");
            SaveDefaults();
        }

        /// <summary>
        /// Set description for a specific vehicle (creates vehicle folder if needed)
        /// </summary>
        public static void SetVehicleDescription(string vehicleName, string category, string description)
        {
            if (string.IsNullOrEmpty(vehicleName)) return;

            LoadVehicleOverrides(vehicleName);

            if (_vehicleCategories.ContainsKey(category))
            {
                _vehicleCategories[category].Description = description;
            }
            else
            {
                _vehicleCategories[category] = new CategoryData
                {
                    Description = description,
                    IsCustom = true
                };
            }

            Log?.Invoke($"[CategoryConfig] Set vehicle description for '{vehicleName}/{category}': {description}");
            SaveVehicleOverrides(vehicleName);
        }

        /// <summary>
        /// Save default categories to file
        /// </summary>
        public static void SaveDefaults()
        {
            try
            {
                EnsureDirectories();

                var config = new ConfigFile { Categories = _defaultCategories };
                string json = JsonConvert.SerializeObject(config, Formatting.Indented);

                File.WriteAllText(DefaultPath, json);
                Log?.Invoke($"[CategoryConfig] Saved defaults to {DefaultPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[CategoryConfig] Error saving defaults: {ex.Message}");
            }
        }

        /// <summary>
        /// Save vehicle-specific overrides to file
        /// </summary>
        public static void SaveVehicleOverrides(string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName) || _vehicleCategories == null) return;

            try
            {
                string vehiclePath = GetVehiclePath(vehicleName);
                string dir = Path.GetDirectoryName(vehiclePath);

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var config = new ConfigFile { Categories = _vehicleCategories };
                string json = JsonConvert.SerializeObject(config, Formatting.Indented);

                File.WriteAllText(vehiclePath, json);
                Log?.Invoke($"[CategoryConfig] Saved overrides for {vehicleName} to {vehiclePath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[CategoryConfig] Error saving vehicle overrides: {ex.Message}");
            }
        }

        /// <summary>
        /// Create default config file with hardcoded defaults
        /// </summary>
        private static void CreateDefaultConfig()
        {
            CreateDefaultsInMemory();
            SaveDefaults();
            Log?.Invoke($"[CategoryConfig] Created default config at {DefaultPath}");
        }

        /// <summary>
        /// Load hardcoded defaults into memory
        /// </summary>
        private static void CreateDefaultsInMemory()
        {
            _defaultCategories = new Dictionary<string, CategoryData>();
            foreach (var kvp in HardcodedDefaults)
            {
                _defaultCategories[kvp.Key] = new CategoryData
                {
                    Description = kvp.Value,
                    IsCustom = false
                };
            }
        }

        /// <summary>
        /// Reload all configs
        /// </summary>
        public static void Reload()
        {
            _defaultsLoaded = false;
            _loadedVehicle = null;
            _vehicleCategories = null;
            LoadDefaults();
        }

        /// <summary>
        /// Add a custom category (to defaults)
        /// </summary>
        public static void AddCustomCategory(string name, string description, List<int> modIndices)
        {
            if (!_defaultsLoaded) LoadDefaults();

            _defaultCategories[name] = new CategoryData
            {
                Description = description,
                IsCustom = true,
                ModIndices = modIndices
            };

            Log?.Invoke($"[CategoryConfig] Added custom category '{name}' with {modIndices?.Count ?? 0} mod indices");
            SaveDefaults();
        }

        /// <summary>
        /// Get all custom categories
        /// </summary>
        public static Dictionary<string, CategoryData> GetCustomCategories()
        {
            if (!_defaultsLoaded) LoadDefaults();

            var custom = new Dictionary<string, CategoryData>();
            foreach (var kvp in _defaultCategories)
            {
                if (kvp.Value.IsCustom)
                {
                    custom[kvp.Key] = kvp.Value;
                }
            }
            return custom;
        }

        // Keep old methods for compatibility
        public static void Load() => LoadDefaults();
        public static void Save() => SaveDefaults();
    }
}
