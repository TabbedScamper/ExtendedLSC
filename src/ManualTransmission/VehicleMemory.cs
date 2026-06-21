using System;
using System.Runtime.InteropServices;
using GTA;

namespace ExtendedLSC.ManualTransmission
{
    /// <summary>
    /// Direct memory access to vehicle transmission data.
    /// Offsets are found via pattern scanning for version independence.
    /// </summary>
    public static class VehicleMemory
    {
        private static bool _initialized = false;
        private static bool _available = false;
        private static string _lastError = null;

        // Logging delegate
        public static Action<string> Log { get; set; }

        #region Kernel32 Imports

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        #endregion

        #region Offsets (found via pattern scanning)

        private static IntPtr _moduleBase = IntPtr.Zero;
        private static uint _moduleSize = 0;

        private static int _currentGearOffset = 0;
        private static int _nextGearOffset = 0;
        private static int _topGearOffset = 0;
        private static int _gearRatiosOffset = 0;
        private static int _currentRPMOffset = 0;
        private static int _clutchOffset = 0;
        private static int _throttleOffset = 0;
        // Reserved for future use
        // private static int _turboOffset = 0;
        // private static int _handbrakeOffset = 0;
        // private static int _steeringAngleOffset = 0;

        #endregion

        #region Properties

        public static bool IsAvailable
        {
            get
            {
                if (!_initialized)
                    Initialize();
                return _available;
            }
        }

        public static string LastError => _lastError;

        #endregion

        #region Initialization

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Log?.Invoke("[VehicleMemory] Initializing...");

                // Find the GTA5.exe module
                IntPtr hProcess = GetCurrentProcess();
                IntPtr hModule = GetModuleHandle(null); // Main module (GTA5.exe)

                if (hModule == IntPtr.Zero)
                {
                    _lastError = "Could not get main module handle";
                    Log?.Invoke($"[VehicleMemory] ERROR: {_lastError}");
                    return;
                }

                MODULEINFO modInfo;
                if (!GetModuleInformation(hProcess, hModule, out modInfo, (uint)Marshal.SizeOf(typeof(MODULEINFO))))
                {
                    _lastError = "Could not get module information";
                    Log?.Invoke($"[VehicleMemory] ERROR: {_lastError}");
                    return;
                }

                Log?.Invoke($"[VehicleMemory] Module base: 0x{modInfo.lpBaseOfDll.ToInt64():X}, Size: {modInfo.SizeOfImage}");
                _moduleBase = modInfo.lpBaseOfDll;
                _moduleSize = modInfo.SizeOfImage;

                // Find gear offsets using pattern scanning
                // Pattern for next/current gear offset (from ikt's code)
                // "\x48\x8D\x8F\x00\x00\x00\x00\x4C\x8B\xC3\xF3\x0F\x11\x7C\x24" "xxx????xxxxxxxx"
                IntPtr gearPatternAddr = FindPattern(modInfo.lpBaseOfDll, modInfo.SizeOfImage,
                    new byte[] { 0x48, 0x8D, 0x8F, 0x00, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0xC3, 0xF3, 0x0F, 0x11, 0x7C, 0x24 },
                    "xxx????xxxxxxxx");

                if (gearPatternAddr != IntPtr.Zero)
                {
                    _nextGearOffset = ReadInt32(gearPatternAddr + 3);
                    _currentGearOffset = _nextGearOffset + 2;
                    _topGearOffset = _nextGearOffset + 6;
                    _gearRatiosOffset = _nextGearOffset + 8;
                    Log?.Invoke($"[VehicleMemory] Gear offsets found: Next=0x{_nextGearOffset:X}, Current=0x{_currentGearOffset:X}, Top=0x{_topGearOffset:X}");
                }
                else
                {
                    Log?.Invoke("[VehicleMemory] WARNING: Could not find gear pattern");
                }

                // Find RPM/clutch/throttle pattern
                // "\x76\x03\x0F\x28\xF0\xF3\x44\x0F\x10\x93" "xxxxxxxxxx"
                IntPtr rpmPatternAddr = FindPattern(modInfo.lpBaseOfDll, modInfo.SizeOfImage,
                    new byte[] { 0x76, 0x03, 0x0F, 0x28, 0xF0, 0xF3, 0x44, 0x0F, 0x10, 0x93 },
                    "xxxxxxxxxx");

