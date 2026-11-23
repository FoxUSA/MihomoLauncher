using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // «Статистика» — the second tab of the DNS window (2026-11-17, variant A of
    // .dev\mockups\dns-stats.html): four numbers, then six tables — request types, blocked
    // domains, top domains; top tiles (instead of the mock-up's clients), companies, countries.
    // Data: Services/DnsStats (counted by MainWindow.DnsStats). Nothing is ever cut with «…»:
    // names wrap (a domain breaks after its dots), the number columns take what they need.
    public partial class SettingsWindow
    {
        // Set by the main window: a tile's small picture and its shown name.
        public Func<AppEntry, double, FrameworkElement> TileIcon { get; set; }
        public Func<AppEntry, string> TileName { get; set; }

        private sealed class StPanel
        {
            public Run Count;
            public StackPanel Rows;
            public ScrollViewer Scroll;
            // Rows made on demand (2026-11-21: the window took ~2 s to open laying out hundreds of
            // rows when five are seen): StPage first, the next page when scrolled near the end.
            public List<Func<Border>> Pending = new List<Func<Border>>();
            public int Made;        // of the current list, rows already in Rows
            public int Want;        // rows the user has scrolled to — kept across a refresh
        }

        private const int StPage = 25;

        // A table's rows, made lazily: what was shown before a refresh is shown again (the scroll
        // position stays), at least one page.
        private void StSetRows(StPanel p, List<Func<Border>> rows)
        {
            p.Pending = rows;
            p.Made = 0;
            StMore(p, Math.Max(StPage, p.Want));
            StFadeLater(p);
        }

        private static void StMore(StPanel p, int upTo)
        {
            upTo = Math.Min(upTo, p.Pending.Count);
            while (p.Made < upTo) p.Rows.Children.Add(p.Pending[p.Made++]());
        }

        // Near the end of what is made: the next page.
        private static void StOnScroll(StPanel p)
        {
            if (p.Made >= p.Pending.Count) return;
            var sv = p.Scroll;
            if (sv.VerticalOffset + sv.ViewportHeight < sv.ExtentHeight - 4 * StRowH) return;
            StMore(p, p.Made + StPage);
            p.Want = p.Made;
        }

        private int _stDays = 30;
        private string _stTile;          // tile filter, null = all
        private string _stMask = "";     // the search mask as applied (DnsStats.MaskOf), "" = none
        private string _stEmptyText;     // what an empty table says while a mask is on
        private DispatcherTimer _stMaskTimer;
        private int _stShown = -1;       // DnsStats.Version last drawn
        private DispatcherTimer _stTimer;
        private StPanel _pTypes, _pBlocked, _pDomains, _pTiles, _pCompanies, _pCountries;
        private TextBlock _stHead;
        private Wpf.Ui.Controls.ToggleSwitch _stKeep;
        private readonly List<Border> _stSeg = new List<Border>();
        private Border _stFilter;
        private TextBlock _stFilterText;
        private TextBlock[] _kpiValue, _kpiSub;

        private static readonly Brush StBlue = Theme.SpeedBlue, StRed = Theme.SpeedRed, StAmber = Theme.SpeedAmber, StGreen = Theme.SpeedGreen;
        private static readonly Brush StViolet = Theme.AccentViolet;
        private static readonly Brush StStripe = Theme.ListStripeOnCard;
        private static readonly Brush StLine = Theme.ListLineOnCard;
        private static readonly Brush StBadge = Theme.BadgeGreyBg;
        private const double StRowH = 31;   // a one-line row, all in: content 24 + padding 3+3 + rule 1
        private const int StMaxRows = 300;


        private void InitDnsStats()
        {
            DnsStatsHost.Content = BuildStats();
            Tabs.SelectionChanged += (s, e) => { if (e.OriginalSource == Tabs) SyncStatsTimer(); };
            Action changed = () => Dispatcher.BeginInvoke(new Action(() => { _stShown = -1; if (TabDnsStats.IsSelected) RefreshStats(); }));
            DnsStats.Changed += changed;
            Closed += (s, e) => { DnsStats.Changed -= changed; _stTimer?.Stop(); _stMaskTimer?.Stop(); };
            DnsMaskBox.Visibility = TabDnsStats.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- the mask (2026-11-18) ----------

        private void OnDnsMaskChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            bool empty = string.IsNullOrEmpty(TxtDnsMask.Text);
            BtnDnsMaskClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            LblDnsMaskHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (_stMaskTimer == null)
            {
                _stMaskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _stMaskTimer.Tick += (s, a) => { _stMaskTimer.Stop(); ApplyDnsMask(); };
            }
            _stMaskTimer.Stop();
            _stMaskTimer.Start();
        }

        private void OnDnsMaskKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; _stMaskTimer?.Stop(); ApplyDnsMask(); }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(TxtDnsMask.Text)) { e.Handled = true; ClearDnsMask(); }
        }

        // A click anywhere on the pill (its padding too) puts the caret in the box.
        private void OnDnsMaskBoxDown(object sender, MouseButtonEventArgs e)
        {
            if (TxtDnsMask.IsKeyboardFocusWithin) return;
            TxtDnsMask.Focus();
            TxtDnsMask.CaretIndex = TxtDnsMask.Text.Length;
            e.Handled = true;
        }

        private void OnDnsMaskClear(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ClearDnsMask();
            TxtDnsMask.Focus();
        }

        private void ClearDnsMask()
        {
            _stMaskTimer?.Stop();
            TxtDnsMask.Text = "";
            ApplyDnsMask();
        }

        private void ApplyDnsMask()
        {
            var m = DnsStats.NormalizeMask(TxtDnsMask.Text);
            if (m == _stMask) return;
            _stMask = m;
            _stShown = -1;
            RefreshStats();
        }

        private void SyncStatsTimer()
        {
            DnsMaskBox.Visibility = TabDnsStats.IsSelected ? Visibility.Visible : Visibility.Collapsed;
            if (TabDnsStats.IsSelected)
            {
                _stShown = -1;
                RefreshStats();
                if (_stTimer == null)
                {
                    _stTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
                    _stTimer.Tick += (s, e) => RefreshStats();
                }
                _stTimer.Start();
            }
            else _stTimer?.Stop();
        }

        // ---------- building ----------

        private UIElement BuildStats()
        {
            var root = new StackPanel();

            // Header: who resolves · since — remember domains — period.
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _stHead = new TextBlock { FontSize = 12.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            head.Children.Add(_stHead);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);
            _stKeep = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = DnsStats.KeepDomains, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("DnsSt_KeepTip") };
            _stKeep.Checked += (s, e) => DnsStats.KeepDomains = true;
            _stKeep.Unchecked += (s, e) => DnsStats.KeepDomains = false;
            right.Children.Add(_stKeep);
            right.Children.Add(new TextBlock { Text = Loc.T("DnsSt_Keep"), FontSize = 12.5, Foreground = Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0), ToolTip = Loc.T("DnsSt_KeepTip") });
            var seg = new Border { BorderBrush = Theme.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true };
            var segRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var p in new[] { new { D = 1, K = "DnsSt_Today" }, new { D = 7, K = "DnsSt_7" }, new { D = 30, K = "DnsSt_30" } })
            {
                int days = p.D;
                var b = new Border { Padding = new Thickness(12, 5, 12, 5), Cursor = Cursors.Hand, Tag = days, Background = Brushes.Transparent, Child = new TextBlock { Text = Loc.T(p.K), FontSize = 12.5 } };
                b.MouseLeftButtonUp += (s, e) => { _stDays = days; _stShown = -1; RefreshStats(); };
                _stSeg.Add(b);
                segRow.Children.Add(b);
            }
            seg.Child = segRow;
            right.Children.Add(seg);
            head.Children.Add(right);
            root.Children.Add(head);

            // Tile filter chip — in the first number's card, in place of its «все плитки» line, so
            // the tab keeps its height (on a line of its own it pushed the footnote under a scroll bar).
            _stFilterText = new TextBlock { FontSize = 12, Foreground = Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var x = new TextBlock { Text = "✕", FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextSecondary };
            var chip = new DockPanel();
            DockPanel.SetDock(x, Dock.Right);
            chip.Children.Add(x);
            chip.Children.Add(_stFilterText);
            _stFilter = new Border
            {
                Background = Theme.SelectionBg, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 0, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 20, Cursor = Cursors.Hand,
                Child = chip, Visibility = Visibility.Collapsed, ToolTip = Loc.T("DnsSt_FilterClearTip"),
            };
            _stFilter.MouseLeftButtonUp += (s, e) => { _stTile = null; _stShown = -1; RefreshStats(); };

            // Four numbers.
            var kpis = new UniformGrid { Columns = 4, Margin = new Thickness(-5, 0, -5, 0) };
            _kpiValue = new TextBlock[4];
            _kpiSub = new TextBlock[4];
            var kpiLabels = new[] { "DnsSt_KpiTotal", "DnsSt_KpiBlocked", "DnsSt_KpiShare", "DnsSt_KpiPeak" };
            for (int i = 0; i < 4; i++)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = Loc.T(kpiLabels[i]), FontSize = 11, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap });
                _kpiValue[i] = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 1) };
                Typography.SetNumeralAlignment(_kpiValue[i], FontNumeralAlignment.Tabular);
                sp.Children.Add(_kpiValue[i]);
                _kpiSub[i] = new TextBlock { FontSize = 12, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, MinHeight = 20 };
                sp.Children.Add(_kpiSub[i]);
                if (i == 0) sp.Children.Add(_stFilter);
                kpis.Children.Add(StCard(sp, new Thickness(5, 0, 5, 0)));
            }
            _kpiValue[0].Foreground = StBlue;
            _kpiValue[1].Foreground = StRed;
            root.Children.Add(kpis);

            // Six tables, two rows of three.
            const double body1 = 5 * StRowH + 4, body2 = 6 * StRowH + 4;   // fits a 1080p screen at 150%
            _pTypes = StMakePanel(Loc.T("DnsSt_Types"), null, body1, false);
            _pBlocked = StMakePanel(Loc.T("DnsSt_Blocked"), StLegend(new[] { StSq(StAmber), StRun(" " + Loc.T("DnsSt_LgStub") + " · "), StSq(StRed), StRun(" " + Loc.T("DnsSt_LgRule")) }), body1, true);
            _pDomains = StMakePanel(Loc.T("DnsSt_Domains"), StLegend3("DnsSt_LgTotal"), body1, true);
            _pTiles = StMakePanel(Loc.T("DnsSt_Tiles"), StLegend3("DnsSt_LgConns"), body2, true);
            _pCompanies = StMakePanel(Loc.T("DnsSt_Companies"), StLegend3("DnsSt_LgTotal").Concat(new Inline[] { new Run("  ⓘ") { ToolTip = Loc.T("DnsSt_CompaniesTip"), Cursor = Cursors.Help } }).ToArray(), body2, false);
            _pCountries = StMakePanel(Loc.T("DnsSt_Countries"), StLegend(new Inline[] { StRun(Loc.T("DnsSt_CountriesSub")) }), body2, false);
            root.Children.Add(StRow3(_pTypes, _pBlocked, _pDomains, new Thickness(-5, 10, -5, 0)));
            root.Children.Add(StRow3(_pTiles, _pCompanies, _pCountries, new Thickness(-5, 10, -5, 4)));
            return root;
        }

        private Inline[] StLegend3(string totalKey)
            => new Inline[] { StRun(Loc.T(totalKey), StBlue), StRun(" · "), StRun(Loc.T("DnsSt_LgBlocked"), StRed), StRun(" · " + Loc.T("DnsSt_LgShare")) };

        private static Inline[] StLegend(Inline[] parts) => parts;
        private static Run StRun(string text, Brush fg = null) { var r = new Run(text); if (fg != null) r.Foreground = fg; return r; }
        private static Run StSq(Brush fg) => new Run("■") { Foreground = fg };

        private static Border StCard(UIElement child, Thickness margin) => new Border
        {
            Background = Theme.CardBg, BorderBrush = Theme.CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = margin, Child = child,
        };

        private static Grid StRow3(StPanel a, StPanel b, StPanel c, Thickness margin)
        {
            var g = new Grid { Margin = margin };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int col = 0;
            foreach (var p in new[] { a, b, c })
            {
                var card = (FrameworkElement)p.Scroll.Tag;
                Grid.SetColumn(card, col++);
                g.Children.Add(card);
            }
            return g;
        }

        private StPanel StMakePanel(string title, Inline[] legend, double bodyHeight, bool withCount)
        {
            var p = new StPanel();
            var dock = new DockPanel();
            var t = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            t.Inlines.Add(new Run(title));
            if (withCount)
            {
                p.Count = new Run("") { FontWeight = FontWeights.Normal, Foreground = Theme.TextMuted, FontSize = 12.5 };
                t.Inlines.Add(p.Count);
            }
            DockPanel.SetDock(t, Dock.Top);
            dock.Children.Add(t);
            var lg = new TextBlock { FontSize = 11.5, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            if (legend != null) foreach (var i in legend) lg.Inlines.Add(i);
            DockPanel.SetDock(lg, Dock.Top);
            dock.Children.Add(lg);
            // Right gap = the scroll bar: WPF-UI draws it OVER the content, and it covered the last
            // column («(48%)», the blocked count) — seen on the stand's render 2026-11-17.
            p.Rows = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            Grid.SetIsSharedSizeScope(p.Rows, true);
            p.Scroll = new ScrollViewer
            {
                Height = bodyHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Content = p.Rows,
            };
            // Rows hidden below the edge: the last visible one fades, so it reads as "scroll for more",
            // not as a row cut in half.
            p.Scroll.ScrollChanged += (s, e) => { StOnScroll(p); StFade(p.Scroll); };
            dock.Children.Add(p.Scroll);
            p.Scroll.Tag = StCard(dock, new Thickness(5, 0, 5, 0));
            return p;
        }

        private static void StFade(ScrollViewer sv)
        {
            bool more = sv.VerticalOffset + sv.ViewportHeight < sv.ExtentHeight - 1;
            if (!more) { sv.OpacityMask = null; return; }
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            double h = Math.Max(1, sv.ActualHeight);
            g.GradientStops.Add(new GradientStop(Colors.Black, 0));
            g.GradientStops.Add(new GradientStop(Colors.Black, Math.Max(0, 1 - 26 / h)));
            g.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            sv.OpacityMask = g;
        }

        // ---------- filling ----------

        private void RefreshStats()
        {
            if (!TabDnsStats.IsSelected) return;
            try
            {
                int ver = DnsStats.Version;
                if (ver == _stShown) return;
                _stShown = ver;

                foreach (var b in _stSeg)
                {
                    bool on = (int)b.Tag == _stDays;
                    b.Background = on ? Theme.SelectionBg : Brushes.Transparent;
                    ((TextBlock)b.Child).FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    ((TextBlock)b.Child).Foreground = on ? Theme.TextPrimary : Theme.TextSecondary;
                }
                if (_stKeep.IsChecked != DnsStats.KeepDomains) _stKeep.IsChecked = DnsStats.KeepDomains;

                var act = DirectDns.Active(_editing);
                var who = act.Count > 0
                    ? Loc.T("DnsSt_HeadOwn", !string.IsNullOrEmpty(act[0].Provider) ? act[0].Provider : act[0].Host)
                    : Loc.T("DnsSt_HeadMachine");
                var since = DnsStats.Since;
                _stHead.Text = who + " · " + (since.HasValue ? Loc.T("DnsSt_Since", since.Value.ToString("dd.MM.yyyy")) : Loc.T("DnsSt_NotYet"));

                // The mask leaves in every table and figure only the names it passes.
                var mask = DnsStats.MaskOf(_stMask);
                System.Net.IPAddress ip;
                _stEmptyText = mask == null ? null
                    : System.Net.IPAddress.TryParse(_stMask, out ip) ? Loc.T("DnsSt_MaskIp", _stMask)
                    : Loc.T("DnsSt_MaskNone", _stMask);
                var all = DnsStats.Query(_stDays, null, mask);
                if (_stTile != null && mask == null && !all.Tiles.Any(t => t.Key == _stTile)) _stTile = null;
                var s = _stTile == null ? all : DnsStats.Query(_stDays, _stTile, mask);

                _stFilter.Visibility = _stTile == null ? Visibility.Collapsed : Visibility.Visible;
                _kpiSub[0].Visibility = _stTile == null ? Visibility.Visible : Visibility.Collapsed;
                if (_stTile != null) _stFilterText.Text = Loc.T("DnsSt_KpiTotalTile", StTileTitle(_stTile));

                // Numbers.
                _kpiValue[0].Text = StN(s.N);
                _kpiSub[0].Text = _stTile != null ? Loc.T("DnsSt_KpiTotalTile", StTileTitle(_stTile))
                                : mask != null ? Loc.T("DnsSt_KpiTotalMask", _stMask) : Loc.T("DnsSt_KpiTotalAll");
                _kpiValue[1].Text = StN(s.Blocked);
                _kpiSub[1].Text = Loc.T("DnsSt_KpiBlockedSub", StN(s.Rule), StN(s.Stub));
                _kpiValue[2].Text = StPct(s.Blocked, s.N);
                _kpiSub[2].Text = Loc.T("DnsSt_KpiShareSub");
                _kpiValue[3].Text = StN(s.PeakBlocked);
                _kpiSub[3].Text = s.PeakDay.HasValue ? s.PeakDay.Value.ToString("dd.MM") : "—";

                FillTypes(s);
                FillBlocked(s);
                FillDomains(s);
                FillTiles(all);
                FillCompanies(s);
                FillCountries(s);
            }
            catch (Exception ex) { Log.Error("dns-stats-view", ex); }
        }

        private void FillTypes(DnsStats.Snapshot s)
        {
            var rows = _pTypes.Rows;
            rows.Children.Clear();
            var items = new[]
            {
                new { Brush = StAmber, Name = Loc.T("DnsSt_TypeStub"), Sub = Loc.T("DnsSt_TypeStubSub"), V = s.Stub },
                new { Brush = StRed, Name = Loc.T("DnsSt_TypeRule"), Sub = Loc.T("DnsSt_TypeRuleSub"), V = s.Rule },
                new { Brush = StViolet, Name = Loc.T("DnsSt_TypeEmpty"), Sub = Loc.T("DnsSt_TypeEmptySub"), V = s.Empty },
                new { Brush = StGreen, Name = Loc.T("DnsSt_TypeOk"), Sub = "", V = s.Allowed },
            };
            int i = 0;
            foreach (var it in items)
            {
                var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = it.Brush, VerticalAlignment = VerticalAlignment.Center };
                // One line each (the panel holds five): what each kind means is in the tooltip.
                var row = StRow(i++, dot, StName(it.Name, null), StNum(StN(it.V), Theme.TextPrimary, true), null, StNum(StPct(it.V, s.N), Theme.TextMuted));
                if (!string.IsNullOrEmpty(it.Sub)) row.ToolTip = it.Sub;
                rows.Children.Add(row);
            }
            // The same split as one bar.
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 2) };
            int col = 0;
            foreach (var it in items)
            {
                if (it.V <= 0) continue;
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(it.V, GridUnitType.Star) });
                var b = new Border { Background = it.Brush };
                Grid.SetColumn(b, col++);
                bar.Children.Add(b);
            }
            if (col > 0) rows.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = bar });
            StFadeLater(_pTypes);
        }

        private void FillBlocked(DnsStats.Snapshot s)
        {
            var list = s.Domains.Where(d => d.Blocked > 0).OrderByDescending(d => d.Blocked).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _pBlocked.Count.Text = " · " + StN(list.Count);
            var rows = _pBlocked.Rows;
            rows.Children.Clear();
            if (list.Count == 0) { rows.Children.Add(StEmpty(s.HasNamelessOnly && !DnsStats.KeepDomains && s.Blocked > 0 ? "DnsSt_NoDomains" : "DnsSt_Empty")); StSetRows(_pBlocked, new List<Func<Border>>()); return; }
            StSetRows(_pBlocked, list.Take(StMaxRows).Select((d, i) => (Func<Border>)(() =>
            {
                var idx = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11.5, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 18 };
                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Background = StKindBrush(d.Main) };
                return StRow(i, idx, StDomain(d.Name), dot, null, StNum(StN(d.Blocked), StRed));
            })).ToList());
        }

        private void FillDomains(DnsStats.Snapshot s)
        {
            _pDomains.Count.Text = " · " + StN(s.Domains.Count);
            var rows = _pDomains.Rows;
            rows.Children.Clear();
            if (s.Domains.Count == 0) { rows.Children.Add(StEmpty(s.HasNamelessOnly && !DnsStats.KeepDomains ? "DnsSt_NoDomains" : "DnsSt_Empty")); StSetRows(_pDomains, new List<Func<Border>>()); return; }
            StSetRows(_pDomains, s.Domains.Take(StMaxRows).Select((d, i) => (Func<Border>)(() =>
                StRow(i, null, StDomain(d.Name), StNum(StN(d.N), StBlue), StNum(StN(d.Blocked), StRed), StNum("(" + StPct(d.Blocked, d.N) + ")", Theme.TextMuted)))).ToList());
        }

        private void FillTiles(DnsStats.Snapshot all)
        {
            _pTiles.Count.Text = " · " + StN(all.Tiles.Count);
            var rows = _pTiles.Rows;
            rows.Children.Clear();
            if (all.Tiles.Count == 0) { rows.Children.Add(StEmpty("DnsSt_Empty")); StSetRows(_pTiles, new List<Func<Border>>()); return; }
            StSetRows(_pTiles, all.Tiles.Select((t, i) => (Func<Border>)(() =>
            {
                var entry = StTileById(t.Key);
                FrameworkElement icon = null;
                try { if (entry != null && TileIcon != null) icon = TileIcon(entry, 24); } catch { icon = null; }
                if (icon == null) icon = StBadgeBox("?", StBadge, 24);
                icon.VerticalAlignment = VerticalAlignment.Center;
                var row = StRow(i, icon, StName(StTileTitle(t.Key), StRouteOf(entry)), StNum(StN(t.N), StBlue), StNum(StN(t.Blocked), StRed), StNum("(" + StPct(t.Blocked, t.N) + ")", Theme.TextMuted));
                var key = t.Key;
                row.Cursor = Cursors.Hand;
                row.ToolTip = Loc.T("DnsSt_TileTip");
                if (key == _stTile) row.Background = Theme.SelectionBg;
                row.MouseLeftButtonUp += (s2, e2) => { _stTile = _stTile == key ? null : key; _stShown = -1; RefreshStats(); };
                return row;
            })).ToList());
        }

        private void FillCompanies(DnsStats.Snapshot s)
        {
            var rows = _pCompanies.Rows;
            rows.Children.Clear();
            if (s.Companies.Count == 0) { rows.Children.Add(StEmpty("DnsSt_Empty")); StSetRows(_pCompanies, new List<Func<Border>>()); return; }
            StSetRows(_pCompanies, s.Companies.Take(StMaxRows).Select((c, i) => (Func<Border>)(() =>
            {
                var name = DnsStats.CompanyDisplay(c.Name);
                var badge = StBadgeBox(StInitials(name), StColorOf(c.Name), 24);
                return StRow(i, badge, StName(name, null), StNum(StN(c.N), StBlue), StNum(StN(c.Blocked), StRed), StNum("(" + StPct(c.Blocked, c.N) + ")", Theme.TextMuted));
            })).ToList());
        }

        private void FillCountries(DnsStats.Snapshot s)
        {
            var rows = _pCountries.Rows;
            rows.Children.Clear();
            if (s.Countries.Count == 0) { rows.Children.Add(StEmpty("DnsSt_Empty")); StSetRows(_pCountries, new List<Func<Border>>()); return; }
            int total = s.Countries.Sum(c => c.N);
            StSetRows(_pCountries, s.Countries.Take(StMaxRows).Select((c, i) => (Func<Border>)(() =>
                StRow(i, StBadgeBox(c.Key, StBadge, 26, 20, 10), StName(c.Name, null), StNum(StN(c.N), StBlue), null, StNum(StPct(c.N, total), Theme.TextMuted)))).ToList());
        }

        private void StFadeLater(StPanel p) => Dispatcher.BeginInvoke(new Action(() => StFade(p.Scroll)), DispatcherPriority.Loaded);

        // ---------- row pieces ----------

        // [lead] [name, takes the rest, wraps] [a] [b] [c] — a/b/c share their widths across the
        // table (SharedSizeGroup), so the columns line up and a long number never cuts anything.
        private static Border StRow(int index, FrameworkElement lead, FrameworkElement name, FrameworkElement a, FrameworkElement b, FrameworkElement c)
        {
            var g = new Grid { MinHeight = StRowH - 7 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "lead" });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "a" });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "b" });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "c" });
            void Put(FrameworkElement el, int col, Thickness m)
            {
                if (el == null) return;
                el.Margin = new Thickness(el.Margin.Left + m.Left, el.Margin.Top, el.Margin.Right + m.Right, el.Margin.Bottom);
                Grid.SetColumn(el, col);
                g.Children.Add(el);
            }
            Put(lead, 0, new Thickness(0, 0, 8, 0));
            Put(name, 1, new Thickness(0, 0, 0, 0));
            Put(a, 2, new Thickness(10, 0, 0, 0));
            Put(b, 3, new Thickness(10, 0, 0, 0));
            Put(c, 4, new Thickness(10, 0, 4, 0));
            return new Border
            {
                Child = g, Padding = new Thickness(4, 3, 2, 3),
                Background = index % 2 == 1 ? StStripe : Brushes.Transparent,
                BorderBrush = StLine, BorderThickness = new Thickness(0, 0, 0, 1),
            };
        }

        private static TextBlock StNum(string text, Brush fg, bool bold = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = 13, Foreground = fg, TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        private static TextBlock StName(string name, string sub)
        {
            var t = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextPrimary };
            t.Inlines.Add(new Run(name ?? ""));
            if (!string.IsNullOrEmpty(sub))
            {
                t.Inlines.Add(new LineBreak());
                t.Inlines.Add(new Run(sub) { FontSize = 11, Foreground = Theme.TextMuted });
            }
            return t;
        }

        // Monospace, and allowed to break only right after a dot (a zero-width space there):
        // «googleads.g.doubleclick.net» wraps as «googleads.g.doubleclick.» / «net», never mid-word.
        private static readonly FontFamily StMono = new FontFamily("Consolas");
        private static TextBlock StDomain(string host)
            => new TextBlock
            {
                Text = (host ?? "").Replace(".", ".​"), FontFamily = StMono, FontSize = 13, ToolTip = host,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextPrimary,
            };

        private static Border StBadgeBox(string text, Brush bg, double w, double h = 0, double font = 0)
        {
            if (h <= 0) h = w;
            return new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(h * 0.24), Background = bg, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text, FontSize = font > 0 ? font : h * 0.42, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextOnColor,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        // Empty under a mask: says the mask matched nothing (or that an IP can't be in here at all).
        private TextBlock StEmpty(string key)
            => new TextBlock { Text = _stEmptyText ?? Loc.T(key), FontSize = 12.5, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 0) };

        private static Brush StKindBrush(DnsStats.Kind k)
            => k == DnsStats.Kind.Stub ? StAmber : k == DnsStats.Kind.NoAddress ? StViolet : k == DnsStats.Kind.Rule ? StRed : StGreen;

        // The tiles' monogram colours: a company gets the same kind of badge a tile does.
        private static readonly Color[] StColors = Theme.MonogramColors;

        private static Brush StColorOf(string name)
        {
            int h = 17;
            foreach (char ch in (name ?? "").ToLowerInvariant()) h = unchecked(h * 31 + ch);
            var b = new SolidColorBrush(StColors[(h & 0x7FFFFFFF) % StColors.Length]);
            b.Freeze();
            return b;
        }

        // «Google» → «Go», «T-Bank» → «TB», «Яндекс» → «Ян».
        private static string StInitials(string name)
        {
            var words = (name ?? "").Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
            if (words.Count == 0) return "?";
            if (words.Count >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            var w0 = words[0];
            return w0.Length >= 2 ? char.ToUpperInvariant(w0[0]) + w0.Substring(1, 1).ToLowerInvariant() : w0.ToUpperInvariant();
        }

        // ---------- tiles ----------

        private AppEntry StTileById(string id)
            => string.IsNullOrEmpty(id) ? null : (_editing?.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.Id == id);

        private string StTileTitle(string id)
        {
            if (string.IsNullOrEmpty(id)) return Loc.T("DnsSt_NoTile");
            var e = StTileById(id);
            if (e == null) return Loc.T("DnsSt_GoneTile");
            try { if (TileName != null) return TileName(e); } catch { }
            return e.Name;
        }

        private static string StRouteOf(AppEntry e)
        {
            if (e == null) return "";
            if (e.IsBlocking) return Loc.T("DnsSt_RouteBlock");
            return Loc.T(e.RequiresVpn ? "DnsSt_RouteVpn" : "DnsSt_RouteDirect");
        }

        private static string StN(int v) => v.ToString("N0", CultureInfo.CurrentCulture);

        private static string StPct(int part, int whole)
        {
            if (whole <= 0 || part <= 0) return "0%";
            double p = part * 100.0 / whole;
            return (p < 10 ? p.ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(p).ToString(CultureInfo.CurrentCulture)) + "%";
        }
    }
}
