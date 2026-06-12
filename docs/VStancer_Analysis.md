# VStancer.asi Complete Analysis

## How VStancer Works (Reverse Engineered)

### 1. Core Architecture

VStancer is a **native C++ ASI plugin** that:
- Loads via ScriptHookV's ASI loader
- Uses `getScriptHandleBaseAddress()` to get vehicle memory addresses
- Pattern scans game memory to find offsets dynamically
- Writes float values directly to game memory using SSE instructions

### 2. ScriptHookV Functions Used

```cpp
getScriptHandleBaseAddress  // Gets memory address of a game entity (vehicle)
getGlobalPtr                // Access global script variables
worldGetAllVehicles         // Enumerate all vehicles
nativeInit / nativePush / nativeCall  // Call game natives
scriptRegister / scriptWait // Script lifecycle
getGameVersion              // Detect game version for compatibility
```

### 3. Pattern Scanning

VStancer finds memory offsets by searching for byte patterns:

**Wheels Pointer Offset:**
```
Pattern: 4C 8B ?? ?? ?? 00 00 48 8B 40 20 48 8B 80 B0 00 00 00
Mask:    xx????xxxxxxxxxxxxxxxxxx
```

**Wheel Suspension Compression:**
```
Pattern: 45 0F 57 C9 F3 0F 11 83 ?? ?? 00 00
Mask:    xxx?xxx???xxxxx
```

**Handling Offset:**
```
Pattern: 88 90 ?? ?? ?? 00 0F B7 90 ?? 00 00 00
Mask:    xxxx????xxx???
```

**StreamRenderWheelWidth/Size:**
```
Pattern: F3 44 0F 10 ?? ?? ?? 00 00
Mask:    xx????xx
```

### 4. What VStancer Modifies

**Suspension Properties (per wheel):**
- FrontCamber / RearCamber - wheel tilt angle
- FrontTrackWidth / RearTrackWidth - wheel lateral position
- FrontHeight / RearHeight - suspension height
- VisualHeight - visual ride height

**Wheel Rendering:**
- VisualWheelSize - rendered wheel diameter
- VisualWheelWidth - rendered wheel width
- VisualWheelType - wheel type index
- VisualModIndex - mod index for wheel

**Tire Properties:**
- FrontTyreRadius / RearTyreRadius
- FrontTyreWidth / RearTyreWidth
- FrontRimRadius / RearRimRadius

### 5. Memory Write Technique

VStancer uses **SSE float instructions** to write values:
- `MOVSS` (F3 0F 11) - Store single-precision float
- `MOVSS` (F3 0F 10) - Load single-precision float

Total in binary: 282 float stores, 278 float loads

### 6. Offset Storage Format (INI Files)

```ini
[Suspension]
FrontCamber = -0.007414      # Radians-like value
FrontHeight = -0.446395      # Meters
FrontTrackWidth = 0.735620   # Meters (absolute position)
RearCamber = 0.004943
RearHeight = -0.446395
RearTrackWidth = 0.725620
VisualHeight = 0.000000

[Wheels]
FrontTyreRadius = 0.353651
FrontTyreWidth = 0.224500
FrontRimRadius = 0.224500
...
```

### 7. Why .NET/SHVDN Can't Do This

| VStancer (C++) | SHVDN (.NET) |
|----------------|--------------|
| Direct pointer arithmetic | Marshal.ReadInt32 (slow, unsafe) |
| Native SSE instructions | No SSE access |
| Same process memory space | Managed memory sandbox |
| Pattern scan with raw pointers | Marshal.Copy crashes on protected regions |
| `getScriptHandleBaseAddress` native | `vehicle.MemoryAddress` (wrapper, may differ) |

### 8. Wheel Memory Structure (Discovered Offsets)

```
CVehicle structure:
+0xB48 (or 0xB40) = Pointer to wheel array

CWheel structure (per wheel):
+0x020 = Position X (float) - lateral offset
+0x024 = Position Y (float) - longitudinal offset
+0x028 = Position Z (float) - vertical offset
+0x160 = Suspension compression (float)
+0x168 = Angular velocity (float)
+0x1C4 = Steering angle (float)
+0x1E0 = Health (float)

CHandlingData structure:
+0x034C = fCamberFront (float)
+0x0350 = fCamberRear (float)

StreamRender structure (visual):
+0x??? = Wheel width (float) - found via pattern
+0x??? = Wheel size (float) - found via pattern
```

### 9. To Replicate in C++

