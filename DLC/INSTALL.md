# ExtendedLSC Add-On DLC (`elsc`)

Add-on DLC that ships ELSC's modified assets so users **never have to hand-edit `vehshare.ytd`** or
any base game file. Currently carries the **clear/whitened vehicle glass texture** required for the live
custom window-color feature (windshield left untouched so it stays clear). Extensible for future ELSC
assets (data files, meta, additional textures).

## How it works
GTA's window-tint glass is a darkening multiply over the shared `vehicle_generic_glasswindows2` texture.
That texture's RGB is near-black (a green cast), so colored tints muddy to black. This DLC swaps in a copy
of `vehshare.ytd` whose **tintable** glass panes are whitened (windshield kept original), so the live tint
color shows vividly. ELSC then writes custom RGB into the in-memory window-color table at runtime.

The DLC overrides `vehshare.ytd` purely by **load order** — exactly how Rockstar's own `patch2023_02`
ships its `vehshare.ytd` over the base game. No base files are modified; fully removable.

## Install (Single Player, GTA V Legacy)
1. Copy the `elsc` folder into:
   `...\Grand Theft Auto V\mods\update\x64\dlcpacks\elsc\`
   (use the `mods` folder via OpenIV; if you don't use a `mods` folder, the real `update\x64\dlcpacks\`)
2. Open `mods\update\update.rpf\common\data\dlclist.xml` in OpenIV (edit mode) and add this line at the
   **very end** of the `<Paths>` list (last = loads last = overrides vanilla):
   ```xml
   <Item>dlcpacks:/elsc/</Item>
   ```
3. Save, launch the game.

## Verify
- Windshield: normal clear glass (no milky haze).
- Side/rear windows: clear on "None"; take vivid color when ELSC applies a custom tint.

## Uninstall
- Delete the `elsc` folder and remove the `dlclist.xml` line. Base game untouched.

## Build notes (for maintainers)
- Source: `patch2023_02` `vehshare.ytd` (latest-loading vanilla copy, 54 textures).
- Texture swapped: index 7 `vehicle_generic_glasswindows2` (1024x1024 DXT5/BC3) ->
  `vehicle_generic_glasswindows2_WHITE_v2.dds` (tintable panes whitened, windshield y<400 kept original).
- Rebuilt headlessly with CodeWalker.Core: `YtdFile` load -> `DDSIO.GetTexture` -> replace data_items[7] ->
  `YtdFile.Save` -> `RpfFile.CreateNew(...OPEN)` + `CreateFile`. Loose modified ytd is in `DLC\_build\`.
- To re-bake the texture: re-run the PIL whiten + texconv BC3, then the CodeWalker swap/pack steps.
