using System.Collections.Generic;
using UnityEngine;

namespace TSViewMod
{
    // The game's map (MapHelperRoom) already lights up the room you are in with a faint white flicker.
    // This makes it obvious: the glow turns red and a pulsing red dot sits on the room.
    public class MapMarker : MonoBehaviour
    {
        private static readonly Color MarkerRed = new Color(1f, 0.05f, 0.05f, 1f);

        private class Marked
        {
            public MapHelperRoom helper;
            public Color originalTint;
            public readonly List<KeyValuePair<Renderer, Transform>> dots = new List<KeyValuePair<Renderer, Transform>>();
            public float dotSize;
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
                foreach (MapHelperRoom helper in FindObjectsOfType<MapHelperRoom>())
                {
                    if (m_marked.Find(m => m.helper == helper) == null)
                    {
                        m_marked.Add(Mark(helper));
                    }
                }
            }

            Camera viewer = Camera.main;
            float pulse = 1f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f);
            foreach (Marked marked in m_marked)
            {
                foreach (KeyValuePair<Renderer, Transform> dot in marked.dots)
                {
                    if (dot.Value == null)
                    {
                        continue;
                    }
                    Bounds room = dot.Key.bounds;
                    Vector3 toViewer = viewer != null ? (viewer.transform.position - room.center).normalized : Vector3.up;
                    dot.Value.position = room.center + toViewer * marked.dotSize * 0.6f;
                    dot.Value.localScale = Vector3.one;
                    float worldSize = marked.dotSize * pulse;
                    Vector3 parentScale = dot.Value.parent != null ? dot.Value.parent.lossyScale : Vector3.one;
                    dot.Value.localScale = new Vector3(worldSize / Safe(parentScale.x), worldSize / Safe(parentScale.y), worldSize / Safe(parentScale.z));
                }
            }
        }

        private Marked Mark(MapHelperRoom helper)
        {
            var marked = new Marked { helper = helper, originalTint = helper.tintColor };
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            float mapSize = SecondLargest(CombinedBounds(helper.gameObject).size);
            foreach (MapHelperStringToGameObject entry in helper.sceneToGameObjects)
            {
                if (entry == null || entry.name != scene || entry.highlight == null)
                {
                    continue;
                }
                Renderer roomRenderer = entry.highlight.GetComponent<Renderer>();
                if (roomRenderer == null)
                {
                    continue;
                }
                float roomSize = SecondLargest(roomRenderer.bounds.size);
                float size = mapSize > 0f ? mapSize * 0.035f : roomSize * 0.5f;
                if (roomSize > 0f)
                {
                    size = Mathf.Min(size, roomSize * 0.9f);
                }
                marked.dotSize = Mathf.Max(size, 0.002f);

                GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                dot.name = "TSViewMod_MapDot";
                Destroy(dot.GetComponent<Collider>());
                dot.layer = entry.highlight.layer;
                dot.transform.SetParent(entry.highlight.transform, worldPositionStays: true);
                Renderer dotRenderer = dot.GetComponent<Renderer>();
                dotRenderer.sharedMaterial = GetMaterial(roomRenderer);
                dotRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                dotRenderer.receiveShadows = false;
                marked.dots.Add(new KeyValuePair<Renderer, Transform>(roomRenderer, dot.transform));
            }
            Loader.Log("map marker: scene " + scene + ", " + marked.dots.Count + " room highlight(s), dot size " + marked.dotSize.ToString("0.0000"));
            Apply(marked);
            return marked;
        }

        private void Apply(Marked marked)
        {
            if (marked.helper != null)
            {
                marked.helper.tintColor = m_enabled ? MarkerRed : marked.originalTint;
            }
            foreach (KeyValuePair<Renderer, Transform> dot in marked.dots)
            {
                if (dot.Value != null)
                {
                    dot.Value.gameObject.SetActive(m_enabled);
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
                    Loader.Log("map marker shader: " + shaderName);
                    return m_material;
                }
            }
            m_material = new Material(fallbackSource.sharedMaterial) { color = MarkerRed };
            m_material.EnableKeyword("_EMISSION");
            m_material.SetColor("_EmissionColor", MarkerRed * 3f);
            Loader.Log("map marker shader: copied from map highlight");
            return m_material;
        }

        private static Bounds CombinedBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers.Length == 0)
            {
                return new Bounds(root.transform.position, Vector3.zero);
            }
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer r in renderers)
            {
                if (r.name != "TSViewMod_MapDot")
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
            return bounds;
        }

        private static float SecondLargest(Vector3 v)
        {
            float a = v.x, b = v.y, c = v.z;
            float max = Mathf.Max(a, Mathf.Max(b, c));
            float min = Mathf.Min(a, Mathf.Min(b, c));
            return a + b + c - max - min;
        }

        private static float Safe(float f)
        {
            return Mathf.Abs(f) < 1e-6f ? 1f : f;
        }
    }
}
