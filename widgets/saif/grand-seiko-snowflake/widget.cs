using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskWidgets;

// A clock drawn to the dial of a Grand Seiko SBGA211. Every dimension below was measured off
// Grand Seiko's own product photograph rather than recalled, and is written as a fraction of the
// dial diameter. The snow field, the applied markers, the date frame and the power-reserve fan
// are one height map, shaded per pixel: surface normals lit by a studio environment, so polished
// steel goes bright where it reflects the softbox and near-black where it reflects the room.
//
// The crown works: slide it right for two detents, the first to quickset the date, the second to
// set the time with the seconds hacked. Nothing touches the system clock - the offset belongs to
// this widget and is remembered.
//
// Personal build. The GS emblem and the "Grand Seiko" wordmark are Seiko's marks, so this is not
// for the gallery; turn Logo off in the menu and the dial is all that is left.
[DeskWidget(Name = "Snowflake", Description = "A clock drawn to the dial of a Grand Seiko Snowflake")]
public sealed class SnowflakeClock : WidgetWindow
{
    // ===================== measured geometry, as fractions of the dial diameter =====================
    const float DashIn = 0.463f, DashOut = 0.481f, DashW = 0.0076f;
    const float D3In = 0.467f, D3Out = 0.491f, D3W = 0.0144f;
    const float MkIn = 0.336f, MkOut = 0.483f, MkWo = 0.056f, MkWi = 0.032f, Mk12Wo = 0.070f, Mk12Wi = 0.040f;
    const float DateR = 0.388f, DateBoxW = 0.138f, DateBoxH = 0.102f, ApW = 0.115f, ApH = 0.080f, NumH = 0.0524f;
    const float PrR = 0.200f, PrAng = 211f, PrRad = 0.167f, PrA0 = 170.0f, PrA1 = 278.8f;
    const float SdR = 0.242f, SdW = 0.175f;
    const float HourL = 0.325f, HourW = 0.058f, MinL = 0.460f, MinW = 0.051f, TailL = 0.088f;
    const float SecL = 0.465f, SecT = 0.175f, SecW0 = 0.0090f, SecW1 = 0.0048f;
    const float SecT0 = 0.0148f, SecT1 = 0.0222f, SecRo = 0.0172f, SecRi = 0.0100f;
    const float Plateau = 0.0070f, Bevel = 1.90f, Roof = 0.30f;

    // The emblem and the wordmark, lifted pixel-exact from the reference photograph as luminance
    // maps and gated against the dial so only the mark itself carries through.
    const int WmW = 129, WmH = 21;
    const float WmWd = 0.28166f, WmHd = 0.04585f, WmDx = 0.00253f, WmR = 0.19760f;
    const int EmW = 50, EmH = 39;
    const float EmWd = 0.10917f, EmHd = 0.08515f, EmDx = 0.00362f, EmR = 0.26092f;
    const string WmData = "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";
    const string EmData = "8e/v8vHx7fXw6Ovx7+vq4uLh4+Xr6Ojn5ezr6/Dd5OTi6dzi2Ofz5+fs8efy7unu8fDy6e306/X2+vLn6ujr5uzt6Ojl7/Ps6+rt6fHz6uLu7ebk4t/i6+vh7fjx3+z08evt7/X28/Hv+Pv27OTp8/v+/fvu6ej07u7y7O/t8+3x9fHm5O3w6OLh5eLn8e7l7vH27O/l+PXw9/j49ero6vzroXRzrPDs6Onq8enn8fDs7vrz4u//8Ons6+Xa2Orz8e727PXv9uHy8vX5+/To6Ob+zF01MTlJqvvr6ers7Ovl7Pf67eX05b3y8ubq7eXa5fn07O/w9uju6vX29/ny5+fp99pTJVp/l4mV7O/s6PnPhaTk+ero7PBkLX3u+eXp5urg6PL29fbv6fbw+ff4+ern6+r8kCdXnrHAtanb8ef421lGrvDr6+j7fCJBVnbW/Orq5eTm4/X39e/v+vP29/r16enn9ORTQpG6w8Ovudvw8/F6Hnne7Onn7+c1RZGnfmS1+Ovj5uevw+7n7/by9vb6+e7p6uf+sTphscLLy8PO7uv3px9Jpdno7evs4kJkp72yh2eZ5OvwqEaQ5/P68fbl9/z16Ojs7P+APIbHzdTU2OPt9cs1NIm7zubo6Oz0dGu0u7vFpXV41ONaNaHf8fPq++n3+O7p7ejv8WJIm8fS2Obk6/PiVh1srcjP6O7p5e3Sdq/EyMnDuYp9dTpqxuju8Pbx4u/v7Ovq6PnPRluz2Orl5uju8ooKWZu6wcHZ6ufm5+3Go77K2dHKw6NvZ6PR6PTx9PLj8PLp6urp/7E8bsPe6Onl6+3ulUKDrL/Kv7zj6+Ll4ujPtMXl29TSzLmwxdvj8PT18uv18evq6+z/mTl+0+Ps6uzm6un02K2quc/Jo7jo6ODe4+DQutXl3drUzczS3Ov19PLu8/Hr6Orr7P+GPpLe7evn6+no6+jz6829zM3AlbTf4t3b1trKxtvm39TS1dfe8vTp6en07Obo6+rr/4VLnuLp6Obv+Ozl6OPp7djT1tC/kKHX3dPT1NXGvtfd39jd5d7l6OPi6urr6Ont6Oz+hlCj4+nj9Nqu4enk5uHm7L+w1tTJjJDP19DNz8zDwNDc2t3j2dnm6ufq6+nq6Ovo7fl+U7Ln5vDuYBWX9+Hi4uq7VHjO1dzKg5DD1crHycnGtcfX0dvf4Onq6enw5+/n6Ort83VYyuvm+4cSNoHt5eHg7okmgcTX2tjCiYS70sXHxce7t8bN0NLU3OHo6Ozt7+fp5+vxaHPk6Pm1Ij58p93j3tvlhkimydjX3tLCkYKy0sTFw8O9uL7Nz9DU3ubg6/Lk4+no6umStOrv3UIveaO11t/Z292BVbPL1t3gzdXLmX2vzsTBv8S+sc/W2eHc3tzk7+Pd6OXm497n5vdyImeiurfX2NTb1XhZt8jZ3+HX0czEkn6uyMPCwsG8t8Td6ODi3+Lt1tTp4uTk6d7yoipNlLK8v9PS0dTObmK1xNvi3dXUzc66noCtzcDAwsSun8vq5eTl5O/U0+ji4d7a7Ms+OYKvwbvLz8/OzsdoZrfQ3snE2N3PzsjLsI2px8HAyqpysOng4+ng7NPH3eHg1+HkaCltrsHExc/Kz8vOwWhpw+bQvb6/09TPytPTrI+8w8HMsWKh5uPm6OLp08/W3djb6pAoUqfAwMjO0cTHyNC8ZXPR3cO+xb62vuLOztjJrbLCwc6uWqDw6fDn3+bZ2c/R2NfjcTWCu8vJ0M3Uw8XFzrhhftW7vsTDxMO9zt3c4tHGwMC/1oI8oerq6+fi8d/k2bvX0N2sYJfG0NDY3t29xMXMsVyD4c6mucXCw8K9yN/g3su6xMx+J02s4OHi6Onj5fHUqsTQy9Wmmr/Q1d7f2L7CwcyuWIHY5Y9ysMfCwcC5xerWu8q/XRo8jMfk6Onv5OLp6daxpszNx8uvvNvp6djNv8TEyqRRiNfixn9dnMTJvsK8wsPNnDUfTYe8097h5PDd6Ojf186nncfMysjEzsrHwsXEycDMp1KK1NnNwo9VdbXMw8XIwHQbJGKZu8nS2tvi8+fl5+Ta3NWUharDx8rIwMC3nIaMnpx3So/PzcvFvqdmY6vEw51IFzl7qr7Ey9XT1+Lw8+Ts7uDg58OKb3iOnKKdf1UxLT9GOjhep83NzMrFxb+KbJaDIyVckrvFx8XM3tTV3+3t6ufl3+Hn2sqmcltRSUM6SWaCmpGAianAy8/K083H08qmdU1GdaO6ycfJz8/RzNzf6ebv7ejj3ubd1tS/q5OLiIynvcXQz8HI08fHz9LY1dbRz825n5q3w8LIzM7W1cvQ59/i6e707OTh7OLV19fVx8/LzdrY1dbUy8vOxcnS2t7X19jT09HIv8jRx8nS1dHPytTb1urm8+3k7ezq3tne39nU0s/a29XV2NDIy9HIyc/V4t/d4NjV1MrJy8/LzcjHyM3Rzs7X4t3m6erz7Obl5+Lf3tvW0tfY09jWzMjJzMfGy9fh2+Dj1M/VxcnMycbJ2tHHztHO3OTf2+Lu7evr6+jl4ePh3NrV19zU1dfRxsDDzcfL1dzV197Z1s/IxsXD1Myix+HKxtLn5enp";

