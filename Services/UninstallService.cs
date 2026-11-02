using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // The actual uninstall steps, each returning a short human result for the progress window
    // (UninstallWindow). Replaces the old translit cmd console (Data\uninstall.bat).
    public static class UninstallService
    {
        // Close every other running MihomoLauncher.exe (NOT this uninstaller instance).
        public static async Task<string> CloseLauncherAsync(CancellationToken ct)
        {
            int me = Process.GetCurrentProcess().Id;
            bool any = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool alive = false;
                foreach (var p in Safe("MihomoLauncher"))
                {
                    if (p.Id == me) { p.Dispose(); continue; }
                    any = true; alive = true;
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
                if (!alive) break;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            return any ? Loc.T("Uni_ResClosed") : Loc.T("Uni_ResNotRunningClose");
        }

        // Stop the mihomo engine: drop TUN through the API, end its scheduled task (works
        // without admin), then kill whatever is left. The TUN-off matters most here — we're
        // leaving the machine for good, so it has to be handed back with its routes and DNS
        // in order, not with a fake-ip cache pointing at a tunnel that no longer exists.
        public static async Task<string> StopMihomoAsync(CancellationToken ct)
        {
            try
            {
                var cfg = AppConfigStore.Load();
                if (cfg != null)
                    await new MihomoController(cfg).SetTunAsync(false, 2, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Error("uninstall.tun-off", ex); }

            try { MihomoSetup.EndTask(); } catch { }
            bool any = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool alive = false;
                foreach (var name in new[] { "mihomo-windows-amd64", "mihomo" })
                    foreach (var p in Safe(name))
                    {
                        any = true; alive = true;
                        try { p.Kill(); } catch { }
                        p.Dispose();
                    }
                if (!alive) break;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            // The engine is gone; its fake-ip answers must go with it.
            EngineShutdown.FlushDns();
            return any ? Loc.T("Uni_ResStopped") : Loc.T("Uni_ResNotRunning");
        }

        // Remove the HKCU\Run autostart entry + any startup-folder shortcuts.
        public static string RemoveAutostart()
        {
            try { Autostart.Disable(); return Loc.T("Uni_ResRemovedM"); }
            catch (Exception ex) { Log.Error("uninstall.autostart", ex); return Loc.T("Uni_ResError", ex.Message); }
        }

        // Delete the mihomo scheduled task. Non-admin /Delete usually fails (the task is
        // SYSTEM), so retry once elevated (single UAC). Returns what happened.
        public static async Task<string> DeleteTaskAsync(CancellationToken ct)
        {
            if (!MihomoSetup.IsTaskInstalled()) return Loc.T("Uni_ResNoTask");

            RunSchtasksDelete(elevated: false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (!MihomoSetup.IsTaskInstalled()) return Loc.T("Uni_ResDeleted");

            // Needs admin — ask for UAC and try again.
            RunSchtasksDelete(elevated: true);
            await Task.Delay(600, ct).ConfigureAwait(false);
            return MihomoSetup.IsTaskInstalled() ? Loc.T("Uni_ResTaskFail") : Loc.T("Uni_ResDeleted");
        }

        // Clean the leftovers that live OUTSIDE the install folder (the user deletes the folder
        // itself, which takes DataUser\ — config, geo DB, mihomo engine — with it):
        //   1) %LocalAppData%\MihomoLauncher    — legacy/migrated config copy, bundled icons, and
        //                                       the engine for pre-DataUser installs.
        //   2) %USERPROFILE%\.config\mihomo  — the generated mihomo config.yaml.
        // The user's rules survive in DataUser\config.xml, so this never loses their settings.
        public static string CleanServiceData()
        {
            var removed = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();

            TryWipe(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MihomoLauncher"),
                "AppData", removed, failed);
            TryWipe(AppConfigStore.DefaultMihomoWorkDir, "mihomo", removed, failed);

            if (failed.Count > 0) return Loc.T("Uni_ResError", string.Join(", ", failed));
            if (removed.Count == 0) return Loc.T("Uni_ResNothing");
            return Loc.T("Uni_ResRemovedP");
        }

        private static void TryWipe(string dir, string label,
            System.Collections.Generic.List<string> removed, System.Collections.Generic.List<string> failed)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
                if (Directory.Exists(dir)) failed.Add(label); else removed.Add(label);
            }
            catch (Exception ex) { Log.Error("uninstall.wipe." + label, ex); failed.Add(label); }
        }

        private static void RunSchtasksDelete(bool elevated)
        {
            try
            {
                // Both of ours: the engine task and its companion "stop" task. One cmd so the
                // elevated variant costs a single UAC prompt, not two.
                // The firewall rules MUST go first, while our exe is still on disk and the task
                // that can run it elevated still exists. A leftover block rule would keep cutting
                // a program off from the internet long after the launcher was removed, with
                // nothing left to explain why — the one way this feature could do lasting harm.
                var self = MihomoSetup.LauncherExePath();
                var args = "/c start /wait \"\" \"" + self + "\" --firewall-clear"
                         + " & schtasks /Delete /TN \"" + MihomoSetup.TaskName + "\" /F"
                         + " & schtasks /Delete /TN \"" + MihomoSetup.StopTaskName + "\" /F"
                         + " & schtasks /Delete /TN \"" + MihomoSetup.FirewallTaskName + "\" /F";
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = args,
                    UseShellExecute = elevated,           // ShellExecute required for runas
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                if (elevated) psi.Verb = "runas";
                using (var p = Process.Start(psi)) { p?.WaitForExit(8000); }
            }
            catch (Exception ex) { Log.Error("uninstall.schtasks", ex); }
        }

        private static Process[] Safe(string name)
        {
            try { return Process.GetProcessesByName(name); }
            catch { return Array.Empty<Process>(); }
        }
    }
}
