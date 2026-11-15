// Snake for Desk Widgets. Two modes (right-click > Mode):
//   Levels  - eat 10 apples to clear a level; every level is faster and has more walls.
//   Endless - no walls, no levels; it just gets faster the longer you grow.
// The board wraps around: leave on the right and you come back on the left. The snake's skin
// changes every 5 apples. Click the board to play (the widget takes the keyboard while you play
// and pauses when you click anywhere else).
//   Arrow keys or WASD: steer     Click beside the head: steer     Esc: pause
// Each mode's game (level, score, best, snake, apple) is saved and comes back after a restart.
// Right-click outside the board for the menu; Appearance switches Windows XP, Liquid Glass and Clean.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Snake", Description = "Snake with levels or endless mode, skins, in XP, Liquid Glass or Clean style")]
public sealed class Snake : CardWidget
{
    const int N = 15, ApplesPerLevel = 10, ApplesPerSkin = 5, Mid = N / 2;
    static readonly int[] DX = { 1, 0, -1, 0 }, DY = { 0, 1, 0, -1 }; // right, down, left, up
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- skins: one every 5 apples, in this order, then round again ----
    sealed class Skin { public string Name; public Color A, B, C; public int Pattern; } // 0 bands, 1 spots, 2 three bands, 3 rainbow

    static readonly Skin[] Skins =
    {
        new Skin { Name = "Emerald",  A = Rgb(0x2EB872), B = Rgb(0x219A5E), C = Rgb(0xB8F5D0), Pattern = 0 },
        new Skin { Name = "Coral",    A = Rgb(0xE0453A), B = Rgb(0x262626), C = Rgb(0xF5C542), Pattern = 2 },
        new Skin { Name = "Ocean",    A = Rgb(0x2F80ED), B = Rgb(0x1C5FB8), C = Rgb(0xD6ECFF), Pattern = 1 },
        new Skin { Name = "Python",   A = Rgb(0xC9B37E), B = Rgb(0xA8915C), C = Rgb(0x5C4326), Pattern = 1 },
        new Skin { Name = "Amethyst", A = Rgb(0x8E5BE8), B = Rgb(0x6B3FC4), C = Rgb(0xF0E6FF), Pattern = 0 },
        new Skin { Name = "Gold",     A = Rgb(0xF2B233), B = Rgb(0xD18E12), C = Rgb(0xFFF1C4), Pattern = 0 },
        new Skin { Name = "Neon",     A = Rgb(0x00E5C3), B = Rgb(0xFF2BD6), C = Color.White,    Pattern = 0 },
        new Skin { Name = "Rainbow",  A = Rgb(0xFF4D4D), B = Rgb(0x4D79FF), C = Color.White,    Pattern = 3 },
    };

    Skin CurrentSkin { get { return Skins[(apples / ApplesPerSkin) % Skins.Length]; } }

    // ---- game state (saved, one copy per mode) ----
    int level = 1, score, startScore, best, eaten, seed, dir, food, apples, startApples;
    bool dead;
    readonly List<int> snake = new List<int>(); // cell indices, head first
    readonly bool[] wall = new bool[N * N];

    // ---- session ----
    bool running, played, cleared;
    int prevTail;          // where the tail was before the last move, for smooth gliding
    float frozenT = 1;     // how far between squares the snake is drawn while paused
    long skinShownAt = -100000;
    readonly Queue<int> turns = new Queue<int>();
    readonly Random rng = new Random();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastStep, lastSave;
    RectangleF board;      // set while drawing

    public Snake(Section s) : base(s, CardSize.Large)
    {
        if (!Restore()) NewRun();
    }

    // ==== modes and levels ====
    bool Endless { get { return Settings.Get("mode", "levels") == "endless"; } }
    string SaveKey { get { return Endless ? "endless" : "game"; } }

    // ms per move. Levels: faster each level. Endless: faster every 2 apples, down to a limit.
    int Interval { get { return Endless ? Math.Max(60, 150 - apples * 2) : Math.Max(70, 170 - (level - 1) * 10); } }

    void SetMode(bool endless)
    {
        if (endless == Endless) return;
        running = false;
        Save();
        Settings.Set("mode", endless ? "endless" : "levels");
        if (!Restore()) { best = 0; NewRun(); } // each mode keeps its own best
        played = cleared = false;
        Render();
    }

    void NewRun()
    {
        score = apples = 0;
        StartLevel(1);
    }

    void StartLevel(int lvl)
    {
        level = Math.Max(1, lvl);
        seed = new Random().Next();
        startScore = score;
        startApples = apples;
        Reset();
    }

