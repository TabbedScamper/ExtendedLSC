using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using Newtonsoft.Json;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// One saved stance, tied to a SPECIFIC car (not a model). Persisted to disk.
    /// Identity, in order of strength:
    ///   1. Decorator ID  - a unique hidden int stamped on the entity (collision-proof while loaded).
    ///   2. Model + plate - survives despawn/respawn + game restarts (the on-disk key).
    ///   3. Fingerprint   - colors/tint/livery/wheel-type, to disambiguate / re-tag after a respawn.
    /// </summary>
    public class StanceRecord
    {
        public int Id;
        public int Model;
        public string Plate = "";
        public int Fingerprint;            // hash of the extended visual fingerprint
        public float FrontCamber, RearCamber;
        public float FrontTrackWidth, RearTrackWidth;
        public float FrontHeight, RearHeight;
        public float Rake;
        public float StockFake;   // clean suspension-mod base for ride height (anti-drift on re-entry)
        public float VisualSize = 1f, VisualWidth = 1f;

        [JsonIgnore]
        public bool IsStock =>
            Math.Abs(FrontCamber) < 0.001f && Math.Abs(RearCamber) < 0.001f &&
            Math.Abs(FrontTrackWidth) < 0.001f && Math.Abs(RearTrackWidth) < 0.001f &&
            Math.Abs(FrontHeight) < 0.001f && Math.Abs(RearHeight) < 0.001f &&
            Math.Abs(Rake) < 0.001f &&
            Math.Abs(VisualSize - 1f) < 0.001f && Math.Abs(VisualWidth - 1f) < 0.001f;
    }

    /// <summary>
    /// Tracks stanced cars and auto-applies their stance when the player comes within range — even when
    /// not driving them. Keyed per specific car so two cars of the same model never share a stance.
    /// </summary>
    public class VehicleStanceManager
    {
        public static Action<string> Log { get; set; }

        private const string DECOR = "ELSC_STANCE_ID";
        private const int DECOR_TYPE_INT = 3;
        private const float RANGE = 70f;          // auto-apply radius
        private const float DROP_RANGE = 90f;     // detach beyond this (hysteresis)
        private const int SCAN_INTERVAL = 500;    // ms between discovery scans

        private readonly Dictionary<int, WheelFitment> _active = new Dictionary<int, WheelFitment>(); // handle -> fitment
        private readonly Dictionary<int, int> _activeId = new Dictionary<int, int>();                  // handle -> stance id
        private readonly List<StanceRecord> _records = new List<StanceRecord>();
        private readonly List<int> _activeScratch = new List<int>();   // reused each frame to iterate _active without allocating
        private int _nextId = 1;
        private int _lastScan = -100000;
        private bool _decorReady = false;
        private string _path;

        /// <summary>Returns the vehicle currently being live-edited in the menu (skip it — the menu owns it).</summary>
        public Func<Vehicle> ExcludeVehicle { get; set; }

        // ====================================================================
        // Lifecycle
        // ====================================================================
        public void Initialize()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "stances.json");

                // Register our decorator early so DECOR_SET/GET work this session.
                if (!Function.Call<bool>(Hash.DECOR_IS_REGISTERED_AS_TYPE, DECOR, DECOR_TYPE_INT))
                    Function.Call(Hash.DECOR_REGISTER, DECOR, DECOR_TYPE_INT);
                _decorReady = true;

                LoadFromDisk();
                Log?.Invoke($"[Stance] Initialized: {_records.Count} saved stance(s), nextId={_nextId}");
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] Initialize error: {ex.Message}"); }
        }

        // ====================================================================
        // Per-tick
        // ====================================================================
        public void Update()
        {
            try
            {
                Vehicle excluded = null;
                try { excluded = ExcludeVehicle?.Invoke(); } catch { }
                int excludedHandle = (excluded != null && excluded.Exists()) ? excluded.Handle : 0;

                // 1) Re-apply active stances every frame (track/camber reset each frame); detach the gone.
                if (_active.Count > 0)
                {
                    Vector3 ppos = Game.Player.Character.Position;
                    // Snapshot the keys into a REUSED list (no per-frame allocation) so Detach() can mutate _active
                    // while we iterate.
                    _activeScratch.Clear();
                    foreach (int k in _active.Keys) _activeScratch.Add(k);
                    foreach (int handle in _activeScratch)
                    {
                        var fit = _active[handle];
                        Vehicle v = fit?.Vehicle;
                        if (v == null || !v.Exists() || handle == excludedHandle ||
                            v.Position.DistanceTo(ppos) > DROP_RANGE)
                        {
                            Detach(handle);
                            continue;
                        }
                        fit.Update();
                    }
                }

                // 2) Throttled discovery of newly in-range stanced cars.
                if (Game.GameTime - _lastScan >= SCAN_INTERVAL)
                {
                    _lastScan = Game.GameTime;
                    Discover(excludedHandle);
                }
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] Update error: {ex.Message}"); }
        }

        private void Discover(int excludedHandle)
        {
            if (_records.Count == 0) return;
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists()) return;

            Vehicle[] nearby = World.GetNearbyVehicles(player.Position, RANGE);
            foreach (var v in nearby)
            {
                if (v == null || !v.Exists()) continue;
                int h = v.Handle;
                if (h == excludedHandle || _active.ContainsKey(h)) continue;

                int id = ResolveStanceId(v);
                if (id <= 0) continue;
                var rec = _records.FirstOrDefault(r => r.Id == id);
                if (rec == null || rec.IsStock) continue;
                Attach(v, rec);
            }
        }

        // ====================================================================
        // Identity resolution
        // ====================================================================
        private int ResolveStanceId(Vehicle v)
        {
            // Fast path: the car still carries our decorator tag.
            if (_decorReady && Function.Call<bool>(Hash.DECOR_EXIST_ON, v, DECOR))
            {
                int tag = Function.Call<int>(Hash.DECOR_GET_INT, v, DECOR);
                if (_records.Any(r => r.Id == tag)) return tag;
            }

            // Fallback: match by model + plate (re-tag if unambiguous).
            int model = v.Model.Hash;
            string plate = PlateOf(v);
            var matches = _records.Where(r => r.Model == model && PlateEq(r.Plate, plate)).ToList();
            if (matches.Count == 1)
            {
                int id = matches[0].Id;
                if (_decorReady) { try { Function.Call(Hash.DECOR_SET_INT, v, DECOR, id); } catch { } }
                return id;
            }
            // Count == 0 -> not one of ours. Count > 1 -> ambiguous (duplicate plate): skip per design,
            // until a plate changes and the match becomes unique again.
            if (matches.Count > 1)
                Log?.Invoke($"[Stance] Ambiguous plate '{plate}' (model {model}) matched {matches.Count} records — skipping.");
            return 0;
        }

        /// <summary>
        /// Apply this specific car's saved stance into an existing fitment instance (used when the player
        /// gets INTO their own stanced car — that car is excluded from the auto-manager). Returns false if
        /// the car has no saved stance.
        /// </summary>
        public bool LoadInto(Vehicle v, WheelFitment fit)
        {
            if (v == null || !v.Exists() || fit == null) return false;
            try
            {
                int id = ResolveStanceId(v);
                if (id <= 0) return false;
                var rec = _records.FirstOrDefault(r => r.Id == id);
                if (rec == null || rec.IsStock) return false;
                ApplyRecord(fit, rec);
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] LoadInto error: {ex.Message}"); return false; }
        }

        private void Attach(Vehicle v, StanceRecord rec)
        {
            try
            {
                var fit = new WheelFitment();
                WheelFitment.Log = Log;
                if (!fit.Initialize(v)) return;
                ApplyRecord(fit, rec);
                _active[v.Handle] = fit;
                _activeId[v.Handle] = rec.Id;
                Log?.Invoke($"[Stance] Applied stance #{rec.Id} to nearby {v.DisplayName} (plate '{rec.Plate}')");
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] Attach error: {ex.Message}"); }
        }

        private void Detach(int handle)
        {
            // Put the (shared, per-model) suspension raise back to stock when we stop managing the car.
            if (_active.TryGetValue(handle, out WheelFitment fit))
            {
                try { fit?.RestoreSuspension(); } catch { }
            }
            _active.Remove(handle);
            _activeId.Remove(handle);
        }

        private static void ApplyRecord(WheelFitment fit, StanceRecord rec)
        {
            fit.FrontCamber = rec.FrontCamber;
            fit.RearCamber = rec.RearCamber;
            fit.FrontTrackWidth = rec.FrontTrackWidth;
            fit.RearTrackWidth = rec.RearTrackWidth;
            fit.FrontHeight = rec.FrontHeight;
            fit.RearHeight = rec.RearHeight;
            fit.Rake = rec.Rake;
            fit.StockFake = rec.StockFake;   // restore the clean ride-height baseline (anti-drift, no re-stamp)
            if (fit.HasVisualWheels)
            {
                fit.VisualSize = rec.VisualSize;
                fit.VisualWidth = rec.VisualWidth;
            }
        }

        // ====================================================================
        // Recording (called on Save / menu exit)
        // ====================================================================
        /// <summary>
        /// Record (or update / remove) the stance for a specific car. If the car is back to stock the
        /// record + decorator are removed so it stops auto-applying. Enforces unique plates: a different
        /// car that already owns this model+plate blocks the save (returns false) — the user is told.
        /// </summary>
        public bool RecordCar(Vehicle v, WheelFitment fit, out string message)
        {
            message = "";
            if (v == null || !v.Exists() || fit == null) { message = "no vehicle"; return false; }
            try
            {
                int model = v.Model.Hash;
                string plate = PlateOf(v);
                int existingTag = (_decorReady && Function.Call<bool>(Hash.DECOR_EXIST_ON, v, DECOR))
                    ? Function.Call<int>(Hash.DECOR_GET_INT, v, DECOR) : 0;

                // Find this car's existing record (by decorator first, else model+plate).
                StanceRecord rec = (existingTag > 0) ? _records.FirstOrDefault(r => r.Id == existingTag) : null;
                if (rec == null) rec = _records.FirstOrDefault(r => r.Model == model && PlateEq(r.Plate, plate));

                bool stock = IsStock(fit);

                if (stock)
                {
                    if (rec != null) { _records.Remove(rec); SaveToDisk(); }
                    if (_decorReady && Function.Call<bool>(Hash.DECOR_EXIST_ON, v, DECOR))
                        try { Function.Call(Hash.DECOR_REMOVE, v, DECOR); } catch { }
                    Detach(v.Handle);
                    message = "stock — stance cleared";
                    return true;
                }

                // Plate-uniqueness guard: another DIFFERENT car already claims this model+plate.
                var conflict = _records.FirstOrDefault(r =>
                    r.Model == model && PlateEq(r.Plate, plate) && (rec == null || r.Id != rec.Id));
                if (conflict != null)
                {
                    message = $"plate '{plate}' already used by another saved car — change the plate first";
                    return false;
                }

                if (rec == null)
                {
                    rec = new StanceRecord { Id = _nextId++ };
                    _records.Add(rec);
                }
                rec.Model = model;
                rec.Plate = plate;
                rec.Fingerprint = Fingerprint(v);
                rec.FrontCamber = fit.FrontCamber;
                rec.RearCamber = fit.RearCamber;
                rec.FrontTrackWidth = fit.FrontTrackWidth;
                rec.RearTrackWidth = fit.RearTrackWidth;
                rec.FrontHeight = fit.FrontHeight;
                rec.RearHeight = fit.RearHeight;
                rec.Rake = fit.Rake;
                rec.StockFake = fit.StockFake;
                rec.VisualSize = fit.VisualSize;
                rec.VisualWidth = fit.VisualWidth;

                if (_decorReady) try { Function.Call(Hash.DECOR_SET_INT, v, DECOR, rec.Id); } catch { }
                SaveToDisk();
                message = $"stance #{rec.Id} saved to this car (plate '{plate}')";
                return true;
            }
            catch (Exception ex) { message = ex.Message; Log?.Invoke($"[Stance] RecordCar error: {ex.Message}"); return false; }
        }

        private static bool IsStock(WheelFitment f) =>
            Math.Abs(f.FrontCamber) < 0.001f && Math.Abs(f.RearCamber) < 0.001f &&
            Math.Abs(f.FrontTrackWidth) < 0.001f && Math.Abs(f.RearTrackWidth) < 0.001f &&
            Math.Abs(f.FrontHeight) < 0.001f && Math.Abs(f.RearHeight) < 0.001f &&
            Math.Abs(f.Rake) < 0.001f &&
            Math.Abs(f.VisualSize - 1f) < 0.001f && Math.Abs(f.VisualWidth - 1f) < 0.001f;

        // ====================================================================
        // Fingerprint + plate helpers
        // ====================================================================
        private static string PlateOf(Vehicle v)
        {
            try { return (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); }
            catch { return ""; }
        }
        private static bool PlateEq(string a, string b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Composite visual signature used to disambiguate / re-identify after a respawn.</summary>
        private static int Fingerprint(Vehicle v)
        {
            unchecked
            {
                int h = 17;
                void Mix(int x) { h = h * 31 + x; }
                try { Mix(Function.Call<int>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v)); } catch { }
                try { Mix((int)v.Mods.PrimaryColor); } catch { }
                try { Mix((int)v.Mods.SecondaryColor); } catch { }
                try { Mix((int)v.Mods.PearlescentColor); } catch { }
                try { Mix((int)v.Mods.RimColor); } catch { }
                try { Mix((int)v.Mods.WindowTint); } catch { }
                try { Mix((int)v.Mods.WheelType); } catch { }
                try { Mix(v.Mods.Livery); } catch { }
                return h;
            }
        }

        // ====================================================================
        // Persistence
        // ====================================================================
        private void LoadFromDisk()
        {
            _records.Clear();
            if (!File.Exists(_path)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(_path));
                if (data?.Records != null) _records.AddRange(data.Records);
                _nextId = Math.Max(data?.NextId ?? 1, _records.Count == 0 ? 1 : _records.Max(r => r.Id) + 1);
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] Load error: {ex.Message}"); }
        }

        private void SaveToDisk()
        {
            try
            {
                var data = new StoreFile { NextId = _nextId, Records = _records };
                File.WriteAllText(_path, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception ex) { Log?.Invoke($"[Stance] Save error: {ex.Message}"); }
        }

        private class StoreFile
        {
            public int NextId = 1;
            public List<StanceRecord> Records = new List<StanceRecord>();
        }
    }
}
