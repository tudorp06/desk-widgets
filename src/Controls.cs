using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeskWidgets
{
    static class Ui
    {
        static int Px(float v) { return WidgetWindow.Px(v); }

        // Runs on the UI thread once the current window message is done (works before Application.Run too).
        public static Timer Later(Action action, int delay = 1)
        {
            var t = new Timer { Interval = delay };
            t.Tick += delegate { t.Dispose(); action(); };
            t.Start();
            return t;
        }
    }

    enum ButtonStyle { Primary, Grey, Tinted, Destructive }

    // Apple's capsule buttons (§8.3): Primary is the one blue-filled button per group; Grey and
    // Tinted are the quiet ones; Destructive is a grey capsule with red text.
    sealed class PillButton : Button
    {
        public ButtonStyle Style;
        bool hover, pressed;

        public PillButton(string text, ButtonStyle style)
        {
            Text = text;
            Style = style;
            Font = Theme.Button;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            int w = TextRenderer.MeasureText(text, Font).Width + WidgetWindow.Px(32);
            Size = new Size(Math.Max(WidgetWindow.Px(76), w), WidgetWindow.Px(32));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.WindowBg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, text;
            switch (Style)
            {
                case ButtonStyle.Primary: fill = Theme.PrimaryFill; text = Theme.OnPrimary; break;
                case ButtonStyle.Tinted: fill = Theme.Tinted; text = Theme.Link; break;
                case ButtonStyle.Destructive: fill = Theme.Fill; text = Theme.Destructive; break;
                default: fill = Theme.Fill; text = Theme.Label; break;
            }
            Theme.FillRound(g, fill, r, r.Height / 2);
            if (hover || pressed) // brightness up on hover, down when pressed
                Theme.FillRound(g, pressed ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(Style == ButtonStyle.Primary ? 28 : 14, 255, 255, 255), r, r.Height / 2);
            if (Focused && ShowFocusCues)
                using (var path = Theme.RoundRect(new RectangleF(1, 1, Width - 3, Height - 3), (Height - 3) / 2f))
                using (var pen = new Pen(Color.FromArgb(153, Theme.Link), 2f))
                    g.DrawPath(pen, path);
            if (!Enabled) text = Color.FromArgb(110, text);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, Color.Transparent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            int w = TextRenderer.MeasureText(Text, Font).Width + WidgetWindow.Px(32);
            Width = Math.Max(WidgetWindow.Px(76), w);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // A scroll panel with smooth mouse-wheel interpolation: intercepts WM_MOUSEWHEEL and eases
    // toward the target position on a critically damped spring instead of jumping. Programmatic
    // scrolls ease the same way. Used for the library window's content area.
    //
    // All content lives inside an inner viewport panel. Scrolling moves that single panel instead of
    // repositioning every child control individually, so each frame is O(1) instead of O(n).
    //
    // What keeps it smooth, each learned the hard way:
    //  - One step per screen refresh. A WinForms Timer fires every 10-16 ms regardless of when the
    //    screen is drawn, so some refreshes got two steps and some none. A background thread waits
    //    for the compositor (DwmFlush) and posts one step per frame; it only lives while scrolling.
    //  - Time comes from a Stopwatch. Environment.TickCount moves in ~16 ms jumps, so the measured
    //    frame time was 0, 16 or 31 ms more or less at random and the speed wobbled with it.
    //  - The spring is solved exactly for the time that passed, so a late frame lands where it
    //    should have instead of overshooting.
    //  - What a step uncovers is painted in that same step. Left to WM_PAINT it arrives a frame
    //    or two late, and the edge of the page flickers empty.
    sealed class SmoothPanel : Panel
    {
        internal readonly Panel viewport;
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        float offset;
        float target;
        float velocity;
        bool animating;
        long lastFrame;      // clock ticks at the previous step
        volatile int run;    // which frame thread is the current one; any other exits
        int framePosted;     // 1 while a step is waiting on the UI thread, so they never pile up
        int contentHeight;

        const float Omega = 16f; // the spring's natural frequency; critically damped

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmFlush();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
        const uint RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;

        public SmoothPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            viewport = new Panel();
            base.Controls.Add(viewport);
        }

        public new ControlCollection Controls { get { return viewport.Controls; } }
        public new void SuspendLayout() { viewport.SuspendLayout(); }
        public new void ResumeLayout() { viewport.ResumeLayout(); }

        public int ContentHeight
        {
            get { return contentHeight; }
            set
            {
                contentHeight = value;
                viewport.Size = new Size(ClientSize.Width, Math.Max(value, ClientSize.Height));
                Clamp();
            }
        }

        float MaxScroll
        {
            get
            {
                int max = contentHeight - ClientSize.Height;
                return max > 0 ? max : 0;
            }
        }

        void Clamp()
        {
            target = Math.Max(0, Math.Min(target, MaxScroll));
            offset = Math.Max(0, Math.Min(offset, MaxScroll));
            viewport.Top = -(int)Math.Round(offset);
        }

        // Moves the page to offset and paints what that uncovered, now.
        void Apply()
        {
            int top = -(int)Math.Round(offset);
            if (viewport.Top == top) return;
            viewport.Top = top;
            if (IsHandleCreated) RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_UPDATENOW | RDW_ALLCHILDREN);
        }

        // Starts stepping towards target, or carries on if it already is.
        void Animate()
        {
            if (!IsHandleCreated) { offset = target; velocity = 0; Clamp(); return; } // nothing on screen to ease
            if (animating) return;
            animating = true;
            clock.Restart();
            lastFrame = 0;
            int mine = ++run;
            MethodInvoker step = delegate
            {
                System.Threading.Interlocked.Exchange(ref framePosted, 0);
                if (mine == run && !IsDisposed) Step();
            };
            var frames = new System.Threading.Thread(delegate()
            {
                while (mine == run)
                {
                    // Returns at the next screen refresh. Without a compositor to wait for, pace it by hand.
                    if (DwmFlush() != 0) System.Threading.Thread.Sleep(8);
                    if (mine != run) break;
                    if (System.Threading.Interlocked.CompareExchange(ref framePosted, 1, 0) != 0) continue;
                    try { BeginInvoke(step); }
                    catch (InvalidOperationException) { break; } // the window closed mid-scroll
                }
            });
            frames.IsBackground = true;
            frames.Start();
        }

        void StopAnimating()
        {
            animating = false;
            run++;
        }

        void Step()
        {
            long now = clock.ElapsedTicks;
            float dt = (now - lastFrame) / (float)System.Diagnostics.Stopwatch.Frequency;
            lastFrame = now;
            if (dt <= 0) return;
            if (dt > 0.05f) dt = 0.05f; // the app was busy: don't leap

            target = Math.Min(target, MaxScroll);

            // A critically damped spring after dt, exactly: x(t) = (x + (v + wx)t) e^-wt.
            float x = offset - target;
            float k = velocity + Omega * x;
            float decay = (float)Math.Exp(-Omega * dt);
            float next = (x + k * dt) * decay;
            velocity = (velocity - Omega * k * dt) * decay;

            if (Math.Abs(next) < 0.5f && Math.Abs(velocity) < 10f)
            {
                offset = target;
                velocity = 0;
                StopAnimating();
            }
            else
                offset = Math.Max(0, Math.Min(target + next, MaxScroll));
            Apply();
        }

        public void ScrollTo(float position)
        {
            position = Math.Max(0, Math.Min(position, MaxScroll));
            if (Math.Abs(offset - position) < 1f) return;
            target = position;
            if (!animating) velocity = 0;
            Animate();
        }

        public void ScrollToTop() { ScrollTo(0); }

        Anim slideAnim;

        public void SlideIn()
        {
            if (slideAnim != null) slideAnim.Cancel();
            int dist = WidgetWindow.Px(20);
            viewport.Left = dist;
            slideAnim = Anim.Run(dist, 0, 100, delegate(float v) { viewport.Left = (int)v; }, delegate { slideAnim = null; });
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)(m.WParam.ToInt64() >> 16);
                float step = -(delta / 120f) * WidgetWindow.Px(60);
                if (!animating) { target = offset; velocity = 0; }
                target = Math.Max(0, Math.Min(target + step, MaxScroll));
                if (Math.Abs(target - offset) >= 0.5f) Animate();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (viewport != null)
            {
                viewport.Width = ClientSize.Width;
                if (contentHeight > 0)
                    viewport.Height = Math.Max(contentHeight, ClientSize.Height);
                Clamp();
            }
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (viewport != null) viewport.BackColor = BackColor;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopAnimating(); // lets the frame thread go
            base.Dispose(disposing);
        }
    }

    // A rounded text field (§8.7): a borderless TextBox inside a painted 8 px-radius frame that
    // turns blue while focused.
    sealed class TextField : Control
    {
        public readonly TextBox Box;

        public TextField(string text, bool secret, bool multiline = false)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Box = new TextBox
            {
                Text = text ?? "", UseSystemPasswordChar = secret, BorderStyle = BorderStyle.None, Multiline = multiline,
                BackColor = Theme.Field, ForeColor = Theme.Label, Font = Theme.Body
            };
            Box.HandleCreated += delegate
            {
                if (placeholder != null) Native.SendMessageText(Box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, placeholder);
            };
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Height = multiline ? WidgetWindow.Px(96) : Box.PreferredHeight + WidgetWindow.Px(14);
            Cursor = Cursors.IBeam;
            // Tab goes straight to the text box; the frame itself never takes focus.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int px = WidgetWindow.Px(10), py = Box.Multiline ? WidgetWindow.Px(8) : (Height - Box.PreferredHeight) / 2;
            Box.SetBounds(px, py, Width - px * 2, Box.Multiline ? Height - py * 2 : Box.PreferredHeight);
        }

        // Shown in grey while the box is empty. Windows draws it, so it is never real text.
        public string Placeholder
        {
            set
            {
                placeholder = value;
                if (Box.IsHandleCreated)
                    Native.SendMessageText(Box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, placeholder);
            }
        }
        string placeholder;

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.WindowBg);
            var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
            float radius = WidgetWindow.Px(Theme.RadiusSmall);
            Theme.FillRound(g, Theme.Field, r, radius);
            using (var path = Theme.RoundRect(r, radius))
            using (var pen = new Pen(Box.Focused ? Theme.Link : Theme.FieldBorder, Box.Focused ? 2f : 1f))
                g.DrawPath(pen, path);
        }
    }
}
