using System.Collections.Generic;

namespace ExtendedLSC
{
    /// <summary>
    /// Complete GTA V vehicle color definitions with proper pearlescent spec values.
    /// Color indices are from GTA V's carcols.meta game data.
    /// </summary>
    public static class VehicleColors
    {
        public struct ColorInfo
        {
            public int ColorIndex;
            public int PearlescentSpec;  // For metallic shine effect
            public string DisplayName;

            public ColorInfo(string name, int color, int spec = 0)
            {
                DisplayName = name;
                ColorIndex = color;
                PearlescentSpec = spec;
            }
        }

        #region Classic/Standard Colors
        public static readonly ColorInfo[] ClassicColors = new ColorInfo[]
        {
            // Blacks & Silvers
            new ColorInfo("Black", 0),
            new ColorInfo("Graphite", 1),
            new ColorInfo("Black Steel", 2),
            new ColorInfo("Dark Silver", 3),
            new ColorInfo("Silver", 4),
            new ColorInfo("Blue Silver", 5),
            new ColorInfo("Rolled Steel", 6),
            new ColorInfo("Shadow Silver", 7),
            new ColorInfo("Stone Silver", 8),
            new ColorInfo("Midnight Silver", 9),
            new ColorInfo("Cast Iron Silver", 10),
            new ColorInfo("Anthracite Black", 11),

            // Reds
            new ColorInfo("Red", 27),
            new ColorInfo("Torino Red", 28),
            new ColorInfo("Formula Red", 29),
            new ColorInfo("Blaze Red", 30),
            new ColorInfo("Grace Red", 31),
            new ColorInfo("Garnet Red", 32),
            new ColorInfo("Sunset Red", 33),
            new ColorInfo("Cabernet Red", 34),
            new ColorInfo("Candy Red", 35),
            new ColorInfo("Wine Red", 143),
            new ColorInfo("Lava Red", 150),

            // Pinks
            new ColorInfo("Hot Pink", 135),
            new ColorInfo("Salmon Pink", 136),
            new ColorInfo("Pink", 137),

            // Oranges
            new ColorInfo("Sunrise Orange", 36),
            new ColorInfo("Orange", 38),
            new ColorInfo("Bright Orange", 138),

            // Yellows
            new ColorInfo("Yellow", 88),
            new ColorInfo("Race Yellow", 89),
            new ColorInfo("Bronze", 90),
            new ColorInfo("Fluorescent Yellow", 91),

            // Greens
            new ColorInfo("Dark Green", 49),
            new ColorInfo("Racing Green", 50),
            new ColorInfo("Sea Green", 51),
            new ColorInfo("Olive Green", 52),
            new ColorInfo("Bright Green", 53),
            new ColorInfo("Petrol Green", 54),
            new ColorInfo("Lime Green", 92),

            // Blues
            new ColorInfo("Galaxy Blue", 61),
            new ColorInfo("Dark Blue", 62),
            new ColorInfo("Saxon Blue", 63),
            new ColorInfo("Blue", 64),
            new ColorInfo("Mariner Blue", 65),
            new ColorInfo("Harbor Blue", 66),
            new ColorInfo("Diamond Blue", 67),
            new ColorInfo("Surf Blue", 68),
            new ColorInfo("Nautical Blue", 69),
            new ColorInfo("Ultra Blue", 70),
            new ColorInfo("Racing Blue", 73),
            new ColorInfo("Light Blue", 74),
            new ColorInfo("Midnight Blue", 141),

            // Purples
            new ColorInfo("Purple", 71),
            new ColorInfo("Spin Purple", 72),
            new ColorInfo("Might Purple", 142),
            new ColorInfo("Bright Purple", 145),

            // Browns
            new ColorInfo("Umber Brown", 94),
            new ColorInfo("Creek Brown", 95),
            new ColorInfo("Chocolate Brown", 96),
            new ColorInfo("Maple Brown", 97),
            new ColorInfo("Saddle Brown", 98),
            new ColorInfo("Straw Brown", 99),
            new ColorInfo("Moss Brown", 100),
            new ColorInfo("Bison Brown", 101),
            new ColorInfo("Woodbeech Brown", 102),
            new ColorInfo("Beechwood Brown", 103),
            new ColorInfo("Sienna Brown", 104),
            new ColorInfo("Sandy Brown", 105),
            new ColorInfo("Bleached Brown", 106),

            // Whites & Creams
            new ColorInfo("Cream", 107),
            new ColorInfo("White", 111),
            new ColorInfo("Frost White", 112),
        };
        #endregion

