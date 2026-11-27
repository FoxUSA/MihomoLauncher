using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using DataGrid = System.Windows.Controls.DataGrid;

namespace MihomoLauncher.Views
{
    public partial class SettingsWindow : FluentWindow, INotifyPropertyChanged
    {
        public AppConfig Result { get; private set; }
        public AppConfig Baseline { get; private set; }   // the config as this window opened

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private readonly AppConfig _editing;
        private readonly ObservableCollection<RouteRow> _routeRows = new ObservableCollection<RouteRow>();

        // dnsOnly: the «DNS» button on the main screen — its own window with the DNS page alone.
        // Otherwise Settings without the DNS page (it has its own button now).
        private readonly bool _dnsOnly;

        public SettingsWindow(AppConfig source, bool dnsOnly = false)
        {
            InitializeComponent();
            _dnsOnly = dnsOnly;
            if (dnsOnly)
            {
                foreach (var it in Tabs.Items.OfType<System.Windows.Controls.TabItem>())
                    it.Visibility = Visibility.Collapsed;   // headers only: the selected page still shows
                // Two tabs of its own (2026-11-17): «Настройки» (the DNS page) and «Статистика».
                TabDns.Visibility = Visibility.Visible;
                TabDns.SetResourceReference(System.Windows.Controls.HeaderedContentControl.HeaderProperty, "DnsSt_TabSettings");
                TabDnsStats.Visibility = Visibility.Visible;
                TabDnsStats.IsSelected = true;   // «Статистика» first and opened by default
                Title = "DNS";
                TitleBarCtl.Title = "DNS";
                BtnExitAppFooter.Visibility = Visibility.Collapsed;
                // Wide enough for the statistics' three columns without cutting a name.
                Width = 1000; MinWidth = 900;
                Height = 560; MinHeight = 420;
                InitDnsStats();
            }
            else
            {
                TabDns.Visibility = Visibility.Collapsed;
                TabDnsStats.Visibility = Visibility.Collapsed;
                if (TabDns.IsSelected || TabDnsStats.IsSelected) Tabs.SelectedIndex = 0;
            }

            // A full copy: the hand-written field-by-field one used to drop FirewallKillSwitch
            // (back to «on» after every Save) and a subscription's update interval.
            _editing = AppConfigStore.Clone(source);
            // The copy as it opened: on Save the main window takes only what differs from it
            // (AppConfigStore.MergeChanged) — this window no longer blocks the main one (2026-11-27).
            Baseline = AppConfigStore.Clone(source);
            FillUi();

            LstRoutes.ItemsSource = _routeRows;
            RefreshLockUi();

            if (!dnsOnly) UpdateMihomoStatus();
            FillUpdatesUi();

            VpnStatus.Changed += OnVpnStatusChanged;
            // «Сохранить» only when something differs: the whole edited config + every field.
            _saveGuard = SaveGuard.Attach(this, BtnSave, () => SaveGuard.Xml(_editing));
            Loaded += (s, e) =>
            {
                if (!_dnsOnly) RefreshDiagnostics();
                if (_dnsOnly) FitDnsWindowToStats();
                Dispatcher.BeginInvoke(new Action(FitHeightToTab), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            Closed += (s, e) => { _closed = true; try { VpnStatus.Changed -= OnVpnStatusChanged; } catch { } };
        }

        private readonly SaveGuard _saveGuard;

        // ---------- Access password ----------

        private void RefreshLockUi()
        {
            bool set = AccessLock.IsSet(_editing);
            BtnLockSet.Content = Loc.T(set ? "Lock_BtnChange" : "Lock_BtnSet");
            BtnLockClear.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
            LblLockState.Text = Loc.T(set ? "Lock_StateOn" : "Lock_StateOff");
        }

        // Set / change: the current password first (if there is one), then the new one twice.
        // Applies to the edited copy — takes effect on «Сохранить», like everything here.
        private async void OnLockSet(object sender, RoutedEventArgs e)
        {
            if (AccessLock.IsSet(_editing) && !await LockPrompt.RequireAsync(this, _editing).ConfigureAwait(true)) return;
            var p1 = new System.Windows.Controls.PasswordBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
            var p2 = new System.Windows.Controls.PasswordBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            System.Windows.Controls.TextBlock Label(string key, double top) => new System.Windows.Controls.TextBlock
            {
                Text = Loc.T(key), FontSize = 13, Foreground = Theme.OverlayBody, Margin = new Thickness(0, top, 0, 4),
            };
            sp.Children.Add(Label("Lock_New", 0));
            sp.Children.Add(p1);
            sp.Children.Add(Label("Lock_Repeat", 10));
            sp.Children.Add(p2);
            p1.Loaded += (s, a) => p1.Focus();
            bool ok = await OverlayDialog.ConfirmContentAsync(this, Loc.T("Lock_SetTitle"), sp,
                Loc.T("Lock_BtnSave"), Loc.T("Set_BtnCancel"), 400).ConfigureAwait(true);
            if (!ok) return;
            if (p1.Password.Length == 0)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Lock_SetTitle"), Loc.T("Lock_Empty")).ConfigureAwait(true);
                return;
            }
            if (p1.Password != p2.Password)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Lock_SetTitle"), Loc.T("Lock_Mismatch")).ConfigureAwait(true);
                return;
            }
            AccessLock.Set(_editing, p1.Password);
            RefreshLockUi();
            await OverlayDialog.MessageAsync(this, Loc.T("Lock_SetTitle"), Loc.T("Lock_SavedHint")).ConfigureAwait(true);
        }

