using System.Collections.Generic;
using System.Reflection;
using Cinemachine;
using Rewired;
using UnityEngine;
using UnityEngine.Rendering;

namespace TSViewMod
{
    public enum ViewMode
    {
        Original,
        ThirdPerson,
        FirstPerson
    }

    // Adds a mod-owned Cinemachine virtual camera that sits one priority step above the game's
    // fixed-angle ClearShot camera. When the game hands control to a puzzle/zoom camera it drops the
    // main camera's priority to 0; we copy that, so puzzles and cutscenes keep their own shots.
    // Runs before CinemachineBrain's LateUpdate so the brain picks up this frame's pose.
    [DefaultExecutionOrder(-500)]
    public class ViewCamera : MonoBehaviour
    {
        private static readonly FieldInfo MainVirtualCameraField =
            typeof(GameplayCameraManager).GetField("m_mainVirtualCamera", BindingFlags.Instance | BindingFlags.NonPublic);

        private Config m_config;
        private ViewMode m_mode;

        private CinemachineVirtualCamera m_vcam;
        private GameplayCameraManager m_cameraManager;
        private PlayerController m_player;
        private Transform m_head;

        private float m_yaw;
        private float m_pitch;
        private Vector3 m_pivot;
        private bool m_snapNextFrame = true;
        private bool m_tooClose;

        // Below CloseDistance from the pivot the camera rises over the shoulder; below FadeDistance she is hidden
        // (shadow only) rather than letting the camera sit inside her head.
        private const float CloseDistance = 0.9f;
        private const float FadeDistance = 0.5f;

        private readonly List<KeyValuePair<Renderer, ShadowCastingMode>> m_hiddenRenderers = new List<KeyValuePair<Renderer, ShadowCastingMode>>();

        private string m_toast;
        private float m_toastUntil;
        private float m_nextSearch;

        public static ViewCamera Instance { get; private set; }

        public Config Config => m_config;

        private void Awake()
        {
            Instance = this;
            m_config = Config.Load();
            m_mode = ParseMode(m_config.StartView);
            Loader.Log("view camera ready, start view " + m_mode + ", cycle key " + m_config.CycleViewKey);
        }

        private void Update()
        {
            // R3 is shared with the inspect reset, so it only cycles views during free play.
            bool r3 = m_config.ControllerR3CyclesView && InFreePlay() && RightStickClicked();
            if (Input.GetKeyDown(m_config.CycleViewKey) || r3)
            {
                SetMode((ViewMode)(((int)m_mode + 1) % 3));
            }
        }

        private bool InFreePlay()
        {
            PlayerMovement movement = m_player != null ? m_player.GetPlayerMovement() : null;
            return movement != null && !movement.GamePaused;
        }

        private void LateUpdate()
        {
            FindSceneObjects();
            EnsureVirtualCamera();

            CinemachineVirtualCameraBase mainCamera = m_cameraManager != null ? MainVirtualCameraField?.GetValue(m_cameraManager) as CinemachineVirtualCameraBase : null;
            bool canTakeOver = m_mode != ViewMode.Original && m_player != null && mainCamera != null && mainCamera.Priority > 0;
            m_vcam.Priority = canTakeOver ? mainCamera.Priority + 1 : 0;
            bool hideBody = canTakeOver && (m_mode == ViewMode.FirstPerson && m_config.HideBodyInFirstPerson || m_mode == ViewMode.ThirdPerson && m_tooClose);
            UpdateBodyVisibility(hideBody);
            if (!canTakeOver)
            {
                m_snapNextFrame = true;
                return;
            }

            CinemachineBrain brain = m_cameraManager.GetBrain();
            bool live = brain != null && brain.ActiveVirtualCamera == (ICinemachineCamera)m_vcam;
            // GameplayState_FreePlay unpauses the player on enter and pauses it on exit, so GamePaused is true
            // while inspecting/rotating items, in menus and during pickups (which share the look input).
            PlayerMovement movement = m_player.GetPlayerMovement();
            bool freePlay = movement == null || !movement.GamePaused;
            if (live && freePlay && Time.timeScale > 0f)
            {
                ReadLookInput();
            }
            PlaceCamera(brain);
        }

