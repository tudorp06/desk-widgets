using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using DeskWidgets;
using DeskWidgets.Widgets;

// Three cities side by side, each with its time in dots and how far it is from yours; a city
// ahead of you is marked in red. The cities are picked from the right-click menu and read from
// Windows' own time zones, so nothing is fetched and nothing is sent.
[DeskWidget(Name = "Nothing World Clock", Description = "See three cities at once, their times in dots and how far they are from yours")]
public sealed class NothingWorldClock : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    // A city and the Windows time zone it keeps. One zone per hour band people actually use.
    static readonly string[][] Cities =
    {
        new[] { "Los Angeles", "Pacific Standard Time" },
        new[] { "Chicago", "Central Standard Time" },
        new[] { "New York", "Eastern Standard Time" },
        new[] { "Sao Paulo", "E. South America Standard Time" },
        new[] { "London", "GMT Standard Time" },
        new[] { "Paris", "W. Europe Standard Time" },
        new[] { "Moscow", "Russian Standard Time" },
        new[] { "Dubai", "Arabian Standard Time" },
        new[] { "Mumbai", "India Standard Time" },
        new[] { "Singapore", "Singapore Standard Time" },
        new[] { "Tokyo", "Tokyo Standard Time" },
        new[] { "Sydney", "AUS Eastern Standard Time" }
    };
    static readonly string[] Defaults = { "London", "New York", "Tokyo" };

    Font labelFont;
    float fontFor = -1f;

    public NothingWorldClock(Section settings) : base(settings, true) { }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    string City(int slot) { return Settings.Get("city" + slot, Defaults[slot]); }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated ? 10 * 1000 : -1;   // the minute is the smallest thing shown
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        for (int slot = 0; slot < 3; slot++)
        {
            var list = menu.AddSubmenu("City " + (slot + 1));
            string current = City(slot);
            int which = slot;
            foreach (string[] city in Cities)
            {
                string name = city[0];
                list.AddChoice(name, name == current, delegate { Settings.Set("city" + which, name); Changed(); });
            }
        }
    }

    // The time in a city, or null when Windows does not know that zone.
    static DateTime? TimeIn(string city, DateTime utc)
    {
        foreach (string[] c in Cities)
            if (c[0] == city)
                try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(c[1])); }
                catch (Exception) { return null; }
        return null;
    }

    // "+5:30 H" ahead, "-4 H" behind, "SAME TIME" where the clocks agree.
    static string Offset(DateTime there, DateTime here)
    {
        int minutes = (int)Math.Round((there - here).TotalMinutes / 5) * 5;
        if (minutes == 0) return "SAME TIME";
        int a = Math.Abs(minutes);
        string hours = (a / 60).ToString(CultureInfo.InvariantCulture);
        string rest = a % 60 != 0 ? ":" + (a % 60).ToString("00", CultureInfo.InvariantCulture) : "";
        return (minutes > 0 ? "+" : "-") + hours + rest + " H";
    }

    // ---------- drawing: everything is a fraction of the card's height ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float s = Math.Min(W, H), m = s * 0.11f;
        bool vibrant = IsVibrant, preview = !IsHandleCreated;
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

        DateTime utc = DateTime.UtcNow, here = utc.ToLocalTime();
        Label(g, "WORLD CLOCK", labelFont, soft, m, m - labelH * 0.2f, W * 0.5f, StringAlignment.Near);
        Label(g, "HERE " + here.ToString("HH:mm", CultureInfo.InvariantCulture), labelFont, soft,
            W - m, m - labelH * 0.2f, W * 0.4f, StringAlignment.Far);

        float gap = s * 0.06f, column = (W - m * 2 - gap * 2) / 3;
        for (int slot = 0; slot < 3; slot++)
        {
            string city = City(slot);
            DateTime? there = TimeIn(city, utc);
            float x = m + slot * (column + gap);

            Label(g, city.ToUpperInvariant(), labelFont, soft, x, m + labelH * 1.9f, column, StringAlignment.Near);

            string text = there.HasValue ? there.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "--:--";
            // Short of the column's full width, so the three readings never run together.
            float cell = Math.Min(s * 0.045f, column * 0.86f / Columns("00:00"));
            DotText(g, ink, text, x, H * 0.52f - cell * 3.5f, cell);

            string offset = there.HasValue ? Offset(there.Value, here) : "UNKNOWN ZONE";
            bool ahead = there.HasValue && there.Value > here.AddMinutes(2);
            Label(g, offset, labelFont, ahead ? accent : soft, x, H - m - labelH * 0.9f, column, StringAlignment.Near);
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
        { '9', new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." } },
        { ':', new[] { ".", ".", "#", ".", ".", "#", "." } },
        { '-', new[] { ".....", ".....", ".....", "#####", ".....", ".....", "....." } }
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
