# TSViewMod — Tormented Souls camera & map mod

A mod for **Tormented Souls** (PC, Unity 2020.3 / Mono) that adds:

- **Third-person and first-person cameras** you can toggle with a key, on top of the original fixed angles.
  Puzzle close-ups and cutscenes still use the game's own cameras.
- **A "you are here" red dot on the map** at your actual position, with the current room tinted red.
- **RE2-style "Reset View" while inspecting items.** Maps turn to lie flat and face you. A prompt appears in the button bar.

No BepInEx or other loader is needed, and no game code is patched. The mod is one DLL plus two small
entries in Unity's startup lists.

## Controls

| Input | Action |
|---|---|
| **F6** or right-stick click (while walking) | Cycle camera: Original → Third person → First person |
| Mouse / right stick | Look around (third/first person) |
| **F7** | Map marker on/off |
| **R**, middle mouse or right-stick click (while inspecting) | Reset the item's rotation |
| **F8** | Debug overlay (scene, position, camera, map fit) + blue door markers on the map |
| **F9** | Debug snapshot: screenshot + state text in `<game>/TSViewMod_debug/` |

Movement uses the analog stick or WASD and follows the camera direction. The d-pad keeps tank controls.

## Install

Requirements: .NET SDK (any recent version) and Python 3. You must own the game. Tested on the GOG build
under Proton on Linux; the same steps work on Windows with the paths changed.

```sh
cd TSViewMod
dotnet build -c Release \
  -p:GameManaged="/path/to/Tormented Souls/TormentedSouls_Data/Managed" \
  -p:OriginalAssemblyCSharp="/path/to/Tormented Souls/TormentedSouls_Data/Managed/Assembly-CSharp.dll"
cd ..
python3 installer/install.py install --game "/path/to/Tormented Souls"
```

The installer copies `TSViewMod.dll` into `TormentedSouls_Data/Managed`. It also registers the DLL in
`ScriptingAssemblies.json` and `RuntimeInitializeOnLoads.json`, keeping the originals as `*.tsviewmod-orig`.

To uninstall: `python3 installer/install.py uninstall --game "/path/to/Tormented Souls"`

## Settings

`TormentedSouls_Data/TSViewMod.cfg` is created on first run. It holds the keys, mouse/stick sensitivity,
invert Y, camera distance, shoulder offset, field of view, whether the body is hidden in first person, the map
marker and the inspect reset. Restart the game after editing it.

Map fits are cached in `TormentedSouls_Data/TSViewMod_mapcache.txt`. Delete that file to recompute them.

## Map survey (optional)

Rooms are fitted the first time you open the map in them. To fit every room in advance, close the game and run:

```sh
python3 installer/survey.py
```

It backs up your saves and settings, then starts the game in a tiny muted window at low priority. The mod continues
your save, teleports through every room on the map with the game's debug teleport, fits each one and quits by
itself (about 5-10 minutes). Afterwards the launcher restores your saves and settings and prints
`TSViewMod_survey_report.txt`. Rooms marked WEAK may have an inaccurate dot. If it's interrupted, run it again and it
continues where it stopped.

## How it works (short)

- **Camera:** a mod-owned Cinemachine virtual camera sits one priority above the game's main ClearShot camera.
  When the game lowers its main camera for a puzzle or zoom camera, the mod lowers its own as well.
- **Map:** each room is its own scene with its own origin, so the mod fits each room separately. It draws
  the room's map highlight into a mask on the GPU and lays the room's NavMesh over it. It then searches 8
  orientations, a range of scales and offsets for the best overlap, and uses door spawn points to break ties.
- **Reset:** turns `Gui3DObject`'s inspected object back to its stored opening rotation.

See `MODLOG.md` for engine notes, decisions and gotchas.

## Credits

Built with AI assistance (Claude). Tormented Souls is © Dual Effect / Abstract Digital / PQube. This repo
contains no game files or decompiled code.
