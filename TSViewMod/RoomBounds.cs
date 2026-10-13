using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // The current room's walkable area (navmesh, or a floor probe where there is none), used to keep the camera out
    // of the void behind collider-less walls and reused by the map fit. Built over a few frames, a second after
    // each scene loads; until then every point counts as inside.
    public class RoomBounds : MonoBehaviour
    {
        private const long FrameBudgetMs = 2;

        private static MapMarker.WorldMask s_mask;
        private static string s_scene;
        private static bool s_ready;
        private float m_buildAt;
        private bool m_building;
        private Coroutine m_job;

        public static bool Inside(Vector3 p, float overhang)
        {
            return !s_ready || s_mask == null || s_scene != SceneManager.GetActiveScene().name || s_mask.Near(new Vector2(p.x, p.z), overhang);
        }

        internal static MapMarker.WorldMask MaskFor(string scene)
        {
            return s_ready && s_scene == scene ? s_mask : null;
        }

        private void Update()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (s_scene != scene)
            {
                s_scene = scene;
                s_mask = null;
                s_ready = false;
                if (m_job != null)
                {
                    StopCoroutine(m_job);
                    m_job = null;
                }
                m_building = false;
                m_buildAt = Time.unscaledTime + 1f; // navmesh and entrance points settle
            }
            if (s_ready || m_building || Time.unscaledTime < m_buildAt)
            {
                return;
            }
            m_buildAt = Time.unscaledTime + 0.5f;
            if (FindObjectOfType<PlayerController>() != null)
            {
                m_job = StartCoroutine(Build(scene));
            }
        }

        private System.Collections.IEnumerator Build(string scene)
        {
            m_building = true;
            var log = new StringBuilder("camera bounds: ").Append(scene);
            MapMarker.WorldMask mask = null;
            yield return MapMarker.WorldMask.BuildSteps(log, Survey.Active ? 80 : FrameBudgetMs, m => mask = m);
            if (s_scene == scene)
            {
                s_mask = mask;
                s_ready = true;
                Loader.Log(log.ToString());
            }
            m_building = false;
            m_job = null;
        }
    }
}
