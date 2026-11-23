using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// Medium: album art, track, artist, progress and previous / play-pause / next for whatever
// Windows is playing (Spotify, browsers, Media Player...). Updates come as events from Windows;
// the widget only redraws once a second while something plays, to move the progress bar.
[DeskWidget(Name = "Liquid Glass Player", Description = "A frosted music card with compact playback controls and a sleepy pixel cat.")]
public sealed class GlassPlayerWidget : CardWidget
{
    MediaSession media;            // opened in OnStart, never for library previews
    MediaSession.Snapshot now;
    Bitmap art;                    // cropped to the art square
    string status;
    bool refreshing, refreshAgain;
    bool seeking;
    float seekFrac = -1;

    public GlassPlayerWidget(Section settings) : base(settings, CardSize.Medium) { }

    protected override string DefaultFace { get { return "vibrant"; } }

    protected override void OnStart()
    {
        Background(() => MediaSession.Open(), (m, error) =>
        {
            if (error != null || m == null) { status = "Media controls aren't available on this version of Windows"; Render(); return; }
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
            Bitmap cover = snap != null && snap.Art != null ? Square(snap.Art) : null;
            return Tuple.Create(snap, cover);
        }, (result, error) =>
        {
            refreshing = false;
            if (error == null)
            {
                now = result.Item1;
                if (!seeking) seekFrac = -1;
                if (art != null) art.Dispose();
                art = result.Item2;
                status = null;
            }
            Render();
            if (refreshAgain) { refreshAgain = false; RefreshMedia(); }
        });
    }

