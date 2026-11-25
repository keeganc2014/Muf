using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using GorillaLocomotion;

// Uses the ModMenuV2 "Monkey Menu Pro" prefab (Resources/ModMenuV2/MonkeyMenuLite) as the menu, worn on your LEFT HAND.
// Left X opens/closes it. A small ball floats in front of your RIGHT hand: touch a button with the ball to press it.
// Desktop: Tab opens it, aim + left click.
// The 5 button slots of the prefab's page 1 are re-used for every page: pages 1-2 are the original ModMenuV2 mods,
// the next pages are your own mods, player / settings and PlayFab. Page buttons at the bottom: Prev / Next.
public class MainMenu : MonoBehaviour
{
    class Btn
    {
        public GameObject go;
        public Material mat;
        public TMP_Text label;
        public Func<string> text;
        public Func<bool> isOn;
        public Action click;
        public float lastPress;
    }

    class Item
    {
        public Func<string> text;
        public Func<bool> isOn;
        public Action click;
    }

    const int PerPage = 5;
    const float MenuScale = 0.6f;       // the prefab is 33 x 47 cm; this makes it about 20 x 28 cm on your hand
    const float PressCooldown = 0.45f;
    const float BallRadius = 0.018f;

    GtagBootstrap boot;
    XRRigInput rig;
    Player player;
    Rigidbody body;

    GameObject root;
    Material plateMat;
    readonly List<Item> items = new List<Item>();
    readonly Btn[] slots = new Btn[PerPage];
    readonly Dictionary<Collider, Btn> byCollider = new Dictionary<Collider, Btn>();
    Btn navPrev, navNext, hover;
    int pageIdx;
    bool open, built;
    Transform ball;

    // colors (the prefab's own materials use a URP shader graph, which this built-in pipeline project cannot draw)
    static readonly Color cPlate = new Color(0.24f, 0.26f, 0.88f);
    static readonly Color cButton = new Color(0.06f, 0.06f, 0.09f);
    static readonly Color cOn = new Color(0.10f, 0.62f, 0.25f);
    static readonly Color cHover = new Color(0.38f, 0.38f, 0.60f);

    // moderator (kept for Teleport All; the rest of the host commands are not on the menu)
    int targetIdx;

    // mod state
    int speedIdx, gravIdx, armIdx;
    bool fly, noclip, platforms, flashlight, darkness, freeze, god, rainbow, ghost, car;
    bool hasSaved;
    Vector3 savedRig;
    readonly GameObject[] plat = new GameObject[2];
    Light torch;
    float rainbowNext;

    static readonly string[] SpeedNames = { "Normal", "Fast", "Super" };
    static readonly string[] GravNames = { "Normal", "Half", "Moon" };
    static readonly float[] JumpMult = { 1.1f, 1.3f, 1.5f };
    static readonly float[] MaxJump = { 6.5f, 9f, 13f };
    static readonly float[] GravScale = { 1f, 0.5f, 0.25f };
    static readonly float[] ArmScales = { 1f, 1.5f, 2f, 3f };
    const float BaseArmLength = 1.5f;
    const float FlySpeed = 6f;
    const float CarAccel = 10f;

    void Start()
    {
        boot = GtagBootstrap.Instance;
        rig = boot.Rig;
        player = boot.Player;
        body = boot.Body;

        BuildItems();
        BuildBall();
        BuildTorch();
        // The prefab / TextMeshPro are NOT touched here: the menu is built the first time you open it (EnsureBuilt),
        // so nothing about the menu can slow down or break the start of the game.
    }

    bool tried;

    void EnsureBuilt()
    {
        if (tried) return;
        tried = true;
        try
        {
            built = BuildPanel();
            PlayFabLite.Create();
        }
        catch (Exception e)
        {
            Debug.LogWarning("Menu: could not be built: " + e);
            built = false;
        }
        if (!built && root != null) { Destroy(root); root = null; }
    }

    // ---------------- the list of buttons (5 per page) ----------------

    void Add(Func<string> text, Func<bool> isOn, Action click)
    {
        items.Add(new Item { text = text, isOn = isOn, click = click });
    }

