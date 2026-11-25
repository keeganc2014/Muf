using System.Globalization;
using UnityEngine;

// monke.obj, stored as plain triangles in Assets/Resources/Rig/monke_mesh.txt (one triangle per line, 9 numbers).
// It is read here instead of imported, so it never depends on the model importer, and the mesh can be changed at runtime.
// The model faces +Z, its head is at the top, and its two long arms hang down at the sides (no legs).
public static class MonkeModel
{
    public const string Resource = "Rig/monke_mesh";

    // measured from the mesh (model units)
    public static readonly Vector3 HeadCenter = new Vector3(0f, 0.56f, 0.03f);
    public static readonly Vector3 RestHandL = new Vector3(-0.391f, -0.601f, -0.047f);
    public static readonly Vector3 RestHandR = new Vector3(0.391f, -0.601f, -0.047f);
    public const float HandTipY = -0.658f;   // lowest point of the model

    public static readonly Vector3 ShoulderL = new Vector3(-0.31f, 0.18f, -0.03f);   // where each arm pivots
    public static readonly Vector3 ShoulderR = new Vector3(0.31f, 0.18f, -0.03f);

    public static Vector3[] Positions;       // 3 per triangle, never edited
    public static int[] Triangles;           // 0,1,2,3,4,5... (every triangle owns its 3 vertices = flat shading)

    // Arm skinning data. Every vertex of an arm is stored relative to its shoulder: how far along the arm it is and how far to
    // the side of the arm's axis. Deform() then swings the arm round the shoulder to point at the hand and stretches it
    // lengthways, so the arm keeps its thickness however far the hand goes.
    static float[] armMask;                  // 0 = body/head, 1 = fully follows the arm, in between = shoulder blend
    static float[] armAlong;
    static Vector3[] armPerp;
    static Vector3 dirRestL, dirRestR;
    static float lenRestL, lenRestR;

    // Part of every triangle (the 10th number on its line in monke_mesh.txt, written by Tools/pack_assets.py monke):
    // 0 = head + body, 1 / 2 = left arm / left hand, 3 / 4 = right arm / right hand. The arms are separate pieces: they are not attached to the body.
    public const int PartBody = 0, PartArmL = 1, PartHandL = 2, PartArmR = 3, PartHandR = 4;
    public static int[] TriPart;
    public static bool HasParts;
    public static Vector3 HandCenterL, HandCenterR;   // the pivot of each detached limb (the middle of its hand)

    static int[] noHeadTriangles;            // everything except the head and neck (what you see of yourself)

    static bool tried;

