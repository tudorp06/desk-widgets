using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// How fast this PC is downloading and uploading right now: the download rate in dot numerals,
// the last twenty seconds as a row of dot columns, and the upload rate.
// The figures come from Windows' own byte counters for the network adapters, read once a
// second; the busiest adapter is shown, so a virtual adapter that mirrors the real one is not
// counted twice. No traffic is inspected and nothing is sent anywhere.
[DeskWidget(Name = "Nothing Net Speed", Description = "Download and upload speed, with a short history in dots")]
public sealed class NothingNetSpeed : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);

    const int Columns = 20, Rows = 4;
    const double QuietRate = 64 * 1024; // the history's scale never drops below this, so idle chatter stays flat

    readonly Timer sample = new Timer { Interval = 1000 };
    readonly Stopwatch watch = new Stopwatch();
    readonly List<double> history = new List<double>(); // download, bytes per second, oldest first
    Dictionary<string, long[]> last;                    // adapter id -> { received, sent }
    NetworkInterface[] adapters;                        // worker thread only
    int adaptersAge;
    double down, up;
    bool busy;
    Font labelFont;
    float fontFor = -1f;

    public NothingNetSpeed(Section settings) : base(settings, false)
    {
        sample.Tick += delegate { Sample(); };
    }

    protected override bool SquareResize { get { return true; } }
    protected override string DefaultFace { get { return "light"; } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    protected override void OnStart()
    {
        watch.Start();
        sample.Start();
        Sample();
    }

    // ---------- data ----------
    void Sample()
    {
        if (busy) return;
        busy = true;
        Background<Dictionary<string, long[]>>(ReadTotals, (totals, error) =>
        {
            busy = false;
            if (error != null || totals == null) return;
            double secs = watch.Elapsed.TotalSeconds;
            watch.Restart();
            if (last != null && secs > 0.2)
            {
                long bestDown = 0, bestUp = 0;
                foreach (var pair in totals)
                {
                    long[] before;
                    if (!last.TryGetValue(pair.Key, out before)) continue;
                    long d = pair.Value[0] - before[0], u = pair.Value[1] - before[1];
                    if (d < 0 || u < 0) continue; // the adapter's counters started again
                    if (d + u > bestDown + bestUp) { bestDown = d; bestUp = u; }
                }
                down = bestDown / secs;
                up = bestUp / secs;
                history.Add(down);
                if (history.Count > Columns) history.RemoveAt(0);
                if (!IsCovered()) Render();
            }
            last = totals;
        });
    }

    // Worker thread: total bytes received and sent by each adapter that is up. The list of
    // adapters is looked up again every half minute, so a new connection is picked up.
    Dictionary<string, long[]> ReadTotals()
    {
        if (adapters == null || ++adaptersAge > 30)
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
            adaptersAge = 0;
        }
        var totals = new Dictionary<string, long[]>();
        foreach (NetworkInterface n in adapters)
        {
            try
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                IPv4InterfaceStatistics st = n.GetIPv4Statistics();
                totals[n.Id] = new[] { st.BytesReceived, st.BytesSent };
            }
            catch (Exception) { } // an adapter that was just removed
        }
        return totals;
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
        double downNow = preview ? 2.4 * 1048576 : down, upNow = preview ? 320 * 1024 : up;
        IList<double> past = preview ? (IList<double>)SampleHistory : history;
        float labelH = labelFont.GetHeight(g);

        // Download: the number large, its unit at half the size on the same baseline.
        Label(g, "DOWNLOAD", labelFont, soft, m, m - labelH * 0.2f, s - m * 2, StringAlignment.Near);
        string unit, number = Rate(downNow, out unit);
        float big = s * 0.024f, small = big / 2, numberY = m + labelH * 0.8f + s * 0.035f;
        float x = DotText(g, ink, number, m, numberY, big);
        DotText(g, ink, " " + unit, x, numberY + 7 * (big - small), small);

        // History: one column a second, newest on the right, scaled to the busiest second shown.
        double peak = QuietRate;
        foreach (double v in past) peak = Math.Max(peak, v);
        float cell = (s - m * 2) / Columns, d = cell * 0.84f, pad = (cell - d) / 2;
        float bottom = s - m - labelH - s * 0.05f;
        using (var b = new SolidBrush(soft))
            for (int i = 0; i < Columns; i++)
            {
                int at = past.Count - Columns + i;
                int tall = at < 0 ? 1 : 1 + (int)Math.Round((Rows - 1) * Math.Min(1.0, past[at] / peak));
                for (int r = 0; r < tall; r++)
                    g.FillEllipse(b, m + i * cell + pad, bottom - (r + 1) * cell + pad, d, d);
            }

        // Upload: label on the left, rate in small dots on the right.
        float labelY = s - m - labelH * 0.9f, tiny = s * 0.011f;
        Label(g, "UPLOAD", labelFont, soft, m, labelY, s * 0.4f, StringAlignment.Near);
        string upText = Rate(upNow, out unit) + " " + unit;
        DotText(g, ink, upText, s - m - DotWidth(upText, tiny), labelY + labelH * 0.55f - tiny * 3.5f, tiny);
    }

    // A rate in bytes per second as a number and a unit: "320" KB/S, "2.4" MB/S.
    static string Rate(double bytesPerSecond, out string unit)
    {
        double kb = bytesPerSecond / 1024;
        if (kb < 1000) { unit = "KB/S"; return Math.Round(kb).ToString(CultureInfo.InvariantCulture); }
        double mb = kb / 1024;
        if (mb < 1000) { unit = "MB/S"; return mb.ToString(mb < 100 ? "0.0" : "0", CultureInfo.InvariantCulture); }
        unit = "GB/S";
        return (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture);
    }

    // Library preview.
    static readonly double[] SampleHistory = MakeSampleHistory();

    static double[] MakeSampleHistory()
    {
        int[] tall = { 1, 2, 1, 2, 3, 2, 1, 1, 2, 3, 4, 3, 2, 1, 1, 2, 3, 2, 1, 2 };
        var h = new double[tall.Length];
        for (int i = 0; i < tall.Length; i++) h[i] = (tall[i] - 1) / 3.0 * 2.4 * 1048576;
        return h;
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
        { 'K', new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" } },
        { 'M', new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" } },
        { 'S', new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." } },
        { '/', new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." } },
        { '.', new[] { ".", ".", ".", ".", ".", ".", "#" } },
        { ' ', new[] { "..", "..", "..", "..", "..", "..", ".." } }
    };

    // Draws text in dots with its top-left at (x, y) and returns where the next character would
    // start; characters without a glyph are skipped.
    static float DotText(Graphics g, Color c, string text, float x, float y, float cell)
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
        return x;
    }

    static float DotWidth(string text, float cell)
    {
        float w = 0;
        foreach (char ch in text)
        {
            string[] rows;
            if (Glyphs.TryGetValue(ch, out rows)) w += (rows[0].Length + 1) * cell;
        }
        return Math.Max(0, w - cell);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sample.Dispose();
            if (labelFont != null) { labelFont.Dispose(); labelFont = null; }
        }
        base.Dispose(disposing);
    }
}
