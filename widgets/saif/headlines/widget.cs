using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DeskWidgets;
using DeskWidgets.Widgets;

// The newest four stories from any RSS or Atom feed: the lead one large, three more under it,
// each with its source and how long ago it arrived. Click a story to open it in your browser.
// The feed is fetched every 20 minutes and kept on disk, so the card is filled before the
// network answers. Only the feed you name is contacted.
[DeskWidget(Name = "Headlines", Description = "Read the newest stories from any RSS feed you choose")]
public sealed class Headlines : CardWidget
{
    const string DefaultFeed = "https://hnrss.org/frontpage";
    const int Show = 4;

    sealed class Story
    {
        public string Title, Link, Source;
        public DateTime When;
    }

    static readonly Story[] Sample =
    {
        new Story { Title = "The newest story from the feed you pick fills these two lines", Source = "Example feed" },
        new Story { Title = "One line each for the three stories after it", Source = "Example feed" },
        new Story { Title = "Every line carries its source and how long ago it arrived", Source = "Example feed" },
        new Story { Title = "Click a story to open it in your browser", Source = "Example feed" }
    };

    readonly List<RectangleF> rows = new List<RectangleF>();
    List<Story> stories = new List<Story>();
    string status;
    bool busy;
    DateTime fetchedAt;

    public Headlines(Section settings) : base(settings, CardSize.Large) { }

    string Feed { get { return Settings.GetText("feed", DefaultFeed); } }

    protected override void OnStart()
    {
        LoadCache();
        Fetch();
    }

    protected override int NextFrameDelay()
    {
        if (!IsHandleCreated) return -1;
        if ((DateTime.UtcNow - fetchedAt).TotalMinutes >= 20) Fetch();
        return 60 * 1000;      // the ages on the lines move on their own
    }

    // ---------- the feed ----------
    void Fetch()
    {
        if (busy) return;
        busy = true;
        fetchedAt = DateTime.UtcNow;
        string url = Feed;
        if (stories.Count == 0) status = "Loading…";
        Background(() => Http.Get(url), (xml, error) =>
        {
            busy = false;
            if (error != null)
            {
                status = stories.Count > 0 ? null : "Couldn't reach the feed";
                Render();
                return;
            }
            try
            {
                List<Story> read = Parse(xml);
                if (read.Count > 0)
                {
                    stories = read;
                    status = null;
                    SaveCache(xml);
                }
                else status = stories.Count > 0 ? null : "Nothing in that feed";
            }
            catch (Exception)
            {
                status = stories.Count > 0 ? null : "That feed can't be read";
            }
            Render();
        });
    }

