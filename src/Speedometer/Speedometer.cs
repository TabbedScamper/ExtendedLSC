using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI.Elements;

namespace ExtendedLSC
{
    public enum SpeedoStyle { Off, Simple, Nfsu }

    /// <summary>Telemetry for the built-in HUDs (from MT when active, else natives).</summary>
    public struct SpeedoFrame
    {
        public float SpeedMph;
        public float Rpm01;       // 0..1
        public string GearText;   // "R" / "N" / "1".."9"
        public bool Redline;
        public bool HasTurbo;     // turbo mod installed
        public float Turbo01;     // 0..1 boost (approx)
        public bool HasNos;       // NOS installed (manual transmission)
        public float Nos01;       // 0..1 NOS charge
    }

    /// <summary>
    /// ELSC speedometer selector. Three styles, all drawn by ELSC from its own telemetry:
    ///  - Off    : hidden.
    ///  - Simple : a clean speed/gear/RPM-bar HUD (free).
    ///  - NFSU2  : the Underground-2-style gauge using the owner's redrawn skin art (no external mod / no song).
    /// </summary>
    public static class Speedo
    {
        public static Action<string> Log;

        public static SpeedoStyle Active = SpeedoStyle.Simple;
        public static bool Mph = true;
        public static float Scale = 1.0f;
        public static float OffX = 0f, OffY = 0f;
        public static Color Accent = Color.FromArgb(255, 235, 60, 60);   // Simple HUD highlight

        public static readonly HashSet<SpeedoStyle> Owned = new HashSet<SpeedoStyle> { SpeedoStyle.Off, SpeedoStyle.Simple };
        public static bool IsFree(SpeedoStyle s) => s == SpeedoStyle.Off || s == SpeedoStyle.Simple;
        public static bool IsOwned(SpeedoStyle s) => IsFree(s) || Owned.Contains(s);

        public static SpeedoStyle? Preview = null;
        public static bool Previewing = false;

        private static string _saveFile, _nfsuDir;
        private static readonly Dictionary<string, SizeF> _dims = new Dictionary<string, SizeF>();

        public static void Initialize(string speedoRoot)
        {
            _saveFile = Path.Combine(speedoRoot, "speedo.json");
            _nfsuDir = Path.Combine(speedoRoot, "nfsu2");
            LoadConfig();
            Log?.Invoke($"[Speedo] active={Active} nfsu2={(Directory.Exists(_nfsuDir) ? "ok" : "MISSING")}");
        }

        public static string StyleName(SpeedoStyle s)
        {
            switch (s)
            {
                case SpeedoStyle.Off: return "Off";
                case SpeedoStyle.Simple: return "Simple";
                case SpeedoStyle.Nfsu: return "NFSU2";
                default: return s.ToString();
            }
        }

        /// <summary>Equip a style (set active + persist). Returns a player-facing note.</summary>
        public static string Equip(SpeedoStyle s)
        {
            Active = s;
            SaveConfig();
            return $"~g~Speedometer: {StyleName(s)}";
        }

        // ================================================================= persistence
        [Serializable]
        private class SaveBlob
        {
            public string Active = "Simple";
            public bool Mph = true;
            public float Scale = 1.0f;
            public float OffX = 0f, OffY = 0f;
            public int AccentArgb = unchecked((int)0xFFEB3C3C);
            public List<string> Owned = new List<string>();
        }

        public static void LoadConfig()
        {
            try
            {
                if (_saveFile == null || !File.Exists(_saveFile)) return;
                var b = Newtonsoft.Json.JsonConvert.DeserializeObject<SaveBlob>(File.ReadAllText(_saveFile));
                if (b == null) return;
                if (Enum.TryParse(b.Active, out SpeedoStyle a)) Active = a;
                Mph = b.Mph; Scale = b.Scale; OffX = b.OffX; OffY = b.OffY;
                Accent = Color.FromArgb(b.AccentArgb);
                foreach (var s in b.Owned) if (Enum.TryParse(s, out SpeedoStyle os)) Owned.Add(os);
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] load error: {ex.Message}"); }
        }

