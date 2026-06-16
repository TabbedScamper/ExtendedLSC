using System;
using System.Runtime.InteropServices;
using GTA;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Direct memory access for wheel VISUAL size/width and COLLISION radii.
    ///
    /// Offsets reverse-engineered and verified live on GTA V Legacy build 3788,
    /// cross-checked against VStancer's own resolved-offset log.
    ///
    ///   Wheel array:     CVehicle + 0xC30  -> CWheel** array ; wheel count (byte) at +0xC38
    ///   Per-CWheel:      +0x110 tyre collider radius, +0x114 rim collider radius,
    ///                    +0x118 tyre collider width   (physics/contact; stock 0.393/0.298/0.298)
    ///   StreamRenderGfx: *(CVehicle+0x48) -> DrawHandler ; +0x370 -> StreamRenderGfx
    ///                    +0x08 visual wheel size, +0xBA0 visual wheel width (per-wheel render scale)
    ///
    /// SAFETY: every pointer is VirtualQuery-validated (committed + readable/writable, span in-region)
    /// BEFORE it is dereferenced. C# try/catch CANNOT catch access violations, so a bad pointer must
    /// never be dereferenced — it returns 0/false instead. This is the managed equivalent of the
    /// native ASI's SEH (__try/__except) guard.
    /// </summary>
    public static unsafe class WheelMemory
    {
        // ---- CVehicle offsets (build 3788) ----
        private const int OFF_WHEELS_PTR  = 0xC30;
        private const int OFF_WHEEL_COUNT = 0xC38;
        private const int OFF_DRAWHANDLER = 0x48;
        private const int OFF_STREAMGFX   = 0x370;
        private const int OFF_HANDLING    = 0x960;   // CVehicle -> CHandlingData* (b3788)
        private const int OFF_SUSP_RAISE  = 0xD0;    // CHandlingData.fSuspensionRaise (visual body lift)
        // CVehicle "fake suspension lowering amount" (b3788) — the RENDER-ONLY visual ride height the game's
        // suspension mod drives (GET_FAKE_SUSPENSION_LOWERING_AMOUNT reads it). Two adjacent copies; write
        // both. Per-vehicle (NOT model-shared), no physics/collision effect. +ve lowers, -ve raises.
        private const int OFF_FAKE_LOWER1 = 0x1A1C;
        private const int OFF_FAKE_LOWER2 = 0x1A20;
        // ---- CWheel field offsets ----
        public const int OFF_CAMBER     = 0x008;   // raw camber
        public const int OFF_CAMBER_INV = 0x010;   // inverse Y-rotation (VStancer convention)
        public const int OFF_X          = 0x030;   // track width / lateral offset (resets every frame)
        public const int OFF_Y          = 0x034;   // longitudinal offset (middle of the local position vec)
        public const int OFF_Z          = 0x038;   // wheel vertical offset (ride height)
        public const int OFF_TYRE_RADIUS = 0x110;
        public const int OFF_RIM_RADIUS  = 0x114;
        public const int OFF_TYRE_WIDTH  = 0x118;
        // ---- StreamRenderGfx field offsets (paired; both written) ----
        private const int OFF_VIS_SIZE   = 0x08;
        private const int OFF_VIS_SIZE2  = 0x0C;
        private const int OFF_VIS_WIDTH  = 0xBA0;
        private const int OFF_VIS_WIDTH2 = 0xBA4;

        // ====================================================================
        // VirtualQuery-based validation (the crash guard)
        // ====================================================================
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public int    AllocationProtect;
            public IntPtr RegionSize;
            public int    State;
            public int    Protect;
            public int    Type;
        }

        [DllImport("kernel32.dll")]
        private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        private const int MEM_COMMIT = 0x1000;
        private const int PAGE_GUARD = 0x100;
        private const int READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80; // RO/RW/WC/ER/ERW/EWC
        private const int WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;              // RW/WC/ERW/EWC

        private static readonly UIntPtr _mbiLen = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));

        // Validated-region RING cache. VirtualQuery is a kernel syscall measured at ~0.5ms EACH in GTA's huge
        // address space — calling it for every field access (~85/frame) ate the whole frame. The vehicle/wheel
        // structs live in a handful of committed regions, so we validate each region ONCE and reuse it. This is
        // a RING (evict-oldest when full) rather than a per-frame clear: a full clear would re-validate every
        // region on the next frame, creating a periodic spike WORSE than a constant cost. The ring stays warm
        // with zero spikes. Crash-safe: we only ever validate addresses of CURRENTLY-EXISTING entities (whose
        // regions are committed) — a transient/bad pointer is a NEW address that misses the ring and is still
        // VirtualQuery-checked; a despawned car's stale entry is simply never queried (no stale handles) and
        // gets overwritten by the ring in time.
        private const int CACHE_N = 64;
        private static readonly long[] _cBase = new long[CACHE_N];
        private static readonly long[] _cEnd = new long[CACHE_N];
        private static readonly int[]  _cProt = new int[CACHE_N];
        private static int _cCount = 0;   // populated entries (0..CACHE_N)
        private static int _cWrite = 0;   // next ring slot to overwrite

        /// <summary>No-op now (the ring self-evicts) — kept so callers don't change. Safe to remove the call.</summary>
        public static void BeginFrame() { }

        private static bool IsValid(long addr, int size, int prot)
        {
            if (addr <= 0x10000 || addr >= 0x7FFFFFFF0000) return false;
            long endAddr = addr + size;

            // Fast path: a previously-validated region fully contains this span with matching protection.
            for (int i = 0; i < _cCount; i++)
                if (addr >= _cBase[i] && endAddr <= _cEnd[i] && (_cProt[i] & prot) != 0) return true;

            try
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery((IntPtr)addr, out mbi, _mbiLen) == UIntPtr.Zero) return false;
                if (mbi.State != MEM_COMMIT) return false;
                if ((mbi.Protect & PAGE_GUARD) != 0) return false;
                long rbase = (long)mbi.BaseAddress, rend = rbase + (long)mbi.RegionSize;
                // Cache this committed, non-guard region into the ring (with its real protection).
                int slot = _cWrite % CACHE_N;
                _cBase[slot] = rbase; _cEnd[slot] = rend; _cProt[slot] = mbi.Protect;
                _cWrite++;
                if (_cCount < CACHE_N) _cCount++;
                if ((mbi.Protect & prot) == 0) return false;
                return endAddr <= rend;                 // whole span stays inside the region
            }
            catch { return false; }
        }

        private static long ReadPtr(long addr)
            => IsValid(addr, 8, READABLE) ? *(long*)addr : 0;
        private static float ReadF32(long addr)
            => IsValid(addr, 4, READABLE) ? *(float*)addr : 0f;
        private static bool WriteF32(long addr, float val)
        {
            if (!IsValid(addr, 4, WRITABLE)) return false;
            *(float*)addr = val; return true;
        }
        private static int ReadU8(long addr)
            => IsValid(addr, 1, READABLE) ? *(byte*)addr : -1;

        private static long Addr(Vehicle v)
            => (v != null && v.Exists() && v.MemoryAddress != IntPtr.Zero) ? v.MemoryAddress.ToInt64() : 0;

        // ====================================================================
        // Wheel pool
        // ====================================================================
        public static int GetWheelCount(Vehicle v)
        {
            long a = Addr(v); if (a == 0) return 0;
            int c = ReadU8(a + OFF_WHEEL_COUNT);
            return (c >= 0 && c <= 10) ? c : 0;
        }

        private static long WheelPtr(long vehAddr, int index)
        {
            long arr = ReadPtr(vehAddr + OFF_WHEELS_PTR);
            if (arr == 0) return 0;
            return ReadPtr(arr + index * 8L);
        }

        // ====================================================================
        // Handling (shared per model) — fSuspensionRaise = stable visual body lift
        // ====================================================================
        private static long HandlingPtr(Vehicle v)
        {
            long a = Addr(v); if (a == 0) return 0;
            return ReadPtr(a + OFF_HANDLING);
        }
        public static bool HasHandling(Vehicle v) => HandlingPtr(v) != 0;

        // CHandlingData float offsets (ikt HandlingInfo.h, b3788) used by the LSC stat bars + live tuning.
        public const int HOFF_DRIVE_FORCE = 0x60;    // fInitialDriveForce   -> Acceleration
        public const int HOFF_MAX_FLAT_VEL = 0x64;   // fInitialDriveMaxFlatVel -> Top Speed
        public const int HOFF_BRAKE_FORCE = 0x6C;    // fBrakeForce          -> Braking
        public const int HOFF_TRACTION_MAX = 0x88;   // fTractionCurveMax    -> Traction
        /// <summary>Read a CHandlingData float (model-shared). Returns 0 if the handling ptr is unavailable.</summary>
        public static float GetHandlingFloat(Vehicle v, int offset)
        {
            long h = HandlingPtr(v);
            return h == 0 ? 0f : ReadF32(h + offset);
        }
        /// <summary>Write a CHandlingData float (model-shared) — used by live tuning.</summary>
        public static bool SetHandlingFloat(Vehicle v, int offset, float val)
        {
            long h = HandlingPtr(v);
            return h != 0 && WriteF32(h + offset, val);
        }

        public static float GetSuspensionRaise(Vehicle v)
        {
            long h = HandlingPtr(v);
            return h == 0 ? 0f : ReadF32(h + OFF_SUSP_RAISE);
        }
        public static bool SetSuspensionRaise(Vehicle v, float val)
        {
            long h = HandlingPtr(v);
            return h != 0 && WriteF32(h + OFF_SUSP_RAISE, val);
        }

        /// <summary>Render-only visual ride height (the game's "fake suspension lowering"). +ve lowers the
        /// body, -ve raises it; no physics/collision change. Per-vehicle. Writes both mirror copies.</summary>
        public static float GetFakeLowering(Vehicle v)
        {
            long a = Addr(v);
            return a == 0 ? 0f : ReadF32(a + OFF_FAKE_LOWER1);
        }
        public static bool SetFakeLowering(Vehicle v, float val)
        {
            long a = Addr(v); if (a == 0) return false;
            bool ok = WriteF32(a + OFF_FAKE_LOWER1, val);
            WriteF32(a + OFF_FAKE_LOWER2, val);
            return ok;
        }

        // ====================================================================
        // Collision (per wheel)
        // ====================================================================
        public static float GetWheelField(Vehicle v, int wheel, int off)
        {
            long a = Addr(v); if (a == 0) return 0f;
            long w = WheelPtr(a, wheel);
            return w == 0 ? 0f : ReadF32(w + off);
        }
        public static bool SetWheelField(Vehicle v, int wheel, int off, float val)
        {
            long a = Addr(v); if (a == 0) return false;
            long w = WheelPtr(a, wheel);
            return w != 0 && WriteF32(w + off, val);
        }

        public static float GetTyreColliderRadius(Vehicle v, int wheel) => GetWheelField(v, wheel, OFF_TYRE_RADIUS);
        public static float GetRimColliderRadius(Vehicle v, int wheel)  => GetWheelField(v, wheel, OFF_RIM_RADIUS);
        public static float GetTyreColliderWidth(Vehicle v, int wheel)  => GetWheelField(v, wheel, OFF_TYRE_WIDTH);

        public static void SetAllColliderRadius(Vehicle v, float tyreRadius, float rimRadius)
        {
            int n = GetWheelCount(v);
            for (int i = 0; i < n; i++)
            {
                SetWheelField(v, i, OFF_TYRE_RADIUS, tyreRadius);
                SetWheelField(v, i, OFF_RIM_RADIUS, rimRadius);
            }
        }
        public static void SetAllColliderWidth(Vehicle v, float width)
        {
            int n = GetWheelCount(v);
            for (int i = 0; i < n; i++)
                SetWheelField(v, i, OFF_TYRE_WIDTH, width);
        }

        // ---- Camber / track / height (per wheel) ----
        public static float GetWheelX(Vehicle v, int wheel) => GetWheelField(v, wheel, OFF_X);
        public static bool  SetWheelX(Vehicle v, int wheel, float val) => SetWheelField(v, wheel, OFF_X, val);
        public static float GetWheelY(Vehicle v, int wheel) => GetWheelField(v, wheel, OFF_Y);
        public static float GetWheelZ(Vehicle v, int wheel) => GetWheelField(v, wheel, OFF_Z);
        public static bool  SetWheelZ(Vehicle v, int wheel, float val) => SetWheelField(v, wheel, OFF_Z, val);
        public static float GetWheelCamber(Vehicle v, int wheel) => GetWheelField(v, wheel, OFF_CAMBER);

        /// <summary>Set camber: raw value at 0x008, inverse Y-rotation at 0x010 (matches VStancer).</summary>
        public static bool SetWheelCamber(Vehicle v, int wheel, float camber)
        {
            long a = Addr(v); if (a == 0) return false;
            long w = WheelPtr(a, wheel); if (w == 0) return false;
            bool ok = WriteF32(w + OFF_CAMBER, camber);
            WriteF32(w + OFF_CAMBER_INV, -camber);
            return ok;
        }

        // ====================================================================
        // Visual (StreamRenderGfx) — only exists for aftermarket wheels
        // ====================================================================
        private static long StreamGfx(long vehAddr)
        {
            long dh = ReadPtr(vehAddr + OFF_DRAWHANDLER);
            if (dh == 0) return 0;
            return ReadPtr(dh + OFF_STREAMGFX);
        }

        public static bool HasVisualWheels(Vehicle v)
        {
            long a = Addr(v); if (a == 0) return false;
            return StreamGfx(a) != 0;
        }

        public static float GetVisualSize(Vehicle v)
        {
            long a = Addr(v); if (a == 0) return 1f;
            long s = StreamGfx(a);
            return s == 0 ? 1f : ReadF32(s + OFF_VIS_SIZE);
        }
        public static bool SetVisualSize(Vehicle v, float val)
        {
            long a = Addr(v); if (a == 0) return false;
            long s = StreamGfx(a); if (s == 0) return false;
            // Write ONLY +0x08 — exactly what VStancer touches for "visual size". The paired field
            // +0x0C is something else and writing it causes track/tread/sink artifacts.
            return WriteF32(s + OFF_VIS_SIZE, val);
        }
        public static float GetVisualWidth(Vehicle v)
        {
            long a = Addr(v); if (a == 0) return 1f;
            long s = StreamGfx(a);
            return s == 0 ? 1f : ReadF32(s + OFF_VIS_WIDTH);
        }
        public static bool SetVisualWidth(Vehicle v, float val)
        {
            long a = Addr(v); if (a == 0) return false;
            long s = StreamGfx(a); if (s == 0) return false;
            // Write ONLY +0xBA0 — what VStancer touches for "visual width" (+0xBA4 is a different field).
            return WriteF32(s + OFF_VIS_WIDTH, val);
        }
    }
}
