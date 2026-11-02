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

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Tri-state "select all" for each table header (true=all, false=none, null=mixed).
        private bool? _allAppsChecked = false, _allSlotsChecked = false;
        public bool? AllAppsChecked { get => _allAppsChecked; set { _allAppsChecked = value; OnPropertyChanged(); } }
        public bool? AllSlotsChecked { get => _allSlotsChecked; set { _allSlotsChecked = value; OnPropertyChanged(); } }

        private readonly AppConfig _editing;
        private readonly ObservableCollection<AppRow> _appRows = new ObservableCollection<AppRow>();
        private readonly ObservableCollection<SlotRow> _slotRows = new ObservableCollection<SlotRow>();
        private readonly ObservableCollection<RouteRow> _routeRows = new ObservableCollection<RouteRow>();

        public SettingsWindow(AppConfig source, bool openDns = false)
        {
            InitializeComponent();
            if (openDns) TabDns.IsSelected = true;

            _editing = Clone(source);
            DataContext = this;   // header "select all" checkboxes bind to AllXChecked
            FillUi();

            GridApps.ItemsSource = _appRows;
            GridSlots.ItemsSource = _slotRows;
            LstRoutes.ItemsSource = _routeRows;

            RefreshAppRows();
            RefreshSlotRows();
            InitRowDrag();

            UpdateMihomoStatus();
            FillUpdatesUi();

            VpnStatus.Changed += OnVpnStatusChanged;
            Loaded += (s, e) =>
            {
                RefreshDiagnostics();
                Dispatcher.BeginInvoke(new Action(FitHeightToTab), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            Closed += (s, e) => { _closed = true; try { VpnStatus.Changed -= OnVpnStatusChanged; } catch { } };
        }

        private static AppConfig Clone(AppConfig c)
        {
            return new AppConfig
            {
                Version = c.Version,
                Language = c.Language,
                RemoteDesktopSafeMode = c.RemoteDesktopSafeMode,
                MihomoExePath = c.MihomoExePath,
                MihomoWorkDir = c.MihomoWorkDir,
                MihomoApiAddr = c.MihomoApiAddr,
                MihomoSecret = c.MihomoSecret,
                MixedPort = c.MixedPort,
                DirectDnsEnabled = c.DirectDnsEnabled,
                DirectDns = new List<string>(c.DirectDns ?? new List<string>()),
                DirectDnsSource = c.DirectDnsSource ?? "",
                VpnSlots =(c.VpnSlots ?? new List<VpnSlot>()).Select(s => new VpnSlot
                {
                    Id = s.Id,
                    Name = s.Name,
                    Kind = s.Kind,
                    Value = s.Value,
                    Enabled = s.Enabled,
                }).ToList(),
                Apps = (c.Apps ?? new List<AppEntry>()).Select(a => new AppEntry
                {
                    Id = a.Id,
                    Name = a.Name,
                    IsAllTraffic = a.IsAllTraffic,
                    AutoConnect = a.AutoConnect,
                    IconPath = a.IconPath,
                    RequiresVpn = a.RequiresVpn,
                    AutoIcon = a.AutoIcon,
                    KillSwitch = a.KillSwitch,
                    CheckUrl = a.CheckUrl,
                    SkipCheck = a.SkipCheck,
                    DisabledVpnSlotIds = new List<string>(a.DisabledVpnSlotIds ?? new List<string>()),
                    VpnSlotOrder = new List<string>(a.VpnSlotOrder ?? new List<string>()),
                    Programs = (a.Programs ?? new List<RuleProgram>()).Select(p => new RuleProgram
                    {
                        Name = p.Name, Path = p.Path, Arguments = p.Arguments, Aumid = p.Aumid, Launch = p.Launch,
                    }).ToList(),
                    Sites = (a.Sites ?? new List<RuleSite>()).Select(s => new RuleSite
                    {
                        Url = s.Url, ClashRuleUrl = s.ClashRuleUrl, Launch = s.Launch,
                        RuleItems = new List<string>(s.RuleItems ?? new List<string>()),
                        RuleFetchedUtc = s.RuleFetchedUtc,
                    }).ToList(),
                }).ToList(),
            };
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
            SwitchSafeMode.IsChecked = _editing.RemoteDesktopSafeMode;
            SwitchFirewall.IsChecked = _editing.FirewallKillSwitch;
            UpdateFirewallStatus();

            TxtConfigPath.Text = AppConfigStore.ConfigPath;
            LblConfigPathHint.Text = Loc.T("Set_DefaultPathHint", AppConfigStore.DefaultConfigPath);

            FillDnsUi();
        }

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
            bool anyOk = false, anyBad = false;
            try
            {
                CommitMihomoFields();   // the engine path may have just been typed on the Mihomo tab
                for (int i = 0; i < entries.Count; i++)
                {
                    var en = entries[i];
                    string text;
                    bool ok = false;
                    try
                    {
                        var answers = await DirectDns.QueryAsync(_editing, en.Engine,
                                          new[] { "example.com", "doubleclick.net" }, CancellationToken.None).ConfigureAwait(true);
                        ok = answers.Count > 0 && answers[0].Ok && answers[0].Addresses.Count > 0;
                        text = DirectDns.Summarize(answers);
                    }
                    catch (Exception ex) { text = Loc.T("Dns_TestFail", DirectDns.Shorten(ex.Message)); }
                    anyOk |= ok; anyBad |= !ok;
                    lines.Add((ok ? "✓ " : "✗ ") + en.Host + " — " + text);
                }
            }
            finally { BtnDnsTest.IsEnabled = true; }

            _dnsTestedSig = DnsSig();
            _dnsTestAnyOk = anyOk;
            LblDnsTest.Text = string.Join(Environment.NewLine, lines);
            LblDnsTest.Foreground = !anyOk ? Theme.AccentRed : anyBad ? Theme.AccentAmber : Theme.AccentGreen;
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

        // ---------- Row collections ----------

        private void RefreshAppRows()
        {
            int sel = GridApps.SelectedIndex;
            _appRows.Clear();
            foreach (var e in _editing.Apps.Where(a => a != null))
                _appRows.Add(new AppRow(e, _editing.VpnSlots));
            if (_appRows.Count > 0)
                GridApps.SelectedIndex = sel >= 0 && sel < _appRows.Count ? sel : 0;
            RecomputeAppsAll();
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

            // No engine yet? Download the latest stable build (GitHub → byfox fallback) to the
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
            _editing.RemoteDesktopSafeMode = SwitchSafeMode.IsChecked == true;
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

        // Download the latest stable engine (GitHub → byfox fallback) to the configured path,
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

        // ---------- Programs ----------

        private void OnAddApp(object sender, RoutedEventArgs e)
        {
            var dlg = new EditRuleDialog(null, _editing.VpnSlots) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _editing.Apps.Add(dlg.Result);
                RefreshAppRows();
                if (_appRows.Count > 0) GridApps.SelectedIndex = _appRows.Count - 1;
            }
        }

        private void OnEditApp(object sender, RoutedEventArgs e)
        {
            var entry = (GridApps.SelectedItem as AppRow)?.Entry;
            if (entry == null) return;
            int idx = _editing.Apps.IndexOf(entry);
            if (idx < 0) return;
            int sel = GridApps.SelectedIndex;
            var dlg = new EditRuleDialog(entry, _editing.VpnSlots) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _editing.Apps[idx] = dlg.Result;
                RefreshAppRows();
                GridApps.SelectedIndex = sel;
            }
        }

        private async void OnDeleteApp(object sender, RoutedEventArgs e)
        {
            // «Весь трафик» is built in — it can be switched off, never deleted.
            var targets = CheckedOrSelected(_appRows, GridApps.SelectedItem as AppRow)
                .Where(t => !t.IsAllTraffic).ToList();
            if (targets.Count == 0) return;
            string body = targets.Count == 1
                ? Loc.T("Set_DeleteOneFromList", targets[0].Name)
                : Loc.T("Set_DeleteRulesMany", targets.Count);
            var ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Set_DeleteRuleTitle"), body, Loc.T("Set_BtnDelete"), Loc.T("Set_BtnCancel")).ConfigureAwait(true);
            if (!ok) return;
            foreach (var t in targets) _editing.Apps.Remove(t);
            RefreshAppRows();
        }

        // Entries to act on: the checked rows, or — if none are checked — the selected row.
        private static List<AppEntry> CheckedOrSelected(IEnumerable<IHasEntry> rows, IHasEntry selected)
        {
            var checkedRows = rows.OfType<CheckRow>().Where(r => r.IsChecked)
                                  .OfType<IHasEntry>().Select(r => r.Entry).ToList();
            if (checkedRows.Count > 0) return checkedRows;
            return selected?.Entry != null ? new List<AppEntry> { selected.Entry } : new List<AppEntry>();
        }

        // Refresh the blackmatrix7 rule lists of every site (with a template) across all rules.
        private async void OnRefreshRules(object sender, RoutedEventArgs e)
        {
            var sites = _editing.Apps.Where(a => a != null)
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
                foreach (var s in sites)
                {
                    try { await ClashRuleService.RefreshAsync(s).ConfigureAwait(true); okCount++; total += s.RuleItems?.Count ?? 0; }
                    catch { }
                }
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

        // Both tables are reordered by dragging a row (GridRowDrag); «Всё остальное» stays first.
        private void InitRowDrag()
        {
            new GridRowDrag<AppRow>(GridApps, _appRows, r => r.Entry != null && r.Entry.IsAllTraffic, OnRulesReordered);
            new GridRowDrag<SlotRow>(GridSlots, _slotRows, null, OnSlotsReordered);
        }

        // The rows' new order → the master Apps list: rows take the positions rows held
        // (null entries, which have no row, stay where they were).
        private void OnRulesReordered(List<AppRow> order, AppRow dropped)
        {
            var slots = Enumerable.Range(0, _editing.Apps.Count).Where(i => _editing.Apps[i] != null).ToList();
            if (slots.Count != order.Count) { RefreshAppRows(); return; }
            for (int k = 0; k < slots.Count; k++) _editing.Apps[slots[k]] = order[k].Entry;
            GridApps.SelectedItem = dropped;
        }

        private void OnSlotsReordered(List<SlotRow> order, SlotRow dropped)
        {
            _editing.VpnSlots.Clear();
            _editing.VpnSlots.AddRange(order.Select(r => r.Slot));
            int at = order.IndexOf(dropped);
            RefreshSlotRows();   // renumbers «#»
            GridSlots.SelectedIndex = at;
        }

        // ---------- VPN slots ----------

        private void RefreshSlotRows()
        {
            int sel = GridSlots.SelectedIndex;
            _slotRows.Clear();
            for (int i = 0; i < _editing.VpnSlots.Count; i++)
                _slotRows.Add(new SlotRow(_editing.VpnSlots[i], i + 1));
            if (_slotRows.Count > 0)
                GridSlots.SelectedIndex = sel >= 0 && sel < _slotRows.Count ? sel : 0;
            RecomputeSlotsAll();
        }

        // No scrolling in Settings: a tab whose content is taller than the window (Mihomo with its
        // routes table, DNS) grows the WINDOW to fit, up to the screen. Grow only — switching
        // tabs back and forth shouldn't make it jump. «Основное» is left alone: its two tables
        // share whatever height there is.
        private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != Tabs) return;
            Dispatcher.BeginInvoke(new Action(FitHeightToTab), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FitHeightToTab()
        {
            try
            {
                if (!IsLoaded || WindowState != WindowState.Normal || Tabs.SelectedIndex <= 0) return;
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

        // The variants card sizes to its rows but never past half of the «Основное» tab, so
        // the rules table below gets whatever the variants don't use (50/50 at most).
        private void OnMainTabSizeChanged(object sender, SizeChangedEventArgs e)
        {
            CardSlots.MaxHeight = Math.Max(0, e.NewSize.Height / 2);
        }

        // «Обновить» (refresh: download at once, with progress) / «Состав» (what the subscription
        // does, list by list) — opened from the variant's own window (EditSlotDialog).
        private void ShowSubscription(VpnSlot slot, bool refresh, Window owner)
        {
            if (slot == null || slot.Kind != VpnSlotKind.ClashSub) return;
            int idx = _editing.VpnSlots.IndexOf(slot);
            var proxyName = ProxyParsers.ProxyNameFor(slot, idx + 1);

            var dlg = new SubscriptionDialog(_editing, proxyName, slot.Value, slot.Name)
            {
                Owner = owner,
                AutoRefresh = refresh,
                // The engine holds a subscription only while some tile rides it; then it re-reads
                // the nodes too. Changed rules reach the config through the main window's watchdog
                // (EnsureSubscriptionsCurrent). A 404 here just means no tile rides it.
                AfterRefresh = async () =>
                {
                    var controller = new MihomoController(_editing);
                    if (await controller.IsApiReachableAsync().ConfigureAwait(true))
                        await controller.RefreshProviderAsync(proxyName + "_prov").ConfigureAwait(true);
                },
            };
            dlg.ShowDialog();
        }

        private void OnAddSlot(object sender, RoutedEventArgs e)
        {
            var dlg = new EditSlotDialog(null, _editing.VpnSlots.Select(s => s.Name)) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Results.Count > 0)
            {
                // Several links pasted at once → several variants.
                foreach (var added in dlg.Results)
                {
                    _editing.VpnSlots.Add(added);
                    // A new variant joins NO existing rule's search: the user assigns it where it
                    // is wanted (Routing table or the rule itself). Rules list what they may NOT
                    // use, so without this every rule would silently start trying the new variant
                    // on its next connect — «не надо сразу включать его в перебор для всех правил».
                    foreach (var app in _editing.Apps ?? new List<AppEntry>())
                    {
                        if (app == null) continue;
                        if (app.DisabledVpnSlotIds == null) app.DisabledVpnSlotIds = new List<string>();
                        if (!app.DisabledVpnSlotIds.Contains(added.Id))
                            app.DisabledVpnSlotIds.Add(added.Id);
                    }
                }
                RefreshSlotRows();
                if (_slotRows.Count > 0) GridSlots.SelectedIndex = _slotRows.Count - 1;
            }
        }

        private void OnEditSlot(object sender, RoutedEventArgs e)
        {
            var idx = GridSlots.SelectedIndex;
            if (idx < 0 || idx >= _editing.VpnSlots.Count) return;
            var slot = _editing.VpnSlots[idx];
            var dlg = new EditSlotDialog(slot,
                _editing.VpnSlots.Where((s, i) => i != idx).Select(s => s.Name)) { Owner = this };
            dlg.ShowSubscription = refresh => ShowSubscription(slot, refresh, dlg);
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _editing.VpnSlots[idx] = dlg.Result;
                RefreshSlotRows();
                GridSlots.SelectedIndex = idx;
            }
        }

        private async void OnDeleteSlot(object sender, RoutedEventArgs e)
        {
            var targets = _slotRows.Where(r => r.IsChecked).Select(r => r.Slot).ToList();
            if (targets.Count == 0)
            {
                var sel = (GridSlots.SelectedItem as SlotRow)?.Slot;
                if (sel != null) targets.Add(sel);
            }
            if (targets.Count == 0) return;
            string body = targets.Count == 1
                ? Loc.T("Set_DeleteOneFromList", targets[0].Name)
                : Loc.T("Set_DeleteVariantsMany", targets.Count);
            var ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Set_DeleteVariantTitle"), body, Loc.T("Set_BtnDelete"), Loc.T("Set_BtnCancel")).ConfigureAwait(true);
            if (!ok) return;
            foreach (var t in targets) _editing.VpnSlots.Remove(t);
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
            if (tag == "apps") { bool v = AllAppsChecked != true; foreach (var r in _appRows) r.IsChecked = v; RecomputeAppsAll(); }
            else if (tag == "slots") { bool v = AllSlotsChecked != true; foreach (var r in _slotRows) r.IsChecked = v; RecomputeSlotsAll(); }
            e.Handled = true;
        }

        private void RecomputeAppsAll() => AllAppsChecked = TriState(_appRows);
        private void RecomputeSlotsAll() => AllSlotsChecked = TriState(_slotRows);

        private static bool? TriState<T>(ObservableCollection<T> rows) where T : CheckRow
        {
            if (rows.Count == 0) return false;
            int c = rows.Count(r => r.IsChecked);
            return c == 0 ? (bool?)false : (c == rows.Count ? (bool?)true : null);
        }

        // ---------- Clear selection on empty-space click ----------

        // Clicking anywhere that isn't a table row / button / input clears the highlighted
        // selection in ALL tables (VPN variants + Programs + Links). Bubbling MouseDown: a
        // click on a row/button is caught by IsWithinInteractive and left alone.
        private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
            GridSlots.SelectedIndex = -1;
            GridApps.SelectedIndex = -1;
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
            RefreshUpdatesInfo();
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
                // No version = GitHub unreachable, only the mirror answered: nothing to compare
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
            BtnSave.IsEnabled = false;
            bool ok;
            try
            {
                // Empty exe path is valid now (CommitMihomoFields fills in the default), so
                // there's nothing to block on here.
                CommitMihomoFields();
                ok = await CommitDnsAsync();
            }
            finally
            {
                _saving = false;
                BtnSave.IsEnabled = true;
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
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnExitApp(object sender, RoutedEventArgs e)
        {
            ExitConfirmOverlay.Visibility = Visibility.Visible;
        }

        private void OnExitConfirm(object sender, RoutedEventArgs e)
        {
            ExitConfirmOverlay.Visibility = Visibility.Collapsed;

            DialogResult = false;
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

        private void OnShowConfig(object sender, RoutedEventArgs e)
        {
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

        // Lets CheckedOrSelected pull the underlying AppEntry from a grid's row VM.
        private interface IHasEntry { AppEntry Entry { get; } }

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

        private static string BuildVpnLabel(AppEntry e, List<VpnSlot> slots)
        {
            return e.RequiresVpn ? Loc.T("Set_Yes") : Loc.T("Set_No");
        }

        public class AppRow : CheckRow, IHasEntry
        {
            public AppEntry Entry { get; }
            public string Name { get; }
            public string DisplayPath { get; }
            public string VpnLabel { get; }
            public string KillSwitchLabel { get; }

            public AppRow(AppEntry e, List<VpnSlot> slots)
            {
                Entry = e;
                Name = e.Name;
                DisplayPath = PathSummary(e);
                VpnLabel = BuildVpnLabel(e, slots);
                // Kill-switch has its own independent status, shown regardless of the VPN toggle.
                // (It only takes effect on routing for a rule that also uses VPN — see
                // MihomoConfigBuilder — but its stored On/Off state is always visible + editable.)
                KillSwitchLabel = e.KillSwitch ? Loc.T("Set_On") : Loc.T("Set_Off");
            }

            // What the tile actually launches: the items flagged Launch=true (programs by
            // name/exe, sites by URL). Falls back to "all programs, else all sites" — exactly
            // the rule AppLauncher uses — so the column reflects what a click will do.
            private static string LaunchSummary(AppEntry e)
            {
                var progs = (e.Programs ?? new List<RuleProgram>()).Where(p => p != null).ToList();
                var sites = (e.Sites ?? new List<RuleSite>()).Where(s => s != null && !string.IsNullOrWhiteSpace(s.Url)).ToList();
                var pRun = progs.Where(p => p.Launch).ToList();
                var sRun = sites.Where(s => s.Launch).ToList();
                if (pRun.Count == 0 && sRun.Count == 0)
                {
                    if (progs.Count > 0) pRun = progs; else sRun = sites;
                }
                var parts = new List<string>();
                foreach (var p in pRun)
                    parts.Add(string.IsNullOrWhiteSpace(p.Name)
                        ? System.IO.Path.GetFileName(p.Path ?? "") : p.Name);
                foreach (var s in sRun) parts.Add(s.Url);
                return parts.Count > 0 ? string.Join(", ", parts) : "—";
            }

            // The ADDRESS of the one item the tile launches — full exe path, the AUMID when a
            // Store app has no path, the site URL, or (for a template) the full rule-list URL.
            // Same selection as LaunchSummary (which showed friendly names instead): the item
            // flagged «Плитка», falling back to the first program, else the first site — the
            // rule AppLauncher itself follows. A rule with a single entry therefore always
            // shows THAT entry, checkbox or not.
            private static string PathSummary(AppEntry e)
            {
                var progs = (e.Programs ?? new List<RuleProgram>())
                    .Where(p => p != null && (!string.IsNullOrWhiteSpace(p.Path) || !string.IsNullOrWhiteSpace(p.Aumid)))
                    .ToList();
                // A template is a site with no URL of its own, only a rule-list address. It's
                // still the rule's one and only entry, so it counts here — otherwise a
                // template-only rule showed an empty «Путь».
                var sites = (e.Sites ?? new List<RuleSite>())
                    .Where(s => s != null && (!string.IsNullOrWhiteSpace(s.Url) || !string.IsNullOrWhiteSpace(s.ClashRuleUrl)))
                    .ToList();

                var prog = progs.FirstOrDefault(p => p.Launch);
                var site = sites.FirstOrDefault(s => s.Launch);
                if (prog == null && site == null)
                {
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
                var num = r.SlotNumber > 0 ? "№" + r.SlotNumber + " " : "";
                SlotLabel = num + "«" + r.SlotName + "»";
                ProxyName = r.ProxyName;
                Target = r.Target;
                StatusLabel = r.NodeUp ? Loc.T("Set_RouteUp") : Loc.T("Set_RouteDown");
            }
        }

        public class SlotRow : CheckRow
        {
            public VpnSlot Slot { get; }
            public int Number { get; }
            public string Name { get; }
            public string KindLabel { get; }
            public string ValueLabel { get; }
            public string EnabledLabel { get; }
            public string StatusLabel { get; }

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
                // Full value — no hard 60-char cut. That clipped the text before the column
                // even ran out of room, so a wide window showed "…" next to empty space. The
                // column's text style trims it visually when it genuinely doesn't fit.
                ValueLabel = s.Value ?? "";
                EnabledLabel = s.Enabled ? "✓" : "—";   // glyphs, no localization

                // What the last connect learned about this variant. Blank until something
                // has actually checked it — an empty cell says "not checked", which is the
                // truth, while a guess here would be read as a verdict.
                switch (SlotHealth.Get(s.Id))
                {
                    case SlotState.Alive: StatusLabel = Loc.T("Set_SlotAnswers"); break;
                    case SlotState.Dead: StatusLabel = Loc.T("Set_SlotNoAnswer"); break;
                    default: StatusLabel = ""; break;
                }
            }
        }
    }
}
