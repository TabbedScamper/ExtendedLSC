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
    public enum SpeedoStyle { Off, Simple, Arcade }

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
        public bool HasShiftPoints; // MT active -> mark the perfect-shift window on the RPM bar
        public float PerfectMin01;  // 0..1 start of the perfect-shift band
        public float PerfectMax01;  // 0..1 end of the perfect-shift band
    }

    /// <summary>
    /// ELSC speedometer selector. Three styles, all drawn by ELSC from its own telemetry:
    ///  - Off    : hidden.
    ///  - Simple : a clean speed/gear/RPM-bar HUD (free).
    ///  - FASTandSPEEDY : an arcade-racer street gauge using the owner's own redrawn skin art (original assets).
    /// </summary>
    public static class Speedo
    {
        public static Action<string> Log;

        public static SpeedoStyle Active = SpeedoStyle.Simple;
        public static bool Mph = true;
        public static float Scale = 1.0f;
        public static float OffX = 0f, OffY = 0f;
        // Per-STYLE color palette — Simple and FASTandSPEEDY each keep their own colors.
        public class Palette
        {
            public Color Accent = Color.FromArgb(255, 235, 60, 60);    // RPM bar / needle
            public Color Speed = Color.White;                          // speed number
            public Color Unit = Color.FromArgb(255, 220, 220, 220);    // MPH / KM·H label
            public Color Gear = Color.White;                           // gear number
            public Color Nos = Color.FromArgb(255, 40, 120, 255);      // NOS bar
            public Color Redline = Color.FromArgb(255, 255, 51, 0);    // redline warning
            public Color Circle1 = Color.FromArgb(255, 30, 34, 42);    // FASTandSPEEDY big dial fill
            public Color Circle2 = Color.FromArgb(255, 30, 34, 42);    // FASTandSPEEDY small dial fill
            public int Circle1Alpha = 205;                             // big dial colour brightness
            public int Circle2Alpha = 205;                             // small dial colour brightness
        }
        // Shipping default colours (owner-styled). A fresh install (no speedo.json) uses these.
        public static readonly Palette PalSimple = new Palette
        {
            Accent = Color.FromArgb(-1), Speed = Color.FromArgb(-1), Unit = Color.FromArgb(-1), Gear = Color.FromArgb(-1),
            Nos = Color.FromArgb(-14124801), Redline = Color.FromArgb(-1360836),
            Circle1 = Color.FromArgb(-15461356), Circle2 = Color.FromArgb(-14802390),
            Circle1Alpha = 205, Circle2Alpha = 205
        };
        public static readonly Palette PalArcade = new Palette
        {
            Accent = Color.FromArgb(-1), Speed = Color.FromArgb(-1), Unit = Color.FromArgb(-3618616), Gear = Color.FromArgb(-1),
            Nos = Color.FromArgb(-14124801), Redline = Color.FromArgb(-1360836),
            Circle1 = Color.FromArgb(-15461356), Circle2 = Color.FromArgb(-15461356),
            Circle1Alpha = 204, Circle2Alpha = 205
        };
        public static Palette ColorsFor(SpeedoStyle s) => s == SpeedoStyle.Arcade ? PalArcade : PalSimple;

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
            _nfsuDir = Path.Combine(speedoRoot, "FASTandSPEEDY");
            LoadConfig();
            Log?.Invoke($"[Speedo] active={Active} FASTandSPEEDY={(Directory.Exists(_nfsuDir) ? "ok" : "MISSING")}");
        }

        public static string StyleName(SpeedoStyle s)
        {
            switch (s)
            {
                case SpeedoStyle.Off: return "Off";
                case SpeedoStyle.Simple: return "Simple";
                case SpeedoStyle.Arcade: return "FASTandSPEEDY";
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
        private class PalBlob
        {
            public int Accent, Speed, Unit, Gear, Nos, Redline, Circle1, Circle2;
            public int Circle1Alpha = 205, Circle2Alpha = 205;
            public PalBlob() { }
            public PalBlob(Palette p)
            {
                Accent = p.Accent.ToArgb(); Speed = p.Speed.ToArgb(); Unit = p.Unit.ToArgb(); Gear = p.Gear.ToArgb();
                Nos = p.Nos.ToArgb(); Redline = p.Redline.ToArgb(); Circle1 = p.Circle1.ToArgb(); Circle2 = p.Circle2.ToArgb();
                Circle1Alpha = p.Circle1Alpha; Circle2Alpha = p.Circle2Alpha;
            }
            public void Into(Palette p)
            {
                p.Accent = Color.FromArgb(Accent); p.Speed = Color.FromArgb(Speed); p.Unit = Color.FromArgb(Unit);
                p.Gear = Color.FromArgb(Gear); p.Nos = Color.FromArgb(Nos); p.Redline = Color.FromArgb(Redline);
                p.Circle1 = Color.FromArgb(Circle1); p.Circle2 = Color.FromArgb(Circle2);
                p.Circle1Alpha = Circle1Alpha; p.Circle2Alpha = Circle2Alpha;
            }
        }

        [Serializable]
        private class SaveBlob
        {
            public string Active = "Simple";
            public bool Mph = true;
            public float Scale = 1.0f;
            public float OffX = 0f, OffY = 0f;
            public PalBlob Simple = null;   // null in old configs -> migrate from the legacy flat fields below
            public PalBlob Arcade = null;
            // Legacy alias: configs saved before the style was renamed stored the Arcade palette under "Nfsu".
            // Read it into Arcade on load; never written back (getter returns null, ignored on serialize).
            [Newtonsoft.Json.JsonProperty("Nfsu", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
            public PalBlob LegacyArcade { get { return null; } set { if (value != null) Arcade = value; } }
            // Legacy single-palette fields (read-only migration for configs saved before per-style colors).
            public int AccentArgb = 0, SpeedArgb = 0, UnitArgb = 0, GearArgb = 0, NosArgb = 0, RedlineArgb = 0,
                       Circle1Argb = 0, Circle2Argb = 0, Circle1Alpha = 205, Circle2Alpha = 205;
            public List<string> Owned = new List<string>();
        }

        public static void LoadConfig()
        {
            try
            {
                if (_saveFile == null || !File.Exists(_saveFile)) return;
                var b = Newtonsoft.Json.JsonConvert.DeserializeObject<SaveBlob>(File.ReadAllText(_saveFile));
                if (b == null) return;
                if (Enum.TryParse(NormStyle(b.Active), out SpeedoStyle a)) Active = a;
                Mph = b.Mph; Scale = b.Scale; OffX = b.OffX; OffY = b.OffY;
                if (b.Simple != null) b.Simple.Into(PalSimple);
                if (b.Arcade != null) b.Arcade.Into(PalArcade);
                // Migrate a pre-per-style config (no nested palettes): copy the one saved palette into BOTH.
                if (b.Simple == null && b.Arcade == null && b.AccentArgb != 0)
                {
                    foreach (var p in new[] { PalSimple, PalArcade })
                    {
                        p.Accent = Color.FromArgb(b.AccentArgb); p.Speed = Color.FromArgb(b.SpeedArgb);
                        p.Unit = Color.FromArgb(b.UnitArgb); p.Gear = Color.FromArgb(b.GearArgb);
                        p.Nos = Color.FromArgb(b.NosArgb); p.Redline = Color.FromArgb(b.RedlineArgb);
                        p.Circle1 = Color.FromArgb(b.Circle1Argb); p.Circle2 = Color.FromArgb(b.Circle2Argb);
                        p.Circle1Alpha = b.Circle1Alpha; p.Circle2Alpha = b.Circle2Alpha;
                    }
                }
                foreach (var s in b.Owned) if (Enum.TryParse(NormStyle(s), out SpeedoStyle os)) Owned.Add(os);
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] load error: {ex.Message}"); }
        }

        /// <summary>Map a style token from an older saved config onto the current enum name (the "Arcade" style was
        /// previously stored under a different name). Pass-through for everything else.</summary>
        private static string NormStyle(string s) =>
            string.Equals(s, "Nfsu", StringComparison.OrdinalIgnoreCase) ? "Arcade" : s;

        public static void SaveConfig()
        {
            try
            {
                if (_saveFile == null) return;
                var b = new SaveBlob
                {
                    Active = Active.ToString(), Mph = Mph, Scale = Scale, OffX = OffX, OffY = OffY,
                    Simple = new PalBlob(PalSimple), Arcade = new PalBlob(PalArcade),
                    Owned = new List<string>()
                };
                foreach (var s in Owned) b.Owned.Add(s.ToString());
                File.WriteAllText(_saveFile, Newtonsoft.Json.JsonConvert.SerializeObject(b, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] save error: {ex.Message}"); }
        }

        /// <summary>Serialize the current speedo config (active style, units, per-style colours, scale/offset) to a
        /// JSON string so a Vehicle Package can carry the speedometer look.</summary>
        public static string ExportConfig()
        {
            try
            {
                var b = new SaveBlob
                {
                    Active = Active.ToString(), Mph = Mph, Scale = Scale, OffX = OffX, OffY = OffY,
                    Simple = new PalBlob(PalSimple), Arcade = new PalBlob(PalArcade), Owned = new List<string>()
                };
                foreach (var s in Owned) b.Owned.Add(s.ToString());
                return Newtonsoft.Json.JsonConvert.SerializeObject(b);
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] export error: {ex.Message}"); return null; }
        }

        /// <summary>Apply a config exported by ExportConfig (from a package). Applies colours/units/scale always;
        /// the active style only if the player OWNS it (a package never unlocks a paid gauge for free). Persists.</summary>
        public static void ImportConfig(string json, bool persist = true)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var b = Newtonsoft.Json.JsonConvert.DeserializeObject<SaveBlob>(json);
                if (b == null) return;
                Mph = b.Mph; Scale = b.Scale; OffX = b.OffX; OffY = b.OffY;
                if (b.Simple != null) b.Simple.Into(PalSimple);
                if (b.Arcade != null) b.Arcade.Into(PalArcade);
                if (Enum.TryParse(NormStyle(b.Active), out SpeedoStyle a) && IsOwned(a)) Active = a;
                if (persist) SaveConfig();   // in-memory only (persist=false) for a live hover preview
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] import error: {ex.Message}"); }
        }

        /// <summary>The active style parsed from an exported config (for setting Speedo.Preview during a package
        /// hover), or null if Off/unparseable.</summary>
        public static SpeedoStyle? ActiveStyleOf(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return null;
                var b = Newtonsoft.Json.JsonConvert.DeserializeObject<SaveBlob>(json);
                if (b != null && Enum.TryParse(NormStyle(b.Active), out SpeedoStyle a) && a != SpeedoStyle.Off) return a;
            }
            catch { }
            return null;
        }

        // ================================================================= dispatch
        public static void Draw(SpeedoFrame f)
        {
            SpeedoStyle style = Preview ?? Active;
            try
            {
                if (style == SpeedoStyle.Simple) DrawSimple(f);
                else if (style == SpeedoStyle.Arcade) DrawArcade(f);
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

                SpeedoStyle style = SpeedoStyle.Arcade;
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
                                       HasTurbo = true, Turbo01 = rpm, HasNos = true, Nos01 = 0.66f,
                                       HasShiftPoints = true, PerfectMin01 = 0.90f, PerfectMax01 = 0.98f });
                Preview = saved;
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[Speedo] preview error: {ex.Message}"); return false; }
        }

        private static float DispSpeed(float mph) => Mph ? mph : mph * 1.60934f;
        private static string Unit => Mph ? "MPH" : "KM/H";

        // ================================================================= SIMPLE
        /// <summary>Public entry so the MT system can force the Simple gauge on even when the
        /// speedometer style is Off (the gear/RPM bar should always show once MT is owned).</summary>
        public static void DrawSimpleForced(SpeedoFrame f)
        {
            try { DrawSimple(f); } catch (Exception ex) { Log?.Invoke($"[Speedo] simple(forced) error: {ex.Message}"); }
        }

        private static void DrawSimple(SpeedoFrame f)
        {
            var P = PalSimple;   // the Simple gauge keeps its own colors (separate from FASTandSPEEDY)
            float ax = 0.86f + OffX, ay = 0.92f + OffY;   // ay = RPM bar centre line (the anchor)
            float sc = Scale;
            float xc = XCorr();                            // ultrawide: compress X so the bar + readout aren't stretched
            float barW = 0.12f * sc * xc, barH = 0.012f * sc;

            // --- RPM bar (the white "gauge") on the anchor line ---
            Rect(ax, ay, barW, barH, Color.FromArgb(150, 0, 0, 0));
            // Redline zone: the top ~14% of the bar tinted with the redline colour (always visible, so it's
            // colorable and you can see where the redline is even below revs).
            float rlFrac = 0.86f, rlW = barW * (1f - rlFrac);
            Rect(ax - barW / 2 + barW * (rlFrac + (1f - rlFrac) / 2f), ay, rlW, barH,
                 Color.FromArgb(150, P.Redline.R, P.Redline.G, P.Redline.B));
            float fill = barW * Math.Max(0f, Math.Min(1f, f.Rpm01));
            Color barC = f.Redline ? P.Redline : P.Accent;
            Rect(ax - barW / 2 + fill / 2, ay, fill, barH * 0.8f, barC);

            // --- Perfect-shift markers: green lines bracketing the sweet-spot band on the bar (MT only) ---
            if (f.HasShiftPoints)
            {
                Color mark = Color.FromArgb(255, 120, 255, 120);
                float lineW = 0.0016f * sc * xc, lineH = barH * 1.6f;
                float lo = Math.Max(0f, Math.Min(1f, f.PerfectMin01));
                float hi = Math.Max(0f, Math.Min(1f, f.PerfectMax01));
                Rect(ax - barW / 2 + barW * lo, ay, lineW, lineH, mark);
                Rect(ax - barW / 2 + barW * hi, ay, lineW, lineH, mark);
            }

            // --- NOS gauge: a thin blue bar tucked directly under the RPM bar (only with NOS) ---
            if (f.HasNos)
            {
                float nosH = barH * 0.6f;
                float nosY = ay + barH / 2f + nosH / 2f + 0.002f * sc;   // stacked against the bottom of the RPM bar
                Rect(ax, nosY, barW, nosH, Color.FromArgb(150, 0, 0, 0));
                float nf = barW * Math.Max(0f, Math.Min(1f, f.Nos01));
                Rect(ax - barW / 2 + nf / 2, nosY, nf, nosH * 0.8f, P.Nos);
            }

            // --- Speed number + unit, stacked ABOVE the bar as a tight "120 / MPH" pair (bar sits under them) ---
            Text($"{DispSpeed(f.SpeedMph):0}", ax - 0.03f * xc, ay - 0.066f * sc, 0.7f * sc, P.Speed, 7, true);
            Text(Unit, ax - 0.03f * xc, ay - 0.028f * sc, 0.28f * sc, P.Unit, 4, true);

            // --- Gear, to the right (aligned with the speed number), with a "GEAR" label like MPH ---
            Color gc = f.GearText == "N" ? Color.Yellow : (f.Redline ? P.Redline : P.Gear);
            Text(f.GearText, ax + 0.05f * xc, ay - 0.070f * sc, 0.8f * sc, gc, 4, true);
            Text("GEAR", ax + 0.05f * xc, ay - 0.028f * sc, 0.28f * sc, P.Unit, 4, true);
        }

        // ================================================================= FASTandSPEEDY (owner's redrawn skin)
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

        private static void DrawArcade(SpeedoFrame f)
        {
            float rpm = Math.Max(0f, Math.Min(1f, f.Rpm01));
            float scale = NA_S * Scale;
            float ax = NA_X + OffX * 1280f;
            float ay = NA_Y + OffY * 720f;
            _xc = XCorr();               // horizontal compression so the dial isn't stretched on ultrawide
            _sprUsed.Clear();            // start fresh: hand out pooled sprites from index 0 again this frame
            var P = PalArcade;             // FASTandSPEEDY keeps its own colors (separate from Simple)
            Color white = Color.White;
            Color acc = P.Accent;        // needle
            Color red = P.Redline;       // redline arc
            Color decor = Color.FromArgb(185, 255, 255, 255);
            // The near-black dial art can't be tinted bright, so we overlay a white-fill mask of each circle in
            // the chosen colour. The slider = how brightly that colour shows (its alpha over the dark dial).
            // main_bg_fill has a transparent HOLE punched over the readout, so the numbers (which GTA renders
            // BELOW script sprites) show on the dark dial instead of being tinted by the glass — full brightness.
            Color c1 = Color.FromArgb(P.Circle1Alpha, P.Circle1.R, P.Circle1.G, P.Circle1.B);
            Color c2 = Color.FromArgb(P.Circle2Alpha, P.Circle2.R, P.Circle2.G, P.Circle2.B);

            // NOS bar across the top (when installed) — bars light up with the charge.
            if (f.HasNos)
            {
                S("NOS_BACKING.png", 1024f, 512f, 215f, -90f, decor, 0f, ax, ay, scale);
                var bars = NosBars();
                int n = bars.Length;
                for (int i = 0; i < n; i++)
                {
                    int alpha = Math.Max(0, Math.Min(255, (int)(f.Nos01 * 255f * 3f) - i * n));
                    if (alpha <= 0) continue;
                    // Route through the per-file sprite POOL (S) instead of `new CustomSprite` each frame — a
                    // file-based CustomSprite has no Dispose, so constructing one per bar per frame leaked a game
                    // texture every frame (bars[i] is an absolute path; S's Path.Combine keeps it as-is).
                    S(bars[i], 34f, 512f, 217f + 34f * i, -98f,
                      Color.FromArgb(alpha, P.Nos.R, P.Nos.G, P.Nos.B), 0f, ax, ay, scale);
                }
            }

            // Turbo gauge (when installed) — second dial lower-left of the main one.
            if (f.HasTurbo)
            {
                float tRot = Math.Max(0f, Math.Min(1f, f.Turbo01)) * 90f + 132f;
                // Turbo bg/turbo were cropped 512->272 (old content origin (8,8)/(22,22)->(0,0)/(14,14)); offsets
                // shifted by (old origin - new origin) so the dial lands at the exact same screen position.
                S("turbo_bg_00.png", 272f, 272f, 88f, 362f, white, 0f, ax, ay, scale);     // dark dial base
                S("turbo_bg_fill.png", 272f, 272f, 88f, 362f, c2, 0f, ax, ay, scale);      // bright colour overlay
                S("turbomax.png", 128f, 128f, 221f, 343f, red, 0f, ax, ay, scale);
                S("turbomax.png", 128f, 128f, 106f, 530f, red, 173f, ax, ay, scale);
                S("turbo_00.png", 272f, 272f, 87f, 360f, white, 0f, ax, ay, scale);
                S("decorsmall.png", 512f, 256f, 142f, 439f, decor, 0f, ax, ay, scale);
                S("arrow_turbo_00.png", 64f, 256f, 185f, 374f, acc, tRot, ax, ay, scale);
            }

            // Main dial layers + RPM needle. main_bg cropped 1024->544 (content origin (4,4)->(0,0)) -> offset +4.
            S("main_bg_00.png", 544f, 544f, 322f, 90f, white, 0f, ax, ay, scale);       // dark dial base
            S("main_bg_fill.png", 544f, 544f, 322f, 90f, c1, 0f, ax, ay, scale);        // bright colour overlay
            S("main_redline_9000.png", 512f, 128f, 280f, 100f, red, 0f, ax, ay, scale);
            S("main_00.png", 512f, 512f, 330f, 107f, white, 0f, ax, ay, scale);
            S("arrow_main_00.png", 64f, 512f, 560f, 114f, acc, rpm * 230f, ax, ay, scale);

            // Shift light at redline (before the readout).
            if (f.Redline) S("SHIFTUP.png", 42f, 42f, 665f, 311f, white, 0f, ax, ay, scale);

            // ---- READOUT: NATIVE TEXT (image digits ghost; text never does). The glass fill has a crisp hole
            // punched over this window so the text shows on the dark dial untinted. Colorable: Speed/Gear/Units.
            int spd = (int)Math.Floor(DispSpeed(f.SpeedMph));
            ArcadeTextRight(spd.ToString(), (ax + 790f * scale * _xc) / 1280f, (ay + 372f * scale) / 720f, 0.92f * Scale, P.Speed);
            ArcadeTextCentre(f.GearText,     (ax + 749f * scale * _xc) / 1280f, (ay + 274f * scale) / 720f, 0.78f * Scale, P.Gear);
            ArcadeTextCentre(Unit,           (ax + 726f * scale * _xc) / 1280f, (ay + 500f * scale) / 720f, 0.42f * Scale, P.Unit);
        }

        private static void ArcadeTextCentre(string s, float cx, float cy, float scl, Color c)
        {
            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, scl, scl);
            Function.Call(Hash.SET_TEXT_COLOUR, c.R, c.G, c.B, c.A);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, s);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, cx, cy, 0);
        }
        private static void ArcadeTextRight(string s, float rx, float ty, float scl, Color c)
        {
            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, scl, scl);
            Function.Call(Hash.SET_TEXT_COLOUR, c.R, c.G, c.B, c.A);
            Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
            Function.Call(Hash.SET_TEXT_WRAP, 0f, rx);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, s);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0f, ty, 0);
        }

        // CustomSprite has no Dispose, so a fresh one each frame leaks a game runtime texture (pool fills ->
        // stale-texture ghost). But ONE shared instance per file collapses repeated digits ("88" draws the same
        // n8 twice — if Draw() is deferred both land at the last position). Solution: a POOL per file. Each call
        // takes the next free instance for that file (creating it once), so same-file-multiple-draws each get
        // their own instance AND every texture is created exactly once. _sprUsed is reset each DrawArcade frame.
        private static readonly Dictionary<string, List<CustomSprite>> _sprPool = new Dictionary<string, List<CustomSprite>>();
        private static readonly Dictionary<string, int> _sprUsed = new Dictionary<string, int>();

        // ---- Ultrawide un-stretch -------------------------------------------------------------------------------
        // GTA.UI.Screen reports a fixed 1280x720, so it can't give the real aspect; GET_SCREEN_ACTIVE_RESOLUTION can.
        // The gauges are drawn in a 16:9 (1280x720) layout, and DRAW_SPRITE/RECT stretch X on wider screens, so we
        // compress every X size + X-offset (about the gauge anchor) by baseAspect/realAspect — 1.0 at 16:9, smaller
        // on ultrawide — which keeps the round dial round and the layout tight. (Cached; refreshed ~twice a second.)
        private static float _xc = 1f;                 // current X-compression factor, set at the top of each Draw
        private static float _xcCache = 1f; private static int _xcFrame = -9999;
        private static float XCorr()
        {
            int fc = Game.FrameCount;
            if (fc - _xcFrame < 120) return _xcCache;
            _xcFrame = fc;
            try
            {
                var ow = new OutputArgument(); var oh = new OutputArgument();
                Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, ow, oh);
                int w = ow.GetResult<int>(), h = oh.GetResult<int>();
                if (w > 0 && h > 0) _xcCache = (1280f / 720f) / ((float)w / h);
            }
            catch { }
            return _xcCache;
        }

        private static void S(string file, float nw, float nh, float ix, float iy, Color tint, float rot,
                              float ax, float ay, float scale, bool centered = false)
        {
            string fp = Path.Combine(_nfsuDir, file);
            if (!File.Exists(fp)) return;
            // Compress width + horizontal offset about the anchor so the gauge keeps its aspect on ultrawide.
            var size = new SizeF(nw * scale * _xc, nh * scale);
            var pos = new PointF(ax + ix * scale * _xc, ay + iy * scale);
            if (!_sprPool.TryGetValue(fp, out var list)) { list = new List<CustomSprite>(); _sprPool[fp] = list; }
            int used = _sprUsed.TryGetValue(fp, out var u) ? u : 0;
            CustomSprite spr;
            if (used < list.Count)
            {
                spr = list[used];
                spr.Size = size; spr.Position = pos; spr.Color = tint; spr.Rotation = rot;
            }
            else
            {
                spr = new CustomSprite(fp, size, pos, tint, rot, centered);
                list.Add(spr);
            }
            _sprUsed[fp] = used + 1;
            spr.Draw();
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
