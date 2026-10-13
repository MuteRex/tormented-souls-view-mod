using System.Collections.Generic;
using Rewired;
using UnityEngine;

namespace TSViewMod
{
    // Keyboard and mouse bindings for the Keyboard & Mouse options page: the game's from its Rewired maps, the
    // mod's from TSViewMod.cfg.
    public static class KeyBindings
    {
        public static List<KeyValuePair<string, string>> Page(int page)
        {
            var rows = new List<KeyValuePair<string, string>>();
            Player player = GetPlayer();
            Config cfg = ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
            switch (page)
            {
                case 0:
                    Add(rows, "Move", Move(player));
                    Add(rows, "Run", Keys(player, "Run"));
                    Add(rows, "Quick Turn", Keys(player, "Fast Turn"), "Back + Run");
                    Add(rows, "Aim", Keys(player, "Aim"));
                    Add(rows, "Shoot", Keys(player, "Shoot"));
                    Add(rows, "Reload", Keys(player, "Reload"));
                    Add(rows, "Look Around", "Mouse");
                    break;
                case 1:
                    Add(rows, "Interact", Keys(player, "Confirm"));
                    Add(rows, "Back", Keys(player, "Cancel"));
                    Add(rows, "Inventory", Keys(player, "Menu"));
                    Add(rows, "Map", Keys(player, "LanternSwitch"));
                    Add(rows, "Pause", Keys(player, "Options"));
                    Add(rows, "Switch Tab", Join(Keys(player, "SkipLeft"), Keys(player, "SkipRight")));
                    break;
                default:
                    if (cfg == null)
                    {
                        break;
                    }
                    Add(rows, "Switch Camera", KeyName(cfg.CycleViewKey));
                    Add(rows, "Look Around", "Mouse");
                    Add(rows, "Map Marker", KeyName(cfg.MapMarkerKey));
                    string click = cfg.ResetRotationMouseButton >= 0 && cfg.ResetRotationMouseButton <= 6 ? MouseName(cfg.ResetRotationMouseButton) : "";
                    Add(rows, "Reset View", Join(KeyName(cfg.ResetRotationKey), click));
                    Add(rows, "Debug Overlay", KeyName(cfg.DebugOverlayKey));
                    Add(rows, "Debug Snapshot", KeyName(cfg.SnapshotKey));
                    break;
            }
            return rows;
        }

        private static void Add(List<KeyValuePair<string, string>> rows, string label, string keys, string fallback = "-")
        {
            rows.Add(new KeyValuePair<string, string>(label, string.IsNullOrEmpty(keys) ? fallback : keys));
        }

        private static Player GetPlayer()
        {
            if (!ReInput.isReady)
            {
                return null;
            }
            UserInputManager input = Object.FindObjectOfType<UserInputManager>();
            return input != null && input.player != null ? input.player : ReInput.players.GetPlayer(0);
        }

        // Element names bound to an action on keyboard and mouse (optionally only one axis direction).
        private static List<string> Names(Player player, string action, Pole? pole = null)
        {
            var names = new List<string>();
            if (player == null)
            {
                return names;
            }
            foreach (ControllerType type in new[] { ControllerType.Keyboard, ControllerType.Mouse })
            {
                foreach (ControllerMap map in player.controllers.maps.GetAllMaps(type))
                {
                    if (!map.enabled)
                    {
                        continue;
                    }
                    foreach (ActionElementMap element in map.GetElementMapsWithAction(action))
                    {
                        if (pole.HasValue && element.axisContribution != pole.Value)
                        {
                            continue;
                        }
                        string name = Pretty(element.elementIdentifierName);
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }
            return names;
        }

        private static string Keys(Player player, string action)
        {
            return string.Join(" / ", Names(player, action).ToArray());
        }

        // Up/left/down/right sets, e.g. "WASD / Arrow Keys".
        private static string Move(Player player)
        {
            List<string> up = Names(player, "Vertical DPAD", Pole.Positive);
            List<string> left = Names(player, "Horizontal DPAD", Pole.Negative);
            List<string> down = Names(player, "Vertical DPAD", Pole.Negative);
            List<string> right = Names(player, "Horizontal DPAD", Pole.Positive);
            var sets = new List<string>();
            for (int i = 0; i < up.Count && i < left.Count && i < down.Count && i < right.Count; i++)
            {
                string[] keys = { up[i], left[i], down[i], right[i] };
                if (keys[0] == "Up" && keys[1] == "Left" && keys[2] == "Down" && keys[3] == "Right")
                {
                    sets.Add("Arrow Keys");
                }
                else
                {
                    bool letters = keys[0].Length == 1 && keys[1].Length == 1 && keys[2].Length == 1 && keys[3].Length == 1;
                    sets.Add(string.Join(letters ? "" : " ", keys));
                }
            }
            return string.Join(" / ", sets.ToArray());
        }

        private static string Join(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " / " + b;
        }

        private static string Pretty(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            switch (name)
            {
                case "Left Mouse Button": return "Left Click";
                case "Right Mouse Button": return "Right Click";
                case "Mouse Button 3": case "Middle Mouse Button": return "Middle Click";
                case "Up Arrow": return "Up";
                case "Down Arrow": return "Down";
                case "Left Arrow": return "Left";
                case "Right Arrow": return "Right";
                case "Escape": return "Esc";
                case "Return": return "Enter";
                case "Left Control": return "Left Ctrl";
                case "Right Control": return "Right Ctrl";
            }
            return name;
        }

        private static string KeyName(KeyCode key)
        {
            string name = key.ToString();
            if (name.StartsWith("Alpha"))
            {
                return name.Substring(5);
            }
            if (name.StartsWith("Mouse"))
            {
                return MouseName(name[name.Length - 1] - '0');
            }
            return name == "Escape" ? "Esc" : name == "Return" ? "Enter" : name;
        }

        private static string MouseName(int button)
        {
            switch (button)
            {
                case 0: return "Left Click";
                case 1: return "Right Click";
                case 2: return "Middle Click";
                default: return "Mouse " + (button + 1);
            }
        }
    }
}
