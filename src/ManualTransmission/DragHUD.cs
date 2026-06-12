using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using GTA;
using GTA.Native;
using GTA.UI;

namespace ExtendedLSC.ManualTransmission
{
    /// <summary>
    /// NFS-Underground-style drag gauge HUD: curved rev tach + gear tab + NOS gauge, all on the RIGHT
    /// side so it never overlaps the minimap/phone. Built from tintable PNG layers (CustomSprite) so
    /// every component can be resprayed. Needle angle is mapped from RPM; calibration constants are
    /// exposed so the design can be tuned from white-screen preview screenshots.
    /// </summary>
    public class DragHUD
    {
        public static Action<string> Log { get; set; }

        // ---- Respray colors (set from the LSC HUD menu) ----
        public Color NeedleColor = Color.FromArgb(255, 245, 248, 255);
        public Color FaceTint    = Color.White;          // White = original art colors
        public Color NosNeedleColor = Color.FromArgb(255, 120, 235, 255);
        public Color NosTint     = Color.White;

        // ---- Layout (fractions of screen) — right side, clear of minimap (bottom-left) ----
        // Sized to match the NFSU2 reference: a large, prominent tach hugging the right edge.
        public float TachCX = 0.88f, TachCY = 0.46f;     // tach pivot center
        public float TachSize = 0.88f;                   // sprite size as fraction of screen HEIGHT
        public float NosCX = 0.95f, NosCY = 0.80f;
        public float NosSize = 0.40f;

        // ---- Needle calibration ----
        // Needle art points +X; arc value 0 at math 235deg, value 10 at math 125deg. Sprite rotation is
        // clockwise, so rotation = 360 - mathAngle  ->  rpm 0 = 125deg, rpm 1.0 = 235deg.
        public float NeedleRotMin = 125f;
        public float NeedleRotMax = 235f;
        public float NosRotMin = 125f, NosRotMax = 235f;

        // ---- Arc geometry (must match the art generator's R_in/R_out and angle range) ----
        private const float ARC_A_LO = 235f, ARC_A_HI = 125f;  // value 0 -> 235deg, value 10 -> 125deg
        private const float ARC_R_NOS = 0.475f;                // NOS-dot radius as fraction of the 512 canvas
        private const int NOS_DOTS = 56;

        private string _hudDir;
        private bool _ok = false;
        private string _face, _needle, _gearTab, _nosDot, _nosDotOff;

        public void Initialize()
        {
            _hudDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "hud");
            _face      = Path.Combine(_hudDir, "tach_face.png");
            _needle    = Path.Combine(_hudDir, "tach_needle.png");
            _gearTab   = Path.Combine(_hudDir, "gear_tab.png");
            _nosDot    = Path.Combine(_hudDir, "nos_dot.png");
            _nosDotOff = Path.Combine(_hudDir, "nos_dot_off.png");
            _ok = File.Exists(_face) && File.Exists(_needle);
            Log?.Invoke($"[DragHUD] assets {( _ok ? "ready" : "MISSING")} in {_hudDir}");
        }

        private static PointF Px(float fx, float fy) => new PointF(fx * Screen.Width, fy * Screen.Height);

        private void Sprite(string file, PointF center, float sizePx, Color tint, float rotation)
        {
            if (!File.Exists(file)) return;
            var spr = new CustomSprite(file, new SizeF(sizePx, sizePx), center, tint, rotation, true);
            spr.Draw();
        }

