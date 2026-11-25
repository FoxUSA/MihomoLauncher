using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using MihomoLauncher.Services;

namespace MihomoLauncher
{
    public partial class App : Application
    {
        // Release keeps ONLY the launcher exe («Mihomo Launcher.exe») in the root and puts every dependency DLL
        // in Data\. The CLR won't probe there on its own and there's no .config, so we
        // resolve app assemblies from Data\ ourselves. Registered in the static ctor so
        // it's wired up before InitializeComponent loads WPF-UI / MaxMind. (Debug runs
        // flat — Data\ won't exist, the handler returns null, GAC/local load as usual.)
        private static readonly string DataDir =
            System.IO.Path.Combine(AppContext.BaseDirectory, "Data");

        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var simpleName = new System.Reflection.AssemblyName(args.Name).Name;
                    var dll = System.IO.Path.Combine(DataDir, simpleName + ".dll");
                    if (System.IO.File.Exists(dll))
                    {
                        try { return System.Reflection.Assembly.LoadFrom(dll); }
                        catch
                        {
                            // LoadFrom throws on a "downloaded from internet" (Mark-of-the-Web)
                            // file when there's no app.config allowing remote sources — which
                            // happens to every DLL extracted from a downloaded zip. Loading from
                            // raw bytes ignores zone evidence, so the mark can't block it.
                            return System.Reflection.Assembly.Load(System.IO.File.ReadAllBytes(dll));
                        }
                    }
                }
                catch { }
                return null;
            };

            // Don't die silently: a crash on a non-UI thread otherwise kills the process with
            // no trace. Log everything to DataUser\logs\crash.log (next to the exe) so a failure
            // on the user's PC leaves a readable stack we can act on — and survives an update.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Crash("AppDomain", e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Crash("UnobservedTask", e.Exception);
                e.SetObserved();
            };
        }

        // Per-user identifiers — Local\ prefix keeps them out of other sessions.
        private const string MutexName = "Local\\MihomoLauncher.SingleInstance.Mutex";
        private const string ShowEventName = "Local\\MihomoLauncher.SingleInstance.ShowEvent";

        private Mutex _singleInstanceMutex;
        private EventWaitHandle _showEvent;
        private Thread _showEventListener;
        private volatile bool _shuttingDown;
        private bool _ownsInstance;

        public static event Action ShowRequested;

        private const int ASFW_ANY = -1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            // Every window fits the screen it opens on (UiHelpers.KeepOnScreen).
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, a) => { if (ReferenceEquals(a.OriginalSource, s)) UiHelpers.KeepOnScreen((Window)s); }));
            // ...and keeps fitting it whenever its size changes while open — any window, any screen
            // (2026-11-24: a window that grew after showing, its data arriving, went past the bottom).
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((s, a) => { if (ReferenceEquals(a.OriginalSource, s)) UiHelpers.KeepOnScreenSoon((Window)s); }));

            // Light or dark (2026-11-24): the saved choice, else as Windows. FIRST, on the UI thread
            // and before any window: it fills the application's colour resources, creates the live
            // brushes code-behind uses, and sets the library's theme (backdrop None — no Mica)
            // and the one accent. Services/Theme.Apply.
            try
            {
                string savedTheme = "";
                try { savedTheme = AppConfigStore.Load()?.UiTheme ?? ""; } catch { }
                Theme.Init(savedTheme);
            }
            catch (Exception ex) { try { Log.Error("theme-init", ex); } catch { } }
            // A new window's frame (DWM dark mode) in the current theme.
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, a) => { if (ReferenceEquals(a.OriginalSource, s)) Theme.ApplyFrame((Window)s); }));

            // Localization: pick the UI language (a saved choice wins, else the system culture)
            // and merge its string dictionary BEFORE any window is built, so every XAML
            // {DynamicResource} resolves in the right language from the first frame.
            try
            {
                string savedLang = "";
                try { savedLang = AppConfigStore.Load()?.Language ?? ""; } catch { }
                Loc.Apply(Loc.Resolve(savedLang));
            }
            catch (Exception ex) { try { Log.Error("loc-init", ex); } catch { } }

            // Firewall-helper mode: started by the MihomoLauncher_fw scheduled task as SYSTEM,
            // because creating a Windows Firewall rule needs administrator rights and the
            // launcher itself must NOT have them. It applies the plan the launcher wrote to
            // DataUser\firewall.state and exits — no window, no tray icon, no single-instance
            // gate (it has to be able to run while the real launcher is running), no engine.
            if (e.Args != null && e.Args.Length >= 1 &&
                (string.Equals(e.Args[0], Services.MihomoSetup.FirewallSyncArg, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.Args[0], "--firewall-clear", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (string.Equals(e.Args[0], "--firewall-clear", StringComparison.OrdinalIgnoreCase))
                        FirewallGuard.ClearAll();
                    else
                    {
                        // Running as SYSTEM through the old firewall task: the moment to move
                        // a pre-service install onto the engine service, no UAC needed.
                        try { Services.MihomoSetup.MigrateToServiceAsSystem(); }
                        catch (Exception ex) { Log.Error("migrate-to-service", ex); }
                        // Network-card priority from Settings → «Сеть», if a plan is waiting
                        // (applied once, then the plan is gone). Same SYSTEM helper, no UAC.
                        try { NicMetrics.ApplyFromPlan(); }
                        catch (Exception ex) { Log.Error("nic-metric-apply", ex); }
                        FirewallGuard.ApplyFromState();
                    }
                }
                catch (Exception ex) { Log.Crash("FirewallSync", ex); }
                Shutdown();
                return;
            }

            // Update-applier mode: this instance was launched (from the staging folder, by the
            // previous launcher) with --apply-update. It just shows the updater progress window
            // and swaps the new files into the install root — none of the normal startup runs.
            if (UpdateApplier.IsUpdaterMode(e.Args))
            {
                try
                {
                    var opt = UpdateApplier.Parse(e.Args);
                    new Views.UpdaterWindow(opt).Show();
                }
                catch (Exception ex)
                {
                    Log.Crash("Updater", ex);
                    Shutdown();
                }
                return;
            }

            // Uninstaller mode: launched by uninstall.exe with --uninstall. Shows the uninstall
            // progress window (stop/remove with a checklist) instead of the old cmd console.
            if (e.Args != null && e.Args.Length >= 1 &&
                string.Equals(e.Args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                try { new Views.UninstallWindow().Show(); }
                catch (Exception ex) { Log.Crash("Uninstaller", ex); Shutdown(); }
                return;
            }

            // Never let a stray UI-thread exception kill the whole launcher.
            DispatcherUnhandledException += (s, args) =>
            {
                // WPF's WindowChrome throws this when DWM composition flips (RDP connect /
                // disconnect, display-driver reset). Harmless — but a MessageBox popped while the
                // window sits hidden in the tray blocks it from being shown again. Log only.
                if (args.Exception is System.Runtime.InteropServices.COMException cex && (uint)cex.HResult == 0x80263001)
                {
                    Log.Info("DWM: композиция рабочего стола переключилась (RDP/видеодрайвер) — ошибка WindowChrome пропущена.");
                    args.Handled = true;
                    return;
                }
                Log.Crash("Dispatcher", args.Exception);
                try
                {
                    MessageBox.Show(Loc.T("Svc_ErrorButContinue", args.Exception.Message),
                        "Mihomo Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                args.Handled = true;
            };

            // Single-instance gate. If another copy already owns the mutex,
            // signal it to surface its window and exit silently.
            bool createdNew;
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out createdNew);
            if (!createdNew)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                    {
                        // Windows only lets the process the user just launched take the
                        // foreground — and that's us, about to exit. Hand the right over,
                        // otherwise the running copy's window stays buried behind others.
                        AllowSetForegroundWindow(ASFW_ANY);
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch { }
                Shutdown();
                return;
            }

            _ownsInstance = true;
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showEventListener = new Thread(ShowEventLoop) { IsBackground = true, Name = "MihomoLauncher-ShowEventListener" };
            _showEventListener.Start();

            IconBundle.EnsureExtracted();

            // Give the install folder + Data\ + DataUser\ their icons and (re)create the
            // help/uninstall shortcuts in the root. Idempotent; self-heals after an update.
            try { FolderIcons.Apply(); } catch { }

            // Clean up the update staging folder left in %TEMP% by a just-finished update.
            try
            {
                var stage = UpdateService.StageDir;
                if (System.IO.Directory.Exists(stage)) System.IO.Directory.Delete(stage, true);
            }
            catch { }
            // …and the old Data.old / <exe>.old an update set aside but could not
            // remove right away (UpdateApplier.SwapIn).
            try { UpdateApplier.CleanAside(AppContext.BaseDirectory.TrimEnd('\\')); } catch { }
            HideLegacyForwarder();

            // If autostart is on, keep it pointed at the current exe (handles a moved/
            // updated launcher) and clean out any old MihomoLauncher autostart entries.
            try { Autostart.SyncIfEnabled(); } catch { }

            // Esc closes any of the launcher's windows except the main one and the updater
            // (2026-11-14: «закрывать любое окно настроек эскейпом»). Bubbling KeyDown, unhandled
            // only: an in-window dialog (OverlayDialog) or a search box that uses Esc itself marks
            // the key handled first, so Esc there still does just that one thing. Closing without
            // DialogResult = true is a cancel to every caller (they all check == true).
            try
            {
                EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent,
                    new System.Windows.Input.KeyEventHandler((s, ke) =>
                    {
                        if (ke.Handled || ke.Key != System.Windows.Input.Key.Escape) return;
                        var w = s as Window;
                        if (w == null || w is Views.MainWindow || w is Views.UpdaterWindow) return;
                        ke.Handled = true;
                        w.Close();
                    }));
            }
            catch (Exception ex) { Log.Error("esc-close", ex); }

            // Started by the HKCU\Run entry at logon (it passes --autostart): come up hidden
            // in the tray and connect on our own, instead of popping a window in mom's face.
            bool silent = Autostart.IsSilentLaunch(e.Args);

            try { Log.Info("Запуск · " + VersionInfo.TitleCaption + (silent ? "  (автозапуск)" : "")); } catch { }

            // Access password + data carried here from another place (another copy of the launcher,
            // another PC or Windows user): nothing runs — no engine, no connect, no window — until
            // the password is given. Given → the data is bound to this place and works as usual.
            if (!UnlockMovedData()) { Shutdown(); return; }

            // Create the main window ourselves (no StartupUri — see App.xaml).
            try
            {
                var win = new Views.MainWindow(silent);
                MainWindow = win;
                if (silent) win.StartHiddenInTray();
                else win.Show();
            }
            catch (Exception ex)
            {
                Log.Crash("MainWindow", ex);
                Shutdown();
            }
        }

        // MihomoLauncher.exe beside «Mihomo Launcher.exe» is the old-name forwarder an update
        // brings (VersionInfo.ExeName). It stays — old shortcuts, pins and the service's
        // --launcher still name it — but hidden, so the folder shows one exe to start.
        private static void HideLegacyForwarder()
        {
            try
            {
                var root = AppContext.BaseDirectory.TrimEnd('\\');
                var me = MihomoSetup.LauncherExePath();
                if (!string.Equals(System.IO.Path.GetFileName(me), VersionInfo.ExeName, StringComparison.OrdinalIgnoreCase)) return;
                var legacy = System.IO.Path.Combine(root, VersionInfo.LegacyExeName);
                if (!System.IO.File.Exists(legacy)) return;
                var attrs = System.IO.File.GetAttributes(legacy);
                if ((attrs & System.IO.FileAttributes.Hidden) == 0)
                    System.IO.File.SetAttributes(legacy, attrs | System.IO.FileAttributes.Hidden);
            }
            catch { }
        }

        private bool UnlockMovedData()
        {
            AppConfig cfg;
            try { cfg = AppConfigStore.Load(); }
            catch (Exception ex) { Log.Error("unlock-load", ex); return true; }
            if (AccessLock.BoundHere(cfg)) return true;

            Log.Info("Пароль доступа: данные перенесены из другого места — нужен пароль.");
            var mode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;   // closing the prompt must not end the app by itself
            try
            {
                var w = new Views.UnlockWindow(cfg, Loc.T("Lock_MovedReason"), Loc.T("Lock_BtnQuit"));
                if (w.ShowDialog() != true || !w.Opened)
                {
                    Log.Info("Пароль доступа: не введён — утилита закрыта, данные не тронуты.");
                    return false;
                }
                AccessLock.BindHere(cfg);
                AppConfigStore.Save(cfg);
                Log.Info("Пароль доступа: введён, данные привязаны к этому месту.");
                return true;
            }
            catch (Exception ex) { Log.Error("unlock-moved", ex); return false; }
            finally { ShutdownMode = mode; }
        }

        private void ShowEventLoop()
        {
            while (!_shuttingDown)
            {
                try
                {
                    if (!_showEvent.WaitOne(500)) continue;
                    if (_shuttingDown) break;
                    Dispatcher.BeginInvoke(new Action(() => ShowRequested?.Invoke()));
                }
                catch { break; }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _shuttingDown = true;
            MihomoSetup.ShuttingDown = true;
            // Stop the VPN engine together with the launcher (only the primary instance).
            // Not a bare kill: EngineShutdown drops TUN through the API and flushes the DNS
            // cache first, so the machine doesn't lose the network for a few seconds after us.
            if (_ownsInstance) { try { EngineShutdown.Stop(); } catch { } }
            // ...and take the firewall kill-switch down with it. Those rules only mean anything
            // while there is a tunnel to force the traffic into; left behind they are a silent
            // "this program has no internet" that outlives the launcher — see ReleaseOnExit.
            if (_ownsInstance) { try { FirewallGuard.ReleaseOnExit(); } catch { } }
            // The firewall release above runs through the engine service and starts it again;
            // stop it right away so nothing of ours is left running (or locked) after we exit.
            if (_ownsInstance) { try { MihomoSetup.StopServiceOnExit(); } catch { } }
            try { VpnMonitor.StopProcessWatchers(); } catch { }
            try { _showEvent?.Set(); } catch { }
            try { _showEvent?.Dispose(); } catch { }
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex?.Dispose(); } catch { }
            base.OnExit(e);
        }
    }
}
