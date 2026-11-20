using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using DeskWidgets;
using DeskWidgets.Widgets;

// This PC's charge in dot numerals over a block of dots, with what the battery is doing and how
// long it has left. Under 20 % on battery the lit dots turn red. Windows is asked directly
// (GetSystemPowerStatus); nothing is fetched and nothing is sent.
[DeskWidget(Name = "Nothing Battery", Description = "Watch this PC's charge in dots, with the state and the time left")]
public sealed class NothingBattery : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    const int Columns = 20, Rows = 2, LowAt = 20;

    // The reading is set either in dot numerals or solid, in Nothing's own grotesque when it is
    // installed. The dots are the default: they are what the rest of the card is made of.
    static readonly string Grotesque = PickFont("NType 82", "NType82", "Segoe UI Variable Display", "Segoe UI", "Arial");

    [StructLayout(LayoutKind.Sequential)]
    struct POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out POWER_STATUS status);

    Font labelFont;
    float fontFor = -1f;

    public NothingBattery(Section settings) : base(settings, false) { }

    protected override bool SquareResize { get { return true; } }

    bool Solid { get { return Settings.Get("numerals", "dots") == "solid"; } }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        bool solid = Solid;
        var numerals = menu.AddSubmenu("Numerals");
        numerals.AddChoice("Dot Matrix", !solid, delegate { Settings.Set("numerals", "dots"); Changed(); });
        numerals.AddChoice("Solid", solid, delegate { Settings.Set("numerals", "solid"); Changed(); });
    }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated ? 30 * 1000 : -1;
    }

    // "4 H 20 MIN LEFT" from the seconds Windows reports, or null when it has no estimate yet.
    static string Remaining(int seconds)
    {
        if (seconds <= 0) return null;
        int minutes = seconds / 60;
        if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " MIN LEFT";
        return (minutes / 60).ToString(CultureInfo.InvariantCulture) + " H " +
               (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + " MIN LEFT";
    }

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

        // The call is skipped for a library preview, so the struct starts out empty either way.
        POWER_STATUS power = new POWER_STATUS();
        bool read = false;
        if (!preview) read = GetSystemPowerStatus(out power);
        int percent = preview ? 82 : read ? power.BatteryLifePercent : (byte)255;
        bool known = percent <= 100;
        bool charging = preview || (read && (power.BatteryFlag & 8) != 0);
        bool plugged = preview || (read && power.ACLineStatus == 1);
        bool noBattery = !preview && read && ((power.BatteryFlag & 128) != 0 || !known);
        bool low = known && !plugged && percent <= LowAt;

        string state = noBattery ? "NO BATTERY" : charging ? "CHARGING" : plugged ? "PLUGGED IN" : low ? "LOW" : "ON BATTERY";
        Label(g, "BATTERY", labelFont, soft, m, m - labelH * 0.2f, s * 0.5f, StringAlignment.Near);
        Label(g, state, labelFont, low ? (vibrant ? pal.Fg : SignalRed) : soft, s - m, m - labelH * 0.2f, s * 0.55f, StringAlignment.Far);

        // The reading, then the block of dots that fills from the left with the charge.
        string reading = noBattery ? "AC" : known ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "--";
        if (Solid) SolidText(g, ink, reading, m, s * 0.37f, s - m * 2, s * 0.34f);
        else
        {
            float cell = Math.Min(s * 0.05f, (s - m * 2) / Width(reading));
            DotText(g, ink, reading, m, s * 0.37f - cell * 3.5f, cell);
        }

        int lit = known && !noBattery ? Math.Max(1, (int)Math.Round(percent / 100.0 * Columns)) : 0;
        float bar = (s - m * 2) / Columns, d = bar * 0.84f, pad = (bar - d) / 2, top = s * 0.62f;
        using (var on = new SolidBrush(low && !vibrant ? SignalRed : ink))
        using (var off = new SolidBrush(Color.FromArgb(46, ink)))
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                    g.FillEllipse(x < lit ? on : off, m + x * bar + pad, top + y * bar + pad, d, d);

        string note = noBattery ? "RUNNING ON MAINS"
            : charging ? "CHARGING TO FULL"
            : read && !preview ? (Remaining(power.BatteryLifeTime) ?? "TIME LEFT UNKNOWN")
            : "3 H 10 MIN LEFT";
        Label(g, note, labelFont, soft, m, s - m - labelH * 0.9f, s - m * 2, StringAlignment.Near);
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
        { 'A', new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
        { 'C', new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." } },
        { '%', new[] { "##..#", "##.#.", "...#.", "..#..", ".#...", ".#.##", "#..##" } },
        { '-', new[] { ".....", ".....", ".....", "#####", ".....", ".....", "....." } }
    };

    static float Width(string text)
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

    // ---------- the same reading set solid ----------
    // An outline at a nominal size, scaled to the band the dot numerals fill and centred on the
    // same line, so switching between the two moves nothing else on the card.
    static void SolidText(Graphics g, Color c, string text, float x, float middle, float maxWidth, float maxHeight)
    {
        using (var family = new FontFamily(Grotesque))
        using (var path = new GraphicsPath())
        {
            path.AddString(text, family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            float scale = Math.Min(maxWidth / b.Width, maxHeight / b.Height);
            using (var fit = new Matrix())
            {
                fit.Translate(x - b.X * scale, middle - (b.Y + b.Height / 2) * scale);
                fit.Scale(scale, scale);
                path.Transform(fit);
            }
            using (var brush = new SolidBrush(c)) g.FillPath(brush, path);
        }
    }

    static string PickFont(params string[] wanted)
    {
        using (var installed = new InstalledFontCollection())
        {
            foreach (string name in wanted)
                foreach (FontFamily f in installed.Families)
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return wanted[wanted.Length - 1];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && labelFont != null) { labelFont.Dispose(); labelFont = null; }
        base.Dispose(disposing);
    }
}
