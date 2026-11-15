// Minesweeper for Desk Widgets. Left click opens a square, right click flags it, clicking an
// open number whose flags are all placed opens its neighbours. Clear a board to move up a level:
// each level is a bigger board with more mines. The game is saved after every move, so it comes
// back exactly as it was after a restart. Right-click outside the board for the menu; Appearance
// switches between Windows XP, Liquid Glass and Clean.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Minesweeper", Description = "Minesweeper with levels, in XP, Liquid Glass or Clean style")]
public sealed class Minesweeper : CardWidget
{
    const int MaxSize = 14;       // 14 x 14 is the biggest board that stays easy to click
    const double IdleLimit = 60;  // the clock stops after a minute without a click

    int level = 1, n, mines;
    bool[] mine, open, flag;
    bool started, lost, won;
    int boom = -1;
    double elapsed;               // seconds actually played, kept across restarts
    DateTime lastTick, lastInput; // both start at MinValue: the clock waits for the first click
    readonly Random rng = new Random();

    public Minesweeper(Section s) : base(s, CardSize.Large)
    {
        if (!Restore()) NewGame(1);
    }

    // ---- levels ----
    static int SizeFor(int lvl) { return Math.Min(MaxSize, 8 + lvl); }

    // Level 1 is the classic 9 x 9 with 10 mines; every level after gets a little denser.
    static int MinesFor(int lvl)
    {
        int s = SizeFor(lvl);
        return (int)Math.Round(s * s * Math.Min(0.25, 0.12 + 0.01 * (lvl - 1)));
    }

    void NewGame(int lvl)
    {
        level = Math.Max(1, lvl);
        n = SizeFor(level);
        mines = MinesFor(level);
        mine = new bool[n * n];
        open = new bool[n * n];
        flag = new bool[n * n];
        started = lost = won = false;
        boom = -1;
        elapsed = 0;
    }

    // ---- saving: one settings value, "level;seconds;state;boom;cells" ----
    // Each cell is one digit: 1 = mine, 2 = open, 4 = flagged, added together.
    void Save()
    {
        var cells = new StringBuilder(n * n);
        for (int i = 0; i < n * n; i++)
            cells.Append((char)('0' + (mine[i] ? 1 : 0) + (open[i] ? 2 : 0) + (flag[i] ? 4 : 0)));
        int state = won ? 3 : lost ? 2 : started ? 1 : 0;
        Settings.Set("game", level + ";" + (int)elapsed + ";" + state + ";" + boom + ";" + cells);
    }

    bool Restore()
    {
        string[] p = Settings.Get("game", "").Split(';');
        int lvl, secs, state, b;
        if (p.Length != 5 || !int.TryParse(p[0], out lvl) || !int.TryParse(p[1], out secs) ||
            !int.TryParse(p[2], out state) || !int.TryParse(p[3], out b)) return false;
        NewGame(lvl);
        if (p[4].Length != n * n) { NewGame(1); return false; }
        for (int i = 0; i < n * n; i++)
        {
            int c = p[4][i] - '0';
            if (c < 0 || c > 7) { NewGame(1); return false; }
            mine[i] = (c & 1) != 0;
            open[i] = (c & 2) != 0;
            flag[i] = (c & 4) != 0;
        }
        started = state >= 1;
        lost = state == 2;
        won = state == 3;
        boom = b;
        elapsed = Math.Max(0, secs);
        return true;
    }

    // ---- styles ----
    protected override string DefaultFace { get { return "xp"; } }

    protected override string[][] AppearanceChoices
    {
        get { return new[] { new[] { "xp", "Windows XP" }, new[] { "vibrant", "Liquid Glass" }, new[] { "light", "Clean" } }; }
    }

    bool Xp { get { return Face == "xp"; } }

    // Number colours, index 1..8. XP keeps the classic ones; glass uses soft tints that read on blur.
    static readonly Color[] XpNumbers = { Color.Empty, Rgb(0x0000FF), Rgb(0x008000), Rgb(0xFF0000), Rgb(0x000080),
        Rgb(0x800000), Rgb(0x008080), Rgb(0x000000), Rgb(0x808080) };
    static readonly Color[] GlassNumbers = { Color.Empty, Rgb(0x9AD8FF), Rgb(0xA6F2B0), Rgb(0xFFA3A3), Rgb(0xD2B4FF),
        Rgb(0xFFCB94), Rgb(0x94F2E8), Color.White, Rgb(0xDDDDDD) };
    static readonly Color XpGray = Rgb(0xC0C0C0), XpDark = Rgb(0x808080);

