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
- v0.8.0 (video 02-14-58, frame-by-frame): v0.7 confirmed working — learned printed side +1 / up axis 0+ on every
  open; reset goes tilted -> flat upright printed side (10.5 s -> 12.5 s); dot correct on the ground floor of
  East Wing Hall; prompt OK. Remaining bug: upstairs part of Corridor_1D (corridor with painting, reached by the
  hall stairs) is drawn as the thin orange strip along the top of the L over Delivery Room; rect clamp parked the
  dot on the rect's corner (Delivery Room). Now the room outline mask is rendered on every open (one 64x64 draw +
  readback) and the dot snaps to the nearest outline cell, nudged toward its 5x5 neighbourhood centre.
- v0.9.0 (video 02-21-01 review, user OK'd fixes):
  * Downstairs dot likely mirrored (player by the green door + angel statues, next to Delivery Room; dot on the west
    side by the stairs). Fit IoU only ~0.40 (navmesh = both floors + stairs vs one outline), so mirror candidates
    were close and doors (weight 0.1) couldn't decide. Now score = 2.0 * door penalty + (1 - IoU); all 8
    orientation scores + door list (with "not on this map") logged; CacheVersion "2" forces a refit.
  * 3rd-person camera went inside Caroline's hood with a wall behind (51-52 s). Now: under 0.9 m the camera rises
    over the shoulder (ceiling-checked); under 0.5 m her renderers go shadow-only.
  * Debug tools: F8 overlay (scene, pos, free play, view/yaw/pitch/distance, mod cam priority/live, active vcam,
    map fit + player uv raw/snapped), blue door markers on the map while the overlay is on, F9 screenshot + state to
    <game>/TSViewMod_debug/. Orient/inverse math verified offline (8 distinct, exact inverse).
- Versioning rule (user, from v0.9.0): +0.1 only when the user confirms a fix works; otherwise patch bumps
  inside the current step (0.9.0 -> 0.9.1 -> 0.9.2 ...).
- v1.0.0: user confirmed the v0.9.0 fixes (door-weighted fit puts the dot on the Delivery Room side; door markers verified). Same code as 0.9.0, version bumped per rule.
- v1.0.1 (video 02-36-10 review):
  * Camera passed through walls with no collider (Corridor_1C, overlay distance 2.08 = unobstructed). Constrain():
    colliders, then NavMesh.Raycast from the player's navmesh point toward the camera's XZ; camera may overhang the
    walkable edge by 0.45 m, else pulled in along its line to the pivot. Rooms without navmesh: colliders only.
  * Map pose learned wrong on 2 of 12 opens ("side -1"; "axis 0") — map opened from inventory flies in flat and
    the opening animation scales from 0, so world-space bounds/round trips were degenerate. Now all map-space maths
    use RelativeMatrix (local TRS chain highlight->map) and the mask view is map-local; learning waits for rotation
    AND scale to settle (gives up after 3 s); each open votes and the majority per map (name|nAxis) is used and
    persisted in TormentedSouls_Data/TSViewMod_mappose.txt.
  * Debug door dots hidden when the sheet's back faces the camera.

## Planned (deferred to save the user's usage — do in a fresh session when weekly usage allows)
Offline room extraction: precompute world->map fits for every room so unvisited rooms are right first time.
- Scene list: globalgamemanagers BuildSettings (UnityPy) -> levelN per room scene.
- Map highlights: find the map prefab(s) with MapHelperRoom (MonoBehaviour typetrees need UnityPy's
  TypeTreeGenerator with the Managed DLLs, as used for the Rewired input dump), read each highlight mesh (UnityPy
  reads meshes regardless of isReadable) + transforms -> outline in map-local space.
- World footprint: NavMeshData in sharedassetsN (m_NavMeshTiles, binary Detour tiles) or floor colliders; doors from
  SceneEntrancePoints + SceneDataSO.DoorConnector.
- Same scoring as runtime (2.0 * door penalty + (1 - IoU)); write TSViewMod_mapcache.txt lines (CacheVersion 2,
  key scene|tab|highlightName) and a report of weak rooms. Runtime keeps working without it.
