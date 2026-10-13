using System;
using System.IO;
using System.Text;
using Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // F8: on-screen state readout and door markers on the map. F9: screenshot + state dump to TSViewMod_debug/.
    public class DebugTools : MonoBehaviour
    {
        public static bool Enabled { get; private set; }

        // Filled in by MapMarker while a map is open.
        public static string MapInfo = "";

        private float m_fps;
        private string m_lastSnapshot;

        private static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "TSViewMod_debug");

        private void Update()
        {
            Config config = ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
            if (config == null)
            {
                return;
            }
            m_fps = Mathf.Lerp(m_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            if (Input.GetKeyDown(config.DebugOverlayKey))
            {
                Enabled = !Enabled;
                ViewCamera.Instance.ShowToast(Enabled ? "Debug overlay: on" : "Debug overlay: off");
            }
            if (Input.GetKeyDown(config.SnapshotKey))
            {
                Snapshot();
            }
        }

        private void OnGUI()
        {
            if (!Enabled)
            {
                return;
            }
            string text = State();
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 70), wordWrap = false };
            var size = style.CalcSize(new GUIContent(text));
            var rect = new Rect(12f, 12f, size.x + 16f, size.y + 12f);
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            style.normal.textColor = new Color(0.6f, 1f, 0.6f);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, size.x, size.y), text, style);
        }

        public string State()
        {
            var sb = new StringBuilder();
            sb.Append("TSViewMod v").Append(typeof(Loader).Assembly.GetName().Version).Append("   ").Append(m_fps.ToString("0")).Append(" fps\n");
            sb.Append("scene: ").Append(SceneManager.GetActiveScene().name).Append('\n');
            PlayerController player = FindObjectOfType<PlayerController>();
            if (player != null)
            {
                Vector3 p = player.transform.position;
                sb.Append("player: ").Append(p.ToString("0.00")).Append("  facing ").Append(player.transform.eulerAngles.y.ToString("0")).Append('\n');
                PlayerMovement movement = player.GetPlayerMovement();
                sb.Append("free play: ").Append(movement != null && !movement.GamePaused).Append('\n');
            }
            ViewCamera view = ViewCamera.Instance;
            if (view != null)
            {
                sb.Append(view.DebugState()).Append('\n');
            }
            Camera main = Camera.main;
            CinemachineBrain brain = main != null ? main.GetComponent<CinemachineBrain>() : null;
            if (brain != null && brain.ActiveVirtualCamera != null)
            {
                sb.Append("active camera: ").Append(brain.ActiveVirtualCamera.Name).Append('\n');
            }
            if (!string.IsNullOrEmpty(MapInfo))
            {
                sb.Append(MapInfo).Append('\n');
            }
            sb.Append("F8 overlay  F9 snapshot -> TSViewMod_debug/");
            if (m_lastSnapshot != null)
            {
                sb.Append("\nlast snapshot: ").Append(m_lastSnapshot);
            }
            return sb.ToString();
        }

        private void Snapshot()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                string image = Path.Combine(Folder, stamp + ".png");
                ScreenCapture.CaptureScreenshot(image);
                File.WriteAllText(Path.Combine(Folder, stamp + ".txt"), State());
                m_lastSnapshot = stamp;
                Loader.Log("debug snapshot " + stamp + ":\n" + State());
                ViewCamera.Instance?.ShowToast("Snapshot saved: " + stamp);
            }
            catch (Exception e)
            {
                Loader.Log("snapshot failed: " + e.Message);
            }
        }
    }
}
