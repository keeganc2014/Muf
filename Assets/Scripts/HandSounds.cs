using UnityEngine;

// Marks a piece of the map with what each of its sub-meshes is made of (set by PackedMesh from Tools/pack_assets.py).
public class SurfaceInfo : MonoBehaviour
{
    public const byte Concrete = 0, Wood = 1, Grass = 2, Metal = 3, Ice = 4;
    public const float IceSlip = 0.97f;   // how freely a hand slides along ice (normal ground: 0.03). 1 = no grip at all.
    public byte[] kinds;
    public bool hasIce;                   // set by PackedMesh: some sub-mesh of this piece is ice (so only these pieces pay for the lookup)

    // true when the triangle this hit touched is ice (the light / dark blue of the mountains)
    public static bool IsIce(RaycastHit hit)
    {
        Collider col = hit.collider;
        if (col == null) return false;
        SurfaceInfo si = col.GetComponent<SurfaceInfo>();
        if (si == null || !si.hasIce) return false;
        return Classify(hit) == Ice;
    }

    // what is at this hit: looks up the sub-mesh (= material) of the triangle that was touched
    public static byte Classify(RaycastHit hit)
    {
        Collider col = hit.collider;
        if (col == null) return Concrete;
        SurfaceInfo si = col.GetComponent<SurfaceInfo>();
        if (si == null || si.kinds == null || si.kinds.Length == 0) return Concrete;
        if (si.kinds.Length == 1) return si.kinds[0];
        var mc = col as MeshCollider;
        if (mc == null || mc.sharedMesh == null) return si.kinds[0];

        int tri = hit.triangleIndex;
        if (tri < 0)
        {
            // sphere casts do not always report the triangle: look again with a ray straight into the surface
            RaycastHit rh;
            if (col.Raycast(new Ray(hit.point + hit.normal * 0.06f, -hit.normal), out rh, 0.2f)) tri = rh.triangleIndex;
            if (tri < 0) return si.kinds[0];
        }
        Mesh m = mc.sharedMesh;
        int start = tri * 3;
        for (int s = 0; s < si.kinds.Length && s < m.subMeshCount; s++)
        {
            var sm = m.GetSubMesh(s);
            if (start >= sm.indexStart && start < sm.indexStart + sm.indexCount) return si.kinds[s];
        }
        return si.kinds[0];
    }
}

// Plays a sound when one of your hands hits something: stone/concrete (default, also ice), wood/bark, grass/leaves or metal.
// Harder, faster hits are louder. Sounds: Assets/Resources/Audio/hit_concrete.mp3, hit_wood.mp3, hit_grass.mp3, hit_metal.mp3.
public class HandSounds : MonoBehaviour
{
    AudioClip concrete, wood, grass, metal;
    AudioSource leftSrc, rightSrc;
    float lastL = -1f, lastR = -1f;
    const float Cooldown = 0.08f;

    public static void Create(GtagBootstrap boot, Transform leftHand, Transform rightHand)
    {
        var go = new GameObject("HandSounds");
        go.transform.SetParent(boot.Player.transform, false);
        var hs = go.AddComponent<HandSounds>();
        hs.concrete = Resources.Load<AudioClip>("Audio/hit_concrete");
        hs.wood = Resources.Load<AudioClip>("Audio/hit_wood");
        hs.grass = Resources.Load<AudioClip>("Audio/hit_grass");
        hs.metal = Resources.Load<AudioClip>("Audio/hit_metal");
        hs.leftSrc = MakeSource(leftHand);
        hs.rightSrc = MakeSource(rightHand);
        boot.Player.HandHit += hs.OnHit;
    }

    static AudioSource MakeSource(Transform hand)
    {
        var a = hand.gameObject.AddComponent<AudioSource>();
        a.playOnAwake = false;
        a.spatialBlend = 0.5f;
        a.minDistance = 1f;
        a.maxDistance = 20f;
        return a;
    }

    void OnHit(bool left, RaycastHit hit, float speed)
    {
        float now = Time.time;
        if (left) { if (now - lastL < Cooldown) return; lastL = now; }
        else { if (now - lastR < Cooldown) return; lastR = now; }

        byte kind = SurfaceInfo.Classify(hit);
        AudioClip clip = kind == SurfaceInfo.Wood ? wood : kind == SurfaceInfo.Grass ? grass : kind == SurfaceInfo.Metal ? metal : concrete;
        if (clip == null) clip = concrete;
        if (clip == null) return;
        AudioSource src = left ? leftSrc : rightSrc;
        src.pitch = Random.Range(0.96f, 1.04f);
        src.PlayOneShot(clip, Mathf.Clamp(0.45f + speed * 0.12f, 0.45f, 1f));
    }
}
