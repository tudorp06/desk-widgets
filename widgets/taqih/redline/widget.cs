// Redline: an endless highway racer for Desk Widgets.
// Weave through traffic on a four-lane highway for as long as you can. The faster you go the more
// you score, squeezing past a car scores more again, and one touch ends the run. Two-Way mode puts
// oncoming traffic in the left-hand lanes and doubles the points. Click the view to play (the
// widget takes the keyboard while you play and pauses when you click anywhere else).
//   Left / Right or A / D: steer     Up or W: accelerate     Down or S: brake     Esc: pause
// Your run and your best are saved and survive restarts. Right-click outside the view for the menu:
// Mode (One-Way, Two-Way), Appearance (Windows XP, Liquid Glass, Clean, Desk) and Theme.
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

[DeskWidget(Name = "Redline", Description = "Endless highway traffic racer, in XP, Liquid Glass, Clean or Desk style")]
public sealed class Redline : CardWidget
{
    // The road is a list of segments; the camera looks along it. 150 world units = 1 metre.
    // 150 world units = 1 metre, so the road is 16 m across: four 4 m lanes, and a car is 2.8 m wide.
    const float SegLen = 600f, RoadW = 1200f, CamH = 1000f, CamDepth = 0.84f, UPM = 150f;
    const float MaxSpeed = UPM * 90f;                 // 90 m/s = 324 km/h
    const float Accel = MaxSpeed / 9f, Braking = -MaxSpeed / 2.2f, Decel = -MaxSpeed / 12f;
    const float CarW = 420f, TruckW = 560f;           // world units across
    const float CarLen = 700f, TruckLen = 1400f;      // world units long
    // Where your own car sits relative to the camera, which is a little way behind it.
    const float Nose = 1300f, Tail = 400f;
    const int Lanes = 4, TrackSegs = 1000, ViewSegs = 75, DashLen = 3;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    sealed class Seg
    {
        public float Curve, YA, YB;          // world: bend and height
        public float X1, Y1, W1, X2, Y2, W2; // screen, this frame
        public float Clip;
        public int Frame;
        public byte Left, Right;             // roadside objects: 0 none, else the kind + 1
    }

    // Kind: 0-2 car, 3 van, 4 truck. Speed is negative for oncoming traffic.
    sealed class Car { public float Z, X, Speed, Target, ShiftIn; public int Kind, Lane; public bool Behind, Fast; }

    sealed class Star { public float X, Y, Vx, Vy, Life; } // a shooting star, in fractions of the view

    // ---- the cars you can drive ----
    const int CarSport = 0, CarRacing = 1, CarGhost = 2, CarOpen = 3;
    static readonly string[] CarIds = { "sport", "racing", "ghost", "open" };
    static readonly string[] CarNames = { "Sport", "Racing", "Ghost", "Open-Wheeler" };

    int Unlocks { get { return Settings.GetInt("unlocks", 3); } }   // Sport and Racing from the start
    bool Unlocked(int c) { return (Unlocks & (1 << c)) != 0; }

    void Unlock(int c, string how)
    {
        if (Unlocked(c)) return;
        Settings.Set("unlocks", Unlocks | (1 << c));
        Flash(how);
    }

    int Chosen
    {
        get
        {
            int i = Array.IndexOf(CarIds, Settings.Get("car", "sport"));
            return i < 0 || !Unlocked(i) ? CarSport : i;
        }
    }

    // The Open-Wheeler is quick and darty, the Racing car is sharper than the Sport, the Ghost is a Sport.
    float CarTop { get { return MaxSpeed * (Chosen == CarOpen ? 1.14f : Chosen == CarRacing ? 1.06f : 1f); } }
    float CarPull { get { return Accel * (Chosen == CarOpen ? 1.6f : Chosen == CarRacing ? 1.25f : 1f); } }
    float CarGrip { get { return Chosen == CarOpen ? 1.5f : Chosen == CarRacing ? 1.22f : 1f; } }

    // ---- saved state ----
    int seed, score, best;
    float distance, speed, playerX;
    bool crashed;

    // ---- the road, rebuilt from its seed every lap ----
    readonly List<Seg> segs = new List<Seg>();
    readonly List<Car> traffic = new List<Car>();

    // ---- session ----
    bool running, played;
    bool left, right, gas, brake;
    float shake, time, tilt, popupT, scoreF;
    string popup = "";
    int frame, lap;
    int combo, starMark, crackSeed;      // overtakes in a row, the last shooting star, the crash pattern
    float comboT, crackT, bounceT;
    int cameo = -1;                      // which driver is waving, or -1
    float cameoT;
    bool onBarrier;
    string typed = "";                   // the last few keys, for the cheat code
    readonly List<Star> stars = new List<Star>();

    // The streak counts overtakes: 1x, 2x, 3x... and each one adds a tenth to what you earn.
    float Mult { get { return 1f + 0.1f * combo; } }
    string MultText { get { return Mult.ToString("0.0", Inv) + "x"; } }

    // How long you have to find the next car. The phantom cheat gives you much longer.
    float ComboWindow { get { return Ghosted ? 5f : 2f; } }

    // Streaks long enough to bring someone out to cheer you on.
    static readonly int[] CameoAt = { 105, 74 };
    static readonly string[] CameoWords = { "HAMMER TIME", "SIMPLY LOVELY" };
    // Their colours, in order: helmet, the band over the crown, the accent, and the race suit.
    static readonly int[] CameoHelmet = { 0xF5D017, 0xF2ECDC };
    static readonly int[] CameoBand = { 0xF5F5F5, 0x1C2A5E };
    static readonly int[] CameoAccent = { 0xD81E24, 0xD41C24 };
    static readonly int[] CameoSuit = { 0xD81E24, 0x16205A };

    // Two cheats: one bounces you off traffic, the other puts you straight through it.
    bool Invincible { get { return Settings.GetBool("invincible", false); } }
    bool Ghosted { get { return Settings.GetBool("phantom", false); } }
    readonly Random rng = new Random();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastMs, lastSave;

    // ---- per-frame drawing ----
    RectangleF v;
    readonly PointF[] quad = new PointF[4];

    public Redline(Section s) : base(s, CardSize.Large)
    {
        if (!Restore()) NewRun();
    }

    static float TrackLen { get { return TrackSegs * SegLen; } }
    bool TwoWay { get { return Settings.Get("mode", "one") == "two"; } }
    string SaveKey { get { return TwoWay ? "twoway" : "game"; } }

    // Lane centres across the road, from -1 (left edge) to 1 (right edge).
    static float LaneX(int lane) { return -1f + (lane + 0.5f) * (2f / Lanes); }
    // In Two-Way mode the two left lanes carry traffic coming the other way.
    bool Oncoming(int lane) { return TwoWay && lane < Lanes / 2; }

    void NewRun()
    {
        score = 0;
        scoreF = 0;
        distance = 0;
        speed = 0;
        playerX = LaneX(TwoWay ? Lanes - 1 : Lanes / 2);
        crashed = false;
        combo = 0;
        comboT = 0;
        crackT = 0;
        starMark = 0;
        lap = 0;
        seed = rng.Next();
        Build();
        traffic.Clear();
        stars.Clear();
    }

    // ==== the road ====
    // Always the same number of segments, flat and straight at both ends, so each new lap joins
    // the last one seamlessly and the layout never repeats.
    void Build()
    {
        var r = new Random(seed);
        segs.Clear();
        AddRoad(20, 40, 20, 0, 0);
        while (segs.Count < TrackSegs - 300)
        {
            int kind = r.Next(10);
            float curve = (0.25f + (float)r.NextDouble() * 0.45f) * (r.Next(2) == 0 ? -1 : 1);
            float hill = (r.Next(3) - 1) * (10 + r.Next(25)) * 200f;
            if (kind < 4) AddRoad(25, 40 + r.Next(50), 25, 0, hill);
            else if (kind < 8) AddRoad(35, 40 + r.Next(40), 35, curve, hill * 0.5f);
            else AddRoad(25, 25, 25, curve * 1.6f, 0);
        }
        // Straighten and flatten the last stretch, so each new one joins the old without a kink.
        if (segs.Count > TrackSegs - 140) segs.RemoveRange(TrackSegs - 140, segs.Count - (TrackSegs - 140));
        float backY = segs[segs.Count - 1].YB, backC = segs[segs.Count - 1].Curve;
        int runout = TrackSegs - segs.Count;
        for (int i = 0; i < runout; i++)
            AddSeg(EaseInOut(backC, 0, Math.Min(1f, i / (runout * 0.35f))), EaseInOut(backY, 0, (float)(i + 1) / runout));

        for (int i = 0; i < segs.Count; i++) // rocks, poles and bushes along the verges
        {
            if (i % 11 == 0) segs[i].Left = (byte)(1 + (i / 11) % 3);
            if ((i + 5) % 13 == 0) segs[i].Right = (byte)(1 + (i / 13) % 3);
        }
    }

    void AddSeg(float curve, float y)
    {
        float y1 = segs.Count == 0 ? 0 : segs[segs.Count - 1].YB;
        segs.Add(new Seg { Curve = curve, YA = y1, YB = y });
    }

    // One stretch of road: it eases into the bend, holds it, then eases out, rising or falling as it goes.
    void AddRoad(int enter, int hold, int leave, float curve, float hill)
    {
        float startY = segs.Count == 0 ? 0 : segs[segs.Count - 1].YB, endY = startY + hill;
        int total = enter + hold + leave;
        for (int i = 0; i < enter; i++) AddSeg(EaseIn(0, curve, (float)i / enter), EaseInOut(startY, endY, (float)i / total));
        for (int i = 0; i < hold; i++) AddSeg(curve, EaseInOut(startY, endY, (float)(enter + i) / total));
        for (int i = 0; i < leave; i++) AddSeg(EaseInOut(curve, 0, (float)i / leave), EaseInOut(startY, endY, (float)(enter + hold + i) / total));
    }

