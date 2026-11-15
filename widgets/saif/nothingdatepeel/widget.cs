using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// A tear-off calendar page: the weekday in red, the day of the month in a big serif.
// The page stays on the last date you peeled to. When the date changes, the bottom-right
// corner lifts; drag it (or click it) to peel the page away and show today. Until the next
// day there is no corner to pull.
[DeskWidget(Name = "Nothing Date Peel", Description = "A tear-off date: peel the corner when the day changes")]
public sealed class NothingDatePeel : CardWidget
{
    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color SignalRed = Rgb(0xD71921);
    // The back of the page: lighter at the lifted corner, darker at the fold.
    static readonly Color DarkFlapA = Rgb(0x66666B), DarkFlapB = Rgb(0x38383B);
    static readonly Color LightFlapA = Rgb(0xF2F2F5), LightFlapB = Rgb(0xD4D4DA);

    // A high-contrast serif with lining figures: the first of these that is installed.
    static readonly string Serif = PickFont("NType 82 Headline", "Bodoni Moda", "Bodoni MT", "Didot", "Times New Roman");

    Font dayFont;
    float fontsFor = -1f;

    // Peel distance in pixels: how far the corner has travelled along the diagonal.
    // 0 is flat, Rest is the lifted corner, 2 * side is the page fully off.
    float peel;
    bool dragging, dragMoved;
    Point dragStart;
    float dragOffset;

    readonly Timer anim = new Timer { Interval = 15 };
    float animFrom, animTo;
    int animStart;
    Action animDone;
    const int AnimMs = 380;

    public NothingDatePeel(Section settings) : base(settings, false)
    {
        anim.Tick += delegate { AnimStep(); };
    }

