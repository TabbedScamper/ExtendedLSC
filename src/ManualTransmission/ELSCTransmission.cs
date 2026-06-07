using System;
using GTA;
using GTA.Native;

namespace ExtendedLSC.ManualTransmission
{
    /// <summary>
    /// ELSC's built-in manual transmission system.
    /// Arcade-style: forgiving, instant shifts, redline penalty.
    /// </summary>
    public class ELSCTransmission
    {
        // Logging
        public static Action<string> Log { get; set; }

        // Control indices
        private const int INPUT_VEH_ACCELERATE = 71;
        private const int INPUT_VEH_BRAKE = 72;

        // State
        private Vehicle _vehicle;
        private bool _enabled = false;
        private bool _inNeutral = false;
        private bool _inReverse = false;
        private ushort _targetGear = 1;
        private DateTime _lastShiftTime = DateTime.MinValue;

        // Track what WE injected last frame (to filter from input detection)
        private bool _injectedBrake = false;
        private bool _injectedGas = false;

        // Performance optimization - reduce memory operations
        private int _updateCounter = 0;
        private const int REDLINE_CHECK_INTERVAL = 3;    // Check redline every 3 frames
        private float _cachedRPM = 0f;
        private byte _cachedTopGear = 6;

        // Arcade Settings (tuned for fun, forgiving gameplay)
        public float ShiftCooldown { get; set; } = 0.1f;        // Very fast shifts allowed
        public float RevMatchStrength { get; set; } = 0.3f;     // Subtle auto rev-match on downshift
        public float ShiftUpRPM { get; set; } = 0.85f;          // Optimal shift point (shift indicator)
        public float ShiftDownRPM { get; set; } = 0.35f;        // Recommended downshift point

        // Redline penalty settings
        public float RedlineStart { get; set; } = 0.93f;        // RPM where power starts dropping
        public float RedlinePenaltyMild { get; set; } = 0.85f;  // Throttle multiplier at redline start (85%)
        public float RedlinePenaltyMax { get; set; } = 0.55f;   // Throttle multiplier at full limiter (55%)

        // Read-only state
        public bool IsEnabled => _enabled;
        public bool InNeutral => _inNeutral;
        public bool InReverse => _inReverse;
        public ushort CurrentGear => _targetGear;

        // Events
        public event Action<ushort> OnGearChanged;
        public event Action<bool> OnNeutralChanged;

        /// <summary>
        /// Check if the transmission system is available (memory access working)
        /// </summary>
        public static bool IsAvailable => VehicleMemory.IsAvailable;

        /// <summary>
        /// Initialize the transmission system
        /// </summary>
        public static void Initialize()
        {
            VehicleMemory.Log = Log;
            VehicleMemory.Initialize();
        }

        /// <summary>
        /// Set the vehicle this transmission controls
        /// </summary>
        public void SetVehicle(Vehicle vehicle)
        {
            _vehicle = vehicle;
            if (vehicle != null && vehicle.Exists())
            {
                // Read current gear state
                _targetGear = VehicleMemory.GetCurrentGear(vehicle);
                if (_targetGear == 0) _targetGear = 1;
                _inNeutral = false;

                // Cache vehicle's top gear (4-8 depending on car)
                _cachedTopGear = VehicleMemory.GetTopGear(vehicle);
            }
        }

        /// <summary>
        /// Enable manual transmission control
        /// </summary>
        public void Enable()
        {
            if (_vehicle == null || !_vehicle.Exists()) return;
            if (!VehicleMemory.IsAvailable)
            {
                Log?.Invoke("[ELSCTransmission] Cannot enable - memory access not available");
                return;
            }

            _enabled = true;
            _targetGear = VehicleMemory.GetCurrentGear(_vehicle);
            if (_targetGear == 0) _targetGear = 1;
            _inNeutral = false;
            _inReverse = false;

            // Cache vehicle's top gear (different cars have different gear counts)
            _cachedTopGear = VehicleMemory.GetTopGear(_vehicle);
            _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);

            // Ensure clutch is fully engaged (arcade - no clutch management)
            VehicleMemory.SetClutch(_vehicle, 1.0f);

            Log?.Invoke($"[ELSCTransmission] Enabled (Arcade Mode), gear: {_targetGear}, top gear: {_cachedTopGear}");
        }

