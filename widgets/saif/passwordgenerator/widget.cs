using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Password Generator", Description = "Generate and copy strong passwords")]
public sealed class PasswordGenerator : CardWidget
{
    const string Letters = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
    const string Digits = "0123456789";
    const string Symbols = "!@#$%&*?";

    string password;
    Random rng = new Random();
    bool copied;
    Timer copyTimer;

    public PasswordGenerator(Section settings) : base(settings, true)
    {
        copyTimer = new Timer();
        copyTimer.Interval = 1500;
        copyTimer.Tick += delegate { copied = false; copyTimer.Stop(); Render(); };
    }

    bool UseLetter { get { return Settings.Get("letter", "1") == "1"; } }
    bool UseDigit { get { return Settings.Get("digit", "1") == "1"; } }
    bool UseSymbol { get { return Settings.Get("symbol", "1") == "1"; } }
    int Length { get { return Settings.GetInt("len", 16); } }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (password == null) Generate();
    }

    void Generate()
    {
        string pool = "";
        if (UseLetter) pool += Letters;
        if (UseDigit) pool += Digits;
        if (UseSymbol) pool += Symbols;
        if (pool.Length == 0) pool = Letters;

        int len = Length;
        var buf = new char[len];
        for (int i = 0; i < len; i++) buf[i] = pool[rng.Next(pool.Length)];
        password = new string(buf);
        Render();
    }

    void CopyToClipboard()
    {
        if (password == null) return;
        try { Clipboard.SetText(password); }
        catch (Exception) { }
        copied = true;
        copyTimer.Stop();
        copyTimer.Start();
        Render();
    }

    void ToggleLetter() { Settings.Set("letter", UseLetter ? "0" : "1"); Generate(); }
    void ToggleDigit() { Settings.Set("digit", UseDigit ? "0" : "1"); Generate(); }
    void ToggleSymbol() { Settings.Set("symbol", UseSymbol ? "0" : "1"); Generate(); }

    float BtnSize { get { return P(36); } }
    float BtnGap { get { return P(4); } }

    RectangleF CopyBtnRect
    {
        get
        {
            float m = P(16), s = BtnSize;
            return new RectangleF(W - m - s, m + P(22), s, s);
        }
    }

    RectangleF DiceBtnRect
    {
        get
        {
            float m = P(16), s = BtnSize, gap = BtnGap;
            return new RectangleF(W - m - s * 2 - gap, m + P(22), s, s);
        }
    }

    RectangleF PillRect(int i)
    {
        float m = P(16), y = H - m - P(18);
        float pw = P(34), ph = P(20), gap = P(6);
        return new RectangleF(m + i * (pw + gap), y, pw, ph);
    }

    bool InRect(Point p, RectangleF r)
    {
        return p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom;
    }

    protected override bool IsButton(Point p)
    {
        if (InRect(p, CopyBtnRect)) return true;
        if (InRect(p, DiceBtnRect)) return true;
        for (int i = 0; i < 3; i++)
            if (InRect(p, PillRect(i))) return true;
        return false;
    }

    protected override void OnWidgetClick(Point p)
    {
        if (InRect(p, CopyBtnRect)) { CopyToClipboard(); return; }
        if (InRect(p, DiceBtnRect)) { Generate(); return; }
        if (InRect(p, PillRect(0))) { ToggleLetter(); return; }
        if (InRect(p, PillRect(1))) { ToggleDigit(); return; }
        if (InRect(p, PillRect(2))) { ToggleSymbol(); return; }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Generate New", Generate);
        menu.Add("Copy Password", CopyToClipboard);
        menu.AddSeparator();
        var len = menu.AddSubmenu("Length");
        int cur = Length;
        foreach (int n in new[] { 8, 12, 16, 20, 24, 32 })
        {
            int v = n;
            len.AddChoice(v.ToString(), cur == v, delegate { Settings.Set("len", v); Generate(); });
        }
    }

    int Strength
    {
        get
        {
            int types = 0;
            if (UseLetter) types++;
            if (UseDigit) types++;
            if (UseSymbol) types++;
            int len = Length;
            if (types == 0) return 0;
            if (types == 1 && len < 12) return 1;
            if (types <= 2 && len < 12) return 2;
            if (len >= 16 && types >= 3) return 4;
            if (len >= 12 && types >= 2) return 3;
            return 2;
        }
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16), s = BtnSize, gap = BtnGap;

        string pw = password ?? "kH7x!mQ9p@Lw3n#R";

        // Header
        Label(g, "Password Generator", F(11, "semibold"), pal.Fg2, m, m);

        // Password display box
        float boxY = m + P(22), boxH = P(36);
        float boxW = W - m * 2 - s * 2 - gap * 2;
        FillRound(g, pal.Fill, new RectangleF(m, boxY, boxW, boxH), P(8));

        // Draw password with colored characters
        float charX = m + P(12), charY = boxY + P(9);
        Font mono = F(14, "semibold");
        float maxCharW = (boxW - P(24)) / Math.Max(1, pw.Length);
        float charW = Math.Min(P(9.5f), maxCharW);

        for (int i = 0; i < pw.Length; i++)
        {
            char ch = pw[i];
            Color col;
            if (Digits.IndexOf(ch) >= 0) col = pal.Blue;
            else if (Symbols.IndexOf(ch) >= 0) col = pal.Orange;
            else col = pal.Fg;

            float x = charX + i * charW;
            if (x + charW > m + boxW - P(8)) break;
            using (var b = new SolidBrush(col))
                g.DrawString(ch.ToString(), mono, b, x, charY, StringFormat.GenericTypographic);
        }

        // Dice button (generate new)
        RectangleF db = DiceBtnRect;
        FillRound(g, pal.Fill, db, P(8));
        DrawDiceIcon(g, pal.Fg2, db.X + db.Width / 2, db.Y + db.Height / 2, P(14));

        // Copy button
        RectangleF cb = CopyBtnRect;
        Color copyBg = copied ? pal.Green : pal.Blue;
        FillRound(g, copyBg, cb, P(8));
        if (copied)
            DrawCheckIcon(g, pal.OnAccent, cb.X + cb.Width / 2, cb.Y + cb.Height / 2, P(8));
        else
            DrawCopyIcon(g, pal.OnAccent, cb.X + cb.Width / 2, cb.Y + cb.Height / 2, P(8));

        // Bottom row: pills + strength bar
        bool letter = UseLetter, digit = UseDigit, symbol = UseSymbol;
        DrawPill(g, PillRect(0), "Aa", letter ? pal.Green : pal.Fill, letter ? pal.OnAccent : pal.Fg2);
        DrawPill(g, PillRect(1), "123", digit ? pal.Blue : pal.Fill, digit ? pal.OnAccent : pal.Fg2);
        DrawPill(g, PillRect(2), "!@#", symbol ? pal.Orange : pal.Fill, symbol ? pal.OnAccent : pal.Fg2);

        // Strength bar
        int str = Strength;
        float barX = m + 3 * (P(34) + P(6)) + P(8);
        float barW = W - m - barX - P(50);
        float barY = H - m - P(10);
        float barH = P(4);
        FillRound(g, pal.Fill, new RectangleF(barX, barY, barW, barH), barH / 2);

        float fill = str / 4f;
        Color barCol = str <= 1 ? pal.Red : str == 2 ? pal.Orange : str == 3 ? pal.Yellow : pal.Green;
        if (fill > 0)
            FillRound(g, barCol, new RectangleF(barX, barY, Math.Max(barH, barW * fill), barH), barH / 2);

        string[] labels = { "Weak", "Fair", "Good", "Strong", "Strong" };
        Label(g, labels[str], F(11, "semibold"), barCol, W - m, H - m - P(16), P(48), StringAlignment.Far);
    }

    void DrawPill(Graphics g, RectangleF r, string text, Color bg, Color fg)
    {
        FillRound(g, bg, r, r.Height / 2);
        Label(g, text, F(11, "semibold"), fg, r.X + r.Width / 2, r.Y + P(3), r.Width, StringAlignment.Center);
    }

    static void DrawCopyIcon(Graphics g, Color c, float cx, float cy, float s)
    {
        float half = s * 0.5f;
        using (var pen = new Pen(c, Math.Max(1f, s * 0.16f)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawRectangle(pen, cx - half + s * 0.15f, cy - half, s * 0.65f, s * 0.7f);
            g.DrawRectangle(pen, cx - half + s * 0.35f, cy - half + s * 0.3f, s * 0.65f, s * 0.7f);
        }
    }

    static void DrawCheckIcon(Graphics g, Color c, float cx, float cy, float s)
    {
        using (var pen = new Pen(c, Math.Max(1.5f, s * 0.2f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLines(pen, new[] {
                new PointF(cx - s * 0.35f, cy),
                new PointF(cx - s * 0.08f, cy + s * 0.3f),
                new PointF(cx + s * 0.4f, cy - s * 0.3f)
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && copyTimer != null) { copyTimer.Stop(); copyTimer.Dispose(); copyTimer = null; }
        base.Dispose(disposing);
    }

    static void DrawDiceIcon(Graphics g, Color c, float cx, float cy, float s)
    {
        float half = s * 0.5f;
        using (var pen = new Pen(c, Math.Max(1f, s * 0.12f)))
        {
            pen.LineJoin = LineJoin.Round;
            using (var path = RoundRect(new RectangleF(cx - half, cy - half, s, s), s * 0.2f))
                g.DrawPath(pen, path);
        }
        float dot = s * 0.09f;
        float inset = s * 0.26f;
        using (var b = new SolidBrush(c))
        {
            g.FillEllipse(b, cx - inset - dot, cy - inset - dot, dot * 2, dot * 2);
            g.FillEllipse(b, cx + inset - dot, cy - inset - dot, dot * 2, dot * 2);
            g.FillEllipse(b, cx - dot, cy - dot, dot * 2, dot * 2);
            g.FillEllipse(b, cx - inset - dot, cy + inset - dot, dot * 2, dot * 2);
            g.FillEllipse(b, cx + inset - dot, cy + inset - dot, dot * 2, dot * 2);
        }
    }
}