    const float CrownWide = 1.085f;   // widget width as a multiple of the dial box, crown shown

    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    public SnowflakeClock(Section settings) : base(settings) { }

    // ===================== settings =====================
    int DialSize { get { return Math.Max(120, Settings.GetInt("size", 220)); } }
    bool WithCase { get { return Settings.Get("frame", "dial") == "case"; } }
    bool WithCrown { get { return Settings.GetBool("crown", true); } }
    bool WithLogo { get { return Settings.GetBool("logo", true); } }
    bool WithDate { get { return Settings.GetBool("date", true); } }
    bool WithReserve { get { return Settings.GetBool("reserve", true); } }
    string ReserveSource { get { return Settings.Get("pr", "auto"); } }

    int OffsetMin
    {
        get { return Settings.GetInt("offset_min", 0); }
        set { Settings.Set("offset_min", value); }
    }
    int DateOff
    {
        get { return Settings.GetInt("date_off", 0); }
        set { Settings.Set("date_off", value); }
    }

    // ===================== layout =====================
    int Strip(int d) { return WithCrown ? (int)Math.Round(d * (CrownWide - 1f)) : 0; }

    protected override bool SquareResize { get { return false; } }

    protected override Size Measure()
    {
        int d = Px(DialSize);
        return new Size(d + Strip(d), d);
    }

    protected override void OnResized(int width, int height)
    {
        int d = Math.Max(Px(100), height);
        Settings.Set("custom_w", d + Strip(d));
        Settings.Set("custom_h", d);
        Invalidate2();
    }

    void Invalidate2()
    {
        if (dial != null) { dial.Dispose(); dial = null; }
        if (glass != null) { glass.Dispose(); glass = null; }
        Render();
    }

    void Changed() { Invalidate2(); }

    // ===================== the crown =====================
    int crownPos;                 // 0 pushed in, 1 quickset date, 2 setting time
    DateTime hackedAt;
    bool hacked;
    Rectangle crownRect;

    void SetCrown(int p)
    {
        if (p < 0) p = 0;
        if (p > 2) p = 2;
        if (p == crownPos) return;
        crownPos = p;
        hacked = p == 2;
        if (hacked) hackedAt = DateTime.Now;
        Render();
    }

    void SetOffset(int v)
    {
        v = ((v + 720) % 1440 + 1440) % 1440 - 720;
        if (v == OffsetMin) return;
        OffsetMin = v;
        Render();
    }

    void SetDateOff(int v)
    {
        if (v < -365) v = -365;
        if (v > 365) v = 365;
        if (v == DateOff) return;
        DateOff = v;
        Render();
    }

    DateTime DialNow()
    {
        DateTime b = hacked ? hackedAt : DateTime.Now;
        return b.AddMinutes(OffsetMin);
    }

    int DialDate()
    {
        return DialNow().AddDays(DateOff).Day;
    }

    // A press on the crown, or any press while the crown is out, must not drag the widget.
    protected override bool IsButton(Point p)
    {
        return crownPos > 0 || crownRect.Contains(p);
    }

