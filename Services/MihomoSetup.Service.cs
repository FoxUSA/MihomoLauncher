using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace MihomoLauncher.Services
{
    // The engine as a Windows service (Data\service.exe, source build\launchers\service.cs).
    //
    // Why: a scheduled task started on demand can sit «в очереди» for minutes — Windows holds
    // tasks back on an almost empty battery and right after an update restart — and the engine
    // never came up (PAPA2, 2026-11-08). A service starts when told to.
    //
    // The service is a thin SYSTEM host with a fixed command line (engine, work dir, launcher —
    // written by the elevated install). The launcher, as the ordinary user, may start / stop it
    // and send it custom commands (its security descriptor says so); nothing it sends carries a
    // path. Installs from before the service keep working through the old tasks until the next
    // engine (re)install moves them over.
    public static partial class MihomoSetup
    {
        public const string ServiceName = "MihomoLauncherEngine";
        public const string ServiceExeName = "service.exe";

        // Custom commands — mirrored in build\launchers\service.cs.
        private const int SvcCmdStartEngine = 130;
        private const int SvcCmdKillEngines = 132;
        private const int SvcCmdFirewallSync = 133;
        private const int SvcCmdKillEnginesAnyPath = 134;

        // Start/stop/query/control for Authenticated Users; full control for SYSTEM and admins.
        private const string ServiceSddl =
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;AU)";

        // Data\service.exe in a release layout; next to the exe in a flat (Debug) one.
        public static string ServiceExePath()
        {
            var inData = Path.Combine(VersionInfo.InstallRoot, "Data", ServiceExeName);
            if (File.Exists(inData)) return inData;
            var flat = Path.Combine(VersionInfo.InstallRoot, ServiceExeName);
            return File.Exists(flat) ? flat : inData;
        }

        public static bool IsServiceInstalled()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    var _ = sc.Status;   // throws when there is no such service
                    return true;
                }
            }
            catch { return false; }
        }

        // The service's command line, from the registry (readable without admin).
        private static string ServiceImagePath()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceName))
                    return Convert.ToString(k?.GetValue("ImagePath", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? "";
            }
            catch { return ""; }
        }

        private static string ImageArg(string image, string name)
        {
            var m = Regex.Match(image ?? "", "--" + name + "\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string ImageExe(string image)
        {
            var m = Regex.Match(image ?? "", "^\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        // Does the installed service point at THIS install (service.exe, engine, work dir,
        // launcher)? A moved folder leaves it pointing at nothing; only a re-install cures that.
        public static bool ServiceMatchesConfig(AppConfig cfg, out string reason)
        {
            reason = "";
            var image = ServiceImagePath();
            if (image.Length == 0) return true;   // unreadable → don't nag

            var checks = new[]
            {
                new[] { "service.exe", ImageExe(image), ServiceExePath() },
                new[] { "exe", ImageArg(image, "engine"), (cfg?.MihomoExePath ?? "").Trim() },
                new[] { "workdir", ImageArg(image, "workdir"), AppConfigStore.ResolveMihomoWorkDir(cfg) },
                new[] { "launcher", ImageArg(image, "launcher"), LauncherExePath() },
            };
            foreach (var c in checks)
            {
                if (c[2].Length == 0 || SamePath(c[1], c[2])) continue;
                if (c[0] == "launcher" && VersionInfo.SameLauncher(c[1], c[2])) continue;   // old exe name, same folder
                reason = c[0] + " службы=" + c[1] + " ожидается=" + c[2];
                return false;
            }
            return true;
        }

        // On the launcher's exit, after the engine and the firewall release: stop the service now
        // instead of letting it idle out. Until it does, Data\service.exe stays locked and copying
        // a new build over the install said «доступ запрещён» for a while (2026-11-09).
        public static void StopServiceOnExit()
        {
            if (IsServiceInstalled()) StopService();   // ShuttingDown is set: no admin prompt on the way out
        }

        public static bool ServiceRunning()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                    return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        public static string ServiceState()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                    return "служба: состояние=" + sc.Status;
            }
            catch (Exception ex) { return "служба: состояние прочитать не удалось (" + ex.Message + ")"; }
        }

        // Start the service if needed, then send it a command. The service stops itself when it
        // has been idle a little while, so a command can land just as it is going down — one
        // retry covers that.
        private static bool SendServiceCommand(int command, string what)
        {
            string error = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var sc = new ServiceController(ServiceName))
                    {
                        if (!EnsureNotHung(sc)) { error = "служба зависла при остановке"; break; }
                        if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                        sc.ExecuteCommand(command);
                        return true;
                    }
                }
                catch (Exception ex) { error = ex.InnerException?.Message ?? ex.Message; }
            }
            try { Log.Info("Служба движка: " + what + " не удалось — " + error); } catch { }
            return false;
        }

        // Stop the service — and with it the engine (OnStop kills it). Waits, so an updater can
        // replace Data\ right after. rescueHung=false on the launcher's way out: no admin prompt
        // while closing; the next start (or the updater) deals with a hung one.
        private static void StopService(bool rescueHung = true)
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Stopped
                        && sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                    if (!EnsureNotHung(sc, rescueHung))
                    {
                        Log.Info("Служба движка: остановить не удалось — служба зависла при остановке.");
                        return;
                    }
                }
                // An engine the service did not start — the old task's, still running after the
                // silent move onto the service — survives the service stopping. Stopping means
                // stopping it too.
                var engine = ImageArg(ServiceImagePath(), "engine");
                string name = engine.Length > 0 ? Path.GetFileNameWithoutExtension(engine) : "";
                if (name.Length > 0 && !EngineGone(name))
                {
                    SendServiceCommand(SvcCmdKillEngines, "остановка движка");
                    WaitEngineGone(name, 5000);
                }
            }
            catch (Exception ex)
            {
                try { Log.Info("Служба движка: остановить не удалось — " + (ex.InnerException?.Message ?? ex.Message)); } catch { }
            }
        }

        // ---- A service hung in «StopPending» ----
        //
        // It answers nothing, cannot be started again and keeps Data\service.exe locked; only
        // killing its process frees it. 2.2.8's service.exe did exactly that on a share of its
        // stops (a deadlock, reproduced: 2 of 20; fixed — 60 of 60 clean), and the machines it
        // reached keep such a process until a reboot — or until something kills it. The process
        // is SYSTEM, so that takes Windows' admin prompt.
        //
        // Once per launcher run, never on the way out. After that the hung service is not waited
        // on again — every call failing at once, instead of each costing ten seconds, is what
        // keeps the window responsive and lets the launcher close («не хочет запускаться»,
        // «крестик не работает»: every one of those calls waited, 2026-11-09).
        private static bool _rescueTried;

        // Set by App.OnExit: from then on nothing here asks for admin rights — no prompt while the
        // launcher is closing, whichever path (engine stop, firewall release) gets here.
        public static volatile bool ShuttingDown;

        // True when the service is NOT stuck stopping (Stopped / Running / starting) — waiting
        // briefly for a stop in progress, rescuing a hung one if allowed.
        private static bool EnsureNotHung(ServiceController sc, bool rescue = true)
        {
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.StopPending) return true;
            if (WaitStopped(sc, _rescueTried ? 0 : 10)) return true;
            if (!rescue || _rescueTried || ShuttingDown) return false;

            _rescueTried = true;
            Log.Info("Служба движка зависла при остановке — завершаю её процесс (запрос прав администратора).");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c taskkill /F /FI \"SERVICES eq " + ServiceName + "\"",
                    UseShellExecute = true,   // runas
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi)) { p?.WaitForExit(15000); }
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                Log.Info("Служба движка: завершить зависший процесс не дали (отказ в запросе прав).");
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("Служба движка: завершить зависший процесс не удалось — " + ex.Message);
                return false;
            }
            bool ok = WaitStopped(sc, 10);
            Log.Info("Служба движка после завершения процесса: " + (ok ? "остановлена." : "всё ещё не остановлена."));
            return ok;
        }

        private static bool WaitStopped(ServiceController sc, int seconds)
        {
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(seconds));
                return true;
            }
            catch (System.ServiceProcess.TimeoutException) { return false; }
        }

        // ---- Moving an existing install onto the service, with no action from the user ----
        //
        // An update can't register a service (that needs admin) — but installs from before the
        // service already HAVE something that runs our exe as SYSTEM: the firewall task, which
        // starts the launcher exe --firewall-sync. After the update that is the NEW exe, and in
        // that mode it registers the service itself (MigrateToServiceAsSystem). No UAC, and the
        // engine keeps running: it is left alone until the launcher next restarts it.

        // A pre-service install the firewall task can move: the task is there and runs THIS exe,
        // and service.exe came with the update.
        public static bool CanMigrateViaFirewallTask()
        {
            if (IsServiceInstalled() || !File.Exists(ServiceExePath())) return false;
            string exe, args;
            return TaskCommand(FirewallTaskName, out exe, out args) && VersionInfo.SameLauncher(exe, LauncherExePath());
        }

        // Asks the firewall task to run (see above). Its own schtasks call: RunFirewallTask would
        // go to the service once it exists.
        public static bool RunLegacyFirewallTask()
        {
            string output;
            var code = RunSchtasks("/Run /TN \"" + FirewallTaskName + "\"", out output);
            return code == 0;
        }

        // Runs as SYSTEM (--firewall-sync). Registers the service from what the old engine task
        // runs — the same engine and work dir, so the move changes HOW it starts, nothing else —
        // then removes the old tasks. A no-op when the service exists or service.exe is missing.
        public static void MigrateToServiceAsSystem()
        {
            if (IsServiceInstalled()) return;
            var svcExe = ServiceExePath();
            if (!File.Exists(svcExe)) return;

            string engine = "", workDir = "";
            string taskExe, taskArgs;
            if (InstalledTaskCommand(out taskExe, out taskArgs))
            {
                engine = taskExe;
                var m = Regex.Match(taskArgs, "-d\\s+\"([^\"]+)\"");
                if (m.Success) workDir = m.Groups[1].Value;
            }
            if (engine.Length == 0 || workDir.Length == 0)
            {
                var cfg = AppConfigStore.Load();
                if (engine.Length == 0) engine = (cfg?.MihomoExePath ?? "").Trim();
                if (workDir.Length == 0) workDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
            }
            if (engine.Length == 0) return;

            var binPath = "\"" + svcExe + "\" --engine \"" + engine + "\" --workdir \"" + workDir
                        + "\" --launcher \"" + LauncherExePath() + "\"";
            string error;
            if (!NativeService.Create(ServiceName, "Mihomo Launcher engine",
                    "Runs the mihomo engine for Mihomo Launcher (byfox.dev).", binPath, ServiceSddl, out error))
            {
                Log.Info("Перевод движка на службу не удался: " + error);
                return;
            }

            // The old engine task goes too: its running engine (if any) is not touched — the
            // service kills it the first time it starts its own. The firewall task is the one
            // running us right now; deleting it does not stop this process.
            DeleteTaskQuietly(TaskName);
            DeleteTaskQuietly(StopTaskName);
            DeleteTaskQuietly(FirewallTaskName);
            Log.Info("Движок переведён на службу " + ServiceName + " (без запроса прав, через задачу брандмауэра).");
        }

        // Through the Task Scheduler COM API, not schtasks.exe — spawning tools from our exe is
        // one of the things Defender's ML holds against it.
        private static void DeleteTaskQuietly(string name)
        {
            object svc = null, folder = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return;
                svc = Activator.CreateInstance(t);
                t.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, svc,
                    new object[] { Type.Missing, Type.Missing, Type.Missing, Type.Missing });
                folder = t.InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, svc, new object[] { "\\" });
                folder.GetType().InvokeMember("DeleteTask", System.Reflection.BindingFlags.InvokeMethod, null, folder,
                    new object[] { name, 0 });
            }
            catch { }   // not there — fine
            finally
            {
                try { if (folder != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(folder); } catch { }
                try { if (svc != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); } catch { }
            }
        }

        // What runs the firewall sync, for the log.
        public static string FirewallHelperName()
            => IsServiceInstalled() ? "служба " + ServiceName : "задача " + FirewallTaskName;

        // The pre-service install's engine task is sitting «в очереди» — Windows has not run it.
        public static bool LegacyTaskQueued()
        {
            if (IsServiceInstalled()) return false;
            return TaskState().Contains("состояние=в очереди");
        }

        // Lines of the elevated setup batch that register the service. %SVC_OK% is 1 afterwards
        // if it worked; the caller falls back to the old tasks otherwise.
        private static string ServiceInstallBatch(string engineExe, string workDir)
        {
            var svcExe = ServiceExePath();
            if (!File.Exists(svcExe)) return "set SVC_OK=0\r\n";

            // sc wants the inner quotes escaped as \" inside its quoted binPath.
            var binPath = "\\\"" + svcExe + "\\\" --engine \\\"" + engineExe + "\\\" --workdir \\\""
                        + workDir + "\\\" --launcher \\\"" + LauncherExePath() + "\\\"";
            return
                "sc stop " + ServiceName + " >nul 2>&1\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                // A service hung in StopPending (2.2.8) only goes this way.
                "taskkill /F /FI \"SERVICES eq " + ServiceName + "\" >nul 2>&1\r\n" +
                "sc delete " + ServiceName + " >nul 2>&1\r\n" +
                "ping -n 2 127.0.0.1 >nul\r\n" +
                "sc create " + ServiceName + " binPath= \"" + binPath + "\" start= demand obj= LocalSystem DisplayName= \"Mihomo Launcher engine\" >nul\r\n" +
                "if errorlevel 1 (set SVC_OK=0) else (set SVC_OK=1)\r\n" +
                "if \"%SVC_OK%\"==\"1\" sc sdset " + ServiceName + " " + ServiceSddl + " >nul\r\n" +
                "if \"%SVC_OK%\"==\"1\" sc description " + ServiceName + " \"Runs the mihomo engine for Mihomo Launcher (byfox.dev).\" >nul\r\n";
        }
    }

    // Registering a service in-process (advapi32), for the SYSTEM-side migration — the same
    // thing `sc create` + `sc description` + `sc sdset` do in the elevated setup batch.
    internal static class NativeService
    {
        private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
        private const uint SERVICE_DEMAND_START = 3;
        private const uint SERVICE_ERROR_NORMAL = 1;
        private const uint SERVICE_CONFIG_DESCRIPTION = 1;
        private const uint DACL_SECURITY_INFORMATION = 4;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SERVICE_DESCRIPTION { public string lpDescription; }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateService(IntPtr scm, string name, string displayName, uint access, uint type,
            uint start, uint errorControl, string binPath, string loadOrderGroup, IntPtr tagId, string dependencies,
            string account, string password);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_DESCRIPTION info);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
            out IntPtr sd, out uint size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceObjectSecurity(IntPtr service, uint info, IntPtr sd);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static bool Create(string name, string displayName, string description, string binPath,
            string sddl, out string error)
        {
            error = "";
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, SC_MANAGER_CREATE_SERVICE);
                if (scm == IntPtr.Zero) { error = "OpenSCManager: " + new System.ComponentModel.Win32Exception().Message; return false; }

                svc = CreateService(scm, name, displayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL, binPath, null, IntPtr.Zero, null, null, null);
                if (svc == IntPtr.Zero) { error = "CreateService: " + new System.ComponentModel.Win32Exception().Message; return false; }

                var desc = new SERVICE_DESCRIPTION { lpDescription = description };
                ChangeServiceConfig2(svc, SERVICE_CONFIG_DESCRIPTION, ref desc);

                // Without this the ordinary user could not start or command it — the whole point.
                IntPtr sd; uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out sd, out size))
                { error = "SDDL: " + new System.ComponentModel.Win32Exception().Message; return false; }
                try
                {
                    if (!SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd))
                    { error = "SetServiceObjectSecurity: " + new System.ComponentModel.Win32Exception().Message; return false; }
                }
                finally { LocalFree(sd); }
                return true;
            }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }
    }
}
