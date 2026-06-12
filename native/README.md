# WheelFitmentAPI - Native ASI for Wheel Memory Access

This is a native C++ ASI plugin that provides safe access to wheel memory structures
that cannot be accessed from .NET/SHVDN due to protected memory regions.

## Why is this needed?

.NET scripts using SHVDN run in a managed memory sandbox. While they can read/write
to direct offsets from a vehicle's base address (like gear, RPM, clutch), they cannot
safely follow pointer chains to access wheel data.

Attempting to access wheel memory from .NET causes game crashes because:
1. The wheels pointer (CVehicle + 0xB48) points to protected memory
2. Each wheel in the array is another pointer that must be dereferenced
3. .NET's Marshal operations crash when accessing these regions

This ASI uses native C++ with ScriptHookV's `getScriptHandleBaseAddress()` to safely
access the same memory that VStancer.asi uses.

## Building

### Prerequisites

1. **Visual Studio 2019 or 2022** with C++ Desktop Development workload
2. **CMake 3.15+**
3. **ScriptHookV SDK** - Download from http://www.dev-c.com/gtav/scripthookv/

### Build Steps

1. Extract ScriptHookV SDK to `native/ScriptHookV_SDK/`:
   ```
   native/
   ├── ScriptHookV_SDK/
   │   ├── inc/
   │   │   ├── main.h
   │   │   ├── natives.h
   │   │   └── types.h
   │   └── lib/
   │       └── ScriptHookV.lib
   └── WheelFitmentAPI/
       ├── WheelFitmentAPI.h
       ├── WheelFitmentAPI.cpp
       └── CMakeLists.txt
   ```

2. Build with CMake:
   ```cmd
   cd native/WheelFitmentAPI
   mkdir build
   cd build
   cmake .. -G "Visual Studio 17 2022" -A x64
   cmake --build . --config Release
   ```

3. Copy `WheelFitmentAPI.asi` to your GTA V scripts folder.

### Alternative: Visual Studio Project

If you prefer using Visual Studio directly:

1. Create new C++ DLL project
2. Add source files from WheelFitmentAPI/
3. Set include path to ScriptHookV_SDK/inc
4. Link against ScriptHookV_SDK/lib/ScriptHookV.lib and psapi.lib
5. Set output extension to .asi

## Installation

1. Build `WheelFitmentAPI.asi`
2. Copy to: `Grand Theft Auto V/scripts/WheelFitmentAPI.asi`
3. Ensure `ScriptHookV.dll` is also installed (required dependency)

## API Reference

### Initialization
- `WF_GetVersion()` - Get API version string
- `WF_IsAvailable()` - Check if API initialized successfully
- `WF_GetLastError()` - Get last error message

### Wheel Position
- `WF_GetWheelCount(vehicle)` - Get number of wheels
- `WF_GetWheelXOffset(vehicle, wheelIndex)` - Get lateral position (track width)
- `WF_SetWheelXOffset(vehicle, wheelIndex, value)` - Set lateral position
- `WF_GetWheelYOffset/SetWheelYOffset` - Longitudinal position
- `WF_GetWheelZOffset/SetWheelZOffset` - Vertical position (ride height)

### Camber
- `WF_GetFrontCamber(vehicle)` - Get front camber from handling data
- `WF_SetFrontCamber(vehicle, value)` - Set front camber
- `WF_GetRearCamber/SetRearCamber` - Rear camber

### High-Level
- `WF_ApplyFitment(vehicle, data)` - Apply complete fitment settings
- `WF_ResetToStock(vehicle)` - Reset to original wheel positions

## C# Integration

Use `WheelFitmentNative.cs` in ExtendedLSC:

```csharp
// Initialize
WheelFitmentNative.Log = msg => GTA.UI.Notification.Show(msg);

if (WheelFitmentNative.IsAvailable)
{
    // Read wheel positions
    int wheelCount = WheelFitmentNative.GetWheelCount(vehicle);
    float frontLeftX = WheelFitmentNative.GetWheelXOffset(vehicle, 0);

    // Apply fitment
    var fitment = new WheelFitmentNative.FitmentData
    {
        FrontCamber = 0.05f,
        RearCamber = 0.08f,
        FrontTrackWidthDelta = 0.03f,
        RearTrackWidthDelta = 0.04f
    };
    WheelFitmentNative.ApplyFitment(vehicle, fitment);
}
else
{
    // API not available - show message to user
    GTA.UI.Notification.Show($"WheelFitmentAPI not found: {WheelFitmentNative.LastError}");
}
```

## Offset Notes

The ASI uses pattern scanning to find offsets, similar to VStancer:

| Data | Offset | Description |
|------|--------|-------------|
| Wheels Pointer | ~0xB48 | CVehicle + offset → wheel array pointer |
| Wheel Position X | 0x20 | CWheel + offset = lateral position |
| Wheel Position Y | 0x24 | CWheel + offset = longitudinal position |
| Wheel Position Z | 0x28 | CWheel + offset = vertical position |
| Front Camber | 0x034C | CHandlingData + offset |
| Rear Camber | 0x0350 | CHandlingData + offset |

Offsets may vary between game versions. Pattern scanning ensures compatibility.

## License

MIT License - Free to use in any project.
