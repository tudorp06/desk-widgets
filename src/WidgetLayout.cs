using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DeskWidgets
{
    // Arranging widgets so they look placed rather than packed.
    //
    // The first version of this snapped everything to a fixed 170 pt cell grid, which is why the
    // gaps were so wide: a widget only filled its cells at exactly 170 or 364 across, so a Compact
    // (144) or Large (212) one claimed a whole extra cell and left the rest of it empty. There are
    // no cells here. Widgets are grouped by width, each group gets columns of its own width, and
    // every gap in the result is exactly one gutter.
    //
    // On top of that sits a chooser: several styles are laid out, each at several anchors, and the
    // one that scores best wins. Tidy Up takes the best; Shuffle takes the best arrangement that is
    // built differently from the one already on screen, so pressing it walks through genuinely
    // different compositions instead of re-rolling the same silhouette.
    static class WidgetLayout
    {
        public enum Anchor { TopRight, TopLeft, BottomRight, BottomLeft }

        public static readonly Anchor[] Anchors =
            { Anchor.TopRight, Anchor.TopLeft, Anchor.BottomRight, Anchor.BottomLeft };

        public static string NameOf(Anchor a)
        {
            switch (a)
            {
                case Anchor.TopLeft: return "Top Left";
                case Anchor.BottomRight: return "Bottom Right";
                case Anchor.BottomLeft: return "Bottom Left";
                default: return "Top Right";
            }
        }

        public static Anchor Parse(string s)
        {
            foreach (var a in Anchors)
                if (string.Equals(NameOf(a), s, StringComparison.OrdinalIgnoreCase)) return a;
            return Anchor.TopRight;
        }

        static bool IsLeft(Anchor a) { return a == Anchor.TopLeft || a == Anchor.BottomLeft; }
        static bool IsTop(Anchor a) { return a == Anchor.TopLeft || a == Anchor.TopRight; }

        // One gutter everywhere. Snug enough that a cluster reads as a single panel.
        public static int Gutter { get { return WidgetWindow.Px(12); } }

        // ---- entry points ----

        public static void TidyAll(bool shuffle)
        {
            if (App.Current == null) return;
            Tidy(App.Current.OpenWidgets, shuffle);
        }

        // Arranges these widgets, each inside the widget area of the screen it is on.
        public static void Tidy(IEnumerable<WidgetWindow> widgets, bool shuffle)
        {
            if (App.Current == null) return;
            var wanted = widgets.Where(w => !w.IsDisposed && w.IsHandleCreated).ToList();
            var moves = new Dictionary<WidgetWindow, Point>();

            foreach (var screen in Screen.AllScreens)
            {
                var here = wanted.Where(w => !w.IsLocked && On(w, screen)).ToList();
                if (here.Count == 0) continue;

                Rectangle area = WidgetArea.For(screen);
                // Locked widgets keep their ground; everything else is arranged around them.
                var blocked = App.Current.OpenWidgets
                    .Where(w => !w.IsDisposed && w.IsLocked && On(w, screen))
                    .Select(Bounds).ToList();

                Plan plan = Choose(here, area, blocked, shuffle);
                if (plan == null) continue;
                foreach (var p in plan.Items) moves[p.W] = p.R.Location;
                Remember(plan);
            }

            Apply(moves);
        }

        static bool On(WidgetWindow w, Screen s)
        {
            return Screen.FromRectangle(Bounds(w)).DeviceName == s.DeviceName;
        }

        static Rectangle Bounds(WidgetWindow w) { return new Rectangle(w.Location, w.WidgetSize); }

        // ---- a laid-out arrangement ----

        sealed class Spot
        {
            public WidgetWindow W;
            public Rectangle R;
        }

        sealed class Plan
        {
            public List<Spot> Items;
            public string Style;
            public Anchor At;
            public Rectangle Box;
            public float Score;
        }

        // ---- choosing between arrangements ----

        // Tidy Up wants the best arrangement there is. Shuffle wants a different one that is still
        // good -- so it rolls the packing itself as well as the style, then picks at random from
        // the ones that scored near the top. Repeating it keeps producing new compositions instead
        // of walking a fixed order, and the score floor is what stops it ever landing on a mess.
        const float GoodEnough = 0.82f;  // of the best score
        const int Rolls = 6;             // randomised passes per shuffle

        static Plan Choose(List<WidgetWindow> ws, Rectangle area, List<Rectangle> blocked, bool shuffle)
        {
            var rng = shuffle ? new Random(unchecked(Environment.TickCount * 2654435761u).GetHashCode()) : null;
            var plans = new List<Plan>();

            // One clean pass, plus several rolled ones when shuffling: a roll changes which widget
            // lands in which column, which family sits on the outside, and how far the stair steps.
            for (int pass = 0; pass <= (shuffle ? Rolls : 0); pass++)
            {
                Random r = pass == 0 ? null : rng;
                foreach (var anchor in Anchors)
                    foreach (var plan in Build(ws, area, anchor, r))
                    {
                        if (!area.Contains(plan.Box)) continue;          // must fit the widget area
                        if (blocked.Any(b => plan.Items.Any(i => i.R.IntersectsWith(b)))) continue;
                        if (Overlaps(plan)) continue;                    // two clusters collided
                        plan.Score = Rate(plan, area, ws);
                        plans.Add(plan);
                    }
            }

            if (plans.Count == 0) return null;
            plans = plans.OrderByDescending(p => p.Score).ToList();
            if (!shuffle) return plans[0];

            float floor = plans[0].Score * GoodEnough;
            var recent = Recent();
            var pool = plans.Where(p => p.Score >= floor && !recent.Contains(Key(p))).ToList();
            if (pool.Count == 0) pool = plans.Where(p => p.Score >= floor).ToList();
            if (pool.Count == 0) pool = plans;
            return Weighted(pool, rng);
        }

        // Random, but leaning towards the better arrangements: a plan twice as far above the floor
        // is four times as likely to come up.
        static Plan Weighted(List<Plan> pool, Random rng)
        {
            double total = 0;
            var weights = new double[pool.Count];
            float worst = pool.Min(p => p.Score);
            for (int i = 0; i < pool.Count; i++)
            {
                double edge = pool[i].Score - worst + 0.02;
                weights[i] = edge * edge;
                total += weights[i];
            }
            double pick = rng.NextDouble() * total;
            for (int i = 0; i < pool.Count; i++)
            {
                pick -= weights[i];
                if (pick <= 0) return pool[i];
            }
            return pool[pool.Count - 1];
        }

        // The two-cluster styles place each half independently, so on a small widget area they can
        // run into each other. Nothing else can overlap, but checking them all is cheap.
        static bool Overlaps(Plan plan)
        {
            for (int i = 0; i < plan.Items.Count; i++)
                for (int j = i + 1; j < plan.Items.Count; j++)
                    if (plan.Items[i].R.IntersectsWith(plan.Items[j].R)) return true;
            return false;
        }

        static string Key(Plan p) { return p.Style + ":" + p.At; }

        // The last few arrangements, so Shuffle doesn't bounce between the same two.
        const int Memory = 3;

        static List<string> Recent()
        {
            if (App.Current == null) return new List<string>();
            return App.Current.LastArrangement.Split('|').Where(s => s.Length > 0).ToList();
        }

        static void Remember(Plan p)
        {
            if (App.Current == null) return;
            var keys = Recent();
            keys.Remove(Key(p));
            keys.Insert(0, Key(p));
            App.Current.LastArrangement = string.Join("|", keys.Take(Memory).ToArray());
        }

        // How good an arrangement looks, 0..1. No term here reads the wallpaper: the result is the
        // same on every machine and every desktop, which is the point -- where widgets are allowed
        // to go is the widget area, and that is the user's call, not a guess from the picture.
        static float Rate(Plan plan, Rectangle area, List<WidgetWindow> ws)
        {
            long used = plan.Items.Sum(i => (long)i.R.Width * i.R.Height);
            long box = Math.Max(1L, (long)plan.Box.Width * plan.Box.Height);

            // Tight: how much of the cluster's footprint is actually widget.
            float compact = Math.Min(1f, used / (float)box);

            // Even: a straight bottom edge is most of what "tidy" looks like.
            float ragged = Raggedness(plan);

            // Shapely: a cluster shaped like the space it sits in, rather than a long thin line.
            float aspect = Fit(Ratio(plan.Box), Ratio(area));

            // Gentle: Tidy Up should look like the widgets were nudged, not teleported.
            float travel = 1f - Math.Min(1f, (float)(Travel(plan) / Diagonal(area)));

            return 0.40f * compact + 0.25f * ragged + 0.20f * aspect + 0.15f * travel;
        }

        static float Ratio(Rectangle r) { return r.Height <= 0 ? 1f : r.Width / (float)r.Height; }

        static float Fit(float a, float b)
        {
            if (a <= 0 || b <= 0) return 0f;
            float d = Math.Abs((float)Math.Log(a / b));
            return Math.Max(0f, 1f - d / 2f);
        }

        // 1 when every column ends on the same line, falling off as they get uneven.
        static float Raggedness(Plan plan)
        {
            var bottoms = plan.Items.GroupBy(i => i.R.Left).Select(g => g.Max(i => i.R.Bottom)).ToList();
            if (bottoms.Count < 2) return 1f;
            float mean = (float)bottoms.Average();
            float var0 = bottoms.Sum(b => (b - mean) * (b - mean)) / bottoms.Count;
            float sd = (float)Math.Sqrt(var0);
            return Math.Max(0f, 1f - sd / Math.Max(1f, plan.Box.Height));
        }

        static double Travel(Plan plan)
        {
            double total = 0;
            foreach (var i in plan.Items)
            {
                double dx = i.R.X - i.W.Location.X, dy = i.R.Y - i.W.Location.Y;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
            return total / Math.Max(1, plan.Items.Count);
        }

        static double Diagonal(Rectangle r)
        {
            return Math.Max(1.0, Math.Sqrt((double)r.Width * r.Width + (double)r.Height * r.Height));
        }

        // ---- the styles ----

        // Every style is the same column packer wearing a different number of columns and a
        // different rule for where the result goes.
        static IEnumerable<Plan> Build(List<WidgetWindow> ws, Rectangle area, Anchor at, Random rng)
        {
            int g = Gutter;
            var plans = new List<Plan>();

            // Block: the tight cluster. One to six columns, let the score pick.
            for (int k = 1; k <= Math.Min(6, ws.Count); k++)
            {
                Size size;
                var spots = Grouped(ws, k, g, rng, out size);
                plans.Add(Settle("Block" + k, at, spots, size, area, 0));
            }

            // Column: a single stack down one side, widths centred on each other.
            {
                Size size;
                var spots = Stack(ws, g, rng, out size);
                plans.Add(Settle("Column", at, spots, size, area, 0));
            }

            // Band: one row along an edge.
            if (ws.Count > 1)
            {
                Size size;
                var spots = Row(ws, g, rng, out size);
                plans.Add(Settle("Band", at, spots, size, area, 0));
            }

            // Stair: the block, with each column dropped a little further than the last.
            if (ws.Count > 2)
            {
                Size size;
                int cols = rng == null ? Math.Min(4, ws.Count) : 2 + rng.Next(Math.Max(1, Math.Min(3, ws.Count - 1)));
                int step = rng == null ? WidgetWindow.Px(28) : WidgetWindow.Px(18 + rng.Next(28));
                var spots = Grouped(ws, cols, g, rng, out size);
                plans.Add(Settle("Stair", at, spots, size, area, step));
            }

            // Split: two clusters in opposite corners, so the middle of the desktop stays clear.
            if (ws.Count > 3)
            {
                var halves = Halve(ws, rng);
                Size aSize, bSize;
                var one = Grouped(halves.Key, Math.Min(2, halves.Key.Count), g, rng, out aSize);
                var two = Grouped(halves.Value, Math.Min(2, halves.Value.Count), g, rng, out bSize);
                var placedA = Put(one, Corner(area, at, aSize));
                var placedB = Put(two, Corner(area, Opposite(at), bSize));
                plans.Add(Wrap("Split", at, placedA.Concat(placedB).ToList()));
            }

            // Frame: a stack on each side, the whole middle of the wallpaper left alone.
            if (ws.Count > 3)
            {
                var halves = Halve(ws, rng);
                Size aSize, bSize;
                var one = Stack(halves.Key, g, rng, out aSize);
                var two = Stack(halves.Value, g, rng, out bSize);
                Anchor left = IsTop(at) ? Anchor.TopLeft : Anchor.BottomLeft;
                Anchor right = IsTop(at) ? Anchor.TopRight : Anchor.BottomRight;
                var placedA = Put(one, Corner(area, left, aSize));
                var placedB = Put(two, Corner(area, right, bSize));
                plans.Add(Wrap("Frame", at, placedA.Concat(placedB).ToList()));
            }

            return plans.Where(p => p != null);
        }

        // Tallest first is what levels the columns, so it's the clean pass. A rolled pass takes a
        // different order on purpose: same tidy geometry, different widget in each slot.
        static List<WidgetWindow> Order(IEnumerable<WidgetWindow> ws, Random rng, Func<WidgetWindow, int> by)
        {
            var list = ws.OrderByDescending(by).ToList();
            if (rng == null) return list;
            switch (rng.Next(3))
            {
                case 0: return list;                                        // keep it level
                case 1: list.Reverse(); return list;                        // smallest first
                default:                                                    // anything goes
                    for (int i = list.Count - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        var t = list[i]; list[i] = list[j]; list[j] = t;
                    }
                    return list;
            }
        }

        static Anchor Opposite(Anchor a)
        {
            switch (a)
            {
                case Anchor.TopLeft: return Anchor.BottomRight;
                case Anchor.TopRight: return Anchor.BottomLeft;
                case Anchor.BottomLeft: return Anchor.TopRight;
                default: return Anchor.TopLeft;
            }
        }

        // Splits into two groups of roughly equal area, biggest first.
        static KeyValuePair<List<WidgetWindow>, List<WidgetWindow>> Halve(List<WidgetWindow> ws, Random rng)
        {
            var a = new List<WidgetWindow>();
            var b = new List<WidgetWindow>();
            long areaA = 0, areaB = 0;
            foreach (var w in Order(ws, rng, x => x.WidgetSize.Width * x.WidgetSize.Height))
            {
                long s = (long)w.WidgetSize.Width * w.WidgetSize.Height;
                if (areaA <= areaB) { a.Add(w); areaA += s; } else { b.Add(w); areaB += s; }
            }
            return new KeyValuePair<List<WidgetWindow>, List<WidgetWindow>>(a, b);
        }

        // ---- the packer ----

        // Widgets of a similar width belong in the same column, so every column edge lines up and
        // no widget is ever padded out to someone else's width.
        static List<List<WidgetWindow>> Families(List<WidgetWindow> ws, int tolerance)
        {
            var families = new List<List<WidgetWindow>>();
            foreach (var w in ws.OrderByDescending(x => x.WidgetSize.Width))
            {
                var found = families.FirstOrDefault(f =>
                    Math.Abs(f[0].WidgetSize.Width - w.WidgetSize.Width) <= tolerance);
                if (found == null) { found = new List<WidgetWindow>(); families.Add(found); }
                found.Add(w);
            }
            return families;
        }

        // k columns in all, shared out between the width families, widest family first.
        static List<Spot> Grouped(List<WidgetWindow> ws, int k, int g, Random rng, out Size size)
        {
            var families = Families(ws, g);
            // Widest family on the outside normally; a rolled pass may flip which side it's on.
            if (rng != null && rng.Next(2) == 0) families.Reverse();
            var counts = new int[families.Count];
            for (int i = 0; i < counts.Length; i++) counts[i] = 1;

            // Spare columns go to whichever family is stacked highest, which is what evens out
            // the bottom edge.
            for (int spare = k - families.Count; spare > 0; spare--)
            {
                int best = -1;
                float tallest = 0;
                for (int i = 0; i < families.Count; i++)
                {
                    if (counts[i] >= families[i].Count) continue;
                    float h = families[i].Sum(w => w.WidgetSize.Height + g) / (float)counts[i];
                    if (h > tallest) { tallest = h; best = i; }
                }
                if (best < 0) break;
                counts[best]++;
            }

            var spots = new List<Spot>();
            int x = 0, maxBottom = 0;
            for (int i = 0; i < families.Count; i++)
            {
                Size fs;
                var laid = Pack(families[i], counts[i], g, rng, out fs);
                foreach (var s in laid) { s.R.Offset(x, 0); spots.Add(s); }
                x += fs.Width + g;
                maxBottom = Math.Max(maxBottom, fs.Height);
            }
            size = new Size(Math.Max(0, x - g), maxBottom);
            return spots;
        }

        // One family into n equal-width columns, each widget going to the shortest column so far.
        // Tallest first is what keeps the columns level (the classic scheduling heuristic).
        static List<Spot> Pack(List<WidgetWindow> family, int n, int g, Random rng, out Size size)
        {
            n = Math.Max(1, Math.Min(n, family.Count));
            int colW = family.Max(w => w.WidgetSize.Width);
            var heights = new int[n];
            var spots = new List<Spot>();

            foreach (var w in Order(family, rng, x => x.WidgetSize.Height))
            {
                int c = 0;
                for (int i = 1; i < n; i++) if (heights[i] < heights[c]) c = i;
                Size sz = w.WidgetSize;
                spots.Add(new Spot
                {
                    W = w,
                    R = new Rectangle(c * (colW + g) + (colW - sz.Width) / 2, heights[c], sz.Width, sz.Height)
                });
                heights[c] += sz.Height + g;
            }

            size = new Size(n * (colW + g) - g, heights.Max() - g);
            return spots;
        }

        // Everything in one column, narrower widgets centred on the widest.
        static List<Spot> Stack(List<WidgetWindow> ws, int g, Random rng, out Size size)
        {
            int colW = ws.Max(w => w.WidgetSize.Width);
            var spots = new List<Spot>();
            int y = 0;
            foreach (var w in Order(ws, rng, x => x.WidgetSize.Width))
            {
                Size sz = w.WidgetSize;
                spots.Add(new Spot { W = w, R = new Rectangle((colW - sz.Width) / 2, y, sz.Width, sz.Height) });
                y += sz.Height + g;
            }
            size = new Size(colW, Math.Max(0, y - g));
            return spots;
        }

        // Everything in one row, tops level, tallest first.
        static List<Spot> Row(List<WidgetWindow> ws, int g, Random rng, out Size size)
        {
            var spots = new List<Spot>();
            int x = 0, h = 0;
            foreach (var w in Order(ws, rng, x2 => x2.WidgetSize.Height))
            {
                Size sz = w.WidgetSize;
                spots.Add(new Spot { W = w, R = new Rectangle(x, 0, sz.Width, sz.Height) });
                x += sz.Width + g;
                h = Math.Max(h, sz.Height);
            }
            size = new Size(Math.Max(0, x - g), h);
            return spots;
        }

        // ---- putting a laid-out cluster somewhere ----

        static Rectangle Corner(Rectangle area, Anchor at, Size size)
        {
            int x = IsLeft(at) ? area.Left : area.Right - size.Width;
            int y = IsTop(at) ? area.Top : area.Bottom - size.Height;
            return new Rectangle(x, y, size.Width, size.Height);
        }

        static List<Spot> Put(List<Spot> spots, Rectangle box)
        {
            foreach (var s in spots) s.R.Offset(box.Location);
            return spots;
        }

        // Moves a cluster to its corner, optionally stepping each column down as it goes.
        static Plan Settle(string style, Anchor at, List<Spot> spots, Size size, Rectangle area, int stagger)
        {
            if (spots.Count == 0) return null;

            if (stagger > 0)
            {
                var columns = spots.Select(s => s.R.Left).Distinct().OrderBy(v => v).ToList();
                foreach (var s in spots) s.R.Offset(0, columns.IndexOf(s.R.Left) * stagger);
                size = new Size(size.Width, size.Height + (columns.Count - 1) * stagger);
            }

            Put(spots, Corner(area, at, size));
            return Wrap(style, at, spots);
        }

        static Plan Wrap(string style, Anchor at, List<Spot> spots)
        {
            if (spots.Count == 0) return null;
            Rectangle box = spots[0].R;
            foreach (var s in spots) box = Rectangle.Union(box, s.R);
            return new Plan { Items = spots, Style = style, At = at, Box = box };
        }

        // ---- moving them there ----

        // Slides each widget to its place, a beat apart down the list (§11), then saves the
        // position the same way finishing a hand-drag does.
        static void Apply(Dictionary<WidgetWindow, Point> targets)
        {
            int i = 0;
            foreach (var kv in targets)
            {
                WidgetWindow w = kv.Key;
                Point from = w.Location, to = kv.Value;
                if (from == to) continue;
                Ui.Later(delegate
                {
                    if (w.IsDisposed || !w.IsHandleCreated) return;
                    Anim.Run(0f, 1f, 320, delegate(float t)
                    {
                        if (w.IsDisposed) return;
                        w.Location = new Point((int)Math.Round(from.X + (to.X - from.X) * t),
                                               (int)Math.Round(from.Y + (to.Y - from.Y) * t));
                    },
                    delegate
                    {
                        if (w.IsDisposed) return;
                        w.MoveTo(to);
                        Arrange.Refresh();
                    });
                }, 1 + i * 25);
                i++;
            }
        }
    }
}
