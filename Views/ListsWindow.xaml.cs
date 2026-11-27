using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // The VPN list («VPN» on the main screen) and the rules list («+») — 2026-11-13 these moved
    // out of Settings → «Основное» into windows of their own.
    //
    // No «Сохранить» here: deleting already asks, and adding / editing has its own dialog with
    // its own «Сохранить». So every change is applied the moment it is made: the window edits
    // the LIVE config and hands each change to MainWindow.CommitLiveEditAsync (snapshot → change
    // → save → apply only what moved). Closing the window loses nothing.
    public partial class ListsWindow : FluentWindow, INotifyPropertyChanged
    {
        public enum Mode { Vpn, Rules }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Tri-state "select all" for each table header (true=all, false=none, null=mixed).
        private bool? _allAppsChecked = false, _allSlotsChecked = false;
        public bool? AllAppsChecked { get => _allAppsChecked; set { _allAppsChecked = value; OnPropertyChanged(); } }
        public bool? AllSlotsChecked { get => _allSlotsChecked; set { _allSlotsChecked = value; OnPropertyChanged(); } }

        private readonly AppConfig _cfg;
        private readonly Mode _mode;
        private readonly Func<Func<Task>, Task> _commit;
        private readonly ObservableCollection<AppRow> _appRows = new ObservableCollection<AppRow>();
        private readonly ObservableCollection<SlotRow> _slotRows = new ObservableCollection<SlotRow>();

        // cfg: the live config. commit: runs one change and applies it (MainWindow); without it
        // the change is only written to disk.
        public ListsWindow(AppConfig cfg, Mode mode, Func<Func<Task>, Task> commit)
        {
            InitializeComponent();
            _cfg = cfg;
            _mode = mode;
            _commit = commit ?? (async change => { await change().ConfigureAwait(true); AppConfigStore.Save(_cfg); });
            DataContext = this;   // header "select all" checkboxes bind to AllXChecked

            bool vpn = mode == Mode.Vpn;
            CardSlots.Visibility = vpn ? Visibility.Visible : Visibility.Collapsed;
            CardRules.Visibility = vpn ? Visibility.Collapsed : Visibility.Visible;
            Title = Loc.T(vpn ? "Set_VpnVariants" : "Set_Rules");
            // The variants are a handful of rows; the rules are the long list.
            if (!vpn) Height = 640;
            // The variants: as wide as the main window and as tall as their rows (2026-11-21, mom's
            // ZenBook at 250%: a fixed 950 × 460 stood wider than the main window, one row and a
            // blank half under it — «окно настроек впн длинное»).
            if (vpn)
            {
                MinWidth = 520;
                MinHeight = 220;
                // Not drawn until it is sized and in place (2026-11-27: it showed at 950×460, then
                // jumped to its fitted size and centre in front of the eyes — «при нажатии кнопки VPN
                // что-то передёргивается»). Off-screen didn't do: something put it back in the top-left
                // corner and the phantom flashed there. DWM «cloak» = the window exists and lays out,
                // the screen just doesn't show it; FitVpnWindow(true) uncloaks it, a timer makes sure.
                SourceInitialized += (s, e) => Cloak(true);
                Loaded += (s, e) => FitVpnWindow(true);
            }

            GridApps.ItemsSource = _appRows;
            GridSlots.ItemsSource = _slotRows;
            RefreshAppRows();
            RefreshSlotRows();
            InitRowDrag();
            if (vpn) CoverVpnListIfLocked();
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        // One change, applied at once. The rows are rebuilt afterwards from the config.
        private async Task CommitAsync(Action change)
        {
            try { await _commit(() => { change(); return Task.CompletedTask; }).ConfigureAwait(true); }
            catch (Exception ex)
            {
                // A manual change that didn't go through is said out loud, not left to the log.
                try { await OverlayDialog.MessageAsync(this, Loc.T("Set_Error"), ex.Message).ConfigureAwait(true); } catch { }
            }
        }

        // ---------- Access password ----------

        // While the access password is not entered the VPN list is not shown at all: a cover
        // with «Показать» sits where the table is.
        private Border _slotsCover;

        private void CoverVpnListIfLocked()
        {
            if (AccessLock.IsOpen(_cfg)) return;
            var host = GridSlots.Parent as Grid;
            if (host == null) return;
            var btn = new Wpf.Ui.Controls.Button
            {
                Content = Loc.T("Lock_BtnShowVpn"),
                Appearance = ControlAppearance.Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 0),
            };
            btn.Click += async (s, e) => await RequireVpnListAsync().ConfigureAwait(true);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Loc.T("Lock_VpnHidden"),
                FontSize = 13,
                Opacity = 0.75,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            sp.Children.Add(btn);
            _slotsCover = new Border { Child = sp, Background = System.Windows.Media.Brushes.Transparent, MinHeight = 90 };
            Grid.SetRow(_slotsCover, Grid.GetRow(GridSlots));
            GridSlots.Visibility = Visibility.Hidden;
            host.Children.Add(_slotsCover);
        }

        private async Task<bool> RequireVpnListAsync()
        {
            if (!await LockPrompt.RequireAsync(this, _cfg).ConfigureAwait(true)) return false;
            if (_slotsCover != null)
            {
                (GridSlots.Parent as Grid)?.Children.Remove(_slotsCover);
                _slotsCover = null;
                GridSlots.Visibility = Visibility.Visible;
            }
            return true;
        }

        // ---------- Rules ----------

        private void RefreshAppRows()
        {
            int sel = GridApps.SelectedIndex;
            _appRows.Clear();
            // Split by the main window's tabs (2026-11-27: «разбей так как разбито по вкладкам»):
            // a captioned line before each tab's rules, like «Добавлено автоматически» in the rule
            // editor. One tab — no lines. The Apps list is grouped by tab (TileTabs.Normalize).
            TileTabs.Ensure(_cfg);
            bool split = _cfg.Tabs.Count > 1;
            int lastTab = -1;
            foreach (var e in _cfg.Apps.Where(a => a != null))
            {
                if (split)
                {
                    int t = TileTabs.IndexOf(_cfg, e);
                    if (t != lastTab)
                    {
                        var tab = _cfg.Tabs[t];
                        _appRows.Add(new AppRow(tab.Id == TileTabs.MainId ? "" : tab.Id, TileTabs.NameOf(tab)));
                        lastTab = t;
                    }
                }
                _appRows.Add(new AppRow(e));
            }
            if (_appRows.Count > 0)
            {
                int at = sel >= 0 && sel < _appRows.Count && !_appRows[sel].IsDivider ? sel
                       : _appRows.ToList().FindIndex(r => !r.IsDivider);
                GridApps.SelectedIndex = at;
            }
            RecomputeAppsAll();
        }

        private async void OnAddApp(object sender, RoutedEventArgs e)
        {
            if (!await LockPrompt.RequireAsync(this, _cfg).ConfigureAwait(true)) return;
            var dlg = new EditRuleDialog(null, _cfg.VpnSlots) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null) return;
            var added = dlg.Result;
            // On the tab open on the main window, at the end of its group (2026-11-16).
            added.TabId = TileTabs.CurrentIdForNew(_cfg);
            await CommitAsync(() => { _cfg.Apps.Add(added); TileTabs.Normalize(_cfg); }).ConfigureAwait(true);
            RefreshAppRows();
            int at = _appRows.ToList().FindIndex(r => r.Entry == added);
            if (at >= 0) GridApps.SelectedIndex = at;
        }

        private async void OnEditApp(object sender, RoutedEventArgs e)
        {
            var entry = (GridApps.SelectedItem as AppRow)?.Entry;
            if (entry == null) return;
            if (!await LockPrompt.RequireAsync(this, _cfg).ConfigureAwait(true)) return;
            if (_cfg.Apps.IndexOf(entry) < 0) return;
            int sel = GridApps.SelectedIndex;
            var dlg = new EditRuleDialog(entry, _cfg.VpnSlots) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null)
            {
                // Cancelled: the amber rows (new auto entries) have been seen — only that flag is
                // written, the discarded edit stays discarded.
                try
                {
                    (System.Windows.Application.Current?.MainWindow as MainWindow)?.AutoNewSeen(entry.Id);
                }
                catch (Exception ex) { Log.Error("auto-new-seen-list", ex); }
                return;
            }
            var edited = dlg.Result;
            await CommitAsync(() =>
            {
                int idx = _cfg.Apps.IndexOf(entry);   // looked up again: the list may have moved meanwhile
                if (idx >= 0) _cfg.Apps[idx] = edited;
            }).ConfigureAwait(true);
            // Saved: the editor's result carries no AutoNew; the amber gear goes back to normal.
            try { (System.Windows.Application.Current?.MainWindow as MainWindow)?.AutoNewSeen(edited.Id); }
            catch (Exception ex) { Log.Error("auto-new-seen-list", ex); }
            RefreshAppRows();
            GridApps.SelectedIndex = sel;
        }

        private async void OnDeleteApp(object sender, RoutedEventArgs e)
        {
            // «Весь трафик» is built in — it can be switched off, never deleted.
            var targets = CheckedOrSelected(_appRows, GridApps.SelectedItem as AppRow)
                .Where(t => !t.IsBuiltIn).ToList();
            if (targets.Count == 0) return;
            string body = targets.Count == 1
                ? Loc.T("Set_DeleteOneFromList", targets[0].Name)
                : Loc.T("Set_DeleteRulesMany", targets.Count);
            var ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Set_DeleteRuleTitle"), body, Loc.T("Set_BtnDelete"), Loc.T("Set_BtnCancel")).ConfigureAwait(true);
            if (!ok) return;
            await CommitAsync(() => { foreach (var t in targets) _cfg.Apps.Remove(t); }).ConfigureAwait(true);
            RefreshAppRows();
        }

        // Entries to act on: the checked rows, or — if none are checked — the selected row.
        private static List<AppEntry> CheckedOrSelected(IEnumerable<AppRow> rows, AppRow selected)
        {
            var checkedRows = rows.Where(r => r.IsChecked).Select(r => r.Entry).ToList();
            if (checkedRows.Count > 0) return checkedRows;
            return selected?.Entry != null ? new List<AppEntry> { selected.Entry } : new List<AppEntry>();
        }

        // Refresh the blackmatrix7 rule lists of every site (with a template) across all rules.
        private async void OnRefreshRules(object sender, RoutedEventArgs e)
        {
            var sites = _cfg.Apps.Where(a => a != null)
                                 .SelectMany(a => a.Sites ?? new List<RuleSite>())
                                 .Where(s => s != null && !string.IsNullOrWhiteSpace(s.ClashRuleUrl))
                                 .ToList();
            if (sites.Count == 0)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Set_NoLists"), Loc.T("Set_NoListsDesc")).ConfigureAwait(true);
                return;
            }
            int sel = GridApps.SelectedIndex;
            BtnRefreshLink.IsEnabled = false;
            int okCount = 0, total = 0;
            try
            {
                // Downloaded first, then swapped into the live rules in one change — the engine
                // is not reloaded once per list.
                var fresh = new List<(RuleSite Site, List<string> Items, string Fetched)>();
                foreach (var s in sites)
                {
                    var copy = new RuleSite { Url = s.Url, ClashRuleUrl = s.ClashRuleUrl, Launch = s.Launch };
                    try
                    {
                        // Off the UI thread: .NET Framework resolves DNS synchronously on the
                        // thread that starts the request — the window froze on a cold lookup.
                        await Task.Run(() => ClashRuleService.RefreshAsync(copy)).ConfigureAwait(true);
                        fresh.Add((s, copy.RuleItems, copy.RuleFetchedUtc));
                        okCount++; total += copy.RuleItems?.Count ?? 0;
                    }
                    catch { }
                }
                if (fresh.Count > 0)
                    await CommitAsync(() =>
                    {
                        foreach (var f in fresh) { f.Site.RuleItems = f.Items; f.Site.RuleFetchedUtc = f.Fetched; }
                    }).ConfigureAwait(true);
                RefreshAppRows();
                GridApps.SelectedIndex = sel;
                await OverlayDialog.MessageAsync(this, Loc.T("Set_Done"),
                    Loc.T("Set_ListsRefreshed", okCount, sites.Count, total)).ConfigureAwait(true);
            }
            finally
            {
                BtnRefreshLink.IsEnabled = true;
            }
        }

        // ---------- Drag to reorder ----------

        private void InitRowDrag()
        {
            // Rule order is routing (further left / higher wins) — not while the access password is closed.
            // A tab's line is not dragged; a rule dragged past one lands on that tab (OnRulesReordered).
            new GridRowDrag<AppRow>(GridApps, _appRows, r => r.IsDivider || (r.Entry != null && r.Entry.IsBuiltIn) || !AccessLock.IsOpen(_cfg), OnRulesReordered);
            // The VPN order is routing: no dragging while the list is closed.
            new GridRowDrag<SlotRow>(GridSlots, _slotRows, r => !AccessLock.IsOpen(_cfg), OnSlotsReordered);
        }

        // The rows' new order → the master Apps list: rows take the positions rows held
        // (null entries, which have no row, stay where they were).
        // A rule dropped under another tab's line moves to that tab (2026-11-27, the owner's choice:
        // «переносит во вкладку») — its tile shows up on that tab of the main window.
        private async void OnRulesReordered(List<AppRow> order, AppRow dropped)
        {
            var entries = new List<AppEntry>();
            var tabOf = new Dictionary<AppEntry, string>();
            bool split = order.Any(r => r.IsDivider);
            string tab = "";
            foreach (var r in order)
            {
                if (r.IsDivider) { tab = r.TabId ?? ""; continue; }
                entries.Add(r.Entry);
                if (split && r.Entry != null && !r.Entry.IsAllTraffic) tabOf[r.Entry] = tab;
            }
            bool stale = false, moved = false;
            await CommitAsync(() =>
            {
                var slots = Enumerable.Range(0, _cfg.Apps.Count).Where(i => _cfg.Apps[i] != null).ToList();
                if (slots.Count != entries.Count) { stale = true; return; }
                for (int k = 0; k < slots.Count; k++) _cfg.Apps[slots[k]] = entries[k];
                foreach (var kv in tabOf)
                    if ((kv.Key.TabId ?? "") != kv.Value)
                    {
                        Log.Info("Правило «" + kv.Key.Name + "» перенесено во вкладку «"
                                 + TileTabs.NameOf(_cfg.Tabs.FirstOrDefault(t => (t.Id == TileTabs.MainId ? "" : t.Id) == kv.Value)) + "».");
                        kv.Key.TabId = kv.Value;
                        moved = true;
                    }
            }).ConfigureAwait(true);
            if (stale || moved)
            {
                RefreshAppRows();
                var row = _appRows.FirstOrDefault(r => r.Entry == dropped?.Entry);
                if (row != null) GridApps.SelectedItem = row;
                return;
            }
            GridApps.SelectedItem = dropped;
        }

        private async void OnSlotsReordered(List<SlotRow> order, SlotRow dropped)
        {
            var slots = order.Select(r => r.Slot).ToList();
            await CommitAsync(() =>
            {
                _cfg.VpnSlots.Clear();
                _cfg.VpnSlots.AddRange(slots);
            }).ConfigureAwait(true);
            int at = order.IndexOf(dropped);
            RefreshSlotRows();   // renumbers «#»
            GridSlots.SelectedIndex = at;
        }

        // ---------- VPN variants ----------

        private void RefreshSlotRows()
        {
            int sel = GridSlots.SelectedIndex;
            _slotRows.Clear();
            for (int i = 0; i < _cfg.VpnSlots.Count; i++)
                _slotRows.Add(new SlotRow(_cfg.VpnSlots[i], i + 1));
            if (_slotRows.Count > 0)
                GridSlots.SelectedIndex = sel >= 0 && sel < _slotRows.Count ? sel : 0;
            RecomputeSlotsAll();
            if (_mode == Mode.Vpn && IsLoaded) FitVpnWindow(false);
            if (_mode == Mode.Vpn) FillSlotGeo();
        }

        // Set by the main window: slot id → (ISO, «Город, Страна»), and «locate the VPNs now».
        internal static Func<string, Tuple<string, string>> SlotGeoOf;
        internal static Func<Task> ResolveGeos;

        // The «Страна, город» column (2026-11-27): what is known at once, then again once the main
        // window has located a VPN just added or changed (a DNS lookup of its server).
        private async void FillSlotGeo()
        {
            SetSlotGeo();
            if (ResolveGeos == null) return;
            try { await ResolveGeos().ConfigureAwait(true); } catch { }
            SetSlotGeo();
        }

        private void SetSlotGeo()
        {
            foreach (var r in _slotRows)
            {
                Tuple<string, string> g = null;
                try { g = SlotGeoOf?.Invoke(r.Slot?.Id); } catch { }
                r.SetGeo(g?.Item1, g?.Item2);
            }
        }

        // Width = the owner's; height = the window around the table + the table's full height
        // (header + every row), no more than the screen. `center` the first time — the window was
        // centred at its XAML size.
        private void FitVpnWindow(bool center)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var wa = SystemParameters.WorkArea;
                    if (center && Owner != null && Owner.ActualWidth > 0)
                        Width = Math.Min(wa.Width, Math.Max(MinWidth, Owner.ActualWidth));
                    if (GridSlots.IsVisible && GridSlots.ActualHeight > 0)
                    {
                        double chrome = ActualHeight - GridSlots.ActualHeight;
                        GridSlots.Measure(new Size(GridSlots.ActualWidth, double.PositiveInfinity));
                        double need = GridSlots.DesiredSize.Height;
                        GridSlots.InvalidateMeasure();   // back to its real constraint next pass
                        double h = Math.Max(MinHeight, Math.Min(wa.Height, Math.Ceiling(chrome + need + 2)));
                        if (Math.Abs(Height - h) > 0.5) Height = h;
                    }
                    if (center && Owner != null)
                    {
                        Left = Owner.Left + (Owner.ActualWidth - Width) / 2;
                        Top = Owner.Top + (Owner.ActualHeight - Height) / 2;
                    }
                    UiHelpers.KeepOnScreen(this);
                }
                catch (Exception ex) { Log.Error("vpn-window-fit", ex); }
                // Shown now, sized and in place — after one more layout pass, so the first frame on
                // screen is the final one.
                if (center)
                    Dispatcher.BeginInvoke(new Action(() => Cloak(false)), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // DWMWA_CLOAK: hidden from the screen while it lays out (see the constructor). A safety
        // timer uncloaks it whatever happens — a window that never shows would be far worse.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        private const int DwmwaCloak = 13;
        private bool _cloaked;

        private void Cloak(bool on)
        {
            if (on == _cloaked) return;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int v = on ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaCloak, ref v, sizeof(int));
                _cloaked = on;
                if (on)
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                    t.Tick += (s, e) => { t.Stop(); Cloak(false); };
                    t.Start();
                }
            }
            catch (Exception ex) { Log.Error("vpn-window-cloak", ex); }
        }

        // «Обновить» (refresh: download at once, with progress) / «Состав» (what the subscription
        // does, list by list) — opened from the variant's own window (EditSlotDialog).
        private void ShowSubscription(VpnSlot slot, bool refresh, Window owner)
        {
            if (slot == null || slot.Kind != VpnSlotKind.ClashSub) return;
            int idx = _cfg.VpnSlots.IndexOf(slot);
            var proxyName = ProxyParsers.ProxyNameFor(slot, idx + 1);

            var dlg = new SubscriptionDialog(_cfg, proxyName, slot.Value, slot.Name)
            {
                Owner = owner,
                AutoRefresh = refresh,
                // The engine holds a subscription only while some tile rides it; then it re-reads
                // the nodes too. Changed rules reach the config through the main window's watchdog
                // (EnsureSubscriptionsCurrent). A 404 here just means no tile rides it.
                AfterRefresh = async () =>
                {
                    var controller = new MihomoController(_cfg);
                    if (await controller.IsApiReachableAsync().ConfigureAwait(true))
                        await controller.RefreshProviderAsync(proxyName + "_prov").ConfigureAwait(true);
                },
            };
            dlg.ShowDialog();
        }

        private async void OnAddSlot(object sender, RoutedEventArgs e)
        {
            if (!await RequireVpnListAsync()) return;
            var dlg = new EditSlotDialog(null, _cfg.VpnSlots.Select(s => s.Name)) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Results.Count == 0) return;
            var added = dlg.Results.ToList();
            await CommitAsync(() =>
            {
                // Several links pasted at once → several variants.
                foreach (var slot in added)
                {
                    _cfg.VpnSlots.Add(slot);
                    // A new variant joins NO existing rule's search: the user assigns it where it
                    // is wanted (Routing table or the rule itself). Rules list what they may NOT
                    // use, so without this every rule would silently start trying the new variant
                    // on its next connect — «не надо сразу включать его в перебор для всех правил».
                    foreach (var app in _cfg.Apps ?? new List<AppEntry>())
                    {
                        if (app == null) continue;
                        if (app.DisabledVpnSlotIds == null) app.DisabledVpnSlotIds = new List<string>();
                        if (!app.DisabledVpnSlotIds.Contains(slot.Id))
                            app.DisabledVpnSlotIds.Add(slot.Id);
                    }
                }
            }).ConfigureAwait(true);
            // Whatever copy their names had belongs to a variant before them (2026-11-25).
            foreach (var slot in added)
                if (slot.Kind == VpnSlotKind.ClashSub)
                    SubscriptionPolicy.ForgetCopy(_cfg, ProxyParsers.ProxyNameFor(slot, _cfg.VpnSlots.IndexOf(slot) + 1));
            RefreshSlotRows();
            if (_slotRows.Count > 0) GridSlots.SelectedIndex = _slotRows.Count - 1;
        }

        // The subscription downloaded in the background while its dialog is open; the «Узел» list
        // then fills from it. A failure is only logged — «Обновить» shows the reason.
        private async System.Threading.Tasks.Task FetchNodesAsync(VpnSlot slot, string proxyName, EditSlotDialog dlg)
        {
            try
            {
                var why = await SubscriptionPolicy.RefreshAsync(_cfg, proxyName, slot.Value, null, System.Threading.CancellationToken.None).ConfigureAwait(true);
                if (why != null) Log.Info("Подписка «" + proxyName + "»: узлы не получены — " + why);
                if (dlg.IsVisible) dlg.SubscriptionNodes = SubscriptionPolicy.ReadNodes(_cfg, proxyName, slot.Value);
            }
            catch (Exception ex) { Log.Error("sub-fetch-nodes", ex); }
        }

        private async void OnEditSlot(object sender, RoutedEventArgs e)
        {
            if (!await RequireVpnListAsync()) return;
            var idx = GridSlots.SelectedIndex;
            if (idx < 0 || idx >= _cfg.VpnSlots.Count) return;
            var slot = _cfg.VpnSlots[idx];
            var dlg = new EditSlotDialog(slot,
                _cfg.VpnSlots.Where((s, i) => i != idx).Select(s => s.Name)) { Owner = this };
            if (slot.Kind == VpnSlotKind.ClashSub)
            {
                var proxyName = ProxyParsers.ProxyNameFor(slot, idx + 1);
                var nodes = SubscriptionPolicy.ReadNodes(_cfg, proxyName, slot.Value);
                dlg.SubscriptionNodes = nodes;
                // No copy of THIS link yet: fetched right away, its nodes appear by themselves —
                // not only after «Обновить» (2026-11-25).
                if (nodes == null) _ = FetchNodesAsync(slot, proxyName, dlg);
                // «Обновить» may bring other nodes: the list follows.
                dlg.ShowSubscription = refresh =>
                {
                    ShowSubscription(slot, refresh, dlg);
                    dlg.SubscriptionNodes = SubscriptionPolicy.ReadNodes(_cfg, proxyName, slot.Value);
                };
            }
            else dlg.ShowSubscription = refresh => ShowSubscription(slot, refresh, dlg);
            if (dlg.ShowDialog() != true || dlg.Result == null) return;
            var edited = dlg.Result;
            // Another link or another name: the copy under its (new) name is not this link's.
            if (edited.Kind == VpnSlotKind.ClashSub
                && (!string.Equals((edited.Value ?? "").Trim(), (slot.Value ?? "").Trim(), StringComparison.Ordinal)
                    || !string.Equals(edited.Name ?? "", slot.Name ?? "", StringComparison.Ordinal)))
                SubscriptionPolicy.ForgetCopy(_cfg, ProxyParsers.ProxyNameFor(edited, idx + 1));
            await CommitAsync(() =>
            {
                int at = _cfg.VpnSlots.IndexOf(slot);
                if (at >= 0) _cfg.VpnSlots[at] = edited;
            }).ConfigureAwait(true);
            RefreshSlotRows();
            GridSlots.SelectedIndex = idx;
        }

        // What a toolbar button acts on: the ticked rows, else the selected one.
        private List<VpnSlot> SlotTargets()
        {
            var targets = _slotRows.Where(r => r.IsChecked).Select(r => r.Slot).ToList();
            if (targets.Count == 0)
            {
                var sel = (GridSlots.SelectedItem as SlotRow)?.Slot;
                if (sel != null) targets.Add(sel);
            }
            return targets;
        }

        // «Обновить» is live only when the targets hold something to download — a subscription.
        private void UpdateSlotButtons()
        {
            if (BtnRefreshSlot == null) return;
            BtnRefreshSlot.IsEnabled = SlotTargets().Any(s => s.Kind == VpnSlotKind.ClashSub);
        }

        private void OnSlotSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSlotButtons();

        // Downloads every targeted subscription again, one window after another — the same
        // window as «Обновить» inside the variant's own dialog.
        private async void OnRefreshSlot(object sender, RoutedEventArgs e)
        {
            var subs = SlotTargets().Where(s => s.Kind == VpnSlotKind.ClashSub).ToList();
            if (subs.Count == 0) return;
            if (!await RequireVpnListAsync()) return;
            foreach (var slot in subs) ShowSubscription(slot, true, this);
        }

        private async void OnDeleteSlot(object sender, RoutedEventArgs e)
        {
            if (!await RequireVpnListAsync()) return;
            var targets = SlotTargets();
            if (targets.Count == 0) return;
            string body = targets.Count == 1
                ? Loc.T("Set_DeleteOneFromList", targets[0].Name)
                : Loc.T("Set_DeleteVariantsMany", targets.Count);
            var ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Set_DeleteVariantTitle"), body, Loc.T("Set_BtnDelete"), Loc.T("Set_BtnCancel")).ConfigureAwait(true);
            if (!ok) return;
            var gone = targets.Where(t => t.Kind == VpnSlotKind.ClashSub)
                              .Select(t => ProxyParsers.ProxyNameFor(t, _cfg.VpnSlots.IndexOf(t) + 1)).ToList();
            await CommitAsync(() => { foreach (var t in targets) _cfg.VpnSlots.Remove(t); }).ConfigureAwait(true);
            foreach (var name in gone) SubscriptionPolicy.ForgetCopy(_cfg, name);
            RefreshSlotRows();
        }

        // ---------- Checkboxes (multi-select) ----------

        private void CellCheck_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CheckRow row)
            {
                row.IsChecked = !row.IsChecked;
                if (row is AppRow) RecomputeAppsAll();
                else if (row is SlotRow) RecomputeSlotsAll();
                e.Handled = true;
            }
        }

        private void HeaderCheck_Click(object sender, MouseButtonEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (tag == "apps") { bool v = AllAppsChecked != true; foreach (var r in _appRows.Where(x => !x.IsDivider)) r.IsChecked = v; RecomputeAppsAll(); }
            else if (tag == "slots") { bool v = AllSlotsChecked != true; foreach (var r in _slotRows) r.IsChecked = v; RecomputeSlotsAll(); }
            e.Handled = true;
        }

        private void RecomputeAppsAll() => AllAppsChecked = TriState(_appRows.Where(r => !r.IsDivider).ToList());
        private void RecomputeSlotsAll()
        {
            AllSlotsChecked = TriState(_slotRows);
            UpdateSlotButtons();
        }

        private static bool? TriState<T>(ICollection<T> rows) where T : CheckRow
        {
            if (rows.Count == 0) return false;
            int c = rows.Count(r => r.IsChecked);
            return c == 0 ? (bool?)false : (c == rows.Count ? (bool?)true : null);
        }

        // ---------- Clear selection on empty-space click ----------

        private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
            GridSlots.SelectedIndex = -1;
            GridApps.SelectedIndex = -1;
            try { Keyboard.Focus(RootGrid); } catch { }
        }

        private static bool IsWithinInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is DataGridRow) return true;
                if (d is System.Windows.Controls.Primitives.DataGridColumnHeader) return true;
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---------- Row VMs ----------

        // Row VM base carrying the per-row checkbox state (multi-select in tables).
        public abstract class CheckRow : INotifyPropertyChanged
        {
            private bool _checked;
            public bool IsChecked
            {
                get => _checked;
                set { if (_checked != value) { _checked = value; OnPropertyChanged(); } }
            }
            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string n = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public class AppRow : CheckRow
        {
            public AppEntry Entry { get; }
            public string Name { get; }
            public string DisplayPath { get; }
            // The state columns (2026-11-23: «всё положительное должно выделяться»; «VPN» removed
            // 2026-11-24): each a
            // StateCell, drawn by ONE template in ListsWindow.xaml — a positive value in its state
            // colour, the rest muted. Rebuilt with the row (RefreshAppRows after every edit).
            public StateCell KillSwitch { get; }
            public StateCell LockNic { get; }
            public StateCell AutoTrack { get; }

            // A tab's captioned line (2026-11-27): no rule behind it, drawn by the IsDivider
            // template of RulesRowStyle; TabId is what a rule dropped under it gets ("" = «Основная»).
            public bool IsDivider { get; }
            public string TabId { get; }

            public AppRow(string tabId, string caption)
            {
                IsDivider = true;
                TabId = tabId ?? "";
                Name = caption ?? "";
                DisplayPath = "";
            }

            public AppRow(AppEntry e)
            {
                Entry = e;
                Name = e.Name;
                DisplayPath = PathSummary(e);
                // The kill-switch and the card lock are protection: the guard switches' violet.
                KillSwitch = new StateCell(e.KillSwitch, Loc.T(e.KillSwitch ? "Set_On" : "Set_Off"), Theme.AccentViolet);
                LockNic = new StateCell(e.LockNic, Loc.T(e.LockNic ? "Set_On" : "Set_Off"), Theme.AccentViolet);
                // «Всё остальное» has no programs to follow: «—», like its «Путь».
                AutoTrack = e.IsAllTraffic
                    ? new StateCell(false, "—", Theme.AccentBlue)
                    : new StateCell(e.AutoTrack, Loc.T(e.AutoTrack ? "Set_On" : "Set_Off"), Theme.AccentBlue);
            }

            // The ADDRESS of the one item the tile launches — full exe path, the AUMID when a
            // Store app has no path, the site URL, or (for a template) the full rule-list URL.
            // The item flagged «Плитка», falling back to the first program, else the first
            // site — the rule AppLauncher itself follows.
            private static string PathSummary(AppEntry e)
            {
                var progs = (e.Programs ?? new List<RuleProgram>())
                    .Where(p => p != null && (!string.IsNullOrWhiteSpace(p.Path) || !string.IsNullOrWhiteSpace(p.Aumid)))
                    .ToList();
                // A template is a site with no URL of its own, only a rule-list address.
                var sites = (e.Sites ?? new List<RuleSite>())
                    .Where(s => s != null && (!string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.ClashRuleUrl)))
                    .ToList();

                var prog = progs.FirstOrDefault(p => p.Launch);
                var site = sites.FirstOrDefault(s => s.Launch);
                var folder = (e.Folders ?? new List<RuleFolder>()).FirstOrDefault(f => f != null && f.Launch && !string.IsNullOrWhiteSpace(f.Path));
                if (prog == null && site == null && folder != null) return folder.Path.Trim();
                if (prog == null && site == null)
                {
                    progs = progs.Where(p => !p.Auto).ToList();
                    if (progs.Count > 0) prog = progs[0];
                    else if (sites.Count > 0) site = sites[0];
                }

                if (prog != null)
                {
                    var v = !string.IsNullOrWhiteSpace(prog.Path) ? prog.Path.Trim() : (prog.Aumid ?? "").Trim();
                    if (v.Length > 0) return v;
                }
                if (site != null)
                {
                    var v = !string.IsNullOrWhiteSpace(site.Url) ? site.Url.Trim() : (site.ClashRuleUrl ?? "").Trim();
                    if (v.Length > 0) return v;
                }
                return "—";
            }
        }

        public class SlotRow : CheckRow
        {
            public VpnSlot Slot { get; }
            public int Number { get; }
            public string Name { get; }
            public string KindLabel { get; }
            public string ValueLabel { get; }

            // Where its server is: the ISO badge and «Город, Страна», as in the speed window.
            public string Iso { get; private set; } = "";
            public string Place { get; private set; } = "";
            public Visibility IsoVisibility => Iso.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            internal void SetGeo(string iso, string place)
            {
                iso = iso ?? ""; place = place ?? "";
                if (iso == Iso && place == Place) return;
                Iso = iso; Place = place;
                OnPropertyChanged(nameof(Iso)); OnPropertyChanged(nameof(Place)); OnPropertyChanged(nameof(IsoVisibility));
            }

            public SlotRow(VpnSlot s, int number)
            {
                Slot = s;
                Number = number;
                Name = s.Name;
                switch (s.Kind)
                {
                    case VpnSlotKind.Vless: KindLabel = "VLESS"; break;
                    case VpnSlotKind.ClashSub: KindLabel = Loc.T("Set_KindClashSub"); break;
                    case VpnSlotKind.Hysteria2: KindLabel = "Hysteria2"; break;
                    case VpnSlotKind.ShareLink: KindLabel = ShareLinks.LabelOf(s.Value); break;
                    default: KindLabel = "WireGuard .conf"; break;
                }
                // Full value; the column's text style trims it when it genuinely doesn't fit.
                ValueLabel = s.Value ?? "";
                // No «Вкл» column any more (2026-11-23): every configuration is in use. No «Проверка»
                // column either (2026-11-23, the owner's call): SlotHealth still orders the connect's
                // sweep, it is just not shown here.
            }
        }
    }

    // One yes/no cell of the rules list: the text, whether it is the positive value, and the
    // colour that value has in the standard (Theme). Drawn by the StateCell template of
    // ListsWindow.xaml: positive — that colour, SemiBold; otherwise TextMuted, normal weight
    // (TextSecondary on the selected row, where TextMuted would sink into TableRowSelected).
    public sealed class StateCell
    {
        public StateCell(bool on, string text, System.Windows.Media.Brush onBrush)
        {
            On = on;
            Text = text ?? "";
            OnBrush = onBrush;
        }

        public bool On { get; }
        public string Text { get; }
        public System.Windows.Media.Brush OnBrush { get; }
    }
}
