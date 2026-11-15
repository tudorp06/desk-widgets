using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// Small/medium/large: multiple notes, swipe between them. First line of each note is its title.
// Lines starting with "- " render as bullet points.
[DeskWidget(Name = "Notes", Description = "Multiple notes, click to edit")]
public sealed class NotesWidget : CardWidget
{
    NoteEditor editor;
    bool editing;

    public NotesWidget(Section settings) : base(settings, false) { }

    // ---- multi-note storage ----
    // Notes are stored as text.0, text.1, ... and edited.0, edited.1, ...
    // "count" is the total; "current" is the visible index.

    int NoteCount
    {
        get { int n = Settings.GetInt("count", 0); return n > 0 ? n : MigrateLegacy(); }
    }

    int Current
    {
        get { return Math.Max(0, Math.Min(Settings.GetInt("current", 0), NoteCount - 1)); }
        set { Settings.Set("current", value); }
    }

    string GetNote(int i) { return Settings.GetText("text." + i.ToString(CultureInfo.InvariantCulture), ""); }

    void SetNote(int i, string text)
    {
        Settings.SetText("text." + i.ToString(CultureInfo.InvariantCulture), text);
        Settings.Set("edited." + i.ToString(CultureInfo.InvariantCulture), DateTime.Now.Ticks);
    }

    long GetEdited(int i)
    {
        long v;
        long.TryParse(Settings.Get("edited." + i.ToString(CultureInfo.InvariantCulture), ""), out v);
        return v;
    }

    // Migrate from old single-note format.
    int MigrateLegacy()
    {
        string text = Settings.GetText("text", null);
        if (text == null)
        {
            string title = Settings.GetText("title", null);
            string body = Settings.GetText("body", null);
            text = title == null && body == null ? "" : (title ?? "") + "\n" + (body ?? "");
        }
        Settings.SetText("text.0", text);
        long ticks;
        if (long.TryParse(Settings.Get("edited", ""), out ticks))
            Settings.Set("edited.0", ticks);
        Settings.Set("count", 1);
        Settings.Set("current", 0);
        Settings.Remove("text");
        Settings.Remove("title");
        Settings.Remove("body");
        Settings.Remove("edited");
        return 1;
    }

    void AddNote()
    {
        int n = NoteCount;
        Settings.Set("count", n + 1);
        SetNote(n, "");
        Current = n;
        Render();
        BeginEdit();
    }

    void DeleteNote()
    {
        int n = NoteCount;
        if (n <= 1) return;
        int c = Current;
        // Shift notes after the deleted one down by one.
        for (int i = c; i < n - 1; i++)
        {
            Settings.SetText("text." + i.ToString(CultureInfo.InvariantCulture),
                GetNote(i + 1));
            Settings.Set("edited." + i.ToString(CultureInfo.InvariantCulture),
                GetEdited(i + 1));
        }
        Settings.Remove("text." + (n - 1).ToString(CultureInfo.InvariantCulture));
        Settings.Remove("edited." + (n - 1).ToString(CultureInfo.InvariantCulture));
        Settings.Set("count", n - 1);
        if (c >= n - 1) Current = n - 2;
        Render();
    }

    void GoPrev() { if (Current > 0) { Current = Current - 1; Render(); } }
    void GoNext() { if (Current < NoteCount - 1) { Current = Current + 1; Render(); } }

    // ---- text area ----
    RectangleF TextArea
    {
        get
        {
            float m = P(16);
            float top = m + P(22);
            float bottom = NoteCount > 1 ? H - m - P(28) : H - m - P(18);
            return new RectangleF(m, top, W - m * 2, bottom - top);
        }
    }

    void BeginEdit()
    {
        if (editor != null) { editor.Activate(); return; }
        editing = true;
        Render();
        RectangleF a = TextArea;
        var screen = new Rectangle(Left + (int)a.X, Top + (int)a.Y, (int)a.Width, (int)a.Height);
        editor = new NoteEditor(screen, GetNote(Current).Replace("\n", "\r\n"), F(13, "regular"), Pal.Fg);
        int idx = Current;
        editor.Done += text =>
        {
            editor = null;
            editing = false;
            text = text.Replace("\r\n", "\n").TrimEnd();
            if (text != GetNote(idx).TrimEnd())
                SetNote(idx, text);
            Render();
        };
        editor.Show();
    }

    // ---- navigation buttons ----
    RectangleF PrevRect
    {
        get { float m = P(16); return new RectangleF(m, H - m - P(20), P(24), P(20)); }
    }

    RectangleF NextRect
    {
        get { float m = P(16); return new RectangleF(W - m - P(24), H - m - P(20), P(24), P(20)); }
    }

    protected override bool IsButton(Point p)
    {
        if (NoteCount <= 1) return false;
        return PrevRect.Contains(p) || NextRect.Contains(p);
    }

    protected override void OnWidgetClick(Point p)
    {
        if (NoteCount > 1)
        {
            if (PrevRect.Contains(p) && Current > 0) { GoPrev(); return; }
            if (NextRect.Contains(p) && Current < NoteCount - 1) { GoNext(); return; }
        }
        BeginEdit();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Edit Note", BeginEdit);
        menu.Add("New Note", AddNote);
        if (NoteCount > 1) menu.Add("Delete Note", DeleteNote);
        if (NoteCount > 1)
        {
            menu.AddSeparator();
            if (Current > 0) menu.Add("Previous Note", GoPrev);
            if (Current < NoteCount - 1) menu.Add("Next Note", GoNext);
        }
    }