    // Puts the level back to its start: walls from the seed, a short snake mid-left, a new apple.
    void Reset()
    {
        Array.Clear(wall, 0, wall.Length);
        var rnd = new Random(seed);
        int segments = Endless ? 0 : Math.Min(12, (level - 1) * 2);
        for (int s = 0; s < segments; s++)
        {
            int x = rnd.Next(N), y = rnd.Next(N), len = 2 + rnd.Next(3), d = rnd.Next(2);
            for (int i = 0; i < len; i++)
            {
                int cx = x + (d == 0 ? i : 0), cy = y + (d == 1 ? i : 0);
                if (cx < N && cy < N && Math.Abs(cy - Mid) > 1) wall[cy * N + cx] = true; // keep the start row clear
            }
        }
        snake.Clear();
        for (int i = 0; i < 3; i++) snake.Add(Mid * N + 4 - i);
        prevTail = snake[2] - 1;
        frozenT = 1;
        dir = 0;
        turns.Clear();
        eaten = 0;
        score = startScore;
        apples = startApples;
        dead = false;
        PlaceFood();
    }

    void PlaceFood()
    {
        var free = new List<int>();
        for (int i = 0; i < N * N; i++) if (!wall[i] && !snake.Contains(i)) free.Add(i);
        food = free.Count > 0 ? free[rng.Next(free.Count)] : -1;
    }

    // ==== saving: "level|score|startScore|best|eaten|seed|dir|food|dead|snake|apples|startApples" ====
    void Save()
    {
        var sb = new StringBuilder();
        sb.Append(level).Append('|').Append(score).Append('|').Append(startScore).Append('|').Append(best).Append('|')
          .Append(eaten).Append('|').Append(seed).Append('|').Append(dir).Append('|').Append(food).Append('|')
          .Append(dead ? 1 : 0).Append('|');
        for (int i = 0; i < snake.Count; i++) { if (i > 0) sb.Append(','); sb.Append(snake[i]); }
        sb.Append('|').Append(apples).Append('|').Append(startApples);
        Settings.Set(SaveKey, sb.ToString());
        lastSave = clock.ElapsedMilliseconds;
    }

