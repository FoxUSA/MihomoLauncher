using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using Image = System.Windows.Controls.Image;
using WinForms = System.Windows.Forms;

namespace MihomoLauncher.Views
{
    public partial class MainWindow : FluentWindow
    {
        private enum UiState { Idle, Checking, Done }

        // Patience beats speed here: the user's mother is not going to press «Подключить» a
        // second time, so every timeout is set to "wait it out" rather than "fail fast". A
        // connect that takes a minute longer is invisible; a rule left unconnected is not.
        private const int SiteCheckTimeoutSeconds = 15;
        private const int SiteCheckAttempts = 3;        // per rule, per variant
        private const int NodeUpWaitSeconds = 20;
        // How many times the whole variant sweep is repeated for rules that are still failing.
        private const int ConnectPasses = 2;
        // At logon everything is cold: the link is "available" while DHCP/DNS are still
        // settling, and a WireGuard handshake through it regularly needs more than a warm
        // machine does. That's the whole of "autostart didn't connect, I pressed the button
        // and it connected instantly" — by then the tunnel is warm.
        private const int NodeUpWaitColdSeconds = 40;

        // Rules that ended up unconnected are retried by the launcher itself, on this period,
        // for as long as they stay unconnected. Nobody has to press anything.
        private const int RuleRetryPeriodSeconds = 90;
        // After this many failures in a row a rule is clearly not coming back by itself: the
        // retry slows to hourly and says so once, instead of failing in silence for days.
        private const int RetryFailsBeforeQuiet = 6;
        private DispatcherTimer _retryTimer;
        private bool _retryBusy;

        // (What used to be an in-memory "stopped by hand" set is now AppEntry.AutoConnect: the
        // same decision, but persisted, so it survives a restart instead of being forgotten.)
        // App.Id -> when it may be tried again. A rule that keeps failing (its site is down
        // everywhere, say) backs off to at most ~12 minutes instead of walking every variant
        // every 90 seconds forever — but it never stops being retried.
        private readonly Dictionary<string, DateTime> _retryNextAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _retryFails = new Dictionary<string, int>();
        // How long to wait for mihomo's API after starting its task. A cold start at logon
        // (TUN adapter + geo databases) is regularly slower than the old 10s allowance, which
        // is one way the "engine didn't start" message showed up on a healthy machine.
        private const int EngineStartWaitSeconds = 15;
        private const int EngineRetryWaitSeconds = 20;

        private AppConfig _config;
        private MihomoController _mihomo;
        private TrafficLogWindow _logWindow;    // modeless live log, one at a time
        private UiState _state = UiState.Idle;
        private CancellationTokenSource _checkCts;
        private bool _hasChecked;
        // App.Id currently being reconnected inline via its tile's ⟳ button (spins that
        // tile's icon and keeps the rest of the UI visible — no full-screen overlay).
        private string _reconnectingId;
        private UpdateCheckResult _update;
        private bool _updating;
        // mihomo engine present (exe on disk) AND its scheduled task registered. Cached so
        // the banner/tiles don't shell out to schtasks on every redraw; refreshed by
        // RefreshEngineState() at startup and after any install/settings change.
        private bool _engineReady;
        // Installed, but it won't come up. Turns the install button into «Переустановить
        // движок» so the cure is on screen instead of only inside an error dialog.
        private bool _engineBroken;

        // "kill mihomo" safety timer: every KillPeriodSeconds it force-stops mihomo (via the
        // scheduled task's /End — the only way a non-admin can stop the elevated engine) so a
        // broken VPN config can't lock you out while setting the launcher up remotely. Paused
        // by default; the play/pause control on the main screen runs it.
        private const int KillPeriodSeconds = 60;
        private DispatcherTimer _killTimer;
        private int _killSecondsLeft;
        private bool _killRunning;
        // Guards the safe-mode switch handler against re-entrancy when we set IsChecked in code.
        private bool _suppressSafeModeEvent;

        // Engine watchdog. mihomo is a separate elevated process, and it does go away under
        // us — killed, wedged, or left alive with TUN down by a shutdown whose /End didn't
        // land. Nothing noticed: the tiles kept saying «подключено» while not one packet was
        // routed, and the only cure was closing the launcher, starting it again and restarting
        // every app. So: look at the engine on a timer, put it back if it can be, and if it
        // can't — stop claiming anything is connected.
        // The adapter name the running engine reports for its own TUN — so the firewall layer
        // recognises OUR tunnel even if it ever ends up outside 198.18.0.0/16.
        private string _tunDevice = "";
        private int _fwSyncing;

        private const int HealthPeriodSeconds = 20;
        private DispatcherTimer _healthTimer;
        private bool _healthBusy;
        // True once the watchdog has reported the engine gone, so the log gets one line per
        // outage instead of one per tick.
        private bool _engineLost;
        // Which "why the engine won't come up" lines have already been written for the CURRENT
        // outage. The watchdog retries every 20 s and a bad night used to fill app.log with a
        // thousand copies of the same two lines. Cleared the moment the engine answers.
        private readonly HashSet<string> _engineFailLogged = new HashSet<string>();
        // How many times the control API has been moved to another port because the configured
        // one was squatted. Two is already generous — a third would mean we're chasing a
        // program that grabs whatever we take, and silently renumbering forever helps nobody.
        private const int MaxApiPortMoves = 2;
        private int _apiPortMoves;

        // App.Id -> launchable now (direct-OK or assigned to a working VPN node).
        private readonly HashSet<string> _workingApps = new HashSet<string>();
        // App.Id -> proxy name it is routed through (only VPN-assigned apps).
        private readonly Dictionary<string, string> _assignment = new Dictionary<string, string>();
        // What _assignment looked like the last time it was actually pushed to the engine. This
        // is what tells us which rules MOVED, and therefore whose already-open sockets have to
        // be cut — see ApplyFinalConfig.
        private readonly Dictionary<string, string> _appliedAssignment = new Dictionary<string, string>();
        // proxy name -> which configured variant (number + name) it came from.
        private readonly Dictionary<string, ProxySlotInfo> _proxySlot = new Dictionary<string, ProxySlotInfo>();
        // rule Id -> VPN last picked on its tile's plate (this session only; the rule's settings
        // are not touched by a pick). Lets the ⟳ work on a rule that ticks no VPN of its own.
        private readonly Dictionary<string, string> _platePick = new Dictionary<string, string>();

        // The VPN the tile's ⟳ / ▷ should use when the rule's own settings tick none: the one it is
        // on right now, else the one last picked on its plate; null = nothing to go through.
        private string TileOwnSlotId(AppEntry e)
        {
            string proxy, pick;
            ProxySlotInfo info;
            var enabledIds = new HashSet<string>((_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).Select(s => s.Id));
            if (_assignment.TryGetValue(e.Id, out proxy) && proxy != null && _proxySlot.TryGetValue(proxy, out info)
                && info != null && enabledIds.Contains(info.Id)) return info.Id;
            if (_platePick.TryGetValue(e.Id, out pick) && enabledIds.Contains(pick)) return pick;
            return null;
        }
        // proxy name -> resolved VPN server geo ("Франция, Париж"), filled in the background.
        private readonly Dictionary<string, string> _proxyGeo = new Dictionary<string, string>();
        // proxy name -> the parsed proxy it came from (so per-tile reconnect can rebuild
        // the config while keeping every other app on its current node).
        private readonly Dictionary<string, ParsedProxy> _parsedProxy = new Dictionary<string, ParsedProxy>();

        private WinForms.NotifyIcon _trayIcon;
        private bool _allowClose;

        // Launched by autostart (see Autostart.SilentArg): the window stays hidden in the tray
        // and the connect flow runs by itself. While nothing is on screen there's no one to
        // answer a dialog, so ShowDialog() only logs — until the user opens the window.
        private readonly bool _autostart;
        private bool _hidden;

        public MainWindow(bool autostart = false)
        {
            InitializeComponent();
            _autostart = autostart;
            _hidden = autostart;

            _config = AppConfigStore.Load();
            _mihomo = new MihomoController(_config);

            // Point VersionInfo at the engine so the version comes from the binary rather than
            // from updates.json, and correct that record if it had drifted.
            VersionInfo.EngineExePath = _config.MihomoExePath ?? "";
            try { VersionInfo.SyncEngineVersionFromBinary(); } catch { }

            SetTitleCaption();
            ApplyAppIcon();

            SetState(UiState.Idle);

            InitTrayIcon();
            App.ShowRequested += OnShowRequested;

            // Language toggle: show the current language on the title-bar button and re-render
            // code-driven UI (tiles + banner) live whenever the language changes.
            UpdateLangButton();
            Loc.LanguageChanged += OnLanguageChanged;
            SyncDnsButton();
            StartDnsStats();

            // Title-bar icons tint on hover: language green, website blue, exit red.
            var rest = Theme.TextSecondary;
            BtnLang.MouseEnter += (s, e) => { IcoLang.Foreground = Theme.AccentGreen; LangCode.Foreground = Theme.AccentGreen; };
            BtnLang.MouseLeave += (s, e) => { IcoLang.Foreground = rest; LangCode.Foreground = rest; };
            BtnWebsite.MouseEnter += (s, e) => IcoWebsite.Foreground = Theme.AccentBlue;
            BtnWebsite.MouseLeave += (s, e) => IcoWebsite.Foreground = rest;
            BtnSettings.MouseEnter += (s, e) => IcoSettings.Foreground = Theme.TextPrimary;
            BtnSettings.MouseLeave += (s, e) => IcoSettings.Foreground = rest;
            BtnExitApp.MouseEnter += (s, e) => IcoExit.Foreground = Theme.AccentRed;
            BtnExitApp.MouseLeave += (s, e) => IcoExit.Foreground = rest;

            // "Remote desktop session" safe-mode timer; reflect the persisted flag (off by
            // default) on the switch + monitor.
            _killTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _killTimer.Tick += KillTimerTick;
            _killSecondsLeft = KillPeriodSeconds;
            SyncSafeModeUi();

            _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HealthPeriodSeconds) };
            _healthTimer.Tick += HealthTick;
            _healthTimer.Start();
            InitStats();

