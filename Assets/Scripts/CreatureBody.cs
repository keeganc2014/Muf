using UnityEngine;

// The visible body of a monster: a packed model (Resources/Creatures/timmy.bytes or spider.bytes) standing on its hand tips.
// The models are single meshes without bones, so a small skeleton is made for them when they are loaded:
//   torso (+ head), and for each arm an upper arm and a forearm (with the hand).
// Every vertex gets its weights from where it is on the model (arm or torso, upper arm or forearm), so the real GPU skinning moves it.
//
// The walk is a hand-over-hand gait, like a gorilla on its knuckles:
//   - each hand is planted on the floor and slides back (relative to the body) while the body moves over it, then lifts, swings
//     forward and plants again; the other hand is half a cycle behind,
//   - the arms bend at the elbow so the planted hand stays exactly where it landed (two-bone IK),
//   - the torso dips, rises over each planted hand and sways onto it; it leans forward more while it hunts.
// Steps follow the distance the monster really walked (Monster calls AddDistance), so the hands never slide on the floor
// and it never glides. When it stands still the arms hang down and it breathes and rocks a little.
// If the model does not look like the expected gorilla (or has no vertices) the old whole-body bob and sway is used instead.
public class CreatureBody : MonoBehaviour
{
    public bool chasing;        // set by Monster
    public Light glow;          // light on its face (set by Monster)

    // ---- gait (all as a share of the model's height) ----
    const float Sweep = 0.40f;        // how far a planted hand slides back relative to the body in one step
    const float Lift = 0.13f;         // how high a hand swings
    const float Crouch = 0.03f;       // the torso sinks this much while walking, so the arms can bend
    const float Bob = 0.015f;         // and rises this much over a planted hand
    const float OldStride = 1.5f;     // metres per cycle for the fallback body

    Transform body;             // the model, origin at the floor
    float phase, speedSmooth, idleTime, lastDist;

    // ---- skeleton ----
    bool rigged;
    float modelH;               // model height in model units
    Transform torso;
    Vector3 torsoPivot;         // where the torso bone sits (model units)
    float strideMeters;         // metres the body moves in one full cycle (each hand plants once)

    class Arm
    {
        public Transform upper, fore;
        public Vector3 shoulderRel;      // shoulder, relative to the torso bone
        public Vector3 hand;             // hand tip at rest (model units)
        public Vector3 restUp, restFore; // directions shoulder->elbow and elbow->hand at rest
        public float l1, l2, side;
    }
    Arm armL, armR;

    // ---------------------------------------------------------------- setup

    public static CreatureBody Create(Transform parent, string resource, float wantedHeight)
    {
        PackedMesh.Loaded l = PackedMesh.Build(resource, "Creature", false, GtagBootstrap.PlayerLayer);
        if (l == null || l.header.height <= 0.01f) { if (l != null) Destroy(l.root); return null; }
        float scale = wantedHeight / l.header.height;
        l.root.transform.SetParent(parent, false);
        l.root.transform.localScale = Vector3.one * scale;
        var c = l.root.AddComponent<CreatureBody>();
        c.body = l.root.transform;
        c.modelH = l.header.height;
        c.strideMeters = 2f * Sweep * wantedHeight;
        c.TryBuildSkeleton();
        return c;
    }

    // where the arm vertices are (as shares of the model height): below the belly everything is arm; above it, an arm vertex is
    // one that lies outside the edge of the torso. The edge differs a little with height.
    static readonly float[] EdgeY = { 0.42f, 0.49f, 0.56f, 0.66f, 0.73f, 0.77f };
    static readonly float[] EdgeX = { 0.178f, 0.17f, 0.16f, 0.19f, 0.146f, 0.146f };

    static float ArmWeight(Vector3 p, float H)
    {
        float yf = p.y / H, xf = Mathf.Abs(p.x) / H;
        if (yf < 0.42f) return 1f;
        float fade = Mathf.Clamp01((0.78f - yf) / 0.02f);      // nothing above the shoulders
        if (fade <= 0f) return 0f;
        float edge = EdgeX[EdgeX.Length - 1];
        for (int i = 1; i < EdgeY.Length; i++)
        {
            if (yf <= EdgeY[i]) { edge = Mathf.Lerp(EdgeX[i - 1], EdgeX[i], Mathf.InverseLerp(EdgeY[i - 1], EdgeY[i], yf)); break; }
        }
        return Mathf.SmoothStep(0f, 1f, (xf - edge) / 0.034f + 0.5f) * fade;
    }

