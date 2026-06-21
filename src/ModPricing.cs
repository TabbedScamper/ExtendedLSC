using System;
using System.Collections.Generic;
using GTA;

namespace ExtendedLSC
{
    /// <summary>
    /// Vehicle modification pricing configuration
    /// Prices scale based on mod index (higher index = more expensive)
    /// </summary>
    public static class ModPricing
    {
        // Base prices per mod type (index 0 price, scales up for higher indices)
        private static readonly Dictionary<VehicleModType, int[]> BasePrices = new Dictionary<VehicleModType, int[]>
        {
            // Performance mods - price per level
            { VehicleModType.Engine, new[] { 900, 1250, 1800, 3350 } },
            { VehicleModType.Brakes, new[] { 1000, 2000, 2700 } },
            { VehicleModType.Transmission, new[] { 1000, 1750, 2950 } },
            { VehicleModType.Suspension, new[] { 200, 400, 700, 1000 } },
            { VehicleModType.Armor, new[] { 500, 1000, 1500, 2000, 2500 } },

            // Cosmetic mods - base + increment per index
            { VehicleModType.FrontBumper, new[] { 500, 300 } },      // base 500, +300 per index
            { VehicleModType.RearBumper, new[] { 400, 250 } },
            { VehicleModType.SideSkirt, new[] { 500, 200 } },
            { VehicleModType.Spoilers, new[] { 600, 250 } },
            { VehicleModType.Hood, new[] { 600, 300 } },
            { VehicleModType.Roof, new[] { 500, 250 } },
            { VehicleModType.Grille, new[] { 300, 150 } },
            { VehicleModType.Fender, new[] { 400, 200 } },
            { VehicleModType.Exhaust, new[] { 300, 200 } },
            { VehicleModType.Frame, new[] { 1000, 400 } },          // Roll cage/chassis
            { VehicleModType.ColumnShifterLevers, new[] { 200, 100 } },
            { VehicleModType.Dashboard, new[] { 200, 100 } },
            { VehicleModType.DialDesign, new[] { 150, 75 } },
            { VehicleModType.DoorSpeakers, new[] { 250, 100 } },
            { VehicleModType.Ornaments, new[] { 200, 100 } },
            { VehicleModType.Plaques, new[] { 150, 75 } },
            { VehicleModType.Seats, new[] { 300, 150 } },
            { VehicleModType.SteeringWheels, new[] { 250, 125 } },
            { VehicleModType.Tank, new[] { 400, 200 } },
            { VehicleModType.TrimDesign, new[] { 200, 100 } },
            { VehicleModType.Windows, new[] { 300, 100 } },
            { VehicleModType.Livery, new[] { 500, 250 } },
            { VehicleModType.Aerials, new[] { 100, 50 } },
            { VehicleModType.ArchCover, new[] { 300, 150 } },
            { VehicleModType.EngineBlock, new[] { 500, 250 } },
            { VehicleModType.AirFilter, new[] { 300, 150 } },
            { VehicleModType.Struts, new[] { 400, 200 } },
        };

        // No single mod/part may cost more than this — every price path is capped to it (rebalance ceiling).
        public const int MaxModPrice = 12000;

        // Toggle mod prices (turbo, etc.)
        public static readonly int TurboPrice = 2500;
        public static readonly int NosPrice = 10000;   // legacy single-NOS price (kept for compatibility)
        // Nitrous tiers — each a separate purchase, rising in cost (NOS 1..4 => index 0..3). Capped at MaxModPrice.
        public static readonly int[] NosTierPrices = { 5000, 8000, 10000, 12000 };
        public static readonly int XenonLightsPrice = 1500;
        public static readonly int CustomTiresPrice = 600;   // Aftermarket (low-profile) tire design
        public static readonly int CustomSuspensionPrice = 2000; // Custom Suspension & Camber (ride height/camber/rake/poke)
        public static readonly int ManualTransmissionPrice = 7500; // Manual Transmission (manual shifting + unlocks transmission tuning)

        // Speedometer skins (bought once, global)
        public static readonly int SpeedoLeFixPrice = 4500;
        public static readonly int SpeedoArcadePrice = 6500;

        // Service prices
        public static readonly int RepairPrice = 200;
        public static readonly int WashPrice = 50;

