using UnityEngine;

namespace TSViewMod
{
    // Captures and hides the pointer while walking in mod views; otherwise restores the game's mode.
    public class CursorCapture
    {
        private bool m_cursorLocked;
        private CursorLockMode m_cursorMode;

        public void Update(bool want)
        {
            if (want && !m_cursorLocked)
            {
                m_cursorMode = Cursor.lockState;
                m_cursorLocked = true;
            }
            else if (!want && m_cursorLocked)
            {
                Cursor.lockState = m_cursorMode;
                m_cursorLocked = false;
                return;
            }
            if (m_cursorLocked && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        public void OnFocus(bool focused)
        {
            if (!focused)
            {
                Update(false);
            }
        }
    }
}