    void BuildItems()
    {
        // page 1 - ModMenuV2 (same order as the prefab)
        Add(() => "Platforms", () => platforms, () => { platforms = !platforms; if (!platforms) ClearPlatforms(); });
        Add(() => "Ghost monke", () => ghost, () => SetGhost(!ghost));
        Add(() => "Gorrila Car", () => car, () => SetCar(!car));
        Add(() => "Rainbow Color", () => rainbow, () => SetRainbow(!rainbow));
        Add(() => "Fly", () => fly, () => { fly = !fly; });
        // page 2 - ModMenuV2 page 2, then your mods
        Add(() => "Noclip", () => noclip, () => SetNoclip(!noclip));
        Add(() => "Teleport All", null, TeleportAll);
        Add(() => "Long Arms " + ArmScales[armIdx] + "x", () => armIdx > 0, () => { armIdx = (armIdx + 1) % ArmScales.Length; ApplyArms(); });
        Add(() => "Speed " + SpeedNames[speedIdx], () => speedIdx > 0, () => { speedIdx = (speedIdx + 1) % 3; ApplySpeed(); });
        Add(() => "Gravity " + GravNames[gravIdx], () => gravIdx > 0, () => { gravIdx = (gravIdx + 1) % 3; ApplyGravity(); });
        // page 3
        Add(() => "Invincible", () => god, () => { god = !god; boot.Invincible = god; });
        Add(() => "Freeze Monster", () => freeze, () => { freeze = !freeze; ApplyFreeze(); });
        Add(() => "Flashlight", () => flashlight, () => { flashlight = !flashlight; torch.enabled = flashlight; });
        Add(() => "Darkness", () => darkness, () => SetDarkness(!darkness));
        Add(() => "Random Spot", null, () => boot.TeleportToGround(boot.FindForestFloor()));
        // page 4
        Add(() => hasSaved ? "Save Pos set" : "Save Pos", () => hasSaved, () => { savedRig = player.transform.position; hasSaved = true; });
        Add(() => "Load Pos", null, () => { if (hasSaved) boot.MoveRig(savedRig); });
        Add(() => "Go to Stump", null, () => boot.TeleportToSpawn());
        Add(() => "Reset Mods", null, ResetMods);
        Add(() => "Name " + PlayerProfile.Name, null, () => { PlayerProfile.NameIdx = PlayerProfile.NameIdx + 1; if (PlayFabLite.Instance != null) PlayFabLite.Instance.SyncName(); });
        // page 5
        Add(() => "Color " + PlayerProfile.ColorNames[PlayerProfile.ColorIdx], null, () => PlayerProfile.ColorIdx = PlayerProfile.ColorIdx + 1);
        Add(() => "Turn " + PlayerProfile.TurnNames[PlayerProfile.TurnIdx], null, () => PlayerProfile.TurnIdx = PlayerProfile.TurnIdx + 1);
        Add(() => "Monster " + (PlayerProfile.MonsterOn ? "ON" : "OFF"), () => PlayerProfile.MonsterOn, ToggleMonster);
        Add(() => "Monster " + PlayerProfile.MonsterNames[PlayerProfile.MonsterSpeedIdx], null, () => PlayerProfile.MonsterSpeedIdx = PlayerProfile.MonsterSpeedIdx + 1);
        Add(() => "Mirror", () => PlayerProfile.MirrorOn, () => PlayerProfile.MirrorOn = !PlayerProfile.MirrorOn);
        // page 6
        Add(() => OnlineManager.Instance != null && OnlineManager.Instance.MicMuted ? "Mic MUTED" : "Mic ON", () => OnlineManager.Instance != null && !OnlineManager.Instance.MicMuted, () => { if (OnlineManager.Instance != null) OnlineManager.Instance.ToggleMic(); });
        Add(() => "Reconnect", null, () => { if (OnlineManager.Instance != null) OnlineManager.Instance.Reconnect(); });
        Add(() => "Reset Board", null, () => Scores.Clear());
        Add(() => "PlayFab Sign In", () => PlayFabLite.Instance != null && PlayFabLite.Instance.LoggedIn, () => { if (PlayFabLite.Instance != null) PlayFabLite.Instance.Login(); });
        Add(() => "PlayFab Send Time", null, () => { if (PlayFabLite.Instance != null) PlayFabLite.Instance.SubmitBestTime(); });
        // page 7
        Add(() => "PlayFab Send Name", null, () => { if (PlayFabLite.Instance != null) PlayFabLite.Instance.SyncName(); });
        Add(() => PlayFabLite.Instance != null ? PlayFabLite.Instance.Status : "PlayFab off", null, null);
    }

