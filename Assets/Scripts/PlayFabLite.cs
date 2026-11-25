using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Tiny PlayFab client that talks to the PlayFab REST API directly (UnityWebRequest), so there is no SDK to
// import and Unity Cloud Build needs nothing extra.
//
// SETUP: make a title at https://developer.playfab.com, copy its "Title ID" (4-5 letters/digits) and put it
// into TitleId below (or call PlayFabLite.SetTitleId at runtime). While TitleId is empty PlayFab stays off
// and the menu says so.
//
// What it does: signs in with a device-based custom ID, sets your display name to the menu name, and sends
// your best survival time to a statistic called "BestSurvivalSeconds" (create that statistic in the PlayFab
// dashboard under Leaderboards to see a global leaderboard).
public class PlayFabLite : MonoBehaviour
{
    public const string TitleId = "";   // <- put your PlayFab Title ID here

    public static PlayFabLite Instance { get; private set; }
    public string Status { get; private set; }
    public bool LoggedIn { get { return !string.IsNullOrEmpty(ticket); } }
    public string PlayFabId { get; private set; }

    string titleId = TitleId;
    string ticket;
    bool busy;

    public static PlayFabLite Create()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("PlayFab");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<PlayFabLite>();
        return Instance;
    }

    public bool Configured { get { return !string.IsNullOrEmpty(titleId); } }

    public void SetTitleId(string id) { titleId = id; }

    void Awake()
    {
        Status = Configured ? "PlayFab: not signed in" : "PlayFab: set Title ID";
    }

    void Start()
    {
        if (Configured) Login();
    }

    // ---------------- public actions ----------------

    public void Login()
    {
        if (!Configured) { Status = "PlayFab: set Title ID"; return; }
        if (busy) return;
        busy = true;
        Status = "PlayFab: signing in...";
        string id = PlayerPrefs.GetString("gt_pf_id", "");
        if (id == "")
        {
            id = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrEmpty(id) || id == SystemInfo.unsupportedIdentifier) id = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString("gt_pf_id", id);
            PlayerPrefs.Save();
        }
        string body = "{\"TitleId\":\"" + titleId + "\",\"CustomId\":\"" + id + "\",\"CreateAccount\":true}";
        StartCoroutine(Post("/Client/LoginWithCustomID", body, false, json =>
        {
            var r = JsonUtility.FromJson<LoginReply>(json);
            if (r != null && r.data != null && !string.IsNullOrEmpty(r.data.SessionTicket))
            {
                ticket = r.data.SessionTicket;
                PlayFabId = r.data.PlayFabId;
                Status = "PlayFab: signed in";
                SyncName();
            }
            else Status = "PlayFab: sign-in failed";
        }));
    }

    public void SyncName()
    {
        if (!LoggedIn) return;
        string body = "{\"DisplayName\":\"" + PlayerProfile.Name + "\"}";
        StartCoroutine(Post("/Client/UpdateUserTitleDisplayName", body, true, json => Status = "PlayFab: name set to " + PlayerProfile.Name));
    }

    public void SubmitBestTime()
    {
        if (!LoggedIn) { Login(); return; }
        float best = 0f;
        foreach (ScoreEntry e in Scores.Load()) if (e.time > best) best = e.time;
        if (best <= 0f) { Status = "PlayFab: no time yet"; return; }
        string body = "{\"Statistics\":[{\"StatisticName\":\"BestSurvivalSeconds\",\"Value\":" + Mathf.RoundToInt(best) + "}]}";
        StartCoroutine(Post("/Client/UpdatePlayerStatistics", body, true, json => Status = "PlayFab: sent " + Mathf.RoundToInt(best) + "s"));
    }

    // ---------------- plumbing ----------------

    System.Collections.IEnumerator Post(string path, string body, bool auth, Action<string> done)
    {
        string url = "https://" + titleId + ".playfabapi.com" + path;
        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (auth) req.SetRequestHeader("X-Authorization", ticket);
            yield return req.SendWebRequest();
            busy = false;
            if (req.result != UnityWebRequest.Result.Success)
            {
                Status = "PlayFab: " + (req.responseCode == 0 ? "no connection" : "error " + req.responseCode);
                yield break;
            }
            done(req.downloadHandler.text);
        }
    }

    [Serializable] class LoginReply { public LoginData data; }
    [Serializable] class LoginData { public string SessionTicket; public string PlayFabId; }
}
