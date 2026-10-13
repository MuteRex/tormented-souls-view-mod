using UnityEngine;

namespace TSViewMod
{
    // Map side of the fit: room highlight meshes drawn into masks on the GPU.
    public partial class MapMarker
    {

        private class MapMask
        {
            public Rect2 rect;
            public float cell;
            public bool[] mask;
            public int count;

            public Vector2 CellCentre(int x, int y)
            {
                return new Vector2(rect.min.x + (x + 0.5f) * cell, rect.min.y + (y + 0.5f) * cell);
            }

            // p if inside the drawn room, else the nearest room cell nudged toward its neighbourhood's middle.
            public Vector2 Snap(Vector2 p)
            {
                int cx = Mathf.FloorToInt((p.x - rect.min.x) / cell);
                int cy = Mathf.FloorToInt((p.y - rect.min.y) / cell);
                if (Get(cx, cy))
                {
                    return p;
                }
                int bx = -1;
                int by = -1;
                float bestDistance = float.MaxValue;
                for (int y = 0; y < MaskRes; y++)
                {
                    for (int x = 0; x < MaskRes; x++)
                    {
                        if (!mask[y * MaskRes + x])
                        {
                            continue;
                        }
                        float d = (CellCentre(x, y) - p).sqrMagnitude;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bx = x;
                            by = y;
                        }
                    }
                }
                if (bx < 0)
                {
                    return p;
                }
                Vector2 sum = Vector2.zero;
                int n = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Get(bx + dx, by + dy))
                        {
                            sum += CellCentre(bx + dx, by + dy);
                            n++;
                        }
                    }
                }
                return sum / n;
            }

            private bool Get(int x, int y)
            {
                return x >= 0 && y >= 0 && x < MaskRes && y < MaskRes && mask[y * MaskRes + x];
            }

            public Vector2 Centroid()
            {
                Vector2 sum = Vector2.zero;
                for (int y = 0; y < MaskRes; y++)
                {
                    for (int x = 0; x < MaskRes; x++)
                    {
                        if (mask[y * MaskRes + x])
                        {
                            sum += CellCentre(x, y);
                        }
                    }
                }
                return count > 0 ? sum / count : rect.Center;
            }
        }

        private MapMask RenderMapMask(Transform map, RoomShape room, int u, int v, int n)
        {
            Rect2 r = room.To2D(u, v);
            float side = Mathf.Max(r.Size.x, r.Size.y) * 1.3f;
            if (side <= 0f)
            {
                return null;
            }
            Vector2 c = r.Center;
            var square = new Rect2 { min = c - Vector2.one * side * 0.5f, max = c + Vector2.one * side * 0.5f };

            MeshFilter filter = room.highlight.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return null;
            }
            bool[] mask = DrawMask(filter.sharedMesh, RelativeMatrix(map, room.highlight.transform), map, square, u, v, n);
            if (mask == null)
            {
                return null;
            }
            var result = new MapMask { rect = square, cell = side / MaskRes, mask = mask };
            foreach (bool on in mask)
            {
                if (on)
                {
                    result.count++;
                }
            }
            return result.count > 0 ? result : null;
        }

        // Orthographic draw of a mesh onto the map plane, read back as a mask (row 0 = lowest v).
        private bool[] DrawMask(Mesh mesh, Matrix4x4 model, Transform map, Rect2 square, int u, int v, int n)
        {
            if (m_maskMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                {
                    Loader.Log("map marker: no mask shader");
                    return null;
                }
                m_maskMaterial = new Material(shader) { color = Color.white };
                m_maskMaterial.SetInt("_Cull", 0);
                m_maskMaterial.SetInt("_ZWrite", 0);
                m_maskMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                m_maskMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                m_maskMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            }
            Matrix4x4 swizzle = Matrix4x4.zero;
            swizzle[0, u] = 1f;
            swizzle[1, v] = 1f;
            swizzle[2, n] = 1f;
            swizzle[3, 3] = 1f;
            // Mesh in map-local space, so the result ignores the opening animation's scale and the sheet's rotation.
            Matrix4x4 view = swizzle;
            Matrix4x4 proj = Matrix4x4.Ortho(square.min.x, square.max.x, square.min.y, square.max.y, -10000f, 10000f);

            if (m_flipChecked == 0)
            {
                m_flipChecked = CheckRowOrder(view, proj, map, square, u, v, n);
            }

            Color32[] pixels = Draw(mesh, model, view, proj);
            if (pixels == null)
            {
                return null;
            }
            var mask = new bool[MaskRes * MaskRes];
            for (int y = 0; y < MaskRes; y++)
            {
                int srcRow = m_flipChecked < 0 ? MaskRes - 1 - y : y;
                for (int x = 0; x < MaskRes; x++)
                {
                    Color32 p = pixels[srcRow * MaskRes + x];
                    mask[y * MaskRes + x] = p.r > 127 || p.g > 127 || p.b > 127;
                }
            }
            return mask;
        }

        private Color32[] Draw(Mesh mesh, Matrix4x4 model, Matrix4x4 view, Matrix4x4 proj)
        {
            RenderTexture rt = RenderTexture.GetTemporary(MaskRes, MaskRes, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(MaskRes, MaskRes, TextureFormat.RGBA32, false);
            try
            {
                var cb = new UnityEngine.Rendering.CommandBuffer { name = "TSViewMod map mask" };
                cb.SetRenderTarget(rt);
                cb.ClearRenderTarget(true, true, Color.black);
                cb.SetViewProjectionMatrices(view, proj);
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    cb.DrawMesh(mesh, model, m_maskMaterial, i, 0);
                }
                Graphics.ExecuteCommandBuffer(cb);
                cb.Release();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, MaskRes, MaskRes), 0, 0, false);
                return tex.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Destroy(tex);
            }
        }

        // Draws a quad that covers only the bottom (low-v) quarter of the view and checks which rows lit up.
        private int CheckRowOrder(Matrix4x4 view, Matrix4x4 proj, Transform map, Rect2 square, int u, int v, int n)
        {
            var mesh = new Mesh();
            Vector3 Corner(float fu, float fv)
            {
                Vector3 local = Vector3.zero;
                local[u] = Mathf.Lerp(square.min.x, square.max.x, fu);
                local[v] = Mathf.Lerp(square.min.y, square.max.y, fv);
                return local;
            }
            mesh.vertices = new[] { Corner(0f, 0f), Corner(1f, 0f), Corner(1f, 0.25f), Corner(0f, 0.25f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            Color32[] pixels = Draw(mesh, Matrix4x4.identity, view, proj);
            Destroy(mesh);
            if (pixels == null)
            {
                return 1;
            }
            int low = 0;
            int high = 0;
            for (int x = 0; x < MaskRes; x++)
            {
                if (pixels[2 * MaskRes + x].r > 127)
                {
                    low++;
                }
                if (pixels[(MaskRes - 3) * MaskRes + x].r > 127)
                {
                    high++;
                }
            }
            int result = high > low ? -1 : 1;
            Loader.Log("map marker: mask row check low " + low + " high " + high + " -> " + (result < 0 ? "flipped" : "normal"));
            return result;
        }
    }
}
