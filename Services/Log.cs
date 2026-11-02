using System;
using System.IO;
using System.Text;

namespace MihomoLauncher.Services
{
    // Lightweight portable logger: writes next to the exe under DataUser\logs\ so the logs
    // travel with the one-folder install AND survive updates — an update wipes Data\ and the
    // exe but never touches DataUser\, and the log of a failed update is exactly what you need
    // afterwards. Files:
    //   crash.log  — fatal / unhandled exceptions (the ones that kill the process).
    //   app.log    — caught errors that would otherwise vanish + key startup milestones.
    //   update.log — the update applier's step log (written by UpdateApplier).
    // Best-effort: never throws, self-trims a file once it passes ~0.5 MB.
    public static class Log
    {
        private static readonly object _lock = new object();
        private const long MaxBytes = 512 * 1024;
        private static bool _migrated;

        public static string Dir => Path.Combine(VersionInfo.DataUserDir, "logs");

        // Where the logs used to live, before they were moved out of the update-wiped Data\.
        private static string LegacyDir => Path.Combine(AppContext.BaseDirectory, "Data", "logs");

        // Fatal: unhandled exception that crashed (or is about to crash) the process.
        public static void Crash(string source, Exception ex) => Write("crash.log", "CRASH/" + source, Render(ex));

        // A caught error that we recovered from but want a record of.
        public static void Error(string where, Exception ex) => Write("app.log", "ERROR/" + where, Render(ex));

        // A notable milestone (startup, engine state, update applied, …).
        public static void Info(string message) => Write("app.log", "INFO", message);

        // A row of the «Проблемы» tab (blocked / failed / DNS), kept after the launcher closes —
        // the window's history dies with the process, and "it blocked something last night" was
        // unanswerable. Separate file so the engine's chatter can't trim app.log away.
        public static void Problem(string message) => Write("problems.log", "PROBLEM", message);

        private static string Render(Exception ex) => ex != null ? ex.ToString() : "(no exception object)";

        private static void Write(string file, string tag, string body)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    MigrateLegacy();
                    var path = Path.Combine(Dir, file);
                    Trim(path);
                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + tag + "]  " + body + "\r\n";
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        // Carry the old Data\logs\ files over once, so the history from before the move isn't
        // stranded in a folder the next update deletes. Appends rather than overwrites: on a
        // machine that already logged to the new location, both halves stay readable in order.
        // Runs under _lock, at most once per process.
        private static void MigrateLegacy()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                var legacy = LegacyDir;
                if (!Directory.Exists(legacy) ||
                    string.Equals(legacy.TrimEnd('\\'), Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return;

                foreach (var src in Directory.GetFiles(legacy, "*.log"))
                {
                    var dest = Path.Combine(Dir, Path.GetFileName(src));
                    try
                    {
                        if (File.Exists(dest)) File.AppendAllText(dest, File.ReadAllText(src), new UTF8Encoding(false));
                        else File.Move(src, dest);
                        if (File.Exists(src)) File.Delete(src);
                    }
                    catch { }
                }
                // Only remove the old folder if nothing else was left in it.
                try { if (Directory.GetFileSystemEntries(legacy).Length == 0) Directory.Delete(legacy); } catch { }
            }
            catch { }
        }

        // Keep the log bounded: once it grows past MaxBytes, drop the older half.
        private static void Trim(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;
                var all = File.ReadAllText(path);
                File.WriteAllText(path, all.Substring(all.Length / 2), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
