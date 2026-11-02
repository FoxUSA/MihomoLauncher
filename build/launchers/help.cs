using System;
using System.Diagnostics;
using System.IO;

// Tiny portable launcher: opens Data\help.html next to this exe. Built at Release-layout time
// with help.ico embedded (csc /win32icon), so it carries its own icon and works wherever the
// folder lands (uses the exe's own directory, no absolute paths).
static class HelpLauncher
{
    [STAThread]
    static void Main()
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string target = Path.Combine(dir, "Data", "help.html");
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }
}
