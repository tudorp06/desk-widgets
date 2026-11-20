using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using DeskWidgets;
using DeskWidgets.Widgets;

// Four habits, each with today's circle, the last seven days as dots and the run of days behind
// it. Click a circle to tick the habit off. The names and the days you ticked are kept in the
// widget's own settings on this PC.
[DeskWidget(Name = "Habits", Description = "Tick off a few daily habits and keep the streak in sight")]
public sealed class Habits : CardWidget
{
    const int Count = 4, Week = 7, Keep = 400;

    static readonly string[] DefaultNames = { "Read 20 pages", "Walk 8,000 steps", "No screens after 11", "Write the log" };
    static readonly int[][] SampleDays =                   // only for the library card
    {
        new[] { 0, 1, 2, 4, 5 }, new[] { 1, 2, 3, 4, 5, 6 }, new[] { 0, 2, 3, 6 }, new[] { 0, 1, 3, 4 }
    };

    readonly RectangleF[] circles = new RectangleF[Count];

    public Habits(Section settings) : base(settings, true) { }

    // ---------- what is kept ----------
    string Name(int i) { return Settings.GetText("name" + i, DefaultNames[i]); }

    // The days a habit was ticked, newest first, as yyyyMMdd.
    List<string> Done(int i)
    {
        var days = new List<string>();
        foreach (string part in Settings.Get("done" + i, "").Split(','))
            if (part.Length == 8) days.Add(part);
        return days;
    }

    void SetDone(int i, List<string> days)
    {
        days.Sort(StringComparer.Ordinal);
        days.Reverse();
        if (days.Count > Keep) days.RemoveRange(Keep, days.Count - Keep);
        Settings.Set("done" + i, string.Join(",", days.ToArray()));
    }

    static string Key(DateTime day) { return day.ToString("yyyyMMdd", CultureInfo.InvariantCulture); }

    bool DoneOn(int i, DateTime day) { return Done(i).Contains(Key(day)); }

    void Toggle(int i)
    {
        List<string> days = Done(i);
        string today = Key(DateTime.Today);
        if (days.Contains(today)) days.Remove(today); else days.Add(today);
        SetDone(i, days);
        Changed();
    }

    // Days in a row up to today; a habit not yet ticked today still shows the run behind it.
    int Streak(int i)
    {
        List<string> days = Done(i);
        if (days.Count == 0) return 0;
        int streak = 0;
        DateTime day = DateTime.Today;
        if (!days.Contains(Key(day))) day = day.AddDays(-1);
        while (days.Contains(Key(day))) { streak++; day = day.AddDays(-1); }
        return streak;
    }

    // ---------- interaction ----------
    protected override bool IsButton(Point p)
    {
        foreach (RectangleF r in circles) if (r.Contains(p)) return true;
        return false;
    }

    protected override void OnWidgetClick(Point p)
    {
        for (int i = 0; i < circles.Length; i++)
            if (circles[i].Contains(p)) { Toggle(i); return; }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Edit Habits…", delegate
        {
            var fields = new InputDialog.Field[Count];
            for (int i = 0; i < Count; i++)
                fields[i] = new InputDialog.Field { Label = "Habit " + (i + 1), Value = Name(i) };
            string[] answer = InputDialog.Ask("Habits", "Four things to keep up with. Leave one empty to hide it.", fields, "Save");
            if (answer == null) return;
            for (int i = 0; i < Count; i++) Settings.SetText("name" + i, (answer[i] ?? "").Trim());
            Changed();
        });
        menu.Add("Clear Today", delegate
        {
            for (int i = 0; i < Count; i++)
            {
                List<string> days = Done(i);
                days.Remove(Key(DateTime.Today));
                SetDone(i, days);
            }
            Changed();
        });
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16);
        Font label = F(11, "semibold"), name = F(13, null);
        bool preview = !IsHandleCreated;
        DateTime today = DateTime.Today;

        var rows = new List<int>();
        for (int i = 0; i < Count; i++) if (Name(i).Length > 0) rows.Add(i);
        if (rows.Count == 0) rows.Add(0);

        int ticked = 0;
        for (int r = 0; r < rows.Count; r++)
            if (preview ? Array.IndexOf(SampleDays[rows[r]], 6) >= 0 : DoneOn(rows[r], today)) ticked++;

        Label(g, "HABITS", label, pal.Fg2, m, m, W * 0.5f, StringAlignment.Near);
        Label(g, ticked.ToString(CultureInfo.InvariantCulture) + " of " + rows.Count.ToString(CultureInfo.InvariantCulture) + " today",
            label, pal.Fg2, W - m, m, W * 0.45f, StringAlignment.Far);

        for (int i = 0; i < circles.Length; i++) circles[i] = RectangleF.Empty;

        float top = m + label.GetHeight(g) * 1.7f, height = (H - m - top) / rows.Count;
        float tick = Math.Min(P(18), height * 0.52f);
        for (int r = 0; r < rows.Count; r++)
        {
            int i = rows[r];
            float cy = top + height * (r + 0.5f);
            if (r > 0)
                using (var line = new Pen(pal.Sep, 1))
                    g.DrawLine(line, m, top + height * r, W - m, top + height * r);

            bool done = preview ? Array.IndexOf(SampleDays[i], 6) >= 0 : DoneOn(i, today);
            var box = new RectangleF(m, cy - tick / 2, tick, tick);
            circles[i] = RectangleF.Inflate(box, P(6), P(6));
            if (done)
            {
                FillCircle(g, pal.Blue, box.X + tick / 2, cy, tick / 2);
                using (var mark = new Pen(pal.OnAccent, Math.Max(1.4f, tick * 0.13f))
                    { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLines(mark, new[]
                    {
                        new PointF(box.X + tick * 0.27f, cy + tick * 0.02f),
                        new PointF(box.X + tick * 0.43f, cy + tick * 0.19f),
                        new PointF(box.X + tick * 0.73f, cy - tick * 0.19f)
                    });
            }
            else
                using (var ring = new Pen(pal.Sep, Math.Max(1.4f, tick * 0.11f)))
                    g.DrawEllipse(ring, box.X, box.Y, tick, tick);

            // The week: seven dots, the last one today.
            float dot = Math.Max(P(2.5f), height * 0.07f), gap = dot * 3f;
            float weekWidth = gap * (Week - 1) + dot * 2;
            float streakRight = W - m;
            string streak = (preview ? SampleDays[i].Length : Streak(i)).ToString(CultureInfo.InvariantCulture) + " d";
            float streakWidth = Math.Max(P(26), TextWidth(g, streak, label) + P(6));
            float weekLeft = streakRight - streakWidth - weekWidth;

            Label(g, Name(i), name, done ? pal.Fg2 : pal.Fg, m + tick + P(10),
                cy - name.GetHeight(g) / 2, weekLeft - (m + tick + P(16)), StringAlignment.Near);

            for (int d = 0; d < Week; d++)
            {
                DateTime day = today.AddDays(d - (Week - 1));
                bool on = preview ? Array.IndexOf(SampleDays[i], d) >= 0 : DoneOn(i, day);
                FillCircle(g, on ? pal.Blue : pal.Sep, weekLeft + dot + gap * d, cy, dot);
            }
            Label(g, streak, label, pal.Fg2, streakRight, cy - label.GetHeight(g) / 2, streakWidth, StringAlignment.Far);
        }
    }
}
