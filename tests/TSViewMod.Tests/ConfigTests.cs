using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TSViewMod;
using UnityEngine;
using Xunit;

public class ConfigTests
{
    // The file layout written before the table-driven rewrite; default.cfg is its output for a default Config.
    private static string[] LegacySerialize(Config c)
    {
        var inv = CultureInfo.InvariantCulture;
        return new[]
            {
                "# Tormented Souls view mod settings. Delete this file to reset to defaults.",
                "# Keys use Unity KeyCode names (F6, F7, V, Tab, ...).",
                "CycleViewKey=" + c.CycleViewKey,
                "MapMarkerKey=" + c.MapMarkerKey,
                "# Original or ThirdPerson",
                "StartView=" + c.StartView,
                "# Camera speed for mouse and for the right stick (set separately), and Y inversion for each.",
                "MouseSensitivity=" + c.MouseSensitivity.ToString(inv),
                "StickSensitivity=" + c.StickSensitivity.ToString(inv),
                "InvertY=" + c.InvertY,
                "MouseInvertY=" + c.MouseInvertY,
                "# Third person: capture and hide the mouse pointer while you walk around (released in menus).",
                "LockCursor=" + c.LockCursor,
                "# Third person: movement keys move her relative to the camera (like the stick) instead of the",
                "# game's tank controls; she walks, and runs while Run is held.",
                "KeyboardCameraRelative=" + c.KeyboardCameraRelative,
                "# Third person: how far (degrees) the camera may swing round from behind her (0 = no limit),",
                "# and the fastest the right stick may turn it (degrees per second; the mouse is never capped).",
                "OrbitLimit=" + c.OrbitLimit.ToString(inv),
                "MaxTurnSpeed=" + c.MaxTurnSpeed.ToString(inv),
                "# Third person: while she moves and you aren't steering the camera, it swings round behind her.",
                "AutoFollow=" + c.AutoFollow,
                "AutoFollowSpeed=" + c.AutoFollowSpeed.ToString(inv),
                "ThirdPersonDistance=" + c.ThirdPersonDistance.ToString(inv),
                "ShoulderOffset=" + c.ShoulderOffset.ToString(inv),
                "# Auto (swaps away from walls), Right or Left",
                "ShoulderSide=" + c.ShoulderSide,
                "PivotHeight=" + c.PivotHeight.ToString(inv),
                "FieldOfView=" + c.FieldOfView.ToString(inv),
                "# Red dot on your current room in the map screen.",
                "MapMarker=" + c.MapMarker,
                "# Pointer on the red dot showing which way she faces.",
                "MapArrow=" + c.MapArrow,
                "# Hand over to the game's own camera while an enemy grabs her and when she dies (those animations",
                "# were made for the fixed cameras).",
                "GameCameraOnGrab=" + c.GameCameraOnGrab,
                "# While inspecting an item: this key, this mouse button (0 left, 1 right, 2 middle, 3-6 extra, -1 none)",
                "# or right-stick click resets its rotation.",
                "InspectReset=" + c.InspectReset,
                "ResetRotationKey=" + c.ResetRotationKey,
                "ResetRotationMouseButton=" + c.ResetRotationMouseButton,
                "# Debugging: on-screen readout (+ door markers on the map), and screenshot + state dump to TSViewMod_debug/.",
                "DebugOverlayKey=" + c.DebugOverlayKey,
                "SnapshotKey=" + c.SnapshotKey,
            };
    }

    private static Config Modified()
    {
        var c = new Config();
        c.CycleViewKey = KeyCode.F10;
        c.StartView = "ThirdPerson";
        c.MouseSensitivity = 3.25f;
        c.InvertY = true;
        c.ShoulderSide = "Left";
        c.ThirdPersonDistance = 1f;
        c.ResetRotationMouseButton = -1;
        c.MapArrow = false;
        return c;
    }

    [Fact]
    public void Defaults_match_the_legacy_layout()
    {
        Assert.Equal(LegacySerialize(new Config()), new Config().Serialize());
    }

    [Fact]
    public void Modified_values_match_the_legacy_layout()
    {
        Assert.Equal(LegacySerialize(Modified()), Modified().Serialize());
    }

    [Fact]
    public void Defaults_match_golden_file()
    {
        Assert.Equal(File.ReadAllLines("default.cfg"), new Config().Serialize());
    }

    [Fact]
    public void Round_trip_keeps_every_value()
    {
        Config c = Modified();
        Assert.Equal(c.Serialize(), Config.Parse(c.Serialize()).Serialize());
    }

    [Fact]
    public void Old_files_share_invert_y_and_tolerate_junk()
    {
        Config c = Config.Parse(new[] { "invertY = True\r", "MouseSensitivity=abc", "Unknown=1", "# FieldOfView=10", "CycleViewKey=f5", "ResetRotationMouseButton=1.7" });
        Assert.True(c.InvertY);
        Assert.True(c.MouseInvertY);
        Assert.Equal(new Config().MouseSensitivity, c.MouseSensitivity);
        Assert.Equal(new Config().FieldOfView, c.FieldOfView);
        Assert.Equal(KeyCode.F5, c.CycleViewKey);
        Assert.Equal(1, c.ResetRotationMouseButton);
    }

    [Fact]
    public void Numbers_use_invariant_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Config c = Config.Parse(new[] { "MouseSensitivity=1.5" });
            Assert.Equal(1.5f, c.MouseSensitivity);
            Assert.Contains("MouseSensitivity=1.5", c.Serialize());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
