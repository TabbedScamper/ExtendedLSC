using System;
using System.Runtime.InteropServices;
using GTA;

namespace ExtendedLSC.WheelFitment
{
    /// <summary>
    /// Native wrapper for WheelFitmentAPI.asi
    /// Provides access to wheel memory that cannot be safely accessed from .NET
    /// </summary>
    public static class WheelFitmentNative
    {
        private const string DLL_NAME = "WheelFitmentAPI.asi";

        private static bool _initialized = false;
        private static bool _available = false;
        private static string _initError = null;

        public static Action<string> Log { get; set; }

        #region Native Imports

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_GetVersion();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_IsAvailable();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_GetLastError();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_GetDebugInfo();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetWheelsOffset();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetHandlingOffset();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong WF_GetVehicleAddress(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WF_SetVehicleAddress(ulong address);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetWheelCount(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetWheelXOffset(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetWheelXOffset(int vehicle, int wheelIndex, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetWheelYOffset(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetWheelYOffset(int vehicle, int wheelIndex, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetWheelZOffset(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetWheelZOffset(int vehicle, int wheelIndex, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetFrontCamber(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetFrontCamber(int vehicle, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetRearCamber(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetRearCamber(int vehicle, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetSuspensionCompression(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetTyreRadius(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetTyreRadius(int vehicle, int wheelIndex, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetTyreWidth(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetTyreWidth(int vehicle, int wheelIndex, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetRimRadius(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetRimRadius(int vehicle, int wheelIndex, float value);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWheelFitmentData
        {
            public float frontCamber;
            public float rearCamber;
            public float frontTrackWidthDelta;
            public float rearTrackWidthDelta;
            public float visualHeight;
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_ApplyFitment(int vehicle, ref NativeWheelFitmentData data);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_ResetToStock(int vehicle);

        // Debug offset testing
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetTestOffset();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WF_SetTestOffset(int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_TestOffsetWrite(int vehicle, int wheelIndex, float delta);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_ReadTestOffset(int vehicle, int wheelIndex);

        // Offset configuration for testing tire width/radius
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WF_SetTyreRadiusOffset(int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WF_SetTyreWidthOffset(int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WF_SetRimRadiusOffset(int offset);

        // Pattern scanning
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_PerformPatternScan();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetPatternScanStatus();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetStreamRenderWheelWidthOffset();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetStreamRenderWheelSizeOffset();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetDrawHandlerPtrOffset();

        // Visual wheel properties
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetVisualWheelSize(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetVisualWheelSize(int vehicle, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetVisualWheelWidth(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetVisualWheelWidth(int vehicle, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_ProbeDrawHandlerOffset(int vehicle, int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_WriteDrawHandlerOffset(int vehicle, int offset, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong WF_GetDrawHandlerPointer(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong WF_GetStreamRenderGfxPointer(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_GetPointerChainDebug(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_ProbeVehicleOffset(int vehicle, int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_WriteVehicleOffset(int vehicle, int offset, float value);

        // Wheel structure scanning
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_ScanWheelStructure(int vehicle, int wheelIndex);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_ScanStreamRenderGfx(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_GetStaticChainDebug();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WF_ScanAllChains();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_GetVisualWheelSizeViaChain(int vehicle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_SetVisualWheelSizeViaChain(int vehicle, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WF_GetWorkingChainIndex();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_ProbeStreamRenderOffset(int vehicle, int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_WriteStreamRenderOffset(int vehicle, int offset, float value);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern float WF_ProbeWheelOffset(int vehicle, int wheelIndex, int offset);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool WF_WriteWheelOffset(int vehicle, int wheelIndex, int offset, float value);

        #endregion

        #region Properties

        /// <summary>
        /// Check if the native API is available
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (!_initialized)
                    Initialize();
                return _available;
            }
        }

        /// <summary>
        /// Get the API version string
        /// </summary>
        public static string Version
        {
            get
            {
                if (!IsAvailable) return "N/A";
                try
                {
                    IntPtr ptr = WF_GetVersion();
                    return Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
                }
                catch
                {
                    return "Error";
                }
            }
        }

        /// <summary>
        /// Get the last error message
        /// </summary>
        public static string LastError
        {
            get
            {
                if (!_initialized) return _initError ?? "Not initialized";
                try
                {
                    IntPtr ptr = WF_GetLastError();
                    return Marshal.PtrToStringAnsi(ptr) ?? "";
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }
        }

        /// <summary>
        /// Get debug information about offset discovery
        /// </summary>
        public static string DebugInfo
        {
            get
            {
                if (!IsAvailable) return "N/A";
                try
                {
                    IntPtr ptr = WF_GetDebugInfo();
                    return Marshal.PtrToStringAnsi(ptr) ?? "";
                }
                catch
                {
                    return "Error";
                }
            }
        }

        /// <summary>
        /// Get the discovered wheels offset (for debugging)
        /// </summary>
        public static int WheelsOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try
                {
                    return WF_GetWheelsOffset();
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Get the discovered handling offset (for debugging)
        /// </summary>
        public static int HandlingOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try
                {
                    return WF_GetHandlingOffset();
                }
                catch
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Get the vehicle address as seen by the native API (for debugging)
        /// </summary>
        public static ulong GetNativeVehicleAddress(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0;
            try
            {
                return WF_GetVehicleAddress(vehicle.Handle);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Set the current vehicle for native operations (must be called before other functions)
        /// </summary>
        public static bool SetCurrentVehicle(Vehicle vehicle)
        {
            if (!IsAvailable)
            {
                Log?.Invoke("[WheelFitmentNative] SetCurrentVehicle: API not available");
                return false;
            }
            if (vehicle == null || !vehicle.Exists())
            {
                Log?.Invoke("[WheelFitmentNative] SetCurrentVehicle: Invalid vehicle");
                return false;
            }
            try
            {
                ulong addr = (ulong)vehicle.MemoryAddress.ToInt64();
                Log?.Invoke($"[WheelFitmentNative] SetCurrentVehicle: Calling WF_SetVehicleAddress(0x{addr:X})");
                WF_SetVehicleAddress(addr);
                Log?.Invoke("[WheelFitmentNative] SetCurrentVehicle: Success");
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Log?.Invoke($"[WheelFitmentNative] SetCurrentVehicle: Function not found in ASI - {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[WheelFitmentNative] SetCurrentVehicle: Error - {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the native API
        /// </summary>
        public static bool Initialize()
        {
            if (_initialized) return _available;
            _initialized = true;

            try
            {
                Log?.Invoke("[WheelFitmentNative] Checking for WheelFitmentAPI.asi...");

                // Try to call the API - this will throw if DLL not found
                _available = WF_IsAvailable();

                if (_available)
                {
                    Log?.Invoke($"[WheelFitmentNative] API available, version: {Version}");
                }
                else
                {
                    _initError = LastError;
                    Log?.Invoke($"[WheelFitmentNative] API not available: {_initError}");
                }
            }
            catch (DllNotFoundException)
            {
                _initError = "WheelFitmentAPI.asi not found in scripts folder";
                Log?.Invoke($"[WheelFitmentNative] {_initError}");
                _available = false;
            }
            catch (Exception ex)
            {
                _initError = $"Failed to load API: {ex.Message}";
                Log?.Invoke($"[WheelFitmentNative] {_initError}");
                _available = false;
            }

            return _available;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get the number of wheels on a vehicle
        /// </summary>
        public static int GetWheelCount(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0;
            return WF_GetWheelCount(vehicle.Handle);
        }

        /// <summary>
        /// Get wheel X offset (lateral position / track width)
        /// </summary>
        public static float GetWheelXOffset(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetWheelXOffset(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set wheel X offset (lateral position / track width)
        /// </summary>
        public static bool SetWheelXOffset(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetWheelXOffset(vehicle.Handle, wheelIndex, value);
        }

        /// <summary>
        /// Get wheel Y offset (longitudinal position)
        /// </summary>
        public static float GetWheelYOffset(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetWheelYOffset(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set wheel Y offset (longitudinal position)
        /// </summary>
        public static bool SetWheelYOffset(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetWheelYOffset(vehicle.Handle, wheelIndex, value);
        }

        /// <summary>
        /// Get wheel Z offset (vertical position / ride height)
        /// </summary>
        public static float GetWheelZOffset(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetWheelZOffset(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set wheel Z offset (vertical position / ride height)
        /// </summary>
        public static bool SetWheelZOffset(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetWheelZOffset(vehicle.Handle, wheelIndex, value);
        }

        /// <summary>
        /// Get front camber angle from handling data
        /// </summary>
        public static float GetFrontCamber(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetFrontCamber(vehicle.Handle);
        }

        /// <summary>
        /// Set front camber angle in handling data
        /// </summary>
        public static bool SetFrontCamber(Vehicle vehicle, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetFrontCamber(vehicle.Handle, value);
        }

        /// <summary>
        /// Get rear camber angle from handling data
        /// </summary>
        public static float GetRearCamber(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetRearCamber(vehicle.Handle);
        }

        /// <summary>
        /// Set rear camber angle in handling data
        /// </summary>
        public static bool SetRearCamber(Vehicle vehicle, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetRearCamber(vehicle.Handle, value);
        }

        /// <summary>
        /// Get suspension compression for a wheel
        /// </summary>
        public static float GetSuspensionCompression(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetSuspensionCompression(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Get tyre radius
        /// </summary>
        public static float GetTyreRadius(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetTyreRadius(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set tyre radius
        /// </summary>
        public static bool SetTyreRadius(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetTyreRadius(vehicle.Handle, wheelIndex, value);
        }

        /// <summary>
        /// Get tyre width
        /// </summary>
        public static float GetTyreWidth(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetTyreWidth(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set tyre width
        /// </summary>
        public static bool SetTyreWidth(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetTyreWidth(vehicle.Handle, wheelIndex, value);
        }

        /// <summary>
        /// Get rim radius
        /// </summary>
        public static float GetRimRadius(Vehicle vehicle, int wheelIndex)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            return WF_GetRimRadius(vehicle.Handle, wheelIndex);
        }

        /// <summary>
        /// Set rim radius
        /// </summary>
        public static bool SetRimRadius(Vehicle vehicle, int wheelIndex, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_SetRimRadius(vehicle.Handle, wheelIndex, value);
        }

        #endregion

        #region High-Level API

        /// <summary>
        /// Fitment data structure for applying complete wheel fitment
        /// </summary>
        public struct FitmentData
        {
            public float FrontCamber;
            public float RearCamber;
            public float FrontTrackWidthDelta;
            public float RearTrackWidthDelta;
            public float VisualHeight;
        }

        /// <summary>
        /// Apply complete wheel fitment settings
        /// </summary>
        public static bool ApplyFitment(Vehicle vehicle, FitmentData data)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;

            var nativeData = new NativeWheelFitmentData
            {
                frontCamber = data.FrontCamber,
                rearCamber = data.RearCamber,
                frontTrackWidthDelta = data.FrontTrackWidthDelta,
                rearTrackWidthDelta = data.RearTrackWidthDelta,
                visualHeight = data.VisualHeight
            };

            return WF_ApplyFitment(vehicle.Handle, ref nativeData);
        }

        /// <summary>
        /// Reset wheel positions to stock
        /// </summary>
        public static bool ResetToStock(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            return WF_ResetToStock(vehicle.Handle);
        }

        #endregion

        #region Debug Offset Testing

        /// <summary>
        /// Get the current test offset
        /// </summary>
        public static int TestOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try { return WF_GetTestOffset(); }
                catch { return 0; }
            }
            set
            {
                if (!IsAvailable) return;
                try { WF_SetTestOffset(value); }
                catch { }
            }
        }

        /// <summary>
        /// Write a delta to the test offset on all wheels
        /// </summary>
        public static bool TestOffsetWriteAll(Vehicle vehicle, float delta)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            bool result = true;
            for (int i = 0; i < 4; i++)
            {
                result &= WF_TestOffsetWrite(vehicle.Handle, i, delta);
            }
            return result;
        }

        /// <summary>
        /// Read value at test offset for wheel 0
        /// </summary>
        public static float ReadTestOffset(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            return WF_ReadTestOffset(vehicle.Handle, 0);
        }

        /// <summary>
        /// Set the tyre radius offset (for testing different memory locations)
        /// </summary>
        public static void SetTyreRadiusOffset(int offset)
        {
            if (!IsAvailable) return;
            try { WF_SetTyreRadiusOffset(offset); }
            catch { }
        }

        /// <summary>
        /// Set the tyre width offset (for testing different memory locations)
        /// </summary>
        public static void SetTyreWidthOffset(int offset)
        {
            if (!IsAvailable) return;
            try { WF_SetTyreWidthOffset(offset); }
            catch { }
        }

        /// <summary>
        /// Set the rim radius offset (for testing different memory locations)
        /// </summary>
        public static void SetRimRadiusOffset(int offset)
        {
            if (!IsAvailable) return;
            try { WF_SetRimRadiusOffset(offset); }
            catch { }
        }

        #endregion

        #region Pattern Scanning

        /// <summary>
        /// Trigger pattern scan for visual wheel offsets
        /// </summary>
        public static bool PerformPatternScan()
        {
            if (!IsAvailable) return false;
            try { return WF_PerformPatternScan(); }
            catch { return false; }
        }

        /// <summary>
        /// Get pattern scan status (0=not started, 1=complete with results, 2=complete no results)
        /// </summary>
        public static int PatternScanStatus
        {
            get
            {
                if (!IsAvailable) return 0;
                try { return WF_GetPatternScanStatus(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Get found draw handler pointer offset
        /// </summary>
        public static int DrawHandlerPtrOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try { return WF_GetDrawHandlerPtrOffset(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Get found StreamRender wheel size offset (from pattern scan)
        /// </summary>
        public static int StreamRenderWheelSizeOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try { return WF_GetStreamRenderWheelSizeOffset(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Get found StreamRender wheel width offset (from pattern scan)
        /// </summary>
        public static int StreamRenderWheelWidthOffset
        {
            get
            {
                if (!IsAvailable) return 0;
                try { return WF_GetStreamRenderWheelWidthOffset(); }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Get visual wheel size (diameter) - uses pattern-scanned offsets
        /// </summary>
        public static float GetVisualWheelSize(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_GetVisualWheelSize(vehicle.Handle); }
            catch { return 0f; }
        }

        /// <summary>
        /// Set visual wheel size (diameter)
        /// </summary>
        public static bool SetVisualWheelSize(Vehicle vehicle, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_SetVisualWheelSize(vehicle.Handle, value); }
            catch { return false; }
        }

        /// <summary>
        /// Get visual wheel width
        /// </summary>
        public static float GetVisualWheelWidth(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_GetVisualWheelWidth(vehicle.Handle); }
            catch { return 0f; }
        }

        /// <summary>
        /// Set visual wheel width
        /// </summary>
        public static bool SetVisualWheelWidth(Vehicle vehicle, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_SetVisualWheelWidth(vehicle.Handle, value); }
            catch { return false; }
        }

        /// <summary>
        /// Probe a specific offset from draw handler (for testing)
        /// </summary>
        public static float ProbeDrawHandlerOffset(Vehicle vehicle, int offset)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_ProbeDrawHandlerOffset(vehicle.Handle, offset); }
            catch { return 0f; }
        }

        /// <summary>
        /// Write to a specific offset from draw handler (for testing)
        /// </summary>
        public static bool WriteDrawHandlerOffset(Vehicle vehicle, int offset, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_WriteDrawHandlerOffset(vehicle.Handle, offset, value); }
            catch { return false; }
        }

        /// <summary>
        /// Get the raw DrawHandler pointer value for debugging
        /// </summary>
        public static ulong GetDrawHandlerPointer(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0;
            SetCurrentVehicle(vehicle);
            try { return WF_GetDrawHandlerPointer(vehicle.Handle); }
            catch { return 0; }
        }

        /// <summary>
        /// Get the StreamRenderGfx pointer value for debugging
        /// </summary>
        public static ulong GetStreamRenderGfxPointer(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0;
            SetCurrentVehicle(vehicle);
            try { return WF_GetStreamRenderGfxPointer(vehicle.Handle); }
            catch { return 0; }
        }

        /// <summary>
        /// Get full pointer chain debug info
        /// </summary>
        public static string GetPointerChainDebug(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return "Not available";
            SetCurrentVehicle(vehicle);
            try
            {
                IntPtr ptr = WF_GetPointerChainDebug(vehicle.Handle);
                return Marshal.PtrToStringAnsi(ptr) ?? "Error";
            }
            catch { return "Exception"; }
        }

        /// <summary>
        /// Probe offset directly from vehicle structure (bypass DrawHandler)
        /// </summary>
        public static float ProbeVehicleOffset(Vehicle vehicle, int offset)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_ProbeVehicleOffset(vehicle.Handle, offset); }
            catch { return 0f; }
        }

        /// <summary>
        /// Write directly to vehicle structure offset
        /// </summary>
        public static bool WriteVehicleOffset(Vehicle vehicle, int offset, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_WriteVehicleOffset(vehicle.Handle, offset, value); }
            catch { return false; }
        }

        /// <summary>
        /// Scan wheel structure for values in wheel dimension range (0.1-1.0)
        /// Returns a string listing offsets and values
        /// </summary>
        public static string ScanWheelStructure(Vehicle vehicle, int wheelIndex = 0)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return "Not available";
            SetCurrentVehicle(vehicle);
            try
            {
                IntPtr ptr = WF_ScanWheelStructure(vehicle.Handle, wheelIndex);
                return Marshal.PtrToStringAnsi(ptr) ?? "Error";
            }
            catch { return "Exception"; }
        }

        /// <summary>
        /// Scan StreamRenderGfx structure for wheel dimension values
        /// </summary>
        public static string ScanStreamRenderGfx(Vehicle vehicle)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return "Not available";
            SetCurrentVehicle(vehicle);
            try
            {
                IntPtr ptr = WF_ScanStreamRenderGfx(vehicle.Handle);
                return Marshal.PtrToStringAnsi(ptr) ?? "Error";
            }
            catch { return "Exception"; }
        }

        /// <summary>
        /// Get static pointer chain debug info
        /// </summary>
        public static string GetStaticChainDebug()
        {
            if (!IsAvailable) return "Not available";
            try
            {
                IntPtr ptr = WF_GetStaticChainDebug();
                return Marshal.PtrToStringAnsi(ptr) ?? "Error";
            }
            catch { return "Exception"; }
        }

        /// <summary>
        /// Scan all known pointer chains to find working ones
        /// </summary>
        public static string ScanAllChains()
        {
            if (!IsAvailable) return "Not available";
            try
            {
                IntPtr ptr = WF_ScanAllChains();
                return Marshal.PtrToStringAnsi(ptr) ?? "Error";
            }
            catch { return "Exception"; }
        }

        /// <summary>
        /// Get visual wheel size using the discovered working chain
        /// </summary>
        public static float GetVisualWheelSizeViaChain(Vehicle vehicle)
        {
            if (!IsAvailable) return 0f;
            try { return WF_GetVisualWheelSizeViaChain(vehicle?.Handle ?? 0); }
            catch { return 0f; }
        }

        /// <summary>
        /// Set visual wheel size using the discovered working chain
        /// </summary>
        public static bool SetVisualWheelSizeViaChain(Vehicle vehicle, float value)
        {
            if (!IsAvailable) return false;
            try { return WF_SetVisualWheelSizeViaChain(vehicle?.Handle ?? 0, value); }
            catch { return false; }
        }

        /// <summary>
        /// Get the index of the working chain (-1 if none found)
        /// </summary>
        public static int WorkingChainIndex
        {
            get
            {
                if (!IsAvailable) return -1;
                try { return WF_GetWorkingChainIndex(); }
                catch { return -1; }
            }
        }

        /// <summary>
        /// Probe StreamRenderGfx at specific offset
        /// </summary>
        public static float ProbeStreamRenderOffset(Vehicle vehicle, int offset)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_ProbeStreamRenderOffset(vehicle.Handle, offset); }
            catch { return 0f; }
        }

        /// <summary>
        /// Write to StreamRenderGfx at specific offset
        /// </summary>
        public static bool WriteStreamRenderOffset(Vehicle vehicle, int offset, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_WriteStreamRenderOffset(vehicle.Handle, offset, value); }
            catch { return false; }
        }

        /// <summary>
        /// Read float at specific wheel structure offset
        /// </summary>
        public static float ProbeWheelOffset(Vehicle vehicle, int wheelIndex, int offset)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return 0f;
            SetCurrentVehicle(vehicle);
            try { return WF_ProbeWheelOffset(vehicle.Handle, wheelIndex, offset); }
            catch { return 0f; }
        }

        /// <summary>
        /// Write float to specific wheel structure offset
        /// </summary>
        public static bool WriteWheelOffset(Vehicle vehicle, int wheelIndex, int offset, float value)
        {
            if (!IsAvailable || vehicle == null || !vehicle.Exists()) return false;
            SetCurrentVehicle(vehicle);
            try { return WF_WriteWheelOffset(vehicle.Handle, wheelIndex, offset, value); }
            catch { return false; }
        }

        #endregion
    }
}
