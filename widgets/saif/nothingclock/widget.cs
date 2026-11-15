using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DeskWidgets;
using DeskWidgets.Widgets;

// A round analog clock in two styles, chosen from the right-click menu:
//   Minimal - a fat hour hand, a thin grey minute hand, and a red dot circling the rim for seconds.
//   Ticks   - sixty tick marks, tapered hands, and a red seconds hand with a counterweight.
// Local time. Nothing is fetched or stored beyond the widget's own settings.
[DeskWidget(Name = "Nothing Clock", Description = "Round analog clock in two dot-and-line styles")]
public sealed class NothingClock : CardWidget
{
    sealed class Look { public Color Face, Ink, Soft, Accent = SignalRed; }

    static readonly Color SignalRed = Rgb(0xD71921);
    static readonly Look DarkLook = new Look { Face = Rgb(0x1B1B1D), Ink = Color.White, Soft = Rgb(0x77777B) };
    static readonly Look LightLook = new Look { Face = Color.White, Ink = Rgb(0x1B1B1D), Soft = Rgb(0xB4B4BA) };
    // Vibrant: frosted glass behind, everything in white, as the app's other vibrant widgets.
    static readonly Look VibrantLook = new Look { Ink = Color.White, Soft = Color.FromArgb(150, 255, 255, 255), Accent = Color.White };

    public NothingClock(Section settings) : base(settings, false) { }

    protected override bool SquareResize { get { return true; } }

    string Style { get { return Settings.Get("style", "minimal"); } }      // minimal | ticks
    string SecondsMode { get { return Settings.Get("seconds", "tick"); } } // off | tick | smooth

    bool FrostOwnShape { get { return true; } }

    // Nearly transparent, so the corners outside the circle still take the mouse (move, resize, menu).
    protected override Color CardBackground(Palette pal) { return Color.FromArgb(1, 0, 0, 0); }

    protected override int NextFrameDelay()
    {
        if (!IsHandleCreated) return -1;
        string mode = SecondsMode;
        DateTime now = DateTime.Now;
        if (mode == "off") return (59 - now.Second) * 1000 + (1000 - now.Millisecond) + 10;
        // The sweep only runs while the clock can be seen.
        if (mode == "smooth" && !IsCovered()) return 33;
        return 1000 - now.Millisecond + 10; // wake just after the next whole second
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        var style = menu.AddSubmenu("Style");
        string cur = Style;
        style.AddChoice("Minimal", cur != "ticks", delegate { Settings.Set("style", "minimal"); Changed(); });
        style.AddChoice("Ticks", cur == "ticks", delegate { Settings.Set("style", "ticks"); Changed(); });

        var seconds = menu.AddSubmenu("Seconds");
        string mode = SecondsMode;
        seconds.AddChoice("Ticking", mode == "tick", delegate { Settings.Set("seconds", "tick"); Changed(); });
        seconds.AddChoice("Smooth Sweep", mode == "smooth", delegate { Settings.Set("seconds", "smooth"); Changed(); });
        seconds.AddChoice("Hidden", mode == "off", delegate { Settings.Set("seconds", "off"); Changed(); });
    }

    // ---------- drawing: laid out on a 200-unit dial, scaled to the widget ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        float s = Math.Min(W, H), c = s / 2f, u = s / 200f;
        Look look = IsVibrant ? VibrantLook : Face == "light" ? LightLook : DarkLook;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (IsVibrant) FillGlassCircle(g, c, c, c - 1.5f);
        else FillCircle(g, look.Face, c, c, c - 0.5f);

        DateTime t = DateTime.Now;
        string mode = SecondsMode;
        double sec = t.Second + (mode == "smooth" ? t.Millisecond / 1000.0 : 0);
        double min = t.Minute + sec / 60.0;
        double hour = t.Hour % 12 + min / 60.0;

