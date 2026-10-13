using Rewired;
using UnityEngine;

namespace TSViewMod
{
    // Gamepad right stick read straight from Rewired joysticks, independent of the game's action maps.
    public static class PadInput
    {
        // Rewired gamepad right stick, read straight from the joystick so it works whatever the game's action map says.
        public static Vector2 RightStick()
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