    static float EaseIn(float a, float b, float p) { return a + (b - a) * p * p; }
    static float EaseInOut(float a, float b, float p) { return a + (b - a) * (float)(-Math.Cos(p * Math.PI) / 2 + 0.5); }
    static float Lerp(float a, float b, float p) { return a + (b - a) * p; }

    // distance only ever grows; the road loops under it.
    Seg SegAt(float z) { return segs[(int)(Math.Max(0, z) / SegLen) % segs.Count]; }

    // ==== traffic ====
    static float HalfWidth(int kind) { return (kind == 4 ? TruckW : kind == 3 ? CarW * 1.12f : CarW) / (2 * RoadW); }

    void Spawn()
    {
        int want = Math.Min(12, 4 + (int)(distance / UPM / 600));   // busier the further you get
        for (int tries = 0; traffic.Count < want && tries < 10; tries++)
        {
            int lane = rng.Next(Lanes);
            bool against = Oncoming(lane);
            // New traffic appears at the far end of the view, never close enough to be unfair.
            float z = distance + (against ? 30000 + (float)rng.NextDouble() * 15000 : 20000 + (float)rng.NextDouble() * 22000);
            int near = 0;
            foreach (var c in traffic)
            {
                if (c.Lane == lane && Math.Abs(c.Z - z) < 5000) { near = 99; break; }
                if (Math.Abs(c.Z - z) < 2500) near++;   // never wall off every lane at once
            }
            if (near >= Lanes - 1) continue;
            int kind = rng.Next(10) < 2 ? 3 + rng.Next(2) : rng.Next(3);
            // Past 1000 points some of the traffic is doing 150-200 km/h and will come past you.
            bool fast = score > 1000 && kind < 3 && rng.Next(100) < 35;
            float cruise = fast ? MaxSpeed * (0.46f + (float)rng.NextDouble() * 0.16f)
                                : MaxSpeed * (0.30f + (float)rng.NextDouble() * 0.22f) * (kind == 4 ? 0.8f : 1f);
            float at = LaneX(lane) + ((float)rng.NextDouble() - 0.5f) * 0.06f;
            traffic.Add(new Car
            {
                Z = z,
                Lane = lane,
                X = at,
                Target = at,
                ShiftIn = 3f + (float)rng.NextDouble() * 6f,
                Speed = against ? -cruise : cruise,
                Kind = kind,
                Fast = fast,
                Behind = false
            });
        }
    }

    // ==== driving ====
    void Step(float dt)
    {
        time += dt;
        shake = Math.Max(0, shake - dt * 2);
        popupT = Math.Max(0, popupT - dt);
        if (comboT > 0 && (comboT -= dt) <= 0) combo = 0;   // two seconds without an overtake ends the streak
        bounceT = Math.Max(0, bounceT - dt);
        if (cameoT > 0 && (cameoT -= dt) <= 0) cameo = -1;
        float pct = Math.Min(1f, speed / MaxSpeed);

        // Steering bites harder the faster you go, and bends push you toward the outside.
        float want = (right ? 1 : 0) - (left ? 1 : 0);
        tilt = Lerp(tilt, want, Math.Min(1f, dt * 10));
        float dx = dt * (0.9f + 1.6f * pct) * CarGrip;
        playerX += dx * want;
        playerX -= dt * pct * SegAt(distance).Curve * 0.5f;

        if (gas) speed += CarPull * dt;
        else if (brake) speed += Braking * dt;
        else speed += Decel * dt;
        speed = Math.Max(0, Math.Min(CarTop, speed));

        // The barriers hold you on the road rather than ending the run.
        float limit = 1f - HalfWidth(0);
        bool hitting = playerX < -limit || playerX > limit;
        if (hitting)
        {
            playerX = Math.Max(-limit, Math.Min(limit, playerX));
            speed *= 1f - Math.Min(0.9f, dt * 2.5f);
            shake = Math.Max(shake, 0.35f);
            if (!onBarrier && TruckAhead()) CountBarrierHit();
        }
        onBarrier = hitting;

        distance += speed * dt;
        if (distance > (lap + 1) * TrackLen) { lap++; seed = rng.Next(); Build(); } // next stretch of road

        float gained = speed * dt / UPM / 10f * (TwoWay ? 2 : 1) * Mult; // a point per 10 m, doubled in Two-Way
        scoreF += gained;
        score = (int)scoreF;
        if (score / 131 != starMark) { starMark = score / 131; AddStar(); } // a shooting star every 131 points

        for (int i = stars.Count - 1; i >= 0; i--)
        {
            Star s = stars[i];
            s.X += s.Vx * dt;
            s.Y += s.Vy * dt;
            s.Life -= dt * 0.7f;
            if (s.Life <= 0) stars.RemoveAt(i);
        }

        Spawn();
        float myHalf = HalfWidth(0);
        for (int i = traffic.Count - 1; i >= 0; i--)
        {
            Car c = traffic[i];
            c.Z += c.Speed * dt;
            // Past 500 points the traffic starts changing lanes in front of you.
            if (score > 500 && c.Speed > 0 && (c.ShiftIn -= dt) <= 0)
            {
                c.ShiftIn = 4f + (float)rng.NextDouble() * 6f;
                int to = c.Lane + (rng.Next(2) == 0 ? -1 : 1);
                if (to >= 0 && to < Lanes && !Oncoming(to) && LaneClear(to, c.Z, c))
                {
                    c.Lane = to;
                    c.Target = LaneX(to);
                }
            }
            if (c.X != c.Target)
            {
                float move = dt * 0.4f;
                c.X = Math.Abs(c.Target - c.X) <= move ? c.Target : c.X + (c.Target > c.X ? move : -move);
            }
            float dz = c.Z - distance;
            float gap = Math.Abs(playerX - c.X) - myHalf - HalfWidth(c.Kind);
            float len = c.Kind == 4 ? TruckLen : CarLen;

            // Only a real overlap counts: your car's nose has to reach the other one's tail, and
            // the two have to overlap across the road by more than a hair.
            if (dz < Nose && dz + len > Tail && gap < -0.015f && !Ghosted)
            {
                if (!Invincible) { Crash(); return; }
                if (bounceT <= 0)   // the cheat is on: shrug it off and lose your speed
                {
                    speed = Math.Min(speed, Math.Abs(c.Speed) * 0.5f);
                    shake = Math.Max(shake, 0.9f);
                    bounceT = 0.6f;
                    Flash("Bounced off");
                }
            }
            if (!c.Behind && dz + len < Tail)
            {
                c.Behind = true;
                bool close = gap < 0.07f;
                combo++;
                comboT = ComboWindow;
                int points = (int)(((c.Speed < 0 ? 30 : 10) + (close ? 50 : 0)) * Mult);
                scoreF += points;
                score = (int)scoreF;
                if (combo == CameoAt[0]) StartCameo(0);
                else if (combo == CameoAt[1]) StartCameo(1);
                else if (combo >= 10) Unlock(CarOpen, "Ten in a row!  Open-Wheeler unlocked");
                else if (close) Flash("Close one!  +" + points);
                else if (combo > 1) Flash(combo + "x streak  (" + MultText + ")");
            }
            if (dz < -3000) traffic.RemoveAt(i);
        }

        // ponytail: saves every 15 s while playing (each save rewrites the settings file); a power cut loses at most that.
        if (clock.ElapsedMilliseconds - lastSave > 15000) Save();
    }

    void Crash()
    {
        crashed = true;
        running = false;
        shake = 1.2f;
        speed = 0;
        combo = 0;
        comboT = 0;
        crackT = 0;                      // the windscreen crack draws itself over the next half second
        crackSeed = rng.Next();
        best = Math.Max(best, score);
        Save();
    }

    void Flash(string text) { popup = text; popupT = 1.6f; }

    // Is there a truck right in front of you? (For the car hidden behind one.)
    bool TruckAhead()
    {
        foreach (var c in traffic)
        {
            float dz = c.Z - distance;
            if (c.Kind == 4 && dz > 0 && dz < 5000 && Math.Abs(c.X - playerX) < 0.55f) return true;
        }
        return false;
    }

    void CountBarrierHit()
    {
        int hits = Settings.GetInt("barrier", 0) + 1;
        Settings.Set("barrier", hits);
        if (hits >= 5) Unlock(CarGhost, "Something white steps out of the shadows...");
        else if (!Unlocked(CarGhost)) Flash(hits + " of 5");
    }

    // Room for a car to move into that lane around z?
    bool LaneClear(int lane, float z, Car moving)
    {
        foreach (var c in traffic)
            if (c != moving && c.Lane == lane && Math.Abs(c.Z - z) < 2600) return false;
        return Math.Abs(z - distance) > 2600 || Math.Abs(LaneX(lane) - playerX) > 0.5f; // don't cut straight across you
    }

    void StartCameo(int which)
    {
        cameo = which;
        cameoT = 3.4f;
    }

    void AddStar()
    {
        if (stars.Count > 3) return;
        stars.Add(new Star
        {
            X = 0.35f + (float)rng.NextDouble() * 0.6f,
            Y = 0.02f + (float)rng.NextDouble() * 0.2f,
            Vx = -0.35f - (float)rng.NextDouble() * 0.25f,
            Vy = 0.1f + (float)rng.NextDouble() * 0.1f,
            Life = 1f
        });
    }

