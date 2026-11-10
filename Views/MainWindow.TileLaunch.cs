using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // Launching from a tile, with feedback. A program can take many seconds to show a window,
    // and with nothing on screen in between the click looked lost — so it got clicked again,
    // ten times over, and ten copies started («мама тыкает в плитку десяток раз»). Now the
    // tile spins from the moment the launch went out until the program shows up, and ignores
    // clicks meanwhile.
    public partial class MainWindow
    {
        // Rules whose tile is launching right now (spinner on, clicks ignored).
        private readonly HashSet<string> _launchingIds = new HashSet<string>();

        // Long enough to be seen at all; short enough not to be in the way.
        private static readonly TimeSpan LaunchSpinMin = TimeSpan.FromSeconds(1.2);
        // A program that is slower than this is still starting, but a spinner that never stops
        // would only look like a hang. The click lock goes with it.
        private static readonly TimeSpan LaunchSpinMax = TimeSpan.FromSeconds(25);

        private async Task LaunchFromTileAsync(AppEntry entry)
        {
            if (entry == null || _launchingIds.Contains(entry.Id)) return;

            RepairProgramPaths("запуск", force: true);
            var names = AppLauncher.LaunchImageNames(entry);
            var before = WindowedPids(names);

            var res = AppLauncher.Launch(entry);
            if (!res.Item1)
            {
                await ShowDialog(Loc.T("Main_OpenFailTitle"), Loc.T("Main_OpenFailBody", entry.Name, res.Item2)).ConfigureAwait(true);
                return;
            }

            _launchingIds.Add(entry.Id);
            StatusFoot.Text = Loc.T("Main_FootLaunching", entry.Name);
            RebuildTiles();
            try
            {
                await WaitForLaunchedAsync(names, before).ConfigureAwait(true);
            }
            catch (Exception ex) { Log.Error("tile-launch-wait", ex); }
            finally
            {
                _launchingIds.Remove(entry.Id);
                if (StatusFoot.Text == Loc.T("Main_FootLaunching", entry.Name)) StatusFoot.Text = "";
                RebuildTiles();
            }
        }

        // Done when something of the launch is on screen:
        //  * the foreground went to another process — a new window takes the foreground from us
        //    (we were foreground at the click, so Windows lets it), and so does an already
        //    running program that the launch merely brought forward, and a browser opening a site;
        //  * or the program has a window it did not have before — for one that opens behind.
        private async Task WaitForLaunchedAsync(List<string> names, HashSet<int> before)
        {
            var started = DateTime.UtcNow;
            int ownPid = Process.GetCurrentProcess().Id;
            while (DateTime.UtcNow - started < LaunchSpinMax)
            {
                await Task.Delay(250).ConfigureAwait(true);
                if (DateTime.UtcNow - started < LaunchSpinMin) continue;

                int fgPid = ForegroundPid();
                if (fgPid != 0 && fgPid != ownPid) return;
                if (WindowedPids(names).Any(pid => !before.Contains(pid))) return;
            }
        }

        // Ids of the target programs' processes that currently have a main window.
        private static HashSet<int> WindowedPids(List<string> names)
        {
            var set = new HashSet<int>();
            foreach (var n in names ?? new List<string>())
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(n); } catch { continue; }
                foreach (var p in procs)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero) set.Add(p.Id); }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            return set;
        }

        private static int ForegroundPid()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return 0;
                GetWindowThreadProcessId(hwnd, out uint pid);
                return (int)pid;
            }
            catch { return 0; }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
