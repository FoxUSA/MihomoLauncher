using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // Drag-to-reorder for the main-screen tiles. Grab a tile, move past a small threshold and
    // a snapshot of it follows the cursor while the real tile turns into a faint hole that
    // travels through the WrapPanel; the other tiles slide aside (FLIP animation). On release
    // the order is written back into _config.Apps. «Всё остальное» stays first and can't move.
    // A press that never passes the threshold is an ordinary click — nothing is swallowed.
    public partial class MainWindow
    {
        private const double TileDragThreshold = 6;

        private FrameworkElement _dragPending;      // pressed, not moved far enough yet
        private Point _dragPressPos;                // in the WrapPanel's coordinates
        private FrameworkElement _dragTile;         // non-null while a drag is in progress
        private Panel _dragPanel;
        private Vector _dragGrab;                   // cursor offset inside the tile
        private TileGhostAdorner _dragGhost;
        private bool _tileRebuildPending;           // RebuildTiles asked during a drag

        private bool TileDragActive => _dragTile != null;

        private void InitTileDrag()
        {
            TilesPanel.PreviewMouseLeftButtonDown += OnTilesMouseDown;
            TilesPanel.PreviewMouseMove += OnTilesMouseMove;
            TilesPanel.PreviewMouseLeftButtonUp += OnTilesMouseUp;
            // LostMouseCapture bubbles — only the panel's own loss ends the drag.
            TilesPanel.LostMouseCapture += (s, e) => { if (TileDragActive && e.OriginalSource == TilesPanel) EndTileDrag(); };
        }

        // The tile (a direct child of the WrapPanel) under the event source, or null.
        private FrameworkElement TileFromSource(object source)
        {
            var d = source as DependencyObject;
            while (d != null && d != TilesPanel)
            {
                var parent = VisualTreeHelper.GetParent(d);
                if (parent is Panel && d is FrameworkElement fe && fe.Tag is AppEntry) return fe;
                d = parent;
            }
            return null;
        }

        private void OnTilesMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragPending = null;
            if (TileDragActive || _state == UiState.Checking) return;
            var tile = TileFromSource(e.OriginalSource);
            var entry = tile?.Tag as AppEntry;
            if (entry == null || entry.IsAllTraffic) return;
            _dragPanel = VisualTreeHelper.GetParent(tile) as Panel;
            if (_dragPanel == null) return;
            _dragPending = tile;
            _dragPressPos = e.GetPosition(_dragPanel);
        }

        private void OnTilesMouseMove(object sender, MouseEventArgs e)
        {
            if (TileDragActive)
            {
                var p = e.GetPosition(_dragPanel);
                _dragGhost?.MoveTo(p - _dragGrab);
                MoveHoleToward(p);
                e.Handled = true;
                return;
            }
            if (_dragPending == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragPending = null; return; }
            var pos = e.GetPosition(_dragPanel);
            if (Math.Abs(pos.X - _dragPressPos.X) < TileDragThreshold
                && Math.Abs(pos.Y - _dragPressPos.Y) < TileDragThreshold) return;
            BeginTileDrag(_dragPending, pos);
            e.Handled = true;
        }

        private void OnTilesMouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragPending = null;
            if (!TileDragActive) return;
            // Swallow the up: it must not reach the tile's launch / ⟳ / ✕ handlers.
            e.Handled = true;
            Mouse.Capture(null);   // → LostMouseCapture → EndTileDrag
            if (TileDragActive) EndTileDrag();
        }

        private void BeginTileDrag(FrameworkElement tile, Point pos)
        {
            _dragPending = null;
            _dragTile = tile;
            var slot = LayoutInformation.GetLayoutSlot(tile);
            var tileTopLeft = new Point(slot.X + tile.Margin.Left, slot.Y + tile.Margin.Top);
            _dragGrab = _dragPressPos - tileTopLeft;

            // Snapshot BEFORE dimming the tile, so the ghost looks like the real thing.
            var layer = AdornerLayer.GetAdornerLayer(_dragPanel);
            if (layer != null)
            {
                _dragGhost = new TileGhostAdorner(_dragPanel, Snapshot(tile), tile.ActualWidth, tile.ActualHeight);
                layer.Add(_dragGhost);
                _dragGhost.MoveTo(pos - _dragGrab);
            }
            tile.Opacity = 0.18;
            Mouse.Capture(TilesPanel, CaptureMode.Element);
        }

        private void EndTileDrag()
        {
            var tile = _dragTile;
            _dragTile = null;
            if (_dragGhost != null)
            {
                AdornerLayer.GetAdornerLayer(_dragPanel)?.Remove(_dragGhost);
                _dragGhost = null;
            }
            if (tile != null) tile.Opacity = 1.0;
            SaveTileOrder();
            if (_tileRebuildPending)
            {
                _tileRebuildPending = false;
                RebuildTiles();
            }
        }

        // Put the dragged tile's hole in the slot nearest to the cursor; animate the rest.
        private void MoveHoleToward(Point p)
        {
            var items = TilesPanel.Items;
            int from = items.IndexOf(_dragTile);
            if (from < 0) return;
            int minIndex = 0;
            while (minIndex < items.Count && (items[minIndex] as FrameworkElement)?.Tag is AppEntry a && a.IsAllTraffic) minIndex++;

            int to = from;
            double best = double.MaxValue;
            for (int i = minIndex; i < items.Count; i++)
            {
                var fe = items[i] as FrameworkElement;
                if (fe == null) continue;
                var r = LayoutInformation.GetLayoutSlot(fe);
                double dx = p.X - (r.X + r.Width / 2), dy = p.Y - (r.Y + r.Height / 2);
                double d = dx * dx + dy * dy;
                if (d < best) { best = d; to = i; }
            }
            if (to == from) return;

            // FLIP: remember where every tile is drawn now, move, then slide each from there.
            var before = new Dictionary<FrameworkElement, Point>();
            foreach (var fe in items.OfType<FrameworkElement>())
            {
                var r = LayoutInformation.GetLayoutSlot(fe);
                var tt = fe.RenderTransform as TranslateTransform;
                before[fe] = new Point(r.X + (tt?.X ?? 0), r.Y + (tt?.Y ?? 0));
            }

            items.RemoveAt(from);
            items.Insert(to, _dragTile);
            _dragPanel.UpdateLayout();
            // Re-inserting the element drops the capture's notion of the tile — keep it on the panel.
            if (Mouse.Captured != TilesPanel) Mouse.Capture(TilesPanel, CaptureMode.Element);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var fe in items.OfType<FrameworkElement>())
            {
                if (fe == _dragTile || !before.TryGetValue(fe, out var old)) continue;
                var r = LayoutInformation.GetLayoutSlot(fe);
                double ox = old.X - r.X, oy = old.Y - r.Y;
                if (Math.Abs(ox) < 0.5 && Math.Abs(oy) < 0.5) continue;
                var tt = fe.RenderTransform as TranslateTransform;
                if (tt == null || tt.IsFrozen) { tt = new TranslateTransform(); fe.RenderTransform = tt; }
                var dur = TimeSpan.FromMilliseconds(180);
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ox, 0, dur) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oy, 0, dur) { EasingFunction = ease });
            }
        }

        // Write the on-screen order back into _config.Apps: tile rules take the positions tile
        // rules already held, in the new order; rules without a tile stay where they were.
        private void SaveTileOrder()
        {
            try
            {
                var ordered = TilesPanel.Items.OfType<FrameworkElement>()
                    .Select(t => t.Tag as AppEntry)
                    .Where(a => a != null && !a.IsAllTraffic).ToList();
                var set = new HashSet<AppEntry>(ordered);
                var slots = Enumerable.Range(0, _config.Apps.Count).Where(i => set.Contains(_config.Apps[i])).ToList();
                if (slots.Count != ordered.Count) return;
                bool changed = false;
                for (int k = 0; k < slots.Count; k++)
                {
                    if (_config.Apps[slots[k]] == ordered[k]) continue;
                    _config.Apps[slots[k]] = ordered[k];
                    changed = true;
                }
                if (!changed) return;
                AppConfigStore.Save(_config);
                Log.Info("Плитки: новый порядок — " + string.Join(", ", ordered.Select(a => a.Name)));
            }
            catch (Exception ex) { Log.Error("tile-order-save", ex); }
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

        // The picture of the tile under the cursor: slightly enlarged, with a shadow, not hit-testable.
        private sealed class TileGhostAdorner : Adorner
        {
            private readonly Image _img;
            private Point _topLeft;

            public TileGhostAdorner(UIElement adorned, ImageSource snap, double w, double h) : base(adorned)
            {
                IsHitTestVisible = false;
                _img = new Image
                {
                    Source = snap, Width = w, Height = h, Opacity = 0.92,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.06, 1.06),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Theme.ShadowColor, BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6,
                    },
                };
                AddVisualChild(_img);
            }

            public void MoveTo(Point topLeft) { _topLeft = topLeft; InvalidateArrange(); }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _img;
            protected override Size MeasureOverride(Size constraint) { _img.Measure(constraint); return _img.DesiredSize; }
            protected override Size ArrangeOverride(Size finalSize)
            {
                _img.Arrange(new Rect(_topLeft.X, _topLeft.Y, _img.Width, _img.Height));
                return finalSize;
            }
        }
    }
}
