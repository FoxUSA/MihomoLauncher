using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The rule editor's part of auto-tracking and folders (2026-11-22):
    //  · the list has two sections — what the person added, then (only when there is any) a thin
    //    line «Добавлено автоматически» and what auto-tracking added (grouping by ItemRow.Section);
    //  · ✕ on an auto row moves it to a third section «Не добавлять автоматически» — a ban for
    //    its exact file (AppEntry.AutoIgnored); ✕ there lifts the ban;
    //  · an auto row dragged up into the first section becomes a manual entry;
    //  · «Папка» adds a whole folder as one row.
    public partial class EditRuleDialog
    {
        private void SetupAutoRows()
        {
            try
            {
                var view = CollectionViewSource.GetDefaultView(_items);
                if (view != null && view.CanGroup && view.GroupDescriptions.Count == 0)
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemRow.Section)));
            }
            catch (Exception ex) { Log.Error("rule-editor-groups", ex); }

            ItemsGrid.PreviewMouseLeftButtonDown += OnGridDown;
            ItemsGrid.PreviewMouseMove += OnGridMove;
            ItemsGrid.PreviewMouseLeftButtonUp += OnGridUp;
            ItemsGrid.LostMouseCapture += (s, e) => { if (_dragItem != null && e.OriginalSource == ItemsGrid) EndDrag(null); };
        }

        private static string ExeNameOf(string path)
        {
            try { return Path.GetFileName((path ?? "").Trim()) ?? ""; } catch { return ""; }
        }

        // ✕ on an auto row: it stops being an entry of the rule and becomes a ban for its exact file,
        // shown at once in the third section «Не добавлять автоматически» (saved into
        // AppEntry.AutoIgnored; a cancel discards it like any other edit).
        private void IgnoreAuto(ItemRow row)
        {
            if (row == null) return;
            bool hadTile = row.Launch;
            _items.Remove(row);
            var key = !string.IsNullOrWhiteSpace(row.Path) ? row.Path.Trim() : ExeNameOf(row.Path);
            if (key.Length == 0) key = (row.Name ?? "").Trim();
            if (key.Length > 0)
            {
                var probe = new AppEntry { AutoIgnored = _items.Where(i => i.IsIgnored).Select(i => i.BanKey).ToList() };
                bool already = key.IndexOf('\\') >= 0 ? AutoPrograms.IsBanned(probe, key)
                             : probe.AutoIgnored.Any(b => string.Equals(b, key, StringComparison.OrdinalIgnoreCase));
                if (!already) _items.Add(ItemRow.FromBan(key));   // the last section: always at the end
            }
            if (hadTile && !_items.Any(i => i.Launch)) GiveTileToBest();
        }

        // An auto row becomes a manual one, at `index` among the manual rows. Taken out and put back:
        // the grid regroups an item only when the collection changes.
        private void MakeManual(ItemRow row, int index)
        {
            if (row == null || !row.IsAuto) return;
            _items.Remove(row);
            row.IsAuto = false;
            row.AutoFrom = "";
            int manual = _items.Count(i => !i.IsAuto && !i.IsIgnored);
            int at = Math.Max(0, Math.Min(index, manual));
            _items.Insert(at, row);
            row.Owner = this;
            ItemsGrid.UpdateLayout();
            ItemsGrid.ScrollIntoView(row);
            ItemsGrid.SelectedItem = row;
        }

        // ---------- «Папка» ----------

        private async void OnAddFolder(object sender, RoutedEventArgs e)
        {
            var picked = FolderPicker.Pick(this, Loc.T("Rule_PickFolderTitle"));
            if (string.IsNullOrWhiteSpace(picked)) return;
            var path = MihomoConfigBuilder.NormalizeFolder(picked);
            if (path.Length == 0) return;

            // A drive root or Windows itself would take every program of the machine.
            string root = "", win = "";
            try { root = (Path.GetPathRoot(path) ?? "").TrimEnd('\\'); } catch { }
            try { win = MihomoConfigBuilder.NormalizeFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); } catch { }
            bool tooWide = string.Equals(path.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)
                        || (win.Length > 0 && (string.Equals(path, win, StringComparison.OrdinalIgnoreCase)
                                               || path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)));
            if (tooWide)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Rule_FolderTooWideTitle"), Loc.T("Rule_FolderTooWide", path)).ConfigureAwait(true);
                return;
            }

            var dup = _items.FirstOrDefault(i => i.IsFolder && string.Equals(MihomoConfigBuilder.NormalizeFolder(i.Path), path, StringComparison.OrdinalIgnoreCase));
            if (dup != null)
            {
                ItemsGrid.ScrollIntoView(dup);
                ItemsGrid.SelectedItem = dup;
                await OverlayDialog.MessageAsync(this, Loc.T("Rule_DupTitle"), Loc.T("Rule_DupFolder", path)).ConfigureAwait(true);
                return;
            }

            string name;
            try { name = Path.GetFileName(path); } catch { name = path; }
            if (string.IsNullOrEmpty(name)) name = path;
            AddRow(new ItemRow { IsSite = false, IsFolder = true, Name = name, Path = path });
            if (string.IsNullOrWhiteSpace(TxtName.Text)) TxtName.Text = name;
        }

        // ---------- dragging an auto row into the manual section ----------

        private const double DragThreshold = 6;
        private ItemRow _dragPending, _dragItem, _openOnUp;
        private Point _dragPress;
        private double _dragGrabY, _dragRowX;
        private RowGhost.Adorner _dragGhost;
        private DataGridRow _dragRow;

        private void OnGridDown(object sender, MouseButtonEventArgs e)
        {
            _dragPending = null;
            _openOnUp = null;
            if (_dragItem != null) return;
            var src = e.OriginalSource as DependencyObject;
            // Its ✕ and its controls react on the press — a drag can't start from there.
            if (Ancestor<ButtonBase>(src) != null || Ancestor<TextBoxBase>(src) != null) return;
            var row = Ancestor<DataGridRow>(src);
            var item = row?.Item as ItemRow;
            if (item == null || !item.IsAuto) return;
            _dragPending = item;
            _dragPress = e.GetPosition(ItemsGrid);
        }

        private void OnGridMove(object sender, MouseEventArgs e)
        {
            if (_dragItem != null)
            {
                var p = e.GetPosition(ItemsGrid);
                _dragGhost?.MoveTo(new Point(_dragRowX, p.Y - _dragGrabY));
                e.Handled = true;
                return;
            }
            if (_dragPending == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragPending = null; return; }
            var pos = e.GetPosition(ItemsGrid);
            if (Math.Abs(pos.Y - _dragPress.Y) < DragThreshold && Math.Abs(pos.X - _dragPress.X) < DragThreshold) return;
            BeginDrag(_dragPending);
            e.Handled = true;
        }

        private void OnGridUp(object sender, MouseButtonEventArgs e)
        {
            _dragPending = null;
            if (_dragItem == null)
            {
                // A click on an auto row's name / path (no drag): open it, as for any row.
                var open = _openOnUp;
                _openOnUp = null;
                if (open != null && (Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item == open)) OpenRow(open);
                return;
            }
            e.Handled = true;
            var at = e.GetPosition(ItemsGrid);
            EndDrag(at);
        }

        private void BeginDrag(ItemRow item)
        {
            _dragPending = null;
            _openOnUp = null;
            var row = RowsOf().FirstOrDefault(r => r.Item == item);
            if (row == null) return;
            _dragItem = item;
            _dragRow = row;
            var topLeft = row.TranslatePoint(new Point(0, 0), ItemsGrid);
            _dragGrabY = _dragPress.Y - topLeft.Y;
            _dragRowX = topLeft.X;
            try
            {
                var layer = AdornerLayer.GetAdornerLayer(ItemsGrid);
                if (layer != null)
                {
                    _dragGhost = new RowGhost.Adorner(ItemsGrid, RowGhost.Snapshot(row), row.ActualWidth, row.ActualHeight);
                    layer.Add(_dragGhost);
                    _dragGhost.MoveTo(new Point(_dragRowX, _dragPress.Y - _dragGrabY));
                }
            }
            catch (Exception ex) { Log.Error("rule-editor-drag", ex); }
            row.Opacity = 0.3;
            Mouse.Capture(ItemsGrid, CaptureMode.Element);
        }

        // `drop` = where the button was released (grid coordinates); null = the drag was lost.
        private void EndDrag(Point? drop)
        {
            var item = _dragItem;
            _dragItem = null;
            if (_dragGhost != null)
            {
                try { AdornerLayer.GetAdornerLayer(ItemsGrid)?.Remove(_dragGhost); } catch { }
                _dragGhost = null;
            }
            if (_dragRow != null) _dragRow.Opacity = 1.0;
            _dragRow = null;
            if (Mouse.Captured == ItemsGrid) Mouse.Capture(null);
            if (item == null || drop == null) return;

            // Above the «Добавлено автоматически» line = into the manual section, at the place
            // between the manual rows the cursor is at.
            double line = AutoSectionTop();
            if (double.IsNaN(line) || drop.Value.Y >= line) return;
            int index = RowsOf()
                .Where(r => r.Item is ItemRow ir && !ir.IsAuto)
                .Count(r => r.TranslatePoint(new Point(0, r.ActualHeight / 2), ItemsGrid).Y < drop.Value.Y);
            MakeManual(item, index);
        }

        // The top of the auto section's header line, in grid coordinates; NaN when not shown.
        private double AutoSectionTop()
        {
            foreach (var g in Descendants<GroupItem>(ItemsGrid))
            {
                var grp = g.DataContext as CollectionViewGroup;
                if (grp != null && "auto".Equals(grp.Name as string, StringComparison.Ordinal) && g.IsVisible)
                    return g.TranslatePoint(new Point(0, 0), ItemsGrid).Y;
            }
            return double.NaN;
        }

        private IEnumerable<DataGridRow> RowsOf() => Descendants<DataGridRow>(ItemsGrid);

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is T hit) yield return hit;
                    stack.Push(c);
                }
            }
        }

        private T Ancestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && d != ItemsGrid)
            {
                if (d is T hit) return hit;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
    }
}
