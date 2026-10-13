using System.Collections.Generic;
using System.Reflection;
using Cinemachine;
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

    // Mod vcam one priority above the game's main camera; yields when the game drops that to 0 (puzzles,
    // cutscenes). Runs before CinemachineBrain's LateUpdate.
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
        // Smoothed arm length: pulls in fast, eases back out.
        private float m_smoothDistance = -1f;
        private float m_holdUntil;
        private const float ReturnDelay = 0.35f;
        private const float ReturnSpeed = 1.5f; // metres per second
        // Shoulder side: +1 right, -1 left.
        private float m_side = 1f;
        private float m_sideTarget = 1f;
        private float m_sideCheckAt;
        private float m_lastLookInput = -10f;
        private Vector3 m_lastPlayerPos;
        private float m_playerSpeed;
        private float m_sideVelocity;
        private float m_distanceVelocity;
        private float m_blockedSince = -1f;
        private float m_lastSwap = -10f;

        // Below FadeDistance she is hidden (shadow only).
        private const float FadeDistance = 0.35f;

        private readonly List<KeyValuePair<Renderer, ShadowCastingMode>> m_hiddenRenderers = new List<KeyValuePair<Renderer, ShadowCastingMode>>();

        private string m_toast;
        private float m_toastUntil;
        private float m_nextSearch;

        private readonly CursorCapture m_cursor = new CursorCapture();
        private readonly ScriptedAttackWatch m_scripted = new ScriptedAttackWatch();
        private readonly VoidFill m_voidFill = new VoidFill();

        public static ViewCamera Instance { get; private set; }

        // First person is developed on the first-person branch.
        public const bool FirstPersonAvailable = false;
        public const string FirstPersonVersion = "0.1.0";

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
            // Keyboard only; on controller the view is set in Options (right-stick click is Reset View).
            if (Input.GetKeyDown(m_config.CycleViewKey))
            {
                SetMode(Next(m_mode, 1));
            }
        }

        public static ViewMode Next(ViewMode mode, int direction)
        {
            ViewMode next = (ViewMode)(((int)mode + direction + 3) % 3);
            if (next == ViewMode.FirstPerson && !FirstPersonAvailable)
            {
                next = (ViewMode)(((int)next + direction + 3) % 3);
            }
            return next;
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
            bool canTakeOver = m_mode != ViewMode.Original && m_player != null && mainCamera != null && mainCamera.Priority > 0 && !m_scripted.Active(m_player, m_config.GameCameraOnGrab);
            m_vcam.Priority = canTakeOver ? mainCamera.Priority + 1 : 0;
            bool hideBody = canTakeOver && (m_mode == ViewMode.FirstPerson && m_config.HideBodyInFirstPerson || m_mode == ViewMode.ThirdPerson && m_tooClose);
            UpdateBodyVisibility(hideBody);
            CinemachineBrain brain = m_cameraManager != null ? m_cameraManager.GetBrain() : null;
            bool live = canTakeOver && brain != null && brain.ActiveVirtualCamera == (ICinemachineCamera)m_vcam;
            m_voidFill.Apply(live && m_mode == ViewMode.ThirdPerson ? brain.OutputCamera : null);
            if (!canTakeOver)
            {
                m_cursor.Update(false);
                KeyboardMovement.Update(false, m_player != null ? m_player.GetPlayerMovement() : null);
                m_snapNextFrame = true;
                return;
            }

            // GamePaused covers menus, inspect and pickups.
            PlayerMovement movement = m_player.GetPlayerMovement();
            bool freePlay = movement == null || !movement.GamePaused;
            bool looking = live && freePlay && Time.timeScale > 0f;
            m_cursor.Update(looking && m_config.LockCursor && Application.isFocused);
            KeyboardMovement.Update(looking && m_config.KeyboardCameraRelative, movement);
            if (looking)
            {
                ReadLookInput();
            }
            PlaceCamera(brain);
        }

        private void OnApplicationFocus(bool focused)
        {
            m_cursor.OnFocus(focused);
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

        public ViewMode Mode => m_mode;

        public void SetMode(ViewMode mode)
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
                    return FirstPersonAvailable ? ViewMode.FirstPerson : ViewMode.ThirdPerson;
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
            // Mouse is uncapped; the stick is capped at MaxTurnSpeed.
            float mouseX = Input.GetAxisRaw("Mouse X") * m_config.MouseSensitivity;
            float mouseY = Input.GetAxisRaw("Mouse Y") * m_config.MouseSensitivity * (m_config.MouseInvertY ? -1f : 1f);
            Vector2 stick = PadInput.RightStick();
            float stickX = stick.x * m_config.StickSensitivity * Time.unscaledDeltaTime;
            float stickY = stick.y * m_config.StickSensitivity * 0.75f * Time.unscaledDeltaTime * (m_config.InvertY ? -1f : 1f);
            if (m_config.MaxTurnSpeed > 0f)
            {
                float maxStep = m_config.MaxTurnSpeed * Time.unscaledDeltaTime;
                stickX = Mathf.Clamp(stickX, -maxStep, maxStep);
            }
            float lookX = mouseX + stickX;
            float lookY = mouseY + stickY;
            if (Mathf.Abs(lookX) + Mathf.Abs(lookY) > 0.05f)
            {
                m_lastLookInput = Time.unscaledTime;
            }
            if (m_mode == ViewMode.ThirdPerson && m_config.OrbitLimit > 0f && m_config.OrbitLimit < 180f && m_player != null)
            {
                // Limit how far the stick orbits from her back, but never drag the camera when she turns.
                float rel = Mathf.DeltaAngle(m_player.transform.eulerAngles.y, m_yaw);
                float limit = m_config.OrbitLimit;
                if (Mathf.Abs(rel) >= limit)
                {
                    if (Mathf.Sign(lookX) == Mathf.Sign(rel))
                    {
                        lookX = 0f;
                    }
                }
                else if (Mathf.Abs(rel + lookX) > limit)
                {
                    lookX = Mathf.Sign(lookX) * limit - rel;
                }
            }
            m_yaw += lookX;
            m_pitch = m_mode == ViewMode.FirstPerson
                ? Mathf.Clamp(m_pitch - lookY, -80f, 75f)
                : Mathf.Clamp(m_pitch - lookY, -15f, 45f); // many rooms have no ceiling
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
                m_smoothDistance = -1f;
                m_lastPlayerPos = playerTransform.position;
                m_playerSpeed = 0f;
                m_snapNextFrame = false;
            }
            float frameDt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Vector3 moved = playerTransform.position - m_lastPlayerPos;
            moved.y = 0f;
            m_playerSpeed = Mathf.Lerp(m_playerSpeed, Mathf.Min(moved.magnitude / frameDt, 10f), 1f - Mathf.Exp(-8f * frameDt));
            m_lastPlayerPos = playerTransform.position;
            if (m_mode == ViewMode.ThirdPerson && m_config.AutoFollow && Time.unscaledTime - m_lastLookInput > 0.6f && m_playerSpeed > 0.4f)
            {
                // Auto follow: swing behind her on curves, not when she walks toward the camera.
                float facing = playerTransform.eulerAngles.y;
                float rel = Mathf.DeltaAngle(m_yaw, facing);
                if (Mathf.Abs(rel) < 150f)
                {
                    float rate = m_config.AutoFollowSpeed * Mathf.Clamp01(m_playerSpeed / 2f) * Mathf.Clamp(Mathf.Abs(rel) / 60f, 0.25f, 1.5f);
                    m_yaw = Mathf.MoveTowardsAngle(m_yaw, facing, rate * frameDt);
                }
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
                // The player rigidbody isn't interpolated, so smooth the pivot.
                Vector3 targetPivot = playerTransform.position + Vector3.up * m_config.PivotHeight;
                m_pivot = Vector3.Lerp(m_pivot, targetPivot, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                UpdateShoulderSide(rotation, playerTransform);
                m_side = Mathf.SmoothDamp(m_side, m_sideTarget, ref m_sideVelocity, 0.35f, Mathf.Infinity, Time.unscaledDeltaTime);

                // Spring arm from the shoulder: only the arm length changes, never the height.
                Vector3 shoulder = CameraCollision.ResolveCollision(m_pivot, m_pivot + rotation * new Vector3(m_config.ShoulderOffset * m_side, 0.1f, 0f), playerTransform);
                Vector3 back = rotation * Vector3.back;
                float full = m_config.ThirdPersonDistance;
                float target = Vector3.Distance(shoulder, CameraCollision.Constrain(shoulder, shoulder + back * full, playerTransform));

                // Pull in quickly (not in one frame), hold, then ease back out.
                float now = Time.unscaledTime;
                float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
                if (m_smoothDistance < 0f)
                {
                    m_smoothDistance = target;
                    m_distanceVelocity = 0f;
                }
                else if (target < m_smoothDistance)
                {
                    if (target < m_smoothDistance - 0.03f)
                    {
                        m_holdUntil = now + ReturnDelay;
                    }
                    m_smoothDistance = Mathf.Max(target, Mathf.SmoothDamp(m_smoothDistance, target, ref m_distanceVelocity, 0.05f, Mathf.Infinity, dt));
                    m_smoothDistance = Mathf.Min(m_smoothDistance, target + 0.25f); // never more than 25 cm "inside"
                }
                else if (now >= m_holdUntil)
                {
                    m_smoothDistance = Mathf.SmoothDamp(m_smoothDistance, target, ref m_distanceVelocity, 0.45f, ReturnSpeed, dt);
                }
                else
                {
                    m_distanceVelocity = 0f;
                }
                position = shoulder + back * m_smoothDistance;
                m_tooClose = m_smoothDistance < FadeDistance;
                m_vcam.m_Lens.FieldOfView = m_config.FieldOfView;
            }
            if (brain != null && brain.OutputCamera != null)
            {
                m_vcam.m_Lens.FarClipPlane = Mathf.Max(brain.OutputCamera.farClipPlane, 100f);
            }
            m_vcam.transform.SetPositionAndRotation(position, rotation);
        }

        // Swap only after a sustained squeeze with room on the other side, with a cooldown.
        private void UpdateShoulderSide(Quaternion rotation, Transform player)
        {
            if (string.Equals(m_config.ShoulderSide, "Right", System.StringComparison.OrdinalIgnoreCase))
            {
                m_sideTarget = 1f;
                return;
            }
            if (string.Equals(m_config.ShoulderSide, "Left", System.StringComparison.OrdinalIgnoreCase))
            {
                m_sideTarget = -1f;
                return;
            }
            if (Time.unscaledTime < m_sideCheckAt)
            {
                return;
            }
            m_sideCheckAt = Time.unscaledTime + 0.1f;
            float here = Vector3.Distance(m_pivot, CameraCollision.Constrain(m_pivot, m_pivot + rotation * ShoulderOffset(m_sideTarget), player));
            float there = Vector3.Distance(m_pivot, CameraCollision.Constrain(m_pivot, m_pivot + rotation * ShoulderOffset(-m_sideTarget), player));
            bool squeezed = there > here + 0.5f && here < m_config.ThirdPersonDistance * 0.7f;
            if (!squeezed)
            {
                m_blockedSince = -1f;
                return;
            }
            if (m_blockedSince < 0f)
            {
                m_blockedSince = Time.unscaledTime;
            }
            if (Time.unscaledTime - m_blockedSince > 0.4f && Time.unscaledTime - m_lastSwap > 1.5f)
            {
                m_sideTarget = -m_sideTarget;
                m_lastSwap = Time.unscaledTime;
                m_blockedSince = -1f;
            }
        }

        private Vector3 ShoulderOffset(float side)
        {
            return new Vector3(m_config.ShoulderOffset * side, 0.1f, -m_config.ThirdPersonDistance);
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

    }
}
