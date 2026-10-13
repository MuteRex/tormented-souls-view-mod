using UnityEngine;

namespace TSViewMod
{
    // Small geometry types shared by the fitting code.
    public partial class MapMarker
    {

        internal struct Rect2
        {
            public Vector2 min;
            public Vector2 max;
            public Vector2 Size => max - min;
            public Vector2 Center => (min + max) * 0.5f;

            public static Rect2 Empty => new Rect2 { min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue) };

            public void Add(Vector2 p)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            public float Distance(Vector2 p)
            {
                float dx = Mathf.Max(min.x - p.x, 0f, p.x - max.x);
                float dy = Mathf.Max(min.y - p.y, 0f, p.y - max.y);
                return Mathf.Sqrt(dx * dx + dy * dy);
            }

            public override string ToString()
            {
                return "[" + min.x.ToString("0.00") + "," + min.y.ToString("0.00") + " .. " + max.x.ToString("0.00") + "," + max.y.ToString("0.00") + "]";
            }
        }

        private class RoomShape
        {
            public string name;
            public string tab;
            public Bounds bounds;
            public GameObject highlight;

            public Rect2 To2D(int u, int v)
            {
                return new Rect2 { min = new Vector2(bounds.min[u], bounds.min[v]), max = new Vector2(bounds.max[u], bounds.max[v]) };
            }
        }
    }
}
