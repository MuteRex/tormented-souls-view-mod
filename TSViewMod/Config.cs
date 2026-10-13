using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        public static string FilePath => Path.Combine(Application.dataPath, "TSViewMod.cfg");

        public static Config Load()
        {
            var config = new Config();
            try
            {
                if (!File.Exists(FilePath))
                {
                    config.Save();
                    return config;
                }
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
                config.CycleViewKey = GetKey(values, "CycleViewKey", config.CycleViewKey);
                config.MapMarkerKey = GetKey(values, "MapMarkerKey", config.MapMarkerKey);
                config.StartView = values.TryGetValue("StartView", out string start) ? start : config.StartView;
                config.MouseSensitivity = GetFloat(values, "MouseSensitivity", config.MouseSensitivity);
                config.StickSensitivity = GetFloat(values, "StickSensitivity", config.StickSensitivity);
                config.InvertY = GetBool(values, "InvertY", config.InvertY);
                config.MouseInvertY = GetBool(values, "MouseInvertY", config.InvertY); // older files: one shared setting
                config.LockCursor = GetBool(values, "LockCursor", config.LockCursor);
                config.KeyboardCameraRelative = GetBool(values, "KeyboardCameraRelative", config.KeyboardCameraRelative);
                config.OrbitLimit = GetFloat(values, "OrbitLimit", config.OrbitLimit);
                config.MaxTurnSpeed = GetFloat(values, "MaxTurnSpeed", config.MaxTurnSpeed);
                config.AutoFollow = GetBool(values, "AutoFollow", config.AutoFollow);
                config.AutoFollowSpeed = GetFloat(values, "AutoFollowSpeed", config.AutoFollowSpeed);
                config.ThirdPersonDistance = GetFloat(values, "ThirdPersonDistance", config.ThirdPersonDistance);
                config.ShoulderOffset = GetFloat(values, "ShoulderOffset", config.ShoulderOffset);
                config.ShoulderSide = values.TryGetValue("ShoulderSide", out string side) ? side : config.ShoulderSide;
                config.PivotHeight = GetFloat(values, "PivotHeight", config.PivotHeight);
                config.FieldOfView = GetFloat(values, "FieldOfView", config.FieldOfView);
                config.FirstPersonFieldOfView = GetFloat(values, "FirstPersonFieldOfView", config.FirstPersonFieldOfView);
                config.HideBodyInFirstPerson = GetBool(values, "HideBodyInFirstPerson", config.HideBodyInFirstPerson);
                config.MapMarker = GetBool(values, "MapMarker", config.MapMarker);
                config.MapArrow = GetBool(values, "MapArrow", config.MapArrow);
                config.GameCameraOnGrab = GetBool(values, "GameCameraOnGrab", config.GameCameraOnGrab);
                config.InspectReset = GetBool(values, "InspectReset", config.InspectReset);
                config.ResetRotationKey = GetKey(values, "ResetRotationKey", config.ResetRotationKey);
                config.ResetRotationMouseButton = (int)GetFloat(values, "ResetRotationMouseButton", config.ResetRotationMouseButton);
                config.DebugOverlayKey = GetKey(values, "DebugOverlayKey", config.DebugOverlayKey);
                config.SnapshotKey = GetKey(values, "SnapshotKey", config.SnapshotKey);
                // Rewrite so newly added settings appear in the file.
                config.Save();
            }
            catch (Exception e)
            {
                Loader.Log("config error, using defaults: " + e.Message);
            }
            return config;
        }

        public void Save()
        {
            var inv = CultureInfo.InvariantCulture;
            File.WriteAllLines(FilePath, new[]
            {
                "# Tormented Souls view mod settings. Delete this file to reset to defaults.",
                "# Keys use Unity KeyCode names (F6, F7, V, Tab, ...).",
                "CycleViewKey=" + CycleViewKey,
                "MapMarkerKey=" + MapMarkerKey,
                "# Original, ThirdPerson or FirstPerson",
                "StartView=" + StartView,
                "# Camera speed for mouse and for the right stick (set separately), and Y inversion for each.",
                "MouseSensitivity=" + MouseSensitivity.ToString(inv),
                "StickSensitivity=" + StickSensitivity.ToString(inv),
                "InvertY=" + InvertY,
                "MouseInvertY=" + MouseInvertY,
                "# Third/first person: capture and hide the mouse pointer while you walk around (released in menus).",
                "LockCursor=" + LockCursor,
                "# Third/first person: movement keys move her relative to the camera (like the stick) instead of the",
                "# game's tank controls; she walks, and runs while Run is held.",
                "KeyboardCameraRelative=" + KeyboardCameraRelative,
                "# Third person: how far (degrees) the camera may swing round from behind her (0 = no limit),",
                "# and the fastest the right stick may turn it (degrees per second; the mouse is never capped).",
                "OrbitLimit=" + OrbitLimit.ToString(inv),
                "MaxTurnSpeed=" + MaxTurnSpeed.ToString(inv),
                "# Third person: while she moves and you aren't steering the camera, it swings round behind her.",
                "AutoFollow=" + AutoFollow,
                "AutoFollowSpeed=" + AutoFollowSpeed.ToString(inv),
                "ThirdPersonDistance=" + ThirdPersonDistance.ToString(inv),
                "ShoulderOffset=" + ShoulderOffset.ToString(inv),
                "# Auto (swaps away from walls), Right or Left",
                "ShoulderSide=" + ShoulderSide,
                "PivotHeight=" + PivotHeight.ToString(inv),
                "FieldOfView=" + FieldOfView.ToString(inv),
                "FirstPersonFieldOfView=" + FirstPersonFieldOfView.ToString(inv),
                "HideBodyInFirstPerson=" + HideBodyInFirstPerson,
                "# Red dot on your current room in the map screen.",
                "MapMarker=" + MapMarker,
                "# Pointer on the red dot showing which way she faces.",
                "MapArrow=" + MapArrow,
                "# Hand over to the game's own camera while an enemy grabs her and when she dies (those animations",
                "# were made for the fixed cameras).",
                "GameCameraOnGrab=" + GameCameraOnGrab,
                "# While inspecting an item: this key, this mouse button (0 left, 1 right, 2 middle, 3-6 extra, -1 none)",
                "# or right-stick click resets its rotation.",
                "InspectReset=" + InspectReset,
                "ResetRotationKey=" + ResetRotationKey,
                "ResetRotationMouseButton=" + ResetRotationMouseButton,
                "# Debugging: on-screen readout (+ door markers on the map), and screenshot + state dump to TSViewMod_debug/.",
                "DebugOverlayKey=" + DebugOverlayKey,
                "SnapshotKey=" + SnapshotKey,
            });
        }

        private static KeyCode GetKey(Dictionary<string, string> values, string key, KeyCode fallback)
        {
            if (values.TryGetValue(key, out string s))
            {
                try
                {
                    return (KeyCode)Enum.Parse(typeof(KeyCode), s, true);
                }
                catch (ArgumentException)
                {
                }
            }
            return fallback;
        }

        private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
        {
            return values.TryGetValue(key, out string s) && bool.TryParse(s, out bool b) ? b : fallback;
        }

        private static float GetFloat(Dictionary<string, string> values, string key, float fallback)
        {
            return values.TryGetValue(key, out string s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }
    }
}
