# ExtendedLSC Study Notes - Pattern Comparison

## Overview
Comparing ExtendedLSC (4,840 lines) against reference mods to identify simplifications and best practices.

---

## 1. MENU SYSTEM COMPARISON

### Our Approach (ExtendedLSC)
```csharp
// Complex nested tracking dictionaries
private Dictionary<int, NativeMenu> modMenusByIndex = new Dictionary<int, NativeMenu>();
private Dictionary<string, NativeMenu> configMenusByPath = new Dictionary<string, NativeMenu>();
private Dictionary<NativeItem, int> itemOwnershipStatus = new Dictionary<NativeItem, int>();
private Dictionary<NativeItem, (string categoryPath, int itemValue, int price)> configItemMetadata;
```

### los_Santos_v2 Approach (SIMPLER)
```csharp
// Single ObjectPool + direct submenu references
private ObjectPool pool;
private NativeMenu upgradeMenu;
private NativeMenu paintSubMenu;
private NativeMenu engineSubMenu;
// etc - flat, simple structure
```

### **SIMPLIFICATION OPPORTUNITY #1**
- **Remove** `modMenusByIndex` dictionary - just store direct references
- **Remove** `configMenusByPath` - use `menu.Shown` event for refresh instead
- **Remove** `configItemMetadata` - store data in NativeItem.Tag property instead

---

## 2. NATIVE BANNER USAGE

### Our Approach
```csharp
// Custom PNG loading with file system access
private CustomSprite customBanner = null;
string bannerPath = Path.Combine(scriptsDir, "ExtendedLSC", "banner.png");
customBanner = new CustomSprite(bannerPath, ...);
```

### los_Santos_v2 Approach (NATIVE GTA TEXTURES)
```csharp
// Uses built-in GTA texture - no file loading needed!
new ScaledTexture(
    PointF.Empty,
    new SizeF(431, 107),
    "shopui_title_carmod",  // Native GTA texture dictionary
    "shopui_title_carmod"   // Native GTA texture name
)
```

### **SIMPLIFICATION OPPORTUNITY #2**
- **Option A**: Use native `shopui_title_carmod` texture (zero file dependencies)
- **Option B**: Keep custom banner but load via DLC YTD (already have this!)
- Current code tries BOTH which is redundant

---

## 3. MENU STATE REFRESH

### Our Approach
```csharp
// Complex refresh with metadata tracking
private void RefreshConfigMenuStatus(string categoryPath, int newInstalledValue)
{
    if (!configMenusByPath.TryGetValue(categoryPath, out var menu)) return;
    foreach (var menuItemBase in menu.Items)
    {
        if (!configItemMetadata.TryGetValue(menuItem, out var metadata)) continue;
        // ... complex logic
    }
}
```

### los_Santos_v2 Approach (EVENT-BASED)
```csharp
// Use menu.Shown event - automatically refreshes when opened
headlightSubMenu.Shown += (sender, args) =>
{
    Vehicle vehicle = Game.Player.Character.CurrentVehicle;
    if (vehicle != null && vehicle.Exists())
    {
        bool xenonEnabled = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, vehicle, 22);
        foreach (var item in headlightItems)
        {
            if (xenonEnabled && originalTitles[item].StartsWith("Install"))
                item.Title = "Installed";
            else
                item.Title = originalTitles[item];
        }
    }
};
```

### **SIMPLIFICATION OPPORTUNITY #3**
- Use `menu.Shown` event instead of manual refresh tracking
- Store original titles in simple dictionary, update on open
- No need for complex metadata tracking

---

## 4. VEHICLE MOD ENUMERATION

### Our Approach
```csharp
// Manual mod type checking
for (int modIndex = 0; modIndex <= 48; modIndex++)
{
    int numMods = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, veh, modIndex);
    if (numMods > 0) { /* add to menu */ }
}
```

### GtaVehiclePersistence Approach (CLEANER)
```csharp
// Use SHVDN's built-in enumeration
Mods = vehicle.Mods.ToArray()
    .Select(mod => new MyVehicleMod() {
        Type = mod.Type,
        Index = mod.Index,
        FriendlyName = mod.LocalizedTypeName
    })
    .ToList();
```

### **SIMPLIFICATION OPPORTUNITY #4**
- Use `vehicle.Mods.ToArray()` instead of manual loop
- SHVDN already handles the 0-48 iteration internally
- Get localized names for free via `LocalizedTypeName`

---

## 5. MOD APPLICATION

