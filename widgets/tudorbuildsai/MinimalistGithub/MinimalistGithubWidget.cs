using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Minimalist GitHub", Description = "A charcoal GitHub contribution calendar with a muted blue heatmap.")]
public sealed class MinimalistGithubWidget : CardWidget
{
    const int Weeks = 30;
    const string Query = "query { viewer { login name avatarUrl(size: 96) followers { totalCount } " +
        "repositories(ownerAffiliations: OWNER) { totalCount } contributionsCollection { contributionCalendar { " +
        "totalContributions weeks { contributionDays { date contributionCount contributionLevel } } } } } }";

    sealed class Data
    {
        public string Login, Name;
        public int Repos, Followers, Total;
        public readonly List<DateTime> Dates = new List<DateTime>();
        public List<int> Counts = new List<int>(), Levels = new List<int>();
        public List<int> FullYear = new List<int>();

        public int FullYearStreak
        {
            get
            {
                int i = FullYear.Count - 1, s = 0;
                if (i >= 0 && FullYear[i] == 0) i--;
                while (i >= 0 && FullYear[i] > 0) { s++; i--; }
                return s;
            }
        }
    }

    Data data;
    Bitmap avatar;
    DateTime? lastSyncedAt;
    string status;
    bool needsToken, busy;
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };

    public MinimalistGithubWidget(Section settings) : base(settings, CardSize.Medium)
    {
        LoadCache();
        refresh.Tick += delegate { Fetch(); };
    }

    static string CacheDir { get { return Path.Combine(PluginApi.DataDir, "cache"); } }
    static string CacheJson { get { return Path.Combine(CacheDir, "minimalist-github.json"); } }
    static string CacheAvatar { get { return Path.Combine(CacheDir, "minimalist-github-avatar.png"); } }

    protected override void OnStart()
    {
        refresh.Start();
        Fetch();
    }

    void LoadCache()
    {
        try
        {
            if (File.Exists(CacheJson))
            {
                data = Parse(File.ReadAllText(CacheJson));
                lastSyncedAt = File.GetLastWriteTime(CacheJson);
            }
            if (File.Exists(CacheAvatar)) avatar = LoadBitmap(File.ReadAllBytes(CacheAvatar));
        }
        catch (Exception) { data = null; }
    }

    void Fetch()
    {
        if (busy) return;
        busy = true;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string json = null, error = null, avatarUrl = null;
            byte[] avatarBytes = null;
            bool tokenProblem = false;
            try
            {
                string token = Token();
                if (token == null) tokenProblem = true;
                else
                {
                    json = Post(token, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "query", Query } }));
                    avatarUrl = AvatarUrl(json);
                    if (avatarUrl != null) avatarBytes = Download(avatarUrl);
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.Unauthorized) tokenProblem = true;
                else error = "Offline";
            }
            catch (Exception) { error = "Couldn't refresh"; }

            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate
            {
                busy = false;
                needsToken = tokenProblem;
                status = tokenProblem ? "Click to connect" : error;
                if (json != null)
                {
                    try
                    {
                        data = Parse(json);
                        lastSyncedAt = DateTime.Now;
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(CacheJson, json);
                        if (avatarBytes != null)
                        {
                            File.WriteAllBytes(CacheAvatar, avatarBytes);
                            if (avatar != null) avatar.Dispose();
                            avatar = LoadBitmap(avatarBytes);
                        }
                    }
                    catch (Exception) { status = "Couldn't read GitHub's reply"; }
                }
                Render();
            }); }
            catch (InvalidOperationException) { }
        });
    }

    string Token()
    {
        string saved = Settings.Get("token", "");
        if (saved.Length > 0)
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception) { return null; }
        }
        return Settings.Get("auth", "") == "gh" ? GhCliToken() : null;
    }

    static bool GhCliInstalled
    {
        get
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "gh.exe"))) return true; }
                catch (ArgumentException) { }
            }
            return false;
        }
    }

    static string GhCliToken()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                return p.ExitCode == 0 && output.Length > 0 ? output : null;
            }
        }
        catch (Exception) { return null; }
    }

    static string Post(string token, string body)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/graphql");
        req.Method = "POST";
        req.UserAgent = "DeskWidgets";
        req.ContentType = "application/json";
        req.Timeout = 20000;
        req.Headers["Authorization"] = "bearer " + token;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        using (var resp = req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }

    static byte[] Download(string url)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "DeskWidgets";
        req.Timeout = 20000;
        using (var resp = req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static Dictionary<string, object> Viewer(string json)
    {
        var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json);
        return (Dictionary<string, object>)((Dictionary<string, object>)root["data"])["viewer"];
    }

    static string AvatarUrl(string json)
    {
        return Viewer(json)["avatarUrl"] as string;
    }

    static int Count(Dictionary<string, object> o, string key)
    {
        return Convert.ToInt32(((Dictionary<string, object>)o[key])["totalCount"]);
    }

    static Data Parse(string json)
    {
        var v = Viewer(json);
        var cal = (Dictionary<string, object>)((Dictionary<string, object>)v["contributionsCollection"])["contributionCalendar"];
        var d = new Data
        {
            Login = (string)v["login"], Name = (v["name"] as string) ?? (string)v["login"],
            Repos = Count(v, "repositories"), Followers = Count(v, "followers"),
            Total = Convert.ToInt32(cal["totalContributions"])
        };
        var weeks = (object[])cal["weeks"];
        for (int w = Math.Max(0, weeks.Length - Weeks); w < weeks.Length; w++)
            foreach (Dictionary<string, object> day in (object[])((Dictionary<string, object>)weeks[w])["contributionDays"])
            {
                d.Dates.Add(DateTime.ParseExact((string)day["date"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                d.Counts.Add(Convert.ToInt32(day["contributionCount"]));
                d.Levels.Add(LevelOf((string)day["contributionLevel"]));
            }
        var all = new List<int>();
        foreach (Dictionary<string, object> week in weeks)
            foreach (Dictionary<string, object> day in (object[])week["contributionDays"])
                all.Add(Convert.ToInt32(day["contributionCount"]));
        d.FullYear = all;
        return d;
    }

    static int LevelOf(string level)
    {
        switch (level)
        {
            case "FIRST_QUARTILE": return 1;
            case "SECOND_QUARTILE": return 2;
            case "THIRD_QUARTILE": return 3;
            case "FOURTH_QUARTILE": return 4;
            default: return 0;
        }
    }

    static Bitmap LoadBitmap(byte[] bytes)
    {
        using (var ms = new MemoryStream(bytes))
        using (var img = Image.FromStream(ms))
            return new Bitmap(img);
    }

    void Connect()
    {
        bool useCli = false;
        bool cli = GhCliInstalled;
        var values = InputDialog.Ask("Connect GitHub",
            "Paste a GitHub personal access token (github.com/settings/tokens). No scopes are needed for public " +
            "contributions; add read:user to include private ones. It's stored encrypted for your Windows account only." +
            (cli ? " Or use the account you're signed in to with the GitHub CLI." : ""),
            new[] { new InputDialog.Field { Label = "Personal access token", Secret = true } }, "Connect",
            cli ? "Use my GitHub CLI login" : null,
            d => { useCli = true; d.DialogResult = System.Windows.Forms.DialogResult.OK; });
        if (useCli)
        {
            Settings.Remove("token");
            Settings.Set("auth", "gh");
            Fetch();
            return;
        }
        if (values == null || values[0].Trim().Length == 0) return;
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(values[0].Trim()), null, DataProtectionScope.CurrentUser);
        Settings.Set("token", Convert.ToBase64String(enc));
        Settings.Remove("auth");
        Fetch();
    }

    bool Connected { get { return Settings.Get("token", "").Length > 0 || Settings.Get("auth", "") == "gh"; } }

    protected override void OnWidgetClick(Point p)
    {
        if (needsToken || data == null) { Connect(); return; }
        try { Process.Start("https://github.com/" + data.Login); }
        catch (Exception) { }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Refresh Now", Fetch);
        if (data != null) menu.Add("Open Profile on GitHub", delegate { OnWidgetClick(Point.Empty); });
        menu.Add(Connected ? "Change Account…" : "Connect GitHub…", Connect);
        if (Connected)
            menu.AddDestructive("Disconnect", delegate { Settings.Remove("token"); Settings.Remove("auth"); Fetch(); });
    }

    static readonly Color[] DarkLevels = { Rgb(0x2C2C2E), Rgb(0x0E4429), Rgb(0x006D32), Rgb(0x26A641), Rgb(0x39D353) };
    static readonly Color[] LightLevels = { Rgb(0xEBEDF0), Rgb(0x9BE9A8), Rgb(0x40C463), Rgb(0x30A14E), Rgb(0x216E39) };
    static readonly Color[] VibrantLevels =
    {
        Color.FromArgb(36, 255, 255, 255), Color.FromArgb(90, 255, 255, 255), Color.FromArgb(140, 255, 255, 255),
        Color.FromArgb(195, 255, 255, 255), Color.FromArgb(245, 255, 255, 255)
    };

    static readonly Color[] Blues = {
        Color.FromArgb(65, 66, 67), Color.FromArgb(52, 67, 77),
        Color.FromArgb(62, 84, 101), Color.FromArgb(80, 109, 133),
        Color.FromArgb(111, 145, 171)
    };

    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(15);
        FillRound(g, Color.FromArgb(35, 35, 35),
            new RectangleF(1, 1, W - 2, H - 2), P(20));
        Color ink = Color.FromArgb(243, 243, 243);
        Color muted = Color.FromArgb(203, 203, 203);
        Data d = data ?? (IsHandleCreated ? null : Sample());
        if (d == null)
        {
            Label(g, status ?? "Fetching GitHub contributions", F(13, "semibold"),
                ink, m, P(32));
            Label(g, "Right-click for connection and refresh options", F(11, "regular"),
                muted, m, P(56));
            return;
        }

        float avatarSize = P(25);
        FillCircle(g, Blues[2], m + avatarSize / 2, m + avatarSize / 2, avatarSize / 2);
        if (avatar != null)
        {
            using (var path = new GraphicsPath())
            {
                var bounds = new RectangleF(m, m, avatarSize, avatarSize);
                path.AddEllipse(bounds);
                var saved = g.Save();
                g.SetClip(path);
                g.DrawImage(avatar, bounds);
                g.Restore(saved);
            }
        }
        var small = F(10, "semibold");
        string total = d.Total.ToString("N0") + " contributions";
        Label(g, total, small, ink, W - m - TextWidth(g, total, small), P(20));
        Label(g, d.Login, F(12, "semibold"), ink, m + avatarSize + P(7), P(18),
            W - P(170), StringAlignment.Near);

        float left = P(51), top = P(58);
        float step = Math.Min((W - left - m) / Weeks, P(12));
        float cell = step - P(2);
        DateTime start = d.Dates.Count == 0 ? DateTime.Today.AddDays(-(Weeks * 7 - 1)) : d.Dates[0];
        start = start.AddDays(-(int)start.DayOfWeek);
        int lastMonthColumn = -4;
        for (int week = 0; week < Weeks; week++)
        {
            DateTime date = start.AddDays(week * 7);
            if (date.Day <= 7 && week - lastMonthColumn >= 3)
            {
                Label(g, date.ToString("MMM"), small, muted, left + week * step, P(42));
                lastMonthColumn = week;
            }
        }
        Label(g, "Mon", small, muted, m, top + step);
        Label(g, "Wed", small, muted, m, top + 3 * step);
        Label(g, "Fri", small, muted, m, top + 5 * step);
        for (int i = 0; i < d.Levels.Count; i++)
        {
            int day = i;
            if (i < d.Dates.Count) day = (d.Dates[i] - start).Days;
            if (day < 0 || day >= Weeks * 7) continue;
            FillRound(g, Blues[d.Levels[i]], new RectangleF(
                left + day / 7 * step, top + day % 7 * step, cell, cell), P(2));
        }

        float footer = H - P(24);
        string sync = lastSyncedAt.HasValue ? "Synced " + lastSyncedAt.Value.ToString("HH:mm") : "Preview";
        Label(g, status ?? sync, small, muted, m, footer);
        float legend = W - P(115);
        Label(g, "Less", small, muted, legend, footer);
        for (int i = 0; i < Blues.Length; i++)
            FillRound(g, Blues[i], new RectangleF(legend + P(25 + i * 9), footer + P(3), P(7), P(7)), P(1));
        Label(g, "More", small, muted, legend + P(73), footer);
    }

    static Data Sample()
    {
        var d = new Data { Login = "you", Name = "Your GitHub", Repos = 12, Followers = 8, Total = 640 };
        var rnd = new Random(7);
        for (int i = 0; i < Weeks * 7; i++)
        {
            int lvl = rnd.Next(10) < 4 ? 0 : rnd.Next(1, 5);
            d.Levels.Add(lvl);
            d.Counts.Add(lvl * 3);
        }
        d.FullYear = d.Counts;
        return d;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            if (avatar != null) { avatar.Dispose(); avatar = null; }
        }
        base.Dispose(disposing);
    }
}