    public static bool Load()
    {
        if (Positions != null) return true;
        if (tried) return false;
        tried = true;

        TextAsset ta = Resources.Load<TextAsset>(Resource);
        if (ta == null)
        {
            Debug.LogWarning("[GtagTest] Assets/Resources/" + Resource + ".txt not found - monke.obj model not available.");
            return false;
        }

        string[] lines = ta.text.Split('\n');
        var pos = new System.Collections.Generic.List<Vector3>(4500);
        var parts = new System.Collections.Generic.List<int>(1500);
        foreach (string line in lines)
        {
            string[] t = line.Trim().Split(' ');
            if (t.Length < 9) continue;
            parts.Add(t.Length >= 10 ? int.Parse(t[9], CultureInfo.InvariantCulture) : -1);
            for (int k = 0; k < 3; k++)
            {
                pos.Add(new Vector3(
                    float.Parse(t[k * 3], CultureInfo.InvariantCulture),
                    float.Parse(t[k * 3 + 1], CultureInfo.InvariantCulture),
                    float.Parse(t[k * 3 + 2], CultureInfo.InvariantCulture)));
            }
        }
        if (pos.Count < 3) return false;

        Positions = pos.ToArray();
        TriPart = parts.ToArray();
        HasParts = true;
        foreach (int pp in TriPart) if (pp < 0) HasParts = false;
        Triangles = new int[Positions.Length];
        for (int i = 0; i < Triangles.Length; i++) Triangles[i] = i;

        var keep = new System.Collections.Generic.List<int>(Triangles.Length);
        for (int f = 0; f < Triangles.Length / 3; f++)
        {
            float cy = (Positions[f * 3].y + Positions[f * 3 + 1].y + Positions[f * 3 + 2].y) / 3f;
            if (cy > 0.40f) continue;   // head and neck
            keep.Add(f * 3); keep.Add(f * 3 + 1); keep.Add(f * 3 + 2);
        }
        noHeadTriangles = keep.ToArray();

        if (HasParts)
        {
            Vector3 sl = Vector3.zero, sr = Vector3.zero;
            int nl = 0, nr = 0;
            for (int f = 0; f < TriPart.Length; f++)
            {
                if (TriPart[f] != PartHandL && TriPart[f] != PartHandR) continue;
                Vector3 c = (Positions[f * 3] + Positions[f * 3 + 1] + Positions[f * 3 + 2]) / 3f;
                if (TriPart[f] == PartHandL) { sl += c; nl++; } else { sr += c; nr++; }
            }
            HandCenterL = nl > 0 ? sl / nl : RestHandL;
            HandCenterR = nr > 0 ? sr / nr : RestHandR;
        }

        Vector3 aL = RestHandL - ShoulderL, aR = RestHandR - ShoulderR;
        lenRestL = aL.magnitude; dirRestL = aL / lenRestL;
        lenRestR = aR.magnitude; dirRestR = aR / lenRestR;

        // Vertices at the same position get the same numbers, so the mesh never tears.
        armMask = new float[Positions.Length];
        armAlong = new float[Positions.Length];
        armPerp = new Vector3[Positions.Length];
        for (int i = 0; i < Positions.Length; i++)
        {
            Vector3 p = Positions[i];
            float my = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.30f, 0.05f, p.y));                // above the shoulder: stays put
            float mx = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.10f, 0.21f, Mathf.Abs(p.x)));     // near the chest: stays put
            armMask[i] = my * mx;
            bool left = p.x < 0f;
            Vector3 rel = p - (left ? ShoulderL : ShoulderR);
            Vector3 d = left ? dirRestL : dirRestR;
            armAlong[i] = Vector3.Dot(rel, d);
            armPerp[i] = rel - d * armAlong[i];
        }
        return true;
    }

    // Swing both arms so the hands end up at handL / handR (model space, the stretched arm never gets shorter than a quarter).
    public static void Deform(Vector3[] outV, Vector3 handL, Vector3 handR)
    {
        Vector3 toL = handL - ShoulderL, toR = handR - ShoulderR;
        Quaternion qL = toL.sqrMagnitude > 0.0001f ? Quaternion.FromToRotation(dirRestL, toL.normalized) : Quaternion.identity;
        Quaternion qR = toR.sqrMagnitude > 0.0001f ? Quaternion.FromToRotation(dirRestR, toR.normalized) : Quaternion.identity;
        float kL = Mathf.Max(0.25f, toL.magnitude / lenRestL);
        float kR = Mathf.Max(0.25f, toR.magnitude / lenRestR);

        for (int i = 0; i < Positions.Length; i++)
        {
            float m = armMask[i];
            if (m <= 0f) { outV[i] = Positions[i]; continue; }
            Vector3 p = Positions[i];
            Vector3 bone;
            if (p.x < 0f) bone = ShoulderL + qL * (armPerp[i] + dirRestL * (armAlong[i] * kL));
            else bone = ShoulderR + qR * (armPerp[i] + dirRestR * (armAlong[i] * kR));
            outV[i] = Vector3.Lerp(p, bone, m);
        }
    }

    public static Mesh NewMesh(bool withoutHead = false)
    {
        var m = new Mesh();
        m.name = "Monke";
        m.MarkDynamic();
        m.vertices = (Vector3[])Positions.Clone();
        m.triangles = withoutHead ? noHeadTriangles : Triangles;
        m.RecalculateNormals();   // every triangle has its own vertices, so these are exact flat normals
        m.RecalculateBounds();
        return m;
    }

    // A mesh made of the triangles of the given parts, moved so that 'pivot' is its origin. withoutHead drops the head and neck.
    public static Mesh PartMesh(string name, int[] wanted, Vector3 pivot, bool withoutHead)
    {
        var v = new System.Collections.Generic.List<Vector3>();
        for (int f = 0; f < TriPart.Length; f++)
        {
            if (System.Array.IndexOf(wanted, TriPart[f]) < 0) continue;
            if (withoutHead && (Positions[f * 3].y + Positions[f * 3 + 1].y + Positions[f * 3 + 2].y) / 3f > 0.40f) continue;
            for (int k = 0; k < 3; k++) v.Add(Positions[f * 3 + k] - pivot);
        }
        var tris = new int[v.Count];
        for (int i = 0; i < tris.Length; i++) tris[i] = i;
        var m = new Mesh();
        m.name = name;
        m.SetVertices(v);
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // A lit material cloned from the one Unity gives primitives (so the shader is always included in the build).
    // A lit material. It uses "Legacy Shaders/Diffuse", which Unity always puts into a build (the Standard shader of the
    // primitive's default material gets left out of builds -> pink/magenta surfaces on the headset).
    public static Material NewMaterial(Color c)
    {
        Shader sh = Shader.Find("Legacy Shaders/Diffuse");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        m.color = c;
        return m;
    }

    // Flat bright material that ignores lights and uses vertex colors (Sprites/Default is always in builds).
    public static Material NewUnlitMaterial(Color c)
    {
        Shader s = Shader.Find("Sprites/Default");
        if (s == null) return NewMaterial(c);
        var m = new Material(s);
        m.color = c;
        return m;
    }
}

