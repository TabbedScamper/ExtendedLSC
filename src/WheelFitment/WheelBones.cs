using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Math;
using GTA.Native;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Wheel fitment adjustments via direct memory access.
    /// Uses known offsets for GTA V Legacy.
    /// </summary>
    public static class WheelBones
    {
        public static Action<string> Log { get; set; }

        // Memory access state
        private static bool _initialized = false;
        private static bool _memoryAccessWorks = false;
        private static int _wheelsPtrOffset = 0;
        private static int _wheelSuspensionOffset = 0;
        private static int _wheelAngleOffset = 0;
        private static bool _usePatternScanning = false; // DISABLED - causes crashes

        // Wheel struct offsets (fallback if pattern scanning fails)
        private const int WHEEL_OFFSET_X = 0x20;
        private const int WHEEL_OFFSET_Y = 0x24;
        private const int WHEEL_OFFSET_Z = 0x28;

        #region Public API

        /// <summary>
        /// Get the number of wheels
        /// </summary>
        public static int GetNumWheels(Vehicle vehicle)
        {
            if (vehicle == null)
                return 0;

            try
            {
                if (!vehicle.Exists())
                    return 0;

                var wheels = vehicle.Wheels;
                return wheels?.Count ?? 4;
            }
            catch
            {
                return 4;
            }
        }

        /// <summary>
        /// Try to initialize memory access for a vehicle
        /// DISABLED: Direct memory access causes crashes. Only handling data camber works.
        /// </summary>
        public static bool TryInitializeMemoryAccess(Vehicle vehicle)
        {
            if (_initialized)
                return _memoryAccessWorks;

            _initialized = true;
            _memoryAccessWorks = false;

            // DISABLED: All direct wheel memory access causes crashes
            // Only handling data camber (via SHVDN's HandlingData) is safe
            Log?.Invoke("[WheelBones] Direct wheel memory access disabled - only camber via handling data available");
            return false;
        }

        /// <summary>
        /// Reset initialization state (call when changing vehicles)
        /// </summary>
        public static void ResetInitialization()
        {
            _initialized = false;
            _memoryAccessWorks = false;
            _wheelsPtrOffset = 0;
            _handlingAccessTested = false;
            _handlingAccessWorks = false;
            _camberCached = false;
        }

        /// <summary>
        /// Check if memory access is available
        /// </summary>
        public static bool IsMemoryAccessEnabled => _memoryAccessWorks;

        /// <summary>
        /// Get wheel position offset
        /// </summary>
        public static Vector3 GetWheelOffset(Vehicle vehicle, int wheelIndex)
        {
            if (!_memoryAccessWorks)
                return Vector3.Zero;

            try
            {
                IntPtr wheelPtr = GetWheelPointer(vehicle, wheelIndex);
                if (wheelPtr == IntPtr.Zero)
                    return Vector3.Zero;

                float x = ReadFloat(wheelPtr, WHEEL_OFFSET_X);
                float y = ReadFloat(wheelPtr, WHEEL_OFFSET_Y);
                float z = ReadFloat(wheelPtr, WHEEL_OFFSET_Z);

                return new Vector3(x, y, z);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Set wheel X offset (for track width)
        /// </summary>
        public static bool SetWheelXOffset(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!_memoryAccessWorks)
                return false;

            try
            {
                IntPtr wheelPtr = GetWheelPointer(vehicle, wheelIndex);
                if (wheelPtr == IntPtr.Zero)
                    return false;

                WriteFloat(wheelPtr, WHEEL_OFFSET_X, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set wheel Z offset (for height)
        /// </summary>
        public static bool SetWheelZOffset(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!_memoryAccessWorks)
                return false;

            try
            {
                IntPtr wheelPtr = GetWheelPointer(vehicle, wheelIndex);
                if (wheelPtr == IntPtr.Zero)
                    return false;

                WriteFloat(wheelPtr, WHEEL_OFFSET_Z, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set full wheel offset
        /// </summary>
        public static void SetWheelOffset(Vehicle vehicle, int wheelIndex, Vector3 offset)
        {
            SetWheelXOffset(vehicle, wheelIndex, offset.X);
            SetWheelZOffset(vehicle, wheelIndex, offset.Z);
        }

        // Compatibility aliases
        public static void SetWheelTrackOffset(Vehicle vehicle, int wheelIndex, float x) => SetWheelXOffset(vehicle, wheelIndex, x);
        public static void SetWheelHeightOffset(Vehicle vehicle, int wheelIndex, float z) => SetWheelZOffset(vehicle, wheelIndex, z);
        public static void InitializeOffsets() { } // No-op for compatibility

        #endregion

        #region Handling Data Camber

        // Handling data offsets for camber (more reliable than wheel struct)
        private const int HANDLING_CAMBER_FRONT = 0x034C;
        private const int HANDLING_CAMBER_REAR = 0x0350;

        // Cache original camber values for reset
        private static float _originalCamberFront = 0f;
        private static float _originalCamberRear = 0f;
        private static bool _camberCached = false;
        private static bool _handlingAccessTested = false;
        private static bool _handlingAccessWorks = false;

        /// <summary>
        /// Get the handling data memory address for a vehicle
        /// </summary>
        public static IntPtr GetHandlingDataAddress(Vehicle vehicle)
        {
            if (vehicle == null)
                return IntPtr.Zero;

            try
            {
                if (!vehicle.Exists())
                    return IntPtr.Zero;

                // Test if handling data access works (only once)
                if (!_handlingAccessTested)
                {
                    _handlingAccessTested = true;
                    var handling = vehicle.HandlingData;
                    if (handling == null)
                    {
                        Log?.Invoke("[WheelBones] HandlingData is null");
                        _handlingAccessWorks = false;
                        return IntPtr.Zero;
                    }
                    _handlingAccessWorks = true;
                    Log?.Invoke("[WheelBones] HandlingData access OK");
                }

                if (!_handlingAccessWorks)
                    return IntPtr.Zero;

                // SHVDN provides HandlingData.MemoryAddress
                return vehicle.HandlingData.MemoryAddress;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[WheelBones] GetHandlingDataAddress error: {ex.Message}");
                _handlingAccessWorks = false;
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Cache original camber values for the vehicle
        /// </summary>
        public static void CacheOriginalCamber(Vehicle vehicle)
        {
            IntPtr handlingAddr = GetHandlingDataAddress(vehicle);
            if (handlingAddr == IntPtr.Zero)
            {
                _camberCached = false;
                return;
            }

            try
            {
                _originalCamberFront = ReadFloat(handlingAddr, HANDLING_CAMBER_FRONT);
                _originalCamberRear = ReadFloat(handlingAddr, HANDLING_CAMBER_REAR);
                _camberCached = true;
                Log?.Invoke($"[WheelBones] Cached original camber: F={_originalCamberFront:F4} R={_originalCamberRear:F4}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[WheelBones] Failed to cache camber: {ex.Message}");
                _camberCached = false;
            }
        }

        /// <summary>
        /// Get front camber from handling data (in radians-like units)
        /// </summary>
        public static float GetFrontCamber(Vehicle vehicle)
        {
            IntPtr handlingAddr = GetHandlingDataAddress(vehicle);
            if (handlingAddr == IntPtr.Zero)
                return 0f;

            return ReadFloat(handlingAddr, HANDLING_CAMBER_FRONT);
        }

        /// <summary>
        /// Get rear camber from handling data (in radians-like units)
        /// </summary>
        public static float GetRearCamber(Vehicle vehicle)
        {
            IntPtr handlingAddr = GetHandlingDataAddress(vehicle);
            if (handlingAddr == IntPtr.Zero)
                return 0f;

            return ReadFloat(handlingAddr, HANDLING_CAMBER_REAR);
        }

        /// <summary>
        /// Set front camber via handling data
        /// Value is in radians-like units (roughly degrees / 22.5)
        /// Negative = negative camber (wheels tilt inward at top)
        /// </summary>
        public static bool SetFrontCamber(Vehicle vehicle, float value)
        {
            IntPtr handlingAddr = GetHandlingDataAddress(vehicle);
            if (handlingAddr == IntPtr.Zero)
                return false;

            try
            {
                // Cache original if not already cached
                if (!_camberCached)
                    CacheOriginalCamber(vehicle);

                WriteFloat(handlingAddr, HANDLING_CAMBER_FRONT, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Set rear camber via handling data
        /// </summary>
        public static bool SetRearCamber(Vehicle vehicle, float value)
        {
            IntPtr handlingAddr = GetHandlingDataAddress(vehicle);
            if (handlingAddr == IntPtr.Zero)
                return false;

            try
            {
                // Cache original if not already cached
                if (!_camberCached)
                    CacheOriginalCamber(vehicle);

                WriteFloat(handlingAddr, HANDLING_CAMBER_REAR, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reset camber to original values
        /// </summary>
        public static void ResetCamber(Vehicle vehicle)
        {
            if (!_camberCached)
                return;

            SetFrontCamber(vehicle, _originalCamberFront);
            SetRearCamber(vehicle, _originalCamberRear);
            Log?.Invoke("[WheelBones] Reset camber to original values");
        }

        /// <summary>
        /// Convert degrees to camber value (roughly degrees / 22.5)
        /// </summary>
        public static float DegreesToCamber(float degrees)
        {
            return degrees / 22.5f;
        }

        /// <summary>
        /// Convert camber value to degrees
        /// </summary>
        public static float CamberToDegrees(float camber)
        {
            return camber * 22.5f;
        }

        #endregion

        #region Private Memory Access

        private static bool ValidateWheelsPtrOffset(IntPtr vehicleAddr, int offset)
        {
            try
            {
                // Step 1: Read the wheels pointer
                IntPtr wheelsPtrAddr = IntPtr.Add(vehicleAddr, offset);
                IntPtr wheelsPtr = ReadPointer(wheelsPtrAddr);

                if (wheelsPtr == IntPtr.Zero || wheelsPtr.ToInt64() < 0x10000)
                {
                    Log?.Invoke($"[WheelBones] Offset 0x{offset:X}: wheelsPtr invalid (0x{wheelsPtr.ToInt64():X})");
                    return false;
                }

                // Step 2: Read the first wheel pointer
                IntPtr firstWheelPtr = ReadPointer(wheelsPtr);

                if (firstWheelPtr == IntPtr.Zero || firstWheelPtr.ToInt64() < 0x10000)
                {
                    Log?.Invoke($"[WheelBones] Offset 0x{offset:X}: firstWheelPtr invalid");
                    return false;
                }

                // Step 3: Try to read wheel X position and validate it's reasonable
                float wheelX = ReadFloat(firstWheelPtr, WHEEL_OFFSET_X);

                // Wheel X should be between -3 and 3 meters from center
                if (float.IsNaN(wheelX) || float.IsInfinity(wheelX) || Math.Abs(wheelX) > 5f)
                {
                    Log?.Invoke($"[WheelBones] Offset 0x{offset:X}: wheelX invalid ({wheelX})");
                    return false;
                }

                Log?.Invoke($"[WheelBones] Offset 0x{offset:X}: validated (wheelX={wheelX:F3})");
                return true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[WheelBones] Offset 0x{offset:X}: exception - {ex.Message}");
                return false;
            }
        }

        private static IntPtr GetWheelPointer(Vehicle vehicle, int wheelIndex)
        {
            if (_wheelsPtrOffset == 0)
                return IntPtr.Zero;

            try
            {
                IntPtr vehicleAddr = vehicle.MemoryAddress;
                if (vehicleAddr == IntPtr.Zero)
                    return IntPtr.Zero;

                IntPtr wheelsPtrAddr = IntPtr.Add(vehicleAddr, _wheelsPtrOffset);
                IntPtr wheelsPtr = ReadPointer(wheelsPtrAddr);

                if (wheelsPtr == IntPtr.Zero)
                    return IntPtr.Zero;

                // Each wheel pointer is 8 bytes (64-bit)
                IntPtr wheelPtrAddr = IntPtr.Add(wheelsPtr, wheelIndex * 8);
                return ReadPointer(wheelPtrAddr);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr ReadPointer(IntPtr address)
        {
            try
            {
                return Marshal.ReadIntPtr(address);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static float ReadFloat(IntPtr baseAddr, int offset)
        {
            try
            {
                IntPtr addr = IntPtr.Add(baseAddr, offset);
                int intValue = Marshal.ReadInt32(addr);
                byte[] bytes = BitConverter.GetBytes(intValue);
                return BitConverter.ToSingle(bytes, 0);
            }
            catch
            {
                return 0f;
            }
        }

        private static void WriteFloat(IntPtr baseAddr, int offset, float value)
        {
            try
            {
                IntPtr addr = IntPtr.Add(baseAddr, offset);
                byte[] bytes = BitConverter.GetBytes(value);
                int intValue = BitConverter.ToInt32(bytes, 0);
                Marshal.WriteInt32(addr, intValue);
            }
            catch
            {
                // Silent fail
            }
        }

        #endregion
    }
}
