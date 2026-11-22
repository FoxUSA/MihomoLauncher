// Data\service.exe — the Windows service that runs the mihomo engine as SYSTEM.
//
// Replaces the scheduled tasks (MihomoLauncher_mihomo / _stop / _fw). A task started on demand
// can sit "queued" for minutes — on a laptop with an almost empty battery, or right after a
// Windows Update restart (PAPA2, 2026-11-08) — and the engine simply never came up. A service
// starts when it is told to.
//
// It does nothing on its own. The launcher (an ordinary user, no UAC) starts the service and
// sends it custom commands; the service's security descriptor lets Authenticated Users start,
// stop and control it, nothing more. What to run is fixed at install time (elevated) in the
// service's command line — the commands carry no paths, so the right to send them is not the
// right to run anything else as SYSTEM.
//
// The service stops itself once there is nothing left to do (no engine, no firewall sync
// running): an idle service would keep Data\service.exe locked and an update could
// not replace it.
//
// Built by build\layout-release.ps1 with the framework csc (no SDK, C# 5, /codepage:65001), into Data\.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

// What Task Manager shows for this process (Processes tab = FileDescription) and what the file's
// Properties say — without it the process was a bare, anonymous «service.exe». csc turns these
// into the file's version resource.
[assembly: AssemblyTitle("Mihomo Launcher Service")]
[assembly: AssemblyDescription("Mihomo Launcher Service")]
[assembly: AssemblyProduct("Mihomo Launcher")]
[assembly: AssemblyCompany("byFox")]
[assembly: AssemblyCopyright("Copyright (c) 2026 byFox")]

namespace MihomoLauncherSvc
{
    public class EngineService : ServiceBase
    {
        public const string Name = "MihomoLauncherEngine";

        // Custom commands (128..255). Mirrored in MihomoLauncher's MihomoSetup.
        public const int CmdStartEngine = 130;
        public const int CmdStopEngine = 131;
        public const int CmdKillEngines = 132;
        public const int CmdFirewallSync = 133;
        // Last resort: every process with OUR engine's file name, wherever it lives — what the
        // old MihomoLauncher_stop task (taskkill /IM) always did. Sent only after 132 failed, i.e.
        // a same-named engine from another folder (a pre-rename MomLauncher install on mom's PC,
        // 2026-11-11) holds the port and nothing else can remove it.
        public const int CmdKillEnginesAnyPath = 134;

        private readonly string _engine, _workDir, _launcher;
        private readonly object _gate = new object();
        private readonly object _fwGate = new object();
        private Process _proc;
        private int _fwBusy;
        private readonly Timer _idle;

        public EngineService(string engine, string workDir, string launcher)
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
            _engine = engine ?? "";
            _workDir = workDir ?? "";
            _launcher = launcher ?? "";
            _idle = new Timer(_ => StopIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
        }

        protected override void OnStart(string[] args)
        {
            // Started bare = "be ready for commands". The launcher sends the command right after.
            ArmIdleStop();
        }

        protected override void OnStop() { StopEngine(); }
        protected override void OnShutdown() { StopEngine(); }

        protected override void OnCustomCommand(int command)
        {
            try
            {
                switch (command)
                {
                    case CmdStartEngine: StartEngine(); break;
                    case CmdStopEngine: StopEngine(); break;
                    case CmdKillEngines: KillEngines(); break;
                    case CmdKillEnginesAnyPath: KillEngines(true); break;
                    case CmdFirewallSync: ThreadPool.QueueUserWorkItem(_ => FirewallSync()); break;
                }
            }
            catch (Exception ex) { Note("command " + command + ": " + ex.Message); }
            ArmIdleStop();
        }