        // Wheel prices by category
        public static readonly Dictionary<int, int> WheelCategoryPrices = new Dictionary<int, int>
        {
            { 0, 2000 },   // Sport
            { 1, 1500 },   // Muscle
            { 2, 1800 },   // Lowrider
            { 3, 1600 },   // SUV
            { 4, 1400 },   // Offroad
            { 5, 2200 },   // Tuner
            { 6, 5000 },   // High End
            { 7, 3500 },   // Benny's Original
            { 8, 3000 },   // Benny's Bespoke
            { 9, 2500 },   // Open Wheel
            { 10, 1800 }, // Street
            { 11, 2000 }, // Track
            { 12, 1000 }, // Bike/Motorcycle
        };

        // Window tint prices
        public static readonly int[] WindowTintPrices = { 0, 500, 400, 350, 300, 250, 200 };

        // Plate prices
        public static readonly int[] PlatePrices = { 0, 200, 200, 200, 200, 200 };

        // Respray prices
        public static readonly int ClassicPrice = 250;      // Standard solid colors
        public static readonly int MetallicPrice = 500;     // Metallic with pearlescent
        public static readonly int MattePrice = 750;        // Matte finish
        public static readonly int MetalPrice = 1500;       // Brushed metal finishes
        public static readonly int ChromePrice = 2500;      // Chrome
        public static readonly int PearlescentPrice = 1000; // Pearlescent overlay
        public static readonly int WheelColorPrice = 500;   // Wheel color change

        /// <summary>
        /// Get the price for a specific mod type and index
        /// </summary>
        public static int GetModPrice(VehicleModType modType, int modIndex)
        {
            return GetModPriceByIndex((int)modType, modIndex);
        }

        /// <summary>
        /// Get the price for a specific mod index and value (capped at MaxModPrice).
        /// </summary>
        public static int GetModPriceByIndex(int modIndex, int valueIndex)
            => Math.Min(RawModPriceByIndex(modIndex, valueIndex), MaxModPrice);

        private static int RawModPriceByIndex(int modIndex, int valueIndex)
        {
            // Try to convert to VehicleModType to use existing pricing
            if (Enum.IsDefined(typeof(VehicleModType), modIndex))
            {
                var modType = (VehicleModType)modIndex;
                if (BasePrices.ContainsKey(modType))
                {
                    var prices = BasePrices[modType];

                    // Performance mods have specific prices per level
                    if (IsPerformanceModIndex(modIndex))
                    {
                        if (valueIndex >= 0 && valueIndex < prices.Length)
                            return prices[valueIndex];
                        return prices[prices.Length - 1];
                    }

                    // Cosmetic mods use base + increment formula
                    int basePrice = prices[0];
                    int increment = prices.Length > 1 ? prices[1] : 100;
                    return basePrice + (increment * valueIndex);
                }
            }

            // Default pricing for unknown mod types
            // Benny's mods (25-48) are generally more expensive
            if (modIndex >= 25 && modIndex <= 48)
                return 500 + (valueIndex * 200);

            return 400 + (valueIndex * 150);
        }

        /// <summary>
        /// Get wheel price for a specific wheel type and index
        /// </summary>
        public static int GetWheelPrice(int wheelType, int wheelIndex)
        {
            int basePrice = WheelCategoryPrices.ContainsKey(wheelType) ? WheelCategoryPrices[wheelType] : 1500;
            return Math.Min(basePrice + (wheelIndex * 200), MaxModPrice); // Higher index = more expensive, capped
        }

        /// <summary>
        /// Check if mod type is a performance upgrade
        /// </summary>
        public static bool IsPerformanceMod(VehicleModType modType)
        {
            return IsPerformanceModIndex((int)modType);
        }

        /// <summary>
        /// Check if mod index is a performance upgrade
        /// </summary>
        public static bool IsPerformanceModIndex(int modIndex)
        {
            // Engine=11, Brakes=12, Transmission=13, Suspension=15, Armor=16
            return modIndex == 11 || modIndex == 12 || modIndex == 13 ||
                   modIndex == 15 || modIndex == 16;
        }

