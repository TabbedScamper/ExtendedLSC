# ExtendedLSC (ELSC)

**A complete reimagining of Los Santos Customs for single-player GTA V — and the first menu that lets vehicle modders organize their custom parts into their *own* named categories and ship that layout with their mod.**

Built with ScriptHookVDotNet 3 + LemonUI. Single-player / story mode only.

> ## ⚠️ Work In Progress — Pre-Release
> This mod is under active development and **not yet a final release**. Expect rough edges, occasional
> bugs, and changing behavior between builds. It is shared early so the community (and other developers)
> can use it, learn from it, and fork it. Please report issues — and read [`docs/DISCOVERIES.md`](docs/DISCOVERIES.md)
> before assuming something is broken.

---

## ✨ The features people have been asking for — for years

GTA V hard-locks custom vehicle parts into ~15 fixed mod slots (spoiler, hood, bumper, skirt…). For over
a decade that has forced modders to cram parts into the *wrong* category and forced players to hunt for
them. **ExtendedLSC fixes the things the scene has wanted forever:**

### 🏆 1. Modders can finally make their OWN categories — in-game, no code
ELSC's flagship **Edit Mode** (press **F6** in the menu) lets a vehicle modder:
- **Create their own named categories** ("A-Arms", "Nerf Bars", "Radiator Scoops"…) and **re-shelve any
  part from any game slot into them**, with custom names and prices — all in-game, no JSON, no recompiling.