// Draws the player's monke.
//  - The body everybody else sees (other players, the mirror): ONE connected monke. Each arm hangs from its shoulder on the torso and
//    bends like a real arm (two bones, elbow in the middle): the upper arm turns to point at the elbow, the forearm at the hand, and
//    the hand turns with your wrist. The arm has a fixed length: once it is stretched out completely it stays that long and only
//    the hand hitbox (the hand sphere, see Player) goes on reaching - the body never gets pulled or warped. Long Arms works the same way.
//  - What you see of yourself (firstPerson): just the hands, which sit exactly on your hand spheres.
//  - Without the rigged model: the old three loose pieces (body+head, left arm, right arm) of the plain monke.obj.
public class MonkeSkin : MonoBehaviour
{
    // Which way the limb points at rest (down) maps to the controller's forward: hand out in front of you, fingers forward.
    static readonly Quaternion LimbOffset = Quaternion.Euler(-90f, 0f, 0f);

    Transform body, limbL, limbR;
    float scale = 1f;

    // ---- rigged model (Resources/Rig/player.bytes, made by Tools/pack_assets.py player from the rigged gorilla.glb) ----
    bool rigged;
    bool ik;                       // connected body: arms bend from the shoulder (see PoseArmIK); false = first-person hands
    class ArmChain
    {
        public Transform upper, fore, hand;
        public Quaternion upperRest, foreRest, handRest;
        public float side;         // -1 = left arm (model's -x), +1 = right arm
    }
    ArmChain chainL, chainR;
    Transform rigRoot, shoulderL, shoulderR;
    Vector3 relPosL, relPosR, headCenterRig;
    Quaternion relRotL, relRotR;
    // At rest the hand bone has its fingers along its local +y and its thumb along local +z. This turns that into the controller's
    // frame (fingers forward, thumb up), so the hand sits on the controller like a hand holding it.
    static readonly Quaternion HandFix = Quaternion.AngleAxis(180f, new Vector3(0f, 1f, 1f).normalized);
    public readonly System.Collections.Generic.List<Renderer> Renderers = new System.Collections.Generic.List<Renderer>();

    // Returns null when the mesh file (with its part tags) is missing (callers then use their old shapes).
    // firstPerson = what you see of yourself: no head, and only the hands (an arm trailing back through your face would block the view).
    public static MonkeSkin Create(Transform parent, int layer, float scale, bool firstPerson = false)
    {
        MonkeSkin rs = CreateRigged(parent, layer, scale, firstPerson);
        if (rs != null) return rs;
        if (!MonkeModel.Load() || !MonkeModel.HasParts) return null;
        var go = new GameObject("Monke");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        var s = go.AddComponent<MonkeSkin>();
        s.scale = scale;
        s.body = s.Piece("Body", MonkeModel.PartMesh("MonkeBody", new[] { MonkeModel.PartBody }, MonkeModel.HeadCenter, firstPerson), layer);
        s.limbL = s.Piece("Left Arm", MonkeModel.PartMesh("MonkeLeft", firstPerson ? new[] { MonkeModel.PartHandL } : new[] { MonkeModel.PartArmL, MonkeModel.PartHandL }, MonkeModel.HandCenterL, false), layer);
        s.limbR = s.Piece("Right Arm", MonkeModel.PartMesh("MonkeRight", firstPerson ? new[] { MonkeModel.PartHandR } : new[] { MonkeModel.PartArmR, MonkeModel.PartHandR }, MonkeModel.HandCenterR, false), layer);
        return s;
    }

    [System.Serializable] public class BoneInfo { public string name; public int parent; public float[] pos, rot; }
    [System.Serializable] public class RigSub { public int count; public string tex; }
    [System.Serializable] public class RigHeader { public BoneInfo[] bones; public int n; public float[] headCenter; public RigSub[] subs; }

