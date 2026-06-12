/*
 * WheelFitmentAPI - Native C++ ASI for wheel memory access
 *
 * Build with ScriptHookV SDK
 */

#define WHEELFITMENTAPI_EXPORTS
#include "WheelFitmentAPI.h"

#include <windows.h>
#include <psapi.h>
#include <string>
#include <unordered_map>
#include <vector>
#include <cstdint>
#include <sstream>

// ScriptHookV SDK
#include "main.h"
#include "natives.h"

// ============================================================================
// Version and State
// ============================================================================

static const char* VERSION = "1.3.0";  // Added pattern scanning
static std::string g_lastError;
static std::string g_debugInfo;
static bool g_initialized = false;
static bool g_available = false;

// ============================================================================
// Memory Offsets
// ============================================================================

static int g_wheelsOffset = 0;           // CVehicle + offset = pointer to wheel array
static int g_numWheelsOffset = 0;        // CVehicle + offset = number of wheels
static int g_handlingOffset = 0;         // CVehicle + offset = pointer to handling data

// Wheel structure offsets - ALL CONFIRMED WORKING via testing!
static int g_wheelCamberOffset = 0x008;  // CWheel + offset = Camber angle (wheel tilt)
static int g_wheelToeOffset = 0x004;     // CWheel + offset = Toe angle
static int g_wheelPosXOffset = 0x030;    // CWheel + offset = X position (track width - left/right)
static int g_wheelPosYOffset = 0x034;    // CWheel + offset = Y position (front/back)
static int g_wheelPosZOffset = 0x038;    // CWheel + offset = Z position (height - up/down)

// Debug: current test offset for probing
static int g_testOffset = 0x020;
static float g_testOriginalValue = 0.0f;
static bool g_testActive = false;

// Handling data offsets
static int g_camberFrontOffset = 0x034C;
static int g_camberRearOffset = 0x0350;

// Known wheel array offsets for different versions - expanded range for 2026+ versions
static const int KNOWN_WHEELS_OFFSETS[] = {
    // 2026 Enhanced Edition range (higher offsets)
    0xD80, 0xD78, 0xD70, 0xD68, 0xD60, 0xD58, 0xD50, 0xD48, 0xD40, 0xD38,
    0xD30, 0xD28, 0xD20, 0xD18, 0xD10, 0xD08, 0xD00, 0xCF8, 0xCF0, 0xCE8,
    0xCE0, 0xCD8, 0xCD0, 0xCC8, 0xCC0, 0xCB8, 0xCB0, 0xCA8, 0xCA0, 0xC98,
    0xC90, 0xC88, 0xC80, 0xC78, 0xC70, 0xC68, 0xC60, 0xC58, 0xC50,
    // 2023-2025 range
    0xC48, 0xC40, 0xC38, 0xC30, 0xC28, 0xC20, 0xC18, 0xC10, 0xC08, 0xC00,
    0xB78, 0xB70, 0xB68, 0xB60, 0xB58, 0xB50, 0xB48, 0xB40, 0xB38, 0xB30,
    0xAF8, 0xAF0, 0xAE8, 0xAE0, 0xAD8, 0xAD0, 0xAC8, 0xAC0,
    // Older versions
    0xA88, 0xA80, 0xA78, 0xA70, 0xA68, 0xA60, 0xA58, 0xA50
};

// Known handling offsets - expanded range
static const int KNOWN_HANDLING_OFFSETS[] = {
    // 2026 Enhanced Edition range
    0xA58, 0xA50, 0xA48, 0xA40, 0xA38, 0xA30, 0xA28, 0xA20, 0xA18, 0xA10,
    0xA08, 0xA00, 0x9F8, 0x9F0, 0x9E8, 0x9E0, 0x9D8, 0x9D0, 0x9C8, 0x9C0,
    0x9B8, 0x9B0, 0x9A8, 0x9A0, 0x998, 0x990, 0x988, 0x980, 0x978, 0x970,
    0x968, 0x960, 0x958, 0x950, 0x948, 0x940, 0x938, 0x930, 0x928, 0x920, 0x918, 0x910,
    0x8F8, 0x8F0, 0x8E8, 0x8E0, 0x8D8, 0x8D0, 0x8C8, 0x8C0, 0x8B8, 0x8B0,
    0x878, 0x870, 0x868, 0x860, 0x858, 0x850, 0x848, 0x840, 0x838, 0x830
};

// ============================================================================
// Pattern Scanning - Found offsets for visual wheel properties
// ============================================================================

static bool g_patternScanComplete = false;
static uintptr_t g_moduleBase = 0;
static size_t g_moduleSize = 0;

// ============================================================================
// VStancer Offsets (discovered via Cheat Engine pointer scan)
// All offsets from: StreamRenderGfx -> [+0x8] -> innerPtr + offset
// ============================================================================
constexpr int VSTANCER_INNER_PTR_OFFSET = 0x8;       // Offset from StreamRenderGfx to inner pointer

// Wheel Fitment Offsets (15 consecutive floats)
constexpr int OFFSET_FRONT_CAMBER           = 0x750;
constexpr int OFFSET_FRONT_TRACK_WIDTH      = 0x754;
constexpr int OFFSET_SUSPENSION_FRONT_HEIGHT = 0x758;
constexpr int OFFSET_REAR_CAMBER            = 0x75C;
constexpr int OFFSET_REAR_TRACK_WIDTH       = 0x760;
constexpr int OFFSET_SUSPENSION_REAR_HEIGHT = 0x764;
constexpr int OFFSET_VISUAL_HEIGHT          = 0x768;
constexpr int OFFSET_PHYS_FRONT_TIRE_RADIUS = 0x76C;
constexpr int OFFSET_PHYS_FRONT_TIRE_WIDTH  = 0x770;
constexpr int OFFSET_PHYS_FRONT_RIM_RADIUS  = 0x774;
constexpr int OFFSET_PHYS_REAR_TIRE_RADIUS  = 0x778;
constexpr int OFFSET_PHYS_REAR_TIRE_WIDTH   = 0x77C;
constexpr int OFFSET_PHYS_REAR_RIM_RADIUS   = 0x780;
constexpr int OFFSET_VISUAL_WHEEL_SIZE      = 0x784;
constexpr int OFFSET_VISUAL_WHEEL_WIDTH     = 0x788;

// Visual wheel render offsets (found via pattern scanning - legacy, now using Cheat Engine offsets)
static int g_visualWheelSizeOffset = 0;    // Offset from some base to wheel size
static int g_visualWheelWidthOffset = 0;   // Offset from some base to wheel width

// Pointers found via pattern scanning
static uintptr_t g_streamRenderGfxPtr = 0;
static int g_streamRenderWheelWidthOffset = 0;
static int g_streamRenderWheelSizeOffset = 0;

// DrawHandler pointer offset (for visual modifications)
static int g_drawHandlerPtrOffset = 0;

// ============================================================================
// Pattern Scanning Implementation
// ============================================================================

// Fixed-size pattern structure to avoid C++ object unwinding issues with SEH
#define MAX_PATTERN_LEN 32

struct PatternData {
    uint8_t bytes[MAX_PATTERN_LEN];
    bool wildcards[MAX_PATTERN_LEN];
    size_t length;
};

static void ParsePattern(const char* pattern, PatternData* out) {
    out->length = 0;
    const char* p = pattern;

    while (*p && out->length < MAX_PATTERN_LEN) {
        // Skip spaces
        while (*p == ' ') p++;
        if (!*p) break;

        if (*p == '?') {
            out->bytes[out->length] = 0;
            out->wildcards[out->length] = true;
            out->length++;
            p++;
            if (*p == '?') p++;  // Handle ?? format
        }
        else {
            char hex[3] = { p[0], p[1], 0 };
            out->bytes[out->length] = (uint8_t)strtol(hex, nullptr, 16);
            out->wildcards[out->length] = false;
            out->length++;
            p += 2;
        }
    }
}