        private async void OnLockClear(object sender, RoutedEventArgs e)
        {
            if (!await LockPrompt.RequireAsync(this, _editing).ConfigureAwait(true)) return;
            AccessLock.Clear(_editing);
            RefreshLockUi();
        }

        private void FillUi()
        {
            // Only show a real exe path; if the engine isn't actually on disk, leave the field
            // empty (honest) with the default location as a watermark + hint. The config still
            // holds the default target so install/detection knows where the engine goes.
            var exe = (_editing.MihomoExePath ?? "").Trim();
            TxtMihomoExe.Text = (exe.Length > 0 && File.Exists(exe)) ? exe : "";
            TxtMihomoExe.PlaceholderText = AppConfigStore.DefaultMihomoExeTarget;
            LblMihomoExeHint.Text = Loc.T("Set_DefaultPathHint", AppConfigStore.DefaultMihomoExeTarget);

            TxtMihomoWorkDir.Text = AppConfigStore.ResolveMihomoWorkDir(_editing);
            ChkAutostart.IsChecked = Autostart.IsEnabled();
            SwitchFirewall.IsChecked = _editing.FirewallKillSwitch;
            // The DNS window shows only its own two tabs (2026-11-21: «окно DNS с задержкой
            // открывается»): the hidden pages' status lines and the card list (WMI) are not read —
            // ~0,3 s. Their editable fields above are still filled: «Сохранить» reads them back.
            if (!_dnsOnly) UpdateFirewallStatus();

            TxtConfigPath.Text = AppConfigStore.ConfigPath;
            LblConfigPathHint.Text = Loc.T("Set_DefaultPathHint", AppConfigStore.DefaultConfigPath);

            FillDnsUi();
        }

        // The «Сеть» tab (Windows' card priority) moved to its own dialog on 2026-11-23 —
        // NicPriorityDialog, opened from the menu of a tile's network-card chip.

        // ---------- DNS for direct traffic ----------

        private bool _dnsFilling;
        private string _dnsTestedSig;      // the entries the last check was run on
        private bool _dnsTestAnyOk;

        private void FillDnsUi()
        {
            _dnsFilling = true;
            var list = _editing.DirectDns ?? new List<string>();
            TxtDns1.Text = list.Count > 0 ? list[0] : "";
            TxtDns2.Text = list.Count > 1 ? string.Join(", ", list.Skip(1)) : "";
            SwitchDirectDns.IsChecked = _editing.DirectDnsEnabled;
            FillDnsSources();
            _dnsFilling = false;
            UpdateDnsUi();
            try { LblDnsDirect.Text = RouteExplainer.DirectSummary(_editing); }
            catch (Exception ex) { Log.Error("dns-direct-summary", ex); LblDnsDirect.Text = ""; }
        }

        private void OnDirectDnsToggle(object sender, RoutedEventArgs e) { if (!_dnsFilling) UpdateDnsUi(); }

        // «Чей DNS»: the typed servers, or one per subscription that sends its own `dns:`.
        private List<DirectDns.SubDnsOffer> _dnsOffers = new List<DirectDns.SubDnsOffer>();
        private string _dnsSource = "";   // "" = typed; else the subscription's slot Id

