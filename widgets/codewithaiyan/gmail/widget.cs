using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using DeskWidgets;
using DeskWidgets.Widgets;

// Medium: the three newest unread mails, sender, subject and time to a row, with a compose button.
// Gmail's inbox Atom feed is the only way to read the unread list without registering an OAuth
// app, so this asks for a Google app password rather than a normal one.
[DeskWidget(Name = "Gmail", Description = "Your newest unread mail")]
public sealed class GmailWidget : CardWidget
{
    // shortcut: the Atom feed is unread mail only, newest first, and needs an app password (2-step
    // verification on); move to the Gmail API with OAuth if Google retires either.
    const string FeedUrl = "https://mail.google.com/mail/feed/atom";
    const string InboxUrl = "https://mail.google.com/mail/u/0/#inbox";
    const string ComposeUrl = "https://mail.google.com/mail/u/0/#inbox?compose=new";
    const int Rows = 3;

    internal sealed class Mail
    {
        public string From, Subject, Link;
        public DateTime When;
    }

    internal sealed class Box
    {
        public readonly List<Mail> Mail = new List<Mail>();
        public int Unread;
    }

    Box box;
    string status;
    bool busy;
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };

    public GmailWidget(Section settings) : base(settings, true)
    {
        refresh.Tick += delegate { Fetch(); };
    }

    protected override void OnStart()
    {
        refresh.Start();
        Fetch();
    }

    bool Connected
    {
        get { return Settings.GetText("user", "").Length > 0 && Settings.Get("pass", "").Length > 0; }
    }

    // ---- reading ----

    void Fetch()
    {
        if (busy) return;
        string user = Settings.GetText("user", "");
        string pass = PluginApi.Unprotect(Settings.Get("pass", ""));
        if (user.Length == 0 || pass == null)
        {
            status = pass == null && Settings.Get("pass", "").Length > 0 ? "Connect again on this PC" : "Click to connect";
            Render();
            return;
        }
        busy = true;
        Background<Box>(delegate { return Parse(Feed(user, pass)); }, delegate(Box result, Exception error)
        {
            busy = false;
            if (error != null)
            {
                var web = error as WebException;
                var resp = web != null ? web.Response as HttpWebResponse : null;
                bool refused = resp != null && (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden);
                status = refused ? "Gmail refused the app password" : error is XmlException ? "Couldn't read Gmail's reply" : "Offline";
            }
            else
            {
                box = result;
                status = null;
            }
            Render();
        });
    }

    static string Feed(string user, string pass)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var req = (HttpWebRequest)WebRequest.Create(FeedUrl);
        req.UserAgent = "DeskWidgets";
        req.Timeout = 20000;
        // Preemptive Basic: Google answers the feed's challenge with a sign-in page, not a retry.
        req.Headers[HttpRequestHeader.Authorization] = "Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + pass));
        using (var resp = req.GetResponse())
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return r.ReadToEnd();
    }

    // The feed is Atom 0.3 in its own namespace; matching on local names keeps that out of the way.
    internal static Box Parse(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml);
        var box = new Box();
        int.TryParse(NodeText(doc.DocumentElement, "fullcount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out box.Unread);
        foreach (XmlNode entry in doc.DocumentElement.ChildNodes)
        {
            if (entry.LocalName != "entry") continue;
            var m = new Mail { Subject = NodeText(entry, "title"), From = NodeText(entry, "name") };
            if (m.From.Length == 0) m.From = NodeText(entry, "email");
            if (m.Subject.Length == 0) m.Subject = "(no subject)";
            DateTime when;
            if (DateTime.TryParse(NodeText(entry, "issued"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                m.When = when.ToLocalTime();
            var link = Find(entry, "link") as XmlElement;
            if (link != null) m.Link = link.GetAttribute("href");
            box.Mail.Add(m);
            if (box.Mail.Count == Rows) break;
        }
        return box;
    }

    static XmlNode Find(XmlNode parent, string localName)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child.LocalName == localName) return child;
            XmlNode deeper = Find(child, localName);
            if (deeper != null) return deeper;
        }
        return null;
    }

    static string NodeText(XmlNode parent, string localName)
    {
        XmlNode n = parent == null ? null : Find(parent, localName);
        return n == null ? "" : n.InnerText.Trim();
    }

    // ---- account ----

    void Connect()
    {
        string[] values = InputDialog.Ask("Connect Gmail",
            "Gmail only hands out your unread mail to an app password, not your normal one: make one at " +
            "myaccount.google.com/apppasswords. It's stored encrypted for your Windows account only.",
            new[]
            {
                new InputDialog.Field { Label = "Gmail address", Value = Settings.GetText("user", "") },
                new InputDialog.Field { Label = "App password", Secret = true }
            }, "Connect");
        if (values == null) return;
        string user = values[0].Trim(), pass = values[1].Replace(" ", ""); // Google shows it in four groups
        if (user.Length == 0 || pass.Length == 0) return;
        Settings.SetText("user", user);
        Settings.Set("pass", PluginApi.Protect(pass));
        box = null;
        status = "Loading…";
        Render();
        Fetch();
    }

    static void Open(string url)
    {
        try { Process.Start(url); }
        catch (Exception) { }
    }

    protected override bool IsButton(Point p) { return ComposeCircle.Contains(p.X, p.Y); }

    protected override void OnWidgetClick(Point p)
    {
        if (ComposeCircle.Contains(p.X, p.Y)) { Open(Connected ? ComposeUrl : InboxUrl); return; }
        if (!Connected) { Connect(); return; }
        Mail m = MailAt(p);
        Open(m != null && !string.IsNullOrEmpty(m.Link) ? m.Link : InboxUrl);
    }

    Mail MailAt(Point p)
    {
        if (box == null) return null;
        for (int i = 0; i < box.Mail.Count; i++)
            if (RowRect(i).Contains(p.X, p.Y)) return box.Mail[i];
        return null;
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Refresh Now", Fetch);
        menu.Add("Open Gmail", delegate { Open(InboxUrl); });
        menu.Add("New Message", delegate { Open(ComposeUrl); });
        menu.Add(Connected ? "Change Account…" : "Connect Gmail…", Connect);
        if (Connected)
            menu.AddDestructive("Disconnect", delegate
            {
                Settings.Remove("user");
                Settings.Remove("pass");
                box = null;
                status = "Click to connect";
                Render();
            });
    }

    // ---- drawing ----

    float Col { get { return P(44); } }
    RectangleF AvatarCircle { get { return new RectangleF(P(16), P(16), Col, Col); } }
    RectangleF ComposeCircle { get { return new RectangleF(P(16), H - P(16) - Col, Col, Col); } }

    RectangleF RowRect(int i)
    {
        float m = P(16), gap = P(8), x = m + Col + P(12);
        float h = (H - m * 2 - gap * (Rows - 1)) / Rows;
        return new RectangleF(x, m + i * (h + gap), W - x - m, h);
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        Box b = box ?? (IsHandleCreated ? null : Sample());

        RectangleF av = AvatarCircle;
        FillCircle(g, pal.Red, av.X + av.Width / 2, av.Y + av.Height / 2, av.Width / 2);
        int unread = b != null ? b.Unread : 0;
        if (unread > 0)
            Label(g, unread > 99 ? "99+" : unread.ToString(CultureInfo.InvariantCulture), F(15, "semibold"), pal.OnAccent,
                av.X + av.Width / 2, av.Y + av.Height / 2 - P(10), av.Width, StringAlignment.Center);
        else
            Envelope(g, pal.OnAccent, Inset(av, av.Width * 0.3f));

        RectangleF comp = ComposeCircle;
        FillCircle(g, pal.Fill, comp.X + comp.Width / 2, comp.Y + comp.Height / 2, comp.Width / 2);
        Pencil(g, pal.Red, Inset(comp, comp.Width * 0.32f));

        for (int i = 0; i < Rows; i++)
        {
            RectangleF r = RowRect(i);
            FillRound(g, pal.Fill, r, P(12));
            Mail m = b != null && i < b.Mail.Count ? b.Mail[i] : null;
            if (m == null) continue;
            float pad = P(12), timeW = P(56);
            Label(g, Time(m.When), F(11, "regular"), pal.Fg2, r.Right - pad, r.Y + (r.Height - P(13)) / 2, timeW, StringAlignment.Far);
            float textW = r.Width - pad * 2 - timeW - P(6);
            Label(g, m.From, F(13, "semibold"), pal.Fg, r.X + pad, r.Y + P(6), textW, StringAlignment.Near);
            Label(g, m.Subject, F(11, "regular"), pal.Fg2, r.X + pad, r.Y + P(22), textW, StringAlignment.Near);
        }

        if (b != null && b.Mail.Count > 0 && status == null) return;
        string line = b == null ? (status ?? "Loading…") : status ?? "No unread mail";
        string hint = b == null && !Connected ? "Click to connect your account." : null;
        RectangleF area = RectangleF.Union(RowRect(0), RowRect(Rows - 1));
        float y = area.Y + area.Height / 2 - (hint == null ? P(8) : P(18));
        Label(g, line, F(13, "semibold"), pal.Fg, area.X + area.Width / 2, y, area.Width - P(24), StringAlignment.Center);
        if (hint != null)
            Label(g, hint, F(11, "regular"), pal.Fg2, area.X + area.Width / 2, y + P(20), area.Width - P(24), StringAlignment.Center);
    }

    static RectangleF Inset(RectangleF r, float by)
    {
        return new RectangleF(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2);
    }

    string Time(DateTime t)
    {
        if (t == DateTime.MinValue) return "";
        return t.Date == DateTime.Today ? t.ToString("h:mm tt") : t.ToString("d MMM");
    }

    static void Envelope(Graphics g, Color c, RectangleF r)
    {
        using (var pen = new Pen(c, Math.Max(1f, r.Height * 0.14f)))
        {
            pen.LineJoin = LineJoin.Round;
            using (var path = RoundRect(r, r.Height * 0.2f)) g.DrawPath(pen, path);
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.1f, r.Top + r.Height * 0.25f),
                new PointF(r.X + r.Width / 2, r.Y + r.Height * 0.62f),
                new PointF(r.Right - r.Width * 0.1f, r.Top + r.Height * 0.25f)
            });
        }
    }

    static void Pencil(Graphics g, Color c, RectangleF r)
    {
        using (var pen = new Pen(c, Math.Max(1f, r.Width * 0.22f)))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawLine(pen, r.Left + r.Width * 0.3f, r.Bottom - r.Height * 0.28f, r.Right - r.Width * 0.1f, r.Top + r.Height * 0.1f);
        }
        using (var tip = new GraphicsPath())
        {
            tip.AddPolygon(new[]
            {
                new PointF(r.Left, r.Bottom),
                new PointF(r.Left + r.Width * 0.08f, r.Bottom - r.Height * 0.32f),
                new PointF(r.Left + r.Width * 0.32f, r.Bottom - r.Height * 0.08f)
            });
            using (var brush = new SolidBrush(c)) g.FillPath(brush, tip);
        }
    }

    // Shown in the library, where no account is connected yet.
    static Box Sample()
    {
        var b = new Box { Unread = 12 };
        DateTime today = DateTime.Today;
        b.Mail.Add(new Mail { From = "Eric Slivka", Subject = "OMG New Gmail Widget!", When = today.AddHours(15).AddMinutes(50) });
        b.Mail.Add(new Mail { From = "Amazon.com", Subject = "Your Amazon.com order #112-0…", When = today.AddHours(14).AddMinutes(4) });
        b.Mail.Add(new Mail { From = "USPS Informed Delivery", Subject = "Your Daily Digest for Mon", When = today.AddHours(7).AddMinutes(29) });
        return b;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}
