// Claude Widget - one-file Desk Widgets community widget.
// Install through Desk Widgets > Installed > Install from File.
// Compatible with Desk Widgets' C# 5 / .NET Framework compiler.
// Uses an installed Brave (preferred) or Edge Chromium browser in app mode.
// NO companion exe, WebView2 NuGet package, build script, or API key required.
// Clicking the small card opens a 930x560 floating Claude browser window.
// A tiny collapse tab appears in the browser's upper-right corner.
// Clicking that tab (or the original card again) closes the floating view
// and animates back to the desktop card. Drag any corner to resize the card.
//
// Limitation: this launches a real browser app window, not an embedded WebView.
// Chromium's browser content appears only *after* the expansion animation;
// it is hidden before collapse, so the transition stays smooth.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DeskWidgets;

[DeskWidget(Name = "Claude", Description = "Claude in a smooth, expandable floating browser window")]
public sealed class ClaudeWidgetSingle : WidgetWindow
{
    private ClaudeFloatWindow popup;
    private bool preferBrave = true;

    public ClaudeWidgetSingle(Section settings) : base(settings) { }

    protected override Size Measure()
    {
        int width = Settings.GetInt("custom_w", 0);
        int height = Settings.GetInt("custom_h", 0);
        return width > 0 && height > 0 ? new Size(width, height) : new Size(Px(170), Px(170));
    }
    // The arrow is a button; the rest of the card keeps WidgetWindow's drag handling.
    protected override bool IsButton(Point p)
    {
        Size size = Measure();
        float side = Math.Min(size.Width, size.Height);
        float scale = side / Px(170);
        float x = (p.X - (size.Width - side) / 2f) / scale;
        float y = (p.Y - (size.Height - side) / 2f) / scale;
        return ExpandButtonBounds.Contains(x, y);
    }

    private static RectangleF ExpandButtonBounds
    {
        get { return new RectangleF(Px(122), Px(122), Px(32), Px(32)); }
    }

    protected override void OnWidgetClick(Point p) { ToggleClaude(); }

    protected override void BuildMenu(PopupMenu menu)
    {
        menu.Add("Expand / Collapse Claude", delegate { ToggleClaude(); });
        menu.Add("Use Brave (preferred)", delegate { preferBrave = true; });
        menu.Add("Use Microsoft Edge", delegate { preferBrave = false; });
        menu.Add("Open in normal browser", delegate { OpenInBrowser(); });
    }

