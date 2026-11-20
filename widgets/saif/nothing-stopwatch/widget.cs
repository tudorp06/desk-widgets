using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// A stopwatch in dot numerals: click the card to start and stop, take a lap or reset it from the
// right-click menu. The red dot blinks while it runs. The count survives a restart, so a
// stopwatch left running keeps counting. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Stopwatch", Description = "Time something: click to start or stop, take a lap from the menu")]
public sealed class NothingStopwatch : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    Font labelFont;
    float fontFor = -1f;

    public NothingStopwatch(Section settings) : base(settings, false) { }

    protected override bool SquareResize { get { return true; } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    // ---------- the count ----------
    // "ms" is what has been counted already; "since" is when the current run started (0 when stopped),
    // both in the widget's settings so the stopwatch is the same after a restart.
    long Counted { get { return Number("ms"); } set { Settings.Set("ms", value); } }
    long Since { get { return Number("since"); } set { Settings.Set("since", value); } }
    bool Running { get { return Since != 0; } }

    // Settings hold text, and these numbers are too large for a float: tick counts need a long.
    long Number(string key)
    {
        long v;
        return long.TryParse(Settings.Get(key, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    long Value()
    {
        if (!Running) return Counted;
        long ms = (long)(DateTime.UtcNow - new DateTime(Since, DateTimeKind.Utc)).TotalMilliseconds;
        return Counted + Math.Max(0, ms);
    }

    void Start()
    {
        Since = DateTime.UtcNow.Ticks;
        Changed();
    }

    void Stop()
    {
        Counted = Value();
        Since = 0;
        Changed();
    }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated && Running ? 70 : -1;   // tenths while it runs, nothing while it waits
    }

    protected override void OnWidgetClick(Point p)
    {
        if (Running) Stop(); else Start();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add(Running ? "Stop" : Value() > 0 ? "Resume" : "Start", delegate { if (Running) Stop(); else Start(); });
        if (Value() > 0)
        {
            menu.Add("Take a Lap", delegate { Settings.Set("lap", Value()); Changed(); });
            menu.Add("Reset", delegate { Settings.Set("ms", 0); Settings.Set("since", 0); Settings.Remove("lap"); Changed(); });
        }
    }

    // mm:ss.t under an hour, h:mm:ss over it - the longer reading only when it is needed.
    static string Clock(long ms)
    {
        long tenths = ms / 100;
        int t = (int)(tenths % 10), s = (int)(tenths / 10 % 60), m = (int)(tenths / 600 % 60), h = (int)(tenths / 36000);
        if (h > 0) return h.ToString(CultureInfo.InvariantCulture) + ":" + Two(m) + ":" + Two(s);
        return Two(m) + ":" + Two(s) + "." + t.ToString(CultureInfo.InvariantCulture);
    }

    static string Two(int n) { return n.ToString("00", CultureInfo.InvariantCulture); }

    // ---------- drawing: everything is a fraction of the card's side ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float s = Math.Min(W, H), m = s * 0.11f;
        bool vibrant = IsVibrant, preview = !IsHandleCreated;
        Color ink = vibrant ? pal.Fg : IsDark ? DarkInk : LightInk;
        Color soft = vibrant ? pal.Fg2 : IsDark ? DarkSoft : LightSoft;
        if (labelFont == null || Math.Abs(s - fontFor) > 0.01f)
        {
            if (labelFont != null) labelFont.Dispose();
            labelFont = new Font("Segoe UI", Math.Max(1f, s * 0.058f), FontStyle.Regular, GraphicsUnit.Pixel);
            fontFor = s;
        }
        float labelH = labelFont.GetHeight(g);
        long value = preview ? 12400 : Value();
        bool running = preview || Running;

        Label(g, "STOPWATCH", labelFont, soft, m, m - labelH * 0.2f, s * 0.6f, StringAlignment.Near);

        // The run light: red while it counts, and off for half of every second so it reads as alive.
        float dot = s * 0.023f;
        bool lit = !running || (DateTime.Now.Millisecond < 500);
        using (var b = new SolidBrush(!running ? Color.FromArgb(46, ink) : lit ? (vibrant ? pal.Fg : SignalRed) : Color.FromArgb(46, ink)))
            g.FillEllipse(b, s - m - dot * 2, m + labelH * 0.1f, dot * 2, dot * 2);

        // The reading, as large as the card's width allows.
        string text = Clock(value);
        float cell = (s - m * 2) / Math.Max(1f, Columns(text));
        cell = Math.Min(cell, s * 0.044f);
        DotText(g, ink, text, m, s * 0.5f - cell * 3.5f, cell);

        string hint = running ? "CLICK TO STOP" : value > 0 ? "CLICK TO RESUME" : "CLICK TO START";
        float bottom = s - m - labelH * 0.9f;
        Label(g, hint, labelFont, soft, m, bottom, s * 0.62f, StringAlignment.Near);
        long lap = Number("lap");
        if (preview) lap = 7300;
        if (lap > 0) Label(g, "LAP " + Clock(lap), labelFont, soft, s - m, bottom, s * 0.5f, StringAlignment.Far);
    }

    // ---------- dot numerals: 7 dots tall, 5 wide, one column between characters ----------
    static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        { '0', new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
        { '1', new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." } },
        { '2', new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" } },
        { '3', new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." } },
        { '4', new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." } },
        { '5', new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." } },
        { '6', new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." } },
        { '7', new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." } },
        { '8', new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." } },
        { '9', new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." } },
        { ':', new[] { ".", ".", "#", ".", ".", "#", "." } },
        { '.', new[] { ".", ".", ".", ".", ".", ".", "#" } }
    };

    // How many dot columns a string takes, including the gap between characters.
    static float Columns(string text)
    {
        float w = 0;
        foreach (char ch in text)
        {
            string[] rows;
            if (!Glyphs.TryGetValue(ch, out rows)) continue;
            w += rows[0].Length + 1;
        }
        return Math.Max(1f, w - 1);
    }

    // Draws text in dots with its top-left at (x, y); characters without a glyph are skipped.
    static void DotText(Graphics g, Color c, string text, float x, float y, float cell)
    {
        float d = cell * 0.84f, pad = (cell - d) / 2;
        using (var b = new SolidBrush(c))
            foreach (char ch in text)
            {
                string[] rows;
                if (!Glyphs.TryGetValue(ch, out rows)) continue;
                for (int r = 0; r < rows.Length; r++)
                    for (int i = 0; i < rows[r].Length; i++)
                        if (rows[r][i] == '#') g.FillEllipse(b, x + i * cell + pad, y + r * cell + pad, d, d);
                x += (rows[0].Length + 1) * cell;
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && labelFont != null) { labelFont.Dispose(); labelFont = null; }
        base.Dispose(disposing);
    }
}
