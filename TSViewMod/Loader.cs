using UnityEngine;

namespace TSViewMod
{
    // Entry point: install.py registers Init in RuntimeInitializeOnLoads.json.
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
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ViewCamera>();
            go.AddComponent<MapMarker>();
            go.AddComponent<InspectReset>();
            go.AddComponent<DebugTools>();
            go.AddComponent<OptionsPage>();
            if (Survey.TakeFlag())
            {
                go.AddComponent<Survey>();
            }
        }

        public static void Log(string message)
        {
            Debug.Log("[TSViewMod] " + message);
        }
    }
}