        private void OnGUI()
        {
            if (m_toast == null || Time.unscaledTime > m_toastUntil)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(18, Screen.height / 40), fontStyle = FontStyle.Bold };
            var rect = new Rect(30f, Screen.height - 90f, Screen.width, 60f);
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), m_toast, style);
            style.normal.textColor = Color.white;
            GUI.Label(rect, m_toast, style);
        }

        public string DebugState()
        {
            string live = "no camera manager";
            if (m_cameraManager != null && m_vcam != null)
            {
                CinemachineBrain brain = m_cameraManager.GetBrain();
                bool isLive = brain != null && brain.ActiveVirtualCamera == (ICinemachineCamera)m_vcam;
                live = "mod camera priority " + m_vcam.Priority + (isLive ? " (live)" : " (game camera live)");
            }
            string distance = m_vcam != null && m_mode == ViewMode.ThirdPerson ? "  distance " + Vector3.Distance(m_vcam.transform.position, m_pivot).ToString("0.00") + (m_tooClose ? " (faded)" : "") : "";
            return "view: " + m_mode + "  yaw " + m_yaw.ToString("0") + " pitch " + m_pitch.ToString("0") + distance + "\n" + live;
        }

        public void ShowToast(string text)
        {
            m_toast = text;
            m_toastUntil = Time.unscaledTime + 2.5f;
        }

        private void SetMode(ViewMode mode)
        {
            m_mode = mode;
            m_snapNextFrame = true;
            Loader.Log("view -> " + mode);
            ShowToast(mode == ViewMode.Original ? "Camera: Original (fixed angles)" : mode == ViewMode.ThirdPerson ? "Camera: Third person" : "Camera: First person");
        }

        private static ViewMode ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "thirdperson":
                case "third":
                    return ViewMode.ThirdPerson;
                case "firstperson":
                case "first":
                    return ViewMode.FirstPerson;
                default:
                    return ViewMode.Original;
            }
        }

        private void FindSceneObjects()
        {
            if (m_cameraManager != null && m_player != null)
            {
                return;
            }
            if (Time.unscaledTime < m_nextSearch)
            {
                return;
            }
            m_nextSearch = Time.unscaledTime + 0.5f;
            if (m_cameraManager == null)
            {
                m_cameraManager = FindObjectOfType<GameplayCameraManager>();
            }
            if (m_player == null)
            {
                RestoreBody();
                m_player = FindObjectOfType<PlayerController>();
                m_head = null;
                m_snapNextFrame = true;
                if (m_player != null)
                {
                    Animator animator = m_player.GetComponentInChildren<Animator>();
                    if (animator != null && animator.isHuman)
                    {
                        m_head = animator.GetBoneTransform(HumanBodyBones.Head);
                    }
                    Loader.Log("player found: " + m_player.name + (m_head != null ? ", head bone " + m_head.name : ", no head bone"));
                }
            }
        }

        private void EnsureVirtualCamera()
        {
            if (m_vcam != null)
            {
                return;
            }
            var go = new GameObject("TSViewMod_VirtualCamera");
            DontDestroyOnLoad(go);
            m_vcam = go.AddComponent<CinemachineVirtualCamera>();
            m_vcam.Priority = 0;
            m_vcam.m_Lens.FieldOfView = m_config.FieldOfView;
            m_vcam.m_Lens.NearClipPlane = 0.05f;
            m_vcam.m_Lens.FarClipPlane = 500f;
        }

        private void ReadLookInput()
        {
            float lookX = Input.GetAxisRaw("Mouse X") * m_config.MouseSensitivity;
            float lookY = Input.GetAxisRaw("Mouse Y") * m_config.MouseSensitivity;
            Vector2 stick = RightStick();
            lookX += stick.x * m_config.StickSensitivity * Time.unscaledDeltaTime;
            lookY += stick.y * m_config.StickSensitivity * 0.75f * Time.unscaledDeltaTime;
            if (m_config.InvertY)
            {
                lookY = -lookY;
            }
            m_yaw += lookX;
            m_pitch = Mathf.Clamp(m_pitch - lookY, m_mode == ViewMode.FirstPerson ? -80f : -40f, 75f);
        }

        private void PlaceCamera(CinemachineBrain brain)
        {
            Transform playerTransform = m_player.transform;
            Vector3 headPosition = m_head != null ? m_head.position : playerTransform.position + Vector3.up * 1.55f;
            if (m_snapNextFrame)
            {
                m_yaw = playerTransform.eulerAngles.y;
                m_pitch = m_mode == ViewMode.FirstPerson ? 0f : 12f;
                m_pivot = playerTransform.position + Vector3.up * m_config.PivotHeight;
                m_snapNextFrame = false;
            }
            Quaternion rotation = Quaternion.Euler(m_pitch, m_yaw, 0f);
            Vector3 position;
            if (m_mode == ViewMode.FirstPerson)
            {
                Quaternion flat = Quaternion.Euler(0f, m_yaw, 0f);
                position = headPosition + Vector3.up * 0.04f + flat * Vector3.forward * 0.12f;
                m_vcam.m_Lens.FieldOfView = m_config.FirstPersonFieldOfView;
            }
            else
            {
                // Smooth the pivot: the player is a non-interpolated rigidbody, so its rendered position steps.
                Vector3 targetPivot = playerTransform.position + Vector3.up * m_config.PivotHeight;
                m_pivot = Vector3.Lerp(m_pivot, targetPivot, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                Vector3 offset = new Vector3(m_config.ShoulderOffset, 0.1f, -m_config.ThirdPersonDistance);
                Vector3 desired = m_pivot + rotation * offset;
                position = ResolveCollision(m_pivot, desired, playerTransform);
                float distance = Vector3.Distance(position, m_pivot);
                if (distance < CloseDistance)
                {
                    // A wall is pushing the camera into her: rise over the shoulder instead (up to a ceiling).
                    float k = Mathf.Clamp01((CloseDistance - distance) / 0.6f);
                    Vector3 highPivot = ResolveCollision(m_pivot, m_pivot + Vector3.up * 0.6f * k, playerTransform);
                    Vector3 high = ResolveCollision(highPivot, highPivot + rotation * offset, playerTransform);
                    if (Vector3.Distance(high, m_pivot) > distance)
                    {
                        position = high;
                        distance = Vector3.Distance(high, m_pivot);
                    }
                }
                m_tooClose = distance < FadeDistance;
                m_vcam.m_Lens.FieldOfView = m_config.FieldOfView;
            }
            if (brain != null && brain.OutputCamera != null)
            {
                m_vcam.m_Lens.FarClipPlane = Mathf.Max(brain.OutputCamera.farClipPlane, 100f);
            }
            m_vcam.transform.SetPositionAndRotation(position, rotation);
        }

        private static Vector3 ResolveCollision(Vector3 pivot, Vector3 desired, Transform player)
        {
            Vector3 dir = desired - pivot;
            float distance = dir.magnitude;
            if (distance < 0.001f)
            {
                return desired;
            }
            dir /= distance;
            const float radius = 0.18f;
            float best = distance;
            foreach (RaycastHit hit in Physics.SphereCastAll(pivot, radius, dir, distance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(player) || hit.collider.attachedRigidbody != null && !hit.collider.attachedRigidbody.isKinematic)
                {
                    continue;
                }
                if (hit.distance < best)
                {
                    best = hit.distance;
                }
            }
            return pivot + dir * Mathf.Max(best - 0.05f, 0.25f);
        }

        private void UpdateBodyVisibility(bool hide)
        {
            if (hide && m_hiddenRenderers.Count == 0 && m_player != null)
            {
                foreach (Renderer r in m_player.GetComponentsInChildren<Renderer>())
                {
                    if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                    {
                        continue;
                    }
                    m_hiddenRenderers.Add(new KeyValuePair<Renderer, ShadowCastingMode>(r, r.shadowCastingMode));
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }
            }
            else if (!hide && m_hiddenRenderers.Count > 0)
            {
                RestoreBody();
            }
        }

        private void RestoreBody()
        {
            foreach (KeyValuePair<Renderer, ShadowCastingMode> pair in m_hiddenRenderers)
            {
                if (pair.Key != null)
                {
                    pair.Key.shadowCastingMode = pair.Value;
                }
            }
            m_hiddenRenderers.Clear();
        }

        // Rewired gamepad right stick, read straight from the joystick so it works whatever the game's action map says.
        private static Vector2 RightStick()
        {
            Vector2 total = Vector2.zero;
            if (!ReInput.isReady)
            {
                return total;
            }
            foreach (Joystick joystick in ReInput.controllers.Joysticks)
            {
                int x = FindElement(joystick, "Right Stick X");
                int y = FindElement(joystick, "Right Stick Y");
                if (x >= 0 && y >= 0)
                {
                    Vector2 v = new Vector2(joystick.GetAxisById(x), joystick.GetAxisById(y));
                    if (v.magnitude > 0.15f)
                    {
                        total += v;
                    }
                }
            }
            return total;
        }

        public static bool RightStickClicked()
        {
            if (!ReInput.isReady)
            {
                return false;
            }
            foreach (Joystick joystick in ReInput.controllers.Joysticks)
            {
                int button = FindElement(joystick, "Right Stick Button");
                if (button >= 0 && joystick.GetButtonDownById(button))
                {
                    return true;
                }
            }
            return false;
        }

        private static int FindElement(Joystick joystick, string name)
        {
            foreach (ControllerElementIdentifier element in joystick.ElementIdentifiers)
            {
                if (element.name == name)
                {
                    return element.id;
                }
            }
            return -1;
        }
    }
}
