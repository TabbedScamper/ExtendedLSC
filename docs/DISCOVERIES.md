# ExtendedLSC — Discoveries & Technical Knowledge Base

Everything we reverse-engineered, figured out, and got burned by while building ExtendedLSC. If you're
extending or forking this mod (human or AI), **read this first** — it will save you days. Nothing here is
secret; it's hard-won practical knowledge about GTA V's vehicle-mod system, SHVDN3, and LemonUI.

> Context: ELSC targets **GTA V Legacy** (build ~b3788 era), ScriptHookVDotNet 3, LemonUI.SHVDN3 2.1.1,
> .NET Framework 4.8.

---

## 1. GTA V's vehicle mod system (the foundation)

A vehicle's parts live in numbered **mod slots** ("mod types"). The key natives:

```csharp
SET_VEHICLE_MOD_KIT(veh, 0);                 // REQUIRED before any mod read/write. Always call it first.
int n   = GET_NUM_VEHICLE_MODS(veh, modType);// how many parts exist in this slot
SET_VEHICLE_MOD(veh, modType, index, false); // apply part `index`  (index = -1 -> STOCK / remove)
int cur = GET_VEHICLE_MOD(veh, modType);      // currently-installed index (-1 = stock)
string lbl = GET_MOD_TEXT_LABEL(veh, modType, index); // GXT label key for the part (often empty/NULL)
```

- **`index = -1` means "stock"** for that slot. Applying it reverts/removes the part. This is how ELSC's
  auto-"None" option works.
- **`SET_VEHICLE_MOD_KIT(veh, 0)` is mandatory** before mod operations or they silently no-op.

### Mod-slot (modType) integer map

The visual/standard slots:

| # | Slot | # | Slot |
|--|--|--|--|
| 0 | Spoiler | 9 | Right Fender (Wing R) |
| 1 | Front Bumper | 10 | Roof |
| 2 | Rear Bumper | 11 | Engine (perf) |
| 3 | Side Skirt | 12 | Brakes (perf) |
| 4 | Exhaust | 13 | Transmission (perf) |
| 5 | Frame / Chassis | 14 | Horn |
| 6 | Grille | 15 | Suspension |
| 7 | Hood (Bonnet) | 16 | Armor |
| 8 | Fender (Wing L) | 23 / 24 | Front / Rear Wheels |