    bool Restore()
    {
        string[] p = Settings.Get(SaveKey, "").Split('|');
        if (p.Length != 10 && p.Length != 12) return false; // 10 = saves from before skins
        try
        {
            level = Math.Max(1, Math.Min(999, int.Parse(p[0], Inv)));
            startScore = int.Parse(p[2], Inv);
            startApples = p.Length == 12 ? int.Parse(p[11], Inv) : 0;
            seed = int.Parse(p[5], Inv);
            Reset();
            score = int.Parse(p[1], Inv);
            best = int.Parse(p[3], Inv);
            eaten = int.Parse(p[4], Inv);
            dir = int.Parse(p[6], Inv) & 3;
            food = int.Parse(p[7], Inv);
            dead = p[8] == "1";
            apples = p.Length == 12 ? Math.Max(0, int.Parse(p[10], Inv)) : 0;
            snake.Clear();
            foreach (string c in p[9].Split(','))
            {
                int i = int.Parse(c, Inv);
                if (i < 0 || i >= N * N) return false;
                snake.Add(i);
            }
            if (food < 0 || food >= N * N || wall[food]) PlaceFood();
            prevTail = snake[snake.Count - 1];
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    // ==== moving ====
    void Turn(int d)
    {
        int last = dir;
        foreach (int t in turns) last = t;
        if (d == last || (d + 2) % 4 == last || turns.Count >= 2) return;
        turns.Enqueue(d);
    }

    void Step()
    {
        if (turns.Count > 0) dir = turns.Dequeue();
        // No edges: leaving one side brings the snake back on the opposite side.
        int head = snake[0], x = (head % N + DX[dir] + N) % N, y = (head / N + DY[dir] + N) % N, next = y * N + x;
        bool grow = next == food;
        bool hitsSelf = snake.IndexOf(next) >= 0 && (grow || next != snake[snake.Count - 1]); // the tail moves away
        if (wall[next] || hitsSelf)
        {
            dead = true;
            running = false;
            frozenT = 1;
            Save();
            return;
        }
        prevTail = snake[snake.Count - 1];
        if (!grow) snake.RemoveAt(snake.Count - 1);
        snake.Insert(0, next);
        if (!grow) return;

        apples++;
        score += Endless ? 1 : level;
        best = Math.Max(best, score);
        if (apples % ApplesPerSkin == 0) skinShownAt = clock.ElapsedMilliseconds;
        if (!Endless && ++eaten >= ApplesPerLevel)
        {
            StartLevel(level + 1);
            cleared = true;
            running = false;
        }
        else PlaceFood();
        Save();
    }

    // ==== play / pause ====
    void Play()
    {
        if (dead)
        {
            if (Endless) NewRun(); else Reset();
        }
        running = played = true;
        cleared = false;
        lastStep = clock.ElapsedMilliseconds - (long)(frozenT * Interval); // carry on exactly where it was drawn
        Activate();
        Render();
    }

    void Pause()
    {
        if (!running) return;
        frozenT = StepT();
        running = false;
        turns.Clear();
        Save();
        Render();
    }

    // How far the snake is between its last square and the next one (0..1).
    float StepT()
    {
        if (!running) return frozenT;
        return Math.Min(1f, (clock.ElapsedMilliseconds - lastStep) / (float)Interval);
    }

    // Widgets normally never take focus; this one does, so it can read the arrow keys while you play.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle &= ~0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override bool IsInputKey(Keys keyData) { return true; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Alt | Keys.F4)) return true; // Alt+F4 must not close the widget behind the app's back
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.KeyCode)
        {
            case Keys.Right: case Keys.D: Turn(0); break;
            case Keys.Down: case Keys.S: Turn(1); break;
            case Keys.Left: case Keys.A: Turn(2); break;
            case Keys.Up: case Keys.W: Turn(3); break;
            case Keys.Escape: Pause(); break;
            case Keys.Space: case Keys.Enter: if (!running) Play(); break;
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Pause();
    }

    protected override bool IsButton(Point p) { return Board().Contains(p); }

    protected override void OnWidgetClick(Point p)
    {
        if (!running && Board().Contains(p)) Play();
    }

    // While playing, a click steers: above or below the head when moving sideways, left or right otherwise.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!running || e.Button != MouseButtons.Left || !Board().Contains(e.Location)) return;
        RectangleF h = CellRect(snake[0]);
        float cx = h.X + h.Width / 2, cy = h.Y + h.Height / 2;
        int moving = dir;
        foreach (int t in turns) moving = t;
        if (moving % 2 == 0) Turn(e.Y < cy ? 3 : 1);
        else Turn(e.X < cx ? 2 : 0);
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var mode = menu.AddSubmenu("Mode");
        mode.AddChoice("Levels", !Endless, delegate { SetMode(false); });
        mode.AddChoice("Endless", Endless, delegate { SetMode(true); });
        if (Endless)
            menu.AddDestructive("New Game", delegate { running = false; NewRun(); Save(); Render(); });
        else
        {
            menu.Add("Restart Level", delegate { running = false; Reset(); Save(); Render(); });
            menu.AddDestructive("Start Over from Level 1", delegate { running = false; NewRun(); Save(); Render(); });
        }
        var theme = menu.AddSubmenu("Theme");
        theme.AddChoice("Light", !Dark, delegate { Settings.Set("theme", "light"); Render(); });
        theme.AddChoice("Dark", Dark, delegate { Settings.Set("theme", "dark"); Render(); });
    }

