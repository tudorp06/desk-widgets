using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using DeskWidgets;
using DeskWidgets.Widgets;

// How long this PC has been in use today, in a dot bar and as hours and minutes.
// Windows keeps no screen-time figure, so the widget counts for itself: time adds up while it is
// running, the PC is unlocked, and the mouse or keyboard was used in the last five minutes
// (turn on "Keep Counting While Idle" for films and calls). The first fifteen columns of the bar
// are the daily limit; the last five light up red once you go past it. The count starts again
// at midnight and is kept in the widget's own settings. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Screen Time", Description = "Time this PC has been in use today, against a daily limit")]
public sealed class NothingScreenTime : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    const int TickMs = 15000;             // how often the count moves on
    const int IdleLimitMs = 5 * 60 * 1000;
    const int Columns = 20, LimitColumns = 15, Rows = 3;
    static readonly int[] Limits = { 4, 6, 8, 10, 12 };

    readonly Timer timer = new Timer { Interval = TickMs };
    double seconds;        // counted today
    string day;            // the day the count belongs to, yyyyMMdd
    int lastTick, savedMinute = -1;
    volatile bool locked;
    SessionSwitchEventHandler onSwitch;

    Font bigFont, unitFont, labelFont;
    float fontsFor = -1f;

    public NothingScreenTime(Section settings) : base(settings, false)
    {
        timer.Tick += delegate { Tick(); };
    }

    protected override bool SquareResize { get { return true; } }
    int LimitHours { get { return Math.Max(1, Settings.GetInt("limit", 8)); } }
    bool CountIdle { get { return Settings.GetBool("count_idle", false); } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    protected override void OnStart()
    {
        day = Settings.Get("day", "");
        seconds = Settings.GetInt("secs", 0);
        RollDay();
        savedMinute = (int)(seconds / 60);
        lastTick = Environment.TickCount;
        onSwitch = (s, e) =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
        };
        SystemEvents.SessionSwitch += onSwitch;
        timer.Start();
    }

    // A new day starts a new count.
    void RollDay()
    {
        string today = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (day == today) return;
        day = today;
        seconds = 0;
        savedMinute = 0;
        Settings.Set("day", day);
        Settings.Set("secs", 0);
    }

    void Tick()
    {
        int now = Environment.TickCount, elapsed = unchecked(now - lastTick);
        lastTick = now;
        string was = day;
        RollDay();
        // A gap much longer than the timer means the PC was asleep: that time doesn't count.
        bool inUse = !locked && (CountIdle || IdleMs() < IdleLimitMs);
        if (inUse && elapsed > 0 && elapsed < TickMs * 4) seconds += elapsed / 1000.0;
        int minute = (int)(seconds / 60);
        if (minute == savedMinute && was == day) return;
        savedMinute = minute;
        Settings.Set("secs", (int)seconds); // once a minute at most
        Render();
    }

    static int IdleMs()
    {
        var info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked(Environment.TickCount - (int)info.dwTime);
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var limit = menu.AddSubmenu("Daily Limit");
        int cur = LimitHours;
        foreach (int h in Limits)
        {
            int hours = h;
            limit.AddChoice(hours + " Hours", cur == hours, delegate { Settings.Set("limit", hours); Changed(); });
        }
        bool idle = CountIdle;
        menu.AddCheck("Keep Counting While Idle", idle, delegate { Settings.Set("count_idle", !idle); });
        menu.Add("Reset Today", delegate
        {
            seconds = 0;
            savedMinute = 0;
            Settings.Set("secs", 0);
            Render();
        });
    }

    // ---------- drawing: everything is a fraction of the card's side ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float s = Math.Min(W, H), m = s * 0.11f;
        EnsureFonts(s);
        bool vibrant = IsVibrant;
        Color ink = vibrant ? pal.Fg : IsDark ? DarkInk : LightInk;
        Color soft = vibrant ? pal.Fg2 : IsDark ? DarkSoft : LightSoft;
        Color lit = vibrant ? pal.Fg2 : ink, over = vibrant ? Color.White : SignalRed, unlit = Color.FromArgb(46, ink);

        // The library preview shows a day that has run past its limit.
        double secs = IsHandleCreated ? seconds : 9 * 3600 + 20 * 60;
        double share = secs / (LimitHours * 3600.0);
        int on = secs <= 0 ? 0 : Math.Max(1, Math.Min(Columns, (int)Math.Round(share * LimitColumns)));

        float cell = (s - m * 2) / Columns, d = cell * 0.84f, pad = (cell - d) / 2;
        using (var a = new SolidBrush(lit))
        using (var b = new SolidBrush(over))
        using (var c = new SolidBrush(unlit))
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                    g.FillEllipse(x >= on ? c : x >= LimitColumns ? b : a, m + x * cell + pad, m + y * cell + pad, d, d);

        // "6 H 20": hours and minutes large, the H between them small, all on one baseline.
        int minutes = (int)(secs / 60);
        string hours = (minutes / 60).ToString(CultureInfo.InvariantCulture), mins = (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        float labelH = labelFont.GetHeight(g), labelY = s - m - labelH;
        float top = labelY - s * 0.02f - bigFont.GetHeight(g);
        float unitTop = top + Ascent(bigFont) - Ascent(unitFont), gap = s * 0.012f, x0 = m - s * 0.01f;
        Label(g, hours, bigFont, ink, x0, top, s, StringAlignment.Near);
        x0 += TextWidth(g, hours, bigFont) + gap;
        Label(g, "H", unitFont, ink, x0, unitTop, s, StringAlignment.Near);
        x0 += TextWidth(g, "H", unitFont) + gap;
        Label(g, mins, bigFont, ink, x0, top, s, StringAlignment.Near);

        string label = share > 1 ? "OVER TODAY'S " + LimitHours + " H" : "TODAY";
        Label(g, label, labelFont, soft, m, labelY, s - m * 2, StringAlignment.Near);
    }

    static float Ascent(Font f)
    {
        return f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);
    }

    void EnsureFonts(float s)
    {
        if (bigFont != null && Math.Abs(s - fontsFor) < 0.01f) return;
        DisposeFonts();
        bigFont = new Font("Segoe UI Light", Math.Max(1f, s * 0.24f), FontStyle.Regular, GraphicsUnit.Pixel);
        unitFont = new Font("Segoe UI", Math.Max(1f, s * 0.12f), FontStyle.Regular, GraphicsUnit.Pixel);
        labelFont = new Font("Segoe UI", Math.Max(1f, s * 0.058f), FontStyle.Regular, GraphicsUnit.Pixel);
        fontsFor = s;
    }

    void DisposeFonts()
    {
        if (bigFont != null) { bigFont.Dispose(); bigFont = null; }
        if (unitFont != null) { unitFont.Dispose(); unitFont = null; }
        if (labelFont != null) { labelFont.Dispose(); labelFont = null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            if (onSwitch != null) { SystemEvents.SessionSwitch -= onSwitch; onSwitch = null; }
            if (day != null) Settings.Set("secs", (int)seconds); // the minutes counted since the last save
            DisposeFonts();
        }
        base.Dispose(disposing);
    }
}
