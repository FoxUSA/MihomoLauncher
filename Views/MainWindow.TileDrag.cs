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
        // Held longer than this and let go without moving: not a click — the tile does not open
        // (2026-11-25: «случайно жмётся открыть программу»). Windows' own double-click time, 500 ms
        // by default: anything longer than that is a hold, not a press.
        internal static TimeSpan TileHoldIsNotClick => TimeSpan.FromMilliseconds(Math.Max(400, System.Windows.Forms.SystemInformation.DoubleClickTime));
        private DateTime _tilePressAt;

        private FrameworkElement _dragPending;      // pressed, not moved far enough yet
        private Point _dragPressPos;                // in the WrapPanel's coordinates
        private FrameworkElement _dragTile;         // non-null while a drag is in progress
        private Panel _dragPanel;
        private Vector _dragGrab;                   // cursor offset inside the tile
        private TileGhostAdorner _dragGhost;
        private bool _tileRebuildPending;           // RebuildTiles asked during a drag
        private bool _dragDetachOnly;               // «Всё остальное»: never moves, can only be pulled out
        private int _dragFromIndex;                 // where the tile was: back there if it is pulled out
        private bool _dragOutside;                  // the cursor is past the window's edge
        private Point? _dragDetachAt;               // let go outside: the tile's window goes there (screen DIP)

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
                // A press on text made of Runs (the speed plate, 2026-11-16) starts at a Run — not a
                // Visual: VisualTreeHelper threw «Run не является Visual». Text elements go up the
                // logical tree to their TextBlock.
                var parent = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                if (parent is Panel && d is FrameworkElement fe && fe.Tag is AppEntry) return fe;
                d = parent;
            }
            return null;
        }

        private void OnTilesMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragPending = null;
            _dragSwallowUp = false;
            _tilePressAt = DateTime.UtcNow;
            // Any time (2026-11-25: «должен иметь возможность схватить её в любое время»): also
            // while a VPN connects or a program starts. An order changed meanwhile is saved at
            // once and reaches the engine when the connecting is over (ApplyRuleOrder waits).
            if (TileDragActive) return;
            var tile = TileFromSource(e.OriginalSource);
            var entry = tile?.Tag as AppEntry;
            if (entry == null) return;
            _dragDetachOnly = entry.IsBuiltIn;
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
                // Past the window's edge (2026-11-25): the same picture goes on following the
                // cursor out there; the tiles and tabs stay as they are.
                var px = PointToScreen(e.GetPosition(this));
                _dragOutside = !OverMainPx(px);
                if (_dragOutside)
                {
                    if (_dragGhost != null) _dragGhost.Visibility = Visibility.Hidden;
                    SetTabDrop(null);
                    ShowPullWindow(PxToDip(px));
                    e.Handled = true;
                    return;
                }
                HidePullWindow();
                if (_dragGhost != null) _dragGhost.Visibility = Visibility.Visible;
                _dragGhost?.MoveTo(p - _dragGrab);
                if (_dragDetachOnly) { e.Handled = true; return; }
                // Over a tab (or «+») below: it lights up and takes the tile on release
                // (MainWindow.TileTabs); the tiles stay where they are meanwhile.
                var tab = TabUnder(e);
                SetTabDrop(tab);
                if (tab == null) MoveHoleToward(p);
                e.Handled = true;
                return;
            }
            if (_dragPending == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragPending = null; return; }
            var pos = e.GetPosition(_dragPanel);
            if (Math.Abs(pos.X - _dragPressPos.X) < TileDragThreshold
                && Math.Abs(pos.Y - _dragPressPos.Y) < TileDragThreshold) return;
            // The order of the tiles is routing (further left wins) — behind the access password.
            // Ask once the drag is unmistakable; this press must not then launch the tile.
            if (!_dragDetachOnly && !AccessLock.IsOpen(_config))
            {
                _dragPending = null;
                _dragSwallowUp = true;
                e.Handled = true;
                _ = LockPrompt.RequireAsync(this, _config);
                return;
            }
            AccessLock.Touch();
            // Connecting / starting redraws the tiles: the one pressed may be gone by now — the
            // drag takes the tile of the same rule that is there now.
            var live = LiveTileOf(_dragPending);
            if (live == null) { _dragPending = null; return; }
            BeginTileDrag(live, pos);
            e.Handled = true;
        }

        private bool _dragSwallowUp;

        // The tile in the panel now for the rule of this (maybe redrawn-away) tile.
        private FrameworkElement LiveTileOf(FrameworkElement pressed)
        {
            if (pressed != null && TilesPanel.Items.Contains(pressed)) return pressed;
            var entry = pressed?.Tag as AppEntry;
            if (entry == null) return null;
            return TilesPanel.Items.OfType<FrameworkElement>()
                .FirstOrDefault(t => t.Tag is AppEntry a && (ReferenceEquals(a, entry) || a.Id == entry.Id));
        }

        private void OnTilesMouseUp(object sender, MouseButtonEventArgs e)
        {
            bool held = _dragPending != null && DateTime.UtcNow - _tilePressAt > TileHoldIsNotClick;
            _dragPending = null;
            if (_dragSwallowUp) { _dragSwallowUp = false; e.Handled = true; return; }
            // Held, not clicked: the tile (its launch, ▷ / ⏸, bin) gets no release.
            if (held && !TileDragActive) { e.Handled = true; return; }
            if (!TileDragActive) return;
            // Swallow the up: it must not reach the tile's launch / ⟳ / ✕ handlers.
            e.Handled = true;
            // Let go past the window's edge: the tile opens in a window of its own right there.
            if (_dragOutside) _dragDetachAt = PxToDip(PointToScreen(e.GetPosition(this)));
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
            _dragFromIndex = TilesPanel.Items.IndexOf(tile);
            _dragOutside = false;
            _dragDetachAt = null;
            MaybeShowDetachHint();

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
            var tabTarget = _tabDrop;
            SetTabDrop(null);
            var detachAt = _dragDetachAt;
            _dragDetachAt = null;
            _dragOutside = false;
            if (detachAt == null) HidePullWindow();
            if (detachAt != null && tile?.Tag is AppEntry pulled)
            {
                _pullWin = null;   // it stays: OpenTileWindow below puts it in place and remembers it
                // Pulled out: the tile goes back where it was (the order is not touched) and opens
                // on its own where it was let go — its border's top-left under the grab point.
                int now = TilesPanel.Items.IndexOf(tile);
                if (now >= 0 && now != _dragFromIndex && _dragFromIndex >= 0)
                {
                    TilesPanel.Items.RemoveAt(now);
                    TilesPanel.Items.Insert(Math.Min(_dragFromIndex, TilesPanel.Items.Count), tile);
                }
                OpenTileWindow(pulled.Id, detachAt.Value.X - _dragGrab.X - tile.Margin.Left, detachAt.Value.Y - _dragGrab.Y - tile.Margin.Top, true);
            }
            else if (tabTarget != null && tile?.Tag is AppEntry moved) MoveTileToTab(moved, tabTarget);
            else if (!_dragDetachOnly) SaveTileOrder();
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
            while (minIndex < items.Count && (items[minIndex] as FrameworkElement)?.Tag is AppEntry a && a.IsBuiltIn) minIndex++;

            int to = from;
            double best = double.MaxValue;
            for (int i = minIndex; i < items.Count; i++)
            {
                var fe = items[i] as FrameworkElement;
                if (fe == null || !(fe.Tag is AppEntry)) continue;   // the «new rule» tile stays last
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
                    .Where(a => a != null && !a.IsBuiltIn).ToList();
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
                ApplyRuleOrder();   // the tile further left wins — effective now, not at the next reload
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