        /// <summary>
        /// Get display name for mod type
        /// </summary>
        public static string GetModTypeName(VehicleModType modType)
        {
            switch (modType)
            {
                case VehicleModType.Engine: return "Engine";
                case VehicleModType.Brakes: return "Brakes";
                case VehicleModType.Transmission: return "Transmission";
                case VehicleModType.Suspension: return "Suspension";
                case VehicleModType.Armor: return "Armor";
                case VehicleModType.FrontBumper: return "Front Bumper";
                case VehicleModType.RearBumper: return "Rear Bumper";
                case VehicleModType.SideSkirt: return "Side Skirts";
                case VehicleModType.Spoilers: return "Spoiler";
                case VehicleModType.Hood: return "Hood";
                case VehicleModType.Roof: return "Roof";
                case VehicleModType.Grille: return "Grille";
                case VehicleModType.Fender: return "Fender";
                case VehicleModType.Exhaust: return "Exhaust";
                case VehicleModType.Frame: return "Roll Cage";
                case VehicleModType.ColumnShifterLevers: return "Shifter";
                case VehicleModType.Dashboard: return "Dashboard";
                case VehicleModType.DialDesign: return "Dials";
                case VehicleModType.DoorSpeakers: return "Speakers";
                case VehicleModType.Ornaments: return "Ornaments";
                case VehicleModType.Plaques: return "Plaques";
                case VehicleModType.Seats: return "Seats";
                case VehicleModType.SteeringWheels: return "Steering Wheel";
                case VehicleModType.Tank: return "Tank";
                case VehicleModType.TrimDesign: return "Trim";
                case VehicleModType.Windows: return "Windows";
                case VehicleModType.Livery: return "Livery";
                case VehicleModType.Aerials: return "Antenna";
                case VehicleModType.ArchCover: return "Arch Cover";
                case VehicleModType.EngineBlock: return "Engine Block";
                case VehicleModType.AirFilter: return "Air Filter";
                case VehicleModType.Struts: return "Struts";
                default: return modType.ToString();
            }
        }

        /// <summary>
        /// Get performance level name (EMS 1, Race Brakes, etc.)
        /// </summary>
        public static string GetPerformanceLevelName(VehicleModType modType, int level)
        {
            return GetPerformanceLevelNameByIndex((int)modType, level);
        }

        /// <summary>
        /// Get performance level name by mod index
        /// </summary>
        public static string GetPerformanceLevelNameByIndex(int modIndex, int level)
        {
            switch (modIndex)
            {
                case 11: // Engine
                    string[] engines = { "EMS Upgrade, Level 1", "EMS Upgrade, Level 2", "EMS Upgrade, Level 3", "EMS Upgrade, Level 4" };
                    return level < engines.Length ? engines[level] : $"Engine Level {level + 1}";

                case 12: // Brakes
                    string[] brakes = { "Street Brakes", "Sport Brakes", "Race Brakes" };
                    return level < brakes.Length ? brakes[level] : $"Brakes Level {level + 1}";

                case 13: // Transmission
                    string[] trans = { "Street Transmission", "Sports Transmission", "Race Transmission" };
                    return level < trans.Length ? trans[level] : $"Transmission Level {level + 1}";

                case 15: // Suspension
                    string[] susp = { "Lowered Suspension", "Street Suspension", "Sport Suspension", "Competition Suspension" };
                    return level < susp.Length ? susp[level] : $"Suspension Level {level + 1}";

                case 16: // Armor
                    string[] armor = { "Armor Upgrade 20%", "Armor Upgrade 40%", "Armor Upgrade 60%", "Armor Upgrade 80%", "Armor Upgrade 100%" };
                    return level < armor.Length ? armor[level] : $"Armor Level {level + 1}";

                default:
                    return $"Level {level + 1}";
            }
        }

        /// <summary>
        /// Get wheel type name
        /// </summary>
        public static string GetWheelTypeName(int wheelType)
        {
            // Indices match the GTA/SHVDN VehicleWheelType enum exactly (6 = BikeWheels, 7 = HighEnd,
            // 8 = Benny's Originals, 9 = Benny's Bespoke). The old table omitted BikeWheels and was off by one.
            string[] types = { "Sport", "Muscle", "Lowrider", "SUV", "Offroad", "Tuner", "Bike Wheels", "High End",
                              "Benny's Originals", "Benny's Bespoke", "Open Wheel", "Street", "Track" };
            return (wheelType >= 0 && wheelType < types.Length) ? types[wheelType] : $"Type {wheelType}";
        }
    }
}
