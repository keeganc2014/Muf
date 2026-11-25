using UnityEngine;

// A mirror in the hat room (the white busts), exactly where the map keeps its own mirror ("mirror_fixed"), standing against the wall
// facing into the room. It replaces the map's own mirror, which has no picture in Unity. It is the one place you see your whole monke body.
// It is a real mirror, not a fixed picture: a second camera sits where your eye would be *behind* the glass and looks out
// through the glass, so the reflection follows your head. It only renders while you are near it and not facing away.
[DefaultExecutionOrder(200)]   // after the body models have been posed for this frame
public class StumpMirror : MonoBehaviour
{
    const float HalfW = 0.5f, HalfH = 0.65f;  // the glass is 1.0 m x 1.3 m
    const float BottomGap = 0.4f;             // from the floor to the bottom of the glass
    const float ShowRange = 12f;              // further away than this the camera is switched off

    GtagBootstrap boot;
    Camera cam;
    RenderTexture rt;
    Transform glass;                          // glass.forward points INTO the mirror, the player stands on the -forward side
    Renderer glassRenderer;

    public static StumpMirror Create(GtagBootstrap b)
    {
        var go = new GameObject("StumpMirror");
        var m = go.AddComponent<StumpMirror>();
        m.boot = b;
        m.Build();
        return m;
    }

    // ---------------- where it stands ----------------

    static bool FloorBelow(Vector3 p, out float y)
    {
        RaycastHit hit;
        if (Physics.Raycast(new Vector3(p.x, p.y + 0.5f, p.z), Vector3.down, out hit, 6f, 1 << 0, QueryTriggerInteraction.Ignore))
        {
            y = hit.point.y;
            return true;
        }
        y = p.y - 1f;
        return false;
    }

    // floorPos = the floor point under the middle of the glass, facing = the way the glass looks (horizontal, into the room)
    void Place(out Vector3 floorPos, out Vector3 facing)
    {
        Vector3 anchor;
        Vector3 look;

        Transform mf = boot.FindInMap("mirror_fixed", false);
        if (mf == null) mf = boot.FindInMap("mirror", true);
        if (mf != null)
        {
            // the map's own mirror spot (in the hat room); its stand and board are replaced by this one
            Renderer[] rs = mf.GetComponentsInChildren<Renderer>();
            anchor = mf.position;
            if (rs.Length > 0)
            {
                Bounds b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                anchor = b.center;
            }
            foreach (Renderer r in rs) r.enabled = false;
            foreach (Collider c in mf.GetComponentsInChildren<Collider>()) c.enabled = false;

            // it looks at the middle of the hat room = the middle of the hat busts
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (string nm in new[] { "cowboyhatmodel", "tophatmodel", "sunhat_model", "bananahat_model" })
            {
                Transform t = boot.FindInMap(nm, false);
                if (t != null) { sum += t.position; n++; }
            }
            Vector3 focus = n > 0 ? sum / n : boot.SpawnPoint;
            look = focus - anchor;
            Debug.Log("[GtagTest] Mirror goes where the map's own mirror is: " + mf.name);
        }
        else
        {
            // no mirror in the map: against the nearest wall around the stump
            Vector3 origin = boot.SpawnPoint + Vector3.up * 1f;
            anchor = origin;
            look = boot.MapBounds.center - boot.SpawnPoint;
            float best = 99f;
            RaycastHit hit;
            for (int i = 0; i < 24; i++)
            {
                Vector3 d = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
                if (Physics.Raycast(origin, d, out hit, 6f, 1 << 0, QueryTriggerInteraction.Ignore) && hit.distance < best)
                {
                    best = hit.distance;
                    anchor = hit.point;
                    anchor.y = boot.SpawnPoint.y + 1f;
                    look = -d;
                }
            }
            Debug.LogWarning("[GtagTest] No mirror object in the map - mirror placed against the nearest wall near the stump.");
        }

        look.y = 0f;
        if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
        look.Normalize();

        // flush against the wall behind it, if there is one close by (so it never sticks out into the way)
        float floorY;
        FloorBelow(anchor, out floorY);
        Vector3 pos = new Vector3(anchor.x, floorY, anchor.z);
        RaycastHit wall;
        if (Physics.Raycast(new Vector3(anchor.x, floorY + 1f, anchor.z), -look, out wall, 1.5f, 1 << 0, QueryTriggerInteraction.Ignore))
        {
            Vector3 wn = wall.normal;
            wn.y = 0f;
            if (wn.sqrMagnitude > 0.01f && Vector3.Dot(wn.normalized, look) > 0.6f) look = wn.normalized;
            pos = new Vector3(wall.point.x, floorY, wall.point.z) + look * 0.14f;
        }
        FloorBelow(new Vector3(pos.x, floorY + 0.5f, pos.z), out floorY);
        pos.y = floorY;

        floorPos = pos;
        facing = look;
    }

