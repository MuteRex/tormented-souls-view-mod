using UnityEngine;

namespace TSViewMod
{
    // Clears the camera to black where the set has no geometry; restores the camera's settings afterwards.
    public class VoidFill
    {
        private Camera m_voidCamera;
        private CameraClearFlags m_voidFlags;
        private Color m_voidColour;

        public void Apply(Camera camera)
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
    }
}