        private void FillDnsSources()
        {
            _dnsOffers = DirectDns.Offered(_editing);
            PnlDnsOffers.Children.Clear();
            _dnsSource = _dnsOffers.Any(o => o.Slot.Id == _editing.DirectDnsSource) ? _editing.DirectDnsSource : "";
            foreach (var offer in _dnsOffers)
            {
                var servers = offer.Servers.Select(DirectDns.Parse).Where(x => x != null)
                                   .Select(x => x.Ok ? DirectDns.Describe(x) + " — " + x.Host : x.Raw);
                var rb = new RadioButton
                {
                    GroupName = "DnsSource", Margin = new Thickness(0, 2, 0, 2), Tag = offer.Slot.Id,
                    Content = new TextBlock
                    {
                        Text = Loc.T("Set_DnsSourceSub", offer.Slot.Name, string.Join("; ", servers)),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    IsChecked = offer.Slot.Id == _dnsSource,
                };
                rb.Checked += OnDnsSourceChanged;
                PnlDnsOffers.Children.Add(rb);
            }
            RbDnsOwn.IsChecked = _dnsSource.Length == 0;
            PnlDnsSource.Visibility = _dnsOffers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDnsSourceChanged(object sender, RoutedEventArgs e)
        {
            _dnsSource = (sender as FrameworkElement)?.Tag as string ?? "";
            if (!_dnsFilling) UpdateDnsUi();
        }

        private DirectDns.SubDnsOffer ChosenDnsOffer() => _dnsOffers.FirstOrDefault(o => o.Slot.Id == _dnsSource);

        private void OnDnsTextChanged(object sender, TextChangedEventArgs e) { if (!_dnsFilling) UpdateDnsUi(); }

        // Several servers pasted into the first field (NextDNS's page lists two IPv6 and a DoH at
        // once): the first stays, the rest go to «Запасной».
        private void OnDns1LostFocus(object sender, RoutedEventArgs e)
        {
            var toks = (TxtDns1.Text ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 2 || !string.IsNullOrWhiteSpace(TxtDns2.Text)) return;
            _dnsFilling = true;
            TxtDns1.Text = toks[0];
            TxtDns2.Text = string.Join(", ", toks.Skip(1));
            _dnsFilling = false;
            UpdateDnsUi();
        }

        private List<DnsEntry> DnsEntries(TextBox box) => DirectDns.ParseMany(box.Text);

        private void UpdateDnsUi()
        {
            bool on = SwitchDirectDns.IsChecked == true;
            bool fromSub = ChosenDnsOffer() != null;
            // The typed servers stay (and stay editable) — they come back if the subscription is
            // un-picked or stops sending DNS — but they are visibly not the ones in use.
            PnlDnsFields.Opacity = on && !fromSub ? 1.0 : 0.55;
            PnlDnsSource.Opacity = on ? 1.0 : 0.55;

            var first = DnsEntries(TxtDns1);
            ShowRecognised(LblDns1, first);

            // A plain address is half of a pair — every provider hands out two. A DoH/DoT/DoQ name
            // already stands for a fleet, so the second line only appears when it has a use.
            bool plain = first.Count > 0 && first[0].Ok && first[0].Proto == DnsProto.Plain;
            var second = Visibility.Visible;
            if (!plain && string.IsNullOrWhiteSpace(TxtDns2.Text)) second = Visibility.Collapsed;
            LblDns2Caption.Visibility = TxtDns2.Visibility = LblDns2.Visibility = second;
            ShowRecognised(LblDns2, DnsEntries(TxtDns2));

            if (_dnsTestedSig != null && _dnsTestedSig != DnsSig()) { LblDnsTest.Text = ""; _dnsTestedSig = null; }
        }

        private void ShowRecognised(TextBlock label, List<DnsEntry> entries)
        {
            if (entries.Count == 0) { label.Text = ""; return; }
            var lines = new List<string>();
            bool bad = false, warn = false;
            bool v6 = DirectDns.MachineHasIPv6();
            foreach (var e in entries)
            {
                if (!e.Ok) { bad = true; lines.Add("✗ " + e.Raw + " — " + e.Error); continue; }
                var line = "✓ " + DirectDns.Describe(e) + "   →  " + e.Engine;
                if (e.IsIPv6 && !v6) { warn = true; line += Environment.NewLine + "   " + Loc.T("Dns_WarnNoIPv6"); }
                if (!string.IsNullOrEmpty(e.Warning)) { warn = true; line += Environment.NewLine + "   " + e.Warning; }
                lines.Add(line);
            }
            label.Text = string.Join(Environment.NewLine, lines);
            label.Foreground = bad ? Theme.AccentRed : warn ? Theme.AccentAmber : Theme.AccentGreen;
        }

        // The servers that will be used: the chosen subscription's, else the typed ones.
        private List<DnsEntry> ValidDnsEntries()
        {
            var offer = ChosenDnsOffer();
            var all = offer != null ? offer.Servers.SelectMany(DirectDns.ParseMany).ToList()
                                    : DnsEntries(TxtDns1).Concat(DnsEntries(TxtDns2)).ToList();
            return all.Where(e => e.Ok)
                      .GroupBy(e => e.Engine, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        private string DnsSig() => string.Join("|", ValidDnsEntries().Select(e => e.Engine));

        private async void OnDnsTest(object sender, RoutedEventArgs e) => await RunDnsTestAsync();

        // Each server through a throwaway copy of the engine — see DirectDns.QueryAsync.
        private async Task RunDnsTestAsync()
        {
            var entries = ValidDnsEntries();
            if (entries.Count == 0) { LblDnsTest.Text = Loc.T("Set_DnsNothingToTest"); LblDnsTest.Foreground = Theme.AccentAmber; return; }
            BtnDnsTest.IsEnabled = false;
            LblDnsTest.Foreground = Theme.TextSecondary;
            LblDnsTest.Text = Loc.T("Set_DnsTesting");
            var lines = new List<string>();
            bool anyOk = false, anyBad = false, anyBlocked = false;
            try
            {
                CommitMihomoFields();   // the engine path may have just been typed on the Mihomo tab
                for (int i = 0; i < entries.Count; i++)
                {
                    var en = entries[i];
                    string text;
                    bool ok = false, blocked = false;
                    try
                    {
                        var answers = await DirectDns.QueryAsync(_editing, en.Engine,
                                          new[] { "example.com", "doubleclick.net" }, CancellationToken.None).ConfigureAwait(true);
                        ok = answers.Count > 0 && answers[0].Ok && answers[0].Addresses.Count > 0;
                        text = DirectDns.Summarize(answers);
                    }
                    catch (DirectDns.ProbeBlockedException ex) { blocked = true; text = ex.Message; }
                    catch (Exception ex) { text = Loc.T("Dns_TestFail", DirectDns.Shorten(ex.Message)); }
                    // Blocked = unknown, not dead: it neither passes nor fails the check.
                    anyOk |= ok; anyBad |= !ok && !blocked; anyBlocked |= blocked;
                    lines.Add((ok ? "✓ " : blocked ? "• " : "✗ ") + en.Host + " — " + text);
                }
            }
            finally { BtnDnsTest.IsEnabled = true; }

            _dnsTestedSig = DnsSig();
            // A server the probe can't reach under strict-route isn't shown to be dead — saving it
            // doesn't need the "it doesn't answer, save anyway?" confirmation.
            _dnsTestAnyOk = anyOk || anyBlocked;
            LblDnsTest.Text = string.Join(Environment.NewLine, lines);
            LblDnsTest.Foreground = anyBad ? (anyOk || anyBlocked ? Theme.AccentAmber : Theme.AccentRed)
                                  : anyBlocked ? Theme.AccentAmber : Theme.AccentGreen;
        }

        // Saving with the switch on: never hand the engine a resolver that doesn't answer without
        // saying so — a dead one here means no site outside the VPN opens.
        private async Task<bool> CommitDnsAsync()
        {
            bool on = SwitchDirectDns.IsChecked == true;
            var raw = new List<string>();
            if (!string.IsNullOrWhiteSpace(TxtDns1.Text)) raw.Add(TxtDns1.Text.Trim());
            if (!string.IsNullOrWhiteSpace(TxtDns2.Text)) raw.Add(TxtDns2.Text.Trim());

            if (on)
            {
                bool fromSub = ChosenDnsOffer() != null;
                var all = fromSub ? ValidDnsEntries() : DnsEntries(TxtDns1).Concat(DnsEntries(TxtDns2)).ToList();
                var bad = all.FirstOrDefault(x => !x.Ok);
                if (bad != null || all.Count == 0)
                {
                    TabDns.IsSelected = true;
                    await OverlayDialog.MessageAsync(this, Loc.T("Set_DnsTitle"),
                        bad != null ? Loc.T("Set_DnsBadEntry", bad.Raw, bad.Error) : Loc.T("Set_DnsNoEntries"));
                    return false;
                }
                if (_dnsTestedSig != DnsSig())
                {
                    TabDns.IsSelected = true;
                    await RunDnsTestAsync();
                }
                if (!_dnsTestAnyOk)
                {
                    TabDns.IsSelected = true;
                    if (!await OverlayDialog.ConfirmAsync(this, Loc.T("Set_DnsTitle"), Loc.T("Set_DnsDeadConfirm"),
                                                          Loc.T("Set_DnsSaveAnyway")))
                        return false;
                }
            }
            _editing.DirectDnsEnabled = on;
            _editing.DirectDns = raw;
            _editing.DirectDnsSource = _dnsSource ?? "";
            return true;
        }

        // Say plainly whether the Windows-level protection is actually in force. "Включено" in a
        // settings dialog while nothing is applied is exactly the kind of comfortable lie this
        // whole feature exists to remove, so the state is read from the firewall itself.
        private void UpdateFirewallStatus()
        {
            try
            {
                if (!_editing.FirewallKillSwitch)
                {
                    LblFirewallStatus.Text = Loc.T("Set_FirewallOff");
                    LblFirewallStatus.Foreground = Theme.TextSecondary;
                    return;
                }
                if (!MihomoSetup.IsFirewallTaskInstalled())
                {
                    LblFirewallStatus.Text = Loc.T("Set_FirewallNeedsSetup");
                    LblFirewallStatus.Foreground = Theme.AccentAmber;
                    return;
                }
                int rules = FirewallGuard.AppliedRuleCount();
                if (rules > 0)
                {
                    LblFirewallStatus.Text = Loc.T("Set_FirewallActive", rules);
                    LblFirewallStatus.Foreground = Theme.AccentGreen;
                }
                else
                {
                    LblFirewallStatus.Text = Loc.T("Set_FirewallNoPrograms");
                    LblFirewallStatus.Foreground = Theme.AccentAmber;
                }
            }
            catch (Exception ex) { Log.Error("firewall-status", ex); }
        }

        // The engine exe actually present on disk?
        private bool EngineExeExists()
        {
            var exe = (_editing.MihomoExePath ?? "").Trim();
            return exe.Length > 0 && File.Exists(exe);
        }

        private async void UpdateMihomoStatus()
        {
            // No exe on disk → not installed (a leftover scheduled task doesn't make it "set up").
            if (!EngineExeExists())
            {
                LblMihomoStatus.Text = Loc.T("Set_StatusNotInstalled");
                LblMihomoStatus.Foreground = Theme.AccentAmber;
                return;
            }
            if (!MihomoSetup.IsTaskInstalled())
            {
                LblMihomoStatus.Text = Loc.T("Set_StatusNotConfigured");
                LblMihomoStatus.Foreground = Theme.AccentAmber;
                return;
            }

            LblMihomoStatus.Text = Loc.T("Set_StatusConfiguredChecking");
            LblMihomoStatus.Foreground = Theme.TextSecondary;

            bool running = false;
            try { running = await new MihomoController(_editing).IsApiReachableAsync().ConfigureAwait(true); }
            catch { }

            LblMihomoStatus.Text = running ? Loc.T("Set_StatusRunning") : Loc.T("Set_StatusConfiguredNotRunning");
            LblMihomoStatus.Foreground = running ? Theme.AccentGreen : Theme.AccentAmber;
        }

        // ---------- mihomo ----------

        private void OnBrowseMihomo(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = Loc.T("Set_FilterMihomoExe"),
            };
            TrySetInitialDir(ofd, TxtMihomoExe.Text);
            if (ofd.ShowDialog(this) == true)
                TxtMihomoExe.Text = ofd.FileName;
        }

        private void OnBrowseWorkDir(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("Set_PickFileInFolder"),
                CheckFileExists = false,
                Filter = Loc.T("Set_FilterAllFiles"),
                FileName = "config.yaml",
            };
            // The work dir IS a folder — open INSIDE it, not its parent. (TrySetInitialDir
            // takes GetDirectoryName, which is right for a file seed like mihomo.exe but
            // would land us one level up for a folder.)
            try
            {
                var seed = (TxtMihomoWorkDir.Text ?? "").Trim();
                if (Directory.Exists(seed))
                    ofd.InitialDirectory = seed;
                else
                {
                    var dir = System.IO.Path.GetDirectoryName(seed);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        ofd.InitialDirectory = dir;
                }
            }
            catch { }
            if (ofd.ShowDialog(this) == true)
            {
                var dir = System.IO.Path.GetDirectoryName(ofd.FileName);
                if (!string.IsNullOrEmpty(dir)) TxtMihomoWorkDir.Text = dir;
            }
        }

        private async void OnSetupMihomo(object sender, RoutedEventArgs e)
        {
            CommitMihomoFields();

            // No engine yet? Download the latest stable build (api.byfox.dev/engine) to the
            // configured path (or a writable default) before setting it up.
            if (string.IsNullOrWhiteSpace(_editing.MihomoExePath) || !File.Exists(_editing.MihomoExePath))
            {
                if (!await DownloadEngineIfMissingAsync().ConfigureAwait(true)) return;
            }

            MihomoSetup.EnsureSecret(_editing);
            try
            {
                var controller = new MihomoController(_editing);
                controller.WriteConfig(MihomoConfigBuilder.BuildArmed(_editing));
            }
            catch (Exception ex)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Set_Error"), Loc.T("Set_ConfigWriteFailed", ex.Message)).ConfigureAwait(true);
                return;
            }

            string err;
            bool ok = MihomoSetup.InstallElevated(_editing, out err);
            UpdateMihomoStatus();
            await OverlayDialog.MessageAsync(this, ok ? Loc.T("Set_Done") : Loc.T("Set_Failed"),
                ok ? Loc.T("Set_SetupSuccess")
                   : Loc.T("Set_SetupFailed", err)).ConfigureAwait(true);
        }

        private void CommitMihomoFields()
        {
            // Empty exe field = "use the default location" → persist the default target so the
            // install flow / engine-installed detection still know where the engine lives.
            var exeText = (TxtMihomoExe.Text ?? "").Trim();
            _editing.MihomoExePath = exeText.Length > 0 ? exeText : AppConfigStore.DefaultMihomoExeTarget;
            _editing.MihomoWorkDir = (TxtMihomoWorkDir.Text ?? "").Trim();
            _editing.FirewallKillSwitch = SwitchFirewall.IsChecked == true;
        }

        // ---------- Launcher config file location (Настройки tab) ----------

        private void OnBrowseConfigPath(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = Loc.T("Set_ConfigSaveTitle"),
                FileName = "config.xml",
                Filter = Loc.T("Set_FilterConfigXml"),
                OverwritePrompt = false,   // pointing at an existing config is fine
            };
            TrySetInitialDir(sfd, TxtConfigPath.Text);
            if (sfd.ShowDialog(this) == true)
                TxtConfigPath.Text = sfd.FileName;
        }

