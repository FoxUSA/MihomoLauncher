using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MihomoLauncher.Services
{
    // Which processes have an application window of their own right now (2026-11-23, for the
    // rules' auto-tracking: a program with its own window is a program, not a helper).
    //
    // "Application window" = what Windows itself puts on the taskbar / in Alt+Tab (Raymond Chen,
    // «Which windows appear in the Alt+Tab list?»): top-level, visible, no owner or WS_EX_APPWINDOW,
    // not WS_EX_TOOLWINDOW, not cloaked (another virtual desktop, a suspended UWP frame), with a
    // title. Console windows do not count: a helper's console is not a window of its own.
    // One EnumWindows per call; never throws.
    public static class AppWindows
    {
        public static HashSet<int> Pids()
        {
            var set = new HashSet<int>();
            try
            {
                var cls = new StringBuilder(256);
                EnumWindowsProc cb = (h, l) =>
                {
                    try { if (IsAppWindow(h, cls)) { uint pid; GetWindowThreadProcessId(h, out pid); if (pid != 0) set.Add((int)pid); } }
                    catch { }
                    return true;
                };
                EnumWindows(cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            catch { }
            return set;
        }

        private static bool IsAppWindow(IntPtr h, StringBuilder cls)
        {
            if (!IsWindowVisible(h)) return false;
            int ex = GetWindowLongW(h, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
            // InternalGetWindowText reads the title from the kernel: it never sends a message to (and so
            // never waits on) another process — GetWindowTextLength would, and a hung window would hang us.
            var title = new StringBuilder(2);
            if (InternalGetWindowText(h, title, title.Capacity) <= 0) return false;
            try
            {
                int cloaked;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            }
            catch { }
            cls.Clear();
            if (GetClassNameW(h, cls, cls.Capacity) > 0)
            {
                var c = cls.ToString();
                if (c == "ConsoleWindowClass" || c == "PseudoConsoleWindow") return false;
            }
            return true;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const uint GW_OWNER = 4;
        private const int DWMWA_CLOAKED = 14;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    }
}
