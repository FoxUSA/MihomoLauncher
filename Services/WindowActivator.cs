using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MihomoLauncher.Services
{
    // A tile click on a program that is already running brings its window forward instead of
    // starting a second copy (2026-11-25). The bring-forward steps are the ones FoxSwitch
    // settled on (WindowTracker.Activate): restore, attach to the foreground thread, raise,
    // SetForegroundWindow, an empty input event if Windows refused, and Alt+Tab-style
    // SwitchToThisWindow as the last resort (that one also reaches windows of programs run
    // as administrator).
    //
    // Only a VISIBLE window counts (on screen or minimized to the taskbar). A program hidden
    // in the tray (µTorrent, Telegram) has none — then the caller starts the exe as before,
    // and such programs show their own window when started a second time.
    public static class WindowActivator
    {
        // true — a window of a running copy of exePath was found and brought forward.
        public static bool TryActivateRunning(string exePath)
        {
            try
            {
                var full = Normalize(exePath);
                if (full.Length == 0 || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

                var pids = new HashSet<uint>();
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full)))
                {
                    try
                    {
                        var img = Normalize(ImagePath((uint)p.Id));
                        if (string.Equals(img, full, StringComparison.OrdinalIgnoreCase)) pids.Add((uint)p.Id);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (pids.Count == 0) return false;

                var hwnd = TopWindowOf(pids);
                if (hwnd == IntPtr.Zero) return false;

                Activate(hwnd);
                Log.Info("Плитка: «" + Path.GetFileName(full) + "» уже запущена — вывели её окно вперёд вместо второго запуска.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("Плитка: проверка запущенной программы не удалась (" + ex.Message + ") — запускаю как обычно.");
                return false;
            }
        }

        private static string Normalize(string path)
        {
            try
            {
                var p = (path ?? "").Trim().Trim('"');
                return p.Length == 0 ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(p));
            }
            catch { return ""; }
        }

        // Works for programs run as administrator too (limited-information access).
        private static string ImagePath(uint pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int len = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, len) : "";
            }
            finally { CloseHandle(h); }
        }

        // EnumWindows walks top-down in Z-order, so the first match is the one used last.
        private static IntPtr TopWindowOf(HashSet<uint> pids)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains(pid)) return true;
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindowTextLength(h) == 0 || IsCloaked(h)) return true;
                if (!IsIconic(h) && GetWindowRect(h, out var r) && (r.Right - r.Left < 50 || r.Bottom - r.Top < 50)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsCloaked(IntPtr h)
        {
            try { return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0; }
            catch { return false; }
        }

        private static void Activate(IntPtr hwnd)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                // a program run as administrator ignores ShowWindow from us; Alt+Tab-style switching gets through
                if (IsIconic(hwnd)) SwitchToThisWindow(hwnd, true);
            }

            // Windows hands the foreground over more willingly from the thread that holds it.
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var ownThread = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != ownThread && AttachThreadInput(ownThread, fgThread, true);
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                BringWindowToTop(hwnd);
                if (!SetForegroundWindow(hwnd) || GetForegroundWindow() != hwnd)
                {
                    // "the last input wasn't ours" lock — an empty input event lifts it
                    SendInput(1, new[] { new INPUT() }, Marshal.SizeOf(typeof(INPUT)));
                    SetForegroundWindow(hwnd);
                }
                if (GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
                if (GetForegroundWindow() != hwnd) Log.Info("Плитка: Windows не дала вывести окно на передний план.");
            }
            finally
            {
                if (attached) AttachThreadInput(ownThread, fgThread, false);
            }
        }

        // ---------- Win32 ----------

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint GW_OWNER = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_CLOAKED = 14;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // An empty mouse input (type 0, all zero) — the size must match the real INPUT union.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;   // largest union member; nested so it lands at offset 8 on x64
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    }
}
