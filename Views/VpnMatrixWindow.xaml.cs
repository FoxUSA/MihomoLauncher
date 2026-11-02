using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
// WPF-UI ships its own TextBlock/Border names; this file builds the grid by hand and means
// the plain WPF ones throughout.
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // One table for the whole routing picture: every rule down the left, every VPN variant
    // across the top, and the cell says whether that rule may use that variant and in which
    // order it tries them.
    //
    // Before this the same information was spread over one dialog per rule, each with its own
    // ▲/▼ list, so "which rules go through Turkey" could only be answered by opening every rule
    // in turn. A matrix answers it by looking at a column.
    //
    // Priority is expressed by clicking rather than by dragging rows: the first variant clicked
    // in a row becomes 1, the next 2, and clicking a numbered cell clears it and closes the gap,
    // so the numbers in a row are always 1..N with nothing missing. A variant that was never
    // clicked does not take part in that rule's search at all — which is the same thing the old
    // checkbox meant, only now the tick and the rank are one control instead of two.
    public partial class VpnMatrixWindow : FluentWindow
    {
        // The edited copy. Handed back only on Save; the caller keeps its own config until then.
        public List<AppEntry> Result { get; private set; }

        private readonly List<AppEntry> _rules;      // rules shown, in config order
        private readonly List<VpnSlot> _slots;       // columns: variants switched on in Settings

        // rule id -> variant ids it uses, best first. Index + 1 is the number in the cell, so
        // removing one entry renumbers everything after it with no extra bookkeeping.
        private readonly Dictionary<string, List<string>> _order =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Variants switched OFF in Settings have no column here, so the user cannot say anything
        // about them. Whatever each rule already said is carried through untouched, or opening
        // this window would silently re-enable every variant the user had switched off globally.
        private readonly Dictionary<string, List<string>> _hiddenDisabled =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _hiddenRanked =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // rule id -> its row of cells, so a click can renumber the whole row in place.
        private readonly Dictionary<string, List<Cell>> _cells =
            new Dictionary<string, List<Cell>>(StringComparer.Ordinal);

        // rule id -> the kill-switch cell in the last column, and its state. It is a plain flag,
        // not part of the ranking, so it lives beside the row rather than inside it: nothing
        // renumbers when it changes. The cell is drawn exactly like a variant cell — same box,
        // same border, same colours — because side by side any difference reads as "this one is
        // a different kind of control", which it is not: both are one click, on or off.
        private readonly Dictionary<string, Cell> _kill =
            new Dictionary<string, Cell>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _killOn =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        // What each row looked like when the window opened, so Save can leave untouched rows
        // completely alone. It matters far beyond tidiness: the model above is NORMALISED (a
        // rule that never stated an order gets every shown variant filled in), so writing it
        // back unconditionally changed the stored routing of rules nobody had clicked — and the
        // caller, which decides what to disconnect by comparing exactly those fields, then
        // dropped the live connection of every rule in the table.
        private readonly Dictionary<string, string> _baseOrder =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _baseKill =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private sealed class Cell
        {
            public string RuleId;
            public string SlotId;
            public Border Box;
            public TextBlock Text;
        }

        public VpnMatrixWindow(AppConfig config)
        {
            InitializeComponent();
            Bar.Title = Title = Loc.T("Vpn_Title");

            var apps = (config?.Apps ?? new List<AppEntry>()).Where(a => a != null).ToList();
            // Only rules that asked for a VPN: for anything else there is nothing to assign, and
            // an empty row would just be noise in a table meant to be read at a glance.
            _rules = apps.Where(a => a.RequiresVpn).ToList();
            _slots = (config?.VpnSlots ?? new List<VpnSlot>())
                .Where(s => s != null && s.Enabled).ToList();

            foreach (var rule in _rules)
            {
                var shown = new HashSet<string>(_slots.Select(s => s.Id), StringComparer.Ordinal);
                var disabled = new HashSet<string>(rule.DisabledVpnSlotIds ?? new List<string>(),
                                                   StringComparer.Ordinal);
                var ranked = rule.VpnSlotOrder ?? new List<string>();

                // What this rule actually uses: allowed variants, in its own order, with any
                // allowed variant it never ranked (one added later) falling in behind them.
                var used = ranked.Where(id => shown.Contains(id) && !disabled.Contains(id)).ToList();
                foreach (var s in _slots)
                    if (!disabled.Contains(s.Id) && !used.Contains(s.Id)) used.Add(s.Id);

                _order[rule.Id] = used;
                _baseOrder[rule.Id] = string.Join(",", used);
                _baseKill[rule.Id] = rule.KillSwitch;
                _hiddenDisabled[rule.Id] = disabled.Where(id => !shown.Contains(id)).ToList();
                _hiddenRanked[rule.Id] = ranked.Where(id => !shown.Contains(id)).ToList();
            }

            BuildMatrix();
            Loaded += (s, e) => FocusManager.SetFocusedElement(this, this);
        }

        // ---------- the table ----------

        private void BuildMatrix()
        {
            if (_rules.Count == 0 || _slots.Count == 0)
            {
                Scroller.Visibility = Visibility.Collapsed;
                LblEmpty.Visibility = Visibility.Visible;
                LblEmpty.Text = Loc.T(_slots.Count == 0 ? "Vpn_NoVariants" : "Vpn_NoRules");
                BtnSave.IsEnabled = false;
                return;
            }

            Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var _ in _slots)
                Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // kill-switch

            Matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            for (int i = 0; i < _rules.Count; i++)
                Matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header: the variant names, and nothing else. The rule column needs no caption —
            // what is in it is obvious, and a heading there would only compete with the names.
            for (int c = 0; c < _slots.Count; c++)
            {
                var name = string.IsNullOrWhiteSpace(_slots[c].Name) ? Loc.T("Rule_NoName") : _slots[c].Name;
                var head = new TextBlock
                {
                    Text = name,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 120,
                    Margin = new Thickness(6, 0, 6, 8),
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                Grid.SetRow(head, 0);
                Grid.SetColumn(head, c + 1);
                Matrix.Children.Add(head);
            }

            // Last column, set apart from the variants by a wider margin: it says nothing about
            // which VPN the rule uses, it says what happens when none of them is up.
            var killHead = new TextBlock
            {
                Text = Loc.T("Vpn_Kill"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 120,
                Margin = new Thickness(24, 0, 6, 8),
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = Loc.T("Vpn_KillTip"),
            };
            Grid.SetRow(killHead, 0);
            Grid.SetColumn(killHead, _slots.Count + 1);
            Matrix.Children.Add(killHead);

            for (int r = 0; r < _rules.Count; r++)
            {
                var rule = _rules[r];

                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(rule.Name) ? Loc.T("Rule_NoName") : rule.Name,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260,
                    Margin = new Thickness(0, 0, 18, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetRow(label, r + 1);
                Grid.SetColumn(label, 0);
                Matrix.Children.Add(label);

                var row = new List<Cell>();
                for (int c = 0; c < _slots.Count; c++)
                {
                    var cell = MakeCell(rule.Id, _slots[c].Id);
                    Grid.SetRow(cell.Box, r + 1);
                    Grid.SetColumn(cell.Box, c + 1);
                    Matrix.Children.Add(cell.Box);
                    row.Add(cell);
                }
                _cells[rule.Id] = row;

                var kill = MakeCell(rule.Id, null);
                kill.Box.Margin = new Thickness(24, 3, 6, 3);   // the gap that sets the column apart
                Grid.SetRow(kill.Box, r + 1);
                Grid.SetColumn(kill.Box, _slots.Count + 1);
                Matrix.Children.Add(kill.Box);
                _kill[rule.Id] = kill;
                _killOn[rule.Id] = rule.KillSwitch;

                Paint(rule.Id);
            }
        }

        private Cell MakeCell(string ruleId, string slotId)
        {
            var text = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(6, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = text,
            };
            var cell = new Cell { RuleId = ruleId, SlotId = slotId, Box = box, Text = text };
            box.MouseLeftButtonUp += (s, e) => { e.Handled = true; Toggle(cell); };
            return cell;
        }

        // A click either adds this variant to the end of the rule's order (so it takes the next
        // free number) or removes it. Removing shifts everything behind it up by one on its own,
        // because the number shown is just the position in the list.
        private void Toggle(Cell cell)
        {
            // The kill-switch cell has no variant behind it: it is the row's own flag.
            if (cell.SlotId == null)
            {
                bool on;
                _killOn.TryGetValue(cell.RuleId, out on);
                _killOn[cell.RuleId] = !on;
                Paint(cell.RuleId);
                return;
            }

            List<string> order;
            if (!_order.TryGetValue(cell.RuleId, out order)) return;
            if (!order.Remove(cell.SlotId)) order.Add(cell.SlotId);
            Paint(cell.RuleId);
        }

        private void Paint(string ruleId)
        {
            Cell killCell;
            if (_kill.TryGetValue(ruleId, out killCell))
            {
                bool killOn;
                _killOn.TryGetValue(ruleId, out killOn);
                PaintBox(killCell, killOn, killOn ? "✓" : "", kill: true);
                killCell.Box.ToolTip = Loc.T("Vpn_KillTip");
            }

            List<Cell> row;
            List<string> order;
            if (!_cells.TryGetValue(ruleId, out row) || !_order.TryGetValue(ruleId, out order)) return;

            foreach (var cell in row)
            {
                int rank = order.IndexOf(cell.SlotId) + 1;      // 0 when not in the list
                bool on = rank > 0;
                PaintBox(cell, on, on ? rank.ToString() : "");
                cell.Box.ToolTip = Loc.T(on ? "Vpn_CellOnTip" : "Vpn_CellOffTip");
            }
        }

        // The one place a cell's look is decided, so the kill-switch column cannot drift away
        // from the variant columns: same box, same border. On = green for a variant, dim orange
        // for the kill-switch (it blocks, it doesn't connect).
        private static void PaintBox(Cell cell, bool on, string text, bool kill = false)
        {
            cell.Text.Text = text;
            cell.Box.Background = !on ? (Brush)System.Windows.Media.Brushes.Transparent
                : new SolidColorBrush(kill ? Color.FromRgb(0x6B, 0x4A, 0x2C) : Color.FromRgb(0x3C, 0x61, 0x49));
            cell.Box.BorderBrush = new SolidColorBrush(!on ? Color.FromRgb(0x55, 0x5F, 0x77)
                : kill ? Color.FromRgb(0x9A, 0x6E, 0x42) : Color.FromRgb(0x58, 0x8A, 0x69));
            cell.Text.Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF));
        }

        // ---------- save ----------

        private void OnSave(object sender, RoutedEventArgs e)
        {
            foreach (var rule in _rules)
            {
                List<string> order;
                if (!_order.TryGetValue(rule.Id, out order)) continue;

                bool killOn;
                _killOn.TryGetValue(rule.Id, out killOn);

                // Untouched row → write nothing at all. Not an optimisation: see _baseOrder.
                string baseOrder;
                bool baseKill;
                if (_baseOrder.TryGetValue(rule.Id, out baseOrder)
                    && _baseKill.TryGetValue(rule.Id, out baseKill)
                    && killOn == baseKill
                    && string.Join(",", order) == baseOrder) continue;

                rule.KillSwitch = killOn;

                // Clicked variants carry the order. Everything shown but not clicked is excluded,
                // which is what "не кликнули — не участвует" means; the variants with no column
                // keep whatever this rule said about them before.
                var shown = _slots.Select(s => s.Id).ToList();
                rule.VpnSlotOrder = order.Concat(_hiddenRanked[rule.Id]).ToList();
                rule.DisabledVpnSlotIds = shown.Where(id => !order.Contains(id))
                                               .Concat(_hiddenDisabled[rule.Id]).ToList();
            }
            Result = _rules;
            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }
    }
}
