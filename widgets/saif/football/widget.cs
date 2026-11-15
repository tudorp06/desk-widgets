using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using DeskWidgets;
using DeskWidgets.Widgets;

// Live scores, results and fixtures for one football league, from ESPN's public scoreboard
// (free, no account). Only the league you chose is sent. Live matches come first, then today's,
// then the last day's results, then what is coming up; a team you follow is always on top.
//
// The month's matches are fetched every 15 minutes. While a match is being played, or is about
// to start, only that day is fetched, once a minute: it is a fraction of the size. The last
// answer is cached, so the widget shows at startup and offline. Narrower than a medium card, it
// shows one match large instead of a list.
[DeskWidget(Name = "Football", Description = "Live scores, results and fixtures for your league")]
public sealed class FootballWidget : CardWidget
{
    sealed class Match
    {
        public string Id, Home, Away, HomeScore, AwayScore, State, Detail; // State: pre, in, post
        public DateTime Kickoff;                                          // UTC
        public int HomeColor, AwayColor;
        public bool Done;                                                 // played to the end, not postponed

        public bool Live { get { return State == "in"; } }
    }

    // ESPN's league code, the name in the menu, and the name on the card.
    static readonly string[][] Leagues =
    {
        new[] { "eng.1", "Premier League", "Premier League" },
        new[] { "esp.1", "LaLiga", "LaLiga" },
        new[] { "ita.1", "Serie A", "Serie A" },
        new[] { "ger.1", "Bundesliga", "Bundesliga" },
        new[] { "fra.1", "Ligue 1", "Ligue 1" },
        new[] { "uefa.champions", "Champions League", "Champions League" },
        new[] { "uefa.europa", "Europa League", "Europa League" },
        new[] { "usa.1", "MLS", "MLS" },
        new[] { "ksa.1", "Saudi Pro League", "Saudi Pro League" },
        new[] { "ind.1", "Indian Super League", "Indian Super League" },
        new[] { "fifa.world", "World Cup", "World Cup" },
    };

    const string Api = "https://site.api.espn.com/apis/site/v2/sports/soccer/";
    const int FullEvery = 15 * 60 * 1000, LiveEvery = 60 * 1000;