    static Bitmap Square(byte[] bytes)
    {
        try
        {
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                float srcSide = Math.Min(img.Width, img.Height);
                int size = Math.Min((int)srcSide, 512);
                if (size <= 0) return null;
                var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, size, size), new RectangleF((img.Width - srcSide) / 2, (img.Height - srcSide) / 2, srcSide, srcSide), GraphicsUnit.Pixel);
                }
                return bmp;
            }
        }
        catch (Exception) { return null; }
    }

    protected override int NextFrameDelay()
    {
        if (now == null || !now.Playing || now.Duration <= TimeSpan.Zero || !IsHandleCreated) return -1;
        return 1000 - DateTime.UtcNow.Millisecond + 20;
    }

    // ---------- layout ----------
    float ArtSize { get { return Math.Min(H - P(32), P(137)); } }
    float RightX { get { return P(16) + ArtSize + P(14); } }
    PointF PlayCenter { get { return new PointF((RightX + W - P(16)) / 2, H / 2 + P(42)); } }
    PointF PrevCenter { get { PointF c = PlayCenter; return new PointF(c.X - P(26), c.Y); } }
    PointF NextCenter { get { PointF c = PlayCenter; return new PointF(c.X + P(26), c.Y); } }

    static bool Within(Point p, PointF c, float r) { float dx = p.X - c.X, dy = p.Y - c.Y; return dx * dx + dy * dy <= r * r; }

    protected override bool IsButton(Point p)
    {
        return now != null && (Within(p, PlayCenter, P(13)) || Within(p, PrevCenter, P(12)) || Within(p, NextCenter, P(16))
            || (now.Duration > TimeSpan.Zero && InProgressBar(p)));
    }

    protected override void OnWidgetClick(Point p)
    {
        if (media == null || now == null) return;
        if (Within(p, PlayCenter, P(13))) media.PlayPause();
        else if (now.CanPrevious && Within(p, PrevCenter, P(12))) media.Previous();
        else if (now.CanNext && Within(p, NextCenter, P(12))) media.Next();
        else if (now.Duration > TimeSpan.Zero && InProgressBar(p)) SeekToPoint(p);
    }

    bool InProgressBar(Point p)
    {
        if (now == null || now.Duration <= TimeSpan.Zero) return false;
        float m = P(16), rx = RightX, rw = W - m - rx;
        float by = H / 2 + P(1), bh = P(4), pad = P(10);
        return p.X >= rx && p.X <= rx + rw && p.Y >= by - pad && p.Y <= by + bh + pad;
    }

    void SeekToPoint(Point p)
    {
        if (media == null || now == null || now.Duration <= TimeSpan.Zero) return;
        float m = P(16), rx = RightX, rw = W - m - rx;
        float frac = Math.Max(0f, Math.Min(1f, (p.X - rx) / rw));
        seekFrac = frac;
        Render();
        SeekTo(TimeSpan.FromSeconds(frac * now.Duration.TotalSeconds));
    }

    void SeekTo(TimeSpan position)
    {
        if (media != null) media.Seek(position.Ticks);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && InProgressBar(e.Location) && now != null && now.Duration > TimeSpan.Zero)
            seeking = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (seeking && (MouseButtons & MouseButtons.Left) != 0)
            SeekToPoint(e.Location);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        seeking = false;
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (media != null && now != null)
        {
            menu.Add(now.Playing ? "Pause" : "Play", media.PlayPause);
            menu.Add("Next Track", media.Next);
            menu.Add("Previous Track", media.Previous);
        }
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        using (var edge = RoundRect(new RectangleF(P(1), P(1), W - P(2), H - P(2)), P(14)))
        using (var sheen = new LinearGradientBrush(new RectangleF(0, 0, W, H),
            Color.FromArgb(128, 233, 246, 255), Color.FromArgb(26, 172, 211, 239), 65f))
        using (var rim = new Pen(sheen, Math.Max(1, P(1))))
            g.DrawPath(rim, edge);

        MediaSession.Snapshot s = IsHandleCreated ? now : Sample();
        float m = P(16), a = ArtSize, rx = RightX, rw = W - m - rx;

        // Album art (or a placeholder note).
        var artRect = new RectangleF((int)Math.Round(m), (int)Math.Round((H - a) / 2), (int)Math.Round(a), (int)Math.Round(a));
        using (var clip = RoundRect(artRect, P(10)))
        {
            if (art != null)
            {
                var state = g.Save();
                g.SetClip(clip);
                g.DrawImage(art, artRect);
                g.Restore(state);
            }
            else if (!IsHandleCreated) PreviewArt(g, clip, artRect);
            else
            {
                using (var b = new SolidBrush(pal.Fill)) g.FillPath(b, clip);
                if (s == null) DrawSleepyCat(g, artRect);
                else NoteGlyph(g, pal.Fg2, artRect);
            }
        }

        if (s == null)
        {
            Label(g, "Nothing playing", F(15, "semibold"), pal.Fg, rx, H / 2 - P(44), rw, StringAlignment.Near);
            Paragraph(g, status ?? "Play music or a video in any app and it shows up here.", F(11, "regular"), pal.Fg2, new RectangleF(rx, H / 2 - P(8), W - rx - m, P(46)));
            return;
        }

        Label(g, s.Title, F(15, "semibold"), pal.Fg, rx, H / 2 - P(44), rw, StringAlignment.Near);
        Label(g, s.Artist, F(13, "regular"), pal.Fg2, rx, H / 2 - P(23), rw, StringAlignment.Near);

        // Progress (only when the app reports a timeline; some browsers don't).
        if (s.Duration > TimeSpan.Zero)
        {
            TimeSpan pos;
            if (seekFrac >= 0)
                pos = TimeSpan.FromSeconds(seekFrac * s.Duration.TotalSeconds);
            else
            {
                pos = s.Position + (s.Playing ? DateTime.UtcNow - s.PositionAtUtc : TimeSpan.Zero);
                if (pos > s.Duration) pos = s.Duration;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            }
            float by = H / 2 + P(1), bh = P(4);
            FillRound(g, pal.Fill, new RectangleF(rx, by, rw, bh), bh / 2);
            float frac = (float)(pos.TotalSeconds / s.Duration.TotalSeconds);
            if (frac > 0) FillRound(g, pal.Fg, new RectangleF(rx, by, Math.Max(bh, rw * frac), bh), bh / 2);
            Label(g, Clock(pos), F(11, "regular"), pal.Fg2, rx, by + P(5), rw / 2, StringAlignment.Near);
            Label(g, Clock(s.Duration), F(11, "regular"), pal.Fg2, rx + rw, by + P(5), rw / 2, StringAlignment.Far);
        }

        // Controls.
        PointF pc = PlayCenter, prev = PrevCenter, next = NextCenter;
        Color glyph = pal.Fg;
        using (var b = new SolidBrush(glyph))
        {
            if (s.Playing)
            {
                float bw = P(4), bh = P(13);
                g.FillRectangle(b, pc.X - P(5.5f), pc.Y - bh / 2, bw, bh);
                g.FillRectangle(b, pc.X + P(1.5f), pc.Y - bh / 2, bw, bh);
            }
            else
            {
                float t = P(7);
                g.FillPolygon(b, new[] { new PointF(pc.X - t * 0.6f, pc.Y - t), new PointF(pc.X - t * 0.6f, pc.Y + t), new PointF(pc.X + t, pc.Y) });
            }
        }
        SkipButton(g, pal, prev, false, s.CanPrevious || !IsHandleCreated);
        SkipButton(g, pal, next, true, s.CanNext || !IsHandleCreated);
    }

    void SkipButton(Graphics g, Palette pal, PointF c, bool forward, bool enabled)
    {
        
        using (var b = new SolidBrush(enabled ? pal.Fg : Color.FromArgb(75, pal.Fg)))
        {
            float d = forward ? 1 : -1;
            float size = P(5);
            g.FillPolygon(b, new[] { new PointF(c.X - d * size, c.Y - size),
                new PointF(c.X - d * size, c.Y + size), new PointF(c.X + d * size, c.Y) });
            g.FillRectangle(b, c.X + d * P(6) - P(1), c.Y - size, P(2), 2 * size);
        }
    }

    static string Clock(TimeSpan t)
    {
        return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                                 : t.Minutes + ":" + t.Seconds.ToString("00");
    }

    static readonly string[] SleepyCat = {
        ".##.......##.", ".#+#.....#+#.", ".#++#####++#.", ".###########.",
        ".##--###--##.", ".+####+####+.", "..#########..", "...#######..."
    };

    static void DrawSleepyCat(Graphics g, RectangleF box)
    {
        int pixel = Math.Max(1, (int)(box.Width / 17));
        int left = (int)(box.X + (box.Width - 13 * pixel) / 2);
        int top = (int)(box.Y + (box.Height - SleepyCat.Length * pixel) / 2);
        using (var fur = new SolidBrush(Color.FromArgb(255, 238, 215)))
        using (var peach = new SolidBrush(Color.FromArgb(255, 179, 149)))
        using (var ink = new SolidBrush(Color.FromArgb(59, 36, 54)))
            for (int y = 0; y < SleepyCat.Length; y++)
                for (int x = 0; x < SleepyCat[y].Length; x++)
                {
                    char mark = SleepyCat[y][x];
                    if (mark == '.') continue;
                    g.FillRectangle(mark == '+' ? peach : mark == '-' ? ink : fur,
                        left + x * pixel, top + y * pixel, pixel, pixel);
                }
    }

    void NoteGlyph(Graphics g, Color c, RectangleF r)
    {
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width * 0.28f;
        using (var b = new SolidBrush(c))
        using (var pen = new Pen(c, Math.Max(1.5f, s * 0.12f)))
        {
            g.FillEllipse(b, cx - s * 0.7f, cy + s * 0.25f, s * 0.55f, s * 0.42f);
            g.FillEllipse(b, cx + s * 0.15f, cy + s * 0.05f, s * 0.55f, s * 0.42f);
            g.DrawLine(pen, cx - s * 0.17f, cy + s * 0.45f, cx - s * 0.17f, cy - s * 0.55f);
            g.DrawLine(pen, cx + s * 0.68f, cy + s * 0.25f, cx + s * 0.68f, cy - s * 0.75f);
            g.DrawLine(pen, cx - s * 0.17f, cy - s * 0.55f, cx + s * 0.68f, cy - s * 0.75f);
        }
    }

    // Library preview: a sample track with painted art.
    static MediaSession.Snapshot Sample()
    {
        return new MediaSession.Snapshot
        {
            Title = "Afterimage", Artist = "Glass Signals", App = "Spotify", Playing = true, CanNext = true, CanPrevious = true,
            Position = TimeSpan.FromSeconds(84), Duration = TimeSpan.FromSeconds(221), PositionAtUtc = DateTime.UtcNow
        };
    }

    void PreviewArt(Graphics g, GraphicsPath clip, RectangleF r)
    {
        var state = g.Save();
        g.SetClip(clip);
        using (var background = new LinearGradientBrush(r, Rgb(0x101720), Rgb(0x334c60), 125f))
            g.FillRectangle(background, r);
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 100f, r.Height / 100f);
        for (int i = 0; i < 8; i++)
        {
            float offset = i * 13;
            using (var ribbon = new GraphicsPath())
            {
                ribbon.AddBezier(-18, 100 + offset, 27, 46 + offset, 67, 65 + offset, 105, -45 + offset);
                using (var pen = new Pen(Color.FromArgb(170, 9, 17, 26), 10))
                    g.DrawPath(pen, ribbon);
                using (var pen = new Pen(Color.FromArgb(175, 137, 193, 228), 2))
                    g.DrawPath(pen, ribbon);
            }
        }
        using (var font = new Font("Segoe UI Semibold", 8, GraphicsUnit.Pixel))
            g.DrawString("A F T E R I M A G E", font, Brushes.White, 12, 72);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (media != null) { media.Changed -= OnMediaChanged; media.Dispose(); media = null; }
            if (art != null) { art.Dispose(); art = null; }
        }
        base.Dispose(disposing);
    }
}
