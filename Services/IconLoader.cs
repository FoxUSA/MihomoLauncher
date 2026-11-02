using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MihomoLauncher.Services
{
    public static class IconLoader
    {
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
