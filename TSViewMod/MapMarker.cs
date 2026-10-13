using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace TSViewMod
{
    // The game's map (MapHelperRoom) only lights up the room you are in, with a faint white flicker.
    // This tints that glow red and puts a pulsing red dot at the player's actual position.
    //
    // Every room is its own scene built around its own origin, so there is no global world->map transform.
    // Per room we fit one by shape: the room's highlight mesh is drawn into a small mask on the GPU (the
    // meshes are not CPU-readable), the room's NavMesh is rasterised into a world mask, and the 8 axis-aligned
    // orientations x scale x offset are searched for the best overlap (IoU). Door spawn points break ties.
    public class MapMarker : MonoBehaviour
    {
        private static readonly Color MarkerRed = new Color(1f, 0.05f, 0.05f, 1f);
        private const int MaskRes = 64;

        private class RoomDot
        {
            public GameObject highlight;
            public Transform dot;
            public Fit fit;
            public bool hasFit;
            public bool ready;
            public string key;
            public float normalCoord;
            public Vector2 rectMin;
            public Vector2 rectMax;
            public MapMask shape;
            public List<KeyValuePair<string, Vector2>> doors;
            public readonly List<Transform> doorDots = new List<Transform>();
        }

        private class Marked
        {
            public MapHelperRoom helper;
            public Color originalTint;
            public string scene;
            public int uAxis;
            public int vAxis;
            public int nAxis;
            public float dotSize;
            public float frontSign = 1f;
            public Vector3 upLocal;
            public bool frontKnown;
            public bool voted;
            public float openedAt;
            public Quaternion lastRotation;
            public Vector3 lastScale;
            public string poseKey;
            public readonly List<RoomDot> rooms = new List<RoomDot>();
        }

        private readonly List<Marked> m_marked = new List<Marked>();
        private bool m_enabled;
        private float m_nextScan;
        private Material m_material;
        private Material m_maskMaterial;
        private PlayerController m_player;
        private int m_flipChecked; // 0 unknown, 1 normal, -1 rows flipped
        private static MapMarker s_instance;

        private void Awake()
        {
            s_instance = this;
        }

        // For the camera: the room's walkable area (navmesh, or the floor probe where there's none), built once per
        // scene a second after it loads. Unknown -> true, so the camera is never blocked by a missing mask.
        private static WorldMask s_roomMask;
        private static string s_roomMaskScene;
        private static float s_roomMaskAt;
        private static bool s_roomMaskBuilt;

        public static bool InsideRoom(Vector3 p, float overhang)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (s_roomMaskScene != scene)
            {
                s_roomMaskScene = scene;
                s_roomMask = null;
                s_roomMaskBuilt = false;
                s_roomMaskAt = Time.unscaledTime + 1f;
            }
            if (!s_roomMaskBuilt)
            {
                if (Time.unscaledTime < s_roomMaskAt || FindObjectOfType<PlayerController>() == null)
                {
                    return true;
                }
                s_roomMaskBuilt = true;
                var log = new StringBuilder("camera bounds: ").Append(scene);
                s_roomMask = WorldMask.Build(log);
                Loader.Log(log.ToString());
            }
            return s_roomMask == null || s_roomMask.Near(new Vector2(p.x, p.z), overhang);
        }

        // For Survey: whether this room's fit is already cached (so the survey can skip it).
        public static bool IsCached(string scene, string tab, string highlightName)
        {
            return FitCache.TryGetTrusted(scene + "|" + tab + "|" + highlightName, out Fit _);
        }

        // For Survey: true once the map has been marked in this scene and every room on it has its fit
        // (from the cache or freshly computed). Lines describe each room; weak = no usable fit.
        public static bool TryGetSceneResult(string scene, out List<string> lines, out bool weak)
        {
            lines = new List<string>();
            weak = false;
            Marked marked = s_instance != null ? s_instance.m_marked.Find(m => m.helper != null && m.scene == scene) : null;
            if (marked == null || marked.rooms.Exists(r => !r.ready))
            {
                return false;
            }
            foreach (RoomDot room in marked.rooms)
            {
                bool roomWeak = !room.hasFit || room.fit.iou < 0.25f; // long corridors fit fine around 0.3
                weak |= roomWeak;
                lines.Add(room.key + (room.hasFit
                    ? "  orientation " + room.fit.orientation + " scale " + room.fit.scale.ToString("0.0000") + " IoU " + room.fit.iou.ToString("0.00")
                    : "  no fit (no navmesh or too weak), dot uses room centre") + (roomWeak ? "  << WEAK" : ""));
            }
            return true;
        }

        private void Start()
        {
            m_enabled = ViewCamera.Instance == null || ViewCamera.Instance.Config.MapMarker;
        }

        private void Update()
        {
            Config config = ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
            if (config != null && Input.GetKeyDown(config.MapMarkerKey))
            {
                config.MapMarker = !config.MapMarker;
                config.Save();
                ViewCamera.Instance.ShowToast(config.MapMarker ? "Map marker: on" : "Map marker: off");
            }
            // The config is the source of truth (the options page changes it too).
            if (config != null && m_enabled != config.MapMarker)
            {
                m_enabled = config.MapMarker;
                foreach (Marked marked in m_marked)
                {
                    Apply(marked);
                }
            }

            m_marked.RemoveAll(m => m.helper == null);
            if (s_byHelper.Count > 0 && s_byHelper.Count != m_marked.Count)
            {
                var dead = new List<MapHelperRoom>();
                foreach (MapHelperRoom key in s_byHelper.Keys)
                {
                    if (key == null)
                    {
                        dead.Add(key);
                    }
                }
                foreach (MapHelperRoom key in dead)
                {
                    s_byHelper.Remove(key);
                }
            }
            if (Time.unscaledTime >= m_nextScan)
            {
                m_nextScan = Time.unscaledTime + 0.25f;
                string scene = SceneManager.GetActiveScene().name;
                foreach (MapHelperRoom helper in FindObjectsOfType<MapHelperRoom>())
                {
                    Marked existing = m_marked.Find(m => m.helper == helper);
                    if (existing != null && existing.scene != scene)
                    {
                        Unmark(existing);
                        m_marked.Remove(existing);
                        existing = null;
                    }
                    if (existing == null)
                    {
                        m_marked.Add(Mark(helper, scene));
                    }
                }
            }

            if (m_player == null)
            {
                m_player = FindObjectOfType<PlayerController>();
            }
            float pulse = 1f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f);
            foreach (Marked marked in m_marked)
            {
                LearnFront(marked);
                foreach (RoomDot room in marked.rooms)
                {
                    if (room.dot == null)
                    {
                        continue;
                    }
                    Vector3 local;
                    if (room.hasFit && m_player != null)
                    {
                        Vector3 p = m_player.transform.position;
                        Vector2 uv = room.fit.Apply(new Vector2(p.x, p.z));
                        Vector2 rawUv = uv;
                        // The player is in this room, so keep the dot inside the room's shape on the map
                        // (navmesh areas like stair landings can map slightly past the drawn outline).
                        // Snap into the drawn shape (an L-shaped room's bounding box covers neighbouring rooms).
                        if (room.shape != null)
                        {
                            uv = room.shape.Snap(uv);
                        }
                        else
                        {
                            float margin = marked.dotSize * 0.3f;
                            uv.x = Mathf.Clamp(uv.x, room.rectMin.x + margin, room.rectMax.x - margin);
                            uv.y = Mathf.Clamp(uv.y, room.rectMin.y + margin, room.rectMax.y - margin);
                        }
                        local = Vector3.zero;
                        local[marked.uAxis] = uv.x;
                        local[marked.vAxis] = uv.y;
                        local[marked.nAxis] = room.normalCoord;
                        if (DebugTools.Enabled)
                        {
                            DebugTools.MapInfo = "map: " + room.key + "  orientation " + room.fit.orientation + " scale " + room.fit.scale.ToString("0.0000")
                                + " IoU " + room.fit.iou.ToString("0.00") + "\n  player map uv " + rawUv.ToString("0.000")
                                + ((rawUv - uv).sqrMagnitude > 1e-8f ? " -> snapped " + uv.ToString("0.000") : " (inside room)")
                                + "\n  blue dots = door spawn points, each should sit at its door";
                        }
                    }
                    else
                    {
                        local = Vector3.zero;
                        local[marked.uAxis] = (room.rectMin.x + room.rectMax.x) * 0.5f;
                        local[marked.vAxis] = (room.rectMin.y + room.rectMax.y) * 0.5f;
                        local[marked.nAxis] = room.normalCoord;
                    }
                    Transform sheet = marked.helper.transform;
                    Vector3 world = sheet.TransformPoint(local);
                    // Lift the dot off the printed side of the sheet (the side the room highlights sit on).
                    Vector3 front = sheet.TransformDirection(FrontNormalLocal(marked.nAxis, marked.frontSign)).normalized;
                    room.dot.position = world + front * marked.dotSize * 0.6f;
                    // Drawn on top of everything, so hide it when the sheet is turned to its back.
                    Camera viewer = MapCamera(room.highlight, world);
                    bool facing = !marked.frontKnown || viewer == null || Vector3.Dot(front, viewer.transform.position - world) > 0f;
                    Renderer dotRenderer = room.dot.GetComponent<Renderer>();
                    if (dotRenderer.enabled != facing)
                    {
                        dotRenderer.enabled = facing;
                    }
                    UpdateDoorDots(marked, room);
                    float size = marked.dotSize * pulse;
                    Vector3 parentScale = room.dot.parent != null ? room.dot.parent.lossyScale : Vector3.one;
                    room.dot.localScale = new Vector3(size / Safe(parentScale.x), size / Safe(parentScale.y), size / Safe(parentScale.z));
                }
            }
        }

        private static readonly Color DoorBlue = new Color(0.1f, 0.45f, 1f, 1f);
        private Material m_doorMaterial;

        // Debug: door spawn points mapped through the room's fit. On a correct fit each sits just inside its doorway.
        private void UpdateDoorDots(Marked marked, RoomDot room)
        {
            bool show = DebugTools.Enabled && m_enabled && room.ready && room.hasFit && room.doors != null;
            if (!show)
            {
                foreach (Transform t in room.doorDots)
                {
                    if (t != null && t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(false);
                    }
                }
                return;
            }
            while (room.doorDots.Count < room.doors.Count)
            {
                Transform t = CreateDot(room.highlight);
                t.name = "TSViewMod_DoorDot";
                if (m_doorMaterial == null)
                {
                    m_doorMaterial = new Material(GetMaterial(t.GetComponent<Renderer>())) { color = DoorBlue };
                }
                t.GetComponent<Renderer>().sharedMaterial = m_doorMaterial;
                room.doorDots.Add(t);
            }
            Transform sheet = marked.helper.transform;
            Vector3 front = sheet.TransformDirection(FrontNormalLocal(marked.nAxis, marked.frontSign)).normalized;
            for (int i = 0; i < room.doorDots.Count; i++)
            {
                Transform t = room.doorDots[i];
                if (t == null)
                {
                    continue;
                }
                bool active = i < room.doors.Count;
                t.gameObject.SetActive(active);
                if (!active)
                {
                    continue;
                }
                Vector2 uv = room.fit.Apply(room.doors[i].Value);
                Vector3 local = Vector3.zero;
                local[marked.uAxis] = uv.x;
                local[marked.vAxis] = uv.y;
                local[marked.nAxis] = room.normalCoord;
                Vector3 world = sheet.TransformPoint(local);
                t.position = world + front * marked.dotSize * 0.6f;
                // Same rule as the red dot: drawn on top, so hide it when the back of the sheet faces the camera.
                Camera viewer = MapCamera(room.highlight, world);
                bool facing = !marked.frontKnown || viewer == null || Vector3.Dot(front, viewer.transform.position - world) > 0f;
                Renderer r = t.GetComponent<Renderer>();
                if (r.enabled != facing)
                {
                    r.enabled = facing;
                }
                float size = marked.dotSize * 0.6f;
                Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
                t.localScale = new Vector3(size / Safe(parentScale.x), size / Safe(parentScale.y), size / Safe(parentScale.z));
            }
        }

        private static Camera m_mapCamera;

        public static Camera MapCamera(GameObject highlight, Vector3 near)
        {
            int bit = 1 << highlight.layer;
            if (m_mapCamera != null && m_mapCamera.isActiveAndEnabled && (m_mapCamera.cullingMask & bit) != 0)
            {
                return m_mapCamera;
            }
            m_mapCamera = null;
            float best = float.MaxValue;
            foreach (Camera c in Camera.allCameras)
            {
                if ((c.cullingMask & bit) == 0)
                {
                    continue;
                }
                float d = (c.transform.position - near).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    m_mapCamera = c;
                }
            }
            return m_mapCamera;
        }

        private Marked Mark(MapHelperRoom helper, string scene)
        {
            var marked = new Marked { helper = helper, originalTint = helper.tintColor, scene = scene };
            Transform map = helper.transform;

            var rooms = new List<RoomShape>();
            Vector3 spreadMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 spreadMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (MapHelperStringToGameObject entry in helper.sceneToGameObjects)
            {
                if (entry == null || entry.highlight == null || string.IsNullOrEmpty(entry.name))
                {
                    continue;
                }
                if (TryLocalBounds(map, entry.highlight, out Bounds b))
                {
                    spreadMin = Vector3.Min(spreadMin, b.min);
                    spreadMax = Vector3.Max(spreadMax, b.max);
                    rooms.Add(new RoomShape { name = entry.name, tab = entry.tabID, bounds = b, highlight = entry.highlight });
                }
            }
            Vector3 spread = spreadMax - spreadMin;
            marked.nAxis = spread.x <= spread.y && spread.x <= spread.z ? 0 : spread.y <= spread.z ? 1 : 2;
            marked.uAxis = marked.nAxis == 0 ? 1 : 0;
            marked.vAxis = marked.nAxis == 2 ? 1 : 2;
            marked.dotSize = Mathf.Max(Mathf.Max(spread[marked.uAxis], spread[marked.vAxis]) * 0.03f, 0.002f);

            marked.openedAt = Time.unscaledTime;
            marked.lastRotation = helper.transform.rotation;
            marked.lastScale = helper.transform.lossyScale;
            marked.poseKey = helper.gameObject.name + "|n" + marked.nAxis;
            if (PoseVotes.TryGetMajority(marked.poseKey, out float knownSign, out Vector3 knownUp))
            {
                // Seen this map before: use the agreed pose straight away (this open still gets a vote).
                marked.frontSign = knownSign;
                marked.upLocal = knownUp;
                marked.frontKnown = true;
            }
            s_byHelper[helper] = marked;
            var pending = new List<KeyValuePair<RoomShape, RoomDot>>();
            var seenHighlights = new HashSet<GameObject>();
            foreach (RoomShape room in rooms)
            {
                if (room.name != scene || !seenHighlights.Add(room.highlight))
                {
                    continue;
                }
                Rect2 roomRect = room.To2D(marked.uAxis, marked.vAxis);
                var dot = new RoomDot
                {
                    highlight = room.highlight,
                    normalCoord = room.bounds.center[marked.nAxis],
                    key = scene + "|" + room.tab + "|" + room.highlight.name,
                    rectMin = roomRect.min,
                    rectMax = roomRect.max
                };
                dot.dot = CreateDot(room.highlight);
                // One small GPU draw + readback of the room's outline; used to keep the dot inside it.
                dot.shape = RenderMapMask(helper.transform, room, marked.uAxis, marked.vAxis, marked.nAxis);
                dot.doors = DoorPoints();
                if (FitCache.TryGetTrusted(dot.key, out Fit cached))
                {
                    dot.fit = cached;
                    dot.hasFit = cached.iou > 0.2f;
                    dot.ready = true;
                }
                else
                {
                    pending.Add(new KeyValuePair<RoomShape, RoomDot>(room, dot));
                }
                marked.rooms.Add(dot);
            }
            Apply(marked);
            if (pending.Count > 0)
            {
                StartCoroutine(FitRooms(marked, rooms, pending));
            }
            else
            {
                Loader.Log("map marker: scene " + scene + ", " + marked.rooms.Count + " dot(s) from cache");
            }
            return marked;
        }

        // Works out the world->map fit for rooms not in the cache, a few milliseconds per frame.
        private IEnumerator FitRooms(Marked marked, List<RoomShape> rooms, List<KeyValuePair<RoomShape, RoomDot>> pending)
        {
            var clock = Stopwatch.StartNew();
            var log = new StringBuilder();
            log.Append("map marker: scene ").Append(marked.scene).Append(", normal axis ").Append(marked.nAxis);
            WorldMask world = WorldMask.Build(log);
            List<KeyValuePair<string, Vector2>> doors = DoorPoints();
            log.Append(", doors ").Append(doors.Count).Append(':');
            foreach (KeyValuePair<string, Vector2> door in doors)
            {
                bool onMap = rooms.Exists(r => r.name == door.Key);
                log.Append(' ').Append(door.Key).Append(onMap ? "" : "(not on this map)").Append('@').Append(door.Value.ToString("0.0"));
            }
            foreach (KeyValuePair<RoomShape, RoomDot> item in pending)
            {
                RoomDot dot = item.Value;
                MapMask mask = world != null && marked.helper != null ? dot.shape : null;
                if (mask != null)
                {
                    var result = new Fit[1];
                    yield return FitShape(world, mask, doors, rooms, marked.uAxis, marked.vAxis, log, result);
                    dot.fit = result[0];
                    dot.hasFit = dot.fit.iou > 0.2f;
                    FitCache.Put(dot.key, dot.fit);
                    log.Append("\n  ").Append(dot.key).Append(": orientation ").Append(dot.fit.orientation)
                        .Append(" scale ").Append(dot.fit.scale.ToString("0.00000")).Append(" IoU ").Append(dot.fit.iou.ToString("0.000"))
                        .Append(dot.hasFit ? "" : " (too weak, using room centre)");
                }
                dot.ready = true;
                if (marked.helper == null)
                {
                    yield break;
                }
            }
            FitCache.Save();
            log.Append("\n  fitted in ").Append(clock.ElapsedMilliseconds).Append(" ms (spread over frames), cached");
            Loader.Log(log.ToString());
            if (marked.helper != null)
            {
                Apply(marked);
            }
        }

        private static readonly Dictionary<MapHelperRoom, Marked> s_byHelper = new Dictionary<MapHelperRoom, Marked>();

        // The game always opens the map printed side toward the viewer, upright. Once the opening animation has
        // settled (and before the player has had time to spin it) record which side faces the map camera and which
        // in-plane axis points up on screen. The dot and the inspect reset use this.
        private static void LearnFront(Marked marked)
        {
            if (marked.voted || marked.helper == null || Survey.Active)
            {
                return; // the survey's off-screen map has no meaningful pose
            }
            Transform sheet = marked.helper.transform;
            float age = Time.unscaledTime - marked.openedAt;
            float turned = Quaternion.Angle(sheet.rotation, marked.lastRotation);
            Vector3 scale = sheet.lossyScale;
            float grew = (scale - marked.lastScale).magnitude / Mathf.Max(scale.magnitude, 1e-6f);
            marked.lastRotation = sheet.rotation;
            marked.lastScale = scale;
            // Opening animations scale the item up from zero and swing it in; wait until both have stopped.
            bool settled = age > 0.35f && turned < 0.2f && grew < 0.002f && scale.magnitude > 1e-4f;
            if (!settled)
            {
                if (age > 3f)
                {
                    marked.voted = true; // never settled (player spinning it straight away): don't vote
                }
                return;
            }
            GameObject probe = marked.rooms.Count > 0 ? marked.rooms[0].highlight : marked.helper.gameObject;
            Camera camera = MapCamera(probe, sheet.position);
            if (camera == null)
            {
                return;
            }
            Vector3 normalWorld = sheet.TransformDirection(FrontNormalLocal(marked.nAxis, 1f));
            float sign = Vector3.Dot(normalWorld, camera.transform.position - sheet.position) >= 0f ? 1f : -1f;
            int upIndex = marked.uAxis;
            float upSign = 1f;
            float best = -1f;
            foreach (int i in new[] { marked.uAxis, marked.vAxis })
            {
                Vector3 axisWorld = sheet.TransformDirection(FrontNormalLocal(i, 1f)).normalized;
                float d = Vector3.Dot(axisWorld, camera.transform.up);
                if (Mathf.Abs(d) > best)
                {
                    best = Mathf.Abs(d);
                    upIndex = i;
                    upSign = Mathf.Sign(d);
                }
            }
            marked.voted = true;
            PoseVotes.Add(marked.poseKey, sign, FrontNormalLocal(upIndex, upSign));
            PoseVotes.TryGetMajority(marked.poseKey, out marked.frontSign, out marked.upLocal);
            marked.frontKnown = true;
            Loader.Log("map marker: this open saw printed side " + sign + ", up axis " + upIndex + (upSign > 0 ? "+" : "-") + " after "
                + age.ToString("0.00") + " s; using majority " + marked.frontSign + " / " + marked.upLocal + " (" + PoseVotes.Describe(marked.poseKey) + ")");
        }

        // Votes for each map's printed side / up axis, kept across sessions in TSViewMod_mappose.txt.
        private static class PoseVotes
        {
            private static Dictionary<string, Dictionary<string, int>> s_votes;

            private static string FilePath => System.IO.Path.Combine(Application.dataPath, "TSViewMod_mappose.txt");

            private static string Encode(float sign, Vector3 up)
            {
                int axis = up.x != 0f ? 0 : up.y != 0f ? 1 : 2;
                return (sign > 0f ? "+" : "-") + "," + axis + "," + (up[axis] > 0f ? "+" : "-");
            }

            public static void Add(string key, float sign, Vector3 up)
            {
                Load();
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally))
                {
                    tally = new Dictionary<string, int>();
                    s_votes[key] = tally;
                }
                string v = Encode(sign, up);
                tally.TryGetValue(v, out int n);
                tally[v] = n + 1;
                Save();
            }

            public static bool TryGetMajority(string key, out float sign, out Vector3 up)
            {
                Load();
                sign = 1f;
                up = Vector3.zero;
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally) || tally.Count == 0)
                {
                    return false;
                }
                string best = null;
                int bestCount = -1;
                foreach (KeyValuePair<string, int> pair in tally)
                {
                    if (pair.Value > bestCount)
                    {
                        best = pair.Key;
                        bestCount = pair.Value;
                    }
                }
                string[] f = best.Split(',');
                sign = f[0] == "+" ? 1f : -1f;
                up = FrontNormalLocal(int.Parse(f[1]), f[2] == "+" ? 1f : -1f);
                return true;
            }

            public static string Describe(string key)
            {
                Load();
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally))
                {
                    return "no votes";
                }
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> pair in tally)
                {
                    parts.Add(pair.Key + " x" + pair.Value);
                }
                return string.Join("; ", parts.ToArray());
            }

            private static void Load()
            {
                if (s_votes != null)
                {
                    return;
                }
                s_votes = new Dictionary<string, Dictionary<string, int>>();
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                    {
                        return;
                    }
                    foreach (string line in System.IO.File.ReadAllLines(FilePath))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length != 3 || line.StartsWith("#"))
                        {
                            continue;
                        }
                        if (!s_votes.TryGetValue(f[0], out Dictionary<string, int> tally))
                        {
                            tally = new Dictionary<string, int>();
                            s_votes[f[0]] = tally;
                        }
                        tally[f[1]] = int.Parse(f[2]);
                    }
                }
                catch (System.Exception e)
                {
                    Loader.Log("map pose votes unreadable, starting over: " + e.Message);
                    s_votes.Clear();
                }
            }

            private static void Save()
            {
                try
                {
                    var lines = new List<string> { "# TSViewMod: which side of each map is printed / which way is up. Delete to relearn." };
                    foreach (KeyValuePair<string, Dictionary<string, int>> map in s_votes)
                    {
                        foreach (KeyValuePair<string, int> vote in map.Value)
                        {
                            lines.Add(map.Key + "\t" + vote.Key + "\t" + vote.Value);
                        }
                    }
                    System.IO.File.WriteAllLines(FilePath, lines);
                }
                catch (System.Exception e)
                {
                    Loader.Log("map pose votes not saved: " + e.Message);
                }
            }
        }

        // For the inspect reset: printed-side normal and screen-up axis in the sheet's local space, once learned.
        public static bool TryGetSheetPose(MapHelperRoom helper, out Vector3 faceLocal, out Vector3 upLocal)
        {
            faceLocal = Vector3.zero;
            upLocal = Vector3.zero;
            if (helper == null || !s_byHelper.TryGetValue(helper, out Marked marked) || !marked.frontKnown)
            {
                return false;
            }
            faceLocal = FrontNormalLocal(marked.nAxis, marked.frontSign);
            upLocal = marked.upLocal;
            return true;
        }

        public static Vector3 FrontNormalLocal(int normal, float sign)
        {
            Vector3 v = Vector3.zero;
            v[normal] = sign;
            return v;
        }

        public static int NormalAxis(MapHelperRoom helper)
        {
            Vector3 spreadMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 spreadMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (MapHelperStringToGameObject entry in helper.sceneToGameObjects)
            {
                if (entry != null && entry.highlight != null && TryLocalBounds(helper.transform, entry.highlight, out Bounds b))
                {
                    spreadMin = Vector3.Min(spreadMin, b.min);
                    spreadMax = Vector3.Max(spreadMax, b.max);
                }
            }
            Vector3 spread = spreadMax - spreadMin;
            return spread.x <= spread.y && spread.x <= spread.z ? 0 : spread.y <= spread.z ? 1 : 2;
        }

        // ---------- fitting ----------

        private struct Fit
        {
            public int orientation;
            public float scale;
            public Vector2 offset;
            public float iou;
            public float score;
            public bool withPrior; // fitted knowing the usual orientation: trust it even if it differs

            public Vector2 Apply(Vector2 world)
            {
                return Orient(orientation, world) * scale + offset;
            }
        }

        // Fits never change for a room, so they are kept in TormentedSouls_Data/TSViewMod_mapcache.txt.
        private static class FitCache
        {
            private static Dictionary<string, Fit> s_fits;

            private static string FilePath => System.IO.Path.Combine(Application.dataPath, "TSViewMod_mapcache.txt");

            public static bool TryGet(string key, out Fit fit)
            {
                Load();
                return s_fits.TryGetValue(key, out fit);
            }

            public static void Put(string key, Fit fit)
            {
                Load();
                s_fits[key] = fit;
            }

            // The whole building is drawn the same way round on every sheet, so once most fits agree on an
            // orientation, that's the expected one (-1 = not enough evidence yet).
            public static int MajorityOrientation()
            {
                Load();
                var counts = new int[8];
                int total = 0;
                foreach (Fit f in s_fits.Values)
                {
                    if (f.orientation >= 0 && f.orientation < 8)
                    {
                        counts[f.orientation]++;
                        total++;
                    }
                }
                int best = 0;
                for (int o = 1; o < 8; o++)
                {
                    if (counts[o] > counts[best])
                    {
                        best = o;
                    }
                }
                return total >= 5 && counts[best] >= total * 0.6f ? best : -1;
            }

            // A cached fit facing a different way than the rest is most likely mirrored/rotated: drop it so the room
            // is refitted with the orientation preference.
            public static bool TryGetTrusted(string key, out Fit fit)
            {
                if (!TryGet(key, out fit))
                {
                    return false;
                }
                int expected = MajorityOrientation();
                if (expected >= 0 && fit.orientation != expected && !fit.withPrior)
                {
                    Loader.Log("map marker: cached " + key + " has orientation " + fit.orientation + ", most rooms use " + expected + "; refitting");
                    s_fits.Remove(key);
                    return false;
                }
                return true;
            }

            private static void Load()
            {
                if (s_fits != null)
                {
                    return;
                }
                s_fits = new Dictionary<string, Fit>();
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                    {
                        return;
                    }
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    foreach (string line in System.IO.File.ReadAllLines(FilePath))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 7 || f[0] != CacheVersion)
                        {
                            continue;
                        }
                        s_fits[f[1]] = new Fit
                        {
                            orientation = int.Parse(f[2], inv),
                            scale = float.Parse(f[3], inv),
                            offset = new Vector2(float.Parse(f[4], inv), float.Parse(f[5], inv)),
                            iou = float.Parse(f[6], inv),
                            withPrior = f.Length > 7 && f[7] == "p"
                        };
                    }
                }
                catch (System.Exception e)
                {
                    Loader.Log("map cache unreadable, refitting: " + e.Message);
                    s_fits.Clear();
                }
            }

            public static void Save()
            {
                try
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var lines = new List<string> { "# TSViewMod map fits. Delete this file to recompute." };
                    foreach (KeyValuePair<string, Fit> pair in s_fits)
                    {
                        Fit f = pair.Value;
                        lines.Add(string.Join("\t", CacheVersion, pair.Key, f.orientation.ToString(inv), f.scale.ToString("R", inv),
                            f.offset.x.ToString("R", inv), f.offset.y.ToString("R", inv), f.iou.ToString("R", inv), f.withPrior ? "p" : "-"));
                    }
                    System.IO.File.WriteAllLines(FilePath, lines);
                }
                catch (System.Exception e)
                {
                    Loader.Log("map cache not saved: " + e.Message);
                }
            }
        }

        // Bump when the fitting method changes so old entries are recomputed.
        private const string CacheVersion = "2";

        // Survey runs at 10 fps with nobody playing, so it can spend far longer per frame.
        private static long FrameBudgetMs => Survey.Active ? 80 : 4;

        // Added to the score of orientations other than the one most rooms use (see MajorityOrientation).
        private const float OrientationPenalty = 0.15f;

        private static IEnumerator FitShape(WorldMask world, MapMask map, List<KeyValuePair<string, Vector2>> doors, List<RoomShape> rooms, int u, int v, StringBuilder log, Fit[] result)
        {
            float mapArea = map.count * map.cell * map.cell;
            float worldArea = world.count * world.cell * world.cell;
            float s0 = Mathf.Sqrt(mapArea / Mathf.Max(worldArea, 1e-6f));
            Vector2 worldCentroid = world.Centroid();
            Vector2 mapCentroid = map.Centroid();
            Rect2 roomRect = map.rect;
            float diag = roomRect.Size.magnitude;
            var slice = Stopwatch.StartNew();
            var best = new Fit { score = float.MaxValue };
            int expected = FitCache.MajorityOrientation();
            if (expected >= 0)
            {
                log.Append("\n    expected orientation o").Append(expected);
            }
            for (int o = 0; o < 8; o++)
            {
                var orientBest = new Fit { score = float.MaxValue };
                foreach (float k in new[] { 0.85f, 0.92f, 1f, 1.08f, 1.15f })
                {
                    float s = s0 * k;
                    Vector2 t0 = mapCentroid - Orient(o, worldCentroid) * s;
                    for (int dy = -6; dy <= 6; dy++)
                    {
                        for (int dx = -6; dx <= 6; dx++)
                        {
                            var fit = new Fit { orientation = o, scale = s, offset = t0 + new Vector2(dx, dy) * map.cell };
                            fit.iou = IoU(world, map, fit, 2);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                        if (slice.ElapsedMilliseconds > FrameBudgetMs)
                        {
                            yield return null;
                            slice.Restart();
                        }
                    }
                }
                Fit coarse = orientBest;
                orientBest.score = float.MaxValue;
                for (int ks = -4; ks <= 4; ks++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            var fit = new Fit { orientation = o, scale = coarse.scale * (1f + ks * 0.01f), offset = coarse.offset + new Vector2(dx, dy) * map.cell * 0.5f };
                            fit.iou = IoU(world, map, fit, 1);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                    }
                    if (slice.ElapsedMilliseconds > FrameBudgetMs)
                    {
                        yield return null;
                        slice.Restart();
                    }
                }
                // Doors decide between mirror/rotation candidates whose shapes overlap about equally: each door's
                // spawn point must land next to the room it leads to. Shape overlap only breaks remaining ties.
                float doorPenalty = DoorPenalty(orientBest, doors, rooms, u, v, roomRect, diag);
                orientBest.score = DoorWeight * doorPenalty + (1f - orientBest.iou) + (expected >= 0 && o != expected ? OrientationPenalty : 0f);
                log.Append("\n    o").Append(o).Append(" IoU ").Append(orientBest.iou.ToString("0.000"))
                    .Append(" doors ").Append(doorPenalty.ToString("0.000")).Append(" score ").Append(orientBest.score.ToString("0.000"));
                if (orientBest.score < best.score)
                {
                    best = orientBest;
                }
            }
            best.withPrior = expected >= 0;
            result[0] = best;
        }

        private static float IoU(WorldMask world, MapMask map, Fit fit, int step)
        {
            // Walk the map mask grid; map each cell centre back into the world mask.
            Vector2 a = Orient(fit.orientation, Vector2.right);
            Vector2 b = Orient(fit.orientation, Vector2.up);
            float inv = 1f / fit.scale;
            int inter = 0;
            int mapInside = 0;
            int worldInside = 0;
            for (int y = 0; y < MaskRes; y += step)
            {
                for (int x = 0; x < MaskRes; x += step)
                {
                    bool inMap = map.mask[y * MaskRes + x];
                    Vector2 q = (map.CellCentre(x, y) - fit.offset) * inv;
                    bool inWorld = world.Get(new Vector2(q.x * a.x + q.y * a.y, q.x * b.x + q.y * b.y));
                    if (inMap)
                    {
                        mapInside++;
                    }
                    if (inWorld)
                    {
                        worldInside++;
                        if (inMap)
                        {
                            inter++;
                        }
                    }
                }
            }
            // World cells that land outside the map grid count against the fit too.
            float sampleCell = map.cell * step;
            float worldCells = world.count * world.cell * world.cell * fit.scale * fit.scale / (sampleCell * sampleCell);
            float union = mapInside + Mathf.Max(worldInside, worldCells) - inter;
            return union > 0f ? inter / union : 0f;
        }

        private const float DoorWeight = 2f;

        private static float DoorPenalty(Fit fit, List<KeyValuePair<string, Vector2>> doors, List<RoomShape> rooms, int u, int v, Rect2 roomRect, float diag)
        {
            float total = 0f;
            int n = 0;
            foreach (KeyValuePair<string, Vector2> door in doors)
            {
                float bestDistance = float.MaxValue;
                foreach (RoomShape r in rooms)
                {
                    if (r.name == door.Key)
                    {
                        bestDistance = Mathf.Min(bestDistance, r.To2D(u, v).Distance(fit.Apply(door.Value)));
                    }
                }
                if (bestDistance < float.MaxValue)
                {
                    total += bestDistance / Mathf.Max(diag, 1e-5f);
                    n++;
                }
            }
            return n > 0 ? total / n : 0f;
        }

        // The 8 axis-aligned orientations: 4 rotations, each optionally mirrored.
        private static Vector2 Orient(int o, Vector2 p)
        {
            if (o >= 4)
            {
                p.x = -p.x;
            }
            switch (o & 3)
            {
                case 1: return new Vector2(-p.y, p.x);
                case 2: return new Vector2(-p.x, -p.y);
                case 3: return new Vector2(p.y, -p.x);
                default: return p;
            }
        }

        // ---------- world side: NavMesh rasterised on the XZ plane ----------

        internal class WorldMask
        {
            public Rect2 rect;
            public float cell;
            public int w;
            public int h;
            public bool[] mask;
            public int count;

            // True if any cell within `radius` metres of p is walkable (lets the camera overhang the walkable edge).
            public bool Near(Vector2 p, float radius)
            {
                int r = Mathf.CeilToInt(radius / cell);
                int cx = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int cy = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                for (int y = cy - r; y <= cy + r; y++)
                {
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x])
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            public bool Get(Vector2 p)
            {
                int x = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int y = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                return x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];
            }

            public Vector2 Centroid()
            {
                Vector2 sum = Vector2.zero;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (mask[y * w + x])
                        {
                            sum += new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
                        }
                    }
                }
                return count > 0 ? sum / count : rect.Center;
            }

            public static WorldMask Build(StringBuilder log)
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length < 3)
                {
                    log.Append(", no navmesh");
                    return BuildFromFloor(log);
                }
                Rect2 rect = Rect2.Empty;
                foreach (Vector3 p in tri.vertices)
                {
                    rect.Add(new Vector2(p.x, p.z));
                }
                float cell = Mathf.Max(rect.Size.x, rect.Size.y) / 96f;
                if (cell <= 0f)
                {
                    return null;
                }
                var m = new WorldMask { rect = rect, cell = cell };
                m.w = Mathf.CeilToInt(rect.Size.x / cell) + 1;
                m.h = Mathf.CeilToInt(rect.Size.y / cell) + 1;
                m.mask = new bool[m.w * m.h];
                for (int i = 0; i + 2 < tri.indices.Length; i += 3)
                {
                    Vector3 a = tri.vertices[tri.indices[i]];
                    Vector3 b = tri.vertices[tri.indices[i + 1]];
                    Vector3 c = tri.vertices[tri.indices[i + 2]];
                    m.Fill(new Vector2(a.x, a.z), new Vector2(b.x, b.z), new Vector2(c.x, c.z));
                }
                foreach (bool on in m.mask)
                {
                    if (on)
                    {
                        m.count++;
                    }
                }
                log.Append(", navmesh ").Append(rect).Append(' ').Append(m.count).Append(" cells");
                return m.count > 0 ? m : null;
            }

            // Rooms without a navmesh (no enemies): probe the floor with physics instead. A cell counts when a ray
            // from chest height lands on a flat surface near the player's floor level with standing room above it,
            // and it's connected to where the player stands (drops furniture tops, walls and outside areas).
            private static WorldMask BuildFromFloor(StringBuilder log)
            {
                // Strict first; some rooms have big solid helper volumes that block every headroom check, so retry
                // with only the floor-height test (walls then count as thin floor strips, fine for fitting).
                return BuildFromFloor(log, true) ?? BuildFromFloor(log, false);
            }

            private static WorldMask BuildFromFloor(StringBuilder log, bool strict)
            {
                PlayerController player = FindObjectOfType<PlayerController>();
                if (player == null)
                {
                    return null;
                }
                Vector3 origin = player.transform.position;
                Scene scene = SceneManager.GetActiveScene();
                Rect2 rect = Rect2.Empty;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Collider collider in root.GetComponentsInChildren<Collider>())
                    {
                        Bounds b = collider.bounds;
                        if (collider.isTrigger || b.size.x > 150f || b.size.z > 150f || b.max.y < origin.y - 1f || b.min.y > origin.y + 2.5f)
                        {
                            continue;
                        }
                        rect.Add(new Vector2(b.min.x, b.min.z));
                        rect.Add(new Vector2(b.max.x, b.max.z));
                    }
                }
                float cell = Mathf.Max(rect.Size.x, rect.Size.y) / 96f;
                if (!(cell > 0f) || cell > 2f)
                {
                    log.Append(", no floor colliders");
                    return null;
                }
                var m = new WorldMask { rect = rect, cell = cell };
                m.w = Mathf.CeilToInt(rect.Size.x / cell) + 1;
                m.h = Mathf.CeilToInt(rect.Size.y / cell) + 1;
                var floor = new bool[m.w * m.h];
                const QueryTriggerInteraction noTriggers = QueryTriggerInteraction.Ignore;
                for (int y = 0; y < m.h; y++)
                {
                    for (int x = 0; x < m.w; x++)
                    {
                        Vector3 top = new Vector3(rect.min.x + (x + 0.5f) * cell, origin.y + 1.6f, rect.min.y + (y + 0.5f) * cell);
                        if ((strict && Physics.CheckSphere(top, 0.15f, ~0, noTriggers))
                            || !Physics.Raycast(top, Vector3.down, out RaycastHit hit, 2.6f, ~0, noTriggers)
                            || hit.normal.y < 0.7f || Mathf.Abs(hit.point.y - origin.y) > 0.6f)
                        {
                            continue;
                        }
                        floor[y * m.w + x] = !strict || !Physics.CheckCapsule(hit.point + Vector3.up * 0.45f, hit.point + Vector3.up * 1.4f, 0.12f, ~0, noTriggers);
                    }
                }
                // Flood fill from the floor cell nearest the player.
                int start = -1;
                float best = float.MaxValue;
                for (int i = 0; i < floor.Length; i++)
                {
                    if (!floor[i])
                    {
                        continue;
                    }
                    Vector2 c = new Vector2(rect.min.x + (i % m.w + 0.5f) * cell, rect.min.y + (i / m.w + 0.5f) * cell);
                    float d = (c - new Vector2(origin.x, origin.z)).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        start = i;
                    }
                }
                m.mask = new bool[floor.Length];
                if (start >= 0 && best < 4f)
                {
                    var stack = new Stack<int>();
                    stack.Push(start);
                    m.mask[start] = true;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        int cx = i % m.w;
                        int cy = i / m.w;
                        foreach (int n in new[] { cx > 0 ? i - 1 : -1, cx < m.w - 1 ? i + 1 : -1, cy > 0 ? i - m.w : -1, cy < m.h - 1 ? i + m.w : -1 })
                        {
                            if (n >= 0 && floor[n] && !m.mask[n])
                            {
                                m.mask[n] = true;
                                stack.Push(n);
                            }
                        }
                    }
                }
                foreach (bool on in m.mask)
                {
                    if (on)
                    {
                        m.count++;
                    }
                }
                log.Append(strict ? ", floor probe " : ", relaxed floor probe ").Append(rect).Append(' ').Append(m.count).Append(" cells");
                return m.count > 20 ? m : null;
            }

            private void Fill(Vector2 a, Vector2 b, Vector2 c)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - rect.min.x) / cell));
                int x1 = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - rect.min.x) / cell));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - rect.min.y) / cell));
                int y1 = Mathf.Min(h - 1, Mathf.FloorToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - rect.min.y) / cell));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 p = new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
                        if (InTriangle(p, a, b, c))
                        {
                            mask[y * w + x] = true;
                        }
                    }
                }
            }

            private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                float d1 = Cross(p, a, b);
                float d2 = Cross(p, b, c);
                float d3 = Cross(p, c, a);
                bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
                bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
                return !(neg && pos);
            }

            private static float Cross(Vector2 p, Vector2 a, Vector2 b)
            {
                return (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            }
        }

        // ---------- map side: highlight mesh drawn into a mask on the GPU ----------

        private class MapMask
        {
            public Rect2 rect;
            public float cell;
            public bool[] mask;
            public int count;

            public Vector2 CellCentre(int x, int y)
            {
                return new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
            }

            // p if it lies inside the drawn room, otherwise the nearest room cell, nudged toward the middle of
            // its neighbourhood so the dot sits inside narrow strips rather than on their edge.
            public Vector2 Snap(Vector2 p)
            {
                int cx = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int cy = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                if (Get(cx, cy))
                {
                    return p;
                }
                int bx = -1;
                int by = -1;
                float bestDistance = float.MaxValue;
                for (int y = 0; y < MaskRes; y++)
                {
                    for (int x = 0; x < MaskRes; x++)
                    {
                        if (!mask[y * MaskRes + x])
                        {
                            continue;
                        }
                        float d = (CellCentre(x, y) - p).sqrMagnitude;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bx = x;
                            by = y;
                        }
                    }
                }
                if (bx < 0)
                {
                    return p;
                }
                Vector2 sum = Vector2.zero;
                int n = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Get(bx + dx, by + dy))
                        {
                            sum += CellCentre(bx + dx, by + dy);
                            n++;
                        }
                    }
                }
                return sum / n;
            }

            private bool Get(int x, int y)
            {
                return x >= 0 && y >= 0 && x < MaskRes && y < MaskRes && mask[y * MaskRes + x];
            }

            public Vector2 Centroid()
            {
                Vector2 sum = Vector2.zero;
                for (int y = 0; y < MaskRes; y++)
                {
                    for (int x = 0; x < MaskRes; x++)
                    {
                        if (mask[y * MaskRes + x])
                        {
                            sum += CellCentre(x, y);
                        }
                    }
                }
                return count > 0 ? sum / count : rect.Center;
            }
        }

        private MapMask RenderMapMask(Transform map, RoomShape room, int u, int v, int n)
        {
            Rect2 r = room.To2D(u, v);
            float side = Mathf.Max(r.Size.x, r.Size.y) * 1.3f;
            if (side <= 0f)
            {
                return null;
            }
            Vector2 c = r.Center;
            var square = new Rect2 { min = c - Vector2.one * side * 0.5f, max = c + Vector2.one * side * 0.5f };

            MeshFilter filter = room.highlight.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return null;
            }
            bool[] mask = DrawMask(filter.sharedMesh, RelativeMatrix(map, room.highlight.transform), map, square, u, v, n);
            if (mask == null)
            {
                return null;
            }
            var result = new MapMask { rect = square, cell = side / MaskRes, mask = mask };
            foreach (bool on in mask)
            {
                if (on)
                {
                    result.count++;
                }
            }
            return result.count > 0 ? result : null;
        }

        // Draws a mesh with an orthographic view of the map plane and reads back which cells it covers.
        // Row 0 of the result is the bottom of the square (lowest v).
        private bool[] DrawMask(Mesh mesh, Matrix4x4 model, Transform map, Rect2 square, int u, int v, int n)
        {
            if (m_maskMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                {
                    Loader.Log("map marker: no mask shader");
                    return null;
                }
                m_maskMaterial = new Material(shader) { color = Color.white };
                m_maskMaterial.SetInt("_Cull", 0);
                m_maskMaterial.SetInt("_ZWrite", 0);
                m_maskMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                m_maskMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m_maskMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            }
            // View = map-local axes (u, v, n) -> (x, y, z).
            Matrix4x4 swizzle = Matrix4x4.zero;
            swizzle[0, u] = 1f;
            swizzle[1, v] = 1f;
            swizzle[2, n] = 1f;
            swizzle[3, 3] = 1f;
            // The mesh is given in the map's local space (RelativeMatrix), so "world" here is map-local and the
            // result doesn't depend on the opening animation's scale or the sheet's current rotation.
            Matrix4x4 view = swizzle;
            Matrix4x4 proj = Matrix4x4.Ortho(square.min.x, square.max.x, square.min.y, square.max.y, -10000f, 10000f);

            if (m_flipChecked == 0)
            {
                m_flipChecked = CheckRowOrder(view, proj, map, square, u, v, n);
            }

            Color32[] pixels = Draw(mesh, model, view, proj);
            if (pixels == null)
            {
                return null;
            }
            var mask = new bool[MaskRes * MaskRes];
            for (int y = 0; y < MaskRes; y++)
            {
                int srcRow = m_flipChecked < 0 ? MaskRes - 1 - y : y;
                for (int x = 0; x < MaskRes; x++)
                {
                    Color32 p = pixels[srcRow * MaskRes + x];
                    mask[y * MaskRes + x] = p.r > 127 || p.g > 127 || p.b > 127;
                }
            }
            return mask;
        }

        private Color32[] Draw(Mesh mesh, Matrix4x4 model, Matrix4x4 view, Matrix4x4 proj)
        {
            RenderTexture rt = RenderTexture.GetTemporary(MaskRes, MaskRes, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(MaskRes, MaskRes, TextureFormat.RGBA32, false);
            try
            {
                var cb = new UnityEngine.Rendering.CommandBuffer { name = "TSViewMod map mask" };
                cb.SetRenderTarget(rt);
                cb.ClearRenderTarget(true, true, Color.black);
                cb.SetViewProjectionMatrices(view, proj);
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    cb.DrawMesh(mesh, model, m_maskMaterial, i, 0);
                }
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, MaskRes, MaskRes), 0, 0, false);
                return tex.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Destroy(tex);
            }
        }

        // Draws a quad that covers only the bottom (low-v) quarter of the view and checks which rows lit up.
        private int CheckRowOrder(Matrix4x4 view, Matrix4x4 proj, Transform map, Rect2 square, int u, int v, int n)
        {
            var mesh = new Mesh();
            Vector3 Corner(float fu, float fv)
            {
                Vector3 local = Vector3.zero;
                local[u] = Mathf.Lerp(square.min.x, square.max.x, fu);
                local[v] = Mathf.Lerp(square.min.y, square.max.y, fv);
                return local;
            }
            mesh.vertices = new[] { Corner(0f, 0f), Corner(1f, 0f), Corner(1f, 0.25f), Corner(0f, 0.25f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            Color32[] pixels = Draw(mesh, Matrix4x4.identity, view, proj);
            Destroy(mesh);
            if (pixels == null)
            {
                return 1;
            }
            int low = 0;
            int high = 0;
            for (int x = 0; x < MaskRes; x++)
            {
                if (pixels[2 * MaskRes + x].r > 127)
                {
                    low++;
                }
                if (pixels[(MaskRes - 3) * MaskRes + x].r > 127)
                {
                    high++;
                }
            }
            int result = high > low ? -1 : 1;
            Loader.Log("map marker: mask row check low " + low + " high " + high + " -> " + (result < 0 ? "flipped" : "normal"));
            return result;
        }

        // ---------- shared helpers ----------

        internal struct Rect2
        {
            public Vector2 min;
            public Vector2 max;
            public Vector2 Size => max - min;
            public Vector2 Center => (min + max) * 0.5f;

            public static Rect2 Empty => new Rect2 { min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue) };

            public void Add(Vector2 p)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            public float Distance(Vector2 p)
            {
                float dx = Mathf.Max(min.x - p.x, 0f, p.x - max.x);
                float dy = Mathf.Max(min.y - p.y, 0f, p.y - max.y);
                return Mathf.Sqrt(dx * dx + dy * dy);
            }

            public override string ToString()
            {
                return "[" + min.x.ToString("0.00") + "," + min.y.ToString("0.00") + " .. " + max.x.ToString("0.00") + "," + max.y.ToString("0.00") + "]";
            }
        }

        private class RoomShape
        {
            public string name;
            public string tab;
            public Bounds bounds;
            public GameObject highlight;

            public Rect2 To2D(int u, int v)
            {
                return new Rect2 { min = new Vector2(bounds.min[u], bounds.min[v]), max = new Vector2(bounds.max[u], bounds.max[v]) };
            }
        }

        // Door spawn points of the current scene, keyed by the scene each door leads to.
        private static List<KeyValuePair<string, Vector2>> DoorPoints()
        {
            var result = new List<KeyValuePair<string, Vector2>>();
            IGameplaySceneManager manager = SingletonGameplaySceneManager.Instance;
            SceneDataSO data = null;
            string sceneId = null;
            try
            {
                data = manager?.GetSceneData();
                sceneId = manager?.GetCurrentSceneID();
            }
            catch (System.Exception)
            {
            }
            foreach (SceneEntrancePoints points in FindObjectsOfType<SceneEntrancePoints>())
            {
                if (points.sceneEntranceList == null)
                {
                    continue;
                }
                foreach (SceneEntrancePoints.EntrancePointData entry in points.sceneEntranceList)
                {
                    if (entry == null || entry.TransformData == null || string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }
                    string target = entry.Name;
                    if (data != null && sceneId != null)
                    {
                        SceneDataSO.DoorConnector connector = data.GetDoorConnector(sceneId, entry.Name);
                        if (connector != null && !string.IsNullOrEmpty(connector.SceneIdOnOpen))
                        {
                            string name = data.GetSceneName(connector.SceneIdOnOpen);
                            target = string.IsNullOrEmpty(name) ? connector.SceneIdOnOpen : name;
                        }
                    }
                    Vector3 p = entry.TransformData.position;
                    result.Add(new KeyValuePair<string, Vector2>(target, new Vector2(p.x, p.z)));
                }
            }
            return result;
        }

        // Highlight mesh bounds in the map's local space (mesh bounds work even when the mesh is not readable).
        // highlight -> map transform built from local TRS only, so it stays valid while the map's parent is scaled
        // to zero by the game's opening animation (world-space round trips would divide by zero there).
        public static Matrix4x4 RelativeMatrix(Transform root, Transform t)
        {
            Matrix4x4 m = Matrix4x4.identity;
            for (Transform x = t; x != null && x != root; x = x.parent)
            {
                m = Matrix4x4.TRS(x.localPosition, x.localRotation, x.localScale) * m;
            }
            return m;
        }

        // Highlight mesh bounds in the map's local space (mesh bounds work even when the mesh is not readable).
        private static bool TryLocalBounds(Transform map, GameObject highlight, out Bounds bounds)
        {
            bounds = default;
            MeshFilter filter = highlight.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return false;
            }
            Matrix4x4 rel = RelativeMatrix(map, highlight.transform);
            Bounds meshBounds = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = meshBounds.center + Vector3.Scale(meshBounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 local = rel.MultiplyPoint3x4(corner);
                if (i == 0)
                {
                    bounds = new Bounds(local, Vector3.zero);
                }
                else
                {
                    bounds.Encapsulate(local);
                }
            }
            return true;
        }

        private Transform CreateDot(GameObject highlight)
        {
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "TSViewMod_MapDot";
            Destroy(dot.GetComponent<Collider>());
            dot.layer = highlight.layer;
            // Child of the highlight so it shows and hides with it when the map tab changes.
            dot.transform.SetParent(highlight.transform, worldPositionStays: true);
            Renderer renderer = dot.GetComponent<Renderer>();
            renderer.sharedMaterial = GetMaterial(highlight.GetComponent<Renderer>());
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return dot.transform;
        }

        private void Apply(Marked marked)
        {
            if (marked.helper != null)
            {
                marked.helper.tintColor = m_enabled ? MarkerRed : marked.originalTint;
            }
            foreach (RoomDot room in marked.rooms)
            {
                if (room.dot != null)
                {
                    room.dot.gameObject.SetActive(m_enabled && room.ready);
                }
            }
        }

        private void Unmark(Marked marked)
        {
            if (marked.helper != null)
            {
                marked.helper.tintColor = marked.originalTint;
            }
            foreach (RoomDot room in marked.rooms)
            {
                if (room.dot != null)
                {
                    Destroy(room.dot.gameObject);
                }
            }
        }

        private Material GetMaterial(Renderer fallbackSource)
        {
            if (m_material != null)
            {
                return m_material;
            }
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null)
            {
                // Always on top: the sheet's folds and paper layers can't swallow the dot.
                m_material = new Material(shader) { color = MarkerRed, renderQueue = 4000 };
                m_material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                m_material.SetInt("_ZWrite", 0);
                m_material.SetInt("_Cull", 0);
                return m_material;
            }
            m_material = new Material(fallbackSource.sharedMaterial) { color = MarkerRed };
            m_material.EnableKeyword("_EMISSION");
            m_material.SetColor("_EmissionColor", MarkerRed * 3f);
            return m_material;
        }

        private static float Safe(float f)
        {
            return Mathf.Abs(f) < 1e-6f ? 1f : f;
        }
    }
}