        // ---------- Download the mihomo engine (used by «Настроить mihomo» when missing) ----------

        // Download the latest stable engine (api.byfox.dev/engine) to the configured path,
        // or to a writable default if none is set. Stamps the local version and updates the
        // path field. Returns false (with a message shown) if it couldn't be downloaded.
        private async Task<bool> DownloadEngineIfMissingAsync()
        {
            var target = (_editing.MihomoExePath ?? "").Trim();
            if (target.Length == 0) target = AppConfigStore.DefaultMihomoExeTarget;

            BtnSetupMihomo.IsEnabled = false;
            LblMihomoStatus.Foreground = Theme.TextSecondary;
            LblMihomoStatus.Text = Loc.T("Set_DeterminingVersion");
            try
            {
                var src = await UpdateService.ResolveMihomoSourceAsync().ConfigureAwait(true);
                if (src == null)
                {
                    LblMihomoStatus.Foreground = Theme.AccentRed;
                    LblMihomoStatus.Text = Loc.T("Set_NoConnection");
                    await OverlayDialog.MessageAsync(this, Loc.T("Set_Failed"),
                        Loc.T("Set_NoConnectionDesc")).ConfigureAwait(true);
                    return false;
                }

                _editing.MihomoExePath = target;
                await UpdateService.UpdateMihomoAsync(_editing, src.Version, src.ZipUrl,
                    p => Dispatcher.BeginInvoke(new Action(() => LblMihomoStatus.Text = Loc.T("Set_Downloading", src.Label, p))),
                    CancellationToken.None).ConfigureAwait(true);

                TxtMihomoExe.Text = target;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("settings-download-engine", ex);
                LblMihomoStatus.Foreground = Theme.AccentRed;
                LblMihomoStatus.Text = Loc.T("Set_DownloadFailedShort");
                await OverlayDialog.MessageAsync(this, Loc.T("Set_Failed"),
                    Loc.T("Set_DownloadFailed", ex.Message)).ConfigureAwait(true);
                return false;
            }
            finally
            {
                BtnSetupMihomo.IsEnabled = true;
            }
        }

