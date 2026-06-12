using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Pattern scanner for finding memory offsets dynamically.
    /// Based on techniques from ikt's GTAVManualTransmission.
    /// </summary>
    public static class PatternScanner
    {
        public static Action<string> Log { get; set; }

        private static IntPtr _moduleBase = IntPtr.Zero;
        private static int _moduleSize = 0;
        private static bool _initialized = false;

        /// <summary>
        /// Initialize the pattern scanner with the game module
        /// </summary>
        public static bool Initialize()
        {
            if (_initialized)
                return _moduleBase != IntPtr.Zero;

            _initialized = true;

            try
            {
                // Get the main game module (GTA5.exe)
                var process = Process.GetCurrentProcess();
                var mainModule = process.MainModule;

                if (mainModule == null)
                {
                    Log?.Invoke("[PatternScanner] Failed to get main module");
                    return false;
                }

                _moduleBase = mainModule.BaseAddress;
                _moduleSize = mainModule.ModuleMemorySize;

                Log?.Invoke($"[PatternScanner] Module: {mainModule.ModuleName} Base: 0x{_moduleBase.ToInt64():X} Size: 0x{_moduleSize:X}");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[PatternScanner] Init error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find a pattern in memory
        /// Pattern format: "48 8B 47 ?? F3 44" where ?? is wildcard
        /// </summary>
        public static IntPtr FindPattern(string pattern)
        {
            if (_moduleBase == IntPtr.Zero)
            {
                if (!Initialize())
                    return IntPtr.Zero;
            }

            try
            {
                // Parse pattern string into bytes and mask
                var parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var bytes = new byte[parts.Length];
                var mask = new bool[parts.Length];

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "??" || parts[i] == "?")
                    {
                        bytes[i] = 0;
                        mask[i] = false; // wildcard
                    }
                    else
                    {
                        bytes[i] = Convert.ToByte(parts[i], 16);
                        mask[i] = true; // must match
                    }
                }

                // Scan memory
                return ScanMemory(_moduleBase, _moduleSize, bytes, mask);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[PatternScanner] FindPattern error: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Read an int32 offset from a pattern match
        /// </summary>
        public static int ReadOffset(IntPtr address, int relativeOffset)
        {
            if (address == IntPtr.Zero)
                return 0;

            try
            {
                return Marshal.ReadInt32(IntPtr.Add(address, relativeOffset));
            }
            catch
            {
                return 0;
            }
        }

        private static IntPtr ScanMemory(IntPtr start, int size, byte[] pattern, bool[] mask)
        {
            // Read memory in chunks to avoid issues
            const int chunkSize = 0x10000; // 64KB chunks
            var buffer = new byte[chunkSize + pattern.Length];

            for (int offset = 0; offset < size - pattern.Length; offset += chunkSize)
            {
                int readSize = Math.Min(chunkSize + pattern.Length, size - offset);

                try
                {
                    Marshal.Copy(IntPtr.Add(start, offset), buffer, 0, readSize);
                }
                catch
                {
                    continue; // Skip unreadable regions
                }

                // Search in buffer
                for (int i = 0; i < readSize - pattern.Length; i++)
                {
                    bool found = true;
                    for (int j = 0; j < pattern.Length; j++)
                    {
                        if (mask[j] && buffer[i + j] != pattern[j])
                        {
                            found = false;
                            break;
                        }
                    }

                    if (found)
                    {
                        return IntPtr.Add(start, offset + i);
                    }
                }
            }

            return IntPtr.Zero;
        }

        #region Known Patterns (from VStancer/Manual Transmission)

        // These patterns are from reverse engineering VStancer.asi and ikt's source code
        // They find offsets dynamically for any game version

        /// <summary>
        /// Find the wheels pointer offset in CVehicle
        /// </summary>
        public static int FindWheelsPointerOffset()
        {
            // Pattern from VStancer: looks for wheel array access
            var addr = FindPattern("4C 8B ?? ?? ?? 00 00 48 8B 40 20 48 8B 80 B0 00 00 00");
            if (addr == IntPtr.Zero)
            {
                Log?.Invoke("[PatternScanner] Wheels pointer pattern not found");
                return 0;
            }

            int offset = ReadOffset(addr, 3);
            Log?.Invoke($"[PatternScanner] Wheels pointer offset: 0x{offset:X}");
            return offset;
        }

        /// <summary>
        /// Find wheel suspension compression offset
        /// </summary>
        public static int FindWheelSuspensionOffset()
        {
            // Pattern from Manual Transmission
            var addr = FindPattern("45 0F 57 C9 F3 0F 11 83 ?? ?? 00 00");
            if (addr == IntPtr.Zero)
            {
                Log?.Invoke("[PatternScanner] Suspension pattern not found");
                return 0;
            }

            int offset = ReadOffset(addr, 8);
            Log?.Invoke($"[PatternScanner] Suspension offset: 0x{offset:X}");
            return offset;
        }

        /// <summary>
        /// Find wheel angle offset (for camber)
        /// </summary>
        public static int FindWheelAngleOffset()
        {
            // Wheel angle is typically near suspension offset
            int suspOffset = FindWheelSuspensionOffset();
            if (suspOffset == 0)
                return 0;

            // Wheel angle is usually at suspension offset + 0x8 or 0xC
            int angleOffset = suspOffset + 0x8;
            Log?.Invoke($"[PatternScanner] Wheel angle offset (estimated): 0x{angleOffset:X}");
            return angleOffset;
        }

        /// <summary>
        /// Find handling data offset in CVehicle
        /// </summary>
        public static int FindHandlingOffset()
        {
            // Pattern from VStancer
            var addr = FindPattern("88 90 ?? ?? ?? 00 0F B7 90 ?? 00 00 00");
            if (addr == IntPtr.Zero)
            {
                Log?.Invoke("[PatternScanner] Handling pattern not found");
                return 0;
            }

            int offset = ReadOffset(addr, 2);
            Log?.Invoke($"[PatternScanner] Handling offset: 0x{offset:X}");
            return offset;
        }

        #endregion
    }
}
