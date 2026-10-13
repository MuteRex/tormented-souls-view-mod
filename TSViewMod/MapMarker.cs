using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // Tints the map's current-room glow red and adds a red dot (with facing arrow) at the player's position.
    // Rooms are separate scenes with no shared world->map transform, so each is fitted by shape: the highlight
    // mesh is drawn into a mask on the GPU, the walkable area is rasterised, and 8 orientations x scale x offset are
    // searched for the best IoU, with door spawn points breaking ties.
    public partial class MapMarker : MonoBehaviour
    {
        private static readonly Color MarkerRed = new Color(1f, 0.05f, 0.05f, 1f);
        private const int MaskRes = 64;

        private class RoomDot
        {
            public GameObject highlight;
            public Transform dot;
            public Transform arrow;
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

        public static bool IsCached(string scene, string tab, string highlightName)
        {
            return FitCache.TryGetTrusted(scene + "|" + tab + "|" + highlightName, out Fit _);
        }

        // For Survey: true once every room on this scene's map has a fit (cached or new).
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
                    Vector3 heading = Vector3.zero; // her facing, in the sheet's local space
                    if (room.hasFit && m_player != null)
                    {
                        Vector3 p = m_player.transform.position;
                        Vector2 uv = room.fit.Apply(new Vector2(p.x, p.z));
                        Vector3 f = m_player.transform.forward;
                        Vector2 ahead = room.fit.Apply(new Vector2(p.x + f.x, p.z + f.z)) - uv;
                        heading[marked.uAxis] = ahead.x;
                        heading[marked.vAxis] = ahead.y;
                        Vector2 rawUv = uv;
                        // Snap into the drawn shape (stair landings can map past the outline; L-shaped rooms' boxes overlap others).
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
                    Vector3 front = sheet.TransformDirection(FrontNormalLocal(marked.nAxis, marked.frontSign)).normalized;
                    room.dot.position = world + front * marked.dotSize * 0.6f;
                    // Drawn on top, so hide it when the sheet's back faces the camera.
                    Camera viewer = MapCamera(room.highlight, world);
                    bool facing = !marked.frontKnown || viewer == null || Vector3.Dot(front, viewer.transform.position - world) > 0f;
                    Renderer dotRenderer = room.dot.GetComponent<Renderer>();
                    if (dotRenderer.enabled != facing)
                    {
                        dotRenderer.enabled = facing;
                    }
                    UpdateArrow(room, sheet.TransformDirection(heading), front, facing && (ViewCamera.Instance == null || ViewCamera.Instance.Config.MapArrow), pulse);
                    UpdateDoorDots(marked, room);
                    float size = marked.dotSize * pulse;
                    Vector3 parentScale = room.dot.parent != null ? room.dot.parent.lossyScale : Vector3.one;
                    room.dot.localScale = new Vector3(size / Safe(parentScale.x), size / Safe(parentScale.y), size / Safe(parentScale.z));
                }
            }
        }

        // Facing pointer: child of the dot, with the dot's pulse cancelled out.
        private static Mesh s_arrowMesh;

        private void UpdateArrow(RoomDot room, Vector3 heading, Vector3 front, bool show, float pulse)
        {
            heading -= front * Vector3.Dot(heading, front);
            show &= heading.sqrMagnitude > 1e-12f;
            if (room.arrow == null)
            {
                if (!show)
                {
                    return;
                }
                var go = new GameObject("TSViewMod_MapArrow");
                go.layer = room.dot.gameObject.layer;
                go.transform.SetParent(room.dot, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = ArrowMesh();
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = room.dot.GetComponent<Renderer>().sharedMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                room.arrow = go.transform;
            }
            Renderer arrowRenderer = room.arrow.GetComponent<Renderer>();
            if (arrowRenderer.enabled != show)
            {
                arrowRenderer.enabled = show;
            }
            if (!show)
            {
                return;
            }
            heading.Normalize();
            float size = room.dot.lossyScale.x / Mathf.Max(pulse, 0.01f); // the dot's size without the pulse
            room.arrow.rotation = Quaternion.LookRotation(front, heading);
            room.arrow.position = room.dot.position + heading * size * 0.55f;
            Vector3 dotScale = room.dot.lossyScale;
            room.arrow.localScale = new Vector3(size / Safe(dotScale.x), size / Safe(dotScale.y), size / Safe(dotScale.z));
        }

        private static Mesh ArrowMesh()
        {
            if (s_arrowMesh == null)
            {
                s_arrowMesh = new Mesh { name = "TSViewMod_Arrow" };
                s_arrowMesh.vertices = new[] { new Vector3(0f, 1.1f, 0f), new Vector3(-0.55f, 0f, 0f), new Vector3(0.55f, 0f, 0f) };
                s_arrowMesh.triangles = new[] { 0, 1, 2, 0, 2, 1 };
                s_arrowMesh.RecalculateNormals();
                s_arrowMesh.RecalculateBounds();
            }
            return s_arrowMesh;
        }

        private static readonly Color DoorBlue = new Color(0.1f, 0.45f, 1f, 1f);
        private Material m_doorMaterial;

        // Debug: door spawn points through the fit; each should sit at its doorway.
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
                // Known map: use the agreed pose straight away (this open still votes).
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
                // GPU draw of the room outline, used to keep the dot inside it.
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

        // Fits uncached rooms, a few milliseconds per frame.
        private IEnumerator FitRooms(Marked marked, List<RoomShape> rooms, List<KeyValuePair<RoomShape, RoomDot>> pending)
        {
            var clock = Stopwatch.StartNew();
            var log = new StringBuilder();
            log.Append("map marker: scene ").Append(marked.scene).Append(", normal axis ").Append(marked.nAxis);
            WorldMask world = RoomBounds.MaskFor(marked.scene);
            if (world == null)
            {
                IEnumerator build = WorldMask.BuildSteps(log, FrameBudgetMs, m => world = m);
                while (build.MoveNext())
                {
                    yield return null;
                }
            }
            else
            {
                log.Append(", walkable area from camera bounds");
            }
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

        // The game opens the map printed side toward the viewer, upright: once settled, record which side and which
        // axis is up. Used by the dot and the inspect reset.
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
            // Wait for the opening scale/swing animation to stop.
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

        // Local-TRS-only transform: valid while the opening animation scales the parent to zero.
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
            // Child of the highlight so it follows map tab changes.
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
