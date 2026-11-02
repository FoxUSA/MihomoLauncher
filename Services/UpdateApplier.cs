using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // Runs inside the FRESHLY EXTRACTED MihomoLauncher.exe (launched with --apply-update from the
    // staging folder). It performs the swap the old, still-running launcher couldn't do to
    // itself: wait for the old process to exit, stop every related process, delete the old
    // Data\ + MihomoLauncher.exe, copy the new files from the staging folder into the install
    // root, then relaunch. DataUser\ and anything not present in the archive are left alone.
    //
    // Every step reports progress so the UpdaterWindow can show exactly what is happening.
    public static class UpdateApplier
    {
        // Parsed --apply-update arguments.
        // Layout: --apply-update <oldPid> "<installRoot>" "<srcRoot>"
        //   srcRoot = folder with the NEW files (the extracted archive). This exe itself runs
        //   from a separate copy of the CURRENT install, so srcRoot is passed explicitly.
        public class Options
        {
            public int OldPid;
            public string InstallRoot;
            public string SrcRoot;
        }

        public static bool IsUpdaterMode(string[] args)
        {
            return args != null && args.Length >= 1 &&
                   string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase);
        }

        public static Options Parse(string[] args)
        {
            var o = new Options { SrcRoot = AppContext.BaseDirectory.TrimEnd('\\') };
            if (args != null)
            {
                if (args.Length >= 2) int.TryParse(args[1], out o.OldPid);
                if (args.Length >= 3) o.InstallRoot = (args[2] ?? "").Trim().TrimEnd('\\');
                if (args.Length >= 4 && !string.IsNullOrWhiteSpace(args[3]))
                    o.SrcRoot = args[3].Trim().TrimEnd('\\');
            }
            return o;
        }

        // Step log that survives the update (DataUser isn't wiped) — first place to look if an
        // update doesn't complete on the user's machine. Sits with the other logs in
        // DataUser\logs\; this runs in the copied applier process, which has no VersionInfo of
        // its own to consult, so the path is built from the install root it was handed.
        private static void UpLog(Options opt, string msg)
        {
            try
            {
                var dir = Path.Combine(opt.InstallRoot ?? "", "DataUser", "logs");
                Directory.CreateDirectory(dir);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n";
                File.AppendAllText(Path.Combine(dir, "update.log"), line, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // The whole swap. onStatus/onPercent drive the progress UI; onPercent is -1 for the
        // indeterminate (waiting) phases and 0..100 during the file copy.
        public static async Task RunAsync(Options opt, Action<string> onStatus, Action<int> onPercent, CancellationToken ct)
        {
            var root = opt.InstallRoot;
            var src = opt.SrcRoot;
            UpLog(opt, "=== Установка обновления: installRoot=" + root + " src=" + src + " oldPid=" + opt.OldPid);

            // 1) Wait for the old launcher to exit so its files are unlocked.
            onStatus?.Invoke(Loc.T("Upd_ClosingApp"));
            onPercent?.Invoke(-1);
            await WaitForExitAsync(opt.OldPid, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            UpLog(opt, "1/5 старая программа закрыта");

            // 2) Stop the VPN engine and make sure no related process keeps a file locked.
            onStatus?.Invoke(Loc.T("Upd_CheckingProcesses"));
            await EnsureProcessesGoneAsync(root, ct).ConfigureAwait(false);
            UpLog(opt, "2/5 процессы остановлены");

            // 3) Preserve user data that lived under the OLD Data\ layout before wiping it
            //    (geo DB / service list / their dates), so we don't re-download ~65 MB.
            onStatus?.Invoke(Loc.T("Upd_SavingUserData"));
            try { MigrateLegacyDataToDataUser(root); } catch (Exception ex) { Log.Error("update.migrate", ex); UpLog(opt, "migrate err: " + ex.Message); }

            // 4) Delete the old Data\ folder and MihomoLauncher.exe (NEVER DataUser\). Clear the
            //    Hidden/System/ReadOnly bits FolderIcons set so nothing blocks the delete.
            onStatus?.Invoke(Loc.T("Upd_RemovingOld"));
            DeleteWithRetry(() => ForceDeleteDir(Path.Combine(root, "Data")));
            DeleteWithRetry(() => { var e = Path.Combine(root, "MihomoLauncher.exe"); if (File.Exists(e)) File.Delete(e); });
            UpLog(opt, "3/5 старая версия удалена (Data + MihomoLauncher.exe)");

            // 5) Copy EVERYTHING from the archive into the install root, overwriting. The
            //    archive has no DataUser\, so user data is untouched; help/uninstall files are
            //    replaced if present. ("всё, что есть в архиве, берём без исключений").
            onStatus?.Invoke(Loc.T("Upd_InstallingNew"));
            CopyTreeWithProgress(src, root, onPercent, ct);
            UpLog(opt, "4/5 новая версия установлена");

            // 6) Re-apply folder icons + shortcuts on the fresh install.
            try { FolderIcons.Apply(root); } catch (Exception ex) { Log.Error("update.icons", ex); }

            // 7) Relaunch the installed launcher.
            onStatus?.Invoke(Loc.T("Upd_RelaunchingApp"));
            onPercent?.Invoke(100);
            var newExe = Path.Combine(root, "MihomoLauncher.exe");
            try
            {
                Process.Start(new ProcessStartInfo { FileName = newExe, UseShellExecute = true, WorkingDirectory = root });
                UpLog(opt, "5/5 запущена новая версия: " + newExe);
            }
            catch (Exception ex) { Log.Error("update.relaunch", ex); UpLog(opt, "relaunch err: " + ex.Message); throw; }
        }

        private static async Task WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
        {
            if (pid <= 0) return;
            var deadline = Environment.TickCount + (int)timeout.TotalMilliseconds;
            while (Environment.TickCount < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try { Process.GetProcessById(pid); }
                catch { return; }   // no such process → it exited
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        // Stop mihomo (its scheduled task + the process) and any leftover MihomoLauncher.exe that
        // isn't us, then verify they're gone. Best-effort but loops a few times.
        private static async Task EnsureProcessesGoneAsync(string root, CancellationToken ct)
        {
            try { MihomoSetup.EndTask(); } catch { }
            int me = Process.GetCurrentProcess().Id;

            for (int attempt = 0; attempt < 15; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                bool any = false;

                // Other MihomoLauncher.exe instances (skip ourselves — we run from the staging copy).
                foreach (var p in SafeGetProcesses("MihomoLauncher"))
                {
                    if (p.Id == me) { p.Dispose(); continue; }
                    any = true;
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }

                // mihomo engine.
                foreach (var name in new[] { "mihomo-windows-amd64", "mihomo" })
                    foreach (var p in SafeGetProcesses(name))
                    {
                        any = true;
                        try { p.Kill(); } catch { }
                        p.Dispose();
                    }

                if (!any) return;
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }

        private static Process[] SafeGetProcesses(string name)
        {
            try { return Process.GetProcessesByName(name); }
            catch { return Array.Empty<Process>(); }
        }

        // Move geo DB / service list out of the old Data\ into DataUser\ if DataUser doesn't
        // already have them, and seed manifest_geo.json from the old manifest.json's geoDate.
        private static void MigrateLegacyDataToDataUser(string root)
        {
            var dataMax = Path.Combine(root, "Data", "maxmind", "GeoLite2-City.mmdb");
            var userMax = Path.Combine(root, "DataUser", "maxmind", "GeoLite2-City.mmdb");
            if (File.Exists(dataMax) && !File.Exists(userMax))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(userMax));
                try { File.Copy(dataMax, userMax, overwrite: false); } catch { }
            }

            var dataCat = Path.Combine(root, "Data", "blackmatrix7+www.txt");
            var userCat = Path.Combine(root, "DataUser", "blackmatrix7+www.txt");
            if (File.Exists(dataCat) && !File.Exists(userCat))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(userCat));
                try { File.Copy(dataCat, userCat, overwrite: false); } catch { }
            }
        }

        // Recursively clear attributes (ReadOnly/Hidden/System) and delete a directory.
        private static void ForceDeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                try { new DirectoryInfo(dir).Attributes = FileAttributes.Directory; } catch { }
            }
            catch { }
            Directory.Delete(dir, true);
        }

        private static void DeleteWithRetry(Action del)
        {
            for (int i = 0; i < 10; i++)
            {
                try { del(); return; }
                catch { Thread.Sleep(300); }
            }
            try { del(); } catch (Exception ex) { Log.Error("update.delete", ex); }
        }

        // Recursively copy src into dst, overwriting, reporting 0..100 by file count.
        private static void CopyTreeWithProgress(string src, string dst, Action<int> onPercent, CancellationToken ct)
        {
            var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            int total = files.Length, done = 0, last = -1;
            Directory.CreateDirectory(dst);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = file.Substring(src.Length).TrimStart('\\', '/');
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                CopyFileWithRetry(file, target);
                done++;
                if (total > 0 && onPercent != null)
                {
                    var pct = (int)((long)done * 100 / total);
                    if (pct != last) { last = pct; onPercent(pct); }
                }
            }
        }

        private static void CopyFileWithRetry(string from, string to)
        {
            for (int i = 0; i < 10; i++)
            {
                try { CopyOverwrite(from, to); return; }
                catch { Thread.Sleep(200); }
            }
            CopyOverwrite(from, to);   // last try: let it throw
        }

        // File.Copy(overwrite) fails on a Hidden/System/ReadOnly destination (e.g. the root
        // desktop.ini FolderIcons writes) with "Access denied" — clear the dest attributes first.
        private static void CopyOverwrite(string from, string to)
        {
            if (File.Exists(to)) { try { File.SetAttributes(to, FileAttributes.Normal); } catch { } }
            File.Copy(from, to, overwrite: true);
        }
    }
}