        /// <summary>
        /// Disable manual transmission control (return to automatic)
        /// </summary>
        public void Disable()
        {
            if (_vehicle != null && _vehicle.Exists())
            {
                // Restore full clutch and throttle
                VehicleMemory.SetClutch(_vehicle, 1.0f);
            }

            _enabled = false;
            _inNeutral = false;
            _inReverse = false;
            Log?.Invoke("[ELSCTransmission] Disabled");
        }

        /// <summary>
        /// Toggle neutral gear
        /// </summary>
        public void ToggleNeutral()
        {
            if (!_enabled) return;

            _inNeutral = !_inNeutral;
            OnNeutralChanged?.Invoke(_inNeutral);

            if (_inNeutral)
            {
                // Disengage clutch in neutral
                VehicleMemory.SetClutch(_vehicle, 0.0f);
            }
            else
            {
                // Re-engage clutch instantly (arcade)
                VehicleMemory.SetClutch(_vehicle, 1.0f);
            }

            Log?.Invoke($"[ELSCTransmission] Neutral: {_inNeutral}");
        }

        /// <summary>
        /// Shift up one gear (arcade-style: instant, forgiving)
        /// Pattern: R → N → 1 → 2 → 3 → 4 → 5 → 6...
        /// </summary>
        public void ShiftUp()
        {
            if (!_enabled || _vehicle == null || !_vehicle.Exists()) return;

            // Check cooldown (very short for arcade feel)
            if ((DateTime.Now - _lastShiftTime).TotalSeconds < ShiftCooldown) return;
            _lastShiftTime = DateTime.Now;

            // From reverse, go to neutral
            if (_inReverse)
            {
                _inReverse = false;
                _inNeutral = true;
                _targetGear = 1; // Keep gear at 1 internally
                OnNeutralChanged?.Invoke(true);
                Log?.Invoke("[ELSCTransmission] Shifted from R to N");
                return;
            }

            // Exit neutral into 1st gear
            if (_inNeutral)
            {
                _inNeutral = false;
                _targetGear = 1;
                OnNeutralChanged?.Invoke(false);
                VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
                VehicleMemory.SetNextGear(_vehicle, _targetGear);
                VehicleMemory.SetClutch(_vehicle, 1.0f);
                OnGearChanged?.Invoke(_targetGear);
                Log?.Invoke("[ELSCTransmission] Shifted from N to 1");
                return;
            }

            byte topGear = VehicleMemory.GetTopGear(_vehicle);
            if (_targetGear >= topGear) return; // Already in top gear

            // Instant shift (arcade - no clutch delay)
            _targetGear++;

            // Set the new gear immediately
            VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
            VehicleMemory.SetNextGear(_vehicle, _targetGear);

            // Keep clutch engaged (arcade - no clutch management)
            VehicleMemory.SetClutch(_vehicle, 1.0f);

            OnGearChanged?.Invoke(_targetGear);
        }

        /// <summary>
        /// Shift down one gear (arcade-style: instant, auto rev-match)
        /// Pattern: ...6 → 5 → 4 → 3 → 2 → 1 → N → R
        /// </summary>
        public void ShiftDown()
        {
            if (!_enabled || _vehicle == null || !_vehicle.Exists()) return;

            // Check cooldown
            if ((DateTime.Now - _lastShiftTime).TotalSeconds < ShiftCooldown) return;
            _lastShiftTime = DateTime.Now;

            // Can't shift down from reverse
            if (_inReverse) return;

            // From neutral, go to reverse
            if (_inNeutral)
            {
                _inNeutral = false;
                _inReverse = true;
                _targetGear = 1; // Keep gear at 1 internally, we handle reverse via input
                OnNeutralChanged?.Invoke(false);
                Log?.Invoke("[ELSCTransmission] Shifted from N to R");
                return;
            }

            if (_targetGear <= 1)
            {
                // Shift into neutral from 1st
                _inNeutral = true;
                OnNeutralChanged?.Invoke(true);
                Log?.Invoke("[ELSCTransmission] Shifted from 1 to N");
                return;
            }

            // Instant shift
            _targetGear--;

            // Auto rev-match (arcade - always smooth)
            float currentRPM = VehicleMemory.GetCurrentRPM(_vehicle);
            float targetRPM = Math.Min(0.95f, currentRPM + RevMatchStrength);
            VehicleMemory.SetCurrentRPM(_vehicle, targetRPM);

            // Set the new gear immediately
            VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
            VehicleMemory.SetNextGear(_vehicle, _targetGear);

            // Keep clutch engaged
            VehicleMemory.SetClutch(_vehicle, 1.0f);

            OnGearChanged?.Invoke(_targetGear);
        }