    // ==== saving: "seed|lap|distance|speed|x|score|best|crashed|traffic" ====
    static string Num(float f) { return f.ToString("0.##", Inv); }
    static float ParseF(string s) { return float.Parse(s, NumberStyles.Float, Inv); }

    void Save()
    {
        var sb = new StringBuilder();
        sb.Append(seed).Append('|').Append(lap).Append('|').Append(Num(distance)).Append('|').Append(Num(speed))
          .Append('|').Append(Num(playerX)).Append('|').Append(score).Append('|').Append(best).Append('|')
          .Append(crashed ? 1 : 0).Append('|');
        for (int i = 0; i < traffic.Count; i++)
        {
            if (i > 0) sb.Append(';');
            Car c = traffic[i];
            sb.Append(Num(c.Z)).Append(',').Append(Num(c.X)).Append(',').Append(Num(c.Speed)).Append(',')
              .Append(c.Kind).Append(',').Append(c.Lane).Append(',').Append(c.Fast ? 1 : 0);
        }
        Settings.Set(SaveKey, sb.ToString());
        lastSave = clock.ElapsedMilliseconds;
    }

    bool Restore()
    {
        string[] p = Settings.Get(SaveKey, "").Split('|');
        if (p.Length != 9) return false;
        try
        {
            seed = int.Parse(p[0], Inv);
            lap = Math.Max(0, int.Parse(p[1], Inv));
            Build();
            distance = Math.Max(0, ParseF(p[2]));
            speed = Math.Max(0, Math.Min(MaxSpeed, ParseF(p[3])));
            playerX = Math.Max(-1f, Math.Min(1f, ParseF(p[4])));
            score = Math.Max(0, int.Parse(p[5], Inv));
            scoreF = score;
            best = Math.Max(0, int.Parse(p[6], Inv));
            crashed = p[7] == "1";
            starMark = score / 131;
            traffic.Clear();
            if (p[8].Length > 0)
                foreach (string one in p[8].Split(';'))
                {
                    string[] c = one.Split(',');
                    if (c.Length != 5 && c.Length != 6) return false;
                    traffic.Add(new Car
                    {
                        Z = ParseF(c[0]), X = ParseF(c[1]), Target = ParseF(c[1]), Speed = ParseF(c[2]),
                        Kind = Math.Max(0, Math.Min(4, int.Parse(c[3], Inv))),
                        Lane = Math.Max(0, Math.Min(Lanes - 1, int.Parse(c[4], Inv))),
                        Fast = c.Length == 6 && c[5] == "1",
                        ShiftIn = 3f + (float)rng.NextDouble() * 6f,
                        Behind = ParseF(c[0]) < distance
                    });
                }
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    // ==== play / pause ====
    void Play()
    {
        if (crashed) NewRun();
        running = played = true;
        left = right = gas = brake = false;
        lastMs = clock.ElapsedMilliseconds;
        Activate();
        Render();
    }

    void Pause()
    {
        if (!running) return;
        running = false;
        left = right = gas = brake = false;
        Save();
        Render();
    }

    void SetMode(bool two)
    {
        if (two == TwoWay) return;
        running = false;
        Save();
        Settings.Set("mode", two ? "two" : "one");
        if (!Restore()) { best = 0; NewRun(); } // each mode keeps its own run and best
        played = false;
        Render();
    }

    // Widgets normally never take focus; this one does, so it can read the keyboard while you play.
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
        Typed(e.KeyCode);
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.A: left = true; break;
            case Keys.Right: case Keys.D: right = true; break;
            case Keys.Up: case Keys.W: if (!running) Play(); else gas = true; break;
            case Keys.Down: case Keys.S: brake = true; break;
            case Keys.Space: case Keys.Enter: if (!running) Play(); else gas = true; break;
            case Keys.Escape: Pause(); break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.A: left = false; break;
            case Keys.Right: case Keys.D: right = false; break;
            case Keys.Up: case Keys.W: case Keys.Space: case Keys.Enter: gas = false; break;
            case Keys.Down: case Keys.S: brake = false; break;
        }
    }

    // Letters and digits are kept in a short buffer: type the cheat code to open up every car.
    void Typed(Keys key)
    {
        char ch = '\0';
        if (key >= Keys.A && key <= Keys.Z) ch = (char)('a' + (key - Keys.A));
        else if (key >= Keys.D0 && key <= Keys.D9) ch = (char)('0' + (key - Keys.D0));
        else if (key >= Keys.NumPad0 && key <= Keys.NumPad9) ch = (char)('0' + (key - Keys.NumPad0));
        if (ch == '\0') return;
        typed += ch;
        if (typed.Length > 10) typed = typed.Substring(typed.Length - 10);
        if (typed.EndsWith("max33"))
        {
            typed = "";
            Settings.Set("unlocks", (1 << CarIds.Length) - 1);
            Flash("Every car unlocked");
            Render();
        }
        else if (typed.EndsWith("stroll18"))
        {
            typed = "";
            bool on = !Invincible;
            Settings.Set("invincible", on);
            Flash(on ? "Invincible: you only lose speed" : "Invincible off");
            Render();
        }
        else if (typed.EndsWith("ghost"))
        {
            typed = "";
            bool on = !Ghosted;
            Settings.Set("phantom", on);
            Flash(on ? "Phantom: straight through, and 5s streaks" : "Phantom off");
            Render();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Pause();
    }

    protected override bool IsButton(Point p) { return View().Contains(p); }

    protected override void OnWidgetClick(Point p)
    {
        if (!running && View().Contains(p)) Play();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var mode = menu.AddSubmenu("Mode");
        mode.AddChoice("One-Way", !TwoWay, delegate { SetMode(false); });
        mode.AddChoice("Two-Way", TwoWay, delegate { SetMode(true); });
        var cars = menu.AddSubmenu("Car");
        for (int i = 0; i < CarIds.Length; i++)
        {
            int k = i;
            if (Unlocked(k)) cars.AddChoice(CarNames[k], Chosen == k, delegate { Settings.Set("car", CarIds[k]); Render(); });
            else cars.Add("Locked", delegate { Flash(Hint(k)); Render(); });
        }
        menu.AddDestructive("New Run", delegate { running = false; NewRun(); Save(); Render(); });
        var theme = menu.AddSubmenu("Theme");
        theme.AddChoice("Light", !Dark, delegate { Settings.Set("theme", "light"); Render(); });
        theme.AddChoice("Dark", Dark, delegate { Settings.Set("theme", "dark"); Render(); });
    }

    static string Hint(int car)
    {
        if (car == CarGhost) return "Scrape the barrier 5 times while following a truck";
        if (car == CarOpen) return "Overtake 10 cars in one streak";
        return "Locked";
    }

    // About 30 frames a second while playing; nothing at all otherwise. Pauses when windows cover it.
    protected override int NextFrameDelay()
    {
        if (!running) return crashed && crackT < 1 ? 33 : -1; // let the crack finish drawing itself
        if (IsCovered()) { BeginInvoke((Action)Pause); return -1; }
        return 33;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && segs.Count > 0) Save();
        base.Dispose(disposing);
    }

    // ==== styles and layout ====
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
    Color Ink { get { return Dark ? Color.White : Rgb(0x1C1C1E); } }

    float Pad { get { return P(Xp ? 12 : 16); } }
    RectangleF Header() { return new RectangleF(Pad, P(Xp ? 32 : 16), W - 2 * Pad, P(40)); }

    RectangleF View()
    {
        float top = P(Xp ? 82 : 64);
        return new RectangleF(Pad, top, W - 2 * Pad, H - top - Pad);
    }

    int Kph { get { return (int)(speed / UPM * 3.6f); } }
    float Km { get { return distance / UPM / 1000f; } }

    // ==== drawing ====
    protected override Color CardBackground(Palette pal) { return Xp ? XpBody : Pl.Bg; }

    protected override void DrawContent(Graphics g, Palette unused)
    {
        if (running)
        {
            long now = clock.ElapsedMilliseconds;
            float dt = Math.Min(0.06f, (now - lastMs) / 1000f);
            lastMs = now;
            Step(dt / 2);          // two small steps a frame: steadier at speed
            if (running) Step(dt / 2);
        }
        else if (crashed && crackT < 1) crackT = Math.Min(1f, crackT + 0.07f);

        Palette pal = Pl;
        if (Xp) DrawXpWindow(g);
        else if (IsVibrant)
            using (var card = CardPath())
            using (var tint = new SolidBrush(Dark ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(120, 255, 255, 255)))
                g.FillPath(tint, card);
        DrawHeader(g, pal);

        v = View();
        GraphicsPath round = Xp ? null : RoundRect(v, P(Desk ? 14 : 12));
        var state = g.Save();
        if (round != null) g.SetClip(round); else g.SetClip(v);
        DrawScene(g, pal);
        if (!running) DrawOverlay(g, pal);
        if (crashed && crackT > 0) DrawCracks(g);
        g.Restore(state);

        if (Xp) Bevel(g, RectangleF.Inflate(v, P(3), P(3)), P(3), XpLo, XpHi);
        else if (IsVibrant)
            using (var pen = new Pen(Color.FromArgb(Dark ? 90 : 160, 255, 255, 255), Math.Max(1f, P(0.8f)))) g.DrawPath(pen, round);
        if (round != null) round.Dispose();
    }

    // Colours for the current style, worked out once a frame.
    Color cGrassA, cGrassB, cRoadA, cRoadB, cEdge, cDash, cRail, cRailPost, cRock;

