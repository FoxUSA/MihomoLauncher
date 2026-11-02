using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace MihomoLauncher.Services
{
    public static class VpnMonitor
    {
        private static readonly List<ManagementEventWatcher> _processWatchers = new List<ManagementEventWatcher>();

        // Subscribe to Windows process-start events for the given image names
        // (without .exe extension). Callback fires on a WMI worker thread —
        // marshal to UI thread if it touches UI.
        public static void StartProcessWatchers(IEnumerable<string> processNames, Action onTargetProcessStarted)
        {
            StopProcessWatchers();
            if (processNames == null) return;
            foreach (var raw in processNames)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = raw.Trim();
                try
                {
                    var query = new WqlEventQuery(
                        "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = '" + name + ".exe'");
                    var watcher = new ManagementEventWatcher(query);
                    watcher.EventArrived += (s, e) =>
                    {
                        try { onTargetProcessStarted?.Invoke(); } catch { }
                    };
                    watcher.Start();
                    _processWatchers.Add(watcher);
                }
                catch
                {
                    // WMI can fail (perms, service down) — silently fall back to polling.
                }
            }
        }

        public static void StopProcessWatchers()
        {
            foreach (var w in _processWatchers)
            {
                try { w.Stop(); } catch { }
                try { w.Dispose(); } catch { }
            }
            _processWatchers.Clear();
        }

        // Cheap check used to skip the taskkill spawn when nothing is running.
        public static bool AnyRunning(IEnumerable<string> processNames)
        {
            if (processNames == null) return false;
            foreach (var raw in processNames)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Process[] procs = null;
                try
                {
                    procs = Process.GetProcessesByName(raw.Trim());
                    if (procs.Length > 0) return true;
                }
                catch { }
                finally
                {
                    if (procs != null)
                        foreach (var p in procs) { try { p.Dispose(); } catch { } }
                }
            }
            return false;
        }

        // Kill the listed apps IN PROCESS (Process.Kill), not by spawning taskkill.exe.
        // Same effect — we only ever kill our own user's processes, which needs no elevation —
        // but no child process at all. Spawning `taskkill /F /IM` from an unsigned binary is
        // one of the behaviours Defender's ML scores as trojan-like (Sabsik.FL.A!ml), and it
        // also cost a process spawn plus a 4s wait on every disconnect.
        public static void KillVpnDependentApps(IEnumerable<string> processNames)
        {
            if (processNames == null) return;
            foreach (var raw in processNames)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                Process[] procs;
                try { procs = Process.GetProcessesByName(raw.Trim()); }
                catch { continue; }

                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited) p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch { /* already gone, or not ours to kill */ }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
        }
    }
}
