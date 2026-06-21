using System;
using System.Diagnostics;

namespace ExtendedLSC
{
    /// <summary>
    /// Detects GTA V Legacy (b3788, GTA5.exe) vs Enhanced (VER_EN_1_0_1013+, GTA5_Enhanced.exe) at runtime and gates
    /// the raw-memory features per edition. Each gate is on only where its offsets/signatures are verified, so ONE
    /// build runs safely on BOTH editions: a feature whose memory isn't confirmed for the running binary stands down
    /// (returns 0 / no-op) instead of corrupting an unrelated struct. Pure native-call features are never gated.
    ///
    /// Findings (verified live via the bridge on b1013): the CVehicle / CWheel / CHandlingData struct layouts are
    /// IDENTICAL between Legacy and Enhanced (handling ptr +0x960, wheel array +0xC30/+0xC38, all the field offsets),
    /// so handling stat bars + live tuning + wheel-geometry fitment work on both. The render-only offsets and the
    /// code-signature / EXE-patch features are only derived for Legacy so far → Legacy-only until re-verified.
    /// </summary>
    public static class GamePlatform
    {
        public static Action<string> Log { get; set; }

        private static bool _resolved;
        private static bool _isEnhanced;

        public static bool IsEnhanced { get { Resolve(); return _isEnhanced; } }
        public static bool IsLegacy => !IsEnhanced;
        public static string EditionName => IsEnhanced ? "Enhanced" : "Legacy";

        // ── per-feature memory gates ──────────────────────────────────────────────────────────────
        // CVehicle/CWheel/CHandlingData struct offsets — verified identical on Legacy b3788 AND Enhanced b1013.
        // Powers the LSC stat bars, live handling tuning, and wheel-geometry fitment (camber/track/collider).
        public static bool StructMemorySupported => true;

        // Fake suspension lowering / ride height (CVehicle+0x1A1C/0x1A20) — VERIFIED identical on Enhanced b1013
        // (the native GET_FAKE_SUSPENSION_LOWERING_AMOUNT reads back exactly what we write to this field).
        public static bool RideHeightSupported => true;

        // Visual wheel size/width (DrawHandler->StreamRenderGfx chain) — VERIFIED on Enhanced b1013 (writing size@+0x08
        // / width@+0xBA0 visibly scaled the wheels). The DrawHandler->StreamRenderGfx pointer offset differs per
        // edition (0x370 Legacy / 0x4B0 Enhanced — handled in WheelMemory.OFF_STREAMGFX); the field offsets match.
        public static bool VisualWheelSupported => true;

        // Window glass color: the carcols WindowColors table is located by a name-hash signature that's VERIFIED
        // present + structure-identical on Enhanced b1013. The expansion machinery (header redirect) is build-agnostic
        // (it dynamically scans for whatever points at the table) and self-verifies via marker hashes, so it activates
        // if it works and safely stays inactive if it doesn't. Vivid color still needs the whitened-glass DLC.
        public static bool WindowColorSupported => true;

        // Manual transmission: gearbox offsets (gear/RPM/clutch/throttle) verified identical on Enhanced b1013 via
        // data-RE, and the clutch-drop EXE patch pattern (C7 ?? 54 CD CC CC 3D) finds the same 6-store cluster on
        // Enhanced (0.1f constant + clutch field offset are build-invariant). Enhanced offsets are hardcoded in
        // VehicleMemory (the Legacy *code* patterns don't match Enhanced, so the scan is edition-branched).
        public static bool ManualTransmissionSupported => true;

        // Steering auto-center EXE NOP patch — verified on both editions (Legacy b3788 sigs + Enhanced b1013 sigs from
        // Nochala/Proper-Steering-Fix, confirmed live: both find the `mov dword [rsi+0x9DC],0` auto-center stores).
        public static bool SteeringFixSupported => true;

        // Remaining code-signature / EXE patches not yet re-derived for Enhanced (none currently). Legacy-only.
        public static bool CodePatchMemorySupported => IsLegacy;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                // Enhanced launches as GTA5_Enhanced.exe under a "...Grand Theft Auto V Enhanced" folder; Legacy is GTA5.exe.
                string f = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                _isEnhanced = f.IndexOf("Enhanced", StringComparison.OrdinalIgnoreCase) >= 0;
                Log?.Invoke($"[Platform] Running {EditionName} ({f}); struct-mem ON, ride-height {(RideHeightSupported ? "ON" : "OFF")}, visual-wheel {(VisualWheelSupported ? "ON" : "OFF")}, code-patch {(CodePatchMemorySupported ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                // Couldn't read the module name — assume Legacy (the fully-verified edition) so nothing cross-fires.
                _isEnhanced = false;
                Log?.Invoke($"[Platform] detect failed ({ex.Message}); assuming Legacy");
            }
        }
    }
}
