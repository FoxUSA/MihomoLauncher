using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MihomoLauncher.Services
{
    // A program's own picture at a size worth showing, the way Explorer gets it.
    //
    // The old fallback (Icon.ExtractAssociatedIcon) only ever returns the 32×32 icon, which a
    // tile blows up to ~66 px — visibly blurred. The shell's image factory hands out the large
    // (256 px) icon of an exe, and given "shell:AppsFolder\<AUMID>" the logo of a Store app,
    // which has no usable exe icon at all.
    public static class ShellIcon
    {
        public static BitmapSource ForFile(string path, int size = 256)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return Get(path, size);
        }

        public static BitmapSource ForStoreApp(string aumid, int size = 256)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return null;
            return Get(@"shell:AppsFolder\" + aumid.Trim(), size);
        }

        private static BitmapSource Get(string parsingName, int size)
        {
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                if (factory == null) return null;
                int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                return FromHBitmap(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) try { Marshal.ReleaseComObject(factory); } catch { }
            }
        }

        // The shell's bitmap is a 32-bit DIB with premultiplied alpha. Imaging.CreateBitmapSource-
        // FromHBitmap drops the alpha (black corners), so the pixels are copied out as Pbgra32,
        // flipped when the DIB is stored bottom-up.
        private static BitmapSource FromHBitmap(IntPtr hbm)
        {
            DIBSECTION ds;
            if (GetObject(hbm, Marshal.SizeOf(typeof(DIBSECTION)), out ds) == 0) return null;
            var bm = ds.dsBm;
            if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;

            int w = bm.bmWidth, h = bm.bmHeight, stride = w * 4;
            var pixels = new byte[h * stride];
            Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

            if (ds.dsBmih.biHeight > 0)   // bottom-up: first row in memory is the bottom one
            {
                var row = new byte[stride];
                for (int y = 0; y < h / 2; y++)
                {
                    int a = y * stride, b = (h - 1 - y) * stride;
                    Buffer.BlockCopy(pixels, a, row, 0, stride);
                    Buffer.BlockCopy(pixels, b, pixels, a, stride);
                    Buffer.BlockCopy(row, 0, pixels, b, stride);
                }
            }

            // Some icons come back with no alpha at all (every A = 0) — then it is opaque.
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            var fmt = PixelFormats.Pbgra32;
            if (!anyAlpha) fmt = PixelFormats.Bgr32;

            var src = BitmapSource.Create(w, h, 96, 96, fmt, null, pixels, stride);
            src.Freeze();
            return src;
        }

        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_ICONONLY = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            public uint dsBitfields0, dsBitfields1, dsBitfields2;
            public IntPtr dshSection;
            public uint dsOffset;
        }

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr h, int c, out DIBSECTION pv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);
    }
}
