using System;
using System.Windows;
using System.Windows.Input;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // Host for a tile's speed view: a movable window of its own (was an overlay locked inside the
    // main window, 2026-11-16). Esc closes it.
    public partial class SpeedWindow : FluentWindow
    {
        // resizable: the detached «кто и куда» (2026-11-16) — sized by the user, the body fills it.
        public SpeedWindow(string title, UIElement body, bool resizable = false)
        {
            InitializeComponent();
            Title = title;
            Bar.Title = title;
            Body.Content = body;
            PreviewKeyDown += (s, e) =>
            {
                // Esc inside the «Очистить?» confirmation is that overlay's own.
                if (e.Key != Key.Escape || OverlayOpen()) return;
                e.Handled = true;
                // An open calendar of the period row closes first (SpeedPeriodBar).
                if (EscapeFirst != null && EscapeFirst()) return;
                // In a search box with text: clears it first (2026-11-25), the next Esc closes.
                var box = Keyboard.FocusedElement as System.Windows.Controls.TextBox;
                if (box != null && box.Text.Length > 0) { box.Clear(); return; }
                Close();
            };
            // A click anywhere outside the field being typed in takes the focus off it (2026-11-25:
            // «сброс фокуса кликом в любое место»), not only a click on something focusable. The
            // click itself goes on as usual.
            // Also: rows and tabs here are not focusable, so after a click nothing had the keyboard
            // and the ↑ / ↓ never arrived (2026-11-25) — the window itself takes it then.
            Focusable = true;
            PreviewMouseDown += (s, e) =>
            {
                var src = e.OriginalSource as DependencyObject;
                var focused = Keyboard.FocusedElement as DependencyObject;
                var box = focused as System.Windows.Controls.Primitives.TextBoxBase;
                if (box != null && IsWithin(src, box)) return;                 // typing on in the field
                if (box == null && focused != null && IsWithin(focused, this) && !ReferenceEquals(focused, this)) return;   // a button etc. keeps it
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // After the press: a field or button it landed on has taken the focus by then.
                    var now = Keyboard.FocusedElement as DependencyObject;
                    if (now == null || !IsWithin(now, this) || now is System.Windows.Controls.Primitives.TextBoxBase && !IsWithin(src, now))
                        Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
            };
            Activated += (s, e) =>
            {
                var now = Keyboard.FocusedElement as DependencyObject;
                if (now == null || !IsWithin(now, this)) Focus();
            };
            if (resizable)
            {
                ResizeMode = ResizeMode.CanResize;
                SizeToContent = SizeToContent.Manual;
                Width = 1100;
                Height = 640;
                MinWidth = 720;
                MinHeight = 380;
                Bar.ShowMaximize = true;
                var grid = (System.Windows.Controls.Grid)Content;
                grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                Body.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                Body.VerticalContentAlignment = VerticalAlignment.Stretch;
                Loaded += (s, e) => UiHelpers.KeepOnScreen(this);
                return;
            }
            // Wider by hand (2026-11-25: «почему я не могу увеличить окно по ширине»): the chart and
            // the addresses stretch; the height still follows the content and stays locked.
            ResizeMode = ResizeMode.CanResize;
            Body.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _baseWidth = Width;
            Loaded += (s, e) =>
            {
                FitToContent();
                UiHelpers.KeepOnScreen(this);
                // The body changes while open (refresh every few seconds, «Всего с…» appearing):
                // follow it, never leave empty space under the buttons.
                Body.SizeChanged += (s2, e2) => { if (e2.HeightChanged) FitToContent(); };
            };
        }

        // Exactly as tall as the content, and locked there (2026-11-16): ResizeMode="NoResize" is
        // not enough for a WPF-UI FluentWindow — its edges could still be dragged, and all that
        // showed up was an empty area. Min = Max = the fitted size, so a drag changes nothing.
        private void FitToContent()
        {
            var root = Content as FrameworkElement;
            if (root == null) return;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            root.Measure(new Size(root.ActualWidth > 0 ? root.ActualWidth : width, double.PositiveInfinity));
            // + the 1-px window frame top and bottom (2026-11-23: was +12, as UiHelpers.FitHeightToContent
            // has — under the buttons that showed as a band twice as tall as in Settings).
            double h = Math.Min(root.DesiredSize.Height + 2, SystemParameters.WorkArea.Height);
            if (h <= 120) return;
            SizeToContent = SizeToContent.Manual;
            MinHeight = 0;
            MaxHeight = double.PositiveInfinity;
            Height = h;
            MinHeight = MaxHeight = h;
            // Width: never under the designed one, up to the screen.
            MinWidth = _baseWidth;
            MaxWidth = Math.Max(_baseWidth, SystemParameters.WorkArea.Width);
        }

        private double _baseWidth = 960;

        // Asked first on Esc; true = it took the key (the period row's calendar was open).
        public Func<bool> EscapeFirst;

        private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
        {
            while (d != null)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private bool OverlayOpen()
        {
            var root = Content as System.Windows.Controls.Grid;
            return root != null && root.Children.Count > 2;
        }
    }
}