        public static void SaveConfig()
        {
            try
            {
                if (_saveFile == null) return;
                var b = new SaveBlob
                {
                    Active = Active.ToString(), Mph = Mph, Scale = Scale, OffX = OffX, OffY = OffY,
                    AccentArgb = Accent.ToArgb(), Owned = new List<string>()
                };
                foreach (var s in Owned) b.Owned.Add(s.ToString());
                File.WriteAllText(_saveFile, Newtonsoft.Json.JsonConvert.SerializeObject(b, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] save error: {ex.Message}"); }
        }

        // ================================================================= dispatch
        public static void Draw(SpeedoFrame f)
        {
            SpeedoStyle style = Preview ?? Active;
            try
            {
                if (style == SpeedoStyle.Simple) DrawSimple(f);
                else if (style == SpeedoStyle.Nfsu) DrawNfsu(f);
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] draw error ({style}): {ex.Message}"); }
        }

        public static bool DrawPreviewIfRequested()
        {
            try
            {
                if (_saveFile == null) return false;
                string pf = Path.Combine(Path.GetDirectoryName(_saveFile), "speedo_preview.txt");
                Previewing = File.Exists(pf);
                if (!Previewing) return false;

                SpeedoStyle style = SpeedoStyle.Nfsu;
                float speed = 120f, rpm = 0.7f; string gear = "4";
                var p = File.ReadAllText(pf).Trim().Split(';');
                if (p.Length > 0 && Enum.TryParse(p[0], true, out SpeedoStyle ps)) style = ps;
                if (p.Length > 1) float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                if (p.Length > 2) float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rpm);
                if (p.Length > 3 && p[3] != "") gear = p[3].Trim();

                Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
                Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.0f, 1.0f, 45, 48, 55, 255);
                var saved = Preview; Preview = style;
                Draw(new SpeedoFrame { SpeedMph = speed, Rpm01 = rpm, GearText = gear, Redline = rpm >= 0.93f,
                                       HasTurbo = true, Turbo01 = rpm, HasNos = true, Nos01 = 0.66f });
                Preview = saved;
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] preview error: {ex.Message}"); return false; }
        }

        private static float DispSpeed(float mph) => Mph ? mph : mph * 1.60934f;
        private static string Unit => Mph ? "MPH" : "KM/H";

        // ================================================================= SIMPLE
        private static void DrawSimple(SpeedoFrame f)
        {
            float ax = 0.86f + OffX, ay = 0.92f + OffY;
            float sc = Scale;
            float barW = 0.12f * sc, barH = 0.012f * sc;
            Rect(ax, ay, barW, barH, Color.FromArgb(150, 0, 0, 0));
            float fill = barW * Math.Max(0f, Math.Min(1f, f.Rpm01));
            Color barC = f.Redline ? Accent : (f.Rpm01 > 0.85f ? Color.FromArgb(255, 100, 255, 100) : Color.White);
            Rect(ax - barW / 2 + fill / 2, ay, fill, barH * 0.8f, barC);
            Text($"{DispSpeed(f.SpeedMph):0}", ax - 0.03f, ay - 0.085f * sc, 0.7f * sc, Color.White, 7, true);
            Text(Unit, ax - 0.03f, ay - 0.018f * sc, 0.30f * sc, Color.FromArgb(200, 220, 220, 220), 4, true);
            Color gc = f.GearText == "N" ? Color.Yellow : (f.Redline ? Accent : Color.White);
            Text(f.GearText, ax + 0.05f, ay - 0.075f * sc, 0.8f * sc, gc, 4, true);
        }

        // ================================================================= NFSU2 (owner's redrawn skin)
        // Replicates the Speedometer-v0.6.1 layout: every sprite drawn at native_size*scale, positioned at
        // anchor + INIoffset*scale; main needle rot = rpm*230deg. Fed by ELSC telemetry (no external mod/song).
        private const float NA_X = 971f, NA_Y = 460f;   // anchor (1280x720 base), owner-placed default — tune via Off X/Y
        private const float NA_S = 0.34f;               // base scale — tune via Size

        private static string[] _nosBars;
        private static string[] NosBars()
        {
            if (_nosBars != null) return _nosBars;
            try
            {
                string d = Path.Combine(_nfsuDir, "nos_alpha");
                _nosBars = Directory.Exists(d) ? Directory.GetFiles(d, "*.png") : new string[0];
                Array.Sort(_nosBars);
            }
            catch { _nosBars = new string[0]; }
            return _nosBars;
        }

