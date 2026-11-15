using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using DeskWidgets;
using DeskWidgets.Widgets;

// Three pills, no card behind them: Wi-Fi, Bluetooth, and Do not disturb at the bottom. Click
// a pill to switch it on or off; a pill is filled while it is on, and Do not disturb turns red.
// Wi-Fi and Bluetooth use the same Windows radio switches as Settings and the quick settings
// panel. Do not disturb uses the setting behind the bell in the notification centre, which
// Windows does not document, so a Windows update could stop that pill working; it then shows
// as not available. A change made in Windows shows here within ten seconds. A PC without one
// of the radios shows that pill as not available. Nothing is sent anywhere.
[DeskWidget(Name = "Nothing Quick Toggles", Description = "Wi-Fi, Bluetooth and Do not disturb as three pills")]
public sealed class NothingToggles : CardWidget
{
    static readonly Color Dark = Rgb(0x1B1B1D), Light = Color.White, Paper = Rgb(0xF2F2F3);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    // ---------- Windows radios (Windows.Devices.Radios, called through its COM interfaces) ----------
    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("combase.dll")] static extern void RoUninitialize();
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPtr(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetInt(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetAt(IntPtr self, uint index, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetState(IntPtr self, int state, out IntPtr operation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetText(IntPtr self, out IntPtr text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PutText(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text);

    const int KindWifi = 1, KindBluetooth = 3;          // RadioKind
    const int KindQuiet = 100;                          // this widget's own: Do not disturb
    const int StateOn = 1, StateOff = 2;                // RadioState (0 unknown, 3 disabled)
    const int NotFound = -1, NotRead = -2;              // this widget's own
    const int AccessAllowed = 1;                        // RadioAccessStatus

    static T Slot<T>(IntPtr obj, int slot) where T : class
    {
        IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // Blocks until a Windows async operation finishes (worker threads only).
    static void WaitFor(IntPtr operation)
    {
        Guid asyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
        IntPtr info;
        Check(Marshal.QueryInterface(operation, ref asyncInfo, out info));
        try
        {
            for (int i = 0; i < 800; i++)
            {
                int status;
                Check(Slot<GetInt>(info, 7)(info, out status));
                if (status == 1) return;
                if (status != 0) throw new InvalidOperationException("Windows radio request failed");
                Thread.Sleep(10);
            }
            throw new TimeoutException("Windows radios did not answer");
        }
        finally { Marshal.Release(info); }
    }

    sealed class Radios { public int Wifi = NotFound, Bluetooth = NotFound, Quiet = NotFound, Access = AccessAllowed; public bool Failed; }

    // Worker thread: reads all three switches; when switchKind is not zero, switches that one first.
    static Radios Visit(int switchKind, bool on)
    {
        var result = new Radios();
        try { VisitRadios(result, switchKind == KindQuiet ? 0 : switchKind, on); }
        catch (Exception) { result.Failed = switchKind != 0 && switchKind != KindQuiet; }
        result.Quiet = Quiet(switchKind == KindQuiet ? (on ? StateOn : StateOff) : 0);
        return result;
    }

    // ---------- Do not disturb (Windows' quiet hours setting; not a documented interface) ----------
    const string QuietOff = "Microsoft.QuietHoursProfile.Unrestricted", QuietOn = "Microsoft.QuietHoursProfile.PriorityOnly";

    // Reads Do not disturb as StateOn or StateOff, switching it first when change is one of
    // those. NotFound when this version of Windows doesn't offer the setting this way.
    static int Quiet(int change)
    {
        object o = null;
        IntPtr unknown = IntPtr.Zero, settings = IntPtr.Zero;
        try
        {
            o = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F53321FA-34F8-4B7F-B9A3-361877CB94CF")));
            unknown = Marshal.GetIUnknownForObject(o);
            Guid iid = new Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F");
            if (Marshal.QueryInterface(unknown, ref iid, out settings) < 0) return NotFound;
            if (change != 0) Slot<PutText>(settings, 4)(settings, change == StateOn ? QuietOn : QuietOff);
            IntPtr text;
            if (Slot<GetText>(settings, 3)(settings, out text) < 0 || text == IntPtr.Zero) return NotFound;
            string profile = Marshal.PtrToStringUni(text);
            Marshal.FreeCoTaskMem(text);
            if (profile == null || !profile.StartsWith("Microsoft.QuietHoursProfile.", StringComparison.Ordinal)) return NotFound;
            return profile == QuietOff ? StateOff : StateOn; // "alarms only" is on too
        }
        catch (Exception) { return NotFound; }
        finally
        {
            if (settings != IntPtr.Zero) Marshal.Release(settings);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
            if (o != null) Marshal.ReleaseComObject(o);
        }
    }

    static void VisitRadios(Radios result, int switchKind, bool on)
    {
        int init = RoInitialize(1);
        IntPtr name = IntPtr.Zero, factory = IntPtr.Zero, op = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            const string cls = "Windows.Devices.Radios.Radio";
            Check(WindowsCreateString(cls, cls.Length, out name));
            Guid statics = new Guid("5FB6A12E-67CB-46AE-AAE9-65919F86EFF4");
            Check(RoGetActivationFactory(name, ref statics, out factory));
            if (switchKind != 0)
            {
                // Ask for access first, as Windows expects before a radio is switched.
                IntPtr ask;
                Check(Slot<GetPtr>(factory, 9)(factory, out ask));
                try { WaitFor(ask); Check(Slot<GetInt>(ask, 8)(ask, out result.Access)); }
                finally { Marshal.Release(ask); }
            }
            Check(Slot<GetPtr>(factory, 6)(factory, out op));
            WaitFor(op);
            Check(Slot<GetPtr>(op, 8)(op, out list));
            int count;
            Check(Slot<GetInt>(list, 7)(list, out count));
            for (uint i = 0; i < count; i++)
            {
                IntPtr radio;
                Check(Slot<GetAt>(list, 6)(list, i, out radio));
                try
                {
                    int kind, state;
                    Check(Slot<GetInt>(radio, 11)(radio, out kind));
                    if (kind == switchKind && result.Access == AccessAllowed)
                    {
                        IntPtr change;
                        Check(Slot<SetState>(radio, 6)(radio, on ? StateOn : StateOff, out change));
                        try { WaitFor(change); Check(Slot<GetInt>(change, 8)(change, out result.Access)); }
                        finally { Marshal.Release(change); }
                    }
                    Check(Slot<GetInt>(radio, 9)(radio, out state));
                    // A PC can have more than one radio of a kind: the pill is on if any of them is.
                    if (kind == KindWifi && result.Wifi != StateOn) result.Wifi = state;
                    else if (kind == KindBluetooth && result.Bluetooth != StateOn) result.Bluetooth = state;
                }
                finally { Marshal.Release(radio); }
            }
        }
        finally
        {
            if (list != IntPtr.Zero) Marshal.Release(list);
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (name != IntPtr.Zero) WindowsDeleteString(name);
            if (init >= 0) RoUninitialize();
        }
    }

    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 10000 };
    int wifi = NotRead, bluetooth = NotRead, quiet = NotRead;
    int switching;      // the kind being switched, or 0
    bool busy;
    string problem;     // shown under the pill that could not be switched
    int problemKind;
    Font nameFont, stateFont;
    float fontsFor = -1f;

    public NothingToggles(Section settings) : base(settings, false)
    {
        poll.Tick += delegate { if (!IsCovered()) Refresh(0, false); };
    }

    protected override bool SquareResize { get { return true; } }

    // No card: nearly transparent, so the gap between the pills still takes the mouse.
    protected override Color CardBackground(Palette pal) { return Color.FromArgb(1, 0, 0, 0); }

    protected override void OnStart()
    {
        poll.Start();
        Refresh(0, false);
    }

    void Refresh(int switchKind, bool on)
    {
        if (busy) return;
        busy = true;
        switching = switchKind;
        if (switchKind != 0) { problem = null; Render(); }
        Background(() => Visit(switchKind, on), (radios, error) =>
        {
            busy = false;
            switching = 0;
            if (error != null || radios == null)
            {
                if (switchKind != 0) { problem = "Couldn't switch"; problemKind = switchKind; }
                else if (wifi == NotRead) { wifi = NotFound; bluetooth = NotFound; quiet = NotFound; }
            }
            else
            {
                bool wanted = on ? radios.Quiet == StateOn : radios.Quiet == StateOff;
                wifi = radios.Wifi;
                bluetooth = radios.Bluetooth;
                quiet = radios.Quiet;
                if (radios.Failed || (switchKind == KindQuiet && !wanted)) { problem = "Couldn't switch"; problemKind = switchKind; }
                else if (switchKind != 0 && radios.Access != AccessAllowed) { problem = "Blocked by Windows"; problemKind = switchKind; }
            }
            Render();
        });
    }

    static bool CanSwitch(int state) { return state == StateOn || state == StateOff; }

    int StateOf(int kind) { return kind == KindWifi ? wifi : kind == KindBluetooth ? bluetooth : quiet; }

    void Flip(int kind)
    {
        int state = StateOf(kind);
        if (CanSwitch(state)) Refresh(kind, state != StateOn);
    }

    // ---------- layout: three pills of the same height, stacked ----------
    static readonly int[] Order = { KindWifi, KindBluetooth, KindQuiet };
    float Side { get { return Math.Min(W, H); } }
    float Gap { get { return Side * 0.055f; } }
    float PillHeight { get { return (Side - Gap * 2) / 3; } }

    protected override void OnWidgetClick(Point p)
    {
        if (p.X > Side) return;
        for (int i = 0; i < Order.Length; i++)
        {
            float top = i * (PillHeight + Gap);
            if (p.Y >= top && p.Y < top + PillHeight) { Flip(Order[i]); return; }
        }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        if (CanSwitch(wifi)) menu.Add(wifi == StateOn ? "Turn Wi-Fi Off" : "Turn Wi-Fi On", delegate { Flip(KindWifi); });
        if (CanSwitch(bluetooth)) menu.Add(bluetooth == StateOn ? "Turn Bluetooth Off" : "Turn Bluetooth On", delegate { Flip(KindBluetooth); });
        if (CanSwitch(quiet)) menu.Add(quiet == StateOn ? "Turn Do Not Disturb Off" : "Turn Do Not Disturb On", delegate { Flip(KindQuiet); });
        menu.Add("Refresh Now", delegate { Refresh(0, false); });
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        float s = Side;
        EnsureFonts(s);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        bool preview = !IsHandleCreated;
        float step = PillHeight + Gap;
        DrawPill(g, 0, KindWifi, "Wi-Fi", preview ? StateOn : wifi);
        DrawPill(g, step, KindBluetooth, "Bluetooth", preview ? StateOff : bluetooth);
        DrawPill(g, step * 2, KindQuiet, "Do not disturb", preview ? StateOn : quiet);
    }

    void DrawPill(Graphics g, float top, int kind, string name, int state)
    {
        float s = Side, h = PillHeight;
        bool vibrant = IsVibrant, on = state == StateOn, usable = CanSwitch(state);
        float inset = vibrant ? 1.5f : 0.5f;
        var rect = new RectangleF(inset, top + inset, s - inset * 2, h - inset * 2);

        // On: the pill is filled in the appearance's strong colour. Off: it sinks back.
        Color fill, ink, soft;
        if (vibrant) { fill = Color.Empty; ink = on ? Color.White : Color.FromArgb(170, 255, 255, 255); soft = Color.FromArgb(on ? 190 : 120, 255, 255, 255); }
        else if (IsDark) { fill = on ? Paper : Dark; ink = on ? Dark : DarkSoft; soft = on ? Rgb(0x6C7076) : Rgb(0x66666B); }
        else { fill = on ? Dark : Light; ink = on ? Color.White : LightSoft; soft = on ? DarkSoft : Rgb(0xB4B4BA); }
        // Do not disturb is the one that silences things: red while it is on.
        if (on && kind == KindQuiet && !vibrant) { fill = SignalRed; ink = Color.White; soft = Color.FromArgb(205, 255, 255, 255); }
        if (!usable && !vibrant) ink = soft;

        using (var shape = RoundRect(rect, rect.Height / 2))
        {
            if (vibrant)
            {
                using (var glass = new TextureBrush(frost)) g.FillPath(glass, shape);
                if (on) using (var lift = new SolidBrush(Color.FromArgb(46, 255, 255, 255))) g.FillPath(lift, shape);
                using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f)) g.DrawPath(edge, shape);
            }
            else using (var b = new SolidBrush(fill)) g.FillPath(b, shape);
        }

        // Icon on a 24-unit grid.
        float k = h * 0.5f / 24f, ox = rect.X + s * 0.1f, oy = rect.Y + (rect.Height - 24 * k) / 2;
        using (var pen = new Pen(ink, 1.7f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (kind == KindWifi)
            {
                float cx = ox + 12 * k, cy = oy + 19.5f * k;
                foreach (float r in new[] { 4.2f, 8.6f, 13f })
                    g.DrawArc(pen, cx - r * k, cy - r * k, r * 2 * k, r * 2 * k, 225, 90);
                FillCircle(g, ink, cx, cy, 1.2f * k);
            }
            else if (kind == KindQuiet)
            {
                g.DrawEllipse(pen, ox + 3.5f * k, oy + 3.5f * k, 17 * k, 17 * k);
                g.DrawLine(pen, ox + 8 * k, oy + 12 * k, ox + 16 * k, oy + 12 * k);
            }
            else
            {
                g.DrawLines(pen, new[]
                {
                    new PointF(ox + 7 * k, oy + 7.5f * k), new PointF(ox + 17 * k, oy + 16.5f * k), new PointF(ox + 12 * k, oy + 21 * k),
                    new PointF(ox + 12 * k, oy + 3 * k), new PointF(ox + 17 * k, oy + 7.5f * k), new PointF(ox + 7 * k, oy + 16.5f * k)
                });
            }
        }

        // Name, and under it the state.
        string text = switching == kind ? (on ? "Turning off…" : "Turning on…")
            : problem != null && problemKind == kind ? problem
            : state == StateOn ? "On" : state == StateOff ? "Off" : state == NotRead ? "…" : state == NotFound ? "Not available" : "Switched off by Windows";
        float x = ox + 24 * k + s * 0.06f, width = rect.Right - s * 0.08f - x;
        float nameH = nameFont.GetHeight(g), stateH = stateFont.GetHeight(g), y = rect.Y + (rect.Height - nameH - stateH) / 2;
        Label(g, name, nameFont, ink, x, y, width, StringAlignment.Near);
        Label(g, text, stateFont, soft, x, y + nameH, width, StringAlignment.Near);
    }

    void EnsureFonts(float s)
    {
        if (nameFont != null && Math.Abs(s - fontsFor) < 0.01f) return;
        DisposeFonts();
        nameFont = new Font("Segoe UI", Math.Max(1f, s * 0.074f), FontStyle.Regular, GraphicsUnit.Pixel);
        stateFont = new Font("Segoe UI", Math.Max(1f, s * 0.056f), FontStyle.Regular, GraphicsUnit.Pixel);
        fontsFor = s;
    }

    void DisposeFonts()
    {
        if (nameFont != null) { nameFont.Dispose(); nameFont = null; }
        if (stateFont != null) { stateFont.Dispose(); stateFont = null; }
    }

    // ---------- vibrant: frosted glass in the widget's own shapes ----------
    // The app paints its frosted wallpaper as a rounded card. That card is rendered off-screen
    // (once per position) and then used as the paint for the pills, so the glass follows the
    // shapes and their edges stay smooth.
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
            DisposeFonts();
            if (frost != null) { frost.Dispose(); frost = null; }
        }
        base.Dispose(disposing);
    }
}
