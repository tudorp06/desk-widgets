using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using DeskWidgets;
using DeskWidgets.Widgets;

// Three rings for the day at the desk, the way the Fitness widget stacks them: time at the PC,
// breaks taken away from it, and minutes inside a long undisturbed run. Each ring has its glyph
// and reading under it, so the rings still read when a face has no colour. It counts how long
// since the last key or mouse move (GetLastInputInfo) and keeps the totals for today only.
//
// Nobody types through a film, so no input on its own is not enough to call it a break: a minute
// with something playing through the Windows media session, or a full-screen app in front, still
// counts as time at the desk. Past two hours of that without a key or a mouse move it stops
// believing you are there - otherwise an album left playing overnight would close the ring.
[DeskWidget(Name = "Day Rings", Description = "Close three rings for desk time, breaks and focus")]
public sealed class DayRings : CardWidget
{
    const int BreakMinutes = 5;     // away this long and it counts as a break
    const int FocusRun = 25;        // a run this long and the minutes in it count as focus
    const int WatchCap = 120;       // a film's worth: past this, playback alone no longer counts

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // Windows' own "do not interrupt" reading: a full-screen app, a game or a presentation.
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    int away;                       // minutes in the current stretch away from the PC
    int quiet;                      // minutes since the last key or mouse, playing or not
    DateTime countedAt = DateTime.MinValue;
    MediaSession media;             // opened in OnStart, never for a library preview
    bool playing;

    public DayRings(Section settings) : base(settings, false) { }

    protected override bool SquareResize { get { return true; } }

    int Goal(string key, int fallback) { return Math.Max(1, Settings.GetInt("goal" + key, fallback)); }
    int Today(string key) { return Settings.GetInt(key, 0); }

    bool CountsWatching { get { return Settings.GetBool("watching", true); } }

    protected override void OnStart()
    {
        RollOver();
        try
        {
            media = MediaSession.Open();
            if (media != null) { media.Changed += OnMedia; ReadMedia(); }
        }
        catch (Exception) { media = null; }
        Count();
    }

