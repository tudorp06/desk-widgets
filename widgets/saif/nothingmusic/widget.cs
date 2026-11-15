using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// Small: what Windows is playing (Spotify, browsers, Media Player...), worked by sliding.
//   The album cover fills the card. Slide it right for the previous track, left for the next:
//   a skip mark shows in the gap, and letting go past it skips. Slide the cover up to uncover
//   the details: a small art tile (click it to play or pause), the track and artist, and a
//   progress bar you can click or drag to seek. Slide down to bring the cover back.
//   A plain click on the cover plays or pauses.
// Because a drag is a slide here, the widget is moved by the grip that appears at the top of
// the card while the mouse is over it. A press-and-hold anywhere, then a drag, moves it too, and
// so does any drag while nothing is playing. Resizing from the corners works as usual.
// Updates come as events from Windows; the widget only redraws once a second while something
// plays and the progress bar is showing. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Music", Description = "Now playing: slide the cover to skip, slide up for details")]
public sealed class NothingMusic : CardWidget
{
    static readonly Color DarkBg = Rgb(0x1B1B1D), DarkInk = Color.White, DarkSoft = Rgb(0x8C8C90), DarkTile = Rgb(0x38383B), DarkTrack = Rgb(0x4A4A4E);
    static readonly Color LightBg = Color.White, LightInk = Rgb(0x1B1B1D), LightSoft = Rgb(0x8E8E93), LightTile = Rgb(0xECECEF), LightTrack = Rgb(0xD4D4DA);

    const int DotGrid = 14; // dots across the art
    const int HoldMs = 350; // a press held this long before moving is a move, not a slide
    const int AnimMs = 240;

    sealed class Cover { public Bitmap Art; public float[] Dots; }

    MediaSession media;            // opened in OnStart, never for library previews
    MediaSession.Snapshot now;
    Bitmap art;                    // the cover, cropped square
    float[] artDots;               // its brightness on a DotGrid x DotGrid grid, 0..1
    Bitmap scaled;                 // art scaled for the tile or the full card, for painting with smooth corners
    string scaledFor;
    string status;
    bool refreshing, refreshAgain;
    float seekFrac = -1;

    Font titleFont, textFont;
    float fontsFor = -1f;

    // Sliding. slide: how far the cover (or the details) has moved sideways, in pixels.
    // lift: how far up the cover is, 0 (covering the card) to 1 (gone, details showing).
    enum Drag { None, Pending, SlideX, SlideY, Seek }
    Drag drag;
    MouseEventArgs downArgs;
    int downTick;
    bool handingToMove, hovering;
    float slide, lift;
    float animFrom, animTo;
    int animStart;
    bool animLift; // which of the two is animating
    readonly Timer anim = new Timer { Interval = 15 };

    public NothingMusic(Section settings) : base(settings, false)
    {
        anim.Tick += delegate { AnimStep(); };
        lift = Open ? 1f : 0f;
    }

    protected override bool SquareResize { get { return true; } }
    bool Dotted { get { return Settings.GetBool("dots", false); } }
    bool Open { get { return Settings.GetBool("open", false); } }
    // By default, sliding right goes back and sliding left goes forward, like turning pages.
    bool RightIsNext { get { return Settings.GetBool("reverse_slide", false); } }

    protected override Color CardBackground(Palette pal)
    {
        if (IsVibrant) return pal.Bg;
        return IsDark ? DarkBg : LightBg;
    }

    // ---------- media ----------
    protected override void OnStart()
    {
        Background(() => MediaSession.Open(), (m, error) =>
        {
            if (error != null) { status = "Media controls unavailable"; Render(); return; }
            media = m;
            media.Changed += OnMediaChanged;
            RefreshMedia();
        });
    }

