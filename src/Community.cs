using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DeskWidgets
{
    // The online half of community widgets. The gallery is a set of files on the website, built
    // from the widgets/ folder of the repository (tools/build-widget-pages.mjs): w/gallery.json
    // lists every widget with the SHA-256 of its source, and each widget has w/<id>/source.cs and
    // w/<id>/preview.png. A widget is there because a pull request for it was reviewed and merged.
    // The app only reads: there is no account, and nothing is sent about what was installed.
    // Installing runs the same hash check and prompt as a local file.
    static class Community
    {
        public const string Website = "https://whoissaaif.github.io/desk-widgets/";
        const string Gallery = Website + "w/";

        public static bool Configured { get { return Website.Length > 0; } }

        static Community()
        {
            // Previews are small files fetched a few at a time; the default is two connections a host.
            try { ServicePointManager.FindServicePoint(new Uri(Website)).ConnectionLimit = 6; }
            catch (Exception) { }
        }

        public sealed class Listing
        {
            public string Id, Name, Category, Description, Author;
            public string Sha256;    // of w/<id>/source.cs; null in a list kept by an older version of the app
            public string Reviewed;  // when it was merged, as the gallery wrote it
            public int Version, Api;
            public bool HasPreview;
        }

        // ---- reading ----

        // The widgets in gallery.json. A list kept on disk by an older version of the app is a
        // bare array of the same rows, and reads the same way.
        static object[] Parse(string json)
        {
            object parsed = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(json);
            var whole = parsed as Dictionary<string, object>;
            object widgets;
            if (whole != null && whole.TryGetValue("widgets", out widgets)) parsed = widgets;
            var rows = parsed as object[];
            if (rows == null) throw new FormatException("Unexpected answer from the gallery.");
            return rows;
        }

        static string Text(object row, string key)
        {
            var d = row as Dictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static int Number(object row, string key)
        {
            int n;
            return int.TryParse(Text(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        // A widget's file in the gallery. The version in the address makes a new version a new
        // URL, past any cache between here and the site; the id is validated before it gets here.
        static string FileUrl(Listing l, string name)
        {
            return Gallery + l.Id + "/" + name + "?v=" + l.Version.ToString(CultureInfo.InvariantCulture);
        }

        // ---- the Explore list, kept ----
        // The list is kept in memory for as long as the app runs and on disk between runs, so
        // Explore opens with its cards already there (and still opens offline). The gallery is
        // asked again only when the copy is older than ExploreKeep.

        static readonly object exploreLock = new object();
        static List<Listing> exploreKept;            // the last answer, or what the disk held
        static DateTime exploreAsked;                // UTC of the last answer from the gallery this run
        static readonly TimeSpan ExploreKeep = TimeSpan.FromMinutes(10);
        static readonly string ExploreFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWidgets", "explore.json");

        // True while the kept list is recent enough that asking the gallery again would be a waste.
        public static bool ExploreFresh
        {
            get { lock (exploreLock) return exploreKept != null && DateTime.UtcNow - exploreAsked < ExploreKeep; }
        }

        // The kept list without asking the gallery, or null when there is none. Call from a
        // background thread: the first call of a run reads the disk.
        public static List<Listing> KeptExplore()
        {
            lock (exploreLock) if (exploreKept != null) return new List<Listing>(exploreKept);
            List<Listing> fromDisk;
            try { fromDisk = Listings(Parse(File.ReadAllText(ExploreFile, Encoding.UTF8))); }
            catch (Exception) { return null; } // no file yet, or one that doesn't parse
            lock (exploreLock)
            {
                if (exploreKept == null) exploreKept = fromDisk;
                return new List<Listing>(exploreKept);
            }
        }

        // Every widget in the gallery, newest first. Call from a background thread.
        public static List<Listing> Explore()
        {
            // Previews are fetched separately, once per version (FetchPreviews), so the list stays small.
            string json = Http.Get(Gallery + "gallery.json");
            List<Listing> list = Listings(Parse(json));
            lock (exploreLock) { exploreKept = new List<Listing>(list); exploreAsked = DateTime.UtcNow; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ExploreFile));
                File.WriteAllText(ExploreFile, json, new UTF8Encoding(false));
            }
            catch (Exception) { } // the copy on disk is a convenience
            return list;
        }

        // Rows from the gallery or from the disk: checked the same way either way.
        static List<Listing> Listings(object[] rows)
        {
            return rows
                .Select(r => new Listing
                {
                    Id = Text(r, "id"), Version = Number(r, "version"), Name = Text(r, "name") ?? "", Category = Text(r, "category"),
                    Description = Text(r, "description") ?? "", Author = Text(r, "author") ?? "", Api = Number(r, "api_version"),
                    Sha256 = Text(r, "sha256"), Reviewed = Text(r, "reviewed_at"),
                    HasPreview = !string.Equals(Text(r, "preview"), "false", StringComparison.OrdinalIgnoreCase)
                })
                .Where(l => Plugins.IsValidId(l.Id) && l.Version > 0)
                .ToList();
        }

        // ---- previews: fetched once per version, then kept on disk ----
        // A version never changes once it is published, so neither does its preview: the cache
        // never goes stale.

        static readonly string PreviewDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWidgets", "previews");

        // File name for a version's preview; the id is validated and the version is a number.
        static string PreviewFile(Listing l, string ext) { return Path.Combine(PreviewDir, l.Id + "_" + l.Version + ext); }

        // One preview from the gallery, kept on disk for next time. A widget with no picture is
        // remembered as such, so it isn't asked for every time; a failed download is not, so it
        // is tried again.
        static Bitmap DownloadPreview(Listing l)
        {
            byte[] bytes = null;
            bool none = !l.HasPreview;
            if (!none)
            {
                try { bytes = Http.GetBytes(FileUrl(l, "preview.png")); }
                catch (WebException e)
                {
                    var resp = e.Response as HttpWebResponse;
                    if (resp == null || resp.StatusCode != HttpStatusCode.NotFound) return null; // offline: nothing cached
                    none = true;
                }
                catch (Exception) { return null; }
            }
            Bitmap bmp = bytes != null ? DecodePreview(bytes) : null;
            try
            {
                Directory.CreateDirectory(PreviewDir);
                if (bmp != null) File.WriteAllBytes(PreviewFile(l, ".png"), bytes);
                else File.WriteAllBytes(PreviewFile(l, ".none"), new byte[0]);
            }
            catch (Exception) { }
            return bmp;
        }

        // All previews for a list of widgets: from the disk cache when it has them, otherwise from
        // the gallery, a few at a time. diskOnly never asks the gallery: a widget whose preview
        // isn't on disk yet simply has none for now. Call from a background thread.
        public static Dictionary<Listing, Bitmap> FetchPreviews(List<Listing> listings, bool diskOnly)
        {
            var result = new Dictionary<Listing, Bitmap>();
            var uncached = new List<Listing>();
            foreach (var l in listings)
            {
                if (!Plugins.IsValidId(l.Id) || l.Version < 1) { result[l] = null; continue; }
                string png = PreviewFile(l, ".png"), none = PreviewFile(l, ".none");
                try
                {
                    if (File.Exists(none)) { result[l] = null; continue; }
                    if (File.Exists(png))
                    {
                        Bitmap cached = DecodePreview(File.ReadAllBytes(png)); // the disk is checked like the network
                        if (cached != null) { result[l] = cached; continue; }
                        File.Delete(png);
                    }
                }
                catch (Exception) { }
                uncached.Add(l);
            }
            if (diskOnly) foreach (var l in uncached) result[l] = null;
            else if (uncached.Count > 0)
            {
                Parallel.ForEach(uncached, new ParallelOptions { MaxDegreeOfParallelism = 6 }, delegate(Listing l)
                {
                    Bitmap bmp = DownloadPreview(l);
                    lock (result) result[l] = bmp;
                });
            }
            return result;
        }

        // Deletes cached previews of versions that are no longer listed.
        public static void PrunePreviews(IEnumerable<Listing> listed)
        {
            try
            {
                if (!Directory.Exists(PreviewDir)) return;
                var keep = new HashSet<string>(listed.Select(l => l.Id + "_" + l.Version), StringComparer.OrdinalIgnoreCase);
                foreach (string f in Directory.GetFiles(PreviewDir))
                    if (!keep.Contains(Path.GetFileNameWithoutExtension(f))) File.Delete(f);
            }
            catch (Exception) { }
        }

        // Untrusted image data: size-capped, decoded into a copy, and dropped if it's odd in any way.
        public static Bitmap DecodePreview(byte[] png)
        {
            if (png == null || png.Length == 0 || png.Length > 105000) return null;
            try
            {
                using (var ms = new MemoryStream(png))
                using (var img = Image.FromStream(ms, false, true))
                {
                    if (img.Width < 1 || img.Height < 1 || img.Width > 1024 || img.Height > 1024) return null;
                    return new Bitmap(img);
                }
            }
            catch (Exception) { return null; }
        }

        // ---- deskwidgets://install/<id>/<version> ----

        // Any web page can open this URL, so it's only a request: the id and version are checked here,
        // everything else is fetched from the gallery, and installing always asks first.
        public static bool TryParseUrl(string url, out string id, out int version)
        {
            id = null;
            version = 0;
            const string prefix = "deskwidgets://install/";
            if (url == null || url.Length > 200 || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string[] parts = url.Substring(prefix.Length).TrimEnd('/').Split('/');
            if (parts.Length != 2 || !Plugins.IsValidId(parts[0])) return false;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1 || version > 100000) return false;
            id = parts[0];
            return true;
        }

        public static void HandleUrl(string url)
        {
            string id;
            int version;
            if (TryParseUrl(url, out id, out version)) Install(id, version);
        }

        public static void Install(string id, int version) { Install(id, version, null); }

        // Fetches the widget from the gallery, checks it, asks, installs. Starts on the UI thread.
        // The gallery publishes one version of a widget, its latest, so that is what is installed;
        // version is the one the link or the update asked for, and the prompt names the one it got.
        // dropAt is where a card dragged out of Explore was let go: once the person has said yes
        // and the widget is installed, one is placed there. Saying no places nothing.
        public static void Install(string id, int version, Point? dropAt)
        {
            if (!Plugins.IsValidId(id)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                Listing listing = null;
                byte[] source = null;
                try
                {
                    listing = Explore().FirstOrDefault(l => l.Id == id);
                    if (listing != null && listing.Version >= version) source = Http.GetBytes(FileUrl(listing, "source.cs"));
                }
                catch (Exception e) { error = "The gallery couldn't be reached. Check your connection and try again. (" + e.Message + ")"; }
                App.RunOnUi(delegate
                {
                    if (error != null) { AlertDialog.Show("Couldn't get the widget.", error); return; }
                    if (listing == null) { AlertDialog.Show("This widget isn't available.", "It may have been removed from the gallery."); return; }
                    // A version merged a moment ago: the site takes a few minutes to show it everywhere.
                    if (source == null) { AlertDialog.Show("This version isn't available yet.", "It was just published. Try again in a few minutes."); return; }
                    Confirm(listing, source, dropAt);
                });
            });
        }

        static void Confirm(Listing l, byte[] bytes, Point? dropAt)
        {
            string name = l.Name.Length > 0 ? l.Name : l.Id;
            // The guarantee: these bytes are the ones that were reviewed.
            if (l.Sha256 == null || Plugins.Sha256(bytes) != l.Sha256)
            {
                AlertDialog.Show(name + " wasn't installed.", "Its code doesn't match what was reviewed, so it wasn't installed. If it was just updated, try again in a few minutes.");
                return;
            }
            if (bytes.Length > Plugins.MaxSourceBytes) { AlertDialog.Show(name + " wasn't installed.", "Its code is too large."); return; }
            string source;
            try { source = new UTF8Encoding(false, true).GetString(bytes); }
            catch (ArgumentException) { AlertDialog.Show(name + " wasn't installed.", "Its code isn't readable text."); return; }
            string hidden = Plugins.HiddenCharacter(source); // the gallery refuses these too; don't rely on it
            if (hidden != null) { AlertDialog.Show(name + " wasn't installed.", Plugins.HiddenMessage(hidden)); return; }
            if (l.Api > PluginApi.Version)
            {
                AlertDialog.Show(name + " needs a newer Desk Widgets.", "Update Desk Widgets, then try again.");
                return;
            }

            PluginInfo old = Plugins.Get(l.Id);
            DateTime reviewed;
            bool hasDate = DateTime.TryParse(l.Reviewed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out reviewed);
            var facts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("By", l.Author.Length > 0 ? l.Author : "unknown"),
                new KeyValuePair<string, string>("Version", old == null ? l.Version.ToString(CultureInfo.InvariantCulture)
                    : l.Version + " (replaces " + old.Version + ")"),
                new KeyValuePair<string, string>("Reviewed", hasDate ? reviewed.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "yes"),
                new KeyValuePair<string, string>("Size", (bytes.Length + 1023) / 1024 + " KB"),
            };
            if (!AlertDialog.ConfirmInstall(name, "Install " + name + "?", facts, source)) return;

            var p = new PluginInfo
            {
                Id = l.Id, Version = l.Version, Api = l.Api, Name = name, Category = l.Category,
                Description = l.Description, Author = l.Author, Sha256 = l.Sha256
            };
            if (!Plugins.InstallChecked(p, bytes, name)) return;
            if (dropAt != null && p.Type != null) App.Current.AddWidget(p.Type, dropAt);
        }

        // ---- updates ----

        // Looks for newer versions of installed community widgets. Never installs them: a new
        // version is new code, so the person decides (Installed shows Update).
        public static void CheckForUpdates()
        {
            if (!Plugins.Installed.Any(p => p.Author != "local")) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Listing> list;
                try { list = Explore(); }
                catch (Exception) { return; } // offline: try again next time
                App.RunOnUi(delegate
                {
                    bool changed = false;
                    foreach (var l in list)
                    {
                        PluginInfo p = Plugins.Get(l.Id);
                        if (p != null && p.Author != "local" && l.Version > p.Version && p.UpdateVersion != l.Version) { p.UpdateVersion = l.Version; changed = true; }
                    }
                    if (changed)
                    {
                        int count = Plugins.Installed.Count(p => p.UpdateVersion > p.Version);
                        if (count > 0)
                            Toast.Show("Updates Available", count == 1 ? "1 widget has a new version." : count + " widgets have new versions.");
                        WidgetRegistry.RaiseChanged();
                    }
                });
            });
        }

        // ---- a preview to publish with ----

        // A widget's snapshot as a PNG under 100 KB, the size the gallery takes, or null. It is
        // what "Save Preview" writes, for the preview.png of a widget you are publishing.
        public static byte[] PreviewPng(WidgetType type)
        {
            Bitmap shot = type != null ? type.Snapshot() : null;
            if (shot == null) return null;
            using (shot)
                for (float scale = 1f; scale >= 0.25f; scale /= 2)
                {
                    int w = Math.Max(1, (int)(shot.Width * scale)), h = Math.Max(1, (int)(shot.Height * scale));
                    if (w > 1024 || h > 1024) continue;
                    using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    using (var ms = new MemoryStream())
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(shot, 0, 0, w, h);
                        }
                        small.Save(ms, ImageFormat.Png);
                        if (ms.Length <= 100 * 1024) return ms.ToArray();
                    }
                }
            return null;
        }
    }
}