    void Palettes(Palette pal)
    {
        if (Xp)
        {
            cGrassA = Dark ? Rgb(0x16331F) : Rgb(0x57A84C); cGrassB = Dark ? Rgb(0x1B3D26) : Rgb(0x66BC57);
            cRoadA = Dark ? Rgb(0x2E2E32) : Rgb(0x5E5E63); cRoadB = Dark ? Rgb(0x343438) : Rgb(0x66666B);
            cEdge = Color.White; cDash = Color.White;
            cRail = Dark ? Rgb(0x8A8A92) : Rgb(0xD8D8DE); cRailPost = Dark ? Rgb(0x4A4A50) : Rgb(0x8E8E96);
            cRock = Dark ? Rgb(0x3A3A40) : Rgb(0x8C8C92);
        }
        else if (IsVibrant)
        {
            int a = Dark ? 1 : 2;
            cGrassA = Color.FromArgb(10 * a, 255, 255, 255); cGrassB = Color.FromArgb(16 * a, 255, 255, 255);
            cRoadA = Color.FromArgb(32 * a, 255, 255, 255); cRoadB = Color.FromArgb(40 * a, 255, 255, 255);
            cEdge = Color.FromArgb(200, 255, 255, 255); cDash = Color.FromArgb(170, 255, 255, 255);
            cRail = Color.FromArgb(150, 255, 255, 255); cRailPost = Color.FromArgb(90, 255, 255, 255);
            cRock = Color.FromArgb(70, 255, 255, 255);
        }
        else if (Desk)
        {
            cGrassA = Dark ? Rgb(0x141416) : Rgb(0xE8E8ED); cGrassB = Dark ? Rgb(0x1A1A1D) : Rgb(0xF2F2F7);
            cRoadA = pal.Fill; cRoadB = pal.Sep;
            cEdge = pal.Fg2; cDash = pal.Orange; cRail = pal.Sep; cRailPost = pal.Fg2; cRock = pal.Sep;
        }
        else
        {
            cGrassA = Dark ? Rgb(0x1E3A24) : Rgb(0xCDEBC8); cGrassB = Dark ? Rgb(0x24442B) : Rgb(0xD9F2D4);
            cRoadA = Dark ? Rgb(0x2C2C2E) : Rgb(0xBFBFC4); cRoadB = Dark ? Rgb(0x343437) : Rgb(0xC9C9CE);
            cEdge = Color.White; cDash = Color.White;
            cRail = Dark ? Rgb(0x6E6E73) : Color.White; cRailPost = Dark ? Rgb(0x48484C) : Rgb(0x9A9AA0);
            cRock = Dark ? Rgb(0x3A3A3C) : Rgb(0xAEAEB2);
        }
    }

    void DrawScene(Graphics g, Palette pal)
    {
        frame++;
        Palettes(pal);
        float shakeX = shake > 0 ? (float)Math.Sin(time * 55) * shake * P(5) : 0;
        var old = g.SmoothingMode;

        DrawSky(g, pal);
        g.SmoothingMode = SmoothingMode.None;

        int baseIdx = (int)(distance / SegLen);
        float basePct = (distance % SegLen) / SegLen;
        Seg baseSeg = segs[baseIdx % segs.Count];
        float camY = Lerp(baseSeg.YA, baseSeg.YB, basePct) + CamH;
        float x = 0, dx = -(baseSeg.Curve * basePct), maxy = v.Bottom;

        using (var brush = new SolidBrush(Color.Black))
            for (int n = 0; n < ViewSegs; n++)
            {
                int abs = baseIdx + n;
                Seg s = segs[abs % segs.Count];
                float z1 = abs * SegLen, z2 = z1 + SegLen;
                s.Clip = maxy;
                s.Frame = frame;
                Project(s.YA, z1, playerX * RoadW - x, camY, out s.X1, out s.Y1, out s.W1);
                Project(s.YB, z2, playerX * RoadW - x - dx, camY, out s.X2, out s.Y2, out s.W2);
                x += dx;
                dx += s.Curve;
                if (z1 - distance <= CamDepth || s.Y2 >= maxy) { s.Frame = -1; continue; }

                bool alt = (abs / DashLen) % 2 == 0;
                float y1 = s.Y1 + 1, y2 = s.Y2, h = Math.Max(1f, y1 - y2);
                brush.Color = alt ? cGrassA : cGrassB;
                g.FillRectangle(brush, v.X, y2, v.Width, h);
                brush.Color = alt ? cRoadA : cRoadB;
                Quad(g, brush, s.X1 - s.W1, s.X1 + s.W1, y1, s.X2 - s.W2, s.X2 + s.W2, y2);

                // Solid lines down both edges, dashed lines between the lanes.
                float e1 = s.W1 * 0.035f, e2 = s.W2 * 0.035f;
                brush.Color = cEdge;
                Band(g, brush, s, -0.955f, e1, e2, y1, y2);
                Band(g, brush, s, 0.955f, e1, e2, y1, y2);
                if (alt)
                {
                    brush.Color = cDash;
                    for (int lane = 1; lane < Lanes; lane++)
                    {
                        float at = -1f + lane * (2f / Lanes);
                        bool middle = lane == Lanes / 2;
                        Band(g, brush, s, at, e1 * (middle ? 1.1f : 0.8f), e2 * (middle ? 1.1f : 0.8f), y1, y2);
                    }
                }

                // Crash barriers: a rail standing just off each edge, on posts.
                float rh1 = s.W1 * 0.13f, rh2 = s.W2 * 0.13f;
                if (rh1 > 0.7f)
                {
                    bool post = abs % 2 == 0;
                    foreach (float side in new[] { -1f, 1f })
                    {
                        float bx1 = s.X1 + side * s.W1 * 1.07f, bx2 = s.X2 + side * s.W2 * 1.07f;
                        if (post)
                        {
                            brush.Color = cRailPost;
                            Quad(g, brush, bx1 - e1, bx1 + e1, y1, bx2 - e2, bx2 + e2, y2 - rh2);
                        }
                        brush.Color = cRail;
                        Quad(g, brush, bx1 - s.W1 * 0.02f, bx1 + s.W1 * 0.02f, y1 - rh1 * 0.55f, bx2 - s.W2 * 0.02f, bx2 + s.W2 * 0.02f, y2 - rh2 * 0.55f);
                    }
                }
                maxy = s.Y2;
            }
        g.SmoothingMode = old;

        // ---- your headlights, when the theme says it is night ----
        float pw = v.Width * CarW / (2 * CamH);   // exactly as wide as the car the crash test uses
        float bob = speed > 0 ? (float)Math.Sin(time * 24) * v.Height * 0.003f * (speed / MaxSpeed) : 0;
        float bx = v.X + v.Width / 2 + tilt * pw * 0.12f + shakeX, by = v.Bottom - v.Height * 0.03f + bob;
        float nose = by - pw * (Chosen == CarOpen ? 0.74f : 0.7f);   // the front of your car
        if (Dark) DrawBeams(g, bx, pw, nose);

        // ---- everything standing on or beside the road, far ones first ----
        for (int n = ViewSegs - 1; n >= 0; n--)
        {
            int abs = baseIdx + n;
            Seg s = segs[abs % segs.Count];
            if (s.Frame != frame) continue;
            if (s.Left != 0) DrawProp(g, s, abs * SegLen, -1.35f, s.Left);
            if (s.Right != 0) DrawProp(g, s, abs * SegLen, 1.35f, s.Right);
        }
        var shown = new List<Car>();
        foreach (var c in traffic) if (c.Z > distance - SegLen && c.Z < distance + ViewSegs * SegLen) shown.Add(c);
        shown.Sort(delegate(Car a, Car b) { return b.Z.CompareTo(a.Z); });
        foreach (var c in shown)
        {
            float sx, sy, sw;
            if (!Place(c.Z, c.X, out sx, out sy, out sw)) continue;
            float px = Math.Min(v.Width * 1.2f, (c.Kind == 4 ? TruckW : c.Kind == 3 ? CarW * 1.12f : CarW) * sw / RoadW);
            if (px < 2) continue;
            DrawCar(g, sx, sy, px, TrafficColor(c.Kind, pal), c.Kind, false, false, c.Speed < 0, c.Fast ? -2 : -1);
        }

        // ---- your car, always at the bottom, leaning into the turn ----
        if (Ghosted)
            using (var aura = new SolidBrush(Color.FromArgb(55, 170, 225, 255)))
                g.FillEllipse(aura, bx - pw * 0.8f, by - pw * 0.95f, pw * 1.6f, pw * 1.15f);
        var carState = g.Save();
        g.TranslateTransform(bx, by);          // a touch of body roll as you change lane
        g.RotateTransform(tilt * 3f);
        g.TranslateTransform(-bx, -by);
        DrawCar(g, bx, by, pw, PlayerColor(pal), 1, brake || !gas && speed > MaxSpeed * 0.05f, true, false, Chosen);
        g.Restore(carState);

        if (cameo >= 0) DrawCameo(g, pal);
        if (running && (Ghosted || Invincible))
            Label(g, Ghosted && Invincible ? "PHANTOM  INVINCIBLE" : Ghosted ? "PHANTOM" : "INVINCIBLE",
                F(11, "semibold"), Color.FromArgb(200, Desk ? pal.Orange : Color.White),
                v.Right - P(10), v.Y + P(8), v.Width, StringAlignment.Far);

        // ---- the streak: how many in a row, what they are worth, and how long you have left ----
        if (combo > 0 && running)
        {
            Color c = Desk ? pal.Orange : Color.White;
            float mx = v.X + P(10), my = v.Y + P(6), bw = P(46);
            Font f = F(20, "bold");
            Label(g, combo + "x", f, Color.FromArgb(130, 0, 0, 0), mx + 1, my + 1);
            Label(g, combo + "x", f, c, mx, my);
            Label(g, MultText + " points", F(11, "semibold"), Color.FromArgb(215, 255, 255, 255), mx + P(30), my + P(9));
            FillRound(g, Color.FromArgb(70, 0, 0, 0), new RectangleF(mx, my + P(27), bw, P(5)), P(2.5f));
            FillRound(g, c, new RectangleF(mx, my + P(27), bw * Math.Max(0f, comboT / ComboWindow), P(5)), P(2.5f));
        }

        if (popupT > 0)
        {
            int a = (int)(255 * Math.Min(1f, popupT * 2));
            Font f = F(14, "bold");
            float cx = v.X + v.Width / 2, py2 = v.Y + v.Height * 0.2f - (1.4f - popupT) * P(10);
            Label(g, popup, f, Color.FromArgb(a / 2, 0, 0, 0), cx + 1, py2 + 1, v.Width, StringAlignment.Center);
            Label(g, popup, f, Color.FromArgb(a, Desk ? pal.Orange : Color.White), cx, py2, v.Width, StringAlignment.Center);
        }
    }