    // RSS 2.0 and Atom, told apart by the element names rather than the namespace.
    static List<Story> Parse(string xml)
    {
        var read = new List<Story>();
        XDocument doc = XDocument.Parse(xml);
        string channel = doc.Descendants().Where(e => e.Name.LocalName == "channel" || e.Name.LocalName == "feed")
            .SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "title")).Select(e => e.Value).FirstOrDefault();

        foreach (XElement item in doc.Descendants().Where(e => e.Name.LocalName == "item" || e.Name.LocalName == "entry"))
        {
            XElement title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title");
            if (title == null) continue;
            XElement link = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
            string href = link == null ? null : (string)link.Attribute("href") ?? link.Value;
            string date = item.Elements()
                .Where(e => e.Name.LocalName == "pubDate" || e.Name.LocalName == "updated" || e.Name.LocalName == "published")
                .Select(e => e.Value).FirstOrDefault();
            DateTime when;
            if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out when))
                when = DateTime.MinValue;
            else when = DateTime.SpecifyKind(when, DateTimeKind.Utc);

            read.Add(new Story
            {
                Title = Clean(title.Value),
                Link = href == null ? null : href.Trim(),
                Source = Clean(channel ?? Host(href)),
                When = when
            });
            if (read.Count == Show) break;
        }
        return read;
    }

    static string Clean(string s)
    {
        if (s == null) return "";
        s = s.Replace("&amp;", "&").Replace("&#39;", "'").Replace("&quot;", "\"").Replace("&nbsp;", " ");
        return string.Join(" ", s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    static string Host(string url)
    {
        try { return url == null ? "" : new Uri(url).Host.Replace("www.", ""); }
        catch (Exception) { return ""; }
    }

    static string Ago(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "";
        TimeSpan old = DateTime.UtcNow - utc;
        if (old.TotalMinutes < 1) return "just now";
        if (old.TotalMinutes < 60) return (int)old.TotalMinutes + " min ago";
        if (old.TotalHours < 24) return (int)old.TotalHours + " h ago";
        return (int)old.TotalDays + " d ago";
    }

    // ---------- the copy kept on disk ----------
    void LoadCache()
    {
        try
        {
            string file = CacheFile("headlines");
            if (!File.Exists(file)) return;
            List<Story> read = Parse(File.ReadAllText(file));
            if (read.Count > 0) stories = read;
        }
        catch (Exception) { }
    }

    void SaveCache(string xml)
    {
        try { File.WriteAllText(CacheFile("headlines"), xml); }
        catch (Exception) { }
    }

    // ---------- interaction ----------
    protected override void OnWidgetClick(Point p)
    {
        for (int i = 0; i < rows.Count && i < stories.Count; i++)
            if (rows[i].Contains(p))
            {
                string link = stories[i].Link;
                if (string.IsNullOrEmpty(link)) return;
                try { Process.Start(link); }
                catch (Exception) { }
                return;
            }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Set Feed…", delegate
        {
            string[] answer = InputDialog.Ask("Headlines", "The address of an RSS or Atom feed. Only this address is contacted.",
                new[] { new InputDialog.Field { Label = "Feed", Value = Feed } }, "Use Feed");
            if (answer == null || answer[0].Trim().Length == 0) return;
            Settings.SetText("feed", answer[0].Trim());
            stories = new List<Story>();
            status = "Loading…";
            fetchedAt = DateTime.MinValue;
            Fetch();
            Render();
        });
        menu.Add("Refresh Now", delegate { fetchedAt = DateTime.MinValue; Fetch(); });
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16);
        Font label = F(11, "semibold"), lead = F(17, "semibold"), line = F(13, null), by = F(11, null);
        List<Story> show = stories.Count > 0 ? stories : (!IsHandleCreated ? new List<Story>(Sample) : new List<Story>());

        Label(g, "HEADLINES", label, pal.Red, m, m, W * 0.6f, StringAlignment.Near);
        Label(g, status ?? (show.Count > 0 ? show[0].Source : ""), label, pal.Fg2, W - m, m, W * 0.4f, StringAlignment.Far);

        rows.Clear();
        if (show.Count == 0)
        {
            Label(g, "No stories yet", F(22, "semibold"), pal.Fg2, m, H / 2 - P(16), W - m * 2, StringAlignment.Near);
            return;
        }

        float y = m + label.GetHeight(g) * 1.9f;
        float leadHeight = lead.GetHeight(g) * 2 + by.GetHeight(g) * 1.2f + P(10);
        float rest = (H - m - (y + leadHeight)) / Math.Max(1, show.Count - 1);

        // The lead story: two lines of 17 pt over its source.
        Story first = show[0];
        rows.Add(new RectangleF(m, y, W - m * 2, leadHeight));
        Paragraph(g, first.Title, lead, pal.Fg, new RectangleF(m, y, W - m * 2, lead.GetHeight(g) * 2.1f));
        Label(g, Line(first), by, pal.Fg2, m, y + lead.GetHeight(g) * 2.15f, W - m * 2, StringAlignment.Near);
        y += leadHeight;

        for (int i = 1; i < show.Count; i++)
        {
            Story s = show[i];
            using (var sep = new Pen(pal.Sep, 1)) g.DrawLine(sep, m, y, W - m, y);
            rows.Add(new RectangleF(m, y, W - m * 2, rest));
            float textTop = y + (rest - line.GetHeight(g) * 1.9f - by.GetHeight(g)) / 2 + P(2);
            Paragraph(g, s.Title, line, pal.Fg, new RectangleF(m, textTop, W - m * 2, line.GetHeight(g) * 2.1f));
            Label(g, Line(s), by, pal.Fg2, m, textTop + line.GetHeight(g) * 2.15f, W - m * 2, StringAlignment.Near);
            y += rest;
        }
    }

    static string Line(Story s)
    {
        string ago = Ago(s.When);
        if (s.Source.Length == 0) return ago;
        return ago.Length == 0 ? s.Source : s.Source + " · " + ago;
    }
}