        #region Metallic Colors (with pearlescent spec for shine)
        public static readonly ColorInfo[] MetallicColors = new ColorInfo[]
        {
            // Blacks & Silvers
            new ColorInfo("Metallic Black", 0, 10),
            new ColorInfo("Metallic Graphite", 1, 5),
            new ColorInfo("Metallic Black Steel", 2, 5),
            new ColorInfo("Metallic Dark Silver", 3, 6),
            new ColorInfo("Metallic Silver", 4, 111),
            new ColorInfo("Metallic Blue Silver", 5, 111),
            new ColorInfo("Metallic Rolled Steel", 6, 4),
            new ColorInfo("Metallic Shadow Silver", 7, 5),
            new ColorInfo("Metallic Stone Silver", 8, 5),
            new ColorInfo("Metallic Midnight Silver", 9, 7),
            new ColorInfo("Metallic Cast Iron Silver", 10, 7),
            new ColorInfo("Metallic Anthracite", 11, 2),
            new ColorInfo("Metallic Black Graphite", 147, 4),

            // Reds
            new ColorInfo("Metallic Red", 27, 36),
            new ColorInfo("Metallic Torino Red", 28, 28),
            new ColorInfo("Metallic Formula Red", 29, 28),
            new ColorInfo("Metallic Blaze Red", 30, 36),
            new ColorInfo("Metallic Grace Red", 31, 27),
            new ColorInfo("Metallic Garnet Red", 32, 25),
            new ColorInfo("Metallic Sunset Red", 33, 47),
            new ColorInfo("Metallic Cabernet Red", 34, 47),
            new ColorInfo("Metallic Candy Red", 35, 25),
            new ColorInfo("Metallic Wine Red", 143, 31),
            new ColorInfo("Metallic Lava Red", 150, 42),

            // Pinks
            new ColorInfo("Metallic Hot Pink", 135, 135),
            new ColorInfo("Metallic Salmon Pink", 136, 5),
            new ColorInfo("Metallic Pink", 137, 3),

            // Oranges & Golds
            new ColorInfo("Metallic Sunrise Orange", 36, 26),
            new ColorInfo("Metallic Gold", 37, 106),
            new ColorInfo("Metallic Orange", 38, 37),
            new ColorInfo("Metallic Bright Orange", 138, 89),

            // Yellows
            new ColorInfo("Metallic Yellow", 88, 88),
            new ColorInfo("Metallic Race Yellow", 89, 88),
            new ColorInfo("Metallic Bronze", 90, 102),
            new ColorInfo("Metallic Fluorescent Yellow", 91, 91),

            // Greens
            new ColorInfo("Metallic Dark Green", 49, 52),
            new ColorInfo("Metallic Racing Green", 50, 53),
            new ColorInfo("Metallic Sea Green", 51, 66),
            new ColorInfo("Metallic Olive Green", 52, 59),
            new ColorInfo("Metallic Bright Green", 53, 59),
            new ColorInfo("Metallic Petrol Green", 54, 60),
            new ColorInfo("Metallic Lime Green", 92, 92),

            // Blues
            new ColorInfo("Metallic Galaxy Blue", 61, 63),
            new ColorInfo("Metallic Dark Blue", 62, 68),
            new ColorInfo("Metallic Saxon Blue", 63, 87),
            new ColorInfo("Metallic Blue", 64, 68),
            new ColorInfo("Metallic Mariner Blue", 65, 87),
            new ColorInfo("Metallic Harbor Blue", 66, 60),
            new ColorInfo("Metallic Diamond Blue", 67, 67),
            new ColorInfo("Metallic Surf Blue", 68, 68),
            new ColorInfo("Metallic Nautical Blue", 69, 74),
            new ColorInfo("Metallic Ultra Blue", 70, 70),
            new ColorInfo("Metallic Racing Blue", 73, 73),
            new ColorInfo("Metallic Light Blue", 74, 74),
            new ColorInfo("Metallic Midnight Blue", 141, 73),

            // Purples
            new ColorInfo("Metallic Purple", 71, 145),
            new ColorInfo("Metallic Spin Purple", 72, 64),
            new ColorInfo("Metallic Might Purple", 146, 145),
            new ColorInfo("Metallic Bright Purple", 145, 74),

            // Browns
            new ColorInfo("Metallic Umber Brown", 94, 104),
            new ColorInfo("Metallic Creek Brown", 95, 97),
            new ColorInfo("Metallic Chocolate Brown", 96, 95),
            new ColorInfo("Metallic Maple Brown", 97, 98),
            new ColorInfo("Metallic Saddle Brown", 98, 95),
            new ColorInfo("Metallic Straw Brown", 99, 106),
            new ColorInfo("Metallic Moss Brown", 100, 100),
            new ColorInfo("Metallic Bison Brown", 101, 95),
            new ColorInfo("Metallic Woodbeech Brown", 102, 105),
            new ColorInfo("Metallic Beechwood Brown", 103, 104),
            new ColorInfo("Metallic Sienna Brown", 104, 104),
            new ColorInfo("Metallic Sandy Brown", 105, 105),
            new ColorInfo("Metallic Bleached Brown", 106, 106),

            // Whites
            new ColorInfo("Metallic Cream", 107, 107),
            new ColorInfo("Metallic White", 111, 0),
            new ColorInfo("Metallic Frost White", 112, 0),
        };
        #endregion