    // forearm share of an arm vertex: 0 above the elbow, 1 below it, blended around it
    static float ForeWeight(Vector3 p, float H)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((0.53f - p.y / H) / 0.20f));
    }

    void TryBuildSkeleton()
    {
        MeshFilter mf = body.GetComponentInChildren<MeshFilter>();
        MeshRenderer mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
        if (mf == null || mr == null || mf.sharedMesh == null) return;
        Mesh mesh = mf.sharedMesh;
        Vector3[] v = mesh.vertices;
        int n = v.Length;
        if (n < 100) return;
        float H = modelH;

        // measure the model: the shoulder, elbow and hand tip of each arm, and the middle of the torso
        Vector3[] sh = new Vector3[2], el = new Vector3[2], hd = new Vector3[2];
        int[] cs = new int[2], ce = new int[2], ch = new int[2];
        Vector3 tor = Vector3.zero; int ct = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = v[i];
            float yf = p.y / H;
            float aw = ArmWeight(p, H);
            if (aw > 0.5f)
            {
                int s = p.x < 0f ? 0 : 1;
                if (yf >= 0.62f && yf < 0.78f) { sh[s] += p; cs[s]++; }
                else if (yf >= 0.38f && yf < 0.46f) { el[s] += p; ce[s]++; }
                else if (yf < 0.06f) { hd[s] += p; ch[s]++; }
            }
            else if (yf > 0.45f && yf < 0.75f) { tor += p; ct++; }
        }
        for (int s = 0; s < 2; s++)
            if (cs[s] == 0 || ce[s] == 0 || ch[s] == 0) return;   // not the expected gorilla shape
        if (ct == 0) return;
        for (int s = 0; s < 2; s++) { sh[s] /= cs[s]; el[s] /= ce[s]; hd[s] /= ch[s]; }
        float torsoZ = tor.z / ct;

        // the bones, all at rest with no rotation (so a vertex's rest position is simply its bind position)
        var rootB = new GameObject("Bones").transform;
        rootB.SetParent(body, false);
        torsoPivot = new Vector3(0f, 0.45f * H, torsoZ);
        torso = NewBone("Torso", rootB, torsoPivot);
        Transform[] bones = new Transform[6];
        bones[0] = rootB; bones[1] = torso;
        Arm[] arms = new Arm[2];
        for (int s = 0; s < 2; s++)
        {
            var a = new Arm();
            a.side = s == 0 ? -1f : 1f;
            a.upper = NewBone(s == 0 ? "UpperArm.L" : "UpperArm.R", torso, sh[s] - torsoPivot);
            a.fore = NewBone(s == 0 ? "Forearm.L" : "Forearm.R", a.upper, el[s] - sh[s]);
            a.shoulderRel = sh[s] - torsoPivot;
            a.hand = hd[s];
            a.restUp = (el[s] - sh[s]).normalized;
            a.restFore = (hd[s] - el[s]).normalized;
            a.l1 = (el[s] - sh[s]).magnitude;
            a.l2 = (hd[s] - el[s]).magnitude;
            arms[s] = a;
            bones[2 + s * 2] = a.upper; bones[3 + s * 2] = a.fore;
        }
        armL = arms[0]; armR = arms[1];

        // skin weights from where each vertex is
        var weights = new BoneWeight[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 p = v[i];
            float wa = ArmWeight(p, H);
            float wf = ForeWeight(p, H);
            int s = p.x < 0f ? 0 : 1;
            var w = new BoneWeight();
            w.boneIndex0 = 1; w.weight0 = 1f - wa;
            w.boneIndex1 = 2 + s * 2; w.weight1 = wa * (1f - wf);
            w.boneIndex2 = 3 + s * 2; w.weight2 = wa * wf;
            weights[i] = w;
        }
        var binds = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) binds[i] = bones[i].worldToLocalMatrix * mf.transform.localToWorldMatrix;
        mesh.boneWeights = weights;
        mesh.bindposes = binds;

        // swap the plain renderer for a skinned one with the same materials
        GameObject g = mf.gameObject;
        Material[] mats = mr.sharedMaterials;
        DestroyImmediate(mr);
        DestroyImmediate(mf);
        var smr = g.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.sharedMaterials = mats;
        smr.bones = bones;
        smr.rootBone = rootB;
        smr.localBounds = new Bounds(new Vector3(0f, H * 0.5f, torsoZ), Vector3.one * H * 1.7f);
        smr.updateWhenOffscreen = false;
        smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        smr.receiveShadows = false;
        smr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        smr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        rigged = true;
    }

    static Transform NewBone(string name, Transform parent, Vector3 localPos)
    {
        var t = new GameObject(name).transform;
        t.gameObject.layer = GtagBootstrap.PlayerLayer;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        t.localRotation = Quaternion.identity;
        return t;
    }

    // ---------------------------------------------------------------- walking

    // total metres walked this frame
    public void AddDistance(float meters)
    {
        lastDist += meters;
        if (rigged) phase = Mathf.Repeat(phase + meters / strideMeters, 1f);
        else phase = Mathf.Repeat(phase + meters / OldStride, 1f);
    }

    void LateUpdate()
    {
        if (body == null) return;
        float dt = Time.deltaTime;
        float speed = dt > 0f ? lastDist / dt : 0f;
        lastDist = 0f;
        speedSmooth = Mathf.Lerp(speedSmooth, speed, 1f - Mathf.Exp(-8f * dt));
        float moving = Mathf.Clamp01(speedSmooth / 0.6f);      // 0 = standing, 1 = walking
        idleTime += dt;

        float sway = Mathf.Sin(phase * Mathf.PI * 2f);
        float pitch = (chasing ? 12f : 5f) * moving + Mathf.Sin(idleTime * 1.3f) * 1.2f * (1f - moving);

        if (!rigged)
        {
            // no skeleton: the whole body bobs, sways and leans
            float bob = Mathf.Abs(Mathf.Cos(phase * Mathf.PI * 2f)) * 0.07f * moving;
            float roll = sway * 5f * moving + Mathf.Sin(idleTime * 0.9f) * 1.5f * (1f - moving);
            body.localPosition = new Vector3(0f, bob, 0f);
            body.localRotation = Quaternion.Euler(pitch * 1.1f, 0f, roll);
            return;
        }

        // torso: sinks while walking, rises over each planted hand (highest when a hand is straight under the shoulder),
        // sways onto the planted hand (right hand is planted in the first half of the cycle)
        float rollT = -4f * sway * moving + Mathf.Sin(idleTime * 0.9f) * 1.5f * (1f - moving);
        float dy = modelH * (-Crouch * moving + Bob * Mathf.Abs(sway) * moving) + Mathf.Sin(idleTime * 1.6f) * modelH * 0.003f * (1f - moving);
        Quaternion qT = Quaternion.Euler(pitch, 0f, rollT);
        torso.localPosition = torsoPivot + new Vector3(0f, dy, 0f);
        torso.localRotation = qT;

        PoseArm(armL, Mathf.Repeat(phase + 0.5f, 1f), qT, moving);
        PoseArm(armR, phase, qT, moving);
    }

    // The hand goes where its step puts it (planted, then swung forward); the arm bends to reach it from the moving shoulder.
    void PoseArm(Arm a, float u, Quaternion qT, float moving)
    {
        float sweep = Sweep * modelH;
        float z, y = a.hand.y;
        if (u < 0.5f)
        {
            z = Mathf.Lerp(sweep * 0.5f, -sweep * 0.5f, u / 0.5f);                          // planted: the body moves over it
        }
        else
        {
            float s = (u - 0.5f) / 0.5f;
            z = Mathf.Lerp(-sweep * 0.5f, sweep * 0.5f, Mathf.SmoothStep(0f, 1f, s));      // swung forward
            y += Lift * modelH * Mathf.Sin(s * Mathf.PI);
        }
        Vector3 stepTarget = new Vector3(a.hand.x, y, a.hand.z + z);
        Vector3 target = Vector3.Lerp(a.hand, stepTarget, moving);

        Vector3 shoulder = torso.localPosition + qT * a.shoulderRel;
        Vector3 to = target - shoulder;
        float d = Mathf.Max(to.magnitude, 1e-4f);
        Vector3 dir = to / d;
        float reach = a.l1 + a.l2;
        float dc = Mathf.Clamp(d, Mathf.Abs(a.l1 - a.l2) + 0.02f * reach, reach * 0.999f);   // an arm never stretches
        Vector3 tip = shoulder + dir * dc;

        // elbow: l1 from the shoulder, l2 from the hand, pointing back and a little out like a gorilla's
        float k = (dc * dc + a.l1 * a.l1 - a.l2 * a.l2) / (2f * dc);
        float h = Mathf.Sqrt(Mathf.Max(a.l1 * a.l1 - k * k, 0f));
        Vector3 hint = new Vector3(a.side * 0.35f, 0.1f, -1f);
        Vector3 pole = hint - dir * Vector3.Dot(hint, dir);
        pole = pole.sqrMagnitude < 1e-4f ? Vector3.back : pole.normalized;
        Vector3 elbow = shoulder + dir * k + pole * h;

        // bone rotations (the model's root space): the upper arm turns from its rest direction to the elbow, the forearm on to the hand
        Quaternion r1 = Quaternion.FromToRotation(qT * a.restUp, elbow - shoulder);
        Quaternion qUp = r1 * qT;
        a.upper.localRotation = Quaternion.Inverse(qT) * qUp;
        Quaternion r2 = Quaternion.FromToRotation(qUp * a.restFore, tip - elbow);
        Quaternion qFore = r2 * qUp;
        a.fore.localRotation = Quaternion.Inverse(qUp) * qFore;
    }
}