    // About 30 frames a second while playing, so the snake glides; nothing at all otherwise.
    protected override int NextFrameDelay()
    {
        if (!running) return -1;
        if (IsCovered()) { BeginInvoke((Action)Pause); return -1; }
        return 30;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && snake.Count > 0) Save();
        base.Dispose(disposing);
    }

    // ==== layout ====
    // Styles ("face"): xp, vibrant (Liquid Glass), light (Clean), desk (matches the app's own widgets).
    bool Xp { get { return Face == "xp"; } }
    bool Desk { get { return Face == "desk"; } }

    protected override string DefaultFace { get { return "xp"; } }

    protected override string[][] AppearanceChoices
    {
        get
        {
            return new[] { new[] { "xp", "Windows XP" }, new[] { "vibrant", "Liquid Glass" }, new[] { "light", "Clean" },
                new[] { "desk", "Desk" } };
        }
    }

    // Light or dark works with every style. Until it's picked, Glass and Desk start dark, XP and Clean light.
    bool Dark
    {
        get
        {
            string t = Settings.Get("theme", "");
            return t == "" ? Desk || IsVibrant : t == "dark";
        }
    }

    Palette Pl { get { return Dark ? DarkPalette : LightPalette; } }
    Color XpBody { get { return Dark ? Rgb(0x3C3C3C) : Rgb(0xC0C0C0); } }
    Color XpHi { get { return Dark ? Rgb(0x626262) : Color.White; } }
    Color XpLo { get { return Dark ? Rgb(0x1A1A1A) : Rgb(0x808080); } }
    Color XpText { get { return Dark ? Color.White : Color.Black; } }
    // Text drawn straight on the board or on glass.
    Color Ink { get { return Xp || Dark ? Color.White : Rgb(0x1C1C1E); } }
    Color Ink2 { get { return Color.FromArgb(Xp || Dark ? 175 : 150, Ink); } }

    float Pad { get { return P(Xp ? 12 : 16); } }
    RectangleF Header() { return new RectangleF(Pad, P(Xp ? 32 : 16), W - 2 * Pad, P(40)); }

    RectangleF Board()
    {
        float top = P(Xp ? 82 : 64);
        float cell = Math.Min((W - 2 * Pad) / N, (H - top - Pad) / N);
        return new RectangleF((W - cell * N) / 2, top, cell * N, cell * N);
    }

    RectangleF CellRect(int i)
    {
        RectangleF b = Board();
        float c = b.Width / N;
        return new RectangleF(b.X + (i % N) * c, b.Y + (i / N) * c, c, c);
    }

    // ==== drawing ====
    protected override Color CardBackground(Palette pal) { return Xp ? XpBody : Pl.Bg; }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (running)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - lastStep > Interval * 3) lastStep = now - Interval; // came back from a stall: no burst of moves
            while (running && now - lastStep >= Interval) { Step(); lastStep += Interval; }
            if (running && now - lastSave > 15000) Save(); // ponytail: periodic save; a power cut loses at most 15 s of moves
        }

        if (Xp) DrawXpWindow(g);
        else if (IsVibrant)
            using (var card = CardPath())
            using (var tint = new SolidBrush(Dark ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(120, 255, 255, 255)))
                g.FillPath(tint, card); // frosted glass, darker or lighter
        DrawHeader(g);

        board = Board();
        RectangleF b = board;
        GraphicsPath round = Xp ? null : RoundRect(RectangleF.Inflate(b, P(4), P(4)), P(Desk ? 14 : 12));
        if (Xp)
        {
            g.FillRectangle(Brushes.Black, b);
            Bevel(g, RectangleF.Inflate(b, P(3), P(3)), P(3), XpLo, XpHi);
        }
        else if (IsVibrant)
        {
            using (var pane = new SolidBrush(Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0))) g.FillPath(pane, round);
            using (var pen = new Pen(Color.FromArgb(Dark ? 90 : 160, 255, 255, 255), Math.Max(1f, P(0.8f)))) g.DrawPath(pen, round);
        }
        else
        {
            using (var pane = new SolidBrush(Pl.Fill)) g.FillPath(pane, round);
            if (Desk) // a faint dot in every square, like the app's grids
                for (int i = 0; i < N * N; i++)
                {
                    RectangleF r = CellRect(i);
                    FillCircle(g, Pl.Sep, r.X + r.Width / 2, r.Y + r.Height / 2, Math.Max(1f, r.Width * 0.07f));
                }
        }

        for (int i = 0; i < N * N; i++) if (wall[i]) DrawWall(g, CellRect(i));
        if (food >= 0) DrawApple(g, CellRect(food));

        var state = g.Save();
        g.SetClip(b); // the snake wraps at the board's edge, so clip it there exactly
        DrawSnake(g);
        long since = clock.ElapsedMilliseconds - skinShownAt;
        if (since < 1800 && apples > 0)
        {
            int a = (int)(255 * Math.Min(1f, (1800 - since) / 400f));
            string text = "New skin: " + CurrentSkin.Name;
            Font f = F(15, "bold");
            if (Ink.R > 128) Label(g, text, f, Color.FromArgb(a / 2, 0, 0, 0), b.X + b.Width / 2 + 1, b.Y + P(9), b.Width, StringAlignment.Center);
            Label(g, text, f, Color.FromArgb(a, Ink), b.X + b.Width / 2, b.Y + P(8), b.Width, StringAlignment.Center);
        }
        g.Restore(state);

        if (!running)
        {
            state = g.Save();
            if (round != null) g.SetClip(round); else g.SetClip(b);
            DrawOverlay(g, b);
            g.Restore(state);
        }
        if (round != null) round.Dispose();
    }

    void DrawHeader(Graphics g)
    {
        RectangleF h = Header();
        if (Xp)
        {
            Bevel(g, h, P(2), XpLo, XpHi);
            float lw = P(52), lh = P(30), ly = h.Y + (h.Height - lh) / 2;
            Lcd(g, score, new RectangleF(h.X + P(6), ly, lw, lh));
            Lcd(g, Endless ? apples : ApplesPerLevel - eaten, new RectangleF(h.Right - P(6) - lw, ly, lw, lh));
            Font f = F(11, "bold");
            Label(g, "SCORE", f, XpText, h.X + P(64), ly + P(1));
            Label(g, "Best " + best, F(11, "regular"), XpText, h.X + P(64), ly + P(15));
            Label(g, "APPLES", f, XpText, h.Right - P(64), ly + P(1), P(80), StringAlignment.Far);
            Label(g, Endless ? "eaten" : "to go", F(11, "regular"), XpText, h.Right - P(64), ly + P(15), P(80), StringAlignment.Far);
            return;
        }
        if (Desk)
        {
            // Like the app's own widgets: a small orange title, a grey status on the right, one big number.
            Label(g, "Snake", F(13, "semibold"), Pl.Orange, h.X, h.Y);
            Label(g, Endless ? "Endless" : "Level " + level, F(11, "regular"), Pl.Fg2, h.Right, h.Y + P(2), h.Width / 2, StringAlignment.Far);
            Label(g, score.ToString(), F(22, "semibold"), Pl.Fg, h.X, h.Y + P(15));
            string progress = (Endless ? apples + " apples" : eaten + " of " + ApplesPerLevel + " apples") + "   Best " + best;
            Label(g, progress, F(11, "regular"), Pl.Fg2, h.Right, h.Y + P(24), h.Width * 0.7f, StringAlignment.Far);
            return;
        }
        Color fg = IsVibrant ? Ink : Pl.Fg, fg2 = IsVibrant ? Ink2 : Pl.Fg2;
        Font cap = F(11, "semibold"), big = F(20, "semibold");
        float third = h.Width / 3;
        string[] caps = { "SCORE", Endless ? "ENDLESS" : "LEVEL " + level, "BEST" };
        string[] vals = { score.ToString(), Endless ? apples.ToString() : eaten + " / " + ApplesPerLevel, best.ToString() };
        for (int i = 0; i < 3; i++)
        {
            float cx = h.X + third * i + third / 2;
            Label(g, caps[i], cap, fg2, cx, h.Y, third, StringAlignment.Center);
            Label(g, vals[i], big, fg, cx, h.Y + P(13), third, StringAlignment.Center);
        }
    }

    void DrawWall(Graphics g, RectangleF r)
    {
        if (Xp)
        {
            using (var b = new SolidBrush(XpBody)) g.FillRectangle(b, r);
            Bevel(g, r, Math.Max(2f, r.Width * 0.12f), XpHi, XpLo);
        }
        else if (IsVibrant) FillRound(g, Dark ? Color.FromArgb(90, 10, 14, 20) : Color.FromArgb(70, 0, 0, 0), RectangleF.Inflate(r, -1, -1), r.Width * 0.25f);
        else FillRound(g, Pl.Fg2, RectangleF.Inflate(r, -1, -1), r.Width * 0.25f);
    }

    void DrawApple(Graphics g, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + r.Height * 0.05f, rad = r.Width * 0.36f;
        if (IsVibrant)
        {
            FillCircle(g, Color.FromArgb(50, 255, 120, 170), cx, cy, rad * 1.6f); // glow
            FillCircle(g, Color.FromArgb(255, 255, 130, 175), cx, cy, rad);
            FillCircle(g, Color.FromArgb(200, 255, 255, 255), cx - rad * 0.35f, cy - rad * 0.35f, rad * 0.3f);
            return;
        }
        Color body = Xp ? Color.Red : Pl.Red, leaf = Xp ? Rgb(0x00C000) : Pl.Green;
        FillCircle(g, body, cx, cy, rad);
        FillCircle(g, Color.FromArgb(150, 255, 255, 255), cx - rad * 0.4f, cy - rad * 0.4f, rad * 0.22f);
        using (var b = new SolidBrush(leaf))
            g.FillEllipse(b, cx, cy - rad * 1.45f, rad * 0.8f, rad * 0.5f);
    }

    // ---- the snake: a smooth tube of overlapping discs that tapers to the tail ----
    sealed class Sample { public float X, Y, R, Tx, Ty; public int Seg; }

    PointF Center(int cell)
    {
        float c = board.Width / N;
        return new PointF(board.X + (cell % N + 0.5f) * c, board.Y + (cell / N + 0.5f) * c);
    }

    // The shortest step from a to b on a board that wraps around.
    PointF Delta(PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (dx > board.Width / 2) dx -= board.Width; else if (dx < -board.Width / 2) dx += board.Width;
        if (dy > board.Height / 2) dy -= board.Height; else if (dy < -board.Height / 2) dy += board.Height;
        return new PointF(dx, dy);
    }

    Color SkinColor(Skin s, int seg)
    {
        switch (s.Pattern)
        {
            case 2: return seg % 4 == 0 ? s.A : seg % 4 == 2 ? s.C : s.B;
            case 3: return Hue((seg * 28) % 360);
            default: return seg % 2 == 0 ? s.A : s.B;
        }
    }

    static Color Hue(int h)
    {
        float x = 1 - Math.Abs(h / 60f % 2 - 1);
        float r = 0, g = 0, b = 0;
        if (h < 60) { r = 1; g = x; } else if (h < 120) { r = x; g = 1; } else if (h < 180) { g = 1; b = x; }
        else if (h < 240) { g = x; b = 1; } else if (h < 300) { r = x; b = 1; } else { r = 1; b = x; }
        return Color.FromArgb((int)(55 + 200 * r), (int)(55 + 200 * g), (int)(55 + 200 * b));
    }

    static Color Mix(Color c, float f) { return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f)); }

    // ponytail: draws the body as many small discs (a few hundred on a long snake); a path-based tube if it ever shows up in CPU use.
    void DrawSnake(Graphics g)
    {
        float c = board.Width / N, t = StepT();
        Skin skin = CurrentSkin;

        // One point per piece, each part-way from where it was to where it is now.
        var pts = new List<PointF>(snake.Count);
        for (int i = 0; i < snake.Count; i++)
        {
            PointF from = Center(i + 1 < snake.Count ? snake[i + 1] : prevTail), to = Center(snake[i]);
            PointF d = Delta(from, to);
            pts.Add(new PointF(from.X + d.X * t, from.Y + d.Y * t));
        }
        for (int i = 1; i < pts.Count; i++) // unwrap, so the line never jumps across the board
        {
            PointF d = Delta(pts[i - 1], pts[i]);
            pts[i] = new PointF(pts[i - 1].X + d.X, pts[i - 1].Y + d.Y);
        }

        // Samples along the line from head to tail, getting thinner toward the tail.
        float total = 0;
        for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
        var samples = new List<Sample>();
        float spacing = c * 0.3f, walked = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            PointF a = pts[i - 1], b = pts[i];
            float len = Dist(a, b);
            if (len <= 0) continue;
            float tx = (a.X - b.X) / len, ty = (a.Y - b.Y) / len; // points toward the head
            for (float s = 0; s < len; s += spacing)
            {
                float along = (walked + s) / Math.Max(1f, total);
                samples.Add(new Sample
                {
                    X = a.X - tx * s, Y = a.Y - ty * s, Tx = tx, Ty = ty, Seg = (int)((walked + s) / c),
                    R = c * (0.38f - 0.2f * (float)Math.Pow(along, 1.5))
                });
            }
            walked += len;
        }
        PointF tail = pts[pts.Count - 1];
        samples.Add(new Sample { X = tail.X, Y = tail.Y, R = c * 0.18f, Seg = (int)(walked / c) });

        using (var brush = new SolidBrush(Color.Black))
        {
            // Shadow, outline, body, pattern, shine: each pass tail first, so the head side lies on top.
            if (!Xp)
            {
                brush.Color = Color.FromArgb(IsVibrant ? 60 : 35, 0, 0, 0);
                for (int i = samples.Count - 1; i >= 0; i--) Disc(g, brush, samples[i].X + c * 0.05f, samples[i].Y + c * 0.08f, samples[i].R);
            }
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                brush.Color = Mix(SkinColor(skin, samples[i].Seg), 0.55f);
                Disc(g, brush, samples[i].X, samples[i].Y, samples[i].R + Math.Max(1f, c * 0.05f));
            }
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                brush.Color = SkinColor(skin, samples[i].Seg);
                Disc(g, brush, samples[i].X, samples[i].Y, samples[i].R);
            }
            if (skin.Pattern == 1)
            {
                brush.Color = skin.C;
                int lastSeg = -1;
                for (int i = 0; i < samples.Count; i++)
                {
                    Sample s = samples[i];
                    if (s.Seg == lastSeg || s.Seg == 0) continue;
                    lastSeg = s.Seg;
                    float side = s.Seg % 2 == 0 ? 1 : -1;
                    Disc(g, brush, s.X - s.Ty * s.R * 0.45f * side, s.Y + s.Tx * s.R * 0.45f * side, s.R * 0.32f);
                }
            }
            brush.Color = Color.FromArgb(IsVibrant ? 45 : 28, 255, 255, 255);
            for (int i = samples.Count - 1; i >= 0; i--)
                Disc(g, brush, samples[i].X - samples[i].R * 0.2f, samples[i].Y - samples[i].R * 0.35f, samples[i].R * 0.45f);

            DrawHead(g, brush, pts, c, skin);
        }
    }

    void DrawHead(Graphics g, SolidBrush brush, List<PointF> pts, float c, Skin skin)
    {
        PointF h = pts[0];
        float fx = DX[dir], fy = DY[dir];
        if (pts.Count > 1)
        {
            float len = Dist(pts[1], h);
            if (len > 0.5f) { fx = (h.X - pts[1].X) / len; fy = (h.Y - pts[1].Y) / len; }
        }
        float px = -fy, py = fx, r = c * 0.46f;
        Color col = SkinColor(skin, 0);

        brush.Color = Mix(col, 0.55f);
        Disc(g, brush, h.X, h.Y, r + Math.Max(1f, c * 0.05f));
        Disc(g, brush, h.X + fx * r * 0.35f, h.Y + fy * r * 0.35f, r * 0.8f + Math.Max(1f, c * 0.05f));
        brush.Color = col;
        Disc(g, brush, h.X, h.Y, r);
        Disc(g, brush, h.X + fx * r * 0.35f, h.Y + fy * r * 0.35f, r * 0.8f);
        brush.Color = Color.FromArgb(IsVibrant ? 70 : 45, 255, 255, 255);
        Disc(g, brush, h.X - r * 0.25f, h.Y - r * 0.35f, r * 0.45f);

        // Eyes look the way the snake is heading; crosses when it crashed.
        foreach (float side in new[] { 1f, -1f })
        {
            float ex = h.X + fx * r * 0.25f + px * r * 0.45f * side, ey = h.Y + fy * r * 0.25f + py * r * 0.45f * side;
            brush.Color = Color.White;
            Disc(g, brush, ex, ey, r * 0.28f);
            if (dead)
                using (var pen = new Pen(Color.Black, Math.Max(1f, r * 0.1f)))
                {
                    float e = r * 0.15f;
                    g.DrawLine(pen, ex - e, ey - e, ex + e, ey + e);
                    g.DrawLine(pen, ex + e, ey - e, ex - e, ey + e);
                }
            else
            {
                brush.Color = Rgb(0x1A1A1A);
                Disc(g, brush, ex + fx * r * 0.09f, ey + fy * r * 0.09f, r * 0.15f);
            }
        }

        // A quick tongue flick now and then.
        if (running && clock.ElapsedMilliseconds % 1400 < 200)
            using (var pen = new Pen(Rgb(0xE8364F), Math.Max(1f, c * 0.06f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                float bx = h.X + fx * r * 1.05f, by = h.Y + fy * r * 1.05f, tx = bx + fx * r * 0.5f, ty = by + fy * r * 0.5f;
                g.DrawLine(pen, bx, by, tx, ty);
                g.DrawLine(pen, tx, ty, tx + (fx + px) * r * 0.2f, ty + (fy + py) * r * 0.2f);
                g.DrawLine(pen, tx, ty, tx + (fx - px) * r * 0.2f, ty + (fy - py) * r * 0.2f);
            }
    }

    static float Dist(PointF a, PointF b) { return (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }

    // A filled circle on the wrapping board: also drawn on the far side when it pokes over an edge.
    void Disc(Graphics g, Brush b, float x, float y, float r)
    {
        float bw = board.Width, bh = board.Height;
        x = board.X + ((x - board.X) % bw + bw) % bw;
        y = board.Y + ((y - board.Y) % bh + bh) % bh;
        float x2 = x - r < board.X ? x + bw : x + r > board.Right ? x - bw : float.NaN;
        float y2 = y - r < board.Y ? y + bh : y + r > board.Bottom ? y - bh : float.NaN;
        g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
        if (!float.IsNaN(x2)) g.FillEllipse(b, x2 - r, y - r, r * 2, r * 2);
        if (!float.IsNaN(y2)) g.FillEllipse(b, x - r, y2 - r, r * 2, r * 2);
        if (!float.IsNaN(x2) && !float.IsNaN(y2)) g.FillEllipse(b, x2 - r, y2 - r, r * 2, r * 2);
    }

    void DrawOverlay(Graphics g, RectangleF b)
    {
        // Dark styles dim the board; light ones (except XP's black board) frost it white with dark text.
        bool light = !Xp && !Dark;
        Color text = light ? Rgb(0x1C1C1E) : Color.White;
        using (var dim = new SolidBrush(light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0)))
            g.FillRectangle(dim, RectangleF.Inflate(b, P(4), P(4)));
        float cx = b.X + b.Width / 2;
        string title, line;
        if (dead) { title = "CRASHED!"; line = Endless ? "Click to play again" : "Click to retry level " + level; }
        else if (cleared) { title = "LEVEL " + (level - 1) + " CLEAR!"; line = "Click to start level " + level; }
        else if (played) { title = "PAUSED"; line = "Click to carry on"; }
        else { title = Endless ? "ENDLESS" : "SNAKE"; line = Endless ? "Click to play" : "Click to play level " + level; }
        Color titleColor = dead ? (light ? Pl.Red : Color.FromArgb(255, 110, 100)) : Desk ? Pl.Orange : text;
        Label(g, title, F(28, "bold"), titleColor, cx, b.Y + b.Height * 0.3f, b.Width, StringAlignment.Center);
        Label(g, line, F(14, "semibold"), text, cx, b.Y + b.Height * 0.3f + P(42), b.Width, StringAlignment.Center);
        Font small = F(11, "regular");
        Color dimText = Color.FromArgb(light ? 170 : 200, text);
        Label(g, "Arrow keys or WASD to steer, or click beside the head", small, dimText, cx, b.Bottom - P(44), b.Width, StringAlignment.Center);
        Label(g, "Edges wrap around.  New skin every " + ApplesPerSkin + " apples.", small, dimText, cx, b.Bottom - P(28), b.Width, StringAlignment.Center);
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
            // Light: the classic blue title bar. Dark: a graphite one.
            Color top = Dark ? Rgb(0x4A4A4A) : Rgb(0x3D95FF), bottom = Dark ? Rgb(0x1A1A1A) : Rgb(0x0055E5);
            using (var frame = new LinearGradientBrush(new RectangleF(0, 0, W, tb + 1), top, bottom, 90f))
                g.FillPath(frame, path);
            using (var edge = new SolidBrush(bottom)) g.FillRectangle(edge, 0, tb, W, H - tb);
        }
        using (var body = new SolidBrush(XpBody)) g.FillRectangle(body, P(3), tb, W - P(6), H - tb - P(3));
        using (var hi = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawLine(hi, r, 1, W - r, 1);
        Font f = F(13, "bold");
        string title = Endless ? "Snake - Endless" : "Snake - Level " + level;
        Label(g, title, f, Color.FromArgb(90, 0, 0, 60), P(11), P(4.5f));
        Label(g, title, f, Color.White, P(10), P(3.5f));
    }

    static readonly int[] Segments = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F }; // 0-9

    void Lcd(Graphics g, int value, RectangleF r)
    {
        g.FillRectangle(Brushes.Black, r);
        string s = Math.Max(0, Math.Min(999, value)).ToString("000");
        float dw = (r.Width - P(4)) / 3;
        for (int d = 0; d < 3; d++)
            Seg7(g, Segments[s[d] - '0'], new RectangleF(r.X + P(2) + d * dw + P(2), r.Y + P(3), dw - P(4), r.Height - P(6)));
    }

    void Seg7(Graphics g, int bits, RectangleF r)
    {
        float t = Math.Max(2f, r.Width * 0.18f), mid = r.Y + r.Height / 2;
        RectangleF[] seg =
        {
            new RectangleF(r.X + t / 2, r.Y, r.Width - t, t),
            new RectangleF(r.Right - t, r.Y + t / 2, t, r.Height / 2 - t / 2),
            new RectangleF(r.Right - t, mid, t, r.Height / 2 - t / 2),
            new RectangleF(r.X + t / 2, r.Bottom - t, r.Width - t, t),
            new RectangleF(r.X, mid, t, r.Height / 2 - t / 2),
            new RectangleF(r.X, r.Y + t / 2, t, r.Height / 2 - t / 2),
            new RectangleF(r.X + t / 2, mid - t / 2, r.Width - t, t),
        };
        using (var on = new SolidBrush(Rgb(0xFF0000)))
        using (var off = new SolidBrush(Rgb(0x400000)))
            for (int k = 0; k < 7; k++) g.FillRectangle((bits >> k & 1) != 0 ? on : off, RectangleF.Inflate(seg[k], -0.5f, -0.5f));
    }

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
}