    bool crownDrag, crownMoved, winding;
    int dragX, dragBase, windY, windBase;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (WithCrown && crownRect.Contains(e.Location))
            {
                crownDrag = true; crownMoved = false; dragX = e.X; dragBase = crownPos;
            }
            else if (crownPos > 0)
            {
                winding = true; windY = e.Y;
                windBase = crownPos == 1 ? DateOff : OffsetMin;
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (crownDrag)
        {
            int dx = e.X - dragX;
            if (Math.Abs(dx) > 3) crownMoved = true;
            SetCrown(dragBase + (int)Math.Round(dx / (double)Math.Max(6, Px(16))));
            return;
        }
        if (winding)
        {
            int d = windY - e.Y;
            if (crownPos == 1) SetDateOff(windBase + (int)Math.Round(d / (double)Math.Max(5, Px(14))));
            else SetOffset(windBase + (int)Math.Round(d / (double)Math.Max(2, Px(3))));
            return;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasCrown = crownDrag, moved = crownMoved, wasWind = winding;
        crownDrag = false; winding = false;
        if (e.Button == MouseButtons.Left && wasCrown && !moved) SetCrown(crownPos == 0 ? 1 : 0);
        if (e.Button == MouseButtons.Left && (wasCrown || wasWind)) return;
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (crownPos == 0) { base.OnMouseWheel(e); return; }
        int dir = e.Delta > 0 ? 1 : -1;
        if (crownPos == 1) SetDateOff(DateOff + dir);
        else SetOffset(OffsetMin + dir * ((Control.ModifierKeys & Keys.Shift) != 0 ? 60 : 1));
    }

    // ===================== frame loop =====================
    protected override int NextFrameDelay()
    {
        if (hacked) return -1;              // the seconds hand is stopped; nothing moves
        if (IsCovered()) return 400;
        return 33;                          // the glide motion of a Spring Drive
    }

    // ===================== deterministic anisotropic value noise =====================
    static uint seed = 0x5E7A11;
    static float Rnd()
    {
        unchecked
        {
            seed = seed + 0x6D2B79F5u;
            uint t = (seed ^ (seed >> 15)) * (1u | seed);
            t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296f;
        }
    }

    static readonly Dictionary<string, float[]> grids = new Dictionary<string, float[]>();
    static float[] Grid(int nx, int ny)
    {
        string k = nx.ToString(CultureInfo.InvariantCulture) + "x" + ny.ToString(CultureInfo.InvariantCulture);
        float[] g;
        if (grids.TryGetValue(k, out g)) return g;
        g = new float[nx * ny];
        for (int i = 0; i < g.Length; i++) g[i] = Rnd();
        grids[k] = g;
        return g;
    }

    static float Vn(float[] g, int nx, int ny, float x, float y)
    {
        float fx = x * nx, fy = y * ny;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        int x1 = ((x0 + 1) % nx + nx) % nx, y1 = ((y0 + 1) % ny + ny) % ny;
        x0 = (x0 % nx + nx) % nx; y0 = (y0 % ny + ny) % ny;
        float a = g[y0 * nx + x0], b = g[y0 * nx + x1], c = g[y1 * nx + x0], d = g[y1 * nx + x1];
        float t1 = a + (b - a) * tx, t2 = c + (d - c) * tx;
        return t1 + (t2 - t1) * ty;
    }

    // Five octaves, each stretched about 3:1 vertically, the two finest creased so they read as
    // wind-blown filaments rather than bumps, with a mask noise breaking them into drifts.
    static readonly int[] OctX = { 150, 76, 38, 17, 7 };
    static readonly int[] OctY = { 48, 26, 15, 9, 5 };
    static readonly float[] OctA = { 0.34f, 0.26f, 0.16f, 0.13f, 0.08f };
    static readonly bool[] OctC = { true, true, false, false, false };

    static float Snow(float x, float y)
    {
        float[] warp = Grid(9, 7), mask = Grid(26, 22);
        float wx = x + 0.055f * (Vn(warp, 9, 7, x, y) - 0.5f);
        float wy = y + 0.022f * (Vn(warp, 9, 7, y, x) - 0.5f);
        float mk = 0.30f + 1.05f * Vn(mask, 26, 22, x, y);
        float s = 0;
        for (int i = 0; i < OctX.Length; i++)
        {
            float v = Vn(Grid(OctX[i], OctY[i]), OctX[i], OctY[i], wx, wy);
            if (OctC[i]) v = Math.Abs(2 * v - 1) * mk;
            s += v * OctA[i];
        }
        return s;
    }

    static readonly Dictionary<int, float[]> fields = new Dictionary<int, float[]>();
    static float[] SnowField(int n)
    {
        float[] f;
        if (fields.TryGetValue(n, out f)) return f;
        f = new float[n * n];
        for (int j = 0; j < n; j++)
        {
            float y = j / (float)n;
            for (int i = 0; i < n; i++) f[j * n + i] = Snow(i / (float)n, y);
        }
        if (fields.Count > 3) fields.Clear();
        fields[n] = f;
        return f;
    }

    // ===================== shading =====================
    // Polished steel reflecting a studio: bright overhead, dark at the horizon, plus a
    // left-to-right term because the reference is lit from the right.
    static float EnvMetal(float nx, float ny, float posx)
    {
        float q = nx * nx + ny * ny; if (q > 1) q = 1;
        float nz = (float)Math.Sqrt(1 - q);
        float rx = 2 * nz * nx, ry = 2 * nz * ny;
        float m = (float)Math.Sqrt(rx * rx + ry * ry), a = 0;
        if (m > 1e-6f)
        {
            double phi = Math.Atan2(ry, rx);
            a = (float)((-5 + 22 * (-Math.Sin(phi)) - 62 * Math.Cos(2 * phi) + 30 * Math.Cos(phi))
                        * Math.Min(1, m * 1.25));
        }
        float e = 200 + a + 42 * posx;
        return e < 12 ? 12 : (e > 253 ? 253 : e);
    }

    static float ShadeSnow(float nx, float ny, float base_)
    {
        float q = nx * nx + ny * ny; if (q > 1) q = 1;
        float nz = (float)Math.Sqrt(1 - q);
        float d = nx * 0.20f + ny * -0.52f + nz * 0.830f; if (d < 0) d = 0;
        float v = base_ * (0.900f + 0.100f * d);
        float sp = nx * 0.1039f + ny * -0.2700f + nz * 0.9504f; if (sp < 0) sp = 0;
        float s2 = sp * sp, s4 = s2 * s2, s8 = s4 * s4;
        return v + 13f * s8 * s8;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = (x - a) / (b - a);
        if (t < 0) t = 0; if (t > 1) t = 1;
        return t * t * (3 - 2 * t);
    }

    // ===================== applied-part height fields =====================
    static float MarkerH(float u, float v, int hour)
    {
        if (u < MkIn - 0.02f || u > MkOut + 0.02f) return 0;
        float wo = hour == 0 ? Mk12Wo : MkWo, wi = hour == 0 ? Mk12Wi : MkWi;
        float t = (u - MkIn) / (MkOut - MkIn);
        float hw = (wi + (wo - wi) * t) * 0.5f;
        float av = v < 0 ? -v : v;
        float d = av - hw;
        float e1 = MkIn - u, e2 = u - MkOut;
        if (e1 > d) d = e1;
        if (e2 > d) d = e2;
        if (d >= 0) return 0;
        float roof = Plateau - Roof * Plateau * (av / hw), ramp = -d * Bevel;
        float h = roof < ramp ? roof : ramp;
        return h > 0 ? h : 0;
    }

    static float FrameH(float u, float v)
    {
        float au = u < 0 ? -u : u, av = v < 0 ? -v : v;
        float dOut = Math.Max(au - DateBoxW * 0.5f, av - DateBoxH * 0.5f);
        if (dOut >= 0) return 0;
        float dAp = Math.Max(au - ApW * 0.5f, av - ApH * 0.5f);
        if (dAp < 0) return -0.0035f;
        float h = Math.Min(Plateau * 1.25f, Math.Min(-dOut * Bevel * 0.95f, dAp * Bevel * 1.15f));
        return h > 0 ? h : 0;
    }

    static float PrH(float dx, float dy)
    {
        float r = (float)Math.Sqrt(dx * dx + dy * dy);
        if (r > PrRad + 0.02f) return 0;
        double a = Math.Atan2(dy, dx) * 180.0 / Math.PI; if (a < 0) a += 360;
        bool inSpan = a >= PrA0 && a <= PrA1;
        double rad = Math.PI / 180.0;
        float dA = inSpan
            ? (float)(-Math.Min(a - PrA0, PrA1 - a) * rad * r)
            : (float)(Math.Min(Math.Abs(a - PrA0), Math.Abs(a - PrA1)) * rad * r);
        float d = Math.Max(r - PrRad, dA);
        if (d >= 0) return 0;
        float h = Math.Min(Plateau * 0.86f, -d * Bevel * 1.15f);
        if (h <= 0) return 0;
        float t = r / PrRad;
        if (t > 0.70f && inSpan) h += (float)(0.00024 * ((t - 0.70f) / 0.30f) * Math.Cos(a * rad * 118));
        else h += (float)(0.00007 * Math.Cos(a * rad * 150));
        return h;
    }

    // ===================== the cached dial =====================
    Bitmap dial, glass;
    int dialFor;
    string dialKey = "";

    string Key(int d)
    {
        return d.ToString(CultureInfo.InvariantCulture) + "|" + (WithCase ? "c" : "d") + "|"
             + (WithLogo ? "l" : "-") + (WithDate ? "t" : "-") + (WithReserve ? "r" : "-");
    }

    Bitmap BuildDial(int d)
    {
        float bez = WithCase ? 0.064f : 0.012f;
        float rd = (0.5f - bez) * d, dd = 2 * rd;
        int n = (int)Math.Round(d * 1.7);
        if (n < 200) n = 200;
        if (n > 900) n = 900;

        float[] field = SnowField(n);
        float[] hgt = new float[n * n];
        byte[] mat = new byte[n * n];
        float sa = 0.0044f * 1.25f;                 // snow relief
        double rad = Math.PI / 180.0;
        float prPx = (float)(Math.Sin(PrAng * rad) * PrR), prPy = (float)(-Math.Cos(PrAng * rad) * PrR);

        for (int j = 0; j < n; j++)
        {
            float v = (j + 0.5f) / n - 0.5f;
            for (int i = 0; i < n; i++)
            {
                float u = (i + 0.5f) / n - 0.5f;
                int idx = j * n + i;
                if (u * u + v * v > 0.2520f) { mat[idx] = 255; continue; }
                float h = (field[idx] - 0.5f) * sa;
                byte m = 0;
                double ang = Math.Atan2(u, -v) * 180.0 / Math.PI; if (ang < 0) ang += 360;
                int hr = ((int)Math.Round(ang / 30.0)) % 12;
                if (!(hr == 3 && WithDate))
                {
                    double a = hr * 30 * rad;
                    float s = (float)Math.Sin(a), c = (float)Math.Cos(a);
                    float mh = MarkerH(u * s - v * c, u * c + v * s, hr);
                    if (mh > 0) { h = mh + h * 0.18f; m = 1; }
                }
                if (WithDate && m == 0)
                {
                    float fh = FrameH(u - DateR, v);
                    if (fh > 0) { h = fh + h * 0.10f; m = 1; }
                    else if (fh < 0) { h = fh; m = 2; }
                }
                if (WithReserve && m == 0)
                {
                    float ph = PrH(u - prPx, v - prPy);
                    if (ph > 0) { h = ph + h * 0.10f; m = 1; }
                }
                hgt[idx] = h; mat[idx] = m;
            }
        }

        byte[] px = new byte[n * n * 4];
        int shk = Math.Max(3, (int)Math.Round(n * 0.0085));
        for (int j = 0; j < n; j++)
        {
            float v = (j + 0.5f) / n - 0.5f;
            for (int i = 0; i < n; i++)
            {
                int idx = j * n + i, o = idx * 4;
                if (mat[idx] == 255) continue;      // left fully transparent
                float u = (i + 0.5f) / n - 0.5f;
                int xm = i > 0 ? idx - 1 : idx, xp = i < n - 1 ? idx + 1 : idx;
                int ym = j > 0 ? idx - n : idx, yp = j < n - 1 ? idx + n : idx;
                float gx = (hgt[xp] - hgt[xm]) * 0.5f * n, gy = (hgt[yp] - hgt[ym]) * 0.5f * n;
                float nl = (float)Math.Sqrt(gx * gx + gy * gy + 1);
                float nx = -gx / nl, ny = -gy / nl;
                float r2 = (float)Math.Sqrt(u * u + v * v) * 2;
                float val; int blue = 0;
                if (mat[idx] == 1) val = EnvMetal(nx, ny, u * 2);
                else if (mat[idx] == 2)
                {
                    float dux = (u - DateR) / ApW + 0.5f, dvy = v / ApH + 0.5f;
                    float eT = dvy < 0.17f ? 1 - dvy / 0.17f : 0, eL = dux < 0.11f ? 1 - dux / 0.11f : 0;
                    val = 241 + 4 * (dux - 0.5f) - 34 * eT - 17 * eL; blue = 1;
                }
                else
                {
                    val = ShadeSnow(nx, ny, 237 - 9 * Smooth(0.45f, 1.02f, r2)) - 8 * Smooth(0.93f, 1.01f, r2);
                    float sh = 0;
                    for (int k = 1; k <= shk; k++)
                    {
                        int si = i + (int)(k * 0.20f + 0.5f), sj = j - (int)(k * 0.98f + 0.5f);
                        if (si >= n || sj < 0) break;
                        if (mat[sj * n + si] == 1) { float f = 1 - (k - 1) / (float)shk; if (f > sh) sh = f; }
                    }
                    val -= 36 * sh;
                }
                byte cl = (byte)(val < 0 ? 0 : (val > 255 ? 255 : val));
                px[o] = (byte)Math.Min(255, cl + blue);   // B
                px[o + 1] = cl;                            // G
                px[o + 2] = cl;                            // R
                px[o + 3] = 255;                           // A
            }
        }

        Bitmap shaded = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        BitmapData bd = shaded.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int j = 0; j < n; j++)
                Marshal.Copy(px, j * n * 4, (IntPtr)(bd.Scan0.ToInt64() + (long)j * bd.Stride), n * 4);
        }
        finally { shaded.UnlockBits(bd); }

