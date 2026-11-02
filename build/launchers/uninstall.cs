using System;
using System.Diagnostics;
using System.IO;

// Tiny portable launcher: starts the WPF uninstaller (MihomoLauncher.exe --uninstall), which
// shows a progress window with a per-step checklist. Built at Release-layout time with
// trash.ico embedded (csc /win32icon), so it carries its own icon and works wherever the
// folder lands (uses the exe's own directory, no absolute paths). Falls back to the
// Data\uninstall.bat console if MihomoLauncher.exe is missing.
static class UninstallLauncher
{
    [STAThread]
    static void Main()
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string exe = Path.Combine(dir, "MihomoLauncher.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--uninstall", WorkingDirectory = dir });
                return;
            }
            string bat = Path.Combine(dir, "Data", "uninstall.bat");
            Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, WorkingDirectory = dir });
        }
        catch { }
    }
}
