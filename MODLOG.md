# Tormented Souls view mod — MODLOG

Goal: toggleable third-person / first-person camera + red "you are here" marker on the in-game map.

## Game
- Tormented Souls, GOG build (goggame-1243021945), Unity 2020.3.16f1, Mono, x64.
- Install: /mnt/media/Games/Tormented Souls ; runs via Steam/Proton as non-Steam shortcut, compatdata 4250254510.
- Player.log: compatdata/4250254510/pfx/drive_c/users/steamuser/AppData/LocalLow/duale-abstractd/Tormented Souls Release/Player.log
- Assembly-CSharp.dll is ALREADY patched (debug panel, 2026-10-12). Original: ~/Games-backups/TormentedSouls/Assembly-CSharp.dll.original
- Decompiled source (do not commit): ~/tormented-souls-decomp/src

## Backups / restore
- Baseline before mod: ~/Games-backups/TormentedSouls-mod-baseline_2026-10-13_010423/
  (Managed/, boot.config, RuntimeInitializeOnLoads.json, ScriptingAssemblies.json, saves, shortcuts.vdf)
- Restore = copy RuntimeInitializeOnLoads.json + ScriptingAssemblies.json back, delete Managed/TSViewMod.dll.
- Saves are also auto-backed-up by ts-save-backup.path to ~/Games-backups/TormentedSouls/<ts>/

## Engine facts
- Camera: CinemachineBrain + main vcam is a CinemachineClearShot (TormentedBestCameraChooser children). Main priority = 100000.
  Puzzles/zoom: GameplayCameraManager.MoveToCamera sets main=0, puzzle cam=10000, then BackToGameCamera restores 100000.
  => mod vcam priority = main>0 ? main+1 : 0 (yields to puzzle cams automatically). Timeline cutscenes override brain anyway.
- PlayerMovement: analog input is camera-relative (uses m_cameraTransform.eulerAngles.y = main camera), dpad = tank. So a free 3rd-person cam steers naturally.
- Map: MapHelperRoom.Start already highlights current scene's room (emission flicker, white tint) + optional 'ping' object. Marker = tint red + add red dot child on highlight.
- Input: legacy InputManager available (UnityEngine.InputLegacyModule, axes "Mouse X"/"Mouse Y"). Rewired for gamepad.

## Route
- Loader-free: add TSViewMod.dll to Managed/, list it in ScriptingAssemblies.json (type 16) and add a
  RuntimeInitializeOnLoads.json entry (loadTypes 0 = AfterSceneLoad) -> TSViewMod.Loader.Init creates a DontDestroyOnLoad GameObject.
  No Assembly-CSharp patch, no Proton DLL overrides, no BepInEx.

## Log
- 2026-10-13 01:08 v0.1.0 built (net472, refs original Assembly-CSharp) and installed with installer/install.py.
  JSON originals kept as *.tsviewmod-orig next to the files.
- 01:09 launched via steam://rungameid/18254704120160059392 -> Player.log shows "[TSViewMod] loaded v0.1.0.0" and
  "view camera ready". Loader route WORKS under Proton with no DLL overrides.
- NEXT: in-game test with a loaded save (needs user): F6/R3 cycle Original->Third->First, mouse/right stick look,
  puzzle close-up yields, map marker log line "map marker: scene ..." + red dot visible.
- 2026-10-13 v0.2.0 (user test feedback on v0.1):
  * Camera rotated while inspecting/rotating items (shared look input). Fix: only read look input when
    PlayerMovement.GamePaused is false (GameplayState_FreePlay unpauses on enter, pauses on exit).
  * Map dot was room-centre only and "nowhere near" in long rooms (Corridor_1D). Rooms are separate scenes
    each around its own origin (NavMesh bounds of Corridor_1C and 1D overlap) => no global transform.
    New: per-room fit of footprint (NavMesh.CalculateTriangulation, else floor colliders near feet) to the
    highlight rect in map-local space over 8 axis-aligned orientations; score = scale anisotropy + door
    spawn points (SceneEntrancePoints -> DoorConnector.SceneIdOnOpen -> neighbour room rect) distance.
    Dot follows player each frame. Diagnostics logged per map open.
