using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ExtendedLSC.WheelFitment;

namespace ExtendedLSC
{
    /// <summary>
    /// Disables GTA V's native steering auto-center so a parked / just-exited vehicle keeps its front wheels turned
    /// instead of snapping back to center. Global EXE NOP patch on the auto-center store instructions (located by
    /// byte-signature scan), restored on unload. Works on BOTH editions — the signatures differ:
    ///   Legacy (b3788): two stores (mov [rbx+disp32],r15d / mov [rdx+disp32],eax).
    ///   Enhanced (b1013+): two `mov dword [rsi+0x9DC], 0` stores (the steering field zeroed = centered), located via
    ///     "moving" + "stationary" auto-center locator patterns and NOP'd at a fixed offset from each.
    /// (Enhanced patterns/offsets from the open-source Nochala/Proper-Steering-Fix, verified live on b1013.)
    /// Fails gracefully if signatures don't match — the per-frame SteeringAngle re-assert still holds the player's car.
    /// </summary>
    public static class SteeringFix
    {
        public static Action<string> Log { get; set; }
        public static bool Applied { get; private set; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint flNewProtect, out uint old);

        // Legacy (b3788) auto-center store instructions — NOP the whole store.
        private const string LEG_SIG_1 = "44 89 BB ?? ?? ?? ?? 8B 0D"; // mov [rbx+disp32], r15d  -> NOP 7 bytes
        private const string LEG_SIG_2 = "89 82 ?? ?? ?? ?? 38 81";     // mov [rdx+disp32], eax   -> NOP 6 bytes

        // Enhanced (b1013+) locator patterns. The actual `mov dword [rsi+0x9DC], 0` store (10 bytes) sits at a fixed
        // offset from each locator: MOVING -> hit-10, STATIONARY -> hit+24. NOP 10 bytes at each.
        private const string ENH_SIG_MOVING     = "31 C0 80 B9 ?? ?? ?? ?? ?? 0F 94 C0 48 8D 15 ?? ?? ?? ?? F3 0F 10 04 82";
        private const string ENH_SIG_STATIONARY = "83 F8 ?? 0F 84 ?? ?? ?? ?? 8B 87 ?? ?? ?? ?? 83 E0 ?? 0F 85";

        // Each applied patch: where we NOP'd + the original bytes (for restore).
        private static readonly List<(IntPtr site, byte[] orig)> _patches = new List<(IntPtr, byte[])>();

        /// <summary>Scan + NOP the auto-center stores for the running edition. Idempotent. True if the patch is in place.</summary>
        public static bool Apply()
        {
            if (Applied) return true;
            if (!GamePlatform.SteeringFixSupported)
            {
                Log?.Invoke($"[SteeringFix] {GamePlatform.EditionName}: auto-center patch unsupported — skipped (per-car re-assert still on)");
                return false;
            }
            try
            {
                if (!PatternScanner.Initialize())
                {
                    Log?.Invoke("[SteeringFix] pattern scanner init failed — auto-center fix skipped");
                    return false;
                }

                bool ok = GamePlatform.IsEnhanced
                    ? FindAndNop(ENH_SIG_MOVING, -10, 10, "Enhanced moving") &&
                      FindAndNop(ENH_SIG_STATIONARY, +24, 10, "Enhanced stationary")
                    : FindAndNop(LEG_SIG_1, 0, 7, "Legacy #1") &&
                      FindAndNop(LEG_SIG_2, 0, 6, "Legacy #2");

                if (!ok)
                {
                    Restore();   // undo any partial patch so we never leave the EXE half-patched
                    Log?.Invoke($"[SteeringFix] {GamePlatform.EditionName} signatures not found — global auto-center fix skipped (per-car re-assert still active)");
                    return false;
                }

                Applied = true;
                Log?.Invoke($"[SteeringFix] steering auto-center patched ({GamePlatform.EditionName}, {_patches.Count} sites)");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[SteeringFix] Apply error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Find a signature, then NOP <paramref name="nopCount"/> bytes at (hit + offset). Returns false if not found.</summary>
        private static bool FindAndNop(string signature, int offsetFromHit, int nopCount, string label)
        {
            IntPtr hit = PatternScanner.FindPattern(signature);
            if (hit == IntPtr.Zero) { Log?.Invoke($"[SteeringFix] {label} pattern not found"); return false; }
            IntPtr site = hit + offsetFromHit;
            byte[] orig = new byte[nopCount];
            ReadProcessMemory(GetCurrentProcess(), site, orig, nopCount, out _);
            byte[] nops = new byte[nopCount];
            for (int i = 0; i < nopCount; i++) nops[i] = 0x90;
            Write(site, nops);
            _patches.Add((site, orig));
            return true;
        }

        /// <summary>Restore the original bytes (called on script unload so we don't leave the EXE patched).</summary>
        public static void Restore()
        {
            try
            {
                foreach (var p in _patches)
                    if (p.orig != null) Write(p.site, p.orig);
            }
            catch { }
            _patches.Clear();
            Applied = false;
        }

        private static void Write(IntPtr site, byte[] bytes)
        {
            VirtualProtect(site, (UIntPtr)bytes.Length, 0x40 /*PAGE_EXECUTE_READWRITE*/, out uint old);
            WriteProcessMemory(GetCurrentProcess(), site, bytes, bytes.Length, out _);
            VirtualProtect(site, (UIntPtr)bytes.Length, old, out _);
        }
    }
}
