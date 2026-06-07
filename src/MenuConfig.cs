using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles folder-based menu configuration.
    ///
    /// Structure:
    ///   ExtendedLSC/Vehicles/Default/     <- Default configs
    ///   ExtendedLSC/Vehicles/{Vehicle}/   <- Vehicle-specific overrides
    ///
    /// Each folder is a category. Folders can contain:
    ///   - Sub-folders (sub-categories)
    ///   - items.json (menu items and category description)
    ///   - Both (mixed)
    ///
    /// items.json format:
    /// {
    ///   "description": "Category description shown in menu",
    ///   "items": [
    ///     { "name": "Item Name", "description": "Optional", "price": 500, "value": 0 },
    ///     ...
    ///   ]
    /// }
    /// </summary>
    public static class MenuConfig
    {
        private static string BasePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ExtendedLSC",
            "Vehicles"
        );

        private static string DefaultPath => Path.Combine(BasePath, "Default");

        public static string CurrentVehicle { get; set; }

        public static Action<string> Log { get; set; }

        #region Data Classes

        /// <summary>
        /// Represents a menu item
        /// </summary>
        public class MenuItem
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public int Price { get; set; }
            public int Value { get; set; }
            public string Type { get; set; }  // Optional: "toggle", "color", etc.

            // For color items
            public int? R { get; set; }
            public int? G { get; set; }
            public int? B { get; set; }
        }

        /// <summary>
        /// Represents items.json content
        /// </summary>
        public class ItemsFile
        {
            public string MenuTitle { get; set; }  // Optional: overrides folder name for submenu title
            public string Description { get; set; }
            public List<MenuItem> Items { get; set; }
        }

        /// <summary>
        /// Represents a category (folder) with its contents
        /// </summary>
        public class Category
        {
            public string Name { get; set; }        // Folder name
            public string MenuTitle { get; set; }   // Display name for submenu (uses Name if not set)
            public string Description { get; set; }
            public string Path { get; set; }
            public List<Category> SubCategories { get; set; } = new List<Category>();
            public List<MenuItem> Items { get; set; } = new List<MenuItem>();
            public bool HasSubCategories => SubCategories.Count > 0;
            public bool HasItems => Items.Count > 0;

            /// <summary>
            /// Gets the display name for the submenu (MenuTitle if set, otherwise Name)
            /// </summary>
            public string DisplayName => !string.IsNullOrEmpty(MenuTitle) ? MenuTitle : Name;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize the folder structure with defaults if needed
        /// </summary>
        public static void Initialize()
        {
            EnsureDefaultStructure();
        }

        /// <summary>
        /// Load a category from the folder structure
        /// Checks vehicle-specific first, then falls back to Default
        /// </summary>
        public static Category LoadCategory(string categoryPath)
        {
            // Try vehicle-specific first
            if (!string.IsNullOrEmpty(CurrentVehicle))
            {
                string vehiclePath = Path.Combine(BasePath, CurrentVehicle, categoryPath);
                if (Directory.Exists(vehiclePath))
                {
                    var category = LoadCategoryFromPath(vehiclePath, categoryPath);
                    if (category != null)
                    {
                        Log?.Invoke($"[MenuConfig] Loaded vehicle-specific category: {categoryPath}");
                        return category;
                    }
                }
            }

            // Fall back to Default
            string defaultPath = Path.Combine(DefaultPath, categoryPath);
            if (Directory.Exists(defaultPath))
            {
                return LoadCategoryFromPath(defaultPath, categoryPath);
            }

            Log?.Invoke($"[MenuConfig] Category not found: {categoryPath}");
            return null;
        }

        /// <summary>
        /// Get category description (returns null if none set)
        /// </summary>
        public static string GetDescription(string categoryPath)
        {
            var category = LoadCategory(categoryPath);
            return category?.Description;
        }

        /// <summary>
        /// Get items for a category
        /// </summary>
        public static List<MenuItem> GetItems(string categoryPath)
        {
            var category = LoadCategory(categoryPath);
            return category?.Items ?? new List<MenuItem>();
        }

        /// <summary>
        /// Get sub-categories for a category
        /// </summary>
        public static List<Category> GetSubCategories(string categoryPath)
        {
            var category = LoadCategory(categoryPath);
            return category?.SubCategories ?? new List<Category>();
        }

        /// <summary>
        /// Get all root-level categories from the Default folder.
        /// This loads the entire folder tree for dynamic menu building.
        /// </summary>
        public static List<Category> GetRootCategories()
        {
            var categories = new List<Category>();

            if (!Directory.Exists(DefaultPath))
            {
                return categories;
            }

            foreach (string dir in Directory.GetDirectories(DefaultPath))
            {
                string folderName = Path.GetFileName(dir);
                var category = LoadCategoryFromPath(dir, folderName);
                if (category != null)
                {
                    categories.Add(category);
                }
            }

            // Sort alphabetically by display name
            categories.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            return categories;
        }

        /// <summary>
        /// Save/update a category's items.json
        /// </summary>
        public static void SaveCategory(string categoryPath, string description, List<MenuItem> items, bool vehicleSpecific = false)
        {
            string basePath = vehicleSpecific && !string.IsNullOrEmpty(CurrentVehicle)
                ? Path.Combine(BasePath, CurrentVehicle)
                : DefaultPath;

            string fullPath = Path.Combine(basePath, categoryPath);

            try
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }

                var itemsFile = new ItemsFile
                {
                    Description = description,
                    Items = items
                };

                string json = JsonConvert.SerializeObject(itemsFile, Formatting.Indented);
                File.WriteAllText(Path.Combine(fullPath, "items.json"), json);

                Log?.Invoke($"[MenuConfig] Saved category: {categoryPath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[MenuConfig] Error saving category {categoryPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Update just the description for a category
        /// </summary>
        public static void SetDescription(string categoryPath, string description, bool vehicleSpecific = false)
        {
            var category = LoadCategory(categoryPath);
            SaveCategory(categoryPath, description, category?.Items ?? new List<MenuItem>(), vehicleSpecific);
        }

        #endregion

        #region Private Methods

        private static Category LoadCategoryFromPath(string fullPath, string categoryPath)
        {
            try
            {
                var category = new Category
                {
                    Name = Path.GetFileName(fullPath),
                    Path = categoryPath
                };

                // Load items.json if it exists, or create default one
                string itemsPath = Path.Combine(fullPath, "items.json");
                if (File.Exists(itemsPath))
                {
                    string json = File.ReadAllText(itemsPath);
                    var itemsFile = JsonConvert.DeserializeObject<ItemsFile>(json);

                    if (itemsFile != null)
                    {
                        category.MenuTitle = itemsFile.MenuTitle;
                        category.Description = itemsFile.Description;
                        category.Items = itemsFile.Items ?? new List<MenuItem>();
                    }
                }
                else
                {
                    // Auto-create items.json with defaults for user-created folders
                    category.MenuTitle = category.Name;
                    var defaultFile = new ItemsFile
                    {
                        MenuTitle = category.Name,
                        Description = null,
                        Items = null
                    };
                    try
                    {
                        string json = JsonConvert.SerializeObject(defaultFile, Formatting.Indented);
                        File.WriteAllText(itemsPath, json);
                        Log?.Invoke($"[MenuConfig] Auto-created items.json for: {categoryPath}");
                    }
                    catch { /* Ignore write errors */ }
                }

                // Load sub-categories (sub-folders)
                foreach (string subDir in Directory.GetDirectories(fullPath))
                {
                    string subName = Path.GetFileName(subDir);
                    string subPath = string.IsNullOrEmpty(categoryPath)
                        ? subName
                        : Path.Combine(categoryPath, subName);

                    var subCategory = LoadCategoryFromPath(subDir, subPath);
                    if (subCategory != null)
                    {
                        category.SubCategories.Add(subCategory);
                    }
                }

                return category;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[MenuConfig] Error loading category from {fullPath}: {ex.Message}");
                return null;
            }
        }

        private static void EnsureDefaultStructure()
        {
            if (!Directory.Exists(DefaultPath))
            {
                Directory.CreateDirectory(DefaultPath);
                Log?.Invoke($"[MenuConfig] Created default folder: {DefaultPath}");
            }

            // Create default categories with items.json files
            CreateDefaultCategories();
        }

        private static void CreateDefaultCategories()
        {
            // Only create if the Default folder is empty
            if (Directory.GetDirectories(DefaultPath).Length > 0)
            {
                return; // Already has content
            }

            Log?.Invoke("[MenuConfig] Creating default category structure...");

            // Armor
            CreateCategory("Armor", "Protect your car's occupants with military spec composite body panels.", new List<MenuItem>
            {
                new MenuItem { Name = "None", Price = 0, Value = -1 },
                new MenuItem { Name = "Armor Upgrade 20%", Price = 2500, Value = 0 },
                new MenuItem { Name = "Armor Upgrade 40%", Price = 5000, Value = 1 },
                new MenuItem { Name = "Armor Upgrade 60%", Price = 10000, Value = 2 },
                new MenuItem { Name = "Armor Upgrade 80%", Price = 17500, Value = 3 },
                new MenuItem { Name = "Armor Upgrade 100%", Price = 25000, Value = 4 }
            });

            // Brakes
            CreateCategory("Brakes", "Increase stopping power and eliminate brake fade.", new List<MenuItem>
            {
                new MenuItem { Name = "Stock Brakes", Price = 0, Value = -1 },
                new MenuItem { Name = "Street Brakes", Price = 2000, Value = 0 },
                new MenuItem { Name = "Sport Brakes", Price = 5500, Value = 1 },
                new MenuItem { Name = "Race Brakes", Price = 12000, Value = 2 }
            });

            // Engine
            CreateCategory("Engine", "Increase brake horsepower.", new List<MenuItem>
            {
                new MenuItem { Name = "Stock Engine", Price = 0, Value = -1 },
                new MenuItem { Name = "EMS Upgrade, Level 1", Price = 2500, Value = 0 },
                new MenuItem { Name = "EMS Upgrade, Level 2", Price = 5000, Value = 1 },
                new MenuItem { Name = "EMS Upgrade, Level 3", Price = 9000, Value = 2 },
                new MenuItem { Name = "EMS Upgrade, Level 4", Price = 13500, Value = 3 }
            });

            // Transmission
            CreateCategory("Transmission", "Improved acceleration with close ratio transmission.", new List<MenuItem>
            {
                new MenuItem { Name = "Stock Transmission", Price = 0, Value = -1 },
                new MenuItem { Name = "Street Transmission", Price = 5000, Value = 0 },
                new MenuItem { Name = "Sports Transmission", Price = 8000, Value = 1 },
                new MenuItem { Name = "Race Transmission", Price = 15000, Value = 2 }
            });

            // Suspension
            CreateCategory("Suspension", "Upgrade to a sports oriented suspension setup.", new List<MenuItem>
            {
                new MenuItem { Name = "Stock Suspension", Price = 0, Value = -1 },
                new MenuItem { Name = "Lowered Suspension", Price = 1000, Value = 0 },
                new MenuItem { Name = "Street Suspension", Price = 2000, Value = 1 },
                new MenuItem { Name = "Sport Suspension", Price = 3400, Value = 2 },
                new MenuItem { Name = "Competition Suspension", Price = 6000, Value = 3 }
            });

            // Turbo
            CreateCategory("Turbo", "Reduce lag turbocharger.", new List<MenuItem>
            {
                new MenuItem { Name = "None", Price = 0, Value = 0, Type = "toggle" },
                new MenuItem { Name = "Turbo Tuning", Price = 12500, Value = 1, Type = "toggle" }
            });

            // Windows (submenu shows as "TINTS")
            CreateCategory("Windows", "A selection of tinted windows.", new List<MenuItem>
            {
                new MenuItem { Name = "None", Price = 0, Value = 0 },
                new MenuItem { Name = "Light Smoke", Price = 500, Value = 3 },
                new MenuItem { Name = "Dark Smoke", Price = 500, Value = 2 },
                new MenuItem { Name = "Limo", Price = 500, Value = 5 }
            }, menuTitle: "TINTS");

            // Lights (with sub-categories)
            CreateCategoryFolder("Lights", "Improve night time visibility and decorative lighting.");

            CreateCategory("Lights/Headlights", "Choose between stock and Xenon headlights.", new List<MenuItem>
            {
                new MenuItem { Name = "Stock Lights", Price = 0, Value = 0, Type = "toggle" },
                new MenuItem { Name = "Xenon Lights", Price = 3000, Value = 1, Type = "toggle" }
            });

            CreateCategoryFolder("Lights/Neon Kits", "Add underglow neon lighting to your vehicle.");

            CreateCategory("Lights/Neon Kits/Neon Layout", "Choose which sides have neon lights.", new List<MenuItem>
            {
                new MenuItem { Name = "None", Price = 0, Value = 0 },
                new MenuItem { Name = "Front", Price = 500, Value = 1 },
                new MenuItem { Name = "Back", Price = 500, Value = 2 },
                new MenuItem { Name = "Left Side", Price = 500, Value = 3 },
                new MenuItem { Name = "Right Side", Price = 500, Value = 4 },
                new MenuItem { Name = "Front and Back", Price = 800, Value = 5 },
                new MenuItem { Name = "All Sides", Price = 1500, Value = 6 }
            });

            CreateCategory("Lights/Neon Kits/Neon Color", "Select your neon light color.", new List<MenuItem>
            {
                new MenuItem { Name = "White", Price = 200, R = 255, G = 255, B = 255, Type = "color" },
                new MenuItem { Name = "Blue", Price = 200, R = 0, G = 0, B = 255, Type = "color" },
                new MenuItem { Name = "Electric Blue", Price = 200, R = 0, G = 150, B = 255, Type = "color" },
                new MenuItem { Name = "Mint Green", Price = 200, R = 50, G = 255, B = 155, Type = "color" },
                new MenuItem { Name = "Lime Green", Price = 200, R = 0, G = 255, B = 0, Type = "color" },
                new MenuItem { Name = "Yellow", Price = 200, R = 255, G = 255, B = 0, Type = "color" },
                new MenuItem { Name = "Golden Shower", Price = 200, R = 255, G = 190, B = 0, Type = "color" },
                new MenuItem { Name = "Orange", Price = 200, R = 255, G = 128, B = 0, Type = "color" },
                new MenuItem { Name = "Red", Price = 200, R = 255, G = 0, B = 0, Type = "color" },
                new MenuItem { Name = "Pony Pink", Price = 200, R = 255, G = 50, B = 100, Type = "color" },
                new MenuItem { Name = "Hot Pink", Price = 200, R = 255, G = 0, B = 255, Type = "color" },
                new MenuItem { Name = "Purple", Price = 200, R = 128, G = 0, B = 255, Type = "color" }
            });

            // Bumpers (with sub-categories)
            CreateCategoryFolder("Bumpers", "Customize front and rear bumpers.");
            CreateCategory("Bumpers/Front Bumper", "Replace your front bumper with custom designs.", null);
            CreateCategory("Bumpers/Rear Bumper", "Replace your rear bumper with custom designs.", null);

            // Wheels (with sub-categories)
            CreateCategoryFolder("Wheels", "Custom rim, tires, and colors.", menuTitle: "WHEELS");
            CreateCategoryFolder("Wheels/Wheel Type", "Select your choice of wheel rims and color then confirm when ready.");
            CreateCategory("Wheels/Wheel Color", "Custom wheel colors", null);

            CreateCategoryFolder("Wheels/Tires", "Bulletproof tires and custom burnout smoke.");
            CreateCategory("Wheels/Tires/Tire Design", "Choose custom tire lettering and designs.", null);
            CreateCategory("Wheels/Tires/Tire Enhancements", "Add bulletproof or other tire upgrades.", new List<MenuItem>
            {
                new MenuItem { Name = "Standard Tires", Price = 0, Value = 0 },
                new MenuItem { Name = "Bulletproof Tires", Price = 2500, Value = 1 }
            });
            CreateCategory("Wheels/Tires/Tire Smoke", "Choose the color of your tire smoke.", new List<MenuItem>
            {
                new MenuItem { Name = "White", Price = 500, R = 255, G = 255, B = 255, Type = "color" },
                new MenuItem { Name = "Black", Price = 500, R = 20, G = 20, B = 20, Type = "color" },
                new MenuItem { Name = "Blue", Price = 500, R = 0, G = 0, B = 255, Type = "color" },
                new MenuItem { Name = "Yellow", Price = 500, R = 255, G = 255, B = 0, Type = "color" },
                new MenuItem { Name = "Purple", Price = 500, R = 128, G = 0, B = 255, Type = "color" },
                new MenuItem { Name = "Orange", Price = 500, R = 255, G = 128, B = 0, Type = "color" },
                new MenuItem { Name = "Green", Price = 500, R = 0, G = 255, B = 0, Type = "color" },
                new MenuItem { Name = "Red", Price = 500, R = 255, G = 0, B = 0, Type = "color" },
                new MenuItem { Name = "Pink", Price = 500, R = 255, G = 0, B = 255, Type = "color" },
                new MenuItem { Name = "Brown", Price = 500, R = 139, G = 69, B = 19, Type = "color" }
            });

            // Respray
            CreateCategoryFolder("Respray", "Transforms vehicle appearance.");
            CreateCategory("Respray/Primary Color", "Choose your primary paint color.", null);
            CreateCategory("Respray/Secondary Color", "Choose your secondary paint color.", null);
            CreateCategory("Respray/Pearlescent", "Add a pearlescent finish.", null);

            // Other categories with just descriptions (items loaded dynamically from vehicle)
            CreateCategory("Exhaust", "Customized sports exhausts.", null);
            CreateCategory("Grille", "Improved engine cooling", null);
            CreateCategory("Hood", "Enhance car engine cooling.", null);
            CreateCategory("Spoiler", "Increase downforce.", null);
            CreateCategory("Skirts", "Enhance your vehicles look with custom side skirts.", null);
            CreateCategory("Roll Cage", "Stiffen your chassis with a roll cage.", null);
            CreateCategory("Roof", "Lower your center of gravity with lightweight roof panels.", null);
            CreateCategoryFolder("Fenders", "Replace your fenders with custom aftermarket designs.");
            CreateCategory("Fenders/Left Fender", "Replace your left fender.", null);
            CreateCategory("Fenders/Right Fender", "Replace your right fender.", null);
            CreateCategory("Horn", "Custom air horns", null);
            CreateCategory("Plate", "Customize license plate.", null);
            CreateCategory("Livery", "Apply a custom decal or livery to your vehicle.", null);

            Log?.Invoke("[MenuConfig] Default category structure created.");
        }

        private static void CreateCategory(string path, string description, List<MenuItem> items, string menuTitle = null)
        {
            string fullPath = Path.Combine(DefaultPath, path);

            try
            {
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }

                // Default MenuTitle to folder name if not specified
                string folderName = Path.GetFileName(path);
                var itemsFile = new ItemsFile
                {
                    MenuTitle = menuTitle ?? folderName,
                    Description = description,
                    Items = items
                };

                string json = JsonConvert.SerializeObject(itemsFile, Formatting.Indented);
                File.WriteAllText(Path.Combine(fullPath, "items.json"), json);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[MenuConfig] Error creating category {path}: {ex.Message}");
            }
        }

        private static void CreateCategoryFolder(string path, string description, string menuTitle = null)
        {
            // Create folder with just a description (items.json with no items)
            CreateCategory(path, description, null, menuTitle);
        }

        #endregion
    }
}
