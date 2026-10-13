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
PREFERRED: option B — in-game survey mode (mod drives GameplayEntranceManager.TeleportToSceneTesting through every
room on a COPY of the save, opens/loads the map, runs the existing fit, writes the cache + weak-room report).
Option A below (offline level-file parsing) is the fallback.
Offline room extraction: precompute world->map fits for every room so unvisited rooms are right first time.
- Scene list: globalgamemanagers BuildSettings (UnityPy) -> levelN per room scene.
- Map highlights: find the map prefab(s) with MapHelperRoom (MonoBehaviour typetrees need UnityPy's
  TypeTreeGenerator with the Managed DLLs, as used for the Rewired input dump), read each highlight mesh (UnityPy
  reads meshes regardless of isReadable) + transforms -> outline in map-local space.
- World footprint: NavMeshData in sharedassetsN (m_NavMeshTiles, binary Detour tiles) or floor colliders; doors from
  SceneEntrancePoints + SceneDataSO.DoorConnector.
- Same scoring as runtime (2.0 * door penalty + (1 - IoU)); write TSViewMod_mapcache.txt lines (CacheVersion 2,
  key scene|tab|highlightName) and a report of weak rooms. Runtime keeps working without it.

## 2026-10-13 v1.0.2: map survey (option B)
- Map item = single Addressable `Items/Map_Zoom.prefab` (Gui3DObject.SetItem: "Items/" + AddressableItemName), all tabs.
- Survey.cs: flag file TSViewMod_survey.flag (deleted on read) -> InitMenuManager.ContinueSaved() -> load the map
  prefab into a hidden DontDestroyOnLoad holder with all its MonoBehaviours disabled (MapHelperRoom.Update NREs without
  the inspect screen's obsBase) -> GameplayEntranceManager.TeleportToSceneTesting(scene) for every scene in
  sceneToGameObjects -> MapMarker marks/fits it as usual (pose voting skipped while Survey.Active) ->
  TSViewMod_survey_report.txt, resume list TSViewMod_survey_done.txt, Application.Quit().
- installer/survey.py: refuses if TormentedSouls.exe runs, installs, backs up Saves + pfx/user.reg (low-res window
  persists via PlayerPrefs; enemy/door code can autosave), stops ts-save-backup.path, launches via Steam, renices,
  restores everything after. Untested in game yet.
- v1.0.3: rooms without a navmesh (no enemies: Bathroom_A, DeliveryRoom, MaternityRoom, *_P ...) get a physics floor
  probe instead (raycast from chest height, flat hit within 0.6 m of the player's floor, standing-room capsule,
  flood fill from the player). Survey turns on the game's own god mode (UserDataManager.SetGodModeStatus, checked in
  PlayerController damage), skips rooms whose fits are all cached (no done-file), WEAK threshold IoU < 0.25.
- Debug teleport side effect: PlayerMovementManager.IsInLightZone NRE spam (game code), harmless.
- First survey run (v1.0.2, 13:43-13:57): all 70 map rooms visited, no deaths, save restored. 37 fitted; 32 had no
  navmesh; BigHall timed out (60 s) because fitting got 4 ms/frame at 10 fps. Orientations: 29x o1, outliers
  ExamRoom o7, Library o0, BedRoom o5, Kitchen o4, Bathroom_B o7, DentalRoom_P o0, GeneratorRoom o2, OvenRoom o2.
- v1.0.4: FitCache.MajorityOrientation (>=5 fits, >=60% share) -> +0.15 score penalty for other orientations, and
  cached outliers are dropped + refitted (TryGetTrusted, in normal play too). Survey frame budget 80 ms, fit timeout 240 s.
- Second survey run (v1.0.4): 42 rooms fitted, 71/73 cached, 58x o1. Still off-majority after the prior (doors
  outvote it): MaternityRoom, Office, Library, MeetingRoom, Kitchen, Bathroom_D(_M), Bathroom_E_M, GeneratorRoom,
  BigHall x4 sheets. No fit: Pharmacy (strict probe 17 cells), Bathroom_E (1 cell).
