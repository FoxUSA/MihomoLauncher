using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// MihomoLauncher.exe - the launcher's name up to 3.1.0. Since 2026-11-19 the launcher is
// "Mihomo Launcher.exe", and this tiny forwarder keeps the old name working:
//   - a 3.1.0 updater looks for MihomoLauncher.exe in update.zip and, when done, starts
//     <root>\MihomoLauncher.exe - so every update.zip carries this file (make-zips.bat);
//   - old HKCU\Run values, shortcuts, taskbar pins, the engine service's --launcher and the
//     pre-service firewall task name it.
// It starts "Mihomo Launcher.exe" beside it with the same arguments. --firewall-sync /
// --firewall-clear (run as SYSTEM by the service or the task) are waited for and their exit
// code passed on; anything else is started and left to run. The launcher hides this file.
// Built by layout-release.ps1 into bin\Release\legacy\ - NOT shipped in the full archive.
static class LegacyForwarder
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(dir, "Mihomo Launcher.exe");
            if (!File.Exists(exe)) return 2;

            var line = new StringBuilder();
            foreach (var a in args)
            {
                if (line.Length > 0) line.Append(' ');
                line.Append(Quote(a));
            }

            bool wait = args.Length > 0 &&
                (string.Equals(args[0], "--firewall-sync", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "--firewall-clear", StringComparison.OrdinalIgnoreCase));

            if (wait)
            {
                var psi = new ProcessStartInfo(exe, line.ToString())
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return 1;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }

            // The user (or Windows at logon) started us: the launcher's window may come forward.
            try { AllowSetForegroundWindow(-1); } catch { }
            Process.Start(new ProcessStartInfo(exe, line.ToString()) { WorkingDirectory = dir, UseShellExecute = true });
            return 0;
        }
        catch { return 1; }
    }

    private static string Quote(string a)
    {
        a = a ?? "";
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }
}