        #region Matte Colors
        public static readonly ColorInfo[] MatteColors = new ColorInfo[]
        {
            new ColorInfo("Matte Black", 12),
            new ColorInfo("Matte Gray", 13),
            new ColorInfo("Matte Light Gray", 14),
            new ColorInfo("Matte Red", 39),
            new ColorInfo("Matte Dark Red", 40),
            new ColorInfo("Matte Orange", 41),
            new ColorInfo("Matte Yellow", 42),
            new ColorInfo("Matte Lime Green", 55),
            new ColorInfo("Matte Dark Blue", 82),
            new ColorInfo("Matte Blue", 83),
            new ColorInfo("Matte Midnight Blue", 84),
            new ColorInfo("Matte Green", 128),
            new ColorInfo("Matte Brown", 129),
            new ColorInfo("Matte White", 131),
            new ColorInfo("Matte Purple", 148),
            new ColorInfo("Matte Dark Purple", 149),
            new ColorInfo("Matte Forest Green", 151),
            new ColorInfo("Matte Olive Drab", 152),
            new ColorInfo("Matte Desert Brown", 153),
            new ColorInfo("Matte Desert Tan", 154),
            new ColorInfo("Matte Foliage Green", 155),
        };
        #endregion

        #region Metal Finishes
        public static readonly ColorInfo[] MetalFinishes = new ColorInfo[]
        {
            new ColorInfo("Brushed Steel", 117, 18),
            new ColorInfo("Brushed Black Steel", 118, 3),
            new ColorInfo("Brushed Aluminum", 119, 5),
            new ColorInfo("Pure Gold", 158, 160),
            new ColorInfo("Brushed Gold", 159, 160),
        };
        #endregion

        #region Chrome
        public static readonly ColorInfo[] ChromeColors = new ColorInfo[]
        {
            new ColorInfo("Chrome", 120),
        };
        #endregion

