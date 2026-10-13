using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace TSViewMod
{
    // World side of the fit: the walkable area rasterised on the XZ plane.
    public partial class MapMarker
    {

        internal class WorldMask
        {
            public Rect2 rect;
            public float cell;
            public int w;
            public int h;
            public bool[] mask;
            public int count;

            public bool Near(Vector2 p, float radius)
            {
                int r = Mathf.CeilToInt(radius / cell);
                int cx = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int cy = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                for (int y = cy - r; y <= cy + r; y++)
                {
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if (x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x])
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            public bool Get(Vector2 p)
            {
                int x = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int y = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                return x >= 0 && y >= 0 && x < w && y < h && mask[y * w + x];
            }

            public Vector2 Centroid()
            {
                Vector2 sum = Vector2.zero;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (mask[y * w + x])
                        {
                            sum += new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
                        }
                    }
                }
                return count > 0 ? sum / count : rect.Center;
            }

            public static WorldMask Build(StringBuilder log)
            {
                WorldMask result = null;
                IEnumerator steps = BuildSteps(log, long.MaxValue, m => result = m);
                while (steps.MoveNext())
                {
                }
                return result;
            }

            // Spread over frames: yields whenever a frame's budget is used up (only the floor probe is slow).
            public static IEnumerator BuildSteps(StringBuilder log, long budgetMs, System.Action<WorldMask> done)
            {
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length < 3)
                {
                    log.Append(", no navmesh");
                    // Strict first; retry with the floor-height test only (some rooms have solid helper volumes).
                    WorldMask floorMask = null;
                    foreach (bool strict in new[] { true, false })
                    {
                        IEnumerator probe = FloorSteps(log, strict, budgetMs, m => floorMask = m);
                        while (probe.MoveNext())
                        {
                            yield return null;
                        }
                        if (floorMask != null)
                        {
                            break;
                        }
                    }
                    done(floorMask);
                    yield break;
                }
                done(FromNavMesh(tri, log));
            }

            private static WorldMask FromNavMesh(NavMeshTriangulation tri, StringBuilder log)
            {
                Rect2 rect = Rect2.Empty;
                foreach (Vector3 p in tri.vertices)
                {
                    rect.Add(new Vector2(p.x, p.z));
                }
                float cell = Mathf.Max(rect.Size.x, rect.Size.y) / 96f;
                if (cell <= 0f)
                {
                    return null;
                }
                var m = new WorldMask { rect = rect, cell = cell };
                m.w = Mathf.CeilToInt(rect.Size.x / cell) + 1;
                m.h = Mathf.CeilToInt(rect.Size.y / cell) + 1;
                m.mask = new bool[m.w * m.h];
                for (int i = 0; i + 2 < tri.indices.Length; i += 3)
                {
                    Vector3 a = tri.vertices[tri.indices[i]];
                    Vector3 b = tri.vertices[tri.indices[i + 1]];
                    Vector3 c = tri.vertices[tri.indices[i + 2]];
                    m.Fill(new Vector2(a.x, a.z), new Vector2(b.x, b.z), new Vector2(c.x, c.z));
                }
                foreach (bool on in m.mask)
                {
                    if (on)
                    {
                        m.count++;
                    }
                }
                log.Append(", navmesh ").Append(rect).Append(' ').Append(m.count).Append(" cells");
                return m.count > 0 ? m : null;
            }

            // No navmesh: probe the floor with rays from chest height (flat, near floor level, headroom), flood-filled from
            // the player.
            private static IEnumerator FloorSteps(StringBuilder log, bool strict, long budgetMs, System.Action<WorldMask> done)
            {
                PlayerController player = FindObjectOfType<PlayerController>();
                if (player == null)
                {
                    done(null);
                    yield break;
                }
                var clock = Stopwatch.StartNew();
                Vector3 origin = player.transform.position;
                Scene scene = SceneManager.GetActiveScene();
                Rect2 rect = Rect2.Empty;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Collider collider in root.GetComponentsInChildren<Collider>())
                    {
                        Bounds b = collider.bounds;
                        if (collider.isTrigger || b.size.x > 150f || b.size.z > 150f || b.max.y < origin.y - 1f || b.min.y > origin.y + 2.5f)
                        {
                            continue;
                        }
                        rect.Add(new Vector2(b.min.x, b.min.z));
                        rect.Add(new Vector2(b.max.x, b.max.z));
                    }
                }
                float cell = Mathf.Max(rect.Size.x, rect.Size.y) / 96f;
                if (!(cell > 0f) || cell > 2f)
                {
                    log.Append(", no floor colliders");
                    done(null);
                    yield break;
                }
                var m = new WorldMask { rect = rect, cell = cell };
                m.w = Mathf.CeilToInt(rect.Size.x / cell) + 1;
                m.h = Mathf.CeilToInt(rect.Size.y / cell) + 1;
                var floor = new bool[m.w * m.h];
                const QueryTriggerInteraction noTriggers = QueryTriggerInteraction.Ignore;
                for (int y = 0; y < m.h; y++)
                {
                    if (clock.ElapsedMilliseconds > budgetMs)
                    {
                        yield return null;
                        clock.Restart();
                    }
                    for (int x = 0; x < m.w; x++)
                    {
                        Vector3 top = new Vector3(rect.min.x + (x + 0.5f) * cell, origin.y + 1.6f, rect.min.y + (y + 0.5f) * cell);
                        if ((strict && Physics.CheckSphere(top, 0.15f, ~0, noTriggers))
                            || !Physics.Raycast(top, Vector3.down, out RaycastHit hit, 2.6f, ~0, noTriggers)
                            || hit.normal.y < 0.7f || Mathf.Abs(hit.point.y - origin.y) > 0.6f)
                        {
                            continue;
                        }
                        floor[y * m.w + x] = !strict || !Physics.CheckCapsule(hit.point + Vector3.up * 0.45f, hit.point + Vector3.up * 1.4f, 0.12f, ~0, noTriggers);
                    }
                }
                int start = -1;
                float best = float.MaxValue;
                for (int i = 0; i < floor.Length; i++)
                {
                    if (!floor[i])
                    {
                        continue;
                    }
                    Vector2 c = new Vector2(rect.min.x + (i % m.w + 0.5f) * cell, rect.min.y + (i / m.w + 0.5f) * cell);
                    float d = (c - new Vector2(origin.x, origin.z)).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        start = i;
                    }
                }
                m.mask = new bool[floor.Length];
                if (start >= 0 && best < 4f)
                {
                    var stack = new Stack<int>();
                    stack.Push(start);
                    m.mask[start] = true;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop();
                        int cx = i % m.w;
                        int cy = i / m.w;
                        foreach (int n in new[] { cx > 0 ? i - 1 : -1, cx < m.w - 1 ? i + 1 : -1, cy > 0 ? i - m.w : -1, cy < m.h - 1 ? i + m.w : -1 })
                        {
                            if (n >= 0 && floor[n] && !m.mask[n])
                            {
                                m.mask[n] = true;
                                stack.Push(n);
                            }
                        }
                    }
                }
                foreach (bool on in m.mask)
                {
                    if (on)
                    {
                        m.count++;
                    }
                }
                log.Append(strict ? ", floor probe " : ", relaxed floor probe ").Append(rect).Append(' ').Append(m.count).Append(" cells");
                done(m.count > 20 ? m : null);
            }

            private void Fill(Vector2 a, Vector2 b, Vector2 c)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - rect.min.x) / cell));
                int x1 = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - rect.min.x) / cell));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - rect.min.y) / cell));
                int y1 = Mathf.Min(h - 1, Mathf.FloorToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - rect.min.y) / cell));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 p = new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
                        if (InTriangle(p, a, b, c))
                        {
                            mask[y * w + x] = true;
                        }
                    }
                }
            }

            private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                float d1 = Cross(p, a, b);
                float d2 = Cross(p, b, c);
                float d3 = Cross(p, c, a);
                bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
                bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
                return !(neg && pos);
            }

            private static float Cross(Vector2 p, Vector2 a, Vector2 b)
            {
                return (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            }
        }
    }
}
