using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MihomoLauncher.Services
{
    // Who is sitting on a local TCP port, and where to move if it's not us.
    //
    // The engine's control port (external-controller, 9090 by default) is the launcher's ONLY
    // way to steer mihomo — and 9090 is the default for every clash-family client, so it is
    // exactly the port another such program grabs. When it's taken, mihomo does NOT stop: it
    // logs "address already in use", keeps running (TUN and all) and simply never answers us.
    // From the launcher's side that looks identical to "the engine won't start", and restarting
    // the task can't fix it — the squatter isn't ours to end, and killing an elevated process
    // from a non-admin launcher isn't possible either. Moving OUR port is the one cure that
    // needs no rights and no user action; the scheduled task passes only -d "<dir>", so the
    // number lives in config.yaml alone and a rewrite + restart is enough.
    public static class PortGuard
    {
        private const int AfInet = 2;
        private const int TcpTableOwnerPidListener = 3;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
            bool sort, int ipVersion, int tblClass, int reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        // "127.0.0.1:9090" -> host + port. False if it isn't a usable address.
        public static bool SplitAddr(string addr, out string host, out int port)
        {
            host = ""; port = 0;
            var s = (addr ?? "").Trim();
            var i = s.LastIndexOf(':');
            if (i <= 0 || i == s.Length - 1) return false;
            if (!int.TryParse(s.Substring(i + 1), out port) || port <= 0 || port > 65535) return false;
            host = s.Substring(0, i);
            return true;
        }

        // PID listening on this TCP port right now, or 0 if the port is free. A listener on
        // 0.0.0.0 counts: it blocks our 127.0.0.1 bind just the same.
        public static int ListenerPid(int port)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (size <= 0) return 0;
                buf = Marshal.AllocHGlobal(size);
                if (GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
                    return 0;

                int count = Marshal.ReadInt32(buf);
                var rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                var p = new IntPtr(buf.ToInt64() + 4);
                for (int n = 0; n < count; n++)
                {
                    var row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(p, typeof(MibTcpRowOwnerPid));
                    // dwLocalPort keeps the port in network byte order in its low word.
                    int rowPort = (int)(((row.localPort & 0xFF) << 8) | ((row.localPort >> 8) & 0xFF));
                    if (rowPort == port) return (int)row.owningPid;
                    p = new IntPtr(p.ToInt64() + rowSize);
                }
                return 0;
            }
            catch (Exception ex) { try { Log.Error("port-owner", ex); } catch { } return 0; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        // Full image path of a process, via WMI — which a standard user can read even for an
        // ELEVATED process, where Process.MainModule throws Access Denied. That distinction is
        // the whole point: our own engine runs elevated, so the name alone ("mihomo-windows-
        // amd64") can't tell our leftover apart from another client shipping the same GitHub
        // asset, and only the path can. Empty when it can't be read — treat that as "not ours".
        public static string ImagePath(int pid)
        {
            if (pid <= 0) return "";
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                           "SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = " + pid))
                using (var results = searcher.Get())
                    foreach (System.Management.ManagementObject mo in results)
                        using (mo)
                            return (mo["ExecutablePath"] as string) ?? "";
            }
            catch (Exception ex) { try { Log.Error("port-image-path", ex); } catch { } }
            return "";
        }

        // Process name for a PID ("mihomo-windows-amd64"), or "" if it's gone. Unlike the image
        // path, this IS readable for an elevated process — which makes it the only identifying
        // detail available in the case that matters.
        public static string ProcessNameOf(int pid)
        {
            if (pid <= 0) return "";
            try { return Process.GetProcessById(pid).ProcessName; }
            catch { return ""; }
        }

        // Log-ready "name (PID n)" for whoever holds the port, or "" when nobody does.
        // The name comes from the process table (readable for everyone) rather than from
        // opening the process, which a non-admin can't do for an elevated one.
        public static string DescribeListener(int port)
        {
            int pid = ListenerPid(port);
            if (pid <= 0) return "";
            string name = "?";
            try { name = Process.GetProcessById(pid).ProcessName; }
            catch { }
            return name + " (PID " + pid + ")";
        }

        // Can we actually bind this port on loopback? Cheaper and more honest than the table
        // scan for the "is it free" question — it also catches exclusive reservations.
        public static bool IsFree(int port)
        {
            TcpListener l = null;
            try
            {
                l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                return true;
            }
            catch { return false; }
            finally { try { if (l != null) l.Stop(); } catch { } }
        }

        // A free port to move the control API to. Walks up from the one we're on (skipping
        // ports another clash-family client would want), then falls back to whatever the OS
        // hands out. Returns 0 if even that fails.
        public static int PickFree(int currentPort, int alsoAvoid = 0)
        {
            var avoid = new HashSet<int> { currentPort, alsoAvoid, 7890, 7891, 7892, 7893, 9090, 9091, 9097 };
            for (int p = 9500; p < 9600; p++)
            {
                if (avoid.Contains(p)) continue;
                if (IsFree(p)) return p;
            }
            TcpListener l = null;
            try
            {
                l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                return ((IPEndPoint)l.LocalEndpoint).Port;
            }
            catch { return 0; }
            finally { try { if (l != null) l.Stop(); } catch { } }
        }
    }
}
