#pragma once

/*
 * WheelFitmentAPI - Native C++ ASI for wheel memory access
 *
 * This ASI provides safe access to wheel memory structures that cannot be
 * accessed from .NET/SHVDN due to protected memory regions.
 *
 * Usage from C#:
 *   [DllImport("WheelFitmentAPI.asi")]
 *   public static extern bool WF_Initialize();
 */

#ifdef WHEELFITMENTAPI_EXPORTS
#define WF_API extern "C" __declspec(dllexport)
#else
#define WF_API extern "C" __declspec(dllimport)
#endif

// ============================================================================
// Initialization
// ============================================================================

/**
 * @brief Get the API version string
 * @return Null-terminated version string (e.g., "1.0.0")
 */
WF_API const char* WF_GetVersion();

/**
 * @brief Check if the API is initialized and ready
 * @return true if wheel memory offsets were found successfully
 */
WF_API bool WF_IsAvailable();

/**
 * @brief Get the last error message (if any)
 * @return Null-terminated error string, or empty string if no error
 */
WF_API const char* WF_GetLastError();

/**
 * @brief Get debug information about offset discovery
 * @return Null-terminated debug string
 */
WF_API const char* WF_GetDebugInfo();

/**
 * @brief Get the discovered wheels offset
 * @return Offset value, or 0 if not found
 */
WF_API int WF_GetWheelsOffset();

/**
 * @brief Get the discovered handling offset
 * @return Offset value, or 0 if not found
 */
WF_API int WF_GetHandlingOffset();

/**
 * @brief Get the current vehicle memory address (for debugging)
 * @param vehicle Script handle (ignored, uses cached address)
 * @return Memory address, or 0 if not set
 */
WF_API unsigned __int64 WF_GetVehicleAddress(int vehicle);

/**
 * @brief Set the vehicle memory address (must be called before other functions)
 * @param address The vehicle's memory address from C# (Vehicle.MemoryAddress)
 */
WF_API void WF_SetVehicleAddress(unsigned __int64 address);

// ============================================================================
// Wheel Count
// ============================================================================

/**
 * @brief Get the number of wheels on a vehicle
 * @param vehicle Script handle of the vehicle
 * @return Number of wheels (0-10), or 0 if invalid
 */
WF_API int WF_GetWheelCount(int vehicle);

// ============================================================================
// Wheel Position (Track Width / Offset)
// ============================================================================

/**
 * @brief Get wheel X position (lateral offset / track width)
 * @param vehicle Script handle of the vehicle
 * @param wheelIndex Wheel index (0 = front-left, 1 = front-right, etc.)
 * @return X position in meters, or 0.0f if failed
 */
WF_API float WF_GetWheelXOffset(int vehicle, int wheelIndex);

/**
 * @brief Set wheel X position (lateral offset / track width)
 * @param vehicle Script handle of the vehicle
 * @param wheelIndex Wheel index
 * @param value New X position in meters
 * @return true if successful
 */
WF_API bool WF_SetWheelXOffset(int vehicle, int wheelIndex, float value);

/**
 * @brief Get wheel Y position (longitudinal offset)
 */
WF_API float WF_GetWheelYOffset(int vehicle, int wheelIndex);

/**
 * @brief Set wheel Y position (longitudinal offset)
 */
WF_API bool WF_SetWheelYOffset(int vehicle, int wheelIndex, float value);

/**
 * @brief Get wheel Z position (vertical offset / ride height)
 */
WF_API float WF_GetWheelZOffset(int vehicle, int wheelIndex);

/**
 * @brief Set wheel Z position (vertical offset / ride height)
 */
WF_API bool WF_SetWheelZOffset(int vehicle, int wheelIndex, float value);

// ============================================================================
// Camber (per-wheel visual offset at 0x008)
// ============================================================================

/**
 * @brief Get front camber angle (reads from front-left wheel)
 * @param vehicle Script handle of the vehicle
 * @return Camber angle in radians
 */
WF_API float WF_GetFrontCamber(int vehicle);

