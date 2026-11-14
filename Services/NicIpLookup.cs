using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // "What address does the internet see through THIS card?" (2026-11-14) — for the stats plate
    // line under the tiles: each card's public IP with its city/country.
    //
    // Asked of api.byfox.dev/ip/me (the same server as the engine, geo and templates) over a socket
    // pinned to the card the same way the engine pins a «nic-» outbound: IP_UNICAST_IF + bind to
    // the card's own IPv4. That goes out of that card past our TUN and its routes — measured
    // 2026-11-14 with TUN + strict-route up: Ethernet → 93.180.133.144 (TR), Wi-Fi 2 →
    // 91.197.105.120 (Sochi), each its own answer.
    //
    // The server's address has to be the REAL one: system DNS gives a fake-ip (198.18.x) while the
    // engine runs, so those are dropped and the engine's own resolver (/dns/query) asked instead.
    // HTTP/1.0 on purpose — the answer comes whole, never chunked; one small JSON line.
    public static class NicIpLookup
    {
        private const string Host = "api.byfox.dev";
        private const string Path = "/ip/me";
        private const int IpUnicastIf = 31;

        public sealed class Result
        {
            public string Card = "";
            public string LocalIp = "";
            public bool Ok;
            public string Ip = "";
            public string City = "";          // English, as the API gives it
            public string Country = "";
            public string CountryCode = "";
            public string Org = "";
            public string Error = "";
            public DateTime At = DateTime.UtcNow;
        }

        // The card's IPv4 (not link-local) and its index, or null when it has none.
        public static Tuple<IPAddress, int> CardAddress(string cardName)
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Name, cardName, StringComparison.OrdinalIgnoreCase));
                if (nic == null || nic.OperationalStatus != OperationalStatus.Up) return null;
                var p = nic.GetIPProperties();
                var v4 = p.UnicastAddresses.Select(u => u.Address)
                          .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
                if (v4 == null) return null;
                return Tuple.Create(v4, p.GetIPv4Properties().Index);
            }
            catch { return null; }
        }

        private static bool IsFake(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b.Length == 4 && b[0] == 198 && (b[1] == 18 || b[1] == 19);
        }

        // Real IPv4 addresses of api.byfox.dev: system DNS minus fake-ip, else the engine's resolver.
        public static async Task<List<IPAddress>> ServerAddressesAsync(MihomoController engine, CancellationToken ct)
        {
            var list = new List<IPAddress>();
            try
            {
                var sys = await Dns.GetHostAddressesAsync(Host).ConfigureAwait(false);
                list.AddRange(sys.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IsFake(a)));
            }
            catch { }
            if (list.Count == 0 && engine != null)
            {
                try { list.AddRange((await engine.ResolveAsync(Host, ct).ConfigureAwait(false)).Where(a => !IsFake(a))); }
                catch { }
            }
            return list;
        }

        public static Task<Result> LookupAsync(string cardName, IList<IPAddress> servers, CancellationToken ct)
            => Task.Run(() => Lookup(cardName, servers, ct), ct);

        private static Result Lookup(string cardName, IList<IPAddress> servers, CancellationToken ct)
        {
            var r = new Result { Card = cardName };
            var addr = CardAddress(cardName);
            if (addr == null) { r.Error = "no address"; return r; }
            r.LocalIp = addr.Item1.ToString();
            if (servers == null || servers.Count == 0) { r.Error = "api.byfox.dev not resolved"; return r; }

            foreach (var server in servers)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var body = Get(addr.Item1, addr.Item2, server);
                    var o = ByfoxApi.ParseJson(body);
                    r.Ip = ByfoxApi.Str(o, "ip");
                    r.City = ByfoxApi.Str(o, "city");
                    r.Country = ByfoxApi.Str(o, "country");
                    r.CountryCode = ByfoxApi.Str(o, "country_code");
                    r.Org = ByfoxApi.Str(o, "org");
                    r.Ok = r.Ip.Length > 0;
                    r.Error = r.Ok ? "" : "empty answer";
                    if (r.Ok) return r;
                }
                catch (Exception ex) { r.Error = ex.Message; }
            }
            return r;
        }

        private static string Get(IPAddress local, int ifIndex, IPAddress server)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(ifIndex));
                s.Bind(new IPEndPoint(local, 0));
                var ar = s.BeginConnect(server, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(8)))
                    throw new TimeoutException("connect timeout");
                s.EndConnect(ar);
                s.ReceiveTimeout = 10000; s.SendTimeout = 10000;

                using (var ns = new NetworkStream(s, false))
                using (var ssl = new SslStream(ns, false))
                {
                    ssl.ReadTimeout = 10000; ssl.WriteTimeout = 10000;
                    ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12, false);
                    var req = "GET " + Path + " HTTP/1.0\r\nHost: " + Host + "\r\nUser-Agent: " + ByfoxApi.UserAgent
                            + "\r\nAccept: application/json\r\nConnection: close\r\n\r\n";
                    var b = Encoding.ASCII.GetBytes(req);
                    ssl.Write(b, 0, b.Length);
                    ssl.Flush();
                    var ms = new MemoryStream();
                    var buf = new byte[4096];
                    int n;
                    while ((n = ssl.Read(buf, 0, buf.Length)) > 0 && ms.Length < 64 * 1024) ms.Write(buf, 0, n);
                    var resp = Encoding.UTF8.GetString(ms.ToArray());
                    int split = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) throw new IOException("bad answer");
                    var status = resp.Substring(0, resp.IndexOf("\r\n", StringComparison.Ordinal));
                    if (status.IndexOf(" 200", StringComparison.Ordinal) < 0) throw new IOException(status.Trim());
                    return resp.Substring(split + 4);
                }
            }
        }
    }
}
