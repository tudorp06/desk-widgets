using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using DeskWidgets;
using DeskWidgets.Widgets;

// A race at a glance: the lap count, the driver you follow, the top three with their gaps and
// tyres, and a car in that driver's team colours riding a bar that fills as the race goes on.
// Timing comes from the same sources as F1 Timing: F1's own live feed, or OpenF1 when signed in.
[DeskWidget(Name = "Race Bar", Description = "Lap count, your driver and the top three, with a car on the lap bar")]
public sealed class RaceBarWidget : CardWidget
{
    const int LiveMs = 6000, IdleMs = 10 * 60 * 1000, LockedMs = 5 * 60 * 1000;

    F1.Board board;
    string status;
    bool busy;
    readonly F1.LiveState live = new F1.LiveState();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();

    F1LiveFeed feed;
    F1.Board feedBoard;
    int feedVersion;
    readonly System.Windows.Forms.Timer feedTick = new System.Windows.Forms.Timer { Interval = 1000 };

    static readonly object TokenLock = new object();
    static string token;
    static DateTime tokenExpires;

    public RaceBarWidget(Section settings) : base(settings, true)
    {
        poll.Tick += delegate { poll.Stop(); Fetch(); };
        feedTick.Tick += delegate { FeedTick(); };
    }

    bool SignedIn { get { return Settings.Get("of1-pass", "").Length > 0; } }
    // The three-letter code of the driver to follow; empty follows whoever leads.
    string Followed { get { return Settings.GetText("driver", "").Trim().ToUpperInvariant(); } }

    protected override void OnStart()
    {
        Fetch();
    }

    // ---------- data (the same flow as the F1 Timing widget) ----------

    static string Token(string user, string password, bool renew)
    {
        lock (TokenLock)
        {
            if (renew || token == null || DateTime.UtcNow > tokenExpires) token = F1.SignIn(user, password, out tokenExpires);
            return token;
        }
    }

    static bool Unauthorized(Exception ex)
    {
        var resp = ex is WebException ? ((WebException)ex).Response as HttpWebResponse : null;
        return resp != null && (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden);
    }

    void Fetch()
    {
        if (busy) return;
        if (board != null && board.Live && IsCovered()) { Schedule(LiveMs); return; }
        busy = true;
        string user = Settings.GetText("of1-user", "");
        string password = SignedIn ? PluginApi.Unprotect(Settings.Get("of1-pass", "")) : null;
        bool[] signInFailed = { false };
        Background(() =>
        {
            string tok = null;
            if (password != null)
                try { tok = Token(user, password, false); }
                catch (Exception) { signInFailed[0] = true; }
            try { return F1.FetchBoard(tok, live); }
            catch (Exception ex)
            {
                if (tok == null || !Unauthorized(ex)) throw;
                return F1.FetchBoard(Token(user, password, true), live);
            }
        }, (b, error) =>
        {
            busy = false;
            if (error != null) status = "Offline";
            else
            {
                board = b;
                status = signInFailed[0] ? "OpenF1 sign-in failed" : null;
                if (b != null && b.LiveLocked) StartFeed(); else StopFeed();
                Remember(b);
            }
            Render();
            Schedule(board != null && board.Live ? LiveMs : board != null && board.LiveLocked ? LockedMs : error != null ? LockedMs : IdleMs);
        });
    }

    void StartFeed()
    {
        if (feed != null) return;
        feed = new F1LiveFeed();
        feedVersion = 0;
        feedTick.Start();
    }

    void StopFeed()
    {
        if (feed == null) return;
        feedTick.Stop();
        feed.Dispose();
        feed = null;
        feedBoard = null;
    }

    void FeedTick()
    {
        if (feed == null) return;
        if (!feed.Running)
        {
            if (DateTime.UtcNow - feed.StoppedUtc > TimeSpan.FromSeconds(30))
            {
                var old = feed;
                feed = new F1LiveFeed();
                feedVersion = 0;
                old.Dispose();
            }
            return;
        }
        if (feed.Version == feedVersion || IsCovered()) return;
        feedVersion = feed.Version;
        feedBoard = feed.Snapshot();
        Remember(feedBoard);
        Render();
    }