        if (Style == "ticks") DrawTicks(g, look, c, u, hour * 30, min * 6, sec * 6, mode != "off");
        else DrawMinimal(g, look, c, u, hour * 30, min * 6, sec * 6, mode != "off");
    }

    static void DrawMinimal(Graphics g, Look look, float c, float u, double hourDeg, double minDeg, double secDeg, bool seconds)
    {
        using (var minute = new Pen(look.Soft, 9 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var hour = new Pen(look.Ink, 17 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            PointF m = Polar(c, minDeg, 66 * u), h = Polar(c, hourDeg, 44 * u);
            g.DrawLine(minute, c, c, m.X, m.Y);
            g.DrawLine(hour, c, c, h.X, h.Y);
        }
        if (seconds)
        {
            PointF d = Polar(c, secDeg, 86 * u);
            FillCircle(g, look.Accent, d.X, d.Y, 5 * u);
        }
    }

    static void DrawTicks(Graphics g, Look look, float c, float u, double hourDeg, double minDeg, double secDeg, bool seconds)
    {
        using (var minor = new Pen(look.Ink, Math.Max(1f, 1.0f * u)))
        using (var major = new Pen(look.Ink, 2.6f * u))
        {
            for (int i = 0; i < 60; i++)
            {
                bool big = i % 5 == 0;
                PointF a = Polar(c, i * 6, (big ? 77 : 85) * u), b = Polar(c, i * 6, 91 * u);
                g.DrawLine(big ? major : minor, a, b);
            }
        }

        using (var ink = new SolidBrush(look.Ink))
        {
            Tapered(g, ink, c, hourDeg, 9 * u, 52 * u, 3.2f * u, 1.6f * u);
            Tapered(g, ink, c, minDeg, 11 * u, 80 * u, 2.2f * u, 1.0f * u);
        }

        if (seconds)
        {
            using (var pen = new Pen(look.Accent, 1.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                PointF tail = Polar(c, secDeg + 180, 32 * u), tip = Polar(c, secDeg, 74 * u);
                g.DrawLine(pen, tail, tip);
                PointF weight = Polar(c, secDeg + 180, 34 * u);
                FillCircle(g, look.Accent, weight.X, weight.Y, 4.5f * u);
            }
        }
        FillCircle(g, look.Ink, c, c, 5.5f * u);
        if (look.Accent != look.Ink) FillCircle(g, look.Accent, c, c, 2.4f * u);
    }

    // A hand that narrows from its tail (behind the centre) to its tip.
    static void Tapered(Graphics g, Brush b, float c, double degrees, float tail, float length, float halfTail, float halfTip)
    {
        GraphicsState state = g.Save();
        g.TranslateTransform(c, c);
        g.RotateTransform((float)degrees);
        g.FillPolygon(b, new[]
        {
            new PointF(-halfTail, tail), new PointF(halfTail, tail), new PointF(halfTip, -length), new PointF(-halfTip, -length)
        });
        g.Restore(state);
    }

    // The point at a clock angle (0 is twelve o'clock, clockwise) and distance from the centre.
    static PointF Polar(float c, double degrees, float radius)
    {
        double a = degrees * Math.PI / 180;
        return new PointF(c + (float)Math.Sin(a) * radius, c - (float)Math.Cos(a) * radius);
    }

    // ---------- vibrant: frosted glass in the widget's own shape ----------
    // The app paints its frosted wallpaper as a rounded card. For a round shape, that card is
    // rendered off-screen (once per position) and then used as the paint for the circle, so the
    // glass follows the shape and its edge stays smooth.
    Bitmap frost;
    Rectangle frostFor;
    bool capturing;

    protected override void Draw(Graphics g)
    {
        if (!IsVibrant || !FrostOwnShape) { base.Draw(g); return; }
        var size = new Size((int)Math.Round(W), (int)Math.Round(H));
        Rectangle where = IsHandleCreated ? new Rectangle(Location, size) : Rectangle.Empty;
        if (frost == null || frost.Size != size || where != frostFor)
        {
            if (frost != null) frost.Dispose();
            frost = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            frost.SetResolution(96, 96);
            using (var fg = Graphics.FromImage(frost))
            {
                capturing = true;
                try { base.Draw(fg); }
                finally { capturing = false; }
            }
            frostFor = where;
        }
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly transparent, so the whole square still takes the mouse
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        DrawContent(g, Pal);
    }

    // A frosted disc with the hairline edge the app gives its glass cards.
    void FillGlassCircle(Graphics g, float cx, float cy, float radius)
    {
        using (var glass = new TextureBrush(frost))
            g.FillEllipse(glass, cx - radius, cy - radius, radius * 2, radius * 2);
        using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
            g.DrawEllipse(edge, cx - radius, cy - radius, radius * 2, radius * 2);
    }

    public override void BackdropChanged()
    {
        if (frost != null) { frost.Dispose(); frost = null; }
        base.BackdropChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && frost != null) { frost.Dispose(); frost = null; }
        base.Dispose(disposing);
    }
}