        private static void TrySetInitialDir(FileDialog ofd, string seed)
        {
            try
            {
                if (string.IsNullOrEmpty(seed)) return;
                var dir = System.IO.Path.GetDirectoryName(seed);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    ofd.InitialDirectory = dir;
            }
            catch { }
        }

        // No scrolling in Settings: a tab whose content is taller than the window (Mihomo with its
        // routes table, DNS) grows the WINDOW to fit, up to the screen. Grow only — switching
        // tabs back and forth shouldn't make it jump.
        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != Tabs) return;
            Dispatcher.BeginInvoke(new Action(FitHeightToTab), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // DNS window: both tabs share one height — the taller of the two (normally «Статистика»),
        // fixed from the start, so switching «Настройки» ⇄ «Статистика» never resizes the window.
        // Runs in Loaded, before the first frame: the stats tab is laid out once, unseen.
        private void FitDnsWindowToStats()
        {
            try
            {
                TabDns.IsSelected = true;
                UpdateLayout();
                FitHeightToTab();
                TabDnsStats.IsSelected = true;   // ends on «Статистика», the tab the window opens with
                UpdateLayout();
                FitHeightToTab();
            }
            catch (Exception ex) { Log.Error("dns-fit-height", ex); }
        }

        private void FitHeightToTab()
        {
            try
            {
                if (!IsLoaded || WindowState != WindowState.Normal || Tabs.SelectedIndex < 0) return;
                var root = Content as FrameworkElement;
                if (root == null) return;
                root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
                double need = root.DesiredSize.Height + (ActualHeight - root.ActualHeight);
                if (need <= ActualHeight + 1) { root.InvalidateMeasure(); return; }
                var work = SystemParameters.WorkArea;
                double h = Math.Min(Math.Ceiling(need), work.Height);
                Height = h;
                if (Top + h > work.Bottom) Top = Math.Max(work.Top, work.Bottom - h);
            }
            catch (Exception ex) { Log.Error("settings-fit-height", ex); }
        }

        // ---------- Clear selection on empty-space click ----------

        // Clicking anywhere that isn't a table row / button / input clears the highlighted
        // selection in ALL tables (VPN variants + Programs + Links). Bubbling MouseDown: a
        // click on a row/button is caught by IsWithinInteractive and left alone.
        private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
            // Move focus onto the (focusable) root grid so any text box — e.g. the «Обновление»
            // period fields — fully loses focus on an empty-space click. Just clearing keyboard
            // focus leaves some Fluent controls showing their accent border; focusing a real
            // element clears it.
            try { Keyboard.Focus(RootGrid); } catch { }
        }

