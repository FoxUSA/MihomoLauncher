using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MihomoLauncher.Services
{
    public static class UiHelpers
    {

        // WPF-UI FluentWindow leaves a tall blank gap under the content when using
        // SizeToContent. Fix it by reading the already-measured content height (do NOT
        // call Measure here — that throws if a layout pass is in progress) and setting
        // the window height explicitly. Call once from the window's Loaded handler.
        // Every window of the app fits the screen it opens on. Sizes are written for a big
        // monitor (Settings 950×780); a laptop at 150 % is 1280×688 for WPF, so the window came up
        // taller than the screen, centred with its title bar above the top edge — nothing to drag
        // it by (PAPA2, 2026-11-10). Registered once for all windows (App.OnStartup → Loaded).
        //
        // Worked in device pixels against the monitor the window is on (per-monitor DPI: WPF's
        // own SystemParameters.WorkArea is the PRIMARY monitor only), then Min sizes are lowered
        // to what fits, or WPF would refuse the shrink. Windows that stay inside are not touched.
        public static void KeepOnScreen(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero || w.WindowState != WindowState.Normal) return;
                var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;   // pixels
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return;

                int width = Math.Min(r.Right - r.Left, wa.Width);
                int height = Math.Min(r.Bottom - r.Top, wa.Height);
                int left = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - width));
                int top = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - height));
                if (width == r.Right - r.Left && height == r.Bottom - r.Top && left == r.Left && top == r.Top) return;

                var dpi = VisualTreeHelper.GetDpi(w);
                double waW = wa.Width / dpi.DpiScaleX, waH = wa.Height / dpi.DpiScaleY;
                if (w.MinWidth > waW) w.MinWidth = waW;
                if (w.MinHeight > waH) w.MinHeight = waH;
                if (height < r.Bottom - r.Top && w.SizeToContent != SizeToContent.Manual) w.SizeToContent = SizeToContent.Manual;

                SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void FitHeightToContent(Window w)
        {
            var root = w.Content as FrameworkElement;
            if (root == null) return;
            w.SizeToContent = SizeToContent.Manual;
            // DesiredSize is populated by the normal layout pass = title bar + content
            // (Auto rows). +12 covers the thin window frame so nothing clips.
            var h = root.DesiredSize.Height + 12;
            // Never exceed the screen's working area, otherwise the bottom is clipped.
            var maxH = SystemParameters.WorkArea.Height;
            if (h > maxH) h = maxH;
            if (h > 120) w.Height = h;
        }
    }
}
