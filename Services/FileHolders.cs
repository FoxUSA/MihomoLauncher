using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MihomoLauncher.Services
{
    // Who holds a file open — Windows' Restart Manager, the same thing that names the program in
    // Explorer's «файл открыт в …». For the updater's «файл занят программой …» (2026-11-16).
    public static class FileHolders
    {
        // "Служба MihomoLauncherEngine (service.exe)", "Dropbox", … joined; "" when unknown.
        public static string Describe(string path)
        {
            try
            {
                var names = Holders(path);
                return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
            }
            catch { return ""; }
        }

        private static List<string> Holders(string path)
        {
            var result = new List<string>();
            uint session;
            var key = new StringBuilder(CchRmSessionKey + 1);
            if (RmStartSession(out session, 0, key) != 0) return result;
            try
            {
                if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return result;
                uint needed = 0, count = 0, reasons = 0;
                int rc = RmGetList(session, out needed, ref count, null, ref reasons);
                if (rc != ErrorMoreData || needed == 0) return result;
                var infos = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    var app = (infos[i].strAppName ?? "").Trim();
                    var svc = (infos[i].strServiceShortName ?? "").Trim();
                    string exe = "";
                    try { using (var p = System.Diagnostics.Process.GetProcessById(infos[i].Process.dwProcessId)) exe = p.ProcessName + ".exe"; } catch { }
                    var name = svc.Length > 0 ? Loc.T("Upd_HolderService", svc) : app.Length > 0 ? app : exe;
                    if (exe.Length > 0 && !name.Equals(exe, StringComparison.OrdinalIgnoreCase)) name += " (" + exe + ")";
                    if (name.Length > 0) result.Add(name);
                }
            }
            finally { RmEndSession(session); }
            return result;
        }

        private const int CchRmSessionKey = 32;
        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
            uint nApplications, RmUniqueProcess[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RmProcessInfo[] rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
