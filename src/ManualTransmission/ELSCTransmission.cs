using System;
using System.Collections.Generic;
using System.Drawing;
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
        public float PerfectShiftMin { get; set; } = 0.90f;
        public float PerfectShiftMax { get; set; } = 0.98f;
        private int _perfectFlashUntil = 0;                     // "GREAT SHIFT!" popup window
        public bool LastShiftPerfect { get; private set; } = false;

        // ---- NOS / nitrous (arcade, NFS-style) ----
        public bool NosInstalled { get; set; } = false;        // purchased in LSC
        public float NosLevel { get; set; } = 1f;              // 0..1 bottle fill
        public bool NosActive { get; private set; } = false;
        public float NosDrainPerSec { get; set; } = 0.40f;     // ~2.5s of continuous boost
        public float NosRefillPerSec { get; set; } = 0.015f;   // super-slow passive refill (~65s to fill)
        public float PerfectShiftNosBump { get; set; } = 0.06f; // a great shift tops up the bottle a little
        public float NosPowerMult { get; set; } = 1.85f;       // torque multiplier while spraying
        public float NosShove { get; set; } = 4.0f;            // extra forward m/s^2 kick while spraying
        public bool NosBoostFx { get; set; } = true;           // SET_VEHICLE_BOOST_ACTIVE whoosh + screen blur
        public Color NosFlameColor { get; set; } = Color.FromArgb(255, 60, 120, 255); // exhaust flame tint (blue)
        private System.Windows.Forms.Keys _nosKey = System.Windows.Forms.Keys.N;
        private bool _nosWasActive = false;
        private static readonly string[] _exhaustBones =
            { "exhaust", "exhaust_2", "exhaust_3", "exhaust_4", "exhaust_5", "exhaust_6", "exhaust_7" };
        private const ulong SET_VEHICLE_BOOST_ACTIVE_HASH = 0x4A04DE7CAB2739A1;
        // Real LS Tuners nitrous flame (proper lifecycle: install on spray edge, uninstall on release).
        private const ulong SET_OVERRIDE_NITROUS_LEVEL_HASH = 0xC8E9B6B71B8E660D;
        private const ulong SET_NITROUS_IS_VISIBLE_HASH = 0x465EEA70AF251045;
        private const ulong SET_NITROUS_IS_ACTIVE_HASH = 0x9E566EA551F4F1A6;
        private const ulong FULLY_CHARGE_NITROUS_HASH = 0x1A2BCC8C636F9226;
        private const ulong CLEAR_NITROUS_HASH = 0xC889AE921400E1ED;
        private bool _nitrousOn = false;            // true while the native nitrous flame is running
        private Vehicle _nitrousVeh = null;         // the vehicle it's installed on (to strip on release)
        private const string NITROUS_SENTINEL = "@nitrous";  // catalog Dict marker for the native-nitrous entry

        /// <summary>Fully strip the game's nitrous system off a vehicle (uninstall override + hide + deactivate + clear).</summary>
        private void StripNitrous(Vehicle veh)
        {
            if (veh == null || !veh.Exists()) return;
            Function.Call((Hash)SET_NITROUS_IS_ACTIVE_HASH, veh.Handle, false);
            Function.Call((Hash)SET_OVERRIDE_NITROUS_LEVEL_HASH, veh.Handle, false, 0f, 0f, 0f, true);
            Function.Call((Hash)SET_NITROUS_IS_VISIBLE_HASH, veh.Handle, false);
            Function.Call((Hash)CLEAR_NITROUS_HASH, veh.Handle);
        }

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
        private int _dynoGearDelta = 0;   // autorun.txt 3rd field: extra gears to apply at launch (tests the +gears fix)
        private System.Text.StringBuilder _csv = null;
        // Runway-end safety: if scripts/ExtendedLSC/runway_end.txt exists ("x;y;z"), a run ends and
        // brakes hard once the car gets within this range of it (no more desert excursions).
        private GTA.Math.Vector3 _runwayEnd = default(GTA.Math.Vector3);
        private bool _runwayEndLoaded = false;
        // Dyno start: if scripts/ExtendedLSC/runway_start.txt exists ("x;y;z;heading"), an autorun first
        // teleports the car here, zeroes velocity, and settles for a moment so every run starts identical.
        private GTA.Math.Vector3 _runwayStart = default(GTA.Math.Vector3);
        private float _runwayStartHeading = 0f;
        private bool _runwayStartLoaded = false;
        private bool _staging = false;       // teleported, holding still, letting suspension settle
        private int _stageUntil = 0;         // settle window end
        private int _stageSecs = 8;          // run length to start once staging completes
        private int _autoBrakeUntil = 0;
        private float _lastTorqueMult = 1f;
        // NFS-style upshift "kick": a brief power surge the moment a new gear engages, so every shift has a
        // satisfying punch (stronger when timed at the sweet spot). Identical on every car = consistent feel.
        private int _shiftKickUntil = 0;
        private float _shiftKickForce = 0f;      // forward lunge (m/s^2) — the punch you FEEL, set per shift
        private float _shiftKickPower = 0f;      // small added engine power for the note (kept low so the
                                                 // tires don't break loose → no post-shift RPM spike)
        private const int ShiftKickMs = 380;
        public float ShiftKickForceBase { get; set; } = 16f;       // tunable (INI)
        public float ShiftKickForcePerfect { get; set; } = 30f;    // tunable (INI)
        private const float ShiftKickPowerBase = 0.25f;
        private const float ShiftKickPowerPerfect = 0.45f;
        // The kick is scaled by how much this shift actually drops the revs, relative to a stock ~28% drop, so a
        // close-ratio shift (tight stock box, or a car whose powerband barely moves) gets a proportionally gentler
        // nudge instead of a full lunge — no more "substantial kickback that forces an immediate re-shift".
        private const float ReferenceShiftDrop = 0.28f;   // stock 5-gear geometric step (1 - 0.90/3.33^(1/4))
        // Shift feedback: controller rumble + exhaust pop + downshift over-rev guard (all tunable).
        public bool RumbleEnabled { get; set; } = true;
        public bool ExhaustPops { get; set; } = true;
        public float OverRevLimit { get; set; } = 1.02f;   // downshift refused if it would exceed this RPM
        private int _shiftPopUntil = 0;
        private int _popTick = 0;
        private const int ShiftPopMs = 130;
        // Dyno realism: a human never shifts at the exact same RPM twice, so randomize the auto-shift point
        // each gear (early-lug .. redline). This exercises imperfect shifts and surfaces any "acts strange
        // when not perfect" gearing behavior the old fixed-point harness always hid.
        private readonly Random _dynoRng = new Random();
        private float _dynoShiftRpm = 0.92f;
        // Rev limiter (bounce): hold each gear AT its redline instead of mushing past the ratio ceiling. The
        // engine "bounces" off the limiter (NFS feel) and the car stops gaining speed → a clear shift point.
        // Non-latching: a TIME-based cut/on cycle (not RPM-based) so the forced on-phase keeps it from dying,
        // and the moment you upshift (RPM drops below the limit) it releases on its own.
        public float LimiterRpm { get; set; } = 0.985f;   // tunable (INI)
        private const int LimiterCutMs = 130;   // mostly cut: the brief on-phase is just for the audible bounce,
        private const int LimiterOnMs = 12;     // not enough power to keep accelerating past the ceiling
        private const float LimiterMinSpeed = 6f;  // don't limit at launch (wheelspin reads false-high RPM)
        private bool _limiterCut = false;
        private const ulong SET_CONTROL_VALUE_NEXT_FRAME_HASH = 0xE8A25867FBA3B05E;
        private const ulong SET_VEHICLE_CHEAT_POWER_INCREASE_HASH = 0xB59E4BD37AE292DB;
        private static string DataDir => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC");

        // Read-only state
        public bool IsEnabled => _enabled;

        // Dyno (dev tool): cheap throttled poll for a pending autorun trigger, so Main can force-enable MT
        // on ANY car (even without MT purchased) and the dyno harness can run on it.
        private int _dynoPollTick = 0;
        private bool _dynoPendingCached = false;
        public bool DynoPending()
        {
            if ((++_dynoPollTick % 20) != 0) return _dynoPendingCached;
            try { _dynoPendingCached = System.IO.File.Exists(System.IO.Path.Combine(DataDir, "autorun.txt")); }
            catch { _dynoPendingCached = false; }
            return _dynoPendingCached;
        }
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
                // Strip any game nitrous a prior build installed on this car (it would auto-trigger + never stop).
                StripNitrous(vehicle);

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

            // NFS gearing: re-space the gears so every upshift drops RPM by the same satisfying amount.
            ApplyNfsGearing(_vehicle);

            // Natural exhaust crackle/pops on deceleration (off-throttle) — the NFS anti-lag sound.
            Function.Call((Hash)0x2BE4BC731D039D5A, true);   // ENABLE_VEHICLE_EXHAUST_POPS

            Log?.Invoke($"[ELSCTransmission] Enabled (Arcade Mode), gear: {_targetGear}, top gear: {_cachedTopGear}");
        }

        // Stock GTA gear ratios bunch up at the top (4th->5th is only a ~15% RPM drop), so upper-gear upshifts
        // barely move the tach and the shift kick is wasted. Re-space every gear GEOMETRICALLY between the
        // universal 1st (3.33) and top (0.90) ratios → a constant ~RPM drop per shift on EVERY car, so shifts
        // feel meaty and consistent (the NFS feel). 1st and top stay stock, so launch + top speed are unchanged.
        private const float NfsGearFirst = 3.3333f;
        private const float NfsGearTop = 0.90f;
        private float[] _appliedRatios = null;   // verbatim cache of the last ratios written (for the periodic re-assert)
        /// <param name="stockGears">The car's ORIGINAL drive-gear count. The geometric step is anchored to it so
        /// the stock gears keep their 3.33->0.90 spacing/top speed, and any ADDED gears (current count &gt; stock)
        /// EXTEND the range downward at the SAME per-shift step (constant RPM drop) instead of re-compressing every
        /// gear into a fixed range. 0 = anchor to the current count (legacy behavior, no extension).</param>
        public void ApplyNfsGearing(Vehicle v, int stockGears = 0)
        {
            if (v == null || !v.Exists()) return;
            int n = VehicleMemory.GetTopGear(v);   // current total drive-gear count (may be stock + tuned)
            if (n < 2 || n > 10) return;
            int baseN = (stockGears >= 2 && stockGears <= 10) ? stockGears : n;
            float step = (float)Math.Pow(NfsGearTop / NfsGearFirst, 1.0 / (baseN - 1));  // constant per-shift ratio step
            var cache = new float[n + 2];                                             // slot index = gear g + 1
            for (int i = 0; i < cache.Length; i++) cache[i] = float.NaN;
            for (int g = 1; g <= n; g++)
            {
                float ratio = NfsGearFirst * (float)Math.Pow(step, g - 1);            // g==baseN -> 0.90; g>baseN -> taller
                VehicleMemory.SetGearRatio(v, g + 1, ratio);                          // array index 2 = 1st gear
                cache[g + 1] = ratio;
            }
            _appliedRatios = cache;   // remember EXACTLY what we wrote so the periodic re-assert restores it verbatim
        }

        /// <summary>Re-write the exact ratios last applied by ApplyNfsGearing. The periodic re-assert must use THIS,
        /// not ApplyNfsGearing — recomputing without the stock count would re-compress an extended-range tune and,
        /// because changing a live gear's ratio shifts the RPM-per-speed instantly, slam the tach mid-drive (feels
        /// like a phantom auto-upshift the gear indicator never shows). Writing the same values is a no-op = no jump.</summary>
        private void ReassertRatios()
        {
            if (_appliedRatios == null || _vehicle == null || !_vehicle.Exists()) return;
            for (int slot = 2; slot < _appliedRatios.Length; slot++)
            {
                float r = _appliedRatios[slot];
                if (!float.IsNaN(r)) VehicleMemory.SetGearRatio(_vehicle, slot, r);
            }
        }

        /// <summary>Re-read the live top-gear count and re-space the ratios. Call AFTER changing the live top gear
        /// (e.g. Vehicle Tuning "+N gears") so MT knows the new count and the new gear gets a real ratio. Pass the
        /// car's stock gear count so added gears extend the range (constant shift spacing) instead of compressing.</summary>
        public void RefreshGears(int stockGears = 0)
        {
            if (_vehicle == null || !_vehicle.Exists()) return;
            _cachedTopGear = VehicleMemory.GetTopGear(_vehicle);
            ApplyNfsGearing(_vehicle, stockGears);
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

            if (_nosWasActive && _vehicle != null && _vehicle.Exists())
                Function.Call((Hash)SET_VEHICLE_BOOST_ACTIVE_HASH, _vehicle.Handle, false);
            _nosWasActive = false;
            StopExhaustFlames();      // kill any looped exhaust flames
            StripNitrous(_vehicle);   // strip any game nitrous left installed

            _enabled = false;
            _inNeutral = false;
            _inReverse = false;
            Log?.Invoke("[ELSCTransmission] Disabled");
        }

        /// <summary>
        /// Fire one short "veh_backfire" flame out of every exhaust bone, tinted to <paramref name="color"/>.
        /// veh_backfire is a brief pop (it doesn't truly loop), so this must be called EVERY FRAME to read as a
        /// continuous flame — auto-disposing non-looped FX, so there are no handles to leak. Public so the LSC
        /// menu can use it to preview the flame colour while the player holds the NOS button. The "core" asset is
        /// requested on first use and the flames begin once it is resident.
        /// </summary>
        // ---- Exhaust-FX showcase ----
        // A catalog of candidate exhaust effects (fire / fireworks / flare / spark) for the player to preview and
        // pick from in the LSC menu, labelled NOS 1..N. Each entry carries its asset dict, effect name, a rearward
        // rotation, and a scale. The fireworks entries are fully tint-aware; the baked-fire ones ignore colour.
        // (Effects can't be bridge-tested — PTFX only start from the script tick — so these are evaluated live.)
        public class ExhaustFx
        {
            public string Name, Dict, Effect;
            public float RotX, RotY, RotZ, Scale;
            public bool Looped;            // continuous effects only start LOOPED; quick pops don't
            public float Drain;            // bottle drain per second (higher = faster burn / shorter)
            public float Power;            // engine torque multiplier while spraying (sustained accel)
            public float Shove;            // extra forward m/s^2 kick while spraying (instant push)
            public bool Colorable;         // true if the flame honours the chosen colour (tint-aware)
            public string Buff;            // short description of this tier's unique buff
            public ExhaustFx(string name, string dict, string effect, float rx, float ry, float rz, float scale,
                             bool looped, float drain, float power, float shove, bool colorable, string buff)
            {
                Name = name; Dict = dict; Effect = effect; RotX = rx; RotY = ry; RotZ = rz; Scale = scale;
                Looped = looped; Drain = drain; Power = power; Shove = shove; Colorable = colorable; Buff = buff;
            }
        }

        public static readonly ExhaustFx[] FxCatalog =
        {
            //                name                 dict             effect                           rX rY  rZ   scl    loop  drain   power  shove color  buff
            // drain scaled so the LONGEST variant (NOS 3) lasts 5s on a full bottle; others kept proportional.
            new ExhaustFx("NOS 1: Muzzle Flash",  "scr_carsteal4", "scr_carsteal5_car_muzzle_flash", 0f, 0f, -90f, 1.5f, false, 0.85f,  1.18f, 9.0f, false, "Launch kick - huge instant push, burns out fast (1.2s)"),
            new ExhaustFx("NOS 2: Backfire",      "core",          "veh_backfire",                   0f, 0f,  0f,  1.3f, false, 0.46f,  1.60f, 1.5f, false, "Acceleration - strong sustained pull, low kick (2.2s)"),
            new ExhaustFx("NOS 3: Nitrous Flame", "veh_xs_vehicle_mods", "veh_nitrous",              0f, 0f,  0f,  1.3f, true,  0.20f,  1.35f, 3.5f, false, "Endurance - moderate boost that lasts longest (5s)"),
            // NOS 4 (custom-colour tier) + the colour picker are deferred to a future update — they need a
            // tint-respecting flame asset (veh_nitrous is baked blue; veh_backfire doesn't tint reliably).
        };

        public int ActiveFxIndex { get; set; } = 0;   // which catalog entry the live NOS spray uses
        private const int FlameThrottle = 3;           // non-looped: emit every Nth call so pops don't stack each frame
        private int _flameTick = 0;
        private readonly List<int> _loopHandles = new List<int>();
        private int _loopActiveFx = -1;                // catalog index of the looped effect currently running, or -1

        /// <summary>Emit catalog effect <paramref name="fxIndex"/> out of every exhaust bone, tinted to colour.
        /// Looped effects start once and stay until StopExhaustFlames(); non-looped pops are re-fired per call.</summary>
        public void EmitExhaustFlames(Vehicle veh, Color color, int fxIndex)
        {
            if (veh == null || !veh.Exists()) return;
            if (fxIndex < 0 || fxIndex >= FxCatalog.Length) fxIndex = 0;
            ExhaustFx fx = FxCatalog[fxIndex];
            if (fx.Dict == NITROUS_SENTINEL)
            {
                // Real nitrous flame: install on the first frame, keep it charged + active every frame so the
                // GAME renders its own nitrous flames; StripNitrous() (on release) uninstalls so nothing persists.
                if (!_nitrousOn)
                {
                    StopExhaustFlames();   // clear any prior particle
                    Function.Call((Hash)SET_OVERRIDE_NITROUS_LEVEL_HASH, veh.Handle, true, 1f, 1f, 10f, true);
                    _nitrousOn = true; _nitrousVeh = veh;
                }
                Function.Call((Hash)FULLY_CHARGE_NITROUS_HASH, veh.Handle);
                Function.Call((Hash)SET_NITROUS_IS_ACTIVE_HASH, veh.Handle, true);
                return;
            }
            if (_nitrousOn) { StripNitrous(_nitrousVeh); _nitrousOn = false; }     // switched off the nitrous effect
            if (string.IsNullOrEmpty(fx.Dict)) { StopExhaustFlames(); return; }    // no-particle entry
            if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, fx.Dict))
            {
                Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, fx.Dict);
                return;
            }
            float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;

            if (fx.Looped)
            {
                if (_loopActiveFx == fxIndex) return;   // already running this effect — leave it on
                StopExhaustFlames();                    // switching effects: clear the old handles first
                foreach (string bone in _exhaustBones)
                {
                    int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, veh, bone);
                    if (bi < 0) continue;
                    Function.Call(Hash.USE_PARTICLE_FX_ASSET, fx.Dict);
                    int h = Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY_BONE,
                        fx.Effect, veh, 0f, 0f, 0f, fx.RotX, fx.RotY, fx.RotZ, bi, fx.Scale, false, false, false);
                    if (h != 0)
                    {
                        Function.Call(Hash.SET_PARTICLE_FX_LOOPED_COLOUR, h, r, g, b, 0);
                        _loopHandles.Add(h);
                    }
                }
                _loopActiveFx = fxIndex;
                return;
            }

            // Non-looped (brief pops) — re-fired every few frames for a continuous look.
            if (_loopActiveFx != -1) StopExhaustFlames();
            if (++_flameTick % FlameThrottle != 0) return;
            foreach (string bone in _exhaustBones)
            {
                int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, veh, bone);
                if (bi < 0) continue;
                Function.Call(Hash.USE_PARTICLE_FX_ASSET, fx.Dict);
                Function.Call(Hash.SET_PARTICLE_FX_NON_LOOPED_COLOUR, r, g, b);
                Function.Call(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY_BONE,
                    fx.Effect, veh, 0f, 0f, 0f, fx.RotX, fx.RotY, fx.RotZ, bi, fx.Scale, false, false, false);
            }
        }

        /// <summary>Stop any looped exhaust-flame handles (call when the spray / preview ends).</summary>
        public void StopExhaustFlames()
        {
            for (int i = 0; i < _loopHandles.Count; i++)
                Function.Call(Hash.STOP_PARTICLE_FX_LOOPED, _loopHandles[i], 0);
            _loopHandles.Clear();
            _loopActiveFx = -1;
            if (_nitrousOn) { StripNitrous(_nitrousVeh); _nitrousOn = false; }   // deactivate + uninstall native nitrous
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
            {
                _perfectFlashUntil = Game.GameTime + 800;   // "GREAT SHIFT!" callout (drawn in Update)
                NosLevel = Math.Min(1f, NosLevel + PerfectShiftNosBump);   // reward: top up the NOS bottle
            }

            // Instant shift (arcade - no clutch delay)
            _targetGear++;

            // Set the new gear immediately
            VehicleMemory.SetCurrentGear(_vehicle, _targetGear);
            VehicleMemory.SetNextGear(_vehicle, _targetGear);

            // Only kick/pop while the car is actually MOVING — shifting from a dead stop must not launch the car
            // forward or crackle. (The lunge is an impulse force; at a standstill it just shoves the car.)
            float shiftSpeed = 0f;
            try { shiftSpeed = _vehicle.Speed; } catch { }
            bool movingAtShift = shiftSpeed > 1.0f;   // ~2.2 mph — clearly in motion, not still/creeping

            // Ratio drop for THIS shift — drives both the rev-match snap and the kick strength.
            float rOld = VehicleMemory.GetGearRatio(_vehicle, _targetGear - 1);
            float rNew = VehicleMemory.GetGearRatio(_vehicle, _targetGear);
            float kickScale = 1f;
            if (rOld > 0.01f && rNew > 0.01f && rNew < rOld)
                kickScale = Math.Min(1f, Math.Max(0.3f, ((rOld - rNew) / rOld) / ReferenceShiftDrop));

            // NFS-style shift kick: a forward lunge (+ small power bump), scaled to the rev drop so close-ratio
            // shifts don't punt you straight back to redline. Bigger on a perfect shift.
            _shiftKickForce = (LastShiftPerfect ? ShiftKickForcePerfect : ShiftKickForceBase) * kickScale;
            _shiftKickPower = (LastShiftPerfect ? ShiftKickPowerPerfect : ShiftKickPowerBase) * kickScale;
            _shiftKickUntil = movingAtShift ? Game.GameTime + ShiftKickMs : 0;

            // Feedback: controller rumble (bigger on a perfect shift) + an exhaust backfire pop (in motion only).
            Rumble(LastShiftPerfect ? 200 : 110, LastShiftPerfect ? 230 : 150);
            if (ExhaustPops && !NosActive && movingAtShift) _shiftPopUntil = Game.GameTime + ShiftPopMs;

            // REV-MATCH SNAP: with the clutch dips patched out, the game only LERPS the rpm toward the
            // new gear's value (~0.8s glide that parks the tach at the top of every gear). Snap it from
            // the real ratios instead — instant dual-clutch-style drop to the correct revs.
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

            // OVER-REV PROTECTION: refuse a downshift that would spin the engine past redline at this speed
            // (e.g. slamming 5th->1st at 100mph). Same ratio convention as the rev-match below.
            {
                float rpmNow = VehicleMemory.GetCurrentRPM(_vehicle);
                float rCur = VehicleMemory.GetGearRatio(_vehicle, _targetGear);
                float rBelow = VehicleMemory.GetGearRatio(_vehicle, _targetGear - 1);
                if (rCur > 0.01f && rBelow > rCur && rpmNow * (rBelow / rCur) > OverRevLimit)
                {
                    Rumble(130, 90);   // buzz to signal the blocked downshift
                    return;
                }
            }

            // Instant shift
            _targetGear--;
            Rumble(90, 110);

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

            // Block reverse while in a forward gear — you must shift to R to back up. The game reverses
            // whenever the brake is held at a standstill; the old speed<0.5 guard let it slip through once the
            // car rolled back past 0.5 m/s. Instead clamp ANY backward motion to a stop, every frame, any speed.
            if (!_inReverse && !_inNeutral)
            {
                var relVel = Function.Call<GTA.Math.Vector3>(Hash.GET_ENTITY_SPEED_VECTOR, _vehicle.Handle, true);
                // ONLY fight the game's brake-induced auto-reverse, which only happens at a near-standstill:
                // SLOW backward creep, braking, on all wheels, on the ground. A real backward SLIDE (rolling
                // down a hill, etc.) is left alone — zeroing its momentum freezes the car, which is the bug.
                if (relVel.Y < -0.08f && relVel.Y > -1.5f)   // slow backward creep only (auto-reverse range)
                {
                    float brakeHeld = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, INPUT_VEH_BRAKE);
                    bool grounded = true, inAir = false;
                    try { grounded = Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, _vehicle); } catch { }
                    try { inAir = Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, _vehicle); } catch { }
                    if (brakeHeld > 0.5f && grounded && !inAir)
                        Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, _vehicle.Handle, 0f);
                }
            }

            // === ENGINE FEEL (every frame) ===
            _cachedRPM = VehicleMemory.GetCurrentRPM(_vehicle);
            if (_updateCounter % 60 == 0)
            {
                _cachedTopGear = VehicleMemory.GetTopGear(_vehicle);
                ReassertRatios();   // restore the EXACT applied ratios (never recompute — that re-compresses + slams RPM)
            }
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

            // REV LIMITER (bounce): when RPM reaches the limit under power, cut the engine on a short time
            // cycle so the car HOLDS at redline instead of lugging past the gear's ceiling. Re-evaluated every
            // frame and releases the instant a shift drops the RPM — so it never latches.
            _limiterCut = false;
            if (throttlePedal > 0.5f && speedNow > LimiterMinSpeed && _cachedRPM >= LimiterRpm)
            {
                int phase = now % (LimiterCutMs + LimiterOnMs);
                _limiterCut = phase < LimiterCutMs;
            }

            // Throttle takeover (see comment at the gear pin): pedal drives the engine, period.
            if (_limiterCut)
                VehicleMemory.SetThrottle(_vehicle, 0f);
            else if (throttlePedal > 0.05f)
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

            // 2) TORQUE PIPELINE (anti-bog assist + NFS upshift kick).
            float mult = 1f;
            // Shift kick: a decaying forward LUNGE (impulse force) — the punch you feel — plus a small power
            // bump for the engine note. Delivering the punch as force (not raw engine power) means the wheels
            // don't break loose, so there's no post-shift RPM spike (the old power-only kick spun the tires).
            if (now < _shiftKickUntil)
            {
                float k = (_shiftKickUntil - now) / (float)ShiftKickMs;   // 1 -> 0
                mult *= 1f + _shiftKickPower * k;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle.Handle, 1,
                    0f, _shiftKickForce * k * Game.LastFrameTime, 0f, 0f, 0f, 0f, 0, true, true, true, false, true);
            }

            // Exhaust backfire pop for a moment after an upshift, and a light rumble buzz while bouncing
            // off the rev limiter.
            if (now < _shiftPopUntil) EmitBackfirePop();
            if (_limiterCut) Rumble(70, 70);
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
                          (Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, ModSettings.NosButton) || Game.IsKeyPressed(ModSettings.NosKey));
            NosActive = nosBtn && NosLevel > 0.02f && throttlePedal > 0.45f && speedNow > 1.5f;
            if (NosActive)
            {
                // Spraying under throttle: this is NOS, not a handbrake — suppress the handbrake input.
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 76, true);
                // Per-tier burn rate + boost from the selected NOS effect.
                ExhaustFx tier = FxCatalog[(ActiveFxIndex >= 0 && ActiveFxIndex < FxCatalog.Length) ? ActiveFxIndex : 0];
                NosLevel = Math.Max(0f, NosLevel - tier.Drain * dt);
                mult *= tier.Power;
                // OVERBOOST: punch THROUGH the rev limiter and the top-gear speed cap while spraying. Clearing
                // the limiter cut keeps the engine making power (so line 889 won't zero it) and restoring the
                // throttle lets RPM climb past the gear's ceiling — so boost keeps pulling instead of bouncing
                // off the limiter / parking at the normal top speed.
                _limiterCut = false;
                VehicleMemory.SetThrottle(_vehicle, throttlePedal);
                float dvN = tier.Shove * dt;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle.Handle, 1,
                    0f, dvN, 0f, 0f, 0f, 0f, 0, true, true, true, false, true);
                EmitExhaustFlames(_vehicle, NosFlameColor, ActiveFxIndex);   // selected catalog effect
                // Boost whoosh + screen blur — fire ONCE on the spray's rising edge. Re-asserting it every frame
                // retriggers the whoosh into a loud continuous roar; one pulse per spray is the quieter "turned down"
                // version. NosBoostFx (INI) can disable it entirely.
                if (NosBoostFx && !_nosWasActive)
                    Function.Call((Hash)SET_VEHICLE_BOOST_ACTIVE_HASH, _vehicle.Handle, true);
            }
            else
            {
                if (_nosWasActive)
                {
                    StopExhaustFlames();
                    Function.Call((Hash)SET_VEHICLE_BOOST_ACTIVE_HASH, _vehicle.Handle, false);
                }
                if (!nosBtn && NosLevel < 1f)
                    NosLevel = Math.Min(1f, NosLevel + NosRefillPerSec * dt);
            }
            _nosWasActive = NosActive;

            if (_limiterCut) mult = 0f;   // rev-limiter fuel cut: no drive force this frame
            _lastTorqueMult = mult;
            Function.Call((Hash)SET_VEHICLE_CHEAT_POWER_INCREASE_HASH, _vehicle.Handle, mult);

            // 3) AUTORUN TEST HARNESS — file-triggered full-throttle runway runs with CSV telemetry
            //    (development tool; inert unless scripts/ExtendedLSC/autorun.txt appears).
            HandleAutorun(now);

            // === AUTO-DOWNSHIFT AT STOP (every 30 frames) ===
            // Only when GENUINELY PARKED ON THE GROUND. Forcing 1st gear while airborne or sliding (wheels off
            // the road) slams the gearbox into a low ratio and freezes the car mid-air / mid-slide — so require
            // the car to be on all wheels and not in the air, not just "slow".
            if (_updateCounter % 30 == 0 && _targetGear > 1)
            {
                float speed = _vehicle.Speed;
                bool grounded = true, inAir = false;
                try { grounded = Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, _vehicle); } catch { }
                try { inAir = Function.Call<bool>(Hash.IS_ENTITY_IN_AIR, _vehicle); } catch { }
                if (speed < 0.5f && grounded && !inAir)
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

        // NOS on an AUTOMATIC transmission. The MT path runs nitrous inside Update()'s torque pipeline; when MT is
        // OFF the game's auto box drives, so Main calls THIS standalone path every tick instead — so nitrous works
        // on the stock automatic too. Same controls/feel (spray button held + on the gas + moving), minus the MT-only
        // overboost-through-the-rev-limiter trick. Boost is applied via SET_VEHICLE_CHEAT_POWER_INCREASE (re-asserted
        // only while spraying, cleared on release) plus a forward shove.
        private bool _nosWasActiveAuto = false;
        public void UpdateNosAutomatic(Vehicle v)
        {
            if (v == null || !v.Exists()) { _nosWasActiveAuto = false; return; }
            float dt = Game.LastFrameTime;
            float throttlePedal = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, INPUT_VEH_ACCELERATE);
            float speedNow = v.Speed;
            bool nosBtn = NosInstalled &&
                          (Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, ModSettings.NosButton) || Game.IsKeyPressed(ModSettings.NosKey));
            NosActive = nosBtn && NosLevel > 0.02f && throttlePedal > 0.45f && speedNow > 1.5f;
            if (NosActive)
            {
                Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 76, true);   // X = NOS on controller; suppress handbrake
                ExhaustFx tier = FxCatalog[(ActiveFxIndex >= 0 && ActiveFxIndex < FxCatalog.Length) ? ActiveFxIndex : 0];
                NosLevel = Math.Max(0f, NosLevel - tier.Drain * dt);
                Function.Call((Hash)SET_VEHICLE_CHEAT_POWER_INCREASE_HASH, v.Handle, tier.Power);
                float dvN = tier.Shove * dt;
                Function.Call(Hash.APPLY_FORCE_TO_ENTITY, v.Handle, 1,
                    0f, dvN, 0f, 0f, 0f, 0f, 0, true, true, true, false, true);
                EmitExhaustFlames(v, NosFlameColor, ActiveFxIndex);
                // Whoosh once on the rising edge (see MT path) instead of a sustained roar; NosBoostFx can disable it.
                if (NosBoostFx && !_nosWasActiveAuto)
                    Function.Call((Hash)SET_VEHICLE_BOOST_ACTIVE_HASH, v.Handle, true);
            }
            else
            {
                if (_nosWasActiveAuto)
                {
                    StopExhaustFlames();
                    Function.Call((Hash)SET_VEHICLE_BOOST_ACTIVE_HASH, v.Handle, false);
                    Function.Call((Hash)SET_VEHICLE_CHEAT_POWER_INCREASE_HASH, v.Handle, 1.0f);   // clear the boost
                }
                if (!nosBtn && NosLevel < 1f)
                    NosLevel = Math.Min(1f, NosLevel + NosRefillPerSec * dt);
            }
            _nosWasActiveAuto = NosActive;
        }

        /// <summary>
        /// Autorun harness: when scripts/ExtendedLSC/autorun.txt appears (format "seconds;mode",
        /// mode = auto | hold1), hold full throttle for that long, optionally auto-shifting in the
        /// perfect window, while logging per-frame CSV telemetry to run_telemetry.csv.
        /// </summary>
        /// <summary>Dump the current car's gearbox to last_ratios.txt so the dyno tool can inspect the
        /// per-gear ratio scale before we impose a normalized template. Format:
        /// "maxFlatVel;topGear;driveGears;r0,r1,...,r8".</summary>
        private void DumpRatios()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                float maxVel = WheelFitment.WheelMemory.GetHandlingFloat(_vehicle, WheelFitment.WheelMemory.HOFF_MAX_FLAT_VEL);
                int topGear = VehicleMemory.GetTopGear(_vehicle);
                int driveGears = WheelFitment.WheelMemory.GetHandlingGears(_vehicle);
                var sb = new System.Text.StringBuilder();
                sb.Append(maxVel.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                sb.Append(topGear).Append(';').Append(driveGears).Append(';');
                for (int g = 0; g <= 8; g++)
                {
                    if (g > 0) sb.Append(',');
                    sb.Append(VehicleMemory.GetGearRatio(_vehicle, g).ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(DataDir, "last_ratios.txt"), sb.ToString());
            }
            catch (Exception ex) { Log?.Invoke($"[ELSC MT] DumpRatios error: {ex.Message}"); }
        }

        /// <summary>Controller rumble pulse (no-op if disabled or on keyboard). intensity 0..256.</summary>
        private void Rumble(int durationMs, int intensity)
        {
            if (RumbleEnabled) Function.Call((Hash)0x48B3886C1358D0D5, 0, durationMs, intensity);   // SET_PAD_SHAKE
        }

        /// <summary>A quick orange backfire pop out of the exhausts (used on upshift). Independent of the NOS
        /// flame state — non-looped, auto-disposing, so nothing to clean up.</summary>
        private void EmitBackfirePop()
        {
            if (_vehicle == null || !_vehicle.Exists()) return;
            if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, "core"))
            {
                Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, "core");
                return;
            }
            if (++_popTick % 2 != 0) return;   // throttle so pops don't stack every frame
            foreach (string bone in _exhaustBones)
            {
                int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, _vehicle, bone);
                if (bi < 0) continue;
                Function.Call(Hash.USE_PARTICLE_FX_ASSET, "core");
                Function.Call(Hash.SET_PARTICLE_FX_NON_LOOPED_COLOUR, 1f, 0.45f, 0.1f);   // orange flame pop
                Function.Call(Hash.START_PARTICLE_FX_NON_LOOPED_ON_ENTITY_BONE,
                    "veh_backfire", _vehicle, 0f, 0f, 0f, 0f, 0f, 0f, bi, 0.9f, false, false, false);
            }
        }

        // Dyno hook: apply the autorun.txt gear delta the SAME way ApplyTuning's "+# gears" does —
        // write the live per-instance top gear, then RefreshGears() to re-cache + re-space the ratios so
        // the newly added gear gets a real ratio. This is the exact code path the in-game tune uses, so a
        // runway dyno with gearDelta!=0 verifies the fix end-to-end.
        private void ApplyDynoGears()
        {
            try
            {
                if (_dynoGearDelta == 0 || _vehicle == null || !_vehicle.Exists()) return;
                int stock = VehicleMemory.GetTopGear(_vehicle);
                int target = stock + _dynoGearDelta;
                if (target < 1) target = 1;
                if (target > 8) target = 8;
                VehicleMemory.SetTopGear(_vehicle, target);
                RefreshGears(stock);   // anchor spacing to the stock count -> added gears extend the range
                Log?.Invoke($"[ELSC MT] AUTORUN gear delta {_dynoGearDelta}: {stock} -> {target} gears");
            }
            catch (Exception ex) { Log?.Invoke($"[ELSC MT] ApplyDynoGears error: {ex.Message}"); }
        }

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

                // Staging: car has been teleported to the start; hold it still until the suspension settles,
                // then begin the timed full-throttle run from an identical launch every time.
                if (_staging)
                {
                    Function.Call(Hash.SET_ENTITY_VELOCITY, _vehicle.Handle, 0f, 0f, 0f);
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, true);
                    if (now >= _stageUntil)
                    {
                        _staging = false;
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, false);
                        _autorunUntil = now + Math.Max(1, _stageSecs) * 1000;
                        _runStartTime = now;
                        _csv = new System.Text.StringBuilder("ms,speed,rpm,gear,throttle,mult\n");
                        _dynoShiftRpm = 0.83f + (float)_dynoRng.NextDouble() * 0.14f;   // varied 1st->2nd point
                        ApplyDynoGears();   // exercise the +gears fix at the identical launch point
                        DumpRatios();
                        Log?.Invoke($"[ELSC MT] AUTORUN launch: {_stageSecs}s mode={_autorunMode} gearDelta={_dynoGearDelta}");
                    }
                    return;
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
                    _dynoGearDelta = 0;
                    if (parts.Length > 2) int.TryParse(parts[2].Trim(), out _dynoGearDelta);
                    _stageSecs = Math.Max(1, secs);

                    // Load the runway start + end markers (once) for teleport-to-start and proximity auto-stop.
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
                    if (!_runwayStartLoaded)
                    {
                        _runwayStartLoaded = true;
                        try
                        {
                            string startFile = System.IO.Path.Combine(DataDir, "runway_start.txt");
                            if (System.IO.File.Exists(startFile))
                            {
                                var ps = System.IO.File.ReadAllText(startFile).Trim().Split(';');
                                _runwayStart = new GTA.Math.Vector3(
                                    float.Parse(ps[0], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(ps[1], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(ps[2], System.Globalization.CultureInfo.InvariantCulture));
                                if (ps.Length > 3) float.TryParse(ps[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _runwayStartHeading);
                                Log?.Invoke($"[ELSC MT] Runway start marker loaded: {_runwayStart} hdg={_runwayStartHeading}");
                            }
                        }
                        catch { }
                    }

                    // Teleport to the recorded start and stage, OR (no marker) launch from here immediately.
                    if (_runwayStart != default(GTA.Math.Vector3))
                    {
                        Function.Call(Hash.SET_ENTITY_VELOCITY, _vehicle.Handle, 0f, 0f, 0f);
                        Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, _vehicle.Handle, _runwayStart.X, _runwayStart.Y, _runwayStart.Z, false, false, false);
                        Function.Call(Hash.SET_ENTITY_HEADING, _vehicle.Handle, _runwayStartHeading);
                        Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, true);
                        _staging = true;
                        _stageUntil = now + 900;   // settle suspension before launch
                        Log?.Invoke($"[ELSC MT] AUTORUN: teleported to start, staging {_stageSecs}s run");
                        return;
                    }

                    _autorunUntil = now + _stageSecs * 1000;
                    _runStartTime = now;
                    _csv = new System.Text.StringBuilder("ms,speed,rpm,gear,throttle,mult\n");
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _vehicle.Handle, false);
                    ApplyDynoGears();
                    DumpRatios();
                    Log?.Invoke($"[ELSC MT] AUTORUN start (no teleport): {secs}s mode={_autorunMode} gearDelta={_dynoGearDelta}");
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
                        && _cachedRPM >= _dynoShiftRpm && _targetGear < _cachedTopGear
                        && _vehicle.Speed > 7f * _targetGear
                        && (DateTime.Now - _lastShiftTime).TotalMilliseconds > 600)
                    {
                        ShiftUp();
                        _dynoShiftRpm = 0.83f + (float)_dynoRng.NextDouble() * 0.14f;   // next shift: 0.83..1.00
                    }

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