        #region Pearlescent Colors (applied over base)
        public static readonly ColorInfo[] PearlescentColors = new ColorInfo[]
        {
            new ColorInfo("Black Pearl", 0),
            new ColorInfo("Graphite Pearl", 1),
            new ColorInfo("Black Steel Pearl", 2),
            new ColorInfo("Dark Silver Pearl", 3),
            new ColorInfo("Silver Pearl", 4),
            new ColorInfo("Blue Silver Pearl", 5),
            new ColorInfo("Rolled Steel Pearl", 6),
            new ColorInfo("Shadow Silver Pearl", 7),
            new ColorInfo("Stone Silver Pearl", 8),
            new ColorInfo("Midnight Silver Pearl", 9),
            new ColorInfo("Cast Iron Pearl", 10),
            new ColorInfo("Anthracite Pearl", 11),
            new ColorInfo("Red Pearl", 27),
            new ColorInfo("Torino Red Pearl", 28),
            new ColorInfo("Formula Red Pearl", 29),
            new ColorInfo("Lava Red Pearl", 30),
            new ColorInfo("Grace Red Pearl", 31),
            new ColorInfo("Garnet Red Pearl", 32),
            new ColorInfo("Sunset Red Pearl", 33),
            new ColorInfo("Cabernet Red Pearl", 34),
            new ColorInfo("Candy Red Pearl", 35),
            new ColorInfo("Sunrise Orange Pearl", 36),
            new ColorInfo("Gold Pearl", 37),
            new ColorInfo("Orange Pearl", 38),
            new ColorInfo("Dark Green Pearl", 49),
            new ColorInfo("Racing Green Pearl", 50),
            new ColorInfo("Sea Green Pearl", 51),
            new ColorInfo("Olive Green Pearl", 52),
            new ColorInfo("Bright Green Pearl", 53),
            new ColorInfo("Petrol Green Pearl", 54),
            new ColorInfo("Galaxy Blue Pearl", 61),
            new ColorInfo("Dark Blue Pearl", 62),
            new ColorInfo("Saxon Blue Pearl", 63),
            new ColorInfo("Blue Pearl", 64),
            new ColorInfo("Mariner Blue Pearl", 65),
            new ColorInfo("Harbor Blue Pearl", 66),
            new ColorInfo("Diamond Blue Pearl", 67),
            new ColorInfo("Surf Blue Pearl", 68),
            new ColorInfo("Nautical Blue Pearl", 69),
            new ColorInfo("Ultra Blue Pearl", 70),
            new ColorInfo("Purple Pearl", 71),
            new ColorInfo("Spin Purple Pearl", 72),
            new ColorInfo("Racing Blue Pearl", 73),
            new ColorInfo("Light Blue Pearl", 74),
            new ColorInfo("Yellow Pearl", 88),
            new ColorInfo("Race Yellow Pearl", 89),
            new ColorInfo("Bronze Pearl", 90),
            new ColorInfo("Fluorescent Yellow Pearl", 91),
            new ColorInfo("Lime Green Pearl", 92),
            new ColorInfo("Umber Brown Pearl", 94),
            new ColorInfo("Creek Brown Pearl", 95),
            new ColorInfo("Chocolate Brown Pearl", 96),
            new ColorInfo("Maple Brown Pearl", 97),
            new ColorInfo("Saddle Brown Pearl", 98),
            new ColorInfo("Straw Brown Pearl", 99),
            new ColorInfo("Moss Brown Pearl", 100),
            new ColorInfo("Bison Brown Pearl", 101),
            new ColorInfo("Woodbeech Brown Pearl", 102),
            new ColorInfo("Beechwood Brown Pearl", 103),
            new ColorInfo("Sienna Brown Pearl", 104),
            new ColorInfo("Sandy Brown Pearl", 105),
            new ColorInfo("Bleached Brown Pearl", 106),
            new ColorInfo("Cream Pearl", 107),
            new ColorInfo("White Pearl", 111),
            new ColorInfo("Frost White Pearl", 112),
        };
        #endregion

        #region Wheel Colors (limited palette)
        public static readonly ColorInfo[] WheelColors = new ColorInfo[]
        {
            new ColorInfo("Black", 0),
            new ColorInfo("Graphite", 1),
            new ColorInfo("Silver", 4),
            new ColorInfo("Gold", 37),
            new ColorInfo("Bronze", 90),
            new ColorInfo("White", 111),
            new ColorInfo("Chrome", 120),
        };
        #endregion

        /// <summary>
        /// Get all color categories for menu building
        /// </summary>
        public static Dictionary<string, ColorInfo[]> GetAllCategories()
        {
            return new Dictionary<string, ColorInfo[]>
            {
                { "Classic", ClassicColors },
                { "Metallic", MetallicColors },
                { "Matte", MatteColors },
                { "Metal", MetalFinishes },
                { "Chrome", ChromeColors },
            };
        }
    }
}