    void OnMedia()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)delegate { if (!IsDisposed) ReadMedia(); }); }
        catch (InvalidOperationException) { }
    }

    void ReadMedia()
    {
        if (media == null) { playing = false; return; }
        MediaSession.Snapshot now = media.Read();
        playing = now != null && now.Playing;
    }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated ? 60 * 1000 : -1;
    }

    protected override void Draw(Graphics g)
    {
        if (IsHandleCreated) Count();
        base.Draw(g);
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Set Goals…", delegate
        {
            string[] answer = InputDialog.Ask("Day Rings", "What a full day looks like for you.",
                new[]
                {
                    new InputDialog.Field { Label = "Hours at the desk", Value = (Goal("desk", 480) / 60).ToString(CultureInfo.InvariantCulture) },
                    new InputDialog.Field { Label = "Breaks", Value = Goal("breaks", 8).ToString(CultureInfo.InvariantCulture) },
                    new InputDialog.Field { Label = "Hours of focus", Value = (Goal("focus", 240) / 60).ToString(CultureInfo.InvariantCulture) }
                }, "Save");
            if (answer == null) return;
            Settings.Set("goaldesk", Clamp(answer[0], 1, 24) * 60);
            Settings.Set("goalbreaks", Clamp(answer[1], 1, 40));
            Settings.Set("goalfocus", Clamp(answer[2], 1, 24) * 60);
            Changed();
        });
        bool watching = CountsWatching;
        menu.AddCheck("Count Playback as Desk Time", watching, delegate { Settings.Set("watching", !watching); Changed(); });
        menu.Add("Start Today Again", delegate
        {
            Settings.Set("desk", 0); Settings.Set("breaks", 0); Settings.Set("focus", 0); Settings.Set("run", 0);
            Changed();
        });
    }

    static int Clamp(string text, int low, int high)
    {
        int v;
        if (!int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) v = low;
        return Math.Max(low, Math.Min(high, v));
    }

    // ---------- counting ----------
    static int IdleSeconds()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)(unchecked((uint)Environment.TickCount - info.dwTime) / 1000);
    }

    void RollOver()
    {
        string today = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (Settings.Get("day", "") == today) return;
        Settings.Set("day", today);
        Settings.Set("desk", 0);
        Settings.Set("breaks", 0);
        Settings.Set("focus", 0);
        Settings.Set("run", 0);
        away = 0;
        quiet = 0;
    }

    // Something is playing, or an app has the whole screen: somebody is watching it.
    bool Watching()
    {
        if (!CountsWatching) return false;
        int state;
        if (SHQueryUserNotificationState(out state) == 0 &&
            (state == QUNS_BUSY || state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
            return true;
        return playing;
    }

    // One tick a minute: at the desk or away, and what that does to the run and the breaks.
    void Count()
    {
        RollOver();
        DateTime now = DateTime.UtcNow;
        if (countedAt != DateTime.MinValue && (now - countedAt).TotalSeconds < 50) return;
        bool first = countedAt == DateTime.MinValue;
        countedAt = now;
        if (first) return;

        bool atDesk = IdleSeconds() < 60;
        quiet = atDesk ? 0 : quiet + 1;

        if (atDesk)
        {
            away = 0;
            Settings.Set("desk", Today("desk") + 1);
            int run = Today("run") + 1;
            Settings.Set("run", run);
            if (run == FocusRun) Settings.Set("focus", Today("focus") + FocusRun);
            else if (run > FocusRun) Settings.Set("focus", Today("focus") + 1);
        }
        else if (quiet <= WatchCap && Watching())
        {
            // Here, but not working: the minute counts and no break is logged, and the focus run
            // still resets, because the focus ring counts minutes you had your hands on the keys.
            away = 0;
            Settings.Set("desk", Today("desk") + 1);
            Settings.Set("run", 0);
        }
        else
        {
            Settings.Set("run", 0);
            away++;
            if (away == BreakMinutes) Settings.Set("breaks", Today("breaks") + 1);
        }
    }

    static string Hours(int minutes)
    {
        if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " m";
        return (minutes / 60).ToString(CultureInfo.InvariantCulture) + " h " +
               (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + " m";
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16);
        Font label = F(11, "semibold"), small = F(11, null);
        bool preview = !IsHandleCreated;

        int desk = preview ? 312 : Today("desk"), breaks = preview ? 6 : Today("breaks"), focus = preview ? 160 : Today("focus");
        int deskGoal = Goal("desk", 480), breakGoal = Goal("breaks", 8), focusGoal = Goal("focus", 240);

        Label(g, "TODAY", label, pal.Fg2, m, m, W * 0.5f, StringAlignment.Near);
        Label(g, DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture), label, pal.Fg2, W - m, m, W * 0.45f, StringAlignment.Far);

        float legendHeight = small.GetHeight(g) * 3.2f;
        float top = m + label.GetHeight(g) * 1.5f, bottom = H - m - legendHeight;
        float radius = Math.Max(P(18), Math.Min((bottom - top) / 2, W * 0.26f));
        float cx = W / 2f, cy = (top + bottom) / 2;
        float stroke = radius * 0.235f;

        var values = new[] { (float)desk / deskGoal, (float)breaks / breakGoal, (float)focus / focusGoal };
        var colours = new[] { pal.Red, pal.Green, pal.Blue };
        for (int i = 0; i < 3; i++)
        {
            float r = radius - i * stroke * 1.32f;
            if (r < stroke) break;
            using (var track = new Pen(pal.Fill, stroke))
                g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);
            float part = Math.Max(0, Math.Min(1, values[i]));
            if (part > 0)
                using (var arc = new Pen(colours[i], stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, -90, 360 * part);
        }

        // The legend: a glyph in the ring's colour, what it counts, and where it has reached.
        var names = new[] { "At the desk", "Breaks", "Focus" };
        var readings = new[] { Hours(desk), breaks.ToString(CultureInfo.InvariantCulture) + " of " + breakGoal.ToString(CultureInfo.InvariantCulture), Hours(focus) };
        float lineHeight = small.GetHeight(g) * 1.07f, y = H - m - lineHeight * 3;
        for (int i = 0; i < 3; i++)
        {
            float middle = y + lineHeight * i + small.GetHeight(g) / 2;
            Glyph(g, i, colours[i], m + P(4), middle, P(4.5f));
            Label(g, names[i], small, pal.Fg2, m + P(13), y + lineHeight * i, W * 0.46f, StringAlignment.Near);
            Label(g, readings[i], small, pal.Fg, W - m, y + lineHeight * i, W * 0.42f, StringAlignment.Far);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && media != null)
        {
            media.Changed -= OnMedia;
            media.Dispose();
            media = null;
        }
        base.Dispose(disposing);
    }

    // Desk, break and focus marks, small enough to sit in a line of 11 pt text.
    static void Glyph(Graphics g, int which, Color c, float cx, float cy, float r)
    {
        using (var pen = new Pen(c, Math.Max(1f, r * 0.34f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (which == 0)
            {
                g.DrawRectangle(pen, cx - r, cy - r * 0.8f, r * 2, r * 1.25f);
                g.DrawLine(pen, cx - r * 1.3f, cy + r * 0.85f, cx + r * 1.3f, cy + r * 0.85f);
            }
            else if (which == 1)
            {
                g.DrawArc(pen, cx - r, cy - r * 0.6f, r * 1.6f, r * 1.8f, 0, 180);
                g.DrawLine(pen, cx + r * 0.6f, cy - r * 0.4f, cx + r * 1.2f, cy + r * 0.2f);
            }
            else
            {
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                g.DrawEllipse(pen, cx - r * 0.3f, cy - r * 0.3f, r * 0.6f, r * 0.6f);
            }
        }
    }
}
