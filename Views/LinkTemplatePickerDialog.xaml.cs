using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    public partial class LinkTemplatePickerDialog : FluentWindow
    {
        public BlackMatrixService Selected { get; private set; }

        // Templates already in the rule (by ClashUrl) — hidden so the same one can't be added twice.
        public HashSet<string> ExcludeClashUrls { get; set; }

        // One card: the template plus what the current search found in it.
        public sealed class TplRow
        {
            public BlackMatrixService Svc { get; set; }
            public string Title => Svc.Title;
            public string NameNote => Svc.NameNote.Length > 0 ? "  " + Svc.NameNote : "";
            public string Desc => Svc.Desc;
            public string CategoryText => CategoryName(Svc.Category);
            public Visibility CategoryVisibility => CategoryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            public string HitText { get; set; } = "";
            public Visibility HitVisibility => HitText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private List<BlackMatrixService> _all;
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private CancellationTokenSource _searchCts;

        public LinkTemplatePickerDialog()
        {
            InitializeComponent();
            _debounce.Tick += async (s, e) => { _debounce.Stop(); await SearchAsync(TxtSearch.Text ?? "").ConfigureAwait(true); };
            // Built on load — by then the caller has set ExcludeClashUrls.
            Loaded += async (s, e) =>
            {
                _all = BlackMatrixCatalog.Pickable()
                    .Where(t => ExcludeClashUrls == null || !ExcludeClashUrls.Contains(t.ClashUrl ?? ""))
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                TxtSearch.Focus();
                await SearchAsync("").ConfigureAwait(true);
            };
            Closed += (s, e) => { try { _searchCts?.Cancel(); } catch { } _debounce.Stop(); };
        }

        public static string CategoryName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            var key = "TplCat_" + code.Trim();
            var t = Loc.T(key);
            return string.IsNullOrEmpty(t) || t == key ? code : t;
        }

        private void OnSearchChanged(object sender, RoutedEventArgs e)
        {
            var q = (TxtSearch.Text ?? "").Trim();
            // Words filter at once; a domain or an IP goes to the online index — after a pause.
            _debounce.Stop();
            if (AsAddress(q, out _, out _)) { LblFind.Text = Loc.T("Tpl_Searching", q); LblFind.Visibility = Visibility.Visible; _debounce.Start(); }
            else _ = SearchAsync(q);
        }

        // A query that names a place on the network: an IP, a URL, or a bare host name.
        private static bool AsAddress(string q, out string host, out IPAddress ip)
        {
            host = ""; ip = null;
            q = (q ?? "").Trim();
            if (q.Length == 0 || q.IndexOf(' ') >= 0) return false;
            if ((q.Contains(".") || q.Contains(":")) && IPAddress.TryParse(q, out ip)) return true;
            ip = null;
            var h = q;
            if (h.Contains("://") && Uri.TryCreate(h, UriKind.Absolute, out var u)) h = u.Host;
            else { var slash = h.IndexOf('/'); if (slash > 0) h = h.Substring(0, slash); }
            h = h.Trim('.').ToLowerInvariant();
            if (h.StartsWith("*.")) h = h.Substring(2);
            if (h.IndexOf('.') <= 0 || h.EndsWith(".")) return false;
            foreach (var c in h) if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')) return false;
            host = h;
            return true;
        }

        private async Task SearchAsync(string query)
        {
            if (_all == null) return;
            try { _searchCts?.Cancel(); } catch { }
            var cts = _searchCts = new CancellationTokenSource();
            query = (query ?? "").Trim();

            if (query.Length == 0)
            {
                Show(_all.Select(t => new TplRow { Svc = t }).ToList(), "");
                return;
            }

            if (AsAddress(query, out var host, out var ip))
            {
                string shown = ip != null ? ip.ToString() : host;
                try
                {
                    var hits = ip != null
                        ? await TemplateIndex.FindIpAsync(ip, cts.Token).ConfigureAwait(true)
                        : await TemplateIndex.FindDomainAsync(host, cts.Token).ConfigureAwait(true);
                    if (cts.IsCancellationRequested) return;
                    var byId = _all.Where(t => t.Id.Length > 0).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                    // The narrowest template first: Telegram before Global, which covers half the web.
                    var rows = hits.Where(h => byId.ContainsKey(h.Id))
                        .Select(h => new TplRow { Svc = byId[h.Id], HitText = Loc.T("Tpl_MatchedBy", h.Why) })
                        .OrderBy(r => Size(r.Svc)).ToList();
                    Show(rows, rows.Count > 0
                        ? Loc.T(ip != null ? "Tpl_FoundIp" : "Tpl_FoundDomain", shown, rows.Count)
                        : Loc.T("Tpl_FoundNone", shown));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (cts.IsCancellationRequested) return;
                    // Offline: still offer what the words find, and say why the index didn't answer.
                    Show(TextMatches(query), Loc.T("Tpl_IndexFail", ex.Message));
                }
                return;
            }

            var found = TextMatches(query);
            Show(found, "");

            // The index's category words (RU/EN) find what no title says: «нейросети» → OpenAI.
            if (query.Length < 3 || query.IndexOf(' ') >= 0) return;
            try
            {
                var words = await TemplateIndex.FindWordAsync(query, cts.Token).ConfigureAwait(true);
                if (cts.IsCancellationRequested) return;
                var have = new HashSet<string>(found.Select(r => r.Svc.Id), StringComparer.OrdinalIgnoreCase);
                var byId = _all.Where(t => t.Id.Length > 0).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                var extra = words.Where(h => byId.ContainsKey(h.Id) && !have.Contains(h.Id))
                    .Select(h => new TplRow { Svc = byId[h.Id], HitText = Loc.T("Tpl_MatchedBy", h.Why) })
                    .OrderBy(r => r.Svc.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (extra.Count > 0) Show(found.Concat(extra).ToList(), "");
            }
            catch { /* offline — the local matches above stand */ }
        }

        private static int Size(BlackMatrixService s) => s.Domains + s.Suffixes + s.Keywords + s.Cidr + s.Cidr6 + s.Asn;

        // Every query word somewhere in name, title, description, tags or category; best first:
        // the name/title itself, then tags, then the description.
        private List<TplRow> TextMatches(string query)
        {
            var words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = new List<KeyValuePair<int, TplRow>>();
            foreach (var t in _all)
            {
                var head = ((t.Name ?? "") + " " + t.Id + " " + t.TitleRu + " " + t.TitleEn).ToLowerInvariant();
                var tags = (string.Join(" ", t.Tags) + " " + t.Category + " " + CategoryName(t.Category)).ToLowerInvariant();
                var desc = ((t.DescRu ?? "") + " " + (t.DescEn ?? "")).ToLowerInvariant();
                int rank = 0;
                bool all = true;
                foreach (var w in words)
                {
                    if (head.Contains(w)) continue;
                    if (tags.Contains(w)) { rank = Math.Max(rank, 2); continue; }
                    if (desc.Contains(w)) { rank = Math.Max(rank, 3); continue; }
                    all = false; break;
                }
                if (!all) continue;
                if (rank == 0 && !(t.Name ?? "").StartsWith(words[0], StringComparison.OrdinalIgnoreCase)
                              && !t.Title.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase)) rank = 1;
                rows.Add(new KeyValuePair<int, TplRow>(rank, new TplRow { Svc = t }));
            }
            return rows.OrderBy(r => r.Key).Select(r => r.Value).ToList();
        }

        private void Show(List<TplRow> rows, string note)
        {
            ItemsHost.ItemsSource = rows;
            LblFind.Text = note ?? "";
            LblFind.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            LblCount.Text = (TxtSearch.Text ?? "").Trim().Length > 0
                ? Loc.T("Tpl_FoundText", rows.Count)
                : Loc.T("Tpl_Count", rows.Count);
        }

        private void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TplRow row) Pick(row.Svc);
        }

        private async void OnInfo(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is TplRow row)) return;
            var add = await OverlayDialog.ConfirmContentAsync(this, row.Svc.Title, InfoCard(row.Svc),
                Loc.T("Tpl_Add"), Loc.T("Dlg_Close"), 500).ConfigureAwait(true);
            if (add) Pick(row.Svc);
        }

        // The «?» card: what the service is, what the list holds, how the tile will check it.
        private static UIElement InfoCard(BlackMatrixService s)
        {
            var sp = new StackPanel();
            TextBlock Line(string text, double size, System.Windows.Media.Brush fg, double top)
            {
                var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
                sp.Children.Add(tb);
                return tb;
            }

            var badges = new WrapPanel();
            void Badge(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                badges.Children.Add(new Border
                {
                    Background = Theme.BadgeNeutralBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1),
                    Margin = new Thickness(0, 0, 6, 4),
                    Child = new TextBlock { Text = text, FontSize = 11, Foreground = Theme.BadgeNeutralText },
                });
            }
            Badge(CategoryName(s.Category));
            if (!string.Equals(s.Title, s.Name, StringComparison.OrdinalIgnoreCase)) Badge(s.Name);
            foreach (var t in s.Tags.Take(6)) Badge(t);
            if (badges.Children.Count > 0) sp.Children.Add(badges);

            if (!string.IsNullOrWhiteSpace(s.Desc)) Line(s.Desc, 14, Theme.OverlayBody, 4);

            var stats = new List<string>();
            if (s.Domains + s.Suffixes > 0) stats.Add(Loc.T("Tpl_InfoDomains", s.Domains + s.Suffixes));
            if (s.Keywords > 0) stats.Add(Loc.T("Tpl_InfoKeywords", s.Keywords));
            if (s.Cidr + s.Cidr6 > 0) stats.Add(Loc.T("Tpl_InfoNets", s.Cidr + s.Cidr6));
            if (s.Asn > 0) stats.Add(Loc.T("Tpl_InfoAsn", s.Asn));
            if (stats.Count > 0) Line(string.Join("  ·  ", stats), 12, Theme.TextSecondary, 12);
            if (s.Sample.Count > 0) Line(Loc.T("Tpl_InfoSample", string.Join(", ", s.Sample.Take(8))), 12, Theme.TextSecondary, 4);

            Line(string.IsNullOrWhiteSpace(s.CheckUrl) ? Loc.T("Tpl_InfoNoCheck") : Loc.T("Tpl_InfoCheck", s.CheckUrl),
                 12, Theme.TextSecondary, 4);

            var upd = BlackMatrixCatalog.ParseDate(s.Updated);
            var src = Loc.T(string.Equals(s.Src, "byfox", StringComparison.OrdinalIgnoreCase) ? "Tpl_InfoSrcByfox" : "Tpl_InfoSrcBm7");
            Line((upd != null ? Loc.T("Tpl_InfoUpdated", upd.Value.ToString("dd.MM.yyyy")) + "  ·  " : "") + src,
                 11, Theme.TextMuted, 10);
            return sp;
        }

        private void Pick(BlackMatrixService svc)
        {
            if (svc == null) return;
            Selected = svc;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
