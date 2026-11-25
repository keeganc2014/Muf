using UnityEngine;

// A monster that walks over the floor on its hands. There are two of them (GtagBootstrap makes both):
//   Forest = Timmy (Resources/Creatures/timmy.bytes), wanders the forest and the rest of the map but never goes into the caves.
//   Cave   = the spider (Resources/Creatures/spider.bytes), lives in the caves and never leaves them.
// It wanders (walks to a spot, looks around for a moment, walks to the next one) until it sees you, then chases
// until it loses you for a few seconds. It never climbs onto things above it and never leaves the ground.
// How far it notices you is set by SightRange / CloseRange below (smaller = easier to sneak past).
public class Monster : MonoBehaviour
{
    public enum Kind { Forest, Cave }

    public bool frozen;                 // "Freeze Monster" mod
    public Kind kind;

    const float Radius = 0.45f;
    const float ForestSightRange = 20f; // how far Timmy can see you (needs a clear line, in front of him)
    const float ForestCloseRange = 6f;  // he always notices you this close, even from behind or through a wall
    const float CaveSightRange = 14f;   // the spider in the tight caves
    const float CaveCloseRange = 5f;
    const float ViewAngle = 170f;       // its field of view
    const float WanderSpeed = 0.45f;    // wandering speed, as a share of its chase speed
    const float LoseTime = 6f;          // seconds after losing sight before it gives up
    const float StepUp = 0.6f;          // tallest step it can walk up
    const float KillDistance = 1.0f;    // horizontal reach
    const float Gravity = 20f;

    GtagBootstrap boot;
    Transform visual;
    CreatureBody corrupt;               // the body (named after the old corrupted monke it replaced)
    float SightRange { get { return kind == Kind.Cave ? CaveSightRange : ForestSightRange; } }
    float CloseRange { get { return kind == Kind.Cave ? CaveCloseRange : ForestCloseRange; } }
    float bodyHeight = 2f;
    float grace, loseTimer, fallSpeed, scanDir = 1f;
    bool chasing;
    Vector3 wanderTarget, progressPos, lastMoveDir = Vector3.forward;
    bool hasTarget;
    float pauseTimer, stuckTimer;

    public static Monster Create(GtagBootstrap b, Kind kind)
    {
        var go = new GameObject(kind == Kind.Cave ? "CaveMonster" : "Monster");
        go.layer = GtagBootstrap.PlayerLayer;
        var m = go.AddComponent<Monster>();
        m.boot = b;
        m.kind = kind;
        m.Build();
        return m;
    }

    static bool InCave(Collider c) { return c != null && c.name.StartsWith("cave"); }
    bool RegionOk(Collider c) { return kind == Kind.Cave ? InCave(c) : !InCave(c); }

    // The monster's origin is at its feet.
    void Build()
    {
        visual = new GameObject("Visual").transform;
        visual.SetParent(transform, false);
        bodyHeight = kind == Kind.Cave ? 1.7f : 2.1f;
        corrupt = CreatureBody.Create(visual, kind == Kind.Cave ? "Creatures/spider" : "Creatures/timmy", bodyHeight);
        if (corrupt == null) BuildFallbackBody();   // model file missing: the old shadow shape
        BuildGlow();
    }

    void BuildFallbackBody()
    {
        Color dark = new Color(0.03f, 0.02f, 0.04f);
        Color red = new Color(1f, 0.05f, 0.05f);

        Part(PrimitiveType.Capsule, new Vector3(0f, 0.95f, 0f), new Vector3(0.9f, 0.95f, 0.7f), dark);
        Part(PrimitiveType.Sphere, new Vector3(0f, 1.8f, 0.05f), new Vector3(0.55f, 0.6f, 0.55f), dark);
        Part(PrimitiveType.Sphere, new Vector3(-0.13f, 1.85f, 0.27f), Vector3.one * 0.11f, red);
        Part(PrimitiveType.Sphere, new Vector3(0.13f, 1.85f, 0.27f), Vector3.one * 0.11f, red);
        Part(PrimitiveType.Capsule, new Vector3(-0.6f, 1.0f, 0.15f), new Vector3(0.2f, 0.7f, 0.2f), dark).localRotation = Quaternion.Euler(25f, 0f, 12f);
        Part(PrimitiveType.Capsule, new Vector3(0.6f, 1.0f, 0.15f), new Vector3(0.2f, 0.7f, 0.2f), dark).localRotation = Quaternion.Euler(25f, 0f, -12f);

    }

    void BuildGlow()
    {
        var lightGo = new GameObject("Glow");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = new Vector3(0f, bodyHeight * 0.85f, bodyHeight * 0.3f);
        var l = lightGo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = kind == Kind.Cave ? new Color(0.7f, 0.2f, 1f) : new Color(1f, 0.1f, 0.1f);
        l.range = 5f;
        l.intensity = 1.0f;
        l.shadows = LightShadows.None;
        if (corrupt != null) corrupt.glow = l;
    }

