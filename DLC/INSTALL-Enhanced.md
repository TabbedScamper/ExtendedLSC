# ExtendedLSC Glass DLC — Enhanced (`elsc`)

Same add-on as the Legacy DLC, but the `vehshare.ytd` inside must be **Gen9** (Enhanced) format —
a Gen8/Legacy ytd will CRASH Enhanced. Only the one glass texture differs from vanilla; everything
else is the game's own ytd.

## Build the Gen9 vehicles.rpf (CodeWalker GUI — the headless tool can't write Gen9)
1. Open Enhanced's `vehshare.ytd` in CodeWalker (the one you extracted).
2. Select texture **`vehicle_generic_glasswindows2`** (index 7) → **Import** your whitened DXT5 dds
   (`vehicle_generic_glasswindows2.dds`, 1024x1024 DXT5) → **Save** the ytd (stays Gen9).
3. Create a new RPF named **`vehicles.rpf`** (OPEN / Enhanced format), add the modified `vehshare.ytd`.
4. Drop that `vehicles.rpf` into `elsc/x64/levels/gta5/` (replacing the PUT_..._HERE.txt marker).

## Install (no base files overwritten)
The shell (`content.xml`, `setup2.xml`) is already staged at:
`...\Grand Theft Auto V Enhanced\mods\update\x64\dlcpacks\elsc\`
(OpenRPF mods folder — mirrors the game path, fully removable.)

Register it so the game loads it (same as Legacy, same as your B-Rims Enhanced DLCs):
- Add to the dlclist used by your Enhanced setup:
  ```xml
  <Item>dlcpacks:/elsc/</Item>
  ```
  at the END of `<Paths>` (last = loads last = overrides vanilla `vehshare.ytd` by load order).

## Verify
- Windshield: clear (no milky haze).
- Side/rear windows: clear on "None"; take VIVID color when ELSC applies a custom glass tint.

## Uninstall
- Delete the `elsc` folder + remove the dlclist line. Base game untouched.
