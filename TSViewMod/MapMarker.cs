using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // The game's map (MapHelperRoom) only lights up the room you are in, with a faint white flicker.
    // This tints that glow red and puts a pulsing red dot at the player's actual position.
    //
    // Every room is its own scene built around its own origin, so there is no global world->map transform.
    // Per room we fit one: the room's walkable footprint (NavMesh, else floor colliders) is matched to the
    // room's highlight rectangle on the map under each of the 8 axis-aligned orientations, and the door
    // spawn points (SceneEntrancePoints) pick the orientation whose doors land next to the right neighbour rooms.
    public class MapMarker : MonoBehaviour
    {
        private static readonly Color MarkerRed = new Color(1f, 0.05f, 0.05f, 1f);

        private struct Rect2
        {
            public Vector2 min;
            public Vector2 max;
            public Vector2 Size => max - min;
            public Vector2 Center => (min + max) * 0.5f;

            public static Rect2 Empty => new Rect2 { min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue) };

            public bool IsValid => max.x > min.x && max.y > min.y;

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
                return "[" + min.x.ToString("0.000") + "," + min.y.ToString("0.000") + " .. " + max.x.ToString("0.000") + "," + max.y.ToString("0.000") + "]";
            }
        }

        // world (x,z) -> map plane (u,v): rotate/mirror by orientation, then per-axis scale and offset.
        private struct Fit
        {
            public int orientation;
            public Vector2 scale;
            public Vector2 offset;
            public float score;

            public Vector2 Apply(Vector2 world)
            {
                Vector2 r = Orient(orientation, world);
                return new Vector2(r.x * scale.x + offset.x, r.y * scale.y + offset.y);
            }
        }

        private class RoomDot
        {
            public GameObject highlight;
            public Transform dot;
            public Fit fit;
            public bool hasFit;
            public float normalCoord;
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
            public readonly List<RoomDot> rooms = new List<RoomDot>();
        }

        private readonly List<Marked> m_marked = new List<Marked>();
        private bool m_enabled;
        private float m_nextScan;
        private Material m_material;

        private void Start()
        {
            m_enabled = ViewCamera.Instance == null || ViewCamera.Instance.Config.MapMarker;
        }

        private void Update()
        {
            Config config = ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
            if (config != null && Input.GetKeyDown(config.MapMarkerKey))
            {
                m_enabled = !m_enabled;
                ViewCamera.Instance.ShowToast(m_enabled ? "Map marker: on" : "Map marker: off");
                foreach (Marked marked in m_marked)
                {
                    Apply(marked);
                }
            }

            m_marked.RemoveAll(m => m.helper == null);
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

            PlayerController player = FindPlayer();
            Camera viewer = Camera.main;
            float pulse = 1f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f);
            foreach (Marked marked in m_marked)
            {
                foreach (RoomDot room in marked.rooms)
                {
                    if (room.dot == null)
                    {
                        continue;
                    }
                    Vector3 local;
                    if (room.hasFit && player != null)
                    {
                        Vector3 p = player.transform.position;
                        Vector2 uv = room.fit.Apply(new Vector2(p.x, p.z));
                        local = Vector3.zero;
                        local[marked.uAxis] = uv.x;
                        local[marked.vAxis] = uv.y;
                        local[marked.nAxis] = room.normalCoord;
                    }
                    else
                    {
                        Renderer r = room.highlight.GetComponent<Renderer>();
                        local = marked.helper.transform.InverseTransformPoint(r != null ? r.bounds.center : room.highlight.transform.position);
                    }
                    Vector3 world = marked.helper.transform.TransformPoint(local);
                    Vector3 toViewer = viewer != null ? (viewer.transform.position - world).normalized : Vector3.up;
                    room.dot.position = world + toViewer * marked.dotSize * 0.6f;
                    float size = marked.dotSize * pulse;
                    Vector3 parentScale = room.dot.parent != null ? room.dot.parent.lossyScale : Vector3.one;
                    room.dot.localScale = new Vector3(size / Safe(parentScale.x), size / Safe(parentScale.y), size / Safe(parentScale.z));
                }
            }
        }

        private PlayerController m_player;

        private PlayerController FindPlayer()
        {
            if (m_player == null)
            {
                m_player = FindObjectOfType<PlayerController>();
            }
            return m_player;
        }

        private Marked Mark(MapHelperRoom helper, string scene)
        {
            var marked = new Marked { helper = helper, originalTint = helper.tintColor, scene = scene };
            Transform map = helper.transform;

            // Every room rectangle on this map, in the map object's local space.
            var roomRects = new Dictionary<string, Rect3>();
            Vector3 spreadMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 spreadMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (MapHelperStringToGameObject entry in helper.sceneToGameObjects)
            {
                if (entry == null || entry.highlight == null || string.IsNullOrEmpty(entry.name))
                {
                    continue;
                }
                if (!TryLocalBounds(map, entry.highlight, out Bounds b))
                {
                    continue;
                }
                spreadMin = Vector3.Min(spreadMin, b.min);
                spreadMax = Vector3.Max(spreadMax, b.max);
                string key = entry.name + "#" + entry.tabID;
                if (!roomRects.ContainsKey(key))
                {
                    roomRects[key] = new Rect3 { name = entry.name, tab = entry.tabID, bounds = b, highlight = entry.highlight };
                }
            }
            Vector3 spread = spreadMax - spreadMin;
            marked.nAxis = spread.x <= spread.y && spread.x <= spread.z ? 0 : spread.y <= spread.z ? 1 : 2;
            marked.uAxis = marked.nAxis == 0 ? 1 : 0;
            marked.vAxis = marked.nAxis == 2 ? 1 : 2;
            float mapSize = Mathf.Max(spread[marked.uAxis], spread[marked.vAxis]);

            Footprint footprint = Footprint.Measure();
            List<KeyValuePair<string, Vector2>> doors = DoorPoints();
            var log = new StringBuilder();
            log.Append("map marker: scene ").Append(scene).Append(", map plane normal axis ").Append(marked.nAxis)
                .Append(", footprint ").Append(footprint.source).Append(' ').Append(footprint.rect).Append(", doors ").Append(doors.Count);

            foreach (Rect3 room in roomRects.Values)
            {
                if (room.name != scene)
                {
                    continue;
                }
                Rect2 roomRect = room.To2D(marked.uAxis, marked.vAxis);
                var dot = new RoomDot { highlight = room.highlight, normalCoord = room.bounds.center[marked.nAxis] };
                if (footprint.rect.IsValid && roomRect.IsValid)
                {
                    dot.fit = BestFit(footprint.rect, roomRect, doors, roomRects, marked.uAxis, marked.vAxis, log);
                    dot.hasFit = true;
                    log.Append("\n  tab ").Append(room.tab).Append(" room ").Append(roomRect).Append(" -> orientation ").Append(dot.fit.orientation)
                        .Append(" score ").Append(dot.fit.score.ToString("0.000"));
                }
                dot.dot = CreateDot(room.highlight);
                marked.rooms.Add(dot);
            }
            marked.dotSize = Mathf.Max(mapSize * 0.03f, 0.002f);
            log.Append("\n  dots ").Append(marked.rooms.Count).Append(", dot size ").Append(marked.dotSize.ToString("0.0000"));
            Loader.Log(log.ToString());
            Apply(marked);
            return marked;
        }

        private static Fit BestFit(Rect2 world, Rect2 room, List<KeyValuePair<string, Vector2>> doors, Dictionary<string, Rect3> rooms, int u, int v, StringBuilder log)
        {
            var best = new Fit { score = float.MaxValue };
            float diag = room.Size.magnitude;
            for (int o = 0; o < 8; o++)
            {
                Rect2 oriented = Rect2.Empty;
                oriented.Add(Orient(o, world.min));
                oriented.Add(Orient(o, world.max));
                oriented.Add(Orient(o, new Vector2(world.min.x, world.max.y)));
                oriented.Add(Orient(o, new Vector2(world.max.x, world.min.y)));
                var fit = new Fit
                {
                    orientation = o,
                    scale = new Vector2(room.Size.x / oriented.Size.x, room.Size.y / oriented.Size.y)
                };
                fit.offset = new Vector2(room.min.x - oriented.min.x * fit.scale.x, room.min.y - oriented.min.y * fit.scale.y);

                // A map has one scale for both axes, so a fit that stretches the room is wrong.
                float score = Mathf.Abs(Mathf.Log(Mathf.Abs(fit.scale.x / fit.scale.y)));
                int matchedDoors = 0;
                foreach (KeyValuePair<string, Vector2> door in doors)
                {
                    Rect2 neighbour = Rect2.Empty;
                    bool found = false;
                    foreach (Rect3 r in rooms.Values)
                    {
                        if (r.name == door.Key)
                        {
                            Rect2 rr = r.To2D(u, v);
                            if (!found || rr.Distance(room.Center) < neighbour.Distance(room.Center))
                            {
                                neighbour = rr;
                                found = true;
                            }
                        }
                    }
                    if (!found)
                    {
                        continue;
                    }
                    matchedDoors++;
                    score += 3f * neighbour.Distance(fit.Apply(door.Value)) / Mathf.Max(diag, 1e-5f);
                }
                fit.score = score;
                log.Append("\n    o").Append(o).Append(" score ").Append(score.ToString("0.000")).Append(" doors ").Append(matchedDoors);
                if (score < best.score)
                {
                    best = fit;
                }
            }
            return best;
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

        private struct Footprint
        {
            public Rect2 rect;
            public string source;

            public static Footprint Measure()
            {
                var nav = Rect2.Empty;
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices != null)
                {
                    foreach (Vector3 p in tri.vertices)
                    {
                        nav.Add(new Vector2(p.x, p.z));
                    }
                }
                if (nav.IsValid)
                {
                    return new Footprint { rect = nav, source = "navmesh" };
                }
                // No navmesh in this room: use floor-like colliders around the player's feet.
                var floor = Rect2.Empty;
                PlayerController player = FindObjectOfType<PlayerController>();
                float feet = player != null ? player.transform.position.y : 0f;
                Scene active = SceneManager.GetActiveScene();
                foreach (Collider c in FindObjectsOfType<Collider>())
                {
                    if (c.isTrigger || c.gameObject.scene != active || !c.enabled)
                    {
                        continue;
                    }
                    Bounds b = c.bounds;
                    if (b.size.y > 1f || Mathf.Abs(b.max.y - feet) > 1f || b.size.x > 60f || b.size.z > 60f)
                    {
                        continue;
                    }
                    floor.Add(new Vector2(b.min.x, b.min.z));
                    floor.Add(new Vector2(b.max.x, b.max.z));
                }
                return new Footprint { rect = floor, source = "floor colliders" };
            }
        }

        private class Rect3
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

        // Highlight mesh bounds in the map's local space (mesh bounds work even when the mesh is not readable).
        private static bool TryLocalBounds(Transform map, GameObject highlight, out Bounds bounds)
        {
            bounds = default;
            MeshFilter filter = highlight.GetComponent<MeshFilter>();
            Bounds meshBounds;
            Transform space;
            if (filter != null && filter.sharedMesh != null)
            {
                meshBounds = filter.sharedMesh.bounds;
                space = highlight.transform;
            }
            else
            {
                Renderer r = highlight.GetComponent<Renderer>();
                if (r == null)
                {
                    return false;
                }
                meshBounds = r.bounds;
                space = null;
            }
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = meshBounds.center + Vector3.Scale(meshBounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 world = space != null ? space.TransformPoint(corner) : corner;
                Vector3 local = map.InverseTransformPoint(world);
                if (first)
                {
                    bounds = new Bounds(local, Vector3.zero);
                    first = false;
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
            // Parent to the map rather than the highlight: the game hides highlights of other tabs, and
            // the dot must follow the same visibility, so keep it under the highlight but positioned by us.
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
                    room.dot.gameObject.SetActive(m_enabled);
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
            foreach (string shaderName in new[] { "Sprites/Default", "UI/Default", "Unlit/Color" })
            {
                Shader shader = Shader.Find(shaderName);
                if (shader != null)
                {
                    m_material = new Material(shader) { color = MarkerRed, renderQueue = 3100 };
                    return m_material;
                }
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
