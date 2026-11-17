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
                if (e.Key == Key.Escape && !OverlayOpen()) { e.Handled = true; Close(); }
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
            // + the thin window frame, as UiHelpers.FitHeightToContent does.
            double h = Math.Min(root.DesiredSize.Height + 12, SystemParameters.WorkArea.Height);
            if (h <= 120) return;
            SizeToContent = SizeToContent.Manual;
            MinHeight = 0;
            MaxHeight = double.PositiveInfinity;
            Height = h;
            MinHeight = MaxHeight = h;
            MinWidth = MaxWidth = width;
        }

        private bool OverlayOpen()
        {
            var root = Content as System.Windows.Controls.Grid;
            return root != null && root.Children.Count > 2;
        }
    }
}
