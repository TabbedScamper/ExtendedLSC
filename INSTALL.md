# ExtendedLSC — Install

A single-player GTA V mod that supercharges **Los Santos Customs**: a deep vehicle-customization menu,
**wheel fitment / stance** (offset, camber, width), a **manual transmission** with a clutch, and a
custom **NFS-style drag HUD** with a NOS mechanic. SHVDN3 / .NET Framework 4.8.

> Single-player only. Run offline in story mode.

---

## Prerequisites
1. **GTA V (Legacy build)** — story mode.
2. **ScriptHookV** (Alexander Blade) — `ScriptHookV.dll` + `dinput8.dll` in the GTA root.
3. **ScriptHookVDotNet 3** (`ScriptHookVDotNet3.asi` + the .NET runtime) in the GTA root.
4. **.NET Framework 4.8** (ships with Windows 10/11).

---

## Install (copy files)
Copy these into your GTA **`scripts\`** folder:
```
scripts\
  ExtendedLSC.dll
  ExtendedLSC.ini
  LemonUI.SHVDN3.dll
  Newtonsoft.Json.dll
```
(All four are in `bin\Release\net48\`. `ScriptHookVDotNet3.dll` is also there for reference but normally
comes from your SHVDN3 install — don't duplicate it if you already have it.)

Then copy the **`hud\`** folder (the drag-HUD gauge art) next to `GTA5.exe` so the path is:
```
<GTA install>\ExtendedLSC\hud\        ← tach_face.png, tach_needle.png, gear_tab.png,
                                         nos_dot.png, nos_dot_off.png, gd_0..9 / gd_N / gd_R, …
```
Without this folder the drag HUD won't draw (everything else still works; the log will say
`[DragHUD] assets MISSING`).

The mod also creates this same **`ExtendedLSC\`** folder for saved builds (`ExtendedLSC\Packages\*.json`)
and an optional custom banner (`ExtendedLSC\banner.png`) on its own.

That's it — launch GTA, load into story mode, and the mod is active.

---

## Configure (`ExtendedLSC.ini`)
```ini
[General]
MenuKey=F5            ; open the menu when near / in a vehicle
AutoOpenInLSC=true    ; auto-open inside Los Santos Customs zones

[Menu]
Position=TopLeft      ; TopLeft | TopRight | BottomLeft | BottomRight
BannerPath=           ; optional custom banner image

[Debug]
EnableLogging=false   ; leave off for normal play (on = writes ExtendedLSC.log)
ShowDebugInfo=false
```

---

## Verify it loaded
1. Get in a vehicle (or drive into an LSC).
2. Press **F5** — the ExtendedLSC menu should appear.
3. If nothing happens: confirm ScriptHookV + ScriptHookVDotNet3 load other mods, and that all four DLLs
   above are in `scripts\`. Set `EnableLogging=true` and check `ExtendedLSC.log` (next to `GTA5.exe`).

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