        // Don't let the «Обновление» tab auto-focus the first period field when it's opened. WPF
        // moves focus into the tab content on selection; we move it onto the (focusable) tab grid
        // instead — posted at ContextIdle so it runs AFTER WPF's own tab-in focus move, otherwise
        // the field grabs focus back.
        private void OnUpdateTabVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (UpdateTabRoot != null && UpdateTabRoot.IsVisible)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Only pull focus off a period field if WPF auto-focused one on tab-in —
                        // never steal a focus the user set themselves by clicking a field.
                        var f = Keyboard.FocusedElement;
                        if (f == TxtAppPeriod || f == TxtEnginePeriod || f == TxtGeoPeriod || f == TxtTplPeriod)
                            Keyboard.Focus(UpdateTabRoot);
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // True if the click landed on (or inside) something that owns the selection or is an
        // interactive control — clearing would fight its own behaviour (e.g. the «Изменить»
        // button needs the row to stay selected; clicking a row selects it).
        private static bool IsWithinInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.DataGridRow) return true;
                if (d is System.Windows.Controls.Primitives.DataGridColumnHeader) return true;
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (d is System.Windows.Controls.Primitives.TextBoxBase) return true;
                if (d is System.Windows.Controls.ComboBox) return true;
                if (d is System.Windows.Controls.ListViewItem) return true;
                if (d is System.Windows.Controls.TabItem) return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---------- Обновление (per-item update + periods) ----------

        private bool _updBusy;

        // Populate the update tab: period boxes from updates.json, info labels from the state.
        private void FillUpdatesUi()
        {
            TxtAppPeriod.Text = UpdateStateStore.AppPeriodDays.ToString();
            TxtEnginePeriod.Text = UpdateStateStore.EnginePeriodDays.ToString();
            TxtGeoPeriod.Text = UpdateStateStore.GeoPeriodDays.ToString();
            TxtTplPeriod.Text = UpdateStateStore.TemplatesPeriodDays.ToString();
            // The periods above are read back on «Сохранить»; the info lines (the .mmdb date read
            // from disk) only on the «Обновление» page, which the DNS window does not have.
            if (!_dnsOnly) RefreshUpdatesInfo();
        }

        private void RefreshUpdatesInfo()
        {
            var st = UpdateStateStore.State;
            LblAppInfo.Text = Loc.T("Upd_InfoVersion", VersionInfo.AppVersion, Stamp(st.AppLastCheck));
            LblEngineInfo.Text = Loc.T("Upd_InfoVersion", VersionInfo.MihomoVersion, Stamp(st.EngineLastCheck));
            LblGeoInfo.Text = Loc.T("Upd_InfoDate", GeoDateDisplay(), Stamp(st.GeoLastCheck));
            LblTplInfo.Text = Loc.T("Upd_InfoDate", TplDateDisplay(), Stamp(st.TemplatesLastCheck));
        }

        private static string Stamp(string s) => string.IsNullOrWhiteSpace(s) ? Loc.T("Upd_Never") : s.Trim();

        // GeoLite2 DB date — read from the installed .mmdb metadata; shown as dd.MM.yyyy.
        private static string GeoDateDisplay()
        {
            var d = GeoIpService.LocalDate();
            return d != null ? d.Value.ToString("dd.MM.yyyy") : Loc.T("Upd_Unknown");
        }

        private static string TplDateDisplay()
        {
            var d = VersionInfo.CatalogDate;
            return string.IsNullOrWhiteSpace(d) ? Loc.T("Upd_Unknown") : d.Trim();
        }

        // Read a period box: a positive integer, else the built-in default.
        private static int ParsePeriod(string s, int fallback)
            => int.TryParse((s ?? "").Trim(), out var v) && v > 0 ? v : fallback;

        // Persist all four period boxes into updates.json.
        private void CommitUpdatePeriods()
        {
            var st = UpdateStateStore.State;
            st.AppPeriodDays = ParsePeriod(TxtAppPeriod.Text, UpdateStateStore.DefaultAppPeriodDays);
            st.EnginePeriodDays = ParsePeriod(TxtEnginePeriod.Text, UpdateStateStore.DefaultEnginePeriodDays);
            st.GeoPeriodDays = ParsePeriod(TxtGeoPeriod.Text, UpdateStateStore.DefaultGeoPeriodDays);
            st.TemplatesPeriodDays = ParsePeriod(TxtTplPeriod.Text, UpdateStateStore.DefaultTemplatesPeriodDays);
            UpdateStateStore.Save();
        }

        // Dispatcher-safe progress/status writers (update-service callbacks fire off-thread).
        private void SetUpdProgress(int p)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(new Action(() => SetUpdProgress(p))); return; }
            UpdProgress.Visibility = Visibility.Visible;
            if (p < 0) { UpdProgress.IsIndeterminate = true; }
            else { UpdProgress.IsIndeterminate = false; UpdProgress.Value = p; }
        }

        private void SetUpdStatus(string s)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(new Action(() => SetUpdStatus(s))); return; }
            LblUpdStatus.Text = s ?? "";
        }

        private void SetUpdButtonsEnabled(bool on)
        {
            BtnUpdateApp.IsEnabled = on;
            BtnUpdateEngine.IsEnabled = on;
            BtnUpdateGeo.IsEnabled = on;
            BtnUpdateTpl.IsEnabled = on;
        }

        // Run one update action with the shared busy-guard, progress bar and status line.
        private async Task RunUpdateAction(Func<Task> body)
        {
            if (_updBusy) return;
            _updBusy = true;
            SetUpdButtonsEnabled(false);
            CommitUpdatePeriods();   // honour a just-typed period before checking
            try
            {
                await body().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("update-tab", ex);
                SetUpdStatus(Loc.T("Upd_FailedFmt", ex.Message));
            }
            finally
            {
                _updBusy = false;
                SetUpdButtonsEnabled(true);
                UpdProgress.Visibility = Visibility.Collapsed;
                RefreshUpdatesInfo();
            }
        }

        private async void OnUpdateApp(object sender, RoutedEventArgs e)
        {
            await RunUpdateAction(async () =>
            {
                SetUpdStatus(Loc.T("Upd_CheckingFmt", Loc.T("Upd_ItemApp")));
                var remote = await UpdateService.LatestAppVersionAsync(CancellationToken.None).ConfigureAwait(true);
                UpdateStateStore.MarkAppChecked();
                if (remote == null) { SetUpdStatus(Loc.T("Upd_NoConnection")); return; }
                if (!VersionInfo.IsNewer(remote, VersionInfo.AppVersion))
                {
                    SetUpdStatus(Loc.T("Upd_UpToDateFmt", Loc.T("Upd_ItemApp")));
                    return;
                }

                // Update available → download the archive + hand off to the staged updater, then exit.
                SetUpdProgress(0);
                await UpdateService.StartAppUpdateAsync(
                    s => SetUpdStatus(s),
                    p => SetUpdProgress(p),
                    CancellationToken.None).ConfigureAwait(true);
                SetUpdStatus(Loc.T("Upd_AppClosing"));
                SetUpdProgress(-1);
                if (Owner is MainWindow main) main.ShutdownForUpdate();
                else Application.Current.Shutdown();
            });
        }

        private async void OnUpdateEngine(object sender, RoutedEventArgs e)
        {
            await RunUpdateAction(async () =>
            {
                SetUpdStatus(Loc.T("Upd_CheckingFmt", Loc.T("Upd_ItemEngine")));
                var src = await UpdateService.ResolveMihomoSourceAsync(CancellationToken.None).ConfigureAwait(true);
                UpdateStateStore.MarkEngineChecked();
                // No version = api.byfox.dev and GitHub unreachable, only the old mirror answered: nothing to compare
                // against, so no update can be offered from here.
                if (src == null || string.IsNullOrWhiteSpace(src.Version)) { SetUpdStatus(Loc.T("Upd_NoConnection")); return; }
                if (!VersionInfo.IsNewer(src.Version, VersionInfo.MihomoVersion))
                {
                    SetUpdStatus(Loc.T("Upd_UpToDateFmt", Loc.T("Upd_ItemEngine")));
                    return;
                }

                CommitMihomoFields();   // ensure a target path (default when the field is empty)
                SetUpdProgress(0);
                await UpdateService.UpdateMihomoAsync(
                    _editing, src.Version, src.ZipUrl,
                    p => { SetUpdProgress(p); SetUpdStatus(Loc.T("Upd_DownloadingFmt", Loc.T("Upd_ItemEngine"), p)); },
                    CancellationToken.None).ConfigureAwait(true);
                SetUpdStatus(Loc.T("Upd_EngineUpdated", VersionInfo.MihomoVersion));
            });
        }

        private async void OnUpdateGeo(object sender, RoutedEventArgs e)
        {
            await RunUpdateAction(async () =>
            {
                SetUpdStatus(Loc.T("Upd_CheckingFmt", Loc.T("Upd_ItemGeo")));
                var remote = await UpdateService.LatestGeoDateAsync(CancellationToken.None).ConfigureAwait(true);
                UpdateStateStore.MarkGeoChecked();
                if (remote == null) { SetUpdStatus(Loc.T("Upd_NoConnection")); return; }

                var local = GeoIpService.LocalDate();
                bool dbMissing = !File.Exists(GeoIpService.DbPath);
                if (!dbMissing && local != null && remote.Value <= local.Value)
                {
                    SetUpdStatus(Loc.T("Upd_UpToDateFmt", Loc.T("Upd_ItemGeo")));
                    return;
                }

                SetUpdProgress(0);
                await UpdateService.UpdateGeoAsync(
                    remote.Value.ToString("yyyyMMdd"),
                    p => { SetUpdProgress(p); SetUpdStatus(Loc.T("Upd_DownloadingFmt", Loc.T("Upd_ItemGeo"), p)); },
                    CancellationToken.None).ConfigureAwait(true);
                SetUpdStatus(Loc.T("Upd_GeoUpdated", remote.Value.ToString("dd.MM.yyyy")));
            });
        }

        private async void OnUpdateTemplates(object sender, RoutedEventArgs e)
        {
            await RunUpdateAction(async () =>
            {
                SetUpdStatus(Loc.T("Upd_CheckingFmt", Loc.T("Upd_ItemTemplates")));
                SetUpdProgress(0);
                int n = await UpdateService.UpdateTemplatesAsync(
                    true, p => SetUpdProgress(p), CancellationToken.None).ConfigureAwait(true);
                SetUpdStatus(Loc.T("Upd_TemplatesUpdated", n));
            });
        }

        // ---------- Save / Cancel ----------

        private bool _saving, _closed;

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            // The DNS check below takes seconds; a second click in the meantime used to land
            // after the window had closed and crash on DialogResult.
            if (_saving || _closed) return;
            _saving = true;
            _saveGuard.Suspended = true;
            BtnSave.IsEnabled = false;
            bool ok;
            try
            {
                // Empty exe path is valid now (CommitMihomoFields fills in the default), so
                // there's nothing to block on here.
                ok = true;
                CommitMihomoFields();
                // Settings no longer shows the DNS page: leave the stored DNS as it is (no
                // DNS check on every Settings save).
                ok = ok && (!_dnsOnly || await CommitDnsAsync());
            }
            finally
            {
                _saving = false;
                BtnSave.IsEnabled = true;   // a failed check leaves the user free to retry
                _saveGuard.Suspended = false;
            }
            if (!ok || _closed) return;   // closed with ✕ / «Отмена» while the check ran

            try
            {
                if (ChkAutostart.IsChecked == true) Autostart.Enable();
                else Autostart.Disable();
            }
            catch { }

            // Relocate the launcher config if the user changed its path. Caller (MainWindow)
            // then saves _config, which writes to the new location.
            try { AppConfigStore.SetConfigPath(TxtConfigPath.Text); } catch { }

            // Persist the per-item update periods (updates.json, separate from the app config).
            try { CommitUpdatePeriods(); } catch { }

            Result = _editing;
            // Shown with Show() since 2026-11-27 — DialogResult exists only for ShowDialog().
            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }

        private void OnExitApp(object sender, RoutedEventArgs e)
        {
            ExitConfirmOverlay.Visibility = Visibility.Visible;
        }

        private void OnExitConfirm(object sender, RoutedEventArgs e)
        {
            ExitConfirmOverlay.Visibility = Visibility.Collapsed;

            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();

            if (Owner is MainWindow main)
                main.RealExit();
            else
                Application.Current.Shutdown();
        }

        private void OnExitCancel(object sender, RoutedEventArgs e)
        {
            ExitConfirmOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnExitOverlayBackdropClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!e.Handled)
                ExitConfirmOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnExitToastClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        // ---------- VPN status / diagnostics tab ----------

        private void OnVpnStatusChanged()
        {
            try { Dispatcher.BeginInvoke(new Action(RefreshRoutes)); } catch { }
        }

        private void OnRefreshDiag(object sender, RoutedEventArgs e) => RefreshDiagnostics();

        private async void OnShowConfig(object sender, RoutedEventArgs e)
        {
            // config.yaml carries every VPN's keys.
            if (!await LockPrompt.RequireAsync(this, _editing).ConfigureAwait(true)) return;
            string path = "", yaml;
            try
            {
                path = new MihomoController(_editing).ConfigPath;
                yaml = File.Exists(path)
                    ? File.ReadAllText(path)
                    : Loc.T("Set_ConfigNotCreated");
            }
            catch (Exception ex) { yaml = Loc.T("Set_ConfigReadFailed", ex.Message); }
            new ConfigViewerDialog(path, yaml) { Owner = this }.ShowDialog();
        }

        private async void RefreshDiagnostics()
        {
            RefreshRoutes();

            if (!EngineExeExists())
            {
                LblDiagEngine.Text = Loc.T("Set_DiagNotInstalled");
                LblDiagEngine.Foreground = Theme.AccentAmber;
                return;
            }
            if (!MihomoSetup.IsTaskInstalled())
            {
                LblDiagEngine.Text = Loc.T("Set_DiagNotConfigured");
                LblDiagEngine.Foreground = Theme.AccentAmber;
                return;
            }
            LblDiagEngine.Text = Loc.T("Set_StatusConfiguredChecking");
            LblDiagEngine.Foreground = Theme.TextSecondary;
            string ver = "";
            try { ver = await new MihomoController(_editing).GetVersionAsync().ConfigureAwait(true); }
            catch { }
            if (!string.IsNullOrEmpty(ver))
            {
                LblDiagEngine.Text = Loc.T("Set_DiagRunning", ver);
                LblDiagEngine.Foreground = Theme.AccentGreen;
            }
            else
            {
                LblDiagEngine.Text = Loc.T("Set_DiagNotResponding");
                LblDiagEngine.Foreground = Theme.AccentAmber;
            }
        }

        private void RefreshRoutes()
        {
            _routeRows.Clear();
            foreach (var r in VpnStatus.Routes)
                _routeRows.Add(new RouteRow(r));
            bool any = _routeRows.Count > 0;
            LstRoutes.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            LblNoRoutes.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- Row VMs ----------

        public class RouteRow
        {
            public string AppName { get; }
            public string SlotLabel { get; }
            public string ProxyName { get; }
            public string Target { get; }
            public string StatusLabel { get; }

            public RouteRow(VpnRoute r)
            {
                AppName = r.AppName;
                // The name alone (2026-11-27): the list number «№n» meant nothing a person sees.
                SlotLabel = "«" + r.SlotName + "»";
                ProxyName = r.ProxyName;
                Target = r.Target;
                StatusLabel = r.NodeUp ? Loc.T("Set_RouteUp") : Loc.T("Set_RouteDown");
            }
        }
    }
}
