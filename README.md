# TSViewMod — Tormented Souls camera & map mod (beta)

An RE2 Remake-style over-the-shoulder camera and a "you are here" map marker for **Tormented Souls** (PC,
Unity 2020.3 / Mono).

> **Beta.** Expect glitches: camera clipping in some spots, odd-looking enemy animations from behind, an
> occasionally misplaced map dot. Please report anything you find in Issues (an F9 snapshot helps).
>
> **Platforms:** Linux (Proton) is tested on the GOG build. Windows is supported but hasn't been tested on a
> native Windows install yet; reports welcome.

## Features

- **Third-person camera** on top of the original fixed angles, switchable in-game. Spring-arm collision, auto
  shoulder swap, auto follow when she runs curves, and black instead of the void where the set has no geometry.
  Puzzle close-ups, cutscenes, enemy grabs and deaths still use the game's own cameras.
  (First person: coming soon.)
- **Options → View Mod** page in the game's own menu style: camera, distance, height, shoulder, field of view,
  stick and mouse speed, invert Y, auto follow, map marker. Fully usable with a controller.
- **Map marker:** a red dot at your actual position on the map, with an arrow showing which way she faces, and
  the current room tinted red.
- **RE2-style Reset View while inspecting items.** Maps turn to lie flat and face you. A prompt appears in the
  button bar.
- **Mouse and keyboard:** pointer captured while you walk, 1:1 mouse look, and camera-relative WASD movement
  (walk, or run while holding Run).

No BepInEx or other loader, and no game code is patched: it's one DLL plus two small entries in Unity's startup
lists.

## Install

You must own the game. Close it before installing. Download `TSViewMod-<version>.zip` from Releases and unzip it.

**Windows:** double-click `install.bat`. It finds the game in Steam or GOG Galaxy (or unzip next to
`TormentedSouls.exe`, or it asks for the folder). To remove: run `install.bat uninstall` from a command prompt.

**Linux (Proton):** requires Python 3.

```sh
python3 install.py install --game "/path/to/Tormented Souls"
python3 install.py uninstall --game "/path/to/Tormented Souls"   # to remove
```

Both installers copy `TSViewMod.dll` into `TormentedSouls_Data/Managed` and register it in
`ScriptingAssemblies.json` and `RuntimeInitializeOnLoads.json` (originals kept as `*.tsviewmod-orig`). They also
add the pre-computed map fits, so the marker works in every room straight away.

**From source:** `dotnet build TSViewMod -c Release -p:GameManaged="<game>/TormentedSouls_Data/Managed"`, then run
an installer from `installer/`.

## Controls

| Input | Action |
|---|---|
| Options → View Mod → Camera, or **F6** | Switch camera: Original ↔ Third person |
| Mouse / right stick | Look around (third person) |
| **F7** | Map marker on/off |
| **R**, middle mouse or right-stick click (while inspecting) | Reset the item's rotation |
| **F8** / **F9** | Debug overlay / debug snapshot (screenshot + state in `<game>/TSViewMod_debug/`) |

## Settings

`TormentedSouls_Data/TSViewMod.cfg` is created on first run, with a comment on every setting. Most are also on the
View Mod options page; the rest (key bindings, turn limits, follow speed, `LockCursor`, `KeyboardCameraRelative`,
`MapArrow`, `GameCameraOnGrab`, ...) are file-only. Restart the game after editing it.

If the game folder is read-only (e.g. under Program Files), the mod keeps its files in the game's save-data
folder instead (`%USERPROFILE%\AppData\LocalLow\duale-abstractd\Tormented Souls Release`).

Map fits are cached in `TormentedSouls_Data/TSViewMod_mapcache.txt`. Delete it to recompute fits as you open the
map in each room.

## Known issues

- Some enemy attacks look odd from behind her; they were animated for the fixed cameras.
- The map dot falls back to the room centre in Pharmacy and Bathroom_E, and may be less accurate in a few large
  rooms.

## For developers

- `installer/survey.py` fits every map room unattended (backs up and restores saves; see the script's header). Its
  defaults are for the author's setup; pass `--game`, `--compat` and `--url` for yours.
- The first-person camera is developed separately and isn't part of this repository yet.
- `MODLOG.md` has engine notes, decisions and gotchas.

## Credits

Made by MuteRex, with help from AI tools during development. Released under the MIT License (see `LICENSE`).
Tormented Souls is © Dual Effect / Abstract Digital / PQube. This repository contains no game files or decompiled
code.
