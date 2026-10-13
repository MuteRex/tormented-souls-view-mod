using System.Collections.Generic;
using System.Reflection;
using Cinemachine;
using Rewired;
using UnityEngine.AI;
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
        // Camera distance from the pivot after smoothing: obstacles pull it in at once, it eases back out.
        private float m_smoothDistance = -1f;
        private float m_holdUntil;
        private const float ReturnDelay = 0.35f;
        private const float ReturnSpeed = 1.5f; // metres per second
        // Shoulder side: +1 right, -1 left. Swaps when a wall squeezes the current side and the other has room.
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

        // When the arm is shorter than FadeDistance she is hidden (shadow only) rather than filling the screen.
        private const float FadeDistance = 0.35f;

        private readonly List<KeyValuePair<Renderer, ShadowCastingMode>> m_hiddenRenderers = new List<KeyValuePair<Renderer, ShadowCastingMode>>();

        private string m_toast;
        private float m_toastUntil;
        private float m_nextSearch;

        public static ViewCamera Instance { get; private set; }

        // First person is parked on the first-person branch (own version line) until it's good enough.
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
            // Keyboard shortcut only; on controller the camera is changed in Options > View Mod
            // (right-stick click is reserved for Reset View while inspecting).
            if (Input.GetKeyDown(m_config.CycleViewKey))
            {
                SetMode(Next(m_mode, 1));
            }
        }

        // Cycles Original -> Third -> First, skipping first person while it isn't available.
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
            bool canTakeOver = m_mode != ViewMode.Original && m_player != null && mainCamera != null && mainCamera.Priority > 0;
            m_vcam.Priority = canTakeOver ? mainCamera.Priority + 1 : 0;
            bool hideBody = canTakeOver && (m_mode == ViewMode.FirstPerson && m_config.HideBodyInFirstPerson || m_mode == ViewMode.ThirdPerson && m_tooClose);
            UpdateBodyVisibility(hideBody);
            CinemachineBrain brain = m_cameraManager != null ? m_cameraManager.GetBrain() : null;
            bool live = canTakeOver && brain != null && brain.ActiveVirtualCamera == (ICinemachineCamera)m_vcam;
            UpdateVoidColour(live && m_mode == ViewMode.ThirdPerson ? brain.OutputCamera : null);
            if (!canTakeOver)
            {
                m_snapNextFrame = true;
                return;
            }

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
            float lookX = Input.GetAxisRaw("Mouse X") * m_config.MouseSensitivity;
            float lookY = Input.GetAxisRaw("Mouse Y") * m_config.MouseSensitivity;
            Vector2 stick = RightStick();
            lookX += stick.x * m_config.StickSensitivity * Time.unscaledDeltaTime;
            lookY += stick.y * m_config.StickSensitivity * 0.75f * Time.unscaledDeltaTime;
            if (m_config.InvertY)
            {
                lookY = -lookY;
            }
            if (m_config.MaxTurnSpeed > 0f)
            {
                float maxStep = m_config.MaxTurnSpeed * Time.unscaledDeltaTime;
                lookX = Mathf.Clamp(lookX, -maxStep, maxStep);
            }
            if (Mathf.Abs(lookX) + Mathf.Abs(lookY) > 0.05f)
            {
                m_lastLookInput = Time.unscaledTime;
            }
            if (m_mode == ViewMode.ThirdPerson && m_config.OrbitLimit > 0f && m_config.OrbitLimit < 180f && m_player != null)
            {
                // RE2-style: the stick can't swing the camera more than OrbitLimit round from her back, but her
                // own turning never drags the camera (she can run toward it, like in RE2).
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
                : Mathf.Clamp(m_pitch - lookY, -15f, 45f); // RE-like tilt range; many rooms have no ceiling
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
                m_smoothDistance = -1f; // new room / view: start at the right distance, no easing
                m_lastPlayerPos = playerTransform.position;
                m_playerSpeed = 0f;
                m_snapNextFrame = false;
            }
            // How fast she's moving (smoothed), for the auto follow below.
            float frameDt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Vector3 moved = playerTransform.position - m_lastPlayerPos;
            moved.y = 0f;
            m_playerSpeed = Mathf.Lerp(m_playerSpeed, Mathf.Min(moved.magnitude / frameDt, 10f), 1f - Mathf.Exp(-8f * frameDt));
            m_lastPlayerPos = playerTransform.position;
            if (m_mode == ViewMode.ThirdPerson && m_config.AutoFollow && Time.unscaledTime - m_lastLookInput > 0.6f && m_playerSpeed > 0.4f)
            {
                // Lazy follow: when she runs a curve or a circle the camera swings round behind her instead of
                // staying put while she circles it. Not when she walks back toward the camera.
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
                // Smooth the pivot: the player is a non-interpolated rigidbody, so its rendered position steps.
                Vector3 targetPivot = playerTransform.position + Vector3.up * m_config.PivotHeight;
                m_pivot = Vector3.Lerp(m_pivot, targetPivot, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                UpdateShoulderSide(rotation, playerTransform);
                m_side = Mathf.SmoothDamp(m_side, m_sideTarget, ref m_sideVelocity, 0.35f, Mathf.Infinity, Time.unscaledDeltaTime);

                // Spring arm, the way RE-style shoulder cameras work: the camera keeps a fixed offset in its own
                // space (no height changes), and only its distance along the arm changes. The arm starts at the
                // shoulder, so a blocked camera slides in toward the shoulder rather than toward the head.
                Vector3 shoulder = ResolveCollision(m_pivot, m_pivot + rotation * new Vector3(m_config.ShoulderOffset * m_side, 0.1f, 0f), playerTransform);
                Vector3 back = rotation * Vector3.back;
                float full = m_config.ThirdPersonDistance;
                float target = Vector3.Distance(shoulder, Constrain(shoulder, shoulder + back * full, playerTransform));

                // Pull in fast but not in one frame (stops single-frame pops from props and edges), never letting the
                // camera sit behind the obstacle for more than a moment; ease back out only after a short hold.
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

        // Auto shoulder: swap only when the current side has been clearly squeezed for a while and the other side
        // really has room, with a cooldown, so walking past furniture doesn't flip the camera back and forth.
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
            float here = Vector3.Distance(m_pivot, Constrain(m_pivot, m_pivot + rotation * ShoulderOffset(m_sideTarget), player));
            float there = Vector3.Distance(m_pivot, Constrain(m_pivot, m_pivot + rotation * ShoulderOffset(-m_sideTarget), player));
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

        // Colliders first, then the room's walkable area: some walls in this fixed-camera game have no collider,
        // but the NavMesh always stops at them, so the camera may overhang its edge by at most NavOverhang.
        private const float NavOverhang = 0.3f;

        private static Vector3 Constrain(Vector3 pivot, Vector3 desired, Transform player)
        {
            Vector3 position = ResolveCollision(pivot, desired, player);
            // Walk out along the arm and stop where it leaves the room's walkable area (+ overhang): walls in this
            // fixed-camera game often have no collider and no back side, and behind them is the void.
            Vector3 arm = position - pivot;
            float length = arm.magnitude;
            if (length < 0.01f || !MapMarker.InsideRoom(pivot, NavOverhang))
            {
                return position;
            }
            const float step = 0.05f;
            float allowed = 0f;
            for (float d = step; d <= length + 1e-4f; d += step)
            {
                if (!MapMarker.InsideRoom(pivot + arm * (d / length), NavOverhang))
                {
                    break;
                }
                allowed = d;
            }
            allowed = Mathf.Min(allowed, length);
            return pivot + arm * (Mathf.Max(allowed, 0.25f) / length);
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
                if (hit.collider.transform.IsChildOf(player) || hit.collider.attachedRigidbody != null && !hit.collider.attachedRigidbody.isKinematic
                    || IgnoredByCamera(hit.collider) || WaistHigh(hit.collider, player.position.y))
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

        // Small props (lamps, chair and bed legs, poles, trolleys) and enemies don't push the camera; only walls,
        // big furniture, floors and ceilings do. Cached per collider.
        private static readonly Dictionary<Collider, bool> s_ignored = new Dictionary<Collider, bool>();

        private static bool IgnoredByCamera(Collider collider)
        {
            if (s_ignored.TryGetValue(collider, out bool ignored))
            {
                return ignored;
            }
            if (s_ignored.Count > 4096)
            {
                s_ignored.Clear(); // old rooms' colliders
            }
            Vector3 size = collider.bounds.size;
            bool thin = size.x < 0.5f && size.z < 0.5f; // poles, legs, lamp stands
            bool small = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < 1.2f; // boxes, chairs, carts
            ignored = thin || small || collider.GetComponentInParent<EnemyBrain>() != null;
            s_ignored[collider] = ignored;
            return ignored;
        }

        // Wherever the camera still sees past the built set, show black (the game's darkness) instead of the
        // camera's default background. Restored as soon as the game's own cameras are back.
        private Camera m_voidCamera;
        private CameraClearFlags m_voidFlags;
        private Color m_voidColour;

        private void UpdateVoidColour(Camera camera)
        {
            if (m_voidCamera != null && m_voidCamera != camera)
            {
                m_voidCamera.clearFlags = m_voidFlags;
                m_voidCamera.backgroundColor = m_voidColour;
                m_voidCamera = null;
            }
            if (camera != null && m_voidCamera == null)
            {
                m_voidCamera = camera;
                m_voidFlags = camera.clearFlags;
                m_voidColour = camera.backgroundColor;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
            }
        }

        // Railings, banisters, tables, counters and benches: the camera looks over/through them (as in RE2) instead
        // of being pushed into her head. Floors (top near her feet) and ceilings (above her head) still count.
        private static bool WaistHigh(Collider collider, float feet)
        {
            Bounds b = collider.bounds;
            return b.size.y < 1.4f && b.max.y > feet + 0.3f && b.max.y < feet + 1.6f && b.min.y > feet - 0.6f;
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
