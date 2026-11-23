using System.Windows;
using System.Windows.Media;

namespace MihomoLauncher.Services
{
    // A table inside a rounded 1 px frame: one outline, not two (2026-11-22: «двоится»).
    //
    // The shared grid styles close a table with their own left / top / right lines; put inside a
    // framed Border they drew a second outline a pixel away from the frame's. The cure, the same
    // in every such place:
    //
    //   <Border CornerRadius="8" BorderThickness="1" …>
    //       <DataGrid Margin="-1,-1,-1,0" svc:FrameClip.Radius="7" … />
    //
    // The grid is pushed 1 px out on those three sides, and this clips it back to the inside of
    // the frame — a rounded rectangle, so the square header corners don't poke through the
    // frame's round ones either. The table's outer lines are cut off; the frame is the outline.
    // Radius = the frame's corner radius minus its line.
    public static class FrameClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(FrameClip), new PropertyMetadata(-1.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject o, double value) => o.SetValue(RadiusProperty, value);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var fe = d as FrameworkElement;
            if (fe == null) return;
            fe.SizeChanged -= OnSizeChanged;
            if ((double)e.NewValue < 0) { fe.Clip = null; return; }
            fe.SizeChanged += OnSizeChanged;
            Apply(fe);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply(sender as FrameworkElement);

        private static void Apply(FrameworkElement fe)
        {
            if (fe == null) return;
            // The element hangs 1 px out to the left, top and right (Margin -1,-1,-1,0).
            double w = fe.ActualWidth - 2, h = fe.ActualHeight - 1, r = GetRadius(fe);
            fe.Clip = w > 0 && h > 0 ? new RectangleGeometry(new Rect(1, 1, w, h), r, r) : null;
        }
    }
}