    Color NumberColor(int k, Palette pal)
    {
        if (Xp) return XpNumbers[k];
        if (IsVibrant) return GlassNumbers[k];
        Color[] clean = { Color.Empty, pal.Blue, pal.Green, pal.Red, pal.Purple, pal.Orange, pal.Blue, pal.Fg, pal.Fg2 };
        return clean[k];
    }

    // ---- layout ----
    float Pad { get { return P(Xp ? 12 : 16); } }
    RectangleF Header() { return new RectangleF(Pad, P(Xp ? 32 : 16), W - 2 * Pad, P(40)); }

    RectangleF Board()
    {
        float top = P(Xp ? 82 : 64);
        float cell = Math.Min((W - 2 * Pad) / n, (H - top - Pad) / n);
        return new RectangleF((W - cell * n) / 2, top, cell * n, cell * n);
    }

    RectangleF FaceButton()
    {
        RectangleF h = Header();
        float s = P(32);
        return new RectangleF(h.X + (h.Width - s) / 2, h.Y + (h.Height - s) / 2, s, s);
    }

    int CellAt(Point p)
    {
        RectangleF b = Board();
        if (!b.Contains(p)) return -1;
        float c = b.Width / n;
        int x = (int)((p.X - b.X) / c), y = (int)((p.Y - b.Y) / c);
        return x < n && y < n ? y * n + x : -1;
    }

    // ---- game ----
    // Mines go in after the first click, never on or next to it, so the first click always opens an area.
    void PlaceMines(int first)
    {
        int fx = first % n, fy = first / n, placed = 0;
        while (placed < mines)
        {
            int i = rng.Next(n * n);
            if (mine[i] || (Math.Abs(i % n - fx) <= 1 && Math.Abs(i / n - fy) <= 1)) continue;
            mine[i] = true;
            placed++;
        }
        started = true;
    }

