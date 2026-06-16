using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ExtendedLSC
{
    /// <summary>
    /// Handles saving and loading of vehicle modification data.
    /// Tracks which mods have been purchased for each vehicle.
    /// </summary>
    public static class VehicleSaveData
    {
        private static string SavePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ExtendedLSC",
            "SaveData.json"
        );

        public static Action<string> Log { get; set; }

        // In-memory cache of save data
        private static SaveFile _saveData;
        private static bool _isDirty = false;

        #region Data Classes

        public class SaveFile
        {
            public Dictionary<string, VehicleData> Vehicles { get; set; } = new Dictionary<string, VehicleData>();
        }

        public class VehicleData
        {
            // Key = mod index (0-48), Value = list of purchased mod values
            public Dictionary<int, List<int>> PurchasedMods { get; set; } = new Dictionary<int, List<int>>();

            // Special purchases (not tied to mod indices)
            public List<string> PurchasedColors { get; set; } = new List<string>();
            public List<int> PurchasedHorns { get; set; } = new List<int>();
            public List<int> PurchasedPlates { get; set; } = new List<int>();
            public List<int> PurchasedTints { get; set; } = new List<int>();
            public bool HasTurbo { get; set; } = false;
            public bool HasXenon { get; set; } = false;
            public bool HasBulletproofTires { get; set; } = false;
            public bool HasManualTransmission { get; set; } = false;
            public bool HasWheelFitment { get; set; } = false;

            // Wheel fitment data
            public FitmentData Fitment { get; set; } = new FitmentData();

            // Custom config-based items: Key = category path (e.g. "Wheels/Tires/Tire Smoke"), Value = list of purchased item values
            public Dictionary<string, List<int>> PurchasedCustomItems { get; set; } = new Dictionary<string, List<int>>();

            // Custom window-glass color (AARRGGBB int). 0 = none/not set (use a normal preset instead).
            public int CustomWindowColor { get; set; } = 0;

            // Persistent tint-array slot assigned to this car's custom color (-1 = unassigned). Persisted so a
            // car keeps the SAME slot forever — adding/removing other cars never reshuffles it.
            public int CustomWindowSlot { get; set; } = -1;

            // Installed engine swap id (see EngineSwaps.All). null/empty = stock engine.
            public string EngineSwapId { get; set; } = null;
        }

        public class FitmentData
        {
            public float FrontCamber { get; set; } = 0f;
            public float RearCamber { get; set; } = 0f;
            public float FrontTrackWidth { get; set; } = 0f;
            public float RearTrackWidth { get; set; } = 0f;
            public float FrontHeight { get; set; } = 0f;
            public float RearHeight { get; set; } = 0f;
            public float Rake { get; set; } = 0f;         // front/rear tilt (+front-low, -rear-low)
            public float StockFake { get; set; } = 0f;    // clean suspension-mod base for ride height (anti-drift)
            public float VisualSize { get; set; } = 1f;   // wheel size multiplier (1 = stock)
            public float VisualWidth { get; set; } = 1f;  // wheel width multiplier (1 = stock)
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize the save system - loads existing data or creates new
        /// </summary>
        public static void Initialize()
        {
            Load();
        }

        /// <summary>
        /// Check if a mod has been purchased for a vehicle
        /// </summary>
        public static bool IsModOwned(string vehicleName, int modIndex, int modValue)
        {
            if (_saveData == null) Load();

            vehicleName = vehicleName.ToLowerInvariant();

            if (!_saveData.Vehicles.TryGetValue(vehicleName, out var vehicleData))
                return false;

            if (!vehicleData.PurchasedMods.TryGetValue(modIndex, out var purchasedValues))
                return false;

            return purchasedValues.Contains(modValue);
        }

        /// <summary>
        /// Mark a mod as purchased for a vehicle
        /// </summary>
        public static void SetModOwned(string vehicleName, int modIndex, int modValue)
        {
            if (_saveData == null) Load();

            vehicleName = vehicleName.ToLowerInvariant();

            if (!_saveData.Vehicles.TryGetValue(vehicleName, out var vehicleData))
            {
                vehicleData = new VehicleData();
                _saveData.Vehicles[vehicleName] = vehicleData;
            }

            if (!vehicleData.PurchasedMods.TryGetValue(modIndex, out var purchasedValues))
            {
                purchasedValues = new List<int>();
                vehicleData.PurchasedMods[modIndex] = purchasedValues;
            }

            if (!purchasedValues.Contains(modValue))
            {
                purchasedValues.Add(modValue);
                _isDirty = true;
                Log?.Invoke($"[SaveData] Marked mod owned: {vehicleName} index={modIndex} value={modValue}");
            }
        }

        /// <summary>
        /// Check if turbo has been purchased
        /// </summary>
        public static bool IsTurboOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.HasTurbo;
        }

        public static void SetTurboOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).HasTurbo = true;
            _isDirty = true;
        }

        /// <summary>Get this vehicle's installed engine swap id (null = stock).</summary>
        public static string GetEngineSwapId(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.EngineSwapId : null;
        }

        /// <summary>Set this vehicle's installed engine swap id (null/empty = revert to stock).</summary>
        public static void SetEngineSwapId(string vehicleName, string swapId)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).EngineSwapId = string.IsNullOrEmpty(swapId) ? null : swapId;
            _isDirty = true;
        }

        /// <summary>
        /// Check if xenon lights have been purchased
        /// </summary>
        public static bool IsXenonOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.HasXenon;
        }

        public static void SetXenonOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).HasXenon = true;
            _isDirty = true;
        }

        /// <summary>
        /// Check if bulletproof tires have been purchased
        /// </summary>
        public static bool IsBulletproofTiresOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.HasBulletproofTires;
        }

        public static void SetBulletproofTiresOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).HasBulletproofTires = true;
            _isDirty = true;
        }

        /// <summary>
        /// Check if manual transmission has been purchased
        /// </summary>
        public static bool IsManualTransmissionOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.HasManualTransmission;
        }

        public static void SetManualTransmissionOwned(string vehicleName, bool owned = true)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).HasManualTransmission = owned;
            _isDirty = true;
        }

        /// <summary>
        /// Check if wheel fitment has been purchased
        /// </summary>
        public static bool IsWheelFitmentOwned(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.HasWheelFitment;
        }

        public static void SetWheelFitmentOwned(string vehicleName, bool owned = true)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).HasWheelFitment = owned;
            _isDirty = true;
        }

        /// <summary>
        /// Get saved fitment data for a vehicle
        /// </summary>
        public static FitmentData GetFitmentData(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            if (_saveData.Vehicles.TryGetValue(vehicleName, out var data))
                return data.Fitment ?? new FitmentData();
            return new FitmentData();
        }

        /// <summary>
        /// Save fitment data for a vehicle
        /// </summary>
        public static void SetFitmentData(string vehicleName, FitmentData fitment)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            data.Fitment = fitment;
            _isDirty = true;
            Log?.Invoke($"[SaveData] Saved fitment for {vehicleName}: FC={fitment.FrontCamber:F3} RC={fitment.RearCamber:F3}");
        }

        /// <summary>
        /// Check if a horn has been purchased
        /// </summary>
        public static bool IsHornOwned(string vehicleName, int hornIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) &&
                   data.PurchasedHorns.Contains(hornIndex);
        }

        public static void SetHornOwned(string vehicleName, int hornIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            if (!data.PurchasedHorns.Contains(hornIndex))
            {
                data.PurchasedHorns.Add(hornIndex);
                _isDirty = true;
            }
        }

        /// <summary>
        /// Check if a plate style has been purchased
        /// </summary>
        public static bool IsPlateOwned(string vehicleName, int plateIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) &&
                   data.PurchasedPlates.Contains(plateIndex);
        }

        public static void SetPlateOwned(string vehicleName, int plateIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            if (!data.PurchasedPlates.Contains(plateIndex))
            {
                data.PurchasedPlates.Add(plateIndex);
                _isDirty = true;
            }
        }

        /// <summary>
        /// Check if a window tint has been purchased
        /// </summary>
        public static bool IsTintOwned(string vehicleName, int tintIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) &&
                   data.PurchasedTints.Contains(tintIndex);
        }

        public static void SetTintOwned(string vehicleName, int tintIndex)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            if (!data.PurchasedTints.Contains(tintIndex))
            {
                data.PurchasedTints.Add(tintIndex);
                _isDirty = true;
            }
        }

        /// <summary>
        /// Check if a custom config item has been purchased
        /// </summary>
        /// <param name="vehicleName">Vehicle display name</param>
        /// <param name="categoryPath">Category path (e.g. "Wheels/Tires/Tire Smoke")</param>
        /// <param name="itemValue">The item's value from config</param>
        public static bool IsCustomItemOwned(string vehicleName, string categoryPath, int itemValue)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            categoryPath = categoryPath.ToLowerInvariant();

            if (!_saveData.Vehicles.TryGetValue(vehicleName, out var data))
                return false;

            if (!data.PurchasedCustomItems.TryGetValue(categoryPath, out var purchasedValues))
                return false;

            return purchasedValues.Contains(itemValue);
        }

        /// <summary>
        /// Mark a custom config item as purchased
        /// </summary>
        public static void SetCustomItemOwned(string vehicleName, string categoryPath, int itemValue)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            categoryPath = categoryPath.ToLowerInvariant();

            var data = EnsureVehicle(vehicleName);

            if (!data.PurchasedCustomItems.TryGetValue(categoryPath, out var purchasedValues))
            {
                purchasedValues = new List<int>();
                data.PurchasedCustomItems[categoryPath] = purchasedValues;
            }

            if (!purchasedValues.Contains(itemValue))
            {
                purchasedValues.Add(itemValue);
                _isDirty = true;
                Log?.Invoke($"[SaveData] Marked custom item owned: {vehicleName} category={categoryPath} value={itemValue}");
            }
        }

        /// <summary>
        /// Get all purchased item values for a category
        /// </summary>
        public static List<int> GetOwnedCustomItems(string vehicleName, string categoryPath)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            categoryPath = categoryPath.ToLowerInvariant();

            if (!_saveData.Vehicles.TryGetValue(vehicleName, out var data))
                return new List<int>();

            if (!data.PurchasedCustomItems.TryGetValue(categoryPath, out var purchasedValues))
                return new List<int>();

            return new List<int>(purchasedValues);
        }

        /// <summary>Get the saved custom window color (AARRGGBB) for a vehicle, or 0 if none.</summary>
        public static int GetCustomWindowColor(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.CustomWindowColor : 0;
        }

        /// <summary>All vehicles that have a custom window color, name -> AARRGGBB. For populating slots so
        /// multiple parked custom cars can all show their colors at once.</summary>
        public static System.Collections.Generic.Dictionary<string, int> GetAllCustomWindowColors()
        {
            if (_saveData == null) Load();
            var result = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var kvp in _saveData.Vehicles)
                if (kvp.Value.CustomWindowColor != 0) result[kvp.Key] = kvp.Value.CustomWindowColor;
            return result;
        }

        /// <summary>Set (or clear with 0) the saved custom window color for a vehicle.</summary>
        public static void SetCustomWindowColor(string vehicleName, int argb)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            if (data.CustomWindowColor != argb)
            {
                data.CustomWindowColor = argb;
                _isDirty = true;
            }
        }

        /// <summary>Get this car's persisted tint slot, or -1 if it hasn't been assigned one.</summary>
        public static int GetCustomWindowSlot(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.CustomWindowSlot : -1;
        }

        /// <summary>Persist this car's tint slot (-1 to release it).</summary>
        public static void SetCustomWindowSlot(string vehicleName, int slot)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            var data = EnsureVehicle(vehicleName);
            if (data.CustomWindowSlot != slot)
            {
                data.CustomWindowSlot = slot;
                _isDirty = true;
            }
        }

        /// <summary>Slots currently in use (car name -> slot), counting only cars that still have a color.</summary>
        public static System.Collections.Generic.Dictionary<string, int> GetAllCustomWindowSlots()
        {
            if (_saveData == null) Load();
            var result = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var kvp in _saveData.Vehicles)
                if (kvp.Value.CustomWindowColor != 0 && kvp.Value.CustomWindowSlot >= 0)
                    result[kvp.Key] = kvp.Value.CustomWindowSlot;
            return result;
        }

        /// <summary>
        /// Save data to disk (call periodically or on menu close)
        /// </summary>
        public static void Save()
        {
            if (!_isDirty || _saveData == null) return;

            try
            {
                string directory = Path.GetDirectoryName(SavePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(_saveData, Formatting.Indented);
                File.WriteAllText(SavePath, json);
                _isDirty = false;
                Log?.Invoke($"[SaveData] Saved to {SavePath}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[SaveData] Error saving: {ex.Message}");
            }
        }

        /// <summary>
        /// Load data from disk
        /// </summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(SavePath))
                {
                    string json = File.ReadAllText(SavePath);
                    _saveData = JsonConvert.DeserializeObject<SaveFile>(json) ?? new SaveFile();
                    Log?.Invoke($"[SaveData] Loaded from {SavePath}");
                }
                else
                {
                    _saveData = new SaveFile();
                    Log?.Invoke("[SaveData] Created new save file");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[SaveData] Error loading: {ex.Message}");
                _saveData = new SaveFile();
            }
        }

        #endregion

        #region Private Methods

        private static VehicleData EnsureVehicle(string vehicleName)
        {
            if (!_saveData.Vehicles.TryGetValue(vehicleName, out var data))
            {
                data = new VehicleData();
                _saveData.Vehicles[vehicleName] = data;
            }
            return data;
        }

        #endregion
    }
}