    void Schedule(int ms)
    {
        poll.Stop();
        poll.Interval = ms;
        poll.Start();
    }

    // The board lists only the top five. When the followed driver is among them, keep their
    // number and team colour, so the badge and the car stay theirs after they drop out of it.
    void Remember(F1.Board b)
    {
        F1.Row r = Find(b, Followed);
        if (r == null) return;
        int rgb = r.Team.ToArgb() & 0xFFFFFF;
        if (Settings.GetInt("driver-rgb", -1) == rgb && Settings.GetInt("driver-num", 0) == r.Number) return;
        Settings.Set("driver-rgb", rgb.ToString(CultureInfo.InvariantCulture));
        Settings.Set("driver-num", r.Number.ToString(CultureInfo.InvariantCulture));
    }

    static F1.Row Find(F1.Board b, string code)
    {
        if (b == null || code.Length == 0) return null;
        foreach (F1.Row r in b.Rows) if (string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    // ---------- menu ----------

    void Follow()
    {
        var values = InputDialog.Ask("Follow a driver",
            "Their three letters on the timing screen, like LEC, NOR or VER. Leave it empty to follow whoever leads.",
            new[] { new InputDialog.Field { Label = "Driver", Value = Followed } }, "Follow");
        if (values == null) return;
        string input = values[0].Trim().ToUpperInvariant();
        if (input.Length > 3) input = input.Substring(0, 3);
        if (input == Followed) return;
        Settings.SetText("driver", input);
        Settings.Remove("driver-rgb");
        Settings.Remove("driver-num");
        Remember(feedBoard ?? board);
        Render();
    }

    void SignIn()
    {
        var values = InputDialog.Ask("OpenF1 account",
            "Live timing comes from F1's own feed, no account needed. If that stops working, a paid OpenF1 account " +
            "(openf1.org) is the alternative: sign in and the widget uses it instead. Your password is stored " +
            "encrypted for your Windows account only.",
            new[]
            {
                new InputDialog.Field { Label = "Username", Value = Settings.GetText("of1-user", "") },
                new InputDialog.Field { Label = "Password", Secret = true }
            }, "Sign In");
        if (values == null || values[0].Trim().Length == 0 || values[1].Length == 0) return;
        string user = values[0].Trim(), password = values[1];
        status = "Signing in…";
        Render();
        Background(() => Token(user, password, true), (tok, error) =>
        {
            if (error != null) { status = Unauthorized(error) ? "Wrong username or password" : "Offline"; Render(); return; }
            Settings.SetText("of1-user", user);
            Settings.Set("of1-pass", PluginApi.Protect(password));
            status = null;
            Fetch();
        });
    }

    void SignOut()
    {
        Settings.Remove("of1-pass");
        lock (TokenLock) token = null;
        Fetch();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Follow Driver…", Follow);
        if (SignedIn) menu.Add("Sign Out of OpenF1", SignOut);
        else menu.Add("Sign In to OpenF1…", SignIn);
        menu.Add("Refresh Now", Fetch);
    }

    // ---------- type: DIN-style digits (Bahnschrift ships with Windows 10 and later) ----------

    readonly Dictionary<string, Font> raceFonts = new Dictionary<string, Font>();
    float raceFontZoom;
    static string raceFamily;

    Font R(float pt)
    {
        if (raceFontZoom != Zoom)
        {
            foreach (var f in raceFonts.Values) f.Dispose();
            raceFonts.Clear();
            raceFontZoom = Zoom;
        }
        if (raceFamily == null)
            using (var probe = new Font("Bahnschrift", 10f))
                raceFamily = probe.Name == "Bahnschrift" ? "Bahnschrift" : "Segoe UI";
        string key = pt.ToString(CultureInfo.InvariantCulture);
        Font font;
        if (!raceFonts.TryGetValue(key, out font))
        {
            font = new Font(raceFamily, P(pt), FontStyle.Bold, GraphicsUnit.Pixel);
            raceFonts[key] = font;
        }
        return font;
    }

    // ---------- drawing ----------

    protected override void DrawContent(Graphics g, Palette pal)
    {
        bool preview = !IsHandleCreated;
        F1.Board b = preview ? Sample() : feedBoard ?? board;
        float m = P(16);
        if (b == null || b.Rows.Count == 0)
        {
            string msg = status ?? (b == null ? "Loading timing…" : "No results yet");
            Label(g, msg, F(15, "semibold"), pal.Fg, W / 2, H / 2 - P(12), W - m * 2, StringAlignment.Center);
            return;
        }

        string code = preview ? "LEC" : Followed;
        F1.Row mine = Find(b, code);
        if (code.Length == 0) mine = b.Rows[0];
        int savedRgb = preview ? -1 : Settings.GetInt("driver-rgb", -1);
        Color team = mine != null ? mine.Team : savedRgb >= 0 ? Rgb(savedRgb) : b.Rows[0].Team;
        int number = mine != null ? mine.Number : preview ? 0 : Settings.GetInt("driver-num", 0);

        // The lap count, or the session's name when it is not a race.
        int total = b.TotalLaps > 0 ? b.TotalLaps : !b.Live && b.Lap > 0 ? b.Lap : 0;
        if (b.IsRace && b.Lap > 0)
        {
            string lap = b.Lap.ToString("00", CultureInfo.InvariantCulture);
            Font big = R(46);
            Label(g, lap, big, pal.Fg, m - P(2), P(5));
            float lw = TextWidth(g, lap, big);
            Label(g, total > 0 ? "/" + total + " lap" : "lap", R(13), pal.Fg, m + lw + P(1), P(33));
        }
        else
        {
            Label(g, b.Session, R(22), pal.Fg, m, P(12), P(158), StringAlignment.Near);
            Label(g, b.Place, F(12, "regular"), pal.Fg2, m, P(40), P(158), StringAlignment.Near);
        }

        // The followed driver: number badge, code, and where they are.
        float by = P(70);
        FillRound(g, pal.Fill, new RectangleF(m, by, P(36), P(36)), P(9));
        string badge = number > 0 ? number.ToString(CultureInfo.InvariantCulture) : mine != null ? "P" + mine.Position : "–";
        Label(g, badge, R(badge.Length > 2 ? 15 : 19), IsVibrant ? pal.Fg : team, m + P(18), by + (badge.Length > 2 ? P(10) : P(8)), P(36), StringAlignment.Center);
        Label(g, mine != null ? mine.Code : code, R(15), pal.Fg, m + P(46), by + P(1), P(100), StringAlignment.Near);
        string where = mine == null ? "Outside the top five"
            : mine.Position == 1 ? "P1 · " + (b.Live ? "Leading" : "Winner")
            : "P" + mine.Position + (string.IsNullOrEmpty(mine.Value) ? "" : " · " + mine.Value);
        if (!b.IsRace && mine != null) where = "P" + mine.Position + (string.IsNullOrEmpty(mine.Value) ? "" : " · " + mine.Value);
        Label(g, status ?? where, F(12, "regular"), pal.Fg2, m + P(46), by + P(19), P(118), StringAlignment.Near);

        // The top three.
        float x0 = P(190);
        for (int i = 0; i < Math.Min(3, b.Rows.Count); i++)
        {
            F1.Row r = b.Rows[i];
            float cy = P(28 + 34 * i);
            Label(g, r.Position.ToString(CultureInfo.InvariantCulture), R(15), pal.Fg, x0 + P(12), cy - P(9), P(20), StringAlignment.Far);
            FillRound(g, IsVibrant ? pal.Fg : r.Team, new RectangleF(x0 + P(19), cy - P(7), P(4), P(14)), P(2));
            Label(g, r.Code, R(16), pal.Fg, x0 + P(30), cy - P(10), P(50), StringAlignment.Near);
            float right = W - m;
            if (r.Tyre != '\0') { DrawTyre(g, pal, r.Tyre, right - P(8.5f), cy); right -= P(25); }
            Label(g, r.Value ?? "", F(12, "regular"), pal.Fg2, right, cy - P(8), P(72), StringAlignment.Far);
        }

        // The lap bar, and the car at the end of what has been run.
        float bx = m, bw = W - m * 2, bh = P(10), barY = P(143);
        FillRound(g, pal.Fill, new RectangleF(bx, barY, bw, bh), bh / 2);
        float done = !b.IsRace ? (b.Live ? 0f : 1f) : total > 0 ? Math.Min(1f, b.Lap / (float)total) : 0f;
        float end = Math.Max(bh, bw * done);
        Color go = IsVibrant ? pal.Fg : IsDark ? Rgb(0x2FD6C9) : Rgb(0x00A396);
        if (done > 0f) FillRound(g, go, new RectangleF(bx, barY, end, bh), bh / 2);
        float carLen = P(94);
        float carX = bx + Math.Max(0f, Math.Min(bw - carLen, (done > 0f ? end : 0f) - P(20)));
        DrawCar(g, carX, barY + bh / 2, carLen, LiveryFor(team));
    }

    void DrawTyre(Graphics g, Palette pal, char c, float cx, float cy)
    {
        Color ring = c == 'S' ? pal.Red : c == 'M' ? pal.Yellow : c == 'I' ? pal.Green : c == 'W' ? pal.Blue
            : IsDark ? pal.Fg : pal.Fg2;
        float r = P(8f);
        using (var pen = new Pen(ring, P(2f))) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        Label(g, c.ToString(), F(10, "semibold"), pal.Fg, cx, cy - P(6.5f), r * 2, StringAlignment.Center);
    }

    // ---------- the car ----------

    // A team's paint: the main colour, the second colour and a small accent. Colours only, in the
    // spirit of each team's car; no badges, sponsors or lettering.
    sealed class Livery { public Color Body, Second, Accent; }

    static Livery Coat(int body, int second, int accent)
    {
        return new Livery { Body = Rgb(body), Second = Rgb(second), Accent = Rgb(accent) };
    }

    static readonly Dictionary<string, Livery> Liveries = new Dictionary<string, Livery>
    {
        { "mclaren", Coat(0xFF8000, 0x16171A, 0x47C7FC) },
        { "ferrari", Coat(0xDA0A16, 0xFFFFFF, 0xFFE600) },
        { "red_bull", Coat(0x14204A, 0xD4002A, 0xFFC906) },
        { "mercedes", Coat(0xC6CACD, 0x101113, 0x00D7B6) },
        { "williams", Coat(0x0A2A86, 0x64C4FF, 0xFFFFFF) },
        { "aston_martin", Coat(0x00594F, 0x0E1A18, 0xCEDC00) },
        { "alpine", Coat(0x0A6FD0, 0xFF87BC, 0xFFFFFF) },
        { "haas", Coat(0xF2F2F2, 0x15161A, 0xE6002B) },
        { "rb", Coat(0xF4F5F7, 0x1B33C4, 0xE4002B) },
        { "audi", Coat(0xB9BCBF, 0x121214, 0xF50537) },
        { "sauber", Coat(0x101311, 0x52E252, 0xFFFFFF) },
        { "cadillac", Coat(0x0F0F11, 0xECECEC, 0x9A9A9E) }
    };

    // The car keeps its paint in every appearance, Vibrant included: it is a picture of the car,
    // where the bars and the badge beside it are accents and turn white.
    // The timing data names a team only by its colour, so the livery is the one whose team colour
    // is nearest. A colour that matches no team gets a plain car in that colour.
    Livery LiveryFor(Color team)
    {
        Livery best = null;
        int bestDistance = 60 * 60 * 3;
        foreach (var kv in Liveries)
        {
            Color c = F1.TeamColor(kv.Key);
            int dr = c.R - team.R, dg = c.G - team.G, db = c.B - team.B, d = dr * dr + dg * dg + db * db;
            if (d < bestDistance) { bestDistance = d; best = kv.Value; }
        }
        return best ?? new Livery { Body = team, Second = Rgb(0x16171A), Accent = Color.White };
    }

    static PointF Pt(double x, double y) { return new PointF((float)x, (float)y); }

    // A shape drawn for the top half of the car and mirrored onto the bottom half: the car is 100
    // units long and 40 wide, nose to the right, with its centre line at y = 20.
    static GraphicsPath Mirrored(PointF[] top, byte[] types)
    {
        var points = new PointF[top.Length * 2];
        var kinds = new byte[top.Length * 2];
        for (int i = 0; i < top.Length; i++)
        {
            points[i] = top[i];
            kinds[i] = types[i];
            points[top.Length * 2 - 1 - i] = new PointF(top[i].X, 40f - top[i].Y);
        }
        // Walking the mirrored half backwards, each segment takes the type of the point it leaves.
        for (int i = 0; i < top.Length; i++)
        {
            int at = top.Length + i, from = top.Length - i;
            kinds[at] = i == 0 ? (byte)1 : types[from];
        }
        kinds[top.Length * 2 - 1] |= 0x80;
        return new GraphicsPath(points, kinds);
    }

    void DrawCar(Graphics g, float x, float cy, float length, Livery paint)
    {
        var state = g.Save();
        float s = length / 100f;
        g.TranslateTransform(x, cy - 20f * s);
        g.ScaleTransform(s, s);

        Color carbon = IsVibrant ? Color.FromArgb(170, 22, 26, 30) : IsDark ? Rgb(0x34353A) : Rgb(0x1E1F23);
        Color rubber = IsVibrant ? Color.FromArgb(215, 14, 16, 18) : IsDark ? Rgb(0x0A0A0B) : Rgb(0x1A1B1E);
        Color rim = IsVibrant ? Color.FromArgb(120, 255, 255, 255) : IsDark ? Rgb(0x7C7D83) : Rgb(0x2A2B2F);

        // Floor: the wide carbon plank the body sits on.
        using (var floor = new GraphicsPath())
        using (var b = new SolidBrush(carbon))
        {
            floor.AddPolygon(new[] { Pt(15, 12), Pt(30, 8.2), Pt(60, 8.6), Pt(67, 14.5), Pt(67, 25.5), Pt(60, 31.4), Pt(30, 31.8), Pt(15, 28) });
            g.FillPath(b, floor);
        }

        // Rear wing: two planes between end plates.
        FillRound(g, paint.Second, new RectangleF(0.5f, 7.5f, 3.6f, 25f), 1f);
        FillRound(g, paint.Body, new RectangleF(4.8f, 7.5f, 3.2f, 25f), 1f);
        using (var plane = RoundRect(new RectangleF(0.5f, 7.5f, 7.5f, 25f), 1f))
        using (var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 0.6f)) g.DrawPath(edge, plane);
        FillRound(g, paint.Accent, new RectangleF(0f, 5.6f, 9.5f, 2.2f), 0.9f);
        FillRound(g, paint.Accent, new RectangleF(0f, 32.2f, 9.5f, 2.2f), 0.9f);

        // Front wing: a swept main plane, a flap behind it, and end plates.
        using (var wing = Mirrored(new[] { Pt(92.5, 20), Pt(92.2, 12), Pt(90.6, 5.4), Pt(95.2, 5.4), Pt(98.4, 12), Pt(99.2, 20) },
            new byte[] { 0, 1, 1, 1, 1, 1 }))
        using (var b = new SolidBrush(paint.Second))
        using (var edge = new Pen(Color.FromArgb(70, 0, 0, 0), 0.6f)) { g.FillPath(b, wing); g.DrawPath(edge, wing); }
        using (var flap = Mirrored(new[] { Pt(90.4, 20), Pt(90, 12.5), Pt(88.6, 6.2), Pt(90.2, 6.2), Pt(91.8, 12.5), Pt(92.1, 20) },
            new byte[] { 0, 1, 1, 1, 1, 1 }))
        using (var b = new SolidBrush(paint.Body)) g.FillPath(b, flap);
        FillRound(g, paint.Accent, new RectangleF(87.6f, 3.6f, 9.4f, 2f), 0.8f);
        FillRound(g, paint.Accent, new RectangleF(87.6f, 34.4f, 9.4f, 2f), 0.8f);

        // Suspension arms, under the body and over the floor.
        using (var arm = new Pen(carbon, 1.1f))
        {
            foreach (float flip in new[] { 1f, -1f })
            {
                Func<double, float> y = v => 20f + flip * (float)(v - 20);
                g.DrawLine(arm, 73.5f, y(11), 70f, y(17.4));
                g.DrawLine(arm, 77.5f, y(11), 80f, y(17.8));
                g.DrawLine(arm, 81.5f, y(11), 86f, y(18.4));
                g.DrawLine(arm, 14f, y(11.5), 19f, y(16.6));
                g.DrawLine(arm, 21f, y(11.5), 27f, y(15.6));
            }
        }

        // Tyres: wide at the back, narrower in front, each with the shine of its shoulder.
        foreach (RectangleF t in new[] { new RectangleF(8.5f, 0.6f, 17.5f, 11f), new RectangleF(8.5f, 28.4f, 17.5f, 11f),
            new RectangleF(70f, 1.8f, 15.5f, 9.6f), new RectangleF(70f, 28.6f, 15.5f, 9.6f) })
        {
            FillRound(g, rubber, t, 3.2f);
            using (var path = RoundRect(t, 3.2f))
            using (var pen = new Pen(rim, 0.7f)) g.DrawPath(pen, path);
            using (var pen = new Pen(Color.FromArgb(IsVibrant ? 60 : 38, 255, 255, 255), 1.1f))
                g.DrawLine(pen, t.Left + 3f, t.Top + t.Height / 2, t.Right - 3f, t.Top + t.Height / 2);
        }

        // Body: gearbox, the coke-bottle waist, the sidepods, the chassis and the long nose.
        using (var body = Mirrored(new[]
            {
                Pt(7, 20), Pt(7, 17.6), Pt(17, 17.2),
                Pt(24, 16.8), Pt(27, 10.6), Pt(37, 10.2),          // out to the sidepod
                Pt(53, 10.6),
                Pt(58.5, 10.8), Pt(60.5, 14), Pt(63, 15.8),         // the inlet, back in to the chassis
                Pt(70, 16.6),
                Pt(80, 17.5), Pt(90, 18.5), Pt(96.2, 19.1),         // the nose
                Pt(97.6, 19.3), Pt(97.8, 19.7), Pt(97.8, 20)
            },
            new byte[] { 0, 1, 1, 3, 3, 3, 1, 3, 3, 3, 1, 3, 3, 3, 3, 3, 3 }))
        {
            using (var b = new SolidBrush(paint.Body)) g.FillPath(b, body);
            // A soft edge all round, so the body reads as rounded rather than cut from paper.
            using (var pen = new Pen(Color.FromArgb(IsVibrant ? 40 : 70, 0, 0, 0), 0.9f)) g.DrawPath(pen, body);
        }
        // The dark mouths of the sidepod inlets, and the airbox behind the driver's head.
        using (var b = new SolidBrush(Color.FromArgb(IsVibrant ? 120 : 190, 10, 10, 12)))
        {
            g.FillPolygon(b, new[] { Pt(56.6, 11.4), Pt(59.6, 12.2), Pt(61.6, 14.8), Pt(58.2, 14.2) });
            g.FillPolygon(b, new[] { Pt(56.6, 28.6), Pt(59.6, 27.8), Pt(61.6, 25.2), Pt(58.2, 25.8) });
            g.FillEllipse(b, 41.6f, 18.4f, 4.4f, 3.2f);
        }

        // Paint: a stripe down the engine cover, a line along each sidepod, and the nose tip.
        using (var spine = Mirrored(new[] { Pt(8, 20), Pt(8, 18.5), Pt(40, 17.2), Pt(46, 18.4), Pt(46, 20) }, new byte[] { 0, 1, 1, 1, 1 }))
        using (var b = new SolidBrush(paint.Second)) g.FillPath(b, spine);
        using (var pen = new Pen(paint.Second, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, 31f, 12.4f, 54f, 12.7f);
            g.DrawLine(pen, 31f, 27.6f, 54f, 27.3f);
        }
        using (var tip = Mirrored(new[] { Pt(91, 20), Pt(91, 18.6), Pt(96.2, 19.1), Pt(97.6, 19.3), Pt(97.8, 19.7), Pt(97.8, 20) },
            new byte[] { 0, 1, 1, 3, 3, 3 }))
        using (var b = new SolidBrush(paint.Accent)) g.FillPath(b, tip);
        using (var pen = new Pen(paint.Accent, 1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, 66f, 20f, 88f, 20f);
        // Mirrors.
        FillRound(g, paint.Body, new RectangleF(59.4f, 11.8f, 2.8f, 1.6f), 0.6f);
        FillRound(g, paint.Body, new RectangleF(59.4f, 26.6f, 2.8f, 1.6f), 0.6f);

        // Cockpit, the driver's helmet, and the halo over them.
        using (var b = new SolidBrush(IsVibrant ? Color.FromArgb(210, 14, 16, 18) : Rgb(0x0B0B0D)))
            g.FillEllipse(b, 46.5f, 16.5f, 13f, 7f);
        FillCircle(g, paint.Accent, 52.2f, 20f, 2.5f);
        using (var b = new SolidBrush(Color.FromArgb(200, 12, 12, 14))) g.FillRectangle(b, 53.2f, 19.3f, 1.4f, 1.4f);
        using (var pen = new Pen(IsVibrant ? Color.FromArgb(230, 40, 44, 48) : Rgb(0x3A3B40), 1.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawBezier(pen, 47.6f, 17.4f, 53f, 15.6f, 58f, 17.6f, 59.6f, 20f);
            g.DrawBezier(pen, 47.6f, 22.6f, 53f, 24.4f, 58f, 22.4f, 59.6f, 20f);
            g.DrawLine(pen, 59.6f, 20f, 62.6f, 20f);
        }

        g.Restore(state);
    }

    // ---------- the picture shown in the library and the gallery ----------

    static F1.Board Sample()
    {
        var b = new F1.Board { Session = "Race", Place = "Montréal", Live = true, IsRace = true, Lap = 8, TotalLaps = 70 };
        object[][] rows =
        {
            new object[] { 4, "NOR", "Norris", "mclaren", 'M', "Leader" }, new object[] { 1, "VER", "Verstappen", "red_bull", 'M', "+0.225" },
            new object[] { 81, "PIA", "Piastri", "mclaren", 'H', "+1.842" }, new object[] { 16, "LEC", "Leclerc", "ferrari", 'M', "+3.412" },
            new object[] { 63, "RUS", "Russell", "mercedes", 'H', "+0.871" }
        };
        for (int i = 0; i < rows.Length; i++)
            b.Rows.Add(new F1.Row
            {
                Number = (int)rows[i][0], Position = i + 1, Code = (string)rows[i][1], Family = (string)rows[i][2],
                Team = F1.TeamColor((string)rows[i][3]), Tyre = (char)rows[i][4], Value = (string)rows[i][5]
            });
        return b;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopFeed();
            poll.Dispose();
            feedTick.Dispose();
            foreach (var f in raceFonts.Values) f.Dispose();
            raceFonts.Clear();
        }
        base.Dispose(disposing);
    }
}