### Our Approach
```csharp
// Direct native calls scattered throughout
Function.Call(Hash.SET_VEHICLE_MOD, vehicle, modType, modIndex, false);
```

### GtaVehiclePersistence Approach (STRUCTURED)
```csharp
// Always call SET_VEHICLE_MOD_KIT first!
GTA.Native.Function.Call(GTA.Native.Hash.SET_VEHICLE_MOD_KIT, vehicle, 0);
foreach (var myMod in Mods)
{
    GTA.Native.Function.Call(GTA.Native.Hash.SET_VEHICLE_MOD, vehicle, myMod.Type, myMod.Index, false);
}
```

### **POTENTIAL BUG CHECK**
- Are we calling `SET_VEHICLE_MOD_KIT` before applying mods?
- This is REQUIRED for mods to work properly
- Reference code ALWAYS calls it first

---

## 6. COLOR SYSTEM

### Our Approach
Currently using config files for colors.

### los_Santos_v2 Approach (COMPREHENSIVE)
```csharp
// Static dictionaries with Color + Spec values
public static readonly Dictionary<string, VehicleColorInfo> ClassicColors = new Dictionary<string, VehicleColorInfo>()
{
    { "Black", new VehicleColorInfo { Color = 0, Spec = 0 } },
    { "Black Graphite", new VehicleColorInfo { Color = 147, Spec = 0 } },
    // ... 100+ colors defined
};

public static readonly Dictionary<string, VehicleColorInfo> MetallicColors = ...;
public static readonly Dictionary<string, VehicleColorInfo> MatteColors = ...;
public static readonly Dictionary<string, VehicleColorInfo> Metals = ...;
public static readonly Dictionary<string, VehicleColorInfo> ChromeColors = ...;
```

### **ENHANCEMENT OPPORTUNITY**
- los_Santos_v2 has complete color definitions with proper Spec values
- Could copy their color dictionaries for accuracy
- Spec value affects metallic shine

---

## 7. INPUT HANDLING

### Our Approach (COMPLEX)
```csharp
// 150+ lines of custom input acceleration logic
private int inputHeldSince = 0;
private int inputLastRepeat = 0;
private int inputRepeatCount = 0;
private int inputInitialDelay = 82;
private int inputSlowRepeat = 255;
private int inputFastRepeat = 83;
private int inputAccelThreshold = 3;
// ... complex timing logic
```

### LemonUI Built-in
```csharp
// LemonUI has HeldTime property
menu.HeldTime = 150;  // ms before repeat starts
```

### **SIMPLIFICATION OPPORTUNITY #5**
- LemonUI already handles input acceleration
- We disabled it with `HeldTime = 999999`
- Either use LemonUI's built-in OR our custom - not both
- If keeping custom, remove LemonUI's and simplify

---

## 8. SOUNDS

### Our Approach
Not using sounds consistently.

### LemonUI Built-in (from source)
```csharp
public static readonly Sound DefaultActivatedSound = new Sound("HUD_FRONTEND_DEFAULT_SOUNDSET", "SELECT");
public static readonly Sound DefaultCloseSound = new Sound("HUD_FRONTEND_DEFAULT_SOUNDSET", "BACK");
public static readonly Sound DefaultUpDownSound = new Sound("HUD_FRONTEND_DEFAULT_SOUNDSET", "NAV_UP_DOWN");
public static readonly Sound DefaultLeftRightSound = new Sound("HUD_FRONTEND_DEFAULT_SOUNDSET", "NAV_LEFT_RIGHT");
public static readonly Sound DefaultDisabledSound = new Sound("HUD_FRONTEND_DEFAULT_SOUNDSET", "ERROR");
```

### **ENHANCEMENT OPPORTUNITY**
- LemonUI plays these automatically when enabled
- Make sure we're not blocking LemonUI's sound system

---

## 9. NEON HANDLING

### Our Approach
Using config files.

### los_Santos_v2 Approach (DIRECT)
```csharp
// Check neon state
bool frontNeon = Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, vehicle, 2);
bool backNeon = Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, vehicle, 3);
bool leftNeon = Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, vehicle, 0);
bool rightNeon = Function.Call<bool>(Hash.GET_VEHICLE_NEON_ENABLED, vehicle, 1);

// Neon indices: 0=Left, 1=Right, 2=Front, 3=Back
```

### **REFERENCE NOTE**
- Neon indices: Left=0, Right=1, Front=2, Back=3
- Use `GET_VEHICLE_NEON_ENABLED` to check state
- Use `SET_VEHICLE_NEON_ENABLED` to toggle

---

## 10. XENON/TOGGLE MOD HANDLING