```cpp
// Get vehicle address
Vehicle vehicle = PED::GET_VEHICLE_PED_IS_IN(playerPed, false);
uintptr_t vehicleAddr = (uintptr_t)getScriptHandleBaseAddress(vehicle);

// Get wheels pointer (offset found via pattern scan)
uintptr_t* wheelsPtr = (uintptr_t*)(vehicleAddr + wheelsOffset);

// Access individual wheel
uintptr_t wheel0 = wheelsPtr[0];  // Front left
uintptr_t wheel1 = wheelsPtr[1];  // Front right
uintptr_t wheel2 = wheelsPtr[2];  // Rear left
uintptr_t wheel3 = wheelsPtr[3];  // Rear right

// Modify wheel X position (track width)
float* wheelPosX = (float*)(wheel0 + 0x20);
*wheelPosX = newTrackWidthValue;

// Modify suspension compression
float* suspComp = (float*)(wheel0 + suspensionOffset);
*suspComp = newHeightValue;
```

### 10. Decompilation Findings (Ghidra Analysis)

From analyzing the decompiled VStancer.asi (500 functions, 36,000+ lines):

**Native Function Calls Identified:**
- `0x1d132d614dd86811` - Entity/Player related
- `0xf1307ef624a80d87` - GET_GAME_TIMER
- `0x11fe353cf9733e6f` - GET_HASH_KEY (vehicle model hashing)
- `0x91aef906bca88877` - IS_DISABLED_CONTROL_PRESSED (menu navigation)
- `0xe2587f8cbbd87b1d` - Control input checking
- `0x67c540aa08e4a6f5` - Audio/sound playing
- `0x51669f7d1fb53d9f` - Script/game state
- `0xfe99b66d079cf6bc` - DISABLE_CONTROL_ACTION

**Version Detection:**
VStancer contains version strings for compatibility:
- v1_0_335_2, v1_0_350_2, v1_0_372_2, v1_0_393_2
- v1_0_463_1, v1_0_505_2, v1_0_573_1, v1_0_617_1
- v1_0_678_1, v1_0_757_2, v1_0_791_2, v1_0_877_1
- v1_0_944_2, v1_0_1011_1, v1_0_1032_1, v1_0_1103_2

**Config Structure (from INI files):**
```ini
[ID]
ModelHash = C0A79989      ; FNV-1a hash of model name
ModelName = toysupmk4

[Suspension]
FrontCamber = -0.007414   ; Radians (negative = inward tilt)
FrontHeight = -0.446395   ; Meters (negative = lowered)
FrontTrackWidth = 0.735620; Meters (absolute wheel X position)
RearCamber = 0.004943
RearHeight = -0.446395
RearTrackWidth = 0.725620
VisualHeight = 0.000000

[Wheels]
FrontTyreRadius = 0.353651
FrontTyreWidth = 0.224500
FrontRimRadius = 0.224500
RearTyreRadius = 0.353651
RearTyreWidth = 0.224500
RearRimRadius = 0.224500
VisualWheelSize = 0.730000
VisualWheelWidth = 0.560000
VisualWheelType = 0
VisualModIndex = 6

[Modifications]
Identifiers = 1_0,1_1,1_2   ; Per-mod overrides
1_0_FrontTrackWidth = 0.025  ; Delta from base
```

### 11. Integration Options for ExtendedLSC

Since .NET cannot safely access wheel memory, here are viable integration approaches:

#### Option A: VStancer INI Integration (Recommended)
1. Detect if VStancer.asi is installed
2. Read/write VStancer's INI config files
3. Let VStancer apply the actual modifications
4. Pros: No crashes, works with existing VStancer
5. Cons: Requires VStancer installed

```csharp
// Example: Write VStancer config
string configPath = Path.Combine(
    "scripts", "VStancer", "Configs",
    $"{vehicleModel}.ini");

var ini = new IniFile(configPath);
ini.Write("Suspension", "FrontCamber", camberValue.ToString());
ini.Write("Suspension", "FrontTrackWidth", trackWidth.ToString());
// VStancer will pick up changes on next interval (5000ms default)
```

#### Option B: Native C++ ASI Companion
1. Create a small C++ ASI that exposes wheel modification functions
2. SHVDN calls the ASI via shared memory or named pipe
3. ASI does the actual memory writes safely
4. Pros: Full control, no VStancer dependency
5. Cons: Requires C++ development

#### Option C: Handling Data Only (Limited)
Only modify camber via handling data (safe in SHVDN):
```csharp
// This is the ONLY safe wheel-related modification in SHVDN
vehicle.HandlingData.CamberStiffness = value;
// But CamberStiffness affects physics, not visual camber angle
```

### 12. Conclusion

VStancer works because:
1. Native C++ has unrestricted memory access
2. Pattern scanning finds correct offsets for any game version
3. Direct float writes via SSE are fast and reliable
4. ScriptHookV provides the bridge to game internals

**For ExtendedLSC, the recommended approach is Option A (VStancer INI Integration):**
- No crashes
- Leverages existing, well-tested code
- Users likely already have VStancer installed
- Full feature compatibility

The wheel fitment code in ExtendedLSC should be refactored to:
1. Check if VStancer is installed
2. If yes: Read/write VStancer INI files for the current vehicle
3. If no: Display message that VStancer is required for wheel fitment
4. Remove all direct memory access code
