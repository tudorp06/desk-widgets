using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// A volume knob for the PC's default speakers or headphones.
//   Turn it: press the knob and drag around it, or roll the mouse wheel anywhere over the widget.
//   Mute: click it. The pointer on the knob turns red while the sound is muted.
// The bar around the knob shows the level: it brightens as it fills and runs into red at full
// volume. Because a drag on the knob turns it, the widget is moved by dragging the ring
// outside the knob. It works through Windows' own volume control, so the level always matches
// the taskbar; changes made elsewhere show here within a second. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Volume", Description = "A volume knob: drag or scroll to turn it, click to mute")]
public sealed class NothingVolume : CardWidget
{
    static readonly Color DarkFace = Rgb(0x1B1B1D), LightFace = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color SignalRed = Rgb(0xD71921);

    const float StartAngle = 135f, Sweep = 270f; // the bar runs clockwise from lower left to lower right
    const float WheelStep = 0.02f;

    // ---------- Windows audio (Core Audio) ----------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // The volume control of the default playback device, or null when there is none.
    static IAudioEndpointVolume Open()
    {
        IMMDeviceEnumerator devices = null;
        IMMDevice device = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (devices.GetDefaultAudioEndpoint(0, 0, out device) < 0 || device == null) return null; // playback, everyday role
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out o) < 0) return null;
            return o as IAudioEndpointVolume;
        }
        catch (Exception) { return null; }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (devices != null) Marshal.ReleaseComObject(devices);
        }
    }

    // The control is kept between reads and looked up again every ten seconds, so the widget
    // follows a change of the default device (plugging in headphones).
    IAudioEndpointVolume endpoint;
    int endpointAge;

    IAudioEndpointVolume Endpoint()
    {
        if (endpoint != null && endpointAge++ < 10) return endpoint;
        DropEndpoint();
        endpoint = Open();
        endpointAge = 0;
        return endpoint;
    }

    void DropEndpoint()
    {
        if (endpoint != null) { Marshal.ReleaseComObject(endpoint); endpoint = null; }
    }

    readonly Timer poll = new Timer { Interval = 1000 };
    float level = 0.64f;
    bool muted, present;

    // Turning: a press on the knob that has moved past the drag threshold.
    bool pressedKnob, turning;
    Point downAt;
    double lastAngle;

    Font textFont;
    float fontFor = -1f;

    public NothingVolume(Section settings) : base(settings, false)
    {
        poll.Tick += delegate { if (!pressedKnob && !IsCovered()) Read(false); };
    }

    protected override bool SquareResize { get { return true; } }

    // Nearly transparent, so the corners outside the circle still take the mouse (move, resize, menu).
    protected override Color CardBackground(Palette pal) { return Color.FromArgb(1, 0, 0, 0); }

    protected override void OnStart()
    {
        Read(true);
        poll.Start();
    }

    void Read(bool always)
    {
        float was = level;
        bool wasMuted = muted, had = present;
        IAudioEndpointVolume v = Endpoint();
        present = v != null;
        if (v != null)
        {
            float l;
            bool m;
            if (v.GetMasterVolumeLevelScalar(out l) < 0 || v.GetMute(out m) < 0) { DropEndpoint(); present = false; }
            else { level = l; muted = m; }
        }
        if (always || Math.Abs(level - was) > 0.001f || muted != wasMuted || present != had) Render();
    }

    // Turning the knob up while muted also brings the sound back.
    void SetLevel(float to)
    {
        to = Math.Max(0f, Math.Min(1f, to));
        IAudioEndpointVolume v = Endpoint();
        if (v == null) return;
        Guid context = Guid.Empty;
        bool up = to > level;
        if (v.SetMasterVolumeLevelScalar(to, ref context) < 0) { DropEndpoint(); return; }
        level = to;
        if (up && muted && v.SetMute(false, ref context) >= 0) muted = false;
        Render();
    }

    void ToggleMute()
    {
        IAudioEndpointVolume v = Endpoint();
        if (v == null) return;
        Guid context = Guid.Empty;
        if (v.SetMute(!muted, ref context) >= 0) muted = !muted;
        Render();
    }

    // ---------- interaction ----------
    float Side { get { return Math.Min(W, H); } }
    float KnobRadius { get { return Side * 0.27f; } }

    bool OnKnob(Point p)
    {
        float c = Side / 2f, dx = p.X - c, dy = p.Y - c, r = KnobRadius * 1.06f;
        return dx * dx + dy * dy <= r * r;
    }

    // Where a point is around the knob's centre, in degrees.
    double AngleOf(Point p)
    {
        float c = Side / 2f;
        return Math.Atan2(p.Y - c, p.X - c) * 180 / Math.PI;
    }

    protected override bool IsButton(Point p) { return present && OnKnob(p); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!present || e.Delta == 0) return;
        // Whole steps, so the readout lands on even numbers.
        float steps = (float)Math.Round(level / WheelStep) + Math.Sign(e.Delta);
        SetLevel(steps * WheelStep);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && present && OnKnob(e.Location))
        {
            pressedKnob = true;
            turning = false;
            downAt = e.Location;
            Capture = true;
            return; // not a press the base should turn into a move
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!pressedKnob) { base.OnMouseMove(e); return; }
        if ((MouseButtons & MouseButtons.Left) == 0) { pressedKnob = false; Capture = false; return; } // the release was missed
        if (!turning)
        {
            Size slop = SystemInformation.DragSize;
            if (Math.Abs(e.X - downAt.X) < slop.Width && Math.Abs(e.Y - downAt.Y) < slop.Height) return;
            turning = true;
            lastAngle = AngleOf(e.Location);
            return;
        }
        // Right at the centre the angle swings wildly: wait until the pointer is clear of it.
        float c = Side / 2f, dx = e.X - c, dy = e.Y - c;
        if (dx * dx + dy * dy < Side * Side * 0.0016f) return;
        double angle = AngleOf(e.Location), turn = angle - lastAngle;
        while (turn > 180) turn -= 360;
        while (turn < -180) turn += 360;
        lastAngle = angle;
        SetLevel(level + (float)(turn / Sweep));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!pressedKnob || e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }
        pressedKnob = false;
        Capture = false;
        if (!turning) ToggleMute(); // a press that didn't move
        turning = false;
    }

    // A click on the ring outside the knob mutes too.
    protected override void OnWidgetClick(Point p)
    {
        if (present) ToggleMute();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (present) menu.Add(muted ? "Unmute" : "Mute", ToggleMute);
        menu.Add("Sound Settings…", delegate
        {
            try { Process.Start("ms-settings:sound"); }
            catch (Exception) { }
        });
    }

    // ---------- drawing ----------
    static Color Mix(Color a, Color b, float t)
    {
        t = Math.Max(0f, Math.Min(1f, t));
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    static PointF OnCircle(float c, float degrees, float radius)
    {
        double a = degrees * Math.PI / 180;
        return new PointF(c + (float)Math.Cos(a) * radius, c + (float)Math.Sin(a) * radius);
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        float s = Side, c = s / 2f;
        bool preview = !IsHandleCreated, vibrant = IsVibrant, dark = IsDark;
        bool has = preview || present, off = !preview && present && muted;
        float lv = has ? Math.Max(0f, Math.Min(1f, level)) : 0f;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (vibrant) FillGlassCircle(g, c, c, c - 1.5f);
        else FillCircle(g, dark ? DarkFace : LightFace, c, c, c - 0.5f);
        Color ink = vibrant || dark ? Color.White : LightInk;

        DrawBar(g, c, s, lv, off, ink, vibrant, dark);
        DrawKnob(g, c, s, lv, off, has, vibrant, dark);

        // The level, in the gap at the bottom of the bar.
        if (textFont == null || Math.Abs(s - fontFor) > 0.01f)
        {
            if (textFont != null) textFont.Dispose();
            textFont = new Font("Segoe UI", Math.Max(1f, s * 0.085f), FontStyle.Regular, GraphicsUnit.Pixel);
            fontFor = s;
        }
        string text = !has ? "—" : off ? "MUTED" : Math.Round(lv * 100).ToString(CultureInfo.InvariantCulture);
        Color textColor = off && !vibrant ? SignalRed : has ? ink : Color.FromArgb(120, ink);
        Label(g, text, textFont, textColor, c, s * 0.795f, s * 0.5f, StringAlignment.Center);
    }

    // The level bar: a dim track, and over it the filled part, whose colour depends on how far
    // round the track each piece sits: soft at the start, full white (or ink) by 70 %, red at the end.
    void DrawBar(Graphics g, float c, float s, float lv, bool off, Color ink, bool vibrant, bool dark)
    {
        float radius = s * 0.405f, thick = s * 0.05f;
        var box = new RectangleF(c - radius, c - radius, radius * 2, radius * 2);
        Color track = vibrant ? Color.FromArgb(50, 255, 255, 255) : dark ? Rgb(0x38383B) : Rgb(0xE3E3E8);
        using (var pen = new Pen(track, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, box, StartAngle, Sweep);
        if (lv <= 0) return;

        Color low = vibrant ? Color.FromArgb(150, 255, 255, 255) : dark ? Rgb(0x8C8C90) : Rgb(0x9A9AA0);
        Color hot = vibrant ? Color.White : SignalRed;
        Color quiet = vibrant ? Color.FromArgb(110, 255, 255, 255) : dark ? Rgb(0x66666B) : Rgb(0xB4B4BA);
        Func<float, Color> at = p => off ? quiet : p < 0.7f ? Mix(low, ink, p / 0.7f) : Mix(ink, hot, (p - 0.7f) / 0.3f);

        const float piece = 3f; // degrees
        float filled = Sweep * lv;
        PointF start = OnCircle(c, StartAngle, radius), end = OnCircle(c, StartAngle + filled, radius);
        FillCircle(g, at(0), start.X, start.Y, thick / 2);
        for (float a = 0; a < filled; a += piece)
        {
            float span = Math.Min(piece + 0.6f, filled - a); // a little overlap hides the joins
            // GDI+ fails with "out of memory" on an arc of almost no sweep; the round end
            // drawn below covers a sliver that short anyway.
            if (span < 0.5f) break;
            using (var pen = new Pen(at((a + piece / 2) / Sweep), thick))
                g.DrawArc(pen, box, StartAngle + a, span);
        }
        FillCircle(g, at(lv), end.X, end.Y, thick / 2);
    }

    // The knob: a soft shadow under it, a bevelled rim with grip lines that turn with it, a
    // domed face lit from above, and a pointer set into the face.
    void DrawKnob(Graphics g, float c, float s, float lv, bool off, bool has, bool vibrant, bool dark)
    {
        float r = KnobRadius, face = r * 0.87f, angle = StartAngle + Sweep * lv;

        using (var shade = new SolidBrush(Color.FromArgb(vibrant ? 12 : dark ? 26 : 10, 0, 0, 0)))
            for (int i = 6; i >= 1; i--)
            {
                float grow = s * 0.007f * i, drop = s * 0.02f + s * 0.004f * i;
                g.FillEllipse(shade, c - r - grow, c - r - grow + drop, (r + grow) * 2, (r + grow) * 2);
            }

        Color rimTop, rimBottom, faceTop, faceBottom, grip;
        if (vibrant)
        {
            rimTop = Color.FromArgb(150, 255, 255, 255); rimBottom = Color.FromArgb(45, 255, 255, 255);
            faceTop = Color.FromArgb(95, 255, 255, 255); faceBottom = Color.FromArgb(30, 255, 255, 255);
            grip = Color.FromArgb(50, 0, 0, 0);
        }
        else if (dark)
        {
            rimTop = Rgb(0x66666B); rimBottom = Rgb(0x1A1A1C); faceTop = Rgb(0x47474B); faceBottom = Rgb(0x232325);
            grip = Color.FromArgb(90, 0, 0, 0);
        }
        else
        {
            rimTop = Color.White; rimBottom = Rgb(0xB9B9C0); faceTop = Rgb(0xFBFBFC); faceBottom = Rgb(0xD9D9DF);
            grip = Color.FromArgb(34, 0, 0, 0);
        }

        var rimBox = new RectangleF(c - r, c - r, r * 2, r * 2);
        using (var rim = new LinearGradientBrush(new RectangleF(rimBox.X, rimBox.Y - 1, rimBox.Width, rimBox.Height + 2), rimTop, rimBottom, 90f))
            g.FillEllipse(rim, rimBox);

        // Grip lines on the rim, every ten degrees, turning with the knob.
        using (var pen = new Pen(grip, Math.Max(1f, s * 0.006f)))
            for (int i = 0; i < 36; i++)
            {
                PointF a = OnCircle(c, angle + i * 10, face), b = OnCircle(c, angle + i * 10, r - s * 0.004f);
                g.DrawLine(pen, a, b);
            }

        var faceBox = new RectangleF(c - face, c - face, face * 2, face * 2);
        using (var fill = new LinearGradientBrush(new RectangleF(faceBox.X, faceBox.Y - 1, faceBox.Width, faceBox.Height + 2), faceTop, faceBottom, 90f))
            g.FillEllipse(fill, faceBox);

        // A sheen toward the upper left, as if lit from there, and a bright lip along the top edge.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(faceBox);
            using (var sheen = new PathGradientBrush(path))
            {
                sheen.CenterPoint = new PointF(c - face * 0.35f, c - face * 0.5f);
                sheen.CenterColor = Color.FromArgb(vibrant ? 40 : dark ? 38 : 150, 255, 255, 255);
                sheen.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                g.FillEllipse(sheen, faceBox);
            }
        }
        using (var lip = new Pen(Color.FromArgb(vibrant ? 70 : dark ? 46 : 200, 255, 255, 255), Math.Max(1f, s * 0.005f)))
            g.DrawArc(lip, faceBox, 200, 140);

        // The pointer: a small well in the face with a lit dot in it; red while muted.
        PointF at = OnCircle(c, angle, face * 0.68f);
        float well = r * 0.115f;
        FillCircle(g, Color.FromArgb(vibrant ? 70 : dark ? 130 : 60, 0, 0, 0), at.X, at.Y, well);
        Color dot = !has ? Color.FromArgb(90, 255, 255, 255) : off && !vibrant ? SignalRed : vibrant || dark ? Color.White : LightInk;
        FillCircle(g, dot, at.X, at.Y, well * 0.62f);
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
        if (!IsVibrant) { base.Draw(g); return; }
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
        if (disposing)
        {
            poll.Dispose();
            DropEndpoint();
            if (textFont != null) { textFont.Dispose(); textFont = null; }
            if (frost != null) { frost.Dispose(); frost = null; }
        }
        base.Dispose(disposing);
    }
}
