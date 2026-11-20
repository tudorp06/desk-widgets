using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace DeskWidgets
{
    // The part of a screen widgets are allowed to live in, drawn by hand in arrange mode.
    //
    // This is how the app knows to stay off your desktop icons, your taskbar and whatever part of
    // the wallpaper you actually want to see -- by being told, rather than by reading the icon
    // list out of Explorer's process or guessing from the picture. One rectangle per monitor.
    //
    // Stored as fractions of that monitor's working area, like widget positions themselves, so an
    // area drawn at 1080p still means the same part of the screen at 4K.
    static class WidgetArea
    {
        // Breathing room when no area has been drawn (§17.13 margins).
        static int Margin { get { return WidgetWindow.Px(32); } }

        // Below this an area is too small to arrange anything in, and is ignored.
        static int Least { get { return WidgetWindow.Px(200); } }

        public static Rectangle Default(Screen s)
        {
            Rectangle r = s.WorkingArea;
            r.Inflate(-Margin, -Margin);
            return r.Width < Least || r.Height < Least ? s.WorkingArea : r;
        }

        public static bool IsCustom(Screen s)
        {
            return Read(s) != null;
        }

        public static Rectangle For(Screen s)
        {
            Rectangle? saved = Read(s);
            return saved.HasValue ? saved.Value : Default(s);
        }

        public static void Set(Screen s, Rectangle screenRect)
        {
            Rectangle wa = s.WorkingArea;
            Rectangle clamped = Rectangle.Intersect(screenRect, wa);
            if (clamped.Width < Least || clamped.Height < Least) return;

            string value = string.Join(",", new[]
            {
                Text((clamped.Left - wa.Left) / (float)wa.Width),
                Text((clamped.Top - wa.Top) / (float)wa.Height),
                Text(clamped.Width / (float)wa.Width),
                Text(clamped.Height / (float)wa.Height)
            });
            if (App.Current != null) App.Current.SetAppSetting(Key(s), value);
        }

        public static void Clear(Screen s)
        {
            if (App.Current != null) App.Current.SetAppSetting(Key(s), null);
        }

        static Rectangle? Read(Screen s)
        {
            if (App.Current == null) return null;
            string raw = App.Current.GetAppSetting(Key(s), "");
            string[] parts = raw.Split(',');
            if (parts.Length != 4) return null;

            float fx, fy, fw, fh;
            if (!Num(parts[0], out fx) || !Num(parts[1], out fy) || !Num(parts[2], out fw) || !Num(parts[3], out fh))
                return null;

            Rectangle wa = s.WorkingArea;
            var r = new Rectangle(wa.Left + (int)Math.Round(fx * wa.Width),
                                  wa.Top + (int)Math.Round(fy * wa.Height),
                                  (int)Math.Round(fw * wa.Width),
                                  (int)Math.Round(fh * wa.Height));
            r = Rectangle.Intersect(r, wa);
            // A screen that shrank can leave a saved area too small to use; fall back rather than
            // arrange into a sliver.
            return r.Width < Least || r.Height < Least ? (Rectangle?)null : r;
        }

        // The device name is "\\.\DISPLAY6"; keep the part that is safe in an INI key.
        static string Key(Screen s)
        {
            var kept = s.DeviceName.Where(char.IsLetterOrDigit).ToArray();
            return "area_" + new string(kept);
        }

        static string Text(float v)
        {
            return Math.Max(0f, Math.Min(1f, v)).ToString("0.####", CultureInfo.InvariantCulture);
        }

        static bool Num(string s, out float v)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
