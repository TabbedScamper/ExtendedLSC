using System;
using System.Runtime.InteropServices;
using ExtendedLSC.WheelFitment;

namespace ExtendedLSC
{
    /// <summary>
    /// Disables GTA V's native steering auto-center so a parked / just-exited vehicle keeps its front wheels
    /// turned instead of snapping back to center (the "tires rotate back when leaving the vehicle" bug). This is
    /// a global EXE NOP patch on the two Legacy auto-center stores — the same technique used by Nochala's
    /// "Proper Steering Fix" .asi (https://www.gta5-mods.com/scripts/proper-steering-fix).
    ///
    /// Legacy (b3xxx) signatures only. Fails gracefully (logs + returns false) if the signatures don't match the
    /// running build — in that case ELSC's per-frame <c>SteeringAngle</c> re-assert still holds the wheels of the
    /// car the player configured in the menu, just not every other vehicle globally.
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

        // Legacy auto-center stores (from Proper Steering Fix). Each store writes the recentered steering value;
        // NOP-ing them leaves the wheels at their last angle. Bytes NOP'd = the store instruction length.
        private const string LEG_SIG_1 = "44 89 BB ?? ?? ?? ?? 8B 0D"; // mov [rbx+disp32], r15d  -> NOP 7 bytes
        private const string LEG_SIG_2 = "89 82 ?? ?? ?? ?? 38 81";     // mov [rdx+disp32], eax   -> NOP 6 bytes

        private static IntPtr _site1, _site2;
        private static byte[] _orig1, _orig2;

        /// <summary>Scan + NOP the auto-center stores. Idempotent. Returns true if the patch is in place.</summary>
        public static bool Apply()
        {
            if (Applied) return true;
            try
            {
                if (!PatternScanner.Initialize())
                {
                    Log?.Invoke("[SteeringFix] pattern scanner init failed — auto-center fix skipped");
                    return false;
                }

                _site1 = PatternScanner.FindPattern(LEG_SIG_1);
                _site2 = PatternScanner.FindPattern(LEG_SIG_2);
                if (_site1 == IntPtr.Zero || _site2 == IntPtr.Zero)
                {
                    Log?.Invoke($"[SteeringFix] Legacy steering signatures not found (s1=0x{_site1.ToInt64():X} s2=0x{_site2.ToInt64():X}) — global auto-center fix skipped (per-car re-assert still active)");
                    return false;
                }

                _orig1 = Nop(_site1, 7);
                _orig2 = Nop(_site2, 6);
                Applied = true;
                Log?.Invoke($"[SteeringFix] steering auto-center patched @ 0x{_site1.ToInt64():X} (7) + 0x{_site2.ToInt64():X} (6)");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[SteeringFix] Apply error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Restore the original bytes (called on script unload so we don't leave the EXE patched).</summary>
        public static void Restore()
        {
            if (!Applied) return;
            try
            {
                if (_orig1 != null) Write(_site1, _orig1);
                if (_orig2 != null) Write(_site2, _orig2);
                Log?.Invoke("[SteeringFix] steering auto-center restored");
            }
            catch { }
            Applied = false;
        }

        private static byte[] Nop(IntPtr site, int count)
        {
            byte[] orig = new byte[count];
            ReadProcessMemory(GetCurrentProcess(), site, orig, count, out _);
            byte[] nops = new byte[count];
            for (int i = 0; i < count; i++) nops[i] = 0x90;
            Write(site, nops);
            return orig;
        }

        private static void Write(IntPtr site, byte[] bytes)
        {
            VirtualProtect(site, (UIntPtr)bytes.Length, 0x40 /*PAGE_EXECUTE_READWRITE*/, out uint old);
            WriteProcessMemory(GetCurrentProcess(), site, bytes, bytes.Length, out _);
            VirtualProtect(site, (UIntPtr)bytes.Length, old, out _);
        }
    }
}
