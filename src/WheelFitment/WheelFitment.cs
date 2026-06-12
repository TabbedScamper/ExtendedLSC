using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Wheel fitment adjustment system - track width, camber, and basic adjustments.
    ///
    /// Uses WheelFitmentAPI.asi (native C++) for safe memory access.
    /// Falls back to limited functionality if ASI is not installed.
    /// </summary>
    public class WheelFitment
    {
        public static Action<string> Log { get; set; }

        // Cached vehicle reference
        private Vehicle _vehicle;
        private bool _nativeApiAvailable = false;
        private bool _initialized = false;

        // Original wheel positions for reset (cached by native API)
        private Dictionary<int, Vector3> _originalWheelOffsets = new Dictionary<int, Vector3>();

        // Current fitment values (relative adjustments)
        private float _frontCamber = 0f;
        private float _rearCamber = 0f;
        private float _frontTrackWidth = 0f;
        private float _rearTrackWidth = 0f;
        private float _frontHeight = 0f;
        private float _rearHeight = 0f;

        // Visual wheel size/width (multipliers, 1.0 = stock) via WheelMemory/StreamRenderGfx.
        // No ASI required for these — pure Marshal memory access.
        private float _visualSize = 1f;   // multiplier (1.0 = no change from the wheel's stock look)
        private float _visualWidth = 1f;  // multiplier
        private float _baseVisualSize = 1f;   // wheel's stock render size (aftermarket wheels are ~0.79)
        private float _baseVisualWidth = 1f;
        private bool _hasVisualWheels = false;
        // Cached stock collision radii/width so visual size scales the contact to match the look.
        private float _stockTyreRadius = 0.393f;
        private float _stockRimRadius = 0.313f;
        private float _stockTyreWidth = 0.298f;
        private bool _stockColliderCached = false;

        // Collider scaling is GENUINE-just-in-time: we never touch the colliders at multiplier 1.0 (which
        // would stamp a possibly-stale cached value and make stock wheels sink). The genuine collider is
        // read from live memory the instant scaling begins, scaled while active, and restored on return
        // to 1.0. CWheel colliders don't reset on a wheel swap, so a cached "stock" value can't be trusted.
        private bool _colliderScaled = false;
        private float _genuineTyreR, _genuineRimR, _genuineTyreW;

        // NATURAL-BASELINE cache, keyed by  model:wheelType:frontWheelModIndex.
        // Each wheel mod (and each wheel type) has its OWN natural visual size and collider radii, so a
        // single per-vehicle baseline is wrong the moment the player swaps rims — or enters a car that
        // already has custom rims. We instead capture the baseline the FIRST time we ever see a given
        // (model + wheel type + wheel-mod index) in its natural state (right after entry, or right after
        // the game installs that rim — before we've scaled anything). After that we reuse the record, so
        // we can never re-capture a value we already enlarged (which made Reset blow up / sizes compound).
        private struct StockWheel
        {
            public bool HasVisual;
            public float BaseSize, BaseWidth;
            public bool ColliderCached;
            public float TyreRadius, RimRadius, TyreWidth;
            public float[] WheelX;   // natural lateral offset per wheel (track-width origin)
            public float[] WheelZ;   // natural vertical offset per wheel (height origin)
        }
        private static readonly Dictionary<string, StockWheel> _stockByKey = new Dictionary<string, StockWheel>();
        private string _currentBaselineKey = null;

        // Limits
        public const float MAX_CAMBER = 0.5f;        // ~28 degrees
        public const float MIN_CAMBER = -0.5f;
        public const float MAX_TRACK_WIDTH = 0.3f;   // 0.3 meters wider
        public const float MIN_TRACK_WIDTH = -0.1f;  // 0.1 meters narrower
        public const float MAX_HEIGHT = 0.15f;       // 0.15 meters higher
        public const float MIN_HEIGHT = -0.15f;      // 0.15 meters lower
        public const float MAX_VISUAL = 1.8f;        // 1.8x wheel size/width
        public const float MIN_VISUAL = 0.5f;        // 0.5x wheel size/width

        // ---- Smooth application (easing) ----
        // The public properties above are TARGETS. Each frame the applied ("current") values below ease
        // toward them exponentially (~0.4s settle), so changes glide like hydraulics instead of snapping —
        // and big collider deltas never shock the physics solver (the cause of the parked-car jumping).
        private float _curFC, _curRC, _curFT, _curRT, _curFH, _curRH;
        private float _curVS = 1f, _curVW = 1f;
        private float _lastAppliedVS = 1f, _lastAppliedVW = 1f;
        private float _curAutoLift = 0f;
        private bool _wasActive = false;
        private const float EASE_RATE = 8f;          // 1/s exponential ease (≈0.4s to settle)

        // Auto-adapt: wheels bigger than the arch can clear automatically lift the body (donk rule —
        // "a wheel that big needs a lift"). NOTE: this is purely AESTHETIC — probing proved GTA wheel
        // wells are holes in the chassis collision (rays from inside the well hit nothing), so an
        // oversized wheel never physically catches the arch; without lift it just looks swallowed.
        // Tuck/poke looks therefore stay completely free.
        private const float ARCH_CLEARANCE = 0.03f;  // free visual arch space before lift kicks in

        // HEIGHT GOES THROUGH THE SUSPENSION, NOT WHEEL-Z. Telemetry proved that pinning wheel-Z at a
        // static offset puts the spring permanently outside its tuned band — at big offsets the body
        // enters a sustained limit-cycle (6-11cm bounce measured at 1.65x + 5.5cm Z pin). The game's own
        // channel for ride height is fSuspensionRaise (CHandlingData) — the solver re-computes its
        // equilibrium around it, so any lift is stable on any vehicle. Wheel-Z is now used ONLY for a
        // small front/rear rake differential, which stays well inside the travel band.
        private const float MAX_RAKE = 0.06f;        // max per-axle wheel-Z differential (front vs rear)

        // Per-MODEL stock suspension values (raise), persisted to disk so a mid-session script reload
        // can never re-capture an already-modified raise as "stock" (handling edits survive reloads).
        private float _stockRaise = 0f;
        private float _travelRange = 0f;             // fSuspensionUpperLimit - fSuspensionLowerLimit
        private float _capMult = MAX_VISUAL;         // per-vehicle max size mult (travel-budget limited)
        private bool _raiseCaptured = false;
        private float _appliedRaiseDelta = 0f;       // last delta actually written (NaN-free tracking)
        private static Dictionary<int, float> _stockRaiseByModel = null;
        private static string _stockHandlingPath = null;

        #region Properties

        // All setters store TARGETS only — Update() eases the applied values toward them each frame.

        public float FrontCamber
        {
            get => _frontCamber;
            set => _frontCamber = Clamp(value, MIN_CAMBER, MAX_CAMBER);
        }

        public float RearCamber
        {
            get => _rearCamber;
            set => _rearCamber = Clamp(value, MIN_CAMBER, MAX_CAMBER);
        }

        public float FrontTrackWidth
        {
            get => _frontTrackWidth;
            set => _frontTrackWidth = Clamp(value, MIN_TRACK_WIDTH, MAX_TRACK_WIDTH);
        }

        public float RearTrackWidth
        {
            get => _rearTrackWidth;
            set => _rearTrackWidth = Clamp(value, MIN_TRACK_WIDTH, MAX_TRACK_WIDTH);
        }

        public float FrontHeight
        {
            get => _frontHeight;
            set => _frontHeight = Clamp(value, MIN_HEIGHT, MAX_HEIGHT);
        }

        public float RearHeight
        {
            get => _rearHeight;
            set => _rearHeight = Clamp(value, MIN_HEIGHT, MAX_HEIGHT);
        }

        /// <summary>Visual wheel size multiplier (1.0 = stock). Also scales the collision radius to match,
        /// and auto-lifts the body when the wheel outgrows the arch clearance (donk rule).</summary>
        public float VisualSize
        {
            get => _visualSize;
            set => _visualSize = Clamp(value, MIN_VISUAL, MAX_VISUAL);
        }

        /// <summary>Visual wheel width multiplier (1.0 = stock). Also scales the collision width to match.</summary>
        public float VisualWidth
        {
            get => _visualWidth;
            set => _visualWidth = Clamp(value, MIN_VISUAL, MAX_VISUAL);
        }

        /// <summary>The wheel's natural overall diameter in meters (for display: inches = this/0.0254).</summary>
        public float BaseVisualDiameter => _baseVisualSize;

        /// <summary>This vehicle's max wheel-size multiplier — the suspension-travel stability budget.
        /// Visual AND collider are both capped here (beyond it the tyre would sink or the body bounce).</summary>
        public float MaxSizeMult => Math.Max(1f, Math.Min(MAX_VISUAL, _capMult));

        /// <summary>True if the current vehicle has aftermarket wheels (visual size/width available).</summary>
        public bool HasVisualWheels => _hasVisualWheels;

        /// <summary>The vehicle this fitment instance is bound to (null if uninitialized).</summary>
        public Vehicle Vehicle => _vehicle;

        public bool IsInitialized => _vehicle != null && _vehicle.Exists() && _initialized;

        /// <summary>
        /// Check if full wheel fitment is available (native API loaded)
        /// </summary>
        public bool IsFullFitmentAvailable => _nativeApiAvailable;

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize fitment system for a vehicle
        /// </summary>
        public bool Initialize(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
            {
                Log?.Invoke("[WheelFitment] Initialize failed - invalid vehicle");
                return false;
            }

            // Switching cars: put the OLD car's (shared, per-model) suspension raise back to stock first.
            if (_vehicle != null && _vehicle != vehicle && _vehicle.Exists())
            {
                try { RestoreSuspension(); } catch { }
            }

            _vehicle = vehicle;
            _initialized = false;
            _camberCached = false;

            // Initialize native API (only once)
            WheelFitmentNative.Log = Log;
            _nativeApiAvailable = WheelFitmentNative.IsAvailable;

            // All fitment (camber/track/height/size/width) now runs through WheelMemory (Marshal) using
            // the live-verified build-3788 offsets — no ASI required. The flaky WheelFitmentAPI.asi
            // brute-force offset probe was locking onto the wrong wheel offset and crashing on write.
            // (The natural X/Z track/height origin is captured inside RefreshBaseline, keyed per wheel,
            //  so it can never be read back from already-modified memory.)

            // Reset fitment values
            _frontCamber = 0f;
            _rearCamber = 0f;
            _frontTrackWidth = 0f;
            _rearTrackWidth = 0f;
            _frontHeight = 0f;
            _rearHeight = 0f;

            // Fresh vehicle -> multipliers start at "no change", then resolve the natural baseline for
            // whatever wheel is currently installed (records it the first time we see this
            // model+wheelType+wheel-mod; handles cars that already have custom rims on entry).
            _visualSize = 1f;
            _visualWidth = 1f;
            // Eased values snap to stock on (re)init — a saved stance loaded right after will then
            // glide in smoothly from stock.
            _curFC = _curRC = _curFT = _curRT = _curFH = _curRH = 0f;
            _curVS = _curVW = 1f;
            _lastAppliedVS = _lastAppliedVW = 1f;
            _curAutoLift = 0f;
            _wasActive = false;
            _currentBaselineKey = null;
            try { _vehicle.Mods.InstallModKit(); } catch { }
            CaptureStockRaise();
            RefreshBaseline();
            _lastFitmentSig = FitmentSig();   // baseline the settle-detector so entry alone doesn't nudge
            _settleAtTime = 0;

            _initialized = true;
            Log?.Invoke($"[WheelFitment] Initialized for {vehicle.DisplayName}");
            return true;
        }

        /// <summary>
        /// Identity of the currently-installed wheel: model + wheel type + front wheel-mod index.
        /// Two different rims (or wheel types) have different natural sizes, so this is the cache key.
        /// </summary>
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

        /// <summary>
        /// Resolve the natural baseline (visual size/width + collider radii) for the wheel currently
        /// installed. Capture it the first time we ever see this model+wheelType+wheel-mod in its natural
        /// state; reuse the record otherwise. Re-applies the current size/width multiplier so the slider
        /// value carries over when the player swaps rims. Called on init and (cheaply) every tick — it
        /// only does work when the wheel identity actually changes.
        /// </summary>
        private void RefreshBaseline()
        {
            if (_vehicle == null || !_vehicle.Exists()) return;
            string key = ResolveWheelKey();
            if (key == null || key == _currentBaselineKey) return;   // unchanged -> nothing to do
            _currentBaselineKey = key;
            _colliderScaled = false;   // new wheel: re-capture its own genuine collider before any scaling

            try
            {
                float[] xs, zs;
                if (_stockByKey.TryGetValue(key, out StockWheel cached))
                {
                    _hasVisualWheels     = cached.HasVisual;
                    _stockColliderCached = cached.ColliderCached;
                    _stockTyreRadius     = cached.TyreRadius;
                    _stockRimRadius      = cached.RimRadius;
                    _stockTyreWidth      = cached.TyreWidth;
                    _baseVisualSize      = cached.BaseSize;
                    _baseVisualWidth     = cached.BaseWidth;
                    xs = cached.WheelX;
                    zs = cached.WheelZ;
                    Log?.Invoke($"[WheelFitment] Baseline (cached) {key}: base={_baseVisualSize:F3} tyreR={_stockTyreRadius:F3}");
                }
                else
                {
                    // First sight of this rim -> memory currently holds its NATURAL values (we have not
                    // scaled this structure yet), so it is safe to record them as the baseline.
                    //
                    // CRITICAL: the StreamRenderGfx struct EXISTS even on stock wheels, but the renderer
                    // only USES its size field for aftermarket rims. On stock wheels (front mod == -1) a
                    // size multiplier would shrink the COLLIDER with no visual change -> tyres sink into
                    // the ground. So visual size/width is only offered with custom rims installed.
                    bool customRims = false;
                    try { customRims = Function.Call<int>(Hash.GET_VEHICLE_MOD, _vehicle, 23) != -1; } catch { }
                    _hasVisualWheels = customRims && WheelMemory.HasVisualWheels(_vehicle);
                    float tr = WheelMemory.GetTyreColliderRadius(_vehicle, 0);
                    if (tr > 0.05f && tr < 2f)
                    {
                        _stockTyreRadius = tr;
                        _stockRimRadius = WheelMemory.GetRimColliderRadius(_vehicle, 0);
                        _stockTyreWidth = WheelMemory.GetTyreColliderWidth(_vehicle, 0);
                        _stockColliderCached = true;
                    }
                    else _stockColliderCached = false;

                    _baseVisualSize = WheelMemory.GetVisualSize(_vehicle);
                    _baseVisualWidth = WheelMemory.GetVisualWidth(_vehicle);
                    if (_baseVisualSize < 0.1f || _baseVisualSize > 5f) _baseVisualSize = 1f;
                    if (_baseVisualWidth < 0.1f || _baseVisualWidth > 5f) _baseVisualWidth = 1f;

                    // Natural X (track origin) and Z (height origin) per wheel — captured ONCE here so the
                    // origin can never be a value we already moved (which corrupted track/height/reset).
                    int n = WheelMemory.GetWheelCount(_vehicle);
                    xs = new float[n];
                    zs = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        xs[i] = WheelMemory.GetWheelX(_vehicle, i);
                        zs[i] = WheelMemory.GetWheelZ(_vehicle, i);
                    }

                    _stockByKey[key] = new StockWheel
                    {
                        HasVisual = _hasVisualWheels,
                        BaseSize = _baseVisualSize,
                        BaseWidth = _baseVisualWidth,
                        ColliderCached = _stockColliderCached,
                        TyreRadius = _stockTyreRadius,
                        RimRadius = _stockRimRadius,
                        TyreWidth = _stockTyreWidth,
                        WheelX = xs,
                        WheelZ = zs
                    };
                    Log?.Invoke($"[WheelFitment] Baseline (captured) {key}: hasVisual={_hasVisualWheels} base={_baseVisualSize:F3} tyreR={_stockTyreRadius:F3}");
                }

                // Rebuild the track/height origin map from the cached natural offsets (never re-read from
                // possibly-modified memory).
                _originalWheelOffsets.Clear();
                if (xs != null && zs != null)
                    for (int i = 0; i < xs.Length && i < zs.Length; i++)
                        _originalWheelOffsets[i] = new Vector3(xs[i], 0f, zs[i]);

                // STABILITY BUDGET (measured live, Buffalo b3788): extra collider radius eats suspension
                // travel; past ~(upper-lower) the body enters a sustained limit-cycle (1.50x stable,
                // 1.65x bounced — travel 0.21m). This vehicle's max size = 85% of its travel as radius.
                _capMult = (_travelRange > 0.01f && _baseVisualSize > 0.05f)
                    ? 1f + (2f * _travelRange * 0.85f) / _baseVisualSize
                    : MAX_VISUAL;

                // The wheel just changed (or first init): the game built a fresh, natural structure for
                // it, wiping any scaling we had on the previous wheel. Re-apply the current multipliers so
                // the size/width the player dialed in sticks across the rim swap.
                ApplyVisual();
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] RefreshBaseline error: {ex.Message}"); }
        }

        /// <summary>Apply visual wheel size/width + scale collision so the car rides on the new size.</summary>
        private void ApplyVisual()
        {
            if (!IsInitialized || !_hasVisualWheels) return;
            try
            {
                // Visual size/width live in StreamRenderGfx, which the game rebuilds fresh on every wheel
                // swap — so _baseVisual* is reliable and writing base*mult (== base at mult 1.0) is harmless.
                // Uses the EASED values so changes glide instead of snapping.
                // Visual and collider are capped TOGETHER at the per-vehicle travel budget: a visual
                // bigger than the collider sinks below the contact point by exactly the difference (body
                // height can't fix that), and a collider past the budget makes the body bounce.
                float sizeMult = Math.Min(_curVS, MaxSizeMult);
                WheelMemory.SetVisualSize(_vehicle, _baseVisualSize * sizeMult);
                WheelMemory.SetVisualWidth(_vehicle, _baseVisualWidth * _curVW);

                bool scaling = Math.Abs(sizeMult - 1f) > 0.001f || Math.Abs(_curVW - 1f) > 0.001f;
                if (scaling)
                {
                    if (!_colliderScaled)
                    {
                        // First scale on this wheel: capture the GENUINE collider from live memory NOW
                        // (still untouched by us) so we scale the real value, never a stale cached one.
                        float tr = WheelMemory.GetTyreColliderRadius(_vehicle, 0);
                        if (tr > 0.05f && tr < 2f)
                        {
                            _genuineTyreR = tr;
                            _genuineRimR = WheelMemory.GetRimColliderRadius(_vehicle, 0);
                            _genuineTyreW = WheelMemory.GetTyreColliderWidth(_vehicle, 0);
                            _colliderScaled = true;
                        }
                    }
                    if (_colliderScaled)
                    {
                        // Collider scales with the SAME capped multiplier as the visual (sizeMult above),
                        // so contact point and rendered tyre always agree — no sink, no bounce.
                        WheelMemory.SetAllColliderRadius(_vehicle, _genuineTyreR * sizeMult, _genuineRimR * sizeMult);
                        WheelMemory.SetAllColliderWidth(_vehicle, _genuineTyreW * _curVW);
                    }
                }
                else if (_colliderScaled)
                {
                    // Back to 1.0x: restore the genuine collider we captured before scaling.
                    WheelMemory.SetAllColliderRadius(_vehicle, _genuineTyreR, _genuineRimR);
                    WheelMemory.SetAllColliderWidth(_vehicle, _genuineTyreW);
                    _colliderScaled = false;
                }
                // else (mult 1.0, never scaled): DO NOT touch the colliders — keep the game's genuine
                // values so stock / unscaled wheels never sink.
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyVisual error: {ex.Message}"); }
        }

        private void CacheOriginalValues()
        {
            _originalWheelOffsets.Clear();
            try
            {
                int numWheels = WheelMemory.GetWheelCount(_vehicle);
                if (numWheels == 0)
                {
                    Log?.Invoke($"[WheelFitment] No wheels detected for {_vehicle.DisplayName}");
                    return;
                }
                for (int i = 0; i < numWheels; i++)
                {
                    float x = WheelMemory.GetWheelX(_vehicle, i);
                    float z = WheelMemory.GetWheelZ(_vehicle, i);
                    _originalWheelOffsets[i] = new Vector3(x, 0f, z);
                }
                Log?.Invoke($"[WheelFitment] Cached {numWheels} wheels (Marshal). W0: X={_originalWheelOffsets[0].X:F3} Z={_originalWheelOffsets[0].Z:F3}");
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] CacheOriginalValues error: {ex.Message}"); }
        }

        #endregion

        #region Fitment Application

        private int _debugCounter = 0;

        // Cache original camber values
        private float _originalFrontCamber = 0f;
        private float _originalRearCamber = 0f;
        private bool _camberCached = false;

        /// <summary>
        /// Apply all fitment adjustments (uses the EASED current values).
        /// </summary>
        public void ApplyFitment()
        {
            if (!IsInitialized) return;

            ApplyCamber();
            if (_originalWheelOffsets.Count > 0)
            {
                ApplyTrackWidth();
                ApplyHeight();
            }
        }

        private void ApplyCamber()
        {
            if (Math.Abs(_curFC) < 0.0005f && Math.Abs(_curRC) < 0.0005f)
                return;
            try
            {
                // Front wheels (0,1), rear (2,3). Left/right mirror the sign so it cambers symmetrically.
                int n = WheelMemory.GetWheelCount(_vehicle);
                for (int i = 0; i < n; i++)
                {
                    bool isFront = (i == 0 || i == 1);
                    float camber = isFront ? _curFC : _curRC;
                    bool isLeft = (i % 2 == 0);
                    WheelMemory.SetWheelCamber(_vehicle, i, isLeft ? camber : -camber);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyCamber error: {ex.Message}"); }
        }

        private void ApplyTrackWidth()
        {
            try
            {
                foreach (var kvp in _originalWheelOffsets)
                {
                    int wheelIndex = kvp.Key;
                    Vector3 original = kvp.Value;
                    bool isFront = (wheelIndex == 0 || wheelIndex == 1);
                    bool isLeft = (wheelIndex % 2 == 0);
                    float trackWidth = isFront ? _curFT : _curRT;
                    float trackOffset = isLeft ? -trackWidth : trackWidth;
                    WheelMemory.SetWheelX(_vehicle, wheelIndex, original.X + trackOffset);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyTrackWidth error: {ex.Message}"); }
        }

        private void ApplyHeight()
        {
            if (Math.Abs(_curFH) < 0.0005f && Math.Abs(_curRH) < 0.0005f)
                return;
            try
            {
                // ONE travel budget per vehicle, shared between wheel size and lift: the extra collider
                // radius already in play spends part of it; lift (wheel-down, negative height) gets the
                // remainder. Inside the budget the suspension stays in its band -> never bounces.
                // Slam (positive height = wheel up) is compression-side and was stable at every value
                // tested, so only the slider limit applies there.
                float appliedMult = Math.Min(Math.Max(_curVS, 1f), MaxSizeMult);
                float extraR = _hasVisualWheels ? _baseVisualSize * (appliedMult - 1f) * 0.5f : 0f;
                float budget = (_travelRange > 0.01f ? _travelRange : 0.20f) * 0.85f;
                float liftMax = Math.Max(0f, budget - extraR);

                foreach (var kvp in _originalWheelOffsets)
                {
                    int wheelIndex = kvp.Key;
                    Vector3 original = kvp.Value;
                    bool isFront = (wheelIndex == 0 || wheelIndex == 1);
                    float h = isFront ? _curFH : _curRH;
                    if (h < -liftMax) h = -liftMax;   // lift clamped to the remaining travel budget
                    WheelMemory.SetWheelZ(_vehicle, wheelIndex, original.Z + h);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] ApplyHeight error: {ex.Message}"); }
        }

        // (Auto-lift removed: with visual and collider capped TOGETHER at the travel budget, a bigger
        //  wheel raises the body naturally — the contact point sits lower, so the body rides higher.)

        // ====================================================================
        // Suspension raise (stock capture retained for the travel budget; fSuspensionRaise itself is
        // init-baked and NOT writable live — verified — so height runs through budgeted wheel-Z)
        // ====================================================================
        /// <summary>Resolve this model's STOCK fSuspensionRaise — from the on-disk cache if we've ever
        /// seen the model before (contamination-proof across reloads), else from live handling.</summary>
        private void CaptureStockRaise()
        {
            _raiseCaptured = false;
            try
            {
                if (_stockRaiseByModel == null)
                {
                    _stockRaiseByModel = new Dictionary<int, float>();
                    _stockHandlingPath = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "stock_handling.dat");
                    try
                    {
                        if (System.IO.File.Exists(_stockHandlingPath))
                            foreach (string line in System.IO.File.ReadAllLines(_stockHandlingPath))
                            {
                                var parts = line.Split('=');
                                if (parts.Length == 2 && int.TryParse(parts[0], out int mh) &&
                                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float rv))
                                    _stockRaiseByModel[mh] = rv;
                            }
                    }
                    catch { }
                }

                var hd = _vehicle.HandlingData;
                if (hd == null || !hd.IsValid) return;
                _travelRange = hd.SuspensionUpperLimit - hd.SuspensionLowerLimit;

                int model = _vehicle.Model.Hash;
                if (_stockRaiseByModel.TryGetValue(model, out float stock))
                {
                    _stockRaise = stock;
                }
                else
                {
                    _stockRaise = hd.SuspensionRaise;
                    _stockRaiseByModel[model] = _stockRaise;
                    try
                    {
                        System.IO.File.AppendAllText(_stockHandlingPath,
                            model + "=" + _stockRaise.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                    }
                    catch { }
                }
                _raiseCaptured = true;
                _appliedRaiseDelta = hd.SuspensionRaise - _stockRaise;   // may be nonzero after a reload
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] CaptureStockRaise error: {ex.Message}"); }
        }

        private void WriteRaise(float delta)
        {
            if (!_raiseCaptured) return;
            try
            {
                var hd = _vehicle.HandlingData;
                if (hd == null || !hd.IsValid) return;
                hd.SuspensionRaise = _stockRaise + delta;
                _appliedRaiseDelta = delta;
            }
            catch { }
        }

        /// <summary>Restore the model's stock suspension raise (Reset / detach / vehicle switch).
        /// NOTE: handling is SHARED per model — restoring affects all cars of this model.</summary>
        public void RestoreSuspension()
        {
            if (_raiseCaptured && Math.Abs(_appliedRaiseDelta) > 0.0001f)
                WriteRaise(0f);
        }

        /// <summary>One-time exact restore of camber/track/height when easing reaches all-zero.</summary>
        private void RestoreGeometry()
        {
            try
            {
                foreach (var kvp in _originalWheelOffsets)
                {
                    WheelMemory.SetWheelCamber(_vehicle, kvp.Key, 0f);
                    WheelMemory.SetWheelX(_vehicle, kvp.Key, kvp.Value.X);
                    WheelMemory.SetWheelZ(_vehicle, kvp.Key, kvp.Value.Z);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] RestoreGeometry error: {ex.Message}"); }
        }

        /// <summary>
        /// Reset all fitment to stock
        /// </summary>
        public void Reset()
        {
            // Targets to stock — easing glides everything back smoothly; when the eased values reach
            // zero, Update() does the one-time exact restore (RestoreGeometry + genuine collider).
            _frontCamber = 0f;
            _rearCamber = 0f;
            _frontTrackWidth = 0f;
            _rearTrackWidth = 0f;
            _frontHeight = 0f;
            _rearHeight = 0f;
            _visualSize = 1f;
            _visualWidth = 1f;
            Log?.Invoke("[WheelFitment] Reset to stock (easing)");
        }

        /// <summary>
        /// Must be called every frame to maintain fitment
        /// </summary>
        // One-shot physics re-settle bookkeeping (see Update / SettlePhysics).
        private int _settleAtTime = 0;
        private float _lastFitmentSig = 0f;

        private float FitmentSig()
            => _curFC + _curRC * 1.7f + _curFT * 2.3f + _curRT * 3.1f
             + _curFH * 4.3f + _curRH * 5.7f + _curVS * 6.1f + _curVW * 7.9f;

        public void Update()
        {
            if (!IsInitialized) return;

            // Detect rim / wheel-type swaps (e.g. the player picked a new wheel in the LSC menu) and
            // re-resolve the natural baseline for the new wheel, re-applying the current multipliers.
            RefreshBaseline();

            // Ease the applied values toward their targets (smooth, framerate-independent).
            float dt = Game.LastFrameTime;
            float k = 1f - (float)Math.Exp(-EASE_RATE * Math.Max(dt, 0.001f));
            _curFC = Ease(_curFC, _frontCamber, k);
            _curRC = Ease(_curRC, _rearCamber, k);
            _curFT = Ease(_curFT, _frontTrackWidth, k);
            _curRT = Ease(_curRT, _rearTrackWidth, k);
            _curFH = Ease(_curFH, _frontHeight, k);
            _curRH = Ease(_curRH, _rearHeight, k);
            _curVS = Ease(_curVS, _visualSize, k);
            _curVW = Ease(_curVW, _visualWidth, k);

            // Camber/track/height reset every frame, so re-apply continuously while anything is non-stock
            // (Marshal, VirtualQuery-guarded). On the frame everything reaches stock, restore exactly once.
            bool active = Math.Abs(_curFC) > 0.0005f || Math.Abs(_curRC) > 0.0005f
                       || Math.Abs(_curFT) > 0.0005f || Math.Abs(_curRT) > 0.0005f
                       || Math.Abs(_curFH) > 0.0005f || Math.Abs(_curRH) > 0.0005f;

            // NOTE: fSuspensionRaise is baked into wheel geometry at wheel-init only (verified live —
            // edits + SET_VEHICLE_MOD re-applies never propagate), so height runs through wheel-Z,
            // budgeted against suspension travel in ApplyHeight. Body height from SIZE comes free:
            // a bigger (capped) collider radius lowers the contact point, riding the body up naturally.

            // Oscillation telemetry (logs 1Hz when DebugLogging on)
            DebugOscillation(active, _curFH);

            if (active)
            {
                ApplyFitment();
                _wasActive = true;
            }
            else if (_wasActive)
            {
                _wasActive = false;
                RestoreGeometry();
                RestoreSuspension();
            }

            // Visual size/width: write only while their eased values are still moving (or on first apply).
            if (Math.Abs(_curVS - _lastAppliedVS) > 0.0005f || Math.Abs(_curVW - _lastAppliedVW) > 0.0005f)
            {
                _lastAppliedVS = _curVS;
                _lastAppliedVW = _curVW;
                ApplyVisual();
            }

            // After any fitment change the car can sit "floating" until physics next runs (it only drops
            // when you drive off). Schedule a single re-settle ~0.12s after the easing stops moving.
            float sig = FitmentSig();
            if (Math.Abs(sig - _lastFitmentSig) > 0.0001f)
            {
                _lastFitmentSig = sig;
                _settleAtTime = Game.GameTime + 120;
            }
            if (_settleAtTime != 0 && Game.GameTime >= _settleAtTime)
            {
                _settleAtTime = 0;
                SettlePhysics();
            }
        }

        private static float Ease(float cur, float target, float k)
        {
            float next = cur + (target - cur) * k;
            return Math.Abs(target - next) < 0.0004f ? target : next;
        }

        // ---- per-frame oscillation telemetry (active only while DebugLogging is on) ----
        // Reads what the GAME left in wheel-Z this frame (before our pin) and the body Z; logs the
        // 1-second spread so we can see exactly which component vibrates and by how much.
        private float _dbgGZmin, _dbgGZmax, _dbgBmin, _dbgBmax, _dbgFightMax;
        private int _dbgFrames = 0, _dbgNextLog = 0;

        private void DebugOscillation(bool active, float zOffFront)
        {
            try
            {
                float gameZ = WheelMemory.GetWheelZ(_vehicle, 0);
                float bodyZ = _vehicle.Position.Z;
                if (_dbgFrames == 0)
                {
                    _dbgGZmin = _dbgGZmax = gameZ;
                    _dbgBmin = _dbgBmax = bodyZ;
                    _dbgFightMax = 0f;
                }
                _dbgGZmin = Math.Min(_dbgGZmin, gameZ); _dbgGZmax = Math.Max(_dbgGZmax, gameZ);
                _dbgBmin = Math.Min(_dbgBmin, bodyZ);  _dbgBmax = Math.Max(_dbgBmax, bodyZ);
                if (_originalWheelOffsets.TryGetValue(0, out Vector3 o))
                    _dbgFightMax = Math.Max(_dbgFightMax, Math.Abs(gameZ - (o.Z + zOffFront)));
                _dbgFrames++;
                if (Game.GameTime >= _dbgNextLog && _dbgFrames > 10)
                {
                    Log?.Invoke($"[WF osc] f={_dbgFrames} gameZ spread={_dbgGZmax - _dbgGZmin:F4} fight={_dbgFightMax:F4} body spread={_dbgBmax - _dbgBmin:F4} curVS={_curVS:F2} raiseDelta={zOffFront:F3} active={active}");
                    _dbgFrames = 0;
                    _dbgNextLog = Game.GameTime + 1000;
                }
            }
            catch { }
        }

        /// <summary>
        /// Force the vehicle's physics to re-evaluate so wheel-offset/collider edits settle immediately
        /// instead of leaving the body floating until the player drives off.
        /// </summary>
        private void SettlePhysics()
        {
            try
            {
                if (_vehicle == null || !_vehicle.Exists()) return;
                // Wake the body for real: a resting/"fixed" vehicle ignores ACTIVATE_PHYSICS and even
                // velocity writes (measured), so the body never rises onto raised suspension / bigger
                // wheels until driven. A small upward impulse breaks the rest state; gravity + the
                // suspension then settle the body to its new natural height. No teleporting
                // (SET_VEHICLE_ON_GROUND_PROPERLY made offset wheels jump).
                Function.Call(Hash.ACTIVATE_PHYSICS, _vehicle);
                if (_vehicle.Speed < 0.5f)
                {
                    Function.Call(Hash.APPLY_FORCE_TO_ENTITY, _vehicle, 1,
                        0f, 0f, 0.5f, 0f, 0f, 0f, 0, false, true, true, false, true);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[WheelFitment] SettlePhysics error: {ex.Message}"); }
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
                VisualSize = _visualSize,
                VisualWidth = _visualWidth
            };
        }

        public void ApplyPreset(FitmentPreset preset)
        {
            // Targets only — easing glides the whole look in over ~0.4s.
            FrontCamber = preset.FrontCamber;
            RearCamber = preset.RearCamber;
            FrontTrackWidth = preset.FrontTrackWidth;
            RearTrackWidth = preset.RearTrackWidth;
            FrontHeight = preset.FrontHeight;
            RearHeight = preset.RearHeight;
            if (_hasVisualWheels)
            {
                VisualSize = preset.VisualSize;
                VisualWidth = preset.VisualWidth;
            }

            Log?.Invoke($"[WheelFitment] Applied preset: {preset.Name}");
        }

        // Style presets: a coherent, stable combination per build culture. Wheel size beyond the arch
        // auto-lifts the body (AutoLift), so Donk "just works" — crank size, body rises with it.
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

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

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