The **Benny's / extended slots** (used heavily by detailed add-ons) keep going: `25 plate holder,
26 vanity plate, 32 seats, 34 knob, 35 plaque, 37 trunk, 38 hydraulics, 39 engine bay 1,
40 engine bay 2, 42 chassis2, 43 chassis3, 44 chassis4, 45 chassis5, 46 door L, 48 livery`. Mod
creators repurpose these freely (e.g. PURE puts handlebars in *plaque*, shocks in *trunk*, frames in
*engine bay 1*).

### `carcols.meta` `<type>` (VMT_) → modType integer

A vehicle's mod kit is defined in `carcols.meta` under `<Kits><Item><visibleMods>`. Each part has a
`<modelName>`, `<modShopLabel>` (a GXT key), and a `<type>` like `VMT_SPOILER`. The **index** the game
uses for `SET_VEHICLE_MOD` is the **0-based position of that part within its VMT type**, in file order.

VMT → modType we verified:

```
VMT_SPOILER 0   VMT_BUMPER_F 1  VMT_BUMPER_R 2  VMT_SKIRT 3    VMT_EXHAUST 4
VMT_CHASSIS 5   VMT_GRILL 6     VMT_BONNET 7    VMT_WING_L 8   VMT_WING_R 9   VMT_ROOF 10
VMT_SEATS 32    VMT_KNOB 34     VMT_PLAQUE 35   VMT_TRUNK 37
VMT_ENGINEBAY1 39  VMT_ENGINEBAY2 40  VMT_CHASSIS2 42  VMT_CHASSIS3 43
VMT_CHASSIS4 44    VMT_CHASSIS5 45    VMT_DOOR_L 46
```

⚠️ **Multiple `carcols` sections can share one VMT type** — the index is continuous across them in file
order. Always compute the index per VMT type across the whole `<visibleMods>` block, not per section.

---

## 2. The re-shelving trick (ELSC's flagship)

GTA gives ~15 fixed cosmetic slots, so modders dump parts wherever they fit. ELSC lets you move any part
into any *named* category **without touching the vehicle files**.

A menu item carries two extra fields:

```jsonc
{ "Name": "Front Fender 2", "Price": 1500, "Value": 9, "SourceModType": 2 }
//  SourceModType = the real game slot (modType)   Value = the index within that slot
```

Selecting the item just calls `SET_VEHICLE_MOD(veh, SourceModType, Value)`. So a "Front Fender" displayed
under a custom "Body 3" category actually drives rear-bumper slot 2, index 9 — the part the creator put
there.

**Hiding the original is *derived*, never stored separately** (so it can't desync):
`GetReshelvedParts()` scans every `items.json` under `Vehicles/{model}/` for `SourceModType` parts and
builds a `HashSet` of `(slot<<20 | value)` keys. A built-in category skips any part whose key is in that
set, and **auto-hides entirely when its visible count hits 0**.

Two bugs we hit here (both fixed, both easy to reintroduce):
- **Recurse all the way down.** `GetReshelvedParts` must walk *every* sub-folder
  (`Directory.GetFiles(root, "items.json", SearchOption.AllDirectories)`). When categories are nested
  (`Bodies/Body 03/Front Fenders/`), a shallow scan misses them and the default category keeps showing.
- **Apply the visible-count gate to the Benny's slots too.** The loop that builds extended categories
  (slots 24–45) originally gated on raw `GET_NUM_VEHICLE_MODS`; it must gate on the **visible** count
  (after re-shelving) or you get "Stock"-only ghost categories (Plaque, Trunk, Air Filter…).

---

## 3. Per-vehicle configs that "just work"

Configs live in `scripts\ExtendedLSC\Vehicles\{folder}\`. The folder is resolved by, in order:
1. an exact match on the vehicle's **display name**, else
2. a folder whose **Jenkins-one-at-a-time hash (joaat, lowercased) equals the model hash**.

Rule 2 is the important one: **name the folder after the model (`blazer4`)** and it matches regardless of
game language and regardless of display-name collisions (many vehicles share a display name). `DisplayName`
is localized and unreliable; the model hash is stable.

```csharp
// joaat must match GTA's: lowercase, the classic add/shift/xor mix.
// Verified: joaat("blazer4") == 0xE5BA6858 == the model hash.
```

Gotcha: `GET_DISPLAY_NAME_FROM_VEHICLE_MODEL` returns the **gameName** from `vehicles.meta` (e.g. `"PURE"`),
*not* the model name, and if that gameName has no GXT entry the on-screen name is unreliable. Match by model
hash.

---

## 4. Parsing a mod's `carcols.meta` to auto-generate a menu

To build a menu config from a vehicle's kit:
1. Extract the `dlc.rpf` (we used **GTAUtil** `extractarchive -i dlc.rpf -o out`).
2. Read `common/data/carcols.meta`, walk `<visibleMods>`, record `(section-comment, VMT type, modelName)`
   and assign each a running 0-based index **per VMT type**.
3. Map VMT → modType (table above) and emit `items.json` with `SourceModType`/`Value`.

**The trap that cost us:** mod authors **comment parts out** (`<!-- <Item>…</Item> -->`) when the game
can't load them all (PURE: "some radiator scoops had to be sacrificed"). A naive regex over `<modelName>`
counts the commented-out ones too, so your indices drift. **Strip comment blocks that contain
`<modelName>` before parsing** (keep section-header comments like `<!--RADIATOR_SCOOPS-->`).

**Always validate against the live game.** Spawn/sit in the vehicle and compare
`GET_NUM_VEHICLE_MODS(veh, modType)` per slot to your parsed counts. That's how we caught the radiator-scoop
mismatch (parsed 44, live 18) and confirmed the other 19 slots were perfect.

---

## 5. LemonUI specifics

- **`NativeMenu.Subtitle` is obsolete → use `.Name`.** (The constructor's 2nd arg is still the subtitle;
  read it back via `.Name`.) We use it as a stable identifier to re-open the same menu after a rebuild.
- **On-screen button hints with real device glyphs:** `LemonUI.Scaleform.InstructionalButtons`. Build with
  `new InstructionalButton(text, GTA.Control.X)` for auto device-matched glyphs; `.Add/.Clear/.Update()`
  then `.Draw()` each frame. F-keys aren't `GTA.Control`s, so they get **no native glyph** — render those
  as text instead.
- **Coordinate canvas:** LemonUI draws on a `(1080 * aspectRatio) × 1080` virtual canvas. Screen-center X
  is `1080f * Screen.AspectRatio / 2f`, not a hardcoded 960.
- **Menu position survives a rebuild** by snapshotting the open menu's `.Name` + `SelectedIndex` before
  tearing menus down and re-opening the match afterward (instead of dumping the user back to root).

---

## 6. On-screen keyboard (text/number entry)

```csharp
DISPLAY_ONSCREEN_KEYBOARD(0, "FMMC_KEY_TIP8", "", defaultText, "", "", "", maxLen);
// then poll each tick:
int s = UPDATE_ONSCREEN_KEYBOARD();   // 1 = accepted, 2 = cancelled, 0/3 = still open
if (s == 1) string result = GET_ONSCREEN_KEYBOARD_RESULT();
```

The 4th argument **pre-fills** the box — we use it so editing a name/price starts from the current value
(press Enter to keep it). While a keyboard is open, `DisableAllControlsThisFrame()` and keep a guard flag so
keystrokes don't leak into the menu underneath.

---

## 7. Autonomous in-game testing (how this mod was largely built/QA'd)

ExtendedLSC publishes live menu state and was developed against a small **TCP bridge** (a separate ASI)
that exposes `call_native_by_name`, `send_keys`, and `reload_scripts`. This let an AImake changes, hot-reload
the DLL (simulated **Insert** key), drive the menu, and read back game state — a tight feedback loop. If you
build something similar, the lessons:

- **Pointer-argument natives crash the bridge** (`DELETE_VEHICLE`, `SET_*_AS_NO_LONGER_NEEDED`). Avoid them;
  spawn at the player's coords and leave old entities rather than deleting.
- **Never send `Esc`** to close a LemonUI menu — it opens the pause menu and can kill the socket. Back out
  with **Backspace**.
- Native args are passed as **plain values** (`[veh, modType]`), not typed dicts, in our bridge.
- The socket **times out while the game is paused** — do before/after reads, not mid-pause.
- `GET_NUM_VEHICLE_MODS` + `GET_VEHICLE_MOD` round-trips are the fastest way to validate a config end-to-end.

---

## 8. Performance

- **`VirtualQuery` is ~0.5 ms per call** inside GTA's process. Validating a memory region on *every field
  access* in a per-frame loop tanked us from 124 → 20 FPS. **Cache validated regions per frame.**
- Don't rebuild scaleforms every frame — only when their content changes (track a signature string).
- SHVDN loads script DLLs **from bytes**, so `Assembly.Location` is empty. Use
  `AppDomain.CurrentDomain.BaseDirectory` for file paths next to the DLL.

---

## 9. Gotchas grab-bag

- **Custom category names must not collide with built-in ones.** ELSC skips dynamic categories whose name
  matches a built-in "special" category (Bumpers, Brakes, Engine, Roof…). Name yours distinctly
  ("Front Bumpers", "Brake Kits") or your parts vanish.
- **`Value = -1` is stock/revert** — ELSC auto-injects a "None" option for any single-slot custom category
  so the user can revert from inside it. Don't treat `-1` as a real re-shelved part (exclude it from the
  hidden-set).
- **Empty menus are clutter.** Skip rendering a category/sub-category with no real items and no
  non-empty descendants.
- **Alt-tab freezes vehicle physics** — relevant when testing stance/limiter behavior.
- Window/door slot 46 vs. window-tint naming can collide; livery is slot 48.

---

## 10. Where things live in the code

| Concern | File / symbol |
|--|--|
| Main script, menu build, OnTick | `src/Main.cs` |
| Per-vehicle config, re-shelving, hide, model-hash match | `src/MenuConfig.cs` |
| INI + remappable `[Controls]` | `src/ModSettings.cs` |
| Manual transmission feel, NOS, limiter | `src/ManualTransmission/` |
| Wheel fitment / stance + memory offsets | `src/WheelFitment/` |
| Stat formula, pricing, categories | `src/ModPricing.cs`, `src/ModCategories.cs` |
| Menu config build/restore | `BuildCategoryMenu`, `RebuildMenusForVehicle`, `DisplayItemsFor`, `GetReshelvedParts` |

See also [STUDY-NOTES.md](../STUDY-NOTES.md) (handling/stat formulas, MT research) and
[CUSTOM-MENUS.md](CUSTOM-MENUS.md) (the modder-facing how-to).
