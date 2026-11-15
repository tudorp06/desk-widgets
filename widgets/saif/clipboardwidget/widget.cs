using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Clipboard", Description = "Recent clipboard history")]
public sealed class ClipboardWidget : CardWidget
{
    static readonly Color[] StripeColors = {
        Rgb(0x0A84FF),  // blue
        Rgb(0xBF5AF2),  // purple
        Rgb(0x30D158),  // green
        Rgb(0xFF9F0A),  // orange
        Rgb(0xFF453A),  // red
        Rgb(0xFFD60A),  // yellow
        Rgb(0x64D2FF),  // teal
        Rgb(0xFF375F)   // pink
    };

    const int MaxHistory = 50;
    const int VisibleSmall = 4;
    readonly List<string> history = new List<string>();
    string lastClip;

    public ClipboardWidget(Section settings) : base(settings, false) { }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadHistory();
        PollClipboard();
    }

    void LoadHistory()
    {
        string stored = Settings.GetText("history", "");
        if (stored.Length == 0) return;
        foreach (string line in stored.Split('\n'))
        {
            if (line.Length > 0) history.Add(line.Replace("\\n", "\n").Replace("\\\\", "\\"));
        }
    }

    void SaveHistory()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(history[i].Replace("\\", "\\\\").Replace("\n", "\\n"));
        }
        Settings.SetText("history", sb.ToString());
    }

    void PollClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (text != null && text.Length > 0 && text != lastClip)
                {
                    lastClip = text;
                    string trimmed = text.Length > 500 ? text.Substring(0, 500) : text;
                    history.Remove(trimmed);
                    history.Insert(0, trimmed);
                    while (history.Count > MaxHistory) history.RemoveAt(history.Count - 1);
                    SaveHistory();
                    Render();
                }
            }
        }
        catch (Exception) { }
    }

    protected override int NextFrameDelay() { return 1500; }

    protected override void Draw(Graphics g)
    {
        PollClipboard();
        base.Draw(g);
    }

    float RowY(int i)
    {
        float m = P(16);
        float top = m + P(22) + P(8);
        return top + i * (P(28) + P(4));
    }

    int RowAt(Point p)
    {
        for (int i = 0; i < Math.Min(VisibleSmall, history.Count); i++)
        {
            float y = RowY(i);
            if (p.Y >= y && p.Y < y + P(28)) return i;
        }
        return -1;
    }

    protected override bool IsButton(Point p)
    {
        return RowAt(p) >= 0;
    }

    protected override void OnWidgetClick(Point p)
    {
        int row = RowAt(p);
        if (row < 0 || row >= history.Count) return;
        try
        {
            Clipboard.SetText(history[row]);
            lastClip = history[row];
        }
        catch (Exception) { }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Clear History", delegate
        {
            history.Clear();
            lastClip = null;
            Settings.SetText("history", "");
            Render();
        });
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16), w = W - m * 2;

        DrawClipGlyph(g, pal.Blue, m, m + P(1), P(12));
        Label(g, "Clipboard", F(13, "semibold"), pal.Blue, m + P(17), m - P(1));

        int count = Math.Min(VisibleSmall, history.Count);

        if (history.Count == 0)
        {
            Label(g, "Nothing copied yet", F(13, "regular"), pal.Fg2, m, m + P(24), w, StringAlignment.Near);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                float y = RowY(i);
                Color stripe = StripeColors[i % StripeColors.Length];
                float stripeW = P(3);
                float rowH = P(28);
                float rowR = P(6);

                FillRound(g, pal.Fill, new RectangleF(m, y, w, rowH), rowR);

                using (var path = RoundRect(new RectangleF(m, y, stripeW, rowH), rowR))
                using (var b = new SolidBrush(stripe))
                {
                    var state = g.Save();
                    g.SetClip(new RectangleF(m, y, stripeW, rowH));
                    g.FillPath(b, path);
                    g.Restore(state);
                }

                string text = history[i].Replace('\n', ' ').Replace('\r', ' ');
                Label(g, text, F(12, "regular"), pal.Fg, m + stripeW + P(5), y + P(7), w - stripeW - P(10), StringAlignment.Near);
            }
        }

        string footer = history.Count == 0 ? "Click to copy" : "Click to copy · " + history.Count + " item" + (history.Count == 1 ? "" : "s");
        Label(g, footer, F(10, "regular"), pal.Fg2, W / 2, H - m - P(10), w, StringAlignment.Center);
    }

    static void DrawClipGlyph(Graphics g, Color c, float x, float y, float s)
    {
        float bw = s * 0.7f, bh = s * 0.85f;
        float bx = x + (s - bw) / 2, by = y + s - bh;
        FillRound(g, c, new RectangleF(bx, by, bw, bh), s * 0.15f);

        float cw = s * 0.35f, ch = s * 0.22f;
        float cx = x + (s - cw) / 2, cy = by - ch * 0.35f;
        FillRound(g, c, new RectangleF(cx, cy, cw, ch), ch * 0.4f);

        using (var pen = new Pen(Color.FromArgb(150, 0, 0, 0), Math.Max(1f, s * 0.08f)))
        {
            float lx = bx + bw * 0.22f, rx = bx + bw * 0.78f;
            g.DrawLine(pen, lx, by + bh * 0.38f, rx, by + bh * 0.38f);
            g.DrawLine(pen, lx, by + bh * 0.58f, rx, by + bh * 0.58f);
            g.DrawLine(pen, lx, by + bh * 0.78f, rx - bw * 0.15f, by + bh * 0.78f);
        }
    }
}