    Transform Part(PrimitiveType type, Vector3 pos, Vector3 scale, Color c)
    {
        var p = GameObject.CreatePrimitive(type);
        Destroy(p.GetComponent<Collider>());
        p.layer = GtagBootstrap.PlayerLayer;
        p.transform.SetParent(visual, false);
        p.transform.localPosition = pos;
        p.transform.localScale = scale;
        p.GetComponent<Renderer>().sharedMaterial = MonkeModel.NewMaterial(Color.white);   // primitives' default shader is stripped from builds -> magenta
        p.GetComponent<Renderer>().material.color = c;
        return p.transform;
    }

    public void SetActive(bool on)
    {
        gameObject.SetActive(on);
        if (on) Respawn();
    }

    // Back at the gazebo, on the ground, a few seconds of mercy and not chasing yet.
    public void Respawn()
    {
        transform.position = boot.MonsterHome(kind);
        transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        grace = 5f;
        chasing = false;
        loseTimer = 0f;
        fallSpeed = 0f;
        hasTarget = false;
        pauseTimer = Random.Range(1f, 3f);
    }

    void Update()
    {
        if (boot == null || boot.Player == null || !boot.Player.enabled) return;
        float dt = Time.deltaTime;
        if (grace > 0f) grace -= dt;

        Vector3 startPos = transform.position;
        Vector3 target = boot.Head.position;
        Vector3 toFlat = new Vector3(target.x - startPos.x, 0f, target.z - startPos.z);
        float flatDist = toFlat.magnitude;

        // ---- kill: only if you are within its reach (a player up a tree is out of reach) ----
        float dy = target.y - startPos.y;
        if (grace <= 0f && flatDist < KillDistance && dy > -0.3f && dy < 2.4f && !boot.Invincible && !boot.IsDying)
        {
            boot.Die();
            return;
        }

        // ---- senses and movement ----
        if (!frozen && !boot.IsDying)
        {
            if (CanSee(startPos, target, toFlat, flatDist)) { chasing = true; loseTimer = LoseTime; }
            else if (chasing)
            {
                loseTimer -= dt;
                if (loseTimer <= 0f) { chasing = false; hasTarget = false; pauseTimer = 0f; }
            }

            if (chasing && flatDist > 0.05f)
            {
                float speed = PlayerProfile.MonsterSpeed * (grace > 0f ? 0.5f : 1f);
                bool moved = Walk(toFlat / flatDist, speed * dt);
                // look where it is going, but right at you when you are close
                if (flatDist < 3f) FaceTowards(toFlat);
                else if (moved) FaceTowards(lastMoveDir);
                else FaceTowards(toFlat);
            }
            else
            {
                Wander(dt);
            }
        }

        // how far it really moved this frame drives its hand steps
        Vector3 now = transform.position;
        float movedMeters = Mathf.Sqrt((now.x - startPos.x) * (now.x - startPos.x) + (now.z - startPos.z) * (now.z - startPos.z));
        if (corrupt != null)
        {
            corrupt.chasing = chasing && !frozen;
            corrupt.AddDistance(movedMeters);
        }

        Ground(dt);
        if (corrupt == null)   // the old shape bobs; the creature body bobs by itself with its steps
            visual.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(Time.time * (chasing ? 6f : 1.5f))) * (chasing ? 0.06f : 0.02f), 0f);
    }

    // Not hunting: walk to a spot somewhere on the map, look around for a few seconds, pick the next spot.
    void Wander(float dt)
    {
        if (pauseTimer > 0f)
        {
            pauseTimer -= dt;
            transform.Rotate(0f, 22f * scanDir * dt, 0f);      // looks around while it waits
            if (Random.value < 0.4f * dt) scanDir = -scanDir;
            return;
        }

        Vector3 pos = transform.position;
        if (!hasTarget)
        {
            hasTarget = PickWanderTarget();
            stuckTimer = 0f;
            progressPos = pos;
            if (!hasTarget) { pauseTimer = 1f; return; }
        }

        Vector3 to = wanderTarget - pos;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist < 1.5f)                                        // arrived
        {
            hasTarget = false;
            pauseTimer = Random.Range(1.5f, 4f);
            return;
        }

        bool moved = Walk(to / dist, PlayerProfile.MonsterSpeed * WanderSpeed * dt);
        if (moved) FaceTowards(lastMoveDir);

        // not getting anywhere (wall, tree, ledge): choose somewhere else instead of walking into it
        stuckTimer += dt;
        if (stuckTimer >= 1.5f)
        {
            Vector3 d = pos - progressPos;
            d.y = 0f;
            if (d.sqrMagnitude < 0.5f * 0.5f) hasTarget = false;
            progressPos = pos;
            stuckTimer = 0f;
        }
    }

    // Mostly short trips (8-30 m) over open ground on its own level, now and then a long trip across its home area
    // (the whole map for Timmy, the caves for the spider).
    bool PickWanderTarget()
    {
        Vector3 pos = transform.position;
        for (int i = 0; i < 14; i++)
        {
            if (i >= 10)
            {
                wanderTarget = kind == Kind.Cave ? boot.FindCaveFloor() : boot.FindForestFloor();   // far away: if it cannot get there it will notice and pick again
                return true;
            }
            float ang = Random.Range(0f, 360f);
            float rad = kind == Kind.Cave ? Random.Range(5f, 20f) : Random.Range(8f, 30f);
            Vector3 p = pos + Quaternion.Euler(0f, ang, 0f) * Vector3.forward * rad;
            RaycastHit hit;
            if (!Physics.Raycast(new Vector3(p.x, pos.y + 6f, p.z), Vector3.down, out hit, 14f, 1 << 0, QueryTriggerInteraction.Ignore)) continue;
            if (hit.normal.y < 0.8f) continue;                              // slope
            if (!RegionOk(hit.collider)) continue;                          // Timmy stays out of the caves, the spider stays in
            if (Mathf.Abs(hit.point.y - pos.y) > 3f) continue;              // another level / a roof
            if (Physics.Raycast(hit.point + Vector3.up * 0.1f, Vector3.up, 4f, 1 << 0, QueryTriggerInteraction.Ignore)) continue;   // under a roof or branch
            wanderTarget = hit.point;
            return true;
        }
        return false;
    }

    bool CanSee(Vector3 pos, Vector3 target, Vector3 toFlat, float flatDist)
    {
        Vector3 eye = pos + Vector3.up * 1.9f;                  // its head is about 1.9 m up
        if (flatDist < CloseRange && Mathf.Abs(target.y - pos.y) < 4f) return true;   // too close to miss, even through a wall (but not from another floor)
        float dist = Vector3.Distance(eye, target);
        if (dist > SightRange) return false;
        if (Vector3.Angle(transform.forward, toFlat) > ViewAngle * 0.5f) return false;   // outside its field of view
        // a clear line to your head or to your chest (no wall/tree in between)
        if (!Physics.Linecast(eye, target, 1 << 0, QueryTriggerInteraction.Ignore)) return true;
        return !Physics.Linecast(eye, target - Vector3.up * 0.5f, 1 << 0, QueryTriggerInteraction.Ignore);
    }

    void FaceTowards(Vector3 flatDir)
    {
        if (flatDir.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(flatDir), 8f * Time.deltaTime);
    }

    // Walk along the ground. Steps around obstacles, will not climb steep things or step off big drops.
    bool Walk(Vector3 dir, float step)
    {
        float[] turns = { 0f, 40f, -40f, 80f, -80f, 120f, -120f };
        foreach (float t in turns)
        {
            Vector3 d = Quaternion.Euler(0f, t, 0f) * dir;
            if (TryStep(d, step)) { lastMoveDir = d; return true; }
        }
        return false;
    }

    bool TryStep(Vector3 d, float step)
    {
        Vector3 pos = transform.position;

        // wall check at knee/chest height
        RaycastHit hit;
        Vector3 origin = pos + Vector3.up * (StepUp + Radius);
        if (Physics.SphereCast(origin, Radius * 0.9f, d, out hit, step + 0.15f, 1 << 0, QueryTriggerInteraction.Ignore))
            return false;

        // ground at the new spot - the ray starts only just above the feet, so a roof or branch overhead is never hit
        Vector3 np = pos + d * step;
        if (!Physics.Raycast(np + Vector3.up * StepUp, Vector3.down, out hit, StepUp + 1.2f, 1 << 0, QueryTriggerInteraction.Ignore))
            return false;                                       // a drop - do not walk off
        if (hit.normal.y < 0.55f) return false;                 // too steep
        if (!RegionOk(hit.collider)) return false;              // would leave its own area (forest <-> caves)
        np.y = pos.y;
        transform.position = np;
        return true;
    }

    // Keep the hands on the floor below. Rises smoothly up gentle slopes, falls with gravity, and never hangs in the air:
    // if there is no floor close below it looks all the way down, and if there is none at all it goes back home.
    void Ground(float dt)
    {
        Vector3 pos = transform.position;
        RaycastHit hit;
        bool found = Physics.Raycast(pos + Vector3.up * StepUp, Vector3.down, out hit, StepUp + 6f, 1 << 0, QueryTriggerInteraction.Ignore)
                  || Physics.Raycast(pos + Vector3.up * 0.2f, Vector3.down, out hit, 500f, 1 << 0, QueryTriggerInteraction.Ignore);
        if (!found)
        {
            Respawn();                                          // fell out of the map
            return;
        }
        float gy = hit.point.y;
        if (gy >= pos.y)
        {
            pos.y = Mathf.MoveTowards(pos.y, gy, 6f * dt);
            fallSpeed = 0f;
        }
        else
        {
            fallSpeed += Gravity * dt;
            pos.y = Mathf.Max(gy, pos.y - fallSpeed * dt);
            if (pos.y <= gy) fallSpeed = 0f;
        }
        transform.position = pos;
    }
}
