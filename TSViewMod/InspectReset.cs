using System.Reflection;
using UnityEngine;

namespace TSViewMod
{
    // RE2-style "reset view" while inspecting an item: R / middle mouse / right-stick click
    // smoothly turns the inspected object back to the rotation it opened with.
    // Gui3DObject keeps that rotation in m_initialRot and rotates m_toRotateObject in its LateUpdate,
    // so this runs after it.
    [DefaultExecutionOrder(1000)]
    public class InspectReset : MonoBehaviour
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo RotateObjectField = typeof(Gui3DObject).GetField("m_toRotateObject", Private);
        private static readonly FieldInfo InitialRotField = typeof(Gui3DObject).GetField("m_initialRot", Private);
        private static readonly FieldInfo CurrentGoField = typeof(Gui3DObject).GetField("m_currentGo", Private);
        private static readonly FieldInfo ClickableField = typeof(Gui3DObject).GetField("m_clickableObject", Private);

        private Transform m_resetting;
        private Quaternion m_target;
        private float m_nextSearch;
        private Gui3DObject m_viewer;

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
            bool pressed = Input.GetKeyDown(config.ResetRotationKey) || Input.GetMouseButtonDown(2) || ViewCamera.RightStickClicked();
            if (!pressed)
            {
                return;
            }
            Gui3DObject viewer = ActiveViewer();
            if (viewer == null)
            {
                return;
            }
            m_resetting = RotateObjectField.GetValue(viewer) as Transform;
            m_target = (Quaternion)InitialRotField.GetValue(viewer);
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
    }
}