    // A driver leans in from the bottom of the screen, thumb up, with a shout across the middle.
    // Helmets stay down: these are nobody in particular, just someone who liked that streak.
    void DrawCameo(Graphics g, Palette pal)
    {
        float t = 3.4f - cameoT;
        float show = Math.Min(1f, Math.Min(t * 3.5f, cameoT * 3f));   // pops up, then drops away
        if (show <= 0) return;
        Color helmet = Rgb(CameoHelmet[cameo]), band = Rgb(CameoBand[cameo]);
        Color accent = Rgb(CameoAccent[cameo]), suit = Rgb(CameoSuit[cameo]);
        Color visor = Rgb(0x15171C), glove = Rgb(0x1A1C22);

        float fw = v.Width * 0.3f;
        float cx = v.X + v.Width * 0.22f;
        float baseY = v.Bottom + fw * 1.8f * (1f - show);   // climbs up from under the screen

        // ---- the body: small, the way these mascots are drawn ----
        float bw = fw * 0.62f, top = baseY - fw * 0.8f;
        FillRound(g, Mix(suit, 0.78f), new RectangleF(cx - bw / 2, top, bw, fw * 0.95f), fw * 0.12f);
        FillRound(g, suit, new RectangleF(cx - bw / 2 + fw * 0.025f, top + fw * 0.03f, bw - fw * 0.05f, fw * 0.95f), fw * 0.1f);
        FillRound(g, band, new RectangleF(cx - bw * 0.24f, top + fw * 0.22f, bw * 0.48f, fw * 0.3f), fw * 0.05f);
        FillRound(g, accent, new RectangleF(cx - bw * 0.5f, top + fw * 0.12f, bw * 0.1f, fw * 0.75f), fw * 0.04f);
        FillRound(g, Rgb(0x1A1C22), new RectangleF(cx - bw * 0.46f, top - fw * 0.05f, bw * 0.92f, fw * 0.17f), fw * 0.06f);

        // ---- arms: one down at the side, one punching the air ----
        using (var arm = new Pen(suit, fw * 0.16f))
        {
            arm.StartCap = LineCap.Round;
            arm.EndCap = LineCap.Round;
            g.DrawLine(arm, cx - bw * 0.44f, top + fw * 0.22f, cx - bw * 0.62f, top + fw * 0.66f);
            g.DrawLine(arm, cx + bw * 0.4f, top + fw * 0.2f, cx + fw * 0.52f, top - fw * 0.34f);
        }
        FillRound(g, glove, new RectangleF(cx - bw * 0.62f - fw * 0.09f, top + fw * 0.6f, fw * 0.19f, fw * 0.21f), fw * 0.07f);
        FillRound(g, glove, new RectangleF(cx + fw * 0.52f - fw * 0.13f, top - fw * 0.48f, fw * 0.26f, fw * 0.28f), fw * 0.09f);

        // ---- the helmet, nearly half the figure ----
        float hr = fw * 0.47f, hy = top - fw * 0.42f, hw = hr * 1.95f, hh = hr * 2.05f;
        var box = new RectangleF(cx - hw / 2, hy - hh / 2, hw, hh);
        FillRound(g, Mix(helmet, 0.82f), box, hr * 0.62f);
        FillRound(g, helmet, RectangleF.Inflate(box, -hr * 0.06f, -hr * 0.06f), hr * 0.58f);
        FillRound(g, band, new RectangleF(cx - hw * 0.45f, hy - hh * 0.45f, hw * 0.9f, hh * 0.27f), hr * 0.34f);
        FillRound(g, accent, new RectangleF(cx - hw * 0.46f, hy - hh * 0.02f, hw * 0.3f, hh * 0.18f), hr * 0.2f);
        FillRound(g, visor, new RectangleF(cx - hw * 0.08f, hy - hh * 0.54f, hw * 0.16f, hh * 0.11f), hr * 0.08f);
        var vis = new RectangleF(cx - hw * 0.42f, hy - hh * 0.12f, hw * 0.84f, hh * 0.34f);
        FillRound(g, accent, new RectangleF(vis.X, vis.Y - hh * 0.08f, vis.Width, hh * 0.07f), hr * 0.1f);
        FillRound(g, visor, vis, hr * 0.28f);
        using (var sheen = new SolidBrush(Color.FromArgb(65, 255, 255, 255)))
            g.FillEllipse(sheen, vis.X + vis.Width * 0.07f, vis.Y + vis.Height * 0.14f, vis.Width * 0.36f, vis.Height * 0.32f);
        FillRound(g, Mix(helmet, 0.92f), new RectangleF(cx - hw * 0.35f, hy + hh * 0.27f, hw * 0.7f, hh * 0.16f), hr * 0.2f);

        // The shout, across the middle of the screen.
        Font f = F(26, "bold");
        string words = CameoWords[cameo];
        var strip = new RectangleF(v.X, v.Y + v.Height * 0.3f, v.Width, P(44));
        int a = (int)(235 * show);
        using (var b = new SolidBrush(Color.FromArgb((int)(150 * show), 0, 0, 0))) g.FillRectangle(b, strip);
        using (var line = new SolidBrush(Color.FromArgb(a, accent)))
        {
            g.FillRectangle(line, strip.X, strip.Y, strip.Width, P(2));
            g.FillRectangle(line, strip.X, strip.Bottom - P(2), strip.Width, P(2));
        }
        float bump = (1f - show) * P(10);
        Label(g, words, f, Color.FromArgb(a / 2, 0, 0, 0), v.X + v.Width / 2 + 2, strip.Y + P(7) + bump + 2, v.Width, StringAlignment.Center);
        Label(g, words, f, Color.FromArgb(a, Desk ? pal.Orange : Color.White), v.X + v.Width / 2, strip.Y + P(7) + bump, v.Width, StringAlignment.Center);
        Label(g, combo + " IN A ROW", F(11, "semibold"), Color.FromArgb(a, helmet),
            v.X + v.Width / 2, strip.Bottom + P(5), v.Width, StringAlignment.Center);
    }

    // At night two beams leave the front of your car and spread up the road, fading by the horizon.
    void DrawBeams(Graphics g, float bx, float pw, float nose)
    {
        float top = v.Y + v.Height * 0.52f;
        if (nose <= top) return;
        var span = new RectangleF(v.X, top - 1, v.Width, nose - top + 2);
        using (var wide = new LinearGradientBrush(span, Color.FromArgb(0, 255, 244, 205), Color.FromArgb(58, 255, 244, 205), 90f))
            g.FillPolygon(wide, new[]
            {
                new PointF(bx - pw * 0.44f, nose), new PointF(bx + pw * 0.44f, nose),
                new PointF(bx + v.Width * 0.4f, top), new PointF(bx - v.Width * 0.4f, top)
            });
        foreach (float side in new[] { -1f, 1f })   // the two bright cores, one per lamp
            using (var beam = new LinearGradientBrush(span, Color.FromArgb(0, 255, 250, 225), Color.FromArgb(70, 255, 250, 225), 90f))
                g.FillPolygon(beam, new[]
                {
                    new PointF(bx + side * pw * 0.17f, nose), new PointF(bx + side * pw * 0.42f, nose),
                    new PointF(bx + side * v.Width * 0.19f, top + v.Height * 0.05f),
                    new PointF(bx + side * v.Width * 0.03f, top + v.Height * 0.05f)
                });
        foreach (float side in new[] { -1f, 1f })   // the lamps themselves, glowing at the front
            using (var lamp = new SolidBrush(Color.FromArgb(150, 255, 250, 225)))
                g.FillEllipse(lamp, bx + side * pw * 0.3f - pw * 0.1f, nose - pw * 0.05f, pw * 0.2f, pw * 0.1f);
    }