    static bool IsHeadBone(string n) { return n.StartsWith("head") || n.StartsWith("neck"); }
    static bool IsArmBone(string n) { return n.StartsWith("shoulder") || n.StartsWith("upper_arm") || n.StartsWith("forearm"); }

    static MonkeSkin CreateRigged(Transform parent, int layer, float scale, bool firstPerson)
    {
        TextAsset ta = Resources.Load<TextAsset>("Rig/player");
        if (ta == null) return null;
        byte[] d = ta.bytes;
        if (d.Length < 8 || d[0] != (byte)'G' || d[1] != (byte)'T' || d[2] != (byte)'R' || d[3] != (byte)'1') return null;
        int jl = System.BitConverter.ToInt32(d, 4);
        RigHeader h = JsonUtility.FromJson<RigHeader>(System.Text.Encoding.UTF8.GetString(d, 8, jl));
        int o = 8 + jl, n = h.n;

        var pos = new float[n * 3]; System.Buffer.BlockCopy(d, o, pos, 0, n * 12); o += n * 12;
        var nrm = new float[n * 3]; System.Buffer.BlockCopy(d, o, nrm, 0, n * 12); o += n * 12;
        var uv = new float[n * 2]; System.Buffer.BlockCopy(d, o, uv, 0, n * 8); o += n * 8;
        var bi = new byte[n * 4]; System.Buffer.BlockCopy(d, o, bi, 0, n * 4); o += n * 4;
        var bw = new float[n * 4]; System.Buffer.BlockCopy(d, o, bw, 0, n * 16); o += n * 16;

        var go = new GameObject("Monke");
        go.layer = layer;
        go.transform.SetParent(parent, false);
        var s = go.AddComponent<MonkeSkin>();
        s.rigged = true;
        s.scale = scale;
        s.headCenterRig = new Vector3(h.headCenter[0], h.headCenter[1], h.headCenter[2]);

        s.rigRoot = new GameObject("Rig").transform;
        s.rigRoot.gameObject.layer = layer;
        s.rigRoot.SetParent(go.transform, false);
        var bones = new Transform[h.bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            BoneInfo b = h.bones[i];
            var t = new GameObject(b.name).transform;
            t.gameObject.layer = layer;
            t.SetParent(b.parent >= 0 ? bones[b.parent] : s.rigRoot, false);
            t.localPosition = new Vector3(b.pos[0], b.pos[1], b.pos[2]);
            t.localRotation = new Quaternion(b.rot[0], b.rot[1], b.rot[2], b.rot[3]);
            bones[i] = t;
        }

        var meshGo = new GameObject("Body");
        meshGo.layer = layer;
        meshGo.transform.SetParent(s.rigRoot, false);
        var binds = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) binds[i] = bones[i].worldToLocalMatrix * meshGo.transform.localToWorldMatrix;

        var verts = new Vector3[n]; var norms = new Vector3[n]; var uvs = new Vector2[n]; var weights = new BoneWeight[n];
        var cat = new int[n];   // 1 = head/neck, 2 = arm (not hand)
        for (int i = 0; i < n; i++)
        {
            verts[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            norms[i] = new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
            uvs[i] = new Vector2(uv[i * 2], uv[i * 2 + 1]);
            var w = new BoneWeight();
            w.boneIndex0 = bi[i * 4]; w.boneIndex1 = bi[i * 4 + 1]; w.boneIndex2 = bi[i * 4 + 2]; w.boneIndex3 = bi[i * 4 + 3];
            w.weight0 = bw[i * 4]; w.weight1 = bw[i * 4 + 1]; w.weight2 = bw[i * 4 + 2]; w.weight3 = bw[i * 4 + 3];
            weights[i] = w;
            string dom = bones[w.boneIndex0].name;
            if (IsHeadBone(dom)) cat[i] = 1; else if (IsArmBone(dom)) cat[i] = 2;
        }
        var mesh = new Mesh();
        mesh.name = "MonkeRigged";
        mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs);
        mesh.boneWeights = weights;
        mesh.bindposes = binds;
        mesh.subMeshCount = h.subs.Length;
        var mats = new Material[h.subs.Length];
        for (int k = 0; k < h.subs.Length; k++)
        {
            var idx = new int[h.subs[k].count];
            System.Buffer.BlockCopy(d, o, idx, 0, idx.Length * 4); o += idx.Length * 4;
            if (firstPerson)
            {
                // what you see of yourself: no head and no arms (only the hands), so nothing blocks your view
                var keep = new System.Collections.Generic.List<int>(idx.Length);
                for (int t = 0; t < idx.Length; t += 3)
                {
                    int a = idx[t], b2 = idx[t + 1], c = idx[t + 2];
                    if (cat[a] == 1 || cat[b2] == 1 || cat[c] == 1) continue;
                    if (cat[a] == 2 && cat[b2] == 2 && cat[c] == 2) continue;
                    keep.Add(a); keep.Add(b2); keep.Add(c);
                }
                idx = keep.ToArray();
            }
            mesh.SetTriangles(idx, k, false);
            mats[k] = MonkeModel.NewMaterial(Color.white);
            if (!string.IsNullOrEmpty(h.subs[k].tex))
            {
                Texture2D tex = Resources.Load<Texture2D>(h.subs[k].tex);
                if (tex != null) mats[k].mainTexture = tex;
            }
        }
        mesh.RecalculateBounds();
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = bones[0];
        smr.sharedMaterials = mats;          // material 0 = the fur color that gets tinted
        smr.updateWhenOffscreen = true;      // the arms are moved away from the body, so the mesh bounds change
        smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        smr.receiveShadows = false;
        s.Renderers.Add(smr);