        /// <summary>
        /// Update the transmission state (call every tick)
        /// Handles neutral/reverse input control and redline penalty
        /// </summary>
        public void Update()
        {
            if (!_enabled || _vehicle == null || !_vehicle.Exists()) return;

            _updateCounter++;

            // === NEUTRAL: Full disconnect, fake revving ===
            if (_inNeutral)
            {
                // Block all drive input
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, INPUT_VEH_ACCELERATE, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, INPUT_VEH_BRAKE, true);

                // Read throttle input for fake revving
                float throttleInput = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, INPUT_VEH_ACCELERATE);

                // Set gear to 1 (not 0/reverse), clutch to 0, throttle to 0
                VehicleMemory.SetCurrentGear(_vehicle, 1);
                VehicleMemory.SetNextGear(_vehicle, 1);
                VehicleMemory.SetClutch(_vehicle, 0.0f);
                VehicleMemory.SetThrottle(_vehicle, 0.0f);

                // Fake revving: manually set RPM based on throttle input
                if (throttleInput > 0.1f)
                {
                    // Rev up based on input (idle ~0.2, max ~1.0)
                    float targetRPM = 0.2f + (throttleInput * 0.75f);
                    _cachedRPM = Math.Min(targetRPM, 0.95f);
                    VehicleMemory.SetCurrentRPM(_vehicle, _cachedRPM);
                }
                else
                {
                    // Return to idle
                    _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);
                    if (_cachedRPM > 0.25f)
                    {
                        VehicleMemory.SetCurrentRPM(_vehicle, _cachedRPM - 0.05f);
                    }
                }

                if (_updateCounter > 10000) _updateCounter = 0;
                return;
            }

            // === REVERSE: RT = reverse, LT = brake/stop ===
            if (_inReverse)
            {
                float vehicleSpeed = _vehicle.Speed;

                // Force gear 0 (reverse)
                VehicleMemory.SetCurrentGear(_vehicle, 0);
                VehicleMemory.SetNextGear(_vehicle, 0);
                VehicleMemory.SetClutch(_vehicle, 1.0f);

                // Disable controls first, then read what WOULD have been pressed
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, INPUT_VEH_ACCELERATE, true);
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, INPUT_VEH_BRAKE, true);

                // Read disabled values - these are what the player is pressing
                float rtRaw = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, INPUT_VEH_ACCELERATE);
                float ltRaw = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, INPUT_VEH_BRAKE);

                // Filter: if we injected last frame, that value is fake
                float realRT = _injectedGas ? 0f : rtRaw;
                float realLT = _injectedBrake ? 0f : ltRaw;

                // Reset injection tracking for this frame
                _injectedBrake = false;
                _injectedGas = false;

                // Use ENABLE_CONTROL_ACTION to re-enable the control we want to use
                // Then SET_CONTROL_VALUE_NEXT_FRAME to inject our value
                const ulong SET_CONTROL_VALUE_NEXT_FRAME = 0xE8A25867FBA3B05E;

                // Priority: RT (reverse) takes precedence over LT (brake)
                if (realRT > 0.1f)
                {
                    // Player pressing RT - make car reverse
                    // Re-enable brake control so our injection works
                    Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, INPUT_VEH_BRAKE, true);
                    Function.Call((Hash)SET_CONTROL_VALUE_NEXT_FRAME, 0, INPUT_VEH_BRAKE, realRT);
                    _injectedBrake = true;
                }
                else if (realLT > 0.1f)
                {
                    // Player pressing LT - slow down/stop
                    // Only apply forward force if actually moving backwards
                    if (vehicleSpeed > 1.0f)
                    {
                        // Moving - apply gas to slow down
                        Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, INPUT_VEH_ACCELERATE, true);
                        Function.Call((Hash)SET_CONTROL_VALUE_NEXT_FRAME, 0, INPUT_VEH_ACCELERATE, realLT);
                        _injectedGas = true;
                    }
                    else
                    {
                        // Stopped or nearly stopped - use handbrake to hold position
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, true);
                    }
                }
                else
                {
                    // Nothing pressed - release handbrake
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, false);
                }

                _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);
                if (_updateCounter > 10000) _updateCounter = 0;
                return;
            }

            // === FORWARD GEARS: Force gear EVERY FRAME ===
            // Full takeover - don't let game's auto transmission interfere
            VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
            VehicleMemory.SetNextGear(_vehicle, _targetGear);
            VehicleMemory.SetClutch(_vehicle, 1.0f);

            // Block auto-reverse in 1st gear when stopped
            if (_targetGear == 1 && _vehicle.Speed < 0.5f)
            {
                // Disable brake to prevent reverse trigger, use handbrake instead
                bool isBraking = Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, INPUT_VEH_BRAKE);
                if (isBraking)
                {
                    Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, INPUT_VEH_BRAKE, true);
                    // Apply handbrake to stop without reversing
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, true);
                }
                else
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, false);
                }
            }

            // === REDLINE PENALTY (every N frames) ===
            if (_updateCounter % REDLINE_CHECK_INTERVAL == 0)
            {
                _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);

                // Cache top gear less frequently
                if (_updateCounter % 60 == 0)
                    _cachedTopGear = VehicleMemory.GetTopGear(_vehicle);

                // Only apply penalty if not in top gear and in redline zone
                if (_targetGear < _cachedTopGear && _cachedRPM > RedlineStart)
                {
                    float redlineProgress = (_cachedRPM - RedlineStart) / (1.0f - RedlineStart);
                    redlineProgress = Math.Min(1.0f, Math.Max(0.0f, redlineProgress));

                    float throttleMultiplier = RedlinePenaltyMild - (redlineProgress * (RedlinePenaltyMild - RedlinePenaltyMax));

                    float currentThrottle = VehicleMemory.GetThrottle(_vehicle);
                    if (currentThrottle > 0.1f)
                    {
                        VehicleMemory.SetThrottle(_vehicle, currentThrottle * throttleMultiplier);
                    }
                }
            }

            // === AUTO-DOWNSHIFT AT STOP (every 30 frames) ===
            if (_updateCounter % 30 == 0 && _targetGear > 1)
            {
                float speed = _vehicle.Speed;
                if (speed < 0.5f)
                {
                    _targetGear = 1;
                    VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
                    VehicleMemory.SetNextGear(_vehicle, _targetGear);
                    OnGearChanged?.Invoke(_targetGear);
                }
            }

            // Reset counter to prevent overflow
            if (_updateCounter > 10000) _updateCounter = 0;
        }

        /// <summary>
        /// Get shift indicator: 0 = none, 1 = shift up recommended, -1 = at redline (shift NOW)
        /// </summary>
        public int GetShiftIndicator()
        {
            if (!_enabled || _vehicle == null || !_vehicle.Exists()) return 0;

            // No shift indicator in reverse or neutral
            if (_inReverse || _inNeutral) return 0;

            float rpm = VehicleMemory.GetCurrentRPM(_vehicle);
            byte topGear = VehicleMemory.GetTopGear(_vehicle);

            // Already in top gear - no shift indicator
            if (_targetGear >= topGear) return 0;

            // At redline - urgent shift indicator
            if (rpm >= RedlineStart)
                return -1; // Redline warning

            // In optimal shift zone
            if (rpm >= ShiftUpRPM)
                return 1; // Shift up recommended

            return 0;
        }

        /// <summary>
        /// Get current RPM for HUD display (uses cached value for performance)
        /// </summary>
        public float GetCurrentRPM()
        {
            return _cachedRPM;
        }

        /// <summary>
        /// Check if currently in redline zone (for HUD effects, uses cached values)
        /// </summary>
        public bool IsInRedline()
        {
            if (!_enabled) return false;
            // Only show redline in forward gears (not reverse or neutral)
            return !_inReverse && !_inNeutral && _targetGear < _cachedTopGear && _cachedRPM >= RedlineStart;
        }

        /// <summary>
        /// Get display string for current gear
        /// </summary>
        public string GetGearString()
        {
            if (_inReverse) return "R";
            if (_inNeutral) return "N";
            return _targetGear.ToString();
        }
    }
}