- v1.0.5: bug fixed: fits made with the prior that still differ were dropped on every load (constant refits); cache
  lines now carry 'p' (withPrior) and those are trusted. Relaxed floor probe (no headroom checks) when strict < 20 cells.
- v1.0.6: pad Reset View prompt gets a generated down-arrow over the analogR icon (game has no R3/stick-click sprite), tinted like the bar label.
- v1.0.7 camera steadiness: obstacles pull the camera in instantly, then it holds 0.35 s and eases back out
  (m_smoothDistance, reset on snap). Colliders ignored by the camera: thin (x,z < 0.5 m), small (max extent < 1.2 m),
  enemies (EnemyBrain parent); cached per collider. Defaults ThirdPersonDistance 1.4, ShoulderOffset 0.45 (user cfg
  updated; previous cfg kept as TSViewMod.cfg.bak-1.0.6). Installed with v1.0.6 prompt arrow.
- Video 14-25-15 (v1.0.7): camera smooth in open rooms, East Hall dot inside its room; press arrow clipped by the bar
  above the icon; camera squeezed between a right-hand wall and her head.
- v1.0.8: automatic shoulder swap (checks both sides every 0.1 s, swaps when the other side has 0.35 m more room,
  glides over ~0.5 s). Pad prompt: stick icon shrunk to 74% and lowered, arrow drawn inside the icon's original space.
- v1.0.9: "View Mod" category in the game's Options menu (OptionsPage.cs). Copies the last category button and the
  panel with the most OptionMenuButtonController rows; strips MultilanguageUIAutotext; ViewModPanel :
  BaseMenuPanelController builds 13 rows from the first row; wired via reflection (OptionPressed /
  OnPressedBackOnPanel, NavMenuItems insert before Back), not via panelsData. Changes apply live and save the cfg.
  Config.ShoulderSide (Auto/Right/Left); map marker follows config.MapMarker. Needs Sirenix.Serialization ref
  (NavItemButton : SerializedMonoBehaviour). Logs a hierarchy dump on injection for layout fixes. Untested.
- v1.0.10: page redesign after user feedback ('looks like shit'): ◀ ▶ button texts were overwritten with labels (only non-button, non-value text is the label now); 10 short rows (Camera, Distance, Height, Shoulder, Field of View, Sensitivity, Invert Y, R3 Camera Switch, Map Marker, Reset View), 1-10 levels instead of metres; rows live in a GridLayoutGroup (Scaled Down), spacing 141 of 1821. Row structure logged.
- v1.0.11: first person disabled on main (ViewCamera.FirstPersonAvailable = false; R3/F6 cycle skips it; menu shows
  "First Person (Coming Soon)"). It lives on branch `first-person` (FirstPersonAvailable = true, own version line
  FirstPersonVersion 0.1.x); main is merged into it after every main commit.
- Third person rework against "bobbing near objects": removed rise-over-shoulder (height changed with wall distance),
  spring arm from the shoulder point with fixed camera-space offset, pull-in SmoothDamp 0.05 s (max 25 cm behind
  target), 0.35 s hold then SmoothDamp 0.45 s out; auto shoulder swap needs 0.4 s of squeeze + 1.5 s cooldown,
  side glides with SmoothDamp 0.35 s; body fades when the arm < 0.35 m.
- v1.1.0 tagged at e6c16e6 (user: bobbing "significantly better", controller view switching works).
- v1.1.1 void/clipping (painting corridor etc.): camera arm stops where it leaves the room's walkable area
  (MapMarker.InsideRoom: navmesh or floor-probe WorldMask per scene, built 1 s after load, overhang 0.3 m, replaces
  NavMesh.Raycast); output camera clears to black while the mod camera is live (restored after); third-person upward
  pitch limited to -25 (many rooms have no ceiling); OrbitLimit 120 deg from her back (camera follows round at
  360 deg/s when exceeded) and MaxTurnSpeed 300 deg/s, both in cfg.
