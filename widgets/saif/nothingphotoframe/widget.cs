using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DeskWidgets;
using DeskWidgets.Widgets;

// A picture in a round frame with a dark ring, or a rounded square. Either one picture of your
// choice, or a folder: then a random picture from it is shown and changed on a schedule, or
// when you click the frame.
// The picture is read from your disk, cropped to fill the frame and kept only at the widget's
// size; it is never uploaded or copied anywhere. Animated GIFs play at their own frame rate
// (at most about 15 frames a second): the compressed file stays in memory and one frame is
// decoded at a time. Playback pauses while the widget is hidden behind other windows.
[DeskWidget(Name = "Nothing Photo Frame", Description = "A picture in a round or rounded-square frame")]
public sealed class NothingPhotoFrame : CardWidget
{
    static readonly Color DarkRing = Rgb(0x1B1B1D), LightRing = Color.White;
    static readonly Color DarkTile = Rgb(0x2A2A2D), DarkDot = Rgb(0x4A4A4E), DarkSoft = Rgb(0x8C8C90);
    static readonly Color LightTile = Rgb(0xECECEF), LightDot = Rgb(0xCBCBD1), LightSoft = Rgb(0x8E8E93);

    const int MaxGifBytes = 40 * 1024 * 1024; // bigger GIFs show their first frame only
    const int MinFrameMs = 66;                // play GIFs at no more than ~15 frames per second
    const int CoveredCheckMs = 1000;

    sealed class Animation : IDisposable
    {
        public MemoryStream Stream;  // GDI+ reads frames from it on demand, so it stays open
        public Image Image;
        public int Frames, Frame;
        public int[] DelaysMs;
        public RectangleF Source;    // the square crop of each frame

        public void Dispose() { Image.Dispose(); Stream.Dispose(); }
    }

    sealed class Loaded { public Bitmap Still; public Animation Anim; }

    Bitmap photo;      // a still cropped to the widget size, or the current GIF frame
    Animation anim;
    int shownFrame = -1;   // which GIF frame photo holds
    int nextFrameDue;      // Environment.TickCount when the next GIF frame is due
    string photoFor;   // the path and options the picture was made from
    bool loading, missing;

    // Folder mode: which file is showing, and the timer that moves on to another.
    static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    static readonly int[] Intervals = { 15, 60, 360, 1440 };
    static readonly string[] IntervalNames = { "Every 15 Minutes", "Every Hour", "Every 6 Hours", "Once a Day" };
    string current;
    int index = -1;
    bool picking;
    readonly Timer shuffle = new Timer();
    readonly Random random = new Random();

    public NothingPhotoFrame(Section settings) : base(settings, false)
    {
        shuffle.Tick += delegate { Next(); };
    }

    protected override bool SquareResize { get { return true; } }
    bool FrostOwnShape { get { return Round; } }

    string PicturePath { get { return Settings.GetText("picture", ""); } }
    string Folder { get { return Settings.GetText("folder", ""); } }
    bool FolderMode { get { return Folder.Length > 0; } }
    bool Subfolders { get { return Settings.GetBool("subfolders", false); } }
    // The file to show: the folder's current pick, or the one chosen picture.
    string SourcePath { get { return FolderMode ? (current ?? "") : PicturePath; } }
    bool Round { get { return Settings.Get("shape", "round") != "square"; } }
    // Vibrant shows pictures desaturated, as the app's other vibrant widgets do.
    bool Mono { get { return Settings.GetBool("mono", false) || IsVibrant; } }
    bool Animate { get { return Settings.GetBool("animate", true); } }
    bool HasPicture { get { return photo != null || anim != null; } }
    float Side { get { return Math.Min(W, H); } }

    // Round: nearly transparent, so the corners outside the circle still take the mouse.
    protected override Color CardBackground(Palette pal)
    {
        if (Round) return Color.FromArgb(1, 0, 0, 0);
        return IsVibrant ? pal.Bg : IsDark ? DarkTile : LightTile;
    }

    protected override void OnStart()
    {
        if (!FolderMode) { Reload(); return; }
        StartShuffle();
        Next();
    }

    void StartShuffle()
    {
        shuffle.Stop();
        shuffle.Interval = Settings.GetInt("every", 60) * 60 * 1000;
        shuffle.Start();
    }

