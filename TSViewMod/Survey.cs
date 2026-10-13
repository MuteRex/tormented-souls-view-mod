using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // Unattended map survey (installer/survey.py): continues the save, teleports through every map room so
    // MapMarker can fit and cache it, writes TSViewMod_survey_report.txt and quits.
    public class Survey : MonoBehaviour
    {
        public static bool Active { get; private set; }

        private static string DataFile(string name) => Path.Combine(Application.dataPath, name);
        private static string FlagPath => DataFile("TSViewMod_survey.flag");
        private static string ReportPath => DataFile("TSViewMod_survey_report.txt");

        private const string MapKey = "Items/Map_Zoom.prefab";
        private const float LoadTimeout = 60f;
        private const float FitTimeout = 240f;

        // Deleted on read so a crash doesn't leave the next launch in survey mode.
        public static bool TakeFlag()
        {
            try
            {
                if (File.Exists(FlagPath))
                {
                    File.Delete(FlagPath);
                    Active = true;
                }
            }
            catch (Exception e)
            {
                Loader.Log("survey flag error: " + e.Message);
            }
            return Active;
        }

        private readonly List<string> m_report = new List<string>();
        private int m_weak;

        private void LowPower()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 10;
            AudioListener.volume = 0f;
            if (Screen.width > 400)
            {
                Screen.SetResolution(320, 180, FullScreenMode.Windowed);
            }
        }

        private IEnumerator Start()
        {
            Loader.Log("survey: started");
            LowPower();
            yield return Run();
            WriteReport();
            Loader.Log("survey: finished, quitting");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        private IEnumerator Run()
        {
            InitMenuManager menu = null;
            yield return WaitFor(() => (menu = FindObjectOfType<InitMenuManager>()) != null, 180f);
            if (menu == null)
            {
                Note("FAILED: main menu never appeared");
                yield break;
            }
            yield return new WaitForSecondsRealtime(5f);
            Loader.Log("survey: continuing save");
            menu.ContinueSaved();
            yield return WaitFor(InGameplay, 180f);
            if (!InGameplay())
            {
                Note("FAILED: save didn't load into gameplay");
                yield break;
            }
            yield return new WaitForSecondsRealtime(3f);
            LowPower(); // the game applies its own settings on load
            GodMode();

            // Map item off screen, with its scripts off (they expect the inspect screen).
            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(MapKey);
            yield return handle;
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Note("FAILED: couldn't load " + MapKey);
                yield break;
            }
            var holder = new GameObject("TSViewMod_SurveyMap");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);
            holder.transform.position = new Vector3(0f, -5000f, 0f);
            GameObject map = Instantiate(handle.Result, holder.transform);
            foreach (MonoBehaviour behaviour in map.GetComponentsInChildren<MonoBehaviour>(true))
            {
                behaviour.enabled = false;
            }
            holder.SetActive(true);
            MapHelperRoom helper = map.GetComponentInChildren<MapHelperRoom>(true);
            if (helper == null)
            {
                Note("FAILED: map prefab has no MapHelperRoom");
                yield break;
            }

            // Rooms on the map without a cached fit.
            var scenes = new List<string>();
            var cached = new HashSet<string>();
            foreach (MapHelperStringToGameObject entry in helper.sceneToGameObjects)
            {
                if (entry == null || entry.highlight == null || string.IsNullOrEmpty(entry.name))
                {
                    continue;
                }
                if (!scenes.Contains(entry.name))
                {
                    scenes.Add(entry.name);
                    cached.Add(entry.name);
                }
                if (!MapMarker.IsCached(entry.name, entry.tabID, entry.highlight.name))
                {
                    cached.Remove(entry.name);
                }
            }
            Note("rooms on the map: " + scenes.Count + ", already fitted: " + cached.Count);

            int index = 0;
            foreach (string scene in scenes)
            {
                index++;
                if (cached.Contains(scene))
                {
                    continue;
                }
                GameplayEntranceManager entrance = FindObjectOfType<GameplayEntranceManager>();
                if (entrance == null || !InGameplay())
                {
                    Note("STOPPED before " + scene + ": not in gameplay any more (" + SceneManager.GetActiveScene().name + ")");
                    yield break;
                }
                Loader.Log("survey: " + index + "/" + scenes.Count + " " + scene);
                float started = Time.realtimeSinceStartup;
                if (SceneManager.GetActiveScene().name != scene)
                {
                    entrance.TeleportToSceneTesting(scene);
                    yield return WaitFor(() => SceneManager.GetActiveScene().name == scene && InGameplay(), LoadTimeout);
                    if (SceneManager.GetActiveScene().name != scene)
                    {
                        Note(scene + ": didn't load (timeout), skipped  << WEAK");
                        m_weak++;
                        continue;
                    }
                    GodMode();
                    yield return new WaitForSecondsRealtime(1.5f); // navmesh + entrance points settle
                }
                List<string> lines = null;
                bool weak = false;
                yield return WaitFor(() => MapMarker.TryGetSceneResult(scene, out lines, out weak), FitTimeout);
                if (lines == null || lines.Count == 0)
                {
                    Note(scene + ": no fit result (timeout)  << WEAK");
                    m_weak++;
                    continue;
                }
                foreach (string line in lines)
                {
                    Note(line);
                }
                if (weak)
                {
                    m_weak++;
                }
                Loader.Log("survey: " + scene + " took " + (Time.realtimeSinceStartup - started).ToString("0.0") + " s");
            }
            Note("all rooms visited");
        }

        // The game's debug god mode; stored in the save, which survey.py restores.
        private static void GodMode()
        {
            UserDataManager data = FindObjectOfType<UserDataManager>();
            if (data != null && !data.GetGodModeStatus())
            {
                data.SetGodModeStatus(true);
                Loader.Log("survey: god mode on");
            }
        }

        private static bool InGameplay()
        {
            return FindObjectOfType<PlayerController>() != null && FindObjectOfType<GameplayEntranceManager>() != null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < end)
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }

        private void Note(string line)
        {
            m_report.Add(line);
            Loader.Log("survey: " + line);
        }

        private void WriteReport()
        {
            var lines = new List<string>
            {
                "TSViewMod map survey " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                "weak or missing rooms: " + m_weak + "  (WEAK = dot may be off there; it falls back to the room centre if there's no fit)",
                ""
            };
            lines.AddRange(m_report);
            try
            {
                File.WriteAllLines(ReportPath, lines);
            }
            catch (Exception e)
            {
                Loader.Log("survey report error: " + e.Message);
            }
        }
    }
}
