# Ship a Custom ELSC Menu With Your Vehicle

ExtendedLSC lets you organize your vehicle's parts into your **own named categories** and ship that layout
as a tiny folder. Players drop it into `scripts\ExtendedLSC\Vehicles\` and it auto-loads for your car.

There are two ways to build one: **in-game Edit Mode** (no JSON) or **hand-edited JSON**.

---

## Option A — In-game Edit Mode (recommended, no code)

1. In `scripts\ExtendedLSC\settings.ini`, set `EditorMode = true` (under `[Developer]`).
2. Get in your vehicle, open the menu (**F5**), then press **F6** to enter Edit Mode (text turns yellow).
3. Now you can:
   - **Add a Category** — appears at the bottom of any menu in edit mode.
   - **Add a Part** — pick parts from the game slots; check several and add them at once.
   - **A / Enter on a part** — rename it and set its price (pre-filled; press Enter to keep).
   - **F2** on a category — rename it (per-vehicle).
   - **Delete** on a category — custom: delete it (parts return to defaults); built-in: hide it
     (Delete again to restore).
   - **CONTROLS** entry — remap any key/button in-game.
4. Everything saves automatically to `scripts\ExtendedLSC\Vehicles\{yourCar}\`. Zip that folder and ship it.

The default categories you don't need can simply be **hidden** (Delete in edit mode), and any single-slot
category automatically gets a **"None"** option so players can revert that part to stock.

---

## Option B — Hand-edited JSON

### Folder layout
```
scripts\ExtendedLSC\Vehicles\
  blazer4\                       ← name it after the MODEL (best) or the in-game display name
    _config.json                 ← optional: hidden categories / renames
    Bodies\                      ← a category = a folder
      items.json                 ← the parts in this category
      Body 03\                   ← categories can nest as deep as you like
        items.json
        Front Fenders\
          items.json
    Engines\
      items.json
```

> **Name the folder after the model** (`blazer4`, `adder`, `t20`…). ELSC matches it by model hash, so it
> works in every language and never collides with other cars that share a display name.

### `items.json` format
```jsonc
{
  "MenuTitle": "Front Fenders",          // optional; defaults to the folder name
  "Description": "Front fenders for Body 3.",
  "Items": [
    {
      "Name": "Front Fender 2",
      "Description": "Optional hover text.",
      "Price": 1500,                      // 0 = Free
      "Value": 9,                          // the part's index within its slot
      "SourceModType": 2                   // the real game mod slot (modType)
    }
  ]
}
```
- `SourceModType` = the GTA mod slot the part actually lives in.
- `Value` = the part's 0-based index in that slot. `Value: -1` = stock (but you don't need to add this —
  ELSC adds a **"None"** automatically to any category that drives one slot).
- For wheels, use `"WheelType": N` instead of `SourceModType` (see Universal Wheels below).

### `_config.json` (optional, per-vehicle)
```jsonc
{
  "HiddenCategories": ["Windows", "Plate", "Livery", "Horn"],  // default categories to hide for this car
  "CategoryRenames": { "Skirts": "Side Nerfs" }                // rename a built-in category
}
```

---

## How do I find a part's `SourceModType` and `Value`?

Every cosmetic part is `(modType, index)`. Two reliable ways:

**1. From the live game (easiest, authoritative).** Sit in the vehicle and, for each slot, read
`GET_NUM_VEHICLE_MODS(veh, modType)`. Then `SET_VEHICLE_MOD(veh, modType, index)` and look at the car to see
what each index is. ELSC's preview-on-hover already does this for you in edit mode.

**2. From `carcols.meta`.** Extract the vehicle's `dlc.rpf`, open `common/data/carcols.meta`, and walk
`<visibleMods>`. Each part's `<type>` (a `VMT_*`) maps to a modType (see
[DISCOVERIES.md §1](DISCOVERIES.md)); the `Value` is the part's 0-based position **within its VMT type**.
Watch out for commented-out `<!-- <Item> -->` parts — the game skips them, so don't count them.

> The PURE example in [`examples/PURE-Quad/`](../examples/PURE-Quad) was generated exactly this way (parse
> `carcols.meta` → validate counts against the live game → emit `items.json`). It's a good template.

---

## Universal wheel packs

Wheels are global, not per-vehicle. A wheel pack ships **one** folder that applies to every car:
```
scripts\ExtendedLSC\Universal\Wheels\{Your Category}\items.json
```
with items using `"WheelType": N` (the GTA wheel-type enum) and `"Value": index`.

---

## Tips

- Hide the defaults you don't use; rename the ones you keep.
- Keep category names **distinct from built-ins** (don't name one "Bumpers" or "Brakes" — use
  "Front Bumpers", "Brake Kits"). Built-in names get skipped.
- Test in-game: a quick check that a part applies confirms your `(SourceModType, Value)` is right.
- Ship the whole `Vehicles\{yourCar}\` folder. That's it.
