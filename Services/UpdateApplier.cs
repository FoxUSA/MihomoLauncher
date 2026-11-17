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
    // The update could not be put in place. RolledBack: the install is exactly as before — start
    // it again and ask to retry after a reboot. LockedFile / Holder: what was in the way, if known.
    public sealed class UpdateBlockedException : Exception
    {
        public string LockedFile { get; }
        public string Holder { get; }
        public bool RolledBack { get; }

        public UpdateBlockedException(string message, string lockedFile, string holder, bool rolledBack, Exception inner)
            : base(message, inner)
        {
            LockedFile = lockedFile;
            Holder = holder ?? "";
            RolledBack = rolledBack;
        }
    }

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

            // 4+5) Put the old Data\ + exe aside, copy the new ones in — or, if anything is in the
            //    way, put everything back as it was (UpdateBlockedException → the old version is
            //    started again and the user is told to retry after a reboot).
            onStatus?.Invoke(Loc.T("Upd_RemovingOld"));
            SwapIn(opt, () => onStatus?.Invoke(Loc.T("Upd_InstallingNew")), onPercent, ct);

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

        // ---- The swap: rename aside, copy in, roll back on any failure ----
        //
        // It used to DELETE Data\ and the exe, then copy. A file nobody could free (the engine
        // service hung with its admin prompt refused, an antivirus, Dropbox) stopped the delete
        // halfway — silently — and then the copy, with «Процесс не может получить доступ к файлу
        // …» on screen and an install left with half its Data\ and no MihomoLauncher.exe at all
        // (2026-11-16). A rename is all or nothing: if it can't happen, nothing has been touched.
        public static void SwapIn(Options opt, Action onCopying, Action<int> onPercent, CancellationToken ct)
        {
            var root = opt.InstallRoot;
            var data = Path.Combine(root, "Data");
            var exe = Path.Combine(root, "MihomoLauncher.exe");
            var dataOld = FreeAside(data + ".old", dir: true);
            var exeOld = FreeAside(exe + ".old", dir: false);
            bool dataMoved = false, exeMoved = false;

            try
            {
                if (Directory.Exists(data)) { WithRetry(() => Directory.Move(data, dataOld)); dataMoved = true; }
                if (File.Exists(exe)) { WithRetry(() => File.Move(exe, exeOld)); exeMoved = true; }
            }
            catch (Exception ex)
            {
                // Which file is held, and by whom — asked before anything is moved back, while the
                // place is still the one that failed.
                string locked = FindLocked(dataMoved ? null : data, exeMoved ? null : exe);
                string holder = locked != null ? FileHolders.Describe(locked) : "";
                bool back = PutBack(data, dataOld, dataMoved, exe, exeOld, exeMoved);
                UpLog(opt, "3/5 ОТМЕНА: старую версию не отложить (" + ex.Message + ")"
                           + (locked != null ? "; занят " + locked + (holder.Length > 0 ? " — " + holder : "") : "")
                           + (back ? "; всё возвращено как было" : "; ВЕРНУТЬ НЕ УДАЛОСЬ"));
                throw new UpdateBlockedException(ex.Message, locked, holder, back, ex);
            }
            UpLog(opt, "3/5 старая версия отложена: " + Path.GetFileName(dataOld) + ", " + Path.GetFileName(exeOld));

            // Copy EVERYTHING from the archive into the install root, overwriting. The archive has
            // no DataUser\, so user data is untouched; help/uninstall files are replaced too.
            onCopying?.Invoke();
            try
            {
                CopyTreeWithProgress(opt.SrcRoot, root, onPercent, ct);
            }
            catch (Exception ex)
            {
                // Half a new version is worse than the old one: clear what got copied, put the old back.
                string locked = ex is IOException ? LockedIn(ex.Message, root) : null;
                string holder = locked != null ? FileHolders.Describe(locked) : "";
                try { if (Directory.Exists(data)) ForceDeleteDir(data); } catch { }
                try { if (File.Exists(exe)) { File.SetAttributes(exe, FileAttributes.Normal); File.Delete(exe); } } catch { }
                bool back = PutBack(data, dataOld, dataMoved, exe, exeOld, exeMoved);
                UpLog(opt, "4/5 ОТМЕНА: новая версия не скопировалась (" + ex.Message + ")"
                           + (back ? "; прежняя возвращена" : "; ВЕРНУТЬ НЕ УДАЛОСЬ"));
                throw new UpdateBlockedException(ex.Message, locked, holder, back, ex);
            }
            UpLog(opt, "4/5 новая версия установлена");

            // The old copies are of no use now; whatever can't go yet goes at the next start.
            CleanAside(root);
        }

        // Moving back whatever was moved aside. True when the install is as it was.
        private static bool PutBack(string data, string dataOld, bool dataMoved, string exe, string exeOld, bool exeMoved)
        {
            bool ok = true;
            if (exeMoved)
            {
                try
                {
                    if (File.Exists(exe)) { File.SetAttributes(exe, FileAttributes.Normal); File.Delete(exe); }
                    WithRetry(() => File.Move(exeOld, exe));
                }
                catch { ok = false; }
            }
            if (dataMoved)
            {
                try
                {
                    if (Directory.Exists(data)) ForceDeleteDir(data);
                    WithRetry(() => Directory.Move(dataOld, data));
                }
                catch { ok = false; }
            }
            return ok;
        }

        // "<path>.old" — or, when a leftover of an earlier attempt is there and can't be removed,
        // "<path>.old-<ticks>".
        private static string FreeAside(string path, bool dir)
        {
            try
            {
                if (dir && Directory.Exists(path)) ForceDeleteDir(path);
                else if (!dir && File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            }
            catch { }
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            return path + "-" + DateTime.Now.Ticks;
        }

        // Remove Data.old* / MihomoLauncher.exe.old* left by an update. Best effort; also run at start.
        public static void CleanAside(string root)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(root, "Data.old*"))
                    try { ForceDeleteDir(d); } catch { }
                foreach (var f in Directory.GetFiles(root, "MihomoLauncher.exe.old*"))
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
            }
            catch { }
        }

        // The first file under `dir` (and `exe`) somebody holds open — opened exclusively to see.
        private static string FindLocked(string dir, string exe)
        {
            var files = new System.Collections.Generic.List<string>();
            try { if (dir != null && Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories)); } catch { }
            if (exe != null && File.Exists(exe)) files.Add(exe);
            foreach (var f in files)
            {
                try { using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                catch (IOException) { return f; }
                catch (UnauthorizedAccessException) { return f; }
                catch { }
            }
            return null;
        }

        // The path quoted in an IOException's message («…к файлу "D:\…\x.exe", так как…»), if it
        // is under the install root.
        private static string LockedIn(string message, string root)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message ?? "", "[\"«']([A-Za-z]:\\\\[^\"»']+)[\"»']");
            return m.Success && m.Groups[1].Value.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? m.Groups[1].Value : null;
        }

        private static void WithRetry(Action act)
        {
            for (int i = 0; i < 10; i++)
            {
                try { act(); return; }
                catch { Thread.Sleep(300); }
            }
            act();   // last try: let it throw
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

            // Tile icons downloaded by builds that cached them in Data\ico (up to 2.2.8): keep
            // them in DataUser\ico, where the cache lives now (IconFetcher) — otherwise the first
            // start after the update shows tiles without icons until it manages to re-download.
            // Shipped ones come along too; harmless, Data\ico is looked up first.
            var dataIco = Path.Combine(root, "Data", "ico");
            var userIco = Path.Combine(root, "DataUser", "ico");
            if (Directory.Exists(dataIco))
            {
                try
                {
                    Directory.CreateDirectory(userIco);
                    foreach (var f in Directory.GetFiles(dataIco, "*.png"))
                    {
                        var to = Path.Combine(userIco, Path.GetFileName(f));
                        if (!File.Exists(to)) try { File.Copy(f, to); } catch { }
                    }
                }
                catch { }
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