            _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RuleRetryPeriodSeconds) };
            _retryTimer.Tick += RetryTick;
            _retryTimer.Start();

            // An adapter appearing or losing its address, a DHCP renew, a remote session opening —
            // all change what the firewall kill-switch must leave alone. Twenty seconds is a long
            // time to be wrong about that, so react to the event as well as to the tick.
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

            // Warm up mihomo on launch and immediately ARM the kill-switch: VPN apps are
            // blocked (REJECT) until mom presses «Подключить». Since the launcher runs at
            // logon, those apps never reach the internet directly, even before connecting.
            SizeChanged += OnWindowSizeChanged;
            InitWindowSizing();
            InitTileDrag();

            // A tile icon is fetched once, when the rule is saved (and at startup). If the network
            // blinked right then — an engine reload is enough — the tile stayed blank until the next
            // launch. Every few minutes, fill in whatever is still missing.
            var iconRetry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
            iconRetry.Tick += async (s, e) =>
            {
                if (IconFetcher.AnyMissing(_config.Apps)) await EnsureAutoIconsAsync(checkServer: false).ConfigureAwait(true);
            };
            iconRetry.Start();
            // A rule saved with a new name gets its icon in the background — show it on arrival.
            IconFetcher.IconDownloaded += () => Dispatcher.BeginInvoke(new Action(async () =>
                await EnsureAutoIconsAsync(checkServer: false).ConfigureAwait(true)));
            // The template catalog and its lists live on api.byfox.dev, which asks to be checked
            // daily — and this window may stay open for weeks. Each tick costs a 1–2 KB manifest
            // at most once a day (the period gate); lists download only when their template moved.
            var contentRefresh = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            contentRefresh.Tick += async (s, e) =>
            {
                try { await UpdateService.UpdateTemplatesAsync(false, null, CancellationToken.None).ConfigureAwait(true); }
                catch { }
                await RefreshLinkRulesAsync().ConfigureAwait(true);
                await EnsureAsnDbAsync().ConfigureAwait(true);
            };
            contentRefresh.Start();
            Loaded += async (s, e) =>
            {
                // Show the engine status (install banner / status banner) immediately, BEFORE
                // the slow startup work below, so the window isn't blank for a few seconds. The
                // check is instant when the engine is absent (File.Exists short-circuits the
                // schtasks query).
                RefreshEngineState();

                // Where every VPN exits, right away — it needs neither the engine nor a connection
                // (2026-11-21: at the end of the start-up it came ~5 min late, after the autoconnect).
                _ = ResolveSlotGeosAsync();

                try
                {
                    // First run: if the VPN engine isn't installed yet, offer to download +
                    // set it up now (GitHub → byfox fallback). Returns quickly if present.
                    // Skipped on autostart — the prompt (and its UAC) would land on a hidden
                    // window; the install banner is waiting when the user opens it.
                    if (!_autostart)
                        await EnsureEngineInstalledAsync(CancellationToken.None).ConfigureAwait(true);

                    // Engine is on disk + set up now → drop the "engine missing" banner right
                    // away. The arm/heal/icons/rules work below takes a couple seconds, and
                    // leaving the warning up that whole time made users re-click, unsure it took.
                    RefreshEngineState();

                    // The engine's lifetime is tied to the launcher's, full stop: up here, down
                    // in App.OnExit. Both things that depend on it — the kill-switch and (when
                    // the DNS tab isn't set to "system") acting as the machine's resolver — are
                    // enforced by its TUN interface, so anything cleverer than "always on while
                    // the launcher runs" silently turns one of them off.
                    //
                    // Arm the kill-switch first (uses version-independent process names), then
                    // self-heal Store apps: a versioned WindowsApps path changes on update, so
                    // resolve the stable AUMID + current path (no re-setup).
                    if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                    {
                        await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                        // The engine answering does NOT mean it is the one we just started: a
                        // previous exit can leave its own behind, alive but with TUN switched
                        // off, and we'd adopt it and route nothing. One health pass right here
                        // catches that immediately instead of up to 20 s later.
                        await CheckEngineHealthAsync().ConfigureAwait(true);
                        // Start collecting the engine's verdicts NOW, not when the log window is
                        // opened — by then the interesting block has already happened.
                        EngineLogStore.Instance.Start(_config);
                    }
                    // A program updated into a new version folder while we were off: point its
                    // rule at the new exe first, so the kill-switch below protects the right file.
                    RepairProgramPaths("старт", force: true);
                    // Put the Windows-level kill-switch in place before anything else is tried.
                    // It does not need the engine, so it must not wait for it either.
                    SyncFirewallSoon();
                    // The rules' auto-tracking and the folder watchers (2026-11-22). Needs neither
                    // the engine nor a connection: helpers that go online are found by their own
                    // TCP connections, so a rule is ready before anything is connected.
                    StartAutoTrack();
                    await AutoHealStoreAppsAsync().ConfigureAwait(true);
                    await EnsureAutoIconsAsync().ConfigureAwait(true);
                    MigrateTemplateUrls();
                    await RefreshLinkRulesAsync().ConfigureAwait(true);
                    await EnsureAsnDbAsync().ConfigureAwait(true);
                }
                catch (Exception ex) { Log.Error("startup", ex); }
                StartAutoTrack();   // idempotent: also when the start-up above threw before it

                // Reflect whether the engine ended up installed (mom may have pressed «Позже»):
                // show/hide the install banner and re-render the status banner accordingly.
                RefreshEngineState();

                // An install from before the engine service: move it over, by itself (silently
                // when the old firewall task can do it, else with Windows' own admin prompt).
                _ = MoveEngineToServiceAsync();

                // Check for updates in the background; reveal the button if any is available.
                try { await CheckForUpdatesAsync().ConfigureAwait(true); }
                catch (Exception ex) { Log.Error("update-check", ex); }

                // Every start — at logon or by hand — brings back exactly what was on when the
                // launcher last closed (or the machine went down): the «ON» button is gone
                // (2026-11-14). On autostart the window is hidden; the tray tooltip carries the
                // outcome.
                if (!_engineReady)
                {
                    Log.Info("Запуск: движок не установлен — восстановление подключений пропущено.");
                    UpdateTrayText();
                }
                else
                {
                    try { await AutoConnectAtStartupAsync().ConfigureAwait(true); }
                    catch (Exception ex) { Log.Error("autostart-connect", ex); }
                }
                // Where every VPN exits — for the picker and the grey plates, connected or not.
                _ = ResolveSlotGeosAsync();
            };

            Closing += (s, e) =>
            {
                if (!_allowClose)
                {
                    e.Cancel = true;
                    _hidden = true;   // back in the tray: no one can answer an in-window dialog
                    AccessLock.Lock();   // whoever opens it from the tray next asks again
                    Hide();
                    return;
                }
                try { App.ShowRequested -= OnShowRequested; } catch { }
                try { Loc.LanguageChanged -= OnLanguageChanged; } catch { }
                try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
                try { _killTimer?.Stop(); } catch { }
                // Before the engine is stopped in App.OnExit — otherwise the watchdog would
                // race the shutdown and start it right back up.
                try { _healthTimer?.Stop(); } catch { }
                try { _retryTimer?.Stop(); } catch { }
                try { _checkCts?.Cancel(); } catch { }
                try { if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; } } catch { }
            };
        }

        private void OnShowRequested() => ShowFromTray();

        private void OnNetworkAddressChanged(object sender, EventArgs e) => SyncFirewallSoon();

        // ---------- Language ----------

        // Toggle RU↔EN, persist the choice, and apply it. Loc.Apply swaps the string
        // dictionary (XAML {DynamicResource} updates live) and raises LanguageChanged.
        private void OnToggleLanguage(object sender, RoutedEventArgs e)
        {
            var next = Loc.Current == AppLang.En ? AppLang.Ru : AppLang.En;
            _config.Language = next == AppLang.En ? "en" : "ru";
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("lang-save", ex); }
            Loc.Apply(next);
        }

        private void OnLanguageChanged()
        {
            // XAML strings refresh themselves via DynamicResource. Re-render the parts built in
            // code: the title-bar toggle caption, the status banner for the current state, and
            // the tiles (their labels are composed in C#).
            try
            {
                UpdateLangButton();
                SyncDnsButton();
                UpdateKillTimerUi();
                UpdateTrayText();
                if (_state == UiState.Idle) SetState(UiState.Idle);
                else if (_state == UiState.Done) UpdateDoneBanner();
                RebuildTiles();
                RenderStats();

                // Tile geo (country/city) is language-specific — drop the cached names and
                // re-resolve in the new language; ResolveProxyGeosAsync refreshes the tiles.
                _proxyGeo.Clear();
                _slotGeo.Clear();
                GeoIpService.ClearCache();
                _ = ResolveProxyGeosAsync();
            }
            catch (Exception ex) { Log.Error("lang-changed", ex); }
        }

        private void UpdateLangButton()
        {
            try { LangCode.Text = Loc.Current == AppLang.En ? "EN" : "RU"; } catch { }
        }

        // Version caption in the title bar: launcher from manifest.json, engine from the exe.
        // WPF-UI 4.3's TitleBar draws no title of its own, so the text goes into our own
        // TxtCaption (TitleBar.Header); Title is set too, purely to keep the property honest.
        private void SetTitleCaption()
        {
            try
            {
                _captionFull = VersionInfo.TitleCaption;
                TxtCaption.Text = _captionFull;
                // The width may already be set: pick the variant that fits it now.
                if (ActualWidth > 0) ApplyChromeFit(ActualWidth);
            }
            catch (Exception ex) { Log.Error("title-caption", ex); }
        }

        // Title-bar globe: open the project website in the default browser.
        private void OnOpenWebsite(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://byfox.dev") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open-website", ex); }
        }

        // Title-bar log: the engine's live verdict list — what it blocked and why. Modeless, so
        // the user can keep clicking around (and reproduce the block) while watching it.
        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            if (_config == null) return;
            try
            {
                // The feed runs from startup (see EngineLogStore.Start in Loaded), so the window
                // opens already showing what happened BEFORE the user thought to look.
                if (!EngineLogStore.Instance.IsRunning) EngineLogStore.Instance.Start(_config);
                if (_logWindow != null && _logWindow.IsLoaded) { _logWindow.Activate(); return; }
                _logWindow = new TrafficLogWindow(_config) { Owner = this };
                _logWindow.Closed += (s2, e2) => _logWindow = null;
                _logWindow.Show();
            }
            catch (Exception ex) { Log.Error("open-log", ex); }
        }

        // Title-bar power: fully quit (not just hide to tray), same confirmation as Settings'
        // «Выйти из программы» — red confirm button. RealExit also stops the mihomo engine.
        private async void OnExitApp(object sender, RoutedEventArgs e)
        {
            bool ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Set_ExitConfirmTitle"), Loc.T("Set_ExitConfirmBody"),
                Loc.T("Set_BtnExit"), Loc.T("Set_BtnCancel"), Wpf.Ui.Controls.ControlAppearance.Danger).ConfigureAwait(true);
            if (ok) RealExit();
        }

        // ---------- "Remote desktop session" safe mode ----------

        // User flipped the main-screen switch: persist it and start/stop monitoring.
        private void OnSafeModeToggle(object sender, RoutedEventArgs e)
        {
            if (_suppressSafeModeEvent) return;
            bool on = SwitchSafeMode.IsChecked == true;
            _config.RemoteDesktopSafeMode = on;
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("safemode-save", ex); }
            if (on) StartSafeMode(); else StopSafeMode();
            ApplyRdpModeRouting(on);
            StatusFoot.Text = Loc.T(on ? "Main_KillTimerOn" : "Main_KillTimerOff");
        }

        // The mode also lets the live remote desktop through (TUN route-exclude + firewall) — or
        // stops letting it through. Both live in the config and the firewall plan, so rebuild
        // them now rather than at the next unrelated change. The first engine stop is still 60 s
        // after switching on (StartSafeMode), never at once.
        private void ApplyRdpModeRouting(bool on)
        {
            Log.Info("Режим RDP " + (on ? "включён: движок будет останавливаться раз в 60 с, удалённый рабочий стол пропускается мимо туннеля и блокировок."
                                        : "выключен: удалённый рабочий стол идёт по правилам, как всё остальное."));
            ReapplyLiveConfig(on ? "режим RDP включён" : "режим RDP выключен");
            SyncFirewallSoon();
        }

        private void StartSafeMode()
        {
            _killRunning = true;
            _killSecondsLeft = KillPeriodSeconds;
            _killTimer.Start();
            UpdateKillTimerUi();
        }

        private void StopSafeMode()
        {
            _killRunning = false;
            _killTimer.Stop();
            _killSecondsLeft = KillPeriodSeconds;
            UpdateKillTimerUi();
        }

        // Reflect the persisted safe-mode flag on the switch + monitor without firing the
        // user-toggle handler. Called at startup and after Settings / the install prompt.
        private void SyncSafeModeUi()
        {
            _suppressSafeModeEvent = true;
            try { SwitchSafeMode.IsChecked = _config.RemoteDesktopSafeMode; } catch { }
            _suppressSafeModeEvent = false;

            if (_config.RemoteDesktopSafeMode) { if (!_killRunning) StartSafeMode(); }
            else { if (_killRunning) StopSafeMode(); }
            UpdateKillTimerUi();
        }

        // Tick once a second WHILE monitoring: the countdown only advances when a mihomo
        // process is actually running. No engine → hold at full and wait; when it appears the
        // countdown runs and, at zero, the engine is force-stopped and the countdown restarts.
        private void KillTimerTick(object sender, EventArgs e)
        {
            if (!IsMihomoProcessRunning())
            {
                _killSecondsLeft = KillPeriodSeconds;   // idle: don't count, wait for the engine
                UpdateKillTimerUi();
                return;
            }
            _killSecondsLeft--;
            if (_killSecondsLeft <= 0)
            {
                KillMihomoNow();
                _killSecondsLeft = KillPeriodSeconds;
            }
            UpdateKillTimerUi();
        }

        // ---------- Engine watchdog ----------

        private async void HealthTick(object sender, EventArgs e)
        {
            if (_healthBusy) return;
            if (_state == UiState.Checking) return;   // a connect/reconnect owns the engine
            if (_updating) return;                    // an update stops it on purpose
            if (_reconnectingId != null) return;      // a per-tile reconnect is mid-flight
            if (!_engineReady) return;                // nothing installed to watch

            // Before anything else, and in safe mode too: a remote session that connected AFTER
            // the live config was written isn't in its route-exclude list, so TUN is capturing
            // its return traffic right now and the session is dropping. Nothing else would
            // rebuild the config — everything is connected and healthy — so it would stay
            // broken until the user reconnected the VPN by hand.
            EnsureRdpPeersExcluded();

            // Before the firewall sync: an updated program's new path is what it must protect.
            RepairProgramPaths("фон");

            // The firewall kill-switch is checked in EVERY tick, including safe mode and
            // including the ticks that return early below. It is the one protection that does not
            // depend on the engine, so the moments when the engine is being killed on purpose are
            // exactly the moments it has to be in place. (Cheap when nothing changed: it compares
            // the plan against the rules already in the firewall and returns.)
            SyncFirewallSoon();

            // Safe mode force-stops the engine every 60 s BY DESIGN; healing it here would
            // fight the very switch the user turned on.
            if (_config?.RemoteDesktopSafeMode == true) return;

            _healthBusy = true;
            try { await CheckEngineHealthAsync().ConfigureAwait(true); }
            catch (Exception ex) { Log.Error("engine-health", ex); }
            finally { _healthBusy = false; }
        }

        // One health pass: alive and routing → nothing to do; alive with TUN down or gone
        // entirely → repair; unrepairable → say so instead of showing green tiles.
        private async Task CheckEngineHealthAsync()
        {
            var state = await _mihomo.ProbeApiAsync().ConfigureAwait(true);

            if (state == MihomoController.ApiState.Up)
            {
                // A second engine can appear at any moment, not just at startup: the scheduler
                // loses track of an instance and the next /Run — ours or its own — starts
                // another. Two engines mean two TUN adapters with the SAME 198.18.0.1, the
                // route table picks one, and if it isn't ours the machine runs on the other
                // one's rules (MATCH,DIRECT — straight out, real IP) while everything here
                // looks healthy: our API answers, our probes go through our own mixed-port.
                // Nothing else in the launcher would ever notice, so it's checked every tick.
                if (MihomoSetup.CountEngines(_config) > 1)
                {
                    Log.Info("Найдено больше одного движка — оставляю один и перезапускаю начисто.");
                    await Task.Run(() => MihomoSetup.StartEngineAlone(_config)).ConfigureAwait(true);
                    if (await _mihomo.WaitApiReachableAsync(EngineStartWaitSeconds).ConfigureAwait(true))
                    {
                        ReapplyLiveConfig();
                        Log.Info("Лишние движки убраны, маршруты перезаписаны.");
                        StatusFoot.Text = Loc.T("Main_FootEngineRestored");
                        UpdateTrayText();
                    }
                    return;   // next tick judges the result
                }

                var tun = await _mihomo.IsTunEnabledAsync().ConfigureAwait(true);
                if (tun == false)
                {
                    // The config we write always has tun.enable: true, so TUN being down means
                    // someone PATCHed it off and never put it back — in practice a previous
                    // launcher exit whose /End didn't land, whose engine this launcher then
                    // adopted on startup. Nothing is routed in that state.
                    Log.Info("Движок работает, но TUN снят — маршрутов нет. Перезаливаю конфиг.");
                    ReapplyLiveConfig();
                    await Task.Delay(1500).ConfigureAwait(true);

                    if (await _mihomo.IsTunEnabledAsync().ConfigureAwait(true) == false)
                    {
                        // It takes the config and still doesn't raise TUN: this engine can't be
                        // steered any more. Restart it outright — that's the manual "закрыть и
                        // открыть утилиту" the user had to do, done for them.
                        Log.Info("TUN не поднялся после перезаливки — перезапускаю движок начисто.");
                        await Task.Run(() => MihomoSetup.StartEngineAlone(_config)).ConfigureAwait(true);
                        if (await _mihomo.WaitApiReachableAsync(EngineStartWaitSeconds).ConfigureAwait(true))
                        {
                            ReapplyLiveConfig();
                            Log.Info("Движок перезапущен, маршруты перезаписаны.");
                            StatusFoot.Text = Loc.T("Main_FootEngineRestored");
                            UpdateTrayText();
                        }
                    }
                }
                else if (tun == true)
                {
                    // TUN says it's up — but that is our own flag read back, not evidence. Ask
                    // Windows which interface would actually carry a packet to the internet: a
                    // sleep/resume, an adapter reset or another VPN client can strip the
                    // tunnel's routes while leaving the interface (and the flag) perfectly up,
                    // and in that state everything leaves through the physical adapter with the
                    // real IP on it while the tiles stay green.
                    var device = await _mihomo.GetTunDeviceAsync().ConfigureAwait(true);
                    if (!string.IsNullOrEmpty(device)) _tunDevice = device;
                    var verdict = RouteGuard.Check(device);
                    NoteFirewallState(verdict != RouteGuard.Verdict.Bypassing);
                    if (verdict == RouteGuard.Verdict.Bypassing)
                        await RepairTunRoutingAsync(device).ConfigureAwait(true);
                    else
                    {
                        EnsureUpstreamCurrent();
                        EnsureSubscriptionsCurrent();
                    }
                }
                if (_engineLost) { _engineLost = false; Log.Info("Движок снова отвечает."); }
                // An engine-failure dialog still on screen is now a wrong question: «переустановить?»
                // pressed after the engine had come back by itself was a second, pointless
                // reinstall (PAPA2, 2026-11-08). Take it down.
                _engineFailureDismiss?.Cancel();
                return;
            }

            // No API. Either the process is gone or it is wedged past talking; either way not
            // one rule is being routed right now, whatever the tiles say.
            //
            // This is the moment the firewall kill-switch exists for, and the moment the log
            // window would otherwise go completely silent: with no engine there is no /logs feed,
            // so the programs it is cutting off would appear to fail for no reason at all.
            NoteFirewallState(false);
            if (!_engineLost)
            {
                _engineLost = true;
                Log.Info("Движок перестал отвечать (" + state + ") — восстанавливаю. "
                         + (IsMihomoProcessRunning() ? "процесс работает" : "процесса нет")
                         + "; " + MihomoSetup.TaskState());
            }

            if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
            {
                ReapplyLiveConfig();
                _engineLost = false;
                Log.Info("Движок поднят заново, маршруты перезаписаны.");
                StatusFoot.Text = Loc.T("Main_FootEngineRestored");
                UpdateTrayText();
                return;
            }

            // Couldn't get it back. Drop the routing state so the banner and the tiles show
            // the truth — "подключено" on a dead engine is what sent the user hunting.
            if (_workingApps.Count > 0 || _assignment.Count > 0)
            {
                Log.Info("Движок поднять не удалось — сбрасываю состояние подключения.");
                _workingApps.Clear();
                _assignment.Clear();
                VpnStatus.Clear();
                SetState(_hasChecked ? UiState.Done : UiState.Idle);
            }
            StatusFoot.Text = Loc.T("Main_FootEngineLost");
            UpdateTrayText();
        }

        // ---------- The rule switch ----------

        // Persisted on every flip rather than at exit: the launcher is killed by logoff and by
        // the updater at least as often as it is closed cleanly, and a state that only survives
        // a clean exit is not a state anything can be built on.
        private void SaveRuleSwitches(string where)
        {
            try { AppConfigStore.Save(_config); }
            catch (Exception ex) { Log.Error("rule-switch-save:" + where, ex); }
        }

        private void SetAutoConnect(AppEntry entry, bool on, string where)
        {
            if (entry == null || entry.AutoConnect == on) return;
            entry.AutoConnect = on;
            SaveRuleSwitches(where);
        }

        // ---------- Keep trying the rules that didn't connect ----------

        // The launcher is used by someone who will not press «Подключить» again, so a rule that
        // failed its probe must not simply sit there red. Every RuleRetryPeriodSeconds one
        // still-unconnected rule is retried exactly the way its tile's ⟳ does it — the path
        // that, by the user's own account, "always works when I press it by hand".
        //
        // It only ever RE-connects: a rule that is switched off is left alone. The filter used to
        // be "not stopped by hand this session", which made "I never turned this on" and "I want
        // this on" the same thing — so rules nobody had asked for came up by themselves, one
        // every 90 seconds, and came back again after every restart even once switched off.
        // Reviving what dropped is this timer's job; deciding what should be on is not.
        private async void RetryTick(object sender, EventArgs e)
        {
            if (_retryBusy || _healthBusy || _updating) return;
            if (_state == UiState.Checking || _reconnectingId != null) return;
            if (!_engineReady || !_hasChecked) return;              // never connected / disconnected on purpose
            if (_config?.RemoteDesktopSafeMode == true) return;     // the engine is being killed by design

            var now = DateTime.UtcNow;
            var pending = (_config.Apps ?? new List<AppEntry>())
                .Where(a => a != null && a.RequiresVpn && !a.IsNoNodeMode && !_workingApps.Contains(a.Id))
                .Where(a => a.AutoConnect)                   // only rules somebody switched on
                // A rule that ticks no VPN can only go where its plate sent it this session —
                // nowhere at all if it never was: nothing to retry (RDP, 2026-11-16).
                .Where(a => !HasNoAllowedVpnVariant(a) || TileOwnSlotId(a) != null)
                .Where(a => !_retryNextAt.TryGetValue(a.Id, out var when) || now >= when)
                .ToList();
            if (pending.Count == 0) return;

            _retryBusy = true;
            try
            {
                // One rule per tick: each retry walks every allowed variant and can take a
                // while, and a queue of them would keep the engine's config churning.
                var entry = pending[0];
                Log.Info("Автоповтор: пробую подключить «" + entry.Name + "» ещё раз.");
                await ReconnectOneAsync(entry, onlySlotId: HasNoAllowedVpnVariant(entry) ? TileOwnSlotId(entry) : null).ConfigureAwait(true);

                if (_workingApps.Contains(entry.Id))
                {
                    _retryFails.Remove(entry.Id);
                    _retryNextAt.Remove(entry.Id);
                    Log.Info("Автоповтор: «" + entry.Name + "» подключено.");
                }
                else
                {
                    int fails;
                    _retryFails.TryGetValue(entry.Id, out fails);
                    fails++;
                    _retryFails[entry.Id] = fails;

                    // 90 s · 1, 2, 4, 8, 16, 32, then 40 for ever — i.e. it backs off to once
                    // an hour and STAYS there. It never gives up, because the person at this
                    // machine will not press anything (that decision stands); it just stops
                    // being expensive. The old ceiling was 8 periods, ~12 minutes: one rule
                    // whose site was simply unreachable produced 207 retries in a week on mom's
                    // machine, and back then each one reloaded the engine — see ApplyFinalConfig.
                    int factor = Math.Min(1 << Math.Min(fails - 1, 10), 40);
                    _retryNextAt[entry.Id] = DateTime.UtcNow.AddSeconds(RuleRetryPeriodSeconds * factor);

                    // Say once, when it settles into the hourly rhythm, that this rule is not
                    // coming back on its own. Silence here is what let «api.xbyfox.com» look healthy
                    // in the log for a week while it had never connected once.
                    if (fails == RetryFailsBeforeQuiet)
                        Log.Info("Правило «" + entry.Name + "» не подключается " + fails
                                 + " раз подряд — перехожу на редкие попытки (раз в час). "
                                 + "Похоже, дело не в лаунчере: проверьте адрес проверки у этого правила.");
                }
            }
            catch (Exception ex) { Log.Error("rule-retry", ex); }
            finally { _retryBusy = false; }
        }

        // Push whatever should be live right now back into the engine: the assigned routes if
        // we are connected, otherwise the armed (kill-switch) config.
        // Keep a live RDP session out of the tunnel even when it appears mid-flight.
        //
        // Self-limiting by construction: rewriting the config refreshes LastRdpExcludes, so the
        // next tick finds nothing missing. When the user deliberately routes an RDP client
        // through the VPN, the builder reports null and this does nothing at all — otherwise it
        // would rewrite the config every 20 s forever, fighting the user's own setting.
        // Follow the network's DNS, the way every Clash client that uses the system resolver
        // does. Under TUN the engine answers every name on the machine and forwards what the
        // rules don't claim to the resolvers it was given when the config was BUILT; after a
        // network change those can be gone, and then nothing resolves — not even "напрямую".
        //
        // A switch passes through half-states (adapter down, no DNS yet, fallback servers for a
        // moment), so a new answer is acted on only once two ticks in a row agree: one rebuild
        // per real change, not one per flicker. Each rebuild is a reload, and reloads are what
        // take the machine's resolver down for a second.
        private string _upstreamPending;

        private void EnsureUpstreamCurrent()
        {
            try
            {
                var written = MihomoConfigBuilder.LastUpstream;
                if (written == null) return;                       // no config built yet

                var nowKey = string.Join(", ", MihomoConfigBuilder.CurrentMachineUpstream());
                var wasKey = string.Join(", ", written);
                if (nowKey == wasKey) { _upstreamPending = null; return; }

                if (_upstreamPending != nowKey) { _upstreamPending = nowKey; return; }
                _upstreamPending = null;

                Log.Info("Сменился DNS сети: у машины теперь " + nowKey + ", а движок спрашивал "
                         + wasKey + " — пересобираю конфиг.");
                ReapplyLiveConfig("смена DNS сети");
            }
            catch (Exception ex) { Log.Error("upstream-check", ex); }
        }

        // The engine re-downloads a subscription on its own schedule (and on «Обновить»), and the
        // new copy may carry different rules. They reach the tiles only through a rebuilt config;
        // a copy that brings the same rules back changes nothing and costs no reload.
        private void EnsureSubscriptionsCurrent()
        {
            try
            {
                var changed = SubscriptionPolicy.ChangedSinceBuilt(_config);
                if (changed == null) return;
                Log.Info("Подписка «" + changed + "» обновилась, правила в ней другие — пересобираю конфиг.");
                ReapplyLiveConfig("правила подписки обновились");
            }
            catch (Exception ex) { Log.Error("subscription-current", ex); }
        }

        private void EnsureRdpPeersExcluded()
        {
            try
            {
                var applied = MihomoConfigBuilder.LastRdpExcludes;
                if (applied == null) return;             // «RDP» mode is off: no RDP rules at all

                var peers = RdpClients.ActivePeerCidrs();
                if (peers == null || peers.Count == 0) return;

                var missing = peers.Where(p => !applied.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count == 0) return;

                Log.Info("Подключился удалённый рабочий стол с адреса " + string.Join(", ", missing)
                         + " — он не был исключён из туннеля, перезаписываю маршруты.");
                ReapplyLiveConfig("новый адрес удалённого рабочего стола");
            }
            catch (Exception ex) { Log.Error("rdp-exclude", ex); }
        }

        // Everything a rule can be recognised by in the engine's connection list: the hosts it
        // claims (its sites, its template's domains, its check address) and the programs it lists.
        private static void RuleTargets(AppEntry app, List<string> hosts, List<string> procs, List<string> nets,
                                        List<string> dirs = null, List<string> files = null)
        {
            // Folder entries: whatever runs from there (by the connection's full process path).
            if (dirs != null && app != null)
                foreach (var f in app.Folders ?? new List<RuleFolder>())
                    if (f != null && !string.IsNullOrWhiteSpace(f.Path)) dirs.Add(f.Path);
            if (app == null) return;
            foreach (var site in app.Sites ?? new List<RuleSite>())
            {
                if (site == null) continue;
                var h = MihomoConfigBuilder.HostOf(site.Url);
                if (!string.IsNullOrEmpty(h)) hosts.Add(h);
                foreach (var item in site.RuleItems ?? new List<string>())
                {
                    // "DOMAIN-SUFFIX,instagram.com" → a host; "IP-CIDR,1.2.3.0/24" → a network.
                    // Both kinds live in the same template list and both route traffic, so both
                    // have to be closable — a connection matched by an IP rule carries no name.
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    int c = item.IndexOf(',');
                    if (c <= 0 || c >= item.Length - 1) continue;
                    var type = item.Substring(0, c).Trim();
                    var value = item.Substring(c + 1).Trim();
                    // The payload can carry rule options ("…,no-resolve"); keep only the address.
                    int opt = value.IndexOf(',');
                    if (opt > 0) value = value.Substring(0, opt).Trim();
                    if (type.StartsWith("IP", StringComparison.OrdinalIgnoreCase)) nets.Add(value);
                    else hosts.Add(value);
                }
            }
            var check = MihomoConfigBuilder.HostOf(MihomoConfigBuilder.RouteUrl(app));
            if (!string.IsNullOrEmpty(check)) hosts.Add(check);

            foreach (var prog in app.Programs ?? new List<RuleProgram>())
            {
                if (prog == null || string.IsNullOrEmpty(prog.Path)) continue;
                // An auto entry is routed by its exact file: only that file's connections are its own.
                if (files != null && MihomoConfigBuilder.AutoFileRegex(prog).Length > 0) { files.Add(prog.Path); continue; }
                try { procs.Add(System.IO.Path.GetFileName(prog.Path)); } catch { }
            }
        }

        // A rule has just been taken off its node. Rewriting the config decides where the NEXT
        // connection goes and nothing else — so the sockets the browser opened a minute ago keep
        // carrying the site, and «остановил, а сайт всё равно открывается» is the result. Cut
        // them, or "stop" is only a promise about the future.
        private void CloseConnectionsFor(IEnumerable<AppEntry> apps)
        {
            var hosts = new List<string>();
            var procs = new List<string>();
            var nets = new List<string>();
            var dirs = new List<string>();
            var files = new List<string>();
            bool catchAll = false;
            foreach (var a in apps ?? Enumerable.Empty<AppEntry>())
            {
                if (a != null && a.IsAllTraffic) catchAll = true;
                else RuleTargets(a, hosts, procs, nets, dirs, files);
            }
            if (hosts.Count == 0 && procs.Count == 0 && nets.Count == 0 && dirs.Count == 0 && files.Count == 0 && !catchAll) return;

            var ctl = _mihomo;
            _ = Task.Run(async () =>
            {
                try { await ctl.CloseConnectionsAsync(hosts, procs, nets, catchAll, folders: dirs, files: files).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("close-rule-connections", ex); }
            });
        }

        // Tell the log window what the FIREWALL is doing, because nothing else can: its blocks
        // never reach the engine, so they never reach /logs. Written only on a change of state —
        // the log is deduped, but a line that says the same thing every twenty seconds is still
        // noise in a window someone is trying to read.
        private bool? _fwCarrying;
        private void NoteFirewallState(bool carrying)
        {
            try
            {
                if (_config?.FirewallKillSwitch != true) return;
                if (FirewallGuard.AppliedRuleCount() <= 0) return;      // nothing is being enforced
                if (_fwCarrying == carrying) return;
                _fwCarrying = carrying;
                EngineLogStore.Instance.Note(
                    carrying ? LogCategory.Passed : LogCategory.Problem,
                    Loc.T(carrying ? "Log_VerdictFirewallOpen" : "Log_VerdictFirewall"),
                    Loc.T(carrying ? "Log_WhyFirewallLifted" : "Log_WhyFirewallBlocking"));
            }
            catch (Exception ex) { Log.Error("firewall-note", ex); }
        }

        // Keep the Windows-level kill-switch in step with the rules, off the UI thread.
        //
        // Deliberately NOT tied to connect/disconnect: the firewall rules say "this program may
        // only talk through the tunnel", which is equally true while connected, while connecting
        // and while disconnected. Rewriting them on every connect would be churn for nothing —
        // and worse, a rule being replaced is a moment in which it isn't enforcing anything.
        // They change only when the rules, the tunnel's addresses or the live RDP peers do.
        private void SyncFirewallSoon()
        {
            if (System.Threading.Interlocked.Exchange(ref _fwSyncing, 1) == 1) return;   // one at a time
            var device = _tunDevice;
            var cfg = _config;
            _ = Task.Run(() =>
            {
                try { FirewallGuard.SyncIfNeeded(cfg, device); }
                catch (Exception ex) { Log.Error("firewall-sync-call", ex); }
                finally { System.Threading.Interlocked.Exchange(ref _fwSyncing, 0); }
            });
        }

        // Programs that install each version into its own folder ("app-1.15" → "app-1.16"):
        // move the rule to the newest exe, so the tile launches it and the firewall kill-switch
        // (keyed on the full path) protects the file that actually runs. Routing itself is by
        // process NAME and survives the update untouched. Quiet: log only.
        // Full scan at most every 10 min, every minute while some program's file is missing,
        // always when forced (startup, tile click).
        private DateTime _pathScanAtUtc = DateTime.MinValue;
        private void RepairProgramPaths(string where, bool force = false)
        {
            try
            {
                var since = DateTime.UtcNow - _pathScanAtUtc;
                if (!force && since < TimeSpan.FromMinutes(1)) return;
                if (!force && since < TimeSpan.FromMinutes(10) && !ProgramPathRepair.AnyMissing(_config)) return;
                _pathScanAtUtc = DateTime.UtcNow;

                var fixes = ProgramPathRepair.RepairAll(_config);
                // Folder entries whose folder is gone (an update renamed its version folder): the
                // engine's regexp already follows the version, the firewall's exe list does not.
                var folderFixes = ProgramPathRepair.RepairFolders(_config);
                foreach (var f in folderFixes)
                    Log.Info("Путь папки обновлён (" + where + "): «" + f.Rule + "» " + f.OldPath + " → " + f.NewPath);
                if (fixes.Count == 0 && folderFixes.Count == 0) return;
                foreach (var f in fixes)
                    Log.Info("Путь программы обновлён (" + where + "): «" + f.Rule + "» " + f.OldPath + " → " + f.NewPath);
                AppConfigStore.Save(_config);
                if (folderFixes.Count > 0) ReapplyLiveConfig("путь папки изменился");
                // Same exe name → the engine's PROCESS-NAME rules are already right; a renamed
                // exe needs the live config rewritten. So does an auto entry (routed by its exact
                // file) whose path moved beyond what its version mask already covers.
                if (folderFixes.Count == 0 && fixes.Any(f =>
                        !string.Equals(System.IO.Path.GetFileName(f.OldPath), System.IO.Path.GetFileName(f.NewPath), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(MihomoConfigBuilder.FileRegex(f.OldPath), MihomoConfigBuilder.FileRegex(f.NewPath), StringComparison.Ordinal)))
                    ReapplyLiveConfig("путь программы изменился");
                SyncFirewallSoon();
                RebuildTiles();
            }
            catch (Exception ex) { Log.Error("path-repair", ex); }
        }

        private void ReapplyLiveConfig(string reason = null)
        {
            try
            {
                // force: this is the repair path — the engine may be running something other
                // than what the file says (fresh restart, routes stripped), so identical text
                // is exactly when it still has to be pushed.
                if (_hasChecked && _assignment.Count > 0) ApplyFinalConfig(CurrentAssignedProxies(), reason, force: true);
                else _ = ArmKillSwitchAsync(CancellationToken.None);
            }
            catch (Exception ex) { Log.Error("engine-reapply", ex); }
        }

        // The tunnel is up but the stack routes around it — every packet is leaving on the
        // physical adapter right now. Rebuild the interface: dropping TUN and raising it makes
        // mihomo tear down and re-install its whole route set, which is what Windows lost.
        // (Dropping it costs nothing here — nothing is being carried anyway.)
        private async Task<bool> RepairTunRoutingAsync(string device)
        {
            Log.Info("Движок работает и TUN включён, но трафик идёт МИМО туннеля — маршруты пропали. Пересобираю интерфейс.");
            StatusFoot.Text = Loc.T("Main_FootRoutesLost");

            await _mihomo.SetTunAsync(false, 3).ConfigureAwait(true);
            await Task.Delay(600).ConfigureAwait(true);
            await _mihomo.SetTunAsync(true, 3).ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);

            if (RouteGuard.Check(device) == RouteGuard.Verdict.ThroughTun)
            {
                Log.Info("Маршруты туннеля восстановлены.");
                StatusFoot.Text = Loc.T("Main_FootRoutesRestored");
                return true;
            }

            // The engine won't rebuild them on command — it's wedged past being steered.
            Log.Info("Маршруты не вернулись — перезапускаю движок начисто.");
            await Task.Run(() => MihomoSetup.StartEngineAlone(_config)).ConfigureAwait(true);
            if (!await _mihomo.WaitApiReachableAsync(EngineStartWaitSeconds).ConfigureAwait(true)) return false;

            ReapplyLiveConfig();
            await Task.Delay(1500).ConfigureAwait(true);
            bool ok = RouteGuard.Check(device) != RouteGuard.Verdict.Bypassing;
            Log.Info(ok ? "Движок перезапущен, маршруты на месте." : "Движок перезапущен, но трафик всё ещё идёт мимо туннеля.");
            StatusFoot.Text = Loc.T(ok ? "Main_FootRoutesRestored" : "Main_FootRoutesLost");
            UpdateTrayText();
            return ok;
        }

        // Is the mihomo engine process alive? (Enumeration is allowed for a non-admin even
        // though the process runs as SYSTEM — only killing it needs the task's /End.)
        private bool IsMihomoProcessRunning()
        {
            try
            {
                var exe = (_config?.MihomoExePath ?? "").Trim();
                var name = exe.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(exe) : "";
                if (!string.IsNullOrEmpty(name))
                    return System.Diagnostics.Process.GetProcessesByName(name).Length > 0;

                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try { if (p.ProcessName.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0) return true; }
                    catch { }
                }
                return false;
            }
            catch { return false; }
        }

        // Stop the elevated engine — runs off the UI thread because schtasks (and the TUN-off
        // call before it) can block for a moment. mihomo stays down until the next connect.
        // This is the RDP dead-man switch, so the orderly teardown matters even more here:
        // the whole point is to give the remote session its network back, not to yank it.
        private void KillMihomoNow()
        {
            try
            {
                _ = Task.Run(() =>
                {
                    try { EngineShutdown.Stop(_config); }
                    catch (Exception ex) { Log.Error("safemode-end", ex); }
                });
                // Logged because from the outside this looks exactly like "the engine keeps
                // dying / won't start" — the log has to show it was safe mode doing it.
                Log.Info("Безопасный режим: движок остановлен по таймеру (" + KillPeriodSeconds + " с).");
                StatusFoot.Text = Loc.T("Main_KillTimerKilled");
            }
            catch (Exception ex) { Log.Error("safemode-kill", ex); }
        }

        private async void OnKillTimerHelp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await ShowDialog(Loc.T("Main_KillTimerHelpTitle"), Loc.T("Main_KillTimerHelpBody")).ConfigureAwait(true);
        }

        private void UpdateKillTimerUi()
        {
            try
            {
                KillCountdown.Text = FormatMmSs(_killSecondsLeft);
                // Off: just the name. On: just the countdown — the name is what you switched on.
                bool on = _config?.RemoteDesktopSafeMode == true;
                var cd = on ? Visibility.Visible : Visibility.Collapsed;
                if (KillCountdown.Visibility != cd)
                {
                    KillCountdown.Visibility = cd;
                    KillLabel.Visibility = on || _topIconsOnly ? Visibility.Collapsed : Visibility.Visible;
                    RecalcHeight();   // re-checks that the row fits the window
                }
            }
            catch { }
        }

        private static string FormatMmSs(int s)
        {
            if (s < 0) s = 0;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        // ---------- Tray ----------

        private void InitTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = Loc.T("Main_WindowTitle"),
                Visible = true,
            };
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) ShowFromTray();
            };
            _trayIcon.DoubleClick += (s, e) => ShowFromTray();

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add(Loc.T("Main_TrayOpen"), null, (s, e) => ShowFromTray());
            menu.Items.Add("-");
            menu.Items.Add(Loc.T("Main_TrayExit"), null, (s, e) => RealExit());
            _trayIcon.ContextMenuStrip = menu;
            UpdateTrayText();
        }

        // Tray tooltip = the state you'd otherwise have to open the window to see. It's the
        // only feedback an autostarted (hidden) launcher gives, so keep it current.
        private void UpdateTrayText()
        {
            try
            {
                if (_trayIcon == null) return;
                string state;
                if (!_engineReady) state = Loc.T("Tray_NoEngine");
                else if (_state == UiState.Checking) state = Loc.T("Tray_Connecting");
                else if (!_hasChecked) state = Loc.T("Tray_Idle");
                else state = _assignment.Count > 0 ? Loc.T("Tray_Connected", _assignment.Count)
                                                   : Loc.T("Tray_NotConnected");
                var text = Loc.T("Main_WindowTitle") + " — " + state;
                // NotifyIcon.Text is capped at 63 chars; anything longer throws.
                if (text.Length > 62) text = text.Substring(0, 62);
                _trayIcon.Text = text;
            }
            catch { }
        }

        // Autostart entry point: create the window (so Loaded — and with it the whole startup
        // + auto-connect flow — actually runs) without ever putting it on screen. Created
        // minimized and out of the taskbar, then immediately hidden: no flash, no taskbar
        // button. The saved WindowState/ShowInTaskbar are restored for the first ShowFromTray.
        public void StartHiddenInTray()
        {
            try
            {
                ShowInTaskbar = false;
                WindowState = WindowState.Minimized;
                Show();
                Hide();
            }
            catch (Exception ex) { Log.Error("start-hidden", ex); }
            finally
            {
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
            }
        }

        // The launcher's own icon, copied next to the exe at Data\fox.ico by the build
        // (see csproj). It's a loose file now — NOT embedded as a WPF resource — so it's
        // loaded from disk for the window, title bar and tray.
        private static string FoxIcoPath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "fox.ico");

        // Set the window + title-bar icon from Data\fox.ico (pack:// URIs no longer work
        // since the icon isn't embedded). No-op if the file is missing.
        private void ApplyAppIcon()
        {
            try
            {
                if (!System.IO.File.Exists(FoxIcoPath)) return;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(FoxIcoPath, UriKind.Absolute);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Icon = bmp;
                // Drawn by us beside the caption (ImgCaptionIcon), NOT as TitleBar.Icon: the
                // template's icon cell carries its own fixed margin, which left a wide gap
                // between the fox and the caption (2026-11-10).
                ImgCaptionIcon.Source = bmp;
            }
            catch { }
        }

        private System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                if (System.IO.File.Exists(FoxIcoPath))
                    return new System.Drawing.Icon(FoxIcoPath);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private void ShowFromTray()
        {
            try
            {
                _hidden = false;          // there's a user in front of it again — dialogs are ok
                ShowInTaskbar = true;
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                BringOnScreen();
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
                // Nothing was ever measured while hidden, so the auto-height is stale.
                RecalcHeight();
            }
            catch (Exception ex)
            {
                // Used to be swallowed silently — and the window just never appeared.
                Log.Error("show-from-tray", ex);
            }
        }

        // An RDP session or a monitor change can leave the window's saved position outside
        // every screen: it "opens" where nobody can see it. Less than 80 px visible → centre
        // it on the primary work area.
        private void BringOnScreen()
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
            double visW = Math.Min(Left + w, vr) - Math.Max(Left, vl);
            double visH = Math.Min(Top + h, vb) - Math.Max(Top, vt);
            if (!double.IsNaN(Left) && !double.IsNaN(Top) && visW >= 80 && visH >= 80) return;
            var wa = SystemParameters.WorkArea;
            Log.Info("Окно было за пределами экрана (" + (int)Left + ", " + (int)Top + ") — вернул в центр.");
            Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - h) / 2);
        }

        public void RealExit()
        {
            _allowClose = true;
            // mihomo is stopped by App.OnExit (EndTask) when the launcher fully exits — the
            // watchdog goes down FIRST so it can't restart the engine we're on our way to stop.
            try { _healthTimer?.Stop(); } catch { }
            try { _retryTimer?.Stop(); } catch { }
            try { _checkCts?.Cancel(); } catch { }
            // Drops the collected history and puts the engine's log level back to quiet.
            try { EngineLogStore.Instance.Stop(); } catch { }
            Close();
            try { Application.Current.Shutdown(); } catch { }
        }

        // Exit so a launcher self-update can proceed (the staged updater, already launched, waits
        // for this process to die, then swaps the files and relaunches). Mirrors the in-place
        // app-update exit path — bypasses the hide-on-close so the process actually terminates.
        // Used by Settings → «Обновление» when the user forces a launcher update from there.
        public void ShutdownForUpdate()
        {
            _allowClose = true;
            try { _healthTimer?.Stop(); } catch { }
            try { _retryTimer?.Stop(); } catch { }
            try { _checkCts?.Cancel(); } catch { }
            try { Application.Current.Shutdown(); } catch { }
        }

        // ---------- State / UI ----------

        private void SetState(UiState s)
        {
            _state = s;
            switch (s)
            {
                case UiState.Checking:
                    // Normal connect flow uses the spinner, not the update progress bar.
                    ConnectingRing.Visibility = Visibility.Visible;
                    UpdateBar.Visibility = Visibility.Collapsed;
                    ConnectingOverlay.Visibility = Visibility.Visible;
                    ResultRoot.Visibility = Visibility.Collapsed;
                    break;

                case UiState.Idle:
                    ConnectingOverlay.Visibility = Visibility.Collapsed;
                    ResultRoot.Visibility = Visibility.Visible;
                    // No engine → the install button already says it; hide the banner so we
                    // don't repeat the same message twice.
                    if (!_engineReady)
                    {
                        TopBanner.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        TopBanner.Visibility = Visibility.Visible;
                        SetBanner(Theme.BannerInfoBg, Theme.BannerInfoBorder, Theme.BannerInfoText,
                            Loc.T("Main_BannerPressConnect"));
                    }
                    StatusFoot.Text = "";
                    break;

                case UiState.Done:
                    ConnectingOverlay.Visibility = Visibility.Collapsed;
                    ResultRoot.Visibility = Visibility.Visible;
                    // Clear any stale footer (e.g. a "Сначала нажмите «Подключить»" left over
                    // from a tile clicked before connecting) — once connected the green banner
                    // says it all. Flows that want a footer here set it AFTER SetState(Done).
                    StatusFoot.Text = "";
                    UpdateDoneBanner();
                    break;
            }
            RebuildTiles();
            UpdateTrayText();
            // New servers get measured right away instead of on the next 30-s round.
            MeasureStatsPing(force: false);
            RenderStats();
        }

        // After a connect the banner only says what the tiles can't: that there are no rules yet.
        // Which tiles are connected and which are not is on the tiles themselves (plate colour and
        // text); repeating it under them — green «всё работает», amber «плитки … не подключены» —
        // was noise (2026-11-10: «не писать очевидное — что не подключено и так видно, так же как
        // и что подключено»). No engine: the install button says it, no banner either.
        private void UpdateDoneBanner()
        {
            if (_engineReady && (_config.Apps == null || _config.Apps.Count == 0))
            {
                TopBanner.Visibility = Visibility.Visible;
                SetBanner(Theme.BannerInfoBg, Theme.BannerInfoBorder, Theme.BannerInfoText,
                    Loc.T("Main_BannerNoApps"));
                return;
            }
            TopBanner.Visibility = Visibility.Collapsed;
        }

        // Set the status message. It is a line of the bottom plate now (2026-11-10: «не резервируй
        // место под объявление, пиши их в этой же плашке»), so only its text colour follows the
        // kind of message; bg/border are kept in the signature for the callers, unused.
        private void SetBanner(Brush bg, Brush border, Brush fg, string text)
        {
            BannerText.Foreground = fg;
            BannerText.Text = text;
            RenderStats();
        }

        // Recompute whether the VPN engine is installed + set up (exe present AND scheduled
        // task registered) and reveal/hide the "install engine" banner button. Cheap-ish
        // (one schtasks query), so call it on real state changes, not on every redraw.
        private void RefreshEngineState()
        {
            try
            {
                var exe = (_config?.MihomoExePath ?? "").Trim();
                _engineReady = exe.Length > 0 && System.IO.File.Exists(exe) && MihomoSetup.IsTaskInstalled();
            }
            catch { _engineReady = false; }

            // The same button does double duty: «установить движок» while there's nothing to
            // run, and «переустановить движок» once something is installed but broken — the
            // repair that, by the user's own experience, is the one that actually works. It
            // must never be a dead end the user can only reach through an error dialog.
            BtnInstallEngine.Visibility = (_engineReady && !_engineBroken) ? Visibility.Collapsed : Visibility.Visible;
            // SetResourceReference, not Content=: a plain string would kill the DynamicResource
            // and the caption would stop following the language switch.
            BtnInstallEngine.SetResourceReference(ContentProperty,
                (_engineReady && _engineBroken) ? "Main_ReinstallEngineBtn" : "Main_InstallEngineBtn");

            // Re-render the status banner for the current state so it matches engine status.
            if (_state == UiState.Done) UpdateDoneBanner();
            else if (_state == UiState.Idle) SetState(UiState.Idle);
            RecalcHeight();
        }

        // ---------- Tiles ----------

        private void RebuildTiles()
        {
            // Mid-drag the tiles must stay put; the rebuild runs when the tile is dropped.
            if (TileDragActive) { _tileRebuildPending = true; return; }
            // Same while a plate's menu (VPN / cards) is open: it hangs on a plate of the old tile,
            // and with that tile gone WPF drops it in the screen's top-left corner. Runs on close.
            if (_vpnPicker != null && _vpnPicker.IsOpen) { _pickerRebuildPending = true; return; }
            TilesPanel.Items.Clear();
            // Rules grouped by tab = the rule order (TileTabs). Anything that moved rules around
            // (the «Правила» list, an import) is put back in groups here — and the engine told.
            if (TileTabs.Normalize(_config))
            {
                try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tabs-normalize", ex); }
                ApplyRuleOrder();
            }
            var tab = TileTabs.Current(_config);
            // Only rules with at least one «Плитка»-checked item get a tile; rules with all
            // checkboxes cleared are routing-only and stay off the main screen.
            // «Весь трафик» first, whatever order the list ended up in. Its name follows the UI
            // language (footer lines, the Routing table and Settings read Name directly).
            foreach (var a in _config.Apps.Where(a => a != null && a.IsAllTraffic))
                a.Name = AllTrafficName();
            foreach (var entry in _config.Apps.Where(a => a != null && a.HasTileTarget && TileTabs.OnTab(_config, a, tab))
                                              .OrderBy(a => a.IsAllTraffic ? 0 : 1))
                TilesPanel.Items.Add(CreateTile(entry));
            // The outline of a new tile ends the row; an empty tab says how to fill it, beside it.
            // Shown whenever it fits without making the window taller (ShowsAddTile): a free place
            // in the last row, or — on another tab, already as tall as «Основная» — a new row
            // inside that height. A full row with no room under it gets none (2026-11-17: «не
            // переноси на новую строчку… просто не показываем»); «APP» adds rules as well.
            int count = TilesPanel.Items.Count;
            bool empty = count == 0;
            if (ShowsAddTile(tab, count, _tileCols)) TilesPanel.Items.Add(AddRuleTile());
            if (empty) TilesPanel.Items.Add(EmptyTabHint());
            // Other tabs are at least as tall as «Основная» (2026-11-17: «зафиксируем размер окна на
            // вкладке основная при переходе в другие вкладки») — switching tabs doesn't make the
            // window jump. Counted from «Основная»'s rows at the current width (all tiles are one
            // height), so a resize on another tab stays right; a tab with more rows still grows.
            TilesPanel.MinHeight = tab.Id == TileTabs.MainId ? 0 : MainTabTilesHeight();
            RenderTabs();
            SyncTileGrid();
            UpdateConflictIcon();
            RecalcHeight();
        }

        // How tall «Основная»'s tiles are now, at _tileCols columns (MainWindow.WindowSize).
        private double MainTabTilesHeight() => TabTileRows(_config.Tabs[0], _tileCols) * TileRowHeight;

        // A tile with no picture at all but a site: fetch that site's icon in the background
        // (once per host per run — IconFetcher remembers) and redraw the tiles if one arrived.
        private void FetchSiteIconSoon(AppEntry entry)
        {
            var host = IconFetcher.SiteHostFor(entry);
            if (string.IsNullOrEmpty(host)) return;
            _ = Task.Run(async () =>
            {
                bool got = false;
                try { got = await IconFetcher.FetchSiteIconAsync(host).ConfigureAwait(false); }
                catch { }
                if (got)
                {
                    Log.Info("Иконка сайта " + host + " скачана для плитки «" + entry.Name + "».");
                    try { Dispatcher.BeginInvoke(new Action(RebuildTiles)); } catch { }
                }
            });
        }

        // The top button row is wider than 680 in some languages / with the Remote-session box, and
        // it was simply cut at the window's edge (the box lost its right rounded corners). Widen
        // the window to the row instead. Children's DesiredSize, not the panel's: a panel's own
        // DesiredSize is clipped to the space it was offered — exactly the width that is too small.
        // Since 2026-11-17 the width is the tile grid's: TileColumns whole tiles (MainWindow.WindowSize),
        // down to a single column. It is the chrome that gives way now (ApplyChromeFit), not the
        // window: the top row loses its words and wraps, the title bar's buttons move under it.
        // True when the chrome changed — its height is not measured yet.
        private bool FitWidthToTopBar()
        {
            MinWidth = GridWindowWidth(1);
            double w = ActualWidth;
            if (!_inSizeMove)   // the drag sizes it (SnapSizingRect)
            {
                w = Math.Min(GridWindowWidth(WantedTileCols()), SystemParameters.WorkArea.Width);
                if (Math.Abs(Width - w) > 0.5) Width = w;
            }
            return ApplyChromeFit(w);
        }

        // The top row's width with its words, measured while they are shown.
        private double TopRowWidth()
        {
            double row = 0;
            foreach (UIElement ch in TopButtons.Children)
            {
                var fe = ch as FrameworkElement;
                if (fe == null || fe.Visibility == Visibility.Collapsed) continue;
                row += fe.DesiredSize.Width;   // DesiredSize includes the element's margins
            }
            return row;
        }

        private const double CaptionButtonsWidth = 2 * 46 + 8;   // «—» + «✕» (no maximize)

        // Five tiles (112 + 3.5 margins each side) and exactly 14 from each tile's edge to the
        // window's (2026-11-16: «14 дипов слева и справа»): TilesArea margins 10.5 + the tiles'
        // own 3.5. No «frame» — this window has none (measured: the old +16+4 made 27 per side).
        private const double TileOuter = TileSizePx + 7;
        private const double TileSizePx = 112;
        // A tile is this much taller than wide (2026-11-18: the name sat ~2 px under the network
        // chip — «название плитки почти в упор лежит под иконкой сети»); the name moves down by
        // TileTitleDrop of it, the plates stay at the bottom.
        private const double TileExtraH = 4;
        private const double TileTitleDrop = 3;
        private double TileHeight => TileSizePx + TileExtraH + NicStrip + SpeedStrip;

        // Header: the caption at its full text width (its DesiredSize may already be squeezed by a
        // too-narrow window) up to its MaxWidth, + its margins 8/12.
        private double CaptionNeed(string text, bool rightMargin = true)
        {
            double caption = Math.Min(TxtCaption.MaxWidth,
                                      TextWidth(text ?? "", TxtCaption.FontSize, TxtCaption.FontWeight));
            return 8 + Math.Ceiling(caption) + (rightMargin ? 12 : 0);
        }

        // The caption as SetTitleCaption made it, and the launcher alone for a narrow window
        // (2026-11-20: «оставляй только название утилиты с версией, если не входит ещё название и
        // версия движка») — longest first. Never the name without the version.
        private string _captionFull = "";
        private string[] CaptionVariants()
        {
            var full = string.IsNullOrEmpty(_captionFull) ? TxtCaption.Text ?? "" : _captionFull;
            return new[] { full, "Mihomo Launcher " + VersionInfo.AppVersion }
                .Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray();
        }

        // «—» + «✕» as the title bar really lays them out (WPF-UI's own buttons, found by type
        // name); the estimate when they are not laid out yet.
        private double CaptionButtonsActual()
        {
            double w = 0;
            try
            {
                var stack = new Stack<DependencyObject>();
                stack.Push(TitleBarMain);
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    var fe = d as FrameworkElement;
                    if (fe != null && fe.IsVisible && fe.GetType().Name == "TitleBarButton") { w += fe.ActualWidth; continue; }
                    int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
            }
            catch { w = 0; }
            return w > 0 ? w : CaptionButtonsWidth;
        }

        // The title bar's own buttons (RU, site, log, settings, exit).
        private double TitleToolsNeed()
        {
            double w = 0;
            foreach (UIElement ch in TitleTools.Children)
            {
                var fe = ch as FrameworkElement;
                if (fe != null && fe.Visibility != Visibility.Collapsed) w += fe.DesiredSize.Width;
            }
            return w;
        }

        // Width change → tiles re-wrap into more/fewer rows → height must follow.
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) { SyncTileGrid(); RecalcHeight(); }
            // Resized not by a drag of the edge (Aero Snap, Win+←): back to the grid's height.
            else if (e.HeightChanged && !_inSizeMove) RecalcHeight();
        }

        // FluentWindow + SizeToContent="Height" under-reports when content changes after
        // the first layout (tiles rebuilt, update banner shown) — it clips the tiles/footer.
        // Force an explicit measure with unbounded height and set Height ourselves.
        private void RecalcHeight()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (ResultRoot.Visibility != Visibility.Visible) return;
                    // The top row wrapped / the title bar's buttons moved: measure after that layout.
                    if (FitWidthToTopBar()) { RecalcHeight(); return; }

                    // Sum the (correctly-arranged) heights of ResultRoot's rows + the title
                    // bar. ActualHeight is right even when SizeToContent under-reports.
                    // No slack: the bottom gap is the plate's own 14 (2026-11-16; a +6 here made it 20).
                    // With no plate the tabs are last: their own 3.5 + 10.5 = the same 14.
                    double content = (TitleBarMain?.ActualHeight ?? 0)
                                     + (StatsStrip.Visibility == Visibility.Visible ? 0 : 10.5);
                    foreach (System.Windows.UIElement ch in ResultRoot.Children)
                    {
                        if (ch.Visibility != Visibility.Visible) continue;
                        var fe = ch as FrameworkElement;
                        if (fe == null) continue;
                        content += fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
                    }

                    double maxH = SystemParameters.WorkArea.Height;
                    if (content > maxH) content = maxH;
                    if (content > 120 && !_inSizeMove)
                    {
                        // Height = the content. Not pinned with Min = Max any more (2026-11-17): the
                        // edges are dragged now, and the drag itself snaps to whole rows
                        // (MainWindow.WindowSize) — no blank gap, no covered tiles.
                        SizeToContent = SizeToContent.Manual;
                        MinHeight = 0;
                        MaxHeight = double.PositiveInfinity;
                        if (Math.Abs(Height - content) > 0.5) Height = content;
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // App is launchable if it doesn't need VPN, or it's in the current working set.
        private bool IsAppEnabled(AppEntry e)
        {
            if (e == null) return false;
            if (!e.RequiresVpn) return true;
            if (e.IsBlocking) return true;   // it is doing its job
            // No VPN ticked and none picked on the plate: the traffic goes direct and works — a
            // «без VPN» tile, as bright as a direct one (2026-11-16: «должны быть ярче, не
            // приглушать»). With a kill-switch it is blocked instead, and stays dimmed.
            if (IsVpnTileGoingDirect(e)) return true;
            return _workingApps.Contains(e.Id);
        }

        // A VPN-kind tile with no VPN ticked, none picked on its plate and no kill-switch: its
        // traffic goes direct. Bright like a direct tile, plate says «без VPN» (not the green
        // «connected» plate — enabled alone used to mean «riding a VPN» for VPN tiles).
        private bool IsVpnTileGoingDirect(AppEntry e)
            => e != null && e.RequiresVpn && !e.IsBlockMode && !e.KillSwitch
               && HasNoAllowedVpnVariant(e) && TileOwnSlotId(e) == null;

        // A VPN rule that requires VPN but has UNCHECKED every available variant in its
        // allow-list (all enabled slots excluded) — it can never connect. This is a config
        // mistake, distinct from a non-VPN rule (which shows "напрямую"). Only true when at
        // least one VPN variant exists to choose from.
        private bool HasNoAllowedVpnVariant(AppEntry e)
        {
            if (e == null || !e.RequiresVpn) return false;
            var enabled = (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList();
            if (enabled.Count == 0) return false;   // no variants configured at all is a different case
            var disabled = new HashSet<string>(e.DisabledVpnSlotIds ?? new List<string>());
            return enabled.All(s => disabled.Contains(s.Id));
        }

        // Short label of which VPN an app uses: "🛡 №2 Дом" for an assigned VPN app,
        // "напрямую" for a non-VPN app, empty otherwise.
        private string TileVpnLabel(AppEntry e)
        {
            if (e == null) return "";
            if (e.RequiresVpn && _assignment.TryGetValue(e.Id, out var proxy))
            {
                _proxySlot.TryGetValue(proxy, out var info);
                var num = info != null && info.Number > 0 ? "№" + info.Number + " " : "";
                var name = info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : proxy;
                return "🛡 " + num + name;
            }
            if (!e.RequiresVpn) return Loc.T("Main_TileDirect");
            return "";
        }

        // The ▷/⏸ tooltip, plus why its frame is violet when the kill-switch is on.
        private static string KillTip(AppEntry entry, string key)
            => Loc.T(key) + (entry.KillSwitch ? "\n\n" + Loc.T("Main_TipKillOn") : "");

        private FrameworkElement CreateTile(AppEntry entry)
        {
            bool reconnecting = entry.Id == _reconnectingId;
            bool launching = _launchingIds.Contains(entry.Id);
            bool enabled = IsAppEnabled(entry);     // what the plate says (VPN riding or not)
            bool notChecked = !_hasChecked && entry.RequiresVpn;
            // How it looks: dimmed only while its plate says «Блокирует» / «Заблокировано · kill-switch».
            bool plateBlocks;
            var plate = MakeVpnPlate(entry, enabled, reconnecting, notChecked, out plateBlocks);
            bool bright = !plateBlocks;
            // Amber while auto-tracking has added something here that was not seen yet (the notice,
            // MainWindow.AutoTrack): the gear AND the tile's frame — the gear alone was too easy to
            // miss (2026-11-22). The frame keeps the blue hover; it comes back when the pointer leaves.
            bool autoNotice = AutoNoticeOn(entry);
            var restBorder = autoNotice ? Theme.NoticeTileBorder : bright ? Theme.TileBorder : Theme.TileDisabledBorder;

            const double TileSize = TileSizePx;
            const double IconSize = 40;   // 44 before the VPN plate took the bottom 37 px

            var border = new Border
            {
                Width = TileSize,
                // Taller by the network-card strip along the bottom (2026-11-14).
                // + the one-line speed strip under that (2026-11-16).
                Height = TileHeight,
                Margin = new Thickness(3.5),
                CornerRadius = new CornerRadius(9),
                Background = bright ? Theme.TileBg : Theme.TileDisabledBg,
                BorderBrush = restBorder,
                BorderThickness = new Thickness(1),
                // hand cursor is reserved for actual buttons; "arrow + hourglass" while it launches
                Cursor = launching ? Cursors.AppStarting : Cursors.Arrow,
                Tag = entry,              // drag-to-reorder finds the rule by it
                Effect = bright ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Theme.ShadowColor,
                    BlurRadius = 10,
                    ShadowDepth = 1,
                    Opacity = 0.45
                } : null,
            };

            // Pinned to the top at a fixed offset, NOT centred: centring moved the icon up or down
            // with the number of text lines under it (name / VPN / country), so neighbouring tiles
            // had their icons at different heights. Only icon + name live here now; the VPN /
            // status lines moved into the plate at the bottom (MakeVpnPlate).
            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };

            // The rule's own icon (the set / a picked file); else the program's own picture, large
            // (Store logo / 256-px exe icon), else the site's icon (IconLoader.Fallback). A site
            // icon not fetched yet is fetched in the background and the tiles redrawn.
            var iconSource = IconLoader.Load(entry.IconPath, null, (int)IconSize)
                             ?? (entry.IsBuiltIn ? null : IconLoader.Fallback(entry));
            // Every tile icon the same size and sharp (2026-11-17: «все в одном стиле и размере,
            // одинаково ровные и чёткие»): a picture too small to fill 40 dip without blowing it up
            // (12306's 48-px favicon came out ragged) is not used — the tile gets its monogram.
            if (iconSource != null && !IconLoader.IsSharp(iconSource)) iconSource = null;
            if (iconSource == null && !entry.IsBuiltIn) FetchSiteIconSoon(entry);
            if (iconSource != null)
            {
                var img = new Image
                {
                    Width = IconSize,
                    Height = IconSize,
                    Source = iconSource,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = launching ? 0.35 : bright ? 1.0 : 0.4,
                };
                // WPF's default (bilinear) samples only a few source pixels per output pixel, so a
                // 1024 px icon squeezed into ~66 px came out grainy and jagged. HighQuality (Fant)
                // averages all of them — smooth at any source size.
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                stack.Children.Add(img);
            }
            else
            {
                var mono = MakeMonogram(entry.IsAllTraffic ? AllTrafficName() : entry.Name, IconSize);
                mono.Opacity = launching ? 0.35 : bright ? 1.0 : 0.4;
                stack.Children.Add(mono);
            }

            // The name fits the tile: a long one («TinyMediaManager») is set smaller, down to
            // 10.5 px, and only past that cut with «…» — the full name is in the tooltip.
            var title = entry.IsAllTraffic ? AllTrafficName() : entry.Name;
            var titleWeight = bright ? FontWeights.SemiBold : FontWeights.Normal;
            const double TitleRoom = 100;
            double titleSize = 13;
            while (titleSize > 10.5 && TextWidth(title, titleSize, titleWeight) > TitleRoom) titleSize -= 0.5;
            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = titleSize,
                FontWeight = titleWeight,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = TitleRoom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = bright ? Theme.TextPrimary : Theme.TextDisabled,
                Margin = new Thickness(0, TileTitleDrop, 0, 0),   // clear of the network chip
            };
            if (TextWidth(title, titleSize, titleWeight) > TitleRoom) titleBlock.ToolTip = title;
            stack.Children.Add(titleBlock);

            // Every tile gets a settings-gear in the top-LEFT that opens this rule's editor.
            // VPN tiles additionally get "reconnect" and "disconnect" chips in the top-right,
            // and every tile the VPN/status plate along the bottom.
            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(plate);
            if (ShowNicPlate && !NicAsIcon) grid.Children.Add(MakeNicPlate(entry, reconnecting));
            // The rule → tile map is shared with the DNS statistics, which count even with the speed
            // plates off: any redraw (a rule edited, added, reordered) makes it stale.
            _speedMapDirty = true;
            if (ShowTileSpeed) grid.Children.Add(MakeSpeedPlate(entry));
            if (launching)
            {
                // Spinning over the (dimmed) icon: the click went through, the program is starting.
                grid.Children.Add(new Wpf.Ui.Controls.ProgressRing
                {
                    IsIndeterminate = true,
                    Width = 34,
                    Height = 34,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 11, 0, 0),
                    IsHitTestVisible = false,
                });
            }

            // The gear in the notice amber (autoNotice, above) — same icon, same size, same click;
            // the click ends the notice.
            grid.Children.Add(MakeCornerChip(Wpf.Ui.Controls.SymbolRegular.Settings24, 14,
                new Thickness(5, 5, 0, 0), HorizontalAlignment.Left,
                autoNotice ? AutoNoticeTip(entry) : Loc.T("Main_TipEditRule"), false,
                () => { EndAutoNotice(entry.Id); OpenRuleEditor(entry); },
                autoNotice ? Theme.NoticeAmber : null));
            // The network card, as an icon under the gear (2026-11-16 try-out, see NicAsIcon).
            if (ShowNicPlate && NicAsIcon) grid.Children.Add(MakeNicChip(entry, reconnecting));

            // Right corner (2026-11-17): the bin on top (swapped the same day), under it ONE switch — ▷ while off, ⏸ while on (it is the old ✕:
            // stops the rule). No ⟳ any more: to reconnect, pick the same VPN
            // on the plate again.
            if (entry.RequiresVpn)
            {
                // «Блокировать» switched on counts as on, exactly like a connected VPN.
                bool vpnUp = _assignment.ContainsKey(entry.Id) || _workingApps.Contains(entry.Id) || entry.IsBlocking;
                // No VPN ticked in the rule → it goes through the VPN the tile is on (or was last
                // picked on its plate); with neither there is nothing to go through. «Блокировать»
                // needs no VPN. A plate pick lasts one session: after a restart a rule ticking none of
                // its own still had AutoConnect set while nothing ran (chrome, 2026-11-17) — not «on».
                bool canConnect = entry.IsBlockMode || !HasNoAllowedVpnVariant(entry) || TileOwnSlotId(entry) != null;
                // On: connected, connecting right now, or switched on and being retried by the
                // auto-retry — ⏸ is the way to stop that from the tile (msedge, 2026-10-10).
                if (vpnUp || reconnecting || (entry.AutoConnect && canConnect))
                {
                    // Busy only with ANOTHER rule: on the tile that is connecting ⏸ is how to stop it.
                    bool offBusy = _reconnectingId != null && _reconnectingId != entry.Id;
                    grid.Children.Add(MakeCornerChip(Wpf.Ui.Controls.SymbolRegular.Pause24, 13,
                        new Thickness(0, 31, 5, 0), HorizontalAlignment.Right, KillTip(entry, "Main_TipDisconnectOne"), offBusy,
                        () => DisconnectOneRule(entry), guard: entry.KillSwitch));
                    if (reconnecting)
                    {
                        // Connecting: a ring spins around the ⏸ (ui:ProgressRing spins reliably — a
                        // RenderTransform on the icon did not). It lets the clicks through to ⏸.
                        grid.Children.Add(new Wpf.Ui.Controls.ProgressRing
                        {
                            IsIndeterminate = true,
                            Width = 28,
                            Height = 28,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 28, 2, 0),
                            IsHitTestVisible = false,
                        });
                    }
                }
                else if (canConnect)
                {
                    string viaSlot = !entry.IsBlockMode && HasNoAllowedVpnVariant(entry) ? TileOwnSlotId(entry) : null;
                    grid.Children.Add(MakeCornerChip(Wpf.Ui.Controls.SymbolRegular.Play24, 14,
                        new Thickness(0, 31, 5, 0), HorizontalAlignment.Right,
                        KillTip(entry, "Main_TipConnectOne"), _reconnectingId != null,
                        async () => await ReconnectOneAsync(entry, manual: true, onlySlotId: viaSlot),   // switches the rule on, see there
                        guard: entry.KillSwitch));
                }
            }

            // The bin, always on top (▷/⏸ under it): deletes the tile with its rule, after
            // asking. Dull red under the pointer. «Всё остальное» is built in — no bin.
            if (!entry.IsBuiltIn)
            {
                var bin = MakeCornerChip(Wpf.Ui.Controls.SymbolRegular.Delete24, 13,
                    new Thickness(0, 5, 5, 0), HorizontalAlignment.Right, Loc.T("Main_TipDeleteTile"), false,
                    () => DeleteTile(entry));
                if (bin is Border binChip)
                {
                    // After the chip's own hover (handlers run in order) — the red wins.
                    binChip.MouseEnter += (s, e) => { binChip.Background = TileBinHotBg; binChip.BorderBrush = TileBinHotBorder; };
                }
                grid.Children.Add(bin);
            }

            // «Всё остальное» is not obvious: a «?» explains it — top centre, shown only while the
            // mouse is over the tile (2026-11-16; under the gear is the network card now).
            if (entry.IsBuiltIn)
            {
                var help = MakeCornerChip(Wpf.Ui.Controls.SymbolRegular.QuestionCircle24, 14,
                    new Thickness(0, 5, 0, 0), HorizontalAlignment.Center, Loc.T("Main_TipBuiltInHelp"), false,
                    () => _ = ShowBuiltInHelpAsync(entry), Theme.SpeedGreen);   // green: grey vanished over the picture
                // It sits over the tile's picture: the chip's own see-through background vanished
                // into it (2026-11-16) — a solid backing of the tile's colour under it.
                var helpBox = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 5, 0, 0),
                    Visibility = Visibility.Hidden,
                };
                helpBox.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Theme.OverlayCardBg });
                help.Margin = new Thickness(0);
                helpBox.Children.Add(help);
                border.MouseEnter += (s, e) => helpBox.Visibility = Visibility.Visible;
                border.MouseLeave += (s, e) => helpBox.Visibility = Visibility.Hidden;
                grid.Children.Add(helpBox);
            }

            border.Child = grid;

            // Every tile is clickable, left and right, whatever its state (2026-11-16: «убери
            // вообще запрет на кликабельность плитки»): the program/site opens exactly as from a
            // shortcut; «Всё остальное» has nothing to open and says where its traffic goes.
            border.MouseEnter += (s, e) =>
            {
                border.Background = Theme.TileBgHover;
                border.BorderBrush = Theme.TileBorderHover;
            };
            border.MouseLeave += (s, e) =>
            {
                // Back to how it looks — a dimmed tile stays dimmed, a noticed one amber.
                border.Background = bright ? Theme.TileBg : Theme.TileDisabledBg;
                border.BorderBrush = restBorder;
            };
            border.MouseLeftButtonUp += (s, e) => OnTileClick(entry);
            border.MouseRightButtonUp += (s, e) => { e.Handled = true; OnTileClick(entry); };

            return border;
        }

        // A tile with no sharp picture: its initials on a rounded square of a colour picked by the
        // name (the same name — the same colour, every time). Drawn, not a bitmap: sharp at any
        // scale, exactly the icon's size. Replaced the grey «?» (2026-11-17).
        private static readonly Color[] MonogramColors = Theme.MonogramColors;

        private FrameworkElement MakeMonogram(string name, double size)
        {
            name = (name ?? "").Trim();
            int h = 17;
            foreach (char c in name.ToLowerInvariant()) h = unchecked(h * 31 + c);
            var color = MonogramColors[(h & 0x7FFFFFFF) % MonogramColors.Length];
            var bg = new LinearGradientBrush(
                Color.FromArgb(0xFF, (byte)Math.Min(255, color.R + 24), (byte)Math.Min(255, color.G + 24), (byte)Math.Min(255, color.B + 24)),
                color, 90);
            bg.Freeze();
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size * 0.24),
                Background = bg,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Initials(name),
                    FontSize = size * 0.42,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.TextOnColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, size * 0.03),
                },
            };
        }

        // «12306» → «12», «GitHub» → «G», «Multi Server Sync» → «MS», «kinozal.tv» → «K».
        private static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var words = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => char.IsLetterOrDigit(w[0])).ToList();
            if (words.Count == 0) return name.Substring(0, 1).ToUpperInvariant();
            if (char.IsDigit(words[0][0])) return new string(words[0].Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
            if (words.Count >= 2 && name.Contains(' ')) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            return words[0].Substring(0, 1).ToUpperInvariant();
        }

        private async void OnTileClick(AppEntry entry)
        {
            // Nothing to launch — the tile just says where the traffic goes.
            if (entry.IsAllTraffic)
            {
                StatusFoot.Text = entry.IsBlocking ? Loc.T("Main_FootAllTrafficBlock")
                    : Loc.T("Main_FootAllTrafficOn", TileVpnLabel(entry).Replace("🛡 ", ""));
                return;
            }
            await LaunchFromTileAsync(entry).ConfigureAwait(true);
        }

        // ---------- Check flow ----------

        // Autostart connect. At logon the adapter is often not up yet, so wait for the network
        // first, then run the normal connect flow — and give it one more go half a minute later
        // if nothing came up (a Wi-Fi link that associates late is the usual reason). Silent:
        // the window is hidden, so the outcome goes to the log and the tray tooltip.
        private async Task AutoConnectAtStartupAsync()
        {
            const int WaitNetworkSeconds = 90;
            var deadline = DateTime.UtcNow.AddSeconds(WaitNetworkSeconds);
            while (!NetworkUp() && DateTime.UtcNow < deadline)
                await Task.Delay(3000).ConfigureAwait(true);

            // Only rules that were switched on are restored at logon — that is the whole point of
            // remembering the state. With none of them on there is nothing to wait for, and the
            // second attempt below would be thirty seconds spent connecting nothing.
            bool anyVpnRule = (_config.Apps ?? new List<AppEntry>())
                .Any(a => a != null && a.RequiresVpn && a.AutoConnect);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                // First pass runs on a machine that booted seconds ago — give the nodes the
                // longer, cold-start allowance. By the second pass (30 s later) it's warm.
                await RunCheckFlowAsync(attempt == 1 ? NodeUpWaitColdSeconds : NodeUpWaitSeconds)
                      .ConfigureAwait(true);
                Log.Info("Автозапуск: подключение (попытка " + attempt + ") — правил через VPN: " + _assignment.Count);
                if (_assignment.Count > 0 || !anyVpnRule) return;
                // The user opened the window in the meantime — it's their turn, don't
                // start a second connect under their hands.
                if (attempt == 1 && _hidden) await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                if (!_hidden) return;
            }
        }

        private static bool NetworkUp()
        {
            try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
            catch { return true; }   // can't tell → assume yes and let the connect flow decide
        }

        // The «ON» button's handler. The button is gone (2026-11-14); kept for nothing else but
        // reference — nothing calls it now.
        private async void OnCheck(object sender, RoutedEventArgs e)
        {
            // «Подключить» is an explicit "bring everything up": every rule that is not already
            // connected gets switched ON. Rules that already have a working node are left strictly
            // alone — not switched, not re-probed, not re-routed — because re-routing a rule that
            // works costs an engine reload, and that reload drops the connections it is carrying.
            bool any = false;
            // «Всё остальное» is not part of "everything" while there are other VPN tiles: then it
            // is switched on only by its own ⟳. With no other VPN rule it IS the whole of ON.
            // An empty «Глобально» takes ALL traffic — switched on only by its own ▷, never by ON,
            // and not counted as one of the "other" VPN tiles.
            bool otherVpnRules = (_config.Apps ?? new List<AppEntry>())
                .Any(a => a != null && a.RequiresVpn && !a.IsAllTraffic);
            foreach (var a in _config.Apps ?? new List<AppEntry>())
            {
                if (a == null || !a.RequiresVpn || (a.IsAllTraffic && otherVpnRules)
                    || a.AutoConnect || _workingApps.Contains(a.Id)) continue;
                a.AutoConnect = true;
                any = true;
            }
            if (any) SaveRuleSwitches("connect-all");
            await RunCheckFlowAsync(manual: true);
        }

        private async Task RunCheckFlowAsync(int nodeWaitSeconds = NodeUpWaitSeconds, bool manual = false)
        {
            try { _checkCts?.Cancel(); } catch { }
            _checkCts = new CancellationTokenSource();
            var ct = _checkCts.Token;

            // Without the engine there's nothing to connect through — offer to install it
            // instead of silently arming an empty config and falsely reporting "Всё работает".
            if (!_engineReady)
            {
                bool installed = await EnsureEngineInstalledAsync(ct).ConfigureAwait(true);
                RefreshEngineState();
                if (!installed)
                {
                    SetState(_hasChecked ? UiState.Done : UiState.Idle);
                    return;
                }
            }

            // What is already connected STAYS connected. This used to start from a clean slate,
            // which meant re-probing and re-routing rules that were working perfectly well — and
            // each of those costs an engine reload, i.e. the live sockets of a rule nobody asked
            // to touch. Only rules that are switched on and not up right now are swept below.

            SetState(UiState.Checking);
            _hasChecked = true;

            var vpnApps = _config.Apps.Where(a => a.RequiresVpn).ToList();

            // Nothing flagged for VPN → arming writes an empty rule set (all direct).
            if (vpnApps.Count == 0)
            {
                await ArmKillSwitchAsync(ct).ConfigureAwait(true);
                SetState(UiState.Done);
                return;
            }

            // Nothing to bring up: either no rule is switched on, or every rule that is is already
            // on a node. Arm whatever needs arming and stop. Without this, a logon with no rules
            // switched on still started the engine and probed every variant in turn — a minute of
            // reloads, each one interrupting the whole machine's traffic, to then connect nothing.
            // («Блокировать» needs no node — its lines are written by the arming/final config.)
            // A rule that ticks no VPN at all has nothing for the sweep to try: it does not count
            // (RDP, 2026-11-16: «Подождите, проверяю доступ» at every start with no VPN tile on —
            // a preflight of every variant and a full-screen wait, to connect nothing).
            if (!vpnApps.Any(a => a.AutoConnect && !a.IsNoNodeMode && !_workingApps.Contains(a.Id) && !HasNoAllowedVpnVariant(a)))
            {
                if (_assignment.Count == 0) await ArmKillSwitchAsync(ct).ConfigureAwait(true);
                else ApplyFinalConfig(CurrentAssignedProxies());
                SetState(UiState.Done);
                return;
            }

            var slots = (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList();
            if (slots.Count == 0)
            {
                SetState(UiState.Done);
                await ShowDialog(Loc.T("Main_NoVariantsTitle"), Loc.T("Main_NoVariantsBody")).ConfigureAwait(true);
                return;
            }

            // mihomo must run — during each trial the launcher itself rides the node
            // under test, so its site probe reflects exactly what the app would see.
            ConnectingDetail.Text = Loc.T("Main_DetailStartingEngine");
            if (!await EnsureMihomoRunningAsync(ct).ConfigureAwait(true))
            {
                SetState(UiState.Done);
                if (!MihomoSetup.IsTaskInstalled())
                    await ShowDialog(Loc.T("Main_EngineNotSetUpTitle"),
                        Loc.T("Main_EngineNotSetUpBody")).ConfigureAwait(true);
                else
                    await ShowEngineFailureAsync(Loc.T("Main_EngineNoStartTitle")).ConfigureAwait(true);
                return;
            }

            // Ask every variant whether it answers at all, before the sweep spends a reload
            // and a node-up wait on each of them in turn. Purely an ORDERING input: a variant
            // that stayed silent moves behind the ones that answered, it is never dropped. If
            // everything ahead of it works, its turn simply never comes — which is the whole
            // point, no minute spent connecting through a dead node — and if nothing else
            // works, it still gets tried exactly as before.
            await PreflightSlotsAsync(slots, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) { SetState(UiState.Idle); return; }
            slots = LiveFirst(slots);

            var failing = vpnApps.Where(a => a.AutoConnect && !a.IsNoNodeMode && !_workingApps.Contains(a.Id) && !HasNoAllowedVpnVariant(a)).ToList();
            // Nodes already carrying rules stay in the config: leaving them out would drop the
            // very rules this sweep is supposed to leave untouched.
            var usedProxies = CurrentAssignedProxies();
            var launcherExe = LauncherProcessName();

            // An app may be pinned to a specific variant (ForcedVpnSlotId) — important for
            // apps with no CheckUrl to probe. A pinned app is eligible only on its own
            // variant and is assigned to it unconditionally once that node is up. A pin
            // that doesn't match an enabled variant falls back to auto.
            var enabledSlotIds = new HashSet<string>(slots.Select(s => s.Id));
            string EffectiveForced(AppEntry a) => EffectiveForcedSlot(a, enabledSlotIds);

            // Which variant a rule wants NEXT — its own ranking, not the global list order.
            // Rules the user never re-ordered behave exactly as before (Settings order).
            var unreadable = new Dictionary<string, string>();   // slot name → why it didn't parse
            var tried = new Dictionary<string, HashSet<string>>();
            HashSet<string> Tried(AppEntry a)
            {
                HashSet<string> set;
                if (!tried.TryGetValue(a.Id, out set)) tried[a.Id] = set = new HashSet<string>();
                return set;
            }
            VpnSlot NextSlotFor(AppEntry a)
                => RuleSlotOrder(a, slots).FirstOrDefault(s => !Tried(a).Contains(s.Id));

            // Whole sweep, repeated for whatever is still failing. A node that wasn't ready on
            // the first lap often is by the second, and the price of another lap is only time.
            for (int pass = 1; pass <= ConnectPasses && failing.Count > 0; pass++)
            {
            if (pass > 1)
            {
                // A fresh lap re-offers every variant to everyone: a rule whose top choice was
                // dead a minute ago should get it back, and one that had to wait for a variant
                // sitting below the current index gets it here.
                tried.Clear();
                Log.Info("Не подключились с первого прохода: " + string.Join(", ", failing.Select(a => a.Name))
                         + " — иду по вариантам ещё раз.");
                try { await Task.Delay(3000, ct).ConfigureAwait(true); }
                catch (OperationCanceledException) { SetState(UiState.Idle); return; }
            }

            for (int i = 0; i < slots.Count; i++)
            {
                if (ct.IsCancellationRequested) { SetState(UiState.Idle); return; }
                if (failing.Count == 0) break;

                var slot = slots[i];
                ConnectingDetail.Text = Loc.T("Main_DetailConnectingVariant", i + 1, slots.Count, slot.Name);

                var parsed = await ParseSlotAsync(slot, i + 1, ct).ConfigureAwait(true);
                if (!parsed.Ok)
                {
                    StatusFoot.Text = Loc.T("Main_FootVariantSkipped", slot.Name, parsed.Error);
                    NoteUnreadableSlot(unreadable, slot, parsed.Error);
                    continue;
                }

                // Remember which configured variant (1-based number + name) this proxy is.
                int slotNumber = (_config.VpnSlots?.IndexOf(slot) ?? -1) + 1;
                _proxySlot[parsed.ProxyName] = new ProxySlotInfo { Id = slot.Id, Number = slotNumber, Name = slot.Name, Server = parsed.Server };
                _parsedProxy[parsed.ProxyName] = parsed;

                // Apps eligible to ride THIS variant: apps pinned to it, plus auto apps for
                // which this variant isn't disabled. A pinned app rides only its own variant
                // (the pin wins over the per-app allow-list); an auto app rides any variant
                // the user didn't uncheck for it. Others stay REJECT until their turn.
                var eligible = failing.Where(a =>
                {
                    var fid = EffectiveForced(a);
                    if (!string.IsNullOrEmpty(fid)) return fid == slot.Id;
                    // A rule rides this variant only when it's the best one it hasn't tried
                    // yet. Anything it ranked higher gets its turn first; a rule whose top
                    // choice sits further down the global list simply waits for it.
                    var want = NextSlotFor(a);
                    return want != null && want.Id == slot.Id;
                }).ToList();
                if (eligible.Count == 0) continue;
                foreach (var a in eligible) Tried(a).Add(slot.Id);

                // One trial per set of network cards (2026-11-16): the launcher's checks must cross
                // the node the way those tiles will — a VPN may answer through one card and not
                // through another. Tiles that leave the card to Windows are one group, as before;
                // with no cards anywhere this is exactly the old single trial.
                var eligibleAll = eligible;
                foreach (var cardGroup in eligibleAll.GroupBy(a => MihomoConfigBuilder.NicKey(a) ?? "").ToList())
                {
                eligible = cardGroup.ToList();
                var nicKey = cardGroup.Key.Length == 0 ? null : cardGroup.Key;

                // Config for this trial: already-assigned apps stay on their nodes (their
                // tunnels keep running in parallel); the eligible apps AND the launcher
                // ride THIS node. The launcher rule goes first so all of its probe traffic
                // goes through the node currently under test.
                // The variant under test goes in ONLY if no already-connected rule is on it. It
                // very often is: «Подключить» leaves working rules alone, so their variants are
                // in usedProxies from the start, and the sweep reaches those same variants for
                // the rules that are still down. Listing one twice made mihomo throw out the
                // whole config (400), the engine kept the previous one, and every rule whose
                // turn that was stayed unconnected — while connecting the same rule by hand
                // worked at once, because the per-tile path always deduped.
                var proxiesForConfig = new List<ParsedProxy>(usedProxies);
                if (!proxiesForConfig.Any(p => p.ProxyName == parsed.ProxyName)) proxiesForConfig.Add(parsed);
                var rules = BuildVpnRules(vpnApps, _assignment, eligible, parsed.ProxyName);
                rules.Insert(0, new RuleDef("PROCESS-NAME", launcherExe, parsed.ProxyName) { Raw = true, Probe = true, Nic = nicKey });

                _mihomo.WriteConfig(MihomoConfigBuilder.Build(_config, proxiesForConfig, rules));
                if (!await _mihomo.ReloadAsync(ct).ConfigureAwait(true))
                {
                    StatusFoot.Text = Loc.T("Main_FootVariantApplyFail", slot.Name);
                    continue;
                }

                // Wait for the node to come up — through these tiles' cards, if they have any.
                bool up = false;
                var nodeName = MihomoConfigBuilder.ViaName(parsed.ProxyName, nicKey);
                var deadline = DateTime.UtcNow.AddSeconds(nodeWaitSeconds);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (await _mihomo.IsNodeUpAsync(nodeName, ct).ConfigureAwait(true)) { up = true; break; }
                    try { await Task.Delay(500, ct).ConfigureAwait(true); }
                    catch (OperationCanceledException) { SetState(UiState.Idle); return; }
                }
                // First-hand knowledge, worth more than the preflight's: a variant that woke
                // up since then stops being demoted, one that just died starts being. Through a
                // tile's own card a failure says nothing about the VPN in general.
                if (nicKey == null || up) SlotHealth.Set(slot.Id, up);
                if (!up)
                {
                    StatusFoot.Text = Loc.T("Main_FootVariantNotUp", slot.Name);
                    continue;
                }

                // Apps with the "force connect" switch on for this variant are assigned
                // unconditionally (no probe). Everything else eligible here — auto apps and
                // apps pinned to this variant WITHOUT the switch — is verified by probing.
                var forcedHere = eligible.Where(a => EffectiveForced(a) == slot.Id && a.ForceConnect).ToList();
                var autoHere = eligible.Where(a => !(EffectiveForced(a) == slot.Id && a.ForceConnect)).ToList();

                ConnectingDetail.Text = Loc.T("Main_DetailProbingSites", slot.Name);
                var viaResults = await ProbeAppsAsync(autoHere, null, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) { SetState(UiState.Idle); return; }

                var nowWorking = new List<AppEntry>(forcedHere);
                foreach (var app in autoHere)
                    if (viaResults.TryGetValue(app.Id, out var ok) && ok) nowWorking.Add(app);

                if (nowWorking.Count > 0)
                {
                    // Same variant, one entry: a second pass re-offers the variants already
                    // tried, so without this the list grows a copy on every lap.
                    if (!usedProxies.Any(p => p.ProxyName == parsed.ProxyName)) usedProxies.Add(parsed);
                    foreach (var app in nowWorking)
                    {
                        _assignment[app.Id] = parsed.ProxyName;
                        _workingApps.Add(app.Id);
                    }
                    failing = failing.Where(a => !_workingApps.Contains(a.Id)).ToList();
                }
                }   // card group
            }
            }

            // Final config: assigned apps on their nodes, the launcher REMOVED (back to
            // direct). This is the kill-switch: split apps are forced through their node
            // with no DIRECT fallback, so if a node drops mihomo BLOCKS that traffic (no
            // IP leak) while mihomo runs — no background polling or app-killing.
            ApplyFinalConfig(usedProxies);
            SetState(UiState.Done);

            // Resolve each used VPN server's country/city in the background, then refresh
            // tiles so each app shows where its VPN actually exits ("Франция, Париж").
            _ = ResolveProxyGeosAsync();

            // Something stayed off and part of the reason is a variant that couldn't be read —
            // say so instead of just leaving the tiles grey. Only to someone who pressed the
            // button: the startup auto-connect calls this twice in a row, a modal would stall it.
            if (manual && failing.Count > 0 && unreadable.Count > 0)
                await ShowUnreadableSlotsAsync(Loc.T("Main_ConnectFailedManyTitle",
                    string.Join(", ", failing.Select(a => a.Name))), unreadable).ConfigureAwait(true);
        }

        // Geo-locate the server of every assigned proxy; fill _proxyGeo and refresh tiles.
        private async Task ResolveProxyGeosAsync()
        {
            try
            {
                var servers = _assignment.Values.Distinct()
                    .Where(p => _proxySlot.TryGetValue(p, out var info) && !string.IsNullOrWhiteSpace(info?.Server))
                    .ToList();
                bool changed = false;
                foreach (var proxy in servers)
                {
                    if (_proxyGeo.ContainsKey(proxy)) continue;
                    var host = _proxySlot[proxy].Server;
                    var geo = await GeoIpService.LookupHostAsync(host).ConfigureAwait(true);
                    var text = geo?.Display ?? "";
                    if (!string.IsNullOrEmpty(text)) { _proxyGeo[proxy] = text; changed = true; }
                }
                if (changed) RebuildTiles();
            }
            catch { }
            // Every other variant too — the picker and the grey plates show where it exits.
            await ResolveSlotGeosAsync().ConfigureAwait(true);
        }

        // Resolve Store/MSIX apps whose stored WindowsApps path has a version baked in:
        // find their stable AUMID + current exe path so updates never break launching —
        // no re-setup needed. Runs in the background; saves + refreshes only if it
        // changed anything.
        private async Task AutoHealStoreAppsAsync()
        {
            try
            {
                // Every program (across all rules) that lives under WindowsApps and is missing
                // its AUMID or whose versioned path no longer exists.
                var candidates = _config.Apps.Where(a => a != null)
                    .SelectMany(a => a.Programs ?? new List<RuleProgram>())
                    .Where(p => p != null &&
                        !string.IsNullOrEmpty(p.Path) &&
                        p.Path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (string.IsNullOrEmpty(p.Aumid) || !System.IO.File.Exists(p.Path))).ToList();
                if (candidates.Count == 0) return;

                var apps = await Task.Run(() => StoreApps.Query()).ConfigureAwait(true);
                if (apps.Count == 0) return;
                var byFamily = apps.GroupBy(a => a.Family, StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                bool changed = false;
                foreach (var prog in candidates)
                {
                    var fam = StoreApps.FamilyFromPath(prog.Path);
                    if (string.IsNullOrEmpty(fam)) continue;
                    if (byFamily.TryGetValue(fam, out var match))
                    {
                        if (prog.Aumid != match.Aumid) { prog.Aumid = match.Aumid; changed = true; }
                        if (!string.IsNullOrEmpty(match.Exe) && prog.Path != match.Exe) { prog.Path = match.Exe; changed = true; }
                    }
                }

                if (changed)
                {
                    AppConfigStore.Save(_config);
                    RebuildTiles();
                }
            }
            catch { }
        }

        // Make sure every AutoIcon entry has its server icon on disk (IconFetcher) and its
        // IconPath pointed at it. Runs at startup because a launcher update wipes Data\ (and
        // thus the icon cache) while the saved AutoIcon flag survives — so icons re-download
        // automatically. Saves + refreshes tiles only if anything changed. Best-effort/quiet.
        private async Task EnsureAutoIconsAsync(bool checkServer = true)
        {
            try
            {
                // On a pool thread: .NET Framework resolves DNS synchronously on the thread that
                // starts a request, so an await from here still froze the window (11 s cold DNS on
                // a two-card machine, 2026-11-16). Snapshot the list on the UI thread.
                var apps = _config.Apps.ToList();
                bool changed = await Task.Run(() => IconFetcher.EnsureAsync(apps, CancellationToken.None, checkServer)).ConfigureAwait(true);
                if (changed)
                {
                    AppConfigStore.Save(_config);
                    RebuildTiles();
                }
            }
            catch { }
        }

        // Weekly: re-fetch the blackmatrix7 rule list of every SITE (across all rules) whose
        // cache is stale, store it back, and re-arm so the fresh ranges take effect. Quiet.
        private async Task RefreshLinkRulesAsync()
        {
            try
            {
                var stale = _config.Apps.Where(a => a != null)
                                        .SelectMany(a => a.Sites ?? new List<RuleSite>())
                                        .Where(s => ClashRuleService.ShouldRefresh(s)).ToList();
                if (stale.Count == 0) return;

                bool changed = false;
                foreach (var site in stale)
                {
                    // Off the UI thread — the synchronous DNS of .NET Framework would freeze the window.
                    var s = site;
                    try { await Task.Run(() => ClashRuleService.RefreshAsync(s)).ConfigureAwait(true); changed = true; }
                    catch { }
                }
                if (!changed) return;

                AppConfigStore.Save(_config);
                await ApplyRuleListChangesAsync().ConfigureAwait(true);
            }
            catch { }
        }

        // Rule lists changed underneath the running config — put them into effect.
        private async Task ApplyRuleListChangesAsync()
        {
            try
            {
                if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                {
                    // Connected → reload the fresh ranges into the LIVE config so routes stay
                    // up (re-arming here would kick every working rule offline). Idle → arm.
                    if (_hasChecked) ApplyFinalConfig(CurrentAssignedProxies());
                    else await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                }
            }
            catch { }
        }

        // Template list URLs saved by builds before 2026-11-03 point at raw.githubusercontent.com
        // (often unreachable from RU) or byfox.dev/data/awg-panel, and for the big domain-set
        // templates at a file holding only part of the list (Apple: 45 lines of 1604). Move each
        // to its api.byfox.dev classical list and let the refresh below re-download it; the
        // cached items keep routing until then.
        private void MigrateTemplateUrls()
        {
            try
            {
                int moved = 0;
                foreach (var site in _config.Apps.Where(a => a != null).SelectMany(a => a.Sites ?? new List<RuleSite>()))
                {
                    if (site == null || !site.HasRuleList) continue;
                    var now = BlackMatrixCatalog.MigrateUrl(site.ClashRuleUrl);
                    if (string.Equals(now, site.ClashRuleUrl, StringComparison.Ordinal)) continue;
                    Log.Info("Шаблон: " + site.ClashRuleUrl + " → " + now);
                    site.ClashRuleUrl = now;
                    site.RuleFetchedUtc = "";
                    moved++;
                }
                if (moved == 0) return;
                AppConfigStore.Save(_config);
                Log.Info("Шаблоны: " + moved + " адрес(ов) переведено на api.byfox.dev.");
            }
            catch (Exception ex) { Log.Error("template-url-migrate", ex); }
        }

        // IP-ASN rules from template lists take effect only once the engine has ASN.mmdb (see
        // MihomoConfigBuilder.AsnDbReady). Fetched only when some list actually has them.
        private async Task EnsureAsnDbAsync()
        {
            try
            {
                // Always fetched now (2026-11-16): the speed window names each address's provider
                // from it. The engine reads it only for IP-ASN rules, so having it changes no config.
                if (_mihomo == null) return;
                bool needed = _config.Apps.Where(a => a != null)
                    .SelectMany(a => a.Sites ?? new List<RuleSite>())
                    .Any(s => s?.RuleItems != null && s.RuleItems.Any(i => (i ?? "").StartsWith("IP-ASN,", StringComparison.OrdinalIgnoreCase)));
                bool had = System.IO.File.Exists(System.IO.Path.Combine(_mihomo.WorkDir, "ASN.mmdb"));
                // A refresh of an existing base needs no reload; the first one brings the IP-ASN rules in.
                if (await Task.Run(() => UpdateService.EnsureAsnDbAsync(_mihomo.WorkDir, CancellationToken.None)).ConfigureAwait(true) && !had && needed)
                    await ApplyRuleListChangesAsync().ConfigureAwait(true);
            }
            catch (Exception ex) { Log.Error("asn-db", ex); }
        }

        // A variant ready for a config. A subscription is fetched first, with its rule lists, and
        // its rules checked by the engine: its routing has to be known before any tile rides it.
        private async Task<ParsedProxy> ParseSlotAsync(VpnSlot slot, int number, CancellationToken ct)
        {
            var parsed = ProxyParsers.Parse(slot, number);
            if (parsed.Ok && parsed.IsProvider && !string.IsNullOrEmpty(parsed.ProviderUrl))
            {
                try
                {
                    var why = await SubscriptionPolicy.PrepareAsync(_config, parsed, ct).ConfigureAwait(true);
                    if (why != null) { parsed.Ok = false; parsed.Error = why; }
                }
                catch (OperationCanceledException) { }
            }
            return parsed;
        }

        // This launcher's own image name, used to route it through the trial node while
        // probing (so the check sees exactly what the app would through that node).
        private static string LauncherProcessName()
        {
            try
            {
                var name = System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return VersionInfo.ExeName;
        }

        // Write the definitive config: assigned apps on their nodes, every other VPN
        // app left at REJECT (blocked) — the kill-switch stays armed for them.
        // `force` = push it even if the text is identical. Only the repair paths need that
        // (routes lost, engine restarted under us): there the point is to make the engine
        // re-read, not to change anything. Everywhere else an unchanged config must NOT be
        // reloaded — see the comment on MihomoController.WriteConfig. The case that made this
        // urgent: an auto-retry for a rule that cannot connect restores exactly the config that
        // was live before it started, so every single retry was a reload that changed nothing.
        // On mom's machine that was 207 retries of one rule, ~390 reloads in a week, and under
        // fake-ip each one takes the machine's RESOLVER down with it — measured: five reloads
        // in three seconds, then a minute in which the launcher itself could not resolve
        // api.github.com or byfox.dev. That is what "the launcher killed my internet" was.
        // `cutFiles`: program files whose open connections are cut after the reload — helpers just
        // auto-added to a tile (MainWindow.AutoTrack), so they redial through it.
        private void ApplyFinalConfig(List<ParsedProxy> usedProxies, string reason = null, bool force = false,
                                      IEnumerable<AppEntry> alsoCut = null, List<string> cutFiles = null)
        {
            var vpnApps = _config.Apps.Where(a => a.RequiresVpn).ToList();
            var liveProxyNames = new HashSet<string>(_assignment.Values);
            var liveProxies = usedProxies.Where(p => liveProxyNames.Contains(p.ProxyName)).ToList();

            // Which rules have just been moved to a different node (or taken off one)? Writing
            // the config decides where the NEXT connection goes and nothing else, so a browser
            // that already holds keep-alives goes on carrying its traffic out of the OLD node.
            // That is what «выбрал Турцию, а IP киргизский» was: the rule was switched, the
            // config said so, and Chrome's live sockets kept using the previous variant until it
            // closed them itself. Cut exactly the rules that moved — cutting the rest would drop
            // working connections for nothing.
            var moved = MovedSinceLastApply();
            // A tile whose path changed without changing node (its network cards, «Напрямую»).
            foreach (var a in alsoCut ?? Enumerable.Empty<AppEntry>())
                if (a != null && !moved.Contains(a)) moved.Add(a);

            var rules = BuildVpnRules(vpnApps, _assignment, null, null);
            try
            {
                bool changed = _mihomo.WriteConfig(MihomoConfigBuilder.Build(_config, liveProxies, rules));
                if (changed || force)
                {
                    var reload = _mihomo.ReloadAsync(CancellationToken.None, reason ?? "применение правил");
                    // After the reload finishes, not before: the engine would re-open them on
                    // the route it is still running. And ONLY if it actually took the new config:
                    // a rejected reload leaves the OLD routing live, so cutting here would kill a
                    // rule's traffic and then hand it straight back to the route it just lost —
                    // the rule goes dark for no reason at all, kill-switch or no kill-switch.
                    if (moved.Count > 0 || (cutFiles != null && cutFiles.Count > 0))
                        _ = reload.ContinueWith(t =>
                        {
                            if (t.Status == TaskStatus.RanToCompletion && t.Result)
                            {
                                if (moved.Count > 0) CloseConnectionsFor(moved);
                                CloseProcessConnections(cutFiles);
                            }
                            else Log.Info("Соединения не разрывались: движок не принял новый конфиг, "
                                          + "правила остались на старом маршруте.");
                        });
                }
                RememberAppliedAssignment();
            }
            catch (Exception ex) { Log.Error("apply-final-config", ex); }

            PublishVpnStatus();
        }

        // Rules whose node differs from the one the engine is actually running for them. A rule
        // that LOST its assignment counts too: it has just dropped to REJECT (kill-switch) or to
        // DIRECT, and its open sockets are precisely the ones that must not survive that.
        private List<AppEntry> MovedSinceLastApply()
        {
            var moved = new List<AppEntry>();
            foreach (var app in _config.Apps ?? new List<AppEntry>())
            {
                if (app == null || string.IsNullOrEmpty(app.Id)) continue;
                string now, before;
                if (!_assignment.TryGetValue(app.Id, out now)) now = null;
                if (!_appliedAssignment.TryGetValue(app.Id, out before)) before = null;
                if (before == null && now == null) continue;          // never routed, still isn't
                if (!string.Equals(before, now, StringComparison.Ordinal)) moved.Add(app);
            }
            return moved;
        }

        private void RememberAppliedAssignment()
        {
            _appliedAssignment.Clear();
            foreach (var kv in _assignment) _appliedAssignment[kv.Key] = kv.Value;
            // A tile moved to another VPN: two tiles that were on the same route may not be any more.
            try { Dispatcher.BeginInvoke(new Action(UpdateConflictIcon)); } catch { }
        }

        // Write the idle "armed" config (all VPN apps -> REJECT) so they have no internet
        // until "Подключить" connects them. Called at startup so the kill-switch is on
        // even before mom presses anything (the launcher runs at logon). No-op if mihomo
        // isn't reachable yet.
        private async Task ArmKillSwitchAsync(CancellationToken ct, List<string> cutFiles = null)
        {
            try
            {
                // Write FIRST, reload second. Bailing out early when the API is down used to
                // leave config.yaml unwritten — and since mihomo exits instantly without one,
                // the engine could never come up to make the API reachable in the first place.
                // That deadlock was permanent: the file is only ever written here and during
                // engine setup, so a work dir without it (fresh copy of the folder, wiped
                // DataUser) stayed broken forever, with only "last result 0x1" to show for it.
                bool changed = _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config));
                if (!await _mihomo.IsApiReachableAsync(ct).ConfigureAwait(true)) return;

                // Nothing to push: the engine is already running exactly this text. At logon
                // that is the normal case — the engine starts by reading this very file and
                // then this method rewrote it byte for byte — and reloading it anyway cost the
                // whole machine its internet for a few seconds at every single startup.
                if (!changed) return;

                bool took = await _mihomo.ReloadAsync(ct, "взвод кил-свитча (не подключено)").ConfigureAwait(true);

                // Armed means blocked from now on — including for the connections that were
                // opened while it wasn't.
                CloseConnectionsFor((_config.Apps ?? new List<AppEntry>())
                    .Where(a => a != null && a.KillSwitch && !_assignment.ContainsKey(a.Id)));
                if (took) CloseProcessConnections(cutFiles);
            }
            catch (Exception ex) { Log.Error("arm-kill-switch", ex); }
        }

        // Push the current routing picture to the shared VpnStatus (read by the
        // diagnostics tab and the main-window badge).
        private void PublishVpnStatus()
        {
            var routes = new List<VpnRoute>();
            foreach (var kv in _assignment)
            {
                var app = _config.Apps.FirstOrDefault(a => a.Id == kv.Key);
                if (app == null) continue;
                _proxySlot.TryGetValue(kv.Value, out var info);
                routes.Add(new VpnRoute
                {
                    AppName = app.Name,
                    ProxyName = kv.Value,
                    SlotNumber = info?.Number ?? 0,
                    SlotName = info?.Name ?? kv.Value,
                    // The rule's own address (check address, else the one moved out of it).
                    Target = HostOf(app.OwnUrl),
                    NodeUp = true,
                });
            }
            VpnStatus.SetRoutes(routes);
        }

        private class ProxySlotInfo
        {
            public string Id;       // the VpnSlot this proxy came from — see SlotSignatures
            public int Number;
            public string Name;
            public string Server;   // VPN endpoint host/IP, for geo lookup
        }

        // The variant an app is pinned to (only if it still matches an enabled variant).
        private static string EffectiveForcedSlot(AppEntry a, HashSet<string> enabledSlotIds)
            => (!string.IsNullOrEmpty(a.ForcedVpnSlotId) && enabledSlotIds.Contains(a.ForcedVpnSlotId))
                ? a.ForcedVpnSlotId : "";

        // The parsed proxies behind the current assignments (for rebuilding the config).
        private List<ParsedProxy> CurrentAssignedProxies()
        {
            return _assignment.Values.Distinct()
                .Select(p => _parsedProxy.TryGetValue(p, out var pp) ? pp : null)
                .Where(p => p != null).ToList();
        }

        // "Отключиться": drop every VPN route and re-arm the kill-switch — VPN apps go back
        // to blocked until the next connect. Non-VPN apps keep working directly.
        private async void OnDisconnect(object sender, RoutedEventArgs e)
        {
            try { _checkCts?.Cancel(); } catch { }
            // Switch every rule OFF, and let that outlive the session: the next start restores
            // what was on when the launcher closed, so "I turned everything off" must not come
            // back as "restore everything" after a reboot.
            bool any = false;
            foreach (var a in _config.Apps ?? new List<AppEntry>())
                if (a != null && a.AutoConnect) { a.AutoConnect = false; any = true; }
            if (any) SaveRuleSwitches("disconnect-all");
            _workingApps.Clear();
            _assignment.Clear();
            _proxySlot.Clear();
            _proxyGeo.Clear();
            _parsedProxy.Clear();
            VpnStatus.Clear();
            _hasChecked = false;
            try { await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true); } catch { }
            SetState(UiState.Idle);
            StatusFoot.Text = Loc.T("Main_FootVpnOff");
        }

        // A variant counts as dead only while the observation is still worth trusting: an
        // outage that ended minutes ago must not keep a healthy node out of the running.
        private static readonly TimeSpan DeadSlotSkipFor = TimeSpan.FromMinutes(5);

        private static bool RecentlyDead(string slotId)
        {
            if (SlotHealth.Get(slotId) != SlotState.Dead) return false;
            var at = SlotHealth.CheckedAt(slotId);       // local time, as SlotHealth records it
            return at.HasValue && DateTime.Now - at.Value < DeadSlotSkipFor;
        }

        // Re-run the connect logic for ONE service, keeping every other app on its node:
        // build a config with the existing assignments + this entry (and the launcher) on a
        // trial node, then probe (or assign immediately if its "force connect" switch is on).
        private readonly HashSet<string> _waitingReconnect = new HashSet<string>();

        private async Task ReconnectOneAsync(AppEntry entry, bool manual = false, string onlySlotId = null)
        {
            var unreadable = new Dictionary<string, string>();   // slot name → why it didn't parse
            if (entry == null || !entry.RequiresVpn) return;
            if (entry.IsNoNodeMode)
            {
                // ▷ on a tile set to «Блокировать» / «Напрямую»: switch it on — no VPN to look for.
                StartBlock(entry);
                return;
            }
            if (_state == UiState.Checking)
            {
                // A press while another connect is running (the one at startup, say) used to be
                // dropped without a word — the tile "worked only the second time" (2026-11-09).
                // A press is kept: it waits for that connect to finish and then runs.
                if (!manual || !_waitingReconnect.Add(entry.Id)) return;   // ten presses = one wait
                try
                {
                    StatusFoot.Text = Loc.T("Main_FootWaitingForConnect", entry.Name);
                    var until = DateTime.UtcNow.AddMinutes(3);
                    while (_state == UiState.Checking && DateTime.UtcNow < until)
                        await Task.Delay(300).ConfigureAwait(true);
                }
                finally { _waitingReconnect.Remove(entry.Id); }
                if (_state == UiState.Checking || _reconnectingId != null) return;
            }

            // The tile's ⟳ means "switch this rule on", and that is exactly the state that has
            // to persist: from now on the rule comes back by itself after a restart, and the
            // auto-retry is allowed to look after it.
            // Off after «Напрямую» with VPNs ticked: its ▷ brings the kill-switch back as it was,
            // exactly like picking a VPN on the plate (saved by SetAutoConnect just below).
            RestoreKillAfterDirect(entry);
            SetAutoConnect(entry, true, "reconnect-one");

            var slots = (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList();
            if (slots.Count == 0)
            {
                await ShowDialog(Loc.T("Main_NoVariantsTitle"), Loc.T("Main_NoVariantsBody")).ConfigureAwait(true);
                return;
            }

            try { _checkCts?.Cancel(); } catch { }
            _checkCts = new CancellationTokenSource();
            var ct = _checkCts.Token;

            // Inline reconnect: keep the tiles visible and spin THIS tile's ⟳ icon
            // (via _reconnectingId) instead of covering everything with the full-screen
            // connecting overlay. Progress goes to the footer line, not the overlay text.
            _state = UiState.Checking;
            _hasChecked = true;
            _reconnectingId = entry.Id;

            // Forget this entry's current route; keep everyone else where they are.
            _assignment.Remove(entry.Id);
            _workingApps.Remove(entry.Id);

            StatusFoot.Text = Loc.T("Main_FootReconnectingApp", entry.Name);
            RebuildTiles();

            bool assigned = false;
            try
            {
                if (!await EnsureMihomoRunningAsync(ct).ConfigureAwait(true))
                {
                    await ShowEngineFailureAsync(Loc.T("Main_EngineNotRunningTitle")).ConfigureAwait(true);
                    return;
                }

                var vpnApps = _config.Apps.Where(a => a.RequiresVpn).ToList();
                var enabledSlotIds = new HashSet<string>(slots.Select(s => s.Id));
                var launcherExe = LauncherProcessName();

                var forcedId = EffectiveForcedSlot(entry, enabledSlotIds);
                // Same ranking the full connect uses — the ⟳ on a tile must reach for this
                // rule's preferred variant first, not for whatever is on top in Settings.
                // onlySlotId: picked by hand from the tile's VPN plate — that variant and no other.
                var candidates = !string.IsNullOrEmpty(onlySlotId)
                    ? slots.Where(s => s.Id == onlySlotId).ToList()
                    : string.IsNullOrEmpty(forcedId)
                    ? RuleSlotOrder(entry, slots)
                    : slots.Where(s => s.Id == forcedId).ToList();

                // Trying a variant is expensive: it rewrites the config, reloads the engine —
                // which takes every connection on the machine down with it for a second or two —
                // and then waits up to NodeUpWaitSeconds for a node that may never answer. Paying
                // that to re-learn what this session already found out buys nothing. With one
                // dead variant at the top of the order and four rules pointing at it, that was
                // four reloads and eighty seconds of dead network per retry round, every round —
                // which is what «весь интернет умер» looked like from the outside.
                // Never skip the last one standing: if every candidate is down, one of them still
                // has to be tried or the rule could never come back on its own.
                // The tile's own network cards (null = Windows decides). «Dead» was learned through
                // Windows' main card; a tile tied to another card may well reach that VPN — so for
                // it nothing is skipped (ekb_home 2026-11-16: dead via Ethernet, alive via Wi-Fi).
                var nicKey = MihomoConfigBuilder.NicKey(entry);
                var liveCandidates = candidates.Where(s => !RecentlyDead(s.Id)).ToList();
                if (liveCandidates.Count > 0 && nicKey == null) candidates = liveCandidates;

                // entry.AutoConnect: the tile's ✕ may switch the rule off while this runs — then
                // stop trying and do not assign it.
                for (int i = 0; i < candidates.Count && !assigned && entry.AutoConnect; i++)
                {
                    if (ct.IsCancellationRequested) return;
                    var slot = candidates[i];
                    int slotNumber = (_config.VpnSlots?.IndexOf(slot) ?? -1) + 1;
                    StatusFoot.Text = Loc.T("Main_FootTryingVariant", entry.Name, slotNumber, slot.Name);

                    var parsed = await ParseSlotAsync(slot, slotNumber, ct).ConfigureAwait(true);
                    if (!parsed.Ok)
                    {
                        StatusFoot.Text = Loc.T("Main_FootVariantSkipped", slot.Name, parsed.Error);
                        NoteUnreadableSlot(unreadable, slot, parsed.Error);
                        continue;
                    }
                    _proxySlot[parsed.ProxyName] = new ProxySlotInfo { Id = slot.Id, Number = slotNumber, Name = slot.Name, Server = parsed.Server };
                    _parsedProxy[parsed.ProxyName] = parsed;

                    var proxiesForConfig = CurrentAssignedProxies();
                    if (!proxiesForConfig.Any(p => p.ProxyName == parsed.ProxyName)) proxiesForConfig.Add(parsed);

                    var rules = BuildVpnRules(vpnApps, _assignment, new List<AppEntry> { entry }, parsed.ProxyName);
                    // The launcher's checks cross the node the way the tile will — through its cards.
                    rules.Insert(0, new RuleDef("PROCESS-NAME", launcherExe, parsed.ProxyName) { Raw = true, Probe = true, Nic = nicKey });
                    _mihomo.WriteConfig(MihomoConfigBuilder.Build(_config, proxiesForConfig, rules));
                    if (!await _mihomo.ReloadAsync(ct).ConfigureAwait(true)) { StatusFoot.Text = Loc.T("Main_FootApplyFail", slot.Name); continue; }

                    bool up = false;
                    var nodeName = MihomoConfigBuilder.ViaName(parsed.ProxyName, nicKey);
                    var deadline = DateTime.UtcNow.AddSeconds(NodeUpWaitSeconds);
                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && entry.AutoConnect)
                    {
                        if (await _mihomo.IsNodeUpAsync(nodeName, ct).ConfigureAwait(true)) { up = true; break; }
                        try { await Task.Delay(500, ct).ConfigureAwait(true); }
                        catch (OperationCanceledException) { return; }
                    }
                    if (!entry.AutoConnect) break;   // switched off mid-wait: says nothing about the node
                    // Through a tile's own card a failure says nothing about the VPN in general.
                    if (nicKey == null || up) SlotHealth.Set(slot.Id, up);
                    if (!up) { StatusFoot.Text = Loc.T("Main_FootVariantNotUp", slot.Name); continue; }

                    bool ok;
                    if (forcedId == slot.Id && entry.ForceConnect)
                    {
                        ok = true;   // "force connect" switch — skip the probe
                    }
                    else
                    {
                        StatusFoot.Text = Loc.T("Main_FootProbingApp", entry.Name, slot.Name);
                        var r = await ProbeAppsAsync(new List<AppEntry> { entry }, null, ct).ConfigureAwait(true);
                        ok = r.TryGetValue(entry.Id, out var v) && v;
                    }
                    if (ct.IsCancellationRequested) return;
                    if (ok && entry.AutoConnect) { _assignment[entry.Id] = parsed.ProxyName; _workingApps.Add(entry.Id); assigned = true; }
                }

                ApplyFinalConfig(CurrentAssignedProxies());
            }
            catch (OperationCanceledException) { }   // ✕ or a full connect took over — handled below
            finally
            {
                _reconnectingId = null;

                // Cancelled by this tile's ✕: the attempt may have left its trial config in the
                // engine (the rule riding the node under test). Put back the real picture and
                // cut whatever the trial opened — the rule is off now.
                bool stoppedByHand = !entry.AutoConnect;
                if (stoppedByHand && ct.IsCancellationRequested)
                {
                    try { ApplyFinalConfig(CurrentAssignedProxies()); } catch (Exception ex) { Log.Error("reconnect-stopped", ex); }
                    CloseConnectionsFor(new[] { entry });
                }

                // If our token was cancelled otherwise, it's because a full connect took over the
                // UI — leave the state/overlay to that flow and just drop the inline spinner.
                if (!ct.IsCancellationRequested || stoppedByHand)
                {
                    SetState(UiState.Done);
                    // Connected / switched off is on the tile; only a failure gets a line.
                    StatusFoot.Text = !entry.AutoConnect || assigned ? ""
                        : entry.IsAllTraffic ? Loc.T("Main_FootAllTrafficFailed")
                        : Loc.T("Main_FootReconnectFailed", entry.Name);
                    _ = ResolveProxyGeosAsync();
                }
            }

            // A variant that can't even be read (its .conf was moved, a link got mangled) fails in
            // a millisecond, and the footer line that said so is overwritten straight away — from
            // the outside the ⟳ simply did nothing. Someone who pressed it gets told why; the
            // background auto-retry only logs (NoteUnreadableSlot), it must not pop a window every 90 s.
            if (manual && !assigned && !ct.IsCancellationRequested && unreadable.Count > 0)
                await ShowUnreadableSlotsAsync(Loc.T("Main_ConnectFailedTitle", entry.Name), unreadable).ConfigureAwait(true);
        }

        // Once per session per distinct error, so the auto-retry doesn't repeat it every round.
        private readonly HashSet<string> _loggedSlotErrors = new HashSet<string>();

        private void NoteUnreadableSlot(Dictionary<string, string> unreadable, VpnSlot slot, string error)
        {
            unreadable[slot.Name] = error;
            if (_loggedSlotErrors.Add(slot.Id + "|" + error))
                Log.Info("VPN-вариант «" + slot.Name + "» не прочитан: " + error);
        }

        private Task ShowUnreadableSlotsAsync(string title, Dictionary<string, string> unreadable)
        {
            var list = string.Join("\n", unreadable.Select(kv => "• " + kv.Key + " — " + kv.Value));
            return ShowDialog(title, Loc.T("Main_VariantsUnreadableBody", list));
        }

        // The variants a rule may use, in ITS OWN order of preference: the ids it ranked first
        // (VpnSlotOrder), then everything else in the Settings order. Disabled-for-this-rule
        // variants are dropped. A rule that was never re-ordered comes back exactly as the
        // global list — the per-rule ranking is a refinement, never a new default.
        private static List<VpnSlot> RuleSlotOrder(AppEntry a, List<VpnSlot> slots)
        {
            var order = a?.VpnSlotOrder ?? new List<string>();
            return LiveFirst((slots ?? new List<VpnSlot>())
                .Where(s => s != null && !(a?.DisabledVpnSlotIds?.Contains(s.Id) ?? false))
                // OrderBy is stable, so everything unranked keeps the Settings order.
                .OrderBy(s => { int i = order.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; })
                .ToList());
        }

        // Same list, with the variants that didn't answer the last check moved to the back.
        //
        // Applied to the rule's own ranking too, not just to the global sweep: otherwise a
        // rule whose favourite variant is dead would sit and wait for that variant's turn —
        // which, having been demoted, now comes last — and connect later than it used to.
        // Demoted, never removed: "didn't answer" is a reason to try it last, not a verdict
        // to act on. Unknown ranks with alive, so before the first check nothing moves.
        private static List<VpnSlot> LiveFirst(List<VpnSlot> slots)
        {
            return (slots ?? new List<VpnSlot>())
                .OrderBy(s => SlotHealth.Get(s.Id) == SlotState.Dead ? 1 : 0)   // stable
                .ToList();
        }

        // One config carrying EVERY variant, one reload, then all of them probed at once.
        // This is the "test all" button every Clash dashboard has — running by itself,
        // because here there is nobody to press it.
        //
        // Best-effort throughout: if a variant won't parse, or the engine won't take the
        // probe config, the result is simply Unknown and the sweep runs exactly as it did
        // before. The check can only reorder the queue, never shorten it.
        private async Task PreflightSlotsAsync(List<VpnSlot> slots, CancellationToken ct)
        {
            if (slots == null || slots.Count < 2) return;   // nothing to order a single variant against

            var proxies = new List<ParsedProxy>();
            var slotByProxy = new Dictionary<string, VpnSlot>(StringComparer.Ordinal);
            for (int i = 0; i < slots.Count; i++)
            {
                var parsed = ProxyParsers.Parse(slots[i], i + 1);
                if (parsed == null || !parsed.Ok) continue;   // a broken variant is the sweep's business, not ours
                proxies.Add(parsed);
                slotByProxy[parsed.ProxyName] = slots[i];
            }
            if (proxies.Count < 2) return;

            try
            {
                ConnectingDetail.Text = Loc.T("Main_DetailCheckingVariants");
                _mihomo.WriteConfig(MihomoConfigBuilder.BuildProbe(_config, proxies));
                if (!await _mihomo.ReloadAsync(ct).ConfigureAwait(true))
                {
                    Log.Info("Предварительная проверка вариантов: движок не принял конфиг — иду по вариантам как обычно.");
                    return;
                }

                var answered = await _mihomo.CheckNodesAsync(slotByProxy.Keys, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested) return;

                foreach (var kv in answered)
                {
                    VpnSlot slot;
                    if (slotByProxy.TryGetValue(kv.Key, out slot)) SlotHealth.Set(slot.Id, kv.Value);
                }

                var alive = slotByProxy.Values.Where(s => SlotHealth.Get(s.Id) == SlotState.Alive).Select(s => s.Name).ToList();
                var dead = slotByProxy.Values.Where(s => SlotHealth.Get(s.Id) == SlotState.Dead).Select(s => s.Name).ToList();
                Log.Info("Предварительная проверка вариантов: отвечают — "
                         + (alive.Count > 0 ? string.Join(", ", alive) : "нет")
                         + "; не отвечают — " + (dead.Count > 0 ? string.Join(", ", dead) : "нет") + ".");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("PreflightSlots", ex); }
        }

        // Build rules covering EVERY VPN app: assigned -> its proxy; trial apps ->
        // trialProxy; anything else -> REJECT (kill-switch on) or DIRECT (kill-switch off,
        // "fail open"). Non-VPN traffic falls through to MATCH,DIRECT.
        private List<RuleDef> BuildVpnRules(List<AppEntry> vpnApps, Dictionary<string, string> assignment,
                                            List<AppEntry> trialApps, string trialProxy)
        {
            var rules = new List<RuleDef>();
            var trialIds = new HashSet<string>((trialApps ?? Enumerable.Empty<AppEntry>()).Select(a => a.Id));
            var vpnSet = new HashSet<AppEntry>(vpnApps ?? new List<AppEntry>());
            // In tile order — the tile further left wins. A tile «без VPN» writes lines only under
            // its kill-switch (blocked: the kill-switch works whatever the tile's mode).
            foreach (var app in (_config.Apps ?? new List<AppEntry>()).Where(a => a != null && (vpnSet.Contains(a) || a.KillSwitch)))
            {
                if (!app.RequiresVpn)
                {
                    MihomoConfigBuilder.AddAppRules(rules, app, "REJECT");
                    continue;
                }
                string target;
                // «Блокировать», switched on: blocked by its own scope, no node involved.
                if (app.IsBlocking) target = "REJECT";
                else if (assignment != null && assignment.TryGetValue(app.Id, out var p)) target = p;
                else if (!string.IsNullOrEmpty(trialProxy) && trialIds.Contains(app.Id)) target = trialProxy;
                // Not on a node: the kill-switch blocks it whether the rule is switched on (VPN
                // down / still connecting) or off — a kill-switch rule never goes out without the
                // VPN. Without the kill-switch it fails open to DIRECT.
                // «Всё остальное» too, switched on or off (user, 2026-11-13).
                else target = app.KillSwitch ? "REJECT" : "DIRECT";
                MihomoConfigBuilder.AddAppRules(rules, app, target);
            }
            return rules;
        }

        // The address a rule is CHECKED with: the check field and nothing else (2026-11-23 — field
        // empty = not checked). No fallback to a site or to the old single-item Path/Arguments: the
        // loader wrote whatever the old connect really probed into the field (AppConfigStore
        // NormalizeCheckAddresses), so nothing that was checked stops being checked.
        private static string ResolveCheckUrl(AppEntry app)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.CheckUrl)) return "";
            return app.CheckUrl.Trim();
        }

        private static string HostOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try { return new Uri(url).Host; } catch { return ""; }
        }

        // Probe a set of apps' CheckUrls concurrently (optionally through a proxy).
        // Returns App.Id -> reachable.
        private async Task<Dictionary<string, bool>> ProbeAppsAsync(List<AppEntry> apps, IWebProxy proxy, CancellationToken ct)
        {
            var result = new Dictionary<string, bool>();
            var targets = new List<KeyValuePair<AppEntry, string>>();
            foreach (var a in apps)
            {
                var url = ResolveCheckUrl(a);
                // Not checked (no check address; SkipCheck is kept equal to that): connect it
                // regardless of any site — the rule rides the first configuration whose node is up.
                if (a.SkipCheck) result[a.Id] = true;
                else if (string.IsNullOrEmpty(url)) result[a.Id] = true;  // nothing to probe — don't gate
                else targets.Add(new KeyValuePair<AppEntry, string>(a, url));
            }
            if (targets.Count == 0) return result;

            var tasks = targets.Select(async t =>
            {
                bool ok = false;
                try
                {
                    SiteProbeResult r = null;
                    // Keep asking while the answer is "nothing came back" — that's the cold
                    // tunnel, not a verdict. A page that DID answer "blocked" is a statement
                    // about this exit IP, and repeating it only delays the next variant.
                    for (int attempt = 1; attempt <= SiteCheckAttempts && !ct.IsCancellationRequested; attempt++)
                    {
                        r = await SiteChecker.ProbeAsync(t.Value, SiteCheckTimeoutSeconds, ct, proxy).ConfigureAwait(false);
                        ok = r.Available;
                        if (ok || r.StatusCode != 0) break;
                        Log.Info("Проверка «" + t.Key.Name + "» (" + t.Value + "), попытка " + attempt
                                 + " из " + SiteCheckAttempts + ": " + r.Reason);
                        try { await Task.Delay(2000, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                    if (!ok && r != null) Log.Info("Правило «" + t.Key.Name + "» не подключилось: " + r.Reason);
                }
                catch { ok = false; }
                return new KeyValuePair<string, bool>(t.Key.Id, ok);
            }).ToList();

            var done = await Task.WhenAll(tasks).ConfigureAwait(true);
            foreach (var kv in done) result[kv.Key] = kv.Value;
            return result;
        }

        // Make sure mihomo is up: if the API doesn't answer, start its scheduled task and wait.
        // A single /Run isn't always enough — the scheduler still considers a crashed instance
        // "running" (the task is MultipleInstancesPolicy=IgnoreNew), so the start is silently
        // ignored and the API never appears. That's the intermittent «не удалось запустить
        // движок»: the second pass ends the task first and starts it clean. Every failure is
        // logged with the task's state so app.log says why, instead of nothing at all.
        private async Task<bool> EnsureMihomoRunningAsync(CancellationToken ct)
        {
            bool ok = await EnsureMihomoRunningCoreAsync(ct).ConfigureAwait(true);
            // Every path into the engine goes through here, so this is the one place that
            // knows whether the engine is currently a lost cause — and therefore whether the
            // main screen should be offering to reinstall it.
            if (ok == _engineBroken)
            {
                _engineBroken = !ok;
                try { RefreshEngineState(); } catch { }
            }
            return ok;
        }

        private async Task<bool> EnsureMihomoRunningCoreAsync(CancellationToken ct)
        {
            var state = await _mihomo.ProbeApiAsync(ct).ConfigureAwait(true);
            if (state == MihomoController.ApiState.Up) return true;
            if (!MihomoSetup.IsTaskInstalled())
            {
                Log.Info("Движок: задача планировщика не установлена — запускать нечего.");
                return false;
            }

            // A task pointing at a path that no longer exists (folder moved or renamed) just
            // fails with 0x80070002 every time — retrying can't fix it, only re-creating the
            // task can. Stop here instead of burning 40 seconds on two doomed attempts; the
            // caller shows the "not set up" prompt, whose single UAC re-registers it.
            string mismatch;
            if (!MihomoSetup.TaskMatchesConfig(_config, out mismatch))
            {
                Log.Info("Задача планировщика указывает не туда — нужна повторная настройка. " + mismatch);
                return false;
            }

            // Never hand the scheduler a task that has nothing to read: mihomo quits at once
            // without config.yaml, and all the task records is "last result 0x1".
            EnsureConfigOnDisk();

            // The engine answers but refuses our token: it's still running on an older
            // config.yaml (different secret). Nothing can be reloaded through the API in that
            // state, so write a fresh armed config and restart the task to pick it up.
            if (state == MihomoController.ApiState.Unauthorized)
            {
                LogEngineFailureOnce("unauth", "Движок отвечает, но не принимает ключ (старый config.yaml) — перезапускаю с текущим конфигом.");
                try { _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config)); }
                catch (Exception ex) { Log.Error("engine-rewrite-config", ex); }
                // A 401 means something IS speaking the engine's API there, with a secret from a
                // config.yaml we've since replaced — in practice our own engine, left running by
                // a session that ended badly. /End alone doesn't reach an instance the scheduler
                // has lost, so kill it before starting ours: otherwise both run, each with its
                // own TUN, and the new one is the one that loses the route table.
                await Task.Run(() => MihomoSetup.StartEngineAlone(_config), ct).ConfigureAwait(true);
                if (await _mihomo.WaitApiReachableAsync(EngineStartWaitSeconds, ct).ConfigureAwait(true)) { _engineFailLogged.Clear(); return true; }
                if (ct.IsCancellationRequested) return false;
                // A fresh engine started from OUR config.yaml cannot answer 401 — the token in
                // it is the one we just wrote. So the thing on that port isn't ours, and no
                // number of restarts will make it ours: take a port of our own instead.
                if (await TryRelocateApiPortAsync(ct).ConfigureAwait(true)) return true;
                LogEngineStartFailure();
                return false;
            }

            await Task.Run(() => MihomoSetup.StartEngineAlone(_config), ct).ConfigureAwait(true);
            if (await _mihomo.WaitApiReachableAsync(EngineStartWaitSeconds, ct).ConfigureAwait(true)) { _engineFailLogged.Clear(); return true; }
            if (ct.IsCancellationRequested) return false;

            LogEngineFailureOnce("norun", "Движок не ответил за " + EngineStartWaitSeconds + " с — перезапускаю задачу. " + MihomoSetup.TaskState());
            await Task.Run(() => MihomoSetup.StartEngineAlone(_config), ct).ConfigureAwait(true);
            if (await _mihomo.WaitApiReachableAsync(EngineRetryWaitSeconds, ct).ConfigureAwait(true))
            {
                Log.Info("Движок поднялся со второй попытки.");
                _engineFailLogged.Clear();
                return true;
            }
            if (ct.IsCancellationRequested) return false;

            // Same story as above, minus the 401: the port can also be held by something that
            // doesn't speak the API at all. If anyone is listening there while our engine is
            // running and silent, that anyone is the reason.
            if (await TryRelocateApiPortAsync(ct).ConfigureAwait(true)) return true;

            LogEngineStartFailure();
            return false;
        }

        // The control port is taken by a program we can't stop — another clash-family client,
        // or an engine of ours left over from a session that ended badly (it runs elevated, so
        // a non-admin launcher can neither /End nor kill it). mihomo doesn't quit over that; it
        // runs on with a dead controller, which is why this used to loop for hours ("движок
        // установлен, но не отвечает"). Move OUR controller to a free port and restart: the
        // number exists only in config.yaml, so nothing else has to be re-registered.
        // Returns true if the engine answers on the new port.
        private async Task<bool> TryRelocateApiPortAsync(CancellationToken ct)
        {
            try
            {
                string host; int port;
                if (!PortGuard.SplitAddr(_config?.MihomoApiAddr ?? "127.0.0.1:9090", out host, out port)) return false;

                int ownerPid = PortGuard.ListenerPid(port);
                var owner = PortGuard.DescribeListener(port);
                if (owner.Length == 0) return false;      // port is free — the engine is failing for some other reason

                // Is the squatter our OWN engine, left over from a session that ended badly?
                // Then moving our port is the wrong cure and the expensive one: the leftover
                // keeps running — holding its TUN, the machine's :53 and the mixed-port our site
                // checks go through — while we start a second engine beside it. Two TUN adapters
                // then fight over the route table, and traffic breaks in ways no rule explains.
                // Kill it and take back the port we always had; only a genuinely foreign program
                // (another clash-family client) is worth moving away from.
                // Identification, in the order the evidence is actually available: the image
                // path when it can be read at all (it can't be, for an elevated process), else
                // the process name — which IS readable, and which for us is the raw GitHub asset
                // name, not something a repackaged client would share.
                var ourExe = (_config?.MihomoExePath ?? "").Trim();
                string ourName = "";
                try { ourName = System.IO.Path.GetFileNameWithoutExtension(ourExe); } catch { }
                var ownerPath = PortGuard.ImagePath(ownerPid);
                bool looksLikeOurs = ourExe.Length > 0 && (ownerPath.Length > 0
                    ? string.Equals(ownerPath, ourExe, StringComparison.OrdinalIgnoreCase)
                    : ourName.Length > 0 && string.Equals(PortGuard.ProcessNameOf(ownerPid), ourName,
                                                          StringComparison.OrdinalIgnoreCase));
                // Any engine of ours alive while our own API is unreachable is an orphan, whatever
                // is holding the port — the name check can only ever add to that, never overrule it.
                if (looksLikeOurs || MihomoSetup.CountEngines(_config) > 0)
                {
                    Log.Info("Порт " + port + " держит наш же движок от прошлого запуска (" + owner
                             + ") — убираю его и занимаю порт обратно.");
                    await Task.Run(() => MihomoSetup.ForceStopEngine(_config, ownerPid), ct).ConfigureAwait(true);

                    // THE line that matters. Moving our controller to a free port makes the API
                    // work again and leaves the actual problem untouched: the orphan keeps its TUN
                    // interface, with the SAME 198.18.0.1 on it, and we start a second engine
                    // beside it. From then on the route table — not us — decides which tunnel
                    // carries the machine, and when it picks the orphan every packet runs on that
                    // engine's stale config, i.e. MATCH,DIRECT: the launcher shows everything
                    // connected while the traffic leaves straight out with the real IP, and each
                    // further restart adds another engine and another adapter. That is exactly
                    // what happened on 2026-08-16 (ports 9090 → 9500 → 9501, three engines, three
                    // Wintun adapters, nothing routed).
                    //
                    // So: no second engine, ever. If the old one cannot be removed, say so and
                    // stop — no tunnel at all is a state the user can see and the kill-switch can
                    // act on; two tunnels is a silent leak.
                    if (MihomoSetup.CountEngines(_config) > 0)
                    {
                        LogEngineFailureOnce("orphan",
                            "Старый движок остановить не удалось — второй запускать нельзя (у него свой "
                            + "туннель с тем же адресом, и трафик пойдёт мимо правил). "
                            + (MihomoSetup.IsStopTaskInstalled()
                                ? "Нужен перезапуск Windows или ручная остановка."
                                : "Нажмите «Переустановить движок» — регистрируется задача принудительной остановки."));
                        return false;
                    }

                    await Task.Run(() => MihomoSetup.StartEngineAlone(_config), ct).ConfigureAwait(true);
                    if (await _mihomo.WaitApiReachableAsync(EngineRetryWaitSeconds, ct).ConfigureAwait(true))
                    {
                        Log.Info("Движок отвечает на прежнем порту " + port + " — переезд не понадобился.");
                        _engineFailLogged.Clear();
                        return true;
                    }

                    // The orphan is gone and ours still can't be reached here: re-read who holds
                    // the port now. Only a listener that is NOT an engine of ours justifies the
                    // move below.
                    ownerPid = PortGuard.ListenerPid(port);
                    owner = PortGuard.DescribeListener(port);
                    if (owner.Length == 0) return false;
                    if (MihomoSetup.CountEngines(_config) > 1)
                    {
                        LogEngineFailureOnce("twin", "Движков снова больше одного — порт не переношу.");
                        return false;
                    }
                    // The one we just started is the listener: the scheduler respawned it. It is
                    // ours, and moving the port would start a second one beside it — the two-tunnel
                    // state the whole block above exists to prevent. It's silent for another reason.
                    if (ourName.Length > 0 && string.Equals(PortGuard.ProcessNameOf(ownerPid), ourName,
                                                            StringComparison.OrdinalIgnoreCase))
                    {
                        LogEngineFailureOnce("ownsilent", "Порт " + port + " держит наш только что запущенный движок ("
                                             + owner + "), но он не отвечает — порт не переношу, второй движок не запускаю.");
                        return false;
                    }
                }

                if (_apiPortMoves >= MaxApiPortMoves)
                {
                    LogEngineFailureOnce("moves", "Порт " + port + " занят (" + owner + "), но переносить API больше некуда.");
                    return false;
                }

                int next = PortGuard.PickFree(port);
                if (next <= 0) return false;

                _apiPortMoves++;
                Log.Info("Порт управления " + port + " занят чужим процессом: " + owner
                         + ". Перевожу движок на " + next + ".");
                _config.MihomoApiAddr = host + ":" + next;

                // A clash-family client that took 9090 has almost certainly taken 7890 as well,
                // and that one is worse than it looks: site checks go through mixed-port, so
                // they would be measuring THEIR tunnel while we route through ours. Only move
                // it when it's held by the very same process — our own engine listens there
                // legitimately whenever it started but couldn't bind the controller.
                int mixed = _config.MixedPort > 0 ? _config.MixedPort : 7890;
                if (ownerPid > 0 && PortGuard.ListenerPid(mixed) == ownerPid)
                {
                    int nextMixed = PortGuard.PickFree(mixed, next);
                    if (nextMixed > 0)
                    {
                        Log.Info("Порт прокси " + mixed + " занят тем же процессом — перевожу на " + nextMixed + ".");
                        _config.MixedPort = nextMixed;
                    }
                }
                AppConfigStore.Save(_config);
                _mihomo = new MihomoController(_config);
                _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config));

                await Task.Run(() => MihomoSetup.StartEngineAlone(_config), ct).ConfigureAwait(true);
                if (await _mihomo.WaitApiReachableAsync(EngineRetryWaitSeconds, ct).ConfigureAwait(true))
                {
                    Log.Info("Движок отвечает на порту " + next + " — управление восстановлено.");
                    _engineFailLogged.Clear();
                    return true;
                }
                Log.Info("Движок не ответил и на порту " + next + ".");
                return false;
            }
            catch (Exception ex) { Log.Error("api-port-relocate", ex); return false; }
        }

        // The watchdog runs every 20 s, so a failure that lasts a night writes the same two
        // lines a thousand times and buries everything else in app.log. Say it once per
        // distinct failure; the next success clears the memory of it.
        private void LogEngineFailureOnce(string tag, string message)
        {
            if (!_engineFailLogged.Add(tag)) return;
            Log.Info(message);
        }

        // The idle (armed) config has to be on disk before the engine is ever started. Normally
        // engine setup writes it, so this only fires when the work dir lost it — a folder copied
        // without DataUser\mihomo, a wiped data dir, a relocated work dir.
        // Before every start: the file the engine is about to read must be one it will START on.
        // "It exists" was the whole check, and that is exactly the hole prof.urfo fell through:
        // a config.yaml written by an older build carried a value the engine refuses, the engine
        // died on it at once, and since the launcher rewrites the config only once the engine
        // answers, nothing ever replaced it — reinstall, restart, same file, same death. Now a
        // missing OR refused file is rebuilt fresh (WriteConfig checks that one too, and falls
        // back to a minimal config if even the fresh one would be refused).
        private void EnsureConfigOnDisk()
        {
            try
            {
                var path = _mihomo?.ConfigPath ?? "";
                bool exists = path.Length > 0 && System.IO.File.Exists(path);
                string why = "";
                if (exists && _mihomo.ConfigOnDiskAccepted(out why) != false) return;

                _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config));
                Log.Info(exists
                    ? "config.yaml движок не принял бы (" + why + ") — перед запуском записал свежий: " + path
                    : "config.yaml отсутствовал — записал стартовый конфиг: " + path);
            }
            catch (Exception ex) { Log.Error("ensure-config-on-disk", ex); }
        }

        // Everything we'd otherwise have to ask the user to check by hand, in one line.
        private void LogEngineStartFailure()
        {
            try
            {
                var exe = (_config?.MihomoExePath ?? "").Trim();
                var cfg = _mihomo?.ConfigPath ?? "";
                var api = _config?.MihomoApiAddr ?? "";
                // Who holds the control port is the one thing this line never said, and it is
                // the answer whenever the engine "is installed but doesn't respond".
                string host, owner = "";
                int p;
                if (PortGuard.SplitAddr(api, out host, out p))
                    owner = PortGuard.DescribeListener(p);
                LogEngineFailureOnce("nostart", "Движок не запустился. " + MihomoSetup.TaskState()
                    + "; exe=" + exe + " (" + (exe.Length > 0 && System.IO.File.Exists(exe) ? "есть" : "НЕТ") + ")"
                    + "; процесс " + (IsMihomoProcessRunning() ? "работает" : "не найден")
                    + "; config.yaml=" + cfg + " (" + (cfg.Length > 0 && System.IO.File.Exists(cfg)
                        ? "есть, " + (_mihomo?.DescribeConfigVerdict() ?? "?") : "НЕТ") + ")"
                    + "; API=" + api + " (" + (owner.Length > 0 ? "занят: " + owner : "никто не слушает") + ")"
                    + "; безопасный режим " + (_config?.RemoteDesktopSafeMode == true ? "ВКЛ" : "выкл")
                    + "; другой VPN: " + (OtherVpn.Running() is string v && v.Length > 0 ? v : "нет"));
            }
            catch { }
        }

        // What to tell the user when the engine won't start. Safe mode force-stops the engine
        // every 60 seconds, so it's the first thing to suspect — name it instead of the
        // generic "try again".
        private string EngineNoStartBody()
        {
            if (_config?.RemoteDesktopSafeMode == true) return Loc.T("Main_EngineNoStartSafeMode");

            // The engine is fine, Windows just hasn't run its scheduled task yet — it holds tasks
            // back on an almost empty battery and right after an update restart (PAPA2,
            // 2026-11-08: four minutes «в очереди», then sleep). Reinstalling moves the engine
            // to the Windows service, which Windows does not hold back.
            if (MihomoSetup.LegacyTaskQueued()) return Loc.T("Main_EngineNoStartQueued");

            // Second suspect: something else owns the control port. We try to move off it on
            // our own, so reaching this message means even that didn't help — then the least
            // useless thing to say is WHICH program is in the way.
            // Third: another VPN running beside us. Two tunnels fight over the routes and the
            // firewall; the cure is closing the other one, which only the user can do.
            var otherVpn = OtherVpn.Running();
            if (otherVpn.Length > 0) return Loc.T("Main_EngineNoStartOtherVpn", otherVpn);

            string host; int port;
            if (PortGuard.SplitAddr(_config?.MihomoApiAddr ?? "", out host, out port))
            {
                var owner = PortGuard.DescribeListener(port);
                // Our engine's own name on the port, and even the service could not remove it:
                // a copy from another (old) install. Not "another VPN client" — say what helps.
                var engineName = "";
                try { engineName = System.IO.Path.GetFileNameWithoutExtension((_config?.MihomoExePath ?? "").Trim()); } catch { }
                if (owner.Length > 0 && engineName.Length > 0
                    && owner.StartsWith(engineName + " ", StringComparison.OrdinalIgnoreCase))
                    return Loc.T("Main_EngineNoStartOldCopy", owner);
                if (owner.Length > 0) return Loc.T("Main_EngineNoStartPortBusy", port, owner);
            }
            return Loc.T("Main_EngineNoStartBody");
        }

        // First-run engine install: if mihomo.exe isn't present, offer to download the latest
        // stable build (GitHub, falling back to byfox.dev) and run the one-time elevated setup
        // (single UAC). Also covers the half-installed case (exe on disk but the scheduled
        // task never registered, e.g. setup was cancelled): it skips the download and just
        // re-runs the elevated setup. Returns true if the engine ends up installed + set up,
        // false otherwise (user declined, offline, or setup cancelled). No-op when ready.
        // skipConfirm: caller is an explicit "install" tap, so go straight to download/setup
        // without the extra yes/no prompt (used by the install banner button).
        private async Task<bool> EnsureEngineInstalledAsync(CancellationToken ct)
        {
            // Hidden (autostart): the prompt would be invisible and its await would never
            // return. Report "not installed" and let the user do it from the open window.
            if (_hidden) return false;

            var exe = (_config.MihomoExePath ?? "").Trim();
            bool exeOk = exe.Length > 0 && System.IO.File.Exists(exe);
            if (exeOk && MihomoSetup.IsTaskInstalled())
            {
                // The task bakes BOTH the engine path and the working dir into its command
                // (mihomo.exe -d "<dir>"). Moving or renaming the install folder leaves those
                // pointing at nothing, and the elevated engine then either can't start at all
                // or reads config.yaml from the wrong place. Detect either mismatch and fall
                // through to re-setup, so a single UAC re-creates the task where it belongs.
                // (Unreadable task → assume OK, don't nag.)
                string mismatch;
                if (MihomoSetup.TaskMatchesConfig(_config, out mismatch)) return true;
                Log.Info("Задача планировщика устарела → повторная настройка. " + mismatch);
            }

            // exe already downloaded but not set up → only the elevated setup remains. The
            // prompt carries the "Remote desktop session" safe-mode switch so you can turn it
            // on BEFORE configuring (important when doing this over RDP/AnyDesk).
            string body = exeOk
                ? Loc.T("Main_EngineNeededBodySetup")
                : Loc.T("Main_EngineNeededBodyInstall");
            var res = await OverlayDialog.ConfirmWithToggleAsync(this, Loc.T("Main_EngineNeededTitle"), body,
                exeOk ? Loc.T("Main_EngineNeededSetupBtn") : Loc.T("Main_EngineNeededInstallBtn"), Loc.T("Main_Later"),
                Loc.T("Main_KillTimerLabel"), _config.RemoteDesktopSafeMode, Loc.T("Main_EngineNeededRdpHint")).ConfigureAwait(true);

            // Apply the switch regardless of which button closed it (you may enable safe mode
            // and press "Later" to keep configuring remotely).
            if (res.Toggle != _config.RemoteDesktopSafeMode)
            {
                _config.RemoteDesktopSafeMode = res.Toggle;
                try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("safemode-save", ex); }
                SyncSafeModeUi();
                ApplyRdpModeRouting(res.Toggle);
            }
            if (!res.Ok) return false;

            ShowUpdateProgress(Loc.T("Main_InstallingEngineTitle"),
                exeOk ? Loc.T("Main_SettingUpAutostart") : Loc.T("Main_ResolvingVersion"));
            try
            {
                if (!exeOk)
                {
                    var src = await UpdateService.ResolveMihomoSourceAsync(ct).ConfigureAwait(true);
                    if (src == null)
                        throw new Exception(Loc.T("Main_NoConnectionResolveEngine"));

                    // Place the engine at a writable default if no path is set yet.
                    var target = (_config.MihomoExePath ?? "").Trim();
                    if (target.Length == 0) target = AppConfigStore.DefaultMihomoExeTarget;
                    _config.MihomoExePath = target;

                    SetUpdateDetail(Loc.T("Main_DownloadingMihomo", src.Label));
                    await UpdateService.UpdateMihomoAsync(_config, src.Version, src.ZipUrl,
                        p => SetUpdateDetail(Loc.T("Main_DownloadingMihomoPct", src.Label, p)),
                        ct).ConfigureAwait(true);

                    _mihomo = new MihomoController(_config);
                    SetTitleCaption();
                }

                // One-time elevated setup (scheduled task) — writes config.yaml, then a single UAC.
                SetUpdateDetail(Loc.T("Main_SettingUpAutostart"));
                MihomoSetup.EnsureSecret(_config);
                AppConfigStore.Save(_config);
                try { _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config)); } catch { }

                string err;
                bool setup = MihomoSetup.InstallElevated(_config, out err);
                HideUpdateProgress();
                if (!setup)
                {
                    await ShowDialog(Loc.T("Main_SetupFailTitle"),
                        Loc.T("Main_SetupFailBody", err)).ConfigureAwait(true);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("install-engine", ex);
                HideUpdateProgress();
                await ShowDialog(Loc.T("Main_InstallEngineFailTitle"), ex.Message).ConfigureAwait(true);
                return false;
            }
        }

        // Full engine reinstall, start to finish, with no questions asked: stop it, delete the
        // exe, download a fresh one, re-register the scheduled task, start it and re-arm the
        // kill-switch. This is the repair the user had to perform by hand every time the engine
        // wedged — reboots didn't fix those installs, reinstalling did — so it belongs on a
        // button next to every engine failure instead of in a support conversation.
        //
        // The one thing it can't do silently is register the scheduled task: that needs
        // elevation, so Windows shows its own consent prompt (and nothing else does).
        private async Task<bool> ReinstallEngineAsync()
        {
            if (_updating) return false;
            _updating = true;
            ShowUpdateProgress(Loc.T("Main_ReinstallEngineTitle"), Loc.T("Main_ReinstallStopping"));
            SetUpdatePercent(-1);
            Log.Info("Переустановка движка: начало.");
            try
            {
                // 1. Down it goes, TUN and all. Then wait for the process to actually vanish —
                //    schtasks returns long before that, and a live process keeps the exe locked.
                await Task.Run(() => EngineShutdown.Stop(_config)).ConfigureAwait(true);
                for (int i = 0; i < 20 && IsMihomoProcessRunning(); i++)
                {
                    if (i == 5) await Task.Run(() => MihomoSetup.EndTask()).ConfigureAwait(true);
                    await Task.Delay(500).ConfigureAwait(true);
                }

                // 2. The old binary is NOT deleted up front: the download below replaces it only
                //    once the new one has passed its sha256 — with a single source
                //    (api.byfox.dev), deleting first would leave no engine at all when it's down.
                var exe = (_config.MihomoExePath ?? "").Trim();
                if (exe.Length == 0) exe = _config.MihomoExePath = AppConfigStore.DefaultMihomoExeTarget;

                // 3. Fresh binary from api.byfox.dev (sha256-checked).
                SetUpdateDetail(Loc.T("Main_ResolvingVersion"));
                var src = await UpdateService.ResolveMihomoSourceAsync(CancellationToken.None).ConfigureAwait(true);
                if (src == null) throw new Exception(Loc.T("Main_NoConnectionResolveEngine"));

                SetUpdateDetail(Loc.T("Main_DownloadingMihomo", src.Label));
                await UpdateService.UpdateMihomoAsync(_config, src.Version, src.ZipUrl,
                    p => { SetUpdatePercent(p); SetUpdateDetail(Loc.T("Main_DownloadingMihomoPct", src.Label, p)); },
                    CancellationToken.None).ConfigureAwait(true);
                SetUpdatePercent(-1);

                // 4. Re-register the task from scratch. Half of what "reinstall the engine"
                //    ever fixed lives here: a task pointing at a stale path, or one whose
                //    instance the scheduler lost track of.
                SetUpdateDetail(Loc.T("Main_SettingUpAutostart"));
                _mihomo = new MihomoController(_config);
                MihomoSetup.EnsureSecret(_config);
                AppConfigStore.Save(_config);
                try { _mihomo.WriteConfig(MihomoConfigBuilder.BuildArmed(_config)); } catch { }

                string err;
                if (!MihomoSetup.InstallElevated(_config, out err))
                {
                    HideUpdateProgress();
                    Log.Info("Переустановка движка: настройка автозапуска не прошла. " + err);
                    await ShowDialog(Loc.T("Main_SetupFailTitle"), Loc.T("Main_SetupFailBody", err)).ConfigureAwait(true);
                    return false;
                }

                // 5. Start it and put the routing back the way it was.
                SetUpdateDetail(Loc.T("Main_ReinstallStarting"));
                _apiPortMoves = 0;
                _engineFailLogged.Clear();
                SetTitleCaption();
                bool up = await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true);
                if (up) await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);

                HideUpdateProgress();
                RefreshEngineState();
                Log.Info("Переустановка движка: " + (up ? "готово, движок отвечает." : "движок установлен, но не ответил."));
                StatusFoot.Text = up ? Loc.T("Main_FootEngineReinstalled") : Loc.T("Main_FootEngineLost");
                if (!up) await ShowDialog(Loc.T("Main_EngineNoStartTitle"), EngineNoStartBody()).ConfigureAwait(true);
                return up;
            }
            catch (Exception ex)
            {
                Log.Error("reinstall-engine", ex);
                HideUpdateProgress();
                await ShowDialog(Loc.T("Main_ReinstallFailTitle"), ex.Message).ConfigureAwait(true);
                return false;
            }
            finally
            {
                _updating = false;
                try { RefreshEngineState(); } catch { }
            }
        }

        // The engine didn't come up. Never a bare "sorry": the message carries the repair
        // button, so the way out is one tap instead of a phone call. Hidden (autostarted)
        // windows can't show a dialog at all — they only get the log line.
        private CancellationTokenSource _engineFailureDismiss;

        private async Task ShowEngineFailureAsync(string title)
        {
            if (_hidden)
            {
                try { Log.Info("(скрыт) " + title + ": " + EngineNoStartBody()); } catch { }
                return;
            }
            try { _engineFailureDismiss?.Cancel(); } catch { }
            var dismiss = _engineFailureDismiss = new CancellationTokenSource();
            bool fix = await OverlayDialog.ConfirmAsync(this, title, EngineNoStartBody(),
                Loc.T("Main_ReinstallEngineBtn"), Loc.T("Dlg_Close"), dismiss.Token).ConfigureAwait(true);
            if (dismiss.IsCancellationRequested) { StatusFoot.Text = Loc.T("Main_FootEngineRestored"); return; }
            if (fix) await ReinstallEngineAsync().ConfigureAwait(true);
        }

        // ---------- Buttons ----------

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try { _checkCts?.Cancel(); } catch { }
            SetState(_hasChecked ? UiState.Done : UiState.Idle);
            StatusFoot.Text = Loc.T("Main_FootStopped");
        }

        // Banner button shown when the engine is missing: download + set up mihomo now, then
        // arm the kill-switch and refresh the UI. Reuses the same flow as the first-run prompt.
        private async void OnInstallEngineClick(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            // Installed but broken → the button is «Переустановить движок» and does exactly
            // that, without the first-run prompt: there is nothing to decide, the engine on
            // disk is the thing that doesn't work.
            if (_engineReady && _engineBroken)
            {
                try { await ReinstallEngineAsync().ConfigureAwait(true); }
                catch (Exception ex) { Log.Error("reinstall-engine-click", ex); }
                return;
            }

            bool installed = false;
            try
            {
                // Tapping the banner shows the same prompt as on first run (with the safe-mode
                // switch) instead of installing straight away.
                installed = await EnsureEngineInstalledAsync(CancellationToken.None).ConfigureAwait(true);
                if (installed)
                {
                    // Engine is on disk + set up — hide the install banner right away so it
                    // doesn't keep hanging there. The remaining post-setup (start the engine,
                    // arm the kill-switch) takes a couple seconds, so keep a clear progress
                    // overlay up instead of leaving the user staring at a stale screen.
                    RefreshEngineState();
                    ShowUpdateProgress(Loc.T("Main_StartingEngineTitle"), Loc.T("Main_AlmostReady"));
                    try
                    {
                        if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                            await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                    }
                    catch (Exception ex) { Log.Error("install-engine-arm", ex); }
                    HideUpdateProgress();
                }
            }
            catch (Exception ex) { Log.Error("install-engine", ex); }
            RefreshEngineState();
            if (installed) StatusFoot.Text = Loc.T("Main_FootEngineInstalled");
        }

        // The VPN table. It edits the live config entries in place (and only on Save), so the
        // work here is the same one Settings does after a per-rule routing change: drop exactly
        // the rules whose routing moved, leave every other rule on its node. Re-sweeping the lot
        // would cost an engine reload and the open connections of rules that did not change.
        private async void OnOpenVpnMatrix(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                var beforeApps = AppRoutingSignatures(_config);
                var dlg = new VpnMatrixWindow(_config) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Result == null) return;

                AppConfigStore.Save(_config);

                bool moved = false;
                var afterApps = AppRoutingSignatures(_config);
                foreach (var kv in beforeApps)
                {
                    string now;
                    if (afterApps.TryGetValue(kv.Key, out now) && now == kv.Value) continue;
                    _assignment.Remove(kv.Key);
                    _workingApps.Remove(kv.Key);
                    moved = true;
                }
                if (!moved) return;

                // ApplyFinalConfig cuts the live sockets of every rule that just changed node,
                // so a rule moved to another variant does not go on using the old one.
                ApplyFinalConfig(CurrentAssignedProxies());
                RebuildTiles();
                _ = ResolveProxyGeosAsync();
            }
            catch (Exception ex) { Log.Error("open-vpn-matrix", ex); }
        }

        private async void OnOpenSettings(object sender, RoutedEventArgs e)
        {
            try { await OpenSettingsAsync().ConfigureAwait(true); }
            catch (Exception ex) { Log.Error("open-settings", ex); }
        }

        // «VPN» / «+»: the VPN list and the rules list. They edit _config live — every change goes
        // through CommitLiveEditAsync — so there is nothing to pick up when they close.
        private void OnOpenVpnList(object sender, RoutedEventArgs e) => OpenListsWindow(ListsWindow.Mode.Vpn);
        private void OnOpenRulesList(object sender, RoutedEventArgs e) => OpenListsWindow(ListsWindow.Mode.Rules);

        private void OpenListsWindow(ListsWindow.Mode mode)
        {
            try
            {
                var dlg = new ListsWindow(_config, mode, CommitLiveEditAsync) { Owner = this };
                dlg.ShowDialog();
                if (mode == ListsWindow.Mode.Vpn) _ = ResolveSlotGeosAsync();   // a VPN added / changed
            }
            catch (Exception ex) { Log.Error("open-lists-" + mode, ex); }
        }

        private async void OnOpenDns(object sender, RoutedEventArgs e)
        {
            try { await OpenSettingsAsync(dnsOnly: true).ConfigureAwait(true); }
            catch (Exception ex) { Log.Error("open-settings-dns", ex); }
        }

        // Sage icon + the servers in the tooltip while the user's own direct-traffic DNS is on.
        private void SyncDnsButton()
        {
            try
            {
                var active = DirectDns.Active(_config);
                IcoDns.Foreground = active.Count > 0 ? Theme.AccentGreen : Theme.TextOnAction;
                BtnDns.ToolTip = active.Count > 0
                    ? Loc.T("Main_DnsTipOwn", string.Join(", ", active.Select(x => x.Engine)))
                    : Loc.T("Main_DnsTipNone");
            }
            catch (Exception ex) { Log.Error("dns-button", ex); }
        }

        private async Task OpenSettingsAsync(bool dnsOnly = false)
        {
            var before = TakeConfigSnap();
            var dlg = new SettingsWindow(_config, dnsOnly) { Owner = this, TileIcon = TileIconSmall, TileName = TileDisplayName };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _config = dlg.Result;
                AppConfigStore.Save(_config);
                _mihomo = new MihomoController(_config);
                // Settings hands back a NEW config object, so the log feed is still watching the
                // old one — re-point it at this one (address and secret can both have changed
                // here). Restarting the feed costs nothing: the history lives in the store.
                EngineLogStore.Instance.Start(_config);
                // The engine path may have been repointed in Settings — re-read its version.
                VersionInfo.EngineExePath = _config.MihomoExePath ?? "";
                try { VersionInfo.SyncEngineVersionFromBinary(); } catch { }

                // The engine may have been (re)downloaded in Settings, which stamps a new
                // local mihomo version — refresh the title-bar caption to match.
                SetTitleCaption();

                await ApplyConfigChangeAsync(before).ConfigureAwait(true);
                RebuildTiles();   // «Сеть» → the card strip on tiles may have been switched on/off
            }
        }

        // What the live picture depends on, taken BEFORE an edit: ApplyConfigChangeAsync compares
        // the config after the edit against it and touches only what moved.
        private sealed class ConfigSnap
        {
            public string Routing, Engine, Dns, Order;
            public Dictionary<string, string> Slots, Apps;
        }

        private ConfigSnap TakeConfigSnap() => new ConfigSnap
        {
            Routing = RoutingSignature(_config),
            Engine = EngineSignature(_config),
            Slots = SlotSignatures(_config),
            Apps = AppRoutingSignatures(_config),
            Dns = DirectDns.Signature(_config),
            Order = RuleOrder(_config),
        };

        // The VPN list and the rules list (VpnListWindow / RulesListWindow) have no «Сохранить»:
        // every add / edit / delete / drag is applied at once. They edit _config itself; each
        // change runs here — snapshot, the change, save, apply — one at a time, so a second click
        // while the first one is still re-arming the engine waits its turn instead of comparing
        // against a half-applied picture.
        private readonly SemaphoreSlim _liveEditGate = new SemaphoreSlim(1, 1);

        internal async Task CommitLiveEditAsync(Func<Task> change)
        {
            await _liveEditGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var before = TakeConfigSnap();
                await change().ConfigureAwait(true);
                AppConfigStore.Save(_config);
                await ApplyConfigChangeAsync(before).ConfigureAwait(true);
            }
            catch (Exception ex) { Log.Error("live-edit", ex); throw; }   // the list window says why
            finally { _liveEditGate.Release(); }
        }

        private async Task ApplyConfigChangeAsync(ConfigSnap before)
        {
            var beforeEngine = before.Engine;
            var beforeSlots = before.Slots;
            var beforeApps = before.Apps;
            var beforeDns = before.Dns;
            var beforeOrder = before.Order;
            // Only touch the live connection if something that affects VPN routing actually
            // changed. Cosmetic edits (reordering apps, renaming, icons) keep it alive.
            if (RoutingSignature(_config) != before.Routing)
            {
                if (EngineSignature(_config) != beforeEngine || !_hasChecked)
                {
                    // The engine itself moved, or nothing is live yet → the whole picture is
                    // stale; tear it all down and re-arm the kill-switch.
                    _workingApps.Clear();
                    _assignment.Clear();
                    _proxySlot.Clear();
                    _proxyGeo.Clear();
                    VpnStatus.Clear();
                    try { await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true); } catch { }
                }
                else
                {
                    // Drop exactly what this edit invalidated, and nothing else:
                    //   · rules whose own routing changed (or that were deleted), and
                    //   · rules riding a variant that was edited, switched off or removed.
                    // Adding a variant, or editing one nothing is on, leaves every live rule
                    // exactly where it is — no reconnect, no reload, no dropped sockets.
                    var afterSlots = SlotSignatures(_config);
                    var addedSlots = new HashSet<string>(afterSlots.Keys.Where(k => !beforeSlots.ContainsKey(k)),
                                                         StringComparer.Ordinal);
                    var afterApps = AppRoutingSignatures(_config, addedSlots);
                    foreach (var kv in beforeApps)
                    {
                        if (!afterApps.TryGetValue(kv.Key, out var now) || now != kv.Value)
                        {
                            _assignment.Remove(kv.Key);
                            _workingApps.Remove(kv.Key);
                        }
                    }

                    var changedSlots = ChangedSlotIds(beforeSlots, afterSlots);
                    if (changedSlots.Count > 0)
                    {
                        foreach (var kv in _assignment.ToList())
                        {
                            ProxySlotInfo info;
                            if (!_proxySlot.TryGetValue(kv.Value, out info) || info?.Id == null) continue;
                            if (!changedSlots.Contains(info.Id)) continue;
                            _assignment.Remove(kv.Key);
                            _workingApps.Remove(kv.Key);
                        }
                        // A variant that changed has to be re-parsed before it is used again;
                        // keeping the old parse would quietly connect to the previous server.
                        foreach (var name in _proxySlot.Where(kv => kv.Value?.Id != null
                                                                 && changedSlots.Contains(kv.Value.Id))
                                                       .Select(kv => kv.Key).ToList())
                        {
                            _proxySlot.Remove(name);
                            _parsedProxy.Remove(name);
                            _proxyGeo.Remove(name);
                        }
                    }

                    // Renaming or reordering variants doesn't move anyone, but the tiles show
                    // the name and number — refresh them on the proxies that stayed live.
                    var slots = _config.VpnSlots ?? new List<VpnSlot>();
                    foreach (var info in _proxySlot.Values)
                    {
                        if (info?.Id == null) continue;
                        var slot = slots.FirstOrDefault(x => x != null && x.Id == info.Id);
                        if (slot == null) continue;
                        info.Name = slot.Name;
                        info.Number = slots.IndexOf(slot) + 1;
                    }

                    ApplyFinalConfig(CurrentAssignedProxies());
                }
            }
            else if (DirectDns.Signature(_config) != beforeDns)
            {
                // Only the direct-traffic DNS changed: the routes stay, the engine just has to
                // re-read the dns: block. (A routing change above already rebuilt it.)
                ReapplyLiveConfig("DNS для прямого трафика");
            }
            SyncDnsButton();

            SetState(UiState.Done);

            // Programs may have been added to (or removed from) a kill-switch rule, and the
            // firewall has to follow — including the case where the LAST protected program was
            // removed, which must take its block rule away with it.
            SyncFirewallSoon();

            EngineLogStore.Instance.RefreshRules(_config);   // rules may have been renamed/added
            // The engine could have been downloaded / set up from the Settings → mihomo tab.
            RefreshEngineState();
            // Rules dragged in Settings: the order is the priority — into config.yaml now.
            if (RuleOrder(_config) != beforeOrder) ApplyRuleOrder();
                }

        // The order of the VPN rules = the order of their lines in config.yaml: where two tiles
        // claim the same traffic, the one further left wins. Not part of RoutingSignature (that
        // one decides whether connections are dropped — a reorder drops nothing).
        private static string RuleOrder(AppConfig cfg) =>
            string.Join("|", (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && a.RequiresVpn).Select(a => a.Id));

        // Rewrite config.yaml in the new order right away (it used to wait for the next reload).
        // Mid-connect it is left alone: that connect writes the final config from _config itself.
        private async void ApplyRuleOrder()
        {
            try
            {
                if (_state == UiState.Checking || _reconnectingId != null) return;
                Log.Info("Порядок правил изменён — конфиг переписан.");
                if (!_hasChecked) await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                else ApplyFinalConfig(CurrentAssignedProxies());
            }
            catch (Exception ex) { Log.Error("apply-rule-order", ex); }
        }

        private static readonly SolidColorBrush TileBinHotBg = Theme.ChipDangerBgHover;
        private static readonly SolidColorBrush TileBinHotBorder = Theme.ChipDangerBorderHover;

        // The tile's bin: the rule goes with its tile (a tile IS a rule). Asked first; behind the
        // access password like every routing change. What it routed falls to «Всё остальное».
        private async void DeleteTile(AppEntry entry)
        {
            try
            {
                if (entry == null || entry.IsBuiltIn || _config.Apps.IndexOf(entry) < 0) return;
                bool ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Main_DelTileTitle", entry.Name),
                    Loc.T("Main_DelTileBody"), Loc.T("Set_BtnDelete"), Loc.T("Set_BtnCancel"),
                    Wpf.Ui.Controls.ControlAppearance.Danger).ConfigureAwait(true);
                if (!ok) return;
                if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                if (_config.Apps.IndexOf(entry) < 0) return;   // gone meanwhile
                await CommitLiveEditAsync(() =>
                {
                    _config.Apps.Remove(entry);
                    return Task.CompletedTask;
                }).ConfigureAwait(true);
                _platePick.Remove(entry.Id);
                Log.Info("Плитка «" + entry.Name + "» удалена вместе с правилом (корзина на плитке).");
                RebuildTiles();
            }
            catch (Exception ex)
            {
                Log.Error("tile-delete", ex);
                try { await OverlayDialog.MessageAsync(this, Loc.T("Set_Error"), ex.Message).ConfigureAwait(true); } catch { }
            }
        }

        // Tile gear: open this rule's editor directly. On save, persist and rebuild tiles;
        // if a routing-relevant field changed while connected, drop + re-arm like Settings does.
        private async void OpenRuleEditor(AppEntry entry)
        {
            if (entry == null) return;
            // The editor changes where the rule's traffic goes — behind the access password.
            if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
            int idx = _config.Apps.IndexOf(entry);
            if (idx < 0) return;

            var before = RoutingSignature(_config);
            var beforeGlobal = GlobalRoutingSignature(_config);
            var dlg = new EditRuleDialog(entry, _config.VpnSlots) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result == null)
            {
                // Cancelled: the amber rows have been seen — only that flag is written.
                AutoNewSeen(entry.Id);
                return;
            }

            _config.Apps[idx] = dlg.Result;
            AutoPrograms.ClearAutoNew(_config, dlg.Result.Id);   // the editor's result carries none anyway
            AppConfigStore.Save(_config);
            EndAutoNotice(dlg.Result.Id);
            EngineLogStore.Instance.RefreshRules(_config);   // so the log names the edited rule
            SyncFirewallSoon();                              // programs/kill-switch may have changed

            if (RoutingSignature(_config) != before)
            {
                if (!_hasChecked)
                {
                    // Idle: the engine is running the ARMED (idle) config. Rewrite it now so the
                    // edit takes effect immediately — no restart. This is what fixes "kill-switch
                    // turned off but the site is still blocked": the armed config still REJECTed
                    // it until re-armed. (Settings does the same for the idle case.)
                    try { await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true); } catch { }
                }
                else if (GlobalRoutingSignature(_config) != beforeGlobal)
                {
                    // Engine/variant (global) change → the whole live picture is stale; tear it
                    // all down and re-arm the kill-switch. User re-connects everything.
                    _workingApps.Clear();
                    _assignment.Clear();
                    _proxySlot.Clear();
                    _proxyGeo.Clear();
                    VpnStatus.Clear();
                    try { await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true); } catch { }
                }
                else
                {
                    // Only THIS rule's routing changed — drop just this rule (same as its ✕),
                    // leaving every other app on its live node. Its ⟳ / «Подключить» brings it
                    // back. Editing one rule no longer knocks the whole fleet offline.
                    _assignment.Remove(entry.Id);
                    _workingApps.Remove(entry.Id);
                    ApplyFinalConfig(CurrentAssignedProxies());
                }
            }

            RebuildTiles();
        }

        // Tile ✕: same as the «Отключиться» button, but for ONE tile. No confirmation —
        // immediate. Drops just this service's route and re-applies the live config so it
        // falls back to its armed state (REJECT with kill-switch, else DIRECT); every other
        // app stays on its node. The tile's ⟳ brings it back.
        // «Блокировать» switched on (▷, or picked on the plate): no node, just the block — written
        // into the config at once, and what is already open in its scope is cut.
        private void StartBlock(AppEntry entry)
        {
            if (entry == null || !entry.IsNoNodeMode) return;
            _assignment.Remove(entry.Id);
            _workingApps.Remove(entry.Id);
            SetAutoConnect(entry, true, "block");
            Log.Info("Плитка «" + entry.Name + "»: «Блокировать» включено.");
            try
            {
                ApplyFinalConfig(CurrentAssignedProxies());
                CloseConnectionsFor(new[] { entry });
            }
            catch (Exception ex) { Log.Error("start-block", ex); }
            RebuildTiles();
            StatusFoot.Text = "";
        }

        private void DisconnectOneRule(AppEntry entry)
        {
            if (entry == null || !entry.RequiresVpn) return;

            // Switched OFF first, in every state. It used to bail out before this whenever the
            // rule had no node — "already off" — but a rule that is switched on and FAILING has
            // no node either, so the ✕ silently did nothing to exactly the rule that needed it:
            // the auto-retry went on reconnecting it hourly, forever (msedge, 2026-10-10).
            SetAutoConnect(entry, false, "disconnect-one");

            // This very rule is mid-reconnect: interrupt it NOW (the ✕ is how you stop a
            // connect), and that attempt's finally restores the config and the tiles.
            if (_reconnectingId == entry.Id)
            {
                try { _checkCts?.Cancel(); } catch { }
                StatusFoot.Text = "";   // «отключено» is on the tile
                return;
            }

            // A «Блокировать» tile holds no node, but its block lives in the config all the same.
            bool connected = _assignment.ContainsKey(entry.Id) || _workingApps.Contains(entry.Id) || entry.IsNoNodeMode;
            if (_reconnectingId == entry.Id || _state == UiState.Checking || !connected)
            {
                RebuildTiles();
                if (_state == UiState.Done) UpdateDoneBanner();
                StatusFoot.Text = "";   // «отключено» is on the tile
                return;
            }

            _assignment.Remove(entry.Id);
            _workingApps.Remove(entry.Id);
            ApplyFinalConfig(CurrentAssignedProxies());
            CloseConnectionsFor(new[] { entry });   // and cut what is already open, or it isn't "stopped"

            RebuildTiles();
            if (_state == UiState.Done) UpdateDoneBanner();   // or the list keeps the stopped rule out of it
            StatusFoot.Text = "";   // «отключено» is on the tile
            _ = ResolveProxyGeosAsync();
        }

        // Fingerprint of everything that affects VPN routing (mihomo engine, VPN variants,
        // and per-app routing fields). Order-independent — reordering apps/variants or
        // changing names/icons does NOT change it, so it won't drop a live connection.
        // The global slice of the routing fingerprint: engine settings + VPN variants only
        // (no per-app fields). Used to tell a whole-fleet change (engine/variant) from a
        // change confined to one rule, so editing one rule can drop just that rule.
        // The parts of "global" that really do invalidate EVERYTHING: the engine itself moves,
        // or the port/API it is driven through changes. Nothing here can be true of one rule and
        // false of another, so there is no finer answer than "start over".
        private static string EngineSignature(AppConfig c)
        {
            if (c == null) return "";
            return string.Join("|", c.MihomoExePath, c.MihomoWorkDir, c.MihomoApiAddr,
                                    c.MihomoSecret, c.MixedPort.ToString());
        }

        // slotId -> what about that variant affects routing. Kept per slot, not as one blob:
        // adding a variant, or editing one nothing is riding, must not disturb the rules that
        // are live on the others. («зачем гасить активные подключения правил, которых я не
        // трогал» — it used to, because the whole list was one signature.)
        private static Dictionary<string, string> SlotSignatures(AppConfig c)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in (c?.VpnSlots ?? new List<VpnSlot>()))
                if (s != null && s.Id != null)
                    map[s.Id] = ((int)s.Kind) + ":" + s.Value + ":" + s.Enabled;
            return map;
        }

        // Variants that changed, were switched off, or disappeared. A variant that only APPEARED
        // is deliberately absent: nothing is riding it yet.
        private static HashSet<string> ChangedSlotIds(Dictionary<string, string> before,
                                                      Dictionary<string, string> after)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in before)
            {
                string now;
                if (!after.TryGetValue(kv.Key, out now) || now != kv.Value) changed.Add(kv.Key);
            }
            return changed;
        }

        private static string GlobalRoutingSignature(AppConfig c)
        {
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(c.MihomoExePath).Append('|').Append(c.MihomoWorkDir).Append('|')
              .Append(c.MihomoApiAddr).Append('|').Append(c.MihomoSecret).Append('|').Append(c.MixedPort).Append("||");
            foreach (var s in (c.VpnSlots ?? new List<VpnSlot>()).OrderBy(s => s.Id, StringComparer.Ordinal))
                sb.Append(s.Id).Append(':').Append((int)s.Kind).Append(':').Append(s.Value).Append(':').Append(s.Enabled).Append(';');
            return sb.ToString();
        }

        // Per-app routing fingerprint (no id prefix): the fields that decide how THIS rule is
        // routed. Two apps with the same value route identically. Shared by RoutingSignature and
        // AppRoutingSignatures so the whole-config and per-app views can never drift.
        private static void AppendAppRouting(System.Text.StringBuilder sb, AppEntry a,
                                             HashSet<string> ignoreSlots = null)
        {
            // SkipCheck belongs here even though it writes no mihomo rule: it decides whether a
            // rule is verified before being assigned, so flipping it has to re-run the connect.
            // HomeUrl too: its host is routed with the rule (MihomoConfigBuilder.RouteUrl).
            sb.Append(a.RequiresVpn).Append(':').Append(a.CheckUrl).Append('|').Append(a.HomeUrl).Append(':').Append(a.KillSwitch)
              .Append(':').Append(a.SkipCheck).Append(':').Append(a.Block).Append(':');
            // Routing depends on each program's image name and each site's domain + rule list.
            foreach (var p in (a.Programs ?? new List<RuleProgram>()))
                // Auto entries route by their exact file, manual ones by name: the flag is routing.
                sb.Append('P').Append(p.Path).Append('/').Append(p.Aumid).Append(p.Auto ? "/A" : "").Append(',');
            foreach (var st in (a.Sites ?? new List<RuleSite>()))
                sb.Append('S').Append(st.Url).Append('/').Append(st.RuleItems?.Count ?? 0).Append(',');
            foreach (var f in (a.Folders ?? new List<RuleFolder>()))
                sb.Append('F').Append(f?.Path).Append(',');
            sb.Append(':');
            foreach (var d in (a.DisabledVpnSlotIds ?? new List<string>()).OrderBy(x => x, StringComparer.Ordinal))
                if (ignoreSlots == null || !ignoreSlots.Contains(d)) sb.Append(d).Append(',');
            // NOT sorted, unlike the set above: here the order is the whole point — changing it
            // is what makes the rule move to another variant on the next connect.
            sb.Append(':');
            foreach (var o in (a.VpnSlotOrder ?? new List<string>()))
                if (ignoreSlots == null || !ignoreSlots.Contains(o)) sb.Append(o).Append(',');
            // The cards (2026-11-19): «Привязка к сетевой карте» in the rule editor pins the tile to
            // one — that has to reach the engine on save like any other routing change.
            sb.Append(':').Append(a.LockNic).Append(':');
            foreach (var n in NetCards.Clean(a.Interfaces)) sb.Append(n).Append(',');
        }

        // appId -> per-app routing fingerprint, for diffing which rules changed across a Settings
        // edit (so only the changed/removed rules are dropped, not the whole live fleet).
        // `ignoreSlots`: variant ids to leave out of the comparison. Settings passes the variants
        // that were just ADDED — each new one is written into every rule's "may not use" list
        // (a new variant joins nobody's search until assigned), which touches every rule's
        // fields without moving a single one of them.
        private static Dictionary<string, string> AppRoutingSignatures(AppConfig c,
                                                                       HashSet<string> ignoreSlots = null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in (c?.Apps ?? new List<AppEntry>()))
            {
                var sb = new System.Text.StringBuilder();
                AppendAppRouting(sb, a, ignoreSlots);
                map[a.Id] = sb.ToString();
            }
            return map;
        }

        private static string RoutingSignature(AppConfig c)
        {
            if (c == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(GlobalRoutingSignature(c)).Append("||");
            foreach (var a in (c.Apps ?? new List<AppEntry>()).OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                sb.Append(a.Id).Append(':');
                AppendAppRouting(sb, a);
                sb.Append(';');
            }
            return sb.ToString();
        }

        // ---------- Updates ----------

        private async Task CheckForUpdatesAsync()
        {
            var res = await UpdateService.CheckAsync().ConfigureAwait(true);
            _update = res;
            BtnUpdate.Visibility = (res != null && res.Any) ? Visibility.Visible : Visibility.Collapsed;
            RecalcHeight();
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_updating || _update == null || !_update.Any) return;

            // Describe exactly what will be updated, then ask yes/no.
            var lines = new List<string>();
            if (_update.AppUpdate)
                lines.Add(Loc.T("Main_UpdateLineLauncher", _update.LocalApp, _update.RemoteApp));
            if (_update.MihomoUpdate)
                lines.Add(Loc.T("Main_UpdateLineEngine", _update.LocalMihomo, _update.RemoteMihomo));
            if (_update.GeoUpdate)
                lines.Add(Loc.T("Main_UpdateLineGeo", _update.RemoteGeo));
            var body = string.Join("\n", lines)
                + (_update.AppUpdate ? Loc.T("Main_UpdateBodyTailRestart") : Loc.T("Main_UpdateBodyTail"));

            bool ok = await OverlayDialog.ConfirmAsync(this, Loc.T("Main_UpdateAvailableTitle"), body, Loc.T("Main_UpdateNow"), Loc.T("Main_Later"))
                                         .ConfigureAwait(true);
            if (!ok) return;

            await RunUpdateAsync().ConfigureAwait(true);
        }

        private async Task RunUpdateAsync()
        {
            _updating = true;
            BtnUpdate.IsEnabled = false;
            ShowUpdateProgress(Loc.T("Main_UpdateTitle"), Loc.T("Main_Preparing"));
            try
            {
                // mihomo first (in place, fast), then the launcher (relaunches).
                if (_update.MihomoUpdate)
                {
                    ShowUpdateProgress(Loc.T("Main_UpdatingEngineTitle"), Loc.T("Main_DownloadingMihomoShort"));
                    await UpdateService.UpdateMihomoAsync(
                        _config, _update.RemoteMihomo, _update.MihomoZipUrl,
                        p => { SetUpdatePercent(p); SetUpdateDetail(Loc.T("Main_DownloadingMihomoShortPct", p)); },
                        CancellationToken.None).ConfigureAwait(true);

                    // Re-arm the kill-switch with the fresh engine, refresh the caption.
                    try
                    {
                        if (await EnsureMihomoRunningAsync(CancellationToken.None).ConfigureAwait(true))
                            await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                    }
                    catch { }
                    SetTitleCaption();
                }

                if (_update.GeoUpdate)
                {
                    ShowUpdateProgress(Loc.T("Main_UpdatingGeoTitle"), Loc.T("Main_Downloading"));
                    await UpdateService.UpdateGeoAsync(
                        _update.RemoteGeo,
                        p => { SetUpdatePercent(p); SetUpdateDetail(Loc.T("Main_DownloadingGeoPct", p)); },
                        CancellationToken.None).ConfigureAwait(true);
                    _proxyGeo.Clear();
                    _ = ResolveProxyGeosAsync();
                }

                if (_update.AppUpdate)
                {
                    ShowUpdateProgress(Loc.T("Main_UpdatingLauncherTitle"), Loc.T("Main_DownloadingNewVersion"));
                    await UpdateService.StartAppUpdateAsync(
                        status => SetUpdateDetail(status),
                        p => { SetUpdatePercent(p); },
                        CancellationToken.None).ConfigureAwait(true);

                    // The staged updater now takes over (its own progress window): it waits for
                    // us to exit, stops processes, wipes Data\ + the exe, installs the archive
                    // and relaunches. We just hand off by shutting down.
                    SetUpdateDetail(Loc.T("Main_ClosingForUpdate"));
                    SetUpdatePercent(-1);
                    _allowClose = true;
                    Application.Current.Shutdown();
                    return;
                }

                // In-place update(s) done (no launcher restart needed).
                HideUpdateProgress();
                BtnUpdate.Visibility = Visibility.Collapsed;
                _update = null;
                StatusFoot.Text = Loc.T("Main_FootUpdateDone");
            }
            catch (Exception ex)
            {
                HideUpdateProgress();
                await ShowDialog(Loc.T("Main_UpdateFailTitle"),
                    Loc.T("Main_UpdateFailBody", ex.Message)).ConfigureAwait(true);
            }
            finally
            {
                _updating = false;
                BtnUpdate.IsEnabled = true;
            }
        }

        private void ShowUpdateProgress(string title, string detail)
        {
            ConnectingTitle.Text = title;
            ConnectingDetail.Text = detail;
            BtnCancel.Visibility = Visibility.Collapsed;
            // Swap the spinner for a determinate progress bar during updates.
            ConnectingRing.Visibility = Visibility.Collapsed;
            UpdateBar.IsIndeterminate = true;
            UpdateBar.Value = 0;
            UpdateBar.Visibility = Visibility.Visible;
            ConnectingOverlay.Visibility = Visibility.Visible;
            ResultRoot.Visibility = Visibility.Collapsed;
        }

        private void SetUpdateDetail(string detail)
        {
            if (Dispatcher.CheckAccess()) ConnectingDetail.Text = detail;
            else Dispatcher.BeginInvoke(new Action(() => ConnectingDetail.Text = detail));
        }

        // percent < 0 → indeterminate (a phase with no measurable progress); 0..100 → determinate.
        private void SetUpdatePercent(int percent)
        {
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply));

            void Apply()
            {
                if (percent < 0) { UpdateBar.IsIndeterminate = true; }
                else { UpdateBar.IsIndeterminate = false; UpdateBar.Value = percent; }
            }
        }

        private void HideUpdateProgress()
        {
            BtnCancel.Visibility = Visibility.Visible;
            UpdateBar.Visibility = Visibility.Collapsed;
            ConnectingRing.Visibility = Visibility.Visible;
            ConnectingTitle.Text = Loc.T("Main_ConnectingTitle");
            SetState(_hasChecked ? UiState.Done : UiState.Idle);
        }

        // ---------- Helpers ----------

        // The log button's icon turns dim red while two tiles claim the same address / program
        // (RuleConflicts) — the «Конфликт URL» tab in that window says where and who wins.
        private void UpdateConflictIcon()
        {
            try
            {
                EngineLogStore.ConfigSource = () => _config;
                RuleConflicts.RouteOf = RouteKey;
                // Only the ones that change where traffic goes: two tiles on the same route, or a
                // narrower address taking its part from a wider one, stay listed but grey.
                int n = RuleConflicts.Find(_config).Count(g => g.Real);
                IcoLog.Foreground = n > 0 ? Theme.ConflictIcon : Theme.TextSecondary;
                BtnLog.ToolTip = n > 0 ? Loc.T("Log_TipConflicts", n) : Loc.T("Log_Tip");
            }
            catch (Exception ex) { Log.Error("conflict-icon", ex); }
        }

        // Where a tile's traffic goes right now, as a key: the VPN it is on (or DIRECT / REJECT
        // when it is on none), its cards and how hard they are held. Equal keys = whichever of
        // two tiles wins an address, it goes the same way. Mirrors MihomoConfigBuilder: a tile
        // without a node writes REJECT under its kill-switch, DIRECT otherwise; cards re-point either.
        private string RouteKey(AppEntry a)
        {
            if (a == null) return null;
            string target;
            if (a.IsBlocking) target = "REJECT";
            else if (a.RequiresVpn)
            {
                string proxy;
                target = _assignment.TryGetValue(a.Id ?? "", out proxy) && !string.IsNullOrEmpty(proxy) ? "P:" + proxy
                       : a.KillSwitch ? "REJECT" : "DIRECT";
            }
            else target = "DIRECT";
            if (target == "REJECT") return target;
            return target + "|nic=" + (MihomoConfigBuilder.NicKey(a) ?? "") + "|lock=" + a.LockNic + "|ks=" + a.KillSwitch;
        }

        // «Весь трафик» while it is the only tile (it then really is all of it), «Всё остальное»
        // as soon as any other rule exists.
        private string AllTrafficName()
        {
            bool others = (_config?.Apps ?? new List<AppEntry>()).Any(a => a != null && !a.IsAllTraffic);
            return Loc.T(others ? "AllTraffic_Name" : "AllTraffic_NameAlone");
        }

        // The «?» on «Всё остальное». The main window is only as tall as its tiles,
        // so the text scrolls inside the card instead of running off the bottom.
        private Task ShowBuiltInHelpAsync(AppEntry entry)
        {
            if (entry == null) return Task.FromResult(true);
            var text = new TextBlock
            {
                Text = Loc.T("AllTraffic_Help"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.OverlayBody,
                Margin = new Thickness(0, 0, 6, 14),
            };
            var scroll = new ScrollViewer
            {
                Content = text,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(120, ActualHeight - 170),
            };
            double width = Math.Max(300, Math.Min(520, ActualWidth - 40));
            return OverlayDialog.ConfirmContentAsync(this,
                AllTrafficName(), scroll, "OK", null, width);
        }

        // OverlayDialog draws INSIDE the window and completes when a button is clicked — on a
        // hidden (autostarted) window that would wait forever. Log the message instead; the
        // user sees the real state as soon as they open the launcher from the tray.
        private Task ShowDialog(string title, string text)
        {
            if (_hidden)
            {
                try { Log.Info("(скрыт) " + title + ": " + text); } catch { }
                return Task.FromResult(true);
            }
            return OverlayDialog.MessageAsync(this, title, text);
        }
    }
}