    // The windscreen breaking: cracks spread out from the point of impact over about half a second.
    void DrawCracks(Graphics g)
    {
        var r = new Random(crackSeed);
        float cx = v.X + v.Width * (0.3f + (float)r.NextDouble() * 0.4f);
        float cy = v.Y + v.Height * (0.28f + (float)r.NextDouble() * 0.3f);
        float reach = v.Width * 0.95f * Math.Min(1f, crackT * 1.5f);
        int a = (int)(235 * Math.Min(1f, crackT * 3));
        if (crackT < 0.25f)   // the white flash of the hit
            using (var flash = new SolidBrush(Color.FromArgb((int)(150 * (1 - crackT * 4)), 255, 255, 255)))
                g.FillRectangle(flash, v);
        using (var pen = new Pen(Color.FromArgb(a, 245, 248, 255), Math.Max(1f, P(1.3f))))
        using (var web = new Pen(Color.FromArgb(a / 2, 245, 248, 255), Math.Max(1f, P(0.7f))))
        {
            float[] ends = new float[26];
            for (int i = 0; i < 13; i++)
            {
                double ang = i * Math.PI * 2 / 13 + r.NextDouble() * 0.35;
                float len = reach * (0.35f + (float)r.NextDouble() * 0.65f);
                float mx = cx + (float)Math.Cos(ang + 0.2) * len * 0.5f, my = cy + (float)Math.Sin(ang + 0.2) * len * 0.5f;
                float ex = cx + (float)Math.Cos(ang) * len, ey = cy + (float)Math.Sin(ang) * len;
                g.DrawLine(pen, cx, cy, mx, my);
                g.DrawLine(pen, mx, my, ex, ey);
                ends[i * 2] = mx;
                ends[i * 2 + 1] = my;
            }
            for (int i = 0; i < 13; i++)   // rings joining the spokes
            {
                int j = (i + 1) % 13;
                g.DrawLine(web, ends[i * 2], ends[i * 2 + 1], ends[j * 2], ends[j * 2 + 1]);
            }
        }
        FillCircle(g, Color.FromArgb(a, 255, 255, 255), cx, cy, P(3) * Math.Min(1f, crackT * 3));
    }

    // A band running along the road at a given offset across it (-1 left edge, 1 right edge).
    void Band(Graphics g, Brush b, Seg s, float at, float e1, float e2, float y1, float y2)
    {
        float bx1 = s.X1 + at * s.W1, bx2 = s.X2 + at * s.W2;
        Quad(g, b, bx1 - e1, bx1 + e1, y1, bx2 - e2, bx2 + e2, y2);
    }

    void Project(float wy, float wz, float camX, float camY, out float sx, out float sy, out float sw)
    {
        float dz = Math.Max(1f, wz - distance);
        float scale = CamDepth / dz;
        sx = v.X + v.Width / 2 + scale * (0 - camX) * v.Width / 2;
        sy = v.Y + v.Height / 2 - scale * (wy - camY) * v.Height / 2;
        sw = scale * RoadW * v.Width / 2;
    }

    // Where something at (z, across) sits on screen this frame. False if it's hidden or off-view.
    bool Place(float z, float across, out float sx, out float sy, out float sw)
    {
        sx = sy = sw = 0;
        Seg s = SegAt(z);
        if (s.Frame != frame) return false;
        float pct = (z % SegLen) / SegLen;
        sy = Lerp(s.Y1, s.Y2, pct);
        sw = Lerp(s.W1, s.W2, pct);
        if (sy > s.Clip || sw <= 0) return false;
        sx = Lerp(s.X1, s.X2, pct) + across * sw;
        return true;
    }

    void Quad(Graphics g, Brush b, float lx1, float rx1, float y1, float lx2, float rx2, float y2)
    {
        quad[0] = new PointF(lx1, y1);
        quad[1] = new PointF(rx1, y1);
        quad[2] = new PointF(rx2, y2);
        quad[3] = new PointF(lx2, y2);
        g.FillPolygon(b, quad);
    }

    // Roadside scenery: 1 rock, 2 pole, 3 bush.
    void DrawProp(Graphics g, Seg s, float z, float across, byte kind)
    {
        float sx, sy, sw;
        if (!Place(z, across, out sx, out sy, out sw)) return;
        float u = sw / RoadW; // world units to pixels here
        if (u <= 0) return;
        if (kind == 2)
        {
            float h = 1200 * u, w = Math.Max(1f, 60 * u);
            using (var b = new SolidBrush(cRailPost)) g.FillRectangle(b, sx - w / 2, sy - h, w, h);
            using (var b = new SolidBrush(cRail)) g.FillRectangle(b, sx - w * 2, sy - h, w * 4, Math.Max(1f, w));
        }
        else
        {
            float r = (kind == 1 ? 300 : 220) * u;
            if (r < 1) return;
            using (var b = new SolidBrush(kind == 1 ? cRock : Mix(cGrassA, 0.8f)))
            {
                g.FillEllipse(b, sx - r, sy - r * 1.3f, r * 2, r * 1.5f);
                if (kind == 3) g.FillEllipse(b, sx - r * 0.5f, sy - r * 1.9f, r * 1.4f, r * 1.3f);
            }
        }
    }

    Color PlayerColor(Palette pal)
    {
        int car = Chosen;
        if (car == CarGhost) return Color.White;
        if (car == CarRacing) return Xp ? Rgb(0x16459E) : IsVibrant ? Color.White : Desk ? pal.Blue : pal.Blue;
        if (car == CarOpen) return Xp ? Rgb(0x1E1E24) : IsVibrant ? Color.White : Desk ? pal.Orange : pal.Fg;
        if (Xp) return Rgb(0xD92B2B);
        if (IsVibrant) return Color.White;
        return Desk ? pal.Orange : pal.Red;
    }

    Color TrafficColor(int kind, Palette pal)
    {
        if (IsVibrant) return Color.FromArgb(Dark ? 210 : 230, 25, 28, 34);
        if (Xp)
        {
            Color[] c = { Rgb(0x2F6FD0), Rgb(0xD6B531), Rgb(0x8E44AD), Rgb(0x2E7D5B), Rgb(0xE8E8E8) };
            return c[kind];
        }
        Color[] p = { pal.Blue, pal.Yellow, pal.Purple, pal.Green, Dark ? pal.Fg2 : Color.White };
        return p[kind];
    }

    // ---- vehicles ----
    // The rear of a performance saloon, in fractions of the car's width and height: the roof tapers
    // in, the body swells over the back wheels, and it tucks back in at the bumper.
    static readonly float[] BodyU = { 0.04f, 0.00f, 0.02f, 0.09f, 0.19f, 0.31f, 0.50f, 0.69f, 0.81f, 0.91f, 0.98f, 1.00f, 0.96f };
    static readonly float[] BodyT = { 0.97f, 0.73f, 0.52f, 0.37f, 0.31f, 0.06f, 0.00f, 0.06f, 0.31f, 0.37f, 0.52f, 0.73f, 0.97f };
    static readonly float[] GlassU = { 0.33f, 0.50f, 0.67f, 0.72f, 0.28f };
    static readonly float[] GlassT = { 0.08f, 0.05f, 0.08f, 0.27f, 0.27f };
    readonly PointF[] bodyPts = new PointF[13], glassPts = new PointF[5];

    PointF[] Outline(float[] us, float[] ts, PointF[] into, float x, float y, float w, float h)
    {
        for (int i = 0; i < into.Length; i++) into[i] = new PointF(x + us[i] * w, y + ts[i] * h);
        return into;
    }

