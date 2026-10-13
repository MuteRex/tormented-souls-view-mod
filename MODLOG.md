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
- v0.3.0: user screenshot showed current-room highlight (East Wing Hall = Corridor_1D) is an L-shape; rect fit put the
  dot in Delivery Room and o4/o1 scores were a coin flip (0.144 vs 0.150). New fit: highlight mesh drawn to a 64x64
  mask via CommandBuffer + Hidden/Internal-Colored (meshes not CPU-readable), navmesh triangles rasterised to a world
  mask, search 8 orientations x scale (area-ratio seed, +-15%) x offset (centroid seed, +-6 cells, then refine), score
  1-IoU + 0.1*door penalty. Row-order self-test logged once. Falls back to room centre if IoU < 0.2.
  Also: RE2-style inspect reset (R / middle mouse / R3) via Gui3DObject.m_initialRot; R3 view-cycle limited to free play.
  Gotcha: `pgrep -f TormentedSouls.exe` matches the shell running it -> wait on the reaper PID instead.
- v0.4.0: map open hitched 1-2 s (fit recomputed every open: ~35M IoU samples in one frame). Now: per-room fits cached
  in TormentedSouls_Data/TSViewMod_mapcache.txt (key scene|tab|highlight, CacheVersion "1"); uncached rooms fit in a
  coroutine with a 4 ms/frame budget, coarse pass sampled every 2nd cell. Dot hidden until its fit is ready.
  Last v0.3 fit for Corridor_1D: orientation 6, scale 0.0679, IoU ~0.40 (user confirmed position correct).
  Inspect reset: maps turn to face the Gui3DObject camera flat & upright (axes from the opening pose); other items
  go to m_initialRot. "[R]/[R3] Reset View" entry cloned into HelpersSwitcher's active bar (game scripts stripped,
  icons hidden); bar hierarchy dumped to log once.
- v0.5.0 (user screenshot 01:50): Reset prompt rendered "[R] Reset View" squeezed into the keycap slot with the cloned
  label still "Back". Bar entry layout (Examinate/BottomHelperBar): Background[Image+SwapSpriteControls] /
  KeyboardText[TMP] / Label Text[TMP+MultilanguageUIAutotext]; SwapKeyboardControls on keyboard-style entries.
  Game UI sprites available: analogL, analogR, bottom/rightButtonDiamond-medium, keyboardCapBlack, mouse icons;
  NO stick-click (R3) art. Now: copy Back entry, keyboard = KeyboardBG keycap + key letter, pad = the Rotate
  entry's current sprite (game-swapped analogR art), label "Reset View".
  Map dot vanished after walking / after reset: dot was lifted toward Camera.main (gameplay cam), which can be behind
  the sheet. Now lifted toward the camera whose cullingMask renders the highlight layer.
- v0.6.0 (user video 01:57): prompt now correct (keycap R / pad analogR). Bugs: (1) map reset showed the BACK of the
  sheet (R/F key drawing page) — camera-based front guess was wrong; (2) dot visible at 1:21, gone at 1:39 after the
  player went up the East Wing Hall stairs. Fix: front side = side the room highlights sit on relative to the other
  sheet meshes (FrontSign, logged); reset faces that side to the camera that renders the highlight layer; dot lifted
  along the front normal, drawn with Hidden/Internal-Colored ZTest Always (queue 4000), hidden when the sheet's back
  faces the camera, and clamped into the current room's rect (stairs/landings can map past the outline).
- v0.7.0 (user video untitled.mp4, log "front sign -1"): the mesh-depth FrontSign heuristic picked the BACK, so the dot
  hid itself (back-facing check) and reset still showed the R/F back page. Replaced with LearnFront: the game opens
  the map printed side toward the viewer, so ~0.35 s after open (rotation settled, or 1.5 s max) record which side of
  the sheet faces the map camera and which in-plane axis points up on screen. Dot hiding and the reset both use it;
  reset falls back to m_initialRot until learned. Logged as "learned printed side ...".