    protected override bool SquareResize { get { return true; } }
    // While a page is waiting to be peeled, the corners peel instead of resizing.
    protected override bool CanResize { get { return !Pending && !anim.Enabled; } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    // ---------- which page is showing ----------
    DateTime ShownDay
    {
        get
        {
            DateTime d;
            if (DateTime.TryParseExact(Settings.Get("shown", ""), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                && d <= DateTime.Today) return d;
            return DateTime.Today;
        }
    }

    bool Pending { get { return ShownDay < DateTime.Today; } }
    float Side { get { return Math.Min(W, H); } }
    float Rest { get { return Math.Max(Side * 0.16f, P(22) + 2f); } }

    void Commit()
    {
        Settings.Set("shown", DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        peel = 0;
        Render();
    }

    // ---------- interaction ----------
    bool InPeelZone(Point p)
    {
        if (!Pending || anim.Enabled) return false;
        float zone = Math.Max(Rest * 1.7f, P(40));
        return p.X >= W - zone && p.Y >= H - zone;
    }

    protected override bool IsButton(Point p) { return InPeelZone(p); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && InPeelZone(e.Location))
        {
            dragging = true;
            dragMoved = false;
            dragStart = e.Location;
            peel = Rest;
            dragOffset = Rest - Along(e.Location);
            Capture = true;
            return; // not a press the base should turn into a move
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) { base.OnMouseMove(e); return; }
        Size slop = SystemInformation.DragSize;
        if (Math.Abs(e.X - dragStart.X) >= slop.Width || Math.Abs(e.Y - dragStart.Y) >= slop.Height) dragMoved = true;
        if (!dragMoved) return;
        peel = Math.Max(Rest * 0.6f, Math.Min(Side * 2, Along(e.Location) + dragOffset));
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
        dragging = false;
        Capture = false;
        // A click, or a pull past halfway, takes the page off; a short pull lets it settle back.
        if (!dragMoved || peel > Side * 0.5f) Animate(peel, Side * 2, Commit);
        else Animate(peel, Rest, null);
    }

    // How far a point is from the bottom-right corner, measured along the diagonal.
    float Along(Point p) { return ((W - p.X) + (H - p.Y)) / 2f; }

    void Animate(float from, float to, Action done)
    {
        animFrom = from; animTo = to; animDone = done;
        animStart = Environment.TickCount;
        anim.Start();
    }

    void AnimStep()
    {
        float p = Math.Min(1f, unchecked(Environment.TickCount - animStart) / (float)AnimMs);
        float eased = 1f - (1f - p) * (1f - p) * (1f - p);
        peel = animFrom + (animTo - animFrom) * eased;
        if (p >= 1f)
        {
            anim.Stop();
            Action done = animDone;
            animDone = null;
            if (done != null) { done(); return; }
        }
        Render();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (Pending) menu.Add("Peel to Today", delegate { if (!anim.Enabled) Animate(Rest, Side * 2, Commit); });
        else menu.Add("Put Yesterday's Page Back", delegate
        {
            Settings.Set("shown", DateTime.Today.AddDays(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Render();
        });
    }

    // Redraw just after each minute boundary, so the corner lifts soon after midnight.
    protected override int NextFrameDelay()
    {
        if (!IsHandleCreated) return -1;
        DateTime now = DateTime.Now;
        return (60 - now.Second) * 1000 - now.Millisecond + 50;
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float s = Side;
        EnsureFonts(s);
        Color ink = IsVibrant ? pal.Fg : IsDark ? DarkInk : LightInk;
        DateTime today = DateTime.Today;

        // The library preview shows the lifted corner, since that is the point of the widget.
        bool preview = !IsHandleCreated;
        DateTime shown = preview ? today : ShownDay;
        bool pending = preview || shown < today;
        float t = !pending ? 0 : (dragging || anim.Enabled) ? peel : Rest;
        if (t < 1f) { DrawPage(g, shown, ink, s); return; }
        t = Math.Min(t, s * 2);
        float k = 2 * s - t; // where the fold meets the top and left edges once it has passed the corners

        // Today's page, seen only where the old page has been folded away.
        PointF a = new PointF(s - t, s), b = new PointF(s, s - t);
        PointF[] revealed = t <= s
            ? new[] { a, b, new PointF(s, s) }
            : new[] { new PointF(k, 0), new PointF(s, 0), new PointF(s, s), new PointF(0, s), new PointF(0, k) };
        PointF[] kept = t <= s
            ? new[] { new PointF(0, 0), new PointF(s, 0), b, a, new PointF(0, s) }
            : new[] { new PointF(0, 0), new PointF(k, 0), new PointF(0, k) };
        DrawClipped(g, revealed, preview ? today.AddDays(1) : today, ink, s);
        DrawClipped(g, kept, shown, ink, s);

        // The folded-back part of the page: the corner mirrored across the fold.
        bool nearCorners = t > s - P(22);
        GraphicsState state = g.Save();
        if (nearCorners) using (var card = CardPath()) g.SetClip(card);

        if (t <= s)
        {
            float d = P(1.3f);
            using (var shade = new SolidBrush(Color.FromArgb(IsDark ? 34 : 16, 0, 0, 0)))
                for (int i = 1; i <= 4; i++)
                    g.FillPolygon(shade, new[] { new PointF(a.X - i * d, a.Y - i * d), new PointF(b.X - i * d, b.Y - i * d), new PointF(s - t - i * d, s - t - i * d) });
        }

        Color flapA = IsVibrant ? Color.FromArgb(235, 255, 255, 255) : IsDark ? DarkFlapA : LightFlapA;
        Color flapB = IsVibrant ? Color.FromArgb(190, 225, 228, 235) : IsDark ? DarkFlapB : LightFlapB;
        using (var flap = new GraphicsPath())
        using (var paint = new LinearGradientBrush(new PointF(s - t, s - t), new PointF(s - t / 2, s - t / 2), flapA, flapB))
        {
            paint.WrapMode = WrapMode.TileFlipXY;
            if (t <= s)
            {
                // A triangle whose free corner keeps the card's rounding.
                float rc = Math.Min(P(22), t * 0.4f);
                flap.AddLine(a.X, a.Y, s - t, s - t + rc);
                flap.AddArc(s - t, s - t, rc * 2, rc * 2, 180, 90);
                flap.AddLine(s - t + rc, s - t, b.X, b.Y);
            }
            else
            {
                flap.AddPolygon(new[] { new PointF(s - t, s - t), new PointF(s - t, k), new PointF(0, k), new PointF(k, 0), new PointF(k, s - t) });
            }
            flap.CloseFigure();
            g.FillPath(paint, flap);
        }
        g.Restore(state);
    }

    void DrawClipped(Graphics g, PointF[] area, DateTime date, Color ink, float s)
    {
        GraphicsState state = g.Save();
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(area);
            g.SetClip(path, CombineMode.Intersect);
        }
        DrawPage(g, date, ink, s);
        g.Restore(state);
    }

    void DrawPage(Graphics g, DateTime date, Color ink, float s)
    {
        string name = date.ToString("ddd", CultureInfo.CurrentCulture);
        string day = date.Day.ToString(CultureInfo.CurrentCulture);
        using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            fmt.Alignment = StringAlignment.Far;
            using (var red = new SolidBrush(IsVibrant ? Color.White : SignalRed))
                g.DrawString(name, dayFont, red, new RectangleF(0, s * 0.075f, s - s * 0.11f, dayFont.GetHeight(g)), fmt);

        }

        // The day number, set exactly like the digital clock's time: the same typeface in its
        // regular cut, at the same height. Only a number too wide for the card is made smaller.
        var box = new RectangleF(s * 0.1f, s * 0.27f, s * 0.8f, s * 0.55f);
        using (var family = new FontFamily(Serif))
        using (var path = new GraphicsPath())
        using (var b = new SolidBrush(ink))
        {
            path.AddString(day, family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF bounds = path.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            float scale = Math.Min(DigitScale(family, s), box.Width / bounds.Width);
            using (var fit = new Matrix())
            {
                fit.Translate(box.X + (box.Width - bounds.Width * scale) / 2 - bounds.X * scale,
                              box.Y + (box.Height - bounds.Height * scale) / 2 - bounds.Y * scale);
                fit.Scale(scale, scale);
                path.Transform(fit);
            }
            PixelOffsetMode was = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.FillPath(b, path);
            g.PixelOffsetMode = was;
        }
    }

    // Digits are drawn half the card's height tall, the same rule as the companion Nothing Digital Clock
    // widget, so the two match exactly side by side at the same Size setting. The height is
    // taken from all ten digits, so it doesn't change with which digits are showing.
    const float DigitHeight = 0.5f;

    static float DigitScale(FontFamily family, float cardHeight)
    {
        using (var all = new GraphicsPath())
        {
            all.AddString("0123456789", family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
            float h = all.GetBounds().Height;
            return h <= 0 ? 1f : cardHeight * DigitHeight / h;
        }
    }

    void EnsureFonts(float s)
    {
        if (dayFont != null && Math.Abs(s - fontsFor) < 0.01f) return;
        DisposeFonts();
        dayFont = new Font(Serif, Math.Max(1f, s * 0.115f), FontStyle.Regular, GraphicsUnit.Pixel);
        fontsFor = s;
    }

    void DisposeFonts()
    {
        if (dayFont != null) { dayFont.Dispose(); dayFont = null; }
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
        if (disposing)
        {
            anim.Dispose();
            DisposeFonts();
        }
        base.Dispose(disposing);
    }
}