    // Folder mode: pick another picture (never the same one twice in a row).
    void Next()
    {
        if (picking || !FolderMode) return;
        picking = true;
        string folder = Folder;
        bool deep = Subfolders;
        int last = index;
        Background(() =>
        {
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
            }
            catch (Exception) { files = new string[0]; } // missing folder or no access
            if (files.Length == 0) return null;
            int pick = random.Next(files.Length);
            if (files.Length > 1 && pick == last) pick = (pick + 1) % files.Length;
            return Tuple.Create(files[pick], pick);
        }, (choice, error) =>
        {
            picking = false;
            if (!FolderMode) return; // switched to a single picture meanwhile
            current = choice != null ? choice.Item1 : null;
            index = choice != null ? choice.Item2 : -1;
            missing = false;
            Reload();
        });
    }
    protected override void Changed() { Reload(); base.Changed(); }
    protected override void OnResized(int width, int height) { Reload(); }

    // ---------- picture ----------
    void Reload()
    {
        string path = SourcePath;
        int side = (int)Math.Round(Side);
        bool mono = Mono;
        string key = path + "|" + side + "|" + mono;
        if (path.Length == 0) { SetPhoto(null, null); return; }
        if (key == photoFor || loading || side <= 0) return;
        loading = true;
        Background(() => LoadPicture(path, side, mono), (pic, error) =>
        {
            loading = false;
            missing = error != null;
            SetPhoto(error == null ? pic : null, key);
            // The size or picture may have changed while this one was decoding.
            if (error == null && key != SourcePath + "|" + (int)Math.Round(Side) + "|" + Mono) Reload();
        });
    }

    void SetPhoto(Loaded pic, string key)
    {
        if (photo != null) { photo.Dispose(); photo = null; }
        if (anim != null) { anim.Dispose(); anim = null; }
        if (pic != null) { photo = pic.Still; anim = pic.Anim; }
        photoFor = HasPicture ? key : null;
        shownFrame = -1;
        nextFrameDue = Environment.TickCount;
        Render();
    }

    // Worker thread: decode, and for stills turn the right way up and crop to the frame.
    static Loaded LoadPicture(string path, int side, bool mono)
    {
        byte[] bytes = File.ReadAllBytes(path); // don't keep the file locked
        var ms = new MemoryStream(bytes);
        Image img = null;
        try
        {
            img = Image.FromStream(ms, false, false);
            int frames = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;
            if (frames > 1 && bytes.Length <= MaxGifBytes)
            {
                var a = new Animation { Stream = ms, Image = img, Frames = frames, DelaysMs = FrameDelays(img, frames), Source = SquareCrop(img.Size) };
                img = null; ms = null; // owned by the animation now
                return new Loaded { Anim = a };
            }
            ApplyExifOrientation(img);
            var bmp = NewBuffer(side);
            PaintInto(bmp, img, SquareCrop(img.Size), mono, InterpolationMode.HighQualityBicubic);
            return new Loaded { Still = bmp };
        }
        finally
        {
            if (img != null) img.Dispose();
            if (ms != null) ms.Dispose();
        }
    }

    static Bitmap NewBuffer(int side)
    {
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        return bmp;
    }

    // The largest centred square of a picture.
    static RectangleF SquareCrop(Size img)
    {
        float side = Math.Min(img.Width, img.Height);
        return new RectangleF((img.Width - side) / 2, (img.Height - side) / 2, side, side);
    }

    // Draws the cropped picture (or GIF frame) to fill the buffer.
    static void PaintInto(Bitmap buffer, Image img, RectangleF src, bool mono, InterpolationMode quality)
    {
        int side = buffer.Width;
        using (var g = Graphics.FromImage(buffer))
        using (var attrs = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy; // a GIF frame replaces the last one, transparency included
            g.InterpolationMode = quality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no faded edge from sampling past the border
            if (mono)
                attrs.SetColorMatrix(new ColorMatrix(new[]
                {
                    new[] { 0.30f, 0.30f, 0.30f, 0, 0 }, new[] { 0.59f, 0.59f, 0.59f, 0, 0 }, new[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                    new[] { 0f, 0, 0, 1, 0 }, new[] { 0f, 0, 0, 0, 1 }
                }));
            g.DrawImage(img, new[] { new PointF(0, 0), new PointF(side, 0), new PointF(0, side) }, src, GraphicsUnit.Pixel, attrs);
        }
    }

    // GIF frame delays (property 0x5100, hundredths of a second). Like browsers, very short
    // delays mean 100 ms.
    static int[] FrameDelays(Image img, int frames)
    {
        var delays = Enumerable.Repeat(100, frames).ToArray();
        const int FrameDelayId = 0x5100;
        if (Array.IndexOf(img.PropertyIdList, FrameDelayId) < 0) return delays;
        byte[] v = img.GetPropertyItem(FrameDelayId).Value;
        for (int i = 0; i < frames && i * 4 + 3 < v.Length; i++)
        {
            int cs = BitConverter.ToInt32(v, i * 4);
            delays[i] = cs <= 1 ? 100 : cs * 10;
        }
        return delays;
    }

    // ---------- animation timing ----------
    protected override int NextFrameDelay()
    {
        if (anim == null || !Animate || !IsHandleCreated) return -1;
        if (IsCovered()) return CoveredCheckMs; // nobody can see it: check again in a second
        return Math.Max(10, nextFrameDue - Environment.TickCount);
    }

    void AdvanceFrameIfDue()
    {
        int now = Environment.TickCount;
        if (!Animate || now - nextFrameDue < 0) return; // off, or a redraw for another reason: keep the frame
        if (IsCovered()) return;
        anim.Frame = (anim.Frame + 1) % anim.Frames;
        nextFrameDue = now + Math.Max(MinFrameMs, anim.DelaysMs[anim.Frame]);
    }

    // Puts the GIF's current frame into the widget-sized buffer, only when it has changed.
    void UpdateFrame(int side)
    {
        if (photo != null && photo.Width != side) { photo.Dispose(); photo = null; }
        if (photo == null) { photo = NewBuffer(side); shownFrame = -1; }
        AdvanceFrameIfDue();
        if (anim.Frame == shownFrame) return;
        anim.Image.SelectActiveFrame(FrameDimension.Time, anim.Frame);
        PaintInto(photo, anim.Image, anim.Source, Mono, InterpolationMode.Bilinear); // per-frame: cheaper than bicubic
        shownFrame = anim.Frame;
    }

    static void ApplyExifOrientation(Image img)
    {
        const int OrientationId = 0x0112;
        if (Array.IndexOf(img.PropertyIdList, OrientationId) < 0) return;
        int o = img.GetPropertyItem(OrientationId).Value[0];
        RotateFlipType t = o == 3 ? RotateFlipType.Rotate180FlipNone : o == 6 ? RotateFlipType.Rotate90FlipNone :
                           o == 8 ? RotateFlipType.Rotate270FlipNone : o == 2 ? RotateFlipType.RotateNoneFlipX :
                           o == 4 ? RotateFlipType.RotateNoneFlipY : RotateFlipType.RotateNoneFlipNone;
        if (t != RotateFlipType.RotateNoneFlipNone) img.RotateFlip(t);
    }

    // ---------- interaction ----------
    protected override void OnWidgetClick(Point p)
    {
        if (FolderMode)
        {
            if (HasPicture) Next();
            else if (!loading && !picking) ChooseFolder();
        }
        else if (!HasPicture && !loading) ChoosePicture();
    }

    void ChooseFolder()
    {
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "Choose the folder of pictures to show";
            dlg.SelectedPath = FolderMode ? Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            Settings.SetText("folder", dlg.SelectedPath);
            index = -1;
            StartShuffle();
            Next();
        }
    }

    void LeaveFolder()
    {
        Settings.SetText("folder", "");
        shuffle.Stop();
        current = null;
        index = -1;
    }

    void ChoosePicture()
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "Choose a picture for the frame";
            dlg.Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff";
            string current = PicturePath;
            try { if (current.Length > 0) dlg.InitialDirectory = Path.GetDirectoryName(current); }
            catch (ArgumentException) { }
            if (dlg.ShowDialog() != DialogResult.OK) return;
            Settings.SetText("picture", dlg.FileName);
            LeaveFolder();
            missing = false;
            Reload();
        }
    }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        bool folder = FolderMode;
        if (folder) menu.Add("Next Picture", Next);
        menu.Add("Choose Picture…", ChoosePicture);
        menu.Add("Choose Folder…", ChooseFolder);
        if (folder)
        {
            bool deep = Subfolders;
            menu.AddCheck("Include Subfolders", deep, delegate { Settings.Set("subfolders", !deep); index = -1; Next(); });
            var every = menu.AddSubmenu("Change Picture");
            int cur = Settings.GetInt("every", 60);
            for (int i = 0; i < Intervals.Length; i++)
            {
                int min = Intervals[i];
                every.AddChoice(IntervalNames[i], cur == min, delegate { Settings.Set("every", min); StartShuffle(); });
            }
        }
        if (folder || PicturePath.Length > 0)
            menu.Add(folder ? "Remove Folder" : "Remove Picture", delegate
            {
                Settings.SetText("picture", "");
                LeaveFolder();
                missing = false;
                Reload();
            });
        var shape = menu.AddSubmenu("Shape");
        bool round = Round;
        shape.AddChoice("Round", round, delegate { Settings.Set("shape", "round"); Changed(); });
        shape.AddChoice("Rounded Square", !round, delegate { Settings.Set("shape", "square"); Changed(); });
        bool mono = Settings.GetBool("mono", false);
        if (!IsVibrant) menu.AddCheck("Black and White", mono, delegate { Settings.Set("mono", !mono); Changed(); });
        bool animate = Animate;
        menu.AddCheck("Play Animated GIFs", animate, delegate { Settings.Set("animate", !animate); nextFrameDue = Environment.TickCount; Render(); });
    }

    // ---------- drawing ----------
    protected override void DrawContent(Graphics g, Palette pal)
    {
        if (capturing) return; // only the frosted backdrop is wanted
        float s = Side, c = s / 2f;
        bool round = Round, dark = IsDark, vibrant = IsVibrant;
        float ring = round ? s * 0.035f : 0;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (round && vibrant) FillGlassCircle(g, c, c, c - 1.5f);
        else if (round) FillCircle(g, dark ? DarkRing : LightRing, c, c, c - 0.5f);

        // The picture is painted as a brush, so the frame's edge stays smooth.
        if (anim != null) UpdateFrame((int)Math.Round(s));
        if (photo != null && photo.Width == (int)Math.Round(s))
        {
            using (var paint = new TextureBrush(photo))
            {
                if (round) g.FillEllipse(paint, ring, ring, s - ring * 2, s - ring * 2);
                else using (var card = CardPath()) g.FillPath(paint, card);
            }
            return;
        }

        // Empty frame: a field of dots, a small picture drawn in dots, and what to do.
        Color tile = dark ? DarkTile : LightTile, dot = dark ? DarkDot : LightDot, soft = dark ? DarkSoft : LightSoft;
        if (vibrant) { dot = Color.FromArgb(60, 255, 255, 255); soft = pal.Fg2; }
        else if (round) FillCircle(g, tile, c, c, c - ring);
        float step = P(9), inner = c - ring - P(5);
        using (var b = new SolidBrush(dot))
            for (float y = c % step; y < s; y += step)
                for (float x = c % step; x < s; x += step)
                {
                    float dx = x - c, dy = y - c;
                    bool inside = round ? dx * dx + dy * dy <= inner * inner
                                        : Math.Abs(dx) <= c - P(9) && Math.Abs(dy) <= c - P(9);
                    // Leave the middle clear for the icon and label.
                    if (inside && !(Math.Abs(dx) < s * 0.3f && Math.Abs(dy) < s * 0.24f)) g.FillEllipse(b, x - P(1.1f), y - P(1.1f), P(2.2f), P(2.2f));
                }

        DrawDots(g, soft, Picture, c, c - s * 0.07f, s * 0.026f);
        string text = !IsHandleCreated ? "Add photo" : (loading || picking) ? "Loading…" : missing ? "Picture not found"
            : FolderMode ? "No pictures here" : "Add photo";
        Label(g, text, F(11, "regular"), soft, c, c + s * 0.1f, s * 0.6f, StringAlignment.Center);
    }

    // Draws a dot bitmap ('#' is a dot) centred on (cx, cy), one dot per cell.
    static void DrawDots(Graphics g, Color color, string[] rows, float cx, float cy, float cell)
    {
        int cols = rows[0].Length;
        float left = cx - cols * cell / 2, top = cy - rows.Length * cell / 2, d = cell * 0.84f, pad = (cell - d) / 2;
        using (var b = new SolidBrush(color))
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < cols; x++)
                    if (rows[y][x] == '#') g.FillEllipse(b, left + x * cell + pad, top + y * cell + pad, d, d);
    }

    // A framed landscape: sun, two hills.
    static readonly string[] Picture =
    {
        "#############",
        "#...........#",
        "#.##........#",
        "#.##....#...#",
        "#......###..#",
        "#..#..#####.#",
        "#.###.#####.#",
        "#############"
    };

    // ---------- vibrant: frosted glass in the widget's own shape ----------
    // The app paints its frosted wallpaper as a rounded card. For a round shape, that card is
    // rendered off-screen (once per position) and then used as the paint for the circle, so the
    // glass follows the shape and its edge stays smooth.
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

    // A frosted disc with the hairline edge the app gives its glass cards.
    void FillGlassCircle(Graphics g, float cx, float cy, float radius)
    {
        using (var glass = new TextureBrush(frost))
            g.FillEllipse(glass, cx - radius, cy - radius, radius * 2, radius * 2);
        using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
            g.DrawEllipse(edge, cx - radius, cy - radius, radius * 2, radius * 2);
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
            shuffle.Dispose();
            if (frost != null) { frost.Dispose(); frost = null; }
            if (photo != null) { photo.Dispose(); photo = null; }
            if (anim != null) { anim.Dispose(); anim = null; }
        }
        base.Dispose(disposing);
    }
}
