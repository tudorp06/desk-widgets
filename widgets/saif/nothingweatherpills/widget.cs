using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using DeskWidgets;
using DeskWidgets.Widgets;

// Small weather in three dark shapes: a pill with the current conditions drawn in dots, a
// circle with the temperature, and a circle with today's high and low.
// Forecast: MET Norway (api.met.no Locationforecast, CC BY 4.0), free and without an account.
// Only the place's coordinates are sent, rounded to four decimals as MET asks. The city name is
// looked up once with Open-Meteo's geocoder. Refreshes every 30 minutes; the last forecast is
// cached so it shows at startup and offline.
[DeskWidget(Name = "Nothing Weather Pills", Description = "Conditions, temperature, high and low in three small shapes")]
public sealed class NothingWeatherPills : CardWidget
{
    // Temperatures are kept in Celsius, as MET sends them, and converted when drawn.
    sealed class Day
    {
        public DateTime Date;
        public double Hi = double.MinValue, Lo = double.MaxValue;
        public string Symbol;
        public int SymbolRank; // how good the source of Symbol is: 3 daytime 12 h, 2 midday 6 h, 1 anything
    }

    sealed class Forecast
    {
        public double Temp;
        public string Symbol = "";
        public List<Day> Days = new List<Day>(); // in the place's own dates, oldest first
    }

    // Dark and Light use these; Vibrant keeps the frosted glass palette.
    static readonly Color DarkBg = Rgb(0x1B1B1D), LightBg = Color.White;
    static readonly Color DarkInk = Color.White, LightInk = Rgb(0x1B1B1D);
    static readonly Color DarkSoft = Rgb(0x8C8C90), LightSoft = Rgb(0x8E8E93);
    static readonly Color SignalRed = Rgb(0xD71921);

