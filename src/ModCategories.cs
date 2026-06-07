using System;
using System.Collections.Generic;
using System.Linq;
using GTA;

namespace ExtendedLSC
{
    /// <summary>
    /// Defines all vehicle modification categories with proper display names
    /// Based on official GTA V mod indices and GXT labels
    /// </summary>
    public static class ModCategories
    {
        /// <summary>
        /// Mod category definition with index, display name, and description
        /// </summary>
        public class ModCategory
        {
            public int Index { get; set; }
            public string DisplayName { get; set; }
            public string SubMenuTitle { get; set; } // Title shown when submenu opens (if different from DisplayName)
            public string Description { get; set; }
            public bool IsBennys { get; set; }
            public bool IsToggle { get; set; }
            public bool IsSpecial { get; set; } // For Repair, Respray, Plate, Wheels
            public bool NoStockOption { get; set; } // True for performance mods that don't show "Stock"

            public ModCategory(int index, string displayName, string description, bool isBennys = false, bool isToggle = false)
            {
                Index = index;
                DisplayName = displayName;
                SubMenuTitle = displayName.ToUpper(); // Default to uppercase display name
                Description = description;
                IsBennys = isBennys;
                IsToggle = isToggle;
                IsSpecial = false;
                NoStockOption = false;
            }
        }

        // Standard LSC Mods (0-24)
        public static readonly ModCategory Spoiler = new ModCategory(0, "Spoiler", "Rear spoiler and wing options");
        public static readonly ModCategory FrontBumper = new ModCategory(1, "Bumpers (Front)", "Front bumper options");
        public static readonly ModCategory RearBumper = new ModCategory(2, "Bumpers (Rear)", "Rear bumper options");
        public static readonly ModCategory SideSkirt = new ModCategory(3, "Skirts", "Side skirt options");
        public static readonly ModCategory Exhaust = new ModCategory(4, "Exhaust", "Exhaust system options");
        public static readonly ModCategory Frame = new ModCategory(5, "Roll Cage", "Roll cage and chassis options");
        public static readonly ModCategory Grille = new ModCategory(6, "Grille", "Front grille options");
        public static readonly ModCategory Hood = new ModCategory(7, "Hood", "Hood and bonnet options");
        public static readonly ModCategory Fender = new ModCategory(8, "Fender", "Fender options") { SubMenuTitle = "FENDERS" };
        public static readonly ModCategory RightFender = new ModCategory(9, "Fender (Right)", "Right fender options") { SubMenuTitle = "FENDERS (RIGHT)" };
        public static readonly ModCategory Roof = new ModCategory(10, "Roof", "Roof options");
        public static readonly ModCategory Engine = new ModCategory(11, "Engine", "Engine performance upgrades") { SubMenuTitle = "ENGINE TUNES", NoStockOption = true };
        public static readonly ModCategory Brakes = new ModCategory(12, "Brakes", "Brake performance upgrades") { SubMenuTitle = "BRAKES", NoStockOption = true };
        public static readonly ModCategory Transmission = new ModCategory(13, "Transmission", "Transmission upgrades") { SubMenuTitle = "TRANSMISSION", NoStockOption = true };
        public static readonly ModCategory Horn = new ModCategory(14, "Horn", "Horn sound options");
        public static readonly ModCategory Suspension = new ModCategory(15, "Suspension", "Suspension upgrades") { SubMenuTitle = "SUSPENSION", NoStockOption = true };
        public static readonly ModCategory Armor = new ModCategory(16, "Armor", "Vehicle armor upgrades");
        public static readonly ModCategory Turbo = new ModCategory(18, "Turbo", "Turbo tuning", false, true);
        public static readonly ModCategory XenonLights = new ModCategory(22, "Lights", "Xenon headlight upgrade", false, true);
        public static readonly ModCategory FrontWheels = new ModCategory(23, "Wheels", "Wheel options");
        public static readonly ModCategory BackWheels = new ModCategory(24, "Wheels (Rear)", "Rear wheel options (motorcycles)");

