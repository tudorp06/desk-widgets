using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using DeskWidgets;
using DeskWidgets.Widgets;

// A streak gauge beside today's prompt and one button that writes the entry, the way the
// Journal widget asks for a line a day. Entries are appended to journal.txt in the app's own
// data folder on this PC and nothing is sent anywhere.
[DeskWidget(Name = "Journal", Description = "Answer a prompt a day and watch the streak hold")]
public sealed class Journal : CardWidget
{
    const int Keep = 400;

    static readonly string[] Prompts =
    {
        "What went better today than you expected?",
        "What took the most out of you today?",
        "Who should hear from you tomorrow?",
        "What would you do differently if today repeated?",
        "What did you learn that you want to keep?",
        "What is worth finishing before the week ends?",
        "What made you stop and look today?",
        "What are you putting off, and why?",
        "What went right that you had no hand in?",
        "What would make tomorrow a good day?"
    };

    RectangleF button;

    public Journal(Section settings) : base(settings, true) { }

    // ---------- what is kept ----------
    List<string> Days()
    {
        var days = new List<string>();
        foreach (string part in Settings.Get("days", "").Split(','))
            if (part.Length == 8) days.Add(part);
        return days;
    }

    static string Key(DateTime day) { return day.ToString("yyyyMMdd", CultureInfo.InvariantCulture); }

    bool WrittenToday { get { return Days().Contains(Key(DateTime.Today)); } }

    int Streak()
    {
        List<string> days = Days();
        if (days.Count == 0) return 0;
        int streak = 0;
        DateTime day = DateTime.Today;
        if (!days.Contains(Key(day))) day = day.AddDays(-1);
        while (days.Contains(Key(day))) { streak++; day = day.AddDays(-1); }
        return streak;
    }

    string Prompt()
    {
        return Prompts[(DateTime.Today.DayOfYear + Settings.GetInt("offset", 0)) % Prompts.Length];
    }

    // Null before the app has settled on a data folder (a library preview), so nothing is written.
    string File
    {
        get { return PluginApi.DataDir == null ? null : Path.Combine(PluginApi.DataDir, "journal.txt"); }
    }

    protected override int NextFrameDelay()
    {
        return IsHandleCreated ? 10 * 60 * 1000 : -1;   // only the day turning changes anything
    }

    // ---------- writing ----------
    void Write()
    {
        string[] answer = InputDialog.Ask("Journal", Prompt(),
            new[] { new InputDialog.Field { Label = "Today", Value = "" } }, "Save");
        if (answer == null) return;
        string text = (answer[0] ?? "").Trim();
        if (text.Length == 0) return;

        try
        {
            if (File != null) System.IO.File.AppendAllText(File,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\t" + Prompt() + "\t" + text + Environment.NewLine);
        }
        catch (Exception) { }

        List<string> days = Days();
        string today = Key(DateTime.Today);
        if (!days.Contains(today)) days.Add(today);
        days.Sort(StringComparer.Ordinal);
        days.Reverse();
        if (days.Count > Keep) days.RemoveRange(Keep, days.Count - Keep);
        Settings.Set("days", string.Join(",", days.ToArray()));
        Settings.SetText("last", text);
        Settings.Set("lastat", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Changed();
    }

    protected override bool IsButton(Point p) { return button.Contains(p); }

    protected override void OnWidgetClick(Point p)
    {
        if (button.Contains(p)) Write();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add(WrittenToday ? "Add Another Entry…" : "Write Today's Entry…", Write);
        menu.Add("Another Prompt", delegate { Settings.Set("offset", Settings.GetInt("offset", 0) + 1); Changed(); });
        menu.Add("Open journal.txt", delegate
        {
            try { if (File != null && System.IO.File.Exists(File)) System.Diagnostics.Process.Start(File); }
            catch (Exception) { }
        });
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16);
        Font label = F(11, "semibold"), prompt = F(15, "semibold"), small = F(11, null), button13 = F(13, "semibold");
        bool preview = !IsHandleCreated;
        int streak = preview ? 12 : Streak();
        bool written = preview ? false : WrittenToday;

        // The gauge: a ring that closes over a week, with the run of days inside it.
        float radius = Math.Min(H * 0.22f, W * 0.12f);
        float cx = m + radius, cy = H / 2 - label.GetHeight(g) * 0.4f;
        float stroke = Math.Max(P(3), radius * 0.2f);
        int inWeek = streak == 0 ? 0 : (streak - 1) % 7 + 1;
        using (var track = new Pen(pal.Fill, stroke))
            g.DrawEllipse(track, cx - radius, cy - radius, radius * 2, radius * 2);
        if (streak > 0)
            using (var arc = new Pen(pal.Orange, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, cx - radius, cy - radius, radius * 2, radius * 2, -90, 360f * inWeek / 7f);
        Label(g, streak.ToString(CultureInfo.InvariantCulture), F(17, "semibold"), pal.Fg,
            cx, cy - F(17, "semibold").GetHeight(g) / 2, radius * 2, StringAlignment.Center);
        Label(g, streak == 1 ? "day" : "days", small, pal.Fg2, cx, cy + radius + P(5), radius * 2.4f, StringAlignment.Center);

        float left = cx + radius + P(16), width = W - m - left;
        Label(g, "JOURNAL", label, pal.Fg2, left, m, width, StringAlignment.Near);

        string headline = written ? Settings.GetText("last", "Entry saved.") : Prompt();
        Paragraph(g, headline, prompt, pal.Fg, new RectangleF(left, m + label.GetHeight(g) * 1.8f, width, prompt.GetHeight(g) * 2.2f));

        // One button, and the line that says when the last entry went in.
        float buttonHeight = P(30), buttonWidth = Math.Min(width, P(84));
        button = new RectangleF(left, H - m - buttonHeight, buttonWidth, buttonHeight);
        using (var shape = RoundRect(button, buttonHeight / 2))
        using (var fill = new SolidBrush(written ? pal.Fill : pal.Fg))
            g.FillPath(fill, shape);
        // On a filled button the label takes the card's own colour, which is white in Vibrant.
        Label(g, written ? "Written" : "Write", button13, written ? pal.Fg : IsVibrant ? pal.OnAccent : CardBackground(pal),
            button.X + button.Width / 2, button.Y + (buttonHeight - button13.GetHeight(g)) / 2, button.Width, StringAlignment.Center);

        string when = preview ? "Last entry yesterday, 9:40 PM" : Settings.Get("lastat", "").Length > 0
            ? "Last entry " + Pretty(Settings.Get("lastat", "")) : "No entries yet";
        Label(g, when, small, pal.Fg2, button.Right + P(12), button.Y + (buttonHeight - small.GetHeight(g)) / 2,
            W - m - button.Right - P(12), StringAlignment.Near);
    }

    static string Pretty(string stamp)
    {
        DateTime when;
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
            return stamp;
        string time = when.ToString("h:mm tt", CultureInfo.InvariantCulture);
        if (when.Date == DateTime.Today) return "today, " + time;
        if (when.Date == DateTime.Today.AddDays(-1)) return "yesterday, " + time;
        return when.ToString("d MMM", CultureInfo.InvariantCulture) + ", " + time;
    }
}
