using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace ExtendedLSC.WindowTint
{
    /// <summary>
    /// Runtime custom window-glass color for ELSC, with EXPANDED slots so multiple cars can each have a
    /// distinct color at once (e.g. a parked collection).
    ///
    /// Requires the dlc_elsc glass texture (white RGB + alpha 0): glass is clear by default and only shows a
    /// color where a tint slot carries one. On session start we locate the global window-color array, inject a
    /// larger one (TARGET_SLOTS), and load every saved custom color into a slot. Each custom car uses a unique
    /// tint enum = its slot. Traffic runs tint -1 and never touches our slots -> zero bleed.
    ///
    /// Slot map: 0 = None (clear), 1-3 = vanilla smoke, POOL_START..TARGET_SLOTS-1 = custom pool.
    ///
    /// Robustness:
    ///  - Slot assignment is DETERMINISTIC (sorted order of saved color models) so it's stable across sessions.
    ///  - A throttled sweep re-applies colors to nearby owned cars (not just the driven one), so a parked
    ///    collection shows correctly even after a relaunch.
    ///  - A throttled integrity check re-injects the array if anything ever resets it.
    /// </summary>
    public class WindowTintManager
    {
        public const int TARGET_SLOTS = 32;
        public const int POOL_START = 4;                 // slots 0-3 reserved for None + smoke
        private const int PREVIEW_SLOT = TARGET_SLOTS - 1;  // 31: reserved EXCLUSIVELY for the live editor —
                                                            // never assigned to a saved car, so scrubbing the
                                                            // picker can never touch another car's color.
        private const int POOL_END = TARGET_SLOTS - 2;   // 30: top of the per-car pool (27 stable slots)

        private bool _disabled = false;           // self-disable on any error so we can never crash-loop
        private int _nextSweep = 0;
        private int _nextIntegrity = 0;
        private int _previewHandle = 0;           // the vehicle currently in the live editor (skip it in Tick)

        /// <summary>Optional player-facing message hook (wired to ELSC's ShowNotification).</summary>
        public Action<string> Notify;

        public bool Ready => !_disabled && WindowTintMemory.Located && WindowTintMemory.Expanded;

        // The array must actually be big enough for the reserved preview slot, or driving a tint enum onto it
        // would render the out-of-range milky/solid-white fallback. Gate the live editor on this.
        public bool CanPreview => Ready && WindowTintMemory.SlotCount > PREVIEW_SLOT;

        /// <summary>Call every tick. Locates + injects the array, then keeps the driven car and nearby owned
        /// cars asserted, and re-injects if the array is ever reset. Self-disables on any error.</summary>
        public void Tick(Vehicle vehicle)
        {
            if (_disabled) return;
            try
            {
                if (!WindowTintMemory.Located) { WindowTintMemory.ScanStep(); return; }
                if (!WindowTintMemory.Expanded)
                {
                    if (WindowTintMemory.ExpandStep(TARGET_SLOTS)) ApplyAllSlotColors();
                    return;
                }

                // Keep our injected array authoritative EVERY tick. The game re-streams the carcols WindowColors
                // atArray on certain events (vehicle spawn/stream, mod application), repointing the header back
                // at the original 5-entry table and/or resetting m_Count to 5. The driven car's tint enum is a
                // custom slot (>= 5), so the moment that happens the glass renders the hardcoded milky-white
                // fallback (0x96FFFFFF). Repointing to our SAME, already-color-filled array (no realloc) fixes it
                // within one frame. This is the cause of the "snaps back to milky white" symptom; the old 5s
                // check left it milky for up to 5 seconds each reset.
                WindowTintMemory.ReassertInjection();

                // Deep fallback (throttled): only if the header itself relocated so a simple repoint can't fix
                // it — rebuild the injection from scratch.
                if (Game.GameTime >= _nextIntegrity)
                {
                    _nextIntegrity = Game.GameTime + 5000;
                    if (!WindowTintMemory.InjectionIntact) { WindowTintMemory.ForceReexpand(); return; }
                }

                // Driven car: keep its color + enum asserted every tick (instant, no flicker).
                AssertCar(vehicle);

                // Nearby owned cars: re-apply on a throttle so a parked collection shows correctly.
                if (Game.GameTime >= _nextSweep)
                {
                    _nextSweep = Game.GameTime + 1500;
                    var ped = Game.Player.Character;
                    if (ped != null && ped.Exists())
                        foreach (var v in World.GetNearbyVehicles(ped.Position, 80f))
                        {
                            if (v == null || !v.Exists()) continue;
                            // Skip cars whose model+plate is shared by ANOTHER live car (ambiguous identity — e.g. two
                            // spawner-spawned cars sharing one placeholder plate). Applying a saved colour by plate would BLEED it onto
                            // the duplicate (a fresh stock spawn inheriting the modified car's tint). The driven car is
                            // always asserted above; per-car dedupe makes plates unique once a car is entered.
                            if (PlateSharedByAnotherCar(v, VehNameCache.Of(v), PlateText(v))) continue;
                            AssertCar(v);
                        }
                }
            }
            catch { _disabled = true; }
        }

        /// <summary>Per-INSTANCE identity for a car: model + license plate. The plate is the only per-car
        /// attribute the game persists across restarts (saved with owned vehicles) and that scripts can read, so
        /// two same-model cars with different plates get different colors that survive a relaunch. Cars without a
        /// distinct plate fall back to sharing by model (same as before).</summary>
        private static string KeyFor(Vehicle v)
        {
            string plate = "";
            try { plate = (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); } catch { }
            return VehNameCache.Of(v) + "|" + plate;
        }

        /// <summary>Ensure a vehicle that has a saved custom color is on its slot, with the slot holding it.</summary>
        private void AssertCar(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            if (_previewHandle != 0 && v.Handle == _previewHandle) return;   // car is in the live editor — leave it
            string key = KeyFor(v);
            int argb = VehicleSaveData.GetCustomWindowColor(key);
            if (argb == 0) return;
            int slot = SlotFor(key);
            WindowTintMemory.SetSlotColor(slot, unchecked((uint)argb));
            if ((int)v.Mods.WindowTint != slot)
                Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, slot);
        }

        /// <summary>Begin live editing: move THIS car onto the exclusive preview slot so scrubbing can never
        /// touch any saved car's slot, and Tick leaves this car alone. Call from the picker's Shown event.</summary>
        public void BeginPreview(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists() || !CanPreview) { _previewHandle = 0; return; }
            _previewHandle = vehicle.Handle;
            Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, vehicle, PREVIEW_SLOT);
        }

        /// <summary>End live editing — Tick resumes asserting the car's real (now-saved) slot. Call from Closed.</summary>
        public void EndPreview() => _previewHandle = 0;

        /// <summary>Live preview: write the color into the EXCLUSIVE preview slot only (no other car uses it).</summary>
        public void PreviewColor(Vehicle vehicle, int argb)
        {
            if (vehicle == null || !vehicle.Exists() || !CanPreview) return;
            WindowTintMemory.SetSlotColor(PREVIEW_SLOT, unchecked((uint)argb));
            if ((int)vehicle.Mods.WindowTint != PREVIEW_SLOT)
                Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, vehicle, PREVIEW_SLOT);
        }

        /// <summary>Apply a custom color (AARRGGBB) to this vehicle and persist it onto its OWN stable slot.</summary>
        public bool ApplyCustomColor(Vehicle vehicle, int argb)
        {
            if (vehicle == null || !vehicle.Exists() || !Ready) return false;
            EnsureUniqueIdentity(vehicle);                     // give it its own plate if it shares one with another car
            string key = KeyFor(vehicle);
            VehicleSaveData.SetCustomWindowColor(key, argb);   // save first so it's in the set
            int slot = SlotFor(key);                           // stable, assigned once, persisted
            VehicleSaveData.Save();
            WindowTintMemory.SetSlotColor(slot, unchecked((uint)argb));
            Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, vehicle, slot);
            return true;
        }

        /// <summary>If another loaded vehicle shares this car's model + plate, the plate can't tell them apart —
        /// so assign THIS car a fresh unique plate (decorators are locked in SP, the plate is the only
        /// restart-persistent per-car identity). No-op when the plate is already unique, so a player's chosen
        /// plate is only changed on a genuine duplicate.</summary>
        private void EnsureUniqueIdentity(Vehicle vehicle)
        {
            try
            {
                string model = VehNameCache.Of(vehicle);
                string plate = PlateText(vehicle);
                if (!PlateSharedByAnotherCar(vehicle, model, plate)) return;

                string fresh = GenerateUniquePlate();
                if (string.IsNullOrEmpty(fresh)) return;
                Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, vehicle, fresh);
                Notify?.Invoke($"~b~Assigned plate {fresh}~s~ so this car keeps its own window tint.");
            }
            catch { }
        }

        private static string PlateText(Vehicle v)
        {
            try { return (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>True if some OTHER currently-loaded vehicle has the same model + plate as this one.</summary>
        private bool PlateSharedByAnotherCar(Vehicle self, string model, string plate)
        {
            foreach (var v in World.GetAllVehicles())
            {
                if (v == null || !v.Exists() || v.Handle == self.Handle) continue;
                if (VehNameCache.Of(v) == model && string.Equals(PlateText(v), plate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>True if a DIFFERENT saved vehicle already uses this exact plate text (so the player can be
        /// warned before reusing it). The car's own current record is excluded.</summary>
        public bool PlateUsedBySavedVehicle(Vehicle self, string plateText)
        {
            string selfKey = self != null ? KeyFor(self) : "";
            string want = (plateText ?? "").Trim();
            foreach (var k in VehicleSaveData.GetAllCustomWindowColors().Keys)
            {
                if (string.Equals(k, selfKey, StringComparison.OrdinalIgnoreCase)) continue;
                int bar = k.LastIndexOf('|');
                string p = bar >= 0 ? k.Substring(bar + 1) : "";
                if (string.Equals(p.Trim(), want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Move this car's saved window color from its old plate-key to the new one, so the tint follows
        /// the car when the player renames its plate (instead of reverting). Clears the old key.</summary>
        public void MoveColorToPlate(string model, string oldPlate, string newPlate)
        {
            string oldKey = model + "|" + (oldPlate ?? "").Trim();
            string newKey = model + "|" + (newPlate ?? "").Trim();
            int c = VehicleSaveData.GetCustomWindowColor(oldKey);
            if (c == 0) return;
            VehicleSaveData.SetCustomWindowColor(newKey, c);
            VehicleSaveData.SetCustomWindowColor(oldKey, 0);
            VehicleSaveData.SetCustomWindowSlot(oldKey, -1);
            VehicleSaveData.Save();
        }

        /// <summary>A plate (max 8 chars) not used by any loaded vehicle nor any saved window-color key.</summary>
        public string GenerateUniquePlate()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in World.GetAllVehicles())
                if (v != null && v.Exists()) used.Add(PlateText(v));
            foreach (var k in VehicleSaveData.GetAllCustomWindowColors().Keys)
            {
                int bar = k.LastIndexOf('|');
                if (bar >= 0) used.Add(k.Substring(bar + 1).Trim());
            }
            for (int n = 1; n <= 9999; n++)
            {
                string p = "ELSC" + n.ToString("D4");   // 8 chars, valid plate text
                if (!used.Contains(p)) return p;
            }
            return null;
        }

        /// <summary>Clear the custom color on this vehicle (called when a normal preset is chosen). Releases its
        /// slot so it can be reused by another car.</summary>
        public void ClearCustomColor(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists()) return;
            string key = KeyFor(vehicle);
            VehicleSaveData.SetCustomWindowColor(key, 0);
            VehicleSaveData.SetCustomWindowSlot(key, -1);
            VehicleSaveData.Save();
        }

        public int CurrentColor(Vehicle vehicle)
            => vehicle == null ? 0 : VehicleSaveData.GetCustomWindowColor(KeyFor(vehicle));

        /// <summary>The last custom color chosen for this car (survives un-equip). Uses the SAME key as the
        /// active color so callers don't have to reconstruct KeyFor (DisplayName + plate).</summary>
        public int LastColor(Vehicle vehicle)
            => vehicle == null ? 0 : VehicleSaveData.GetLastCustomWindowColor(KeyFor(vehicle));

        // ---- persistent slot assignment: each car keeps ONE slot for life; adding/removing others never
        //      reshuffles it. Stored in save data so it's stable across sessions too. ----
        private int SlotFor(string carName)
        {
            string key = carName.ToLowerInvariant();
            int s = VehicleSaveData.GetCustomWindowSlot(key);
            if (s >= POOL_START && s <= POOL_END) return s;      // already assigned — keep it
            s = LowestFreeSlot(key);
            VehicleSaveData.SetCustomWindowSlot(key, s);
            VehicleSaveData.Save();
            return s;
        }

        /// <summary>Lowest pool slot not currently held by another colored car.</summary>
        private int LowestFreeSlot(string forKey)
        {
            var used = new HashSet<int>();
            foreach (var kvp in VehicleSaveData.GetAllCustomWindowSlots())
                if (!string.Equals(kvp.Key, forKey, StringComparison.OrdinalIgnoreCase)) used.Add(kvp.Value);
            for (int i = POOL_START; i <= POOL_END; i++) if (!used.Contains(i)) return i;
            return POOL_START;   // pool exhausted (>27 custom-color cars at once): reuse (extremely unlikely)
        }

        /// <summary>Write every saved color into its (stable) slot — so any car of that model shows it.</summary>
        private void ApplyAllSlotColors()
        {
            foreach (var kvp in VehicleSaveData.GetAllCustomWindowColors())
                WindowTintMemory.SetSlotColor(SlotFor(kvp.Key), unchecked((uint)kvp.Value));
        }

        // ---- ARGB helpers ----
        public static int PackArgb(int a, int r, int g, int b)
            => ((a & 0xFF) << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);

        public static void UnpackArgb(int argb, out int a, out int r, out int g, out int b)
        {
            a = (argb >> 24) & 0xFF; r = (argb >> 16) & 0xFF; g = (argb >> 8) & 0xFF; b = argb & 0xFF;
        }
    }
}
