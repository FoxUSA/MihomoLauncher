using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MihomoLauncher.Views
{
    // One tile in a window of its own (2026-11-25: «вынести плитку как есть в детач окно»): no
    // frame, no title, always on top, not in the taskbar — just the tile, 1 in 1 as in the main
    // window except the bin, which is a ✕ closing this window. The main window keeps the tile.
    // Moved by dragging the tile itself; a press that does not move is the tile's own click.
    // Dropped back on the main window it closes (the main window lights up while it is over it).
    internal sealed class TileWindow : Window
    {
        public readonly string TileId;
        public bool UserClosed;                      // ✕ / dropped home — forget it; else (app exit) keep it

        public Func<Point, bool> IsOverMain;         // screen pixels → over the main window?
        public Action<bool> DropHint;                // light the main window up / back
        public Action Home;                          // dropped on the main window
        public Action<TileWindow> Moved;             // left somewhere new: remember it
        public Action Touched;                       // any press on it (dialogs it leads to may need the main window)

        private readonly ContentControl _host = new ContentControl();
        private bool _pending, _moving, _overMain;
        private bool _moved;                         // this press moved the window: its release is no click
        private DateTime _pressAt;
        private POINT _press;
        private double _startLeft, _startTop;
        private const int Threshold = 6;   // device pixels before a press becomes a move

        public TileWindow(string tileId)
        {
            TileId = tileId;
            // Not the app's window style (the library's): it paints its own dark face and sets a
            // minimum size — a square showed behind the tile (2026-11-25). Only the tile is seen.
            Style = null;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Title = "Mihomo Launcher";
            Content = _host;

            PreviewMouseLeftButtonDown += (s, e) =>
            {
                Touched?.Invoke();
                GetCursorPos(out _press);
                _startLeft = Left;
                _startTop = Top;
                _pending = true;
                _moving = false;
                _moved = false;
                _pressAt = DateTime.UtcNow;
            };
            PreviewMouseRightButtonDown += (s, e) => Touched?.Invoke();
            PreviewMouseMove += (s, e) =>
            {
                if (!_pending) return;
                if (e.LeftButton != MouseButtonState.Pressed) { _pending = false; EndMove(false); return; }
                POINT p;
                GetCursorPos(out p);
                if (!_moving)
                {
                    if (Math.Abs(p.X - _press.X) < Threshold && Math.Abs(p.Y - _press.Y) < Threshold) return;
                    _moving = true;
                    _moved = true;
                    CaptureMouse();
                }
                var dpi = VisualTreeHelper.GetDpi(this);
                Left = _startLeft + (p.X - _press.X) / dpi.DpiScaleX;
                Top = _startTop + (p.Y - _press.Y) / dpi.DpiScaleY;
                bool over = IsOverMain != null && IsOverMain(new Point(p.X, p.Y));
                if (over != _overMain) { _overMain = over; DropHint?.Invoke(over); }
                e.Handled = true;
            };
            PreviewMouseLeftButtonUp += (s, e) =>
            {
                _pending = false;
                // The release after a move, or after a hold (MainWindow.TileHoldIsNotClick), is not
                // the tile's click (nor its ✕ / gear) — even if the move already ended on its own.
                bool held = DateTime.UtcNow - _pressAt > MainWindow.TileHoldIsNotClick;
                if (_moved || held) e.Handled = true;
                _moved = false;
                if (_moving) EndMove(true);
            };
            LostMouseCapture += (s, e) => { if (_moving) EndMove(true); };
        }

        // The tile on an opaque backing of its own shape, with ClearType text (2026-11-25: «тусклая»):
        // in a see-through window WPF draws text in grey shades unless told the ground under it is
        // solid, and the tile came out paler than in the main window.
        public void SetTile(FrameworkElement tile)
        {
            // Pinned to the top-left and exactly the tile's size: stretched over the whole window the
            // backing stood out round the tile as a second outline (2026-11-25, «опять призрак?»).
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.VerticalAlignment = VerticalAlignment.Top;
            var backing = new Border
            {
                Margin = tile.Margin,
                Width = tile.Width,
                Height = tile.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(9),
                IsHitTestVisible = false,
            };
            backing.SetResourceReference(Border.BackgroundProperty, "Theme.TileBg");
            g.Children.Add(backing);
            g.Children.Add(tile);
            RenderOptions.SetClearTypeHint(g, ClearTypeHint.Enabled);
            TextOptions.SetTextRenderingMode(g, TextRenderingMode.ClearType);
            _host.Content = g;
        }

        private void EndMove(bool dropped)
        {
            if (!_moving) return;
            _moving = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            bool home = _overMain;
            if (_overMain) { _overMain = false; DropHint?.Invoke(false); }
            if (!dropped) return;
            if (home) Home?.Invoke();
            else Moved?.Invoke(this);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);
    }
}