- v1.1.2: user: right-stick click must only be Reset View; controller camera switching only via Options > View Mod. Removed R3 view cycling, its cfg key and menu row.
- v1.1.3 (video 16-09-03): stair railing collapsed the arm into her head (she faded -> pseudo first person); door: pitch swung to near top-down / from below. Fix: waist-high colliders (height < 1.4 m, top between feet+0.3 and feet+1.6) don't block the camera; third-person pitch -15..45. Items-cleared map tint (RE2 style) researched and put on hold by the user.
- v1.1.4: auto follow (cfg AutoFollow/AutoFollowSpeed 120, menu row): after 0.6 s without look input and while she moves > 0.4 m/s, yaw eases toward her facing (rate scales with speed and angle), skipped when she walks back toward the camera (>150 deg).

## v1.2.0 (2026-10-13)
User confirmed the mod is in a good, playable state (third-person camera, auto follow, options page, map dot, inspect reset). Known cosmetic: some enemy attack animations look odd from the third-person angle (they were authored for the fixed cameras).

## v1.2.1 (2026-10-13)
RE2-style turning (from the user's RE2 clip): the orbit limit now only stops the stick/mouse swinging the camera past it; her own turning no longer drags the camera round at 360 deg/s, so she can run toward the camera like Leon does. Auto follow still recentres on curves.

## v1.2.2 (2026-10-13)
Mouse and keyboard: pointer captured/hidden while walking in the mod views (LockCursor; restored to the game's mode in menus, map, inspect, game cameras, alt-tab). Mouse no longer limited by MaxTurnSpeed. Separate MouseSensitivity / MouseInvertY; Options rows now Stick Speed, Mouse Speed, Invert Y (Off/Stick/Mouse/Both). Reset View on/off moved to cfg only to keep 10 rows. ResetRotationMouseButton in cfg.

## v1.3.0 (2026-10-13)
User confirmed mouse support (v1.2.2). Keyboard: the game maps movement keys to the "DPAD" actions = tank controls (PlayerMovement.Movement dpad branch). While a mod view is live, KeyboardMovement adds the same keys to "Horizontal/Vertical Axis" in the keyboard Rewired maps (UserInputManager uses the analog, camera-relative path when both are non-zero) and removes them again outside. Analog path picks walk/run by magnitude (m_walkInputRange 0.2-0.7), so for keys max is raised to 1.5 unless Run is held. Config KeyboardCameraRelative. Side effect: no keyboard quick-turn (S+Run) in mod views.

## v1.3.1 (2026-10-13)
Game camera takes over while the player animator has Grabbed/Dead set (or is in the Grabbed state) + 0.8 s, then the mod view snaps back behind her (GameCameraOnGrab). Map: red dot gets a facing pointer (flat double-sided triangle, child of the dot, direction = fit.Apply(p+forward)-fit.Apply(p) in sheet space; MapArrow). Door camera-carry idea dropped: rooms load behind a loading screen anyway.

## v1.3.2 (2026-10-13)
Public release prep: Linux only (Windows in progress), no trailer. Comments condensed. Installer finds the DLL next to itself (release zip) or in bin/, takes --game, and copies the bundled map fits (data/TSViewMod_mapcache.txt) if the player has none. csproj falls back to the installed Assembly-CSharp.dll.

## v1.3.3-beta (2026-10-13)
Labelled beta. Windows: installer/install.ps1 + install.bat (Steam libraryfolders.vdf / GOG registry detection, UTF-8 no BOM, PS 5.1 array fix); verified with pwsh 7.6 on Linux to produce the same JSON as install.py, idempotent, uninstall restores originals. Not run on native Windows. Mod: Loader.DataDir falls back to persistentDataPath when TormentedSouls_Data is read-only (seeded from installed copies); Config.Save no longer throws.
