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
            public bool HasManualTransmission { get; set; } = false;   // owned (bought)
            // True when Manual Transmission is the ACTIVE transmission (vs a vanilla level / Stock automatic).
            public bool ManualTransmissionEquipped { get; set; } = false;
            // Nitrous, per-vehicle: bitmask of owned tiers, and the equipped tier (-1 = none / Off).
            public int NosOwnedTiers { get; set; } = 0;
            public int NosEquippedTier { get; set; } = -1;
            public bool HasWheelFitment { get; set; } = false;
            // True when Custom Suspension & Camber is the ACTIVE suspension (vs a vanilla level / Stock). Lets us
            // distinguish "custom equipped" from plain Stock when no vanilla suspension mod is installed.
            public bool CustomSuspensionEquipped { get; set; } = false;

            // Wheel fitment data
            public FitmentData Fitment { get; set; } = new FitmentData();

            // Handling fine-tune data (Vehicle Tuning category)
            public TuningData Tuning { get; set; } = new TuningData();

            // Custom config-based items: Key = category path (e.g. "Wheels/Tires/Tire Smoke"), Value = list of purchased item values
            public Dictionary<string, List<int>> PurchasedCustomItems { get; set; } = new Dictionary<string, List<int>>();

            // Custom window-glass color (AARRGGBB int). 0 = none/not set (use a normal preset instead).
            public int CustomWindowColor { get; set; } = 0;        // 0 when not equipped (un-equipped clears it)
            public int LastCustomWindowColor { get; set; } = 0;    // last chosen color; survives un-equip for re-equip

            // Persistent tint-array slot assigned to this car's custom color (-1 = unassigned). Persisted so a
            // car keeps the SAME slot forever — adding/removing other cars never reshuffles it.
            public int CustomWindowSlot { get; set; } = -1;

            // Installed engine swap id (see EngineSwaps.All). null/empty = stock engine.
            public string EngineSwapId { get; set; } = null;

            // Display name of the package last applied to this car (null = none) — drives the "equipped" marker.
            public string EquippedPackage { get; set; } = null;

            // Speedometer is PER-CAR: which style is equipped on THIS vehicle (0 = Off, 1 = Simple, 2 = Nfsu).
            // The look/colours/units are global (Speedo config); only the on/off + style is per-car.
            public int SpeedoStyle { get; set; } = 0;
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

        // Handling fine-tune: multipliers vs factory stock for force fields (1 = stock), absolute for the
        // bias/lock fields (-1 = "leave at stock"). Unlocked = the one-time dyno fee has been paid for this car.
        public class TuningData
        {
            public bool Unlocked { get; set; } = false;
            public float TorqueMult { get; set; } = 1f;       // fInitialDriveForce
            public float TopSpeedMult { get; set; } = 1f;     // fInitialDriveMaxFlatVel
            public float GripMult { get; set; } = 1f;         // fTractionCurveMax
            public float BrakeForceMult { get; set; } = 1f;   // fBrakeForce
            public float SteerLockMult { get; set; } = 1f;    // fSteeringLock
            public float DriveBias { get; set; } = -1f;       // fDriveBiasFront 0..1 (-1 = stock)
            public float BrakeBias { get; set; } = -1f;       // fBrakeBiasFront 0..1 (-1 = stock)
            public int GearCount { get; set; } = 0;            // extra gears over stock (0 = stock; gated by engine)
            // Absolute overrides for extended handling fields (keyed by handling.meta tag, e.g. "fSuspensionForce").
            // Reserved for the suspension/anti-roll controls + setup save/load. Empty = all stock.
            public Dictionary<string, float> Fields { get; set; } = new Dictionary<string, float>();
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

        /// <summary>Remove every saved vehicle whose license-plate component equals <paramref name="plate"/>
        /// (case-insensitive). VehicleKey = "DisplayName|PLATE", so we compare the part after the last '|'.
        /// Returns the number of entries removed; persists immediately if any were.</summary>
        public static int PurgeVehiclesByPlate(string plate)
        {
            if (_saveData == null) Load();
            if (_saveData?.Vehicles == null || string.IsNullOrEmpty(plate)) return 0;
            string want = plate.Trim();
            var toRemove = new List<string>();
            foreach (var key in _saveData.Vehicles.Keys)
            {
                int bar = key.LastIndexOf('|');
                string p = bar >= 0 ? key.Substring(bar + 1) : "";
                if (string.Equals(p.Trim(), want, StringComparison.OrdinalIgnoreCase)) toRemove.Add(key);
            }
            foreach (var k in toRemove) _saveData.Vehicles.Remove(k);
            if (toRemove.Count > 0)
            {
                _isDirty = true;
                Save();
                Log?.Invoke($"[SaveData] Purged {toRemove.Count} vehicle save(s) with plate '{plate}'");
            }
            return toRemove.Count;
        }

        /// <summary>True if there is a saved VehicleData entry for this key (case-insensitive).</summary>
        public static bool HasVehicleData(string key)
        {
            if (_saveData == null) Load();
            if (string.IsNullOrEmpty(key)) return false;
            return _saveData.Vehicles.ContainsKey(key.ToLowerInvariant());
        }

        /// <summary>
        /// Move the saved build from oldKey to newKey (used when a car's plate is renamed so its entire
        /// build follows). No-op if oldKey has no data or newKey already has data.
        /// </summary>
        public static void MigrateVehicle(string oldKey, string newKey)
        {
            if (_saveData == null) Load();
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;

            oldKey = oldKey.ToLowerInvariant();
            newKey = newKey.ToLowerInvariant();
            if (oldKey == newKey) return;

            if (_saveData.Vehicles.TryGetValue(oldKey, out var data) &&
                !_saveData.Vehicles.ContainsKey(newKey))
            {
                _saveData.Vehicles[newKey] = data;
                _saveData.Vehicles.Remove(oldKey);
                _isDirty = true;
                Log?.Invoke($"[SaveData] Migrated vehicle data: '{oldKey}' -> '{newKey}'");
            }
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

        /// <summary>Get the package last applied to this vehicle (null = none) — for the equipped marker.</summary>
        public static string GetEquippedPackage(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.EquippedPackage : null;
        }

        /// <summary>Record the package last applied to this vehicle (null = none / cleared).</summary>
        public static void SetEquippedPackage(string vehicleName, string packageName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).EquippedPackage = string.IsNullOrEmpty(packageName) ? null : packageName;
            _isDirty = true;
        }

        /// <summary>Speedometer style equipped on THIS vehicle (0 = Off, 1 = Simple, 2 = Nfsu).</summary>
        public static int GetSpeedoStyle(string vehicleName)
        {
            if (_saveData == null) Load();
            if (string.IsNullOrEmpty(vehicleName)) return 0;
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.SpeedoStyle : 0;
        }

        public static void SetSpeedoStyle(string vehicleName, int style)
        {
            if (_saveData == null) Load();
            if (string.IsNullOrEmpty(vehicleName)) return;
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).SpeedoStyle = style;
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

        // ---- Nitrous (per-vehicle) ----
        public static int GetNosOwnedTiers(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.NosOwnedTiers : 0;
        }

        public static void SetNosOwnedTiers(string vehicleName, int mask)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).NosOwnedTiers = mask;
            _isDirty = true;
        }

        public static int GetNosEquippedTier(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.NosEquippedTier : -1;
        }

        public static void SetNosEquippedTier(string vehicleName, int tier)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).NosEquippedTier = tier;
            _isDirty = true;
        }

        public static bool IsManualTransmissionEquipped(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.ManualTransmissionEquipped;
        }

        public static void SetManualTransmissionEquipped(string vehicleName, bool equipped)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).ManualTransmissionEquipped = equipped;
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

        public static bool IsCustomSuspensionEquipped(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) && data.CustomSuspensionEquipped;
        }

        public static void SetCustomSuspensionEquipped(string vehicleName, bool equipped)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).CustomSuspensionEquipped = equipped;
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

        /// <summary>Get saved handling-tuning data for a vehicle (defaults = stock).</summary>
        public static TuningData GetTuningData(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            if (_saveData.Vehicles.TryGetValue(vehicleName, out var data))
                return data.Tuning ?? new TuningData();
            return new TuningData();
        }

        /// <summary>Save handling-tuning data for a vehicle.</summary>
        public static void SetTuningData(string vehicleName, TuningData tuning)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            EnsureVehicle(vehicleName).Tuning = tuning;
            _isDirty = true;
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

        /// <summary>The last custom window color the player chose for this car (survives un-equip).</summary>
        public static int GetLastCustomWindowColor(string vehicleName)
        {
            if (_saveData == null) Load();
            vehicleName = vehicleName.ToLowerInvariant();
            return _saveData.Vehicles.TryGetValue(vehicleName, out var data) ? data.LastCustomWindowColor : 0;
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
            if (argb != 0) data.LastCustomWindowColor = argb;   // remember the chosen color so un-equip can restore it
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
