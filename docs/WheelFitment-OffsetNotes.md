# Wheel Fitment — Offset Notes (historical RE notes)

## Overview
A pattern-scanning fitment ASI finds memory offsets dynamically, making it work across different game versions. This is why hardcoded offsets in our implementation keep failing.

## Key Offsets such a mod Finds

### Vehicle Structure
- **Wheels Pointer Offset** - Pointer to array of wheel pointers
- **Wheel Count Offset** - Number of wheels on vehicle
- **Handling Offset** - Pointer to handling data

### Wheel Structure (per wheel)
- **Wheel Suspension Compression Offset** - Suspension height
- **Wheel Angle Offset** - Wheel rotation/camber
- **Wheel Angular Velocity Offset** - Spinning speed
- **Wheel Steering Angle Offset** - Current steering
- **Wheel Brake Offset** - Brake force
- **Wheel Power Offset** - Engine power to wheel
- **Wheel Health Offset** - Damage state
- **Wheel Load Offset** - Weight on wheel
- **Wheel Traction Vector X/Y/Length Offset** - Traction forces
- **Wheel Flags Offset** - Various flags
- **Wheel Drive Flags Offset** - Drive type flags
- **Wheel Material Type Offset** - Surface material
- **Wheel Downforce Offset** - Aerodynamic downforce
- **Wheel Overheat Offset** - Thermal state
- **Wheel Steering Multiplier Offset** - Steering ratio

### Visual/Rendering (KEY for visual changes)
- **StreamRenderGfxPtrOffset** - Graphics render pointer
- **StreamRenderWheelSizeOffset** - Visual wheel diameter
- **StreamRenderWheelWidthOffset** - Visual wheel width
- **DrawHandlerPtrOffset** - Draw handler pointer

## Config File Keys (from INI)
```ini
[Suspension]
FrontCamber = -0.007414
FrontHeight = -0.446395
FrontTrackWidth = 0.735620
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
```

## Pattern Scanning Examples

the reference fitment mod scans for byte patterns like:
```
Wheels Pointer: 4C 8B ?? ?? ?? 00 00 48 8B 40 20 48 8B 80 B0 00 00 00
Handling Offset: 88 90 ?? ?? ?? 00 0F B7 90 ?? 00 00 00
StreamRenderWheelWidth: F3 44 0F 10 ?? ?? ?? 00 00 F3 44 0F 10 ?? ?? ?? 00 00
```

The `??` are wildcards that match any byte.

## Why Our Implementation Failed

1. **Hardcoded offsets** (0xB48, 0xB40) only work for specific game versions
2. **No pattern scanning** - we guessed offsets instead of finding them dynamically
3. **Wrong structure access** - we tried wheel struct positions, the reference fitment mod uses rendering offsets

## Recommended Approach

### Option 1: Pattern Scanning (Complex but Robust)
Implement pattern scanning like the reference fitment mod does:
1. Scan game memory for known byte patterns
2. Calculate offsets from pattern match locations
3. Cache offsets for the session

### Option 2: Use Handling Data (Simpler)
Use SHVDN's HandlingData class which provides:
- `vehicle.HandlingData.MemoryAddress` - base address
- Known offsets: 0x034C (FrontCamber), 0x0350 (RearCamber)
- This only affects physics, not visual wheel angle

### Option 3: Hook into the reference fitment mod
If the fitment ASI is present, read its config files and let it do the work.

## References
- [GTAVManualTransmission Offsets.hpp](https://github.com/ikt32/GTAVManualTransmission/blob/master/Gears/Memory/Offsets.hpp)
- (external FiveM fitment reference)
- [Ghidra](https://github.com/NationalSecurityAgency/ghidra/releases) - for further reverse engineering
