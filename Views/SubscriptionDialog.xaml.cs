using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Theme = MihomoLauncher.Services.Theme;

namespace MihomoLauncher.Views
{
    // «Подписка»: what a Clash subscription does, list by list — the way the issuer's panel shows
    // it, not as a million addresses — and «Обновить» with a real progress bar. Each list says
    // what happens to it (through the VPN, blocked, direct) and opens to show what it holds; the
    // last row answers the question every subscription raises: what about everything else.
    public partial class SubscriptionDialog : FluentWindow
    {
        private readonly AppConfig _cfg;
        private readonly string _proxyName, _url, _name;
        private CancellationTokenSource _cts;
        private List<string> _entries = new List<string>();
        private int _openToken;

        // Start downloading as soon as the window opens (the «Обновить» button in Settings).
        public bool AutoRefresh { get; set; }

        // After a successful download: let the running engine re-read the nodes, if it holds them.
        public Func<Task> AfterRefresh { get; set; }

        public SubscriptionDialog(AppConfig cfg, string proxyName, string url, string name)
        {
            InitializeComponent();
            _cfg = cfg; _proxyName = proxyName; _url = url; _name = name;
            LblTitle.Text = Loc.T("Sub_Title", name);
            Title = LblTitle.Text;
            Loaded += (s, e) =>
            {
                Render();
                if (AutoRefresh || !File.Exists(SubscriptionPolicy.CachePath(_cfg, _proxyName))) _ = RefreshAsync();
            };
            Closing += (s, e) => _cts?.Cancel();
        }

        // ---------- the list of rules ----------

        private void Render()
        {
            RowsHost.Children.Clear();
            _views.Clear();
            lock (_cache) _cache.Clear();   // «Обновить» may have rewritten the lists
            var cache = new FileInfo(SubscriptionPolicy.CachePath(_cfg, _proxyName));
            if (!cache.Exists)
            {
                LblSummary.Text = Loc.T("Sub_NotDownloaded");
                LblHint.Visibility = Visibility.Collapsed;
                return;
            }

            var policy = SubscriptionPolicy.For(_cfg, _proxyName);
            var when = cache.LastWriteTime.ToString("dd.MM.yyyy HH:mm");
            if (policy == null)
            {
                // Links only (base64 / Shadowrocket): nothing to choose, the node takes it all.
                LblSummary.Text = Loc.T("Sub_SummaryNoRules", when);
                LblHint.Visibility = Visibility.Collapsed;
                RowsHost.Children.Add(MakeRestRow(SubAction.Vpn, Loc.T("Sub_RestNoRules")));
                return;
            }

            LblSummary.Text = Loc.T("Sub_Summary", policy.NodeCount, policy.Rows.Count(r => r.RelPath != null), when);
            LblHint.Visibility = Visibility.Visible;
            _final = policy.FinalAction;
            foreach (var row in policy.Rows) RowsHost.Children.Add(MakeRow(row));
            RowsHost.Children.Add(MakeRestRow(policy.FinalAction, Loc.T("Sub_RestHint")));
            if (!string.IsNullOrWhiteSpace(TxtFind.Text)) _ = FindAsync();
        }

        // ---------- search through every list ----------

        // One list card on screen, so the search can mark it.
        private sealed class RowView
        {
            public SubRow Row;
            public Border Card;
            public TextBlock HitLine;
        }

        private readonly List<RowView> _views = new List<RowView>();
        private readonly Dictionary<SubRow, List<string>> _cache = new Dictionary<SubRow, List<string>>();
        private SubAction _final = SubAction.Direct;
        private SubQuery _query = SubSearch.Parse("");
        private System.Windows.Threading.DispatcherTimer _findTimer;
        private int _findToken;

        private List<string> Entries(SubRow row)
        {
            lock (_cache)
            {
                List<string> hit;
                if (_cache.TryGetValue(row, out hit)) return hit;
            }
            var items = SubscriptionPolicy.ReadEntries(_cfg, row);
            lock (_cache) _cache[row] = items;
            return items;
        }

