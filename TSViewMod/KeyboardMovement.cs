using System.Collections.Generic;
using System.Reflection;
using Rewired;
using UnityEngine;

namespace TSViewMod
{
    // The game sends keyboard movement keys to its "DPAD" actions, which drive tank controls (left/right turn her,
    // up/down walk forward/back). That suits the fixed cameras but not a camera behind her. While a mod view is
    // live, the same keys are also mapped to the analog move axes; when both are held the game uses the analog,
    // camera-relative path (UserInputManager.Update), like the left stick. The analog path picks walk/run from the
    // stick magnitude, so for keys the walk threshold is raised unless Run is held.
    public static class KeyboardMovement
    {
        private static readonly FieldInfo WalkRangeField = typeof(PlayerMovement).GetField("m_walkInputRange", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly List<KeyValuePair<ControllerMap, int>> s_added = new List<KeyValuePair<ControllerMap, int>>();
        private static Player s_player;
        private static PlayerMovement s_movement;
        private static float s_walkMax = -1f;

        public static void Update(bool active, PlayerMovement movement)
        {
            Player player = GetPlayer();
            if (player == null)
            {
                return;
            }
            if (active && s_added.Count == 0)
            {
                AddMaps(player);
            }
            else if (!active && s_added.Count > 0)
            {
                RemoveMaps();
            }

            // Walk unless Run is held, while the keys are what's moving her.
            bool keys = active && player.controllers.GetLastActiveController()?.type == ControllerType.Keyboard;
            SetWalkMax(movement, keys && !player.GetButton("Run") ? 1.5f : -1f);
        }

        private static Player GetPlayer()
        {
            if (s_player == null && ReInput.isReady)
            {
                UserInputManager input = Object.FindObjectOfType<UserInputManager>();
                s_player = input != null ? input.player : null;
            }
            return s_player;
        }

        private static void AddMaps(Player player)
        {
            InputAction moveX = ReInput.mapping.GetAction("Horizontal Axis");
            InputAction moveY = ReInput.mapping.GetAction("Vertical Axis");
            InputAction padX = ReInput.mapping.GetAction("Horizontal DPAD");
            InputAction padY = ReInput.mapping.GetAction("Vertical DPAD");
            if (moveX == null || moveY == null || padX == null || padY == null)
            {
                return;
            }
            foreach (ControllerMap map in player.controllers.maps.GetAllMaps(ControllerType.Keyboard))
            {
                if (!map.enabled)
                {
                    continue;
                }
                foreach (ActionElementMap source in map.AllMaps.ToArrayCopy())
                {
                    int target = source.actionId == padX.id ? moveX.id : source.actionId == padY.id ? moveY.id : -1;
                    if (target >= 0 && map.CreateElementMap(target, source.axisContribution, source.elementIdentifierId, source.elementType, source.axisRange, source.invert, out ActionElementMap added))
                    {
                        s_added.Add(new KeyValuePair<ControllerMap, int>(map, added.id));
                    }
                }
            }
            Loader.Log("keyboard movement: camera-relative (" + s_added.Count + " keys mapped)");
        }

        private static void RemoveMaps()
        {
            foreach (KeyValuePair<ControllerMap, int> entry in s_added)
            {
                entry.Key?.DeleteElementMap(entry.Value);
            }
            s_added.Clear();
            Loader.Log("keyboard movement: back to the game's controls");
        }

        // max < 0 restores the game's own value.
        private static void SetWalkMax(PlayerMovement movement, float max)
        {
            if (s_movement != movement)
            {
                s_movement = movement;
                s_walkMax = -1f;
            }
            if (movement == null || WalkRangeField == null || !(WalkRangeField.GetValue(movement) is RangeFloat range))
            {
                return;
            }
            if (s_walkMax < 0f)
            {
                s_walkMax = range.max;
            }
            range.max = max < 0f ? s_walkMax : max;
        }

        private static ActionElementMap[] ToArrayCopy(this IList<ActionElementMap> list)
        {
            var copy = new ActionElementMap[list.Count];
            list.CopyTo(copy, 0);
            return copy;
        }
    }
}
