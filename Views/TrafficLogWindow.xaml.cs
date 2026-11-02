using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // «Что заблокировано и почему» — a view onto EngineLogStore. It owns no data: the feed runs
    // for the whole life of the launcher, so closing and reopening this window keeps the history.
    public partial class TrafficLogWindow : FluentWindow
    {
        private readonly ICollectionView _view;

        public TrafficLogWindow(AppConfig cfg)
        {
            InitializeComponent();
            _cfg = cfg;
            Title = Loc.T("Log_Title");
            Bar.Title = Title;

            var store = EngineLogStore.Instance;
            _view = CollectionViewSource.GetDefaultView(store.Rows);
            _view.Filter = o => { var r = o as LogRow; return r != null && Fits(r); };

            // Newest first. Rows are deduped, so a repeat only bumps the row it already has —
            // in insertion order the times then read out of sequence (03:27:09, 03:27:15,
            // 03:27:10…). Live sorting moves a bumped row back to the top instead.
            _view.SortDescriptions.Add(new SortDescription("Time", ListSortDirection.Descending));
            var live = _view as ICollectionViewLiveShaping;
            if (live != null && live.CanChangeLiveSorting)
            {
                live.LiveSortingProperties.Add("Time");
                live.IsLiveSorting = true;
            }

            Grid.ItemsSource = _view;
            Tabs.SelectedIndex = 0;
            UpdateTabHint();
            UpdateTabHeaders();

            // Row counts on the headers, refreshed on a timer rather than on every arrival:
            // during a busy page the feed adds rows several times a second, and recounting the
            // whole list each time would be the most expensive thing this window does.
            _countTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700),
            };
            _countTimer.Tick += (s, e) => { UpdateTabHeaders(); RecomputeAppGroups(); };
            _countTimer.Start();

            _byApp = _byAppSticky;
            ChkByApp.IsChecked = _byApp;
            RecomputeAppGroups();

            ShowStatus(store.LastStatus);
            store.Status += OnStatus;
            Loc.LanguageChanged += OnLanguageChanged;
            Closed += (s, e) =>
            {
                store.Status -= OnStatus;
                Loc.LanguageChanged -= OnLanguageChanged;
                try { _countTimer.Stop(); } catch { }
                try { _filterTimer?.Stop(); } catch { }
                CancelLive();
            };
        }

        private readonly System.Windows.Threading.DispatcherTimer _countTimer;

        // The headers are built in code (name + count), so a language switch has to redraw
        // them by hand — a DynamicResource would have done it for us.
        private void OnLanguageChanged()
        {
            UpdateTabHeaders();
            UpdateTabHint();
        }

        // "Проблемы ×4" — how many LINES the tab holds. Repeats are already folded into their
        // row (the «Повторов» column), so this counts distinct entries, not events.
        private void UpdateTabHeaders()
        {
            var rows = EngineLogStore.Instance.Rows;
            int problems = 0, ads = 0, outside = 0;
            foreach (var r in rows)
            {
                if (r == null) continue;
                switch (r.Category)
                {
                    case LogCategory.AdBlock: ads++; break;
                    case LogCategory.Outside: outside++; break;
                    case LogCategory.Problem: problems++; break;
                }
            }

            TabProblems.Header = WithCount("Log_TabProblems", problems);
            TabAds.Header = WithCount("Log_TabAds", ads);
            TabOutside.Header = WithCount("Log_TabOutside", outside);
            TabAll.Header = WithCount("Log_TabAll", rows.Count);
        }

        private static string WithCount(string key, int n)
        {
            var text = Loc.T(key);
            return n > 0 ? text + "  ×" + n : text;
        }

        // Which tab is showing. Ads and foreign direct failures are kept — they were worth
        // seeing — just not mixed in with the launcher's own verdicts, where sixty tracker
        // lines in a row hid the one that mattered.
        private string CurrentTab
        {
            get
            {
                var item = Tabs?.SelectedItem as System.Windows.Controls.TabItem;
                var tag = item?.Tag as string;
                return string.IsNullOrEmpty(tag) ? "problem" : tag;
            }
        }

        private bool Fits(LogRow r)
        {
            // Looking for one address: every row about it, whichever tab it would sit in — a
            // site that "doesn't open" may be in Проблемы, in the ad tab or in Мимо утилиты.
            if (_filter.Length > 0) return Matches(r, _filter);
            if (!FitsTab(r)) return false;
            // Per-app collapse: only the group's representative is shown, and it carries the
            // count of everything folded behind it.
            if (_byApp && !_appRep.ContainsValue(r)) return false;
            return true;
        }

        private bool FitsTab(LogRow r)
        {
            switch (CurrentTab)
            {
                case "ads": return r.Category == LogCategory.AdBlock;
                case "outside": return r.Category == LogCategory.Outside;
                case "all": return true;
                default: return r.Category == LogCategory.Problem;
            }
        }

        // One line per program instead of one per address.
        //
        // The plain dedup folds only rows that are identical (same verdict, host, program and
        // reason). A program that reaches fifty different hosts therefore still produces fifty
        // lines and buries whatever mattered — which is what the user hit. This groups by
        // program + verdict, shows the group's most recent row and hangs «+N» on it.
        //
        // Recomputed on the same 0.7 s timer as the counters (grouping over live data has to be
        // done in one pass; a filter callback is asked about rows one at a time, in no order).
        private void RecomputeAppGroups()
        {
            var previous = new List<LogRow>(_appRep.Values);
            _appRep.Clear();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            if (_byApp)
            {
                foreach (var r in EngineLogStore.Instance.Rows)
                {
                    if (r == null || !FitsTab(r)) continue;
                    var key = r.AppKey;
                    int n;
                    counts[key] = counts.TryGetValue(key, out n) ? n + 1 : 1;

                    LogRow best;
                    if (!_appRep.TryGetValue(key, out best) || r.Time > best.Time) _appRep[key] = r;
                }
            }

            // Clear the badge wherever it no longer belongs, then stamp the current winners.
            foreach (var r in previous) if (r != null) r.CollapsedCount = 0;
            foreach (var kv in _appRep)
            {
                int n;
                counts.TryGetValue(kv.Key, out n);
                kv.Value.CollapsedCount = n > 1 ? n - 1 : 0;
            }

            // Only disturb the view when the visible set actually changed.
            bool changed = previous.Count != _appRep.Count;
            if (!changed) foreach (var r in previous) if (!_appRep.ContainsValue(r)) { changed = true; break; }
            if (changed) _view.Refresh();
        }

        private void OnByAppChanged(object sender, RoutedEventArgs e)
        {
            _byApp = ChkByApp?.IsChecked == true;
            _byAppSticky = _byApp;
            RecomputeAppGroups();
            _view.Refresh();
        }

        private bool _byApp;
        private readonly Dictionary<string, LogRow> _appRep = new Dictionary<string, LogRow>(StringComparer.Ordinal);

        // Survives closing and reopening the window — the setting is about how the user reads
        // the log, and re-ticking it every time is exactly the kind of friction this is meant
        // to remove. In memory only, like the log itself.
        private static bool _byAppSticky;

        private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_view == null) return;
            RecomputeAppGroups();     // groups are per tab, so they change with it
            _view.Refresh();
            UpdateTabHint();
        }

        private void UpdateTabHint()
        {
            if (LblTabHint == null) return;
            switch (CurrentTab)
            {
                case "ads": LblTabHint.Text = Loc.T("Log_HintAds"); break;
                case "outside": LblTabHint.Text = Loc.T("Log_HintOutside"); break;
                case "all": LblTabHint.Text = Loc.T("Log_HintAll"); break;
                default: LblTabHint.Text = Loc.T("Log_HintProblems"); break;
            }
        }

        private void OnStatus(string message)
        {
            Dispatcher.Invoke(() => ShowStatus(message));
        }

        private void ShowStatus(string message)
        {
            bool ok = string.IsNullOrWhiteSpace(message);
            LblStatus.Text = ok ? Loc.T("Log_Listening") : Loc.T("Log_NoEngine");
            // The engine's error text is a whole .NET sentence — useful when something really is
            // wrong, far too long for a line that sits next to the tabs. It lives in the tooltip.
            LblStatus.ToolTip = ok ? null : message;
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            EngineLogStore.Instance.Clear();
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            foreach (var o in _view)
            {
                var r = o as LogRow;
                if (r == null) continue;
                sb.AppendLine(string.Join("\t", r.TimeText, r.CountText, r.VerdictText,
                                                r.RuleText, r.ProcessName, r.Host, r.WhyText));
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        // ---------- search: «куда пойдёт этот адрес» ----------

        private readonly AppConfig _cfg;
        private string _filter = "";
        private System.Windows.Threading.DispatcherTimer _filterTimer;
        private System.Threading.CancellationTokenSource _liveCts;

        private static bool Matches(LogRow r, string f)
        {
            return Has(r.Host, f) || Has(r.ProcessName, f) || Has(r.RuleText, f);
        }

        private static bool Has(string s, string f) => !string.IsNullOrEmpty(s) && s.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;

        // What the rows are filtered by: the host out of a pasted URL, or the text as typed.
        private static string FilterOf(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return "";
            Uri u;
            if (t.IndexOf("://", StringComparison.Ordinal) > 0 && Uri.TryCreate(t, UriKind.Absolute, out u) && u.Host.Length > 0)
                return u.Host.Trim('[', ']');
            int slash = t.IndexOf('/');
            return slash > 0 ? t.Substring(0, slash) : t;
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_filterTimer == null)
            {
                _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _filterTimer.Tick += (s, a) =>
                {
                    _filterTimer.Stop();
                    _filter = FilterOf(TxtSearch.Text);
                    if (_filter.Length == 0) { ExplainBox.Visibility = Visibility.Collapsed; CancelLive(); }
                    RecomputeAppGroups();
                    _view.Refresh();
                    UpdateFound();
                };
            }
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Explain(); }
            else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; TxtSearch.Text = ""; }
        }

        private void OnExplain(object sender, RoutedEventArgs e) => Explain();

        private void UpdateFound()
        {
            if (LblFound == null) return;
            if (_filter.Length == 0) { LblFound.Text = ""; return; }
            int n = 0;
            foreach (var o in _view) n++;
            LblFound.Text = n > 0 ? Loc.T("Log_FoundRows", n) : Loc.T("Log_FoundNone");
        }

        private void CancelLive()
        {
            try { _liveCts?.Cancel(); } catch { }
            _liveCts = null;
        }

        private async void Explain()
        {
            var query = (TxtSearch.Text ?? "").Trim();
            if (query.Length == 0) return;
            _filter = FilterOf(query);
            RecomputeAppGroups();
            _view.Refresh();
            UpdateFound();

            CancelLive();
            RouteExplainer.Result res;
            try { res = RouteExplainer.Explain(_cfg, query); }
            catch (Exception ex) { Log.Error("route-explain", ex); res = new RouteExplainer.Result { Text = ex.Message }; }
            TxtExplain.Text = res.Text;
            ExplainBox.Visibility = Visibility.Visible;

            // Goes DIRECT somewhere → ask the resolver that will answer it, for real. This is the
            // line that tells "your DNS cuts this site" apart from "the VPN does".
            if (res.Host == null || res.DirectServers.Count == 0) return;
            var server = res.DirectServers[0];
            var who = res.DirectIsOwn ? Loc.T("Log_LiveOwn", server) : Loc.T("Log_LiveMachine", server);
            var baseText = res.Text + Environment.NewLine + Environment.NewLine;
            TxtExplain.Text = baseText + Loc.T("Log_LiveAsking", who);

            var cts = new System.Threading.CancellationTokenSource();
            _liveCts = cts;
            string line;
            try
            {
                var answers = await DirectDns.QueryAsync(_cfg, server, new[] { res.Host }, cts.Token).ConfigureAwait(true);
                var a = answers.FirstOrDefault();
                if (a == null || !a.Ok) line = Loc.T("Log_LiveFail", who, DirectDns.Shorten(a?.Error));
                else if (a.Stub) line = Loc.T("Log_LiveStub", who, string.Join(", ", a.Addresses));
                else if (a.NxDomain) line = Loc.T("Log_LiveNx", who);
                else if (a.Addresses.Count == 0) line = Loc.T("Log_LiveEmpty", who);
                else line = Loc.T("Log_LiveOk", who, string.Join(", ", a.Addresses.Take(4)), a.Ms);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { line = Loc.T("Log_LiveFail", who, DirectDns.Shorten(ex.Message)); }
            if (cts.IsCancellationRequested || _liveCts != cts) return;
            TxtExplain.Text = baseText + line;
        }
    }
}