- **Hide** the default categories they don't need, **rename** the ones they keep, and **remap** every control.
- **Ship the result with their vehicle.** The layout is saved as a small folder you drop into
  `scripts\ExtendedLSC\Vehicles\` — it auto-loads for that car for everyone who installs it.

This is the entire reason the mod exists. A car with 250+ parts crammed into 22 generic slots becomes a
clean, sensible menu organized exactly how the creator intended. **See the [PURE Quad example](examples/PURE-Quad).**

### 🔌 2. "Drop it in and it just works" config sharing
Ship a folder named after the **model** (`Vehicles/blazer4/`) and ELSC matches it by model hash —
**language-independent, no renaming, zero setup** for the end user.

### 🎮 3. Real, remappable controls with on-screen controller glyphs
A context-sensitive button-hint bar shows **real device glyphs that auto-switch between Xbox, PlayStation,
and keyboard**. Every binding — menu key, edit-mode keys, walk-around, manual transmission, nitrous — is
**remappable from a `[Controls]` section in the INI *and* from an in-game CONTROLS menu** (edit mode).

### 🧰 4. A faithful LSC clone, but better
Authentic Los Santos Customs experience with quality-of-life the original never had: live **preview-on-hover**,
custom **stat bars**, **ownership tracking**, clean category organization, and **no empty/stock-only menus**
(categories with nothing real in them simply don't show).

### 🏎️ 5. Deep vehicle systems
- **Manual transmission** — clutch model, arcade-style shift "kick", geometric gearing, a real rev limiter,
  and a **NOS** mechanic with tiers.
- **Wheel fitment / stance** — live offset / camber / width, with a stability auto-recovery system.
- **Walk-around camera** — part-focused presets + free roam to inspect your build.
- **Engine swaps**, **light/neon customization**, **custom plates**, and more.

---

## 🚗 Example included: the PURE Quad (250+ parts)

[`examples/PURE-Quad/`](examples/PURE-Quad) contains a real, shipping ELSC menu for **PURE** — a 250+ part
quad-customization mod (a GTA V recreation of Disney/Black Rock's *PURE*). It turns 251 parts crammed into
22 GTA slots into a clean menu: **Bodies → each body → its matching fenders / scoops / seats**, plus global
categories for engines, handlebars, brakes, and more. It's the proof of what ELSC enables.

> The PURE *vehicle* itself (a 600 MB+ add-on DLC) is distributed separately — only its ELSC **menu config**
> lives here, as the reference example.

---

## 📦 Install

See **[INSTALL.md](INSTALL.md)** for full steps. Quick version:

1. Install **ScriptHookV** + **ScriptHookVDotNet 3** (.NET Framework 4.8).
2. Copy into your GTA V `scripts\` folder — **if you don't have a `scripts` folder, create one** next to
   `GTA5.exe` (same folder as `ScriptHookV.dll`):
   - `ExtendedLSC.dll`, `LemonUI.SHVDN3.dll`, `Newtonsoft.Json.dll`, `NAudio.dll` (from `bin/Release/net48/`).
3. Launch story mode, get in a vehicle, press **F5** (or the menu opens automatically at a Los Santos Customs).

### ✅ Tested / required versions

ELSC was built and verified on:

| Component | Version |
|---|---|
| **GTA V (Legacy)** | build **1.0.3788.0** (v3788) |
| **ScriptHookV** | the build for **v3788** (Apr 2026) |
| **ScriptHookVDotNet 3** | **NIGHTLY `v3.7.0.167`** (API 3.7.0) — ⚠️ the stable **3.6.0** release was *not* enough; use the nightly |

> Get the SHVDN nightly from the [ScriptHookVDotNet releases/nightly page](https://github.com/scripthookvdotnet/scripthookvdotnet/releases).
> If ELSC doesn't load, a too-old SHVDN is the most common cause.

> **Antivirus note:** like all SHVDN memory-editing mods, ELSC may trip a false-positive. It is
> single-player only and makes **no network connections**. See [SECURITY.md](SECURITY.md).

---

## 📝 Changelog

### v1.1 — Performance pass
Focused on cutting ExtendedLSC's per-frame overhead (helps most on lower-end CPUs). These are pure
script-side optimizations — they work identically on **Legacy and Enhanced** (no edition-specific code changed):

- **Removed redundant per-frame work** building the mod's internal per-vehicle save-tags (plates, window
  tint, snapshots):
  - the vehicle **display-name** lookup (the heaviest) is now resolved **once and cached** — eliminated
    from the per-frame path;
  - the vehicle **model** lookup is cached per frame (**~70% fewer** of those calls).
- **Removed a per-frame memory allocation** in the stance system (a garbage-collection / micro-stutter source).
- **The player's current vehicle is resolved once per frame** instead of several times.
- Net: ~10 fewer engine calls per frame in typical play.

> If you still hit frame drops on 1.1, please report it — there's a small read-only capture tool that can
> measure exactly what's happening on your machine so the cause can be pinned down precisely.

### v1.0
Initial release.

---

## 🛠️ For modders & developers

- **[docs/CUSTOM-MENUS.md](docs/CUSTOM-MENUS.md)** — how to build and ship a custom menu for your vehicle
  (in-game edit mode *or* hand-edited JSON), the config format, and how to find a part's slot + index.
- **[docs/DISCOVERIES.md](docs/DISCOVERIES.md)** — the technical knowledge base: everything we reverse-
  engineered and every gotcha we hit (GTA's mod-slot system, the re-shelving trick, model-hash matching,
  parsing `carcols.meta`, LemonUI scaleforms, on-screen keyboards, and a long list of traps). **Start here
  if you want to extend or fork ELSC.**
- **[STUDY-NOTES.md](STUDY-NOTES.md)** — handling/stat formulas and manual-transmission research.
- **[docs/VStancer_ReverseEngineering.md](docs/VStancer_ReverseEngineering.md)** — wheel-fitment memory work.

Build it yourself: `dotnet build ExtendedLSC.csproj -c Release` (output in `bin/Release/net48/`).

---

## 🗺️ Status & roadmap

**Working now:** core LSC clone, edit mode (create/rename/hide/re-shelve), config sharing by model, control
remapping + glyph bar, manual transmission + NOS, wheel fitment, walk-around camera, engine swaps, the PURE
example.

**Still rough / planned:** broader vehicle testing, more universal wheel packs, progression-gated tuning,
polish passes. This is pre-release — see the WIP banner above.

---

## 🙏 Credits

- **ExtendedLSC** by TabbedScamper.
- Built on **ScriptHookVDotNet 3** and **LemonUI** (by Guad / Lemon).
- PURE example vehicle modeled by Disney / Black Rock Studios; GTA V conversion by TabbedScamper.

## License

See [LICENSE](LICENSE). Forks and contributions welcome — this mod is meant to be a base others can build on.
