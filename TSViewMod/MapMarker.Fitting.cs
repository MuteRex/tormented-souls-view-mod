using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace TSViewMod
{
    // World -> map fitting per room: orientation, scale and offset search, scored by IoU and door positions.
    public partial class MapMarker
    {

        private struct Fit
        {
            public int orientation;
            public float scale;
            public Vector2 offset;
            public float iou;
            public float score;
            public bool withPrior; // fitted knowing the usual orientation: trust it even if it differs

            public Vector2 Apply(Vector2 world)
            {
                return Orient(orientation, world) * scale + offset;
            }
        }

        // Cached in TSViewMod_mapcache.txt; fits never change.
        private static class FitCache
        {
            private static Dictionary<string, Fit> s_fits;

            private static string FilePath => Loader.DataFile("TSViewMod_mapcache.txt");

            public static bool TryGet(string key, out Fit fit)
            {
                Load();
                return s_fits.TryGetValue(key, out fit);
            }

            public static void Put(string key, Fit fit)
            {
                Load();
                s_fits[key] = fit;
            }

            // The building is drawn the same way round on every sheet: the majority orientation (-1 = too few fits).
            public static int MajorityOrientation()
            {
                Load();
                var counts = new int[8];
                int total = 0;
                foreach (Fit f in s_fits.Values)
                {
                    if (f.orientation >= 0 && f.orientation < 8)
                    {
                        counts[f.orientation]++;
                        total++;
                    }
                }
                int best = 0;
                for (int o = 1; o < 8; o++)
                {
                    if (counts[o] > counts[best])
                    {
                        best = o;
                    }
                }
                return total >= 5 && counts[best] >= total * 0.6f ? best : -1;
            }

            // An off-majority fit is likely mirrored: drop it so the room is refitted with the prior.
            public static bool TryGetTrusted(string key, out Fit fit)
            {
                if (!TryGet(key, out fit))
                {
                    return false;
                }
                int expected = MajorityOrientation();
                if (expected >= 0 && fit.orientation != expected && !fit.withPrior)
                {
                    Loader.Log("map marker: cached " + key + " has orientation " + fit.orientation + ", most rooms use " + expected + "; refitting");
                    s_fits.Remove(key);
                    return false;
                }
                return true;
            }

            private static void Load()
            {
                if (s_fits != null)
                {
                    return;
                }
                s_fits = new Dictionary<string, Fit>();
                try
                {
                    if (!System.IO.File.Exists(FilePath))
                    {
                        return;
                    }
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    foreach (string line in System.IO.File.ReadAllLines(FilePath))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 7 || f[0] != CacheVersion)
                        {
                            continue;
                        }
                        s_fits[f[1]] = new Fit
                        {
                            orientation = int.Parse(f[2], inv),
                            scale = float.Parse(f[3], inv),
                            offset = new Vector2(float.Parse(f[4], inv), float.Parse(f[5], inv)),
                            iou = float.Parse(f[6], inv),
                            withPrior = f.Length > 7 && f[7] == "p"
                        };
                    }
                }
                catch (System.Exception e)
                {
                    Loader.Log("map cache unreadable, refitting: " + e.Message);
                    s_fits.Clear();
                }
            }

            public static void Save()
            {
                try
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var lines = new List<string> { "# TSViewMod map fits. Delete this file to recompute." };
                    foreach (KeyValuePair<string, Fit> pair in s_fits)
                    {
                        Fit f = pair.Value;
                        lines.Add(string.Join("\t", CacheVersion, pair.Key, f.orientation.ToString(inv), f.scale.ToString("R", inv),
                            f.offset.x.ToString("R", inv), f.offset.y.ToString("R", inv), f.iou.ToString("R", inv), f.withPrior ? "p" : "-"));
                    }
                    System.IO.File.WriteAllLines(FilePath, lines);
                }
                catch (System.Exception e)
                {
                    Loader.Log("map cache not saved: " + e.Message);
                }
            }
        }

        // Bump when the fitting method changes so old entries are recomputed.
        private const string CacheVersion = "2";

        private static long FrameBudgetMs => Survey.Active ? 80 : 4;

        // Score penalty for orientations other than the majority one.
        private const float OrientationPenalty = 0.15f;

        private static IEnumerator FitShape(WorldMask world, MapMask map, List<KeyValuePair<string, Vector2>> doors, List<RoomShape> rooms, int u, int v, StringBuilder log, Fit[] result)
        {
            float mapArea = map.count * map.cell * map.cell;
            float worldArea = world.count * world.cell * world.cell;
            float s0 = Mathf.Sqrt(mapArea / Mathf.Max(worldArea, 1e-6f));
            Vector2 worldCentroid = world.Centroid();
            Vector2 mapCentroid = map.Centroid();
            Rect2 roomRect = map.rect;
            float diag = roomRect.Size.magnitude;
            var slice = Stopwatch.StartNew();
            var best = new Fit { score = float.MaxValue };
            int expected = FitCache.MajorityOrientation();
            if (expected >= 0)
            {
                log.Append("\n    expected orientation o").Append(expected);
            }
            for (int o = 0; o < 8; o++)
            {
                var orientBest = new Fit { score = float.MaxValue };
                foreach (float k in new[] { 0.85f, 0.92f, 1f, 1.08f, 1.15f })
                {
                    float s = s0 * k;
                    Vector2 t0 = mapCentroid - Orient(o, worldCentroid) * s;
                    for (int dy = -6; dy <= 6; dy++)
                    {
                        for (int dx = -6; dx <= 6; dx++)
                        {
                            var fit = new Fit { orientation = o, scale = s, offset = t0 + new Vector2(dx, dy) * map.cell };
                            fit.iou = IoU(world, map, fit, 2);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                        if (slice.ElapsedMilliseconds > FrameBudgetMs)
                        {
                            yield return null;
                            slice.Restart();
                        }
                    }
                }
                Fit coarse = orientBest;
                orientBest.score = float.MaxValue;
                for (int ks = -4; ks <= 4; ks++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            var fit = new Fit { orientation = o, scale = coarse.scale * (1f + ks * 0.01f), offset = coarse.offset + new Vector2(dx, dy) * map.cell * 0.5f };
                            fit.iou = IoU(world, map, fit, 1);
                            fit.score = 1f - fit.iou;
                            if (fit.score < orientBest.score)
                            {
                                orientBest = fit;
                            }
                        }
                    }
                    if (slice.ElapsedMilliseconds > FrameBudgetMs)
                    {
                        yield return null;
                        slice.Restart();
                    }
                }
                // Doors decide between equally overlapping mirror/rotation candidates; shape overlap breaks remaining ties.
                float doorPenalty = DoorPenalty(orientBest, doors, rooms, u, v, roomRect, diag);
                orientBest.score = DoorWeight * doorPenalty + (1f - orientBest.iou) + (expected >= 0 && o != expected ? OrientationPenalty : 0f);
                log.Append("\n    o").Append(o).Append(" IoU ").Append(orientBest.iou.ToString("0.000"))
                    .Append(" doors ").Append(doorPenalty.ToString("0.000")).Append(" score ").Append(orientBest.score.ToString("0.000"));
                if (orientBest.score < best.score)
                {
                    best = orientBest;
                }
            }
            best.withPrior = expected >= 0;
            result[0] = best;
        }

        private static float IoU(WorldMask world, MapMask map, Fit fit, int step)
        {
            Vector2 a = Orient(fit.orientation, Vector2.right);
            Vector2 b = Orient(fit.orientation, Vector2.up);
            float inv = 1f / fit.scale;
            int inter = 0;
            int mapInside = 0;
            int worldInside = 0;
            for (int y = 0; y < MaskRes; y += step)
            {
                for (int x = 0; x < MaskRes; x += step)
                {
                    bool inMap = map.mask[y * MaskRes + x];
                    Vector2 q = (map.CellCentre(x, y) - fit.offset) * inv;
                    bool inWorld = world.Get(new Vector2(q.x * a.x + q.y * a.y, q.x * b.x + q.y * b.y));
                    if (inMap)
                    {
                        mapInside++;
                    }
                    if (inWorld)
                    {
                        worldInside++;
                        if (inMap)
                        {
                            inter++;
                        }
                    }
                }
            }
            float sampleCell = map.cell * step;
            float worldCells = world.count * world.cell * world.cell * fit.scale * fit.scale / (sampleCell * sampleCell);
            float union = mapInside + Mathf.Max(worldInside, worldCells) - inter;
            return union > 0f ? inter / union : 0f;
        }

        private const float DoorWeight = 2f;

        private static float DoorPenalty(Fit fit, List<KeyValuePair<string, Vector2>> doors, List<RoomShape> rooms, int u, int v, Rect2 roomRect, float diag)
        {
            float total = 0f;
            int n = 0;
            foreach (KeyValuePair<string, Vector2> door in doors)
            {
                float bestDistance = float.MaxValue;
                foreach (RoomShape r in rooms)
                {
                    if (r.name == door.Key)
                    {
                        bestDistance = Mathf.Min(bestDistance, r.To2D(u, v).Distance(fit.Apply(door.Value)));
                    }
                }
                if (bestDistance < float.MaxValue)
                {
                    total += bestDistance / Mathf.Max(diag, 1e-5f);
                    n++;
                }
            }
            return n > 0 ? total / n : 0f;
        }

        // 4 rotations, each optionally mirrored.
        private static Vector2 Orient(int o, Vector2 p)
        {
            if (o >= 4)
            {
                p.x = -p.x;
            }
            switch (o & 3)
            {
                case 1: return new Vector2(-p.y, p.x);
                case 2: return new Vector2(-p.x, -p.y);
                case 3: return new Vector2(p.y, -p.x);
                default: return p;
            }
        }
    }
}
