using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// A round button for the microphone: one click mutes or unmutes it, and the disc turns red
// while it is muted. It works on the default microphone, both the everyday one and the one
// Windows uses for calls when they differ, through Windows' own volume control, so the mute
// shows in Windows' sound settings too. Mutes made elsewhere show here within two seconds.
// Nothing is recorded or sent anywhere.
[DeskWidget(Name = "Nothing Mic Mute", Description = "One click mutes or unmutes your microphone")]
public sealed class NothingMicMute : CardWidget
{
    static readonly Color DarkFace = Rgb(0x1B1B1D), LightFace = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color SignalRed = Rgb(0xD71921);

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

    const int Recording = 1, RoleEveryday = 0, RoleCalls = 2, ClsCtxAll = 23;

    // The volume control of the default microphone for a role, or null when there is none.
    static IAudioEndpointVolume Open(int role)
    {
        IMMDeviceEnumerator devices = null;
        IMMDevice device = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (devices.GetDefaultAudioEndpoint(Recording, role, out device) < 0 || device == null) return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out o) < 0) return null;
            return o as IAudioEndpointVolume;
        }
        catch (Exception) { return null; }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (devices != null) Marshal.ReleaseComObject(devices);
        }
    }

    readonly Timer poll = new Timer { Interval = 2000 };
    bool muted, present;
    Font textFont;
    float fontFor = -1f;

    public NothingMicMute(Section settings) : base(settings, false)
    {
        poll.Tick += delegate { if (!IsCovered()) Read(false); };
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
        bool was = muted, had = present;
        IAudioEndpointVolume v = Open(RoleCalls) ?? Open(RoleEveryday);
        present = v != null;
        if (v != null)
        {
            bool m;
            if (v.GetMute(out m) >= 0) muted = m;
            Marshal.ReleaseComObject(v);
        }
        if (always || muted != was || present != had) Render();
    }

    void Toggle()
    {
        bool want = !muted, done = false;
        foreach (int role in new[] { RoleCalls, RoleEveryday })
        {
            IAudioEndpointVolume v = Open(role);
            if (v == null) continue;
            Guid context = Guid.Empty;
            if (v.SetMute(want, ref context) >= 0) done = true;
            Marshal.ReleaseComObject(v);
        }
        if (done) muted = want;
        Read(true);
    }

    protected override void OnWidgetClick(Point p)
    {
        if (present) Toggle();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (present) menu.Add(muted ? "Unmute Microphone" : "Mute Microphone", Toggle);
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        float s = Math.Min(W, H), c = s / 2f;
        bool preview = !IsHandleCreated, vibrant = IsVibrant;
        bool has = preview || present, off = preview || (present && muted);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (vibrant) FillGlassCircle(g, c, c, c - 1.5f);
        else FillCircle(g, off ? SignalRed : IsDark ? DarkFace : LightFace, c, c, c - 0.5f);
        Color ink = vibrant || off || IsDark ? Color.White : LightInk;
        if (!has) ink = Color.FromArgb(120, ink);

        // The microphone, drawn on a 24-unit grid: capsule, cradle, stem, and a slash when muted.
        float k = s * 0.25f / 24f, ox = c - 12 * k, oy = c - s * 0.2f;
        using (var pen = new Pen(ink, 1.7f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            using (var capsule = RoundRect(new RectangleF(ox + 9 * k, oy + 3 * k, 6 * k, 11 * k), 3 * k)) g.DrawPath(pen, capsule);
            g.DrawArc(pen, ox + 5.5f * k, oy + 5 * k, 13 * k, 13 * k, 0, 180);
            g.DrawLine(pen, ox + 12 * k, oy + 18 * k, ox + 12 * k, oy + 21 * k);
            if (off) g.DrawLine(pen, ox + 4 * k, oy + 4 * k, ox + 20 * k, oy + 20 * k);
        }

        if (textFont == null || Math.Abs(s - fontFor) > 0.01f)
        {
            if (textFont != null) textFont.Dispose();
            textFont = new Font("Segoe UI", Math.Max(1f, s * 0.075f), FontStyle.Regular, GraphicsUnit.Pixel);
            fontFor = s;
        }
        string text = !has ? "No microphone" : off ? "Mic muted" : "Mic on";
        Label(g, text, textFont, ink, c, oy + 24 * k + s * 0.035f, s * 0.8f, StringAlignment.Center);
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
            if (textFont != null) { textFont.Dispose(); textFont = null; }
            if (frost != null) { frost.Dispose(); frost = null; }
        }
        base.Dispose(disposing);
    }
}
