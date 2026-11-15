using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using DeskWidgets;
using DeskWidgets.Widgets;

// A digital clock with no plate behind it: a small date line over the time in a large serif,
// sitting straight on the wallpaper. Appearance switches between white or dark text on the
// wallpaper, a dark or light card, and the frosted Vibrant card. Local time; nothing is fetched.
[DeskWidget(Name = "Nothing Digital Clock", Description = "Large serif time and date, straight on the wallpaper")]
public sealed class NothingDigitalClock : CardWidget
{
    static readonly Color Ink = Rgb(0x1B1B1D), CardSoft = Rgb(0x8C8C90);

    // A high-contrast serif with lining figures: the first of these that is installed.
    static readonly string Serif = PickFont("NType 82 Headline", "Bodoni Moda", "Bodoni MT", "Didot", "Times New Roman");

    public NothingDigitalClock(Section settings) : base(settings, true) { }

    protected override string DefaultFace { get { return "clear"; } }
    protected override string[][] AppearanceChoices
    {
        get
        {
            return new[]
            {
                new[] { "clear", "White Text" }, new[] { "clear-dark", "Dark Text" },
                new[] { "dark", "Dark Card" }, new[] { "light", "Light Card" }, new[] { "vibrant", "Vibrant" }
            };
        }
    }

    bool Clear { get { return Face == "clear" || Face == "clear-dark"; } }
    bool DarkText { get { return Face == "clear-dark" || Face == "light"; } }
    bool Use24 { get { return Settings.GetBool("h24", false); } }

    // Without a card the fill is nearly transparent, so the whole rectangle still takes the mouse.
    protected override Color CardBackground(Palette pal)
    {
        if (Clear) return Color.FromArgb(1, 0, 0, 0);
        return Face == "light" ? Color.White : Ink;
    }

    // Redraw just after each minute boundary.
    protected override int NextFrameDelay()
    {
        if (!IsHandleCreated) return -1;
        DateTime now = DateTime.Now;
        return (60 - now.Second) * 1000 - now.Millisecond + 50;
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        bool h24 = Use24;
        menu.AddCheck("24-Hour Time", h24, delegate { Settings.Set("h24", !h24); Changed(); });
        bool date = Settings.GetBool("date", true);
        menu.AddCheck("Show Date", date, delegate { Settings.Set("date", !date); Changed(); });
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        DateTime now = DateTime.Now;
        string time = Use24
            ? now.ToString("H:mm", CultureInfo.InvariantCulture)
            : (now.Hour % 12 == 0 ? 12 : now.Hour % 12).ToString(CultureInfo.InvariantCulture) + now.ToString(":mm", CultureInfo.InvariantCulture);
        string date = now.ToString("ddd, d MMM", CultureInfo.CurrentCulture);
        bool showDate = Settings.GetBool("date", true);

        Color main = DarkText ? Ink : Color.White;
        Color soft = Clear ? Color.FromArgb(DarkText ? 175 : 215, main) : IsVibrant ? pal.Fg2 : CardSoft;

        float m = Math.Min(W, H) * 0.09f;
        float dateSize = Math.Max(8f, H * 0.095f), gap = H * 0.07f;
        float dateH = showDate ? dateSize * 1.25f + gap : 0;

        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (var family = new FontFamily(Serif))
        using (var path = new GraphicsPath())
        {
            // Outline at a nominal size, then scale it to fill the space left under the date.
            path.AddString(time, family, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            float scale = Math.Min((W - m * 2) / b.Width, (H - m * 2 - dateH) / b.Height);
            float w = b.Width * scale, h = b.Height * scale;
            // Centred both ways; the date line starts where the time does.
            float left = (W - w) / 2, top = (H - (dateH + h)) / 2 + dateH;
            using (var fit = new Matrix())
            {
                fit.Translate(left - b.X * scale, top - b.Y * scale);
                fit.Scale(scale, scale);
                path.Transform(fit);
            }

            // On the bare wallpaper, white text gets a soft shadow so it holds up on bright pictures.
            bool shadow = Clear && !DarkText;
            if (shadow)
            {
                float d = Math.Max(1f, h * 0.012f);
                using (var shade = new SolidBrush(Color.FromArgb(34, 0, 0, 0)))
                    for (int i = 1; i <= 3; i++)
                    {
                        GraphicsState state = g.Save();
                        g.TranslateTransform(0, d * i);
                        g.FillPath(shade, path);
                        g.Restore(state);
                    }
            }
            using (var brush = new SolidBrush(main)) g.FillPath(brush, path);

            if (showDate)
            {
                using (var font = new Font("Segoe UI", dateSize, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    fmt.FormatFlags |= StringFormatFlags.NoWrap;
                    float x = left + h * 0.03f, y = top - dateH;
                    if (shadow)
                        using (var shade = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                            g.DrawString(date, font, shade, x, y + 1f, fmt);
                    using (var brush = new SolidBrush(soft)) g.DrawString(date, font, brush, x, y, fmt);
                }
            }
        }
    }

    static string PickFont(params string[] wanted)
    {
        using (var installed = new InstalledFontCollection())
        {
            foreach (string name in wanted)
                foreach (FontFamily f in installed.Families)
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return wanted[wanted.Length - 1];
    }
}
