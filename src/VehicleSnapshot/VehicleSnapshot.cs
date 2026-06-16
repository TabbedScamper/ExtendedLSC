using System;
using System.Collections.Generic;
using GTA;
using GTA.Native;

namespace ExtendedLSC.VehicleSnapshot
{
    /// <summary>
    /// A full snapshot of a vehicle's APPLIED customization (mods, paint, tint, wheels, neon, extras, …).
    /// Captured when the player customizes a car in ELSC and re-applied to the matching car (by identity) on
    /// game load, so the look survives independent of GTA's native persistence.
    ///
    /// Every native read/write is individually try/caught so a native missing on a given build can't break the
    /// whole capture/apply. Wheel visual size/width + stance are intentionally NOT here — the fitment system
    /// owns those (keyed by the same identity).
    /// </summary>
    public class VehicleSnapshot
    {
        // Toggle-mod slots (separate native API from regular mods).
        private static readonly int[] TOGGLE_MODS = { 17, 18, 19, 20, 21, 22 };

        public int WheelType { get; set; } = -1;
        public Dictionary<int, int> Mods { get; set; } = new Dictionary<int, int>();      // regular modType -> index
        public bool CustomTires { get; set; } = false;
        public Dictionary<int, bool> Toggles { get; set; } = new Dictionary<int, bool>(); // toggle modType -> on

        // Paint
        public int PrimaryColor { get; set; } = -1;
        public int SecondaryColor { get; set; } = -1;
        public int PearlColor { get; set; } = -1;
        public int WheelColor { get; set; } = -1;
        public bool PrimaryCustom { get; set; } = false;
        public int[] PrimaryRgb { get; set; }
        public bool SecondaryCustom { get; set; } = false;
        public int[] SecondaryRgb { get; set; }

        // Lights / extras
        public int WindowTint { get; set; } = -1;
        public int[] TyreSmoke { get; set; }                  // r,g,b
        public bool[] Neon { get; set; }                      // left,right,front,back
        public int[] NeonColor { get; set; }                  // r,g,b
        public int Livery { get; set; } = -1;
        public int PlateStyle { get; set; } = -1;
        public Dictionary<int, bool> Extras { get; set; } = new Dictionary<int, bool>();  // extra id -> on
        public bool BulletproofTires { get; set; } = false;

        // ---------------------------------------------------------------
        public static VehicleSnapshot Capture(Vehicle v)
        {
            var s = new VehicleSnapshot();
            if (v == null || !v.Exists()) return s;
            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch { }

            try { s.WheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, v); } catch { }
            try { s.CustomTires = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, v, 23); } catch { }

