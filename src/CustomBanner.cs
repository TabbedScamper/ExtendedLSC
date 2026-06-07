using System;
using System.Drawing;
using System.IO;
using GTA;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Elements;

namespace ExtendedLSC
{
    /// <summary>
    /// Custom banner that loads from PNG file and inherits from LemonUI's BaseElement
    /// so it can be used as a NativeMenu banner with proper coordinate handling.
    /// </summary>
    public class CustomBanner : BaseElement
    {
        private CustomSprite sprite;
        private bool isLoaded = false;

        // Cached screen values for recalculation
        private float lastScreenWidth;
        private float lastScreenHeight;

        public CustomBanner(string filePath) : base(PointF.Empty, new SizeF(433, 108))
        {
            if (File.Exists(filePath))
            {
                // Create sprite with dummy values - we'll set real ones in Recalculate
                sprite = new CustomSprite(filePath, new SizeF(100, 100), new PointF(0, 0), Color.White, 0f, false);
                isLoaded = true;
            }
        }

        public bool IsLoaded => isLoaded;

        /// <summary>
        /// Recalculate sprite coordinates when resolution changes or properties update
        /// </summary>
        public override void Recalculate()
        {
            if (sprite == null) return;

            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;

            // Cache for change detection
            lastScreenWidth = screenW;
            lastScreenHeight = screenH;

            // LemonUI literalPosition uses 1080p base with aspect ratio for X
            float aspectRatio = screenW / screenH;
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            // Position: convert from LemonUI's coordinate system to screen pixels
            float screenPosX = literalPosition.X / lemonXBase * screenW;
            float screenPosY = literalPosition.Y / lemonYBase * screenH;

            // Size: We want same PIXEL size regardless of aspect ratio
            // literalSize is in 1080p-base, scale by height ratio
            float scale = screenH / 1080f;
            float desiredPixelW = literalSize.Width * scale;  // e.g., 433 pixels
            float desiredPixelH = literalSize.Height * scale; // e.g., 108 pixels

            // CustomSprite uses 1280x720 base - convert screen pixels to that base
            // This makes the banner stay same pixel size on any aspect ratio
            float spriteW = desiredPixelW * (1280f / screenW);
            float spriteH = desiredPixelH * (720f / screenH);

            // Position also needs to be in 1280x720 base
            float spritePosX = screenPosX * (1280f / screenW);
            float spritePosY = screenPosY * (720f / screenH);

            sprite.Position = new PointF(spritePosX, spritePosY);
            sprite.Size = new SizeF(spriteW, spriteH);
        }

        /// <summary>
        /// Draw the banner
        /// </summary>
        public override void Draw()
        {
            if (sprite == null || !isLoaded) return;

            // Check if resolution changed
            if (Math.Abs(lastScreenWidth - GTA.UI.Screen.Width) > 1 ||
                Math.Abs(lastScreenHeight - GTA.UI.Screen.Height) > 1)
            {
                Recalculate();
            }

            sprite.Draw();
        }
    }
}
