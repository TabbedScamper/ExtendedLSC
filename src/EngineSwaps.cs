using System.Collections.Generic;

namespace ExtendedLSC
{
    /// <summary>
    /// A purchasable engine swap. The SOUND comes from FORCE_VEHICLE_ENGINE_AUDIO (a GTA vehicle audio ref —
    /// just the model name), the POWER from Vehicle.EnginePowerMultiplier. Because GET_VEHICLE_ACCELERATION
    /// does NOT read the power multiplier, each swap also carries display-only stat bonuses (AccelBonus /
    /// SpeedBonus) that ELSC folds into the LSC stat bars + performance index (PI) so the menu reflects what the player did.
    /// </summary>
    public class EngineSwap
    {
        public string Id;          // stable save key
        public string Name;        // menu label
        public string AudioName;   // FORCE_VEHICLE_ENGINE_AUDIO argument (vehicle audio/model name)
        public float PowerMult;    // EnginePowerMultiplier value (~percent added; ~1 = stock)
        public int Price;
        public float AccelBonus;   // added to the Acceleration stat fraction (0-1 scale)
        public float SpeedBonus;   // added to the Top Speed stat fraction (0-1 scale)
        public string Description;

        public EngineSwap(string id, string name, string audio, float power, int price,
                          float accelBonus, float speedBonus, string desc)
        {
            Id = id; Name = name; AudioName = audio; PowerMult = power; Price = price;
            AccelBonus = accelBonus; SpeedBonus = speedBonus; Description = desc;
        }
    }

    public static class EngineSwaps
    {
        // Ordered weakest -> strongest. Audio names   power values are easy to retune after in-game feel tests.
        public static readonly List<EngineSwap> All = new List<EngineSwap>
        {
            new EngineSwap("street_i4", "Tuned Turbo Inline-4", "sultan",   18f,   4000, 0.10f, 0.03f,
                "A high-revving turbo four. Crisp, eager street power."),
            new EngineSwap("muscle_v8", "American Muscle V8",    "dominator",32f,   6000, 0.16f, 0.05f,
                "Big-displacement pushrod V8. Deep idle, lazy torque."),
            new EngineSwap("twin_v6",   "Twin-Turbo V6",         "banshee",  48f,   8500, 0.20f, 0.07f,
                "Compact twin-turbo six. Smooth, relentless boost."),
            new EngineSwap("super_v8",  "Supercharged V8",       "gauntlet", 64f,  11000, 0.25f, 0.09f,
                "Belt-driven blower V8. Aggressive whine, savage pull."),
            new EngineSwap("electric",  "Electric Drive Unit",   "cyclone",  72f,  14000, 0.34f, 0.06f,
                "Dual electric motors. Silent launch, instant torque."),
            new EngineSwap("race_v12",  "Motorsport V12",        "zentorno", 90f,  16500, 0.30f, 0.13f,
                "Naturally-aspirated race V12. A screaming top end."),
            new EngineSwap("hyper_w16", "Quad-Turbo W16",        "adder",   130f,  20000, 0.40f, 0.18f,
                "The pinnacle. Obscene, endless power everywhere."),
        };

        public static EngineSwap Lookup(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in All) if (s.Id == id) return s;
            return null;
        }
    }
}