        private static void DrawNfsu(SpeedoFrame f)
        {
            float rpm = Math.Max(0f, Math.Min(1f, f.Rpm01));
            float scale = NA_S * Scale;
            float ax = NA_X + OffX * 1280f;
            float ay = NA_Y + OffY * 720f;
            Color white = Color.White;
            Color red = Color.FromArgb(255, 255, 51, 0);
            Color decor = Color.FromArgb(185, 255, 255, 255);

            // NOS bar across the top (when installed) — bars light up with the charge.
            if (f.HasNos)
            {
                S("NOS_BACKING.png", 1024f, 512f, 215f, -90f, decor, 0f, ax, ay, scale);
                var bars = NosBars();
                int n = bars.Length;
                for (int i = 0; i < n; i++)
                {
                    int alpha = Math.Max(0, Math.Min(255, (int)(f.Nos01 * 255f * 3f) - i * n));
                    if (alpha <= 0 || !File.Exists(bars[i])) continue;
                    new CustomSprite(bars[i], new SizeF(34f * scale, 512f * scale),
                        new PointF(ax + (217f + 34f * i) * scale, ay + -98f * scale),
                        Color.FromArgb(alpha, 0, 51, 153), 0f, false).Draw();
                }
            }

            // Turbo gauge (when installed) — second dial lower-left of the main one.
            if (f.HasTurbo)
            {
                float tRot = Math.Max(0f, Math.Min(1f, f.Turbo01)) * 90f + 132f;
                S("turbo_bg_00.png", 512f, 512f, 80f, 354f, white, 0f, ax, ay, scale);
                S("turbomax.png", 128f, 128f, 221f, 343f, red, 0f, ax, ay, scale);
                S("turbomax.png", 128f, 128f, 106f, 530f, red, 173f, ax, ay, scale);
                S("turbo_00.png", 512f, 512f, 79f, 352f, white, 0f, ax, ay, scale);
                S("decorsmall.png", 512f, 256f, 142f, 439f, decor, 0f, ax, ay, scale);
                S("arrow_turbo_00.png", 64f, 256f, 185f, 374f, white, tRot, ax, ay, scale);
            }

            // Main dial layers + RPM needle.
            S("main_bg_00.png", 1024f, 1024f, 318f, 86f, white, 0f, ax, ay, scale);
            S("main_redline_9000.png", 512f, 128f, 280f, 100f, red, 0f, ax, ay, scale);
            S("main_00.png", 512f, 512f, 330f, 107f, white, 0f, ax, ay, scale);
            S("arrow_main_00.png", 64f, 512f, 560f, 114f, white, rpm * 230f, ax, ay, scale);

            // Gear (u2_d_{R/N/#}).
            string g = f.GearText == "R" ? "u2_d_R" : f.GearText == "N" ? "u2_d_N" : "u2_d_" + f.GearText;
            SizeF gd = Dims("digits/" + g + ".png");
            S("digits/" + g + ".png", gd.Width * 1.2f, 57.6f, 721f, 296f, Color.FromArgb(255, 51, 255, 51), 0f, ax, ay, scale);

            // Speed digits, right-aligned to END at SpeedInfo_x (790). Fixed glyph advance (52; 54 for '7').
            int spd = (int)Math.Floor(DispSpeed(f.SpeedMph));
            string txt = spd.ToString();
            float Adv(char c) => c == '7' ? 54f : 52f;
            float total = 0f; foreach (char c in txt) total += Adv(c);
            float x = 790f - total;
            foreach (char c in txt)
            {
                S("digits/speed/n" + c + ".png", Adv(c), 78f, x, 388f, white, 0f, ax, ay, scale);
                x += Adv(c);
            }

            // Unit (MPH / KM/H) at SpeedTitle (680,504).
            if (Mph) { S("digits/speed/M.png", 35f, 33.6f, 693.65f, 504f, white, 0f, ax, ay, scale, true);
                       S("digits/speed/P.png", 26.6f, 33.6f, 728.1f, 504f, white, 0f, ax, ay, scale, true);
                       S("digits/speed/H.png", 28f, 33.6f, 758f, 504f, white, 0f, ax, ay, scale, true); }
            else     { S("digits/speed/K.png", 29.4f, 33.6f, 680f, 504f, white, 0f, ax, ay, scale, true);
                       S("digits/speed/M.png", 35f, 33.6f, 709.9f, 504f, white, 0f, ax, ay, scale, true);
                       S("digits/speed/H.png", 28f, 33.6f, 758f, 504f, white, 0f, ax, ay, scale, true); }

            // Shift light at redline.
            if (f.Redline) S("SHIFTUP.png", 42f, 42f, 665f, 311f, white, 0f, ax, ay, scale);
        }

        private static void S(string file, float nw, float nh, float ix, float iy, Color tint, float rot,
                              float ax, float ay, float scale, bool centered = false)
        {
            string fp = Path.Combine(_nfsuDir, file);
            if (!File.Exists(fp)) return;
            new CustomSprite(fp, new SizeF(nw * scale, nh * scale),
                new PointF(ax + ix * scale, ay + iy * scale), tint, rot, centered).Draw();
        }

        /// <summary>Native pixel dimensions of a PNG (cached) via the IHDR header.</summary>
        private static SizeF Dims(string rel)
        {
            string fp = Path.Combine(_nfsuDir, rel);
            if (_dims.TryGetValue(fp, out var d)) return d;
            d = new SizeF(32f, 48f);
            try
            {
                using (var fs = File.OpenRead(fp))
                {
                    var b = new byte[24];
                    if (fs.Read(b, 0, 24) == 24)
                    {
                        int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                        int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                        if (w > 0 && h > 0) d = new SizeF(w, h);
                    }
                }
            }
            catch { }
            _dims[fp] = d;
            return d;
        }

        private static void Text(string s, float fx, float fy, float scale, Color c, int font, bool center)
        {
            Function.Call(Hash.SET_TEXT_FONT, font);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, c.R, c.G, c.B, c.A);
            Function.Call(Hash.SET_TEXT_CENTRE, center);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, s);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, fx, fy, 0);
        }

        private static void Rect(float fx, float fy, float w, float h, Color c)
            => Function.Call(Hash.DRAW_RECT, fx, fy, w, h, c.R, c.G, c.B, c.A);
    }
}
