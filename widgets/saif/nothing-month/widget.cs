using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using DeskWidgets;
using DeskWidgets.Widgets;

// Today's date in large dots beside the whole month as a grid of them: the days gone are dim,
// today is red and the rest are ink. Weeks start on Monday. It reads the clock and nothing else.
[DeskWidget(Name = "Nothing Month", Description = "See the whole month as dots, today in red beside the date")]
public sealed class NothingMonth : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    static readonly string[] Months = { "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY",
        "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER" };
    static readonly string[] Days = { "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY", "SUNDAY" };
    static readonly string[] Initials = { "M", "T", "W", "T", "F", "S", "S" };

    Font labelFont;
    float fontFor = -1f;

    public NothingMonth(Section settings) : base(settings, true) { }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated ? 60 * 1000 : -1;   // only midnight changes anything
    }

    // Monday is column 0, the way a month is printed here.
    static int Weekday(DateTime d) { return ((int)d.DayOfWeek + 6) % 7; }

    // ---------- drawing: everything is a fraction of the card's height ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float s = Math.Min(W, H), m = s * 0.11f;
        bool vibrant = IsVibrant;
        Color ink = vibrant ? pal.Fg : IsDark ? DarkInk : LightInk;
        Color soft = vibrant ? pal.Fg2 : IsDark ? DarkSoft : LightSoft;
        Color accent = vibrant ? pal.Fg : SignalRed;
        if (labelFont == null || Math.Abs(s - fontFor) > 0.01f)
        {
            if (labelFont != null) labelFont.Dispose();
            labelFont = new Font("Segoe UI", Math.Max(1f, s * 0.058f), FontStyle.Regular, GraphicsUnit.Pixel);
            fontFor = s;
        }
        float labelH = labelFont.GetHeight(g);

        DateTime today = DateTime.Today;
        float left = m, leftWidth = Math.Min(W * 0.36f, s * 0.78f);

        Label(g, Months[today.Month - 1] + " " + today.Year.ToString(CultureInfo.InvariantCulture),
            labelFont, soft, left, m - labelH * 0.2f, leftWidth, StringAlignment.Near);

        string number = today.Day.ToString(CultureInfo.InvariantCulture);
        float cell = Math.Min(s * 0.095f, leftWidth / Columns(number));
        DotText(g, ink, number, left, H * 0.5f - cell * 3.5f, cell);

        Label(g, Days[Weekday(today)], labelFont, soft, left, H - m - labelH * 0.9f, leftWidth, StringAlignment.Near);

        // The month: one dot a day, seven across, starting on the weekday the first falls on.
        float gridLeft = left + leftWidth + s * 0.07f, gridWidth = W - m - gridLeft;
        if (gridWidth < s * 0.3f) return;
        DateTime first = new DateTime(today.Year, today.Month, 1);
        int lead = Weekday(first), days = DateTime.DaysInMonth(today.Year, today.Month);
        int rows = (int)Math.Ceiling((lead + days) / 7.0);
        float gridTop = m + labelH * 1.7f, gridHeight = H - m - gridTop;
        float step = Math.Min(gridWidth / 7f, gridHeight / rows);
        float originX = gridLeft + (gridWidth - step * 7) / 2, originY = gridTop + (gridHeight - step * rows) / 2;

        for (int i = 0; i < 7; i++)
            Label(g, Initials[i], labelFont, soft, originX + step * (i + 0.5f), m - labelH * 0.2f, step, StringAlignment.Center);

        using (var past = new SolidBrush(Color.FromArgb(46, ink)))
        using (var rest = new SolidBrush(ink))
        using (var now = new SolidBrush(accent))
            for (int day = 1; day <= days; day++)
            {
                int index = lead + day - 1;
                float cx = originX + (index % 7 + 0.5f) * step, cy = originY + (index / 7 + 0.5f) * step;
                bool isToday = day == today.Day;
                float r = step * (isToday ? 0.34f : 0.26f);
                g.FillEllipse(isToday ? now : day < today.Day ? past : rest, cx - r, cy - r, r * 2, r * 2);
            }
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
        { '9', new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." } }
    };

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
