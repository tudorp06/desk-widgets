using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// Free space on one drive: a block of dots where the red ones are the used part, the free
// space in dot numerals, and the drive's size. The drive is chosen from the right-click menu
// (the Windows drive to begin with) and read once a minute. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Storage", Description = "Free space on a drive, in dots")]
public sealed class NothingStorage : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    const int Columns = 20, Rows = 4;

    readonly Timer refresh = new Timer { Interval = 60 * 1000 };
    long free = -1, total;
    bool busy, missing;
    Font labelFont;
    float fontFor = -1f;

    public NothingStorage(Section settings) : base(settings, false)
    {
        refresh.Tick += delegate { Fetch(); };
    }

    protected override bool SquareResize { get { return true; } }
    string Drive { get { return Settings.GetText("drive", Path.GetPathRoot(Environment.SystemDirectory)); } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    protected override void OnStart()
    {
        refresh.Start();
        Fetch();
    }

    // A drive that has gone to sleep or lives on the network can take a while to answer.
    void Fetch()
    {
        if (busy) return;
        busy = true;
        string drive = Drive;
        Background(() =>
        {
            var d = new DriveInfo(drive);
            return d.IsReady ? new[] { d.AvailableFreeSpace, d.TotalSize } : null;
        }, (sizes, error) =>
        {
            busy = false;
            missing = error != null || sizes == null || sizes[1] <= 0;
            if (!missing) { free = sizes[0]; total = sizes[1]; }
            Render();
            if (drive != Drive) Fetch(); // the drive was changed while this one was being read
        });
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var drives = menu.AddSubmenu("Drive");
        string cur = Drive;
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
            string root = d.Name;
            drives.AddChoice(root.TrimEnd('\\'), string.Equals(root, cur, StringComparison.OrdinalIgnoreCase),
                delegate { Settings.SetText("drive", root); free = -1; Fetch(); });
        }
        menu.Add("Refresh Now", Fetch);
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
        long freeNow = preview ? 212L << 30 : free, totalNow = preview ? 476L << 30 : total;
        bool known = freeNow >= 0 && totalNow > 0 && (preview || !missing);
        float labelH = labelFont.GetHeight(g);

        Label(g, "STORAGE", labelFont, soft, m, m - labelH * 0.2f, s * 0.5f, StringAlignment.Near);
        Label(g, (preview ? "C:" : Drive.TrimEnd('\\')).ToUpperInvariant(), labelFont, soft, s - m, m - labelH * 0.2f, s * 0.3f, StringAlignment.Far);

        // Used space fills the block from the left in red; what is free stays in the ink colour.
        int used = known ? (int)Math.Round((1.0 - (double)freeNow / totalNow) * Columns) : 0;
        if (known && freeNow < totalNow) used = Math.Max(1, used);
        float cell = (s - m * 2) / Columns, d = cell * 0.84f, pad = (cell - d) / 2, top = s * 0.5f - Rows * cell / 2 - s * 0.03f;
        using (var a = new SolidBrush(vibrant ? Color.White : SignalRed))
        using (var b = new SolidBrush(known ? (vibrant ? Color.FromArgb(90, 255, 255, 255) : ink) : Color.FromArgb(46, ink)))
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                    g.FillEllipse(x < used ? a : b, m + x * cell + pad, top + y * cell + pad, d, d);

        float dot = s * 0.019f, labelY = s - m - labelH * 0.9f;
        if (known)
        {
            DotText(g, ink, Amount(freeNow), m, labelY - s * 0.03f - dot * 7, dot);
            Label(g, "FREE OF " + Amount(totalNow), labelFont, soft, m, labelY, s - m * 2, StringAlignment.Near);
        }
        else Label(g, missing ? "DRIVE NOT FOUND" : "READING…", labelFont, soft, m, labelY, s - m * 2, StringAlignment.Near);
    }

    // Sizes as Windows shows them: 1 GB is 1024 MB.
    static string Amount(long bytes)
    {
        double gb = bytes / 1073741824.0;
        if (gb >= 1000) return (gb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
        if (gb >= 10) return Math.Round(gb).ToString(CultureInfo.InvariantCulture) + " GB";
        if (gb >= 1) return gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        return Math.Round(bytes / 1048576.0).ToString(CultureInfo.InvariantCulture) + " MB";
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
        { 'B', new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." } },
        { 'G', new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####" } },
        { 'M', new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" } },
        { 'T', new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." } },
        { '.', new[] { ".", ".", ".", ".", ".", ".", "#" } },
        { ' ', new[] { "..", "..", "..", "..", "..", "..", ".." } }
    };

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
        if (disposing)
        {
            refresh.Dispose();
            if (labelFont != null) { labelFont.Dispose(); labelFont = null; }
        }
        base.Dispose(disposing);
    }
}