            for (int m = 0; m <= 48; m++)
            {
                if (Array.IndexOf(TOGGLE_MODS, m) >= 0)
                {
                    try { s.Toggles[m] = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, m); } catch { }
                }
                else
                {
                    try { int val = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, m); if (val >= 0) s.Mods[m] = val; } catch { }
                }
            }

            try
            {
                var p = new OutputArgument(); var sec = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_COLOURS, v, p, sec);
                s.PrimaryColor = p.GetResult<int>(); s.SecondaryColor = sec.GetResult<int>();
            }
            catch { }
            try
            {
                var pe = new OutputArgument(); var wh = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, v, pe, wh);
                s.PearlColor = pe.GetResult<int>(); s.WheelColor = wh.GetResult<int>();
            }
            catch { }
            try
            {
                s.PrimaryCustom = Function.Call<bool>(Hash.GET_IS_VEHICLE_PRIMARY_COLOUR_CUSTOM, v);
                if (s.PrimaryCustom)
                {
                    var r = new OutputArgument(); var g = new OutputArgument(); var b = new OutputArgument();
                    Function.Call(Hash.GET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, r, g, b);
                    s.PrimaryRgb = new[] { r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>() };
                }
            }
            catch { }
            try
            {
                s.SecondaryCustom = Function.Call<bool>(Hash.GET_IS_VEHICLE_SECONDARY_COLOUR_CUSTOM, v);
                if (s.SecondaryCustom)
                {
                    var r = new OutputArgument(); var g = new OutputArgument(); var b = new OutputArgument();
                    Function.Call(Hash.GET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, r, g, b);
                    s.SecondaryRgb = new[] { r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>() };
                }
            }
            catch { }

            try { s.WindowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, v); } catch { }
            try
            {
                var r = new OutputArgument(); var g = new OutputArgument(); var b = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, v, r, g, b);
                s.TyreSmoke = new[] { r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>() };
            }
            catch { }
            try
            {
                s.Neon = new bool[4];
                for (int i = 0; i < 4; i++) s.Neon[i] = Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, v, i);
                var r = new OutputArgument(); var g = new OutputArgument(); var b = new OutputArgument();
                Function.Call(Hash.GET_VEHICLE_NEON_COLOUR, v, r, g, b);
                s.NeonColor = new[] { r.GetResult<int>(), g.GetResult<int>(), b.GetResult<int>() };
            }
            catch { }

            try { s.Livery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, v); } catch { }
            try { s.PlateStyle = Function.Call<int>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v); } catch { }
            for (int e = 1; e <= 14; e++)
                try { if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v, e)) s.Extras[e] = Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, v, e); } catch { }
            try { s.BulletproofTires = !Function.Call<bool>(Hash.GET_VEHICLE_TYRES_CAN_BURST, v); } catch { }

            return s;
        }

        public void Apply(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch { }

            if (WheelType >= 0) try { Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, v, WheelType); } catch { }
            if (Mods != null)
                foreach (var kv in Mods)
                    try { Function.Call(Hash.SET_VEHICLE_MOD, v, kv.Key, kv.Value, CustomTires); } catch { }
            if (Toggles != null)
                foreach (var kv in Toggles)
                    try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, kv.Key, kv.Value); } catch { }

            if (PrimaryColor >= 0 && SecondaryColor >= 0)
                try { Function.Call(Hash.SET_VEHICLE_COLOURS, v, PrimaryColor, SecondaryColor); } catch { }
            if (PearlColor >= 0 && WheelColor >= 0)
                try { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v, PearlColor, WheelColor); } catch { }
            if (PrimaryCustom && PrimaryRgb != null && PrimaryRgb.Length == 3)
                try { Function.Call(Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, PrimaryRgb[0], PrimaryRgb[1], PrimaryRgb[2]); } catch { }
            if (SecondaryCustom && SecondaryRgb != null && SecondaryRgb.Length == 3)
                try { Function.Call(Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, SecondaryRgb[0], SecondaryRgb[1], SecondaryRgb[2]); } catch { }

            if (WindowTint >= 0) try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, WindowTint); } catch { }
            if (TyreSmoke != null && TyreSmoke.Length == 3)
                try { Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v, TyreSmoke[0], TyreSmoke[1], TyreSmoke[2]); } catch { }
            if (Neon != null && Neon.Length == 4)
                for (int i = 0; i < 4; i++) try { Function.Call(Hash.SET_VEHICLE_NEON_ENABLED, v, i, Neon[i]); } catch { }
            if (NeonColor != null && NeonColor.Length == 3)
                try { Function.Call(Hash.SET_VEHICLE_NEON_COLOUR, v, NeonColor[0], NeonColor[1], NeonColor[2]); } catch { }

            if (Livery >= 0) try { Function.Call(Hash.SET_VEHICLE_LIVERY, v, Livery); } catch { }
            if (PlateStyle >= 0) try { Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v, PlateStyle); } catch { }
            if (Extras != null)
                foreach (var kv in Extras)
                    try { Function.Call(Hash.SET_VEHICLE_EXTRA, v, kv.Key, !kv.Value); } catch { }   // native: 0=on,1=off
            try { Function.Call(Hash.SET_VEHICLE_TYRES_CAN_BURST, v, !BulletproofTires); } catch { }
        }
    }
}
