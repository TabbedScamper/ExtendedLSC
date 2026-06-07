using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles wheel enumeration and application
    /// Supports both stock wheels and add-on wheel packs
    /// </summary>
    public static class WheelManager
    {
        // GTA's wheel categories (VehicleWheelType enum)
        public enum WheelCategory
        {
            Sport = 0,
            Muscle = 1,
            Lowrider = 2,
            SUV = 3,
            Offroad = 4,
            Tuner = 5,
            HighEnd = 6,
            BennysOriginal = 7,
            BennysBespoke = 8,
            OpenWheel = 9,
            Street = 10
        }

        /// <summary>
        /// Get all available wheels for a category
        /// </summary>
        public static List<WheelInfo> GetWheelsInCategory(Vehicle vehicle, WheelCategory category)
        {
            var wheels = new List<WheelInfo>();

            if (vehicle == null) return wheels;

            // First set the wheel type category
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, vehicle, (int)category);

            // Get number of wheels available in this category
            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, vehicle, (int)VehicleModType.FrontWheel);

            for (int i = 0; i < count; i++)
            {
                // Get wheel name (if available)
                string name = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, vehicle, (int)VehicleModType.FrontWheel, i);

                wheels.Add(new WheelInfo
                {
                    Index = i,
                    Category = category,
                    Name = string.IsNullOrEmpty(name) ? $"Wheel {i + 1}" : Game.GetLocalizedString(name),
                    IsCustom = false
                });
            }

            return wheels;
        }

        /// <summary>
        /// Apply a wheel to the vehicle
        /// </summary>
        public static void ApplyWheel(Vehicle vehicle, WheelCategory category, int wheelIndex, bool customTires = false)
        {
            if (vehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle, 0);

            // Set wheel category type first
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, vehicle, (int)category);

            // Apply front wheels
            Function.Call(Hash.SET_VEHICLE_MOD, vehicle, (int)VehicleModType.FrontWheel, wheelIndex, customTires);

            // Apply rear wheels (for bikes/vehicles with different rear)
            int rearCount = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, vehicle, (int)VehicleModType.RearWheel);
            if (rearCount > 0 && wheelIndex < rearCount)
            {
                Function.Call(Hash.SET_VEHICLE_MOD, vehicle, (int)VehicleModType.RearWheel, wheelIndex, customTires);
            }
        }

        /// <summary>
        /// Get current wheel info from vehicle
        /// </summary>
        public static (WheelCategory category, int index) GetCurrentWheel(Vehicle vehicle)
        {
            if (vehicle == null) return (WheelCategory.Sport, -1);

            int category = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, vehicle);
            int index = Function.Call<int>(Hash.GET_VEHICLE_MOD, vehicle, (int)VehicleModType.FrontWheel);

            return ((WheelCategory)category, index);
        }

        /// <summary>
        /// Set wheel color
        /// </summary>
        public static void SetWheelColor(Vehicle vehicle, VehicleColor color)
        {
            if (vehicle == null) return;

            // Get current colors
            int primary, secondary;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_COLOURS, vehicle, &primary, &secondary);
            }

            // Set extra color (wheel color is extra color index 0)
            Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, vehicle, (int)color, 0);
        }

        /// <summary>
        /// Toggle custom tires (affects tire smoke color ability)
        /// </summary>
        public static void SetCustomTires(Vehicle vehicle, bool custom)
        {
            if (vehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, vehicle, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, vehicle, (int)VehicleModType.FrontWheel,
                Function.Call<int>(Hash.GET_VEHICLE_MOD, vehicle, (int)VehicleModType.FrontWheel), custom);
        }
    }

    public class WheelInfo
    {
        public int Index { get; set; }
        public WheelManager.WheelCategory Category { get; set; }
        public string Name { get; set; }
        public bool IsCustom { get; set; }

        // For add-on wheels
        public string DLCPack { get; set; }
        public string ModelName { get; set; }
    }
}