        Bitmap bmp = new Bitmap(d, d, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using (Graphics g = Graphics.FromImage(bmp))
        using (shaded)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            float cx = d / 2f;

            if (WithCase) DrawCase(g, d);

            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(cx - rd, cx - rd, rd * 2, rd * 2);
                g.SetClip(clip);
                g.DrawImage(shaded, cx - rd, cx - rd, dd, dd);
                g.ResetClip();
            }

            DrawPrint(g, cx, cx, dd);
            DrawRehaut(g, cx, cx, rd, d);
        }
        return bmp;
    }

    static void DrawCase(Graphics g, int d)
    {
        using (GraphicsPath p = new GraphicsPath())
        {
            p.AddEllipse(0.5f, 0.5f, d - 1f, d - 1f);
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(d * 0.08f, 0), new PointF(d * 0.92f, d),
                       Color.White, Color.FromArgb(0x7C, 0x83, 0x8B)))
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = new[] { 0f, .18f, .37f, .5f, .63f, .84f, 1f };
                cb.Colors = new[]
                {
                    Rgb(0xFDFDFE), Rgb(0xD7DCE1), Rgb(0xA8AFB7), Rgb(0xEAEDF0),
                    Rgb(0x9FA6AE), Rgb(0xCFD5DB), Rgb(0x7C838B)
                };
                b.InterpolationColors = cb;
                g.FillPath(b, p);
            }
        }
    }

    void DrawRehaut(Graphics g, float cx, float cy, float rd, int d)
    {
        using (LinearGradientBrush b = new LinearGradientBrush(
                   new PointF(cx - rd, cy - rd), new PointF(cx + rd, cy + rd),
                   Color.FromArgb(102, 128, 136, 146), Color.FromArgb(92, 138, 146, 156)))
        using (Pen pen = new Pen(b, Math.Max(1f, d * 0.0040f)))
            g.DrawEllipse(pen, cx - rd, cy - rd, rd * 2, rd * 2);

        if (WithCase)
        {
            float r2 = rd + d * 0.011f;
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(cx - rd, cy - rd), new PointF(cx + rd, cy + rd),
                       Rgb(0xFBFCFD), Rgb(0x8F969E)))
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = new[] { 0f, .3f, .55f, 1f };
                cb.Colors = new[] { Rgb(0xFBFCFD), Rgb(0xC3C9D0), Rgb(0xEDF0F2), Rgb(0x8F969E) };
                b.InterpolationColors = cb;
                using (Pen pen = new Pen(b, d * 0.019f))
                    g.DrawEllipse(pen, cx - r2, cy - r2, r2 * 2, r2 * 2);
            }
        }
    }

    static Color Rgb(int rgb) { return Color.FromArgb(unchecked((int)0xFF000000) | rgb); }

    // ===================== printed layer =====================
    void DrawPrint(Graphics g, float cx, float cy, float dd)
    {
        GraphicsState st = g.Save();
        g.TranslateTransform(cx, cy);

        using (SolidBrush ink = new SolidBrush(Rgb(0x202124)))
        {
            for (int m = 0; m < 60; m++)
            {
                bool big = m == 15 && WithDate;
                if (m % 5 == 0 && !big) continue;
                GraphicsState s2 = g.Save();
                g.RotateTransform(m * 6f);
                float r0 = (big ? D3In : DashIn) * dd, r1 = (big ? D3Out : DashOut) * dd;
                float w = (big ? D3W : DashW) * dd;
                using (GraphicsPath p = RoundRect(new RectangleF(-w / 2, -r1, w, r1 - r0),
                                                  Math.Min(w * 0.32f, dd * 0.002f)))
                    g.FillPath(ink, p);
                g.Restore(s2);
            }
        }

        if (WithDate)
        {
            float dx = DateR * dd, aw = ApW * dd, ah = ApH * dd;
            using (GraphicsPath p = RoundRect(new RectangleF(dx - aw / 2, -ah / 2, aw, ah), dd * 0.004f))
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(0, -ah / 2), new PointF(0, ah / 2), Rgb(0xE2E4E4), Rgb(0xE6E8E8)))
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = new[] { 0f, .26f, 1f };
                cb.Colors = new[] { Rgb(0xE2E4E4), Rgb(0xEFF1F1), Rgb(0xE6E8E8) };
                b.InterpolationColors = cb;
                g.FillPath(b, p);
            }
        }

        if (WithLogo)
        {
            Bitmap em = EmblemBitmap(), wm = WordmarkBitmap();
            float ew = EmWd * dd, eh = EmHd * dd;
            g.DrawImage(em, EmDx * dd - ew / 2, -EmR * dd - eh / 2, ew, eh);
            float ww = WmWd * dd, wh = WmHd * dd;
            g.DrawImage(wm, WmDx * dd - ww / 2, -WmR * dd - wh / 2, ww, wh);

            using (SolidBrush b = new SolidBrush(Rgb(0x1C2F6E)))
                Tracked(g, "SPRING DRIVE", 0, SdR * dd, SdW * dd, dd * 0.0215f, b);
        }
        g.Restore(st);
    }

    // ===================== the lifted marks =====================
    static Bitmap wmBmp, emBmp;

    static Bitmap WordmarkBitmap()
    {
        if (wmBmp == null) wmBmp = Sprite(WmData, WmW, WmH, true);
        return wmBmp;
    }
    static Bitmap EmblemBitmap()
    {
        if (emBmp == null) emBmp = Sprite(EmData, EmW, EmH, false);
        return emBmp;
    }

    static Bitmap Sprite(string data, int w, int h, bool wordmark)
    {
        byte[] lum = Convert.FromBase64String(data);
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < w * h && i < lum.Length; i++)
        {
            int v = lum[i], o = i * 4;
            float a;
            byte r, gg, bb;
            if (wordmark)
            {
                a = (228 - v) / 178f;
                r = 23; gg = 25; bb = 27;
            }
            else
            {
                a = (Math.Abs(v - 233) - 12) / 16f;
                int q = Math.Min(255, v + 4);
                r = (byte)q; gg = (byte)q; bb = (byte)Math.Min(255, q + 2);
            }
            if (a < 0) a = 0; if (a > 1) a = 1;
            a = a * a * (3 - 2 * a);
            px[o] = bb; px[o + 1] = gg; px[o + 2] = r; px[o + 3] = (byte)Math.Round(a * 255);
        }
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int j = 0; j < h; j++)
                Marshal.Copy(px, j * w * 4, (IntPtr)(bd.Scan0.ToInt64() + (long)j * bd.Stride), w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    // ===================== text =====================
    static FontFamily numerals;
    static FontFamily Numerals()
    {
        if (numerals != null) return numerals;
        string[] want = { "Century Gothic", "Bahnschrift", "Segoe UI" };
        foreach (string name in want)
        {
            try { numerals = new FontFamily(name); break; }
            catch (ArgumentException) { }
        }
        if (numerals == null) numerals = FontFamily.GenericSansSerif;
        return numerals;
    }

    static void Glyph(Graphics g, string s, FontFamily fam, FontStyle style, float em,
                      float cx, float cy, float stretch, Brush brush)
    {
        using (GraphicsPath p = new GraphicsPath())
        {
            p.AddString(s, fam, (int)style, em, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF b = p.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            using (Matrix m = new Matrix())
            {
                m.Translate(cx, cy);
                m.Scale(stretch, 1f);
                m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                p.Transform(m);
            }
            g.FillPath(brush, p);
        }
    }

    static void Tracked(Graphics g, string text, float cx, float cy, float width, float em, Brush brush)
    {
        FontFamily fam = Numerals();
        float[] w = new float[text.Length];
        float total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString(text.Substring(i, 1), fam, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = p.GetBounds();
                w[i] = b.Width <= 0 ? em * 0.4f : b.Width;
            }
            total += w[i];
        }
        float gap = text.Length > 1 ? (width - total) / (text.Length - 1) : 0;
        float x = cx - width / 2;
        for (int i = 0; i < text.Length; i++)
        {
            Glyph(g, text.Substring(i, 1), fam, FontStyle.Bold, em, x + w[i] / 2, cy, 1f, brush);
            x += w[i] + gap;
        }
    }

    // One fixed cell per digit and one fixed stretch, so 8 and 29 are plainly the same face.
    void DrawDate(Graphics g, float cx, float cy, float dd, bool highlight)
    {
        if (!WithDate) return;
        float dx = DateR * dd, aw = ApW * dd, ah = ApH * dd;
        GraphicsState st = g.Save();
        g.TranslateTransform(cx, cy);
        using (GraphicsPath clip = RoundRect(new RectangleF(dx - aw / 2, -ah / 2, aw, ah), dd * 0.004f))
        {
            g.SetClip(clip);
            string s = DialDate().ToString(CultureInfo.InvariantCulture);
            float cell = dd * 0.0445f, em = NumH * dd / 0.72f;
            float x0 = dx - cell * s.Length / 2;
            using (SolidBrush b = new SolidBrush(Rgb(0x121414)))
                for (int i = 0; i < s.Length; i++)
                    Glyph(g, s.Substring(i, 1), Numerals(), FontStyle.Regular, em,
                          x0 + cell * (i + 0.5f), dd * 0.0016f, 1.10f, b);
            g.ResetClip();
        }
        if (highlight)
        {
            using (Pen pen = new Pen(Color.FromArgb(230, 42, 65, 168), Math.Max(1f, dd * 0.0042f)))
            using (GraphicsPath p = RoundRect(new RectangleF(dx - aw / 2 - dd * 0.014f, -ah / 2 - dd * 0.014f,
                                                             aw + dd * 0.028f, ah + dd * 0.028f), dd * 0.012f))
                g.DrawPath(pen, p);
        }
        g.Restore(st);
    }

    // ===================== the crown =====================
    Rectangle DrawCrown(Graphics g, float cx, float cy, float outerR, int d, int pos)
    {
        float push = pos * d * 0.019f;
        float x0 = cx + outerR + d * 0.004f + push, bw = d * 0.040f, bh = d * 0.052f;
        float sh = d * 0.026f, sx = cx + outerR - d * 0.008f;

        using (LinearGradientBrush b = new LinearGradientBrush(
                   new PointF(0, cy - sh / 2), new PointF(0, cy + sh / 2), Rgb(0xEFF2F4), Rgb(0x7A818A)))
        {
            ColorBlend cb = new ColorBlend();
            cb.Positions = new[] { 0f, .42f, 1f };
            cb.Colors = new[] { Rgb(0xEFF2F4), Rgb(0xAEB5BC), Rgb(0x7A818A) };
            b.InterpolationColors = cb;
            g.FillRectangle(b, sx, cy - sh / 2, x0 - sx + d * 0.004f, sh);
        }

        RectangleF body = new RectangleF(x0, cy - bh, bw, bh * 2);
        using (GraphicsPath p = RoundRect(body, d * 0.007f))
        {
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(0, cy - bh), new PointF(0, cy + bh), Rgb(0xCBD1D7), Rgb(0x737A82)))
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = new[] { 0f, .16f, .52f, .84f, 1f };
                cb.Colors = new[] { Rgb(0xCBD1D7), Rgb(0xFAFBFC), Rgb(0x9BA3AB), Rgb(0xE4E8EC), Rgb(0x737A82) };
                b.InterpolationColors = cb;
                g.FillPath(b, p);
            }
            GraphicsState st = g.Save();
            g.SetClip(p);
            int n = Math.Max(7, (int)Math.Round(bh * 2 / Math.Max(2f, d * 0.0115f)));
            float step = bh * 2 / n;
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(77, 88, 96, 106)))
            using (SolidBrush light = new SolidBrush(Color.FromArgb(107, 255, 255, 255)))
                for (int i = 0; i < n; i++)
                {
                    float yy = cy - bh + i * step;
                    g.FillRectangle(dark, x0, yy, bw, step * 0.46f);
                    g.FillRectangle(light, x0, yy + step * 0.46f, bw, step * 0.26f);
                }
            g.Restore(st);
            using (Pen pen = new Pen(Color.FromArgb(128, 84, 92, 102), Math.Max(0.5f, d * 0.0016f)))
                g.DrawPath(pen, p);
        }
        return Rectangle.Round(new RectangleF(x0 - d * 0.012f, cy - bh - d * 0.012f,
                                              bw + d * 0.024f, bh * 2 + d * 0.024f));
    }

    // ===================== hands =====================
    static Color FacetColor(double ang, int side)
    {
        float t = 0.45f * side, nz = 0.8930f;
        float d = (float)(0.52 * (Math.Cos(ang) * t) - 0.72 * (Math.Sin(ang) * t) + 0.46 * nz);
        if (d < 0) d = 0;
        float e = 70 + 270 * d, sp = d - 0.56f;
        if (sp > 0) e += 900 * sp * sp;
        if (e < 0) e = 0; if (e > 253) e = 253;
        int r = (int)e, gg = (int)Math.Min(253, e * 1.003f), bb = (int)Math.Min(253, e * 1.010f);
        return Color.FromArgb(255, r, gg, bb);
    }

    static void Blade(Graphics g, float dd, double ang, float len, float wid, float tail)
    {
        GraphicsState st = g.Save();
        g.RotateTransform((float)(ang * 180.0 / Math.PI));
        float tip = -len * dd, w = wid * dd * 0.5f, tl = tail * dd;
        float b = len * dd * 0.035f, mx = w * 0.81f, my = -len * dd * 0.29f;

        PointF[] outline =
        {
            new PointF(0, tip), new PointF(mx, my), new PointF(w, -b),
            new PointF(0, tl), new PointF(-w, -b), new PointF(-mx, my)
        };
        PointF[] left = { new PointF(0, tip), new PointF(-mx, my), new PointF(-w, -b), new PointF(0, tl) };

        using (SolidBrush right = new SolidBrush(FacetColor(ang, 1)))
            g.FillPolygon(right, outline);
        using (SolidBrush lb = new SolidBrush(FacetColor(ang, -1)))
            g.FillPolygon(lb, left);
        using (Pen pen = new Pen(Color.FromArgb(87, 78, 86, 96), Math.Max(0.5f, dd * 0.0020f)))
        {
            pen.LineJoin = LineJoin.Miter;
            g.DrawPolygon(pen, outline);
        }
        using (Pen pen = new Pen(Color.FromArgb(87, 64, 71, 80), Math.Max(0.4f, dd * 0.0013f)))
            g.DrawLine(pen, 0, tip, 0, tl);
        g.Restore(st);
    }

    void DrawHands(Graphics g, float cx, float cy, float dd, DateTime t, float reserve)
    {
        GraphicsState st = g.Save();
        g.TranslateTransform(cx, cy);
        double rad = Math.PI / 180.0;

        float sec = t.Second + (hacked ? 0 : t.Millisecond / 1000f);
        float min = t.Minute + sec / 60f;
        float hr = (t.Hour % 12) + min / 60f;

        if (WithReserve)
        {
            double pa = PrAng * rad;
            float px0 = (float)(Math.Sin(pa) * PrR * dd), py0 = (float)(-Math.Cos(pa) * PrR * dd);
            double ang = (PrA0 + (PrA1 - PrA0) * (reserve / 100.0)) * rad;
            float tx = px0 + (float)(Math.Cos(ang) * PrRad * dd * 0.86);
            float ty = py0 + (float)(Math.Sin(ang) * PrRad * dd * 0.86);
            using (Pen sp = new Pen(Color.FromArgb(82, 76, 84, 94), dd * 0.0085f))
            { sp.StartCap = LineCap.Round; sp.EndCap = LineCap.Round; g.DrawLine(sp, px0 + dd * 0.003f, py0 + dd * 0.004f, tx + dd * 0.003f, ty + dd * 0.004f); }
            using (Pen edge = new Pen(Color.FromArgb(204, 68, 76, 86), dd * 0.0096f))
            { edge.StartCap = LineCap.Round; edge.EndCap = LineCap.Round; g.DrawLine(edge, px0, py0, tx, ty); }
            using (LinearGradientBrush b = new LinearGradientBrush(new PointF(px0, py0), new PointF(tx, ty), Rgb(0xE6E9EC), Rgb(0xB6BDC4)))
            using (Pen pen = new Pen(b, dd * 0.0070f))
            { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawLine(pen, px0, py0, tx, ty); }
            float br = dd * 0.0160f;
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(px0 - br, py0 - br), new PointF(px0 + br, py0 + br), Rgb(0xFDFEFE), Rgb(0xE2E6E9)))
                g.FillEllipse(b, px0 - br, py0 - br, br * 2, br * 2);
            using (SolidBrush b = new SolidBrush(Rgb(0x80888F)))
                g.FillEllipse(b, px0 - dd * 0.010f, py0 - dd * 0.010f, dd * 0.020f, dd * 0.020f);
        }

        // soft shadow under the two blades, cast down and a touch left
        GraphicsState sh = g.Save();
        g.TranslateTransform(-dd * 0.0022f, dd * 0.0075f);
        using (SolidBrush shadow = new SolidBrush(Color.FromArgb(70, 68, 78, 90)))
        {
            ShadowBlade(g, shadow, dd, hr / 12.0 * Math.PI * 2, HourL, HourW, TailL);
            ShadowBlade(g, shadow, dd, min / 60.0 * Math.PI * 2, MinL, MinW, TailL);
        }
        g.Restore(sh);

        Blade(g, dd, hr / 12.0 * Math.PI * 2, HourL, HourW, TailL);
        Blade(g, dd, min / 60.0 * Math.PI * 2, MinL, MinW, TailL);

        // the hub cap of the hour and minute hands, seen through the open collet
        float cr = dd * 0.0096f;
        using (LinearGradientBrush b = new LinearGradientBrush(
                   new PointF(-dd * 0.012f, -dd * 0.012f), new PointF(dd * 0.012f, dd * 0.012f),
                   Color.White, Rgb(0x868D95)))
        {
            ColorBlend cb = new ColorBlend();
            cb.Positions = new[] { 0f, .52f, 1f };
            cb.Colors = new[] { Color.White, Rgb(0xC6CCD2), Rgb(0x868D95) };
            b.InterpolationColors = cb;
            g.FillEllipse(b, -cr, -cr, cr * 2, cr * 2);
        }
        using (Pen pen = new Pen(Color.FromArgb(115, 70, 78, 88), Math.Max(0.4f, dd * 0.0011f)))
            g.DrawEllipse(pen, -cr, -cr, cr * 2, cr * 2);

        SecondsHand(g, dd, sec / 60.0 * Math.PI * 2);
        g.Restore(st);
    }

    static void ShadowBlade(Graphics g, Brush brush, float dd, double ang, float len, float wid, float tail)
    {
        GraphicsState st = g.Save();
        g.RotateTransform((float)(ang * 180.0 / Math.PI));
        float tip = -len * dd, w = wid * dd * 0.5f, tl = tail * dd;
        float b = len * dd * 0.035f, mx = w * 0.81f, my = -len * dd * 0.29f;
        g.FillPolygon(brush, new[]
        {
            new PointF(0, tip), new PointF(mx, my), new PointF(w, -b),
            new PointF(0, tl), new PointF(-w, -b), new PointF(-mx, my)
        });
        g.Restore(st);
    }

    // A slender pointer that stops at the collet, an open blued ring, and a tail that flares and
    // then stops BLUNT - it never comes to a point.
    static void SecondsHand(Graphics g, float dd, double ang)
    {
        GraphicsState st = g.Save();
        g.RotateTransform((float)(ang * 180.0 / Math.PI));
        float len = SecL * dd, tail = SecT * dd;
        float w0 = SecW0 * dd * 0.5f, w1 = SecW1 * dd * 0.5f;
        float t0 = SecT0 * dd * 0.5f, t1 = SecT1 * dd * 0.5f;
        float ro = SecRo * dd, ri = SecRi * dd, rg = ro * 0.94f;

        using (LinearGradientBrush b = new LinearGradientBrush(
                   new PointF(-t1, 0), new PointF(t1, 0), Rgb(0x2A46B8), Rgb(0x0D1740)))
        {
            ColorBlend cb = new ColorBlend();
            cb.Positions = new[] { 0f, .23f, .30f, .37f, .58f, .82f, 1f };
            cb.Colors = new[]
            {
                Rgb(0x2A46B8), Rgb(0x5178DF), Rgb(0xB3CDFF), Rgb(0x3652BE),
                Rgb(0x1D2E80), Rgb(0x14215C), Rgb(0x0D1740)
            };
            b.InterpolationColors = cb;

            g.FillPolygon(b, new[]
            {
                new PointF(-w1, -len), new PointF(w1, -len), new PointF(w0, -rg), new PointF(-w0, -rg)
            });

            float re = t1 * 0.40f;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddLine(-t0, rg, t0, rg);
                p.AddLine(t0, rg, t1, tail - re);
                p.AddBezier(t1, tail - re, t1, tail, t1 - re, tail, t1 - re, tail);
                p.AddLine(t1 - re, tail, -(t1 - re), tail);
                p.AddBezier(-(t1 - re), tail, -t1, tail, -t1, tail - re, -t1, tail - re);
                p.CloseFigure();
                g.FillPath(b, p);
            }

            using (Pen ring = new Pen(b, ro - ri))
            {
                float rm = (ro + ri) / 2;
                g.DrawEllipse(ring, -rm, -rm, rm * 2, rm * 2);
            }
        }
        g.Restore(st);
    }

    // ===================== glass =====================
    static Bitmap BuildGlass(int d, float cx, float cy, float rd)
    {
        Bitmap b = new Bitmap(d, d, PixelFormat.Format32bppArgb);
        b.SetResolution(96, 96);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            DrawGlass(g, cx, cy, rd);
        }
        return b;
    }

    static void DrawGlass(Graphics g, float cx, float cy, float rd)
    {
        GraphicsState st = g.Save();
        using (GraphicsPath clip = new GraphicsPath())
        {
            clip.AddEllipse(cx - rd, cy - rd, rd * 2, rd * 2);
            g.SetClip(clip);
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new PointF(cx - rd, cy - rd), new PointF(cx + rd * 0.1f, cy + rd * 0.5f),
                       Color.FromArgb(61, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = new[] { 0f, .30f, .56f, 1f };
                cb.Colors = new[]
                {
                    Color.FromArgb(61, 255, 255, 255), Color.FromArgb(13, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)
                };
                b.InterpolationColors = cb;
                g.FillEllipse(b, cx - rd, cy - rd, rd * 2, rd * 2);
            }
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(cx - rd, cy - rd, rd * 2, rd * 2);
                using (PathGradientBrush pg = new PathGradientBrush(p))
                {
                    pg.CenterPoint = new PointF(cx, cy);
                    pg.CenterColor = Color.FromArgb(0, 38, 46, 56);
                    pg.SurroundColors = new[] { Color.FromArgb(26, 38, 46, 56) };
                    Blend bl = new Blend(3);
                    bl.Positions = new[] { 0f, 0.74f, 1f };
                    bl.Factors = new[] { 0f, 0f, 1f };
                    pg.Blend = bl;
                    g.FillEllipse(pg, cx - rd, cy - rd, rd * 2, rd * 2);
                }
            }
            g.ResetClip();
        }
        g.Restore(st);
    }

    // ===================== power reserve =====================
    float Reserve()
    {
        string src = ReserveSource;
        if (src == "auto") src = HasBattery() ? "battery" : "uptime";
        if (src == "battery")
        {
            PowerStatus ps = SystemInformation.PowerStatus;
            if (!HasBattery()) return 100;
            float p = ps.BatteryLifePercent;
            if (p < 0 || p > 1) return 100;
            return p * 100f;
        }
        if (src == "uptime")
        {
            double h;
            try { h = GetTickCount64() / 3600000.0; }
            catch (Exception) { h = Environment.TickCount / 3600000.0; }
            double left = 72 - h;
            if (left < 0) left = 0;
            return (float)(left / 72 * 100);
        }
        if (src == "workday")
        {
            DateTime now = DateTime.Now;
            double h = now.Hour + now.Minute / 60.0;
            if (h < 9) return 100;
            if (h > 18) return 0;
            return (float)((18 - h) / 9 * 100);
        }
        if (src == "disk")
        {
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                if (di.TotalSize > 0) return (float)(di.AvailableFreeSpace / (double)di.TotalSize * 100);
            }
            catch (Exception) { }
            return 100;
        }
        if (src == "event")
        {
            DateTime now = DateTime.Now;
            double best = -1;
            foreach (CalEvent e in PluginApi.CalendarEvents)
            {
                if (e.Start <= now) continue;
                double h = (e.Start - now).TotalHours;
                if (best < 0 || h < best) best = h;
            }
            if (best < 0) return 100;
            return (float)Math.Min(100, best / 72 * 100);
        }
        return 100;
    }

    static bool HasBattery()
    {
        try
        {
            PowerStatus ps = SystemInformation.PowerStatus;
            return (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
        }
        catch (Exception) { return false; }
    }

    // ===================== draw =====================
    protected override void Draw(Graphics g)
    {
        Size sz = WidgetSize;
        int d = sz.Height;
        if (d < 40) return;

        string key = Key(d);
        if (dial == null || dialFor != d || dialKey != key)
        {
            if (dial != null) dial.Dispose();
            if (glass != null) { glass.Dispose(); glass = null; }
            dial = BuildDial(d);
            dialFor = d;
            dialKey = key;
        }

        // Render() clears the surface only during the appear animation; every other frame hands
        // us last frame's pixels. Without this the alpha-1 hit-test fill below blends onto itself
        // each frame and the transparent corners darken into a solid box within seconds.
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        float bez = WithCase ? 0.064f : 0.012f;
        float rd = (0.5f - bez) * d, dd = 2 * rd, cx = d / 2f, cy = d / 2f;

        // The crown sits against the OUTER edge - the case band when there is one, otherwise the
        // dial itself - or the case paints straight over it.
        if (WithCrown) crownRect = DrawCrown(g, cx, cy, WithCase ? d * 0.5f - 1 : rd, d, crownPos);
        else crownRect = Rectangle.Empty;

        g.DrawImageUnscaled(dial, 0, 0);
        DrawDate(g, cx, cy, dd, crownPos == 1);
        DrawHands(g, cx, cy, dd, DialNow(), Reserve());
        // The glass never changes, so it is painted once and blitted: a PathGradientBrush
        // across the whole dial every frame was most of the per-frame cost.
        if (glass == null) glass = BuildGlass(d, cx, cy, rd);
        g.DrawImageUnscaled(glass, 0, 0);

        // Alpha = 1 so the transparent parts still receive mouse events (layered window hit-test).
        using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            g.FillRectangle(hit, 0, 0, sz.Width, sz.Height);
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float dia = Math.Max(0.01f, radius * 2);
        if (radius <= 0.01f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, dia, dia, 180, 90);
        p.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
        p.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
        p.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ===================== menu =====================
    protected override void BuildMenu(PopupMenu menu)
    {
        PopupMenu size = menu.AddSubmenu("Size");
        int cur = Settings.GetInt("size", 220);
        int[] sizes = { 170, 220, 300, 380 };
        string[] names = { "Small", "Medium", "Large", "Extra Large" };
        for (int i = 0; i < sizes.Length; i++)
        {
            int px = sizes[i];
            size.AddChoice(names[i], cur == px && Settings.GetInt("custom_w", 0) == 0, delegate
            {
                Settings.Set("size", px);
                Settings.Remove("custom_w");
                Settings.Remove("custom_h");
                Changed();
            });
        }

        PopupMenu frame = menu.AddSubmenu("Frame");
        frame.AddChoice("Dial", !WithCase, delegate { Settings.Set("frame", "dial"); Changed(); });
        frame.AddChoice("Case", WithCase, delegate { Settings.Set("frame", "case"); Changed(); });

        PopupMenu pr = menu.AddSubmenu("Power Reserve");
        string s = ReserveSource;
        pr.AddChoice("Automatic", s == "auto", delegate { Settings.Set("pr", "auto"); Render(); });
        pr.AddSeparator();
        pr.AddChoice("Battery", s == "battery", delegate { Settings.Set("pr", "battery"); Render(); });
        pr.AddChoice("Uptime", s == "uptime", delegate { Settings.Set("pr", "uptime"); Render(); });
        pr.AddChoice("Working Day", s == "workday", delegate { Settings.Set("pr", "workday"); Render(); });
        pr.AddChoice("Disk Free", s == "disk", delegate { Settings.Set("pr", "disk"); Render(); });
        pr.AddChoice("Next Event", s == "event", delegate { Settings.Set("pr", "event"); Render(); });
        pr.AddChoice("Always Full", s == "full", delegate { Settings.Set("pr", "full"); Render(); });

        menu.AddCheck("Crown", WithCrown, delegate
        {
            Settings.Set("crown", !WithCrown);
            crownPos = 0; hacked = false;
            Settings.Remove("custom_w"); Settings.Remove("custom_h");
            Changed();
        });
        menu.AddCheck("Logo", WithLogo, delegate { Settings.Set("logo", !WithLogo); Changed(); });
        menu.AddCheck("Date", WithDate, delegate { Settings.Set("date", !WithDate); Changed(); });
        menu.AddCheck("Power Reserve Dial", WithReserve, delegate { Settings.Set("reserve", !WithReserve); Changed(); });

        if (OffsetMin != 0 || DateOff != 0 || crownPos != 0)
        {
            menu.AddSeparator();
            menu.Add("Back to Local Time", delegate
            {
                OffsetMin = 0; DateOff = 0; crownPos = 0; hacked = false;
                Render();
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (dial != null) { dial.Dispose(); dial = null; }
            if (glass != null) { glass.Dispose(); glass = null; }
        }
        base.Dispose(disposing);
    }
}
