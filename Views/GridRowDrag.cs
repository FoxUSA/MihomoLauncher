using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // Drag-to-reorder for the rows of a Settings table (replaces the old «Выше» / «Ниже»).
    // Same feel as the main-screen tiles: grab a row, move past a small threshold and a picture
    // of it follows the cursor while the real row turns into a faint hole travelling through the
    // table; the other rows slide aside. On release the new order goes to onDrop. Rows that are
    // pinned (here: «Всё остальное», always first) neither move nor let anything above them.
    // A press that never passes the threshold is an ordinary click / double-click.
    internal sealed class GridRowDrag<T> where T : class
    {
        private const double Threshold = 6;
        private const double EdgeScroll = 22;   // px from the top/bottom edge that scrolls the table

        private readonly DataGrid _grid;
        private readonly ObservableCollection<T> _rows;
        private readonly Func<T, bool> _pinned;
        private readonly Action<List<T>, T> _onDrop;

        private T _pending;                 // pressed, not moved far enough yet
        private Point _pressPos;            // in the grid's coordinates
        private T _item;                    // non-null while a drag is in progress
        private int _startIndex;
        private double _grabY;              // cursor offset inside the row
        private double _rowX;
        private RowGhostAdorner _ghost;

        public GridRowDrag(DataGrid grid, ObservableCollection<T> rows, Func<T, bool> pinned, Action<List<T>, T> onDrop)
        {
            _grid = grid; _rows = rows; _pinned = pinned ?? (x => false); _onDrop = onDrop;
            grid.PreviewMouseLeftButtonDown += OnDown;
            grid.PreviewMouseMove += OnMove;
            grid.PreviewMouseLeftButtonUp += OnUp;
            // LostMouseCapture bubbles — only the grid's own loss ends the drag.
            grid.LostMouseCapture += (s, e) => { if (_item != null && e.OriginalSource == _grid) End(); };
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pending = null;
            if (_item != null) return;
            var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null) return;
            // The checkbox column reacts on the press itself — a drag can't start from there.
            var cell = Ancestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell != null && cell.Column is DataGridTemplateColumn) return;
            var item = row.Item as T;
            if (item == null || _pinned(item)) return;
            _pending = item;
            _pressPos = e.GetPosition(_grid);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_item != null)
            {
                var p = e.GetPosition(_grid);
                _ghost?.MoveTo(new Point(_rowX, p.Y - _grabY));
                AutoScroll(p);
                MoveHoleToward(e);
                e.Handled = true;
                return;
            }
            if (_pending == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _pending = null; return; }
            var pos = e.GetPosition(_grid);
            if (Math.Abs(pos.Y - _pressPos.Y) < Threshold && Math.Abs(pos.X - _pressPos.X) < Threshold) return;
            Begin(_pending);
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _pending = null;
            if (_item == null) return;
            e.Handled = true;
            Mouse.Capture(null);   // → LostMouseCapture → End
            if (_item != null) End();
        }

        private void Begin(T item)
        {
            _pending = null;
            var row = Row(item);
            if (row == null) return;
            _item = item;
            _startIndex = _rows.IndexOf(item);
            var topLeft = row.TranslatePoint(new Point(0, 0), _grid);
            _grabY = _pressPos.Y - topLeft.Y;
            _rowX = topLeft.X;

            // Snapshot BEFORE dimming the row, so the ghost looks like the real thing.
            var layer = AdornerLayer.GetAdornerLayer(_grid);
            if (layer != null)
            {
                _ghost = new RowGhostAdorner(_grid, Snapshot(row), row.ActualWidth, row.ActualHeight);
                layer.Add(_ghost);
                _ghost.MoveTo(new Point(_rowX, _pressPos.Y - _grabY));
            }
            row.Opacity = 0.18;
            Mouse.Capture(_grid, CaptureMode.Element);
        }

        private void End()
        {
            var item = _item;
            _item = null;
            if (_ghost != null)
            {
                AdornerLayer.GetAdornerLayer(_grid)?.Remove(_ghost);
                _ghost = null;
            }
            foreach (var r in RealizedRows()) r.Opacity = 1.0;
            if (item != null && _rows.IndexOf(item) != _startIndex)
                _onDrop?.Invoke(_rows.ToList(), item);
        }

        // Put the dragged row's hole where the cursor is; animate the rest.
        private void MoveHoleToward(MouseEventArgs e)
        {
            int from = _rows.IndexOf(_item);
            if (from < 0) return;
            int minIndex = 0;
            while (minIndex < _rows.Count && _pinned(_rows[minIndex])) minIndex++;

            int to = from;
            double best = double.MaxValue;
            foreach (var row in RealizedRows())
            {
                var it = row.Item as T;
                int i = it == null ? -1 : _rows.IndexOf(it);
                if (i < minIndex) continue;
                var panel = VisualTreeHelper.GetParent(row) as UIElement;
                if (panel == null) continue;
                var r = LayoutInformation.GetLayoutSlot(row);
                double d = Math.Abs(e.GetPosition(panel).Y - (r.Y + r.Height / 2));
                if (d < best) { best = d; to = i; }
            }
            if (to == from || to < minIndex) return;

            // FLIP: remember where every row is drawn now, move, then slide each from there.
            var before = new Dictionary<object, double>();
            foreach (var row in RealizedRows())
            {
                var tt = row.RenderTransform as TranslateTransform;
                before[row.Item] = LayoutInformation.GetLayoutSlot(row).Y + (tt?.Y ?? 0);
            }

            _rows.Move(from, to);
            _grid.UpdateLayout();
            if (Mouse.Captured != _grid) Mouse.Capture(_grid, CaptureMode.Element);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var row in RealizedRows())
            {
                if (row.Item == _item) { row.Opacity = 0.18; row.RenderTransform = null; continue; }
                row.Opacity = 1.0;
                if (!before.TryGetValue(row.Item, out var oldY)) continue;
                double oy = oldY - LayoutInformation.GetLayoutSlot(row).Y;
                if (Math.Abs(oy) < 0.5) continue;
                var tt = row.RenderTransform as TranslateTransform;
                if (tt == null || tt.IsFrozen) { tt = new TranslateTransform(); row.RenderTransform = tt; }
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(oy, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            }
        }

        // Near the top/bottom edge of a table taller than its box, scroll toward the cursor.
        private void AutoScroll(Point p)
        {
            var sv = Descendant<ScrollViewer>(_grid);
            if (sv == null) return;
            var q = _grid.TranslatePoint(p, sv);
            if (q.Y < EdgeScroll + 26) sv.LineUp();          // 26 = the column header
            else if (q.Y > sv.ActualHeight - EdgeScroll) sv.LineDown();
        }

        private DataGridRow Row(T item) => _grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;

        private IEnumerable<DataGridRow> RealizedRows()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_grid.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow r) yield return r;
        }

        private TAnc Ancestor<TAnc>(DependencyObject d) where TAnc : DependencyObject
        {
            while (d != null && d != _grid)
            {
                if (d is TAnc hit) return hit;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static TD Descendant<TD>(DependencyObject d) where TD : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is TD hit) return hit;
                var deeper = Descendant<TD>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static ImageSource Snapshot(FrameworkElement fe)
        {
            double w = Math.Max(1, fe.ActualWidth), h = Math.Max(1, fe.ActualHeight);
            var dpi = VisualTreeHelper.GetDpi(fe);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(fe), null, new Rect(0, 0, w, h));
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        // The picture of the row under the cursor: with a shadow, not hit-testable.
        private sealed class RowGhostAdorner : Adorner
        {
            private readonly Border _box;
            private Point _topLeft;

            public RowGhostAdorner(UIElement adorned, ImageSource snap, double w, double h) : base(adorned)
            {
                IsHitTestVisible = false;
                _box = new Border
                {
                    Width = w, Height = h, Opacity = 0.95,
                    Background = (Brush)Application.Current.TryFindResource("ControlFillColorSecondaryBrush")
                                 ?? Brushes.Transparent,
                    BorderBrush = Theme.AccentBlue, BorderThickness = new Thickness(1),
                    Child = new Image { Source = snap, Width = w, Height = h },
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Theme.ShadowColor, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.6,
                    },
                };
                AddVisualChild(_box);
            }

            public void MoveTo(Point topLeft) { _topLeft = topLeft; InvalidateArrange(); }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _box;
            protected override Size MeasureOverride(Size constraint) { _box.Measure(constraint); return _box.DesiredSize; }
            protected override Size ArrangeOverride(Size finalSize)
            {
                _box.Arrange(new Rect(_topLeft.X, _topLeft.Y, _box.Width, _box.Height));
                return finalSize;
            }
        }
    }
}