        // Benny's/Lowrider Mods (25-48)
        public static readonly ModCategory Plateholder = new ModCategory(25, "Plateholder", "License plate holder options", true);
        public static readonly ModCategory VanityPlate = new ModCategory(26, "Vanity Plate", "Vanity plate options", true);
        public static readonly ModCategory TrimDesign = new ModCategory(27, "Trim Design", "Interior trim design options", true);
        public static readonly ModCategory Ornament = new ModCategory(28, "Ornament", "Hood ornament options", true);
        public static readonly ModCategory Dashboard = new ModCategory(29, "Dashboard", "Dashboard options", true);
        public static readonly ModCategory DialDesign = new ModCategory(30, "Dial Design", "Dial and gauge design options", true);
        public static readonly ModCategory DoorSpeakers = new ModCategory(31, "Door Speakers", "Door speaker options", true);
        public static readonly ModCategory Seats = new ModCategory(32, "Seats", "Seat options", true);
        public static readonly ModCategory SteeringWheel = new ModCategory(33, "Steering Wheel", "Steering wheel options", true);
        public static readonly ModCategory ColumnShifter = new ModCategory(34, "Column Shifter", "Column shifter lever options", true);
        public static readonly ModCategory Plaque = new ModCategory(35, "Plaque", "Interior plaque options", true);
        public static readonly ModCategory TrunkSpeakers = new ModCategory(36, "Trunk Speakers", "Trunk speaker and subwoofer options", true);
        public static readonly ModCategory Trunk = new ModCategory(37, "Trunk", "Trunk options", true);
        public static readonly ModCategory Hydraulics = new ModCategory(38, "Hydraulics", "Hydraulics system options", true);
        public static readonly ModCategory EngineBlock = new ModCategory(39, "Engine Block", "Engine block cover options", true);
        public static readonly ModCategory AirFilter = new ModCategory(40, "Air Filter", "Air filter options", true);
        public static readonly ModCategory Struts = new ModCategory(41, "Struts", "Engine strut options", true);
        public static readonly ModCategory ArchCover = new ModCategory(42, "Arch Cover", "Wheel arch cover options", true);
        public static readonly ModCategory Aerials = new ModCategory(43, "Aerials", "Antenna options", true);
        public static readonly ModCategory Trim = new ModCategory(44, "Trim", "Exterior trim options", true);
        public static readonly ModCategory Tank = new ModCategory(45, "Tank", "Tank options", true);
        public static readonly ModCategory Windows = new ModCategory(46, "Windows", "Window options", true);
        public static readonly ModCategory Livery = new ModCategory(48, "Livery", "Vehicle livery options");

        // Special categories (not mod indices)
        public static readonly ModCategory Repair = new ModCategory(-1, "Repair", "Repair all vehicle damage") { IsSpecial = true };
        public static readonly ModCategory Respray = new ModCategory(-2, "Respray", "Change vehicle colors") { IsSpecial = true };
        public static readonly ModCategory Plate = new ModCategory(-3, "Plate", "License plate style options") { IsSpecial = true };
        public static readonly ModCategory WindowTint = new ModCategory(-4, "Windows", "Window tint options") { IsSpecial = true };

