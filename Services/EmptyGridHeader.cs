using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MihomoLauncher.Services
{
    // An empty table keeps the header it has with rows (2026-11-23: «Добавить правило» showed
    // «Плитка | Тип | Имя | '» squeezed to the left, the «Что открывается» caption cut to one letter
    // and the rest of the header bar empty).
    //
    // Why: the DataGrid shares the room among its star (*) columns by the width of its rows'
    // viewport, and with no rows there is no viewport to measure — a star column falls back to its
    // MinWidth. So, while the table has no rows, each star column's MinWidth is raised to its share
    // of what the fixed columns leave (by its star weight); the moment a row appears the original
    // MinWidth is put back and the grid shares the room itself again. Re-done on every resize.
    //
    // On for every table through AppDataGridStyle (Themes\Common\DataGridStyles.xaml), so a new table
    // gets it without anyone remembering the trap.
    public static class EmptyGridHeader
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
            "Fill", typeof(bool), typeof(EmptyGridHeader), new PropertyMetadata(false, OnFillChanged));

        public static bool GetFill(DependencyObject o) => (bool)o.GetValue(FillProperty);
        public static void SetFill(DependencyObject o, bool value) => o.SetValue(FillProperty, value);

        // A star column's own MinWidth, kept the first time it is raised (NaN = not touched yet).
        private static readonly DependencyProperty OwnMinProperty = DependencyProperty.RegisterAttached(
            "OwnMin", typeof(double), typeof(EmptyGridHeader), new PropertyMetadata(double.NaN));

        // One pending re-fit per grid: a burst of row changes / resizes is one pass.
        private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
            "Pending", typeof(bool), typeof(EmptyGridHeader), new PropertyMetadata(false));

        private static void OnFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var grid = d as DataGrid;
            if (grid == null || !(bool)e.NewValue) return;
            grid.Loaded += (s, a) => Schedule(grid);
            grid.SizeChanged += (s, a) => { if (a.WidthChanged) Schedule(grid); };
            grid.IsVisibleChanged += (s, a) => { if (grid.IsVisible) Schedule(grid); };
            ((INotifyCollectionChanged)grid.Items).CollectionChanged += (s, a) => Schedule(grid);
        }

        private static void Schedule(DataGrid grid)
        {
            if ((bool)grid.GetValue(PendingProperty)) return;
            grid.SetValue(PendingProperty, true);
            // After layout: the fixed columns' widths are known by then.
            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                grid.SetValue(PendingProperty, false);
                try { Apply(grid); } catch { /* cosmetic — never worth an error */ }
            }), DispatcherPriority.Loaded);
        }

        private static void Apply(DataGrid grid)
        {
            if (!GetFill(grid) || !grid.IsLoaded) return;
            var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
            var stars = shown.Where(c => c.Width.IsStar).ToList();
            if (stars.Count == 0) return;
            foreach (var c in stars)
                if (double.IsNaN((double)c.GetValue(OwnMinProperty))) c.SetValue(OwnMinProperty, c.MinWidth);

            if (grid.Items.Count > 0)
            {
                // Rows: the grid shares the room itself — just give back what we raised.
                foreach (var c in stars)
                {
                    double own = (double)c.GetValue(OwnMinProperty);
                    if (Math.Abs(c.MinWidth - own) > 0.5) c.MinWidth = own;
                }
                return;
            }

            double avail = Available(grid);
            if (avail <= 0) return;
            double fixedW = shown.Where(c => !c.Width.IsStar).Sum(c => c.ActualWidth);
            double rest = avail - fixedW - 1;   // a pixel short: never a reason to overflow
            if (rest <= 0) return;
            double weight = stars.Sum(c => c.Width.Value);
            if (weight <= 0) return;
            foreach (var c in stars)
            {
                double own = (double)c.GetValue(OwnMinProperty);
                double want = Math.Max(own, Math.Floor(rest * c.Width.Value / weight));
                if (Math.Abs(c.MinWidth - want) > 0.5) c.MinWidth = want;
            }
        }

        // The width the columns live in: the grid's inside, less the row-header offset and a
        // vertical scroll bar when one is shown («Visible» in the shared style shows it empty too).
        private static double Available(DataGrid grid)
        {
            double w = grid.ActualWidth - grid.BorderThickness.Left - grid.BorderThickness.Right
                     - grid.Padding.Left - grid.Padding.Right - grid.CellsPanelHorizontalOffset;
            var sv = grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer;
            if (sv != null && sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
            {
                var bar = sv.Template?.FindName("PART_VerticalScrollBar", sv) as FrameworkElement;
                w -= bar != null && bar.ActualWidth > 0 ? bar.ActualWidth : SystemParameters.VerticalScrollBarWidth;
            }
            return w;
        }
    }
}
