using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        private readonly AppConfig _cfg;             // for the tabs the rows are split by
        private readonly List<VpnSlot> _slots;       // columns: variants switched on in Settings
        private readonly List<VpnSlot> _slotsAll;    // every variant, for HasTickedVpn

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

        // «Каскад» (2026-11-27): the column before the kill-switch, the row's AppEntry.Cascade. On, the
        // numbers in the row are the hops of one chain (blue, at most three), not a priority.
        private const string CascadeCell = "\u0001cascade";
        private readonly Dictionary<string, Cell> _casc =
            new Dictionary<string, Cell>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _cascOn =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _baseCasc =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        // The cascade's own hops per rule (AppEntry.CascadeHops) — a memory apart from _order: the
        // «Каскад» cell flips which of the two the row shows and edits (2026-11-27).
        private readonly Dictionary<string, List<string>> _cascOrder =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _baseCascOrder =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // What the row shows and edits now: its hops when «Каскад» is on, else its ordinary order.
        private List<string> RowOrder(string ruleId)
        {
            bool c;
            List<string> o;
            if (_cascOn.TryGetValue(ruleId, out c) && c)
            {
                if (!_cascOrder.TryGetValue(ruleId, out o)) _cascOrder[ruleId] = o = new List<string>();
                return o;
            }
            return _order.TryGetValue(ruleId, out o) ? o : null;
        }

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
            // Every tile, «без VPN»/«Напрямую» ones too (2026-11-16: «роутинг должен показывать все
            // плитки») — their row starts empty; a number clicked there makes it a VPN tile.
            _rules = apps;
            _cfg = config;
            _slotsAll = config?.VpnSlots;
            _slots = (config?.VpnSlots ?? new List<VpnSlot>())
                .Where(s => s != null).ToList();

            foreach (var rule in _rules)
            {
                var shown = new HashSet<string>(_slots.Select(s => s.Id), StringComparer.Ordinal);
                var disabled = new HashSet<string>(rule.DisabledVpnSlotIds ?? new List<string>(),
                                                   StringComparer.Ordinal);
                var ranked = rule.VpnSlotOrder ?? new List<string>();

                // What this rule actually uses: allowed variants, in its own order, with any
                // allowed variant it never ranked (one added later) falling in behind them.
                // A direct tile with nothing ticked uses no VPN at all: an empty row, not «all of them».
                bool direct = !rule.RequiresVpn && !AppConfigStore.HasTickedVpn(rule, _slotsAll);
                var used = direct ? new List<string>()
                    : ranked.Where(id => shown.Contains(id) && !disabled.Contains(id)).ToList();
                if (!direct)
                    foreach (var s in _slots)
                        if (!disabled.Contains(s.Id) && !used.Contains(s.Id)) used.Add(s.Id);

                _order[rule.Id] = used;
                _baseOrder[rule.Id] = string.Join(",", used);
                _baseKill[rule.Id] = rule.KillSwitch;
                _hiddenDisabled[rule.Id] = disabled.Where(id => !shown.Contains(id)).ToList();
                _hiddenRanked[rule.Id] = ranked.Where(id => !shown.Contains(id)).ToList();
            }

            BuildMatrix();
            // «Сохранить» only when a rank or a kill-switch differs from what the window opened with.
            if (BtnSave.IsEnabled)
                SaveGuard.Attach(this, BtnSave, () =>
                    string.Join(";", _rules.Select(r =>
                    {
                        List<string> o, h; bool k, c;
                        _order.TryGetValue(r.Id, out o);
                        _cascOrder.TryGetValue(r.Id, out h);
                        _killOn.TryGetValue(r.Id, out k);
                        _cascOn.TryGetValue(r.Id, out c);
                        return r.Id + "=" + string.Join(",", o ?? new List<string>()) + (k ? "+k" : "") + (c ? "+c" : "")
                               + "/" + string.Join(",", h ?? new List<string>());
                    })));
            Loaded += (s, e) =>
            {
                FocusManager.SetFocusedElement(this, this);
                FitToTable();
            };
        }

        // The window is as big as the table, not a fixed 900×600 with the table in a corner and
        // the rest empty (2026-11-10: «тут много лишнего пространства»). Two passes: the width
        // first (the hint above re-wraps to it), then the height of whatever that produced.
        private void FitToTable()
        {
            try
            {
                if (Scroller.Visibility != Visibility.Visible) return;
                const double frame = 4;    // the 1-px window border + rounding; 20 left all the slack on the right and under the table (2026-11-27)
                double w = Math.Max(Well.DesiredSize.Width + Scroller.Margin.Left + Scroller.Margin.Right + frame, 460);
                SizeToContent = SizeToContent.Manual;
                MinWidth = Math.Min(MinWidth, w);
                Width = w;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        double h = Bar.ActualHeight
                                 + Hint.ActualHeight + Hint.Margin.Top + Hint.Margin.Bottom
                                 + Well.DesiredSize.Height + 8
                                 + Footer.ActualHeight + frame;
                        MinHeight = Math.Min(MinHeight, h);
                        Height = h;

                        // Re-centre on the owner: it was centred at the old 900×600.
                        if (Owner != null && Owner.WindowState == WindowState.Normal)
                        {
                            Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                            Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
                        }
                        UiHelpers.KeepOnScreen(this);   // a long rule list still has to fit the screen
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
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

            // A table like the rules list (2026-11-27: «сделай тоже таблицей такой же как апп»): a header
            // row, hairlines between cells (TableLine, right + bottom, as AppDataGridCellStyle), rows of
            // the same height (26, AppDataGridStyle.RowHeight), alternating and lit under the mouse, in a
            // well (VpnMatrixWindow.xaml). Split by the main window's tabs with the same captioned line
            // as the rules list. The cascade / kill-switch columns are set apart by a 2-px line.
            int cols = _slots.Count + 3;
            Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var _ in _slots)
                Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // cascade
            Matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // kill-switch

            int rowAt = 0;
            Matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header

            // Header: the variant names, and nothing over the rule column — what is in it is obvious.
            AddCell(new TextBlock(), 0, 0, true, false, null);
            for (int c = 0; c < _slots.Count; c++)
            {
                var name = string.IsNullOrWhiteSpace(_slots[c].Name) ? Loc.T("Rule_NoName") : _slots[c].Name;
                AddCell(HeaderText(name, null), 0, c + 1, true, true, null);
            }
            AddCell(HeaderText(Loc.T("Vpn_CascadeCol"), Loc.T("Vpn_CascadeTip")), 0, _slots.Count + 1, true, true, null);
            AddCell(HeaderText(Loc.T("Vpn_Kill"), Loc.T("Vpn_KillTip")), 0, _slots.Count + 2, true, true, null);

            var tabs = _cfg?.Tabs;
            bool split = tabs != null && tabs.Count > 1;
            int lastTab = -1, stripe = 0;
            for (int r = 0; r < _rules.Count; r++)
            {
                var rule = _rules[r];

                if (split)
                {
                    int t = TileTabs.IndexOf(_cfg, rule);
                    if (t != lastTab)
                    {
                        lastTab = t;
                        Matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        rowAt++;
                        var line = TabLine(TileTabs.NameOf(tabs[t]));
                        Grid.SetRow(line, rowAt);
                        Grid.SetColumnSpan(line, cols);
                        Matrix.Children.Add(line);
                        stripe = 0;
                    }
                }

                Matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                rowAt++;

                // The row's background behind its cells: every other row TableRowAlt, TableRowHover
                // under the mouse — the rules list's rows.
                var normal = stripe++ % 2 == 1 ? (Brush)Theme.TableRowAlt : Brushes.Transparent;
                var back = new Border { Background = normal };
                Grid.SetRow(back, rowAt);
                Grid.SetColumnSpan(back, cols);
                Panel.SetZIndex(back, -1);
                Matrix.Children.Add(back);
                var rowCells = new List<Border>();

                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(rule.Name) ? Loc.T("Rule_NoName") : rule.Name,
                    FontSize = 12,
                    Foreground = Theme.TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                rowCells.Add(AddCell(label, rowAt, 0, false, false, null));

                var row = new List<Cell>();
                for (int c = 0; c < _slots.Count; c++)
                {
                    var cell = MakeCell(rule.Id, _slots[c].Id);
                    rowCells.Add(AddCell(cell.Box, rowAt, c + 1, false, true, null));
                    row.Add(cell);
                }
                _cells[rule.Id] = row;

                var casc = MakeCell(rule.Id, CascadeCell);
                rowCells.Add(AddCell(casc.Box, rowAt, _slots.Count + 1, false, true, null));
                _casc[rule.Id] = casc;
                _cascOn[rule.Id] = rule.Cascade;
                _baseCasc[rule.Id] = rule.Cascade;
                var hops = (rule.CascadeHops ?? new List<string>())
                    .Where(id => _slots.Any(s => s.Id == id)).Distinct().Take(Services.Cascade.MaxHops).ToList();
                _cascOrder[rule.Id] = hops;
                _baseCascOrder[rule.Id] = string.Join(",", hops);

                var kill = MakeCell(rule.Id, null);
                rowCells.Add(AddCell(kill.Box, rowAt, _slots.Count + 2, false, true, null));
                _kill[rule.Id] = kill;
                _killOn[rule.Id] = rule.KillSwitch;

                foreach (var rc in rowCells)
                {
                    rc.MouseEnter += (s, e) => back.Background = Theme.TableRowHover;
                    rc.MouseLeave += (s, e) => back.Background = normal;
                }

                Paint(rule.Id);
            }
        }

        // One cell of the table: hairlines right and bottom (the header: top too), 8 px inside,
        // 26 high (the header 34) — the rules list's cell and header. The last variant column gets a
        // 2-px right line: the two flag columns after it say nothing about which VPN.
        private Border AddCell(UIElement child, int row, int col, bool header, bool center, string tip)
        {
            double right = col == _slots.Count ? 2 : 1;
            var b = new Border
            {
                BorderBrush = Theme.TableLine,
                BorderThickness = header ? new Thickness(0, 1, right, 1) : new Thickness(0, 0, right, 1),
                Background = header ? (Brush)Theme.TableHeaderBg : Brushes.Transparent,
                Padding = new Thickness(8, 0, 8, 0),
                MinHeight = header ? 34 : 26,
                Child = child,
                SnapsToDevicePixels = true,
            };
            if (child is FrameworkElement fe)
            {
                fe.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            if (tip != null) b.ToolTip = tip;
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Matrix.Children.Add(b);
            return b;
        }

        private TextBlock HeaderText(string text, string tip)
        {
            var t = new TextBlock
            {
                Text = SmartWrap(text ?? "", 13, FontWeights.SemiBold),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimary,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 4, 0, 4),
            };
            t.ToolTip = tip ?? (t.Text.Contains("\n") ? text : null);
            return t;
        }

        // ---------- the header's smart line breaks ----------

        // A VPN name breaks where its own seams are — after «_», «-», «.», «/», «+», a space, and
        // between a small and a capital letter — at the places that make its longest line the
        // shortest, so the column is as narrow as it can be (2026-11-27: «умный перенос названий
        // впн … чтобы столбик стал максимально уже»). The seam a line breaks at is dropped
        // («fox» / «chrome», «Kill» / «switch» — «если перенес то прочерк не надо оставлять»);
        // seams inside a line stay. At most two lines («максимум в две строчки»); the second has
        // to win at least WrapLineGain DIP; nothing is gained below the square.
        private const int WrapMaxLines = 2;
        private static readonly char[] WrapSeams = { '_', '-', '.', '/', '+', ' ' };
        private const double WrapMinWidth = 20;   // the cell's square: the column is never narrower
        private const double WrapLineGain = 4;

        private string SmartWrap(string text, double size, FontWeight weight)
        {
            var parts = WrapParts(text);
            int n = parts.Count;
            if (n < 2) return text;

            double dip = 1;
            try { dip = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            var tf = new Typeface(FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
            var w = new double[n + 1, n + 1];   // w[i, j]: parts i..j-1 on one line
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j <= n; j++)
                {
                    var line = WrapLine(parts, i, j, n);
                    w[i, j] = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                tf, size, Brushes.Black, dip).Width;
                }

            string best = text;
            double bestW = Math.Max(w[0, n], WrapMinWidth);
            for (int lines = 2; lines <= Math.Min(WrapMaxLines, n); lines++)
            {
                // dp[k, j]: the narrowest widest line putting parts 0..j-1 on k lines.
                var dp = new double[lines + 1, n + 1];
                var cut = new int[lines + 1, n + 1];
                for (int k = 0; k <= lines; k++)
                    for (int j = 0; j <= n; j++) dp[k, j] = double.MaxValue;
                dp[0, 0] = 0;
                for (int k = 1; k <= lines; k++)
                    for (int j = k; j <= n; j++)
                        for (int i = k - 1; i < j; i++)
                        {
                            if (dp[k - 1, i] == double.MaxValue) continue;
                            double v = Math.Max(dp[k - 1, i], w[i, j]);
                            if (v < dp[k, j]) { dp[k, j] = v; cut[k, j] = i; }
                        }
                double got = Math.Max(dp[lines, n], WrapMinWidth);
                if (got > bestW - WrapLineGain) continue;
                var rows = new List<string>();
                for (int k = lines, j = n; k > 0; k--)
                {
                    int i = cut[k, j];
                    rows.Insert(0, WrapLine(parts, i, j, n));
                    j = i;
                }
                best = string.Join("\n", rows);
                bestW = got;
            }
            return best;
        }

        // Parts i..j-1 as one line; a line that ends at a break (not the name's end) loses its seam.
        private static string WrapLine(List<string> parts, int i, int j, int n)
        {
            var line = string.Concat(parts.Skip(i).Take(j - i));
            return j < n ? line.TrimEnd(WrapSeams) : line.TrimEnd(' ');
        }

        private static List<string> WrapParts(string text)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (sb.Length > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]))
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                }
                sb.Append(c);
                if ("_-./+ ".IndexOf(c) >= 0 && i < text.Length - 1)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        // A tab's captioned line across the table — the rules list's (ListsWindow RulesRowStyle) and
        // the rule editor's «Добавлено автоматически»: line — caption — line in the accent blue,
        // a hairline under it.
        private static Border TabLine(string caption)
        {
            var g = new Grid { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l1 = new Border { Height = 1, Background = Theme.AccentBlue, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
            var cap = new TextBlock
            {
                Text = caption ?? "", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.AccentBlue,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0),
            };
            var l2 = new Border { Height = 1, Background = Theme.AccentBlue, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(cap, 1);
            Grid.SetColumn(l2, 2);
            g.Children.Add(l1); g.Children.Add(cap); g.Children.Add(l2);
            return new Border
            {
                Height = 26,
                BorderBrush = Theme.TableLine,
                BorderThickness = new Thickness(0, 0, 0, 1),
                SnapsToDevicePixels = true,
                Child = g,
            };
        }

        private Cell MakeCell(string ruleId, string slotId)
        {
            var text = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new Border
            {
                // 20, as the rule editor's squares: the row stays 26, the rules list's (2026-11-27).
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
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

            bool cascade;
            _cascOn.TryGetValue(cell.RuleId, out cascade);

            if (cell.SlotId == CascadeCell)
            {
                if (_slots.Count < 2) return;   // nothing to chain with fewer than two VPNs
                // The other memory is shown; neither list changes (2026-11-27).
                _cascOn[cell.RuleId] = !cascade;
                Paint(cell.RuleId);
                return;
            }

            var order = RowOrder(cell.RuleId);
            if (order == null) return;
            if (!order.Remove(cell.SlotId))
            {
                if (cascade && order.Count >= Services.Cascade.MaxHops) return;   // three hops already
                order.Add(cell.SlotId);
            }
            Paint(cell.RuleId);
        }

        private enum CellKind { Rank, Hop, Kill, Cascade }

        private void Paint(string ruleId)
        {
            Cell killCell;
            if (_kill.TryGetValue(ruleId, out killCell))
            {
                bool killOn;
                _killOn.TryGetValue(ruleId, out killOn);
                PaintBox(killCell, killOn, killOn ? "✓" : "", CellKind.Kill);
                killCell.Box.ToolTip = Loc.T("Vpn_KillTip");
            }

            bool cascade;
            _cascOn.TryGetValue(ruleId, out cascade);
            Cell cascCell;
            if (_casc.TryGetValue(ruleId, out cascCell))
            {
                PaintBox(cascCell, cascade, cascade ? "✓" : "", CellKind.Cascade);
                // Fewer than two VPNs: nothing to chain — the cell is greyed and says why.
                bool can = _slots.Count >= 2;
                cascCell.Box.Opacity = can ? 1.0 : 0.35;
                cascCell.Box.Cursor = can ? Cursors.Hand : Cursors.Arrow;
                cascCell.Box.ToolTip = Loc.T(can ? "Vpn_CascadeTip" : "Edit_CascadeNeedTwo");
            }

            List<Cell> row;
            var order = RowOrder(ruleId);
            if (!_cells.TryGetValue(ruleId, out row) || order == null) return;

            bool full = cascade && order.Count >= Services.Cascade.MaxHops;
            foreach (var cell in row)
            {
                int rank = order.IndexOf(cell.SlotId) + 1;      // 0 when not in the list
                bool on = rank > 0;
                PaintBox(cell, on, on ? rank.ToString() : "", cascade ? CellKind.Hop : CellKind.Rank);
                cell.Box.Opacity = full && !on ? 0.35 : 1.0;
                cell.Box.ToolTip = cascade ? Loc.T("Vpn_CascadeTip") : Loc.T(on ? "Vpn_CellOnTip" : "Vpn_CellOffTip");
            }
        }

        // The one place a cell's look is decided, so the kill-switch column cannot drift away
        // from the variant columns: same box, same border. On = green for a variant (in use = ok),
        // violet for the kill-switch — protection on, the colour the tile's kill-switch chip and
        // the rule editor's switch use (2026-11-22; it was a brown nothing else used). A cascade
        // (its column, and the hops of its row) is the accent blue — the editor's colour for it.
        private static void PaintBox(Cell cell, bool on, string text, CellKind kind)
        {
            cell.Text.Text = text;
            bool blue = kind == CellKind.Hop || kind == CellKind.Cascade;
            cell.Box.Background = !on ? (Brush)System.Windows.Media.Brushes.Transparent
                : kind == CellKind.Kill ? Theme.GuardCellBg : blue ? Theme.AccentSoft : Theme.RankOnBg;
            cell.Box.BorderBrush = !on ? Theme.RankOffBorder
                : kind == CellKind.Kill ? Theme.GuardBorder : blue ? Theme.Accent : Theme.RankOnBorder;
            cell.Text.Foreground = blue ? Theme.TextPrimary : Theme.TextOnAction;
        }

        // ---------- save ----------

        // Raised as Save starts writing the rules: the main window takes its «before» picture of the
        // routing then — not when the window opened, since it no longer blocks the main one and the
        // routing may have moved meanwhile (2026-11-27).
        public event Action BeforeApply;

        private void OnSave(object sender, RoutedEventArgs e)
        {
            try { BeforeApply?.Invoke(); } catch (Exception ex) { Log.Error("vpn-matrix-before", ex); }
            foreach (var rule in _rules)
            {
                List<string> order;
                if (!_order.TryGetValue(rule.Id, out order)) continue;

                bool killOn, cascOn;
                _killOn.TryGetValue(rule.Id, out killOn);
                _cascOn.TryGetValue(rule.Id, out cascOn);

                // Untouched row → write nothing at all. Not an optimisation: see _baseOrder.
                List<string> hops;
                if (!_cascOrder.TryGetValue(rule.Id, out hops)) hops = new List<string>();
                string baseOrder, baseHops;
                bool baseKill, baseCasc;
                if (_baseOrder.TryGetValue(rule.Id, out baseOrder)
                    && _baseKill.TryGetValue(rule.Id, out baseKill)
                    && _baseCasc.TryGetValue(rule.Id, out baseCasc)
                    && _baseCascOrder.TryGetValue(rule.Id, out baseHops)
                    && killOn == baseKill && cascOn == baseCasc
                    && string.Join(",", order) == baseOrder && string.Join(",", hops) == baseHops) continue;

                rule.KillSwitch = killOn;
                rule.Cascade = cascOn;
                rule.CascadeHops = new List<string>(hops);

                // Clicked variants carry the order. Everything shown but not clicked is excluded,
                // which is what "не кликнули — не участвует" means; the variants with no column
                // keep whatever this rule said about them before.
                var shown = _slots.Select(s => s.Id).ToList();
                rule.VpnSlotOrder = order.Concat(_hiddenRanked[rule.Id]).ToList();
                rule.DisabledVpnSlotIds = shown.Where(id => !order.Contains(id))
                                               .Concat(_hiddenDisabled[rule.Id]).ToList();

                // A «без VPN» tile given a VPN here becomes a VPN tile, switched off — the same as
                // AppConfigStore.NormalizeDirectWithTicks: the plate shows the first VPN in grey
                // with ▷, traffic keeps going direct until it is pressed.
                if (!rule.RequiresVpn && !rule.Block && (order.Count > 0 || (cascOn && hops.Count >= 2)))
                {
                    rule.RequiresVpn = true;
                    rule.AutoConnect = false;
                }
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