    private static void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://claude.ai/new") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Claude", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleClaude()
    {
        if (IsDisposed || Disposing) return;
        if (popup != null && !popup.IsDisposed)
        {
            popup.Collapse();
            return;
        }

        Rectangle small = new Rectangle(Left, Top, Width, Height);
        Bitmap image = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        using (Graphics g = Graphics.FromImage(image)) Draw(g);
        bool restoreCard = Visible;
        popup = new ClaudeFloatWindow(small, preferBrave, image);
        popup.FormClosed += delegate
        {
            popup = null;
            if (restoreCard && !IsDisposed && !Disposing)
            {
                Show();
                Render();
            }
        };
        popup.Show();
        popup.Update();
        if (restoreCard) Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClaudeFloatWindow current = popup;
            popup = null;
            if (current != null) current.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void Draw(Graphics g)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Size size = Measure();
        float side = Math.Min(size.Width, size.Height);
        float scale = side / Px(170);
        using (GraphicsPath p = RoundRect(new RectangleF(0, 0, size.Width - 1, size.Height - 1), Px(22) * scale))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(28, 28, 30)))
            g.FillPath(brush, p);

        GraphicsState state = g.Save();
        try
        {
            // Keep the artwork proportional and centered in rectangular cards.
            g.TranslateTransform((size.Width - side) / 2f, (size.Height - side) / 2f);
            g.ScaleTransform(scale, scale);
            DrawContent(g);
        }
        finally { g.Restore(state); }
    }

    private void DrawContent(Graphics g)
    {
        DrawMark(g, Px(33), Px(36), Px(17));

        using (Font font = new Font("Segoe UI Semibold", Px(20), GraphicsUnit.Pixel))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(248, 246, 242)))
            g.DrawString("Claude", font, b, Px(16), Px(73));

        using (Font font = new Font("Segoe UI", Px(12), GraphicsUnit.Pixel))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(154, 151, 147)))
            g.DrawString("Your floating workspace", font, b,
                new RectangleF(Px(16), Px(105), Px(147), Px(31)));

        using (SolidBrush b = new SolidBrush(Color.FromArgb(51, 51, 54)))
            g.FillEllipse(b, ExpandButtonBounds);

        // Two opposing corners convey expansion without a font-dependent arrow.
        RectangleF button = ExpandButtonBounds;
        float cx = button.Left + button.Width / 2f, cy = button.Top + button.Height / 2f;
        float d = 5.5f * Dpi, gap = 1.5f * Dpi;
        using (Pen pen = new Pen(Color.FromArgb(248, 246, 242), 1.5f * Dpi))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new PointF[] { new PointF(cx, cy - d),
                new PointF(cx + d, cy - d), new PointF(cx + d, cy) });
            g.DrawLine(pen, cx + gap, cy - gap, cx + d, cy - d);
            g.DrawLines(pen, new PointF[] { new PointF(cx - d, cy),
                new PointF(cx - d, cy + d), new PointF(cx, cy + d) });
            g.DrawLine(pen, cx - gap, cy + gap, cx - d, cy + d);
        }
    }

    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void DrawMark(Graphics g, float cx, float cy, float radius, float opacity = 1f)
    {
        using (Pen pen = new Pen(Color.FromArgb((int)(255 * opacity), 216, 135, 104), Math.Max(2f, radius / 5f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            for (int i = 0; i < 12; i++)
            {
                double a = Math.PI * i / 6d;
                g.DrawLine(pen,
                    cx + (float)Math.Cos(a) * radius * .23f,
                    cy + (float)Math.Sin(a) * radius * .23f,
                    cx + (float)Math.Cos(a) * radius,
                    cy + (float)Math.Sin(a) * radius);
            }
        }
    }
}

internal sealed class ClaudeFloatWindow : Form
{
    private static readonly Color Back = Color.FromArgb(26, 26, 27);
    private static readonly Color TextColor = Color.FromArgb(247, 244, 240);
    private static readonly Color Secondary = Color.FromArgb(169, 166, 162);

    private readonly Rectangle compact;
    private readonly Rectangle expanded;
    private readonly bool preferBrave;
    private Rectangle start;
    private Rectangle finish;
    private readonly Timer animation = new Timer();
    private readonly Timer finder = new Timer();
    private readonly Stopwatch watch = new Stopwatch();
    private readonly Stopwatch findWatch = new Stopwatch();
    private bool collapsing;
    private bool active;
    private bool launching;
    private const double DurationMs = 380;
    private string processName;
    private IntPtr browserHwnd = IntPtr.Zero;
    private HashSet<IntPtr> knownWindows;
    private ClaudeCollapseTab collapseTab;
    private Bitmap cardImage;
    private string message = "Opening Claude";

    // Native Chromium app windows have the standard Chrome_WidgetWin_1 class.
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint WM_CLOSE = 0x0010;

    public ClaudeFloatWindow(Rectangle source, bool brave, Bitmap image = null)
    {
        compact = source;
        cardImage = image;
        preferBrave = brave;
        Rectangle work = Screen.FromRectangle(source).WorkingArea;
        int width = Math.Min(930, work.Width - 24);
        int height = Math.Min(560, work.Height - 24);
        expanded = new Rectangle(
            work.Left + (work.Width - width) / 2,
            work.Top + (work.Height - height) / 2,
            width, height);

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ControlBox = false;
        MinimumSize = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Back;
        DoubleBuffered = true;
        Bounds = compact;
        KeyPreview = true;

        animation.Interval = 15;
        animation.Tick += AnimationTick;
        finder.Interval = 90;
        finder.Tick += FinderTick;
        Shown += delegate { if (!collapsing) BeginTween(expanded, false); };
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Collapse();
        };
        Resize += delegate { SetRoundRegion(); };
        SetRoundRegion();
    }

    private void BeginTween(Rectangle goal, bool reverse)
    {
        if (IsDisposed) return;
        animation.Stop();
        finder.Stop();
        collapsing = reverse;
        start = Bounds;
        finish = goal;
        watch.Restart();
        Show();
        BringToFront();
        animation.Start();
    }

    private void AnimationTick(object sender, EventArgs e)
    {
        double t = Math.Min(1d, watch.Elapsed.TotalMilliseconds / DurationMs);
        // Quintic ease in/out: gradual start + gradual stop, reversible.
        double eased = t < .5d ? 16d * Math.Pow(t, 5)
                               : 1d - Math.Pow(-2d * t + 2d, 5) / 2d;
        Bounds = new Rectangle(
            Lerp(start.X, finish.X, eased),
            Lerp(start.Y, finish.Y, eased),
            Math.Max(16, Lerp(start.Width, finish.Width, eased)),
            Math.Max(16, Lerp(start.Height, finish.Height, eased)));
        Invalidate();
        if (t < 1d) return;

        animation.Stop();
        watch.Stop();
        Bounds = finish;
        if (collapsing)
        {
            Close();
        }
        else if (!launching)
        {
            LaunchClaude();
        }
    }

    private static int Lerp(int a, int b, double t)
    {
        return (int)Math.Round(a + (b - a) * t);
    }

    private void LaunchClaude()
    {
        launching = true;
        string exe;
        string name;
        if (!TryFindBrowser(preferBrave, out exe, out name))
        {
            message = "Install Brave or Edge to open Claude.";
            Invalidate();
            return;
        }

        processName = name;
        knownWindows = SnapshotWindows();
        string args = "--app=https://claude.ai/new --new-window --window-size="
            + expanded.Width + "," + expanded.Height
            + " --window-position=" + expanded.Left + "," + expanded.Top;
        try
        {
            // Existing signed-in browser profile is reused (separate login not required).
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            message = "Connecting to Claude...";
            Invalidate();
            findWatch.Restart();
            finder.Start();
        }
        catch (Exception ex)
        {
            message = "Browser failed to launch: " + ex.Message;
            Invalidate();
        }
    }

    private void FinderTick(object sender, EventArgs e)
    {
        IntPtr found = FindNewBrowserWindow();
        if (found != IntPtr.Zero)
        {
            finder.Stop();
            findWatch.Stop();
            browserHwnd = found;
            Native.SetWindowPos(browserHwnd, IntPtr.Zero, expanded.Left,
                expanded.Top, expanded.Width, expanded.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            active = true;
            collapseTab = new ClaudeCollapseTab(this, browserHwnd);
            Hide();
            collapseTab.Show();
            collapseTab.TrackPosition();
            Native.SetForegroundWindow(browserHwnd);
            return;
        }
        if (findWatch.Elapsed.TotalSeconds > 12)
        {
            finder.Stop();
            message = "Claude opened, but its window could not be attached.\n"
                + "Click the small widget again to collapse.\n"
                + "You can also press Esc here.";
            Invalidate();
        }
    }

    public void Collapse()
    {
        if (IsDisposed || collapsing) return;
        // A pending browser launch may still complete; stop waiting for it.
        finder.Stop();
        if (collapseTab != null && !collapseTab.IsDisposed)
        {
            collapseTab.Hide();
            collapseTab.Close();
            collapseTab = null;
        }
        Rectangle collapseFrom = Bounds;
        Native.RECT browserBounds;
        if (browserHwnd != IntPtr.Zero && Native.GetWindowRect(browserHwnd, out browserBounds))
            collapseFrom = Rectangle.FromLTRB(browserBounds.Left, browserBounds.Top, browserBounds.Right, browserBounds.Bottom);
        bool browserWasVisible = CloseBrowser();
        launching = true;
        // During an unfinished expansion, reverse from the *current* rectangle.
        // When the browser was visible, start at the full-sized rectangle.
        if (browserWasVisible) Bounds = collapseFrom;
        message = "Claude";
        BeginTween(compact, true);
    }

    private bool CloseBrowser()
    {
        IntPtr browser = browserHwnd;
        browserHwnd = IntPtr.Zero;
        active = false;
        if (browser == IntPtr.Zero || !Native.IsWindow(browser)) return false;
        // Close just the attached window, never the whole browser process.
        Native.ShowWindow(browser, SW_HIDE);
        Native.PostMessage(browser, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            // Widget removal must clean up immediately, even during an animation
            // or while the shell is hidden behind the browser.
            collapsing = true;
            launching = true;
            animation.Stop();
            finder.Stop();
            animation.Dispose();
            finder.Dispose();
            watch.Stop();
            findWatch.Stop();
            ClaudeCollapseTab tab = collapseTab;
            collapseTab = null;
            if (tab != null) tab.Dispose();
            CloseBrowser();
            if (cardImage != null) { cardImage.Dispose(); cardImage = null; }
        }
        base.Dispose(disposing);
    }

    private static bool TryFindBrowser(bool brave, out string exe, out string name)
    {
        string[] browserOrder = brave ? new string[] { "brave", "msedge" }
                                     : new string[] { "msedge", "brave" };
        foreach (string key in browserOrder)
        {
            string root = key == "brave" ? "BraveSoftware\\Brave-Browser\\Application\\brave.exe"
                                           : "Microsoft\\Edge\\Application\\msedge.exe";
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), root),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), root),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root)
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    exe = path;
                    name = key;
                    return true;
                }
            }
        }
        exe = null;
        name = null;
        return false;
    }

    private HashSet<IntPtr> SnapshotWindows()
    {
        HashSet<IntPtr> windows = new HashSet<IntPtr>();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr extra)
        {
            if (IsChromiumWindow(hwnd)) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private IntPtr FindNewBrowserWindow()
    {
        IntPtr result = IntPtr.Zero;
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr extra)
        {
            if (!knownWindows.Contains(hwnd) && IsChromiumWindow(hwnd))
            {
                result = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private bool IsChromiumWindow(IntPtr hwnd)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;
        StringBuilder buf = new StringBuilder(128);
        Native.GetClassName(hwnd, buf, buf.Capacity);
        if (buf.ToString() != "Chrome_WidgetWin_1") return false;
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return false;
        try
        {
            string name = Process.GetProcessById((int)pid).ProcessName;
            return string.Equals(name, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.Clear(Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float reveal = cardImage == null ? 1f : Math.Min(1f, ExpansionProgress() * 2f);
        if (cardImage != null && reveal < 1f)
        {
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix matrix = new ColorMatrix();
                matrix.Matrix33 = 1f - reveal;
                attributes.SetColorMatrix(matrix);
                g.DrawImage(cardImage, ClientRectangle, 0, 0, cardImage.Width, cardImage.Height,
                    GraphicsUnit.Pixel, attributes);
            }
        }
        if (reveal <= 0f) return;
        int min = Math.Min(ClientSize.Width, ClientSize.Height);
        int r = Math.Max(15, Math.Min(34, min / 6));
        float centerY = Height * .42f;
        ClaudeWidgetSingle.DrawMark(g, Width / 2f - 48f, centerY, r / 1.25f, reveal);
        using (Font f = new Font("Segoe UI Semibold", Math.Max(14f, Math.Min(27f, min / 10f)), GraphicsUnit.Pixel))
        using (Brush b = new SolidBrush(Color.FromArgb((int)(255 * reveal), TextColor)))
            g.DrawString("Claude", f, b, Width / 2f - 27f, centerY - 14f);

        if (min > 200)
        {
            using (Font f = new Font("Segoe UI", 13, GraphicsUnit.Pixel))
            using (Brush b = new SolidBrush(Color.FromArgb((int)(255 * reveal), Secondary)))
            {
                SizeF size = g.MeasureString(message, f);
                g.DrawString(message, f, b, Math.Max(12f, (Width - size.Width) / 2f), Height * .58f);
            }
        }
    }

    private float ExpansionProgress()
    {
        int distance = expanded.Width - compact.Width + expanded.Height - compact.Height;
        if (distance == 0) return 0f;
        return Math.Max(0f, Math.Min(1f,
            (Width - compact.Width + Height - compact.Height) / (float)distance));
    }

    private void SetRoundRegion()
    {
        if (Width < 6 || Height < 6) return;
        float progress = ExpansionProgress();
        float cardRadius = Math.Min(compact.Width, compact.Height) * 22f / 170f;
        int radius = Math.Max(1, (int)Math.Round(cardRadius + (22 - cardRadius) * progress));
        using (GraphicsPath p = ClaudeWidgetSingle.RoundRect(
            new RectangleF(0, 0, Width, Height), radius))
        {
            Region old = Region;
            Region = new Region(p);
            if (old != null) old.Dispose();
        }
    }

    // Is the browser still open? Called by the collapse-tab tracker.
    internal bool BrowserIsAlive()
    {
        return !IsDisposed && active && browserHwnd != IntPtr.Zero && Native.IsWindow(browserHwnd);
    }

    internal void BrowserClosed()
    {
        if (!IsDisposed && !collapsing) Collapse();
    }
}

// This is a small floating COLLAPSE button, not a separate executable.
// It sits in the title bar, beside the browser's own window controls.
internal sealed class ClaudeCollapseTab : Form
{
    private readonly ClaudeFloatWindow owner;
    private readonly IntPtr browser;
    private readonly Timer follow = new Timer();
    private readonly Button button = new CollapseButton();
    private readonly ToolTip tooltip = new ToolTip();
    private const uint GA_ROOT = 2;

    public ClaudeCollapseTab(ClaudeFloatWindow owner, IntPtr browser)
    {
        this.owner = owner;
        this.browser = browser;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(1, 1);
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(42, 41, 41);
        Size = new Size(24, 24);
        button.Dock = DockStyle.Fill;
        button.AccessibleName = "Collapse Claude";
        button.ForeColor = Color.White;
        button.BackColor = Color.FromArgb(52, 51, 51);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        button.Click += delegate { owner.Collapse(); };
        Controls.Add(button);
        tooltip.SetToolTip(button, "Collapse Claude to desktop widget");
        follow.Interval = 200;
        follow.Tick += delegate { TrackPosition(); };
        Shown += delegate { TrackPosition(); follow.Start(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void SetVisibleCore(bool value)
    {
        // Set the final bounds before Windows can paint the first visible frame.
        if (value && !UpdatePosition()) return;
        IntPtr foreground = value ? Native.GetForegroundWindow() : IntPtr.Zero;
        base.SetVisibleCore(value);
        // WinForms can activate a newly shown form despite its native style.
        if (value && foreground != IntPtr.Zero && Native.GetForegroundWindow() == Handle)
            Native.SetForegroundWindow(foreground);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE: keep typing focus in Claude.
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            follow.Stop();
            follow.Dispose();
            tooltip.Dispose();
        }
        base.Dispose(disposing);
    }

    public void TrackPosition()
    {
        if (!owner.BrowserIsAlive())
        {
            owner.BrowserClosed();
            return;
        }
        if (!UpdatePosition()) { Hide(); return; }
        // Hide the tab when some other application has focus.
        IntPtr fg = Native.GetForegroundWindow();
        bool foreground = Native.GetAncestor(fg, GA_ROOT) == browser || fg == Handle;
        if (foreground && !Visible) Show();
        else if (!foreground && Visible) Hide();
    }

    private bool UpdatePosition()
    {
        Native.RECT r;
        if (!Native.GetWindowRect(browser, out r) || Native.IsIconic(browser)) return false;
        float scale = WidgetWindow.Dpi;
        try
        {
            uint dpi = Native.GetDpiForWindow(browser);
            if (dpi > 0) scale = dpi / 96f;
        }
        catch (EntryPointNotFoundException) { }
        Native.RECT caption;
        Rectangle captionBounds = Rectangle.Empty;
        if (Native.DwmGetWindowAttribute(browser, 5, out caption, Marshal.SizeOf(typeof(Native.RECT))) == 0)
            captionBounds = Rectangle.FromLTRB(caption.Left, caption.Top, caption.Right, caption.Bottom);
        Bounds = ControlBounds(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), captionBounds, scale);
        return true;
    }

    internal static Rectangle ControlBounds(Rectangle window, Rectangle caption, float scale)
    {
        int gap = Math.Max(2, (int)Math.Round(4 * scale));
        if (caption.Width <= 0 || caption.Height <= 0 || caption.Left < 0
            || caption.Right > window.Width || caption.Bottom > window.Height)
        {
            // Chromium may not expose caption metrics while creating its window.
            caption = new Rectangle(window.Width - (int)Math.Round(138 * scale),
                (int)Math.Round(8 * scale), (int)Math.Round(138 * scale), (int)Math.Round(32 * scale));
        }
        int side = Math.Max(12, Math.Min((int)Math.Round(24 * scale), caption.Height - 2 * gap));
        return new Rectangle(window.Left + Math.Max(gap, caption.Left - gap - side),
            window.Top + caption.Top + (caption.Height - side) / 2, side, side);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 1 || Height < 1) return;
        using (GraphicsPath p = ClaudeWidgetSingle.RoundRect(new RectangleF(0, 0, Width, Height), Width / 4f))
        {
            Region old = Region;
            Region = new Region(p);
            if (old != null) old.Dispose();
        }
    }

    private sealed class CollapseButton : Button
    {
        public CollapseButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            g.Clear(hover ? Color.FromArgb(72, 71, 71) : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(Width, Height) / 24f;
            float cx = Width / 2f, cy = Height / 2f, d = 5 * scale;
            using (Pen pen = new Pen(ForeColor, Math.Max(1, 1.6f * scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
                g.DrawLines(pen, new PointF[] { new PointF(cx - d, cy - scale),
                    new PointF(cx - d, cy + d), new PointF(cx + scale, cy + d) });
            }
        }
    }
}

internal static class Native
{
    internal delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsCallback cb, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }
}