    // ---------------- build ----------------

    void Build()
    {
        Vector3 floorPos, facing;
        Place(out floorPos, out facing);
        transform.position = floorPos + Vector3.up * (BottomGap + HalfH);
        transform.rotation = Quaternion.LookRotation(-facing, Vector3.up);   // forward = into the wall, the glass looks along 'facing'

        // frame, back panel and two legs (on the Default layer, so hands can grab / bump them)
        Color wood = new Color(0.28f, 0.17f, 0.09f), dark = new Color(0.12f, 0.08f, 0.05f);
        float t = 0.07f;
        Box("Top", new Vector3(0f, HalfH + t * 0.5f, -0.02f), new Vector3(2f * HalfW + 2f * t, t, 0.1f), wood);
        Box("Bottom", new Vector3(0f, -HalfH - t * 0.5f, -0.02f), new Vector3(2f * HalfW + 2f * t, t, 0.1f), wood);
        Box("Left", new Vector3(-HalfW - t * 0.5f, 0f, -0.02f), new Vector3(t, 2f * HalfH, 0.1f), wood);
        Box("Right", new Vector3(HalfW + t * 0.5f, 0f, -0.02f), new Vector3(t, 2f * HalfH, 0.1f), wood);
        Box("Back", Vector3.zero, new Vector3(2f * HalfW + 2f * t, 2f * HalfH + 2f * t, 0.06f), dark);
        float legTop = -0.2f, legBottom = -(BottomGap + HalfH);   // local heights: up to the panel, down to the floor
        Box("LegL", new Vector3(-0.4f, (legTop + legBottom) * 0.5f, 0.07f), new Vector3(0.07f, legTop - legBottom, 0.07f), dark);
        Box("LegR", new Vector3(0.4f, (legTop + legBottom) * 0.5f, 0.07f), new Vector3(0.07f, legTop - legBottom, 0.07f), dark);

        // the glass: a quad on its own layer (the mirror camera does not draw this layer). The picture is flipped left-right,
        // because the mirror camera looks at the player from the other side.
        var g = new GameObject("Glass");
        g.layer = GtagBootstrap.MirrorLayer;
        g.transform.SetParent(transform, false);
        g.transform.localPosition = new Vector3(0f, 0f, -0.032f);
        glass = g.transform;
        g.AddComponent<MeshFilter>().sharedMesh = QuadMesh();
        glassRenderer = g.AddComponent<MeshRenderer>();
        glassRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        glassRenderer.receiveShadows = false;

        rt = new RenderTexture(448, 576, 16);   // same shape as the glass
        rt.name = "MirrorTexture";
        rt.antiAliasing = 1;
        rt.filterMode = FilterMode.Bilinear;
        rt.Create();
        Material mat = MonkeModel.NewUnlitMaterial(Color.white);
        mat.mainTexture = rt;
        glassRenderer.sharedMaterial = mat;

        // the camera that draws the reflection. It is switched off and drawn by hand, only when needed.
        var cg = new GameObject("MirrorCamera");
        cg.transform.SetParent(transform, false);
        cam = cg.AddComponent<Camera>();
        cam.enabled = false;
        cam.targetTexture = rt;
        // everything, including your own whole body (hidden layer) - but not the glass itself, and not the head-less copy of your body
        cam.cullingMask = ~((1 << GtagBootstrap.MirrorLayer) | (1 << GtagBootstrap.FirstPersonLayer));
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = boot.Cam.backgroundColor;
        cam.farClipPlane = 300f;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.stereoTargetEye = StereoTargetEyeMask.None;        // a plain flat picture, even in a headset
        Debug.Log("[GtagTest] Mirror placed at " + transform.position + ", looking along " + facing);
    }

