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

        // Redline (arcade): FULL power all the way up — NO throttle cut, ever. With the clutch engaged
        // the RPM is locked to wheel speed, so any cut at the "limiter" can never release (the wheels
        // hold the RPM there) and the car crawls at the cut throttle — that was the 25% dump in 1st.
        // The game clamps RPM at 1.0 naturally; a gear simply tops out at its ratio ceiling.
        public float RedlineStart { get; set; } = 0.93f;        // HUD redline zone start (shift indicator)

        // Engine braking (arcade): lifting off decelerates the car, harder in lower gears and at
        // higher RPM — so downshifting visibly slows you down (decel jumps as RPM rises + gear drops).
        public float EngineBrakeStrength { get; set; } = 5.0f;  // m/s^2 at RPM 1.0 in 1st gear

        // Perfect shift (NFS drag style): shifting up in the sweet spot is rewarded ONLY with a
        // "GREAT SHIFT!" callout — no power boost (we want skill-feedback, not a Forza torque cheat).
        public float PerfectShiftMin { get; set; } = 0.80f;
        public float PerfectShiftMax { get; set; } = 0.97f;
        private int _perfectFlashUntil = 0;                     // "GREAT SHIFT!" popup window
        public bool LastShiftPerfect { get; private set; } = false;

        // ---- NOS / nitrous (arcade, NFS-style) ----
        public bool NosInstalled { get; set; } = false;        // purchased in LSC
        public float NosLevel { get; set; } = 1f;              // 0..1 bottle fill
        public bool NosActive { get; private set; } = false;
        public float NosDrainPerSec { get; set; } = 0.40f;     // ~2.5s of continuous boost
        public float NosRefillPerSec { get; set; } = 0.09f;    // slow passive refill
        public float NosPowerMult { get; set; } = 1.85f;       // torque multiplier while spraying
        public float NosShove { get; set; } = 4.0f;            // extra forward m/s^2 kick while spraying
        private System.Windows.Forms.Keys _nosKey = System.Windows.Forms.Keys.N;

        // Anti-bog (arcade forgiveness): too high a gear at low RPM never stalls — a hidden torque
        // assist keeps the car pulling away, just lazily.
        public float BogRPM { get; set; } = 0.50f;              // below this RPM the assist ramps in
        public float BogAssistMax { get; set; } = 4.0f;         // max torque multiplier at idle RPM

        // Gear-ceiling hold: when a pinned gear reaches its ratio ceiling, the GAME cuts throttle and
        // lets RPM collapse to idle (measured: rpm 1.0 -> 0.27 at constant max speed). Arcade boxes
        // scream at redline instead. We learn each gear's rpm/speed ratio and, when the game's cut is
        // detected (rpm far below what speed implies, pedal down), hold RPM at redline + full throttle.
        private float _gearRpmPerSpeed = 0f;                    // learned ratio for the current gear
        private ushort _ratioGear = 0;

        // Telemetry/autorun harness (active only while an autorun is triggered via autorun.txt)
        private int _autorunUntil = 0;
        private int _runStartTime = 0;
        private string _autorunMode = "auto";
        private System.Text.StringBuilder _csv = null;
        // Runway-end safety: if scripts/ExtendedLSC/runway_end.txt exists ("x;y;z"), a run ends and
        // brakes hard once the car gets within this range of it (no more desert excursions).
        private GTA.Math.Vector3 _runwayEnd = default(GTA.Math.Vector3);
        private bool _runwayEndLoaded = false;
        private int _autoBrakeUntil = 0;
        private float _lastTorqueMult = 1f;
        private const ulong SET_CONTROL_VALUE_NEXT_FRAME_HASH = 0xE8A25867FBA3B05E;
        private const ulong SET_VEHICLE_CHEAT_POWER_INCREASE_HASH = 0xB59E4BD37AE292DB;
        private static string DataDir => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC");

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

            // Disable the game's auto-shift clutch drops — without this the auto-box state machine
            // fights the pinned gear every physics step and torque dies (see VehicleMemory patches).
            VehicleMemory.ApplyShiftPatches();

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

            // Give the game its automatic gearbox back
            VehicleMemory.RestoreShiftPatches();

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

            // PERFECT SHIFT: catching the sweet spot at the moment of the shift rewards a brief
            // torque boost (applied per-frame in Update via cheat-power).
            float rpmAtShift = VehicleMemory.GetCurrentRPM(_vehicle);
            LastShiftPerfect = rpmAtShift >= PerfectShiftMin && rpmAtShift <= PerfectShiftMax;
            if (LastShiftPerfect)
                _perfectFlashUntil = Game.GameTime + 800;   // "GREAT SHIFT!" callout (drawn in Update)

            // Instant shift (arcade - no clutch delay)
            _targetGear++;

            // Set the new gear immediately
            VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
            VehicleMemory.SetNextGear(_vehicle, _targetGear);

            // REV-MATCH SNAP: with the clutch dips patched out, the game only LERPS the rpm toward the
            // new gear's value (~0.8s glide that parks the tach at the top of every gear). Snap it from
            // the real ratios instead — instant dual-clutch-style drop to the correct revs.
            float rOld = VehicleMemory.GetGearRatio(_vehicle, _targetGear - 1);
            float rNew = VehicleMemory.GetGearRatio(_vehicle, _targetGear);
            if (rOld > 0.01f && rNew > 0.01f && rNew < rOld)
            {
                float snapped = Math.Max(0.2f, rpmAtShift * (rNew / rOld));
                VehicleMemory.SetCurrentRPM(_vehicle, snapped);
                _cachedRPM = snapped;
            }

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

            // TRUE REV-MATCH from the real gear ratios (blip to exactly where the shorter gear puts
            // the revs at this speed; falls back to the old fixed bump if ratios are unreadable).
            float currentRPM = VehicleMemory.GetCurrentRPM(_vehicle);
            float rHigh = VehicleMemory.GetGearRatio(_vehicle, _targetGear + 1);
            float rLow = VehicleMemory.GetGearRatio(_vehicle, _targetGear);
            float targetRPM = (rHigh > 0.01f && rLow > rHigh)
                ? Math.Min(0.98f, currentRPM * (rLow / rHigh))
                : Math.Min(0.95f, currentRPM + RevMatchStrength);
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
            // THROTTLE TAKEOVER: the game's auto-box logic still runs underneath the gear pin, and
            // whenever it disagrees with our gear it enters a perpetual mid-shift THROTTLE CUT
            // (measured: engine throttle forced to 0.00 in 3rd at full pedal -> car decays to a stop).
            // Owning the throttle field every frame ends the fight for good (same approach as ikt's MT).
            // (throttlePedal is computed below; the write happens after it's read.)

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

            // === ENGINE FEEL (every frame) ===
            _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);
            if (_updateCounter % 60 == 0)
                _cachedTopGear = VehicleMemory.GetTopGear(_vehicle);
            int now = Game.GameTime;

            // 1) NO LIMITER CUT — full power to each gear's ratio ceiling (see RedlineStart comment:
            //    RPM is wheel-locked in gear, so any cut would latch on permanently).

            // Effective pedal: physical input OR autorun injection. GET_CONTROL_NORMAL can't see
            // SET_CONTROL_VALUE_NEXT_FRAME injection (measured: engine braking was eating 3rd gear
            // alive at "full throttle" during test runs), so the injection flag from last frame counts.
            float throttlePedal = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, INPUT_VEH_ACCELERATE);
            if (_injectedGas && throttlePedal < 1f) throttlePedal = 1f;
            _injectedGas = false;   // re-set by HandleAutorun at the end of this frame if still injecting
            float speedNow = _vehicle.Speed;

            // Throttle takeover (see comment at the gear pin): pedal drives the engine, period.
            if (throttlePedal > 0.05f)
                VehicleMemory.SetThrottle(_vehicle, throttlePedal);

            // 1a) (Removed gear-ceiling RPM hold: the clutch-drop patch fixes power delivery so RPM now
            //      tracks the gear naturally. The old hold learned a per-gear rpm/speed ratio that got
            //      poisoned by the high-rpm/low-speed launch sample and then pinned RPM to redline for
            //      the whole gear — that was the "always at the top of the gear after shifting" bug.)

            // 1b) ENGINE BRAKING — lifting off drags the car down, scaled by RPM and (inversely) gear,
            //     so a downshift instantly deepens the drag. This is what makes downshifts slow you.
            if (throttlePedal < 0.10f && speedNow > 2.0f && _cachedRPM > 0.25f)
            {
                float decel = EngineBrakeStrength * _cachedRPM / Math.Max(1, (int)_targetGear);
                float dv = decel * Game.LastFrameTime;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle.Handle, 1,
                    0f, -dv, 0f, 0f, 0f, 0f, 0, true /*dir relative*/, true, true /*mass-relative*/, false, true);
            }

            // 1c) "GREAT SHIFT!" callout (silent, pops then fades) — right side, clear of the minimap.
            if (now < _perfectFlashUntil)
            {
                float t = (_perfectFlashUntil - now) / 800f;               // 1 -> 0 over the window
                float scale = 0.85f + 0.45f * Math.Max(0f, t - 0.7f) / 0.3f;  // punchy pop at the start
                int alpha = (int)(255 * Math.Min(1f, t / 0.4f));             // fade out at the end
                var txt = new GTA.UI.TextElement("GREAT SHIFT!",
                    new System.Drawing.PointF(0.82f * GTA.UI.Screen.Width, 0.40f * GTA.UI.Screen.Height), scale,
                    System.Drawing.Color.FromArgb(alpha, 120, 235, 255),
                    GTA.UI.Font.Pricedown, GTA.UI.Alignment.Center);
                txt.Outline = true;
                txt.Draw();
            }

            // 2) TORQUE PIPELINE (anti-bog assist only — perfect shift gives NO power boost now).
            float mult = 1f;
            // Anti-bog: in too high a gear at low RPM the GAME's drive force is literally ZERO
            // (measured: full pedal in 3rd at 6 m/s -> rpm pinned at idle, car decays to a stop),
            // so a torque multiplier can't help (0 x N = 0). Instead: a direct physical push that
            // fades out as the engine wakes up — guaranteed arcade pull-away — plus a lugging RPM
            // floor so the engine sounds like it's working rather than dead.
            if (_targetGear >= 2 && _cachedRPM < BogRPM && throttlePedal > 0.15f && speedNow < 30f)
            {
                float bog = (BogRPM - _cachedRPM) / BogRPM;               // 0..1 (1 = idle RPM)
                float assistAccel = 3.5f * bog * throttlePedal;           // m/s^2 of hidden push
                float dvA = assistAccel * Game.LastFrameTime;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle.Handle, 1,
                    0f, dvA, 0f, 0f, 0f, 0f, 0, true, true, true, false, true);
                // Lugging audio/tach: engine chugs at low revs instead of reading dead-idle
                float lugRPM = 0.30f + 0.15f * throttlePedal;
                if (_cachedRPM < lugRPM)
                {
                    VehicleMemory.SetCurrentRPM(_vehicle, lugRPM);
                    _cachedRPM = lugRPM;
                }
            }
            // 2b) NOS / NITROUS — hold the key while on the gas to spray: big torque + a forward shove,
            //     bottle drains while active and slowly refills when not. Rides the same torque pipeline.
            float dt = Game.LastFrameTime;
            // CONTROLLER FIRST (user is a controller player): Xbox X = INPUT_VEH_HANDBRAKE (control 76).
            // In a straight-line drag you don't need handbrake, so we read it as NOS and suppress the
            // handbrake itself only while NOS is eligible (throttle down + moving) so normal handbraking
            // still works at low/zero throttle. Keyboard N is the secondary fallback.
            bool nosBtn = NosInstalled &&
                          (Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, 76) || Game.IsKeyPressed(_nosKey));
            NosActive = nosBtn && NosLevel > 0.02f && throttlePedal > 0.45f && speedNow > 1.5f;
            if (NosActive)
            {
                // Spraying under throttle: this is NOS, not a handbrake — suppress the handbrake input.
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 76, true);
                NosLevel = Math.Max(0f, NosLevel - NosDrainPerSec * dt);
                mult *= NosPowerMult;
                float dvN = NosShove * dt;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle.Handle, 1,
                    0f, dvN, 0f, 0f, 0f, 0f, 0, true, true, true, false, true);
            }
            else if (!nosBtn && NosLevel < 1f)
            {
                NosLevel = Math.Min(1f, NosLevel + NosRefillPerSec * dt);
            }

            _lastTorqueMult = mult;
            Function.Call((Hash)SET_VEHICLE_CHEAT_POWER_INCREASE_HASH, _vehicle.Handle, mult);

            // 3) AUTORUN TEST HARNESS — file-triggered full-throttle runway runs with CSV telemetry
            //    (development tool; inert unless scripts/ExtendedLSC/autorun.txt appears).
            HandleAutorun(now);

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
        /// Autorun harness: when scripts/ExtendedLSC/autorun.txt appears (format "seconds;mode",
        /// mode = auto | hold1), hold full throttle for that long, optionally auto-shifting in the
        /// perfect window, while logging per-frame CSV telemetry to run_telemetry.csv.
        /// </summary>
        private void HandleAutorun(int now)
        {
            try
            {
                // Post-run safety braking (also covers proximity-triggered early stops)
                if (now < _autoBrakeUntil)
                {
                    Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, INPUT_VEH_BRAKE, true);
                    Function.Call((Hash)SET_CONTROL_VALUE_NEXT_FRAME_HASH, 0, INPUT_VEH_BRAKE, 1.0f);
                }

                if (_autorunUntil == 0)
                {
                    if (_updateCounter % 30 != 0) return;
                    string trigger = System.IO.Path.Combine(DataDir, "autorun.txt");
                    if (!System.IO.File.Exists(trigger)) return;
                    string[] parts = System.IO.File.ReadAllText(trigger).Trim().Split(';');
                    System.IO.File.Delete(trigger);
                    int secs = 8;
                    int.TryParse(parts[0], out secs);
                    _autorunMode = parts.Length > 1 ? parts[1].Trim() : "auto";
                    _autorunUntil = now + Math.Max(1, secs) * 1000;
                    _runStartTime = now;
                    _csv = new System.Text.StringBuilder("ms,speed,rpm,gear,throttle,mult\n");
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, false);
                    // Load the runway end marker (if recorded) for proximity auto-stop
                    if (!_runwayEndLoaded)
                    {
                        _runwayEndLoaded = true;
                        try
                        {
                            string endFile = System.IO.Path.Combine(DataDir, "runway_end.txt");
                            if (System.IO.File.Exists(endFile))
                            {
                                var pe = System.IO.File.ReadAllText(endFile).Trim().Split(';');
                                _runwayEnd = new GTA.Math.Vector3(
                                    float.Parse(pe[0], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(pe[1], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(pe[2], System.Globalization.CultureInfo.InvariantCulture));
                                Log?.Invoke($"[ELSC MT] Runway end marker loaded: {_runwayEnd}");
                            }
                        }
                        catch { }
                    }
                    Log?.Invoke($"[ELSC MT] AUTORUN start: {secs}s mode={_autorunMode}");
                    return;
                }

                // Proximity auto-stop: end the run and brake hard before the runway runs out
                if (_runwayEnd != default(GTA.Math.Vector3) &&
                    _vehicle.Position.DistanceTo2D(_runwayEnd) < 80f)
                {
                    _autorunUntil = now;   // forces the completion branch below this frame
                    _autoBrakeUntil = now + 3000;
                    Log?.Invoke("[ELSC MT] AUTORUN: runway end ahead - braking");
                }

                if (now < _autorunUntil)
                {
                    // Mode "downs": accelerate for the first 45% of the run, then coast and downshift
                    // every 900ms — measures engine-brake decel per gear. Other modes: hold throttle.
                    int elapsed = now - _runStartTime;
                    int total = _autorunUntil - _runStartTime;
                    bool coasting = _autorunMode == "downs" && elapsed > total * 45 / 100;

                    if (!coasting)
                    {
                        Function.Call(Hash.ENABLE_CONTROL_ACTION, 0, INPUT_VEH_ACCELERATE, true);
                        Function.Call((Hash)SET_CONTROL_VALUE_NEXT_FRAME_HASH, 0, INPUT_VEH_ACCELERATE, 1.0f);
                        _injectedGas = true;
                    }
                    else if (_targetGear > 1 && (DateTime.Now - _lastShiftTime).TotalMilliseconds > 900)
                    {
                        ShiftDown();
                    }

                    // Auto-shift inside the perfect window (also exercises the boost).
                    // Wheelspin guard: launch wheelspin reads ~0.9 RPM at walking pace, so also require
                    // a minimum ground speed per gear before shifting (else 1->2->3 fires instantly and
                    // the run dies in a bogged high gear).
                    if (!coasting && (_autorunMode == "auto" || _autorunMode == "downs")
                        && _cachedRPM >= 0.92f && _targetGear < _cachedTopGear
                        && _vehicle.Speed > 7f * _targetGear
                        && (DateTime.Now - _lastShiftTime).TotalMilliseconds > 600)
                        ShiftUp();

                    _csv?.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1:F2},{2:F4},{3},{4:F3},{5:F2}\n",
                        now - _runStartTime, _vehicle.Speed, _cachedRPM, _targetGear,
                        VehicleMemory.GetThrottle(_vehicle), _lastTorqueMult);
                }
                else
                {
                    string outPath = System.IO.Path.Combine(DataDir, "run_telemetry.csv");
                    if (_csv != null) System.IO.File.WriteAllText(outPath, _csv.ToString());
                    _csv = null;
                    _autorunUntil = 0;
                    Log?.Invoke("[ELSC MT] AUTORUN complete -> run_telemetry.csv");
                }
            }
            catch (Exception ex) { Log?.Invoke($"[ELSC MT] Autorun error: {ex.Message}"); _autorunUntil = 0; _csv = null; }
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
