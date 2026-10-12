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
        private Material m_maskMaterial;
        private PlayerController m_player;
        private int m_flipChecked; // 0 unknown, 1 normal, -1 rows flipped

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

            if (m_player == null)
            {
                m_player = FindObjectOfType<PlayerController>();
            }
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
                    if (room.hasFit && m_player != null)
                    {
                        Vector3 p = m_player.transform.position;
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

            var log = new StringBuilder();
            log.Append("map marker: scene ").Append(scene).Append(", normal axis ").Append(marked.nAxis);
            WorldMask world = WorldMask.Build(log);
            List<KeyValuePair<string, Vector2>> doors = DoorPoints();
            log.Append(", doors ").Append(doors.Count);

            var seenHighlights = new HashSet<GameObject>();
            foreach (RoomShape room in rooms)
            {
                if (room.name != scene || !seenHighlights.Add(room.highlight))
                {
                    continue;
                }
                var dot = new RoomDot { highlight = room.highlight, normalCoord = room.bounds.center[marked.nAxis] };
                if (world != null)
                {
                    MapMask mask = RenderMapMask(map, room, marked.uAxis, marked.vAxis, marked.nAxis);
                    if (mask != null)
                    {
                        dot.fit = FitShape(world, mask, doors, rooms, marked.uAxis, marked.vAxis, log);
                        dot.hasFit = dot.fit.iou > 0.2f;
                        log.Append("\n  tab ").Append(room.tab).Append(": orientation ").Append(dot.fit.orientation)
                            .Append(" scale ").Append(dot.fit.scale.ToString("0.00000")).Append(" IoU ").Append(dot.fit.iou.ToString("0.000"))
                            .Append(dot.hasFit ? "" : " (too weak, using room centre)");
                    }
                }
                dot.dot = CreateDot(room.highlight);
                marked.rooms.Add(dot);
            }
            log.Append("\n  dots ").Append(marked.rooms.Count);
            Loader.Log(log.ToString());
            Apply(marked);
            return marked;
        }

        // ---------- fitting ----------

        private struct Fit
        {
            public int orientation;
            public float scale;
            public Vector2 offset;
            public float iou;
            public float score;

            public Vector2 Apply(Vector2 world)
            {
                return Orient(orientation, world) * scale + offset;
            }
        }

        private static Fit FitShape(WorldMask world, MapMask map, List<KeyValuePair<string, Vector2>> doors, List<RoomShape> rooms, int u, int v, StringBuilder log)
        {
            float mapArea = map.count * map.cell * map.cell;
            float worldArea = world.count * world.cell * world.cell;
            float s0 = Mathf.Sqrt(mapArea / Mathf.Max(worldArea, 1e-6f));
            Vector2 worldCentroid = world.Centroid();
            Vector2 mapCentroid = map.Centroid();

            Rect2 roomRect = map.rect;
            float diag = roomRect.Size.magnitude;
            var best = new Fit { score = float.MaxValue };
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
                            fit.iou = IoU(world, map, fit);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                    }
                }
                // Refine scale and offset around the best coarse candidate.
                Fit coarse = orientBest;
                for (int ks = -4; ks <= 4; ks++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            var fit = new Fit { orientation = o, scale = coarse.scale * (1f + ks * 0.01f), offset = coarse.offset + new Vector2(dx, dy) * map.cell * 0.5f };
                            fit.iou = IoU(world, map, fit);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                    }
                }
                orientBest.score += 0.1f * DoorPenalty(orientBest, doors, rooms, u, v, roomRect, diag);
                log.Append("\n    o").Append(o).Append(" IoU ").Append(orientBest.iou.ToString("0.000")).Append(" score ").Append(orientBest.score.ToString("0.000"));
                if (orientBest.score < best.score)
                {
                    best = orientBest;
                }
            }
            return best;
        }

        private static float IoU(WorldMask world, MapMask map, Fit fit)
        {
            // Walk the map mask grid; map each cell centre back into the world mask.
            Vector2 a = Orient(fit.orientation, Vector2.right);
            Vector2 b = Orient(fit.orientation, Vector2.up);
            float inv = 1f / fit.scale;
            int inter = 0;
            int worldInside = 0;
            for (int y = 0; y < MaskRes; y++)
            {
                for (int x = 0; x < MaskRes; x++)
                {
                    Vector2 uv = map.CellCentre(x, y);
                    Vector2 q = (uv - fit.offset) * inv;
                    Vector2 w = new Vector2(Vector2.Dot(q, a), Vector2.Dot(q, b));
                    bool inWorld = world.Get(w);
                    if (inWorld)
                    {
                        worldInside++;
                        if (map.mask[y * MaskRes + x])
                        {
                            inter++;
                        }
                    }
                }
            }
            // World cells that land outside the map grid count against the fit too.
            float worldCellsInMapUnits = world.count * world.cell * world.cell * fit.scale * fit.scale / (map.cell * map.cell);
            float worldTotal = Mathf.Max(worldInside, worldCellsInMapUnits);
            float union = map.count + worldTotal - inter;
            return union > 0f ? inter / union : 0f;
        }

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

        private class WorldMask
        {
            public Rect2 rect;
            public float cell;
            public int w;
            public int h;
            public bool[] mask;
            public int count;

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
                    return null;
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
            bool[] mask = DrawMask(filter.sharedMesh, room.highlight.transform.localToWorldMatrix, map, square, u, v, n);
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
            Matrix4x4 view = swizzle * map.worldToLocalMatrix;
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
                return map.TransformPoint(local);
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

        private struct Rect2
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
        private static bool TryLocalBounds(Transform map, GameObject highlight, out Bounds bounds)
        {
            bounds = default;
            MeshFilter filter = highlight.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return false;
            }
            Bounds meshBounds = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = meshBounds.center + Vector3.Scale(meshBounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 local = map.InverseTransformPoint(highlight.transform.TransformPoint(corner));
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
