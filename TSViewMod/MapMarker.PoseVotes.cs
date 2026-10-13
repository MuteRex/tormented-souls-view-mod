using System.Collections.Generic;
using UnityEngine;

namespace TSViewMod
{
    // Map pose memory: which side of each map sheet is printed and which way is up.
    public partial class MapMarker
    {
        // Per-map votes for printed side / up axis, persisted in TSViewMod_mappose.txt.
        private static class PoseVotes
        {
            private static Dictionary<string, Dictionary<string, int>> s_votes;

            private static string FilePath => Loader.DataFile("TSViewMod_mappose.txt");

            private static string Encode(float sign, Vector3 up)
            {
                int axis = up.x != 0f ? 0 : up.y != 0f ? 1 : 2;
                return (sign > 0f ? "+" : "-") + "," + axis + "," + (up[axis] > 0f ? "+" : "-");
            }

            public static void Add(string key, float sign, Vector3 up)
            {
                Load();
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally))
                {
                    tally = new Dictionary<string, int>();
                    s_votes[key] = tally;
                }
                string v = Encode(sign, up);
                tally.TryGetValue(v, out int n);
                tally[v] = n + 1;
                Save();
            }

            public static bool TryGetMajority(string key, out float sign, out Vector3 up)
            {
                Load();
                sign = 1f;
                up = Vector3.zero;
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally) || tally.Count == 0)
                {
                    return false;
                }
                string best = null;
                int bestCount = -1;
                foreach (KeyValuePair<string, int> pair in tally)
                {
                    if (pair.Value > bestCount)
                    {
                        best = pair.Key;
                        bestCount = pair.Value;
                    }
                }
                string[] f = best.Split(',');
                sign = f[0] == "+" ? 1f : -1f;
                up = FrontNormalLocal(int.Parse(f[1]), f[2] == "+" ? 1f : -1f);
                return true;
            }

            public static string Describe(string key)
            {
                Load();
                if (!s_votes.TryGetValue(key, out Dictionary<string, int> tally))
                {
                    return "no votes";
                }
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> pair in tally)
                {
                    parts.Add(pair.Key + " x" + pair.Value);
                }
                return string.Join("; ", parts.ToArray());
            }

            private static void Load()
            {
                if (s_votes != null)
                {
                    return;
                }
                s_votes = new Dictionary<string, Dictionary<string, int>>();
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                    {
                        return;
                    }
                    foreach (string line in System.IO.File.ReadAllLines(FilePath))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length != 3 || line.StartsWith("#"))
                        {
                            continue;
                        }
                        if (!s_votes.TryGetValue(f[0], out Dictionary<string, int> tally))
                        {
                            tally = new Dictionary<string, int>();
                            s_votes[f[0]] = tally;
                        }
                        tally[f[1]] = int.Parse(f[2]);
                    }
                }
                catch (System.Exception e)
                {
                    Loader.Log("map pose votes unreadable, starting over: " + e.Message);
                    s_votes.Clear();
                }
            }

            private static void Save()
            {
                try
                {
                    var lines = new List<string> { "# TSViewMod: which side of each map is printed / which way is up. Delete to relearn." };
                    foreach (KeyValuePair<string, Dictionary<string, int>> map in s_votes)
                    {
                        foreach (KeyValuePair<string, int> vote in map.Value)
                        {
                            lines.Add(map.Key + "\t" + vote.Key + "\t" + vote.Value);
                        }
                    }
                    System.IO.File.WriteAllLines(FilePath, lines);
                }
                catch (System.Exception e)
                {
                    Loader.Log("map pose votes not saved: " + e.Message);
                }
            }
        }
    }
}
