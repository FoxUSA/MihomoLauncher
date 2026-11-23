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
        private readonly SaveGuard _saveGuard;
        private readonly ObservableCollection<ItemRow> _items = new ObservableCollection<ItemRow>();
        // Rows stay in the Settings order; each one carries its own rank, and a click renumbers
        // the whole set, so every row has to be able to push its new number to the UI.
        private ObservableCollection<SlotAllowChoice> _allowChoices = new ObservableCollection<SlotAllowChoice>();
        // (The lists of configurations "switched off in Settings" are gone with that switch,
        // 2026-11-23: every configuration has its row here.)

        // The rule's own address, never checked and not shown (AppEntry.HomeUrl): carried over as it
        // was, and set to a template's site when a template is added — the job the check field did.
        private string _homeUrl = "";

        public EditRuleDialog(AppEntry existing, List<VpnSlot> slots = null)
        {
            InitializeComponent();
            _editing = existing;
            Title = existing == null ? Loc.T("Rule_TitleAdd") : Loc.T("Rule_TitleEdit");
            Bar.Title = Title;

            ItemsGrid.ItemsSource = _items;
            SetupAutoRows();
            BuildAllowSlotChoices(slots, existing);

            if (existing != null)
            {
                TxtName.Text = existing.Name;
                // The field shows what is CHECKED (2026-11-23: filled = checked). The loader keeps
                // SkipCheck equal to "no check address"; should an entry still hold an address under
                // SkipCheck, it is shown empty and kept as the rule's own address, not checked.
                bool unchecked_ = existing.SkipCheck && !string.IsNullOrWhiteSpace(existing.CheckUrl);
                TxtCheckUrl.Text = unchecked_ ? "" : (existing.CheckUrl ?? "");
                _homeUrl = unchecked_ ? existing.CheckUrl.Trim() : (existing.HomeUrl ?? "");
                // Empty field = the server's icon (2026-11-23, the switch is gone): a server icon's
                // cached file is not shown as if the person had picked it.
                TxtIcon.Text = existing.AutoIcon ? "" : (existing.IconPath ?? "");
                ChkKillSwitch.IsChecked = existing.KillSwitch;
                if (existing.LockNic)
                {
                    _lockCard = NetCards.Clean(existing.Interfaces).FirstOrDefault();
                    _lockQuiet = true;
                    ChkLockNic.IsChecked = true;
                    _lockQuiet = false;
                    TxtLockNic.Text = _lockCard ?? "";
                }
                ChkAutoTrack.IsChecked = existing.AutoTrack;

                // What the person added first, what auto-tracking added last (its own section).
                foreach (var p in (existing.Programs ?? new List<RuleProgram>()).Where(p => p != null && !p.Auto))
                    _items.Add(ItemRow.FromProgram(p));
                foreach (var f in existing.Folders ?? new List<RuleFolder>())
                    if (f != null) _items.Add(ItemRow.FromFolder(f));
                foreach (var s in existing.Sites ?? new List<RuleSite>())
                    _items.Add(ItemRow.FromSite(s));
                foreach (var p in (existing.Programs ?? new List<RuleProgram>()).Where(p => p != null && p.Auto))
                    _items.Add(ItemRow.FromProgram(p));
                // Third section: what this rule refuses to auto-add (AppEntry.AutoIgnored).
                foreach (var ban in existing.AutoIgnored ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(ban)) _items.Add(ItemRow.FromBan(ban));
            }
            else
            {
                ChkKillSwitch.IsChecked = true;
                ChkAutoTrack.IsChecked = true;
            }

            AdoptItems();
            _items.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (ItemRow r in e.NewItems) r.Owner = this;
                UpdateEmptyHint();
            };
            UpdateEmptyHint();

            // «Сохранить» only when something differs: the table rows, the VPN ranks + the fields.
            _saveGuard = SaveGuard.Attach(this, BtnSave, () =>
                SaveGuard.Join(_items, o =>
                {
                    var r = (ItemRow)o;
                    return (r.IsSite ? "S|" : r.IsFolder ? "F|" : "P|") + r.Url + "|" + r.ClashRuleUrl + "|" + r.Name + "|" + r.Path
                         + "|" + r.Arguments + "|" + r.Aumid + "|" + (r.Launch ? "1" : "0") + (r.IsAuto ? "|A" : "") + (r.IsIgnored ? "|I" : "");
                })
                + "#" + SaveGuard.Join(_allowChoices, o => ((SlotAllowChoice)o).Id + ":" + ((SlotAllowChoice)o).Rank)
                + "#" + _homeUrl,
                // A new rule: on as soon as it has a name or anything in the table.
                existing == null
                    ? (Func<bool>)(() => !string.IsNullOrWhiteSpace(TxtName.Text) || _items.Count > 0)
                    : null);

            // «Весь трафик» has nothing to list, name or check: what is left of the rule is which
            // VPN it goes through and whether the internet stops when that VPN is down.
            if (existing != null && existing.IsAllTraffic)
            {
                Title = Bar.Title = existing.Name;
                RowName.Visibility = Visibility.Collapsed;
                RowIcon.Visibility = Visibility.Collapsed;
                RowItems.Visibility = Visibility.Collapsed;
                RowAddButtons.Visibility = Visibility.Collapsed;
                ChkAutoTrack.Visibility = Visibility.Collapsed;   // it has no programs to follow
                ChkKillSwitch.ToolTip = Loc.T("AllTraffic_KillTip");
                System.Windows.Controls.Grid.SetRow(RowVpn, 1);
                MinHeight = 300;
                Height = 440;
            }
        }

        // «Привязка к сетевой карте»: the card the tile goes through at the moment it is switched on.
        private string _lockCard;
        private bool _lockQuiet;

        private async void OnLockNicChecked(object sender, RoutedEventArgs e)
        {
            if (_lockQuiet) return;
            // The rule's cards as they are now (a new rule has none: Windows' card).
            var card = NetCards.CurrentOf(_editing ?? new AppEntry());
            if (card == null)
            {
                _lockQuiet = true;
                ChkLockNic.IsChecked = false;
                _lockQuiet = false;
                await OverlayDialog.MessageAsync(this, Loc.T("Rule_LockNicNoneTitle"), Loc.T("Rule_LockNicNone")).ConfigureAwait(true);
                return;
            }
            _lockCard = card;
            TxtLockNic.Text = card;
        }

        private void OnLockNicUnchecked(object sender, RoutedEventArgs e)
        {
            if (_lockQuiet) return;
            _lockCard = null;
            TxtLockNic.Text = "";
        }

        // The tile's cards as saved: pinned → just that one card; otherwise as they were.
        private List<string> SavedInterfaces()
        {
            if (ChkLockNic.IsChecked == true && !string.IsNullOrEmpty(_lockCard)) return new List<string> { _lockCard };
            return new List<string>(_editing?.Interfaces ?? new List<string>());
        }

        // Launch is single-select (radio): ticking one row clears every other. Called from the
        // ItemRow.Launch setter so it fires for both clicks and programmatic changes.
        internal void ClearSiblingLaunch(ItemRow keep)
        {
            foreach (var i in _items)
                if (!ReferenceEquals(i, keep) && i.Launch) i.Launch = false;
        }

        // Wire the back-reference and collapse any pre-existing multi-launch down to the first.
        // None ticked is left as it is: the person took the tick off by hand to hide the tile.
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

        // Deleting the ticked row must not hide the tile on its own (2026-11-16: «мы не скрываем
        // авто плитки» — only an unticked box, by hand, hides it): the tick moves to what is left,
        // program > address > template, the first of the best kind.
        private void GiveTileToBest()
        {
            var best = _items.Where(i => !i.IsAuto && !i.IsIgnored).OrderByDescending(i => i.TileRank).FirstOrDefault();
            if (best != null) best.Launch = true;
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
            var disabled = new HashSet<string>(existing?.DisabledVpnSlotIds ?? new List<string>());
            var order = existing?.VpnSlotOrder ?? new List<string>();
            if (slots != null)
            {
                foreach (var s in slots)
                {
                    // Every configuration has its row (2026-11-23: none is switched off any more).
                    if (s == null) continue;
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
            BtnAllSlots.Visibility = _allowChoices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateAllSlotsButton();
        }

        // «Отметить все» / «Снять все» (2026-11-23): nothing numbered → number every configuration
        // 1..N in the order they are shown; anything numbered → clear every number. The same model
        // as a click on a square (Renumber over the "used" list), so the rest of the editor — the
        // save, the «Сохранить» guard — sees exactly what N clicks would have made.
        private void OnAllSlots(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            bool any = _allowChoices.Any(c => c.Rank > 0);
            Renumber(any ? new List<string>() : _allowChoices.Select(c => c.Id).ToList());
        }

        // The button says what a click would do; called from Renumber, so it follows every change.
        private void UpdateAllSlotsButton()
        {
            if (BtnAllSlots == null) return;
            BtnAllSlots.SetResourceReference(System.Windows.Controls.ContentControl.ContentProperty,
                _allowChoices.Any(c => c.Rank > 0) ? "Rule_SlotsNone" : "Rule_SlotsAll");
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
            UpdateAllSlotsButton();
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
            // runtime instead of at build time. The same squares as the routing matrix (Theme).
            private static readonly System.Windows.Media.Brush OnFill = Theme.RankOnBg;
            private static readonly System.Windows.Media.Brush OnEdge = Theme.RankOnBorder;
            private static readonly System.Windows.Media.Brush OffEdge = Theme.RankOffBorder;

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

            // A folder entry (2026-11-22): one row for the whole folder, Path = the folder.
            public bool IsFolder { get; set; }
            // Added by auto-tracking — lives in the second section; why it was added.
            private bool _isAuto;
            public bool IsAuto
            {
                get => _isAuto;
                set
                {
                    if (_isAuto == value) return;
                    _isAuto = value;
                    OnPropertyChanged(); OnPropertyChanged(nameof(Section)); OnPropertyChanged(nameof(LaunchVisibility));
                    OnPropertyChanged(nameof(NameTip)); OnPropertyChanged(nameof(PathTip));
                }
            }
            public string AutoFrom { get; set; } = "";
            // The editor's two sections (the grid groups by this): "manual" / "auto".
            public string Section => IsIgnored ? "ignored" : IsAuto ? "auto" : "manual";

            // Third section (2026-11-22): a file this rule refuses to auto-add — not an entry of the
            // rule, no routing line; muted, no «Плитка», not draggable. ✕ lifts the ban.
            public bool IsIgnored { get; set; }
            // What AppEntry.AutoIgnored holds for it: the full path, or a bare exe name (older configs).
            public string BanKey { get; set; } = "";

            // Tooltips: an auto row says why it is there and what can be done with it.
            public string AutoTip
            {
                get
                {
                    var from = AutoFrom ?? "";
                    return from.StartsWith("folder:", StringComparison.OrdinalIgnoreCase)
                        ? Loc.T("Rule_AutoFromDir", from.Substring(7))
                        : Loc.T("Rule_AutoFromChild", from.Length > 0 ? from : "?");
                }
            }
            public string NameTip => IsIgnored ? Loc.T("Rule_IgnoredTip") : IsAuto ? AutoTip : IsFolder ? Loc.T("Rule_FolderTip") : DisplayName;
            public string PathTip => IsIgnored ? Loc.T("Rule_IgnoredTip") : IsAuto ? AutoTip : ProgTitle;

            // A template is a site that carries a Clash rule-list URL; its table cell shows the
            // template path (read-only), a plain site shows an editable URL box.
            public bool IsTemplate => IsSite && !string.IsNullOrWhiteSpace(ClashRuleUrl);
            // «Плитка» on any row again (2026-11-13): a rule of nothing but templates had no tile at
            // all. A template's tile opens the rule's check address (AppLauncher.LaunchUrl).
            // Not on an auto row: a helper is started by its own program, never from the tile.
            // Auto rows have it too (2026-11-22) and stay auto when ticked.
            public Visibility LaunchVisibility => IsIgnored ? Visibility.Collapsed : Visibility.Visible;
            // Who gets the tile automatically when a row is added: program / folder > address >
            // template; an auto row never.
            internal int TileRank => IsAuto || IsIgnored ? 0 : !IsSite ? 3 : IsTemplate ? 1 : 2;
            public Visibility SiteVisibility => (IsSite && !IsTemplate) ? Visibility.Visible : Visibility.Collapsed;
            public Visibility TemplatePathVisibility => IsTemplate ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ProgVisibility => IsSite ? Visibility.Collapsed : Visibility.Visible;
            public Visibility TemplateVisibility => string.IsNullOrEmpty(TemplateLabel) ? Visibility.Collapsed : Visibility.Visible;
            public string TypeLabel => IsTemplate ? Loc.T("Rule_TypeTemplate") : IsSite ? Loc.T("Rule_TypeSite")
                : IsFolder ? Loc.T("Rule_TypeFolder") : (string.IsNullOrEmpty(Aumid) ? Loc.T("Rule_TypeProg") : "Store");
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
                    if (IsFolder)
                    {
                        if (!string.IsNullOrWhiteSpace(Name)) return Name;
                        var trimmed = (Path ?? "").TrimEnd('\\', '/');
                        var leaf = System.IO.Path.GetFileName(trimmed);
                        return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
                    }
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
                IsAuto = p.Auto, AutoFrom = p.AutoFrom ?? "", IsAutoNew = p.Auto && p.AutoNew,
            };

            // Added by auto-tracking and not seen yet: outlined in amber this one time (the flag is
            // never written back — ToProgram leaves AutoNew off, a cancel clears it in MainWindow).
            public bool IsAutoNew { get; set; }

            // A ban: a full path (shown with it) or an old bare exe name (shown without a path).
            public static ItemRow FromBan(string ban)
            {
                var b = (ban ?? "").Trim();
                bool isPath = b.IndexOf('\\') >= 0 || b.IndexOf('/') >= 0;
                string name = b;
                if (isPath) { try { name = System.IO.Path.GetFileName(b); } catch { name = b; } }
                return new ItemRow { IsSite = false, IsIgnored = true, BanKey = b, Name = name, Path = isPath ? b : "" };
            }

            public static ItemRow FromFolder(RuleFolder f) => new ItemRow
            {
                IsSite = false, IsFolder = true, Launch = f.Launch, Name = f.Name ?? "", Path = f.Path ?? "",
            };

            public RuleFolder ToFolder() => new RuleFolder
            {
                Launch = Launch, Name = Name ?? "", Path = MihomoConfigBuilder.NormalizeFolder(Path),
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
                Auto = IsAuto, AutoFrom = IsAuto ? (AutoFrom ?? "") : "",
            };

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        // Should a newly added row take the tile? Yes when nothing has it yet, or when the row
        // outranks the one that has it (program > address > template). Equal rank never steals,
        // so the switch happens once per kind: the first program takes the tile from an address
        // or a template, the first address takes it from a template; later ones leave it be, and
        // a tick moved by hand stays where it was put as long as nothing higher is added.
        private bool TakesTile(ItemRow row)
        {
            var cur = _items.FirstOrDefault(i => i.Launch);
            return cur == null || row.TileRank > cur.TileRank;
        }

        private void UpdateEmptyHint()
        {
            if (LblItemsEmpty != null)
                LblItemsEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRemoveItem(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ItemRow row)) return;
            // ✕ on a row of the third section lifts the ban: the file may be auto-added again.
            // ✕ on an auto row moves it there at once: no longer an entry of the rule, a ban
            // (AppEntry.AutoIgnored) — never auto-added to THIS rule again.
            if (row.IsAuto) { IgnoreAuto(row); return; }
            _items.Remove(row);
            // The ticked row deleted: the tick moves to what is left, the tile stays.
            if (row.Launch && !_items.Any(i => i.Launch)) GiveTileToBest();
        }

        // Click on the «Тип» cell: open what the row points at. Site → its URL, Шаблон → the
        // template path, Программа/Store → reveal the file in Explorer. Handled on preview-down so
        // the DataGrid never selects/highlights the row — only the cursor changes (Hand).
        private void OnTypeClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!((sender as FrameworkElement)?.DataContext is ItemRow row)) return;
            // An auto row can be dragged from here: it opens on release, if it was not dragged.
            if (row.IsAuto) { _openOnUp = row; return; }
            if (row.IsIgnored) return;   // a ban: nothing to open, only its ✕
            OpenRow(row);
        }

        private void OpenRow(ItemRow row)
        {
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
            AddRow(new ItemRow { IsSite = true, Url = "" });
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
            // The template's own site becomes the rule's own address (HomeUrl): routed with the rule
            // and what the tile opens — overwritten each time a template is added, as the check
            // field was. NOT the check field any more (2026-11-23: nothing is put there by itself —
            // a filled field means "checked").
            if (!string.IsNullOrWhiteSpace(svc.CheckUrl)) _homeUrl = svc.CheckUrl.Trim();

            var row = new ItemRow
            {
                IsSite = true,
                Url = "",
                ClashRuleUrl = svc.ClashUrl ?? "",
                TemplateName = svc.Name ?? "",
                TemplateLabel = Loc.T("Rule_TplLoading", svc.Name),
            };
            AddRow(row);

            try
            {
                // Started off the UI thread: .NET Framework resolves DNS synchronously on the thread
                // that begins the request — a cold lookup froze the whole editor for seconds.
                var clashUrl = svc.ClashUrl;
                var items = await Task.Run(() => ClashRuleService.FetchRuleItemsAsync(clashUrl)).ConfigureAwait(true);
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

        // Paths / AUMIDs of the programs already in this rule — the pickers leave those out.
        private HashSet<string> ProgramSet(Func<ItemRow, string> pick)
        {
            return new HashSet<string>(
                _items.Where(i => !i.IsSite && !i.IsFolder && !i.IsIgnored).Select(i => (pick(i) ?? "").Trim()).Where(v => v.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        private void OnAddProgRunning(object sender, RoutedEventArgs e)
        {
            var dlg = new ProcessPickerDialog { Owner = this, ExcludePaths = ProgramSet(i => i.Path) };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedPath))
            {
                var name = !string.IsNullOrWhiteSpace(dlg.SelectedName) ? dlg.SelectedName
                    : System.IO.Path.GetFileNameWithoutExtension(dlg.SelectedPath);
                AddProgram(name, dlg.SelectedPath, "");
            }
        }

        private void OnAddProgStore(object sender, RoutedEventArgs e)
        {
            var dlg = new StoreAppPickerDialog
            {
                Owner = this,
                ExcludeAumids = ProgramSet(i => i.Aumid),
                ExcludePaths = ProgramSet(i => i.Path),
            };
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

        private async void AddProgram(string name, string path, string aumid)
        {
            // The same program twice in one rule does nothing but clutter the table: same Store
            // app (AUMID) or same exe path — say so and add nothing (2026-11-14).
            var dup = _items.FirstOrDefault(i => !i.IsSite && !i.IsFolder && !i.IsIgnored && (
                (!string.IsNullOrWhiteSpace(aumid) && string.Equals((i.Aumid ?? "").Trim(), aumid.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(path) && string.Equals((i.Path ?? "").Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase))));
            // Picked by hand what auto-tracking had already added: it simply becomes a manual entry.
            if (dup != null && dup.IsAuto)
            {
                MakeManual(dup, _items.Count(i => !i.IsAuto && !i.IsIgnored));
                return;
            }
            if (dup != null)
            {
                ItemsGrid.ScrollIntoView(dup);
                ItemsGrid.SelectedItem = dup;
                var shown = !string.IsNullOrWhiteSpace(name) ? name.Trim() : System.IO.Path.GetFileName(path ?? "");
                await OverlayDialog.MessageAsync(this, Loc.T("Rule_DupTitle"), Loc.T("Rule_DupProgram", shown)).ConfigureAwait(true);
                return;
            }
            AddRow(new ItemRow { IsSite = false, Name = name, Path = path, Aumid = aumid });
            // Name the rule after the FILE (chrome, Telegram, …) rather than the picker's
            // friendly label, which for a running process is whatever the window/description
            // says and for a Store app is the display name — neither matches the process the
            // routing rule will key on. Falls back to the picker's name when there's no path
            // (a Store app registered by AUMID only). Never overwrites what's already typed.
            var fromFile = "";
            try { fromFile = System.IO.Path.GetFileNameWithoutExtension(path ?? ""); } catch { }
            var label = !string.IsNullOrWhiteSpace(fromFile) ? fromFile : (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(TxtName.Text) && label.Length > 0) TxtName.Text = label;
            // No check address guessed from the program any more (2026-11-23): filled = checked.
        }

        // Add a row and scroll the table to it so the newest item is always visible.
        private void AddRow(ItemRow row)
        {
            bool take = TakesTile(row);
            row.Owner = this;
            // Above the auto section: what the person adds is always in the first one.
            int firstAuto = _items.IndexOf(_items.FirstOrDefault(i => i.IsAuto || i.IsIgnored));
            if (firstAuto >= 0 && !row.IsAuto) _items.Insert(firstAuto, row);
            else _items.Add(row);
            if (take) row.Launch = true;   // the setter clears the previous holder
            ItemsGrid.UpdateLayout();
            ItemsGrid.ScrollIntoView(row);
        }

        // ---------- Icon ----------

        private void OnBrowseIcon(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("Rule_PickIconTitle"),
                Filter = Loc.T("Rule_PickIconFilter"),
            };
            if (ofd.ShowDialog(this) == true) TxtIcon.Text = ofd.FileName;
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
            _saveGuard.Suspended = true;
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
                        // As it is: «Напрямую» on the plate sets it false (2026-11-16) — saving the
                        // card must not quietly put the tile back on a VPN.
                        RequiresVpn = _editing.RequiresVpn,
                        KillSwitchBeforeDirect = _editing.KillSwitchBeforeDirect,
                        AutoConnect = _editing.AutoConnect,
                        SkipCheck = _editing.SkipCheck,
                        CheckUrl = _editing.CheckUrl ?? "",
                        HomeUrl = _editing.HomeUrl ?? "",
                        AutoIcon = false,
                        Block = _editing.Block,
                        IconPath = _editing.IconPath,
                        KillSwitch = ChkKillSwitch.IsChecked == true,
                        LockNic = ChkLockNic.IsChecked == true && !string.IsNullOrEmpty(_lockCard),
                        Interfaces = SavedInterfaces(),
                        DisabledVpnSlotIds = _allowChoices.Where(c => c.Rank <= 0).Select(c => c.Id).ToList(),
                        VpnSlotOrder = _allowChoices.Where(c => c.Rank > 0).OrderBy(c => c.Rank)
                            .Select(c => c.Id).ToList(),
                    };
                    try { DialogResult = true; } catch (InvalidOperationException) { }
                    Close();
                    return;
                }

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

                // Manual ones first, auto ones after — the order the editor shows them in.
                var progs = _items.Where(i => !i.IsSite && !i.IsFolder && !i.IsIgnored).OrderBy(i => i.IsAuto ? 1 : 0).Select(i => i.ToProgram())
                                  .Where(p => !string.IsNullOrWhiteSpace(p.Path) || !string.IsNullOrWhiteSpace(p.Aumid)).ToList();
                var folders = _items.Where(i => i.IsFolder).Select(i => i.ToFolder())
                                    .Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
                if (sites.Count == 0 && progs.Count == 0 && folders.Count == 0)
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
                // falls back to the rule's own address: the check address, else the site the
                // template brought (HomeUrl; AppLauncher does the same), so one of them has to
                // exist before «Плитка» on a template means anything.
                bool tileOnTemplate = sites.Any(s => s.Launch && s.HasRuleList && string.IsNullOrWhiteSpace(s.Url));
                if (tileOnTemplate && checkUrl.Length == 0 && string.IsNullOrWhiteSpace(_homeUrl))
                {
                    await OverlayDialog.MessageAsync(this, Loc.T("Rule_NotFilled"),
                        Loc.T("Rule_TplNeedCheckUrl")).ConfigureAwait(true);
                    TxtCheckUrl.Focus();
                    return;
                }

                var entry = _editing != null ? new AppEntry { Id = _editing.Id } : new AppEntry();
                entry.TabId = _editing?.TabId ?? "";   // the tab is chosen on the main window, kept here
                entry.Name = name;
                entry.Sites = sites;
                entry.Programs = progs;
                entry.Folders = folders;
                entry.AutoTrack = ChkAutoTrack.IsChecked == true;
                // The third section, as it stands (paths, or old bare names). A file the person put
                // back by hand is no longer "removed".
                entry.AutoIgnored = _items.Where(i => i.IsIgnored).Select(i => (i.BanKey ?? "").Trim())
                    .Where(b => b.Length > 0)
                    .Where(b => !progs.Any(p => AutoPrograms.IsBanned(new AppEntry { AutoIgnored = new List<string> { b } }, p.Path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                entry.CheckUrl = checkUrl;
                entry.HomeUrl = (_homeUrl ?? "").Trim();
                // VPN or «Напрямую» is chosen on the tile's plate now (the switch is gone): an
                // edited rule keeps what it had, a new one starts on VPN, as the switch defaulted.
                entry.RequiresVpn = _editing == null || _editing.RequiresVpn;
                // The network cards are chosen on the tile's card chip — carried over as they are,
                // unless «Привязка к сетевой карте» pins the tile to the one it goes through now.
                entry.LockNic = ChkLockNic.IsChecked == true && !string.IsNullOrEmpty(_lockCard);
                entry.Interfaces = SavedInterfaces();
                entry.KillSwitchBeforeDirect = _editing != null && _editing.KillSwitchBeforeDirect;
                entry.KillSwitch = ChkKillSwitch.IsChecked == true;
                // The address is the switch (2026-11-23): filled = checked, empty = not checked.
                entry.SkipCheck = checkUrl.Length == 0;
                // The icon path is the switch too: empty = the server's icon, a path = the person's.
                entry.AutoIcon = string.IsNullOrWhiteSpace(TxtIcon.Text);
                // A configuration with no number takes no part in this rule, which is exactly what
                // the exclusion list has always meant; the numbered ones, in their order, are the
                // priority.
                entry.DisabledVpnSlotIds = _allowChoices.Where(c => c.Rank <= 0).Select(c => c.Id).ToList();
                entry.VpnSlotOrder = _allowChoices.Where(c => c.Rank > 0).OrderBy(c => c.Rank)
                    .Select(c => c.Id).ToList();

                ApplyIcon(entry);

                if (!IsLoaded) return;
                Result = entry;
                try { DialogResult = true; } catch (InvalidOperationException) { }
                Close();
            }
            finally
            {
                _saving = false;
                _saveGuard.Suspended = false;
                if (IsLoaded) BtnSave.IsEnabled = true;   // a refused save (validation) can be retried
            }
        }

        // Auto mode: download by name into the icon cache (force-refresh). Manual: copy the chosen
        // file into the icon store. Mirrors the old per-app/per-link behaviour.
        private void ApplyIcon(AppEntry entry)
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
                // New slug (renamed rule, other program…): never wait for the server here — Save
                // hung up to 8 s on it (cold DNS), mostly to learn there is no such icon. Take
                // what is on disk; if nothing, download in the background and MainWindow puts
                // it on the tile when it arrives (IconFetcher.IconDownloaded).
                entry.IconPath = IconFetcher.CachedFor(entry);
                if (entry.IconPath.Length == 0) IconFetcher.FetchInBackground(entry);
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