        private void OnFindChanged(object sender, RoutedEventArgs e)
        {
            if (_findTimer == null)
            {
                _findTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _findTimer.Tick += (s, a) => { _findTimer.Stop(); _ = FindAsync(); };
            }
            _findTimer.Stop();
            _findTimer.Start();
        }

        private void OnFindKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { _findTimer?.Stop(); _ = FindAsync(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape && TxtFind.Text.Length > 0) { TxtFind.Text = ""; e.Handled = true; }
        }

        private async Task FindAsync()
        {
            int token = ++_findToken;
            var q = SubSearch.Parse(TxtFind.Text);
            _query = q;
            if (q.IsEmpty)
            {
                FindBox.Visibility = Visibility.Collapsed;
                foreach (var v in _views) { v.Card.Opacity = 1; v.HitLine.Visibility = Visibility.Collapsed; }
                return;
            }

            FindBox.Visibility = Visibility.Visible;
            LblFind.Text = Loc.T("Sub_Finding");
            // A site meets the address lists only through its addresses — ask the engine, which
            // is what it does itself before checking those lists (real answers, never fake-ip).
            if (q.Host != null)
            {
                try { q.Resolved = await new MihomoController(_cfg).ResolveAsync(q.Host).ConfigureAwait(true); } catch { }
                if (token != _findToken) return;
            }

            var views = _views.ToList();
            var hits = await Task.Run(() => views.Select(v =>
            {
                var items = Entries(v.Row);
                return items == null ? null : SubSearch.Find(items, q);
            }).ToList()).ConfigureAwait(true);
            if (token != _findToken) return;

            RowView first = null;
            SubHit firstHit = null;
            int lists = 0;
            for (int i = 0; i < views.Count; i++)
            {
                var v = views[i];
                var h = hits[i];
                if (h == null)
                {
                    v.Card.Opacity = 0.45;
                    v.HitLine.Visibility = Visibility.Visible;
                    v.HitLine.Foreground = Theme.TextMuted;
                    v.HitLine.Text = Loc.T("Sub_HitUnreadable");
                    continue;
                }
                if (h.Count == 0)
                {
                    v.Card.Opacity = 0.45;
                    v.HitLine.Visibility = Visibility.Collapsed;
                    continue;
                }
                lists++;
                v.Card.Opacity = 1;
                v.HitLine.Visibility = Visibility.Visible;
                if (h.Catch != null && first == null) { first = v; firstHit = h; }
                bool catches = h.Catch != null;
                v.HitLine.Foreground = catches ? Theme.AccentAmber : Theme.AccentBlue;
                v.HitLine.Text = catches
                    ? Loc.T("Sub_HitCatches", h.Catch) + (h.CatchIp != null ? " " + Loc.T("Sub_HitViaIp", h.CatchIp) : "")
                    : Loc.T("Sub_HitCount", h.Count.ToString("N0"), h.Example);
            }

            // The answer: which list wins (the first that catches — the engine stops there).
            var what = q.Host ?? q.Ip?.ToString();
            if (q.CanCatch)
            {
                if (first != null)
                    LblFind.Text = Loc.T("Sub_FindFirst", what, first.Row.Title ?? Loc.T("Sub_Individual"), ActText(first.Row.Action))
                                 + (firstHit.CatchIp != null ? " " + Loc.T("Sub_FindViaIp", firstHit.CatchIp) : "");
                else
                    LblFind.Text = Loc.T("Sub_FindRest", what, ActText(_final));
                if (q.Host != null && q.Resolved.Count == 0)
                    LblFind.Text += " " + Loc.T("Sub_FindNoIp");
            }
            else
            {
                LblFind.Text = lists == 0 ? Loc.T("Sub_FindNone", q.Text) : Loc.T("Sub_FindLists", q.Text, lists);
            }
        }

