using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MihomoLauncher.Services
{
    public static class IconLoader
    {
        // Big enough to fill a 40-dip tile icon at 150% (60 px) without being blown up visibly —
        // measured after TrimTransparent, which cuts the empty margins (2026-11-17).
        public const int MinSharpPx = 56;

        public static bool IsSharp(ImageSource s) =>
            s is BitmapSource b && Math.Max(b.PixelWidth, b.PixelHeight) >= MinSharpPx;

        // The biggest picture inside an image file's bytes (every frame of an .ico), in pixels.
        public static int PixelSizeOf(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    return dec.Frames.Count == 0 ? 0 : dec.Frames.Max(f => Math.Max(f.PixelWidth, f.PixelHeight));
                }
            }
            catch { return 0; }
        }

        public static ImageSource Load(string iconPath, string exePath, int size = 96)
        {
            try
            {
                if (!string.IsNullOrEmpty(iconPath))
                {
                    var resolved = ResolveOnDisk(iconPath);
                    if (resolved != null)
                        return TrimTransparent(LoadFile(resolved));

                    var packResult = TryLoadPack(iconPath);
                    if (packResult != null) return TrimTransparent(packResult);
                }
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    using (var ico = Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico != null)
                            return TrimTransparent(ToImageSource(ico));
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        // When the rule has no icon of its own (nothing in the set): the program's own picture,
        // large — a Store app's logo, an exe's 256-px icon — else the site's icon if it has been
        // fetched, else the old 32-px exe icon. Null = nothing (the caller draws a placeholder and
        // may fetch the site icon). Cached: tiles are rebuilt often, the shell call is not free.
        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> _fallbackCache =
            new System.Collections.Generic.Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Fallback(Models.AppEntry app)
        {
            if (app == null) return null;
            try
            {
                // Never an auto-added helper's icon — unless the person ticked «Плитка» on it.
                var progs = (app.Programs ?? new System.Collections.Generic.List<Models.RuleProgram>())
                    .Where(p => p != null && (!p.Auto || p.Launch)).OrderBy(p => p.Launch ? 0 : 1).ToList();
                // A folder with «Плитка» and nothing better: the system's folder icon (2026-11-22).
                var tileFolder = (app.Folders ?? new System.Collections.Generic.List<Models.RuleFolder>())
                    .FirstOrDefault(f => f != null && f.Launch && !string.IsNullOrWhiteSpace(f.Path));
                if (tileFolder != null && !progs.Any(p => p.Launch))
                {
                    var key = "folder:" + tileFolder.Path;
                    ImageSource cached;
                    if (!_fallbackCache.TryGetValue(key, out cached))
                    {
                        BitmapSource img = null;
                        try { if (Directory.Exists(tileFolder.Path)) img = ShellIcon.ForFile(tileFolder.Path); } catch { }
                        cached = img != null ? (ImageSource)TrimTransparent(img) : null;
                        _fallbackCache[key] = cached;
                    }
                    if (cached != null) return cached;
                }
                foreach (var p in progs)
                {
                    var key = !string.IsNullOrWhiteSpace(p.Aumid) ? "aumid:" + p.Aumid : "file:" + p.Path;
                    ImageSource cached;
                    if (_fallbackCache.TryGetValue(key, out cached)) { if (cached != null) return cached; continue; }

                    BitmapSource img = null;
                    if (!string.IsNullOrWhiteSpace(p.Aumid)) img = ShellIcon.ForStoreApp(p.Aumid);
                    if (img == null && !string.IsNullOrWhiteSpace(p.Path) && File.Exists(p.Path)) img = ShellIcon.ForFile(p.Path);
                    var result = img != null ? (ImageSource)TrimTransparent(img) : null;
                    _fallbackCache[key] = result;
                    if (result != null) return result;
                }

                var siteFile = IconFetcher.SiteIconFile(IconFetcher.SiteHostFor(app));
                if (siteFile.Length > 0)
                {
                    var img = Load(siteFile, null);
                    if (img != null && IsSharp(img)) return img;
                }

                // The old 32-px exe icon only if it is somehow big enough — else the tile's monogram.
                var exe = progs.FirstOrDefault(p => !string.IsNullOrEmpty(p.Path))?.Path;
                var small = Load(null, exe);
                return small != null && IsSharp(small) ? small : null;
            }
            catch { return null; }
        }

        private static string ResolveOnDisk(string p)
        {
            try
            {
                if (Path.IsPathRooted(p) && File.Exists(p)) return p;

                var bundled = IconBundle.ResolveBundled(p);
                if (bundled != null) return bundled;

                var rel = Path.Combine(AppContext.BaseDirectory,
                    p.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(rel)) return rel;
            }
            catch { }
            return null;
        }

        private static BitmapSource LoadFile(string path)
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using (var ico = new Icon(path, 256, 256))
                    return ToImageSource(ico);
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // WPF caches pictures by path: an icon re-downloaded into the same file would keep
            // showing the old picture until restart.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource TryLoadPack(string relativePath)
        {
            try
            {
                var packUri = new Uri(
                    "pack://application:,,,/" + relativePath.TrimStart('/').Replace('\\', '/'),
                    UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = packUri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource ToImageSource(Icon icon)
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }

        // Removes the transparent border around the actual visible glyph so all
        // icons end up the same effective size when scaled into a tile, regardless
        // of how much padding their source PNG/ICO baked in.
        private static BitmapSource TrimTransparent(BitmapSource src, byte alphaThreshold = 16)
        {
            if (src == null) return null;
            try
            {
                var bgra = src.Format == PixelFormats.Bgra32
                    ? src
                    : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

                int w = bgra.PixelWidth;
                int h = bgra.PixelHeight;
                if (w <= 2 || h <= 2) return src;

                int stride = w * 4;
                var pixels = new byte[h * stride];
                bgra.CopyPixels(pixels, stride, 0);

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte alpha = pixels[rowOffset + x * 4 + 3];
                        if (alpha > alphaThreshold)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX < 0) return src; // fully transparent — leave as is

                int cw = maxX - minX + 1;
                int ch = maxY - minY + 1;
                if (cw == w && ch == h) return src; // no padding to trim

                var cropped = new CroppedBitmap(bgra, new Int32Rect(minX, minY, cw, ch));
                cropped.Freeze();
                return cropped;
            }
            catch
            {
                return src;
            }
        }
    }
}