    int PageCount() { return Mathf.Max(1, Mathf.CeilToInt(items.Count / (float)PerPage)); }

    // ---------------- the prefab ----------------

    bool BuildPanel()
    {
        GameObject prefab = null;
        TMP_FontAsset daydream = null;
        try
        {
            daydream = Resources.Load<TMP_FontAsset>("ModMenuV2/Daydream DEMO SDF");
            if (daydream != null && EnsureTmpSettings(daydream))
                prefab = Resources.Load<GameObject>("ModMenuV2/MonkeyMenuLite");
        }
        catch (Exception e) { Debug.LogWarning("Menu: TextMeshPro is not ready: " + e.Message); }
        if (prefab == null)
        {
            Debug.LogWarning("Menu: could not load Resources/ModMenuV2 (font or prefab missing, or TextMeshPro could not start).");
            return false;
        }

        root = Instantiate(prefab);
        root.name = "MonkeyMenuLite";
        foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true)) t.font = daydream;   // every text uses the allowed font
        SetLayer(root.transform, GtagBootstrap.MenuLayer);
        root.transform.localScale = Vector3.one * MenuScale;

        Transform mm = root.transform.Find("ModManagers");
        if (mm != null) mm.gameObject.SetActive(false);   // the original mod scripts (Photon / EasyInputs) are replaced by the mods below
        Transform menu = root.transform.Find("Menu");
        if (menu == null) { Debug.LogWarning("Menu: prefab has no 'Menu' object."); return false; }
        Transform page2 = menu.Find("Page2");
        if (page2 != null) page2.gameObject.SetActive(false);

        plateMat = MonkeModel.NewMaterial(cPlate);
        var plateRend = menu.GetComponent<MeshRenderer>();
        if (plateRend != null) plateRend.sharedMaterial = plateMat;

        string[] names = { "Platformbutton", "GhostMonkebutton", "GorillaCarButton", "Rainbow Color", "Fly" };
        for (int i = 0; i < PerPage; i++)
        {
            Transform t = menu.Find("Page1/Buttons/" + names[i]);
            if (t == null) { Debug.LogWarning("Menu: button not found: " + names[i]); return false; }
            slots[i] = MakeBtn(t);
        }

        Transform next = menu.Find("PageButtons/Page2Button");
        Transform prev = menu.Find("PageButtons/Page1Button");
        if (next == null || prev == null) { Debug.LogWarning("Menu: page buttons not found."); return false; }
        prev.gameObject.SetActive(true);
        next.gameObject.SetActive(true);
        Place(next, 0.25f);
        Place(prev, -0.25f);
        navNext = MakeBtn(next);
        navPrev = MakeBtn(prev);
        navNext.text = () => "Next Page";
        navNext.click = () => pageIdx = (pageIdx + 1) % PageCount();
        navPrev.text = () => "Prev Page";
        navPrev.click = () => pageIdx = (pageIdx + PageCount() - 1) % PageCount();

        // a little light so the menu stays readable in Darkness
        var lg = new GameObject("MenuLight");
        lg.transform.SetParent(root.transform, false);
        lg.transform.localPosition = new Vector3(-0.5f, 0f, 0f);
        var l = lg.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = 1.2f;
        l.intensity = 1.1f;
        l.shadows = LightShadows.None;
        return true;
    }

    // TextMeshPro normally needs its "TMP Settings" asset (from the Essential Resources import). On a Cloud Build that import has not
    // been done, so when the asset is missing we create the settings in memory and point them at the Daydream font.
    static bool EnsureTmpSettings(TMP_FontAsset font)
    {
        const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        FieldInfo inst = typeof(TMP_Settings).GetField("s_Instance", any);
        if (inst == null) return TMP_Settings.instance != null;
        if (inst.GetValue(null) as TMP_Settings != null) return true;

        var real = Resources.Load<TMP_Settings>("TMP Settings");
        if (real != null) { inst.SetValue(null, real); return true; }

        var made = ScriptableObject.CreateInstance<TMP_Settings>();
        FieldInfo def = typeof(TMP_Settings).GetField("m_defaultFontAsset", any);
        if (def != null) def.SetValue(made, font);
        FieldInfo wrap = typeof(TMP_Settings).GetField("m_enableWordWrapping", any);
        if (wrap != null) wrap.SetValue(made, true);
        inst.SetValue(null, made);
        return true;
    }

    // The Daydream demo font only has: letters, digits, space and  ! " ' , . / ?  - everything else is turned into a space.
    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || " !\"',./?".IndexOf(c) >= 0;
            char o = ok ? c : ' ';
            if (o == ' ' && sb.Length > 0 && sb[sb.Length - 1] == ' ') continue;
            sb.Append(o);
        }
        return sb.ToString().Trim();
    }

    static void Place(Transform t, float z)
    {
        Vector3 p = t.localPosition; p.z = z; t.localPosition = p;
        Vector3 s = t.localScale; s.z = 0.36f; t.localScale = s;
    }

    Btn MakeBtn(Transform t)
    {
        var b = new Btn { go = t.gameObject };
        var r = t.GetComponent<MeshRenderer>();
        if (r != null)
        {
            b.mat = MonkeModel.NewMaterial(cButton);
            r.sharedMaterial = b.mat;
        }
        b.label = t.GetComponentInChildren<TMP_Text>(true);
        if (b.label != null)
        {
            b.label.richText = false;
            b.label.enableAutoSizing = true;
            b.label.fontSizeMin = 6f;
            b.label.fontSizeMax = 36f;
        }
        Collider c = t.GetComponent<Collider>();
        if (c == null) c = t.gameObject.AddComponent<BoxCollider>();
        c.isTrigger = true;
        byCollider[c] = b;
        return b;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    // The small ball in front of your right hand, same blue as the menu plate. Touch a button with it to press it.
    void BuildBall()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "MenuBall";
        Destroy(go.GetComponent<Collider>());
        go.transform.localScale = Vector3.one * BallRadius * 2f;
        go.GetComponent<Renderer>().sharedMaterial = MonkeModel.NewMaterial(cPlate);
        ball = go.transform;
        go.SetActive(false);
    }

    void BuildTorch()
    {
        var go = new GameObject("Flashlight");
        go.transform.SetParent(boot.Head, false);
        torch = go.AddComponent<Light>();
        torch.type = LightType.Spot;
        torch.range = 35f;
        torch.spotAngle = 60f;
        torch.intensity = 3f;
        torch.shadows = LightShadows.None;
        torch.enabled = false;
    }

    // ---------------- update ----------------

    void Update()
    {
        if (boot == null || !player.enabled) return;

        if (rig.MenuTogglePressed) SetOpen(!open);
        if (boot.IsDying && open) SetOpen(false);

        UpdateMenu();
        UpdateFly();
        UpdateCar();
        UpdatePlatforms();
        UpdateRainbow();
    }

    void SetOpen(bool value)
    {
        open = value;
        if (open) EnsureBuilt();
        if (built) root.SetActive(open);
        if (ball != null) ball.gameObject.SetActive(open && rig.XRActive);
        if (open) pageIdx = 0;
        hover = null;
    }

    void UpdateMenu()
    {
        if (!open || !built) return;

        // worn on the left hand; the front of the prefab is its -X side, which is turned toward your head
        Vector3 headPos = boot.Head.position;
        Vector3 pos;
        if (rig.XRActive)
        {
            Vector3 lp = boot.LeftHand.position;
            pos = lp + boot.LeftHand.up * 0.08f + (headPos - lp).normalized * 0.05f;
        }
        else
        {
            pos = headPos + boot.Head.forward * 0.6f + boot.Head.right * -0.12f + boot.Head.up * -0.05f;
        }
        Vector3 away = (pos - headPos).normalized;
        Vector3 up = Vector3.ProjectOnPlane(boot.Head.up, away).normalized;
        root.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.Cross(away, up), up));

        // the ball floats in front of the right controller; on desktop you aim with the view instead
        Vector3 pointer = Vector3.zero;
        if (rig.XRActive)
        {
            pointer = boot.RightHand.position + boot.RightHand.forward * 0.09f;
            ball.position = pointer;
            if (!ball.gameObject.activeSelf) ball.gameObject.SetActive(true);
        }
        else if (ball.gameObject.activeSelf) ball.gameObject.SetActive(false);

        Collider hit = null;
        if (rig.XRActive)
        {
            Collider[] hits = Physics.OverlapSphere(pointer, BallRadius, 1 << GtagBootstrap.MenuLayer, QueryTriggerInteraction.Collide);
            if (hits.Length > 0) hit = hits[0];
        }
        else
        {
            RaycastHit rh;
            Ray ray = new Ray(boot.Head.position, boot.Head.forward);
            if (Physics.Raycast(ray, out rh, 3f, 1 << GtagBootstrap.MenuLayer, QueryTriggerInteraction.Collide))
                hit = rh.collider;
        }
        Btn touched = null;
        if (hit != null) byCollider.TryGetValue(hit, out touched);

        // touching a button presses it (like the original ModButton); on desktop it needs a left click
        bool press = touched != null && (rig.XRActive ? hover != touched : rig.ClickPressed);
        hover = touched;
        if (press && touched.go.activeInHierarchy && touched.click != null && Time.time - touched.lastPress > PressCooldown)
        {
            touched.lastPress = Time.time;
            rig.Pulse();
            touched.click();
        }

        Refresh();
    }

    void Refresh()
    {
        for (int i = 0; i < PerPage; i++)
        {
            Btn b = slots[i];
            int idx = pageIdx * PerPage + i;
            bool has = idx < items.Count;
            if (b.go.activeSelf != has) b.go.SetActive(has);
            if (!has) { b.click = null; continue; }
            Item it = items[idx];
            b.text = it.text;
            b.isOn = it.isOn;
            b.click = it.click;
            Style(b);
        }

        int pages = PageCount();
        navNext.text = () => "Page " + (((pageIdx + 1) % pages) + 1);
        navPrev.text = () => "Page " + (((pageIdx + pages - 1) % pages) + 1);
        navNext.go.SetActive(pages > 1);
        navPrev.go.SetActive(pages > 1);
        Style(navNext);
        Style(navPrev);
    }

    void Style(Btn b)
    {
        if (b.label != null) b.label.text = Clean(b.text());
        if (b.mat == null) return;
        Color c = cButton;
        if (b.isOn != null && b.isOn()) c = cOn;
        if (b == hover && b.click != null) c = cHover;
        b.mat.color = c;
    }

    void UpdateFly()
    {
        bool flying = fly || noclip;
        body.useGravity = !flying;
        if (!flying) return;

        if (rig.FlyHeld && !open && !boot.IsDying)
        {
            Vector3 dir = rig.XRActive ? boot.RightHand.forward : boot.Head.forward;
            body.velocity = dir * FlySpeed;
        }
        else
        {
            body.velocity = Vector3.zero;
        }
    }

    void UpdatePlatforms()
    {
        if (!platforms) return;
        HandPlatform(0, rig.LeftGrip, boot.LeftHand);
        HandPlatform(1, rig.RightGrip, boot.RightHand);
    }

    void HandPlatform(int i, bool grip, Transform hand)
    {
        if (grip && plat[i] == null)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
            p.name = "Platform";
            p.transform.localScale = new Vector3(0.5f, 0.04f, 0.5f);
            p.transform.position = hand.position + Vector3.down * 0.05f;
            p.transform.rotation = Quaternion.Euler(0f, hand.eulerAngles.y, 0f);
            p.GetComponent<Renderer>().sharedMaterial = MonkeModel.NewMaterial(Color.white);   // primitives' default shader is stripped from builds -> magenta
            p.GetComponent<Renderer>().material.color = new Color(0.2f, 0.75f, 0.3f);
            plat[i] = p;
        }
        else if (!grip && plat[i] != null)
        {
            Destroy(plat[i]);
            plat[i] = null;
        }
    }

    void ClearPlatforms()
    {
        for (int i = 0; i < plat.Length; i++)
        {
            if (plat[i] != null) Destroy(plat[i]);
            plat[i] = null;
        }
    }

    // ---------------- mods / settings ----------------

    void ApplySpeed()
    {
        player.jumpMultiplier = JumpMult[speedIdx];
        player.maxJumpSpeed = MaxJump[speedIdx];
    }

    void ApplyGravity()
    {
        Physics.gravity = Vector3.down * 9.81f * GravScale[gravIdx];
    }

    // Longer reach, but the push you get from each swing stays the same as normal arms.
    void ApplyArms()
    {
        rig.ArmScale = ArmScales[armIdx];
        player.maxArmLength = BaseArmLength * ArmScales[armIdx];
        player.handMoveScale = 1f / ArmScales[armIdx];
    }

    void SetCar(bool on) { car = on; }

    // Gorilla Car: right trigger drives you forward, left trigger backwards (flat, the way the head looks)
    void UpdateCar()
    {
        if (!car || open || boot.IsDying) return;
        Vector3 f = boot.Head.forward; f.y = 0f;
        if (f.sqrMagnitude < 0.01f) return;
        f.Normalize();
        if (rig.RightTrigger) body.AddForce(f * CarAccel, ForceMode.Acceleration);
        if (rig.LeftTrigger) body.AddForce(-f * CarAccel, ForceMode.Acceleration);
    }

    // brings every other player to you (the host checks the same code as before)
    void TeleportAll()
    {
        OnlineManager om = OnlineManager.Instance;
        if (om == null) return;
        foreach (ulong id in om.RemoteIds()) om.SendMod(2, id, player.transform.position);
    }

    void SetRainbow(bool on)
    {
        rainbow = on;
        PlayerProfile.Rainbow = on;
        PlayerProfile.Refresh();
    }

    void UpdateRainbow()
    {
        if (!rainbow || Time.time < rainbowNext) return;
        rainbowNext = Time.time + 0.07f;
        PlayerProfile.Refresh();
    }

    void SetGhost(bool on)
    {
        ghost = on;
        if (OnlineManager.Instance != null) OnlineManager.Instance.Ghost = on;
    }

    void ApplyFreeze()
    {
        boot.SetMonstersFrozen(freeze);
    }

    void ToggleMonster()
    {
        PlayerProfile.MonsterOn = !PlayerProfile.MonsterOn;
        boot.SetMonsterEnabled(PlayerProfile.MonsterOn);
        ApplyFreeze();
    }

    void SetNoclip(bool on)
    {
        noclip = on;
        player.locomotionEnabledLayers = on ? (LayerMask)0 : (LayerMask)1;
        boot.HeadCollider.isTrigger = on;
        boot.BodyCollider.isTrigger = on;
        body.velocity = Vector3.zero;
        boot.ResyncPlayer();

        // turning noclip off while inside a wall would trap you - go back to the stump instead
        if (!on && Physics.CheckSphere(boot.Head.position, 0.25f, 1 << 0, QueryTriggerInteraction.Ignore))
            boot.TeleportToSpawn();
    }

    void SetDarkness(bool on)
    {
        darkness = on;
        boot.Sun.intensity = on ? 0.04f : 1f;
        RenderSettings.ambientLight = on ? new Color(0.02f, 0.02f, 0.03f) : new Color(0.55f, 0.58f, 0.62f);
        RenderSettings.fog = on;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.035f;
        RenderSettings.fogColor = Color.black;
        boot.Cam.backgroundColor = on ? Color.black : new Color(0.45f, 0.62f, 0.85f);
        if (on && !flashlight) { flashlight = true; torch.enabled = true; }
    }

    void ResetMods()
    {
        speedIdx = 0; gravIdx = 0; armIdx = 0;
        fly = false; platforms = false; flashlight = false; freeze = false; god = false;
        car = false;
        if (rainbow) SetRainbow(false);
        if (ghost) SetGhost(false);
        torch.enabled = false;
        ClearPlatforms();
        if (noclip) SetNoclip(false);
        if (darkness) SetDarkness(false);
        boot.Invincible = false;
        ApplySpeed(); ApplyGravity(); ApplyArms(); ApplyFreeze();
        body.useGravity = true;
    }

    // ---------------- moderator ----------------

    ulong CurrentTarget(out bool ok)
    {
        ok = false;
        OnlineManager om = OnlineManager.Instance;
        if (om == null) return 0;
        List<ulong> ids = om.RemoteIds();
        if (ids.Count == 0) return 0;
        targetIdx = Mathf.Clamp(targetIdx, 0, ids.Count - 1);
        ok = true;
        return ids[targetIdx];
    }

    string TargetName()
    {
        bool ok;
        ulong id = CurrentTarget(out ok);
        return ok ? OnlineManager.Instance.NameOf(id) : "none online";
    }

    void CycleTarget()
    {
        OnlineManager om = OnlineManager.Instance;
        if (om == null) return;
        int n = om.RemoteIds().Count;
        if (n > 0) targetIdx = (targetIdx + 1) % n;
    }

    void ModAction(byte action)
    {
        bool ok;
        ulong id = CurrentTarget(out ok);
        if (ok) OnlineManager.Instance.SendMod(action, id, player.transform.position);
    }

    void GoToTarget()
    {
        bool ok;
        ulong id = CurrentTarget(out ok);
        if (ok) OnlineManager.Instance.TeleportToPlayer(id);
    }

    void OnDestroy()
    {
        Physics.gravity = Vector3.down * 9.81f;
    }
}
