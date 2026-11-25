using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
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
            ScrollIfTooTall(w);   // deferred: runs after the window has been fitted below
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

        // After a size change (App: every window's SizeChanged), once the new size has applied —
        // one pending call per window. Only a shown, normal window; a drag of its edge by the person
        // is clamped to the screen the same way.
        private static readonly DependencyProperty FitPendingProperty =
            DependencyProperty.RegisterAttached("FitPending", typeof(bool), typeof(UiHelpers), new PropertyMetadata(false));

        public static void KeepOnScreenSoon(Window w)
        {
            if (w == null || !w.IsLoaded || (bool)w.GetValue(FitPendingProperty)) return;
            w.SetValue(FitPendingProperty, true);
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                w.SetValue(FitPendingProperty, false);
                if (w.IsLoaded) KeepOnScreen(w);
            }), DispatcherPriority.Loaded);
        }

        // A window sized by its content that is taller than the screen (2026-11-24: the speed
        // window at 250 % on a 1080p laptop — its lower half and buttons were off the screen and,
        // fixed to its content, it could not be resized): as Windows programs do, the window stays
        // screen-sized and everything under the title bar scrolls. Nothing changes where it fits.
        //
        // The rows under the title bar move into one Grid inside a ScrollViewer (vertical only — a
        // horizontal one would give star columns infinite width); the title bar stays put, and the
        // root keeps two rows, so OverlayDialog still covers the whole window. Resizable windows are
        // left alone: their lists scroll by themselves. Done once per window.
        private static readonly DependencyProperty ScrollWrappedProperty =
            DependencyProperty.RegisterAttached("ScrollWrapped", typeof(bool), typeof(UiHelpers), new PropertyMetadata(false));

        public static void ScrollIfTooTall(Window w)
        {
            if (w == null) return;
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if ((bool)w.GetValue(ScrollWrappedProperty) || w.GetType().Name == "MainWindow") return;
                    bool fitted = w.ResizeMode == ResizeMode.NoResize || (w.MinHeight > 0 && w.MinHeight == w.MaxHeight);
                    if (!fitted) return;
                    var root = w.Content as Grid;
                    if (root == null || root.RowDefinitions.Count < 2 || root.ActualHeight <= 0) return;
                    var bar = root.Children.OfType<Wpf.Ui.Controls.TitleBar>().FirstOrDefault(b => Grid.GetRow(b) == 0);
                    if (bar == null) return;
                    root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
                    // Against the WINDOW: a capped window still lays its root out full height,
                    // just clipped (measured 2026-11-24: root 4268 in a 1392 window).
                    if (root.DesiredSize.Height + 2 <= w.ActualHeight + 1) return;

                    var body = new Grid();
                    foreach (var cd in root.ColumnDefinitions)
                        body.ColumnDefinitions.Add(new ColumnDefinition { Width = cd.Width, MinWidth = cd.MinWidth, MaxWidth = cd.MaxWidth });
                    for (int i = 1; i < root.RowDefinitions.Count; i++)
                    {
                        var rd = root.RowDefinitions[i];
                        body.RowDefinitions.Add(new RowDefinition { Height = rd.Height, MinHeight = rd.MinHeight, MaxHeight = rd.MaxHeight });
                    }
                    foreach (var c in root.Children.Cast<UIElement>().Where(c => c != bar && Grid.GetRow(c) >= 1).ToList())
                    {
                        int r = Grid.GetRow(c);
                        root.Children.Remove(c);
                        Grid.SetRow(c, r - 1);
                        body.Children.Add(c);
                    }
                    while (root.RowDefinitions.Count > 1) root.RowDefinitions.RemoveAt(1);
                    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    var sv = new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Focusable = false,
                        Content = body,
                    };
                    Grid.SetRow(sv, 1);
                    if (root.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(sv, root.ColumnDefinitions.Count);
                    root.Children.Add(sv);
                    w.SetValue(ScrollWrappedProperty, true);
                    Log.Info("Окно «" + w.Title + "» выше экрана — содержимое прокручивается.");
                }
                catch (Exception ex) { Log.Error("scroll-if-too-tall", ex); }
            }), DispatcherPriority.ContextIdle);
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