    List<Match> matches;
    string status;
    bool busy;
    DateTime lastFull;
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = FullEvery };

    public FootballWidget(Section settings) : base(settings, CardSize.Medium)
    {
        refresh.Tick += delegate { Fetch(false); };
        LoadCache();
    }

    string League { get { return Settings.Get("league", "eng.1"); } }
    string Team { get { return Settings.Get("team", "").Trim(); } }

    string LeagueName
    {
        get
        {
            string code = League;
            foreach (var l in Leagues) if (l[0] == code) return l[2];
            return "Football";
        }
    }

    protected override void OnStart()
    {
        refresh.Start();
        Fetch(true);
    }

    // ---- data ----

    // full: the whole month (and the next, near its end). Otherwise only the days that have a
    // match in play or about to start, merged into what is already held.
    void Fetch(bool full)
    {
        if (busy) return;
        DateTime now = DateTime.UtcNow;
        if (matches == null || (now - lastFull).TotalMilliseconds >= FullEvery - 5000) full = true;
        var dates = new List<string>();
        if (full)
        {
            DateTime local = DateTime.Now;
            dates.Add(local.ToString("yyyyMM", CultureInfo.InvariantCulture));
            if (local.Day > 21) dates.Add(local.AddMonths(1).ToString("yyyyMM", CultureInfo.InvariantCulture));
        }
        else
        {
            // ESPN files a match under its date in US Eastern time.
            foreach (var m in matches.Where(m => Active(m, now)))
            {
                string day = m.Kickoff.AddHours(-5).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!dates.Contains(day)) dates.Add(day);
            }
            if (dates.Count == 0) { Schedule(); return; }
        }

        busy = true;
        string league = League, file = CacheFile("football");
        Background(() =>
        {
            var found = new List<Match>();
            foreach (string d in dates)
                found.AddRange(Parse(Http.Get(Api + league + "/scoreboard?limit=200&dates=" + d)));
            return found;
        }, (found, error) =>
        {
            busy = false;
            if (league != League) { Fetch(true); return; } // the league was changed meanwhile
            if (error != null)
            {
                status = "Offline";
                refresh.Interval = 2 * LiveEvery; // try again soon, not in a quarter of an hour
                Render();
                return;
            }
            else
            {
                status = null;
                if (full) { matches = found; lastFull = DateTime.UtcNow; }
                else Merge(found);
                SaveCache(file, league);
            }
            Schedule();
            Render();
        });
    }

    // In play, or close enough to kickoff (or to a late finish) to be worth watching by the minute.
    static bool Active(Match m, DateTime now)
    {
        if (m.Live) return true;
        if (m.State != "pre") return false;
        double minutes = (m.Kickoff - now).TotalMinutes;
        return minutes < 5 && minutes > -180;
    }

    void Merge(List<Match> fresh)
    {
        foreach (var f in fresh)
        {
            int i = matches.FindIndex(m => m.Id == f.Id);
            if (i >= 0) matches[i] = f; else matches.Add(f);
        }
    }

    // The next look: a minute while something is on, otherwise at the next kickoff or in
    // fifteen minutes, whichever is sooner.
    void Schedule()
    {
        DateTime now = DateTime.UtcNow;
        int wait = FullEvery;
        if (matches != null)
        {
            if (matches.Any(m => Active(m, now))) wait = LiveEvery;
            else
            {
                foreach (var m in matches.Where(m => m.State == "pre" && m.Kickoff > now))
                {
                    double ms = (m.Kickoff - now).TotalMilliseconds - 4 * 60 * 1000;
                    if (ms < wait) wait = (int)Math.Max(LiveEvery, ms);
                }
            }
        }
        refresh.Interval = wait;
    }

    static List<Match> Parse(string json)
    {
        var list = new List<Match>();
        var root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
        foreach (object e in Arr(root, "events"))
        {
            var ev = e as Dictionary<string, object>;
            var comp = Arr(ev, "competitions").FirstOrDefault() as Dictionary<string, object>;
            var type = Dict(Dict(comp, "status"), "type");
            DateTime kickoff;
            if (ev == null || comp == null || type == null) continue;
            if (!DateTime.TryParse(Str(ev, "date"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out kickoff)) continue;
            var m = new Match
            {
                Id = Str(ev, "id"), Kickoff = kickoff, State = Str(type, "state") ?? "pre",
                Detail = Str(type, "shortDetail") ?? "", Done = Str(type, "completed") == "True"
            };
            foreach (object c in Arr(comp, "competitors"))
            {
                var side = c as Dictionary<string, object>;
                var team = Dict(side, "team");
                if (team == null) continue;
                string name = Str(team, "shortDisplayName") ?? Str(team, "displayName") ?? Str(team, "abbreviation") ?? "";
                int color = Hex(Str(team, "color"));
                if (Str(side, "homeAway") == "away") { m.Away = name; m.AwayScore = Str(side, "score"); m.AwayColor = color; }
                else { m.Home = name; m.HomeScore = Str(side, "score"); m.HomeColor = color; }
            }
            if (m.Id != null && m.Home != null && m.Away != null) list.Add(m);
        }
        return list;
    }

    static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
    }

    static object[] Arr(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v is object[] ? (object[])v : new object[0];
    }

    static string Str(Dictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
    }

    static int Hex(string s)
    {
        int rgb;
        return s != null && s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb) ? rgb : 0x8E8E93;
    }

    // ---- cache: the league, then one match a line ----

    static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

    void SaveCache(string file, string league)
    {
        if (file == null || matches == null) return;
        try
        {
            var lines = new List<string> { league };
            foreach (var m in matches)
                lines.Add(string.Join("\t", new[]
                {
                    Clean(m.Id), m.Kickoff.Ticks.ToString(CultureInfo.InvariantCulture), Clean(m.Home), Clean(m.Away),
                    Clean(m.HomeScore), Clean(m.AwayScore), Clean(m.State), Clean(m.Detail),
                    m.HomeColor.ToString(CultureInfo.InvariantCulture), m.AwayColor.ToString(CultureInfo.InvariantCulture), m.Done ? "1" : "0"
                }));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllLines(file, lines);
        }
        catch (Exception) { } // the cache is a convenience
    }

    void LoadCache()
    {
        string file = CacheFile("football");
        if (file == null || !File.Exists(file)) return;
        try
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines[0] != League) return;
            var list = new List<Match>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] v = lines[i].Split('\t');
                if (v.Length < 11) continue;
                list.Add(new Match
                {
                    Id = v[0], Kickoff = new DateTime(long.Parse(v[1], CultureInfo.InvariantCulture), DateTimeKind.Utc),
                    Home = v[2], Away = v[3], HomeScore = v[4], AwayScore = v[5], State = v[6], Detail = v[7],
                    HomeColor = int.Parse(v[8], CultureInfo.InvariantCulture), AwayColor = int.Parse(v[9], CultureInfo.InvariantCulture),
                    Done = v[10] == "1"
                });
            }
            matches = list;
        }
        catch (Exception) { matches = null; }
    }

    // ---- what to show, in order ----

    bool Followed(Match m)
    {
        string t = Team;
        return t.Length > 0 && (Has(m.Home, t) || Has(m.Away, t));
    }

    static bool Has(string name, string part)
    {
        return name != null && name.IndexOf(part, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }

    // 0 in play, 1 today, 2 finished in the last day and a half, 3 coming up, 4 older results.
    static int Group(Match m, DateTime now)
    {
        if (m.Live) return 0;
        if (m.Kickoff.ToLocalTime().Date == now.ToLocalTime().Date) return 1;
        if (m.Kickoff > now) return 3;
        return (now - m.Kickoff).TotalHours <= 36 ? 2 : 4;
    }

    static List<Match> Ordered(List<Match> all, DateTime now, Func<Match, bool> followed)
    {
        var sorted = all
            .OrderBy(m => Group(m, now))
            // Upcoming and today's run forward in time; results run back from the newest.
            .ThenBy(m => { int g = Group(m, now); return g == 2 || g == 4 ? -m.Kickoff.Ticks : m.Kickoff.Ticks; })
            .ToList();
        // The followed team's match that matters most now (the first in that order) leads.
        Match mine = sorted.FirstOrDefault(followed);
        if (mine != null) { sorted.Remove(mine); sorted.Insert(0, mine); }
        return sorted;
    }

    // ---- menu ----

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var leagues = menu.AddSubmenu("League");
        string current = League;
        foreach (var l in Leagues)
        {
            string code = l[0];
            leagues.AddChoice(l[1], current == code, delegate { SetLeague(code); });
        }
        string team = Team;
        menu.Add(team.Length > 0 ? "Following " + team + "…" : "Follow a Team…", FollowTeam);
        menu.Add("Refresh Now", delegate { Fetch(true); });
    }

    void SetLeague(string code)
    {
        if (code == League) return;
        Settings.Set("league", code);
        matches = null;
        status = null;
        lastFull = DateTime.MinValue;
        Render();
        Fetch(true);
    }

    void FollowTeam()
    {
        var values = InputDialog.Ask("Follow a Team",
            "Type a team's name, or part of it (Arsenal, Madrid, Inter). Its match is kept at the top.\nLeave it empty to follow no one.",
            new[] { new InputDialog.Field { Label = "Team", Value = Team } }, "Follow");
        if (values == null) return;
        Settings.Set("team", values[0].Trim());
        Render();
    }

    // ---- drawing ----

    protected override void DrawContent(Graphics g, Palette pal)
    {
        bool sample = !IsHandleCreated; // the library preview: never fetches, so it shows a matchday
        DateTime now = DateTime.UtcNow;
        List<Match> all = sample ? Sample(now) : matches;
        float m = P(16);

        bool anyLive = all != null && all.Any(x => x.Live);
        string right = anyLive ? "LIVE" : status;
        float rightW = right == null ? 0 : TextWidth(g, right.ToUpperInvariant(), F(11, "semibold")) + P(8);
        Label(g, (sample ? "Premier League" : LeagueName).ToUpperInvariant(), F(11, "semibold"), pal.Green, m, P(14),
            W - m * 2 - rightW, StringAlignment.Near);
        if (right != null)
            Label(g, right.ToUpperInvariant(), F(11, "semibold"), anyLive ? pal.Red : pal.Fg2, W - m, P(14), rightW, StringAlignment.Far);

        if (all == null || all.Count == 0)
        {
            string text = all == null ? (status ?? "Loading matches…") : "No matches this month";
            Label(g, text, F(13, "regular"), pal.Fg2, W / 2, H / 2 - P(4), W - m * 2, StringAlignment.Center);
            return;
        }

        Func<Match, bool> followed = sample ? (Func<Match, bool>)(x => x.Home == "Arsenal") : Followed;
        List<Match> order = Ordered(all, now, followed);
        if (W < P(250)) { DrawOne(g, pal, order[0], now, m); return; }

        float top = P(38), rowH = P(24);
        int rows = Math.Max(1, (int)((H - top - P(8)) / rowH));
        for (int i = 0; i < Math.Min(rows, order.Count); i++)
            DrawRow(g, pal, order[i], now, m, top + rowH * i, rowH, followed(order[i]));
    }

    // One line: when, home, score, away. The score sits in the middle with the names either side.
    void DrawRow(Graphics g, Palette pal, Match x, DateTime now, float m, float y, float rowH, bool mine)
    {
        if (mine) FillRound(g, pal.Fill, new RectangleF(m - P(6), y, W - m * 2 + P(12), rowH - P(2)), P(7));
        float whenW = P(54), scoreW = P(46), dot = P(4);
        float mid = m + whenW + (W - m * 2 - whenW) / 2;
        float nameW = (W - m * 2 - whenW - scoreW) / 2 - dot * 2 - P(8);
        float ty = y + P(3);

        Label(g, When(x, now), F(11, x.Live ? "semibold" : "regular"), x.Live ? pal.Red : pal.Fg2, m, ty + P(1.5f), whenW - P(4), StringAlignment.Near);

        Color home = pal.Fg, away = pal.Fg;
        int hs, aw;
        if (x.Done && int.TryParse(x.HomeScore, out hs) && int.TryParse(x.AwayScore, out aw) && hs != aw)
        {
            if (hs < aw) home = pal.Fg2; else away = pal.Fg2;
        }
        float left = mid - scoreW / 2, rightX = mid + scoreW / 2;
        Label(g, x.Home, F(13, "regular"), home, left - dot * 2 - P(8), ty, nameW, StringAlignment.Far);
        Dot(g, pal, x.HomeColor, left - dot - P(2), y + rowH / 2 - P(1), dot);
        // Before kickoff the middle holds the time it starts, where the score will be.
        if (x.State == "pre") Label(g, Kickoff(x), F(12, "regular"), pal.Fg2, mid, ty + P(0.5f), scoreW, StringAlignment.Center);
        else Label(g, Score(x), F(13, "semibold"), pal.Fg, mid, ty, scoreW, StringAlignment.Center);
        Dot(g, pal, x.AwayColor, rightX + dot + P(2), y + rowH / 2 - P(1), dot);
        Label(g, x.Away, F(13, "regular"), away, rightX + dot * 2 + P(8), ty, nameW, StringAlignment.Near);
    }

    // A small card: the one match that matters most, large.
    void DrawOne(Graphics g, Palette pal, Match x, DateTime now, float m)
    {
        float w = W - m * 2, dot = P(5);
        bool played = x.State != "pre";
        for (int i = 0; i < 2; i++)
        {
            float y = P(44) + P(30) * i;
            Dot(g, pal, i == 0 ? x.HomeColor : x.AwayColor, m + dot, y + P(11), dot);
            Label(g, i == 0 ? x.Home : x.Away, F(15, "semibold"), pal.Fg, m + dot * 2 + P(8), y, w - dot * 2 - P(8) - (played ? P(30) : 0), StringAlignment.Near);
            if (played) Label(g, (i == 0 ? x.HomeScore : x.AwayScore) ?? "", F(20, "semibold"), pal.Fg, W - m, y - P(4), P(30), StringAlignment.Far);
        }
        string when;
        DateTime local = x.Kickoff.ToLocalTime();
        if (x.Live) when = x.Detail;
        else if (x.State == "post") when = x.Done ? "Full time" : x.Detail;
        else if (local.Date == DateTime.Now.Date) when = "Today " + local.ToString("t", CultureInfo.CurrentCulture);
        else when = local.ToString("ddd d MMM", CultureInfo.CurrentCulture) + " · " + local.ToString("t", CultureInfo.CurrentCulture);
        Label(g, when, F(13, x.Live ? "semibold" : "regular"), x.Live ? pal.Red : pal.Fg2, m, P(112), w, StringAlignment.Near);
    }

    // Team colours mean nothing on a frosted card, where everything is white.
    void Dot(Graphics g, Palette pal, int rgb, float cx, float cy, float r)
    {
        if (IsVibrant) { FillCircle(g, pal.Fg2, cx, cy, r); return; }
        FillCircle(g, pal.Sep, cx, cy, r + P(0.75f)); // a rim, so a white or black kit still shows
        FillCircle(g, Rgb(rgb), cx, cy, r);
    }

    static string Score(Match x)
    {
        if (x.HomeScore == null || x.AwayScore == null) return "v";
        return x.HomeScore + " – " + x.AwayScore;
    }

    // Kickoff as it fits between two names: 20:30, or 8:30 where the clock is twelve-hour.
    static string Kickoff(Match x)
    {
        bool twelve = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("tt");
        return x.Kickoff.ToLocalTime().ToString(twelve ? "h:mm" : "HH:mm", CultureInfo.InvariantCulture);
    }

    // The minute while it is on, FT once it is over, the day before it starts.
    static string When(Match x, DateTime now)
    {
        if (x.Live) return x.Detail;
        if (x.State == "post") return x.Done ? "FT" : Short(x.Detail);
        DateTime local = x.Kickoff.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return "Today";
        return local.ToString("ddd d", CultureInfo.CurrentCulture);
    }

    static string Short(string s) { return s.Length > 6 ? s.Substring(0, 6) : s; }

    static List<Match> Sample(DateTime now)
    {
        DateTime today = now.ToLocalTime().Date.AddHours(12).ToUniversalTime();
        Func<string, string, string, string, string, string, int, int, DateTime, Match> make =
            (id, home, away, hs, aw, state, hc, ac, at) => new Match
            {
                Id = id, Home = home, Away = away, HomeScore = hs, AwayScore = aw, State = state, HomeColor = hc, AwayColor = ac,
                Kickoff = at, Detail = state == "in" ? "67'" : state == "post" ? "FT" : "", Done = state == "post"
            };
        return new List<Match>
        {
            make("1", "Arsenal", "Chelsea", "2", "1", "in", 0xE20520, 0x144992, now.AddMinutes(-80)),
            make("2", "Liverpool", "Man City", "1", "1", "in", 0xD11317, 0x99C5EA, now.AddMinutes(-80)),
            make("3", "Spurs", "Newcastle", "3", "0", "post", 0xFFFFFF, 0x000000, today.AddHours(-3)),
            make("4", "Aston Villa", "Brighton", null, null, "pre", 0x660E36, 0x0606FA, today.AddHours(9)),
            make("5", "Everton", "Man Utd", null, null, "pre", 0x0606FA, 0xDA020E, today.AddHours(31.5)),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}
