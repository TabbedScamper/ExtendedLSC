# ExtendedLSC — How to use it

After install (see `INSTALL.md`), load into story mode, get in a vehicle, and press **F5**.

> Single-player only. All changes are to your own vehicle in story mode.
> ⚠️ Pre-release / work in progress — see the [README](README.md).

---

## Opening & navigating the menu
- **F5** — open / close the menu (remappable: `MenuKey` in `settings.ini` `[Controls]`, or in-game).
- Navigate with the standard menu controls (arrow keys / D-pad, Enter / A to select, Backspace / B to go
  back). A button-hint bar at the bottom shows the current actions with **real controller glyphs** that
  auto-switch between Xbox / PlayStation / keyboard.

---

## Features

### 1. Vehicle customization
A deeper, cleaner LSC menu — categories for paint, wheels, body, lights, performance, and more, with
pricing, **ownership tracking**, and **live preview-on-hover**. Empty or stock-only categories are hidden
automatically. Builds can be **saved** and re-loaded (stored as JSON in `<GTA>\ExtendedLSC\Packages\`).

### 2. Edit Mode — make your own categories (the flagship)
Set `EditorMode = true` in `settings.ini` (`[Developer]`), open the menu, and press **F6**. You can:
- **Add a Category** and **re-shelve** any part from any slot into it, with custom names/prices.
- **F2** to rename a category, **Delete** to hide a built-in one (or delete a custom one), **A/Enter** to
  rename + re-price a part.
- Open the **CONTROLS** entry to remap any key/button in-game.
- Everything saves to `scripts\ExtendedLSC\Vehicles\<your car>\` — ship that folder with your vehicle.

Full modder guide: [docs/CUSTOM-MENUS.md](docs/CUSTOM-MENUS.md).

### 3. Wheel fitment & stance
Dial in the look in real time: **offset / poke**, **camber**, and **width**, with live preview and a
stability auto-recovery system.
> This reads/writes a guarded vehicle-memory path validated against the current build. If a future game
> patch shifts memory layout, fitment may need an update while the rest of the menu keeps working.

### 4. Manual transmission + NOS
A clutch-based manual gearbox with an arcade-style shift "kick", geometric gearing, a real rev limiter, and a
**NOS** nitrous mechanic (tiers). Enable/configure it in `settings.ini` `[Features]` (`ManualTransmission`,
`Nos`, and the `Mt*` feel values). Shift / neutral / nitrous keys are all remappable in `[Controls]`.

### 5. Walk-around camera
Press the walk-around button (default **Y** on controller) to step around the car with part-focused presets
and free roam, so you can inspect your build.

---

## Configuration recap (`scripts\ExtendedLSC\settings.ini`)
| Section | What it controls |
|---|---|
| `[Features]` | Turn optional systems on/off (camera, manual transmission, fitment, NOS…) + MT feel values |
| `[Controls]` | Remap every key/button (`MenuKey`, `EditModeKey`, walk-around, shift, nitrous…) |
| `[UI]` | `ScrollIndicators`, `ShowRPM` |
| `[Developer]` | `EditorMode` (modder edit mode), `DebugLogging` (writes `ExtendedLSC.log`) |

All bindings can also be remapped **in-game** from the CONTROLS menu (Edit Mode).

---

## Troubleshooting
- **F5 does nothing** → confirm ScriptHookV + ScriptHookVDotNet3 are loading other mods, and that
  `ExtendedLSC.dll`, `LemonUI.SHVDN3.dll`, and `Newtonsoft.Json.dll` are all in `scripts\`.
- **A control doesn't respond** → check `[Controls]` in `settings.ini` for a conflicting/remapped binding.
- **Fitment looks wrong after a game update** → memory offsets may have shifted with the patch; report it.
  The rest of the menu is unaffected.
- **Need a log** → set `DebugLogging = true` in `settings.ini` (`[Developer]`), reproduce, and check
  `ExtendedLSC.log` next to `GTA5.exe`.