        // Native text at NORMALIZED (0..1) screen coords — same space as the sprite fractions, so text
        // and sprites line up exactly (TextElement uses a different 1280x720 space that fought us).
        private static void DrawText(string s, float nx, float ny, float scale, Color c, int font, bool center)
        {
            Function.Call(Hash.SET_TEXT_FONT, font);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, c.R, c.G, c.B, c.A);
            Function.Call(Hash.SET_TEXT_CENTRE, center);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, s);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, nx, ny, 0);
        }

        /// <summary>Draw the gauge. rpm/nos are 0..1; redline highlights; flashGreatShift pops a callout.</summary>
        public void Draw(float rpm, int gear, float speedMph, float nos, bool hasNos, bool redline)
        {
            if (!_ok) return;
            try
            {
                float tachPx = TachSize * Screen.Height;
                PointF tc = Px(TachCX, TachCY);

                Sprite(_face, tc, tachPx, FaceTint, 0f);
                float needRot = NeedleRotMin + (NeedleRotMax - NeedleRotMin) * Math.Max(0f, Math.Min(1f, rpm));
                Sprite(_needle, tc, tachPx, redline ? Color.FromArgb(255, 255, 70, 70) : NeedleColor, needRot);

                // NOS bar fused to the OUTER edge of the arc: a row of cyan dots that light up from
                // value 0 (bottom) toward 10 (top) as the bottle fills. Replaces the separate gauge.
                if (hasNos)
                {
                    float aspect = (float)Screen.Width / Screen.Height;
                    float rDot = ARC_R_NOS * tachPx;            // screen px from the tach center
                    float dotPx = tachPx * 0.030f;              // small + dense -> reads as a solid bar
                    int lit = (int)Math.Round(Math.Max(0f, Math.Min(1f, nos)) * NOS_DOTS);
                    for (int i = 0; i < NOS_DOTS; i++)
                    {
                        float vv = 10f * i / (NOS_DOTS - 1);
                        float a = (ARC_A_LO + (ARC_A_HI - ARC_A_LO) * (vv / 10f)) * (float)Math.PI / 180f;
                        // X uses /aspect because px size is a fraction of HEIGHT but X is fraction of WIDTH
                        var dp = new PointF(tc.X + rDot * (float)Math.Cos(a), tc.Y - rDot * (float)Math.Sin(a));
                        Sprite(i < lit ? _nosDot : _nosDotOff, dp, dotPx, i < lit ? NosNeedleColor : Color.White, 0f);
                    }
                }

                // Gear tab (caps the top of the arc, just above the "10") + gear number centered in it.
                // Native text renders the number; offset so it sits ON the dark plate (not above it).
                float tabFx = TachCX - 0.070f, tabFy = TachCY - 0.272f;
                float tabPx = tachPx * 0.22f;
                Sprite(_gearTab, Px(tabFx, tabFy), tabPx, FaceTint, 0f);
                // Gear number as a SPRITE (native text always renders UNDER sprites, so the plate hid it).
                string g = gear < 0 ? "R" : (gear == 0 ? "N" : gear.ToString());
                string digit = Path.Combine(_hudDir, "gd_" + g + ".png");
                Sprite(digit, Px(tabFx + 0.002f, tabFy - 0.004f), tabPx * 0.62f, FaceTint, 0f);

                // Speed readout under the tach
                DrawText($"{speedMph:0}", TachCX, TachCY + 0.07f, 0.85f, Color.FromArgb(235, 240, 240, 240), 7, true);
                DrawText("MPH", TachCX, TachCY + 0.155f, 0.34f, Color.FromArgb(190, 220, 220, 220), 4, true);
            }
            catch (Exception ex) { Log?.Invoke($"[DragHUD] Draw error: {ex.Message}"); }
        }

        // ================================================================
        // WHITE-SCREEN UI PREVIEW HARNESS (development tool)
        // While scripts/ExtendedLSC/ui_preview.txt exists, paint the whole screen white and draw the
        // HUD at posed values ("rpm;gear;mph;nos" in the file) so a screenshot isolates the UI for
        // design iteration. Delete the file to clear instantly.
        // ================================================================
        public bool DrawPreviewIfRequested()
        {
            try
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "ui_preview.txt");
                if (!File.Exists(f)) return false;

                float rpm = 0.8f; int gear = 5; float mph = 120f; float nos = 0.66f;
                try
                {
                    var p = File.ReadAllText(f).Trim().Split(';');
                    if (p.Length > 0 && p[0] != "") float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out rpm);
                    if (p.Length > 1) int.TryParse(p[1], out gear);
                    if (p.Length > 2) float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out mph);
                    if (p.Length > 3) float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out nos);
                }
                catch { }

                // Fullscreen white wash
                Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.0f, 1.0f, 255, 255, 255, 255);
                Draw(rpm, gear, mph, nos, true, rpm >= 0.93f);
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[DragHUD] preview error: {ex.Message}"); return false; }
        }
    }
}
