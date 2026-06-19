# ExtendedLSC — Install

A single-player GTA V mod that supercharges **Los Santos Customs**: a deep vehicle-customization menu that
lets modders **organize custom parts into their own named categories** (Edit Mode), **wheel fitment /
stance** (offset, camber, width), a **manual transmission** with a clutch + NOS, a **walk-around camera**,
and fully **remappable controls with on-screen device glyphs**. SHVDN3 / .NET Framework 4.8.

> **⚠️ Work in progress / pre-release** — expect rough edges. See the [README](README.md) for the full
> feature list, and [docs/CUSTOM-MENUS.md](docs/CUSTOM-MENUS.md) if you're a modder shipping a menu.

> Single-player only. Run offline in story mode.

---

## Prerequisites
1. **GTA V (Legacy build)** — story mode.
2. **ScriptHookV** (Alexander Blade) — `ScriptHookV.dll` + `dinput8.dll` in the GTA root.
3. **ScriptHookVDotNet 3** (`ScriptHookVDotNet3.asi` + the .NET runtime) in the GTA root.
4. **.NET Framework 4.8** (ships with Windows 10/11).

---

## Install (copy files)
Copy these three DLLs (from `bin\Release\net48\`) into your GTA **`scripts\`** folder:
```
scripts\
  ExtendedLSC.dll
  LemonUI.SHVDN3.dll
  Newtonsoft.Json.dll
```
(`ScriptHookVDotNet3.dll` is also in that folder for reference but normally comes from your SHVDN3
install — don't duplicate it if you already have it.)

The mod creates its own **`scripts\ExtendedLSC\`** folder on first run for its settings, saved builds
(`Packages\*.json`), and an optional custom banner (`banner.png`). Nothing else to copy.

That's it — launch GTA, load into story mode, and the mod is active.

---

## Configure (`scripts\ExtendedLSC\settings.ini`)
On first run the mod generates **`scripts\ExtendedLSC\settings.ini`** — a documented INI with four sections.
Edit it and reload the script (or restart) to apply.

- **`[Features]`** — toggle optional systems on/off: `CustomCamera`, `ManualTransmission`, `VehicleTuning`,
  `LightCustomization`, `VStancerIntegration`, `WheelFitmentProMode`, `Nos`, and the manual-transmission feel
  values (`MtKickForce`, `MtLimiterRpm`, …). Set the ones you don't want to `false`.
- **`[Controls]`** — remap **every** binding: `MenuKey` (default `F5`), the edit-mode keys
  (`EditModeKey=F6`, `RenameCategoryKey=F2`, `DeleteCategoryKey=Delete`, `DebugMenuKey=F7`), walk-around and
  shift/nitrous bindings. Keyboard keys by name (`F5`, `Delete`, `NumPad0`…); controller buttons by index.
  On-screen glyphs auto-match your device. You can also remap everything **in-game** from the CONTROLS menu
  in Edit Mode.
- **`[UI]`** — `ScrollIndicators`, `ShowRPM`.
- **`[Developer]`** — `EditorMode` (Edit Mode for modders; default `false`) and `DebugLogging`
  (writes `ExtendedLSC.log`; default `false`).

> There is no separate `ExtendedLSC.ini` — `settings.ini` is the only config file.

## Edit Mode (for modders)
Set `EditorMode = true` in `settings.ini` (`[Developer]`), open the menu, and press **F6**. You can create
your own categories, re-shelve/rename parts, hide default categories, and ship the result with your vehicle.
Full guide: [docs/CUSTOM-MENUS.md](docs/CUSTOM-MENUS.md).

---

## Verify it loaded
1. Get in a vehicle (or drive into an LSC).
2. Press **F5** — the ExtendedLSC menu should appear.
3. If nothing happens: confirm ScriptHookV + ScriptHookVDotNet3 load other mods, and that the three DLLs
   above are in `scripts\`. Set `DebugLogging = true` in `settings.ini` (`[Developer]`) and check
   `ExtendedLSC.log` (next to `GTA5.exe`).

---

## ⚠️ Antivirus false positives
Like most ScriptHookVDotNet mods, ExtendedLSC **reads and writes GTA V's memory** (that's how live
stance/fitment and the manual transmission work). To a virus scanner that behavior looks like a cheat
tool, so some antivirus engines may flag `ExtendedLSC.dll` as `HackTool` / `Riskware` / a generic trojan.
**These are false positives** — the mod is single-player only, touches nothing but your own game, and
makes no network connections.

If your AV quarantines it:
- The full source is public — read it or build the DLL yourself (`dotnet build -c Release`).
- Add your GTA V install folder to your antivirus exclusions.
- See `SECURITY.md` for exactly what memory the mod touches and why.

Do **not** disable your antivirus wholesale — an exclusion for the GTA folder is enough.

## Notes
- **Stance/fitment** adjusts wheel offset/camber/width in real time. It uses a guarded memory path
  validated against the current build; if a future game patch changes layout, fitment may need an
  update while the rest of the menu keeps working.
- Saved builds persist in `ExtendedLSC\Packages\` so you can re-apply a setup to a vehicle later.

See **`USAGE.md`** for how to drive every feature.
