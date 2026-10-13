using UnityEngine;

namespace TSViewMod
{
    // Entry point. Unity calls Init because installer/install.py lists it in
    // RuntimeInitializeOnLoads.json (loadTypes 0 = after the first scene loads).
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
