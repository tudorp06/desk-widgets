using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace DeskWidgets
{
    // Arrange mode: a layer over the desktop where several widgets are picked at once and acted
    // on together -- moved as a block, themed, resized, locked or removed in one go.
    //
    // Why a mode and not a bare-desktop gesture: the empty desktop is Explorer's window. Drawing
    // a rubber band there would mean a global mouse hook racing Explorer's own selection
    // rectangle, so instead this is entered from a widget's menu or the tray and left with Esc.
    // Inside it every pixel and every click is ours.
    static class Arrange
    {
        static ArrangeOverlay overlay;

        public static bool Active { get { return overlay != null && !overlay.IsDisposed; } }

        public static void Enter() { Enter(null); }

        // 'first' is the widget whose menu started the mode; it begins selected.
        public static void Enter(WidgetWindow first)
        {
            if (App.Current == null) return;
            if (Active) { overlay.Activate(); return; }
            var o = new ArrangeOverlay();
            overlay = o;
            o.FormClosed += delegate { if (overlay == o) overlay = null; };
            if (first != null) o.Preselect(first);
            o.Show();
            o.Activate();
        }

        public static void Exit()
        {
            var o = overlay;
            overlay = null;
            if (o != null && !o.IsDisposed) o.Close();
        }

        // Widgets moved or changed under us (Tidy Up finishing, a size change): redraw the rings.
        public static void Refresh()
        {
            if (Active) overlay.Render();
        }

        // Opens arrange mode ready for the next drag to become the widget area (from Settings).
        public static void EnterAndSetArea()
        {
            Enter();
            if (Active) overlay.BeginSetArea();
        }
    }

    sealed class ArrangeOverlay : Form
    {
        static int Px(float v) { return WidgetWindow.Px(v); }

        readonly List<WidgetWindow> selection = new List<WidgetWindow>();
        readonly List<WidgetWindow> bandBase = new List<WidgetWindow>(); // selection before a Ctrl+band
        readonly Dictionary<WidgetWindow, Point> moveOrigin = new Dictionary<WidgetWindow, Point>();

        LayeredSurface surface;
        Rectangle area; // the whole virtual desktop, in screen coordinates
        byte alpha;
        Anim fade;

        bool banding;
        Point bandFrom, bandTo;
        // The next drag draws the widget area instead of picking widgets.
        bool settingArea;

        bool pressed, moving;
        bool pressWasSelected; // the widget under the press was already picked: a plain click drops it
        Point pressAt;
        WidgetWindow pressOn;

        readonly Timer focusWatch = new Timer { Interval = 250 };
        int shownAt;

        public ArrangeOverlay()
        {
            area = SystemInformation.VirtualScreen;
            Text = "Arrange Widgets";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Bounds = area;

            // The mode ends when the user goes somewhere else entirely. Checked on a timer rather
            // than on Deactivate, because our own menus and dialogs take focus too and must not
            // count: anything still inside this process is part of arranging.
            focusWatch.Tick += delegate
            {
                // A grace period: the overlay has to win the foreground first, and on a busy
                // machine that takes a moment. Leaving before it does would close the mode the
                // instant it opened.
                if (Environment.TickCount - shownAt < 750) return;
                if (!IsOurs(Native.GetForegroundWindow())) Arrange.Exit();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Layered for per-pixel alpha. Not topmost and not NOACTIVATE: it behaves like an
                // ordinary desktop window, so clicking another app simply covers it, and it can
                // take Escape. TOOLWINDOW keeps it out of Alt+Tab.
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        static bool IsOurs(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid == MyPid;
        }

        public void Preselect(WidgetWindow w)
        {
            selection.Clear();
            selection.Add(w);
        }

        public void BeginSetArea()
        {
            settingArea = true;
            selection.Clear();
            Render();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            shownAt = Environment.TickCount;
            surface = new LayeredSurface(area.Width, area.Height);
            Render();
            fade = Anim.Run(0f, 255f, 140, delegate(float v)
            {
                alpha = (byte)v;
                if (IsHandleCreated && !IsDisposed) Present();
            });
            focusWatch.Start();
        }

        // ---- drawing ----

        public void Render()
        {
            if (surface == null || IsDisposed) return;
            Graphics g = surface.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            // A scrim this faint is what the mode looks like -- and what makes it clickable: a
            // layered window passes the mouse through wherever its pixels are fully transparent,
            // so the scrim must cover every pixel. Nothing here is ever cleared to alpha 0.
            g.Clear(Color.FromArgb(15, 0, 0, 0));
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            DrawAreas(g);

            foreach (var w in Widgets())
                if (selection.Contains(w)) DrawRing(g, Local(new Rectangle(w.Location, w.WidgetSize)));

            if (banding) DrawBand(g, Local(BandRect()));

            DrawHint(g);
            Present();
        }

        void Present()
        {
            if (surface != null && IsHandleCreated && !IsDisposed) surface.Present(Handle, alpha);
        }

        Rectangle Local(Rectangle screenRect)
        {
            screenRect.Offset(-area.Left, -area.Top);
            return screenRect;
        }

        // The region widgets are arranged inside, as a dashed outline. Shown while it is being
        // drawn, and afterwards on any screen where one has been set -- a screen still using the
        // whole desktop has nothing worth outlining.
        void DrawAreas(Graphics g)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (!settingArea && !WidgetArea.IsCustom(screen)) continue;
                Rectangle r = Local(WidgetArea.For(screen));
                using (var pen = new Pen(Color.FromArgb(settingArea ? 190 : 110, Theme.PrimaryFill), Px(1.5f)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    pen.DashPattern = new[] { 6f, 5f };
                    using (var path = Theme.RoundRect(r, Px(14))) g.DrawPath(pen, path);
                }
            }
        }

        void DrawRing(Graphics g, Rectangle r)
        {
            var ring = r;
            ring.Inflate(Px(3), Px(3));
            using (var path = Theme.RoundRect(ring, Px(22)))
            {
                using (var glow = new Pen(Color.FromArgb(60, Theme.PrimaryFill), Px(5)))
                    g.DrawPath(glow, path);
                using (var pen = new Pen(Theme.PrimaryFill, Px(2)))
                    g.DrawPath(pen, path);
            }
        }

        void DrawBand(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var fill = new SolidBrush(Color.FromArgb(38, Theme.PrimaryFill)))
                g.FillRectangle(fill, r);
            using (var pen = new Pen(Color.FromArgb(200, Theme.PrimaryFill), 1f))
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        void DrawHint(Graphics g)
        {
            string text = settingArea
                ? "Drag the area widgets should stay inside · Esc to cancel"
                : selection.Count == 0
                ? "Drag to select · T to tidy · S to shuffle · Esc to finish"
                : selection.Count + (selection.Count == 1 ? " widget selected" : " widgets selected")
                  + " · right-click for options";

            Font f = Theme.Menu;
            SizeF size = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic);
            int padX = Px(16), h = Px(34);
            int w = (int)Math.Ceiling(size.Width) + padX * 2;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            var pill = Local(new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - h - Px(40), w, h));

            Theme.FillRound(g, Theme.MenuBg, pill, h / 2f);
            using (var edge = new Pen(Theme.MenuEdge, 1f))
            using (var path = Theme.RoundRect(pill, h / 2f))
                g.DrawPath(edge, path);
            using (var brush = new SolidBrush(Theme.Label))
            using (var fmt = new StringFormat(StringFormat.GenericTypographic)
                   { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, brush, pill, fmt);
        }

        // ---- widgets under the pointer ----

        IEnumerable<WidgetWindow> Widgets()
        {
            if (App.Current == null) return new WidgetWindow[0];
            return App.Current.OpenWidgets.Where(w => !w.IsDisposed && w.IsHandleCreated && w.Visible);
        }

        WidgetWindow HitTest(Point screen)
        {
            WidgetWindow found = null;
            foreach (var w in Widgets())
                if (new Rectangle(w.Location, w.WidgetSize).Contains(screen)) found = w; // last one wins
            return found;
        }

        Rectangle BandRect()
        {
            return Rectangle.FromLTRB(Math.Min(bandFrom.X, bandTo.X), Math.Min(bandFrom.Y, bandTo.Y),
                                      Math.Max(bandFrom.X, bandTo.X), Math.Max(bandFrom.Y, bandTo.Y));
        }

        // ---- mouse ----

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Point screen = PointToScreen(e.Location);
            if (e.Button != MouseButtons.Left) return;

            // While the area is being drawn, a press anywhere starts the rectangle -- widgets
            // under the pointer are part of the picture, not targets.
            if (settingArea)
            {
                pressOn = null;
                bandBase.Clear();
                banding = true;
                bandFrom = bandTo = screen;
                Render();
                return;
            }

            WidgetWindow hit = HitTest(screen);
            if (hit != null)
            {
                // Clicking widgets adds them up, the way picking files does: an unselected one
                // joins the selection on the way down (so it can be dragged straight away), and
                // one that was already in it drops out on the way up, if the click wasn't a drag.
                pressWasSelected = selection.Contains(hit);
                if (!pressWasSelected) { selection.Add(hit); Render(); }
                pressed = true;
                pressAt = screen;
                pressOn = hit;
                moveOrigin.Clear();
                foreach (var w in selection) moveOrigin[w] = w.Location;
                return;
            }

            pressOn = null;
            // A band adds to what is already picked; only a click on bare desktop clears.
            bandBase.Clear();
            bandBase.AddRange(selection);
            banding = true;
            bandFrom = bandTo = screen;
            Render();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point screen = PointToScreen(e.Location);

            if (banding && settingArea)
            {
                bandTo = screen;
                Render();
                return;
            }

            if (banding)
            {
                bandTo = screen;
                Rectangle band = BandRect();
                selection.Clear();
                selection.AddRange(bandBase);
                foreach (var w in Widgets())
                    if (band.IntersectsWith(new Rectangle(w.Location, w.WidgetSize)) && !selection.Contains(w))
                        selection.Add(w);
                Render();
                return;
            }

            if ((MouseButtons & MouseButtons.Left) == 0) { pressed = false; return; }
            if (!pressed) return;

            if (!moving)
            {
                Size slop = SystemInformation.DragSize;
                if (Math.Abs(screen.X - pressAt.X) < slop.Width && Math.Abs(screen.Y - pressAt.Y) < slop.Height) return;
                // Locked widgets don't travel, not even in a group.
                if (selection.All(w => w.IsLocked)) { pressed = false; return; }
                moving = true;
            }

            MoveSelection(new Point(screen.X - pressAt.X, screen.Y - pressAt.Y));
            Render();
        }

        // One delta for the whole group, clamped so the block stays on the desktop -- clamping each
        // widget on its own would squash the shape the user arranged.
        void MoveSelection(Point delta)
        {
            var travellers = selection.Where(w => !w.IsLocked && moveOrigin.ContainsKey(w)).ToList();
            if (travellers.Count == 0) return;

            Rectangle box = Rectangle.Empty;
            foreach (var w in travellers)
            {
                var r = new Rectangle(moveOrigin[w], w.WidgetSize);
                box = box == Rectangle.Empty ? r : Rectangle.Union(box, r);
            }
            delta.X = Math.Max(area.Left - box.Left, Math.Min(area.Right - box.Right, delta.X));
            delta.Y = Math.Max(area.Top - box.Top, Math.Min(area.Bottom - box.Bottom, delta.Y));

            foreach (var w in travellers)
            {
                Point o = moveOrigin[w];
                w.Location = new Point(o.X + delta.X, o.Y + delta.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point screen = PointToScreen(e.Location);

            if (e.Button == MouseButtons.Right)
            {
                WidgetWindow hit = HitTest(screen);
                if (hit != null && !selection.Contains(hit)) { selection.Clear(); selection.Add(hit); Render(); }
                ShowMenu(screen);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (banding)
            {
                banding = false;
                bandBase.Clear();
                Rectangle drawn = BandRect();
                // A press and release on the same spot is a click on bare desktop, not a band of
                // no width: it clears the selection, and clears the mode once there is nothing
                // left to clear.
                bool tap = drawn.Width < Px(4) && drawn.Height < Px(4);

                if (settingArea)
                {
                    if (tap) { Render(); return; } // still waiting for a real drag
                    settingArea = false;
                    if (drawn.Width > Px(40) && drawn.Height > Px(40))
                    {
                        WidgetArea.Set(Screen.FromRectangle(drawn), drawn);
                        Toast.Show("Widget Area Set", "Tidy Up keeps widgets inside it from now on.");
                    }
                    Render();
                    return;
                }

                if (tap)
                {
                    if (selection.Count == 0) { Arrange.Exit(); return; }
                    selection.Clear();
                }
                Render();
                return;
            }

            if (moving)
            {
                moving = false;
                pressed = false;
                // Saves the fractional position the same way finishing a hand-drag does.
                foreach (var w in selection.Where(x => !x.IsLocked && !x.IsDisposed)) w.MoveTo(w.Location);
                moveOrigin.Clear();
                Render();
                return;
            }

            pressed = false;
            // A click (not a drag) on a widget that was already picked takes it back out.
            if (pressOn != null && pressWasSelected) { selection.Remove(pressOn); Render(); }
            pressOn = null;
        }

        // ---- the group menu ----

        void ShowMenu(Point screen)
        {
            var menu = new PopupMenu();

            if (selection.Count == 0)
            {
                menu.Add("Tidy Up", delegate { WidgetLayout.TidyAll(false); });
                menu.Add("Shuffle", delegate { WidgetLayout.TidyAll(true); });
                menu.AddSeparator();
                menu.Add("Select All Widgets", delegate { SelectAll(); });
                AddAreaItems(menu, screen);
                menu.AddSeparator();
                menu.Add("Done", Arrange.Exit);
                menu.Show(screen);
                return;
            }

            var picked = selection.ToList();
            int n = picked.Count;

            var arrange = menu.AddSubmenu("Arrange");
            arrange.Add("Tidy Up", delegate { WidgetLayout.Tidy(picked, false); });
            arrange.Add("Shuffle", delegate { WidgetLayout.Tidy(picked, true); });
            arrange.AddSeparator();
            arrange.Add("Align Left", delegate { AlignLeft(picked); });
            arrange.Add("Align Top", delegate { AlignTop(picked); });
            arrange.Add("Space Evenly", delegate { SpaceEvenly(picked); });

            var look = menu.AddSubmenu("Appearance");
            foreach (var choice in new[] { new[] { "dark", "Dark" }, new[] { "light", "Light" }, new[] { "vibrant", "Vibrant" } })
            {
                string id = choice[0];
                look.AddChoice(choice[1], picked.All(w => w.Settings.Get("face", "dark") == id),
                    delegate { ApplyAll(picked, "face", id); });
            }

            var size = menu.AddSubmenu("Size");
            int[] scales = { 85, 100, 125 };
            string[] scaleNames = { "Compact", "Standard", "Large" };
            for (int i = 0; i < scales.Length; i++)
            {
                int pct = scales[i];
                size.AddChoice(scaleNames[i], picked.All(w => w.Settings.GetInt("scale", 100) == pct),
                    delegate { ApplyAll(picked, "scale", pct); });
            }

            var opacity = menu.AddSubmenu("Opacity");
            foreach (int pct in new[] { 100, 85, 70, 50 })
            {
                int p = pct;
                opacity.AddChoice(p + "%", picked.All(w => w.Settings.GetInt("opacity", 100) == p),
                    delegate { ApplyAll(picked, "opacity", p); });
            }

            menu.AddSeparator();
            bool allLocked = picked.All(w => w.IsLocked);
            menu.AddCheck("Lock Position", allLocked, delegate { ApplyAll(picked, "locked", !allLocked); });
            menu.AddSeparator();
            menu.AddDestructive(n == 1 ? "Remove Widget" : "Remove " + n + " Widgets", delegate
            {
                if (App.Current == null) return;
                App.Current.RemoveWidgets(picked);
                selection.RemoveAll(picked.Contains);
                Render();
            });

            menu.Show(screen);
        }

        // Where widgets are allowed to go on the screen the menu was opened on.
        void AddAreaItems(PopupMenu menu, Point screen)
        {
            Screen on = Screen.FromPoint(screen);
            menu.AddSeparator();
            menu.Add("Set Widget Area…", delegate { BeginSetArea(); });
            if (WidgetArea.IsCustom(on))
                menu.Add("Use Whole Screen", delegate { WidgetArea.Clear(on); Render(); });
        }

        void ApplyAll(List<WidgetWindow> picked, string key, object value)
        {
            foreach (var w in picked)
            {
                if (w.IsDisposed) continue;
                w.Settings.Set(key, value);
                w.SettingsChangedExternally();
            }
            Render();
        }

        // Arranges the selection, or everything when nothing is picked.
        void Rearrange(bool shuffle)
        {
            if (selection.Count > 0) WidgetLayout.Tidy(selection.ToList(), shuffle);
            else WidgetLayout.TidyAll(shuffle);
        }

        void SelectAll()
        {
            selection.Clear();
            selection.AddRange(Widgets());
            Render();
        }

        void AlignLeft(List<WidgetWindow> picked)
        {
            var movable = picked.Where(w => !w.IsLocked && !w.IsDisposed).ToList();
            if (movable.Count < 2) return;
            int x = movable.Min(w => w.Location.X);
            foreach (var w in movable) w.MoveTo(new Point(x, w.Location.Y));
            Render();
        }

        void AlignTop(List<WidgetWindow> picked)
        {
            var movable = picked.Where(w => !w.IsLocked && !w.IsDisposed).ToList();
            if (movable.Count < 2) return;
            int y = movable.Min(w => w.Location.Y);
            foreach (var w in movable) w.MoveTo(new Point(w.Location.X, y));
            Render();
        }

        // Equal gaps along whichever axis the selection is spread out on.
        void SpaceEvenly(List<WidgetWindow> picked)
        {
            var movable = picked.Where(w => !w.IsLocked && !w.IsDisposed).ToList();
            if (movable.Count < 3) return;

            int spanX = movable.Max(w => w.Location.X + w.WidgetSize.Width) - movable.Min(w => w.Location.X);
            int spanY = movable.Max(w => w.Location.Y + w.WidgetSize.Height) - movable.Min(w => w.Location.Y);
            bool horizontal = spanX >= spanY;

            var sorted = horizontal
                ? movable.OrderBy(w => w.Location.X).ToList()
                : movable.OrderBy(w => w.Location.Y).ToList();

            int used = horizontal ? sorted.Sum(w => w.WidgetSize.Width) : sorted.Sum(w => w.WidgetSize.Height);
            int total = horizontal ? spanX : spanY;
            int gap = (total - used) / (sorted.Count - 1);
            if (gap < 0) gap = Px(12);

            int at = horizontal ? sorted.Min(w => w.Location.X) : sorted.Min(w => w.Location.Y);
            foreach (var w in sorted)
            {
                w.MoveTo(horizontal ? new Point(at, w.Location.Y) : new Point(w.Location.X, at));
                at += (horizontal ? w.WidgetSize.Width : w.WidgetSize.Height) + gap;
            }
            Render();
        }

        // ---- keyboard ----

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape:
                case Keys.Enter:
                    // Escape backs out of drawing the area first, and only then out of the mode.
                    if (settingArea) { settingArea = false; banding = false; Render(); }
                    else Arrange.Exit();
                    e.Handled = true;
                    return;
                case Keys.A:
                    if (e.Control) { SelectAll(); e.Handled = true; }
                    return;
                // Tidy and shuffle from the keyboard, so trying arrangements is one key, not a
                // trip through the menu each time.
                case Keys.T:
                    Rearrange(false);
                    e.Handled = true;
                    return;
                case Keys.S:
                    Rearrange(true);
                    e.Handled = true;
                    return;
                case Keys.Delete:
                case Keys.Back:
                    if (selection.Count > 0 && App.Current != null)
                    {
                        var picked = selection.ToList();
                        App.Current.RemoveWidgets(picked);
                        selection.RemoveAll(picked.Contains);
                        Render();
                        e.Handled = true;
                    }
                    return;
                case Keys.Left: Nudge(-1, 0, e); return;
                case Keys.Right: Nudge(1, 0, e); return;
                case Keys.Up: Nudge(0, -1, e); return;
                case Keys.Down: Nudge(0, 1, e); return;
            }
        }

        // Arrow keys move the selection; Shift for one pixel at a time. The position is written to
        // the config when the key comes back up, not on every repeat -- held down that would be
        // thirty rewrites of widgets.ini a second.
        bool nudged;

        void Nudge(int dx, int dy, KeyEventArgs e)
        {
            var movable = selection.Where(w => !w.IsLocked && !w.IsDisposed).ToList();
            if (movable.Count == 0) return;
            int step = e.Shift ? Px(1) : Px(8);
            foreach (var w in movable)
            {
                var r = new Rectangle(w.Location.X + dx * step, w.Location.Y + dy * step,
                                      w.WidgetSize.Width, w.WidgetSize.Height);
                r.X = Math.Max(area.Left, Math.Min(area.Right - r.Width, r.X));
                r.Y = Math.Max(area.Top, Math.Min(area.Bottom - r.Height, r.Y));
                w.Location = r.Location;
            }
            nudged = true;
            Render();
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (!nudged) return;
            nudged = false;
            foreach (var w in selection.Where(x => !x.IsLocked && !x.IsDisposed)) w.MoveTo(w.Location);
        }

        protected override bool IsInputKey(Keys key)
        {
            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down) return true;
            return base.IsInputKey(key);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            focusWatch.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (fade != null) { fade.Cancel(); fade = null; }
                focusWatch.Dispose();
                // The one big buffer in the app lives only as long as the mode does.
                if (surface != null) { surface.Dispose(); surface = null; }
            }
            base.Dispose(disposing);
        }
    }
}