    static Color Lighten(Color c, float f)
    {
        return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));
    }

    // A modern open-wheel racer from behind: two huge rear tyres, a narrow body between them, the
    // rear wing up on swan-neck mounts, a single exhaust, the yellow rain light and the diffuser.
    void DrawOpenWheeler(Graphics g, float cx, float bottom, float w, Color body, bool braking)
    {
        float h = w * 0.78f, x = cx - w / 2, y = bottom - h;
        Color rubber = Rgb(0x121216), carbon = Rgb(0x15171E), accent = Mix(Lighten(body, 0.1f), 1f);

        using (var shadow = new SolidBrush(Color.FromArgb(95, 0, 0, 0)))
            g.FillEllipse(shadow, x - w * 0.03f, bottom - h * 0.07f, w * 1.06f, h * 0.15f);

        // Tyres, with a lighter band where the light catches the shoulder.
        foreach (float side in new[] { 0f, 1f })
        {
            float tx = side == 0 ? x : x + w * 0.705f;
            FillRound(g, rubber, new RectangleF(tx, y + h * 0.3f, w * 0.295f, h * 0.7f), w * 0.045f);
            if (w >= 22)
            {
                FillRound(g, Rgb(0x25252C), new RectangleF(tx + w * 0.02f, y + h * 0.33f, w * 0.255f, h * 0.06f), w * 0.02f);
                FillRound(g, Rgb(0x0C0C10), new RectangleF(tx + w * 0.03f, y + h * 0.88f, w * 0.235f, h * 0.1f), w * 0.015f);
            }
        }

        // Floor and diffuser between the wheels.
        FillRound(g, carbon, new RectangleF(cx - w * 0.21f, y + h * 0.72f, w * 0.42f, h * 0.28f), w * 0.02f);
        if (w >= 18)
            using (var fin = new SolidBrush(Rgb(0x2A2C35)))
                for (int i = 0; i < 5; i++)
                    g.FillRectangle(fin, cx - w * 0.185f + i * w * 0.09f, y + h * 0.78f, w * 0.022f, h * 0.2f);

        // Engine cover and crash structure, with the beam wing across it.
        FillRound(g, Mix(body, 0.8f), new RectangleF(cx - w * 0.13f, y + h * 0.32f, w * 0.26f, h * 0.46f), w * 0.05f);
        FillRound(g, body, new RectangleF(cx - w * 0.1f, y + h * 0.3f, w * 0.2f, h * 0.36f), w * 0.04f);
        if (w >= 20)
        {
            FillRound(g, accent, new RectangleF(cx - w * 0.055f, y + h * 0.31f, w * 0.11f, h * 0.2f), w * 0.02f);
            FillRound(g, carbon, new RectangleF(cx - w * 0.19f, y + h * 0.6f, w * 0.38f, h * 0.045f), w * 0.015f);
        }

        // Suspension arms reaching out to the wheels.
        if (w >= 24)
            using (var arm = new Pen(Rgb(0x3A3C46), Math.Max(1f, w * 0.018f)))
                foreach (float side in new[] { -1f, 1f })
                {
                    g.DrawLine(arm, cx + side * w * 0.11f, y + h * 0.42f, cx + side * w * 0.33f, y + h * 0.47f);
                    g.DrawLine(arm, cx + side * w * 0.11f, y + h * 0.62f, cx + side * w * 0.33f, y + h * 0.6f);
                }

        // Exhaust, then the rain light under it.
        if (w >= 16)
        {
            FillCircle(g, Rgb(0x30323A), cx, y + h * 0.56f, w * 0.045f);
            FillCircle(g, Rgb(0x0A0A0C), cx, y + h * 0.56f, w * 0.03f);
        }
        // The light panel doubles as the brake light: it burns red when you lift off.
        if (w >= 20)
        {
            if (braking || Dark)
                using (var glow = new SolidBrush(Color.FromArgb(braking ? 120 : 45, 255, 60, 40)))
                    g.FillEllipse(glow, cx - w * 0.17f, y + h * 0.57f, w * 0.34f, h * 0.26f);
            FillRound(g, Rgb(0xD8E000), new RectangleF(cx - w * 0.045f, y + h * 0.64f, w * 0.09f, h * 0.11f), w * 0.015f);
            Color led = braking ? Rgb(0xFF2A18) : Dark ? Rgb(0x8E2414) : Rgb(0x4A5000);
            using (var b = new SolidBrush(led))
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                        g.FillRectangle(b, cx - w * 0.03f + c * w * 0.025f, y + h * 0.665f + r * h * 0.026f, w * 0.012f, h * 0.014f);
        }

        // The rear wing, hung from two swan necks, with endplates at the tips.
        float wy = y + h * 0.1f, span = w * 0.86f;
        if (w >= 18)
            using (var neck = new SolidBrush(Rgb(0x23252C)))
            {
                g.FillRectangle(neck, cx - w * 0.07f, wy, w * 0.025f, h * 0.24f);
                g.FillRectangle(neck, cx + w * 0.045f, wy, w * 0.025f, h * 0.24f);
            }
        using (var wing = new SolidBrush(carbon))
        {
            g.FillRectangle(wing, cx - span / 2, wy, span, h * 0.085f);
            g.FillRectangle(wing, cx - span / 2, wy - h * 0.075f, w * 0.055f, h * 0.24f);
            g.FillRectangle(wing, cx + span / 2 - w * 0.055f, wy - h * 0.075f, w * 0.055f, h * 0.24f);
        }
        if (w >= 20)
        {
            using (var edge = new SolidBrush(Rgb(0x2E313A))) g.FillRectangle(edge, cx - span / 2, wy, span, h * 0.014f);
            FillRound(g, accent, new RectangleF(cx - span * 0.36f, wy + h * 0.03f, span * 0.72f, h * 0.025f), h * 0.012f);
            FillRound(g, body, new RectangleF(cx - w * 0.012f, wy - h * 0.1f, w * 0.024f, h * 0.1f), w * 0.01f); // aerial
        }
    }

    // A vehicle seen from behind, or head-on for oncoming traffic. Detail drops away with distance.
    // style: the chosen car for yours, -1 for ordinary traffic, -2 for the fast traffic.
    void DrawCar(Graphics g, float cx, float bottom, float w, Color body, int kind, bool braking, bool player, bool facing, int style)
    {
        if (style == CarOpen && player) { DrawOpenWheeler(g, cx, bottom, w, body, braking); return; }
        bool truck = kind == 4, van = kind == 3;
        float h = w * (truck ? 1.3f : van ? 0.95f : 0.74f);
        float x = cx - w / 2, y = bottom - h;

        using (var shadow = new SolidBrush(Color.FromArgb(85, 0, 0, 0)))
            g.FillEllipse(shadow, x - w * 0.05f, bottom - h * 0.09f, w * 1.1f, h * 0.18f);
        if (w < 8) { FillRound(g, body, new RectangleF(x, y + h * 0.35f, w, h * 0.6f), w * 0.2f); return; }

        Color shade = Mix(body, 0.72f), deep = Mix(body, 0.5f);
        using (var tyre = new SolidBrush(Rgb(0x15151A)))   // wheels first; the body sits over them
        {
            g.FillRectangle(tyre, x - w * 0.015f, y + h * 0.55f, w * 0.12f, h * 0.44f);
            g.FillRectangle(tyre, x + w * 0.895f, y + h * 0.55f, w * 0.12f, h * 0.44f);
        }

        if (truck || van)
        {
            float top = van ? 0.06f : 0f;
            FillRound(g, shade, new RectangleF(x, y + h * top, w, h * (0.95f - top)), w * 0.07f);
            FillRound(g, body, new RectangleF(x + w * 0.035f, y + h * (top + 0.03f), w * 0.93f, h * (0.88f - top)), w * 0.06f);
            if (w >= 16)
            {
                if (van) FillRound(g, Color.FromArgb(215, 18, 24, 32), new RectangleF(x + w * 0.14f, y + h * 0.12f, w * 0.72f, h * 0.2f), w * 0.05f);
                else using (var line = new Pen(deep, Math.Max(1f, w * 0.02f)))
                    {
                        g.DrawLine(line, x + w * 0.5f, y + h * 0.08f, x + w * 0.5f, y + h * 0.78f); // shutter split
                        g.DrawLine(line, x + w * 0.06f, y + h * 0.12f, x + w * 0.94f, y + h * 0.12f);
                    }
            }
        }
        else
        {
            PointF[] pts = Outline(BodyU, BodyT, bodyPts, x, y, w, h);
            using (var paint = new LinearGradientBrush(new RectangleF(x, y - 1, w, h + 2), Lighten(body, 0.32f), shade, 90f))
                g.FillClosedCurve(paint, pts, FillMode.Winding, 0.35f);
            if (w >= 14)
                using (var pen = new Pen(deep, Math.Max(1f, w * 0.015f)))
                    g.DrawClosedCurve(pen, pts, 0.35f, FillMode.Winding);
            using (var glass = new LinearGradientBrush(new RectangleF(x + w * 0.25f, y - 1, w * 0.5f, h * 0.3f + 2),
                Color.FromArgb(235, 52, 62, 78), Color.FromArgb(235, 14, 18, 26), 90f))
                g.FillClosedCurve(glass, Outline(GlassU, GlassT, glassPts, x, y, w, h), FillMode.Winding, 0.3f);
            if (w >= 18) FillRound(g, deep, new RectangleF(x + w * 0.17f, y + h * 0.29f, w * 0.66f, h * 0.035f), h * 0.016f); // boot lip
            if (style == CarRacing && player && w >= 16)
            {
                using (var stripe = new SolidBrush(Color.FromArgb(210, 245, 245, 250)))
                {
                    g.FillRectangle(stripe, x + w * 0.42f, y, w * 0.055f, h * 0.62f);
                    g.FillRectangle(stripe, x + w * 0.525f, y, w * 0.055f, h * 0.62f);
                }
                using (var wing = new SolidBrush(Rgb(0x1C1C20)))   // bolt-on rear wing
                {
                    g.FillRectangle(wing, x + w * 0.03f, y + h * 0.19f, w * 0.94f, h * 0.06f);
                    g.FillRectangle(wing, x + w * 0.06f, y + h * 0.19f, w * 0.05f, h * 0.12f);
                    g.FillRectangle(wing, x + w * 0.89f, y + h * 0.19f, w * 0.05f, h * 0.12f);
                }
            }
            else if (style == -2 && w >= 18)   // the fast traffic wears a lip spoiler
                FillRound(g, Rgb(0x1C1C20), new RectangleF(x + w * 0.14f, y + h * 0.26f, w * 0.72f, h * 0.045f), h * 0.02f);
            if (style == CarGhost && player && w >= 15)
            {
                FillCircle(g, Rgb(0x14387F), x + w * 0.5f, y + h * 0.38f, w * 0.075f);
                FillCircle(g, Color.White, x + w * 0.5f, y + h * 0.38f, w * 0.028f);
            }
        }

        if (w < 13) return;  // too far off for lights and trim
        float ly = y + h * (truck ? 0.8f : van ? 0.66f : 0.43f), lh = h * (truck || van ? 0.09f : 0.1f);

        if (facing)          // coming the other way: headlights and a grille, not tail lights
        {
            Color lamp = Rgb(0xFFF6D8);
            FillRound(g, lamp, new RectangleF(x + w * 0.06f, ly, w * 0.24f, lh), lh * 0.4f);
            FillRound(g, lamp, new RectangleF(x + w * 0.7f, ly, w * 0.24f, lh), lh * 0.4f);
            if (w >= 20) FillRound(g, Rgb(0x16161A), new RectangleF(x + w * 0.34f, ly + lh * 0.05f, w * 0.32f, lh * 0.95f), lh * 0.3f);
            using (var glow = new SolidBrush(Color.FromArgb(70, 255, 245, 205)))
                g.FillEllipse(glow, x - w * 0.12f, ly - h * 0.1f, w * 1.24f, h * 0.34f);
            return;
        }

        // Tail lights: a bar across each hip that hooks down toward the middle. They are lit all
        // the time at night, and brighter still under braking.
        Color core = braking ? Rgb(0xFF3A2A) : Dark ? Rgb(0xE5392B) : Rgb(0xC62F22), casing = Rgb(0x4A0F0F);
        float bw = w * 0.27f;
        for (int side = 0; side < 2; side++)
        {
            float lx = side == 0 ? x + w * 0.05f : x + w * 0.95f - bw;
            FillRound(g, casing, new RectangleF(lx, ly, bw, lh), lh * 0.35f);
            FillRound(g, core, new RectangleF(lx + bw * 0.08f, ly + lh * 0.18f, bw * 0.84f, lh * 0.64f), lh * 0.3f);
            if (w < 22 || truck || van) continue;
            float hx = side == 0 ? lx + bw * 0.62f : lx + bw * 0.03f;
            FillRound(g, casing, new RectangleF(hx, ly + lh * 0.7f, bw * 0.35f, lh * 0.95f), lh * 0.25f);
            FillRound(g, core, new RectangleF(hx + bw * 0.06f, ly + lh * 0.82f, bw * 0.23f, lh * 0.7f), lh * 0.2f);
        }
        if (braking || Dark)
        {
            if (braking && w >= 20 && !truck && !van)
                FillRound(g, Rgb(0xFF4433), new RectangleF(x + w * 0.41f, y + h * 0.055f, w * 0.18f, h * 0.025f), h * 0.012f);
            using (var glow = new SolidBrush(Color.FromArgb(braking ? (player ? 95 : 75) : 42, 255, 60, 40)))
                g.FillEllipse(glow, x - w * 0.05f, ly - h * 0.07f, w * 1.1f, h * 0.38f);
        }
        if (w < 26 || truck) return;

        // Number plate between twin pipes, with a dark diffuser under the bumper.
        float by = y + h * (van ? 0.78f : 0.62f);
        FillRound(g, Rgb(0x27272B), new RectangleF(x + w * 0.13f, by + h * 0.14f, w * 0.74f, h * 0.13f), w * 0.03f);
        FillRound(g, Rgb(0xE8E8EA), new RectangleF(x + w * 0.41f, by, w * 0.18f, h * 0.085f), w * 0.015f);
        using (var pipe = new SolidBrush(Rgb(0xB4B8C0)))
        {
            g.FillEllipse(pipe, x + w * 0.22f, by + h * 0.155f, w * 0.1f, h * 0.07f);
            g.FillEllipse(pipe, x + w * 0.68f, by + h * 0.155f, w * 0.1f, h * 0.07f);
        }
    }

    void DrawSky(Graphics g, Palette pal)
    {
        float hz = v.Y + v.Height * 0.5f;
        if (!IsVibrant)
        {
            Color top, bottom;
            if (Xp) { top = Dark ? Rgb(0x0A1430) : Rgb(0x2E7BD6); bottom = Dark ? Rgb(0x1E2E52) : Rgb(0xBCE2FF); }
            else if (Desk) { top = Dark ? Rgb(0x0E0E10) : Rgb(0xE8E8ED); bottom = Dark ? Rgb(0x1C1C1E) : Color.White; }
            else { top = Dark ? Rgb(0x16161A) : Rgb(0xBFE0FF); bottom = Dark ? Rgb(0x26262B) : Rgb(0xEAF5FF); }
            using (var sky = new LinearGradientBrush(new RectangleF(v.X, v.Y - 1, v.Width, v.Height * 0.55f + 1), top, bottom, 90f))
                g.FillRectangle(sky, v.X, v.Y, v.Width, v.Height * 0.55f);
            if (Dark)
                for (int k = 0; k < 26; k++)
                {
                    float sx = v.X + ((k * 41.3f) % v.Width + v.Width) % v.Width;
                    float sy = v.Y + (k * 17 % 100) / 100f * (hz - v.Y) * 0.8f;
                    FillCircle(g, Color.FromArgb(Desk ? 80 : 160, 255, 255, 255), sx, sy, Math.Max(0.8f, v.Width * 0.003f));
                }
            else if (Xp)
            {
                FillCircle(g, Color.FromArgb(230, 255, 240, 170), v.X + v.Width * 0.74f, v.Y + v.Height * 0.12f, v.Height * 0.06f);
                FillCircle(g, Color.FromArgb(55, 255, 250, 200), v.X + v.Width * 0.74f, v.Y + v.Height * 0.12f, v.Height * 0.1f);
            }
        }
        // Headlands along the horizon, drifting as the road bends.
        Color hill = Xp ? (Dark ? Rgb(0x15291C) : Rgb(0x3E8C4A)) : IsVibrant ? Color.FromArgb(Dark ? 26 : 45, 255, 255, 255)
            : Desk ? (Dark ? Rgb(0x202024) : Rgb(0xDCDCE1)) : Color.FromArgb(Dark ? 60 : 110, pal.Sep);
        using (var b = new SolidBrush(hill))
            for (int k = -1; k < 6; k++)
            {
                float hw = v.Width * 0.55f, hh = v.Height * (0.07f + (k & 1) * 0.05f);
                float hx = v.X + ((k * hw * 0.8f - distance * 0.0005f) % (hw * 5) + hw * 5) % (hw * 5) - hw;
                g.FillEllipse(b, hx, hz - hh, hw, hh * 2);
            }

        // Shooting stars: one every 131 points.
        foreach (var s in stars)
        {
            float sx = v.X + s.X * v.Width, sy = v.Y + s.Y * v.Height;
            if (sy > hz) continue;
            int a = (int)(255 * Math.Min(1f, s.Life * 1.6f));
            using (var tail = new Pen(Color.FromArgb(a / 2, 255, 255, 255), Math.Max(1f, P(1.3f))))
            {
                tail.StartCap = LineCap.Round;
                tail.EndCap = LineCap.Round;
                g.DrawLine(tail, sx - s.Vx * v.Width * 0.16f, sy - s.Vy * v.Height * 0.16f, sx, sy);
            }
            FillCircle(g, Color.FromArgb(a, 255, 255, 255), sx, sy, Math.Max(1f, P(1.6f)));
        }
    }

    void DrawHeader(Graphics g, Palette pal)
    {
        RectangleF h = Header();
        string km = Km.ToString("0.0", Inv) + " km";
        if (Xp)
        {
            Bevel(g, h, P(2), XpLo, XpHi);
            float lw = P(52), lh = P(30), ly = h.Y + (h.Height - lh) / 2;
            Lcd(g, Kph, new RectangleF(h.X + P(6), ly, lw, lh));
            Font f = F(11, "bold");
            Label(g, "KM/H", f, XpText, h.X + P(62), ly + P(1));
            Label(g, km, F(11, "regular"), XpText, h.X + P(62), ly + P(15));
            Label(g, score.ToString(), F(15, "bold"), XpText, h.Right - P(6), ly, P(110), StringAlignment.Far);
            Label(g, "Best " + best + (TwoWay ? "  two-way" : ""), F(11, "regular"), XpText, h.Right - P(6), ly + P(17), P(140), StringAlignment.Far);
            return;
        }
        Color fg = IsVibrant ? Ink : pal.Fg, fg2 = IsVibrant ? Color.FromArgb(Dark ? 175 : 150, Ink) : pal.Fg2;
        if (Desk)
        {
            // Like the app's own widgets: a small orange title, a grey status on the right, one big number.
            Label(g, "Redline", F(13, "semibold"), pal.Orange, h.X, h.Y);
            Label(g, (TwoWay ? "Two-Way   " : "One-Way   ") + km, F(11, "regular"), fg2, h.Right, h.Y + P(2), h.Width / 2, StringAlignment.Far);
            Label(g, Kph.ToString(), F(22, "semibold"), fg, h.X, h.Y + P(14));
            Label(g, "km/h", F(11, "regular"), fg2, h.X + TextWidth(g, Kph.ToString(), F(22, "semibold")) + P(4), h.Y + P(25));
            Label(g, score + "   best " + best, F(13, "semibold"), fg, h.Right, h.Y + P(20), h.Width * 0.6f, StringAlignment.Far);
            return;
        }
        Font cap = F(11, "semibold"), big = F(20, "semibold");
        float third = h.Width / 3;
        string[] caps = { "KM/H", TwoWay ? "TWO-WAY" : "SCORE", "BEST" };
        string[] vals = { Kph.ToString(), score.ToString(), best.ToString() };
        for (int i = 0; i < 3; i++)
        {
            float cx = h.X + third * i + third / 2;
            Label(g, caps[i], cap, fg2, cx, h.Y, third, StringAlignment.Center);
            Label(g, vals[i], big, fg, cx, h.Y + P(13), third, StringAlignment.Center);
        }
    }

    void DrawOverlay(Graphics g, Palette pal)
    {
        bool light = !Xp && !Dark;
        Color text = light ? Rgb(0x1C1C1E) : Color.White;
        using (var dim = new SolidBrush(light ? Color.FromArgb(190, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(dim, v);
        float cx = v.X + v.Width / 2;
        string title, line;
        if (crashed) { title = "CRASHED!"; line = "Scored " + score + ".  Click to go again"; }
        else if (played) { title = "PAUSED"; line = "Click to carry on"; }
        else { title = "REDLINE"; line = TwoWay ? "Click to drive two-way" : "Click to drive"; }
        Color titleColor = crashed ? (light ? pal.Red : Color.FromArgb(255, 110, 100)) : Desk ? pal.Orange : text;
        Label(g, title, F(28, "bold"), titleColor, cx, v.Y + v.Height * 0.24f, v.Width, StringAlignment.Center);
        Label(g, line, F(14, "semibold"), text, cx, v.Y + v.Height * 0.24f + P(42), v.Width, StringAlignment.Center);
        Font small = F(11, "regular");
        Color dimText = Color.FromArgb(light ? 170 : 200, text);
        Label(g, "Up or W to accelerate, Down or S to brake", small, dimText, cx, v.Bottom - P(60), v.Width, StringAlignment.Center);
        Label(g, "Left/Right or A/D to change lane.  Esc pauses", small, dimText, cx, v.Bottom - P(44), v.Width, StringAlignment.Center);
        Label(g, "Squeeze past a car for bonus points.  Best " + best, small, dimText, cx, v.Bottom - P(28), v.Width, StringAlignment.Center);
    }

    static Color Mix(Color c, float f) { return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f)); }

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
        string title = "Redline - " + (TwoWay ? "Two-Way" : "Highway");
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