    // ---- drawing ----
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16), w = W - m * 2;
        int idx = Current, total = NoteCount;

        DrawNoteGlyph(g, pal.Yellow, m, m + P(1), P(12));
        string header = total > 1
            ? "Notes  " + (idx + 1).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture)
            : "Notes";
        Label(g, header, F(13, "semibold"), pal.Yellow, m + P(17), m - P(1));

        if (!editing)
        {
            string text = GetNote(idx);
            float bodyTop = m + P(22);

            if (text.Trim().Length == 0)
            {
                Label(g, "Click to write…", F(13, "regular"), pal.Fg2, m, bodyTop + P(2), w, StringAlignment.Near);
            }
            else
            {
                string[] lines = text.Split('\n');
                string title = lines[0];
                Label(g, title, F(15, "bold"), pal.Fg, m, bodyTop, w, StringAlignment.Near);

                float y = bodyTop + P(22);
                float maxY = total > 1 ? H - m - P(30) : H - m - P(18);
                Font bodyFont = F(13, "regular");

                for (int i = 1; i < lines.Length && y < maxY; i++)
                {
                    string line = lines[i];
                    if (line.Length >= 2 && line[0] == '-' && line[1] == ' ')
                    {
                        string bullet = "• " + line.Substring(2);
                        Label(g, bullet, bodyFont, pal.Fg2, m + P(4), y, w - P(4), StringAlignment.Near);
                    }
                    else
                    {
                        Label(g, line, bodyFont, pal.Fg2, m, y, w, StringAlignment.Near);
                    }
                    y += P(18);
                }
            }
        }

        // Edited timestamp.
        long ticks = GetEdited(idx);
        if (ticks > 0)
        {
            var t = new DateTime(ticks);
            float ey = total > 1 ? H - m - P(28) : H - m - P(13);
            Label(g, "Edited " + (t.Date == DateTime.Today ? t.ToString("t") : t.ToString("d MMM")), F(11, "regular"), pal.Fg2, m, ey);
        }

        // Page navigation dots and arrows.
        if (total > 1)
        {
            float cy = H - m - P(8), cx = W / 2;
            int maxDots = 7;
            int dots = Math.Min(total, maxDots);
            float dotR = P(3), dotGap = P(10);
            float startX = cx - (dots - 1) * dotGap / 2;
            for (int i = 0; i < dots; i++)
            {
                Color dc = i == idx ? pal.Fg : pal.Fill;
                FillCircle(g, dc, startX + i * dotGap, cy, dotR);
            }

            // Chevron arrows.
            Color arrowOn = pal.Fg2, arrowOff = pal.Fill;
            DrawChevron(g, idx > 0 ? arrowOn : arrowOff, m + P(6), cy, P(5), true);
            DrawChevron(g, idx < total - 1 ? arrowOn : arrowOff, W - m - P(6), cy, P(5), false);
        }
    }

    static void DrawChevron(Graphics g, Color c, float cx, float cy, float s, bool left)
    {
        float d = left ? -1 : 1;
        using (var pen = new Pen(c, Math.Max(1.5f, s * 0.25f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(pen, cx + d * s * 0.3f, cy - s, cx - d * s * 0.3f, cy);
            g.DrawLine(pen, cx - d * s * 0.3f, cy, cx + d * s * 0.3f, cy + s);
        }
    }

    static void DrawNoteGlyph(Graphics g, Color c, float x, float y, float s)
    {
        FillRound(g, c, new RectangleF(x, y, s, s), s * 0.22f);
        using (var pen = new Pen(Color.FromArgb(150, 0, 0, 0), Math.Max(1f, s * 0.11f)))
        {
            g.DrawLine(pen, x + s * 0.25f, y + s * 0.38f, x + s * 0.75f, y + s * 0.38f);
            g.DrawLine(pen, x + s * 0.25f, y + s * 0.62f, x + s * 0.6f, y + s * 0.62f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && editor != null) editor.Close();
        base.Dispose(disposing);
    }

    // Borderless text box laid exactly over the note's text, in the note's own colours, so typing
    // looks like writing on the widget. (Layered widget windows can't host a real text box.)
    sealed class NoteBox : TextBox
    {
        public Action<Point> ContextMenuRequested;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007B && ContextMenuRequested != null) // WM_CONTEXTMENU
            {
                int lp = (int)(long)m.LParam;
                ContextMenuRequested(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                return;
            }
            base.WndProc(ref m);
        }
    }

    public sealed class NoteEditor : Form
    {
        readonly NoteBox box;
        bool finished, menuOpen;
        public event Action<string> Done;

        public NoteEditor(Rectangle screenRect, string text, Font font, Color fore)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = screenRect;
            Color key = fore.GetBrightness() > 0.5f ? Color.FromArgb(1, 1, 1) : Color.FromArgb(254, 254, 254);
            TransparencyKey = key;
            BackColor = key;
            box = new NoteBox
            {
                Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None, BackColor = key, ForeColor = fore, Font = font,
                Dock = DockStyle.Fill, Text = text
            };
            box.ContextMenuRequested = p =>
            {
                menuOpen = true;
                var menu = new PopupMenu();
                menu.Add("Undo", delegate { box.Undo(); });
                menu.AddSeparator();
                menu.Add("Cut", delegate { box.Cut(); });
                menu.Add("Copy", delegate { box.Copy(); });
                menu.Add("Paste", delegate { box.Paste(); });
                menu.AddSeparator();
                menu.Add("Select All", delegate { box.SelectAll(); });
                menu.Show(p);
            };
            Activated += delegate { menuOpen = false; };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; box.SelectAll(); }
            };
            Controls.Add(box);
            Deactivate += delegate { if (!menuOpen) Finish(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: not in the taskbar or Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            box.Focus();
            box.SelectionStart = box.TextLength;
        }

        void Finish()
        {
            if (finished) return;
            finished = true;
            if (Done != null) Done(box.Text);
            BeginInvoke((Action)Close);
        }
    }
}