### los_Santos_v2 Approach
```csharp
// Check if xenon is installed
bool xenonEnabled = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, vehicle, 22);

// Toggle xenon
Function.Call(Hash.TOGGLE_VEHICLE_MOD, vehicle, 22, true);  // Install
Function.Call(Hash.TOGGLE_VEHICLE_MOD, vehicle, 22, false); // Remove
```

### **REFERENCE NOTE**
- Mod index 22 = Xenon Headlights
- Mod index 18 = Turbo
- Use `IS_TOGGLE_MOD_ON` to check, `TOGGLE_VEHICLE_MOD` to set

---

## PRIORITY SIMPLIFICATIONS

### HIGH PRIORITY (Reduce Complexity)
1. **Replace dictionary tracking** with `menu.Shown` events
2. **Use native banner** `shopui_title_carmod` as fallback
3. **Use `vehicle.Mods.ToArray()`** instead of manual loop

### MEDIUM PRIORITY (Code Quality)
4. **Verify `SET_VEHICLE_MOD_KIT`** is called before all mod applications
5. **Simplify input handling** - use LemonUI's or custom, not hybrid
6. **Extract color definitions** from los_Santos_v2

### LOW PRIORITY (Polish)
7. **Add proper sound effects** using LemonUI's Sound class
8. **Consolidate debug modes** (already done!)

---

## FILE SIZE COMPARISON

| Mod | Main File Lines | Notes |
|-----|-----------------|-------|
| **ExtendedLSC** | 4,840 | Complex, many features |
| **los_Santos_v2** | ~2,800 | Single file, simpler |
| **GtaVehiclePersistence** | ~300 | Clean, focused |
| **LemonUI NativeMenu** | 1,904 | Framework source |

### Target: Reduce Main.cs to ~3,000 lines by removing redundant tracking

---

## NATIVE FUNCTION REFERENCE

```csharp
// Mod Kit (MUST call first!)
Hash.SET_VEHICLE_MOD_KIT  // (vehicle, 0) - enables mod system

// Standard Mods
Hash.GET_NUM_VEHICLE_MODS // (vehicle, modType) -> count
Hash.SET_VEHICLE_MOD      // (vehicle, modType, modIndex, variation)
Hash.GET_VEHICLE_MOD      // (vehicle, modType) -> current index
Hash.REMOVE_VEHICLE_MOD   // (vehicle, modType)

// Toggle Mods (Turbo=18, Xenon=22)
Hash.IS_TOGGLE_MOD_ON     // (vehicle, modType) -> bool
Hash.TOGGLE_VEHICLE_MOD   // (vehicle, modType, toggle)

// Neons (0=Left, 1=Right, 2=Front, 3=Back)
Hash.SET_VEHICLE_NEON_ENABLED    // (vehicle, index, toggle)
Hash.GET_VEHICLE_NEON_ENABLED    // (vehicle, index) -> bool
Hash.SET_VEHICLE_NEON_COLOUR     // (vehicle, r, g, b)
Hash.GET_VEHICLE_NEON_COLOUR     // (vehicle, &r, &g, &b)

// Colors
Hash.SET_VEHICLE_COLOURS              // (vehicle, primary, secondary)
Hash.SET_VEHICLE_EXTRA_COLOURS        // (vehicle, pearlescent, wheel)
Hash.SET_VEHICLE_MOD_COLOR_1          // (vehicle, paintType, color, pearl)
Hash.SET_VEHICLE_MOD_COLOR_2          // (vehicle, paintType, color)
Hash.SET_VEHICLE_INTERIOR_COLOUR      // (vehicle, color)
Hash.SET_VEHICLE_DASHBOARD_COLOUR     // (vehicle, color)

// Window Tint
Hash.SET_VEHICLE_WINDOW_TINT          // (vehicle, tintIndex)
Hash.GET_VEHICLE_WINDOW_TINT          // (vehicle) -> tintIndex

// Wheels
Hash.SET_VEHICLE_WHEEL_TYPE           // (vehicle, wheelType)
Hash.GET_VEHICLE_WHEEL_TYPE           // (vehicle) -> wheelType

// License Plate
Hash.SET_VEHICLE_NUMBER_PLATE_TEXT    // (vehicle, text)
Hash.SET_VEHICLE_NUMBER_PLATE_TEXT_INDEX // (vehicle, styleIndex)
```

---

## NEXT STEPS

1. Review this document
2. Decide which simplifications to implement
3. Create backup branch before changes
4. Implement changes incrementally with testing
