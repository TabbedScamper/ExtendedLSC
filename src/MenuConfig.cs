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

        /// <summary>
        /// Pick the per-vehicle config folder for the active vehicle. Modders ship a folder named by the
        /// MODEL (e.g. "blazer4") — stable + language-independent — OR by the in-game display name. We resolve
        /// CurrentVehicle to whichever folder actually exists so a dropped-in config "just works".
        /// Priority: exact display-name folder, then a folder whose joaat() matches the model hash, else display name.
        /// </summary>
        public static void SetCurrentVehicle(string displayName, uint modelHash)
        {
            CurrentVehicle = ResolveVehicleFolder(displayName, modelHash);
        }

        private static string ResolveVehicleFolder(string displayName, uint modelHash)
        {
            try
            {
                if (Directory.Exists(BasePath))
                {
                    // 1) exact display-name folder wins (back-compat with existing configs)
                    if (!string.IsNullOrEmpty(displayName) && Directory.Exists(Path.Combine(BasePath, displayName)))
                        return displayName;
                    // 2) otherwise match a folder by model-name hash (e.g. folder "blazer4" -> joaat == model hash)
                    foreach (string dir in Directory.GetDirectories(BasePath))
                    {
                        string name = Path.GetFileName(dir);
                        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Universal", StringComparison.OrdinalIgnoreCase)) continue;
                        if (Joaat(name) == modelHash) return name;
                    }
                }
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] ResolveVehicleFolder error: {ex.Message}"); }
            return displayName;
        }

        // Jenkins one-at-a-time hash (GTA model-name hash), computed on the lowercased name.
        private static uint Joaat(string text)
        {
            uint hash = 0;
            foreach (char c in text.ToLowerInvariant())
            {
                hash += c;
                hash += hash << 10;
                hash ^= hash >> 6;
            }
            hash += hash << 3;
            hash ^= hash >> 11;
            hash += hash << 15;
            return hash;
        }

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

            // Source slot for re-shelved parts (the ELSC edit-mode feature): the game VehicleModType this part
            // actually lives in. -1 = use the category's native slot (default behaviour). When set, this item
            // applies (SourceModType, Value) regardless of which custom category it's displayed under — that's
            // what lets a custom category mix parts pulled from different game slots (e.g. a fender flare that
            // the game filed under Side Skirts).
            public int SourceModType { get; set; } = -1;

            // Wheel items (universal wheel categories): the GTA wheel TYPE this rim belongs to (-1 = not a wheel).
            // When set, the item is a rim applied via SET_VEHICLE_WHEEL_TYPE(WheelType) + SET_VEHICLE_MOD(23/24, Value).
            public int WheelType { get; set; } = -1;

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

        /// <summary>Per-vehicle edit-mode config (_config.json): which whole default categories are hidden.
        /// (Hidden PARTS are not stored here — they're derived from what the custom categories contain, so
        /// the two can never get out of sync.)</summary>
        public class VehicleConfig
        {
            public List<string> HiddenCategories { get; set; } = new List<string>();
            // Per-vehicle display-name overrides for BUILT-IN categories (key = original title, e.g. "Skirts").
            public Dictionary<string, string> CategoryRenames { get; set; } = new Dictionary<string, string>();
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

            // Also include THIS vehicle's own custom categories (created in edit mode) that aren't in Default.
            if (!string.IsNullOrEmpty(CurrentVehicle))
            {
                string vehRoot = Path.Combine(BasePath, CurrentVehicle);
                if (Directory.Exists(vehRoot))
                {
                    foreach (string dir in Directory.GetDirectories(vehRoot))
                    {
                        string folderName = Path.GetFileName(dir);
                        if (categories.Exists(c => string.Equals(c.Name, folderName, StringComparison.OrdinalIgnoreCase))) continue;
                        var vc = LoadCategoryFromPath(dir, folderName);
                        if (vc != null) categories.Add(vc);
                    }
                }
            }

            // Sort alphabetically by display name
            categories.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            return categories;
        }

        /// <summary>Create a new per-vehicle custom category folder under <paramref name="parentPath"/> (""=root,
        /// or e.g. "Wheels/Wheel Type"). False if the name is invalid or the category already exists.</summary>
        public static bool CreateVehicleCategory(string parentPath, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(CurrentVehicle)) return false;
            name = name.Trim();
            string rel = string.IsNullOrEmpty(parentPath) ? name : Path.Combine(parentPath, name);
            string path = Path.Combine(BasePath, CurrentVehicle, rel);
            try
            {
                if (Directory.Exists(path)) return false;
                Directory.CreateDirectory(path);
                var f = new ItemsFile { MenuTitle = name, Description = null, Items = new List<MenuItem>() };
                File.WriteAllText(Path.Combine(path, "items.json"), JsonConvert.SerializeObject(f, Formatting.Indented));
                Log?.Invoke($"[MenuConfig] Created vehicle category: {CurrentVehicle}/{rel}");
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] CreateVehicleCategory error: {ex.Message}"); return false; }
        }

        /// <summary>This vehicle's custom sub-categories (folders) directly under <paramref name="parentPath"/>
        /// (""=root). Used to render modder categories inside any menu, including built-in submenus.</summary>
        public static List<Category> GetVehicleSubCategories(string parentPath)
        {
            var list = new List<Category>();
            if (string.IsNullOrEmpty(CurrentVehicle)) return list;
            string dir = Path.Combine(BasePath, CurrentVehicle, parentPath ?? "");
            if (!Directory.Exists(dir)) return list;
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                string rel = string.IsNullOrEmpty(parentPath) ? name : Path.Combine(parentPath, name);
                var cat = LoadCategoryFromPath(sub, rel);
                if (cat != null) list.Add(cat);
            }
            return list;
        }

        /// <summary>Rename a per-vehicle custom category (its display title; keeps the folder). Returns false if the
        /// category isn't a per-vehicle custom one.</summary>
        public static bool RenameVehicleCategory(string categoryPath, string newName)
        {
            if (string.IsNullOrEmpty(CurrentVehicle) || string.IsNullOrWhiteSpace(categoryPath) || string.IsNullOrWhiteSpace(newName)) return false;
            string dir = Path.Combine(BasePath, CurrentVehicle, categoryPath);
            if (!Directory.Exists(dir)) return false;
            try
            {
                string itemsPath = Path.Combine(dir, "items.json");
                ItemsFile f = File.Exists(itemsPath) ? JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(itemsPath)) : null;
                if (f == null) f = new ItemsFile();
                f.MenuTitle = newName.Trim();
                File.WriteAllText(itemsPath, JsonConvert.SerializeObject(f, Formatting.Indented));
                Log?.Invoke($"[MenuConfig] Renamed category {categoryPath} -> {newName}");
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] RenameVehicleCategory error: {ex.Message}"); return false; }
        }

        /// <summary>Set the per-vehicle description for a category path (preserves the category's title + items).</summary>
        public static void SetVehicleDescription(string categoryPath, string description)
        {
            if (string.IsNullOrEmpty(CurrentVehicle) || string.IsNullOrWhiteSpace(categoryPath)) return;
            string dir = Path.Combine(BasePath, CurrentVehicle, categoryPath);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string itemsPath = Path.Combine(dir, "items.json");
                ItemsFile f = File.Exists(itemsPath) ? JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(itemsPath)) : null;
                if (f == null) f = new ItemsFile { MenuTitle = Path.GetFileName(categoryPath) };
                f.Description = description;
                File.WriteAllText(itemsPath, JsonConvert.SerializeObject(f, Formatting.Indented));
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] SetVehicleDescription error: {ex.Message}"); }
        }

        /// <summary>Append a part (MenuItem, typically with a SourceModType set) to a per-vehicle custom
        /// category's items.json, creating the file/folder if needed.</summary>
        public static bool AddItemToVehicleCategory(string categoryName, MenuItem item)
        {
            if (string.IsNullOrEmpty(CurrentVehicle) || string.IsNullOrWhiteSpace(categoryName) || item == null) return false;
            string dir = Path.Combine(BasePath, CurrentVehicle, categoryName);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string itemsPath = Path.Combine(dir, "items.json");
                ItemsFile f = null;
                if (File.Exists(itemsPath)) f = JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(itemsPath));
                if (f == null) f = new ItemsFile { MenuTitle = categoryName };
                if (f.Items == null) f.Items = new List<MenuItem>();
                f.Items.Add(item);
                File.WriteAllText(itemsPath, JsonConvert.SerializeObject(f, Formatting.Indented));
                InvalidateReshelvedCache();   // this part is now re-shelved -> hide it from its default category
                Log?.Invoke($"[MenuConfig] Added part to {CurrentVehicle}/{categoryName}: {item.Name}");
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] AddItemToVehicleCategory error: {ex.Message}"); return false; }
        }

        private static bool UpdateItem(string itemsPath, int index, string name, int price)
        {
            try
            {
                if (!File.Exists(itemsPath)) return false;
                var f = JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(itemsPath));
                if (f?.Items == null || index < 0 || index >= f.Items.Count) return false;
                if (!string.IsNullOrWhiteSpace(name)) f.Items[index].Name = name;
                f.Items[index].Price = price;
                File.WriteAllText(itemsPath, JsonConvert.SerializeObject(f, Formatting.Indented));
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] UpdateItem error: {ex.Message}"); return false; }
        }

        /// <summary>Edit a part's name/price in a per-vehicle custom category (edit mode).</summary>
        public static bool UpdateVehicleCategoryItem(string categoryPath, int index, string name, int price)
        {
            if (string.IsNullOrEmpty(CurrentVehicle) || string.IsNullOrWhiteSpace(categoryPath)) return false;
            return UpdateItem(Path.Combine(BasePath, CurrentVehicle, categoryPath, "items.json"), index, name, price);
        }

        /// <summary>Edit a rim's name/price in a universal wheel category (edit mode).</summary>
        public static bool UpdateUniversalWheelItem(string categoryName, int index, string name, int price)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return false;
            bool ok = UpdateItem(Path.Combine(UniversalWheelsBase, categoryName, "items.json"), index, name, price);
            if (ok) InvalidateUniversalWheelCache();
            return ok;
        }

        // ---- Per-vehicle edit-mode config (hidden parts / categories) ----
        private static VehicleConfig _cfgCache;
        private static string _cfgCacheVehicle;

        public static VehicleConfig GetVehicleConfig()
        {
            if (string.IsNullOrEmpty(CurrentVehicle)) return new VehicleConfig();
            if (_cfgCache != null && _cfgCacheVehicle == CurrentVehicle) return _cfgCache;
            var cfg = new VehicleConfig();
            try
            {
                string p = Path.Combine(BasePath, CurrentVehicle, "_config.json");
                if (File.Exists(p)) cfg = JsonConvert.DeserializeObject<VehicleConfig>(File.ReadAllText(p)) ?? new VehicleConfig();
            }
            catch { }
            _cfgCache = cfg; _cfgCacheVehicle = CurrentVehicle;
            return cfg;
        }

        private static void SaveVehicleConfig(VehicleConfig cfg)
        {
            if (string.IsNullOrEmpty(CurrentVehicle)) return;
            try
            {
                string dir = Path.Combine(BasePath, CurrentVehicle);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "_config.json"), JsonConvert.SerializeObject(cfg, Formatting.Indented));
                _cfgCache = cfg; _cfgCacheVehicle = CurrentVehicle;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] SaveVehicleConfig error: {ex.Message}"); }
        }

        // The set of (slot,value) parts that live in THIS vehicle's custom categories — derived (and cached) so a
        // part shown in a custom category is automatically hidden from its default category. Key = slot<<20 | value.
        private static HashSet<long> _reshelvedCache;
        private static string _reshelvedVehicle;
        private static long PartKey(int slot, int value) => ((long)slot << 20) | (uint)value;

        public static HashSet<long> GetReshelvedParts()
        {
            if (string.IsNullOrEmpty(CurrentVehicle)) return new HashSet<long>();
            if (_reshelvedCache != null && _reshelvedVehicle == CurrentVehicle) return _reshelvedCache;
            var set = new HashSet<long>();
            try
            {
                string vroot = Path.Combine(BasePath, CurrentVehicle);
                if (Directory.Exists(vroot))
                {
                    // Recurse to ANY depth — nested categories (e.g. Bodies/Body 03/Front Fenders) re-shelve
                    // their parts too, so their default slots must hide just like top-level categories.
                    foreach (string itemsPath in Directory.GetFiles(vroot, "items.json", SearchOption.AllDirectories))
                    {
                        var f = JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(itemsPath));
                        if (f?.Items == null) continue;
                        foreach (var it in f.Items)
                            if (it.SourceModType >= 0 && it.Value >= 0) set.Add(PartKey(it.SourceModType, it.Value));
                    }
                }
            }
            catch { }
            _reshelvedCache = set; _reshelvedVehicle = CurrentVehicle;
            return set;
        }

        public static void InvalidateReshelvedCache() { _reshelvedCache = null; _reshelvedVehicle = null; }

        /// <summary>Is this (slot, value) part re-shelved into a custom category (so hide it from its default)?</summary>
        public static bool IsPartHidden(int slot, int value) => GetReshelvedParts().Contains(PartKey(slot, value));

        /// <summary>Per-vehicle display-name override for a BUILT-IN category, or null if none.</summary>
        public static string GetCategoryRename(string original)
        {
            if (string.IsNullOrEmpty(original)) return null;
            var cfg = GetVehicleConfig();
            return (cfg.CategoryRenames != null && cfg.CategoryRenames.TryGetValue(original, out var n) && !string.IsNullOrWhiteSpace(n)) ? n : null;
        }

        public static void SetCategoryRename(string original, string newName)
        {
            if (string.IsNullOrWhiteSpace(original)) return;
            var cfg = GetVehicleConfig();
            if (cfg.CategoryRenames == null) cfg.CategoryRenames = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(newName)) cfg.CategoryRenames.Remove(original);
            else cfg.CategoryRenames[original] = newName.Trim();
            SaveVehicleConfig(cfg);
        }

        /// <summary>Is a built-in default category hidden for the current vehicle (via _config.json)?</summary>
        public static bool IsCategoryHidden(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            var cfg = GetVehicleConfig();
            return cfg.HiddenCategories != null && cfg.HiddenCategories.Contains(title);
        }

        /// <summary>Hide or show a built-in default category for the current vehicle (persists to _config.json).</summary>
        public static void SetCategoryHidden(string title, bool hidden)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(CurrentVehicle)) return;
            var cfg = GetVehicleConfig();
            if (cfg.HiddenCategories == null) cfg.HiddenCategories = new List<string>();
            bool has = cfg.HiddenCategories.Contains(title);
            if (hidden && !has) cfg.HiddenCategories.Add(title);
            else if (!hidden && has) cfg.HiddenCategories.Remove(title);
            else return;
            SaveVehicleConfig(cfg);
        }

        /// <summary>Is <paramref name="name"/> a custom per-vehicle category (a folder under Vehicles/{model}/)?</summary>
        public static bool IsVehicleCategory(string name)
        {
            if (string.IsNullOrEmpty(CurrentVehicle) || string.IsNullOrWhiteSpace(name)) return false;
            return Directory.Exists(Path.Combine(BasePath, CurrentVehicle, name));
        }

        /// <summary>Delete a custom category and RESTORE its parts to their default slots (un-hides every part
        /// that was re-shelved into it, then removes the folder).</summary>
        public static bool DeleteVehicleCategory(string name)
        {
            if (!IsVehicleCategory(name)) return false;
            string dir = Path.Combine(BasePath, CurrentVehicle, name);
            try
            {
                // Removing the folder removes its items -> those parts are no longer re-shelved, so they
                // automatically reappear in their default categories (just invalidate the derived cache).
                Directory.Delete(dir, true);
                InvalidateReshelvedCache();
                Log?.Invoke($"[MenuConfig] Deleted vehicle category + restored parts: {CurrentVehicle}/{name}");
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] DeleteVehicleCategory error: {ex.Message}"); return false; }
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

        // ---- Universal wheel categories (NOT per-vehicle — wheels are global). Live at ExtendedLSC/Universal/Wheels/
        // so a wheel pack can ship that one folder and it applies to every car. ----
        private static string UniversalWheelsBase => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "Universal", "Wheels");

        public static List<Category> GetUniversalWheelCategories()
        {
            var list = new List<Category>();
            if (!Directory.Exists(UniversalWheelsBase)) return list;
            foreach (string dir in Directory.GetDirectories(UniversalWheelsBase))
            {
                var cat = LoadCategoryFromPath(dir, Path.GetFileName(dir));
                if (cat != null) list.Add(cat);
            }
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static bool IsUniversalWheelCategory(string name) =>
            !string.IsNullOrWhiteSpace(name) && Directory.Exists(Path.Combine(UniversalWheelsBase, name));

        public static bool CreateUniversalWheelCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            string path = Path.Combine(UniversalWheelsBase, name);
            try
            {
                if (Directory.Exists(path)) return false;
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "items.json"),
                    JsonConvert.SerializeObject(new ItemsFile { MenuTitle = name, Items = new List<MenuItem>() }, Formatting.Indented));
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] CreateUniversalWheelCategory error: {ex.Message}"); return false; }
        }

        public static bool AddWheelToUniversalCategory(string categoryName, MenuItem item)
        {
            if (string.IsNullOrWhiteSpace(categoryName) || item == null) return false;
            string dir = Path.Combine(UniversalWheelsBase, categoryName);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string ip = Path.Combine(dir, "items.json");
                ItemsFile f = File.Exists(ip) ? JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(ip)) : null;
                if (f == null) f = new ItemsFile { MenuTitle = categoryName };
                if (f.Items == null) f.Items = new List<MenuItem>();
                f.Items.Add(item);
                File.WriteAllText(ip, JsonConvert.SerializeObject(f, Formatting.Indented));
                InvalidateUniversalWheelCache();   // this rim is now re-shelved -> hide it from the default list
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] AddWheelToUniversalCategory error: {ex.Message}"); return false; }
        }

        public static bool RenameUniversalWheelCategory(string name, string newName)
        {
            if (!IsUniversalWheelCategory(name) || string.IsNullOrWhiteSpace(newName)) return false;
            try
            {
                string ip = Path.Combine(UniversalWheelsBase, name, "items.json");
                ItemsFile f = File.Exists(ip) ? JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(ip)) : new ItemsFile();
                if (f == null) f = new ItemsFile();
                f.MenuTitle = newName.Trim();
                File.WriteAllText(ip, JsonConvert.SerializeObject(f, Formatting.Indented));
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] RenameUniversalWheelCategory error: {ex.Message}"); return false; }
        }

        public static bool DeleteUniversalWheelCategory(string name)
        {
            if (!IsUniversalWheelCategory(name)) return false;
            try { Directory.Delete(Path.Combine(UniversalWheelsBase, name), true); InvalidateUniversalWheelCache(); return true; }
            catch (Exception ex) { Log?.Invoke($"[MenuConfig] DeleteUniversalWheelCategory error: {ex.Message}"); return false; }
        }

        // Derived set of (wheelType, index) rims that live in some universal wheel category, so they're hidden
        // from the default per-type rim list (mirrors the body-part GetReshelvedParts, but global, not per-vehicle).
        private static HashSet<long> _wheelReshelvedCache;
        public static HashSet<long> GetUniversalWheelRimSet()
        {
            if (_wheelReshelvedCache != null) return _wheelReshelvedCache;
            var set = new HashSet<long>();
            try
            {
                if (Directory.Exists(UniversalWheelsBase))
                    foreach (string dir in Directory.GetDirectories(UniversalWheelsBase))
                    {
                        string ip = Path.Combine(dir, "items.json");
                        if (!File.Exists(ip)) continue;
                        var f = JsonConvert.DeserializeObject<ItemsFile>(File.ReadAllText(ip));
                        if (f?.Items == null) continue;
                        foreach (var it in f.Items) if (it.WheelType >= 0) set.Add(PartKey(it.WheelType, it.Value));
                    }
            }
            catch { }
            _wheelReshelvedCache = set;
            return set;
        }

        public static void InvalidateUniversalWheelCache() { _wheelReshelvedCache = null; }

        /// <summary>Is this rim (wheelType, index) re-shelved into a universal category (so hide it from the default
        /// per-type list)?</summary>
        public static bool IsRimHidden(int wheelType, int index) => GetUniversalWheelRimSet().Contains(PartKey(wheelType, index));

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
                new MenuItem { Name = "Limo", Description = "Darkest tint.", Price = 500, Value = 1 }
                // NOTE: window-tint enums 4-6 (Stock/Limo/Green) resolve to a hardcoded white fallback that
                // looks milky on the clear-glass texture, and overlap the custom-color slot pool. Presets must
                // stay on slots 0-3 (enum 0-3); any other color is available via the Custom Color picker.
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