    Forecast data;
    string status;
    bool busy;
    readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };

    public NothingWeatherPills(Section settings) : base(settings, false)
    {
        refresh.Tick += delegate { Fetch(); };
        LoadCache();
    }

    string Place { get { return Settings.GetText("place", ""); } }
    bool HasPlace { get { return Settings.Get("lat", "").Length > 0; } }
    bool Fahrenheit { get { return Settings.Get("units", "c") == "f"; } }

    protected override bool SquareResize { get { return true; } }
    bool FrostOwnShape { get { return true; } }

    // No card: nearly transparent, so the gaps between the shapes still take the mouse.
    protected override Color CardBackground(Palette pal) { return Color.FromArgb(1, 0, 0, 0); }

    protected override void OnStart()
    {
        refresh.Start();
        Fetch();
    }

    // Redraw at the top of each hour so the forecast row moves on at midnight even offline.
    protected override int NextFrameDelay()
    {
        if (!IsHandleCreated) return -1;
        DateTime now = DateTime.Now;
        return (int)(now.Date.AddHours(now.Hour + 1) - now).TotalMilliseconds + 500;
    }

    // ---------- data ----------
    void LoadCache()
    {
        string file = CacheFile("nothing-weather-pills");
        if (file == null || !File.Exists(file)) return;
        try { data = Parse(File.ReadAllText(file), OffsetHours()); RememberToday(data); }
        catch (Exception) { data = null; }
    }

    // MET sends times in UTC and no time zone. The PC's own offset is used when the place is
    // roughly where the PC is; otherwise the offset is estimated from the longitude.
    double OffsetHours()
    {
        double lon;
        double pc = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalHours;
        if (!double.TryParse(Settings.Get("lon", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) return pc;
        return Math.Abs(pc - lon / 15) <= 1.5 ? pc : Math.Round(lon / 15);
    }

    DateTime PlaceToday() { return DateTime.UtcNow.AddHours(OffsetHours()).Date; }

    // The forecast only covers the hours still to come, so by evening today's high would be
    // missing. Keep the highest and lowest seen for today across refreshes.
    void RememberToday(Forecast f)
    {
        if (f == null || !HasPlace) return;
        DateTime today = PlaceToday();
        Day d = null;
        foreach (Day x in f.Days) if (x.Date == today) d = x;
        if (d == null) return;
        string key = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        double hi, lo;
        if (Settings.Get("seen_day", "") == key &&
            double.TryParse(Settings.Get("seen_hi", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out hi) &&
            double.TryParse(Settings.Get("seen_lo", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lo))
        {
            d.Hi = Math.Max(d.Hi, hi);
            d.Lo = Math.Min(d.Lo, lo);
            if (d.Hi == hi && d.Lo == lo) return; // nothing new to save
        }
        Settings.Set("seen_day", key);
        Settings.Set("seen_hi", d.Hi.ToString("R", CultureInfo.InvariantCulture));
        Settings.Set("seen_lo", d.Lo.ToString("R", CultureInfo.InvariantCulture));
    }

    void Fetch()
    {
        if (busy || !HasPlace) { Render(); return; }
        busy = true;
        double lat, lon;
        double.TryParse(Settings.Get("lat", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        double.TryParse(Settings.Get("lon", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
        string url = string.Format(CultureInfo.InvariantCulture,
            "https://api.met.no/weatherapi/locationforecast/2.0/complete?lat={0:0.####}&lon={1:0.####}", lat, lon);
        double offset = OffsetHours();
        string file = CacheFile("nothing-weather-pills");
        Background(() => Http.Get(url), (json, error) =>
        {
            busy = false;
            if (error != null) status = "Offline";
            else
            {
                try
                {
                    data = Parse(json, offset);
                    RememberToday(data);
                    status = null;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file, json);
                }
                catch (Exception) { status = "No forecast"; }
            }
            Render();
        });
    }

    static Forecast Parse(string json, double offsetHours)
    {
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        var series = (object[])((Dictionary<string, object>)root["properties"])["timeseries"];
        var f = new Forecast();
        DateTime nowUtc = DateTime.UtcNow, today = nowUtc.AddHours(offsetHours).Date;
        bool haveCurrent = false;
        foreach (object item in series)
        {
            var entry = (Dictionary<string, object>)item;
            DateTime utc = DateTime.ParseExact((string)entry["time"], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var d = (Dictionary<string, object>)entry["data"];
            double temp = Convert.ToDouble(((Dictionary<string, object>)((Dictionary<string, object>)d["instant"])["details"])["air_temperature"]);
            double t;
            string s1 = Symbol(d, "next_1_hours", out t, out t);
            double hi6, lo6;
            string s6 = Symbol(d, "next_6_hours", out hi6, out lo6);
            string s12 = Symbol(d, "next_12_hours", out t, out t);

            // Now: the latest entry that has started (a cached forecast moves along with the clock).
            if (!haveCurrent || utc <= nowUtc)
            {
                f.Temp = temp;
                f.Symbol = s1 ?? s6 ?? s12 ?? f.Symbol;
                haveCurrent = true;
            }

            DateTime local = utc.AddHours(offsetHours);
            // Past the hourly part of the forecast, entries are six hours apart and carry that
            // block's max and min. They count for the day the middle of the block falls in.
            if (s1 == null && !double.IsNaN(hi6) && !double.IsNaN(lo6))
            {
                Day block = DayFor(f, local.AddHours(3).Date, today);
                if (block != null) { block.Hi = Math.Max(block.Hi, hi6); block.Lo = Math.Min(block.Lo, lo6); }
            }
            Day day = DayFor(f, local.Date, today);
            if (day == null) continue;
            day.Hi = Math.Max(day.Hi, temp);
            day.Lo = Math.Min(day.Lo, temp);
            // The day's icon: the 12 hours from early morning if there is one, else the 6 hours around midday.
            int rank = 0;
            string symbol = null;
            if (s12 != null && local.Hour >= 4 && local.Hour <= 9) { rank = 3; symbol = s12; }
            else if (s6 != null && local.Hour >= 9 && local.Hour <= 14) { rank = 2; symbol = s6; }
            else if ((s1 ?? s6 ?? s12) != null) { rank = 1; symbol = s1 ?? s6 ?? s12; }
            if (rank > day.SymbolRank) { day.SymbolRank = rank; day.Symbol = symbol; }
        }
        if (!haveCurrent) throw new FormatException("empty forecast");
        // The last day can be a single midnight reading with no conditions: leave it out.
        while (f.Days.Count > 0 && f.Days[f.Days.Count - 1].SymbolRank == 0) f.Days.RemoveAt(f.Days.Count - 1);
        return f;
    }

    // The forecast day for a date, added in order when it's new; null for days already over.
    static Day DayFor(Forecast f, DateTime date, DateTime today)
    {
        if (date < today) return null;
        for (int i = f.Days.Count - 1; i >= 0; i--) if (f.Days[i].Date == date) return f.Days[i];
        var d = new Day { Date = date };
        f.Days.Add(d);
        return d;
    }

    // The symbol of one of an entry's look-ahead blocks, or null; and its max / min temperature (NaN when absent).
    static string Symbol(Dictionary<string, object> data, string block, out double max, out double min)
    {
        max = double.NaN; min = double.NaN;
        object o;
        if (!data.TryGetValue(block, out o)) return null;
        var b = (Dictionary<string, object>)o;
        object details;
        if (b.TryGetValue("details", out details) && details is Dictionary<string, object>)
        {
            var dd = (Dictionary<string, object>)details;
            object v;
            if (dd.TryGetValue("air_temperature_max", out v)) max = Convert.ToDouble(v);
            if (dd.TryGetValue("air_temperature_min", out v)) min = Convert.ToDouble(v);
        }
        object summary;
        if (!b.TryGetValue("summary", out summary)) return null;
        object code;
        return ((Dictionary<string, object>)summary).TryGetValue("symbol_code", out code) ? code as string : null;
    }

    void SetPlace()
    {
        var values = InputDialog.Ask("Weather location",
            "Type a city. Its name is looked up once with Open-Meteo; after that only its coordinates are sent to get the forecast.",
            new[] { new InputDialog.Field { Label = "City", Value = Place } }, "Find");
        if (values == null || values[0].Trim().Length == 0) return;
        string query = values[0].Trim();
        status = "Looking up " + query + "…";
        Render();
        Background(() => Http.Get("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(query)),
            (json, error) =>
            {
                if (error != null) { status = "Offline"; Render(); return; }
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                object[] results = root.ContainsKey("results") ? root["results"] as object[] : null;
                if (results == null || results.Length == 0) { status = "Couldn't find " + query; Render(); return; }
                var r = (Dictionary<string, object>)results[0];
                Settings.SetText("place", (string)r["name"]);
                Settings.Set("lat", Convert.ToDouble(r["latitude"]).ToString(CultureInfo.InvariantCulture));
                Settings.Set("lon", Convert.ToDouble(r["longitude"]).ToString(CultureInfo.InvariantCulture));
                data = null;
                status = null;
                Fetch();
            });
    }

    // ---------- interaction ----------
    protected override void OnWidgetClick(Point p)
    {
        if (!HasPlace) SetPlace();
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add(HasPlace ? "Change Location…" : "Set Location…", SetPlace);
        menu.Add("Refresh Now", Fetch);
        var units = menu.AddSubmenu("Units");
        units.AddChoice("Celsius (°C)", !Fahrenheit, delegate { Settings.Set("units", "c"); Changed(); });
        units.AddChoice("Fahrenheit (°F)", Fahrenheit, delegate { Settings.Set("units", "f"); Changed(); });
    }

    // ---------- drawing ----------
    Font textFont, tempFont;
    float fontsFor = -1f;

    void EnsureFonts(float s)
    {
        if (textFont != null && Math.Abs(s - fontsFor) < 0.01f) return;
        DisposeFonts();
        textFont = new Font("Segoe UI", Math.Max(1f, s * 0.074f), FontStyle.Regular, GraphicsUnit.Pixel);
        tempFont = new Font("Segoe UI Light", Math.Max(1f, s * 0.17f), FontStyle.Regular, GraphicsUnit.Pixel);
        fontsFor = s;
    }

    void DisposeFonts()
    {
        if (textFont != null) { textFont.Dispose(); textFont = null; }
        if (tempFont != null) { tempFont.Dispose(); tempFont = null; }
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        bool vibrant = IsVibrant;
        Color ink = vibrant ? pal.Fg : IsDark ? DarkInk : LightInk;
        Color soft = vibrant ? pal.Fg2 : IsDark ? DarkSoft : LightSoft;
        Forecast f = data ?? (IsHandleCreated ? null : Sample());

        // A pill across the top, two circles below, all the same height.
        float s = Math.Min(W, H), gap = s * 0.07f, d = (s - gap) / 2, inset = vibrant ? 1.5f : 0.5f;
        EnsureFonts(s);
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var pill = new RectangleF(inset, inset, s - inset * 2, d - inset * 2);
        var left = new RectangleF(inset, d + gap + inset, d - inset * 2, d - inset * 2);
        var right = new RectangleF(s - d + inset, d + gap + inset, d - inset * 2, d - inset * 2);
        Brush plate = vibrant ? (Brush)new TextureBrush(frost) : new SolidBrush(IsDark ? DarkBg : LightBg);
        using (plate)
        using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
        using (var shape = RoundRect(pill, pill.Height / 2))
        {
            g.FillPath(plate, shape);
            g.FillEllipse(plate, left);
            g.FillEllipse(plate, right);
            if (vibrant) { g.DrawPath(edge, shape); g.DrawEllipse(edge, left); g.DrawEllipse(edge, right); }
        }

        // Pill: the conditions in dots, then in words.
        float cell = s * 0.0215f, iconW = 11 * cell, lineH = textFont.GetHeight(g);
        float iconX = pill.X + s * 0.1f + iconW / 2, midY = pill.Y + pill.Height / 2;
        DrawDots(g, ink, f != null ? BigIcon(f.Symbol) : BigPartly, iconX, midY, cell);
        string words = f != null ? (status ?? Describe(f.Symbol)) : HasPlace ? (status ?? "Loading…") : "Set your city";
        float textX = iconX + iconW / 2 + s * 0.07f;
        Label(g, words, textFont, ink, textX, midY - lineH * 0.52f, pill.Right - s * 0.07f - textX, StringAlignment.Near);

        // Left circle: the temperature now.
        float cy = left.Y + left.Height / 2;
        string temp = f != null ? Deg(f.Temp) : "—";
        // The degree sign makes the number look off-centre, so centre on the digits alone.
        float nudge = f != null ? TextWidth(g, "°", tempFont) / 2 : 0;
        Label(g, temp, tempFont, ink, left.X + left.Width / 2 + nudge, cy - tempFont.GetHeight(g) * 0.52f, left.Width, StringAlignment.Center);

        // Right circle: today's high and low.
        int first = 0;
        DateTime placeToday = IsHandleCreated ? PlaceToday() : DateTime.Today;
        while (f != null && first < f.Days.Count - 1 && f.Days[first].Date < placeToday) first++;
        Day today = f != null && f.Days.Count > 0 ? f.Days[first] : null;
        float cx = right.X + right.Width / 2, row = lineH * 1.25f;
        for (int i = 0; i < 2; i++)
        {
            bool up = i == 0;
            string value = today == null ? "—" : Deg(up ? today.Hi : today.Lo);
            float y = cy + (up ? -row : 0) + (row - lineH) / 2, tw = TextWidth(g, value, textFont);
            float arrowW = s * 0.035f, space = s * 0.03f, x = cx - (arrowW + space + tw) / 2;
            Arrow(g, up ? ink : soft, x + arrowW / 2, y + lineH * 0.54f, s * 0.05f, arrowW, up);
            Label(g, value, textFont, up ? ink : soft, x + arrowW + space, y, tw + 2, StringAlignment.Near);
        }
    }

    // A small arrow centred on (cx, cy), pointing up or down.
    static void Arrow(Graphics g, Color c, float cx, float cy, float height, float width, bool up)
    {
        float tip = up ? cy - height / 2 : cy + height / 2, tail = up ? cy + height / 2 : cy - height / 2;
        float barb = up ? tip + width / 2 : tip - width / 2;
        using (var pen = new Pen(c, Math.Max(1f, height * 0.14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, cx, tail, cx, tip);
            g.DrawLines(pen, new[] { new PointF(cx - width / 2, barb), new PointF(cx, tip), new PointF(cx + width / 2, barb) });
        }
    }

    string Deg(double celsius)
    {
        double t = Fahrenheit ? celsius * 9 / 5 + 32 : celsius;
        return Math.Round(t).ToString(CultureInfo.InvariantCulture) + "°";
    }

    // MET symbol codes look like "partlycloudy_day", "lightrainshowers_night", "heavysnowandthunder".
    static string Base(string symbol)
    {
        if (symbol == null) return "";
        int cut = symbol.IndexOf('_');
        return cut < 0 ? symbol : symbol.Substring(0, cut);
    }

    static string Describe(string symbol)
    {
        string s = Base(symbol);
        if (s.Length == 0) return "—";
        if (s == "clearsky") return "Clear";
        if (s == "fair") return "Mostly Clear";
        if (s == "partlycloudy") return "Partly Cloudy";
        if (s == "cloudy") return "Cloudy";
        if (s == "fog") return "Fog";
        if (s.Contains("thunder")) return "Thunderstorms";
        string weight = s.StartsWith("light") ? "Light " : s.StartsWith("heavy") ? "Heavy " : "";
        if (s.Contains("sleet")) return weight + "Sleet";
        if (s.Contains("snow")) return weight + (s.Contains("showers") ? "Snow Showers" : "Snow");
        if (s.Contains("showers")) return weight + "Showers";
        if (s.Contains("rain")) return weight + "Rain";
        return "—";
    }

    // 0 clear, 1 partly, 2 cloud, 3 fog, 4 rain, 5 snow, 6 storm
    static int Kind(string symbol)
    {
        string s = Base(symbol);
        if (s == "clearsky") return 0;
        if (s == "fair" || s == "partlycloudy") return 1;
        if (s == "fog") return 3;
        if (s.Contains("thunder")) return 6;
        if (s.Contains("snow") || s.Contains("sleet")) return 5;
        if (s.Contains("rain")) return 4;
        return 2;
    }

    static string[] BigIcon(string symbol)
    {
        switch (Kind(symbol))
        {
            case 0: return symbol.EndsWith("_night") ? BigMoon : BigSun;
            case 1: return BigPartly;
            case 3: return BigFog;
            case 4: return BigRain;
            case 5: return BigSnow;
            case 6: return BigStorm;
            default: return BigCloud;
        }
    }

    // Draws a dot bitmap ('#' is a dot) centred on (cx, cy), one dot per cell.
    static void DrawDots(Graphics g, Color c, string[] rows, float cx, float cy, float cell)
    {
        int cols = rows[0].Length;
        float left = cx - cols * cell / 2, top = cy - rows.Length * cell / 2, d = cell * 0.84f, pad = (cell - d) / 2;
        using (var b = new SolidBrush(c))
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < cols; x++)
                    if (rows[y][x] == '#') g.FillEllipse(b, left + x * cell + pad, top + y * cell + pad, d, d);
    }

    // ---------- dot bitmaps ----------
    static readonly string[] BigSun =
    {
        "....#....",
        ".#.....#.",
        "...###...",
        "..#####..",
        "#.#####.#",
        "..#####..",
        "...###...",
        ".#.....#.",
        "....#...."
    };
    static readonly string[] BigMoon =
    {
        "..####..",
        ".###....",
        "###.....",
        "###.....",
        "###.....",
        "###....#",
        ".###.###",
        "..#####."
    };
    static readonly string[] BigPartly =
    {
        ".#.#.#.....",
        "..###......",
        ".####.###..",
        "..##.#####.",
        ".##########",
        "###########",
        "###########",
        ".#########."
    };
    static readonly string[] BigCloud =
    {
        "...####....",
        "..######...",
        ".########..",
        "###########",
        "###########",
        ".#########."
    };
    static readonly string[] BigFog =
    {
        "###########",
        "...........",
        ".#########.",
        "...........",
        "###########",
        "...........",
        "..#######.."
    };
    static readonly string[] BigRain =
    {
        "...####....",
        "..#######..",
        ".#########.",
        "###########",
        "###########",
        "...........",
        "..#..#..#..",
        ".#..#..#..."
    };
    static readonly string[] BigSnow =
    {
        "...####....",
        "..#######..",
        ".#########.",
        "###########",
        "###########",
        "...........",
        ".#...#...#.",
        "...#...#..."
    };
    static readonly string[] BigStorm =
    {
        "...####....",
        "..#######..",
        ".#########.",
        "###########",
        "###########",
        ".....##....",
        "....##.....",
        "....#......"
    };

    // Library preview.
    static Forecast Sample()
    {
        var f = new Forecast { Temp = 24, Symbol = "partlycloudy_day" };
        f.Days.Add(new Day { Date = DateTime.Today, Symbol = "partlycloudy_day", Hi = 28, Lo = 24 });
        return f;
    }

    // ---------- vibrant: frosted glass in the widget's own shapes ----------
    // The app paints its frosted wallpaper as a rounded card. That card is rendered off-screen
    // (once per position) and then used as the paint for the pill and circles, so the glass
    // follows the shapes and their edges stay smooth.
    Bitmap frost;
    Rectangle frostFor;
    bool capturing;

    protected override void Draw(Graphics g)
    {
        if (!IsVibrant || !FrostOwnShape) { base.Draw(g); return; }
        var size = new Size((int)Math.Round(W), (int)Math.Round(H));
        Rectangle where = IsHandleCreated ? new Rectangle(Location, size) : Rectangle.Empty;
        if (frost == null || frost.Size != size || where != frostFor)
        {
            if (frost != null) frost.Dispose();
            frost = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            frost.SetResolution(96, 96);
            using (var fg = Graphics.FromImage(frost))
            {
                capturing = true;
                try { base.Draw(fg); }
                finally { capturing = false; }
            }
            frostFor = where;
        }
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.FromArgb(1, 0, 0, 0)); // nearly transparent, so the whole square still takes the mouse
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        DrawContent(g, Pal);
    }

    public override void BackdropChanged()
    {
        if (frost != null) { frost.Dispose(); frost = null; }
        base.BackdropChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            DisposeFonts();
            if (frost != null) { frost.Dispose(); frost = null; }
        }
        base.Dispose(disposing);
    }
}