                if (rpmPatternAddr != IntPtr.Zero)
                {
                    _currentRPMOffset = ReadInt32(rpmPatternAddr + 10);
                    _clutchOffset = _currentRPMOffset + 0xC;
                    _throttleOffset = _currentRPMOffset + 0x10;
                    Log?.Invoke($"[VehicleMemory] RPM offset: 0x{_currentRPMOffset:X}, Clutch: 0x{_clutchOffset:X}, Throttle: 0x{_throttleOffset:X}");
                }
                else
                {
                    Log?.Invoke("[VehicleMemory] WARNING: Could not find RPM pattern");
                }

                // Check if we have minimum required offsets
                if (_currentGearOffset != 0 && _currentRPMOffset != 0 && _clutchOffset != 0)
                {
                    _available = true;
                    Log?.Invoke("[VehicleMemory] Initialization successful!");
                }
                else
                {
                    _lastError = "Required offsets not found";
                    Log?.Invoke($"[VehicleMemory] ERROR: {_lastError}");
                }
            }
            catch (Exception ex)
            {
                _lastError = $"Exception during init: {ex.Message}";
                Log?.Invoke($"[VehicleMemory] ERROR: {_lastError}");
            }
        }

        #endregion

        #region Pattern Scanning

        private static IntPtr FindPattern(IntPtr baseAddress, uint size, byte[] pattern, string mask)
        {
            byte[] buffer = new byte[size];
            int bytesRead;

            if (!ReadProcessMemory(GetCurrentProcess(), baseAddress, buffer, (int)size, out bytesRead))
                return IntPtr.Zero;

            for (int i = 0; i < bytesRead - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (mask[j] == '?' || buffer[i + j] == pattern[j])
                        continue;
                    found = false;
                    break;
                }

                if (found)
                    return baseAddress + i;
            }

            return IntPtr.Zero;
        }

        private static int ReadInt32(IntPtr address)
        {
            byte[] buffer = new byte[4];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), address, buffer, 4, out bytesRead);
            return BitConverter.ToInt32(buffer, 0);
        }

        #endregion

        #region Gearbox Auto-Shift Patches

        // THE root cause of every "manual transmission fights me" symptom (half-power 1st, gears that
        // decay at full throttle): the game's auto-shift state machine keeps trying to shift underneath
        // the pinned gear and drops the CLUTCH to 0.1f every physics step (mid-shift disengage = zero
        // torque). Six `mov dword [rbx+0x54], 0x3DCCCCCD` stores inside one state-machine function
        // (verified live on b3788: NOPing exactly these six turned a dying 3rd gear into a clean
        // 0-103mph pull). We NOP them while the manual box is active and restore on disable —
        // the same approach ikt's Manual Transmission uses, with patterns re-derived for this build.
        private static IntPtr[] _shiftPatchSites = null;
        private static byte[][] _shiftPatchOrig = null;
        private static bool _shiftPatchesApplied = false;

        private static readonly byte[] CLUTCH_DROP_PATTERN = { 0xC7, 0x00, 0x54, 0xCD, 0xCC, 0xCC, 0x3D };
        private const string CLUTCH_DROP_MASK = "x?xxxxx";

        /// <summary>Locate the clutch-drop stores: all pattern hits, filtered to the dense cluster
        /// (≥4 hits within 4KB) that is the auto-shift state machine — lookalike stores elsewhere
        /// (e.g. initializers) are excluded by the clustering.</summary>
        private static void FindShiftPatchSites()
        {
            if (_shiftPatchSites != null || _moduleBase == IntPtr.Zero) return;
            try
            {
                var hits = FindPatternAll(_moduleBase, _moduleSize, CLUTCH_DROP_PATTERN, CLUTCH_DROP_MASK);
                var cluster = new System.Collections.Generic.List<IntPtr>();
                foreach (var h in hits)
                {
                    int near = 0;
                    foreach (var o in hits)
                        if (Math.Abs(o.ToInt64() - h.ToInt64()) <= 0x1000) near++;
                    if (near >= 4) cluster.Add(h);
                }
                _shiftPatchSites = cluster.ToArray();
                _shiftPatchOrig = new byte[_shiftPatchSites.Length][];
                Log?.Invoke($"[VehicleMemory] Auto-shift clutch-drop sites: {hits.Count} hits, {cluster.Count} in cluster");
            }
            catch (Exception ex) { Log?.Invoke($"[VehicleMemory] FindShiftPatchSites error: {ex.Message}"); }
        }

        /// <summary>NOP the auto-shift clutch drops (call when the manual box engages).</summary>
        public static void ApplyShiftPatches()
        {
            FindShiftPatchSites();
            if (_shiftPatchesApplied || _shiftPatchSites == null || _shiftPatchSites.Length < 4) return;
            try
            {
                for (int i = 0; i < _shiftPatchSites.Length; i++)
                {
                    IntPtr site = _shiftPatchSites[i];
                    var orig = new byte[7];
                    int n;
                    ReadProcessMemory(GetCurrentProcess(), site, orig, 7, out n);
                    if (orig[0] != 0xC7) continue;   // already patched or unexpected — skip
                    _shiftPatchOrig[i] = orig;
                    uint oldProt;
                    VirtualProtect(site, (UIntPtr)7, 0x40 /*PAGE_EXECUTE_READWRITE*/, out oldProt);
                    WriteProcessMemory(GetCurrentProcess(), site, new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, 7, out n);
                    VirtualProtect(site, (UIntPtr)7, oldProt, out oldProt);
                }
                _shiftPatchesApplied = true;
                Log?.Invoke($"[VehicleMemory] Auto-shift clutch drops PATCHED ({_shiftPatchSites.Length} sites)");
            }
            catch (Exception ex) { Log?.Invoke($"[VehicleMemory] ApplyShiftPatches error: {ex.Message}"); }
        }

        /// <summary>Restore the original instructions (manual box disabled / vehicle exited).</summary>
        public static void RestoreShiftPatches()
        {
            if (!_shiftPatchesApplied || _shiftPatchSites == null) return;
            try
            {
                for (int i = 0; i < _shiftPatchSites.Length; i++)
                {
                    if (_shiftPatchOrig[i] == null) continue;
                    uint oldProt;
                    int n;
                    VirtualProtect(_shiftPatchSites[i], (UIntPtr)7, 0x40, out oldProt);
                    WriteProcessMemory(GetCurrentProcess(), _shiftPatchSites[i], _shiftPatchOrig[i], 7, out n);
                    VirtualProtect(_shiftPatchSites[i], (UIntPtr)7, oldProt, out oldProt);
                }
                _shiftPatchesApplied = false;
                Log?.Invoke("[VehicleMemory] Auto-shift clutch drops RESTORED");
            }
            catch (Exception ex) { Log?.Invoke($"[VehicleMemory] RestoreShiftPatches error: {ex.Message}"); }
        }

        private static System.Collections.Generic.List<IntPtr> FindPatternAll(IntPtr baseAddress, uint size, byte[] pattern, string mask)
        {
            var results = new System.Collections.Generic.List<IntPtr>();
            byte[] buffer = new byte[size];
            int bytesRead;
            if (!ReadProcessMemory(GetCurrentProcess(), baseAddress, buffer, (int)size, out bytesRead))
                return results;
            for (int i = 0; i < bytesRead - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (mask[j] == '?' || buffer[i + j] == pattern[j]) continue;
                    found = false;
                    break;
                }
                if (found) results.Add(baseAddress + i);
            }
            return results;
        }

        #endregion

        #region Entity Memory Access

        /// <summary>
        /// Get the memory address of a vehicle entity
        /// </summary>
        public static IntPtr GetVehicleAddress(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists())
                return IntPtr.Zero;

            // Use SHVDN's MemoryAddress property
            return vehicle.MemoryAddress;
        }

        #endregion

        #region Read Functions

        public static ushort GetCurrentGear(Vehicle vehicle)
        {
            if (!_available || _currentGearOffset == 0) return 0;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0;

            byte[] buffer = new byte[2];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _currentGearOffset, buffer, 2, out bytesRead);
            return BitConverter.ToUInt16(buffer, 0);
        }

        public static ushort GetNextGear(Vehicle vehicle)
        {
            if (!_available || _nextGearOffset == 0) return 0;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0;

            byte[] buffer = new byte[2];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _nextGearOffset, buffer, 2, out bytesRead);
            return BitConverter.ToUInt16(buffer, 0);
        }

        public static byte GetTopGear(Vehicle vehicle)
        {
            if (!_available || _topGearOffset == 0) return 0;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0;

            byte[] buffer = new byte[1];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _topGearOffset, buffer, 1, out bytesRead);
            return buffer[0];
        }

        /// <summary>Gear ratio for a gear (index 0 = reverse, 1 = first, ...). Used for rev-match snaps.</summary>
        public static float GetGearRatio(Vehicle vehicle, int gear)
        {
            if (!_available || _gearRatiosOffset == 0 || gear < 0 || gear > 10) return 0f;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0f;

            byte[] buffer = new byte[4];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _gearRatiosOffset + gear * 4, buffer, 4, out bytesRead);
            return BitConverter.ToSingle(buffer, 0);
        }

        public static float GetCurrentRPM(Vehicle vehicle)
        {
            if (!_available || _currentRPMOffset == 0) return 0f;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0f;

            byte[] buffer = new byte[4];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _currentRPMOffset, buffer, 4, out bytesRead);
            return BitConverter.ToSingle(buffer, 0);
        }

        public static float GetClutch(Vehicle vehicle)
        {
            if (!_available || _clutchOffset == 0) return 0f;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0f;

            byte[] buffer = new byte[4];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _clutchOffset, buffer, 4, out bytesRead);
            return BitConverter.ToSingle(buffer, 0);
        }

        public static float GetThrottle(Vehicle vehicle)
        {
            if (!_available || _throttleOffset == 0) return 0f;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return 0f;

            byte[] buffer = new byte[4];
            int bytesRead;
            ReadProcessMemory(GetCurrentProcess(), addr + _throttleOffset, buffer, 4, out bytesRead);
            return BitConverter.ToSingle(buffer, 0);
        }

        #endregion

        #region Write Functions

        /// <summary>Write a gear's ratio (index 0 = reverse-area, 2 = 1st, 3 = 2nd, ... top = N+1). Used by
        /// the NFS gearing template to space gears for a consistent RPM drop per shift.</summary>
        public static void SetGearRatio(Vehicle vehicle, int gear, float ratio)
        {
            if (!_available || _gearRatiosOffset == 0 || gear < 0 || gear > 10) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;
            byte[] buffer = BitConverter.GetBytes(ratio);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _gearRatiosOffset + gear * 4, buffer, 4, out bytesWritten);
        }

        public static void SetCurrentGear(Vehicle vehicle, ushort gear)
        {
            if (!_available || _currentGearOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;

            byte[] buffer = BitConverter.GetBytes(gear);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _currentGearOffset, buffer, 2, out bytesWritten);
        }

        public static void SetNextGear(Vehicle vehicle, ushort gear)
        {
            if (!_available || _nextGearOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;

            byte[] buffer = BitConverter.GetBytes(gear);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _nextGearOffset, buffer, 2, out bytesWritten);
        }

        /// <summary>Write the LIVE gearbox top-gear (drive gear) count (UINT8) on THIS vehicle instance — the
        /// field the game + MT actually read. (CHandlingData.nInitialDriveGears only applies on vehicle reload.)
        /// Caller must also set a ratio for any newly-added gear (e.g. ELSCTransmission.ApplyNfsGearing).</summary>
        public static void SetTopGear(Vehicle vehicle, int gears)
        {
            if (!_available || _topGearOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;
            if (gears < 1) gears = 1; if (gears > 8) gears = 8;
            byte[] buffer = { (byte)gears };
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _topGearOffset, buffer, 1, out bytesWritten);
        }

        public static void SetClutch(Vehicle vehicle, float value)
        {
            if (!_available || _clutchOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;

            byte[] buffer = BitConverter.GetBytes(value);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _clutchOffset, buffer, 4, out bytesWritten);
        }

        public static void SetThrottle(Vehicle vehicle, float value)
        {
            if (!_available || _throttleOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;

            byte[] buffer = BitConverter.GetBytes(value);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _throttleOffset, buffer, 4, out bytesWritten);
        }

        public static void SetCurrentRPM(Vehicle vehicle, float value)
        {
            if (!_available || _currentRPMOffset == 0) return;
            IntPtr addr = GetVehicleAddress(vehicle);
            if (addr == IntPtr.Zero) return;

            byte[] buffer = BitConverter.GetBytes(value);
            int bytesWritten;
            WriteProcessMemory(GetCurrentProcess(), addr + _currentRPMOffset, buffer, 4, out bytesWritten);
        }

        #endregion
    }
}
