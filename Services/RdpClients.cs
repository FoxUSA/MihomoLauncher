using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace MihomoLauncher.Services
{
    // Active Remote Desktop (RDP) peer IPs, as /32 (or /128) CIDRs. Used to keep a live RDP
    // session OUT of the mihomo TUN route table — otherwise TUN's auto-route captures the
    // session's return traffic and the remote desktop keeps dropping while the launcher
    // runs. The RDP listening port is read from the registry (admin-configurable, not always
    // 3389). Cached briefly so a connect burst (many config rebuilds) doesn't respawn netstat.
    public static class RdpClients
    {
        private static readonly object _lock = new object();
        private static List<string> _cache;
        private static DateTime _cacheAtUtc = DateTime.MinValue;
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

        public static List<string> ActivePeerCidrs()
        {
            lock (_lock)
            {
                if (_cache != null && (DateTime.UtcNow - _cacheAtUtc) < Ttl) return _cache;
            }
            var result = Detect();
            lock (_lock) { _cache = result; _cacheAtUtc = DateTime.UtcNow; }
            return result;
        }

        private static List<string> Detect()
        {
            var result = new List<string>();
            try
            {
                var port = RdpListenPort().ToString();
                var psi = new ProcessStartInfo("netstat", "-n")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    foreach (var raw in outp.Split('\n'))
                    {
                        var line = raw.Trim();
                        // "TCP  <local>:<rdpPort>  <remote>:<port>  ESTABLISHED"
                        if (line.IndexOf("ESTABLISHED", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length < 3) continue;
                        if (!EndsWithPort(cols[1], port)) continue;   // local side = the RDP listener
                        var cidr = Cidr(cols[2]);
                        if (cidr != null) result.Add(cidr);
                    }
                }
            }
            catch { }
            return result.Distinct().ToList();
        }

        // The actual RDP listening port (admins can change it from the default 3389):
        // HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp\PortNumber.
        private static int RdpListenPort()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"))
                {
                    if (k != null && k.GetValue("PortNumber") is int p && p > 0 && p <= 65535)
                        return p;
                }
            }
            catch { }
            return 3389;
        }

        private static bool EndsWithPort(string endpoint, string port)
        {
            int i = endpoint.LastIndexOf(':');
            return i > 0 && endpoint.Substring(i + 1) == port;
        }

        // "203.0.113.7:54321" -> "203.0.113.7/32"; "[fe80::1]:54321" -> "fe80::1/128".
        private static string Cidr(string endpoint)
        {
            int i = endpoint.LastIndexOf(':');
            if (i <= 0) return null;
            var host = endpoint.Substring(0, i).Trim('[', ']');
            int zone = host.IndexOf('%');                 // "fe80::1%12": a zone is not allowed
            if (zone > 0) host = host.Substring(0, zone);  // in a prefix, and the engine rejects it
            if (!IPAddress.TryParse(host, out var ip)) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            ip.ScopeId = 0;
            return ip + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");
        }
    }
}