        if (!firstPerson)
        {
            // the connected body: the arm bones stay where they are on the torso and are turned every frame (PoseArmIK)
            s.chainL = FindChain(bones, true);
            s.chainR = FindChain(bones, false);
            if (s.chainL == null || s.chainR == null) { Destroy(go); return null; }   // not the expected rig: use the plain parts model
            s.ik = true;
            s.rigRoot.localScale = Vector3.one * scale;
            return s;
        }

        // first-person hands: the arm is not drawn, so each shoulder bone is simply lifted off the torso and driven from its hand
        Transform limbs = new GameObject("Arms").transform;
        limbs.gameObject.layer = layer;
        limbs.SetParent(go.transform, false);
        for (int i = 0; i < bones.Length; i++)
        {
            string bn = bones[i].name;
            bool left = bn.StartsWith("shoulder.L"), right = bn.StartsWith("shoulder.R");
            if (!left && !right) continue;
            Transform hand = null;
            foreach (Transform t in bones[i].GetComponentsInChildren<Transform>()) if (t.name.StartsWith(left ? "hand.L" : "hand.R")) hand = t;
            if (hand == null) continue;
            Vector3 rp = bones[i].InverseTransformPoint(hand.position);
            Quaternion rr = Quaternion.Inverse(bones[i].rotation) * hand.rotation;
            bones[i].SetParent(limbs, true);
            if (left) { s.shoulderL = bones[i]; s.relPosL = rp; s.relRotL = rr; } else { s.shoulderR = bones[i]; s.relPosR = rp; s.relRotR = rr; }
        }
        if (s.shoulderL == null || s.shoulderR == null) { Destroy(go); return null; }   // not the expected rig: use the plain parts model
        s.rigRoot.localScale = Vector3.one * scale;
        return s;
    }

    static ArmChain FindChain(Transform[] bones, bool left)
    {
        string sfx = left ? ".L" : ".R";
        Transform up = null, fore = null, hand = null;
        foreach (Transform t in bones)
        {
            if (t.name.StartsWith("upper_arm" + sfx)) up = t;
            else if (t.name.StartsWith("forearm" + sfx)) fore = t;
            else if (t.name.StartsWith("hand" + sfx)) hand = t;
        }
        if (up == null || fore == null || hand == null) return null;
        return new ArmChain
        {
            upper = up, fore = fore, hand = hand,
            upperRest = up.localRotation, foreRest = fore.localRotation, handRest = hand.localRotation,
            side = left ? -1f : 1f
        };
    }

    Transform Piece(string name, Mesh mesh, int layer)
    {
        var g = new GameObject(name);
        g.layer = layer;
        g.transform.SetParent(transform, false);
        g.transform.localScale = Vector3.one * scale;
        g.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = g.AddComponent<MeshRenderer>();
        r.sharedMaterial = MonkeModel.NewMaterial(Color.white);
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        Renderers.Add(r);
        return g.transform;
    }

    // headWorld = where the player's head is, yaw = which way the body faces (turned around Y only),
    // handL / handR = world position and rotation of each controller.
    public void Pose(Vector3 headWorld, Quaternion yaw, Vector3 handLPos, Quaternion handLRot, Vector3 handRPos, Quaternion handRRot)
    {
        if (rigged)
        {
            rigRoot.SetPositionAndRotation(headWorld - yaw * (headCenterRig * scale), yaw);
            if (ik)
            {
                PoseArmIK(chainL, handLPos, handLRot, yaw);
                PoseArmIK(chainR, handRPos, handRRot, yaw);
            }
            else
            {
                PlaceArm(shoulderL, relPosL, relRotL, handLPos, handLRot);
                PlaceArm(shoulderR, relPosR, relRotR, handRPos, handRRot);
            }
            return;
        }
        body.SetPositionAndRotation(headWorld - yaw * (MonkeModel.HeadCenter * scale), yaw);
        limbL.SetPositionAndRotation(handLPos, handLRot * LimbOffset);
        limbR.SetPositionAndRotation(handRPos, handRRot * LimbOffset);
    }

    // Two-bone arm: the shoulder stays on the torso, the upper arm points at the elbow, the forearm at the hand.
    // The arm never gets longer than it is: when the hand is further away than the arm reaches, the visible hand stops at the end of the
    // arm (still pointing the right way) while the hitbox goes on to wherever your hand really is.
    void PoseArmIK(ArmChain c, Vector3 handPos, Quaternion handRot, Quaternion yaw)
    {
        // start from the rest pose each frame, so lengths and starting directions never drift
        c.upper.localRotation = c.upperRest;
        c.fore.localRotation = c.foreRest;
        c.hand.localRotation = c.handRest;

        Vector3 shoulder = c.upper.position;
        Vector3 restElbow = c.fore.position;
        Vector3 restHand = c.hand.position;
        float l1 = (restElbow - shoulder).magnitude, l2 = (restHand - restElbow).magnitude;
        if (l1 < 1e-4f || l2 < 1e-4f) return;

        Vector3 toHand = handPos - shoulder;
        float d = toHand.magnitude;
        Vector3 dir = d > 1e-4f ? toHand / d : yaw * Vector3.forward;
        float reach = l1 + l2;
        float dc = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 0.02f * reach, reach * 0.999f);   // the arm cannot stretch (or fold through itself)
        Vector3 target = shoulder + dir * dc;

        // where the elbow goes: on the circle of points that are l1 from the shoulder and l2 from the hand, on the side the elbow points
        float a = (dc * dc + l1 * l1 - l2 * l2) / (2f * dc);
        float h = Mathf.Sqrt(Mathf.Max(l1 * l1 - a * a, 0f));
        Vector3 hint = yaw * new Vector3(c.side * 0.55f, -1f, -0.35f);          // elbows point down, out to the side and a little back
        Vector3 pole = hint - dir * Vector3.Dot(hint, dir);
        if (pole.sqrMagnitude < 1e-4f) pole = yaw * new Vector3(c.side, 0f, 0f);
        pole.Normalize();
        Vector3 elbow = shoulder + dir * a + pole * h;

        c.upper.rotation = Quaternion.FromToRotation(restElbow - shoulder, elbow - shoulder) * c.upper.rotation;
        Vector3 elbowNow = c.fore.position;
        c.fore.rotation = Quaternion.FromToRotation(c.hand.position - elbowNow, target - elbowNow) * c.fore.rotation;

        // the forearm takes half of the hand's roll, so the wrist skin does not twist like a wrung cloth
        Quaternion handTarget = handRot * HandFix;
        Quaternion rel = Quaternion.Inverse(c.fore.rotation) * handTarget;
        float tw = 2f * Mathf.Atan2(rel.y, rel.w) * Mathf.Rad2Deg;
        if (tw > 180f) tw -= 360f; else if (tw < -180f) tw += 360f;
        c.fore.rotation = c.fore.rotation * Quaternion.AngleAxis(tw * 0.5f, Vector3.up);
        c.hand.rotation = handTarget;
    }

    // moves a whole (rigid) arm so that its hand bone sits at the controller and turns with it
    void PlaceArm(Transform shoulder, Vector3 relPos, Quaternion relRot, Vector3 handPos, Quaternion handRot)
    {
        Quaternion target = handRot * HandFix;
        shoulder.localScale = Vector3.one * scale;
        shoulder.rotation = target * Quaternion.Inverse(relRot);
        shoulder.position = handPos - shoulder.rotation * (relPos * scale);
    }

    void OnDestroy()
    {
        foreach (Renderer r in Renderers)
        {
            if (r == null) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
        }
    }
}
