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
        public bool ControllerR3CyclesView = true;
        public string StartView = "Original";
        public float MouseSensitivity = 2.5f;
        public float StickSensitivity = 160f;
        public bool InvertY = false;
        public float ThirdPersonDistance = 2.1f;
        public float ShoulderOffset = 0.35f;
        public float PivotHeight = 1.45f;
        public float FieldOfView = 60f;
        public float FirstPersonFieldOfView = 70f;
        public bool HideBodyInFirstPerson = true;
        public bool MapMarker = true;
        public bool InspectReset = true;
        public KeyCode ResetRotationKey = KeyCode.R;
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
                config.ControllerR3CyclesView = GetBool(values, "ControllerR3CyclesView", config.ControllerR3CyclesView);
                config.StartView = values.TryGetValue("StartView", out string start) ? start : config.StartView;
                config.MouseSensitivity = GetFloat(values, "MouseSensitivity", config.MouseSensitivity);
                config.StickSensitivity = GetFloat(values, "StickSensitivity", config.StickSensitivity);
                config.InvertY = GetBool(values, "InvertY", config.InvertY);
                config.ThirdPersonDistance = GetFloat(values, "ThirdPersonDistance", config.ThirdPersonDistance);
                config.ShoulderOffset = GetFloat(values, "ShoulderOffset", config.ShoulderOffset);
                config.PivotHeight = GetFloat(values, "PivotHeight", config.PivotHeight);
                config.FieldOfView = GetFloat(values, "FieldOfView", config.FieldOfView);
                config.FirstPersonFieldOfView = GetFloat(values, "FirstPersonFieldOfView", config.FirstPersonFieldOfView);
                config.HideBodyInFirstPerson = GetBool(values, "HideBodyInFirstPerson", config.HideBodyInFirstPerson);
                config.MapMarker = GetBool(values, "MapMarker", config.MapMarker);
                config.InspectReset = GetBool(values, "InspectReset", config.InspectReset);
                config.ResetRotationKey = GetKey(values, "ResetRotationKey", config.ResetRotationKey);
                config.DebugOverlayKey = GetKey(values, "DebugOverlayKey", config.DebugOverlayKey);
                config.SnapshotKey = GetKey(values, "SnapshotKey", config.SnapshotKey);
                // Rewrite so settings added in newer versions appear in the file (values are kept).
                config.Save();
            }
            catch (Exception e)
            {
                Loader.Log("config error, using defaults: " + e.Message);
            }
            return config;
        }

        private void Save()
        {
            var inv = CultureInfo.InvariantCulture;
            File.WriteAllLines(FilePath, new[]
            {
                "# Tormented Souls view mod settings. Delete this file to reset to defaults.",
                "# Keys use Unity KeyCode names (F6, F7, V, Tab, ...).",
                "CycleViewKey=" + CycleViewKey,
                "MapMarkerKey=" + MapMarkerKey,
                "# Click the right stick to cycle views.",
                "ControllerR3CyclesView=" + ControllerR3CyclesView,
                "# Original, ThirdPerson or FirstPerson",
                "StartView=" + StartView,
                "MouseSensitivity=" + MouseSensitivity.ToString(inv),
                "StickSensitivity=" + StickSensitivity.ToString(inv),
                "InvertY=" + InvertY,
                "ThirdPersonDistance=" + ThirdPersonDistance.ToString(inv),
                "ShoulderOffset=" + ShoulderOffset.ToString(inv),
                "PivotHeight=" + PivotHeight.ToString(inv),
                "FieldOfView=" + FieldOfView.ToString(inv),
                "FirstPersonFieldOfView=" + FirstPersonFieldOfView.ToString(inv),
                "HideBodyInFirstPerson=" + HideBodyInFirstPerson,
                "# Red dot on your current room in the map screen.",
                "MapMarker=" + MapMarker,
                "# While inspecting an item: this key, middle mouse or right-stick click resets its rotation.",
                "InspectReset=" + InspectReset,
                "ResetRotationKey=" + ResetRotationKey,
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
