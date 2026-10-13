using System;
using System.IO;
using UnityEngine;

namespace TSViewMod
{
    // Entry point: the installer registers Init in RuntimeInitializeOnLoads.json.
    public static class Loader
    {
        private static bool s_initialized;

        public static void Init()
        {
            if (s_initialized)
            {
                return;
            }
            s_initialized = true;
            Log("loaded v" + typeof(Loader).Assembly.GetName().Version);
            var go = new GameObject("TSViewMod");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ViewCamera>();
            go.AddComponent<RoomBounds>();
            go.AddComponent<MapMarker>();
            go.AddComponent<InspectReset>();
            go.AddComponent<DebugTools>();
            go.AddComponent<OptionsPage>();
            if (Survey.TakeFlag())
            {
                go.AddComponent<Survey>();
            }
        }

        // Mod files live next to the game data; if that folder is read-only (e.g. under Program Files on Windows)
        // they go to the game's persistent data folder instead, seeded once from the installed copies.
        private static string s_dataDir;

        public static string DataDir
        {
            get
            {
                if (s_dataDir == null)
                {
                    s_dataDir = Writable(Application.dataPath) ? Application.dataPath : Application.persistentDataPath;
                    if (s_dataDir != Application.dataPath)
                    {
                        Log("game folder is read-only, mod files go to " + s_dataDir);
                    }
                }
                return s_dataDir;
            }
        }

        public static string DataFile(string name)
        {
            string path = Path.Combine(DataDir, name);
            string installed = Path.Combine(Application.dataPath, name);
            try
            {
                if (path != installed && !File.Exists(path) && File.Exists(installed))
                {
                    File.Copy(installed, path);
                }
            }
            catch (Exception e)
            {
                Log("couldn't copy " + name + ": " + e.Message);
            }
            return path;
        }

        private static bool Writable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "TSViewMod_write.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Log(string message)
        {
            Debug.Log("[TSViewMod] " + message);
        }
    }
}
