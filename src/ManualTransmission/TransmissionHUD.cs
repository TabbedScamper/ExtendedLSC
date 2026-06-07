using System;
using System.Drawing;
using GTA;
using GTA.Native;
using GTA.UI;

namespace ExtendedLSC.ManualTransmission
{
    /// <summary>
    /// HUD display for ELSC manual transmission (arcade-style)
    /// </summary>
    public class TransmissionHUD
    {
        private ELSCTransmission _transmission;

        // Position settings (0.0-1.0 screen coordinates)
        public float GearX { get; set; } = 0.95f;
        public float GearY { get; set; } = 0.88f;
        public float GearScale { get; set; } = 0.8f;

        public float RPMBarX { get; set; } = 0.85f;
        public float RPMBarY { get; set; } = 0.92f;
        public float RPMBarWidth { get; set; } = 0.12f;
        public float RPMBarHeight { get; set; } = 0.012f;

        public bool ShowGearIndicator { get; set; } = true;
        public bool ShowRPMBar { get; set; } = true;
        public bool ShowShiftLight { get; set; } = true;

        // Colors
        public Color GearColor { get; set; } = Color.White;
        public Color GearNeutralColor { get; set; } = Color.Yellow;
        public Color GearOptimalColor { get; set; } = Color.FromArgb(255, 100, 255, 100); // Green - good shift zone
        public Color GearRedlineColor { get; set; } = Color.FromArgb(255, 255, 60, 60);   // Red - losing power!
        public Color RPMBackgroundColor { get; set; } = Color.FromArgb(150, 0, 0, 0);
        public Color RPMForegroundColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
        public Color RPMOptimalColor { get; set; } = Color.FromArgb(255, 100, 255, 100);  // Green
        public Color RPMRedlineColor { get; set; } = Color.FromArgb(255, 255, 60, 60);    // Red

        public TransmissionHUD(ELSCTransmission transmission)
        {
            _transmission = transmission;
        }

        /// <summary>
        /// Draw the HUD (call every frame)
        /// </summary>
        public void Draw(Vehicle vehicle)
        {
            if (_transmission == null || !_transmission.IsEnabled) return;
            if (vehicle == null || !vehicle.Exists()) return;

            if (ShowGearIndicator)
                DrawGearIndicator();

            if (ShowRPMBar)
                DrawRPMBar(vehicle);
        }

        private void DrawGearIndicator()
        {
            string gearText = _transmission.GetGearString();
            Color color = GearColor;

            if (_transmission.InNeutral)
            {
                color = GearNeutralColor;
            }
            else
            {
                int shiftIndicator = _transmission.GetShiftIndicator();

                if (shiftIndicator == -1) // Redline - losing power!
                {
                    // Fast flash red to indicate power loss
                    if ((Game.GameTime / 80) % 2 == 0)
                        color = GearRedlineColor;
                    else
                        color = Color.FromArgb(255, 180, 40, 40); // Darker red
                }
                else if (shiftIndicator == 1 && ShowShiftLight) // Optimal shift zone
                {
                    // Gentle pulse green to indicate good time to shift
                    if ((Game.GameTime / 150) % 2 == 0)
                        color = GearOptimalColor;
                }
            }

            DrawText(gearText, GearX, GearY, GearScale, color, true);

            // Draw "SHIFT!" text when in redline
            if (_transmission.IsInRedline() && !_transmission.InNeutral)
            {
                // Flash "SHIFT!" above gear indicator
                if ((Game.GameTime / 100) % 2 == 0)
                {
                    DrawText("SHIFT!", GearX, GearY - 0.05f, 0.4f, GearRedlineColor, true);
                }
            }
        }

        private void DrawRPMBar(Vehicle vehicle)
        {
            float rpm = _transmission.GetCurrentRPM();
            float optimalShift = _transmission.ShiftUpRPM;
            float redlineStart = _transmission.RedlineStart;

            // Background
            DrawRect(RPMBarX, RPMBarY, RPMBarWidth, RPMBarHeight, RPMBackgroundColor);

            // Calculate fill width
            float fillWidth = RPMBarWidth * rpm;
            float fillX = RPMBarX - (RPMBarWidth / 2) + (fillWidth / 2);

            // Determine fill color based on RPM zone
            Color fillColor;
            if (rpm >= redlineStart)
            {
                // Redline zone - flash red (losing power!)
                if ((Game.GameTime / 80) % 2 == 0)
                    fillColor = RPMRedlineColor;
                else
                    fillColor = Color.FromArgb(255, 200, 50, 50);
            }
            else if (rpm >= optimalShift)
            {
                // Optimal zone - green (good time to shift)
                fillColor = RPMOptimalColor;
            }
            else
            {
                // Normal zone - white
                fillColor = RPMForegroundColor;
            }

            // Draw filled portion
            DrawRect(fillX, RPMBarY, fillWidth, RPMBarHeight * 0.8f, fillColor);

            // Optimal shift marker (green line)
            float optimalX = RPMBarX - (RPMBarWidth / 2) + (RPMBarWidth * optimalShift);
            DrawRect(optimalX, RPMBarY, 0.002f, RPMBarHeight * 1.2f, RPMOptimalColor);

            // Redline marker (red line)
            float redlineX = RPMBarX - (RPMBarWidth / 2) + (RPMBarWidth * redlineStart);
            DrawRect(redlineX, RPMBarY, 0.002f, RPMBarHeight * 1.2f, RPMRedlineColor);
        }

        private void DrawText(string text, float x, float y, float scale, Color color, bool center = false)
        {
            Function.Call(Hash.SET_TEXT_FONT, 4); // Pricedown font
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, color.R, color.G, color.B, color.A);
            Function.Call(Hash.SET_TEXT_OUTLINE);

            if (center)
                Function.Call(Hash.SET_TEXT_CENTRE, true);

            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y);
        }

        private void DrawRect(float x, float y, float width, float height, Color color)
        {
            Function.Call(Hash.DRAW_RECT, x, y, width, height, color.R, color.G, color.B, color.A);
        }
    }
}
