using System;
using System.Collections.Generic;
using System.IO;
using GTA;
using GTA.Native;
using Newtonsoft.Json;

namespace ExtendedLSC.VehicleSnapshot
{
    /// <summary>
    /// Saves the full applied state of every car the player customizes in ELSC (keyed by model + plate), and
    /// re-applies it on game load by sweeping nearby vehicles for matching identities — so a customized car
    /// keeps its exact look across sessions even where GTA's native persistence wouldn't.
    ///
    /// Restore is once-per-instance: each live handle is applied at most once per session (first sighting), so
    /// the sweep never fights the player's live edits.
    /// </summary>
    public class VehicleSnapshotManager
    {
        public Action<string> Log;

        private Dictionary<string, VehicleSnapshot> _snaps = new Dictionary<string, VehicleSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _restored = new HashSet<int>();   // handles already restored this session
        private bool _loaded = false;
        private int _nextSweep = 0;
        private bool _disabled = false;

        private static string SavePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "vehicle_snapshots.json");

        /// <summary>Identity = model + plate (lower-cased). Matches the window-tint/fitment identity scheme.</summary>
        public static string Key(Vehicle v)
        {
            if (v == null || !v.Exists()) return null;
            string plate = "";
            try { plate = (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); } catch { }
            return (v.DisplayName + "|" + plate).ToLowerInvariant();
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(SavePath))
                    _snaps = JsonConvert.DeserializeObject<Dictionary<string, VehicleSnapshot>>(File.ReadAllText(SavePath))
                             ?? new Dictionary<string, VehicleSnapshot>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log?.Invoke($"[Snapshot] load failed: {ex.Message}"); }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SavePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SavePath, JsonConvert.SerializeObject(_snaps, Formatting.Indented));
            }
            catch (Exception ex) { Log?.Invoke($"[Snapshot] save failed: {ex.Message}"); }
        }

        /// <summary>Capture + persist this car's full applied state (call when the player finishes customizing).</summary>
        public void CaptureCurrent(Vehicle v)
        {
            if (_disabled || v == null || !v.Exists()) return;
            try
            {
                Load();
                string key = Key(v);
                if (key == null) return;
                _snaps[key] = VehicleSnapshot.Capture(v);
                _restored.Add(v.Handle);   // the live car already matches; don't re-apply over the player
                Save();
                Log?.Invoke($"[Snapshot] saved {key}");
            }
            catch (Exception ex) { Log?.Invoke($"[Snapshot] capture error: {ex.Message}"); _disabled = true; }
        }

        /// <summary>Throttled sweep: re-apply saved snapshots to matching nearby cars (game-load restore). Each
        /// car instance is restored at most once per session.</summary>
        public void Tick()
        {
            if (_disabled) return;
            try
            {
                Load();
                if (_snaps.Count == 0) return;
                if (Game.GameTime < _nextSweep) return;
                _nextSweep = Game.GameTime + 2500;

                var ped = Game.Player.Character;
                if (ped == null || !ped.Exists()) return;

                var nearby = World.GetNearbyVehicles(ped.Position, 80f);

                // Count how many live cars share each identity. We must NOT apply a saved snapshot to an AMBIGUOUS
                // duplicate (e.g. several Menyoo cars all plated "MENYOO") — that overwrites them all with one car's
                // look. Skipped duplicates are left un-restored so they apply correctly once the plate dedup makes
                // them unique.
                var keyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in nearby)
                {
                    if (v == null || !v.Exists()) continue;
                    string k = Key(v);
                    if (k == null) continue;
                    keyCount[k] = keyCount.TryGetValue(k, out int c) ? c + 1 : 1;
                }

                foreach (var v in nearby)
                {
                    if (v == null || !v.Exists() || _restored.Contains(v.Handle)) continue;
                    string key = Key(v);
                    if (key == null) continue;
                    if (keyCount.TryGetValue(key, out int cnt) && cnt > 1) continue;   // ambiguous duplicate -> skip

                    if (_snaps.TryGetValue(key, out var snap))
                    {
                        snap.Apply(v);
                        Log?.Invoke($"[Snapshot] restored {key}");
                    }
                    _restored.Add(v.Handle);   // mark seen either way (don't re-test traffic every sweep)
                }

                // Bound the seen-set so it can't grow forever over a long session.
                if (_restored.Count > 2000) _restored.Clear();
            }
            catch (Exception ex) { Log?.Invoke($"[Snapshot] tick error: {ex.Message}"); _disabled = true; }
        }
    }
}
