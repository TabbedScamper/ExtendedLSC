using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace ExtendedLSC
{
    /// <summary>
    /// Memoizes GET_DISPLAY_NAME_FROM_VEHICLE_MODEL by model hash. A model's display-name token (e.g. "ADDER")
    /// NEVER changes, yet SHVDN's <see cref="Vehicle.DisplayName"/> re-calls the native on every access — and ELSC
    /// reads it many times per frame to build per-vehicle keys (VehicleKey, window-tint key, snapshot key), some in
    /// loops over nearby cars. The profiler showed GET_DISPLAY_NAME_FROM_VEHICLE_MODEL at ~6 calls/frame (and an
    /// equal number of GET_ENTITY_MODEL, since DisplayName reads .Model too).
    ///
    /// <para>Returns the SAME string SHVDN would (it wraps the identical native + Model.Hash), so any save-data keys
    /// built from it are byte-for-byte unchanged — this is a pure speed cache, not a behavior change.</para>
    /// </summary>
    public static class VehNameCache
    {
        private static readonly Dictionary<int, string> _byModel = new Dictionary<int, string>();

        // Per-FRAME model-by-handle cache. A handle's model is constant within a frame, but ELSC reads the current
        // vehicle's model many times per frame (VehicleKey from plate-binding, window-tint, snapshot, …). NewFrame()
        // clears it once per OnTick so each handle costs ONE GET_ENTITY_MODEL per frame instead of ~10. Cleared from
        // Main.OnTick rather than keyed on Game.GameTime so we don't spend a GET_GAME_TIMER native to save a model read.
        private static readonly Dictionary<int, int> _modelByHandle = new Dictionary<int, int>();

        /// <summary>Call once at the top of OnTick to invalidate the per-frame model cache.</summary>
        public static void NewFrame() => _modelByHandle.Clear();

        /// <summary>This vehicle's model hash, cached for the current frame (one GET_ENTITY_MODEL per handle per frame).</summary>
        public static int ModelHash(Vehicle v)
        {
            if (v == null || !v.Exists()) return 0;
            int h = v.Handle;
            if (!_modelByHandle.TryGetValue(h, out int model))
            {
                model = v.Model.Hash;                       // GET_ENTITY_MODEL — once per handle per frame
                _modelByHandle[h] = model;
            }
            return model;
        }

        /// <summary>Display-name token for this vehicle's model (cached forever). Mirrors <c>vehicle.DisplayName</c>.</summary>
        public static string Of(Vehicle v)
        {
            if (v == null || !v.Exists()) return null;
            int model = ModelHash(v);                       // per-frame cached model is the permanent-memo key
            if (!_byModel.TryGetValue(model, out string name))
            {
                name = Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, model);
                _byModel[model] = name;                     // immutable per model → safe to keep for the session
            }
            return name;
        }
    }
}