        // Exactly one engine: anything left from before (a lost child, a crashed session) goes
        // first — two engines mean two TUN adapters fighting over the route table.
        private void StartEngine()
        {
            lock (_gate)
            {
                if (_proc != null && !_proc.HasExited) return;
                KillEngines();
                if (!File.Exists(_engine)) { Note("engine not found: " + _engine); return; }

                var psi = new ProcessStartInfo
                {
                    FileName = _engine,
                    Arguments = "-d \"" + _workDir + "\"",
                    WorkingDirectory = Path.GetDirectoryName(_engine),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                if (p == null) return;
                // ArmIdleStop takes no lock — it MUST NOT: the Exited handler runs under the
                // Process object's own lock, and StopEngine's WaitForExit takes that lock while
                // holding _gate. A lock here was a deadlock: OnStop never returned and the service
                // hung «StopPending» for good (PAPA2, 2026-11-09).
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) => ArmIdleStop();
                _proc = p;
            }
        }

        private void StopEngine()
        {
            lock (_gate)
            {
                if (_proc != null)
                {
                    try { if (!_proc.HasExited) { _proc.Kill(); _proc.WaitForExit(5000); } } catch { }
                    try { _proc.Dispose(); } catch { }
                    _proc = null;
                }
                KillEngines();
            }
        }

        // Every process running OUR engine image, child or not — as SYSTEM we may kill them all,
        // which the launcher itself (a medium-integrity process) never could.
        private void KillEngines(bool anyPath = false)
        {
            string name;
            try { name = Path.GetFileNameWithoutExtension(_engine); } catch { return; }
            if (string.IsNullOrEmpty(name)) return;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule.FileName; } catch { }
                    if (!anyPath && path.Length > 0 && !string.Equals(path, _engine, StringComparison.OrdinalIgnoreCase)) continue;
                    if (anyPath && path.Length > 0) Note("killing engine from another folder: " + path);
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // The launcher's own exe, as SYSTEM, in its --firewall-sync mode: it reads the plan the
        // launcher wrote and applies it. One at a time — two would fight over the same rules —
        // and one that arrives meanwhile still runs afterwards (the plan may have moved on).
        private void FirewallSync()
        {
            lock (_fwGate)
            {
                Interlocked.Increment(ref _fwBusy);
                try
                {
                    var launcher = ResolveLauncher(_launcher);
                    if (!File.Exists(launcher)) { Note("launcher not found: " + _launcher); return; }
                    var psi = new ProcessStartInfo
                    {
                        FileName = launcher,
                        Arguments = "--firewall-sync",
                        WorkingDirectory = Path.GetDirectoryName(launcher),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null && !p.WaitForExit(120000)) { try { p.Kill(); } catch { } }
                    }
                }
                catch (Exception ex) { Note("firewall sync: " + ex.Message); }
                finally { Interlocked.Decrement(ref _fwBusy); ArmIdleStop(); }
            }
        }

        // The launcher was MihomoLauncher.exe up to 3.1.0 and is «Mihomo Launcher.exe» since
        // (2026-11-19). A service registered before names the old file — which is now only a
        // forwarder, or gone — so the new name beside it wins when it is there.
        private static string ResolveLauncher(string launcher)
        {
            try
            {
                if (string.Equals(Path.GetFileName(launcher), "MihomoLauncher.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var renamed = Path.Combine(Path.GetDirectoryName(launcher), "Mihomo Launcher.exe");
                    if (File.Exists(renamed)) return renamed;
                }
            }
            catch { }
            return launcher;
        }

        // Nothing running a while after the last event → stop the service (see the header).
        // Lock-free on purpose — see StartEngine. Timer.Change is thread-safe.
        private void ArmIdleStop()
        {
            try { _idle.Change(5000, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private void StopIfIdle()
        {
            bool idle;
            lock (_gate) idle = (_proc == null || _proc.HasExited) && Volatile.Read(ref _fwBusy) == 0;
            if (idle) { try { Stop(); } catch { } }
        }

        private static void Note(string msg)
        {
            try { EventLog.WriteEntry(Name, msg, EventLogEntryType.Warning); } catch { }
        }

        // Command line (set at install): --engine "<mihomo.exe>" --workdir "<dir>" --launcher "<Mihomo Launcher.exe>"
        public static void Main(string[] args)
        {
            string engine = "", workDir = "", launcher = "";
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--engine") engine = args[++i];
                else if (args[i] == "--workdir") workDir = args[++i];
                else if (args[i] == "--launcher") launcher = args[++i];
            }
            ServiceBase.Run(new EngineService(engine, workDir, launcher));
        }
    }
}