/**
 * @brief Set front camber angle (applies to both front wheels)
 * @param vehicle Script handle of the vehicle
 * @param value Camber angle in radians
 * @return true if successful
 */
WF_API bool WF_SetFrontCamber(int vehicle, float value);

/**
 * @brief Get rear camber angle (reads from rear-left wheel)
 */
WF_API float WF_GetRearCamber(int vehicle);

/**
 * @brief Set rear camber angle (applies to both rear wheels)
 */
WF_API bool WF_SetRearCamber(int vehicle, float value);

// ============================================================================
// Suspension
// ============================================================================

/**
 * @brief Get suspension compression for a wheel
 * @param vehicle Script handle of the vehicle
 * @param wheelIndex Wheel index
 * @return Suspension compression value (0.0 = fully extended, 1.0 = fully compressed)
 */
WF_API float WF_GetSuspensionCompression(int vehicle, int wheelIndex);

// ============================================================================
// Visual Wheel Properties
// ============================================================================

/**
 * @brief Get tyre radius
 * @param vehicle Script handle of the vehicle
 * @param wheelIndex Wheel index
 * @return Tyre radius in meters
 */
WF_API float WF_GetTyreRadius(int vehicle, int wheelIndex);

/**
 * @brief Set tyre radius
 */
WF_API bool WF_SetTyreRadius(int vehicle, int wheelIndex, float value);

/**
 * @brief Get tyre width
 */
WF_API float WF_GetTyreWidth(int vehicle, int wheelIndex);

/**
 * @brief Set tyre width
 */
WF_API bool WF_SetTyreWidth(int vehicle, int wheelIndex, float value);

/**
 * @brief Get rim radius
 */
WF_API float WF_GetRimRadius(int vehicle, int wheelIndex);

/**
 * @brief Set rim radius
 */
WF_API bool WF_SetRimRadius(int vehicle, int wheelIndex, float value);

// ============================================================================
// Batch Operations (for performance)
// ============================================================================

/**
 * @brief Wheel fitment data structure for batch operations
 */
struct WheelFitmentData {
    float frontCamber;
    float rearCamber;
    float frontTrackWidthDelta;  // Change from original (+ = wider)
    float rearTrackWidthDelta;
    float visualHeight;          // Not yet implemented
};

/**
 * @brief Apply complete fitment settings to a vehicle
 * @param vehicle Script handle of the vehicle
 * @param data Pointer to WheelFitmentData structure
 * @return true if successful
 */
WF_API bool WF_ApplyFitment(int vehicle, const WheelFitmentData* data);

/**
 * @brief Reset wheel positions to original (cached when first accessed)
 * @param vehicle Script handle of the vehicle
 * @return true if successful
 */
WF_API bool WF_ResetToStock(int vehicle);

// ============================================================================
// Debug: Offset Testing
// ============================================================================

/**
 * @brief Get current test offset
 */
WF_API int WF_GetTestOffset();

/**
 * @brief Set test offset for probing
 */
WF_API void WF_SetTestOffset(int offset);

/**
 * @brief Write a delta to the current test offset
 */
WF_API bool WF_TestOffsetWrite(int vehicle, int wheelIndex, float delta);

/**
 * @brief Read value at current test offset
 */
WF_API float WF_ReadTestOffset(int vehicle, int wheelIndex);

// ============================================================================
// Offset Configuration (for testing tire width/radius)
// ============================================================================

/**
 * @brief Set tyre radius offset for testing
 */
WF_API void WF_SetTyreRadiusOffset(int offset);

/**
 * @brief Set tyre width offset for testing
 */
WF_API void WF_SetTyreWidthOffset(int offset);

/**
 * @brief Set rim radius offset for testing
 */
WF_API void WF_SetRimRadiusOffset(int offset);

// ============================================================================
// Pattern Scanning
// ============================================================================

/**
 * @brief Perform pattern scan to find visual wheel offsets
 * @return true if scan completed
 */
WF_API bool WF_PerformPatternScan();

/**
 * @brief Get pattern scan status
 * @return 0=not started, 1=complete with results, 2=complete no results
 */
WF_API int WF_GetPatternScanStatus();

