using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MihomoLauncher.Services
{
    // Per-user autostart via HKCU\...\Run. Deliberately per-user (NOT a SYSTEM scheduled
    // task): the launcher must run in the logged-in user's session so it can see and
    // launch that user's Store apps. The stored command is always the CURRENT exe path,
    // so moving/updating the launcher just needs a re-enable (or the automatic SyncIfEnabled
    // at startup). Any other MihomoLauncher entries (old paths, leftover shortcuts) are removed.
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MihomoLauncher";

        // Passed on the autostart command line so the launcher knows it was started by Windows
        // at logon (as opposed to the user double-clicking it): it then starts hidden in the
        // tray and connects on its own. See App.OnStartup / MainWindow.StartHiddenInTray.
        public const string SilentArg = "--autostart";

        // The exact Run value we want: our exe, quoted, plus the silent-start flag.
        private static string Command() => "\"" + CurrentExePath() + "\" " + SilentArg;

        // Was this process started by the HKCU\Run entry?
        public static bool IsSilentLaunch(string[] args)
        {
            if (args == null) return false;
            foreach (var a in args)
            {
                var s = (a ?? "").TrimStart('-', '/');
                if (string.Equals(s, "autostart", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string CurrentExePath()
        {
            try
            {
                var p = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try { return Assembly.GetEntryAssembly()?.Location ?? ""; } catch { return ""; }
        }

        // Enabled = our Run value exists AND points to the exe we're running from.
        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k?.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v) && PathsEqual(StripQuotes(v), CurrentExePath());
                }
            }
            catch { return false; }
        }

        public static void Enable()
        {
            RemoveStaleRunEntries();   // drop old-path / duplicate MihomoLauncher Run values first
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    k?.SetValue(ValueName, Command());
                }
            }
            catch (Exception ex)
            {
                // Silence here is expensive: the machine simply comes up next time with no
                // launcher, therefore no VPN and no kill-switch, and the screen still shows
                // the autostart switch turned on.
                Log.Info("Автозапуск: не удалось прописать запуск при входе в систему — " + ex.Message);
            }
            CleanStartupShortcuts();   // we use the registry — remove any startup-folder copies
        }

        public static void Disable()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    if (k != null && k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex)
            {
                Log.Info("Автозапуск: не удалось убрать запуск при входе в систему — " + ex.Message);
            }
            RemoveStaleRunEntries();
            CleanStartupShortcuts();
        }

        // Called at startup: if autostart is on but the stored path is stale (launcher was
        // moved/updated), refresh it to the current location; otherwise just sweep away any
        // other MihomoLauncher autostart entries. No-op when autostart is off.
        public static void SyncIfEnabled()
        {
            try
            {
                string stored;
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    stored = k?.GetValue(ValueName) as string;

                if (string.IsNullOrEmpty(stored)) return;                 // off -> leave alone
                // Rewrite whenever the stored command isn't exactly what we'd write today:
                // a moved/updated launcher (stale path) OR an entry from a build before the
                // --autostart flag existed (right path, missing argument).
                if (!string.Equals(stored.Trim(), Command(), StringComparison.OrdinalIgnoreCase))
                    Enable();                                             // refresh + clean
                else
                {
                    RemoveStaleRunEntries();
                    CleanStartupShortcuts();
                }
            }
            catch { }
        }

        // Remove any Run value that launches a MihomoLauncher.exe other than our current one
        // (old versions/locations), plus any duplicate-named leftovers.
        private static void RemoveStaleRunEntries()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    if (k == null) return;
                    var cur = CurrentExePath();
                    foreach (var name in k.GetValueNames())
                    {
                        var data = k.GetValue(name) as string;
                        if (string.IsNullOrEmpty(data)) continue;
                        var path = StripQuotes(data);
                        bool isMom = path.IndexOf("MihomoLauncher", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                     path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                        if (!isMom) continue;
                        // Keep only our exact current entry.
                        if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase) &&
                            PathsEqual(path, cur))
                            continue;
                        try { k.DeleteValue(name, false); } catch { }
                    }
                }
            }
            catch { }
        }

        // Remove any *.lnk in the user's Startup folder that points to a MihomoLauncher.exe
        // (or is named MihomoLauncher) — these are old autostart shortcuts we no longer use.
        private static void CleanStartupShortcuts()
        {
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (!Directory.Exists(dir)) return;
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                {
                    var target = ResolveShortcutTarget(lnk);
                    bool isMom =
                        (!string.IsNullOrEmpty(target) &&
                         target.IndexOf("MihomoLauncher", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
                        Path.GetFileNameWithoutExtension(lnk)
                            .IndexOf("MihomoLauncher", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMom) { try { File.Delete(lnk); } catch { } }
                }
            }
            catch { }
        }

        // Resolve a .lnk target via WScript.Shell (late-bound, no COM reference needed).
        private static string ResolveShortcutTarget(string lnkPath)
        {
            object shell = null, sc = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "";
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                    new object[] { lnkPath });
                if (sc == null) return "";
                return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string ?? "";
            }
            catch { return ""; }
            finally
            {
                try { if (sc != null) Marshal.FinalReleaseComObject(sc); } catch { }
                try { if (shell != null) Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        private static string StripQuotes(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end > 0) return s.Substring(1, end - 1);
            }
            return s;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