        private static string ActText(SubAction a)
        {
            switch (a)
            {
                case SubAction.Vpn: return Loc.T("Sub_ActVpn");
                case SubAction.Block: return Loc.T("Sub_ActBlock");
                case SubAction.Pass: return Loc.T("Sub_ActPass");
                default: return Loc.T("Sub_ActDirect");
            }
        }

        private UIElement MakeRow(SubRow row)
        {
            var title = row.Title ?? Loc.T("Sub_Individual");
            var count = new TextBlock { FontSize = 12, Foreground = Theme.TextMuted, Margin = new Thickness(0, 2, 0, 0), Text = "…" };
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            left.Children.Add(count);
            var hitLine = new TextBlock
            {
                FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };
            left.Children.Add(hitLine);

            var show = new Button { Content = Loc.T("Sub_Show"), FontSize = 12, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(10, 0, 0, 0) };
            show.Click += (s, e) => OpenEntries(row, title);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            var badge = Badge(row.Action);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
            Grid.SetColumn(show, 2);
            grid.Children.Add(show);

            // Counting an 8 MB list takes a moment — off the UI thread, filled in when ready.
            Task.Run(() => Entries(row)?.Count).ContinueWith(t =>
            {
                var n = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                count.Text = n.HasValue ? Loc.T("Sub_Entries", n.Value.ToString("N0"))
                           : row.Format == "mrs" ? Loc.T("Sub_EntriesBinary") : Loc.T("Sub_EntriesMissing");
                show.IsEnabled = n.HasValue;
            }, TaskScheduler.FromCurrentSynchronizationContext());

            var card = Card(grid, Theme.CardBg, Theme.CardBorder);
            _views.Add(new RowView { Row = row, Card = card, HitLine = hitLine });
            return card;
        }

