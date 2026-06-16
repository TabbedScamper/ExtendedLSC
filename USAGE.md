# ExtendedLSC — How to use it

After install (see `INSTALL.md`), load into story mode, get in a vehicle, and press **F5**. Inside Los
Santos Customs zones the menu can auto-open (`AutoOpenInLSC=true`).

> Single-player only. All changes are to your own vehicle in story mode.

---

## Opening & navigating the menu
- **F5** — open / close the menu (configurable in `ExtendedLSC.ini`).
- Navigate with the standard menu controls (arrow keys / numpad, Enter to select, Backspace to go back).
- Menu position is set by `Position=` in the INI (`TopLeft` / `TopRight` / `BottomLeft` / `BottomRight`).

---

## Features

### 1. Vehicle customization
A deeper LSC menu — categories for paint, wheels, body, performance, and more, with pricing and
ownership tracking. Builds you apply can be **saved** and re-loaded: they're stored as JSON in
`<GTA>\ExtendedLSC\Packages\`, so you can re-apply a setup to a vehicle later.

### 2. Wheel fitment & stance
Dial in the look in real time from the menu: **offset / poke**, **camber**, and **width**. Use it for
flush fitment or an aggressive stretched-and-poked stance. Adjustments preview live on the car.
> This reads/writes a guarded vehicle-memory path validated against the current build. If a future game
> patch shifts memory layout, fitment may need an update while the rest of the menu keeps working.

### 3. Manual transmission
A clutch-based manual gearbox for a more involved driving feel — shift through gears yourself instead of
the automatic box. Configure/enable it from the menu.

### 4. Drag HUD + NOS (NFS-style)
A curved rev tach, gear indicator, and a **NOS** gauge on the right of the screen — styled after
NFS Underground drag racing. The NOS dots light up as boost builds; redline highlights warn you to
shift. (Requires the `ExtendedLSC\hud\` art from install; if missing, the HUD just won't draw.)

---

## Configuration recap (`ExtendedLSC.ini`)
| Key | Effect |
|---|---|
| `MenuKey=F5` | menu open/close key |
| `AutoOpenInLSC=true` | auto-open inside LSC zones |
| `Position=TopLeft` | on-screen menu corner |
| `BannerPath=` | optional custom menu banner |
| `EnableLogging=false` | turn on only to debug (writes `ExtendedLSC.log` next to `GTA5.exe`) |

---

## Troubleshooting
- **F5 does nothing** → confirm ScriptHookV + ScriptHookVDotNet3 are loading other mods, and that
  `ExtendedLSC.dll`, `LemonUI.SHVDN3.dll`, and `Newtonsoft.Json.dll` are all in `scripts\`.
- **Drag HUD doesn't show** → the `ExtendedLSC\hud\` folder (gauge PNGs) is missing or misplaced; it
  must sit next to `GTA5.exe`. Turn on logging and look for `[DragHUD] assets MISSING`.
- **Fitment looks wrong after a game update** → memory offsets may have shifted with the patch; report
  it. The rest of the menu is unaffected.
- **Need a log** → set `EnableLogging=true`, reproduce, and check `ExtendedLSC.log` next to `GTA5.exe`.