        /// <summary>
        /// All mod categories indexed by mod index
        /// </summary>
        public static readonly Dictionary<int, ModCategory> AllCategories = new Dictionary<int, ModCategory>
        {
            { 0, Spoiler },
            { 1, FrontBumper },
            { 2, RearBumper },
            { 3, SideSkirt },
            { 4, Exhaust },
            { 5, Frame },
            { 6, Grille },
            { 7, Hood },
            { 8, Fender },
            { 9, RightFender },
            { 10, Roof },
            { 11, Engine },
            { 12, Brakes },
            { 13, Transmission },
            { 14, Horn },
            { 15, Suspension },
            { 16, Armor },
            { 18, Turbo },
            { 22, XenonLights },
            { 23, FrontWheels },
            { 24, BackWheels },
            { 25, Plateholder },
            { 26, VanityPlate },
            { 27, TrimDesign },
            { 28, Ornament },
            { 29, Dashboard },
            { 30, DialDesign },
            { 31, DoorSpeakers },
            { 32, Seats },
            { 33, SteeringWheel },
            { 34, ColumnShifter },
            { 35, Plaque },
            { 36, TrunkSpeakers },
            { 37, Trunk },
            { 38, Hydraulics },
            { 39, EngineBlock },
            { 40, AirFilter },
            { 41, Struts },
            { 42, ArchCover },
            { 43, Aerials },
            { 44, Trim },
            { 45, Tank },
            { 46, Windows },
            { 48, Livery },
        };

        /// <summary>
        /// Get display name for a mod index
        /// </summary>
        public static string GetDisplayName(int modIndex)
        {
            if (AllCategories.TryGetValue(modIndex, out var category))
                return category.DisplayName;
            return $"Unknown ({modIndex})";
        }

        /// <summary>
        /// Get description for a mod index
        /// </summary>
        public static string GetDescription(int modIndex)
        {
            if (AllCategories.TryGetValue(modIndex, out var category))
                return category.Description;
            return "Unknown modification";
        }

        /// <summary>
        /// Check if a mod index is a Benny's mod
        /// </summary>
        public static bool IsBennysMod(int modIndex)
        {
            if (AllCategories.TryGetValue(modIndex, out var category))
                return category.IsBennys;
            return modIndex >= 25 && modIndex <= 48;
        }

        /// <summary>
        /// Check if a mod index is a toggle mod (Turbo, Xenon)
        /// </summary>
        public static bool IsToggleMod(int modIndex)
        {
            return modIndex == 18 || modIndex == 22;
        }

        /// <summary>
        /// Get available categories for a vehicle, sorted alphabetically
        /// </summary>
        public static List<ModCategory> GetAvailableCategories(Vehicle vehicle, bool includeDamageRepair = true)
        {
            var available = new List<ModCategory>();

            if (vehicle == null) return available;

            // Check each mod index for available mods
            foreach (var kvp in AllCategories)
            {
                int modIndex = kvp.Key;
                var category = kvp.Value;

                // Skip toggle mods - they're handled separately
                if (category.IsToggle) continue;

                // Skip rear wheels for non-motorcycles
                if (modIndex == 24 && !vehicle.Model.IsBike) continue;

                // Check if vehicle has mods for this category
                int modCount = GTA.Native.Function.Call<int>(GTA.Native.Hash.GET_NUM_VEHICLE_MODS, vehicle, modIndex);
                if (modCount > 0)
                {
                    available.Add(category);
                }
            }

            // Add toggle mods
            available.Add(Turbo);
            available.Add(XenonLights);

            // Add special categories
            if (includeDamageRepair && vehicle.HealthFloat < vehicle.MaxHealthFloat)
            {
                available.Add(Repair);
            }
            available.Add(Respray);
            available.Add(Plate);

            // Check for livery
            int liveryCount = GTA.Native.Function.Call<int>(GTA.Native.Hash.GET_VEHICLE_LIVERY_COUNT, vehicle);
            if (liveryCount > 0 && !available.Contains(Livery))
            {
                available.Add(Livery);
            }

            // Sort alphabetically by display name
            available = available.OrderBy(c => c.DisplayName).ToList();

            return available;
        }

        /// <summary>
        /// Track which categories the player has viewed (for yellow star indicator)
        /// </summary>
        private static HashSet<string> viewedCategories = new HashSet<string>();

        public static bool HasViewed(string categoryName)
        {
            return viewedCategories.Contains(categoryName);
        }

        public static void MarkAsViewed(string categoryName)
        {
            viewedCategories.Add(categoryName);
        }

        public static void ClearViewedStatus()
        {
            viewedCategories.Clear();
        }
    }
}
