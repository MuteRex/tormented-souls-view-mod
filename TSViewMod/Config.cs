using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TSViewMod
{
    // Plain key=value file at TormentedSouls_Data/TSViewMod.cfg. Written with defaults on first run.
    public class Config
    {
        public KeyCode CycleViewKey = KeyCode.F6;
        public KeyCode MapMarkerKey = KeyCode.F7;
        public string StartView = "Original";
        public float MouseSensitivity = 2.5f;
        public float StickSensitivity = 160f;
        public bool InvertY = false;
        public bool MouseInvertY = false;
        public bool LockCursor = true;
        public bool KeyboardCameraRelative = true;
        public float OrbitLimit = 120f;
        public float MaxTurnSpeed = 300f;
        public bool AutoFollow = true;
        public float AutoFollowSpeed = 120f;
        public float ThirdPersonDistance = 1.4f;
        public float ShoulderOffset = 0.45f;
        public string ShoulderSide = "Auto";
        public float PivotHeight = 1.45f;
        public float FieldOfView = 60f;
        public float FirstPersonFieldOfView = 70f;
        public bool HideBodyInFirstPerson = true;
        public bool MapMarker = true;
        public bool MapArrow = true;
        public bool GameCameraOnGrab = true;
        public bool InspectReset = true;
        public KeyCode ResetRotationKey = KeyCode.R;
        public int ResetRotationMouseButton = 2;
        public KeyCode DebugOverlayKey = KeyCode.F8;
        public KeyCode SnapshotKey = KeyCode.F9;

        public static string FilePath => Loader.DataFile("TSViewMod.cfg");

        // File layout: each setting once, in file order, with the comment lines written above it.
        private static readonly (string name, string comment)[] Layout =
        {
            (nameof(CycleViewKey), "# Tormented Souls view mod settings. Delete this file to reset to defaults.\n# Keys use Unity KeyCode names (F6, F7, V, Tab, ...)."),
            (nameof(MapMarkerKey), null),
            (nameof(StartView), "# Original, ThirdPerson or FirstPerson"),
            (nameof(MouseSensitivity), "# Camera speed for mouse and for the right stick (set separately), and Y inversion for each."),
            (nameof(StickSensitivity), null),
            (nameof(InvertY), null),
            (nameof(MouseInvertY), null),
            (nameof(LockCursor), "# Third/first person: capture and hide the mouse pointer while you walk around (released in menus)."),
            (nameof(KeyboardCameraRelative), "# Third/first person: movement keys move her relative to the camera (like the stick) instead of the\n# game's tank controls; she walks, and runs while Run is held."),
            (nameof(OrbitLimit), "# Third person: how far (degrees) the camera may swing round from behind her (0 = no limit),\n# and the fastest the right stick may turn it (degrees per second; the mouse is never capped)."),
            (nameof(MaxTurnSpeed), null),
            (nameof(AutoFollow), "# Third person: while she moves and you aren't steering the camera, it swings round behind her."),
            (nameof(AutoFollowSpeed), null),
            (nameof(ThirdPersonDistance), null),
            (nameof(ShoulderOffset), null),
            (nameof(ShoulderSide), "# Auto (swaps away from walls), Right or Left"),
            (nameof(PivotHeight), null),
            (nameof(FieldOfView), null),
            (nameof(FirstPersonFieldOfView), null),
            (nameof(HideBodyInFirstPerson), null),
            (nameof(MapMarker), "# Red dot on your current room in the map screen."),
            (nameof(MapArrow), "# Pointer on the red dot showing which way she faces."),
            (nameof(GameCameraOnGrab), "# Hand over to the game's own camera while an enemy grabs her and when she dies (those animations\n# were made for the fixed cameras)."),
            (nameof(InspectReset), "# While inspecting an item: this key, this mouse button (0 left, 1 right, 2 middle, 3-6 extra, -1 none)\n# or right-stick click resets its rotation."),
            (nameof(ResetRotationKey), null),
            (nameof(ResetRotationMouseButton), null),
            (nameof(DebugOverlayKey), "# Debugging: on-screen readout (+ door markers on the map), and screenshot + state dump to TSViewMod_debug/."),
            (nameof(SnapshotKey), null),
        };

        public static Config Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    var fresh = new Config();
                    fresh.Save();
                    return fresh;
                }
                Config config = Parse(File.ReadAllLines(FilePath));
                // Rewrite so newly added settings appear in the file.
                config.Save();
                return config;
            }
            catch (Exception e)
            {
                Loader.Log("config error, using defaults: " + e.Message);
                return new Config();
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, Serialize());
            }
            catch (Exception e)
            {
                Loader.Log("config not saved: " + e.Message);
            }
        }

        // Unknown keys and unparsable values are ignored (the default stays).
        public static Config Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length > 0 && !line.StartsWith("#") && eq > 0)
                {
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            var config = new Config();
            foreach ((string name, string _) in Layout)
            {
                if (values.TryGetValue(name, out string text))
                {
                    FieldInfo field = typeof(Config).GetField(name);
                    if (TryConvert(text, field.FieldType, out object value))
                    {
                        field.SetValue(config, value);
                    }
                }
            }
            if (!values.ContainsKey(nameof(MouseInvertY)))
            {
                config.MouseInvertY = config.InvertY; // older files: one shared setting
            }
            return config;
        }

        public string[] Serialize()
        {
            var lines = new List<string>();
            foreach ((string name, string comment) in Layout)
            {
                if (comment != null)
                {
                    lines.AddRange(comment.Split('\n'));
                }
                object value = typeof(Config).GetField(name).GetValue(this);
                lines.Add(name + "=" + (value is float f ? f.ToString(CultureInfo.InvariantCulture) : value.ToString()));
            }
            return lines.ToArray();
        }

        private static bool TryConvert(string text, Type type, out object value)
        {
            value = null;
            if (type == typeof(string))
            {
                value = text;
            }
            else if (type == typeof(bool) && bool.TryParse(text, out bool b))
            {
                value = b;
            }
            else if ((type == typeof(float) || type == typeof(int)) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                value = type == typeof(int) ? (object)(int)f : f;
            }
            else if (type == typeof(KeyCode))
            {
                try
                {
                    value = Enum.Parse(typeof(KeyCode), text, true);
                }
                catch (ArgumentException)
                {
                }
            }
            return value != null;
        }
    }
}
