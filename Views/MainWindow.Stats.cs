using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // The strip under the tiles (2026-11-10). Once the banner stopped repeating what the tiles
    // already show, that space went empty — so it now carries what the tiles CAN'T show:
    //   • latency to each VPN server a tile is riding (the engine's /delay, every 30 s);
    //   • how many times since launch the kill-switch / the DNS cut a connection;
    //   • the latest such cut (only the latest — never older ones, they read as happening now).
    // Click = the full log window. Read-only: nothing here decides routing.
    public partial class MainWindow
    {
        private const int StatsTickSeconds = 5;
        private const int StatsPingEveryTicks = 6;          // 6 × 5 s = 30 s

        private DispatcherTimer _statsTimer;
        private int _statsTick;
        private bool _statsMeasuring;
        private string _statsPingSet = "";                   // the proxies last measured, joined
        // proxy name -> last measured delay (null = no answer); absent = not measured yet.
        private readonly Dictionary<string, int?> _proxyDelay = new Dictionary<string, int?>(StringComparer.Ordinal);

        private void InitStats()
        {
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StatsTickSeconds) };
            _statsTimer.Tick += (s, e) => StatsTick();
            _statsTimer.Start();

            // The footer note is written from a dozen places; the plate has to follow it (show a
            // line that just appeared, drop one that was cleared) without each of them knowing.
            var textProp = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            textProp?.AddValueChanged(StatusFoot, (s, e) => RenderStats());
            var visProp = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(UIElement.VisibilityProperty, typeof(UIElement));
            visProp?.AddValueChanged(TopBanner, (s, e) => RenderStats());
        }

        private void StatsTick()
        {
            if (_hidden || WindowState == WindowState.Minimized) return;   // nobody is looking
            _statsTick++;
            MaybeRefreshNicIps();
            if (_statsTick % StatsPingEveryTicks == 0) MeasureStatsPing(force: true);
            else MeasureStatsPing(force: false);
            RenderStats();
        }

        // The proxies the connected tiles are riding right now.
        private List<string> StatsProxies()
        {
            if (_state != UiState.Done) return new List<string>();
            return _assignment.Values.Where(p => !string.IsNullOrEmpty(p))
                              .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        // force = the 30-s refresh; otherwise only when the set of servers changed (a tile
        // connected / switched VPN), so a new server gets its number without a 30-s wait.
        private async void MeasureStatsPing(bool force)
        {
            if (_statsMeasuring || _mihomo == null) return;
            var proxies = StatsProxies();
            var set = string.Join("\n", proxies);
            if (!force && set == _statsPingSet) return;
            _statsPingSet = set;

            foreach (var gone in _proxyDelay.Keys.Where(k => !proxies.Contains(k)).ToList()) _proxyDelay.Remove(gone);
            if (proxies.Count == 0) return;

            _statsMeasuring = true;
            try
            {
                var ctl = _mihomo;
                var tasks = proxies.Select(p => ctl.MeasureDelayAsync(PingEngineName(p), CancellationToken.None)).ToList();
                await System.Threading.Tasks.Task.WhenAll(tasks).ConfigureAwait(true);
                // The tile may have been disconnected meanwhile — keep only what's still in use.
                var now = StatsProxies();
                for (int i = 0; i < proxies.Count; i++)
                    if (now.Contains(proxies[i])) _proxyDelay[proxies[i]] = tasks[i].Result;
            }
            catch { }
            finally { _statsMeasuring = false; }
            RenderStats();
        }

        private bool _statsRendering;

        private void RenderStats()
        {
            if (StatsStrip == null || _statsRendering) return;
            _statsRendering = true;
            try { RenderStatsCore(); }
            finally { _statsRendering = false; }
        }

        private void RenderStatsCore()
        {
            var store = EngineLogStore.Instance;
            var proxies = StatsProxies();
            int hits = store.KillSwitchHits + store.DnsHits;

            // Snapshot of the layout, to re-fit the window height only when it actually moved.
            string before = LayoutSig();

            bool showMsg = TopBanner.Visibility == Visibility.Visible && !string.IsNullOrEmpty(BannerText.Text);
            bool showFoot = !string.IsNullOrWhiteSpace(StatusFoot.Text);
            bool showStats = _engineReady && (proxies.Count > 0 || hits > 0);
            bool showPing = showStats && proxies.Count > 0;

            BannerText.Visibility = showMsg ? Visibility.Visible : Visibility.Collapsed;
            StatusFoot.Visibility = showFoot ? Visibility.Visible : Visibility.Collapsed;
            StatsPing.Visibility = showPing ? Visibility.Visible : Visibility.Collapsed;
            StatsBlocksRow.Visibility = showStats ? Visibility.Visible : Visibility.Collapsed;
            bool showNics = ShowNicIps;
            StatsNics.Visibility = showNics ? Visibility.Visible : Visibility.Collapsed;
            // + the «a tile can be pulled out» line (MainWindow.TileDetach), while it shows.
            // + a cascade that didn't build: which hop and why (MainWindow.Cascade).
            StatsStrip.Visibility = showMsg || showFoot || showStats || showNics || DetachHintOn || CascadeLineOn ? Visibility.Visible : Visibility.Collapsed;

            // Only the stats are about the log; a plate holding just a message is not a link.
            StatsStrip.Cursor = showStats ? Cursors.Hand : null;
            StatsStrip.ToolTip = showStats ? Loc.T("Main_StatsTip") : null;

            // A small gap between whichever lines are visible, none above the first.
            bool first = true;
            foreach (UIElement ch in StatsLines.Children)
            {
                var fe = ch as FrameworkElement;
                if (fe == null) continue;
                bool visible = fe == TopBanner ? showMsg : fe.Visibility == Visibility.Visible;
                if (!visible) continue;
                fe.Margin = new Thickness(0, first ? 0 : 4, 0, 0);
                first = false;
            }

            if (showStats) RenderStatsRows(store, proxies);
            else StatsPing.Children.Clear();
            RenderNicIps();

            if (LayoutSig() != before) RecalcHeight();
        }

        private string LayoutSig()
        {
            return StatsStrip.Visibility + "|" + BannerText.Visibility + "|" + BannerText.Text + "|"
                 + StatusFoot.Visibility + "|" + StatusFoot.Text + "|"
                 + StatsPing.Visibility + "|" + StatsPing.Children.Count + "|" + StatsBlocksRow.Visibility + "|"
                 + StatsNics.Visibility + "|" + NicIpSig() + "|" + (CascadeLineOn ? _cascadeLine.Text : "");
        }

        private void RenderStatsRows(EngineLogStore store, List<string> proxies)
        {
            // ---- latency row ----
            StatsPing.Children.Clear();
            if (proxies.Count > 0)
            {
                StatsPing.Children.Add(new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Wpf.Ui.Controls.SymbolRegular.TopSpeed24,
                    FontSize = 14,
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                StatsPing.Children.Add(new TextBlock
                {
                    Text = Loc.T("Main_StatsPing"),
                    FontSize = 12.5,
                    Foreground = Theme.TextSecondary,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                for (int i = 0; i < proxies.Count; i++)
                    StatsPing.Children.Add(PingChip(proxies[i], i < proxies.Count - 1));
            }

            // ---- blocks row ----
            StatsCounters.Text = Loc.T("Main_StatsCounters", store.KillSwitchHits, store.DnsHits);
            // Only the LATEST block — cycling through older ones put long-gone events on screen
            // as if they were happening now (2026-11-10).
            var r = store.LastBlock;
            if (r == null) StatsTicker.Text = "· " + Loc.T("Main_StatsNothing");
            else
            {
                var who = !string.IsNullOrEmpty(r.ProcessName) ? r.ProcessName
                        : !string.IsNullOrEmpty(r.RuleText) ? r.RuleText : "?";
                var where = string.IsNullOrEmpty(r.Host) ? "—" : r.Host;
                var kind = r.Block == BlockKind.Dns ? "DNS" : "Kill-switch";
                StatsTicker.Text = "· " + Loc.T("Main_StatsLast", kind, who, where, r.Time.ToString("HH:mm:ss"));
            }
        }

        // What the engine is asked about for one entry of the row: a cascade (MainWindow.Cascade) is
        // measured through the whole chain — its last copy, for the cards of a tile riding it.
        private string PingEngineName(string proxy)
        {
            if (!MihomoConfigBuilder.IsChain(proxy)) return proxy;
            var rider = (_config.Apps ?? new List<Models.AppEntry>())
                .FirstOrDefault(a => a != null && _assignment.TryGetValue(a.Id, out var t) && t == proxy);
            return MihomoConfigBuilder.ChainNodeName(proxy, MihomoConfigBuilder.NicKey(rider));
        }

        // "№1 Нидерланды 84 мс" — the number tinted: green fast, amber slow, red no answer.
        // A cascade: «Turkey › ekb_home › KG_pc 190 мс».
        private UIElement PingChip(string proxy, bool separator)
        {
            ProxySlotInfo info;
            string name = MihomoConfigBuilder.IsChain(proxy)
                ? string.Join(" › ", MihomoConfigBuilder.ChainHops(proxy).Select(h => _proxySlot.TryGetValue(h, out var hi) && !string.IsNullOrEmpty(hi?.Name) ? hi.Name : h))
                : _proxySlot.TryGetValue(proxy, out info) && info != null && !string.IsNullOrEmpty(info.Name)
                ? info.Name   // the name alone (no «№n», 2026-11-27)
                : proxy;

            int? ms;
            bool measured = _proxyDelay.TryGetValue(proxy, out ms);
            string value = !measured ? Loc.T("Main_StatsMeasuring")
                         : ms.HasValue ? Loc.T("Main_StatsMs", ms.Value)
                         : Loc.T("Main_StatsNoAnswer");
            var brush = !measured ? Theme.TextMuted
                      : !ms.HasValue ? Theme.AccentRed
                      : ms.Value < 400 ? Theme.AccentGreen
                      : ms.Value < 1000 ? Theme.AccentAmber
                      : Theme.AccentRed;

            var tb = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new Run(name + " ") { Foreground = Theme.TextSecondary });
            tb.Inlines.Add(new Run(value) { Foreground = brush, FontWeight = FontWeights.SemiBold });
            if (separator) tb.Inlines.Add(new Run("   ·   ") { Foreground = Theme.TextMuted });
            return tb;
        }

        private void OnStatsClick(object sender, MouseButtonEventArgs e)
        {
            if (StatsBlocksRow.Visibility != Visibility.Visible) return;   // just a message, no stats
            OnOpenLog(sender, null);
        }
    }
}
