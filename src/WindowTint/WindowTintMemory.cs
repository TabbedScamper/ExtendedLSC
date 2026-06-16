using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ExtendedLSC.WindowTint
{
    /// <summary>
    /// Locates and edits the global vehicle WINDOW-TINT color table (carcols.ymt WindowColors) in live
    /// memory, so ELSC can set custom glass colors at runtime.
    ///
    /// The table is an array of 5 entries { u32 colorARGB; u32 nameHash } the glass shader samples (indexed
    /// by each vehicle's window-tint enum) to color tintable glass. The COLORS get overwritten when we set a
    /// custom color, but the 5 NAME HASHES are invariant constants across game builds — they form a rock-solid
    /// 40-byte signature. We AOB-scan committed writable memory for that signature once per session (the table
    /// address is ASLR-bound) and cache it.
    ///
    /// Pairs with the dlc_elsc glass texture (white RGB + alpha 0): clear by default, vivid where a tint slot
    /// carries a color. Format is AARRGGBB. Traffic runs window tint -1 (factory default) and never uses an
    /// explicit slot, so writing a reserved slot only affects the player's car -> zero bleed.
    ///
    /// SAFETY: every read is VirtualQuery-validated (committed + readable, span in-region) before deref, exactly
    /// like WheelMemory. C# can't catch access violations, so a bad pointer returns 0/does nothing.
    /// </summary>
    public static unsafe class WindowTintMemory
    {
        // Invariant WindowColors name hashes (b3788, stable across builds). Entry i: color @ T+i*8, name @ T+i*8+4.
        private static readonly uint[] NAME_HASHES =
            { 0x7B883999, 0xF225DD57, 0x0882659E, 0xBD702E79, 0x8DB8E889 };
        public const int SLOT_COUNT = 5;
        private const int ENTRY_STRIDE = 8;   // {u32 color, u32 name}

        private static long _tableAddr = 0;
        public static bool Located => _tableAddr != 0;
        public static long TableAddress => _tableAddr;

        // ---- resumable scan cursor (chunked across ticks so the game never hitches) ----
        private static long _scanCursor = 0x10000;
        private static bool _scanDone = false;
        private const long SCAN_CEILING = 0x7FFFFFFF0000;
        private const int  SCAN_BUDGET_PER_STEP = 8 * 1024 * 1024;   // ~8MB / tick (bulk-copied, gentle)
        private const int  SCAN_CHUNK = 1 * 1024 * 1024;            // bulk read size
        private const int  SIG_LEN = 40;                            // 5 entries x 8 bytes
        private const int  MARK_SPAN = (SLOT_COUNT + 1) * ENTRY_STRIDE;  // 48: signature + the first custom slot
        private const uint INJECT_MARK = 0xE15C0000u;               // custom-slot name-hash marker base
        [ThreadStatic] private static byte[] _scanBuf;
        private static bool _stateChecked = false;   // have we tried to resume from the saved state file yet?

        // ================= VirtualQuery validation (same pattern as WheelMemory) =================
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, int flAllocationType, int flProtect);

        private const int MEM_COMMIT_RESERVE = 0x3000;   // MEM_COMMIT | MEM_RESERVE
        private const int PAGE_READWRITE = 0x04;

        private const int MEM_COMMIT = 0x1000;
        private const int PAGE_GUARD = 0x100;
        private const int READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
        private const int WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;

        // Region-validity cache. VirtualQuery is a ~0.5ms syscall in GTA's process; this module only validates
        // STATIC session-global addresses (the carcols table region + our injected VirtualAlloc page + the
        // atArray header), none of which move or get freed during a session, so the cache persists all session
        // (cleared only on Invalidate / re-scan). Reduces per-frame cost from ~1.8ms to ~0.
        private static readonly UIntPtr _mbiLen = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
        private const int VQ_CACHE_N = 8;
        private static readonly long[] _vqBase = new long[VQ_CACHE_N];
        private static readonly long[] _vqEnd = new long[VQ_CACHE_N];
        private static readonly int[] _vqProt = new int[VQ_CACHE_N];
        private static int _vqCount = 0;
        private static void ClearValidationCache() { _vqCount = 0; }

        private static bool IsValid(long addr, int size, int prot)
        {
            if (addr <= 0x10000 || addr >= SCAN_CEILING) return false;
            long endAddr = addr + size;
            for (int i = 0; i < _vqCount; i++)
                if (addr >= _vqBase[i] && endAddr <= _vqEnd[i] && (_vqProt[i] & prot) != 0) return true;
            try
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQuery((IntPtr)addr, out mbi, _mbiLen) == UIntPtr.Zero) return false;
                if (mbi.State != MEM_COMMIT) return false;
                if ((mbi.Protect & PAGE_GUARD) != 0) return false;
                long rbase = (long)mbi.BaseAddress, rend = rbase + (long)mbi.RegionSize;
                if (_vqCount < VQ_CACHE_N) { _vqBase[_vqCount] = rbase; _vqEnd[_vqCount] = rend; _vqProt[_vqCount] = mbi.Protect; _vqCount++; }
                if ((mbi.Protect & prot) == 0) return false;
                return endAddr <= rend;
            }
            catch { return false; }
        }

        private static uint ReadU32(long addr)
            => IsValid(addr, 4, READABLE) ? *(uint*)addr : 0u;

        // ================= table location (chunked AOB scan) =================

        /// <summary>Advance the table scan by one budgeted step. Call from OnTick until Located is true.
        /// Bulk-copies each region into a managed buffer (NO per-byte syscalls) and scans that. Returns true
        /// once the table is found (or already found / scan exhausted).</summary>
        public static bool ScanStep()
        {
            if (_tableAddr != 0) return true;
            // Fast path: a previous expansion this game session left a state file. Script reloads reset our
            // statics but NOT game memory or its ASLR layout, so the saved addresses are still valid — adopt
            // them instantly instead of re-scanning (this is what makes reloads quick instead of "initializing").
            if (!_stateChecked) { _stateChecked = true; if (TryResumeFromState()) return true; }
            if (_scanDone) return false;
            if (_scanBuf == null) _scanBuf = new byte[SCAN_CHUNK];

            int budget = SCAN_BUDGET_PER_STEP;
            while (budget > 0)
            {
                if (_scanCursor >= SCAN_CEILING) { _scanDone = true; return false; }

                MEMORY_BASIC_INFORMATION mbi;
                UIntPtr len = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
                if (VirtualQuery((IntPtr)_scanCursor, out mbi, len) == UIntPtr.Zero) { _scanDone = true; return false; }

                long regionBase = (long)mbi.BaseAddress, regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0) { _scanDone = true; return false; }
                long regionEnd = regionBase + regionSize;
                bool ok = mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & WRITABLE) != 0;

                if (ok)
                {
                    long pos = Math.Max(_scanCursor, regionBase);
                    while (pos + SIG_LEN <= regionEnd && budget > 0)
                    {
                        int chunk = (int)Math.Min(SCAN_CHUNK, regionEnd - pos);
                        try { Marshal.Copy((IntPtr)pos, _scanBuf, 0, chunk); }
                        catch { break; }   // region changed between query and copy — bail this region
                        int off = FindSignature(_scanBuf, chunk);
                        if (off >= 0) { _tableAddr = pos + off; _origTableAddr = _tableAddr; return true; }
                        long advance = chunk - SIG_LEN + 4;   // overlap so a straddling signature isn't missed
                        if (advance < 4) advance = chunk;
                        pos += advance;
                        budget -= chunk;
                    }
                    if (pos + SIG_LEN <= regionEnd) { _scanCursor = pos; return false; }   // budget hit; resume
                }
                _scanCursor = regionEnd;
            }
            return false;
        }

        /// <summary>Find the name-hash signature in a managed buffer. Returns table offset or -1.</summary>
        private static int FindSignature(byte[] buf, int len)
        {
            uint n0 = NAME_HASHES[0];
            fixed (byte* p = buf)
            {
                for (int o = 0; o + SIG_LEN <= len; o += 4)
                {
                    if (*(uint*)(p + o + 4) != n0) continue;
                    bool match = true;
                    for (int i = 1; i < SLOT_COUNT; i++)
                        if (*(uint*)(p + o + i * ENTRY_STRIDE + 4) != NAME_HASHES[i]) { match = false; break; }
                    if (match) return o;
                }
            }
            return -1;
        }

        /// <summary>Reset the cached address (e.g. after a session/vehicle reset) to force a re-scan.</summary>
        public static void Invalidate()
        {
            _tableAddr = 0;
            _scanCursor = 0x10000;
            _scanDone = false;
            _stateChecked = false;
            ClearValidationCache();
            _headerAddr = 0;
            _headerCursor = 0x10000;
            _headerScanDone = false;
            _origTableAddr = 0;
            _injectedArray = 0;
            Expanded = false;
            SlotCount = SLOT_COUNT;
        }

        // ================= array expansion (inject more slots) =================
        // The color table is the m_Elements of an atArray { void* m_Elements; u16 m_Count; u16 m_Capacity }.
        // Color resolution is `enum < m_Count ? array[enum] : fallback` — data-driven. So we allocate a bigger
        // array, copy the originals, and repoint the header => enum 0..N-1 address N slots. (Verified live.)
        private static long _headerCursor = 0x10000;
        private static bool _headerScanDone = false;
        private static long _headerAddr = 0;
        private static long _origTableAddr = 0;   // the original 5-entry array (header points here pre-expand)
        private static long _injectedArray = 0;   // our VirtualAlloc'd larger array
        public static bool Expanded { get; private set; } = false;
        public static int SlotCount { get; private set; } = SLOT_COUNT;

        /// <summary>True if our injected array is still the one the header points at AND m_Count is still our
        /// expanded size. The game can reset EITHER (repoint elements back to the original table, or just drop
        /// m_Count to 5) — either one puts a custom-slot tint enum out of range => milky-white fallback.</summary>
        public static bool InjectionIntact => Expanded && _injectedArray != 0
            && ReadU64(_headerAddr) == _injectedArray
            && (ReadU32(_headerAddr + 8) & 0xFFFF) == SlotCount;

        /// <summary>Cheap per-tick guard: if the header drifted off our (already color-filled) array or its
        /// m_Count shrank, repoint it back at the SAME pre-allocated array — no realloc, colors intact, fixed
        /// within one frame. No-ops when everything's already correct. Returns true if it had to repair.
        /// Returns false (and leaves the deep fallback to rescan) if the header address itself is no longer
        /// writable, i.e. it relocated.</summary>
        public static bool ReassertInjection()
        {
            if (!Expanded || _injectedArray == 0 || _headerAddr == 0) return false;
            long curEl = ReadU64(_headerAddr);
            int curCount = (int)(ReadU32(_headerAddr + 8) & 0xFFFF);
            if (curEl == _injectedArray && curCount == SlotCount) return false;   // fully intact, nothing to do
            if (!IsValid(_headerAddr, 12, WRITABLE)) return false;                // header gone — needs a rescan
            WriteU64(_headerAddr, _injectedArray);
            WriteU16(_headerAddr + 8, (ushort)SlotCount);     // m_Count
            WriteU16(_headerAddr + 10, (ushort)SlotCount);    // m_Capacity
            _tableAddr = _injectedArray;
            return true;
        }

        /// <summary>Force ExpandStep to run again (e.g. if the game reset the array). Points slot R/W back at
        /// the original table until the re-expand completes.</summary>
        public static void ForceReexpand()
        {
            Expanded = false;
            _injectedArray = 0;
            _tableAddr = _origTableAddr;
            SlotCount = SLOT_COUNT;
        }

        private static void WriteU64(long addr, long val) { if (IsValid(addr, 8, WRITABLE)) *(long*)addr = val; }
        private static void WriteU16(long addr, ushort val) { if (IsValid(addr, 2, WRITABLE)) *(ushort*)addr = val; }
        private static long ReadU64(long addr) => IsValid(addr, 8, READABLE) ? *(long*)addr : 0;

        /// <summary>Locate the array header and repoint it to a fresh, larger array. Chunked across ticks.
        /// Call after the table is Located, until it returns true. Idempotent.</summary>
        public static bool ExpandStep(int targetCount)
        {
            if (Expanded) return true;
            if (_tableAddr == 0 || targetCount <= SLOT_COUNT) return false;

            if (_headerAddr == 0)
            {
                if (!FindHeaderStep()) return false;     // still searching this tick
                if (_headerAddr == 0) return false;
            }

            // The header's m_Elements always == _tableAddr here (FindHeaderStep matched on it). Inspect m_Count:
            int headCount = (int)(ReadU32(_headerAddr + 8) & 0xFFFF);
            if (headCount != SLOT_COUNT)
            {
                // Not the fresh 5-entry layout. After a SCRIPT RELOAD, a previous injection of ours is often
                // still live in game memory (header already points at our 32-slot array). Detect our own array
                // by the custom name-hash marker we stamp into the extra slots, and ADOPT it — otherwise we'd
                // leave SlotCount at 5 and the reserved preview slot (31) would be out of range => solid-white
                // glass. If it's NOT ours, leave the built-ins untouched rather than corrupt memory.
                bool oursAndBigEnough = headCount > SLOT_COUNT && _tableAddr != 0
                    && ReadU32(_tableAddr + SLOT_COUNT * ENTRY_STRIDE + 4) == (INJECT_MARK + SLOT_COUNT);
                if (oursAndBigEnough)
                {
                    _injectedArray = _tableAddr;
                    SlotCount = headCount;
                    Expanded = true;
                    SaveState();
                    return true;
                }
                Expanded = true;
                return true;
            }

            IntPtr mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)(targetCount * ENTRY_STRIDE), MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (mem == IntPtr.Zero) { Expanded = true; return true; }   // alloc failed: keep the 5 built-ins
            long na = mem.ToInt64();

            for (int i = 0; i < SLOT_COUNT; i++)   // preserve None + smoke + their name hashes (from the original)
            {
                *(uint*)(na + i * ENTRY_STRIDE)     = ReadU32(_origTableAddr + i * ENTRY_STRIDE);
                *(uint*)(na + i * ENTRY_STRIDE + 4) = ReadU32(_origTableAddr + i * ENTRY_STRIDE + 4);
            }
            for (int i = SLOT_COUNT; i < targetCount; i++)   // new custom slots: clear, arbitrary unique name
            {
                *(uint*)(na + i * ENTRY_STRIDE)     = 0u;
                *(uint*)(na + i * ENTRY_STRIDE + 4) = INJECT_MARK + (uint)i;
            }

            WriteU64(_headerAddr, na);
            WriteU16(_headerAddr + 8, (ushort)targetCount);    // m_Count
            WriteU16(_headerAddr + 10, (ushort)targetCount);   // m_Capacity

            _tableAddr = na;          // slots now live in our array
            _injectedArray = na;
            SlotCount = targetCount;
            Expanded = true;
            SaveState();              // so a script reload can re-adopt this instantly instead of re-scanning
            return true;
        }

        // ================= reload-resume state file (addresses survive a SCRIPT reload, not a game restart) =====
        private static string StatePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "wintint_inject.bin");

        /// <summary>Persist the injection addresses so the next script reload adopts them without a full scan.</summary>
        private static void SaveState()
        {
            try
            {
                if (_headerAddr == 0 || _injectedArray == 0) return;
                string dir = Path.GetDirectoryName(StatePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var b = new byte[32];
                BitConverter.GetBytes(_headerAddr).CopyTo(b, 0);
                BitConverter.GetBytes(_injectedArray).CopyTo(b, 8);
                BitConverter.GetBytes(_origTableAddr).CopyTo(b, 16);
                BitConverter.GetBytes((long)SlotCount).CopyTo(b, 24);
                File.WriteAllBytes(StatePath, b);
            }
            catch { /* non-fatal: we just fall back to scanning next time */ }
        }

        /// <summary>Try to adopt a prior injection from the state file. Validates that the saved addresses still
        /// describe OUR live array (header still points at it + our marker present) — so it self-rejects stale
        /// addresses from a previous game launch (ASLR moved everything) and we cleanly re-scan instead.</summary>
        private static bool TryResumeFromState()
        {
            try
            {
                if (!File.Exists(StatePath)) return false;
                var b = File.ReadAllBytes(StatePath);
                if (b.Length < 32) return false;
                long header = BitConverter.ToInt64(b, 0);
                long array  = BitConverter.ToInt64(b, 8);
                long orig   = BitConverter.ToInt64(b, 16);
                int slots   = (int)BitConverter.ToInt64(b, 24);
                if (header == 0 || array == 0 || slots <= SLOT_COUNT) return false;

                // Validate the live layout still matches what we saved (guards against stale post-restart addrs).
                if (!IsValid(header, 12, WRITABLE)) return false;
                if (ReadU64(header) != array) return false;                                  // header still ours?
                if ((int)(ReadU32(header + 8) & 0xFFFF) != slots) return false;              // count matches?
                if (ReadU32(array + SLOT_COUNT * ENTRY_STRIDE + 4) != INJECT_MARK + SLOT_COUNT) return false; // marker?

                _headerAddr = header;
                _injectedArray = array;
                _origTableAddr = orig != 0 ? orig : array;
                _tableAddr = array;
                SlotCount = slots;
                Expanded = true;
                return true;
            }
            catch { return false; }
        }

        /// <summary>One budgeted step of the heap scan for the atArray header (a qword == original table addr).
        /// Bulk-copies regions into the managed buffer and scans that (no per-byte syscalls).</summary>
        private static bool FindHeaderStep()
        {
            if (_headerScanDone) return false;
            if (_scanBuf == null) _scanBuf = new byte[SCAN_CHUNK];
            long target = _tableAddr;
            int budget = SCAN_BUDGET_PER_STEP;
            while (budget > 0)
            {
                if (_headerCursor >= SCAN_CEILING) { _headerScanDone = true; return false; }

                MEMORY_BASIC_INFORMATION mbi;
                UIntPtr len = (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
                if (VirtualQuery((IntPtr)_headerCursor, out mbi, len) == UIntPtr.Zero) { _headerScanDone = true; return false; }
                long regionBase = (long)mbi.BaseAddress, regionSize = (long)mbi.RegionSize;
                if (regionSize <= 0) { _headerScanDone = true; return false; }
                long regionEnd = regionBase + regionSize;
                bool ok = mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & WRITABLE) != 0;

                if (ok)
                {
                    long pos = Math.Max(_headerCursor, regionBase);
                    while (pos + 12 <= regionEnd && budget > 0)
                    {
                        int chunk = (int)Math.Min(SCAN_CHUNK, regionEnd - pos);
                        try { Marshal.Copy((IntPtr)pos, _scanBuf, 0, chunk); }
                        catch { break; }
                        int off = FindQword(_scanBuf, chunk, target);
                        if (off >= 0) { _headerAddr = pos + off; return true; }
                        long advance = chunk - 16;   // 16-byte overlap so a header struct straddling a chunk isn't missed
                        if (advance < 8) advance = chunk;
                        pos += advance; budget -= chunk;
                    }
                    if (pos + 12 <= regionEnd) { _headerCursor = pos; return false; }
                }
                _headerCursor = regionEnd;
            }
            return false;
        }

        /// <summary>Find the atArray HEADER for the table in a managed buffer. Not just any pointer-to-table —
        /// the struct must look like a real atArray { m_Elements==target; u16 m_Count==m_Capacity; count in a
        /// plausible range }. Without this we'd lock onto an unrelated pointer-to-table (m_Count garbage) and
        /// expansion would abort at 5 slots. Returns offset or -1.</summary>
        private static int FindQword(byte[] buf, int len, long target)
        {
            fixed (byte* p = buf)
            {
                for (int o = 0; o + 12 <= len; o += 8)
                {
                    if (*(long*)(p + o) != target) continue;
                    ushort cnt = *(ushort*)(p + o + 8);
                    ushort cap = *(ushort*)(p + o + 10);
                    if (cnt == cap && cnt >= SLOT_COUNT && cnt <= 256) return o;   // genuine atArray header
                }
            }
            return -1;
        }

        // ================= read / write slot colors (AARRGGBB) =================

        public static bool SetSlotColor(int slot, uint argb)
        {
            if (_tableAddr == 0 || slot < 0 || slot >= SlotCount) return false;
            long addr = _tableAddr + slot * ENTRY_STRIDE;
            if (!IsValid(addr, 4, WRITABLE)) return false;
            *(uint*)addr = argb;
            return true;
        }

        public static uint GetSlotColor(int slot)
        {
            if (_tableAddr == 0 || slot < 0 || slot >= SlotCount) return 0;
            return ReadU32(_tableAddr + slot * ENTRY_STRIDE);
        }
    }
}
