using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MihomoLauncher.Services
{
    // Who started whom on this machine, and who talks to the internet (2026-11-22, for the rules'
    // auto-tracking — AutoPrograms decides, this only watches).
    //
    // A background thread takes a Toolhelp32 snapshot every 2 s (pid, parent pid, exe name — a
    // few milliseconds, no rights needed). A pid not seen before gets its full path
    // (QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION, which a plain user has for
    // its own processes) and its creation time. No WMI: slow, and its process-start events need
    // admin — the launcher always runs as the plain user.
    //
    // Exited processes are kept for 10 minutes, so a helper can still be tied to a parent that is
    // already gone (a launcher stub that starts the real app and quits at once). Pid reuse is
    // guarded the usual way: a parent must have started before its child.
    //
    // Network, without the engine: GetExtendedTcpTable (owner pid) — a TCP connection to a
    // non-loopback address marks the process as one that went online (sticky). Works while the
    // engine is down, so a rule is ready before the person connects. Loopback-only talk (language
    // servers, local IPC) does not count.
    public sealed class ProcessTracker : IDisposable
    {
        public sealed class Proc
        {
            public int Pid, ParentPid;
            public string Name = "";     // image name from the snapshot ("Code.exe")
            public string Path;          // full path, null when it can't be read (other user / elevated)
            public DateTime Created;     // UTC, MinValue when unknown
            public bool Alive = true;
            public DateTime ExitedAt;
            public bool Net;             // had a TCP connection to a non-loopback address

            public Proc Copy() => (Proc)MemberwiseClone();
        }

        private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan KeepExited = TimeSpan.FromMinutes(10);

        private readonly object _lock = new object();
        private readonly Dictionary<int, Proc> _alive = new Dictionary<int, Proc>();
        private readonly List<Proc> _exited = new List<Proc>();
        private Thread _thread;
        private volatile bool _stop;
        private volatile object _gen;   // the running loop's token: a restarted tracker never runs two loops
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private bool _loggedError;

        public bool Running => _thread != null && _thread.IsAlive && !_stop;

        public void Start()
        {
            if (Running) return;
            _stop = false;
            var gen = new object();
            _gen = gen;
            _thread = new Thread(() => Loop(gen)) { IsBackground = true, Name = "ProcessTracker", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Log.Info("Авто-добавление: слежение за процессами включено.");
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop = true;
            _gen = null;
            try { _wake.Set(); } catch { }
            _thread = null;
            lock (_lock) { _alive.Clear(); _exited.Clear(); }
            Log.Info("Авто-добавление: слежение за процессами выключено (нет правил, за которыми следить).");
        }

        public void Dispose() { Stop(); }

        // Everything known right now, alive and recently exited — copies, safe to read anywhere.
        public List<Proc> Snapshot()
        {
            lock (_lock)
            {
                var list = new List<Proc>(_alive.Count + _exited.Count);
                foreach (var p in _alive.Values) list.Add(p.Copy());
                foreach (var p in _exited) list.Add(p.Copy());
                return list;
            }
        }

        private void Loop(object gen)
        {
            while (!_stop && ReferenceEquals(_gen, gen))
            {
                try { Pass(); }
                catch (Exception ex)
                {
                    if (!_loggedError) { _loggedError = true; Log.Error("process-tracker", ex); }
                }
                try { _wake.WaitOne(Period); } catch { return; }
            }
        }

        private void Pass()
        {
            var now = DateTime.UtcNow;
            var snap = TakeSnapshot();
            if (snap == null) return;
            var net = TcpOwners();

            lock (_lock)
            {
                var present = new HashSet<int>();
                foreach (var s in snap)
                {
                    present.Add(s.Pid);
                    Proc known;
                    // Same pid, same image, same parent → the same process (a parent pid never
                    // changes during a process's life). Anything else is a new one on a reused pid.
                    if (_alive.TryGetValue(s.Pid, out known)
                        && known.ParentPid == s.ParentPid
                        && string.Equals(known.Name, s.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (net.Contains(s.Pid)) known.Net = true;
                        continue;
                    }
                    if (known != null) Retire(known, now);
                    var p = new Proc { Pid = s.Pid, ParentPid = s.ParentPid, Name = s.Name };
                    Describe(p);
                    if (net.Contains(s.Pid)) p.Net = true;
                    _alive[s.Pid] = p;
                }
                foreach (var pid in _alive.Keys.Where(k => !present.Contains(k)).ToList())
                {
                    Retire(_alive[pid], now);
                    _alive.Remove(pid);
                }
                _exited.RemoveAll(p => now - p.ExitedAt > KeepExited);
            }
        }

        private void Retire(Proc p, DateTime now)
        {
            p.Alive = false;
            p.ExitedAt = now;
            _exited.Add(p);
        }

        // ---------- Toolhelp32 ----------

        private struct Snap { public int Pid, ParentPid; public string Name; }

        private static List<Snap> TakeSnapshot()
        {
            var h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            try
            {
                var list = new List<Snap>(400);
                var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                if (!Process32FirstW(h, ref e)) return list;
                do
                {
                    if (e.th32ProcessID != 0)
                        list.Add(new Snap { Pid = (int)e.th32ProcessID, ParentPid = (int)e.th32ParentProcessID, Name = e.szExeFile ?? "" });
                    e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                } while (Process32NextW(h, ref e));
                return list;
            }
            finally { CloseHandle(h); }
        }

        // Full path + creation time. Fails quietly for what a plain user may not open.
        private static void Describe(Proc p)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h == IntPtr.Zero) return;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size)) p.Path = sb.ToString(0, size);
                long c, x, k, u;
                if (GetProcessTimes(h, out c, out x, out k, out u) && c > 0)
                {
                    try { p.Created = DateTime.FromFileTimeUtc(c); } catch { }
                }
            }
            finally { CloseHandle(h); }
        }

        // ---------- TCP owners ----------

        // Pids with a TCP connection (connecting, established or closing) to a non-loopback address.
        private static HashSet<int> TcpOwners()
        {
            var set = new HashSet<int>();
            try { ReadTcp(AF_INET, set); } catch { }
            try { ReadTcp(AF_INET6, set); } catch { }
            return set;
        }

        private static void ReadTcp(int family, HashSet<int> set)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                size += 4096;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int rc = GetExtendedTcpTable(buf, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (rc != 0) return;
                    int n = Marshal.ReadInt32(buf);
                    var p = buf + 4;
                    if (family == AF_INET)
                    {
                        // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                        for (int i = 0; i < n; i++, p += 24)
                        {
                            int state = Marshal.ReadInt32(p);
                            uint remote = (uint)Marshal.ReadInt32(p + 12);
                            int pid = Marshal.ReadInt32(p + 20);
                            if (pid <= 4 || !Counts(state)) continue;
                            byte first = (byte)(remote & 0xFF);
                            if (remote == 0 || first == 127) continue;   // loopback / nothing
                            set.Add(pid);
                        }
                    }
                    else
                    {
                        // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16],
                        // remoteScope, remotePort, state, pid — 56 bytes.
                        var addr = new byte[16];
                        for (int i = 0; i < n; i++, p += 56)
                        {
                            Marshal.Copy(p + 24, addr, 0, 16);
                            int state = Marshal.ReadInt32(p + 48);
                            int pid = Marshal.ReadInt32(p + 52);
                            if (pid <= 4 || !Counts(state)) continue;
                            var ip = new IPAddress(addr);
                            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) continue;
                            if (ip.IsIPv4MappedToIPv6 && ip.MapToIPv4().GetAddressBytes()[0] == 127) continue;
                            set.Add(pid);
                        }
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }

        // MIB_TCP_STATE: SYN_SENT 3 … LAST_ACK 10, without LISTEN (2) and SYN_RCVD (4, someone else
        // connecting to us); TIME_WAIT has no owner any more.
        private static bool Counts(int state) => state == 3 || (state >= 5 && state <= 10);

        // ---------- interop ----------

        private const uint TH32CS_SNAPPROCESS = 0x2;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int AF_INET = 2, AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
    }
}