    void Box(string name, Vector3 localPos, Vector3 size, Color c)
    {
        var b = GameObject.CreatePrimitive(PrimitiveType.Cube);   // has a collider on the Default layer
        b.name = name;
        b.transform.SetParent(transform, false);
        b.transform.localPosition = localPos;
        b.transform.localScale = size;
        b.GetComponent<Renderer>().sharedMaterial = MonkeModel.NewMaterial(Color.white);   // primitives' default shader is stripped from builds -> magenta
        b.GetComponent<Renderer>().material.color = c;
    }

    static Mesh QuadMesh()
    {
        var m = new Mesh();
        m.name = "MirrorGlass";
        m.vertices = new[]
        {
            new Vector3(-HalfW, -HalfH, 0f), new Vector3(-HalfW, HalfH, 0f),
            new Vector3(HalfW, HalfH, 0f), new Vector3(HalfW, -HalfH, 0f)
        };
        m.uv = new[] { new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f) };   // u flipped
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // ---------------- reflection ----------------

    void LateUpdate()
    {
        if (boot == null || boot.Cam == null || cam == null) return;
        glassRenderer.enabled = PlayerProfile.MirrorOn;      // off = just the dark back panel
        if (!PlayerProfile.MirrorOn) return;

        Vector3 gp = glass.position;
        Vector3 n = -glass.forward;                          // out of the glass, towards the player
        Vector3 eye = boot.Head.position;
        float d = Vector3.Dot(eye - gp, n);                  // how far in front of the glass your eye is
        if (d < 0.1f) return;                                // beside or behind it
        Vector3 toMirror = gp - eye;
        if (toMirror.sqrMagnitude > ShowRange * ShowRange) return;
        if (Vector3.Dot(boot.Head.forward, toMirror.normalized) < -0.25f) return;   // you are looking away

        // the eye as seen in the glass: the same distance behind it
        Vector3 reflectedEye = eye - 2f * d * n;
        Quaternion rot = Quaternion.LookRotation(n, Vector3.up);
        cam.transform.SetPositionAndRotation(reflectedEye, rot);

        // the view is exactly the window of the glass seen from the reflected eye (an off-axis frustum), nearest plane = the glass,
        // so the wall and everything behind the mirror is cut away
        Vector3 right = rot * Vector3.right, up = rot * Vector3.up;
        float l = float.MaxValue, r = float.MinValue, bt = float.MaxValue, tp = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Vector3 corner = gp + glass.right * (HalfW * ((i & 1) == 0 ? -1f : 1f)) + glass.up * (HalfH * ((i & 2) == 0 ? -1f : 1f));
            Vector3 a = corner - reflectedEye;
            float x = Vector3.Dot(a, right), y = Vector3.Dot(a, up);
            l = Mathf.Min(l, x); r = Mathf.Max(r, x);
            bt = Mathf.Min(bt, y); tp = Mathf.Max(tp, y);
        }
        cam.projectionMatrix = Frustum(l, r, bt, tp, d, cam.farClipPlane);
        cam.backgroundColor = boot.Cam.backgroundColor;      // follows Darkness
        cam.Render();
    }

    static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
    {
        var m = new Matrix4x4();
        m[0, 0] = 2f * n / (r - l); m[0, 2] = (r + l) / (r - l);
        m[1, 1] = 2f * n / (t - b); m[1, 2] = (t + b) / (t - b);
        m[2, 2] = -(f + n) / (f - n); m[2, 3] = -2f * f * n / (f - n);
        m[3, 2] = -1f;
        return m;
    }

    void OnDestroy()
    {
        if (rt != null) { rt.Release(); Destroy(rt); }
    }
}
