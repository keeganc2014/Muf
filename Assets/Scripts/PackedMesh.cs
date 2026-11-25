using System;
using System.Collections.Generic;
using UnityEngine;

// Loads the compact model files made by Tools/pack_assets.py (Resources/Map/map.bytes, Resources/Creatures/*.bytes).
// Reading them here instead of using a model importer keeps the project independent of FBX/glTF packages:
// the 500k-triangle map is 6 MB zipped instead of 70 MB.
// Layout: 'GTP1' | int jsonLength | json | blob. Every chunk is one mesh with one sub-mesh per material:
// positions ushort[3n] (inside the chunk's box), normals sbyte[3n], uvs ushort[2n] (inside the chunk's uv box), indices int[].
public static class PackedMesh
{
    [Serializable] public class SubInfo { public int mat; public int count; }
    [Serializable] public class ChunkInfo
    {
        public string name, group;
        public bool collide;
        public int n, off;
        public float[] pmin, pmax, uvmin, uvmax;
        public SubInfo[] subs;
    }
    [Serializable] public class MatInfo { public string tex; public float[] color; public bool unlit, alpha; public string surf; }
    [Serializable] public class BoxInfo { public float[] min, max; }
    [Serializable] public class BoardInfo { public float[] center, normal, up; public float w, h, t; }
    [Serializable] public class Header
    {
        public MatInfo[] materials;
        public ChunkInfo[] chunks;
        public BoxInfo bounds, cave, forest;
        public BoardInfo board;
        public float height;
    }

    public class Loaded
    {
        public GameObject root;
        public Header header;
    }

    // Builds every chunk as a child of a new root object. Chunks with a "group" are put under an object with that name
    // (that is how the map's computer, scoreboard, campground... can be found by name).
    public static Loaded Build(string resource, string rootName, bool colliders, int layer)
    {
        TextAsset ta = Resources.Load<TextAsset>(resource);
        if (ta == null)
        {
            Debug.LogWarning("[GtagTest] Assets/Resources/" + resource + ".bytes not found.");
            return null;
        }
        byte[] d = ta.bytes;
        if (d.Length < 8 || d[0] != (byte)'G' || d[1] != (byte)'T' || d[2] != (byte)'P' || d[3] != (byte)'1')
        {
            Debug.LogWarning("[GtagTest] " + resource + " is not a packed model file.");
            return null;
        }
        int jsonLen = BitConverter.ToInt32(d, 4);
        Header h = JsonUtility.FromJson<Header>(System.Text.Encoding.UTF8.GetString(d, 8, jsonLen));
        int blob = 8 + jsonLen;

        Material[] mats = new Material[h.materials.Length];
        for (int i = 0; i < mats.Length; i++) mats[i] = MakeMaterial(h.materials[i]);

        var root = new GameObject(rootName);
        root.layer = layer;
        var groups = new Dictionary<string, Transform>();
        foreach (ChunkInfo c in h.chunks)
        {
            Transform parent = root.transform;
            if (!string.IsNullOrEmpty(c.group))
            {
                if (!groups.TryGetValue(c.group, out parent))
                {
                    var g = new GameObject(c.group);
                    g.layer = layer;
                    g.transform.SetParent(root.transform, false);
                    parent = g.transform;
                    groups[c.group] = parent;
                }
            }
            var go = new GameObject(c.name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            Mesh mesh = BuildMesh(c, d, blob);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var cm = new Material[c.subs.Length];
            for (int s = 0; s < cm.Length; s++) cm[s] = mats[c.subs[s].mat];
            mr.sharedMaterials = cm;
            if (colliders)
            {
                // what each sub-mesh sounds like when a hand hits it (HandSounds)
                var si = go.AddComponent<SurfaceInfo>();
                si.kinds = new byte[c.subs.Length];
                for (int k = 0; k < si.kinds.Length; k++)
                {
                    string sf = h.materials[c.subs[k].mat].surf;
                    si.kinds[k] = sf == "wood" ? SurfaceInfo.Wood : sf == "grass" ? SurfaceInfo.Grass
                                : sf == "metal" ? SurfaceInfo.Metal : sf == "ice" ? SurfaceInfo.Ice : SurfaceInfo.Concrete;
                    if (si.kinds[k] == SurfaceInfo.Ice) si.hasIce = true;
                }
            }
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            if (colliders && c.collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        return new Loaded { root = root, header = h };
    }

    static Mesh BuildMesh(ChunkInfo c, byte[] d, int blob)
    {
        int n = c.n;
        int o = blob + c.off;
        var q = new ushort[n * 3];
        Buffer.BlockCopy(d, o, q, 0, n * 6); o += n * 6;
        var nq = new sbyte[n * 3];
        Buffer.BlockCopy(d, o, nq, 0, n * 3); o += n * 3;
        var uq = new ushort[n * 2];
        Buffer.BlockCopy(d, o, uq, 0, n * 4); o += n * 4;

        var verts = new Vector3[n];
        var norms = new Vector3[n];
        var uvs = new Vector2[n];
        float sx = (c.pmax[0] - c.pmin[0]) / 65535f, sy = (c.pmax[1] - c.pmin[1]) / 65535f, sz = (c.pmax[2] - c.pmin[2]) / 65535f;
        float su = (c.uvmax[0] - c.uvmin[0]) / 65535f, sv = (c.uvmax[1] - c.uvmin[1]) / 65535f;
        for (int i = 0; i < n; i++)
        {
            verts[i] = new Vector3(c.pmin[0] + q[i * 3] * sx, c.pmin[1] + q[i * 3 + 1] * sy, c.pmin[2] + q[i * 3 + 2] * sz);
            var nn = new Vector3(nq[i * 3], nq[i * 3 + 1], nq[i * 3 + 2]);
            norms[i] = nn.sqrMagnitude > 0f ? nn.normalized : Vector3.up;
            uvs[i] = new Vector2(c.uvmin[0] + uq[i * 2] * su, c.uvmin[1] + uq[i * 2 + 1] * sv);
        }

        var mesh = new Mesh();
        mesh.name = c.name;
        mesh.indexFormat = n > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = c.subs.Length;
        for (int s = 0; s < c.subs.Length; s++)
        {
            var idx = new int[c.subs[s].count];
            Buffer.BlockCopy(d, o, idx, 0, idx.Length * 4); o += idx.Length * 4;
            mesh.SetTriangles(idx, s, false);
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    static Material MakeMaterial(MatInfo mi)
    {
        // glTF colours are linear; Material.color wants the sRGB value
        Color c = new Color(mi.color[0], mi.color[1], mi.color[2], mi.color.Length > 3 ? mi.color[3] : 1f).gamma;
        Texture2D tex = string.IsNullOrEmpty(mi.tex) ? null : Resources.Load<Texture2D>(mi.tex);
        Material m;
        if (mi.unlit)
        {
            m = MonkeModel.NewUnlitMaterial(c);   // Sprites/Default: ignores light, blends alpha, draws both faces
        }
        else
        {
            m = MonkeModel.NewMaterial(c);
        }
        if (tex != null) m.mainTexture = tex;
        return m;
    }
}