        // «Всё остальное»: where traffic matching no list goes — the subscription's MATCH.
        private UIElement MakeRestRow(SubAction action, string hint)
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = Loc.T("Sub_Rest"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary });
            left.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            var badge = Badge(action);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
            var card = Card(grid, Theme.BannerInfoBg, Theme.BannerInfoBorder);
            card.Margin = new Thickness(0, 6, 0, 8);
            return card;
        }

        private static Border Card(UIElement content, Brush bg, Brush border) => new Border
        {
            Child = content,
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 6),
        };

        private static Border Badge(SubAction action)
        {
            Brush color;
            string text;
            switch (action)
            {
                case SubAction.Vpn: color = Theme.AccentBlue; text = Loc.T("Sub_ActVpn"); break;
                case SubAction.Block: color = Theme.AccentRed; text = Loc.T("Sub_ActBlock"); break;
                case SubAction.Pass: color = Theme.AccentGrey; text = Loc.T("Sub_ActPass"); break;
                default: color = Theme.AccentGreen; text = Loc.T("Sub_ActDirect"); break;
            }
            return new Border
            {
                Child = new TextBlock { Text = text, FontSize = 12, Foreground = color },
                BorderBrush = color,
                BorderThickness = new Thickness(1),
                Background = Theme.BadgeNeutralBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 3, 10, 3),
                MinWidth = 110,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
        }

        // ---------- one list opened ----------

        private async void OpenEntries(SubRow row, string title)
        {
            int token = ++_openToken;
            LblListTitle.Text = title;
            ListBadge.Content = Badge(row.Action);
            TxtSearch.Text = TxtFind.Text ?? "";   // the search over all lists carries on inside one
            EntriesHost.ItemsSource = null;
            LblShown.Text = "…";
            ViewLists.Visibility = Visibility.Collapsed;
            ViewEntries.Visibility = Visibility.Visible;

            var items = await Task.Run(() => Entries(row) ?? new List<string>()).ConfigureAwait(true);
            if (token != _openToken) return;   // another list was opened meanwhile
            _entries = items;
            ApplyFilter();
            TxtSearch.Focus();
        }

        private void OnSearchChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            // Same rules as the search over all lists: a mask, or text plus what catches the site.
            var q = SubSearch.Parse(TxtSearch.Text);
            if (q.Host != null && _query.Host == q.Host) q.Resolved = _query.Resolved;
            var view = q.IsEmpty ? _entries : _entries.Where(x => SubSearch.Matches(x, q)).ToList();
            EntriesHost.ItemsSource = view;
            LblShown.Text = q.IsEmpty
                ? Loc.T("Sub_Entries", _entries.Count.ToString("N0"))
                : Loc.T("Sub_Shown", view.Count.ToString("N0"), _entries.Count.ToString("N0"));
        }

        private void OnBack(object sender, RoutedEventArgs e)
        {
            _openToken++;
            ViewEntries.Visibility = Visibility.Collapsed;
            ViewLists.Visibility = Visibility.Visible;
            _entries = new List<string>();
            EntriesHost.ItemsSource = null;
        }

        // ---------- «Обновить» ----------

        private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            BtnRefresh.IsEnabled = false;
            PanelProgress.Visibility = Visibility.Visible;
            LblStep.Foreground = Theme.TextPrimary;
            Bar.IsIndeterminate = false;
            ShowProgress(new SubProgress { Stage = SubStage.Downloading, EtaSeconds = -1, Total = -1 });

            string why;
            try
            {
                why = await SubscriptionPolicy.RefreshAsync(_cfg, _proxyName, _url,
                    new Progress<SubProgress>(ShowProgress), _cts.Token).ConfigureAwait(true);
                if (why == null && AfterRefresh != null)
                    try { await AfterRefresh().ConfigureAwait(true); } catch { }
            }
            catch (OperationCanceledException) { return; }   // the window is closing
            catch (Exception ex) { why = ex.Message; }
            finally
            {
                _cts = null;
                if (IsLoaded) BtnRefresh.IsEnabled = true;
            }

            Bar.Value = 100;
            LblPercent.Text = "";
            LblBytes.Text = "";
            LblEta.Text = "";
            LblStep.Text = why == null ? Loc.T("Sub_Done") : Loc.T("Sub_Failed", why);
            LblStep.TextTrimming = TextTrimming.None;
            LblStep.TextWrapping = TextWrapping.Wrap;
            LblStep.Foreground = why == null ? Theme.BannerOkText : Theme.AccentRed;
            if (ViewEntries.Visibility != Visibility.Visible) Render();
        }

        private void ShowProgress(SubProgress p)
        {
            Bar.Value = Math.Round(p.Overall * 100);
            LblPercent.Text = (int)Math.Round(p.Overall * 100) + "%";
            LblStep.TextTrimming = TextTrimming.CharacterEllipsis;
            LblStep.TextWrapping = TextWrapping.NoWrap;
            LblStep.Text = p.Stage == SubStage.Checking ? Loc.T("Sub_StepCheck")
                         : p.Stage == SubStage.Done ? Loc.T("Sub_Done")
                         : p.Title == null ? Loc.T("Sub_StepSub")
                         : Loc.T("Sub_StepList", p.Step, p.Steps - 1, p.Title);
            if (p.Stage != SubStage.Downloading) { LblBytes.Text = ""; LblEta.Text = ""; return; }
            LblBytes.Text = p.Bytes <= 0 ? Loc.T("Sub_Connecting")
                          : p.Total > 0 ? Loc.T("Sub_BytesOf", Size(p.Bytes), Size(p.Total), Size((long)p.BytesPerSecond))
                          : Loc.T("Sub_Bytes", Size(p.Bytes), Size((long)p.BytesPerSecond));
            LblEta.Text = p.EtaSeconds >= 0 ? Loc.T("Sub_Eta", Duration(p.EtaSeconds)) : "";
        }

        private static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " " + Loc.T("Unit_B");
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0") + " " + Loc.T("Unit_KB");
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " " + Loc.T("Unit_MB");
        }

        private static string Duration(double seconds)
        {
            var s = (int)Math.Ceiling(seconds);
            return s < 60 ? Loc.T("Sub_Sec", s) : Loc.T("Sub_MinSec", s / 60, s % 60);
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
