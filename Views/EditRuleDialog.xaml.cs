using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // The unified rule editor: a rule is a name + one table of items (sites + programs) that
    // share one set of VPN settings and one icon. Each item has a "launch from tile" checkbox.
    // Add buttons (URL / Шаблон URL / Приложение / Процесс / Store) sit under the table.
    // Replaces the old separate EditAppDialog / EditLinkDialog.
    public partial class EditRuleDialog : FluentWindow
    {
        public AppEntry Result { get; private set; }

        private readonly AppEntry _editing;
        private readonly ObservableCollection<ItemRow> _items = new ObservableCollection<ItemRow>();
        // Rows stay in the Settings order; each one carries its own rank, and a click renumbers
        // the whole set, so every row has to be able to push its new number to the UI.
        private ObservableCollection<SlotAllowChoice> _allowChoices = new ObservableCollection<SlotAllowChoice>();
        // Variants switched off in Settings: not shown, but carried over on save so switching a
        // variant back on restores what this rule had said about it.
        private List<string> _hiddenDisabledIds = new List<string>();
        private List<string> _hiddenRankedIds = new List<string>();

        public EditRuleDialog(AppEntry existing, List<VpnSlot> slots = null)
        {
            InitializeComponent();
            _editing = existing;
            Title = existing == null ? Loc.T("Rule_TitleAdd") : Loc.T("Rule_TitleEdit");
            Bar.Title = Title;

            ItemsGrid.ItemsSource = _items;
            BuildAllowSlotChoices(slots, existing);

            if (existing != null)
            {
                TxtName.Text = existing.Name;
                TxtCheckUrl.Text = existing.CheckUrl ?? "";
                TxtIcon.Text = existing.IconPath ?? "";
                ChkVpn.IsChecked = existing.RequiresVpn;
                ChkKillSwitch.IsChecked = existing.KillSwitch;
                ChkSkipCheck.IsChecked = existing.SkipCheck;
                ChkAutoIcon.IsChecked = existing.AutoIcon;

                foreach (var p in existing.Programs ?? new List<RuleProgram>())
                    _items.Add(ItemRow.FromProgram(p));
                foreach (var s in existing.Sites ?? new List<RuleSite>())
                    _items.Add(ItemRow.FromSite(s));
            }
            else
            {
                ChkVpn.IsChecked = true;
                ChkKillSwitch.IsChecked = true;
                ChkSkipCheck.IsChecked = true;
                ChkAutoIcon.IsChecked = true;
            }

            AdoptItems();
            _items.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (ItemRow r in e.NewItems) r.Owner = this;
                UpdateEmptyHint();
            };
            UpdateEmptyHint();
            UpdateIconMode();

            TxtName.LostFocus += (s, e) => AutoFillCheckUrl();

            // «Весь трафик» has nothing to list, name or check: what is left of the rule is which
            // VPN it goes through and whether the internet stops when that VPN is down.
            if (existing != null && existing.IsAllTraffic)
            {
                Title = Bar.Title = Loc.T("AllTraffic_Name");
                RowName.Visibility = Visibility.Collapsed;
                RowIcon.Visibility = Visibility.Collapsed;
                RowItems.Visibility = Visibility.Collapsed;
                RowAddButtons.Visibility = Visibility.Collapsed;
                ChkVpn.Visibility = Visibility.Collapsed;
                ChkKillSwitch.Margin = new Thickness(0);
                ChkKillSwitch.ToolTip = Loc.T("AllTraffic_KillTip");
                System.Windows.Controls.Grid.SetRow(RowVpn, 1);
                MinHeight = 300;
                Height = 440;
            }
        }

        // Launch is single-select (radio): ticking one row clears every other. Called from the
        // ItemRow.Launch setter so it fires for both clicks and programmatic changes.
        internal void ClearSiblingLaunch(ItemRow keep)
        {
            foreach (var i in _items)
                if (!ReferenceEquals(i, keep) && i.Launch) i.Launch = false;
        }

        // Wire the back-reference and collapse any pre-existing multi-launch down to the first.
        private void AdoptItems()
        {
            foreach (var i in _items) i.Owner = this;
            bool seen = false;
            foreach (var i in _items)
            {
                if (!i.Launch) continue;
                if (seen) i.Launch = false; else seen = true;
            }
        }

        // Click on empty space (not a row / button / input) drops the table selection and
        // commits + releases focus from any editing URL field.
        private void OnBackgroundClick(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
            ItemsGrid.SelectedItem = null;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
        }

        private static bool IsWithinInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.DataGridRow) return true;
                if (d is System.Windows.Controls.Primitives.DataGridColumnHeader) return true;
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---------- VPN variant allow-list ----------

        private void BuildAllowSlotChoices(List<VpnSlot> slots, AppEntry existing)
        {
            _allowChoices = new ObservableCollection<SlotAllowChoice>();
            _hiddenDisabledIds = new List<string>();
            _hiddenRankedIds = new List<string>();
            var disabled = new HashSet<string>(existing?.DisabledVpnSlotIds ?? new List<string>());
            var order = existing?.VpnSlotOrder ?? new List<string>();
            if (slots != null)
            {
                foreach (var s in slots)
                {
                    if (s == null) continue;
                    if (!s.Enabled)
                    {
                        // Switched off in Settings -> it never takes part in the search, so there's
                        // nothing to allow here. Keep whatever the rule already said about it, so
                        // the choice comes back untouched when the variant is switched on again.
                        if (disabled.Contains(s.Id)) _hiddenDisabledIds.Add(s.Id);
                        if (order.Contains(s.Id)) _hiddenRankedIds.Add(s.Id);
                        continue;
                    }
                    // No "№n" prefix any more: that number is the variant's place in the GLOBAL
                    // Settings list, and here the order is this rule's own — two numberings on
                    // one line contradict each other. The name alone, exactly as it was typed.
                    var name = string.IsNullOrWhiteSpace(s.Name) ? Loc.T("Rule_NoName") : s.Name;
                    _allowChoices.Add(new SlotAllowChoice { Id = s.Id, Label = name });
                }
            }

            // Which variants this rule actually uses, best first: the ones it ranked, then any
            // allowed variant it never ranked (one added to Settings later) behind them. That
            // list IS the numbering — position n becomes the number n + 1 in the box.
            var used = order.Where(id => !disabled.Contains(id)
                                         && _allowChoices.Any(c => c.Id == id)).ToList();
            foreach (var c in _allowChoices)
                if (!disabled.Contains(c.Id) && !used.Contains(c.Id)) used.Add(c.Id);
            Renumber(used);

            AllowSlots.ItemsSource = _allowChoices;
            LblNoAllowSlots.Visibility = _allowChoices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // A click either gives this variant the next free number or takes its number away; the
        // rest renumber so the row always reads 1..N with no gaps. The Tag carries the slot id
        // rather than the row object: DataContext walking is the fragile way to answer "which
        // row was that" when a template can be rebuilt under you.
        private void OnSlotToggle(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;

            var used = _allowChoices.Where(c => c.Rank > 0).OrderBy(c => c.Rank)
                                    .Select(c => c.Id).ToList();
            if (!used.Remove(id)) used.Add(id);
            Renumber(used);
        }

        // `used` is the priority order; everything not in it has no number and takes no part.
        private void Renumber(List<string> used)
        {
            foreach (var c in _allowChoices)
            {
                int i = used.IndexOf(c.Id);
                c.Rank = i < 0 ? 0 : i + 1;
            }
        }

        public class SlotAllowChoice : INotifyPropertyChanged
        {
            public string Id { get; set; }
            public string Label { get; set; }

            private int _rank;   // 0 = not used by this rule at all
            public int Rank
            {
                get { return _rank; }
                set
                {
                    if (_rank == value) return;
                    _rank = value;
                    Raise(nameof(Rank)); Raise(nameof(RankText));
                    Raise(nameof(BoxFill)); Raise(nameof(BoxEdge)); Raise(nameof(BoxTip));
                }
            }

            public string RankText { get { return _rank > 0 ? _rank.ToString() : ""; } }

            // Brushes, not colour strings: a string would only reach Background/BorderBrush
            // through the binding engine’s type converter, and a typo there fails silently at
            // runtime instead of at build time. Frozen so every row can share the one instance.
            private static readonly System.Windows.Media.Brush OnFill = Freeze(0x3C, 0x61, 0x49);
            private static readonly System.Windows.Media.Brush OnEdge = Freeze(0x58, 0x8A, 0x69);
            private static readonly System.Windows.Media.Brush OffEdge = Freeze(0x55, 0x5F, 0x77);

            private static System.Windows.Media.Brush Freeze(byte r, byte g, byte b)
            {
                var br = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(r, g, b));
                br.Freeze();
                return br;
            }

            public System.Windows.Media.Brush BoxFill
            {
                get { return _rank > 0 ? OnFill : System.Windows.Media.Brushes.Transparent; }
            }
            public System.Windows.Media.Brush BoxEdge { get { return _rank > 0 ? OnEdge : OffEdge; } }
            public string BoxTip { get { return Loc.T(_rank > 0 ? "Vpn_CellOnTip" : "Vpn_CellOffTip"); } }

            public event PropertyChangedEventHandler PropertyChanged;
            private void Raise(string n)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new PropertyChangedEventArgs(n));
            }
        }

        // ---------- Unified item row (site OR program) ----------

        public class ItemRow : INotifyPropertyChanged
        {
            public bool IsSite { get; set; }
            // Back-reference so ticking one row's checkbox clears the others (radio behaviour),
            // no matter whether the change came from a click or from code.
            internal EditRuleDialog Owner;
            private bool _launch;
            public bool Launch
            {
                get => _launch;
                set
                {
                    if (_launch == value) return;
                    _launch = value;
                    OnPropertyChanged();
                    if (value) Owner?.ClearSiblingLaunch(this);
                }
            }

            // Site
            private string _url = "";
            public string Url
            {
                get => _url;
                set { _url = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); }
            }
            public string ClashRuleUrl { get; set; } = "";
            public string TemplateName { get; set; } = "";
            public List<string> RuleItems { get; set; } = new List<string>();
            public string RuleFetchedUtc { get; set; } = "";
            private string _templateLabel = "";
            public string TemplateLabel
            {
                get => _templateLabel;
                set { _templateLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(TemplateVisibility)); }
            }

            // Program
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public string Arguments { get; set; } = "";
            public string Aumid { get; set; } = "";

            // A template is a site that carries a Clash rule-list URL; its table cell shows the
            // template path (read-only), a plain site shows an editable URL box.
            public bool IsTemplate => IsSite && !string.IsNullOrWhiteSpace(ClashRuleUrl);
            public Visibility SiteVisibility => (IsSite && !IsTemplate) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility TemplatePathVisibility => IsTemplate ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ProgVisibility => IsSite ? Visibility.Collapsed : Visibility.Visible;
            public Visibility TemplateVisibility => string.IsNullOrEmpty(TemplateLabel) ? Visibility.Collapsed : Visibility.Visible;
            public string TypeLabel => IsTemplate ? Loc.T("Rule_TypeTemplate") : IsSite ? Loc.T("Rule_TypeSite") : (string.IsNullOrEmpty(Aumid) ? Loc.T("Rule_TypeProg") : "Store");
            public Brush TypeChipBg => IsSite ? Theme.BannerInfoBg : (string.IsNullOrEmpty(Aumid) ? Theme.BannerOkBg : Theme.BannerWarnBg);
            public string ProgTitle => string.IsNullOrWhiteSpace(Name)
                ? System.IO.Path.GetFileNameWithoutExtension(Path ?? "") : Name;
            // Program cell of «Что открывается»: just the path (or AUMID for a Store app) — the
            // name now has its own column.
            public string ProgOneLine => string.IsNullOrEmpty(Path) ? Aumid : Path;

            // «Имя» column: template name / site host / exe file name / Store display name.
            public string DisplayName
            {
                get
                {
                    if (IsTemplate) return TemplateName ?? "";
                    if (IsSite)
                    {
                        var u = (Url ?? "").Trim();
                        if (u.Length == 0) return "";
                        Uri uri;
                        if (Uri.TryCreate(u.Contains("://") ? u : "https://" + u, UriKind.Absolute, out uri))
                            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host.Substring(4) : uri.Host;
                        return u;
                    }
                    if (!string.IsNullOrEmpty(Aumid)) return string.IsNullOrWhiteSpace(Name) ? ProgTitle : Name;
                    var file = System.IO.Path.GetFileName(Path ?? "");
                    return string.IsNullOrEmpty(file) ? ProgTitle : file;
                }
            }

            public static ItemRow FromSite(RuleSite s)
            {
                int n = s.RuleItems?.Count ?? 0;
                bool isTemplate = !string.IsNullOrWhiteSpace(s.ClashRuleUrl);
                // Recover the template's display name from the catalog by its rule URL.
                var tplName = isTemplate ? BlackMatrixCatalog.NameForUrl(s.ClashRuleUrl) : "";
                return new ItemRow
                {
                    IsSite = true,
                    Launch = s.Launch,
                    Url = s.Url ?? "",
                    ClashRuleUrl = s.ClashRuleUrl ?? "",
                    TemplateName = tplName,
                    RuleItems = s.RuleItems ?? new List<string>(),
                    RuleFetchedUtc = s.RuleFetchedUtc ?? "",
                    TemplateLabel = isTemplate
                        ? (tplName.Length > 0 ? Loc.T("Rule_TplRanges", tplName, n) : Loc.T("Rule_TplRangesNoName", n))
                        : "",
                };
            }

            public static ItemRow FromProgram(RuleProgram p) => new ItemRow
            {
                IsSite = false, Launch = p.Launch, Name = p.Name ?? "", Path = p.Path ?? "",
                Arguments = p.Arguments ?? "", Aumid = p.Aumid ?? "",
            };

            public RuleSite ToSite() => new RuleSite
            {
                Launch = Launch, Url = (Url ?? "").Trim(), ClashRuleUrl = (ClashRuleUrl ?? "").Trim(),
                RuleItems = RuleItems ?? new List<string>(), RuleFetchedUtc = RuleFetchedUtc ?? "",
            };

            public RuleProgram ToProgram() => new RuleProgram
            {
                Launch = Launch, Name = Name ?? "", Path = (Path ?? "").Trim(),
                Arguments = Arguments ?? "", Aumid = Aumid ?? "",
            };

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        // At least one item should be marked to launch — default the first added one.
        private bool NothingLaunches() => !_items.Any(i => i.Launch);

        private void UpdateEmptyHint()
        {
            if (LblItemsEmpty != null)
                LblItemsEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRemoveItem(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ItemRow row) _items.Remove(row);
        }

        // Click on the «Тип» cell: open what the row points at. Site → its URL, Шаблон → the
        // template path, Программа/Store → reveal the file in Explorer. Handled on preview-down so
        // the DataGrid never selects/highlights the row — only the cursor changes (Hand).
        private void OnTypeClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!((sender as FrameworkElement)?.DataContext is ItemRow row)) return;
            try
            {
                if (row.IsTemplate)
                {
                    if (!string.IsNullOrWhiteSpace(row.ClashRuleUrl)) OpenInBrowser(row.ClashRuleUrl);
                }
                else if (row.IsSite)
                {
                    var url = NormalizeUrl(row.Url);
                    if (!string.IsNullOrWhiteSpace(url)) OpenInBrowser(url);
                }
                else
                {
                    RevealInExplorer(row.Path);
                }
            }
            catch { /* best-effort navigation */ }
        }

        private static void OpenInBrowser(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return;
            if (!u.Contains("://")) u = "https://" + u;
            Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
        }

        private static void RevealInExplorer(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }

        // Tidy a hand-typed URL: trim, strip all inner whitespace, drop a stray leading «@»,
        // and add https:// if no scheme. Returns "" if nothing usable.
        private static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var u = new string((raw ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Trim().TrimStart('@');
            if (u.Length == 0) return "";
            if (!u.Contains("://")) u = "https://" + u;
            return u;
        }

        // True if the string looks like a usable http(s) URL with a host containing a dot.
        private static bool LooksLikeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            return uri.Host.Contains(".");
        }

        // ---------- Add: sites ----------

        private void OnAddSiteUrl(object sender, RoutedEventArgs e)
        {
            AddRow(new ItemRow { IsSite = true, Launch = NothingLaunches(), Url = "" });
        }

        private async void OnAddSiteTemplate(object sender, RoutedEventArgs e)
        {
            var already = new HashSet<string>(
                _items.Where(i => i.IsTemplate).Select(i => i.ClashRuleUrl ?? ""),
                StringComparer.OrdinalIgnoreCase);
            var dlg = new LinkTemplatePickerDialog { Owner = this, ExcludeClashUrls = already };
            if (dlg.ShowDialog() != true || dlg.Selected == null) return;

            var svc = dlg.Selected;
            // Guard against a duplicate slipping through (e.g. stale list).
            if (already.Contains(svc.ClashUrl ?? "")) return;
            if (string.IsNullOrWhiteSpace(TxtName.Text) && !string.IsNullOrWhiteSpace(svc.Name)) TxtName.Text = svc.Name;
            // The template's own check site goes into the «проверка подключения» row — overwritten
            // each time a template is added (the template path itself lives in the table row below).
            if (!string.IsNullOrWhiteSpace(svc.CheckUrl)) TxtCheckUrl.Text = svc.CheckUrl;

            var row = new ItemRow
            {
                IsSite = true,
                // A template has no URL of its own — its tile opens the rule's check address
                // (auto-filled from the catalog just above). Default it on when nothing else
                // launches, so a template-only rule still gets a tile.
                Launch = NothingLaunches(),
                Url = "",
                ClashRuleUrl = svc.ClashUrl ?? "",
                TemplateName = svc.Name ?? "",
                TemplateLabel = Loc.T("Rule_TplLoading", svc.Name),
            };
            AddRow(row);

            try
            {
                var items = await ClashRuleService.FetchRuleItemsAsync(svc.ClashUrl).ConfigureAwait(true);
                row.RuleItems = items;
                row.RuleFetchedUtc = DateTime.UtcNow.ToString("o");
                row.TemplateLabel = Loc.T("Rule_TplRanges", svc.Name, items.Count);
            }
            catch (Exception ex)
            {
                row.TemplateLabel = Loc.T("Rule_TplLoadError", ex.Message);
            }
        }

        // ---------- Add: programs ----------

        private void OnAddProgRunning(object sender, RoutedEventArgs e)
        {
            var dlg = new ProcessPickerDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedPath))
            {
                var name = !string.IsNullOrWhiteSpace(dlg.SelectedName) ? dlg.SelectedName
                    : System.IO.Path.GetFileNameWithoutExtension(dlg.SelectedPath);
                AddProgram(name, dlg.SelectedPath, "");
            }
        }

        private void OnAddProgStore(object sender, RoutedEventArgs e)
        {
            var dlg = new StoreAppPickerDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedAumid))
            {
                var name = !string.IsNullOrWhiteSpace(dlg.SelectedName) ? dlg.SelectedName
                    : System.IO.Path.GetFileNameWithoutExtension(dlg.SelectedExe ?? "");
                AddProgram(name, dlg.SelectedExe ?? "", dlg.SelectedAumid);
            }
        }

        private void OnAddProgPath(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("Rule_PickProgramTitle"),
                Filter = Loc.T("Rule_PickProgramFilter"),
            };
            if (ofd.ShowDialog(this) == true)
                AddProgram(System.IO.Path.GetFileNameWithoutExtension(ofd.FileName), ofd.FileName, "");
        }

        private void AddProgram(string name, string path, string aumid)
        {
            AddRow(new ItemRow { IsSite = false, Launch = NothingLaunches(), Name = name, Path = path, Aumid = aumid });
            // Name the rule after the FILE (chrome, Telegram, …) rather than the picker's
            // friendly label, which for a running process is whatever the window/description
            // says and for a Store app is the display name — neither matches the process the
            // routing rule will key on. Falls back to the picker's name when there's no path
            // (a Store app registered by AUMID only). Never overwrites what's already typed.
            var fromFile = "";
            try { fromFile = System.IO.Path.GetFileNameWithoutExtension(path ?? ""); } catch { }
            var label = !string.IsNullOrWhiteSpace(fromFile) ? fromFile : (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(TxtName.Text) && label.Length > 0) TxtName.Text = label;
            AutoFillCheckUrl(path);
        }

        // Add a row and scroll the table to it so the newest item is always visible.
        private void AddRow(ItemRow row)
        {
            _items.Add(row);
            ItemsGrid.UpdateLayout();
            ItemsGrid.ScrollIntoView(row);
        }

        // ---------- Icon / VPN toggles ----------

        private void OnAutoIconToggled(object sender, RoutedEventArgs e) => UpdateIconMode();

        // Auto on  → the manual «Обзор» + path are dimmed (server icon is used).
        // Auto off → the manual controls are active and the «Иконка с сервера» label dims.
        private void UpdateIconMode()
        {
            bool auto = ChkAutoIcon.IsChecked == true;
            if (IconManualPanel != null)
            {
                IconManualPanel.IsEnabled = !auto;
                IconManualPanel.Opacity = auto ? 0.4 : 1.0;
            }
            if (LblAutoIcon != null) LblAutoIcon.Opacity = auto ? 1.0 : 0.4;
        }

        private void OnBrowseIcon(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("Rule_PickIconTitle"),
                Filter = Loc.T("Rule_PickIconFilter"),
            };
            if (ofd.ShowDialog(this) == true) TxtIcon.Text = ofd.FileName;
        }

        private void AutoFillCheckUrl(string pathHint = null)
        {
            if (!string.IsNullOrWhiteSpace(TxtCheckUrl.Text)) return;
            var guess = KnownServices.GuessCheckUrl(TxtName.Text, pathHint ?? "");
            if (!string.IsNullOrEmpty(guess)) TxtCheckUrl.Text = guess;
        }

        // ---------- Save / Cancel ----------

        // Guards against re-entrant saves: OnSave awaits a (possibly slow) icon download, during
        // which the modal window stays responsive — without this flag, extra Save clicks queue
        // more OnSave runs; the first one closes the window and the rest crash setting
        // DialogResult on a no-longer-modal window.
        private bool _saving;

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            if (_saving) return;
            _saving = true;
            BtnSave.IsEnabled = false;
            try
            {
                if (_editing != null && _editing.IsAllTraffic)
                {
                    // Everything but the two visible settings is carried over untouched
                    // (AppConfigStore.EnsureAllTraffic pins the rest anyway).
                    Result = new AppEntry
                    {
                        Id = _editing.Id,
                        Name = _editing.Name,
                        IsAllTraffic = true,
                        RequiresVpn = true,
                        AutoConnect = _editing.AutoConnect,
                        SkipCheck = _editing.SkipCheck,
                        AutoIcon = false,
                        IconPath = _editing.IconPath,
                        KillSwitch = ChkKillSwitch.IsChecked == true,
                        DisabledVpnSlotIds = _allowChoices.Where(c => c.Rank <= 0).Select(c => c.Id)
                            .Concat(_hiddenDisabledIds).ToList(),
                        VpnSlotOrder = _allowChoices.Where(c => c.Rank > 0).OrderBy(c => c.Rank)
                            .Select(c => c.Id).Concat(_hiddenRankedIds).ToList(),
                    };
                    try { DialogResult = true; } catch (InvalidOperationException) { }
                    Close();
                    return;
                }

                AutoFillCheckUrl();
                var name = (TxtName.Text ?? "").Trim();
                if (name.Length == 0)
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_NotFilled"), Loc.T("Rule_NeedName")).ConfigureAwait(true);
                    TxtName.Focus();
                    return;
                }

                // Collect sites: templates kept as-is (routed via their rule list); hand-typed sites
                // get whitespace stripped, https:// added, and are validated before saving.
                var sites = new List<RuleSite>();
                var badUrls = new List<string>();
                foreach (var i in _items.Where(i => i.IsSite))
                {
                    var s = i.ToSite();
                    if (i.IsTemplate) { sites.Add(s); continue; }
                    var url = NormalizeUrl(s.Url);
                    if (url.Length == 0) continue;                 // empty plain site → drop
                    if (!LooksLikeUrl(url)) { badUrls.Add(s.Url); continue; }
                    s.Url = url;
                    sites.Add(s);
                }
                if (badUrls.Count > 0)
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_BadUrlTitle"),
                        Loc.T("Rule_BadUrlList", string.Join("\n• ", badUrls))).ConfigureAwait(true);
                    return;
                }

                var progs = _items.Where(i => !i.IsSite).Select(i => i.ToProgram())
                                  .Where(p => !string.IsNullOrWhiteSpace(p.Path) || !string.IsNullOrWhiteSpace(p.Aumid)).ToList();
                if (sites.Count == 0 && progs.Count == 0)
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_EmptyTitle"), Loc.T("Rule_NeedOneItem")).ConfigureAwait(true);
                    return;
                }

                // Normalize the connection-check address too (whitespace + scheme), validate if set.
                var checkUrl = NormalizeUrl(TxtCheckUrl.Text);
                if (checkUrl.Length > 0 && !LooksLikeUrl(checkUrl))
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_BadUrlTitle"),
                        Loc.T("Rule_BadCheckUrl")).ConfigureAwait(true);
                    TxtCheckUrl.Focus();
                    return;
                }

                // A template carries only a rule list — there's no address to open. Its tile
                // falls back to the rule's check address (AppLauncher does the same), so that
                // one has to exist before «Плитка» on a template means anything.
                bool tileOnTemplate = sites.Any(s => s.Launch && s.HasRuleList && string.IsNullOrWhiteSpace(s.Url));
                if (tileOnTemplate && checkUrl.Length == 0)
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_NotFilled"),
                        Loc.T("Rule_TplNeedCheckUrl")).ConfigureAwait(true);
                    TxtCheckUrl.Focus();
                    return;
                }

                var entry = _editing != null ? new AppEntry { Id = _editing.Id } : new AppEntry();
                entry.Name = name;
                entry.Sites = sites;
                entry.Programs = progs;
                entry.CheckUrl = checkUrl;
                entry.RequiresVpn = ChkVpn.IsChecked == true;
                entry.KillSwitch = ChkKillSwitch.IsChecked == true;
                entry.SkipCheck = ChkSkipCheck.IsChecked == true;
                entry.AutoIcon = ChkAutoIcon.IsChecked == true;
                // A variant with no number takes no part in this rule, which is exactly what the
                // exclusion list has always meant; the numbered ones, in their order, are the
                // priority. Variants with no row here (switched off in Settings) keep whatever
                // this rule said about them before.
                entry.DisabledVpnSlotIds = _allowChoices.Where(c => c.Rank <= 0).Select(c => c.Id)
                    .Concat(_hiddenDisabledIds).ToList();
                entry.VpnSlotOrder = _allowChoices.Where(c => c.Rank > 0).OrderBy(c => c.Rank)
                    .Select(c => c.Id).Concat(_hiddenRankedIds).ToList();

                await ApplyIconAsync(entry, name).ConfigureAwait(true);

                // The window may have been closed during the icon fetch — DialogResult on a
                // no-longer-modal window throws, so bail / guard.
                if (!IsLoaded) return;
                Result = entry;
                try { DialogResult = true; } catch (InvalidOperationException) { }
                Close();
            }
            finally
            {
                _saving = false;
                if (IsLoaded) BtnSave.IsEnabled = true;
            }
        }

        // Auto mode: download by name into Data\ico (force-refresh). Manual: copy the chosen
        // file into the icon store. Mirrors the old per-app/per-link behaviour.
        private async Task ApplyIconAsync(AppEntry entry, string name)
        {
            if (entry.AutoIcon)
            {
                // The server icon is picked by the slug (name / program / site). Same slug as
                // before → the server would answer the same as last time, so don't make Save
                // wait for it (it hung up to 8 s on every click, longest for rules the server
                // has no icon for). Startup's EnsureAutoIcons still fills in a missing one.
                if (_editing != null && _editing.AutoIcon
                    && string.Equals(IconFetcher.SlugFor(_editing), IconFetcher.SlugFor(entry), StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(_editing.IconPath) || System.IO.File.Exists(_editing.IconPath)))
                {
                    entry.IconPath = _editing.IconPath ?? "";
                    return;
                }
                string fetched = "";
                // Cap the save-time download so a slow/unreachable server can't freeze Save for
                // the full HttpClient timeout (15s).
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
                        fetched = await IconFetcher.TryFetchAsync(entry, force: true, cts.Token).ConfigureAwait(true);
                }
                catch { }
                entry.IconPath = fetched ?? "";
                return;
            }

            var iconText = (TxtIcon.Text ?? "").Trim();
            if (string.IsNullOrEmpty(iconText))
                entry.IconPath = "";
            else if (_editing != null && string.Equals(iconText, _editing.IconPath, StringComparison.OrdinalIgnoreCase))
                entry.IconPath = _editing.IconPath;
            else
            {
                var copied = AppConfigStore.CopyIconToStore(iconText);
                entry.IconPath = string.IsNullOrEmpty(copied) ? iconText : copied;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