/**
 * @brief Get found StreamRender wheel width offset
 */
WF_API int WF_GetStreamRenderWheelWidthOffset();

/**
 * @brief Get found StreamRender wheel size offset
 */
WF_API int WF_GetStreamRenderWheelSizeOffset();

/**
 * @brief Get found DrawHandler pointer offset
 */
WF_API int WF_GetDrawHandlerPtrOffset();

// ============================================================================
// Visual Wheel Properties (via DrawHandler)
// ============================================================================

/**
 * @brief Get visual wheel size (diameter)
 */
WF_API float WF_GetVisualWheelSize(int vehicle);

/**
 * @brief Set visual wheel size (diameter)
 */
WF_API bool WF_SetVisualWheelSize(int vehicle, float value);

/**
 * @brief Get visual wheel width
 */
WF_API float WF_GetVisualWheelWidth(int vehicle);

/**
 * @brief Set visual wheel width
 */
WF_API bool WF_SetVisualWheelWidth(int vehicle, float value);

/**
 * @brief Probe a specific offset from draw handler (for testing)
 */
WF_API float WF_ProbeDrawHandlerOffset(int vehicle, int offset);

/**
 * @brief Write to a specific offset from draw handler (for testing)
 */
WF_API bool WF_WriteDrawHandlerOffset(int vehicle, int offset, float value);

/**
 * @brief Get the raw DrawHandler pointer value for debugging
 */
WF_API unsigned long long WF_GetDrawHandlerPointer(int vehicle);

/**
 * @brief Get the StreamRenderGfx pointer for debugging
 */
WF_API unsigned long long WF_GetStreamRenderGfxPointer(int vehicle);

/**
 * @brief Get full pointer chain debug info
 */
WF_API const char* WF_GetPointerChainDebug(int vehicle);

/**
 * @brief Get static pointer chain debug info (VStancer-style offsets)
 */
WF_API const char* WF_GetStaticChainDebug();

// ============================================================================
// Multi-Chain Scanner
// ============================================================================

/**
 * @brief Scan all known pointer chains and find working ones
 * @return Debug string with scan results
 */
WF_API const char* WF_ScanAllChains();

/**
 * @brief Get visual wheel size using the discovered working chain
 */
WF_API float WF_GetVisualWheelSizeViaChain(int vehicle);

/**
 * @brief Set visual wheel size using the discovered working chain
 */
WF_API bool WF_SetVisualWheelSizeViaChain(int vehicle, float value);

/**
 * @brief Get the index of the working chain (-1 if none found)
 */
WF_API int WF_GetWorkingChainIndex();

/**
 * @brief Probe offset directly from vehicle structure (bypass DrawHandler)
 */
WF_API float WF_ProbeVehicleOffset(int vehicle, int offset);

/**
 * @brief Write directly to vehicle structure offset
 */
WF_API bool WF_WriteVehicleOffset(int vehicle, int offset, float value);

// ============================================================================
// Wheel Structure Scanning
// ============================================================================

/**
 * @brief Scan wheel structure for values in wheel dimension range (0.1-1.0)
 * @param vehicle Script handle
 * @param wheelIndex Wheel index to scan
 * @return String with found offsets and values
 */
WF_API const char* WF_ScanWheelStructure(int vehicle, int wheelIndex);

/**
 * @brief Scan StreamRenderGfx structure for wheel dimension values
 */
WF_API const char* WF_ScanStreamRenderGfx(int vehicle);

/**
 * @brief Probe StreamRenderGfx at specific offset
 */
WF_API float WF_ProbeStreamRenderOffset(int vehicle, int offset);

/**
 * @brief Write to StreamRenderGfx at specific offset
 */
WF_API bool WF_WriteStreamRenderOffset(int vehicle, int offset, float value);

/**
 * @brief Read float at specific wheel structure offset
 */
WF_API float WF_ProbeWheelOffset(int vehicle, int wheelIndex, int offset);

/**
 * @brief Write float to specific wheel structure offset
 */
WF_API bool WF_WriteWheelOffset(int vehicle, int wheelIndex, int offset, float value);
