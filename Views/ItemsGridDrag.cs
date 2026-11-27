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

namespace MihomoLauncher.Views
{
    // Drag-to-reorder for the items of an ItemsControl laid out in a grid (the rule editor's VPN
    // list, two per row — 2026-11-27: «добавь возможность перетаскивать местами впн как это сделано
    // в его отдельном окне»). The same feel as GridRowDrag: past a small threshold a picture of the
    // item follows the cursor, the item itself turns into a faint hole that travels through the
    // grid, the others slide aside (in X and Y). A press that never passes the threshold is an
    // ordinary click. A finished drag swallows its button-up, so the click handler doesn't fire.
    internal sealed class ItemsGridDrag<T> where T : class
    {
        private const double Threshold = 6;
        private const double HoleOpacity = 0.18;

        private readonly ItemsControl _ctl;
        private readonly ObservableCollection<T> _items;
        private readonly Action _onMoved;

        private T _pending;
        private Point _pressPos;     // in the control's coordinates
        private T _item;             // non-null while dragging
        private int _startIndex;
        private Vector _grab;        // cursor offset inside the item
        private RowGhost.Adorner _ghost;

        public ItemsGridDrag(ItemsControl ctl, ObservableCollection<T> items, Action onMoved)
        {
            _ctl = ctl; _items = items; _onMoved = onMoved;
            ctl.PreviewMouseLeftButtonDown += OnDown;
            ctl.PreviewMouseMove += OnMove;
            ctl.PreviewMouseLeftButtonUp += OnUp;
            ctl.LostMouseCapture += (s, e) => { if (_item != null && e.OriginalSource == _ctl) End(); };
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pending = null;
            if (_item != null) return;
            var c = ContainerOf(e.OriginalSource as DependencyObject);
            var item = c == null ? null : _ctl.ItemContainerGenerator.ItemFromContainer(c) as T;
            if (item == null) return;
            _pending = item;
            _pressPos = e.GetPosition(_ctl);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_item != null)
            {
                _ghost?.MoveTo(e.GetPosition(_ctl) - _grab);
                MoveHoleToward(e);
                e.Handled = true;
                return;
            }
            if (_pending == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _pending = null; return; }
            var p = e.GetPosition(_ctl);
            if (Math.Abs(p.Y - _pressPos.Y) < Threshold && Math.Abs(p.X - _pressPos.X) < Threshold) return;
            Begin(_pending);
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _pending = null;
            if (_item == null) return;
            e.Handled = true;   // no click (toggle) after a drag
            Mouse.Capture(null);
            if (_item != null) End();
        }

        private void Begin(T item)
        {
            _pending = null;
            var c = Container(item);
            if (c == null) return;
            _item = item;
            _startIndex = _items.IndexOf(item);
            var topLeft = c.TranslatePoint(new Point(0, 0), _ctl);
            _grab = _pressPos - topLeft;

            var layer = AdornerLayer.GetAdornerLayer(_ctl);
            if (layer != null)
            {
                _ghost = new RowGhost.Adorner(_ctl, RowGhost.Snapshot(c), c.ActualWidth, c.ActualHeight);
                layer.Add(_ghost);
                _ghost.MoveTo(topLeft);
            }
            c.Opacity = HoleOpacity;
            Mouse.Capture(_ctl, CaptureMode.Element);
        }

        private void End()
        {
            var item = _item;
            _item = null;
            if (_ghost != null)
            {
                AdornerLayer.GetAdornerLayer(_ctl)?.Remove(_ghost);
                _ghost = null;
            }
            foreach (var c in Containers()) c.Opacity = 1.0;
            if (item != null && _items.IndexOf(item) != _startIndex) _onMoved?.Invoke();
        }

        // The hole goes to the item whose centre is nearest the cursor; the rest slide (FLIP).
        private void MoveHoleToward(MouseEventArgs e)
        {
            int from = _items.IndexOf(_item);
            if (from < 0) return;
            int to = from;
            double best = double.MaxValue;
            foreach (var c in Containers())
            {
                int i = _items.IndexOf(_ctl.ItemContainerGenerator.ItemFromContainer(c) as T);
                if (i < 0) continue;
                var parent = VisualTreeHelper.GetParent(c) as UIElement;
                if (parent == null) continue;
                var r = LayoutInformation.GetLayoutSlot(c);
                var p = e.GetPosition(parent);
                double dx = p.X - (r.X + r.Width / 2), dy = p.Y - (r.Y + r.Height / 2);
                double d = dx * dx + dy * dy;
                if (d < best) { best = d; to = i; }
            }
            if (to == from) return;

            var before = new Dictionary<object, Point>();
            foreach (var c in Containers())
            {
                var tt = c.RenderTransform as TranslateTransform;
                var r = LayoutInformation.GetLayoutSlot(c);
                before[_ctl.ItemContainerGenerator.ItemFromContainer(c)] = new Point(r.X + (tt?.X ?? 0), r.Y + (tt?.Y ?? 0));
            }

            _items.Move(from, to);
            _ctl.UpdateLayout();
            if (Mouse.Captured != _ctl) Mouse.Capture(_ctl, CaptureMode.Element);

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var c in Containers())
            {
                var it = _ctl.ItemContainerGenerator.ItemFromContainer(c);
                if (it == _item) { c.Opacity = HoleOpacity; c.RenderTransform = null; continue; }
                c.Opacity = 1.0;
                if (!before.TryGetValue(it, out var old)) continue;
                var r = LayoutInformation.GetLayoutSlot(c);
                double ox = old.X - r.X, oy = old.Y - r.Y;
                if (Math.Abs(ox) < 0.5 && Math.Abs(oy) < 0.5) continue;
                var tt = c.RenderTransform as TranslateTransform;
                if (tt == null || tt.IsFrozen) { tt = new TranslateTransform(); c.RenderTransform = tt; }
                var dur = TimeSpan.FromMilliseconds(160);
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(ox, 0, dur) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(oy, 0, dur) { EasingFunction = ease });
            }
        }

        private FrameworkElement Container(T item) => _ctl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

        private IEnumerable<FrameworkElement> Containers()
        {
            for (int i = 0; i < _items.Count; i++)
                if (_ctl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c) yield return c;
        }

        private FrameworkElement ContainerOf(DependencyObject d)
        {
            if (d == null) return null;
            try { return _ctl.ContainerFromElement(d) as FrameworkElement; }
            catch { return null; }
        }
    }
}
