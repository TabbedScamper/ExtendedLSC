using System;
using System.Collections.Generic;
using System.IO;
using GTA;
using GTA.Math;
using GTA.Native;
using Newtonsoft.Json;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Wheel fitment — RAW sliders. Each slider value is written straight to wheel memory every frame:
    /// camber, track width, ride height (wheel-Z), and (custom rims only) visual size/width. No easing,
    /// no clamps, no budgets, no collider coupling, no auto-lift — the sliders are the only authority.
    ///
    /// The only "smart" part left is capturing the NATURAL per-wheel reference (stock X/Z origin + base
    /// visual size/width) so the offsets/multipliers are measured from the right baseline across rim swaps.
    /// Everything that used to auto-adjust (collider scaling, float-seating, lift budgets, size caps,
    /// re-assert gates, oscillation telemetry, suspension-raise) was removed for a clean slate.
    /// </summary>
    public class WheelFitment
    {
        public static Action<string> Log { get; set; }

        private Vehicle _vehicle;
        private bool _nativeApiAvailable = false;
        private bool _initialized = false;

        // Natural per-wheel offsets for the current wheel: X = track origin, Z = height origin.
        private Dictionary<int, Vector3> _originalWheelOffsets = new Dictionary<int, Vector3>();

        // RAW slider values (relative adjustments).
        private float _frontCamber = 0f;
        private float _rearCamber = 0f;
        private float _frontTrackWidth = 0f;
        private float _rearTrackWidth = 0f;
        private float _frontHeight = 0f;
        private float _rearHeight = 0f;
        private float _rake = 0f;                 // front/rear tilt (per-axle wheel-Z); +front-low, -rear-low
        private bool _rakeActive = false;         // was rake pinned last frame (for one-time stock-Z release)
        private bool _camberActive = false;       // was camber written last frame (one-time stock release)
        private bool _trackActive = false;        // was track/poke written last frame (one-time stock release)

        // Visual wheel size/width (multipliers, 1.0 = stock) via WheelMemory/StreamRenderGfx — custom rims only.
        private float _visualSize = 1f;
        private float _visualWidth = 1f;
        private float _baseVisualSize = 1f;    // wheel's natural render size
        private float _baseVisualWidth = 1f;   // wheel's natural render width
        private bool _hasVisualWheels = false;

        // Ride height rides on the game's FAKE (render-only) suspension lowering — CVehicle+0x1A1C/0x1A20,
        // the value GET_FAKE_SUSPENSION_LOWERING_AMOUNT reads and the suspension mod drives. It's a pure
        // VISUAL offset: the body drops/raises with NO physics, NO collision change, NO suspension fight (so
        // it can never buzz or pop-under), and it's PER-VEHICLE (no model-shared traffic bleed). +ve lowers,
        // -ve raises. _stockFake = whatever the installed suspension mod set (usually 0); our slider adds to it.
        private float _stockFake = 0f;
        private bool _fakeApplied = false;
        /// <summary>While true, our ride-height (fake-lowering) is NOT written — so the game's own suspension
        /// mod preview shows through. Set by Main while previewing a native suspension level.</summary>
        public bool SuspendRideHeight { get; set; } = false;
        private bool _visSizeApplied = false, _visWidthApplied = false; // did we last write a non-stock visual?
        private bool _sizeColApplied = false, _widthColApplied = false;  // did we last write a non-stock collider?
        private float[] _baseTyreR = null, _baseRimR = null, _baseTyreW = null; // natural collider radii/width
        // Change detector -> a small physics nudge so the body re-settles in real time while you adjust.
        private float _lastSig = float.NaN;
        // Geometry (camber/track/rake) is only re-asserted up to this time — refreshed while moving or just
        // after a change. Parked + idle past it = no per-frame geometry writes (the values hold).
        private int _reassertUntil = 0;
        // --- Live stability monitor: every frame, look at the recent ~0.4s of body Z + the SOLVER's wheel Z
        // and flag OSCILLATION (many direction reversals + real amplitude). Reversals are what separate a
        // freak-out (wheel buzzing back-and-forth) from a fast slider move (wheel travelling one way), so it
        // can judge THROUGH adjustment instead of only after you stop. ---
        private readonly float[] _recB = new float[24];        // recent ~0.4s of body Z
        private readonly float[] _recS = new float[24];        // recent ~0.4s of suspension travel (bone-Z minus body-Z)
        private int _wheelBoneIdx = -2;                        // -2 = uncached; cached wheel_lf bone index
        private int _recN = 0;
        private int _lastChangeAt = 0;                         // last slider-change time (don't snapshot mid-move)
        private float _oscMs = 0f;                             // accumulated time the wheel/body has been oscillating
        private bool _hasSafe = false;
        private float[] _safe = null;                          // last KNOWN-STABLE [fc,rc,ft,rt,fh,rh,vs,vw]
        public bool InstabilityTripped { get; private set; }
        public string InstabilityMsg { get; private set; }

        // NATURAL-BASELINE cache, keyed by model:wheelType:frontWheelModIndex. Each wheel has its own
        // natural visual size + offsets, so we capture the FIRST time we ever see a given wheel in its
        // natural state (before we've touched anything) and reuse it — so we never re-capture a value we
        // already modified.
        private struct StockWheel
        {
            public bool HasVisual;
            public float BaseSize, BaseWidth;
            public float[] WheelX;   // natural lateral offset per wheel (track-width origin)
            public float[] WheelZ;   // natural vertical offset per wheel (height origin)
            public float[] TyreR;    // natural tyre collider radius per wheel (physics ride height)
            public float[] RimR;     // natural rim collider radius per wheel
            public float[] TyreW;    // natural tyre collider width per wheel
        }
        private static readonly Dictionary<string, StockWheel> _stockByKey = new Dictionary<string, StockWheel>();
        private string _currentBaselineKey = null;

        // Persist the captured NATURAL baselines to disk so a SCRIPT RELOAD (which wipes these statics while the
        // live wheel memory still holds the applied stance) reuses the clean baseline instead of re-capturing
        // from already-stanced memory and compounding the offset each reload. Keyed by model:wheelType:wheelMod,
        // which is stock geometry — valid across reloads AND game restarts.
        private static bool _baselinesLoaded = false;
        private static string BaselinePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "wheel_baselines.json");
        private static void LoadBaselines()
        {
            if (_baselinesLoaded) return;
            _baselinesLoaded = true;
            try
            {
                if (!File.Exists(BaselinePath)) return;
                var d = JsonConvert.DeserializeObject<Dictionary<string, StockWheel>>(File.ReadAllText(BaselinePath));
                if (d != null) foreach (var kv in d) if (!_stockByKey.ContainsKey(kv.Key)) _stockByKey[kv.Key] = kv.Value;
            }
            catch { }
        }
        private static void SaveBaselines()
        {
            try
            {
                string dir = Path.GetDirectoryName(BaselinePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(BaselinePath, JsonConvert.SerializeObject(_stockByKey));
            }
            catch { }
        }

        // PER-VEHICLE stable origin. The suspension mount X/Z is a property of the VEHICLE, not the rim, so
        // it is captured ONCE per vehicle and reused for every rim — re-reading it from (mid-settle) memory
        // on each swap made the captured origin drift. Reset on Initialize (new vehicle).
        private float[] _vehicleNatX = null;
        private float[] _vehicleNatZ = null;

        // Slider ranges — the UI in Main.cs builds the sliders from these. NOTHING here clamps to them.
        // Deliberately WIDE (no hard stance limits) — these are the only bound and the raw system applies
        // whatever you dial in. Big values may bounce/clip; that's expected with limits removed.
        public const float MAX_CAMBER = 1.0f;        // ~57 degrees
        public const float MIN_CAMBER = -1.0f;
        // Poke (track width) is VISUAL ONLY (CWheel+0x030 moves the render wheel, NOT the physics contact —
        // verified). Clamped so wheels can't tuck way under the car or stick way outside the body.
        public const float MAX_TRACK_WIDTH = 0.15f;  // 15 cm poke (outward)
        public const float MIN_TRACK_WIDTH = -0.15f; // 15 cm tuck (inward)
        public const float MAX_HEIGHT = 0.5f;        // 0.5 meters higher
        public const float MIN_HEIGHT = -0.5f;       // 0.5 meters lower
        // Ride-height clamp in FAKE-LOWERING units (+ve = slam/lower, -ve = lift/raise). The visible range is
        // roughly ±0.30 (a dramatic pump); ±0.25 is a strong stance both ways. Render-only, so this is purely
        // an aesthetic cap, not a stability limit.
        public const float MAX_RAISE = 0.25f;        // max slam (body down)
        public const float MIN_RAISE = -0.25f;       // max lift (body up)
        // Rake (front/rear tilt) via per-axle visual wheel-Z. +ve = front lower, -ve = rear lower. Small range
        // — this touches the suspension solver (unlike the render-only fields), so kept tight + monitor-backed.
        public const float MAX_RAKE = 0.10f;         // 10 cm front-low tilt
        public const float MIN_RAKE = -0.10f;        // 10 cm rear-low tilt
        public const float MAX_VISUAL = 3.0f;        // 3.0x wheel size/width
        public const float MIN_VISUAL = 0.3f;        // 0.3x wheel size/width

        #region Properties

        // RAW slider values — written straight to memory each frame (no easing, no clamps, no auto-adjust).
        // The slider UI in Main.cs defines the usable range; nothing here second-guesses it.

        public float FrontCamber     { get => _frontCamber;     set => _frontCamber = value; }
        public float RearCamber      { get => _rearCamber;      set => _rearCamber = value; }
        public float FrontTrackWidth { get => _frontTrackWidth; set => _frontTrackWidth = value; }
        public float RearTrackWidth  { get => _rearTrackWidth;  set => _rearTrackWidth = value; }
        public float FrontHeight     { get => _frontHeight;     set => _frontHeight = value; }
        public float RearHeight      { get => _rearHeight;      set => _rearHeight = value; }
        public float Rake            { get => _rake;            set => _rake = value; }
        public float VisualSize      { get => _visualSize;      set => _visualSize = value; }
        public float VisualWidth     { get => _visualWidth;     set => _visualWidth = value; }
        /// <summary>The clean suspension-mod base our ride-height offsets from. Saved with the stance and
        /// restored on entry so we never re-read it from a field that's still holding our slam.</summary>
        public float StockFake       { get => _stockFake;       set => _stockFake = value; }

        /// <summary>The wheel's natural overall diameter in meters (for display: inches = this/0.0254).</summary>
        public float BaseVisualDiameter => _baseVisualSize;
        public float BaseVisualWidth => _baseVisualWidth;

        /// <summary>No auto size cap (raw sliders). Kept for the slider builder; returns the slider max.</summary>
        public float MaxSizeMult => MAX_VISUAL;

        /// <summary>True if the current vehicle has aftermarket wheels (visual size/width available).</summary>
        public bool HasVisualWheels => _hasVisualWheels;

        /// <summary>The vehicle this fitment instance is bound to (null if uninitialized).</summary>
        public Vehicle Vehicle => _vehicle;

        public bool IsInitialized => _vehicle != null && _vehicle.Exists() && _initialized;

        public bool IsFullFitmentAvailable => _nativeApiAvailable;

        #endregion

        #region Initialization

        /// <summary>Bind to a vehicle and capture its natural wheel baseline. Resets all sliders to stock.</summary>
        public bool Initialize(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
            {
                Log?.Invoke("[WheelFitment] Initialize failed - invalid vehicle");
                return false;
            }

            _vehicle = vehicle;
            _initialized = false;

            WheelFitmentNative.Log = Log;
            _nativeApiAvailable = WheelFitmentNative.IsAvailable;

            // Sliders start at stock.
            _frontCamber = 0f;
            _rearCamber = 0f;
            _frontTrackWidth = 0f;
            _rearTrackWidth = 0f;
            _frontHeight = 0f;
            _rearHeight = 0f;
            _rake = 0f;
            _rakeActive = false;
            _camberActive = false;
            _trackActive = false;
            _visualSize = 1f;
            _visualWidth = 1f;

            _currentBaselineKey = null;
            _vehicleNatX = null;   // new vehicle: re-capture the stable origin once
            _vehicleNatZ = null;
            _wheelBoneIdx = -2;    // re-resolve the wheel bone for the new vehicle
            _hasSafe = false; _recN = 0; _oscMs = 0f;
            _lastSig = float.NaN;
            _reassertUntil = Game.GameTime + 2500;   // re-assert geometry for ~2.5s so a loaded stance applies
            _visSizeApplied = false; _visWidthApplied = false;
            _sizeColApplied = false; _widthColApplied = false;

            try { _vehicle.Mods.InstallModKit(); } catch { }

            // Provisional baseline from the current field. This is correct on a FIRST visit (field = the
            // suspension-mod base). On RE-entry the field is still holding our previous slam (set-and-forget),
            // so this read is polluted — but the caller immediately overrides _stockFake with the CLEAN value
            // saved alongside the stance (see StockFake / the load paths), so we never re-stamp the suspension
            // (which caused an on-entry shake) and the held field never has to change.
            _stockFake = WheelMemory.GetFakeLowering(vehicle);
            if (_stockFake < -1f || _stockFake > 1f) _stockFake = 0f;
            _fakeApplied = false;
            RefreshBaseline();

            _initialized = true;
            Log?.Invoke($"[WheelFitment] Initialized for {vehicle.DisplayName}");
            return true;
        }

        /// <summary>Identity of the currently-installed wheel: model + wheel type + front wheel-mod index.
        /// Two different rims (or wheel types) have different natural sizes, so this is the cache key.</summary>
        private string ResolveWheelKey()
        {
            try
            {
                int model = _vehicle.Model.Hash;
                int wheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, _vehicle);
                int frontIdx = Function.Call<int>(Hash.GET_VEHICLE_MOD, _vehicle, 23); // 23 = front wheels
                return model + ":" + wheelType + ":" + frontIdx;
            }
            catch { return null; }
        }

        #endregion

        #region Baseline + raw application

        /// <summary>
        /// Resolve the natural baseline (visual size/width + per-wheel X/Z origin) for the wheel currently
        /// installed. Capture it the first time we ever see this model+wheelType+wheel-mod in its natural
        /// state; reuse the record otherwise. Called on init and (cheaply) every tick — it only does work
        /// when the wheel identity actually changes, then re-stamps the raw slider values so they survive a
        /// rim swap.
        /// </summary>
        private void RefreshBaseline()
        {
            if (_vehicle == null || !_vehicle.Exists()) return;
            LoadBaselines();   // pull any disk-persisted clean baselines first (cheap, runs once per session)
            string key = ResolveWheelKey();
            if (key == null || key == _currentBaselineKey) return;   // unchanged -> nothing to do
            _currentBaselineKey = key;

            try
            {
                float[] xs, zs;
                if (_stockByKey.TryGetValue(key, out StockWheel cached))
                {
                    _hasVisualWheels = cached.HasVisual;
                    _baseVisualSize = cached.BaseSize;
                    _baseVisualWidth = cached.BaseWidth;
                    xs = cached.WheelX;
                    zs = cached.WheelZ;
                    _baseTyreR = cached.TyreR;
                    _baseRimR = cached.RimR;
                    _baseTyreW = cached.TyreW;
                    Log?.Invoke($"[WheelFitment] Baseline (cached) {key}: base={_baseVisualSize:F3} hasVisual={_hasVisualWheels}");
                }
                else
                {
                    // First sight of this rim -> memory currently holds its NATURAL values (we have not
                    // scaled this structure yet), so it is safe to record them as the baseline. Visual
                    // size/width is only meaningful for aftermarket rims (front mod != -1).
                    bool customRims = false;
                    try { customRims = Function.Call<int>(Hash.GET_VEHICLE_MOD, _vehicle, 23) != -1; } catch { }
                    _hasVisualWheels = customRims && WheelMemory.HasVisualWheels(_vehicle);

                    _baseVisualSize = WheelMemory.GetVisualSize(_vehicle);
                    _baseVisualWidth = WheelMemory.GetVisualWidth(_vehicle);
                    if (_baseVisualSize < 0.1f || _baseVisualSize > 5f) _baseVisualSize = 1f;
                    if (_baseVisualWidth < 0.1f || _baseVisualWidth > 5f) _baseVisualWidth = 1f;

                    int n = WheelMemory.GetWheelCount(_vehicle);
                    xs = new float[n];
                    zs = new float[n];
                    _baseTyreR = new float[n];
                    _baseRimR = new float[n];
                    _baseTyreW = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        xs[i] = WheelMemory.GetWheelX(_vehicle, i);
                        zs[i] = WheelMemory.GetWheelZ(_vehicle, i);
                        _baseTyreR[i] = WheelMemory.GetTyreColliderRadius(_vehicle, i);
                        _baseRimR[i]  = WheelMemory.GetRimColliderRadius(_vehicle, i);
                        _baseTyreW[i] = WheelMemory.GetTyreColliderWidth(_vehicle, i);
                    }

                    // CRITICAL: a baseline read is only TRUE NATURAL when no stance is currently applied. If a
                    // stance is active, the live read is polluted by our own scaling/poke, so we must NOT cache or
                    // persist it (that's what corrupted the file and broke reloads). Capture it for this frame so
                    // the wheel still renders, but leave the key UN-cached so a later clean encounter records the
                    // true natural. Visual size/width are the per-vehicle stateful fields that pollute worst.
                    bool stanceActive = Math.Abs(_visualSize - 1f) > 0.001f || Math.Abs(_visualWidth - 1f) > 0.001f
                        || Math.Abs(_frontTrackWidth) > 1e-4f || Math.Abs(_rearTrackWidth) > 1e-4f
                        || Math.Abs(_frontHeight) > 1e-4f || Math.Abs(_rearHeight) > 1e-4f
                        || Math.Abs(_rake) > 1e-4f || Math.Abs(_frontCamber) > 1e-4f || Math.Abs(_rearCamber) > 1e-4f;
                    if (!stanceActive)
                    {
                        _stockByKey[key] = new StockWheel
                        {
                            HasVisual = _hasVisualWheels,
                            BaseSize = _baseVisualSize,
                            BaseWidth = _baseVisualWidth,
                            WheelX = xs,
                            WheelZ = zs,
                            TyreR = _baseTyreR,
                            RimR = _baseRimR,
                            TyreW = _baseTyreW
                        };
                        SaveBaselines();   // persist ONLY clean captures so reloads reuse a real natural baseline
                    }
                    Log?.Invoke($"[WheelFitment] Baseline ({(stanceActive ? "live-polluted, not cached" : "captured")}) {key}: base={_baseVisualSize:F3}");
                }

                // PER-VEHICLE STABLE ORIGIN: reuse the origin captured the first time we baselined THIS
                // vehicle, instead of the just-read live values, so a rim swap can't drift the origin.
                if (_vehicleNatZ != null && _vehicleNatX != null && xs != null && zs != null
                    && _vehicleNatZ.Length == zs.Length && _vehicleNatX.Length == xs.Length)
                {
                    xs = _vehicleNatX;
                    zs = _vehicleNatZ;
                }
                else if (xs != null && zs != null)
                {
                    _vehicleNatX = (float[])xs.Clone();
                    _vehicleNatZ = (float[])zs.Clone();
                }

                // Rebuild the track/height origin map from the cached natural offsets.
                _originalWheelOffsets.Clear();
                if (xs != null && zs != null)
                    for (int i = 0; i < xs.Length && i < zs.Length; i++)
                        _originalWheelOffsets[i] = new Vector3(xs[i], 0f, zs[i]);

                // The wheel just changed (or first init): the game built a fresh, natural structure for it,
                // wiping any scaling we had on the previous wheel. Re-stamp the raw slider values.
                ApplyRaw();
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] RefreshBaseline error: {ex.Message}"); }
        }

        // ---- Preview lock: hold the player's EXACT applied stance (absolute memory values) while they scroll
        // rims, so every previewed rim shows the same width/size/height instead of the fitment re-deriving an
        // unreliable per-rim baseline (which compounds the wheels wider or skinnier as you scroll). ----
        private bool _previewLock = false;
        private bool _lockHasVisual;
        private float _lockVisSize, _lockVisWidth, _lockFake;
        private float[] _lockTyreR, _lockRimR, _lockTyreW;

        public void BeginPreviewLock()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                _lockHasVisual = _hasVisualWheels;
                _lockVisSize = WheelMemory.GetVisualSize(_vehicle);
                _lockVisWidth = WheelMemory.GetVisualWidth(_vehicle);
                _lockFake = WheelMemory.GetFakeLowering(_vehicle);
                int n = WheelMemory.GetWheelCount(_vehicle);
                _lockTyreR = new float[n]; _lockRimR = new float[n]; _lockTyreW = new float[n];
                for (int i = 0; i < n; i++)
                {
                    _lockTyreR[i] = WheelMemory.GetTyreColliderRadius(_vehicle, i);
                    _lockRimR[i] = WheelMemory.GetRimColliderRadius(_vehicle, i);
                    _lockTyreW[i] = WheelMemory.GetTyreColliderWidth(_vehicle, i);
                }
                _previewLock = true;
            }
            catch { }
        }
        public void EndPreviewLock()
        {
            try
            {
                // The locked field value = clean_base * current_mult. Restore the CLEAN base so the resuming
                // fitment re-applies the SAME absolute stance (base*mult) instead of squaring the multiplier
                // (which balloons the width when you back out / buy). Pin the current key so RefreshBaseline
                // won't immediately re-capture the still-scaled live value and re-pollute it.
                if (_previewLock && _lockHasVisual && _vehicle != null && _vehicle.Exists())
                {
                    if (Math.Abs(_visualWidth) > 0.01f) _baseVisualWidth = _lockVisWidth / _visualWidth;
                    if (Math.Abs(_visualSize) > 0.01f) _baseVisualSize = _lockVisSize / _visualSize;
                    if (_baseTyreR != null && _lockTyreR != null && _baseRimR != null && _lockRimR != null
                        && _baseTyreW != null && _lockTyreW != null)
                        for (int i = 0; i < _baseTyreR.Length && i < _lockTyreR.Length; i++)
                        {
                            if (Math.Abs(_visualSize) > 0.01f) { _baseTyreR[i] = _lockTyreR[i] / _visualSize; _baseRimR[i] = _lockRimR[i] / _visualSize; }
                            if (Math.Abs(_visualWidth) > 0.01f) _baseTyreW[i] = _lockTyreW[i] / _visualWidth;
                        }
                    _currentBaselineKey = ResolveWheelKey();
                }
            }
            catch { }
            _previewLock = false;
        }

        private void ReassertPreviewLock()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                if (_lockHasVisual)
                {
                    WheelMemory.SetVisualSize(_vehicle, _lockVisSize);
                    WheelMemory.SetVisualWidth(_vehicle, _lockVisWidth);
                    int n = WheelMemory.GetWheelCount(_vehicle);
                    if (_lockTyreR != null)
                        for (int i = 0; i < n && i < _lockTyreR.Length; i++)
                        {
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_RADIUS, _lockTyreR[i]);
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_RIM_RADIUS, _lockRimR[i]);
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_WIDTH, _lockTyreW[i]);
                        }
                }
                WheelMemory.SetFakeLowering(_vehicle, _lockFake);
            }
            catch { }
        }

        // Opportunistic self-heal: whenever the stance is at STOCK, the live wheel IS its true natural, so
        // re-capture the cached baseline from it. This auto-corrects any baseline that was recorded polluted
        // (the cause of reload-corrupted stances) — the player just needs to be at stock for a moment.
        private int _lastHealAt = 0;
        private void HealBaselineIfStock()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists() || !_hasVisualWheels) return;
                // Visual size/width + colliders are clean when the VISUAL is at stock (size & width = 1). The
                // X/Z origin is clean only when the GEOMETRY (track/rake/camber) is also at stock.
                bool visStock = Math.Abs(_visualSize - 1f) < 0.001f && Math.Abs(_visualWidth - 1f) < 0.001f;
                bool geomStock = Math.Abs(_frontTrackWidth) < 1e-4f && Math.Abs(_rearTrackWidth) < 1e-4f
                    && Math.Abs(_frontHeight) < 1e-4f && Math.Abs(_rearHeight) < 1e-4f && Math.Abs(_rake) < 1e-4f
                    && Math.Abs(_frontCamber) < 1e-4f && Math.Abs(_rearCamber) < 1e-4f;
                if (!visStock) return;   // can't trust the visual baseline while a size/width mult is applied
                if (Game.GameTime - _lastHealAt < 500) return;
                _lastHealAt = Game.GameTime;
                string key = ResolveWheelKey();
                if (key == null) return;
                float ns = WheelMemory.GetVisualSize(_vehicle), nw = WheelMemory.GetVisualWidth(_vehicle);
                if (ns < 0.1f || ns > 5f || nw < 0.1f || nw > 5f) return;
                bool cached = _stockByKey.TryGetValue(key, out var cur) && cur.HasVisual;
                if (cached && Math.Abs(cur.BaseWidth - nw) < 0.004f && Math.Abs(cur.BaseSize - ns) < 0.004f
                    && cur.WheelX != null && cur.TyreW != null) return; // already clean
                _baseVisualSize = ns; _baseVisualWidth = nw;
                int n = WheelMemory.GetWheelCount(_vehicle);
                // colliders: clean now (visual at stock). origin: clean only if geometry also stock, else keep cached.
                _baseTyreR = new float[n]; _baseRimR = new float[n]; _baseTyreW = new float[n];
                float[] xs = (geomStock || !cached || cur.WheelX == null) ? new float[n] : cur.WheelX;
                float[] zs = (geomStock || !cached || cur.WheelZ == null) ? new float[n] : cur.WheelZ;
                for (int i = 0; i < n; i++)
                {
                    if (geomStock || !cached || cur.WheelX == null) { xs[i] = WheelMemory.GetWheelX(_vehicle, i); zs[i] = WheelMemory.GetWheelZ(_vehicle, i); }
                    _baseTyreR[i] = WheelMemory.GetTyreColliderRadius(_vehicle, i);
                    _baseRimR[i] = WheelMemory.GetRimColliderRadius(_vehicle, i);
                    _baseTyreW[i] = WheelMemory.GetTyreColliderWidth(_vehicle, i);
                }
                _stockByKey[key] = new StockWheel { HasVisual = true, BaseSize = ns, BaseWidth = nw,
                    WheelX = xs, WheelZ = zs, TyreR = _baseTyreR, RimR = _baseRimR, TyreW = _baseTyreW };
                SaveBaselines();
                Log?.Invoke($"[WheelFitment] Baseline healed (vis stock, geom {(geomStock ? "stock" : "active")}) {key}: size={ns:F3} width={nw:F3}");
            }
            catch { }
        }

        /// <summary>Must be called every frame to maintain fitment.</summary>
        public void Update()
        {
            if (!IsInitialized) return;
            if (_previewLock) { ReassertPreviewLock(); return; }  // hold the exact stance while previewing rims
            HealBaselineIfStock();   // self-correct any polluted baseline whenever the player is at stock
            RefreshBaseline();   // refresh the reference offsets/base visual on a wheel swap (re-stamps both)

            // RENDER fields (fake-lowering + visual size/width) get wiped by render events like opening the
            // menu, so re-assert them EVERY frame (they no-op at stock — cheap).
            ApplyRender();

            float sig = _frontCamber + _rearCamber * 1.7f + _frontTrackWidth * 2.3f + _rearTrackWidth * 3.1f
                      + _frontHeight * 4.3f + _rearHeight * 5.7f + _visualSize * 6.1f + _visualWidth * 7.9f
                      + _rake * 8.3f;
            bool changed = !float.IsNaN(_lastSig) && Math.Abs(sig - _lastSig) > 0.0001f;
            _lastSig = sig;

            bool moving = false;
            try { moving = _vehicle.Speed > 0.1f; } catch { }

            // The CWheel SOLVER fields (camber/track/rake) HOLD while parked, but the suspension solver can
            // reset them under real driving — so only re-assert (and run the stability monitor) while MOVING or
            // for a short settle window after a change. Parked + idle = zero geometry writes, zero monitor cost.
            if (changed || moving) _reassertUntil = Game.GameTime + 1500;
            if (changed)
            {
                Nudge();
                _lastChangeAt = Game.GameTime;
            }

            if (Game.GameTime < _reassertUntil)
            {
                // Capture the SOLVER's wheel-Z BEFORE ApplyGeometry re-pins it — its oscillation is the buzz.
                float solverWheelZ = WheelMemory.GetWheelZ(_vehicle, 0);
                ApplyGeometry();
                MonitorStability(solverWheelZ);
            }
        }

        /// <summary>Watch the body for a SUSTAINED bounce (the "going nuts" failure) while parked/slow, and
        /// if it won't settle, revert to the last stable values (or stock) and flag a warning for the UI.
        /// Triggers only after a change has had time to settle (so it ignores the apply transient + nudge),
        /// and only at low speed (so driving over bumps never false-trips it).</summary>
        private void MonitorStability(float solverWheelZ)
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                bool active = Math.Abs(_frontCamber) > 1e-4f || Math.Abs(_rearCamber) > 1e-4f
                           || Math.Abs(_frontTrackWidth) > 1e-4f || Math.Abs(_rearTrackWidth) > 1e-4f
                           || Math.Abs(_frontHeight) > 1e-4f || Math.Abs(_rearHeight) > 1e-4f
                           || Math.Abs(_rake) > 1e-4f
                           || Math.Abs(_visualSize - 1f) > 1e-3f || Math.Abs(_visualWidth - 1f) > 1e-3f;
                if (!active) { _hasSafe = false; _recN = 0; _oscMs = 0f; return; }
                // HORIZONTAL speed only — a parked car that's VIBRATING has high vertical velocity but ~0
                // horizontal, so total Speed would wrongly gate the buzz out. Driving = horizontal motion.
                Vector3 vel = _vehicle.Velocity;
                float horiz = (float)Math.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
                if (horiz > 2.5f) { _recN = 0; _oscMs = 0f; return; }   // driving -> don't judge

                // Suspension travel = wheel bone world-Z relative to body world-Z. This is the RENDERED wheel
                // position, which moves when the wheels visibly flip up/down (the solver's 0x038 / body-Z may
                // not show it).
                if (_wheelBoneIdx == -2)
                    _wheelBoneIdx = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, _vehicle, "wheel_lf");
                float susp = solverWheelZ;
                if (_wheelBoneIdx >= 0)
                {
                    Vector3 bw = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, _vehicle, _wheelBoneIdx);
                    susp = bw.Z - _vehicle.Position.Z;
                }

                // Prime the whole ring with the first sample so the window is valid from frame 1 (no 0.4s
                // blind spot waiting for it to fill — primed slots read flat, so they never false-trip).
                if (_recN == 0)
                    for (int k = 0; k < _recB.Length; k++)
                    { _recB[k] = _vehicle.Position.Z; _recS[k] = susp; }
                _recB[_recN % _recB.Length] = _vehicle.Position.Z;
                _recS[_recN % _recS.Length] = susp;
                _recN++;

                // Detect on the SUSPENSION (rendered wheel vs body) — that's where the flip lives (diagnostic:
                // suspBone amp~0.11 rev~5 while 0x038/bodyZ were flat). >=3 reversals rules out a monotonic
                // slider move; amplitude rules out tiny jitter. Body Z is a weak backup.
                Analyze(_recS, out int sRev, out float sAmp);
                Analyze(_recB, out int bRev, out float bAmp);
                bool oscNow = (sRev >= 2 && sAmp > 0.025f) || (bRev >= 2 && bAmp > 0.045f);
                float dt = Math.Min(Game.LastFrameTime, 0.1f) * 1000f;
                // A violent buzz climbs the accumulator twice as fast AND trips at a lower threshold, so an
                // obvious freak-out is caught in ~0.2s while a borderline wobble still needs ~0.4s of evidence.
                bool violent = sAmp > 0.08f || bAmp > 0.10f;
                _oscMs = oscNow ? _oscMs + dt * (violent ? 2f : 1f) : Math.Max(0f, _oscMs - dt * 1.5f);
                float trip = violent ? 200f : 400f;

                if (_oscMs > trip)
                {
                    if (_hasSafe && _safe != null)
                    {
                        _frontCamber = _safe[0]; _rearCamber = _safe[1];
                        _frontTrackWidth = _safe[2]; _rearTrackWidth = _safe[3];
                        _frontHeight = _safe[4]; _rearHeight = _safe[5];
                        _visualSize = _safe[6]; _visualWidth = _safe[7];
                        _rake = _safe[8];
                        InstabilityMsg = "Setup became unstable — reverted to last stable fitment";
                    }
                    else
                    {
                        Reset();
                        InstabilityMsg = "Setup became unstable — reverted to stock";
                    }
                    InstabilityTripped = true;
                    _lastSig = float.NaN;     // don't treat the revert as a user change
                    _recN = 0; _oscMs = 0f;
                    Nudge();
                }
                else if (_oscMs < 50f && sRev < 2 && bRev < 2 && sAmp < 0.02f && bAmp < 0.02f && Game.GameTime - _lastChangeAt > 400)
                {
                    // Settled, quiet, and not mid-adjust -> remember as a known-good fallback.
                    _safe = new[] { _frontCamber, _rearCamber, _frontTrackWidth, _rearTrackWidth, _frontHeight, _rearHeight, _visualSize, _visualWidth, _rake };
                    _hasSafe = true;
                }
            }
            catch { }
        }

        /// <summary>Count significant direction reversals + peak-to-peak amplitude over the recent ring (in
        /// chronological order). Many reversals = oscillation; few = a monotonic move.</summary>
        private void Analyze(float[] ring, out int reversals, out float amplitude)
        {
            int len = ring.Length;
            int start = _recN % len;     // oldest sample is the next slot to be overwritten
            float mn = float.MaxValue, mx = float.MinValue;
            int rev = 0, lastSign = 0;
            float prev = 0f; bool have = false;
            for (int k = 0; k < len; k++)
            {
                float v = ring[(start + k) % len];
                if (v < mn) mn = v; if (v > mx) mx = v;
                if (have)
                {
                    float d = v - prev;
                    if (Math.Abs(d) > 0.004f)
                    {
                        int s = d > 0 ? 1 : -1;
                        if (lastSign != 0 && s != lastSign) rev++;
                        lastSign = s;
                    }
                }
                prev = v; have = true;
            }
            reversals = rev;
            amplitude = mx - mn;
        }

        /// <summary>UI polls this each tick — returns a one-shot warning string when a revert just happened.</summary>
        public string ConsumeInstability()
        {
            if (!InstabilityTripped) return null;
            InstabilityTripped = false;
            return InstabilityMsg;
        }

        /// <summary>Full apply — gated wheel GEOMETRY + always-on RENDER fields. Used on a wheel swap
        /// (RefreshBaseline) to re-stamp everything onto the fresh wheel.</summary>
        private void ApplyRaw() { ApplyGeometry(); ApplyRender(); }

        /// <summary>CWheel SOLVER fields: camber, track/poke (X), rake (per-axle Z). These HOLD while parked
        /// but the suspension solver can reset them under real driving, so Update only re-asserts them while
        /// MOVING or just after a change (see the reassert window). Each writes only while dialed in, with a
        /// one-time stock write on release — a bone-stock car writes nothing.</summary>
        private void ApplyGeometry()
        {
            try
            {
                // Rake = per-axle visual wheel-Z tilt (+front-low, -rear-low). Pin only while non-zero;
                // one-time stock-Z write on release (never pin at stock).
                float rake = _rake;
                if (rake > MAX_RAKE) rake = MAX_RAKE;
                if (rake < MIN_RAKE) rake = MIN_RAKE;
                bool rakeOn = Math.Abs(rake) > 0.0005f;

                bool camberOn = Math.Abs(_frontCamber) > 0.0005f || Math.Abs(_rearCamber) > 0.0005f;
                bool trackOn  = Math.Abs(_frontTrackWidth) > 0.0005f || Math.Abs(_rearTrackWidth) > 0.0005f;

                if (camberOn || trackOn || rakeOn || _camberActive || _trackActive || _rakeActive)
                {
                    foreach (var kvp in _originalWheelOffsets)
                    {
                        int i = kvp.Key;
                        Vector3 o = kvp.Value;
                        bool isLeft = (i % 2 == 0);
                        bool isFront = (i == 0 || i == 1);
                        float camber = isFront ? _frontCamber : _rearCamber;
                        float track = isFront ? _frontTrackWidth : _rearTrackWidth;
                        if (track > MAX_TRACK_WIDTH) track = MAX_TRACK_WIDTH;   // clamp poke (visual-only) so
                        if (track < MIN_TRACK_WIDTH) track = MIN_TRACK_WIDTH;   // saved/preset values stay sane

                        // Camber: write while dialed in; one-time stock (0) write on release.
                        if (camberOn) WheelMemory.SetWheelCamber(_vehicle, i, isLeft ? camber : -camber);
                        else if (_camberActive) WheelMemory.SetWheelCamber(_vehicle, i, 0f);

                        // Track/poke: write while dialed in; one-time stock-X write on release.
                        if (trackOn) WheelMemory.SetWheelX(_vehicle, i, o.X + (isLeft ? -track : track));
                        else if (_trackActive) WheelMemory.SetWheelX(_vehicle, i, o.X);

                        // Rake: front wheels +rake (up into arch = corner sits lower), rear wheels -rake.
                        if (rakeOn) WheelMemory.SetWheelZ(_vehicle, i, o.Z + (isFront ? rake : -rake));
                        else if (_rakeActive) WheelMemory.SetWheelZ(_vehicle, i, o.Z);
                    }
                }
                _camberActive = camberOn;
                _trackActive = trackOn;
                _rakeActive = rakeOn;
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyGeometry error: {ex.Message}"); }
        }

        /// <summary>RENDER fields: fake-lowering body height (CVehicle, render-only) + visual wheel size/width
        /// + matching physics colliders. These get reset by RENDER events (opening the menu), NOT by driving,
        /// so Update re-asserts them EVERY frame — but each writes only while dialed in (stock = no writes).</summary>
        private void ApplyRender()
        {
            try
            {
                // Ride height via the game's FAKE (render-only) suspension lowering. The slider stores +cm-lift
                // as NEGATIVE height; fake-lowering is +ve=lower, so the slam offset is +(_frontHeight). Average
                // both axles (single whole-body value) and clamp. Restore the mod's baseline once on release.
                // While previewing a native suspension level, leave the field alone so the game's preview shows.
                if (!SuspendRideHeight) WriteFakeLowering();

                if (_hasVisualWheels)
                {
                    // Only TOUCH the render size/width when the player has actually dialed in a non-stock
                    // multiplier. At 1.0 we leave the game's natural value alone — writing base*1.0 every
                    // frame would stamp our captured base, and if that base was read a frame off during a
                    // wheel swap the wheel renders the wrong size (floats/sinks) on a car you never sized.
                    bool sizeOn = Math.Abs(_visualSize - 1f) > 0.001f;
                    bool widthOn = Math.Abs(_visualWidth - 1f) > 0.001f;
                    if (sizeOn) { WheelMemory.SetVisualSize(_vehicle, _baseVisualSize * _visualSize); _visSizeApplied = true; }
                    else if (_visSizeApplied) { WheelMemory.SetVisualSize(_vehicle, _baseVisualSize); _visSizeApplied = false; }
                    if (widthOn) { WheelMemory.SetVisualWidth(_vehicle, _baseVisualWidth * _visualWidth); _visWidthApplied = true; }
                    else if (_visWidthApplied) { WheelMemory.SetVisualWidth(_vehicle, _baseVisualWidth); _visWidthApplied = false; }

                    // Scale the PHYSICS colliders to match the rendered wheel so the contact patch (and thus
                    // ride height + collision) tracks the visual size/width. Tyre+rim radius follow size,
                    // collider width follows width. Restore the captured stock value once on release.
                    int wn = WheelMemory.GetWheelCount(_vehicle);
                    if (sizeOn && _baseTyreR != null && _baseRimR != null)
                    {
                        for (int i = 0; i < wn && i < _baseTyreR.Length; i++)
                        {
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_RADIUS, _baseTyreR[i] * _visualSize);
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_RIM_RADIUS, _baseRimR[i] * _visualSize);
                        }
                        _sizeColApplied = true;
                    }
                    else if (_sizeColApplied && _baseTyreR != null && _baseRimR != null)
                    {
                        for (int i = 0; i < wn && i < _baseTyreR.Length; i++)
                        {
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_RADIUS, _baseTyreR[i]);
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_RIM_RADIUS, _baseRimR[i]);
                        }
                        _sizeColApplied = false;
                    }
                    if (widthOn && _baseTyreW != null)
                    {
                        for (int i = 0; i < wn && i < _baseTyreW.Length; i++)
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_WIDTH, _baseTyreW[i] * _visualWidth);
                        _widthColApplied = true;
                    }
                    else if (_widthColApplied && _baseTyreW != null)
                    {
                        for (int i = 0; i < wn && i < _baseTyreW.Length; i++)
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_WIDTH, _baseTyreW[i]);
                        _widthColApplied = false;
                    }
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyRender error: {ex.Message}"); }
        }

        /// <summary>A small upward impulse to break a parked car's rest state so it settles onto the new
        /// fitment immediately (a resting/"fixed" body ignores ACTIVATE_PHYSICS and velocity writes). No
        /// teleport — SET_VEHICLE_ON_GROUND_PROPERLY makes offset wheels jump.</summary>
        public void Nudge()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                Function.Call(Hash.ACTIVATE_PHYSICS, _vehicle);
                if (_vehicle.Speed < 0.5f)
                    Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle, 1,
                        0f, 0f, 0.3f, 0f, 0f, 0f, 0, false, true, true, false, true);
            }
            catch { }
        }

        /// <summary>Reset every slider to stock.</summary>
        public void Reset()
        {
            _frontCamber = 0f;
            _rearCamber = 0f;
            _frontTrackWidth = 0f;
            _rearTrackWidth = 0f;
            _frontHeight = 0f;
            _rearHeight = 0f;
            _rake = 0f;
            _visualSize = 1f;
            _visualWidth = 1f;
            Log?.Invoke("[WheelFitment] Reset to stock");
        }

        /// <summary>Clear our ride-height offset, restoring the car's fake-lowering to the suspension mod's
        /// baseline. Called on wheel-preview and vehicle handoff. Render-only + per-vehicle, so there's no
        /// model-shared bleed to undo — this just removes our visual slam so a previewed/handed-off car sits
        /// at its mod-defined height. The stance re-applies from save on re-entry.</summary>
        public void RestoreSuspension()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                WheelMemory.SetFakeLowering(_vehicle, _stockFake);
                _fakeApplied = false;

                // Also clear the VISUAL wheel size/width + their colliders back to natural — otherwise a stance's
                // scale stays baked into the wheels while previewing rims (wheels look skinny + ride height drifts
                // rim-to-rim). The per-wheel CWheel fields rebuild on a swap, but the StreamRenderGfx visual scale
                // is per-vehicle and persists, so it MUST be reset here.
                if (_hasVisualWheels)
                {
                    if (_visSizeApplied)  { WheelMemory.SetVisualSize(_vehicle, _baseVisualSize);  _visSizeApplied = false; }
                    if (_visWidthApplied) { WheelMemory.SetVisualWidth(_vehicle, _baseVisualWidth); _visWidthApplied = false; }
                    int n = WheelMemory.GetWheelCount(_vehicle);
                    if (_sizeColApplied && _baseTyreR != null && _baseRimR != null)
                        for (int i = 0; i < n && i < _baseTyreR.Length; i++)
                        {
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_RADIUS, _baseTyreR[i]);
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_RIM_RADIUS, _baseRimR[i]);
                        }
                    _sizeColApplied = false;
                    if (_widthColApplied && _baseTyreW != null)
                        for (int i = 0; i < n && i < _baseTyreW.Length; i++)
                            WheelMemory.SetWheelField(_vehicle, i, WheelMemory.OFF_TYRE_WIDTH, _baseTyreW[i]);
                    _widthColApplied = false;
                }
            }
            catch { }
        }

        /// <summary>Write the ride-height (fake-lowering) field right now — used to apply our stance on the
        /// SAME frame as a menu transition (e.g. landing on "Custom Suspension and Camber") so there's no
        /// one-frame gap where the field sits at the base.</summary>
        public void ApplyRideHeightNow()
        {
            try { if (_vehicle != null && _vehicle.Exists()) WriteFakeLowering(); } catch { }
        }

        // Compute + write the fake-lowering (ride height) field. Shared by ApplyRender + ApplyRideHeightNow.
        private void WriteFakeLowering()
        {
            float slamOffset = (_frontHeight + _rearHeight) * 0.5f;
            if (slamOffset > MAX_RAISE) slamOffset = MAX_RAISE;
            if (slamOffset < MIN_RAISE) slamOffset = MIN_RAISE;
            bool fakeOn = Math.Abs(slamOffset) > 0.0005f;
            if (fakeOn) { WheelMemory.SetFakeLowering(_vehicle, _stockFake + slamOffset); _fakeApplied = true; }
            else if (_fakeApplied) { WheelMemory.SetFakeLowering(_vehicle, _stockFake); _fakeApplied = false; }
        }

        /// <summary>Re-read the car's current fake-lowering as our new baseline — call after the game's own
        /// suspension mod changes the ride height (so our slider offsets from the new level, not the old).</summary>
        public void RefreshStockFake()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                _stockFake = WheelMemory.GetFakeLowering(_vehicle);
                if (_stockFake < -1f || _stockFake > 1f) _stockFake = 0f;
                _fakeApplied = false;
            }
            catch { }
        }

        #endregion

        #region Presets

        public class FitmentPreset
        {
            public string Name { get; set; }
            public string Blurb { get; set; }          // one-line style description for the menu
            public float FrontCamber { get; set; }
            public float RearCamber { get; set; }
            public float FrontTrackWidth { get; set; }
            public float RearTrackWidth { get; set; }
            public float FrontHeight { get; set; }
            public float RearHeight { get; set; }
            public float Rake { get; set; }               // front/rear tilt (+front-low, -rear-low)
            public float VisualSize { get; set; } = 1f;   // wheel size multiplier (needs custom rims)
            public float VisualWidth { get; set; } = 1f;  // wheel width multiplier (needs custom rims)
        }

        public FitmentPreset GetCurrentAsPreset(string name)
        {
            return new FitmentPreset
            {
                Name = name,
                FrontCamber = _frontCamber,
                RearCamber = _rearCamber,
                FrontTrackWidth = _frontTrackWidth,
                RearTrackWidth = _rearTrackWidth,
                FrontHeight = _frontHeight,
                RearHeight = _rearHeight,
                Rake = _rake,
                VisualSize = _visualSize,
                VisualWidth = _visualWidth
            };
        }

        public void ApplyPreset(FitmentPreset preset)
        {
            FrontCamber = preset.FrontCamber;
            RearCamber = preset.RearCamber;
            FrontTrackWidth = preset.FrontTrackWidth;
            RearTrackWidth = preset.RearTrackWidth;
            FrontHeight = preset.FrontHeight;
            RearHeight = preset.RearHeight;
            Rake = preset.Rake;
            if (_hasVisualWheels)
            {
                VisualSize = preset.VisualSize;
                VisualWidth = preset.VisualWidth;
            }
            Log?.Invoke($"[WheelFitment] Applied preset: {preset.Name}");
        }

        // Style presets: a coherent combination per build culture. These are just raw slider values now —
        // re-tune freely.
        public static readonly FitmentPreset[] BuiltInPresets = new[]
        {
            new FitmentPreset
            {
                Name = "Stock",
                Blurb = "Factory fitment"
            },
            new FitmentPreset
            {
                Name = "Slammed",
                Blurb = "Dropped on its sills, tucked wheels",
                FrontCamber = 0.10f, RearCamber = 0.12f,
                FrontHeight = 0.10f, RearHeight = 0.10f,
                FrontTrackWidth = 0.02f, RearTrackWidth = 0.02f
            },
            new FitmentPreset
            {
                Name = "Stance",
                Blurb = "Hard camber, poke, stretched rubber",
                FrontCamber = 0.17f, RearCamber = 0.20f,
                FrontTrackWidth = 0.05f, RearTrackWidth = 0.05f,
                FrontHeight = 0.08f, RearHeight = 0.08f,
                VisualWidth = 0.90f
            },
            new FitmentPreset
            {
                Name = "Donk",
                Blurb = "Huge wheels, body rides high",
                VisualSize = 1.50f, VisualWidth = 1.05f
            },
            new FitmentPreset
            {
                Name = "Lifted Truck",
                Blurb = "Big rubber, wide track, lifted",
                VisualSize = 1.25f, VisualWidth = 1.10f,
                FrontTrackWidth = 0.07f, RearTrackWidth = 0.07f,
                FrontHeight = -0.10f, RearHeight = -0.10f
            },
            new FitmentPreset
            {
                Name = "Drag",
                Blurb = "Fat rear meats, ready to launch",
                VisualSize = 1.05f, VisualWidth = 1.20f,
                RearTrackWidth = 0.05f
            },
            new FitmentPreset
            {
                Name = "Track Day",
                Blurb = "Mild camber and track for grip",
                FrontCamber = 0.05f, RearCamber = 0.04f,
                FrontTrackWidth = 0.05f, RearTrackWidth = 0.04f,
                FrontHeight = 0.04f, RearHeight = 0.04f
            }
        };

        #endregion

        #region Helpers

        public static float ToDegrees(float radians)
        {
            return radians * (180f / (float)Math.PI);
        }

        public static float ToRadians(float degrees)
        {
            return degrees * ((float)Math.PI / 180f);
        }

        #endregion
    }
}
