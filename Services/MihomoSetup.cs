using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MihomoLauncher.Services
{
    // One-time, elevated setup of mihomo as a Scheduled Task that runs at logon
    // with highest privileges. After this (single UAC prompt), mihomo starts
    // elevated automatically on every logon and the non-admin launcher controls
    // it purely via the RESTful API.
    public static class MihomoSetup
    {
        public const string TaskName = "MihomoLauncher_mihomo";

        // Second, on-demand task whose only job is to kill the engine. It exists because the
        // engine runs as SYSTEM and the launcher can't touch it — see ForceStopEngine.
        public const string StopTaskName = "MihomoLauncher_stop";

        // Third, on-demand task: applies the firewall kill-switch. Creating a Windows Firewall
        // rule requires administrator rights, and the launcher deliberately runs as the ordinary
        // logged-in user (it has to, or Store apps are invisible to it). So the rules are applied
        // by this task — our own exe, started by the scheduler as SYSTEM with --firewall-sync,
        // which reads the plan the launcher wrote and does nothing else. Registered by the same
        // single elevated install as the other two, so the user still sees exactly one UAC prompt.
        public const string FirewallTaskName = "MihomoLauncher_fw";
        public const string FirewallSyncArg = "--firewall-sync";

        // Generate the external-controller secret once; returns true if it changed.
        public static bool EnsureSecret(AppConfig cfg)
        {
            if (cfg == null) return false;
            if (!string.IsNullOrEmpty(cfg.MihomoSecret)) return false;
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            cfg.MihomoSecret = Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
            return true;
        }

        private static System.Text.Encoding OemEncoding()
        {
            try { return System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return System.Text.Encoding.Default; }
        }

        // Run schtasks.exe and capture what it said. Returns exit code -1 if it couldn't be
        // started/timed out. Output is read BEFORE WaitForExit so a chatty child can't
        // deadlock on a full pipe.
        private static int RunSchtasks(string args, out string output, bool unicode = false)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // /XML comes out UTF-16; everything else is written in the console's OEM
                // codepage (cp866 on a Russian Windows). Without saying so, .NET decodes it
                // with the ANSI default and localized words arrive as mojibake in the log.
                psi.StandardOutputEncoding = unicode ? System.Text.Encoding.Unicode : OemEncoding();
                psi.StandardErrorEncoding = OemEncoding();

                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }   // don't leave a stuck schtasks behind
                        return -1;
                    }
                    output = (so ?? "").Trim();
                    if (output.Length == 0) output = (se ?? "").Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        // Start mihomo via its scheduled task (runs elevated, no UAC — running your
        // own task is allowed). Used by the launcher to bring mihomo up on demand.
        // Returns true if the scheduler accepted the /Run (which is NOT yet a promise that
        // mihomo's API is up — the caller polls for that).
        public static bool RunTask()
        {
            string output;
            var code = RunSchtasks("/Run /TN \"" + TaskName + "\"", out output);
            if (code != 0)
                try { Log.Info("schtasks /Run → код " + code + (output.Length > 0 ? ": " + output : "")); } catch { }
            return code == 0;
        }

        // Status + last exit code of the task, for the log when the engine won't come up.
        // Read through the Task Scheduler COM API (late-bound, no COM reference) rather than
        // schtasks output: the fields come back as numbers, so there's no localized text and
        // no console-codepage guessing — a GUI process gets schtasks' text in the OEM codepage
        // and it lands in the log as mojibake.
        public static string TaskState()
        {
            object svc = null, folder = null, task = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return "задача: планировщик недоступен";
                svc = Activator.CreateInstance(t);
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc,
                    new object[] { Type.Missing, Type.Missing, Type.Missing, Type.Missing });
                folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { "\\" });
                task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder,
                    new object[] { TaskName });

                var state = Convert.ToInt32(task.GetType().InvokeMember("State", BindingFlags.GetProperty, null, task, null));
                var last = Convert.ToInt64(task.GetType().InvokeMember("LastTaskResult", BindingFlags.GetProperty, null, task, null));
                var lastRun = Convert.ToDateTime(task.GetType().InvokeMember("LastRunTime", BindingFlags.GetProperty, null, task, null));

                // 0x41301 = still running, 0x41306 = terminated (our /End), 0 = exited cleanly.
                return "задача: состояние=" + StateName(state)
                     + ", последний результат=0x" + (last & 0xFFFFFFFFL).ToString("X")
                     + ", последний запуск=" + lastRun.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception ex)
            {
                return "задача: состояние прочитать не удалось (" + ex.Message + ")";
            }
            finally
            {
                try { if (task != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task); } catch { }
                try { if (folder != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder); } catch { }
                try { if (svc != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(svc); } catch { }
            }
        }

        // TASK_STATE_* from the scheduler API.
        private static string StateName(int s)
        {
            switch (s)
            {
                case 0: return "неизвестно";
                case 1: return "отключена";
                case 2: return "в очереди";
                case 3: return "готова";
                case 4: return "выполняется";
                default: return s.ToString();
            }
        }

        private static string XmlEscape(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // Stop the running mihomo (ends the task instance and its process tree).
        // Allowed for the task owner without elevation, same as /Run.
        public static void EndTask()
        {
            string output;
            RunSchtasks("/End /TN \"" + TaskName + "\"", out output);
        }

        // Kill engine processes that OUR config points at but the task no longer controls.
        //
        // `schtasks /End` only ends the instance the scheduler still knows about. An engine
        // whose instance it lost — a launcher that was killed, an update that swapped the exe,
        // a task re-registered underneath a running process — survives it and keeps everything
        // it holds: its TUN interface, the machine's :53, and the control port. The next
        // session then finds "the engine answers but refuses our key", or a squatter on 9090,
        // and ends up running a SECOND engine alongside it — two TUN adapters fighting over the
        // route table, which is felt as sites that break for no reason until a restart.
        //
        // Kills in-process (no taskkill.exe spawn — that behaviour is one of the things
        // Defender's ML scores as trojan-like, see VpnMonitor).
        //
        // IDENTIFYING it is the hard part, and measured, not assumed: for a process running
        // elevated, a standard user reads neither Process.MainModule nor WMI ExecutablePath —
        // both come back empty. So the image path is used when it IS readable, and otherwise
        // the caller has to supply independent evidence: `blockerPid`, a process we have just
        // established is holding the engine's own control port. The process name alone is
        // never enough — another client shipping the same GitHub build would match it, and
        // that one isn't ours to touch.
        //
        // And it may simply fail: terminating an elevated process from a medium-integrity
        // launcher is refused by Windows, whatever we know about it. That's logged and not
        // fatal — the caller falls back to moving our own port, as it did before.
        public static int KillLeftoverEngines(AppConfig cfg, int blockerPid = 0)
        {
            var exe = (cfg?.MihomoExePath ?? "").Trim();
            if (exe.Length == 0) return 0;

            string name;
            try { name = Path.GetFileNameWithoutExtension(exe); }
            catch { return 0; }
            if (string.IsNullOrEmpty(name)) return 0;

            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { return 0; }

            int killed = 0;
            foreach (var p in procs)
            {
                try
                {
                    var path = PortGuard.ImagePath(p.Id);
                    bool ours = path.Length > 0
                        ? string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)
                        : (blockerPid > 0 && p.Id == blockerPid);
                    if (!ours || p.HasExited) continue;

                    p.Kill();
                    p.WaitForExit(3000);
                    killed++;
                    Log.Info("Остановлен движок, оставшийся от прошлого запуска: PID " + p.Id + ".");
                }
                catch (Exception ex)
                {
                    Log.Info("Движок от прошлого запуска (PID " + p.Id + ") остановить не удалось — "
                             + "он запущен с правами администратора: " + ex.Message);
                }
                finally { try { p.Dispose(); } catch { } }
            }
            return killed;
        }

        // The exe + arguments baked into the installed task, read through the Task Scheduler
        // COM API. Returns false if the task isn't installed or can't be read.
        //
        // This used to parse `schtasks /Query /XML`, which is a trap: with stdout REDIRECTED
        // schtasks writes that XML in the CONSOLE codepage, not the UTF-16 its header claims.
        // Decoding it as Unicode produced mojibake, the regex never matched, and the caller
        // read that as "nothing to worry about" — so a task left pointing at a path that no
        // longer exists was never detected. It failed with 0x80070002 (file not found) forever.
        public static bool InstalledTaskCommand(out string exe, out string arguments)
        {
            return TaskCommand(TaskName, out exe, out arguments);
        }

        // Same, for any of our tasks by name.
        public static bool TaskCommand(string taskName, out string exe, out string arguments)
        {
            exe = ""; arguments = "";
            object svc = null, folder = null, task = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) return false;
                svc = Activator.CreateInstance(t);
                t.InvokeMember("Connect", BindingFlags.InvokeMethod, null, svc,
                    new object[] { Type.Missing, Type.Missing, Type.Missing, Type.Missing });
                folder = t.InvokeMember("GetFolder", BindingFlags.InvokeMethod, null, svc, new object[] { "\\" });
                task = folder.GetType().InvokeMember("GetTask", BindingFlags.InvokeMethod, null, folder,
                    new object[] { taskName });

                // Take the definition as XML text rather than walking the Actions collection:
                // its indexer is a parameterised PROPERTY, which late binding gets wrong
                // (DISP_E_MEMBERNOTFOUND), and this way there's one thing to parse, already a
                // proper .NET string — no console codepage anywhere in the path.
                var def = task.GetType().InvokeMember("Definition", BindingFlags.GetProperty, null, task, null);
                var xml = Convert.ToString(def.GetType().InvokeMember("XmlText", BindingFlags.GetProperty, null, def, null)) ?? "";
                if (xml.Length == 0) return false;

                exe = FirstTag(xml, "Command");
                arguments = FirstTag(xml, "Arguments");
                return exe.Length > 0;
            }
            catch (Exception ex)
            {
                try { Log.Error("InstalledTaskCommand", ex); } catch { }
                return false;
            }
            finally
            {
                if (task != null) Marshal.ReleaseComObject(task);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (svc != null) Marshal.ReleaseComObject(svc);
            }
        }

        // The working dir baked into the installed task's command (the -d "<dir>" argument),
        // or null if the task isn't installed / can't be read.
        public static string InstalledTaskWorkDir()
        {
            string exe, args;
            if (!InstalledTaskCommand(out exe, out args)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(args, "-d\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Does the installed task actually point at the engine and work dir we're configured
        // for? A folder that was moved/renamed leaves the task behind pointing at the old
        // location; running it then fails with 0x80070002 and no amount of retrying helps —
        // only re-creating the task does. `reason` is a log-ready description of the mismatch.
        public static bool TaskMatchesConfig(AppConfig cfg, out string reason)
        {
            reason = "";
            string taskExe, args;
            if (!InstalledTaskCommand(out taskExe, out args)) return true;   // unreadable → don't nag

            var wantExe = (cfg?.MihomoExePath ?? "").Trim();
            var wantDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
            var m = System.Text.RegularExpressions.Regex.Match(args, "-d\\s+\"([^\"]+)\"");
            var taskDir = m.Success ? m.Groups[1].Value : "";

            if (wantExe.Length > 0 && !SamePath(taskExe, wantExe))
            {
                reason = "exe задачи=" + taskExe + " ожидается=" + wantExe;
                return false;
            }
            if (taskDir.Length > 0 && !SamePath(taskDir, wantDir))
            {
                reason = "workdir задачи=" + taskDir + " ожидается=" + wantDir;
                return false;
            }
            return true;
        }

        // Text of the first <tag>…</tag> in the task XML, XML-unescaped, or "" if absent.
        private static string FirstTag(string xml, string tag)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                xml, "<" + tag + ">(.*?)</" + tag + ">",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return "";
            return m.Groups[1].Value
                .Replace("&quot;", "\"").Replace("&apos;", "'")
                .Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Trim();
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals((a ?? "").Trim().TrimEnd('\\'), (b ?? "").Trim().TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }

        // Is the scheduled task already registered?
        public static bool IsTaskInstalled()
        {
            string output;
            return RunSchtasks("/Query /TN \"" + TaskName + "\"", out output) == 0;
        }

        // Definition of the companion "stop" task. Its own method so it can be validated
        // against the Task Scheduler without registering anything (which needs admin, so a
        // typo here would otherwise only surface during a real elevated install).
        public static string BuildStopTaskXml(string engineExeName)
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)</SecurityDescriptor></RegistrationInfo>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "  </Settings>\r\n" +
                // Absolute path, resolved now: an action's <Command> is expanded by the
                // scheduler, and there is no reason to depend on that when we can be exact.
                "  <Actions Context=\"Author\"><Exec><Command>" +
                XmlEscape(Path.Combine(Environment.SystemDirectory, "taskkill.exe")) + "</Command>" +
                "<Arguments>/F /IM \"" + XmlEscape(engineExeName ?? "") + "\"</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        public static bool IsStopTaskInstalled()
        {
            string output;
            return RunSchtasks("/Query /TN \"" + StopTaskName + "\"", out output) == 0;
        }

        // Definition of the firewall task: our own exe, as SYSTEM, with --firewall-sync.
        // `Queue` (not Parallel, not IgnoreNew): two syncs must never run at once — they would
        // fight over the same rules — and a request that arrives while one is running must not be
        // dropped, or the firewall would be left one generation behind the config.
        public static string BuildFirewallTaskXml(string launcherExe)
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)</SecurityDescriptor></RegistrationInfo>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + XmlEscape(launcherExe ?? "") + "</Command>" +
                "<Arguments>" + FirewallSyncArg + "</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        public static bool IsFirewallTaskInstalled()
        {
            string output;
            return RunSchtasks("/Query /TN \"" + FirewallTaskName + "\"", out output) == 0;
        }

        // Ask the helper to bring the firewall in line with the plan on disk. No UAC: starting a
        // task you are allowed to start is not an elevation prompt.
        public static bool RunFirewallTask()
        {
            string output;
            var code = RunSchtasks("/Run /TN \"" + FirewallTaskName + "\"", out output);
            if (code != 0)
                try { Log.Info("Брандмауэр: задача не запустилась, код " + code + (output.Length > 0 ? ": " + output : "")); } catch { }
            return code == 0;
        }

        // Path of the running launcher — what the firewall task is registered to run. A moved or
        // updated install leaves the task pointing at a path that no longer exists, and the only
        // cure is re-registering it, so this is compared on startup.
        public static string LauncherExePath()
        {
            try
            {
                var p = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return Path.Combine(VersionInfo.InstallRoot, "MihomoLauncher.exe");
        }

        // Does the installed firewall task still point at THIS exe? False also when it isn't
        // installed at all, so the caller's single "needs the elevated install" path covers both.
        public static bool FirewallTaskMatchesExe()
        {
            if (!IsFirewallTaskInstalled()) return false;
            string exe, args;
            if (!TaskCommand(FirewallTaskName, out exe, out args)) return true;   // unreadable → don't nag
            return SamePath(exe, LauncherExePath());
        }

        // Kill the engine for certain, whatever state it is in.
        //
        // The engine runs as SYSTEM (see InstallElevated), so the launcher — a medium-integrity
        // process of an ordinary user — cannot terminate it, no matter how well it has
        // identified it. And `schtasks /End` only reaches an instance the scheduler still
        // tracks; the one that matters here is exactly the one it has lost.
        //
        // So the killing is delegated to something that does have the rights: a second
        // on-demand task, registered as SYSTEM by the same elevated install, whose only action
        // is taskkill on the engine image. Starting a task you're allowed to start needs no
        // UAC, so this is silent — the user never sees a prompt for it. (taskkill is spawned by
        // the scheduler, not by our unsigned exe, so the Defender heuristic that made us stop
        // spawning it ourselves doesn't apply here.)
        //
        // Order: the polite ways first, the hammer only if they didn't work.
        // Start the engine, guaranteeing there is exactly ONE on the machine.
        //
        // Every extra engine here is OURS — the task started it and then lost track of it, and
        // `/Run` happily starts another whenever the scheduler believes nothing is running. Two
        // engines mean two TUN adapters, and the route table picks a winner that isn't
        // necessarily the one we steer: the machine then routes by the OTHER one's config
        // (usually MATCH,DIRECT, i.e. straight out with the real IP) while our UI reports
        // everything connected, because our probes go through our own mixed-port. So the field
        // is cleared before every start, and never left to chance.
        public static void StartEngineAlone(AppConfig cfg)
        {
            ForceStopEngine(cfg);   // cheap no-op when nothing is running
            RunTask();
        }

        // How many engine processes exist right now. More than one is always a fault: the task
        // is single-instance by policy, so a second one means the scheduler lost track of the
        // first and started another. Two engines = two TUN adapters, and the route table picks
        // one — not necessarily ours.
        public static int CountEngines(AppConfig cfg)
        {
            var exe = (cfg?.MihomoExePath ?? "").Trim();
            if (exe.Length == 0) return 0;
            try
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                if (string.IsNullOrEmpty(name)) return 0;
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
                return procs.Length;
            }
            catch { return 0; }
        }

        public static bool ForceStopEngine(AppConfig cfg, int blockerPid = 0)
        {
            var exe = (cfg?.MihomoExePath ?? "").Trim();
            if (exe.Length == 0) return false;

            string name;
            try { name = Path.GetFileNameWithoutExtension(exe); }
            catch { return false; }
            if (string.IsNullOrEmpty(name)) return false;

            // `schtasks /End` returns long before the process is actually gone, so give it a
            // moment before escalating. Skipping this would mean every ordinary shutdown ends
            // in a /F kill, and the engine would never get to tear its TUN down in order.
            EndTask();
            if (WaitEngineGone(name, 2500)) return true;

            KillLeftoverEngines(cfg, blockerPid);
            if (WaitEngineGone(name, 1000)) return true;

            if (!IsStopTaskInstalled())
            {
                Log.Info("Движок не остановить: задача принудительной остановки не установлена "
                         + "(нужна переустановка движка — она регистрируется в ней).");
                return false;
            }

            string output;
            var code = RunSchtasks("/Run /TN \"" + StopTaskName + "\"", out output);
            if (code != 0)
                Log.Info("Задача принудительной остановки не запустилась: код " + code
                         + (output.Length > 0 ? " " + output : ""));

            // The scheduler returns as soon as it has accepted the request; taskkill itself
            // takes a moment, and the process needs another to actually go.
            if (WaitEngineGone(name, 5000))
            {
                Log.Info("Движок остановлен принудительно (задача " + StopTaskName + ").");
                return true;
            }
            Log.Info("Движок не остановился даже принудительно — процесс " + name + " ещё жив.");
            return false;
        }

        private static bool WaitEngineGone(string processName, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                if (EngineGone(processName)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                try { System.Threading.Thread.Sleep(200); } catch { return false; }
            }
        }

        private static bool EngineGone(string processName)
        {
            try
            {
                var procs = Process.GetProcessesByName(processName);
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
                return procs.Length == 0;
            }
            catch { return false; }
        }

        // Create + start the task. Triggers ONE UAC prompt (runas). Returns false
        // with a reason in `error` if the user cancels or it fails.
        public static bool InstallElevated(AppConfig cfg, out string error)
        {
            error = "";
            try
            {
                var exe = (cfg.MihomoExePath ?? "").Trim();
                if (exe.Length == 0 || !File.Exists(exe))
                {
                    error = Loc.T("Svc_MihomoExeNotFound");
                    return false;
                }
                var workDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
                Directory.CreateDirectory(workDir);

                // Register from XML so the task:
                //  * runs as SYSTEM (S-1-5-18) in session 0 => mihomo's console window is
                //    never rendered on the user's desktop (fully hidden), and is elevated;
                //  * grants Authenticated Users read+execute (GRGX) via the security
                //    descriptor => the non-admin launcher can /Run it on demand and /End
                //    it on exit without UAC;
                //  * runs mihomo.exe directly => /End terminates mihomo itself, no orphan;
                //  * has NO triggers (on-demand only): the engine must come up when the
                //    launcher connects, never on its own. A LogonTrigger here would start
                //    the tunnel at every logon regardless of the launcher's autostart
                //    setting — that's what the Autostart Run entry is for.
                var xmlPath = Path.Combine(Path.GetTempPath(), "mihomolauncher_mihomo_task.xml");
                var xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                    "  <RegistrationInfo><SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)</SecurityDescriptor></RegistrationInfo>\r\n" +
                    "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                    "  <Settings>\r\n" +
                    "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                    "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                    "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                    "    <Enabled>true</Enabled>\r\n" +
                    "    <Hidden>false</Hidden>\r\n" +
                    "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                    "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                    "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                    "  </Settings>\r\n" +
                    "  <Actions Context=\"Author\"><Exec><Command>" + XmlEscape(exe) + "</Command><Arguments>-d \"" + XmlEscape(workDir) + "\"</Arguments></Exec></Actions>\r\n" +
                    "</Task>\r\n";
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);

                // The companion "stop" task. Same principal (SYSTEM) and the same security
                // descriptor, so the non-admin launcher can /Run it without UAC — that is the
                // whole point: it can then kill an engine it has no rights to touch itself,
                // including one the scheduler has lost track of and /End can no longer reach.
                // Parallel, not IgnoreNew: a second request must never be silently dropped.
                var stopXmlPath = Path.Combine(Path.GetTempPath(), "mihomolauncher_stop_task.xml");
                File.WriteAllText(stopXmlPath, BuildStopTaskXml(Path.GetFileName(exe)), System.Text.Encoding.Unicode);

                // The firewall task — the kill-switch that keeps working when the engine does
                // not. Registered here so the whole setup still costs ONE prompt; it is written
                // even when the feature is off, because turning it on later must not need admin.
                var fwXmlPath = Path.Combine(Path.GetTempPath(), "mihomolauncher_fw_task.xml");
                File.WriteAllText(fwXmlPath, BuildFirewallTaskXml(LauncherExePath()), System.Text.Encoding.Unicode);

                // A tiny batch avoids nested-quote hell. Kill+delete any previous task
                // (incl. an old visible-console variant) and stray mihomo first.
                var bat = Path.Combine(Path.GetTempPath(), "mihomolauncher_mihomo_setup.bat");
                var content =
                    "@echo off\r\n" +
                    "schtasks /End /TN \"" + TaskName + "\" >nul 2>&1\r\n" +
                    "schtasks /Delete /TN \"" + TaskName + "\" /F >nul 2>&1\r\n" +
                    "taskkill /F /IM \"" + Path.GetFileName(exe) + "\" >nul 2>&1\r\n" +
                    "schtasks /Delete /TN \"" + StopTaskName + "\" /F >nul 2>&1\r\n" +
                    "schtasks /Create /TN \"" + StopTaskName + "\" /XML \"" + stopXmlPath + "\" /F >nul 2>&1\r\n" +
                    "schtasks /Delete /TN \"" + FirewallTaskName + "\" /F >nul 2>&1\r\n" +
                    "schtasks /Create /TN \"" + FirewallTaskName + "\" /XML \"" + fwXmlPath + "\" /F >nul 2>&1\r\n" +
                    "schtasks /Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F\r\n" +
                    "schtasks /Run /TN \"" + TaskName + "\"\r\n" +
                    "exit /b %errorlevel%\r\n";
                File.WriteAllText(bat, content);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"" + bat + "\"",
                    UseShellExecute = true,   // required for the runas verb
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { error = Loc.T("Svc_SetupStartFailed"); return false; }
                    p.WaitForExit(20000);
                }

                return IsTaskInstalled();
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
            {
                error = Loc.T("Svc_SetupCancelledNeedAdmin");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
