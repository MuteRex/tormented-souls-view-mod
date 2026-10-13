using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TSViewMod
{
    // RE2-style "reset view" while inspecting an item: R / middle mouse / right-stick click smoothly turns
    // the inspected object back to the rotation it opened with. Maps instead turn to face the viewer flat,
    // so the whole sheet is readable. A "Reset View" prompt is added to the game's button bar.
    // Gui3DObject keeps the opening rotation in m_initialRot and rotates m_toRotateObject in its LateUpdate,
    // so this runs after it.
    [DefaultExecutionOrder(1000)]
    public class InspectReset : MonoBehaviour
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo RotateObjectField = typeof(Gui3DObject).GetField("m_toRotateObject", Private);
        private static readonly FieldInfo InitialRotField = typeof(Gui3DObject).GetField("m_initialRot", Private);
        private static readonly FieldInfo CurrentGoField = typeof(Gui3DObject).GetField("m_currentGo", Private);
        private static readonly FieldInfo ClickableField = typeof(Gui3DObject).GetField("m_clickableObject", Private);
        private static readonly FieldInfo CameraField = typeof(Gui3DObject).GetField("m_camera", Private);

        private const string PromptName = "TSViewMod_ResetPrompt";

        private Transform m_resetting;
        private Quaternion m_target;
        private float m_nextSearch;
        private Gui3DObject m_viewer;
        private bool m_loggedBar;

        private void Start()
        {
            if (RotateObjectField == null || InitialRotField == null)
            {
                Loader.Log("inspect reset disabled: Gui3DObject fields not found");
                enabled = false;
            }
        }

        private void Update()
        {
            Config config = ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
            if (config == null || !config.InspectReset)
            {
                return;
            }
            Gui3DObject viewer = ActiveViewer();
            UpdatePrompt(viewer != null, config);
            if (viewer == null)
            {
                return;
            }
            bool pressed = Input.GetKeyDown(config.ResetRotationKey) || Input.GetMouseButtonDown(2) || ViewCamera.RightStickClicked();
            if (!pressed)
            {
                return;
            }
            m_resetting = RotateObjectField.GetValue(viewer) as Transform;
            if (m_resetting == null)
            {
                return;
            }
            Quaternion initial = (Quaternion)InitialRotField.GetValue(viewer);
            MapHelperRoom map = m_resetting.GetComponentInChildren<MapHelperRoom>();
            Camera camera = map != null ? MapMarker.MapCamera(map.gameObject, map.transform.position) : null;
            if (camera == null)
            {
                camera = CameraField?.GetValue(viewer) as Camera;
            }
            m_target = map != null && camera != null ? FaceViewer(m_resetting, initial, map, camera.transform) : initial;
        }

        private void LateUpdate()
        {
            if (m_resetting == null)
            {
                return;
            }
            m_resetting.rotation = Quaternion.Slerp(m_resetting.rotation, m_target, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            if (Quaternion.Angle(m_resetting.rotation, m_target) < 0.5f)
            {
                m_resetting.rotation = m_target;
                m_resetting = null;
            }
        }

        // Rotation for the pivot that lays the map sheet flat toward the camera, printed side up, upright as it opened.
        private static Quaternion FaceViewer(Transform pivot, Quaternion initial, MapHelperRoom map, Transform camera)
        {
            Transform sheet = map.transform;
            int normal = MapMarker.NormalAxis(map);
            Vector3 faceLocal = MapMarker.FrontNormalLocal(normal, MapMarker.FrontSign(map, normal));
            Quaternion current = pivot.rotation;

            // "Up" is the in-plane axis that pointed most upward on screen when the item opened.
            pivot.rotation = initial;
            int upIndex = -1;
            float upSign = 1f;
            float bestDot = -1f;
            for (int i = 0; i < 3; i++)
            {
                if (i == normal)
                {
                    continue;
                }
                float d = Vector3.Dot(Axis(sheet, i), camera.up);
                if (Mathf.Abs(d) > bestDot)
                {
                    bestDot = Mathf.Abs(d);
                    upIndex = i;
                    upSign = Mathf.Sign(d);
                }
            }
            Vector3 upLocal = Vector3.zero;
            upLocal[upIndex] = upSign;
            pivot.rotation = current;

            // Delta that takes the sheet's printed face/up onto camera-facing/camera-up, applied to the pivot.
            Vector3 faceNow = sheet.TransformDirection(faceLocal);
            Vector3 upNow = sheet.TransformDirection(upLocal);
            Quaternion from = Quaternion.LookRotation(faceNow, upNow);
            Quaternion to = Quaternion.LookRotation((camera.position - sheet.position).normalized, camera.up);
            return to * Quaternion.Inverse(from) * current;
        }

        private static Vector3 Axis(Transform t, int i)
        {
            return i == 0 ? t.right : i == 1 ? t.up : t.forward;
        }

        // The inspection viewer that currently shows a loaded, rotatable item, if any.
        public Gui3DObject ActiveViewer()
        {
            if (m_viewer == null && Time.unscaledTime >= m_nextSearch)
            {
                m_nextSearch = Time.unscaledTime + 0.5f;
                m_viewer = FindObjectOfType<Gui3DObject>();
            }
            if (m_viewer == null || !m_viewer.isActiveAndEnabled)
            {
                return null;
            }
            bool hasItem = CurrentGoField == null || CurrentGoField.GetValue(m_viewer) as GameObject != null;
            bool rotatable = ClickableField == null || (bool)ClickableField.GetValue(m_viewer);
            return hasItem && rotatable ? m_viewer : null;
        }

        // ---------- "Reset View" entry on the game's button bar ----------
        // Each bar entry is: Background (icon Image), optional KeyboardText (key letter on a keycap), Label Text.
        // We copy the bar's "Back" entry and drive it like the game drives its own: on keyboard a keycap with the
        // key letter (SwapKeyboardControls.KeyboardBG), on a pad the game's right-stick icon taken from the
        // "Rotate" entry, which the game already swaps between Xbox/PlayStation/Switch art.

        private class Prompt
        {
            public GameObject root;
            public Image icon;
            public TMP_Text key;
            public TMP_Text label;
            public Sprite keyboardBackground;
            public Image rotateIcon;
            public SwapSpriteControls rotateSwap;
        }

        private readonly Dictionary<GameObject, Prompt> m_prompts = new Dictionary<GameObject, Prompt>();

        private void UpdatePrompt(bool show, Config config)
        {
            HelpersSwitcher bar = HelpersSwitcher.instance;
            if (bar == null || bar.states == null)
            {
                return;
            }
            foreach (GameObject state in bar.states)
            {
                if (state == null)
                {
                    continue;
                }
                m_prompts.TryGetValue(state, out Prompt prompt);
                bool want = show && state.activeInHierarchy;
                if (want && prompt == null)
                {
                    prompt = CreatePrompt(state);
                    m_prompts[state] = prompt;
                }
                if (prompt == null || prompt.root == null)
                {
                    continue;
                }
                if (prompt.root.activeSelf != want)
                {
                    prompt.root.SetActive(want);
                }
                if (want)
                {
                    Refresh(prompt, config);
                }
            }
        }

        private static void Refresh(Prompt prompt, Config config)
        {
            bool pad = prompt.rotateIcon != null && prompt.rotateSwap != null && prompt.rotateIcon.sprite != null
                && prompt.rotateIcon.sprite != prompt.rotateSwap.Mouse;
            if (pad)
            {
                SetSprite(prompt.icon, prompt.rotateIcon.sprite);
                SetText(prompt.key, "");
            }
            else
            {
                SetSprite(prompt.icon, prompt.keyboardBackground);
                SetText(prompt.key, KeyLabel(config.ResetRotationKey));
            }
            SetText(prompt.label, "Reset View");
        }

        private static string KeyLabel(KeyCode key)
        {
            string name = key.ToString();
            if (name.StartsWith("Alpha"))
            {
                return name.Substring(5);
            }
            return name.ToUpperInvariant();
        }

        private static void SetSprite(Image image, Sprite sprite)
        {
            if (image != null && sprite != null && image.sprite != sprite)
            {
                image.sprite = sprite;
            }
        }

        private static void SetText(TMP_Text text, string value)
        {
            if (text != null && text.text != value)
            {
                text.text = value;
            }
        }

        private Prompt CreatePrompt(GameObject state)
        {
            if (!m_loggedBar)
            {
                m_loggedBar = true;
                var sb = new StringBuilder("button bar: ");
                Dump(state.transform, 0, sb);
                Loader.Log(sb.ToString());
            }
            SwapKeyboardControls template = null;
            SwapSpriteControls rotate = null;
            foreach (SwapKeyboardControls entry in state.GetComponentsInChildren<SwapKeyboardControls>(false))
            {
                if (entry.name != PromptName)
                {
                    template = entry;
                }
            }
            foreach (SwapSpriteControls swap in state.GetComponentsInChildren<SwapSpriteControls>(false))
            {
                if (swap.GetAction() == HintableMarks.Actions.Rotate)
                {
                    rotate = swap;
                }
            }
            if (template == null || template.image == null || template.text == null)
            {
                Loader.Log("button bar: no keyboard-style entry to copy in " + state.name);
                return null;
            }
            GameObject copy = Instantiate(template.gameObject, template.transform.parent);
            copy.name = PromptName;
            copy.transform.SetAsLastSibling();
            var prompt = new Prompt
            {
                root = copy,
                keyboardBackground = template.KeyboardBG,
                rotateSwap = rotate,
                rotateIcon = rotate != null ? rotate.GetComponent<Image>() : null
            };
            // Find our copies of the template's icon and key text by matching child paths.
            prompt.icon = FindCopy(template.transform, copy.transform, template.image.transform)?.GetComponent<Image>();
            prompt.key = FindCopy(template.transform, copy.transform, template.text.transform)?.GetComponent<TMP_Text>();
            foreach (TMP_Text text in copy.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text != prompt.key)
                {
                    prompt.label = text;
                }
            }
            // Drop the game's own scripts (localisation, icon swapping) so they don't overwrite ours.
            foreach (MonoBehaviour behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(behaviour is Graphic) && !(behaviour is LayoutGroup) && !(behaviour is LayoutElement) && !(behaviour is ContentSizeFitter))
                {
                    DestroyImmediate(behaviour);
                }
            }
            Loader.Log("button bar: added Reset View prompt in " + state.name + (rotate != null ? " (pad icon from Rotate)" : " (keyboard icon only)"));
            return prompt;
        }

        private static Transform FindCopy(Transform originalRoot, Transform copyRoot, Transform original)
        {
            var path = new List<int>();
            for (Transform t = original; t != null && t != originalRoot; t = t.parent)
            {
                path.Add(t.GetSiblingIndex());
            }
            Transform result = copyRoot;
            for (int i = path.Count - 1; i >= 0 && result != null; i--)
            {
                result = path[i] < result.childCount ? result.GetChild(path[i]) : null;
            }
            return result;
        }

        private static void Dump(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 4)
            {
                return;
            }
            sb.Append('\n').Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            foreach (Component c in t.GetComponents<Component>())
            {
                if (!(c is Transform))
                {
                    sb.Append(" [").Append(c.GetType().Name).Append(']');
                }
            }
            TMP_Text tmp = t.GetComponent<TMP_Text>();
            if (tmp != null)
            {
                sb.Append(" \"").Append(tmp.text).Append('"');
            }
            foreach (Transform child in t)
            {
                Dump(child, depth + 1, sb);
            }
        }
    }
}