// Inner scan function that can use SEH (no C++ objects)
static uintptr_t FindPatternInner(uintptr_t start, size_t size, const PatternData* pat) {
    __try {
        for (size_t i = 0; i < size - pat->length; i++) {
            bool found = true;
            for (size_t j = 0; j < pat->length; j++) {
                if (!pat->wildcards[j]) {
                    uint8_t mem = *(uint8_t*)(start + i + j);
                    if (mem != pat->bytes[j]) {
                        found = false;
                        break;
                    }
                }
            }
            if (found) {
                return start + i;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        // Memory access exception during scan
    }
    return 0;
}

static uintptr_t FindPattern(uintptr_t start, size_t size, const char* pattern) {
    PatternData pat;
    ParsePattern(pattern, &pat);
    if (pat.length == 0) return 0;
    return FindPatternInner(start, size, &pat);
}

// Read a relative offset from pattern match and resolve to absolute address
static uintptr_t ResolveRelativeAddress(uintptr_t instructionAddr, int offsetPosition, int instructionSize) {
    __try {
        int32_t relOffset = *(int32_t*)(instructionAddr + offsetPosition);
        return instructionAddr + instructionSize + relOffset;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static bool GetModuleInfo() {
    if (g_moduleBase != 0) return true;

    HMODULE hModule = GetModuleHandleA("GTA5.exe");
    if (!hModule) {
        hModule = GetModuleHandleA(nullptr);  // Main executable
    }
    if (!hModule) return false;

    MODULEINFO modInfo;
    if (GetModuleInformation(GetCurrentProcess(), hModule, &modInfo, sizeof(modInfo))) {
        g_moduleBase = (uintptr_t)modInfo.lpBaseOfDll;
        g_moduleSize = modInfo.SizeOfImage;
        return true;
    }
    return false;
}

// Additional offsets from FiveM source
static int g_streamRenderGfxPtrOffset = 0;  // Offset from DrawHandler to StreamRenderGfx

static void PerformPatternScan() {
    if (g_patternScanComplete) return;
    g_patternScanComplete = true;

    if (!GetModuleInfo()) {
        g_debugInfo += "Pattern scan: Failed to get module info\n";
        return;
    }

    std::stringstream debug;
    debug << "Pattern scan: Module base=0x" << std::hex << g_moduleBase
          << " size=0x" << g_moduleSize << std::dec << "\n";

    // ========================================================================
    // FiveM Patterns (from citizenfx/fivem VehicleExtraNatives.cpp)
    // ========================================================================

    // Pattern 1: DrawHandlerPtrOffset
    // FiveM pattern: "44 0F 2F 43 48 45 8D"
    // Offset is uint8 at pattern+4
    const char* drawHandlerPattern = "44 0F 2F 43 48 45 8D";
    uintptr_t drawAddr = FindPattern(g_moduleBase, g_moduleSize, drawHandlerPattern);
    if (drawAddr) {
        g_drawHandlerPtrOffset = *(uint8_t*)(drawAddr + 4);
        debug << "FiveM DrawHandler pattern found at 0x" << std::hex << drawAddr
              << " offset=0x" << g_drawHandlerPtrOffset << std::dec << "\n";
    }
    else {
        debug << "FiveM DrawHandler pattern NOT found, trying alternate...\n";

        // Alternate pattern for newer game versions
        const char* altDrawPattern = "48 8B 81 ?? ?? 00 00 48 85 C0 74";
        uintptr_t altAddr = FindPattern(g_moduleBase, g_moduleSize, altDrawPattern);
        if (altAddr) {
            g_drawHandlerPtrOffset = *(int32_t*)(altAddr + 3);
            debug << "Alt DrawHandler pattern found at 0x" << std::hex << altAddr
                  << " offset=0x" << g_drawHandlerPtrOffset << std::dec << "\n";
        }
        else {
            debug << "Alt DrawHandler pattern also NOT found\n";
        }
    }

    // Pattern 2: StreamRenderWheelWidth and StreamRenderWheelSize offsets
    // FiveM pattern: "48 89 01 B8 00 00 80 3F 66 44 89 51"
    // Width is uint32 at pattern+23, Size is uint8 at pattern+20
    const char* streamRenderPattern = "48 89 01 B8 00 00 80 3F 66 44 89 51";
    uintptr_t streamAddr = FindPattern(g_moduleBase, g_moduleSize, streamRenderPattern);
    if (streamAddr) {
        g_streamRenderWheelSizeOffset = *(uint8_t*)(streamAddr + 20);
        g_streamRenderWheelWidthOffset = *(int32_t*)(streamAddr + 23);
        debug << "FiveM StreamRender pattern found at 0x" << std::hex << streamAddr
              << " sizeOff=0x" << g_streamRenderWheelSizeOffset
              << " widthOff=0x" << g_streamRenderWheelWidthOffset << std::dec << "\n";
    }
    else {
        debug << "FiveM StreamRender pattern NOT found, trying alternate...\n";

        // Original VStancer pattern
        const char* vstancerPattern = "F3 44 0F 10 ?? ?? ?? 00 00";
        uintptr_t vstAddr = FindPattern(g_moduleBase, g_moduleSize, vstancerPattern);
        if (vstAddr) {
            g_streamRenderWheelWidthOffset = *(int32_t*)(vstAddr + 4);
            debug << "VStancer StreamRender pattern found at 0x" << std::hex << vstAddr
                  << " widthOff=0x" << g_streamRenderWheelWidthOffset << std::dec << "\n";
        }
        else {
            debug << "VStancer StreamRender pattern also NOT found\n";
        }
    }

    // Pattern 3: StreamRenderGfxPtrOffset (offset from DrawHandler to StreamRenderGfx)
    // This might be a fixed offset like 0x20 or found via pattern
    // Try common offsets
    g_streamRenderGfxPtrOffset = 0x20;  // Common offset, may need adjustment
    debug << "Using StreamRenderGfxPtrOffset=0x" << std::hex << g_streamRenderGfxPtrOffset << std::dec << "\n";

    g_debugInfo += debug.str();
}

// ============================================================================
// Cache for original wheel positions
// ============================================================================

struct OriginalWheelData {
    std::vector<float> xOffsets;
    float frontCamber;
    float rearCamber;
    bool cached;
};
static std::unordered_map<int, OriginalWheelData> g_originalCache;

// ============================================================================
// Helper: Check if pointer looks valid
// ============================================================================

static bool IsValidPointer(uintptr_t ptr) {
    // Basic sanity check - pointer should be in reasonable range
    // GTA V typically loads above 0x140000000 on x64
    return (ptr > 0x10000 && ptr < 0x7FFFFFFFFFFF);
}

// ============================================================================
// Offset Detection with Detailed Probing
// ============================================================================

static bool ProbeWheelsOffsetInner(uintptr_t vehicleAddr, int offset) {
    uintptr_t wheelsPtr = *(uintptr_t*)(vehicleAddr + offset);

    if (wheelsPtr == 0) {
        return false;
    }

    if (!IsValidPointer(wheelsPtr)) {
        return false;
    }

    // Try to read first wheel pointer
    uintptr_t wheel0 = *(uintptr_t*)wheelsPtr;

    if (wheel0 == 0) {
        return false;
    }

    if (!IsValidPointer(wheel0)) {
        return false;
    }

    // Try to read wheel X position - should be a reasonable float
    float wheelX = *(float*)(wheel0 + g_wheelPosXOffset);

    // Check if it's a valid float (not NaN, not infinite, reasonable range)
    if (wheelX != wheelX) return false; // NaN check
    if (wheelX < -5.0f || wheelX > 5.0f) return false;  // Typical wheel X is -1 to +1 meters

    // Also check Y and Z to be more confident
    float wheelY = *(float*)(wheel0 + g_wheelPosYOffset);
    float wheelZ = *(float*)(wheel0 + g_wheelPosZOffset);

    if (wheelY != wheelY || wheelZ != wheelZ) return false; // NaN check
    if (wheelY < -10.0f || wheelY > 10.0f) return false;
    if (wheelZ < -5.0f || wheelZ > 5.0f) return false;

    return true;
}

static bool ProbeWheelsOffset(uintptr_t vehicleAddr, int offset) {
    __try {
        return ProbeWheelsOffsetInner(vehicleAddr, offset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool ProbeHandlingOffset(uintptr_t vehicleAddr, int offset) {
    __try {
        uintptr_t handlingPtr = *(uintptr_t*)(vehicleAddr + offset);

        if (handlingPtr == 0 || !IsValidPointer(handlingPtr)) {
            return false;
        }

        // Try to read camber - should be a small float (radians, typically -0.5 to +0.5)
        float camber = *(float*)(handlingPtr + g_camberFrontOffset);

        // NaN check and range check
        if (camber != camber) return false;
        if (camber < -1.0f || camber > 1.0f) return false;

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

static bool InitializeOffsets() {
    if (g_initialized) return g_available;
    g_initialized = true;

    g_debugInfo = "WheelFitmentAPI v" + std::string(VERSION) + " initializing...\n";
    g_available = true; // Will probe when we have a vehicle

    return true;
}

static bool EnsureOffsetsForVehicle(uintptr_t vehicleAddr) {
    if (vehicleAddr == 0) {
        g_lastError = "Invalid vehicle address (0)";
        return false;
    }

    std::stringstream debug;
    debug << "Probing vehicle at 0x" << std::hex << vehicleAddr << std::dec << "\n";

    // If we already have working offsets, verify they still work
    if (g_wheelsOffset != 0) {
        if (ProbeWheelsOffset(vehicleAddr, g_wheelsOffset)) {
            return true;
        }
        // Offset no longer works, reset and re-probe
        debug << "Previous offset 0x" << std::hex << g_wheelsOffset << " no longer works, re-probing...\n";
        g_wheelsOffset = 0;
    }

    // Probe all known wheel offsets
    debug << "Probing wheel offsets:\n";

    for (int offset : KNOWN_WHEELS_OFFSETS) {
        if (ProbeWheelsOffset(vehicleAddr, offset)) {
            g_wheelsOffset = offset;
            g_numWheelsOffset = offset - 8;
            debug << "Found working wheels offset: 0x" << std::hex << offset << std::dec << "\n";
            break;
        }
    }

    if (g_wheelsOffset == 0) {
        debug << "No working wheels offset found!\n";
        g_lastError = "Could not find wheels offset";
    }

    // Probe handling offsets
    if (g_handlingOffset == 0 || g_handlingOffset < 0x800) {
        debug << "Probing handling offsets:\n";
        g_handlingOffset = 0; // Reset invalid offset

        for (int offset : KNOWN_HANDLING_OFFSETS) {
            if (ProbeHandlingOffset(vehicleAddr, offset)) {
                g_handlingOffset = offset;
                debug << "Found working handling offset: 0x" << std::hex << offset << std::dec << "\n";
                break;
            }
        }

        if (g_handlingOffset == 0) {
            debug << "No working handling offset found\n";
        }
    }

    g_debugInfo = debug.str();
    return (g_wheelsOffset != 0);
}

// ============================================================================
// Helper Functions
// ============================================================================

// Vehicle address passed directly from C# (Vehicle.MemoryAddress)
// We no longer use getScriptHandleBaseAddress because it only works
// from within a proper ScriptHookV script context, not from a library DLL.
static uintptr_t g_currentVehicleAddr = 0;

static uintptr_t GetVehicleAddress(int vehicle) {
    // Use the address set by WF_SetVehicleAddress instead of getScriptHandleBaseAddress
    // The 'vehicle' parameter is now ignored - we use the cached address
    (void)vehicle;
    return g_currentVehicleAddr;
}

static uintptr_t GetWheelAddress(int vehicle, int wheelIndex) {
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0;

    // Ensure we have valid offsets
    if (!EnsureOffsetsForVehicle(vehAddr)) return 0;

    __try {
        // Read wheels array pointer from vehicle
        uintptr_t wheelsArrayPtr = *(uintptr_t*)(vehAddr + g_wheelsOffset);
        if (wheelsArrayPtr == 0 || !IsValidPointer(wheelsArrayPtr)) return 0;

        // Index into wheels array (each entry is a pointer, 8 bytes on x64)
        uintptr_t wheelPtr = *(uintptr_t*)(wheelsArrayPtr + (wheelIndex * 8));
        if (!IsValidPointer(wheelPtr)) return 0;

        return wheelPtr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

static uintptr_t GetHandlingAddress(int vehicle) {
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0;

    // Ensure we have valid offsets
    EnsureOffsetsForVehicle(vehAddr);

    if (g_handlingOffset == 0) return 0;

    __try {
        uintptr_t ptr = *(uintptr_t*)(vehAddr + g_handlingOffset);
        if (!IsValidPointer(ptr)) return 0;
        return ptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// ============================================================================
// API Implementation
// ============================================================================

WF_API const char* WF_GetVersion() {
    return VERSION;
}

WF_API bool WF_IsAvailable() {
    return InitializeOffsets();
}

WF_API const char* WF_GetLastError() {
    return g_lastError.c_str();
}

WF_API const char* WF_GetDebugInfo() {
    return g_debugInfo.c_str();
}

WF_API int WF_GetWheelsOffset() {
    return g_wheelsOffset;
}

WF_API int WF_GetHandlingOffset() {
    return g_handlingOffset;
}

WF_API unsigned __int64 WF_GetVehicleAddress(int vehicle) {
    (void)vehicle;
    return (unsigned __int64)g_currentVehicleAddr;
}

WF_API void WF_SetVehicleAddress(unsigned __int64 address) {
    g_currentVehicleAddr = (uintptr_t)address;
}

WF_API int WF_GetWheelCount(int vehicle) {
    if (!InitializeOffsets()) return 0;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0;

    // Ensure offsets are valid
    if (!EnsureOffsetsForVehicle(vehAddr)) return 0;

    // Probe for wheels by checking each slot
    int count = 0;
    for (int i = 0; i < 10; i++) {
        if (GetWheelAddress(vehicle, i) != 0) {
            count++;
        }
        else {
            break;
        }
    }
    return count;
}

// ============================================================================
// Wheel Position (Track Width)
// ============================================================================

WF_API float WF_GetWheelXOffset(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_wheelPosXOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetWheelXOffset(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        // Write to visual X offset (0x030 - confirmed working!)
        *(float*)(wheelAddr + g_wheelPosXOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_GetWheelYOffset(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_wheelPosYOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetWheelYOffset(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + g_wheelPosYOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_GetWheelZOffset(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_wheelPosZOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetWheelZOffset(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + g_wheelPosZOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ============================================================================
// Camber (per-wheel visual offset at 0x008)
// ============================================================================

WF_API float WF_GetFrontCamber(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    // Get front left wheel (index 0) camber
    uintptr_t wheelAddr = GetWheelAddress(vehicle, 0);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_wheelCamberOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetFrontCamber(int vehicle, float value) {
    if (!InitializeOffsets()) return false;

    bool success = true;

    // Set camber on front wheels (0 = front left, 1 = front right)
    for (int i = 0; i < 2; i++) {
        uintptr_t wheelAddr = GetWheelAddress(vehicle, i);
        if (wheelAddr == 0) continue;

        __try {
            // Left wheel gets positive camber, right wheel gets negative (or vice versa for visual effect)
            float adjustedValue = (i == 0) ? value : -value;
            *(float*)(wheelAddr + g_wheelCamberOffset) = adjustedValue;
            // Also set inverse Y rotation (0x010) for proper camber display
            *(float*)(wheelAddr + 0x010) = -adjustedValue;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            success = false;
        }
    }

    return success;
}

WF_API float WF_GetRearCamber(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    // Get rear left wheel (index 2) camber
    uintptr_t wheelAddr = GetWheelAddress(vehicle, 2);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_wheelCamberOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetRearCamber(int vehicle, float value) {
    if (!InitializeOffsets()) return false;

    bool success = true;

    // Set camber on rear wheels (2 = rear left, 3 = rear right)
    for (int i = 2; i < 4; i++) {
        uintptr_t wheelAddr = GetWheelAddress(vehicle, i);
        if (wheelAddr == 0) continue;

        __try {
            // Left wheel gets positive camber, right wheel gets negative
            float adjustedValue = (i == 2) ? value : -value;
            *(float*)(wheelAddr + g_wheelCamberOffset) = adjustedValue;
            // Also set inverse Y rotation (0x010) for proper camber display
            *(float*)(wheelAddr + 0x010) = -adjustedValue;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            success = false;
        }
    }

    return success;
}

// Old handling data camber functions (kept for reference)
WF_API float WF_GetFrontCamberHandling(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t handlingAddr = GetHandlingAddress(vehicle);
    if (handlingAddr == 0) return 0.0f;

    __try {
        return *(float*)(handlingAddr + g_camberFrontOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetFrontCamberHandling(int vehicle, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t handlingAddr = GetHandlingAddress(vehicle);
    if (handlingAddr == 0) return false;

    __try {
        *(float*)(handlingAddr + g_camberFrontOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_GetRearCamberHandling(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t handlingAddr = GetHandlingAddress(vehicle);
    if (handlingAddr == 0) return 0.0f;

    __try {
        return *(float*)(handlingAddr + g_camberRearOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetRearCamberHandling(int vehicle, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t handlingAddr = GetHandlingAddress(vehicle);
    if (handlingAddr == 0) return false;

    __try {
        *(float*)(handlingAddr + g_camberRearOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ============================================================================
// Suspension
// ============================================================================

WF_API float WF_GetSuspensionCompression(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + 0x160); // Suspension compression offset
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// ============================================================================
// Visual Wheel Properties (offsets need testing - use debug menu to verify)
// NOTE: 0x110 was found to be Z height, NOT tire radius!
// These offsets are placeholders - use WF_TestOffsetWrite to find real ones
// ============================================================================

// Placeholder offset - needs testing to find actual tire radius
static int g_tyreRadiusOffset = 0x11C;  // UNVERIFIED - test with debug menu
static int g_tyreWidthOffset = 0x120;   // UNVERIFIED - test with debug menu
static int g_rimRadiusOffset = 0x124;   // UNVERIFIED - test with debug menu

WF_API float WF_GetTyreRadius(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_tyreRadiusOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetTyreRadius(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + g_tyreRadiusOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_GetTyreWidth(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_tyreWidthOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetTyreWidth(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + g_tyreWidthOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_GetRimRadius(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_rimRadiusOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_SetRimRadius(int vehicle, int wheelIndex, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + g_rimRadiusOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Functions to set the tire/rim offset values for testing
WF_API void WF_SetTyreRadiusOffset(int offset) {
    g_tyreRadiusOffset = offset;
}

WF_API void WF_SetTyreWidthOffset(int offset) {
    g_tyreWidthOffset = offset;
}

WF_API void WF_SetRimRadiusOffset(int offset) {
    g_rimRadiusOffset = offset;
}

// ============================================================================
// Batch Operations
// ============================================================================

WF_API bool WF_ApplyFitment(int vehicle, const WheelFitmentData* data) {
    if (!InitializeOffsets() || data == nullptr) return false;

    // Cache original positions if not already cached
    auto it = g_originalCache.find(vehicle);
    if (it == g_originalCache.end() || !it->second.cached) {
        OriginalWheelData original;
        original.cached = true;
        original.frontCamber = WF_GetFrontCamber(vehicle);
        original.rearCamber = WF_GetRearCamber(vehicle);
        int numWheels = WF_GetWheelCount(vehicle);
        for (int i = 0; i < numWheels; i++) {
            original.xOffsets.push_back(WF_GetWheelXOffset(vehicle, i));
        }
        g_originalCache[vehicle] = original;
    }

    // Apply camber
    WF_SetFrontCamber(vehicle, data->frontCamber);
    WF_SetRearCamber(vehicle, data->rearCamber);

    // Apply track width
    int numWheels = WF_GetWheelCount(vehicle);
    auto& original = g_originalCache[vehicle];

    for (int i = 0; i < numWheels && i < (int)original.xOffsets.size(); i++) {
        bool isFront = (i == 0 || i == 1);
        bool isLeft = (i % 2 == 0);

        float trackDelta = isFront ? data->frontTrackWidthDelta : data->rearTrackWidthDelta;
        float offset = isLeft ? -trackDelta : trackDelta;
        float newX = original.xOffsets[i] + offset;

        WF_SetWheelXOffset(vehicle, i, newX);
    }

    return true;
}

WF_API bool WF_ResetToStock(int vehicle) {
    if (!InitializeOffsets()) return false;

    auto it = g_originalCache.find(vehicle);
    if (it == g_originalCache.end() || !it->second.cached) {
        return false; // No cached original data
    }

    auto& original = it->second;
    for (int i = 0; i < (int)original.xOffsets.size(); i++) {
        WF_SetWheelXOffset(vehicle, i, original.xOffsets[i]);
    }

    // Reset camber to original
    WF_SetFrontCamber(vehicle, original.frontCamber);
    WF_SetRearCamber(vehicle, original.rearCamber);

    return true;
}

// ============================================================================
// Debug: Offset Testing
// ============================================================================

WF_API int WF_GetTestOffset() {
    return g_testOffset;
}

WF_API void WF_SetTestOffset(int offset) {
    g_testOffset = offset;
}

WF_API bool WF_TestOffsetWrite(int vehicle, int wheelIndex, float delta) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        // Read current value at test offset
        float currentVal = *(float*)(wheelAddr + g_testOffset);

        // Write modified value
        *(float*)(wheelAddr + g_testOffset) = currentVal + delta;

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API float WF_ReadTestOffset(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + g_testOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// ============================================================================
// Pattern Scanning API
// ============================================================================

WF_API bool WF_PerformPatternScan() {
    PerformPatternScan();
    return g_patternScanComplete;
}

WF_API int WF_GetPatternScanStatus() {
    // Returns: 0 = not started, 1 = complete with results, 2 = complete no results
    if (!g_patternScanComplete) return 0;
    if (g_streamRenderWheelWidthOffset != 0 || g_drawHandlerPtrOffset != 0) return 1;
    return 2;
}

WF_API int WF_GetStreamRenderWheelWidthOffset() {
    return g_streamRenderWheelWidthOffset;
}

WF_API int WF_GetStreamRenderWheelSizeOffset() {
    return g_streamRenderWheelSizeOffset;
}

WF_API int WF_GetDrawHandlerPtrOffset() {
    return g_drawHandlerPtrOffset;
}

// Helper to get StreamRenderGfx pointer (inner function for SEH)
static uintptr_t GetStreamRenderGfxInner(uintptr_t vehAddr) {
    __try {
        // First get DrawHandler from vehicle
        uintptr_t drawHandler = *(uintptr_t*)(vehAddr + g_drawHandlerPtrOffset);
        if (drawHandler == 0 || !IsValidPointer(drawHandler)) return 0;

        // Then get StreamRenderGfx from DrawHandler
        uintptr_t streamRenderGfx = *(uintptr_t*)(drawHandler + g_streamRenderGfxPtrOffset);
        if (streamRenderGfx == 0 || !IsValidPointer(streamRenderGfx)) return 0;

        return streamRenderGfx;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Helper to get VStancer inner pointer (StreamRenderGfx -> [+0x8])
static uintptr_t GetVStancerInnerPtr(uintptr_t vehAddr) {
    uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
    if (streamRenderGfx == 0) return 0;

    __try {
        uintptr_t innerPtr = *(uintptr_t*)(streamRenderGfx + VSTANCER_INNER_PTR_OFFSET);
        if (innerPtr != 0 && IsValidPointer(innerPtr)) {
            return innerPtr;
        }
        return 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Static pointer chain from Cheat Engine scan
// Chain: GTA5.exe+02DC8A70 -> E0 -> 18 -> 0 -> 58 -> 20 -> 8 -> value
static uintptr_t g_staticChainBase = 0;
static bool g_staticChainInitialized = false;
static char g_staticChainDebug[1024] = {0};

static uintptr_t GetVStancerInnerPtrStatic() {
    if (!g_staticChainInitialized) {
        g_staticChainInitialized = true;
        // Get GTA5.exe base address
        HMODULE hModule = GetModuleHandleA("GTA5.exe");
        if (hModule) {
            g_staticChainBase = (uintptr_t)hModule;
        }
    }

    if (g_staticChainBase == 0) return 0;

    __try {
        // Follow chain: GTA5.exe+02DC8A70 -> E0 -> 18 -> 0 -> 58 -> 20 -> 8
        uintptr_t ptr = g_staticChainBase + 0x02DC8A70;

        ptr = *(uintptr_t*)ptr;
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0xE0);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0x18);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0x0);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0x58);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0x20);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        ptr = *(uintptr_t*)(ptr + 0x8);
        if (ptr == 0 || !IsValidPointer(ptr)) return 0;

        return ptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Inner function for static chain debug (SEH safe - no C++ objects)
struct StaticChainResult {
    uintptr_t step[8];
    int failStep;
    float wheelSize;
    bool exception;
};

static void GetStaticChainInner(StaticChainResult* result) {
    result->failStep = -1;
    result->wheelSize = 0;
    result->exception = false;

    __try {
        uintptr_t ptr = g_staticChainBase + 0x02DC8A70;
        result->step[0] = ptr;

        uintptr_t val = *(uintptr_t*)ptr;
        result->step[1] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 1; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0xE0);
        result->step[2] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 2; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0x18);
        result->step[3] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 3; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0x0);
        result->step[4] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 4; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0x58);
        result->step[5] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 5; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0x20);
        result->step[6] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 6; return; }
        ptr = val;

        val = *(uintptr_t*)(ptr + 0x8);
        result->step[7] = val;
        if (val == 0 || !IsValidPointer(val)) { result->failStep = 7; return; }
        ptr = val;

        result->wheelSize = *(float*)(ptr + 0x784);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        result->exception = true;
    }
}

// Debug function to dump static chain state
WF_API const char* WF_GetStaticChainDebug() {
    // Initialize static chain if needed
    if (!g_staticChainInitialized) {
        g_staticChainInitialized = true;
        HMODULE hModule = GetModuleHandleA("GTA5.exe");
        if (hModule) {
            g_staticChainBase = (uintptr_t)hModule;
        }
    }

    std::stringstream ss;
    ss << "=== Static Chain Debug ===\n";
    ss << "GTA5.exe base: 0x" << std::hex << g_staticChainBase << "\n";

    if (g_staticChainBase == 0) {
        ss << "Base not found!\n";
        strcpy_s(g_staticChainDebug, ss.str().c_str());
        return g_staticChainDebug;
    }

    StaticChainResult result = {0};
    GetStaticChainInner(&result);

    ss << "Step 0 (base+02DC8A70): 0x" << result.step[0] << "\n";
    ss << "Step 1 [ptr]: 0x" << result.step[1] << (result.failStep == 1 ? " INVALID" : "") << "\n";
    ss << "Step 2 [+E0]: 0x" << result.step[2] << (result.failStep == 2 ? " INVALID" : "") << "\n";
    ss << "Step 3 [+18]: 0x" << result.step[3] << (result.failStep == 3 ? " INVALID" : "") << "\n";
    ss << "Step 4 [+0]: 0x" << result.step[4] << (result.failStep == 4 ? " INVALID" : "") << "\n";
    ss << "Step 5 [+58]: 0x" << result.step[5] << (result.failStep == 5 ? " INVALID" : "") << "\n";
    ss << "Step 6 [+20]: 0x" << result.step[6] << (result.failStep == 6 ? " INVALID" : "") << "\n";
    ss << "Step 7 [+8]: 0x" << result.step[7] << (result.failStep == 7 ? " INVALID" : "") << "\n";

    if (result.exception) {
        ss << "EXCEPTION during chain traversal\n";
    } else if (result.failStep == -1) {
        ss << "Final ptr: 0x" << result.step[7] << "\n";
        ss << std::dec << "WheelSize at +0x784: " << result.wheelSize << "\n";
    }

    strcpy_s(g_staticChainDebug, ss.str().c_str());
    return g_staticChainDebug;
}

// ============================================================================
// Multi-Chain Scanner - Try all known pointer chains to find visual wheel size
// ============================================================================

#define MAX_CHAIN_DEPTH 6

struct PointerChain {
    const char* moduleName;
    uintptr_t moduleOffset;
    int offsets[MAX_CHAIN_DEPTH];
    int depth;
    int finalOffset;  // Offset to read the float from final pointer
};

// Chains extracted from Cheat Engine pointer scan
// These go through ScriptHookV.dll which is always loaded
static const PointerChain g_knownChains[] = {
    // ScriptHookV.dll+001E2228 based chains
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0xB0, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0xB0, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0x78, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0x78, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0x28, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0x28, 0, 0}, 4, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC0, 0x110, 0, 0}, 4, 0xC4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC0, 0x110, 0, 0}, 4, 0xC4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0xF8, 0x8, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0xF8, 0x8, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0xA0, 0x110, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0xA0, 0x110, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E2228, {0x118, 0x8, 0xC8, 0xC0, 0x110, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E2228, {0x110, 0x18, 0xC8, 0xC0, 0x110, 0}, 5, 0xC4},

    // ScriptHookV.dll+001E7828 based chains
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC8, 0xB0, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC8, 0xB0, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC8, 0x78, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC8, 0x78, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC8, 0x28, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC8, 0x28, 0}, 5, 0xD4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC0, 0x110, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC0, 0x110, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC8, 0x98, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC8, 0x98, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x28, 0x8, 0xC8, 0xA8, 0}, 5, 0xC4},
    {"ScriptHookV.dll", 0x001E7828, {0x10, 0x30, 0x18, 0xC8, 0xA8, 0}, 5, 0xC4},

    // VStancer.asi based chains (only work if VStancer is loaded)
    {"VStancer.asi", 0x000DC6A0, {0x298, 0x298, 0x2C0, 0x2B0, 0x274, 0}, 5, 0x0},
    {"VStancer.asi", 0x000DC6A0, {0x298, 0x290, 0x100, 0x2B0, 0x2B0, 0x274}, 6, 0x0},
    {"VStancer.asi", 0x000DC698, {0x20, 0x100, 0x2B0, 0x2B0, 0x2B0, 0x274}, 6, 0x0},
    {"VStancer.asi", 0x000DC698, {0x38, 0x110, 0, 0, 0, 0}, 2, 0xC4},
    {"VStancer.asi", 0x000DC698, {0x30, 0, 0, 0, 0, 0}, 1, 0xC4},
    {"VStancer.asi", 0x000DC698, {0x30, 0x100, 0, 0, 0, 0}, 2, 0xC4},
};

static const int g_numKnownChains = sizeof(g_knownChains) / sizeof(g_knownChains[0]);

// Cache for working chain
static int g_workingChainIndex = -1;
static uintptr_t g_cachedModuleBase = 0;
static char g_chainScanDebug[4096] = {0};

// SEH-safe chain follower
static uintptr_t FollowChainInner(uintptr_t base, const int* offsets, int depth) {
    __try {
        uintptr_t ptr = base;
        for (int i = 0; i < depth; i++) {
            ptr = *(uintptr_t*)(ptr + offsets[i]);
            if (ptr == 0 || !IsValidPointer(ptr)) return 0;
        }
        return ptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// SEH-safe float reader
static float ReadFloatSafe(uintptr_t addr, int offset, bool* success) {
    *success = false;
    __try {
        float val = *(float*)(addr + offset);
        // Check if it's a reasonable wheel size (0.3 to 1.5)
        if (val == val && val >= 0.3f && val <= 1.5f) {
            *success = true;
            return val;
        }
        return 0.0f;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Try all chains and find one that works
WF_API const char* WF_ScanAllChains() {
    std::stringstream ss;
    ss << "=== Scanning " << g_numKnownChains << " pointer chains ===\n\n";

    g_workingChainIndex = -1;
    int foundCount = 0;

    for (int i = 0; i < g_numKnownChains; i++) {
        const PointerChain& chain = g_knownChains[i];

        // Get module base
        HMODULE hModule = GetModuleHandleA(chain.moduleName);
        if (!hModule) {
            continue;  // Module not loaded, skip
        }

        uintptr_t moduleBase = (uintptr_t)hModule;
        uintptr_t startAddr = moduleBase + chain.moduleOffset;

        // Follow the chain
        uintptr_t finalPtr = FollowChainInner(startAddr, chain.offsets, chain.depth);
        if (finalPtr == 0) {
            continue;  // Chain failed
        }

        // Try to read a float at the final offset
        bool success = false;
        float value = ReadFloatSafe(finalPtr, chain.finalOffset, &success);

        if (success) {
            foundCount++;
            ss << "FOUND #" << foundCount << ": " << chain.moduleName << "+0x" << std::hex << chain.moduleOffset;
            ss << " -> ";
            for (int j = 0; j < chain.depth; j++) {
                ss << std::hex << chain.offsets[j];
                if (j < chain.depth - 1) ss << " -> ";
            }
            ss << " -> +" << std::hex << chain.finalOffset;
            ss << std::dec << " = " << value << "\n";

            // Store first working chain
            if (g_workingChainIndex == -1) {
                g_workingChainIndex = i;
                g_cachedModuleBase = moduleBase;
            }
        }
    }

    if (foundCount == 0) {
        ss << "No working chains found!\n";
        ss << "Make sure you're in a vehicle.\n";
    } else {
        ss << "\nTotal working chains: " << foundCount << "\n";
        ss << "Using chain index: " << g_workingChainIndex << "\n";
    }

    strncpy_s(g_chainScanDebug, ss.str().c_str(), sizeof(g_chainScanDebug) - 1);
    return g_chainScanDebug;
}

// Get visual wheel size using discovered chain
WF_API float WF_GetVisualWheelSizeViaChain(int vehicle) {
    (void)vehicle;  // Not used, chain is global

    if (g_workingChainIndex < 0) return 0.0f;

    const PointerChain& chain = g_knownChains[g_workingChainIndex];

    // Re-get module base in case it changed
    HMODULE hModule = GetModuleHandleA(chain.moduleName);
    if (!hModule) return 0.0f;

    uintptr_t moduleBase = (uintptr_t)hModule;
    uintptr_t startAddr = moduleBase + chain.moduleOffset;

    uintptr_t finalPtr = FollowChainInner(startAddr, chain.offsets, chain.depth);
    if (finalPtr == 0) return 0.0f;

    __try {
        return *(float*)(finalPtr + chain.finalOffset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Set visual wheel size using discovered chain
WF_API bool WF_SetVisualWheelSizeViaChain(int vehicle, float value) {
    (void)vehicle;

    if (g_workingChainIndex < 0) return false;

    const PointerChain& chain = g_knownChains[g_workingChainIndex];

    HMODULE hModule = GetModuleHandleA(chain.moduleName);
    if (!hModule) return false;

    uintptr_t moduleBase = (uintptr_t)hModule;
    uintptr_t startAddr = moduleBase + chain.moduleOffset;

    uintptr_t finalPtr = FollowChainInner(startAddr, chain.offsets, chain.depth);
    if (finalPtr == 0) return false;

    __try {
        *(float*)(finalPtr + chain.finalOffset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Get the working chain index
WF_API int WF_GetWorkingChainIndex() {
    return g_workingChainIndex;
}

// Generic getter for VStancer float property
static float GetVStancerFloat(uintptr_t vehAddr, int offset) {
    // Try static pointer chain first (from Cheat Engine scan)
    uintptr_t innerPtr = GetVStancerInnerPtrStatic();

    // Fall back to vehicle-based chain if static fails
    if (innerPtr == 0) {
        innerPtr = GetVStancerInnerPtr(vehAddr);
    }

    if (innerPtr == 0) return 0.0f;

    __try {
        return *(float*)(innerPtr + offset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Generic setter for VStancer float property
static bool SetVStancerFloat(uintptr_t vehAddr, int offset, float value) {
    // Try static pointer chain first (from Cheat Engine scan)
    uintptr_t innerPtr = GetVStancerInnerPtrStatic();

    // Fall back to vehicle-based chain if static fails
    if (innerPtr == 0) {
        innerPtr = GetVStancerInnerPtr(vehAddr);
    }

    if (innerPtr == 0) return false;

    __try {
        *(float*)(innerPtr + offset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Visual wheel size/width functions using DrawHandler -> StreamRenderGfx path
// FiveM approach: Read DIRECTLY from StreamRenderGfx at pattern-discovered offsets
// (NOT via inner pointer like VStancer)
WF_API float WF_GetVisualWheelSize(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    // Use pattern-discovered offset directly on StreamRenderGfx
    if (g_streamRenderWheelSizeOffset != 0) {
        uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
        if (streamRenderGfx != 0) {
            __try {
                return *(float*)(streamRenderGfx + g_streamRenderWheelSizeOffset);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                return 0.0f;
            }
        }
    }

    // Fallback to VStancer inner pointer approach
    return GetVStancerFloat(vehAddr, OFFSET_VISUAL_WHEEL_SIZE);
}

WF_API bool WF_SetVisualWheelSize(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    if (value == 0.0f) return false;  // FiveM checks this

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    // Use pattern-discovered offset directly on StreamRenderGfx
    if (g_streamRenderWheelSizeOffset != 0) {
        uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
        if (streamRenderGfx != 0) {
            __try {
                *(float*)(streamRenderGfx + g_streamRenderWheelSizeOffset) = value;
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                return false;
            }
        }
    }

    // Fallback to VStancer inner pointer approach
    return SetVStancerFloat(vehAddr, OFFSET_VISUAL_WHEEL_SIZE, value);
}

WF_API float WF_GetVisualWheelWidth(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    // Use pattern-discovered offset directly on StreamRenderGfx
    if (g_streamRenderWheelWidthOffset != 0) {
        uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
        if (streamRenderGfx != 0) {
            __try {
                return *(float*)(streamRenderGfx + g_streamRenderWheelWidthOffset);
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                return 0.0f;
            }
        }
    }

    // Fallback to VStancer inner pointer approach
    return GetVStancerFloat(vehAddr, OFFSET_VISUAL_WHEEL_WIDTH);
}

WF_API bool WF_SetVisualWheelWidth(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    if (value == 0.0f) return false;  // FiveM checks this

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    // Use pattern-discovered offset directly on StreamRenderGfx
    if (g_streamRenderWheelWidthOffset != 0) {
        uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
        if (streamRenderGfx != 0) {
            __try {
                *(float*)(streamRenderGfx + g_streamRenderWheelWidthOffset) = value;
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                return false;
            }
        }
    }

    // Fallback to VStancer inner pointer approach
    return SetVStancerFloat(vehAddr, OFFSET_VISUAL_WHEEL_WIDTH, value);
}

// ============================================================================
// VStancer Property Accessors (all discovered offsets)
// ============================================================================

WF_API float WF_GetVStancerFrontCamber(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_FRONT_CAMBER);
}

WF_API bool WF_SetVStancerFrontCamber(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_FRONT_CAMBER, value);
}

WF_API float WF_GetVStancerRearCamber(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_REAR_CAMBER);
}

WF_API bool WF_SetVStancerRearCamber(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_REAR_CAMBER, value);
}

WF_API float WF_GetVStancerFrontTrackWidth(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_FRONT_TRACK_WIDTH);
}

WF_API bool WF_SetVStancerFrontTrackWidth(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_FRONT_TRACK_WIDTH, value);
}

WF_API float WF_GetVStancerRearTrackWidth(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_REAR_TRACK_WIDTH);
}

WF_API bool WF_SetVStancerRearTrackWidth(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_REAR_TRACK_WIDTH, value);
}

WF_API float WF_GetVStancerSuspensionFrontHeight(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_SUSPENSION_FRONT_HEIGHT);
}

WF_API bool WF_SetVStancerSuspensionFrontHeight(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_SUSPENSION_FRONT_HEIGHT, value);
}

WF_API float WF_GetVStancerSuspensionRearHeight(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_SUSPENSION_REAR_HEIGHT);
}

WF_API bool WF_SetVStancerSuspensionRearHeight(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_SUSPENSION_REAR_HEIGHT, value);
}

WF_API float WF_GetVStancerVisualHeight(int vehicle) {
    if (!InitializeOffsets()) return 0.0f;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return GetVStancerFloat(vehAddr, OFFSET_VISUAL_HEIGHT);
}

WF_API bool WF_SetVStancerVisualHeight(int vehicle, float value) {
    if (!InitializeOffsets()) return false;
    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;
    if (g_drawHandlerPtrOffset == 0) PerformPatternScan();
    return SetVStancerFloat(vehAddr, OFFSET_VISUAL_HEIGHT, value);
}

// Probe visual offsets at a specific offset from draw handler
WF_API float WF_ProbeDrawHandlerOffset(int vehicle, int offset) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    if (g_drawHandlerPtrOffset == 0) return 0.0f;

    __try {
        uintptr_t drawHandler = *(uintptr_t*)(vehAddr + g_drawHandlerPtrOffset);
        if (drawHandler == 0 || !IsValidPointer(drawHandler)) return 0.0f;

        return *(float*)(drawHandler + offset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Debug: Get the raw pointer value at DrawHandler offset
WF_API unsigned long long WF_GetDrawHandlerPointer(int vehicle) {
    if (!InitializeOffsets()) return 0;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    if (g_drawHandlerPtrOffset == 0) return 0;

    __try {
        uintptr_t drawHandler = *(uintptr_t*)(vehAddr + g_drawHandlerPtrOffset);
        return (unsigned long long)drawHandler;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

// Debug: Get StreamRenderGfx pointer
WF_API unsigned long long WF_GetStreamRenderGfxPointer(int vehicle) {
    if (!InitializeOffsets()) return 0;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    return (unsigned long long)GetStreamRenderGfxInner(vehAddr);
}

// Debug: Dump full pointer chain info
static char g_pointerChainDebug[2048] = {0};

// Struct to hold probe results (no C++ objects, SEH safe)
struct PointerChainData {
    uintptr_t vehAddr;
    uintptr_t drawHandler;
    bool drawHandlerValid;
    uintptr_t streamRenderGfx;
    bool streamRenderGfxValid;
    float wheelSize;
    float wheelWidth;
    bool exception;
    // Probed valid pointers from DrawHandler
    uintptr_t dhProbePtr[8];
    int dhProbeOff[8];
    int dhProbeCount;
    // Probed valid pointers from Vehicle
    uintptr_t vehProbePtr[16];
    int vehProbeOff[16];
    int vehProbeCount;
};

// SEH-safe inner probe function
static void ProbePointerChainInner(uintptr_t vehAddr, PointerChainData* data) {
    data->vehAddr = vehAddr;
    data->exception = false;
    data->drawHandler = 0;
    data->drawHandlerValid = false;
    data->streamRenderGfx = 0;
    data->streamRenderGfxValid = false;
    data->wheelSize = 0;
    data->wheelWidth = 0;
    data->dhProbeCount = 0;
    data->vehProbeCount = 0;

    __try {
        data->drawHandler = *(uintptr_t*)(vehAddr + g_drawHandlerPtrOffset);
        data->drawHandlerValid = IsValidPointer(data->drawHandler);

        if (data->drawHandlerValid) {
            data->streamRenderGfx = *(uintptr_t*)(data->drawHandler + g_streamRenderGfxPtrOffset);
            data->streamRenderGfxValid = IsValidPointer(data->streamRenderGfx);

            if (data->streamRenderGfxValid) {
                if (g_streamRenderWheelSizeOffset != 0) {
                    data->wheelSize = *(float*)(data->streamRenderGfx + g_streamRenderWheelSizeOffset);
                }
                if (g_streamRenderWheelWidthOffset != 0) {
                    data->wheelWidth = *(float*)(data->streamRenderGfx + g_streamRenderWheelWidthOffset);
                }
            }
            else {
                // Probe DrawHandler offsets
                for (int off = 0; off <= 0x40 && data->dhProbeCount < 8; off += 8) {
                    uintptr_t ptr = *(uintptr_t*)(data->drawHandler + off);
                    if (IsValidPointer(ptr)) {
                        data->dhProbeOff[data->dhProbeCount] = off;
                        data->dhProbePtr[data->dhProbeCount] = ptr;
                        data->dhProbeCount++;
                    }
                }
            }
        }
        else {
            // Probe vehicle offsets
            for (int off = 0x40; off <= 0x100 && data->vehProbeCount < 16; off += 8) {
                uintptr_t ptr = *(uintptr_t*)(vehAddr + off);
                if (IsValidPointer(ptr)) {
                    data->vehProbeOff[data->vehProbeCount] = off;
                    data->vehProbePtr[data->vehProbeCount] = ptr;
                    data->vehProbeCount++;
                }
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        data->exception = true;
    }
}

WF_API const char* WF_GetPointerChainDebug(int vehicle) {
    if (!InitializeOffsets()) {
        strcpy_s(g_pointerChainDebug, "Not initialized");
        return g_pointerChainDebug;
    }

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) {
        strcpy_s(g_pointerChainDebug, "Invalid vehicle address");
        return g_pointerChainDebug;
    }

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    // Probe with SEH
    PointerChainData data;
    ProbePointerChainInner(vehAddr, &data);

    // Now format using stringstream (no SEH needed here)
    std::stringstream ss;
    ss << "=== Pointer Chain Debug ===\n";
    ss << "Vehicle: 0x" << std::hex << data.vehAddr << std::dec << "\n";
    ss << "DrawHandlerPtrOffset: 0x" << std::hex << g_drawHandlerPtrOffset << std::dec << "\n";
    ss << "StreamRenderGfxPtrOffset: 0x" << std::hex << g_streamRenderGfxPtrOffset << std::dec << "\n";
    ss << "StreamRenderWheelSizeOffset: 0x" << std::hex << g_streamRenderWheelSizeOffset << std::dec << "\n";
    ss << "StreamRenderWheelWidthOffset: 0x" << std::hex << g_streamRenderWheelWidthOffset << std::dec << "\n";

    if (data.exception) {
        ss << "EXCEPTION during probe\n";
    }
    else {
        ss << "DrawHandler: 0x" << std::hex << data.drawHandler;
        if (data.drawHandlerValid) {
            ss << " (VALID)\n";
            ss << "StreamRenderGfx: 0x" << std::hex << data.streamRenderGfx;
            if (data.streamRenderGfxValid) {
                ss << " (VALID)\n";
                ss << "WheelSize: " << std::dec << data.wheelSize << "\n";
                ss << "WheelWidth: " << std::dec << data.wheelWidth << "\n";
            }
            else {
                ss << " (INVALID)\n";
                ss << "Probing DrawHandler offsets for valid pointers:\n";
                for (int i = 0; i < data.dhProbeCount; i++) {
                    ss << "  +0x" << std::hex << data.dhProbeOff[i] << " = 0x" << data.dhProbePtr[i] << " (valid)\n";
                }
            }
        }
        else {
            ss << " (INVALID - not a pointer)\n";
            ss << "Probing vehicle offsets for DrawHandler:\n";
            for (int i = 0; i < data.vehProbeCount; i++) {
                ss << "  +0x" << std::hex << data.vehProbeOff[i] << " = 0x" << data.vehProbePtr[i] << " (valid ptr)\n";
            }
        }
    }

    std::string result = ss.str();
    strncpy_s(g_pointerChainDebug, result.c_str(), sizeof(g_pointerChainDebug) - 1);
    return g_pointerChainDebug;
}

// Probe directly from vehicle structure (bypass DrawHandler)
WF_API float WF_ProbeVehicleOffset(int vehicle, int offset) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;

    __try {
        return *(float*)(vehAddr + offset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// Write directly to vehicle structure offset
WF_API bool WF_WriteVehicleOffset(int vehicle, int offset, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;

    __try {
        *(float*)(vehAddr + offset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

WF_API bool WF_WriteDrawHandlerOffset(int vehicle, int offset, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    if (g_drawHandlerPtrOffset == 0) return false;

    __try {
        uintptr_t drawHandler = *(uintptr_t*)(vehAddr + g_drawHandlerPtrOffset);
        if (drawHandler == 0 || !IsValidPointer(drawHandler)) return false;

        *(float*)(drawHandler + offset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ============================================================================
// Wheel Structure Scanning - Find offsets with wheel-dimension-like values
// ============================================================================

// Storage for scan results
static char g_wheelScanResults[4096] = {0};
static char g_streamRenderScanResults[4096] = {0};

// SEH-safe float read for StreamRenderGfx scan
static float ReadStreamRenderFloatSafe(uintptr_t addr, int offset, bool* valid) {
    *valid = false;
    __try {
        float val = *(float*)(addr + offset);
        *valid = true;
        return val;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

// SEH-safe float write for StreamRenderGfx
static bool WriteStreamRenderFloatSafe(uintptr_t addr, int offset, float value) {
    __try {
        *(float*)(addr + offset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Probe StreamRenderGfx at specific offset
WF_API float WF_ProbeStreamRenderOffset(int vehicle, int offset) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return 0.0f;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
    if (streamRenderGfx == 0) return 0.0f;

    bool valid = false;
    return ReadStreamRenderFloatSafe(streamRenderGfx, offset, &valid);
}

// Write to StreamRenderGfx at specific offset
WF_API bool WF_WriteStreamRenderOffset(int vehicle, int offset, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) return false;

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
    if (streamRenderGfx == 0) return false;

    return WriteStreamRenderFloatSafe(streamRenderGfx, offset, value);
}

// Scan StreamRenderGfx structure for wheel dimension values
WF_API const char* WF_ScanStreamRenderGfx(int vehicle) {
    if (!InitializeOffsets()) {
        strcpy_s(g_streamRenderScanResults, "Not initialized");
        return g_streamRenderScanResults;
    }

    uintptr_t vehAddr = GetVehicleAddress(vehicle);
    if (vehAddr == 0) {
        strcpy_s(g_streamRenderScanResults, "Invalid vehicle");
        return g_streamRenderScanResults;
    }

    if (g_drawHandlerPtrOffset == 0) {
        PerformPatternScan();
    }

    uintptr_t streamRenderGfx = GetStreamRenderGfxInner(vehAddr);
    if (streamRenderGfx == 0) {
        strcpy_s(g_streamRenderScanResults, "StreamRenderGfx not found");
        return g_streamRenderScanResults;
    }

    std::stringstream ss;
    ss << "StreamRenderGfx at 0x" << std::hex << streamRenderGfx << std::dec << "\n";
    ss << "Scanning for wheel dimensions (0.2-1.0):\n";

    // Scan for floats in wheel dimension range
    for (int offset = 0; offset < 0x100; offset += 4) {
        bool valid = false;
        float val = ReadStreamRenderFloatSafe(streamRenderGfx, offset, &valid);

        if (!valid) continue;
        if (val != val) continue;  // NaN
        if (val == INFINITY || val == -INFINITY) continue;

        // Look for typical wheel size (0.5-0.8) or width (0.2-0.6)
        float absVal = val < 0 ? -val : val;
        if (absVal >= 0.2f && absVal <= 1.0f) {
            ss << "  0x" << std::hex << offset << std::dec << " = " << val << "\n";
        }
    }

    std::string result = ss.str();
    strncpy_s(g_streamRenderScanResults, result.c_str(), sizeof(g_streamRenderScanResults) - 1);
    return g_streamRenderScanResults;
}

// Inner function for safe memory read (SEH compatible - no C++ objects)
static float ReadWheelFloatSafe(uintptr_t wheelAddr, int offset, bool* valid) {
    *valid = false;
    __try {
        float val = *(float*)(wheelAddr + offset);
        *valid = true;
        return val;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API const char* WF_ScanWheelStructure(int vehicle, int wheelIndex) {
    if (!InitializeOffsets()) {
        strcpy_s(g_wheelScanResults, "Not initialized");
        return g_wheelScanResults;
    }

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) {
        strcpy_s(g_wheelScanResults, "Invalid wheel address");
        return g_wheelScanResults;
    }

    std::stringstream ss;
    ss << "Wheel " << wheelIndex << " at 0x" << std::hex << wheelAddr << std::dec << "\n";
    ss << "Offsets with values 0.1-1.0 (likely wheel dimensions):\n";

    // Scan from 0x000 to 0x200 looking for floats in wheel dimension range
    for (int offset = 0; offset < 0x200; offset += 4) {
        bool valid = false;
        float val = ReadWheelFloatSafe(wheelAddr, offset, &valid);

        if (!valid) continue;

        // Skip NaN and infinity
        if (val != val) continue;  // NaN check
        if (val == INFINITY || val == -INFINITY) continue;

        // Look for values typical of wheel dimensions (radius 0.3-0.5, width 0.1-0.4)
        if (val >= 0.1f && val <= 1.0f) {
            ss << "  0x" << std::hex << offset << std::dec << " = " << val << "\n";
        }
    }

    std::string result = ss.str();
    strncpy_s(g_wheelScanResults, result.c_str(), sizeof(g_wheelScanResults) - 1);
    return g_wheelScanResults;
}

// Probe a specific offset on wheel structure and write a delta
WF_API float WF_ProbeWheelOffset(int vehicle, int wheelIndex, int offset) {
    if (!InitializeOffsets()) return 0.0f;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return 0.0f;

    __try {
        return *(float*)(wheelAddr + offset);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0.0f;
    }
}

WF_API bool WF_WriteWheelOffset(int vehicle, int wheelIndex, int offset, float value) {
    if (!InitializeOffsets()) return false;

    uintptr_t wheelAddr = GetWheelAddress(vehicle, wheelIndex);
    if (wheelAddr == 0) return false;

    __try {
        *(float*)(wheelAddr + offset) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ============================================================================
// DLL Entry Point
// ============================================================================

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID lpReserved) {
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        // Perform pattern scan early
        PerformPatternScan();
        break;
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}