    List<int> Around(int i)
    {
        var list = new List<int>(8);
        int x = i % n, y = i / n;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < n && ny < n) list.Add(ny * n + nx);
            }
        return list;
    }

    int Count(int i, bool[] what)
    {
        int k = 0;
        foreach (int j in Around(i)) if (what[j]) k++;
        return k;
    }

    void Reveal(int i)
    {
        var todo = new Stack<int>();
        todo.Push(i);
        while (todo.Count > 0)
        {
            int k = todo.Pop();
            if (open[k] || flag[k]) continue;
            open[k] = true;
            if (mine[k]) { lost = true; boom = k; return; }
            if (Count(k, mine) == 0) foreach (int j in Around(k)) todo.Push(j);
        }
        int opened = 0;
        foreach (bool o in open) if (o) opened++;
        if (opened == n * n - mines)
        {
            won = true;
            for (int k = 0; k < mine.Length; k++) flag[k] = mine[k];
        }
    }

    // Clicking an open number with exactly that many flags around it opens the rest.
    void Chord(int i)
    {
        int k = Count(i, mine);
        if (k == 0 || Count(i, flag) != k) return;
        foreach (int j in Around(i)) if (!lost) Reveal(j);
    }

    int MinesLeft()
    {
        int f = 0;
        foreach (bool b in flag) if (b) f++;
        return mines - f;
    }

    bool Playing { get { return started && !lost && !won; } }
    bool Idle { get { return (DateTime.Now - lastInput).TotalSeconds >= IdleLimit; } }

    // Adds the time since the last frame while someone is playing. Gaps over two seconds (PC locked,
    // asleep, switched off) and idle stretches don't count, so coming back finds the same time.
    void Tick()
    {
        DateTime now = DateTime.Now;
        if (Playing && !Idle)
        {
            double d = (now - lastTick).TotalSeconds;
            if (d > 0 && d < 2) elapsed = Math.Min(999, elapsed + d);
        }
        lastTick = now;
    }

    // ---- input ----
    protected override bool IsButton(Point p) { return CellAt(p) >= 0 || FaceButton().Contains(p); }

    protected override void OnWidgetClick(Point p)
    {
        int i = CellAt(p);
        bool onFace = FaceButton().Contains(p);
        if (!onFace && i < 0) return;
        Tick();
        lastInput = lastTick = DateTime.Now;

        if (won && (onFace || i >= 0)) NewGame(level + 1);  // board cleared: next level
        else if (lost || onFace) NewGame(level);            // lost, or asked for a fresh board
        else if (!flag[i])
        {
            if (!started) PlaceMines(i);
            if (open[i]) Chord(i); else Reveal(i);
        }
        Save();
        Render();
    }

    // Right click on the board flags during a game; anywhere else it opens the usual menu.
    protected override void OnMouseUp(MouseEventArgs e)
    {
        int i = e.Button == MouseButtons.Right ? CellAt(e.Location) : -1;
        if (i < 0 || lost || won) { base.OnMouseUp(e); return; }
        if (open[i]) return;
        Tick();
        lastInput = lastTick = DateTime.Now;
        flag[i] = !flag[i];
        Save();
        Render();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("New Game", delegate { NewGame(level); Save(); Render(); });
        menu.AddDestructive("Start Over from Level 1", delegate { NewGame(1); Save(); Render(); });
    }

    // Tick once a second while a game runs. After a minute without a click, save the time and stop.
    protected override int NextFrameDelay()
    {
        if (!Playing) return -1;
        if (Idle) { Save(); return -1; }
        return 1000 - DateTime.Now.Millisecond;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && mine != null) Save(); // app closing: keep the last seconds
        base.Dispose(disposing);
    }

    // ---- drawing ----
    static readonly StringFormat Centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    protected override Color CardBackground(Palette pal) { return Xp ? XpGray : pal.Bg; }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        Tick();
        if (Xp) DrawXpWindow(g);
        else if (IsVibrant) DrawGlassSheen(g);
        DrawHeader(g, pal);

        RectangleF b = Board();
        if (Xp) Bevel(g, Inflate(b, P(3)), P(3), XpDark, Color.White);
        float c = b.Width / n;
        Font numbers = F((float)Math.Round(c / Zoom * 0.54f), Xp ? "bold" : "semibold");
        for (int i = 0; i < n * n; i++)
            DrawCell(g, new RectangleF(b.X + (i % n) * c, b.Y + (i / n) * c, c, c), i, pal, numbers);

        if (won) Banner(g, pal, "Level " + level + " cleared! Click for level " + (level + 1));
        else if (lost) Banner(g, pal, "Boom! Click to try level " + level + " again");
    }

    void DrawHeader(Graphics g, Palette pal)
    {
        RectangleF h = Header(), face = FaceButton();
        int mood = lost ? 2 : won ? 1 : 0;
        int secs = (int)elapsed;
        if (Xp)
        {
            Bevel(g, h, P(2), XpDark, Color.White);
            float lw = P(52), lh = P(30), ly = h.Y + (h.Height - lh) / 2;
            Lcd(g, MinesLeft(), new RectangleF(h.X + P(6), ly, lw, lh));
            Lcd(g, secs, new RectangleF(h.Right - P(6) - lw, ly, lw, lh));
            using (var bg = new SolidBrush(XpGray)) g.FillRectangle(bg, face);
            Bevel(g, face, P(2), Color.White, XpDark);
            Smiley(g, Inflate(face, -P(5)), Rgb(0xFFFF00), Color.Black, mood);
            return;
        }

        // Glass and Clean: level and flags left, time right, a round face button between.
        Font caption = F(11, "semibold"), big = F(20, "semibold");
        Color fg = pal.Fg, fg2 = pal.Fg2;
        Label(g, "LEVEL " + level, caption, fg2, h.X, h.Y);
        float icon = P(20);
        DrawFlag(g, new RectangleF(h.X - P(2), h.Y + P(19), icon, icon), fg, IsVibrant ? Color.White : pal.Red);
        Label(g, MinesLeft().ToString(), big, fg, h.X + icon, h.Y + P(13));
        Label(g, "TIME", caption, fg2, h.Right, h.Y, h.Width / 3, StringAlignment.Far);
        Label(g, (secs / 60) + ":" + (secs % 60).ToString("00"), big, fg, h.Right, h.Y + P(13), h.Width / 3, StringAlignment.Far);

        if (IsVibrant) GlassBlob(g, face, true);
        else FillCircle(g, pal.Fill, face.X + face.Width / 2, face.Y + face.Height / 2, face.Width / 2);
        Smiley(g, Inflate(face, -P(7)), Color.Empty, fg, mood);
    }

    void DrawCell(Graphics g, RectangleF r, int i, Palette pal, Font numbers)
    {
        bool showMine = mine[i] && (open[i] || (lost && !flag[i]));
        bool wrongFlag = lost && flag[i] && !mine[i];
        int k = open[i] && !mine[i] ? Count(i, mine) : 0;
        RectangleF glyph = Inflate(r, -r.Width * 0.18f);

        if (Xp)
        {
            using (var bg = new SolidBrush(i == boom ? Color.Red : XpGray)) g.FillRectangle(bg, r);
            if (open[i] || showMine || wrongFlag)
                using (var line = new Pen(XpDark, Math.Max(1f, P(1))))
                {
                    g.DrawLine(line, r.Left, r.Top, r.Right, r.Top);
                    g.DrawLine(line, r.Left, r.Top, r.Left, r.Bottom);
                }
            else Bevel(g, r, Math.Max(2f, r.Width * 0.1f), Color.White, XpDark);
        }
        else
        {
            RectangleF tile = Inflate(r, -Math.Max(1f, r.Width * 0.05f));
            float radius = r.Width * (IsVibrant ? 0.24f : 0.18f);
            bool raised = !open[i] && !showMine && !wrongFlag;
            if (IsVibrant)
            {
                if (raised) GlassBlob(g, tile, false);
                else FillRound(g, i == boom ? Color.FromArgb(170, 255, 69, 58) : Color.FromArgb(18, 255, 255, 255), tile, radius);
            }
            else FillRound(g, i == boom ? pal.Red : raised ? pal.Sep : pal.Fill, tile, radius);
        }

        Color mineColor = Xp ? Color.Black : IsVibrant || i == boom ? Color.White : pal.Fg;
        if (showMine || wrongFlag) DrawMine(g, glyph, mineColor, Xp);
        if (wrongFlag) using (var x = new Pen(Xp ? Color.Red : pal.Red, Math.Max(1.5f, r.Width * 0.07f)))
            {
                g.DrawLine(x, glyph.Left, glyph.Top, glyph.Right, glyph.Bottom);
                g.DrawLine(x, glyph.Right, glyph.Top, glyph.Left, glyph.Bottom);
            }
        else if (flag[i] && !open[i])
            DrawFlag(g, glyph, Xp ? Color.Black : pal.Fg, Xp ? Color.Red : IsVibrant ? Color.White : pal.Red);
        if (k > 0)
            using (var b = new SolidBrush(NumberColor(k, pal)))
                g.DrawString(k.ToString(), numbers, b, r, Centered);
    }

    // The end-of-game message, centred over the board in the style's own look.
    void Banner(Graphics g, Palette pal, string text)
    {
        Font f = F(13, "semibold");
        RectangleF b = Board();
        float w = Math.Min(b.Width, TextWidth(g, text, f) + P(28)), h = P(34);
        var r = new RectangleF(b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h);
        Color ink;
        if (Xp)
        {
            using (var tip = new SolidBrush(Rgb(0xFFFFE1))) g.FillRectangle(tip, r); // XP tooltip yellow
            using (var edge = new Pen(Color.Black, 1f)) g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
            ink = Color.Black;
        }
        else if (IsVibrant)
        {
            FillRound(g, Color.FromArgb(170, 20, 24, 28), r, h / 2);
            GlassBlob(g, r, true);
            ink = Color.White;
        }
        else
        {
            FillRound(g, pal.Fg, r, h / 2);
            ink = pal.Bg;
        }
        Label(g, text, f, ink, r.X + r.Width / 2, r.Y + (h - f.Height) / 2, w - P(16), StringAlignment.Center);
    }

    // ---- XP pieces ----
    void DrawXpWindow(Graphics g)
    {
        float tb = P(24), r = P(8);
        using (var path = new GraphicsPath())
        {
            path.AddArc(0, 0, r * 2, r * 2, 180, 90);
            path.AddArc(W - r * 2, 0, r * 2, r * 2, 270, 90);
            path.AddLine(W, r, W, H);
            path.AddLine(W, H, 0, H);
            path.CloseFigure();
            using (var frame = new LinearGradientBrush(new RectangleF(0, 0, W, tb + 1), Rgb(0x3D95FF), Rgb(0x0055E5), 90f))
                g.FillPath(frame, path);
            using (var edge = new SolidBrush(Rgb(0x0055E5))) g.FillRectangle(edge, 0, tb, W, H - tb);
        }
        using (var body = new SolidBrush(XpGray)) g.FillRectangle(body, P(3), tb, W - P(6), H - tb - P(3));
        using (var hi = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawLine(hi, r, 1, W - r, 1);

        float ic = P(16);
        DrawMine(g, new RectangleF(P(7), (tb - ic) / 2, ic, ic), Color.Black, true);
        Font f = F(13, "bold");
        string title = "Minesweeper - Level " + level;
        Label(g, title, f, Color.FromArgb(90, 0, 0, 60), P(29), P(4.5f));
        Label(g, title, f, Color.White, P(28), P(3.5f));
    }

    // Seven-segment counter: red lit segments, dim red unlit ones, on black.
    static readonly int[] Segments = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F, 0x40 }; // 0-9, minus

    void Lcd(Graphics g, int value, RectangleF r)
    {
        g.FillRectangle(Brushes.Black, r);
        string s = value < 0 ? "-" + Math.Min(99, -value).ToString("00") : Math.Min(999, value).ToString("000");
        float dw = (r.Width - P(4)) / 3;
        for (int d = 0; d < 3; d++)
        {
            int bits = s[d] == '-' ? Segments[10] : Segments[s[d] - '0'];
            Seg7(g, bits, new RectangleF(r.X + P(2) + d * dw + P(2), r.Y + P(3), dw - P(4), r.Height - P(6)));
        }
    }

    void Seg7(Graphics g, int bits, RectangleF r)
    {
        float t = Math.Max(2f, r.Width * 0.18f), mid = r.Y + r.Height / 2;
        RectangleF[] seg =
        {
            new RectangleF(r.X + t / 2, r.Y, r.Width - t, t),                       // a top
            new RectangleF(r.Right - t, r.Y + t / 2, t, r.Height / 2 - t / 2),      // b top right
            new RectangleF(r.Right - t, mid, t, r.Height / 2 - t / 2),              // c bottom right
            new RectangleF(r.X + t / 2, r.Bottom - t, r.Width - t, t),              // d bottom
            new RectangleF(r.X, mid, t, r.Height / 2 - t / 2),                      // e bottom left
            new RectangleF(r.X, r.Y + t / 2, t, r.Height / 2 - t / 2),              // f top left
            new RectangleF(r.X + t / 2, mid - t / 2, r.Width - t, t),               // g middle
        };
        using (var on = new SolidBrush(Rgb(0xFF0000)))
        using (var off = new SolidBrush(Rgb(0x400000)))
            for (int k = 0; k < 7; k++) g.FillRectangle((bits >> k & 1) != 0 ? on : off, Inflate(seg[k], -0.5f));
    }

    // Classic 3D edge: light on the top and left, dark on the bottom and right (swap for sunken).
    static void Bevel(Graphics g, RectangleF r, float t, Color light, Color dark)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var lb = new SolidBrush(light))
            g.FillPolygon(lb, new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right - t, r.Top + t),
                new PointF(r.Left + t, r.Top + t), new PointF(r.Left + t, r.Bottom - t), new PointF(r.Left, r.Bottom) });
        using (var db = new SolidBrush(dark))
            g.FillPolygon(db, new[] { new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom),
                new PointF(r.Left + t, r.Bottom - t), new PointF(r.Right - t, r.Bottom - t), new PointF(r.Right - t, r.Top + t) });
        g.SmoothingMode = old;
    }

    // ---- Liquid Glass pieces ----
    // A light falling from above: bright top edge fading into the frosted card.
    void DrawGlassSheen(Graphics g)
    {
        using (var card = CardPath())
        {
            var state = g.Save();
            g.SetClip(card);
            using (var sheen = new LinearGradientBrush(new RectangleF(0, 0, W, H * 0.5f),
                Color.FromArgb(55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillRectangle(sheen, 0, 0, W, H * 0.5f);
            g.Restore(state);
            using (var rim = new LinearGradientBrush(new RectangleF(0, 0, W, H), Color.FromArgb(150, 255, 255, 255),
                Color.FromArgb(30, 255, 255, 255), 90f))
            using (var pen = new Pen(rim, P(1.2f)))
                g.DrawPath(pen, card);
        }
    }

    // A raised glass droplet: translucent body, highlight on top, bright rim.
    void GlassBlob(Graphics g, RectangleF r, bool round)
    {
        float radius = round ? Math.Min(r.Width, r.Height) / 2 : r.Width * 0.24f;
        using (var path = RoundRect(r, radius))
        {
            using (var body = new LinearGradientBrush(r, Color.FromArgb(85, 255, 255, 255), Color.FromArgb(35, 255, 255, 255), 90f))
                g.FillPath(body, path);
            var top = new RectangleF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.06f, r.Width * 0.76f, r.Height * 0.38f);
            using (var shine = RoundRect(top, Math.Min(radius, top.Height / 2)))
            using (var b = new LinearGradientBrush(top, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillPath(b, shine);
            using (var rim = new LinearGradientBrush(Inflate(r, 1), Color.FromArgb(170, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90f))
            using (var pen = new Pen(rim, Math.Max(1f, P(0.8f))))
                g.DrawPath(pen, path);
        }
    }

    // ---- glyphs shared by every style ----
    void DrawMine(Graphics g, RectangleF r, Color c, bool shine)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rad = r.Width * 0.3f;
        using (var pen = new Pen(c, Math.Max(1f, r.Width * 0.08f)))
            for (int k = 0; k < 4; k++)
            {
                double a = k * Math.PI / 4;
                float dx = (float)Math.Cos(a) * r.Width * 0.46f, dy = (float)Math.Sin(a) * r.Width * 0.46f;
                g.DrawLine(pen, cx - dx, cy - dy, cx + dx, cy + dy);
            }
        FillCircle(g, c, cx, cy, rad);
        if (shine) FillCircle(g, Color.White, cx - rad * 0.35f, cy - rad * 0.35f, rad * 0.25f);
    }

    void DrawFlag(Graphics g, RectangleF r, Color pole, Color cloth)
    {
        float px = r.X + r.Width * 0.58f;
        using (var b = new SolidBrush(cloth))
            g.FillPolygon(b, new[] { new PointF(px, r.Y + r.Height * 0.05f), new PointF(px, r.Y + r.Height * 0.52f),
                new PointF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.3f) });
        using (var pen = new Pen(pole, Math.Max(1f, r.Width * 0.09f)))
        {
            g.DrawLine(pen, px, r.Y + r.Height * 0.05f, px, r.Y + r.Height * 0.85f);
            g.DrawLine(pen, r.X + r.Width * 0.3f, r.Y + r.Height * 0.88f, r.X + r.Width * 0.86f, r.Y + r.Height * 0.88f);
        }
    }

    // mood: 0 playing, 1 won (sunglasses), 2 lost (crossed eyes). An empty fill draws outline only.
    void Smiley(Graphics g, RectangleF r, Color fill, Color ink, int mood)
    {
        float w = r.Width, cx = r.X + w / 2, cy = r.Y + w / 2;
        if (fill != Color.Empty) FillCircle(g, fill, cx, cy, w / 2);
        using (var pen = new Pen(ink, Math.Max(1f, w * 0.07f)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawEllipse(pen, r);
            float ex = w * 0.18f, ey = cy - w * 0.12f, e = w * 0.07f;
            if (mood == 2)
                foreach (float x in new[] { cx - ex, cx + ex })
                {
                    g.DrawLine(pen, x - e, ey - e, x + e, ey + e);
                    g.DrawLine(pen, x + e, ey - e, x - e, ey + e);
                }
            else if (mood == 1)
            {
                g.DrawLine(pen, cx - w * 0.36f, ey - e * 0.4f, cx + w * 0.36f, ey - e * 0.4f);
                FillCircle(g, ink, cx - ex, ey, e * 1.5f);
                FillCircle(g, ink, cx + ex, ey, e * 1.5f);
            }
            else
            {
                FillCircle(g, ink, cx - ex, ey, e);
                FillCircle(g, ink, cx + ex, ey, e);
            }
            var mouth = new RectangleF(cx - w * 0.22f, cy - w * 0.08f, w * 0.44f, w * 0.36f);
            if (mood == 2) g.DrawArc(pen, mouth.X, cy + w * 0.12f, mouth.Width, mouth.Height, 200, 140);
            else g.DrawArc(pen, mouth, 20, 140);
        }
    }

    static RectangleF Inflate(RectangleF r, float by) { return RectangleF.Inflate(r, by, by); }
}