    // From a Windows worker thread: hop to the UI thread, then read in the background.
    void OnMediaChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)RefreshMedia); }
        catch (InvalidOperationException) { }
    }

    // Read the session in the background. Bursts of events (a track change fires several) are
    // folded into at most one extra read.
    void RefreshMedia()
    {
        if (media == null) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        var m = media;
        Background(() =>
        {
            var snap = m.Read();
            return Tuple.Create(snap, snap != null && snap.Art != null ? Decode(snap.Art) : null);
        }, (result, error) =>
        {
            refreshing = false;
            if (error == null)
            {
                now = result.Item1;
                if (drag != Drag.Seek) seekFrac = -1;
                if (art != null) art.Dispose();
                art = result.Item2 != null ? result.Item2.Art : null;
                artDots = result.Item2 != null ? result.Item2.Dots : null;
                DropScaled();
                status = null;
            }
            Render();
            if (refreshAgain) { refreshAgain = false; RefreshMedia(); }
        });
    }

    // Worker thread: the cover cropped to a square, and its brightness sampled for the dot view.
    static Cover Decode(byte[] bytes)
    {
        try
        {
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                float srcSide = Math.Min(img.Width, img.Height);
                int size = Math.Min((int)srcSide, 512);
                if (size <= 0) return null;
                var src = new RectangleF((img.Width - srcSide) / 2, (img.Height - srcSide) / 2, srcSide, srcSide);
                var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, size, size), src, GraphicsUnit.Pixel);
                }
                return new Cover { Art = bmp, Dots = Brightness(bmp) };
            }
        }
        catch (Exception) { return null; }
    }

    // Average brightness of each cell of a DotGrid x DotGrid grid, stretched so the darkest cell is 0 and the brightest 1.
    static float[] Brightness(Bitmap source)
    {
        var dots = new float[DotGrid * DotGrid];
        using (var small = new Bitmap(DotGrid, DotGrid, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, DotGrid, DotGrid));
            }
            BitmapData data = small.LockBits(new Rectangle(0, 0, DotGrid, DotGrid), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * DotGrid];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            small.UnlockBits(data);
            float min = 1, max = 0;
            for (int y = 0; y < DotGrid; y++)
                for (int x = 0; x < DotGrid; x++)
                {
                    int i = y * data.Stride + x * 4; // blue, green, red, alpha
                    float l = (px[i] * 0.11f + px[i + 1] * 0.59f + px[i + 2] * 0.30f) / 255f;
                    dots[y * DotGrid + x] = l;
                    min = Math.Min(min, l); max = Math.Max(max, l);
                }
            float range = max - min;
            for (int i = 0; i < dots.Length; i++) dots[i] = range < 0.05f ? 0.6f : (dots[i] - min) / range;
        }
        return dots;
    }

    void DropScaled()
    {
        if (scaled != null) { scaled.Dispose(); scaled = null; }
        scaledFor = null;
    }

    // The progress bar only moves while the details are in view.
    protected override int NextFrameDelay()
    {
        if (now == null || !now.Playing || now.Duration <= TimeSpan.Zero || !IsHandleCreated || lift <= 0) return -1;
        return 1000 - DateTime.UtcNow.Millisecond + 20;
    }

    // ---------- layout: everything is a fraction of the card's side ----------
    float Side { get { return Math.Min(W, H); } }
    float Inset { get { return Side * 0.11f; } }
    RectangleF TileRect { get { float a = Side * 0.4f; return new RectangleF(Inset, Inset, a, a); } }
    RectangleF BarRect { get { float h = Math.Max(2f, Side * 0.014f); return new RectangleF(Inset, H - Inset - h, W - Inset * 2, h); } }
    float SlideLimit { get { return Side * 0.34f; } }  // how far the cover can travel sideways
    float SlideCommit { get { return Side * 0.2f; } }  // past this, letting go skips
    bool DetailsShowing { get { return lift >= 1f; } }

    bool OnBar(Point p)
    {
        if (!DetailsShowing || now == null || now.Duration <= TimeSpan.Zero) return false;
        RectangleF b = BarRect;
        float pad = Side * 0.06f;
        return p.X >= b.X && p.X <= b.Right && p.Y >= b.Y - pad && p.Y <= b.Bottom + pad;
    }

    // The app's resize handles live in the corners; presses there are left to it.
    bool InResizeCorner(Point p)
    {
        if (Locked) return false;
        int zone = Px(24);
        bool x = p.X < zone || p.X >= W - zone, y = p.Y < zone || p.Y >= H - zone;
        return x && y;
    }

    // The grip: a strip along the top edge, between the corners, that moves the widget.
    RectangleF GripRect { get { float w = Side * 0.36f; return new RectangleF((W - w) / 2, 0, w, Side * 0.11f); } }
    bool GripShowing { get { return hovering && !Locked && drag == Drag.None && IsHandleCreated; } }

    // ---------- interaction ----------
    protected override bool IsButton(Point p)
    {
        if (handingToMove || now == null || (!Locked && GripRect.Contains(p))) return false;
        return !DetailsShowing || TileRect.Contains(p) || OnBar(p);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; handingToMove = false; Render(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Render(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        handingToMove = false;
        // Corners resize, the grip moves, and with nothing playing there is nothing to slide:
        // those presses are the app's ordinary ones.
        bool plain = InResizeCorner(e.Location) || (!Locked && GripRect.Contains(e.Location)) || (now == null && lift <= 0);
        if (e.Button != MouseButtons.Left || plain) { base.OnMouseDown(e); return; }
        if (anim.Enabled) FinishAnim();
        drag = Drag.Pending;
        downArgs = e;
        downTick = Environment.TickCount;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag == Drag.None)
        {
            if ((MouseButtons & MouseButtons.Left) == 0) handingToMove = false;
            base.OnMouseMove(e);
            if (!Locked && GripRect.Contains(e.Location) && !InResizeCorner(e.Location) && Cursor != Cursors.SizeAll) Cursor = Cursors.SizeAll;
            return;
        }
        if ((MouseButtons & MouseButtons.Left) == 0) { EndDrag(e.Location, false); return; } // the release was missed
        int dx = e.X - downArgs.X, dy = e.Y - downArgs.Y;

        if (drag == Drag.Pending)
        {
            Size slop = SystemInformation.DragSize;
            if (Math.Abs(dx) < slop.Width && Math.Abs(dy) < slop.Height) return;
            bool sideways = Math.Abs(dx) > Math.Abs(dy);
            bool held = unchecked(Environment.TickCount - downTick) >= HoldMs;
            if (OnBar(downArgs.Location)) drag = Drag.Seek;
            else if (held || (sideways && (now == null || media == null)))
            {
                // Pressed and held, or nothing to skip: this is the ordinary drag that moves the widget.
                drag = Drag.None;
                Capture = false;
                handingToMove = true;
                base.OnMouseDown(downArgs);
                base.OnMouseMove(e);
                return;
            }
            else drag = sideways ? Drag.SlideX : Drag.SlideY;
        }

        if (drag == Drag.Seek) SeekToPoint(e.Location);
        else if (drag == Drag.SlideX)
        {
            float limit = SlideLimit;
            // A direction the player can't skip in only gives a little.
            bool toNext = (dx > 0) == RightIsNext;
            if (!(toNext ? now.CanNext : now.CanPrevious)) limit *= 0.25f;
            slide = Math.Max(-limit, Math.Min(limit, dx));
            Render();
        }
        else if (drag == Drag.SlideY)
        {
            // Up lifts the cover off; down from the details pulls it back over.
            float start = Open ? 1f : 0f;
            lift = Math.Max(0f, Math.Min(1f, start - dy / H));
            Render();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (drag == Drag.None || e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
        EndDrag(e.Location, true);
    }

    void EndDrag(Point p, bool released)
    {
        Drag was = drag;
        drag = Drag.None;
        Capture = false;
        if (was == Drag.Pending) { if (released) Tap(p); }
        else if (was == Drag.SlideX)
        {
            if (released && Math.Abs(slide) >= SlideCommit && media != null)
            {
                if ((slide > 0) == RightIsNext) media.Next(); else media.Previous();
            }
            Animate(false, slide, 0);
        }
        else if (was == Drag.SlideY)
        {
            // A quarter of the way is enough to change over.
            SetOpen(Open ? lift > 0.75f : lift > 0.25f);
        }
    }

    // A press that didn't move.
    void Tap(Point p)
    {
        if (media == null || now == null) return;
        if (!DetailsShowing || TileRect.Contains(p)) media.PlayPause();
        else if (OnBar(p)) SeekToPoint(p);
    }

    void SetOpen(bool open)
    {
        Settings.Set("open", open);
        Animate(true, lift, open ? 1f : 0f);
    }

    void SeekToPoint(Point p)
    {
        if (media == null || now == null || now.Duration <= TimeSpan.Zero) return;
        RectangleF b = BarRect;
        seekFrac = Math.Max(0f, Math.Min(1f, (p.X - b.X) / b.Width));
        Render();
        media.Seek(TimeSpan.FromSeconds(seekFrac * now.Duration.TotalSeconds).Ticks);
    }

    void Animate(bool liftAxis, float from, float to)
    {
        animLift = liftAxis; animFrom = from; animTo = to;
        animStart = Environment.TickCount;
        anim.Start();
    }

    void AnimStep()
    {
        float p = Math.Min(1f, unchecked(Environment.TickCount - animStart) / (float)AnimMs);
        float eased = 1f - (1f - p) * (1f - p) * (1f - p);
        float v = animFrom + (animTo - animFrom) * eased;
        if (animLift) lift = v; else slide = v;
        if (p >= 1f) FinishAnim();
        else Render();
    }

    void FinishAnim()
    {
        anim.Stop();
        if (animLift) lift = animTo; else slide = animTo;
        Render();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (media != null && now != null)
        {
            menu.Add(now.Playing ? "Pause" : "Play", media.PlayPause);
            menu.Add("Next Track", media.Next);
            menu.Add("Previous Track", media.Previous);
            menu.AddSeparator();
        }
        bool open = Open;
        menu.Add(open ? "Show Cover" : "Show Details", delegate { SetOpen(!open); });
        bool dotted = Dotted;
        menu.AddCheck("Album Art in Dots", dotted, delegate { Settings.Set("dots", !dotted); Changed(); });
        bool reversed = RightIsNext;
        menu.AddCheck("Reverse Slide Direction", reversed, delegate { Settings.Set("reverse_slide", !reversed); });
    }

    // ---------- drawing ----------
    Color Ink(Palette pal) { return IsVibrant ? pal.Fg : IsDark ? DarkInk : LightInk; }
    Color Soft(Palette pal) { return IsVibrant ? pal.Fg2 : IsDark ? DarkSoft : LightSoft; }
    Color Tile(Palette pal) { return IsVibrant ? pal.Fill : IsDark ? DarkTile : LightTile; }
    Color Track(Palette pal) { return IsVibrant ? pal.Sep : IsDark ? DarkTrack : LightTrack; }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        DrawFaces(g, pal);
        if (!GripShowing) return;
        // The grip: a small dark pill with three dots, readable over any cover.
        RectangleF grip = GripRect;
        float w = Side * 0.17f, h = Side * 0.045f, cx = grip.X + grip.Width / 2, cy = Side * 0.055f;
        FillRound(g, Color.FromArgb(150, 0, 0, 0), new RectangleF(cx - w / 2, cy - h / 2, w, h), h / 2);
        for (int i = -1; i <= 1; i++) FillCircle(g, Color.FromArgb(235, 255, 255, 255), cx + i * w * 0.24f, cy, h * 0.19f);
    }

    void DrawFaces(Graphics g, Palette pal)
    {
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool preview = !IsHandleCreated;
        float up = preview ? 1f : lift; // the library preview shows the details

        // Details showing: they slide sideways themselves.
        if (up >= 1f)
        {
            if (slide == 0) { DrawDetails(g, pal); return; }
            SkipMark(g, pal, slide);
            GraphicsState shifted = g.Save();
            using (var card = CardPath()) g.SetClip(card, CombineMode.Intersect);
            g.TranslateTransform(slide, 0);
            DrawDetails(g, pal);
            g.Restore(shifted);
            return;
        }

        // Cover at rest: it is the card.
        if (up <= 0 && slide == 0)
        {
            using (var card = CardPath()) DrawCover(g, pal, card, 0, 0);
            return;
        }

        // Cover on the move: what's under it, then the cover, kept inside the card.
        if (up > 0) DrawDetails(g, pal);
        else SkipMark(g, pal, slide);
        GraphicsState state = g.Save();
        using (var card = CardPath()) g.SetClip(card, CombineMode.Intersect);
        float x = slide, y = -up * H;
        using (var shape = RoundRect(new RectangleF(x, y, W, H), P(22))) DrawCover(g, pal, shape, x, y);
        g.Restore(state);
    }

    // The album cover filling a card-shaped path whose top-left corner is at (x, y).
    void DrawCover(Graphics g, Palette pal, GraphicsPath shape, float x, float y)
    {
        if (art != null && !Dotted)
        {
            EnsureScaled((int)Math.Ceiling(Math.Max(W, H)), IsVibrant);
            using (var paint = new TextureBrush(scaled))
            {
                paint.TranslateTransform(x, y);
                g.FillPath(paint, shape);
            }
            return;
        }
        using (var b = new SolidBrush(Tile(pal))) g.FillPath(b, shape);
        var area = new RectangleF(x, y, W, H);
        if (art != null && artDots != null) { DrawArtDots(g, Ink(pal), artDots, area); return; }
        // Nothing playing, or a player that gives no cover: a note, and what's going on.
        float side = Side;
        DrawDots(g, Ink(pal), Note, x + W / 2, y + H / 2 - side * 0.06f, side * 0.034f);
        EnsureFonts(side);
        string line = now == null ? (status ?? "Not playing") : now.Title ?? "";
        Label(g, line, textFont, Soft(pal), x + W / 2, y + H / 2 + side * 0.17f, W - Inset * 2, StringAlignment.Center);
    }

    // The skip mark in the gap the slide has opened: faint until it is far enough to skip.
    void SkipMark(Graphics g, Palette pal, float moved)
    {
        bool right = moved > 0, forward = right == RightIsNext;
        float reach = Math.Min(1f, Math.Abs(moved) / SlideCommit), size = Side * 0.05f;
        float cx = right ? Math.Abs(moved) / 2 : W - Math.Abs(moved) / 2, cy = H / 2, d = forward ? 1 : -1;
        using (var b = new SolidBrush(Color.FromArgb((int)(255 * (reach < 1f ? reach * 0.55f : 1f)), Ink(pal))))
            for (int k = 0; k < 2; k++)
            {
                float px = cx + d * (-size + k * size);
                g.FillPolygon(b, new[] { new PointF(px, cy - size), new PointF(px, cy + size), new PointF(px + d * size, cy) });
            }
    }

    void DrawDetails(Graphics g, Palette pal)
    {
        bool preview = !IsHandleCreated;
        Color ink = Ink(pal), soft = Soft(pal);
        MediaSession.Snapshot s = preview ? Sample() : now;
        float side = Side, m = Inset;
        EnsureFonts(side);

        // Art tile: the cover, the cover in dots, or a note drawn in dots. It is the play / pause button.
        RectangleF tile = TileRect;
        float[] dots = preview ? SampleDots : artDots;
        using (var shape = RoundRect(tile, side * 0.04f))
        {
            if (art != null && !Dotted && !preview)
            {
                EnsureScaled((int)Math.Ceiling(tile.Width), IsVibrant);
                using (var paint = new TextureBrush(scaled))
                {
                    paint.TranslateTransform(tile.X, tile.Y);
                    g.FillPath(paint, shape);
                }
            }
            else
            {
                using (var b = new SolidBrush(Tile(pal))) g.FillPath(b, shape);
                if (dots != null && (Dotted || preview)) DrawArtDots(g, ink, dots, tile);
                else DrawDots(g, ink, Note, tile.X + tile.Width / 2, tile.Y + tile.Height / 2, tile.Width * 0.52f / 9f);
            }
            // Paused: the art dims under a play mark.
            if (s != null && !s.Playing)
            {
                using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillPath(dim, shape);
                float t = tile.Width * 0.13f, cx = tile.X + tile.Width / 2, cy = tile.Y + tile.Height / 2;
                using (var b = new SolidBrush(Color.White))
                    g.FillPolygon(b, new[] { new PointF(cx - t * 0.7f, cy - t), new PointF(cx - t * 0.7f, cy + t), new PointF(cx + t * 1.1f, cy) });
            }
        }

        // A dot-drawn note in the top-right corner.
        float cell = side * 0.0155f;
        DrawDots(g, s != null && s.Playing ? ink : soft, Note, W - m - cell * 4.5f, m + cell * 4.5f, cell);

        // Track, then artist, then the progress bar.
        RectangleF bar = BarRect;
        float titleY = tile.Bottom + side * 0.045f, titleH = titleFont.GetHeight(g);
        if (s == null) Label(g, status ?? "Not playing", titleFont, soft, m, titleY, W - m * 2, StringAlignment.Near);
        else
        {
            Label(g, s.Title ?? "", titleFont, ink, m, titleY, W - m * 2, StringAlignment.Near);
            Label(g, s.Artist ?? "", textFont, ink, m, titleY + titleH * 1.02f, W - m * 2, StringAlignment.Near);
        }

        FillRound(g, Track(pal), bar, bar.Height / 2);
        if (s != null && s.Duration > TimeSpan.Zero)
        {
            TimeSpan pos;
            if (seekFrac >= 0) pos = TimeSpan.FromSeconds(seekFrac * s.Duration.TotalSeconds);
            else
            {
                pos = s.Position + (s.Playing ? DateTime.UtcNow - s.PositionAtUtc : TimeSpan.Zero);
                if (pos > s.Duration) pos = s.Duration;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            }
            float frac = (float)(pos.TotalSeconds / s.Duration.TotalSeconds);
            if (frac > 0) FillRound(g, ink, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * frac), bar.Height), bar.Height / 2);
        }
    }

    // The cover scaled once per size, so it can be painted as a brush with smooth corners.
    // Vibrant shows it desaturated, as the app's other vibrant widgets do.
    void EnsureScaled(int size, bool gray)
    {
        size = Math.Max(1, size);
        string key = size + "|" + gray;
        if (scaled != null && scaledFor == key) return;
        DropScaled();
        scaled = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        scaled.SetResolution(96, 96);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var dest = new RectangleF(0, 0, size, size);
            if (gray) DrawGray(g, art, dest); else g.DrawImage(art, dest);
        }
        scaledFor = key;
    }

    // The cover as a grid of dots: the brighter the cell, the bigger the dot.
    static void DrawArtDots(Graphics g, Color c, float[] dots, RectangleF area)
    {
        float pad = area.Width * 0.09f, cell = (area.Width - pad * 2) / DotGrid;
        using (var b = new SolidBrush(c))
            for (int y = 0; y < DotGrid; y++)
                for (int x = 0; x < DotGrid; x++)
                {
                    float d = cell * (0.16f + 0.78f * dots[y * DotGrid + x]);
                    g.FillEllipse(b, area.X + pad + x * cell + (cell - d) / 2, area.Y + pad + y * cell + (cell - d) / 2, d, d);
                }
    }

    // Draws a dot bitmap ('#' is a dot) centred on (cx, cy), one dot per cell.
    static void DrawDots(Graphics g, Color c, string[] rows, float cx, float cy, float cell)
    {
        int cols = rows[0].Length;
        float left = cx - cols * cell / 2, top = cy - rows.Length * cell / 2, d = cell * 0.84f, pad = (cell - d) / 2;
        using (var b = new SolidBrush(c))
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < cols; x++)
                    if (rows[y][x] == '#') g.FillEllipse(b, left + x * cell + pad, top + y * cell + pad, d, d);
    }

    static readonly string[] Note =
    {
        "..#######",
        "..#######",
        "..#.....#",
        "..#.....#",
        "..#.....#",
        "..#.....#",
        "###...###",
        "###...###",
        ".#.....#."
    };

    void EnsureFonts(float side)
    {
        if (textFont != null && Math.Abs(side - fontsFor) < 0.01f) return;
        DisposeFonts();
        titleFont = new Font("Segoe UI", Math.Max(1f, side * 0.092f), FontStyle.Regular, GraphicsUnit.Pixel);
        textFont = new Font("Segoe UI", Math.Max(1f, side * 0.076f), FontStyle.Regular, GraphicsUnit.Pixel);
        fontsFor = side;
    }

    void DisposeFonts()
    {
        if (titleFont != null) { titleFont.Dispose(); titleFont = null; }
        if (textFont != null) { textFont.Dispose(); textFont = null; }
    }

    // Library preview: a sample track, with a sun over a hill for the dotted art.
    static MediaSession.Snapshot Sample()
    {
        return new MediaSession.Snapshot
        {
            Title = "Low Tide Radio", Artist = "Harbour Lights", App = "", Playing = true, CanNext = true, CanPrevious = true,
            Position = TimeSpan.FromSeconds(84), Duration = TimeSpan.FromSeconds(221), PositionAtUtc = DateTime.UtcNow
        };
    }

    static readonly float[] SampleDots = MakeSampleDots();

    static float[] MakeSampleDots()
    {
        var dots = new float[DotGrid * DotGrid];
        for (int y = 0; y < DotGrid; y++)
            for (int x = 0; x < DotGrid; x++)
            {
                float sun = (float)Math.Sqrt((x - 9.5f) * (x - 9.5f) + (y - 4f) * (y - 4f));
                float hill = y - (9.5f - 2.2f * (float)Math.Sin(x / (DotGrid - 1f) * Math.PI));
                dots[y * DotGrid + x] = sun < 2.6f ? 1f : hill > 0 ? 0.55f : 0.12f + 0.02f * y;
            }
        return dots;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            anim.Dispose();
            if (media != null) { media.Changed -= OnMediaChanged; media.Dispose(); media = null; }
            if (art != null) { art.Dispose(); art = null; }
            DropScaled();
            DisposeFonts();
        }
        base.Dispose(disposing);
    }
}
