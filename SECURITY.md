# Security & Privacy — ExtendedLSC

This document explains exactly what ExtendedLSC does to your system, why antivirus may flag it, and how
to report a problem. The short version: it is a **single-player GTA V mod that edits your own game's
memory and makes no network connections**.

## What it does (and why AV may flag it)
ExtendedLSC is a ScriptHookVDotNet 3 mod. To deliver real-time wheel fitment/stance and a manual
transmission, it reads and writes GTA V's process memory. Specifically:

- **Reads/writes vehicle memory** (`WheelMemory.cs`, `VehicleMemory.cs`) — wheel offset/camber/width and
  transmission gear/RPM/clutch fields of the vehicle you are driving. Every memory access is validated
  with `VirtualQuery` before dereferencing, so a bad pointer becomes a no-op instead of a crash.
- **Patches a few in-memory CPU instructions** (`ApplyShiftPatches`) — temporarily NOPs the game's
  automatic-shift sites so the manual gearbox can take over. These patches are **restored** when the
  feature is disabled.
- **P/Invokes `kernel32`/`psapi`** for the above memory operations.

Editing another process's memory and patching code in memory are exactly the behaviors antivirus
heuristics associate with cheats/trojans, so some engines may report `ExtendedLSC.dll` as `HackTool`,
`Riskware`, or a generic detection. **These are false positives.** The source is public — read it or
rebuild the DLL yourself (`dotnet build -c Release`).

## Data & privacy
- **No network access of any kind.** ExtendedLSC does not connect to the internet, phone home, or send
  telemetry. It cannot — there is no networking code in it.
- **Local files only.** It writes saved builds and settings to `<GTA install>\ExtendedLSC\` (e.g.
  `SaveData.json`, `Packages\*.json`, `stances.json`) and an optional `ExtendedLSC.log` (only when you
  enable logging). Nothing leaves your machine.

## Scope & safe use
- **Single-player only.** Do not use this (or any memory mod) in **GTA Online** — that risks a ban and is
  not supported.
- All memory changes affect only your current session/vehicle and are not written back to the game files.
- Run on a personal machine; managed/work machines with enterprise EDR may block memory-editing mods.

## Reporting a vulnerability or concern
If you find a security issue (e.g. a crash that could corrupt save data, or unexpected behavior), please
open a **private GitHub Security Advisory** on this repository, or a regular issue for non-sensitive
reports. Include your game build, the mod version, and steps to reproduce. Please do **not** post
exploit details publicly before they're addressed.
