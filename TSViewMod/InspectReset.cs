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
        private readonly Dictionary<GameObject, GameObject> m_prompts = new Dictionary<GameObject, GameObject>();
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
            Camera camera = CameraField?.GetValue(viewer) as Camera;
            m_target = map != null && camera != null ? FaceViewer(m_resetting, initial, map.transform, camera.transform) : initial;
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

        // Rotation for the pivot that lays the map sheet flat toward the camera, upright as it opened.
        private static Quaternion FaceViewer(Transform pivot, Quaternion initial, Transform sheet, Transform camera)
        {
            int normal = MapMarker.NormalAxis(sheet.GetComponent<MapHelperRoom>());
            Quaternion current = pivot.rotation;

            // Work out which side of the sheet faces the viewer and which in-plane axis is "up", using the opening pose.
            pivot.rotation = initial;
            Vector3 toCamera = (camera.position - sheet.position).normalized;
            Vector3 face = Axis(sheet, normal);
            if (Vector3.Dot(face, toCamera) < 0f)
            {
                face = -face;
            }
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
            Vector3 faceLocal = sheet.InverseTransformDirection(face);
            Vector3 upLocal = sheet.InverseTransformDirection(Axis(sheet, upIndex) * upSign);
            pivot.rotation = current;

            // Delta that takes the sheet's current face/up onto camera-facing/camera-up, applied to the pivot.
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
                m_prompts.TryGetValue(state, out GameObject prompt);
                bool want = show && state.activeInHierarchy;
                if (want && prompt == null)
                {
                    prompt = CreatePrompt(state);
                    m_prompts[state] = prompt;
                }
                if (prompt != null)
                {
                    if (prompt.activeSelf != want)
                    {
                        prompt.SetActive(want);
                    }
                    if (want)
                    {
                        SetLabel(prompt, Label(config));
                    }
                }
            }
        }

        private static string Label(Config config)
        {
            bool pad = false;
            try
            {
                pad = ReInput.isReady && ReInput.controllers.GetLastActiveControllerType() == ControllerType.Joystick;
            }
            catch (System.Exception)
            {
            }
            return (pad ? "[R3]" : "[" + config.ResetRotationKey + "]") + " Reset View";
        }

        private GameObject CreatePrompt(GameObject state)
        {
            if (!m_loggedBar)
            {
                m_loggedBar = true;
                var sb = new StringBuilder("button bar: ");
                Dump(state.transform, 0, sb);
                Loader.Log(sb.ToString());
            }
            // Clone the last visible entry that has a label, so the new prompt inherits the bar's font and layout.
            // The bar is the outermost layout group holding two or more labelled entries (Rotate, Interact, Back...).
            Transform template = null;
            foreach (LayoutGroup group in state.GetComponentsInChildren<LayoutGroup>(false))
            {
                Transform last = null;
                int entries = 0;
                foreach (Transform child in group.transform)
                {
                    if (child.gameObject.activeInHierarchy && child.name != PromptName && HasLabel(child))
                    {
                        last = child;
                        entries++;
                    }
                }
                if (entries >= 2)
                {
                    template = last;
                    break;
                }
            }
            if (template == null)
            {
                Loader.Log("button bar: no prompt entry to copy in " + state.name);
                return null;
            }
            GameObject prompt = Instantiate(template.gameObject, template.parent);
            prompt.name = PromptName;
            prompt.transform.SetAsLastSibling();
            // Drop the game's own scripts (localisation, input icons) so they don't overwrite our label.
            foreach (MonoBehaviour behaviour in prompt.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(behaviour is Graphic) && !(behaviour is LayoutGroup) && !(behaviour is LayoutElement) && !(behaviour is ContentSizeFitter))
                {
                    DestroyImmediate(behaviour);
                }
            }
            // The copied icon would show the wrong button, so hide icons and put the key in the label.
            foreach (Image image in prompt.GetComponentsInChildren<Image>(true))
            {
                image.enabled = false;
            }
            Loader.Log("button bar: added Reset View prompt next to " + template.name + " in " + state.name);
            return prompt;
        }

        private static void SetLabel(GameObject prompt, string text)
        {
            TMP_Text tmp = prompt.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                if (tmp.text != text)
                {
                    tmp.text = text;
                }
                return;
            }
            Text legacy = prompt.GetComponentInChildren<Text>(true);
            if (legacy != null && legacy.text != text)
            {
                legacy.text = text;
            }
        }

        private static bool HasLabel(Transform t)
        {
            return t.GetComponentInChildren<TMP_Text>(true) != null || t.GetComponentInChildren<Text>(true) != null;
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
