using System.Windows;
using System.Windows.Controls;

namespace MihomoLauncher.Services
{
    public static class UiHelpers
    {
        // GridView has no star-sizing, so the last column is left at a fixed width and the
        // rest of the row is empty. Make the LAST GridViewColumn soak up whatever width the
        // other (fixed) columns leave, and keep it in sync as the window is resized.
        public static void FillLastGridColumn(ListView list)
        {
            if (list == null) return;
            void Resize()
            {
                if (!(list.View is GridView gv) || gv.Columns.Count == 0) return;
                double used = 0;
                for (int i = 0; i < gv.Columns.Count - 1; i++) used += gv.Columns[i].ActualWidth;
                // Leave room for the vertical scrollbar + the ListView's border/padding so
                // the last column never forces a horizontal scrollbar.
                double avail = list.ActualWidth - used - SystemParameters.VerticalScrollBarWidth - 14;
                if (avail > 80) gv.Columns[gv.Columns.Count - 1].Width = avail;
            }
            list.SizeChanged += (s, e) => Resize();
            list.Loaded += (s, e) => Resize();
        }


        // WPF-UI FluentWindow leaves a tall blank gap under the content when using
        // SizeToContent. Fix it by reading the already-measured content height (do NOT
        // call Measure here — that throws if a layout pass is in progress) and setting
        // the window height explicitly. Call once from the window's Loaded handler.
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
