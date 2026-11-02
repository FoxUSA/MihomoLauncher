using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    public enum DnsProto { Plain, Tcp, DoT, DoH, DoH3, DoQ }

    // One resolver the user typed, recognised and rewritten into the form the engine accepts.
    public sealed class DnsEntry
    {
        public string Raw = "";        // as typed
        public bool Ok;
        public string Error;           // why it can't be used (null when Ok)
        public DnsProto Proto;
        public string Engine = "";     // what goes into config.yaml
        public string Host = "";       // server name or address
        public bool IsIp;              // addressed by IP → needs no bootstrap resolver
        public bool IsIPv6;
        public string Provider;        // "NextDNS · профиль abc123", "Cloudflare", null
        public string Warning;         // usable, but worth a word
    }

    // «DNS для прямого трафика» — the resolver the engine uses ONLY for connections it sends out
    // DIRECT (mihomo's `direct-nameserver`).
    //
    // Why only those. The DNS feature removed on 2026-08-15 replaced the engine's `nameserver`,
    // and that one answers far more than "sites outside the VPN": the names Windows asks for
    // through fake-ip-filter, the lookups made to match IP rules, the VPN nodes' own addresses.
    // A DoH host the ISP throttles therefore stalled VPN traffic too — the "conflict" between the
    // two. `direct-nameserver` touches none of that: `nameserver` stays the machine's own, and the
    // chosen resolver is asked only when a connection has already been routed DIRECT. Verified on
    // the v1.19.33 binary: with a dead direct-nameserver a DIRECT connection fails with
    // «dns resolve failed: couldn't find ip» while /dns/query (the main resolver) keeps answering.
    public static class DirectDns
    {
        // ---------- recognising what was typed ----------

        // Everything in a pasted text — NextDNS's setup page, a comma list, one per line.
        public static List<DnsEntry> ParseMany(string text)
        {
            var list = new List<DnsEntry>();
            foreach (var tok in (text ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var e = Parse(tok);
                if (e != null) list.Add(e);
            }
            return list;
        }

        public static DnsEntry Parse(string token)
        {
            var t = (token ?? "").Trim().Trim('"', '\'', '<', '>');
            if (t.Length == 0) return null;
            var e = new DnsEntry { Raw = t };
            try { Recognise(e, t, 0); }
            catch { Fail(e, Loc.T("Dns_ErrUnknown")); }
            if (e.Ok) Label(e);
            return e;
        }

        private static void Fail(DnsEntry e, string why) { e.Ok = false; e.Error = why; }

        private static void Recognise(DnsEntry e, string t, int depth)
        {
            var lower = t.ToLowerInvariant();
            if (lower.StartsWith("sdns://")) { FromStamp(e, t.Substring(7), depth); return; }

            string scheme = null, rest = t;
            int sep = t.IndexOf("://", StringComparison.Ordinal);
            if (sep > 0) { scheme = lower.Substring(0, sep); rest = t.Substring(sep + 3); }

            switch (scheme)
            {
                case "https":
                case "doh":
                    DoH(e, rest, h3: Regex.IsMatch(rest, @"#.*h3=true", RegexOptions.IgnoreCase)); return;
                case "h3":
                case "doh3":
                    DoH(e, rest, h3: true); return;
                case "tls":
                case "dot":
                    HostOnly(e, rest, DnsProto.DoT, "tls://"); return;
                case "quic":
                case "doq":
                    HostOnly(e, rest, DnsProto.DoQ, "quic://"); return;
                case "udp":
                case "dns":
                    HostOnly(e, rest, DnsProto.Plain, ""); return;
                case "tcp":
                    HostOnly(e, rest, DnsProto.Tcp, "tcp://"); return;
                case null:
                    break;
                default:
                    Fail(e, Loc.T("Dns_ErrScheme", scheme)); return;
            }

            // No scheme: decide by the shape.
            IPAddress ip; int port;
            if (TrySplitAddress(t, out ip, out port))
            {
                SetAddress(e, ip, port, DnsProto.Plain, "");
                return;
            }
            // A NextDNS profile id on its own — what its dashboard shows in big letters.
            if (Regex.IsMatch(t, "^[0-9a-f]{6}$", RegexOptions.IgnoreCase))
            {
                DoH(e, "dns.nextdns.io/" + t.ToLowerInvariant(), h3: false);
                return;
            }
            if (t.IndexOf('/') > 0) { DoH(e, t, h3: false); return; }

            // A bare server name: what Android calls "Private DNS" — DNS-over-TLS. With a port,
            // the port says which.
            string host = t; port = 0;
            int colon = t.LastIndexOf(':');
            if (colon > 0 && int.TryParse(t.Substring(colon + 1), out port)) host = t.Substring(0, colon);
            if (port == 53) { HostOnly(e, t, DnsProto.Plain, "udp://"); return; }
            if (port == 443) { DoH(e, host + "/dns-query", h3: false); return; }
            HostOnly(e, t, DnsProto.DoT, "tls://");
        }

        // "1.1.1.1", "1.1.1.1:5353", "2a07:a8c0::ab:c123", "[2a07:a8c0::ab:c123]:53".
        private static bool TrySplitAddress(string s, out IPAddress ip, out int port)
        {
            ip = null; port = 0;
            s = (s ?? "").Trim();
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                var tail = s.Substring(close + 1);
                if (tail.StartsWith(":") && !int.TryParse(tail.Substring(1), out port)) return false;
                return IPAddress.TryParse(s.Substring(1, close - 1), out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            }
            int colons = s.Count(c => c == ':');
            if (colons >= 2)
                return IPAddress.TryParse(s, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            var addr = s;
            if (colons == 1)
            {
                int c = s.IndexOf(':');
                if (!int.TryParse(s.Substring(c + 1), out port)) return false;
                addr = s.Substring(0, c);
            }
            // IPAddress.TryParse is lenient ("1" → 0.0.0.1); the engine is not.
            return addr.Count(ch => ch == '.') == 3 && IPAddress.TryParse(addr, out ip)
                   && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        private static void SetAddress(DnsEntry e, IPAddress ip, int port, DnsProto proto, string prefix)
        {
            if (port < 0 || port > 65535) { Fail(e, Loc.T("Dns_ErrPort")); return; }
            var b = ip.GetAddressBytes();
            if (b.Length == 4 && b[0] == 198 && b[1] == 18) { Fail(e, Loc.T("Dns_ErrOwnTunnel")); return; }
            if (b.Length == 4 && b[0] == 0) { Fail(e, Loc.T("Dns_ErrUnknown")); return; }

            e.Ok = true;
            e.Proto = proto;
            e.IsIp = true;
            e.IsIPv6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            e.Host = ip.ToString();
            var hostPart = e.IsIPv6 ? (port > 0 || prefix.Length > 0 ? "[" + e.Host + "]" : e.Host) : e.Host;
            e.Engine = prefix + hostPart + (port > 0 ? ":" + port : "");
        }

        // tls:// quic:// tcp:// udp:// — a server and maybe a port, nothing else.
        private static void HostOnly(DnsEntry e, string rest, DnsProto proto, string prefix)
        {
            var hp = rest;
            int cut = hp.IndexOfAny(new[] { '/', '#', '?' });
            if (cut >= 0) hp = hp.Substring(0, cut);
            IPAddress ip; int port;
            if (TrySplitAddress(hp, out ip, out port)) { SetAddress(e, ip, port, proto, prefix); return; }

            string host = hp; port = 0;
            int colon = hp.LastIndexOf(':');
            if (colon > 0)
            {
                if (!int.TryParse(hp.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                { Fail(e, Loc.T("Dns_ErrPort")); return; }
                host = hp.Substring(0, colon);
            }
            if (!ValidHostName(host)) { Fail(e, Loc.T("Dns_ErrHost", host)); return; }
            e.Ok = true;
            e.Proto = proto;
            e.Host = host.ToLowerInvariant();
            e.Engine = (prefix.Length == 0 ? "udp://" : prefix) + e.Host + (port > 0 ? ":" + port : "");
        }

        // https://host[:port]/path — and HTTP/3 is not a scheme for the engine: it is the same URL
        // with #h3=true (a literal h3:// makes it refuse the WHOLE config — see the DNS gotchas).
        private static void DoH(DnsEntry e, string rest, bool h3)
        {
            var r = rest;
            int hash = r.IndexOf('#');
            if (hash >= 0) r = r.Substring(0, hash);
            int slash = r.IndexOf('/');
            var hp = slash >= 0 ? r.Substring(0, slash) : r;
            var path = slash >= 0 ? r.Substring(slash) : "/dns-query";
            if (path == "/") path = "/dns-query";

            Uri uri;
            if (!Uri.TryCreate("https://" + hp + path, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
            { Fail(e, Loc.T("Dns_ErrHost", hp)); return; }

            IPAddress ip;
            var host = uri.Host.Trim('[', ']');
            e.IsIp = IPAddress.TryParse(host, out ip);
            if (!e.IsIp && !ValidHostName(host)) { Fail(e, Loc.T("Dns_ErrHost", host)); return; }
            e.IsIPv6 = e.IsIp && ip.AddressFamily == AddressFamily.InterNetworkV6;
            e.Ok = true;
            e.Proto = h3 ? DnsProto.DoH3 : DnsProto.DoH;
            e.Host = host.ToLowerInvariant();
            e.Engine = "https://" + uri.Authority.ToLowerInvariant() + uri.AbsolutePath + (h3 ? "#h3=true" : "");
        }

        private static bool ValidHostName(string host)
        {
            if (string.IsNullOrWhiteSpace(host) || host.IndexOf('.') < 0) return false;
            return Uri.CheckHostName(host) == UriHostNameType.Dns;
        }

        // DNS stamps (sdns://) — what dnscrypt-proxy, AdGuard and NextDNS hand out for "other
        // clients". Plain, DoH, DoT and DoQ are decoded; DNSCrypt is a protocol the engine lacks.
        private static void FromStamp(DnsEntry e, string b64, int depth)
        {
            if (depth > 0) { Fail(e, Loc.T("Dns_ErrUnknown")); return; }
            var s = b64.Replace('-', '+').Replace('_', '/');
            while (s.Length % 4 != 0) s += "=";
            byte[] b;
            try { b = Convert.FromBase64String(s); }
            catch { Fail(e, Loc.T("Dns_ErrStamp")); return; }
            if (b.Length < 10) { Fail(e, Loc.T("Dns_ErrStamp")); return; }

            int pos = 9;   // protocol byte + 8 bytes of properties
            string Lp()
            {
                if (pos >= b.Length) return "";
                int len = b[pos++];
                if (pos + len > b.Length) len = b.Length - pos;
                var str = Encoding.UTF8.GetString(b, pos, len);
                pos += len;
                return str;
            }
            void SkipVlp()
            {
                while (pos < b.Length)
                {
                    int len = b[pos++];
                    pos += len & 0x7f;
                    if ((len & 0x80) == 0) break;
                }
            }

            string addr, host, path;
            switch (b[0])
            {
                case 0x00:
                    addr = Lp();
                    Recognise(e, addr, depth + 1);
                    return;
                case 0x02:
                    addr = Lp(); SkipVlp(); host = Lp(); path = Lp();
                    DoH(e, (host.Length > 0 ? host : addr) + path, h3: false);
                    return;
                case 0x03:
                    addr = Lp(); SkipVlp(); host = Lp();
                    HostOnly(e, host.Length > 0 ? host : addr, DnsProto.DoT, "tls://");
                    return;
                case 0x04:
                    addr = Lp(); SkipVlp(); host = Lp();
                    HostOnly(e, host.Length > 0 ? host : addr, DnsProto.DoQ, "quic://");
                    return;
                case 0x01:
                    Fail(e, Loc.T("Dns_ErrDnsCrypt")); return;
                default:
                    Fail(e, Loc.T("Dns_ErrStamp")); return;
            }
        }

        // Who runs it — only for the well-known services, so the user sees their NextDNS profile
        // recognised rather than a bare URL.
        private static void Label(DnsEntry e)
        {
            var h = e.Host ?? "";
            string Path0()
            {
                var m = Regex.Match(e.Engine, @"^https://[^/]+/([^/#?]+)");
                return m.Success ? m.Groups[1].Value : "";
            }

            if (h.EndsWith("nextdns.io") || h.StartsWith("45.90.28.") || h.StartsWith("45.90.30.")
                || h.StartsWith("2a07:a8c0:") || h.StartsWith("2a07:a8c1:"))
            {
                string id = "";
                var m = Regex.Match(h, @"^([0-9a-z]+)\.dns\d?\.nextdns\.io$");
                if (m.Success) id = m.Groups[1].Value;
                else if (e.Proto == DnsProto.DoH || e.Proto == DnsProto.DoH3) id = Path0();
                else if (e.IsIPv6)
                {
                    // 2a07:a8c0::ab:c123 — the profile id spread over the last two groups.
                    var groups = h.Split(':');
                    if (groups.Length >= 2) id = (groups[groups.Length - 2] + groups[groups.Length - 1]).PadLeft(6, '0');
                }
                if (id == "dns-query") id = "";
                e.Provider = id.Length > 0 ? Loc.T("Dns_ProviderNextDns", id) : "NextDNS";
                if (id.Length == 0 && e.IsIp && !e.IsIPv6) e.Warning = Loc.T("Dns_WarnNextDnsLinkedIp");
                return;
            }
            if (h.Contains("adguard-dns.com") || h.Contains("adguard.com") || h.StartsWith("94.140.14.") || h.StartsWith("94.140.15.")
                || h.StartsWith("2a10:50c0:"))
            { e.Provider = "AdGuard DNS"; return; }
            if (h.Contains("cloudflare") || h == "one.one.one.one" || h.StartsWith("1.1.1.") || h.StartsWith("1.0.0.")
                || h.StartsWith("2606:4700:4700:"))
            { e.Provider = "Cloudflare"; return; }
            if (h == "dns.google" || h == "8.8.8.8" || h == "8.8.4.4" || h.StartsWith("2001:4860:4860:"))
            { e.Provider = "Google"; return; }
            if (h.Contains("quad9.net") || h == "9.9.9.9" || h == "149.112.112.112" || h.StartsWith("2620:fe:"))
            { e.Provider = "Quad9"; return; }
            if (h.Contains("yandex") || h.StartsWith("77.88.8."))
            { e.Provider = Loc.T("Dns_ProviderYandex"); return; }
            if (h.Contains("controld.com") || h.StartsWith("76.76.2.") || h.StartsWith("76.76.10."))
            { e.Provider = "Control D"; return; }
            if (h.Contains("mullvad.net")) { e.Provider = "Mullvad"; return; }
            if (h == "127.0.0.1" || h == "::1") { e.Provider = Loc.T("Dns_ProviderLocal"); return; }
            var ip = h.Split('.');
            if (ip.Length == 4 && (ip[0] == "192" && ip[1] == "168" || ip[0] == "10" || ip[0] == "172"))
                e.Provider = Loc.T("Dns_ProviderLan");
        }

        public static string ProtoText(DnsProto p)
        {
            switch (p)
            {
                case DnsProto.Tcp: return Loc.T("Dns_ProtoTcp");
                case DnsProto.DoT: return Loc.T("Dns_ProtoDoT");
                case DnsProto.DoH: return Loc.T("Dns_ProtoDoH");
                case DnsProto.DoH3: return Loc.T("Dns_ProtoDoH3");
                case DnsProto.DoQ: return Loc.T("Dns_ProtoDoQ");
                default: return Loc.T("Dns_ProtoPlain");
            }
        }

        // "DoH — DNS через HTTPS · NextDNS, профиль abc123 · IPv6"
        public static string Describe(DnsEntry e)
        {
            if (e == null) return "";
            if (!e.Ok) return e.Error ?? "";
            var parts = new List<string> { ProtoText(e.Proto) };
            if (!string.IsNullOrEmpty(e.Provider)) parts.Add(e.Provider);
            if (e.IsIPv6) parts.Add("IPv6");
            return string.Join(" · ", parts);
        }

        // ---------- what the config gets ----------

        // The entries the engine is given right now — empty when the feature is off. Order kept:
        // the first one is "основной".
        public static List<DnsEntry> Active(AppConfig cfg)
        {
            if (cfg == null || !cfg.DirectDnsEnabled) return new List<DnsEntry>();
            var sub = ChosenOffer(cfg);
            return (sub != null ? sub.Servers : (cfg.DirectDns ?? new List<string>()))
                .SelectMany(ParseMany)
                .Where(e => e.Ok)
                .GroupBy(e => e.Engine, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .ToList();
        }

        // ---------- DNS a subscription sends ----------

        // A Clash subscription is a whole client config, and some carry a `dns:` block with the
        // resolvers its issuer wants used (on a phone it IS the client's DNS). Here it can take
        // the same single place the typed servers take — direct traffic — if the user picks it.
        public sealed class SubDnsOffer
        {
            public VpnSlot Slot;
            public List<string> Servers = new List<string>();
        }

        // Every Clash subscription whose downloaded copy has `dns: enable: true` and servers.
        public static List<SubDnsOffer> Offered(AppConfig cfg)
        {
            var list = new List<SubDnsOffer>();
            if (cfg?.VpnSlots == null) return list;
            for (int i = 0; i < cfg.VpnSlots.Count; i++)
            {
                var slot = cfg.VpnSlots[i];
                if (slot == null || slot.Kind != VpnSlotKind.ClashSub) continue;
                try
                {
                    var path = SubscriptionPolicy.CachePath(cfg, ProxyParsers.ProxyNameFor(slot, i + 1));
                    var servers = ReadSubDns(path);
                    if (servers.Count > 0) list.Add(new SubDnsOffer { Slot = slot, Servers = servers });
                }
                catch { }
            }
            return list;
        }

        // The subscription picked in «DNS», or null — not picked, gone, or it no longer sends DNS
        // (then the typed servers apply).
        public static SubDnsOffer ChosenOffer(AppConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.DirectDnsSource)) return null;
            return Offered(cfg).FirstOrDefault(o => o.Slot.Id == cfg.DirectDnsSource);
        }

        // `dns:` → `enable:` and `nameserver:` (block list or [flow, list]). Only the top-level
        // block; everything else in the file is the engine's business.
        private static List<string> ReadSubDns(string path)
        {
            var servers = new List<string>();
            if (!File.Exists(path)) return servers;
            bool inDns = false, inNs = false, enabled = false;
            foreach (var raw in File.ReadLines(path))
            {
                if (raw.Length == 0 || raw.TrimStart().StartsWith("#")) continue;
                bool top = raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-';
                if (top)
                {
                    if (inDns) break;   // the block is over
                    inDns = raw.TrimEnd() == "dns:";
                    continue;
                }
                if (!inDns) continue;
                var t = raw.Trim();
                int indent = raw.Length - raw.TrimStart().Length;
                if (inNs && t.StartsWith("-")) { Add(servers, t.Substring(1)); continue; }
                if (indent <= 2) inNs = false;
                if (t.StartsWith("enable:")) enabled = Value(t) == "true";
                else if (t.StartsWith("nameserver:"))
                {
                    var v = Value(t);
                    if (v.StartsWith("[")) foreach (var part in v.Trim('[', ']').Split(',')) Add(servers, part);
                    else if (v.Length == 0) inNs = true;
                    else Add(servers, v);
                }
            }
            return enabled ? servers : new List<string>();
        }

        private static string Value(string line)
        {
            int c = line.IndexOf(':');
            var v = c < 0 ? "" : line.Substring(c + 1).Trim();
            int hash = v.IndexOf(" #", StringComparison.Ordinal);
            return (hash >= 0 ? v.Substring(0, hash) : v).Trim().Trim('"', '\'');
        }

        private static void Add(List<string> to, string s)
        {
            s = (s ?? "").Trim().Trim('"', '\'');
            int hash = s.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) s = s.Substring(0, hash).Trim();
            if (s.Length > 0) to.Add(s);
        }

        public static string Signature(AppConfig cfg)
            => string.Join("|", Active(cfg).Select(e => e.Engine));

        public static bool MachineHasIPv6()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); } catch { continue; }
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6
                                                          && !g.Address.Equals(IPAddress.IPv6Any))) continue;
                    foreach (var a in props.UnicastAddresses)
                    {
                        var ip = a.Address;
                        if (ip.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo) continue;
                        var b = ip.GetAddressBytes();
                        if ((b[0] & 0xfe) == 0xfc) continue;   // ULA
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---------- asking a resolver for real ----------

        public sealed class Answer
        {
            public string Name = "";
            public bool Ok;                 // the server answered (even if with nothing)
            public long Ms;
            public List<string> Addresses = new List<string>();
            public bool Stub;               // 0.0.0.0 / :: — the server blocks this name
            public bool NxDomain;
            public string Error = "";
        }

        private const string ProbeExeName = "mihomo-dnscheck.exe";
        private static readonly SemaphoreSlim _probeGate = new SemaphoreSlim(1, 1);

        // Ask `server` for each of `names`, exactly the way the engine would — by running the
        // engine. Only the engine speaks every protocol here (DoQ and DoH3 are QUIC, which .NET
        // Framework does not have), and only the engine proves the string it will be handed.
        //
        // It runs as a COPY under its own file name. The launcher's watchdog counts engines by
        // process name every 20 s, and a second "mihomo-windows-amd64" makes it restart the real
        // engine from scratch — a machine-wide outage to check one DNS server. No TUN, no
        // mixed-port: the copy touches neither routes nor ports beyond its own API, and it is
        // bound to the physical adapter so its queries don't go through the live tunnel (where
        // «Всё остальное» would quietly carry them over a VPN node).
        public static async Task<List<Answer>> QueryAsync(AppConfig cfg, string server, IList<string> names, CancellationToken ct)
        {
            var result = new List<Answer>();
            var src = (cfg?.MihomoExePath ?? "").Trim();
            if (src.Length == 0 || !File.Exists(src))
                throw new InvalidOperationException(Loc.T("Dns_ErrNoEngine"));

            await _probeGate.WaitAsync(ct).ConfigureAwait(false);
            Process p = null;
            try
            {
                var dir = Path.Combine(VersionInfo.DataUserDir, "dnscheck");
                Directory.CreateDirectory(dir);
                KillStrayProbes();
                var exe = Path.Combine(dir, ProbeExeName);
                var si = new FileInfo(src);
                var di = new FileInfo(exe);
                if (!di.Exists || di.Length != si.Length || di.LastWriteTimeUtc != si.LastWriteTimeUtc)
                {
                    File.Copy(src, exe, true);
                    File.SetLastWriteTimeUtc(exe, si.LastWriteTimeUtc);
                }

                int port = FreePort();
                var secret = Guid.NewGuid().ToString("N");
                var boot = BootstrapServers();
                var sb = new StringBuilder();
                sb.AppendLine("mode: rule");
                sb.AppendLine("log-level: warning");
                sb.AppendLine("external-controller: 127.0.0.1:" + port);
                sb.AppendLine("secret: \"" + secret + "\"");
                var nic = PhysicalInterfaceName();
                if (!string.IsNullOrEmpty(nic)) sb.AppendLine("interface-name: \"" + nic.Replace("\"", "") + "\"");
                sb.AppendLine("dns:");
                sb.AppendLine("  enable: true");
                sb.AppendLine("  ipv6: true");
                sb.AppendLine("  enhanced-mode: redir-host");
                sb.AppendLine("  default-nameserver:");
                foreach (var b in boot) sb.AppendLine("    - \"" + b + "\"");
                sb.AppendLine("  nameserver:");
                sb.AppendLine("    - \"" + server.Replace("\"", "") + "\"");
                sb.AppendLine("rules:");
                sb.AppendLine("  - MATCH,DIRECT");
                File.WriteAllText(Path.Combine(dir, "config.yaml"), sb.ToString(), new UTF8Encoding(false));

                var psi = new ProcessStartInfo(exe, "-d \"" + dir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = dir,
                };
                p = Process.Start(psi);
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(9) })
                {
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                    var api = "http://127.0.0.1:" + port;

                    // Up within ~0.3 s normally; a config it refuses makes it exit instead.
                    var until = DateTime.UtcNow.AddSeconds(6);
                    bool up = false;
                    while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
                    {
                        if (p.HasExited) break;
                        try
                        {
                            using (var r = await http.GetAsync(api + "/version", ct).ConfigureAwait(false))
                                if (r.IsSuccessStatusCode) { up = true; break; }
                        }
                        catch { }
                        await Task.Delay(150, ct).ConfigureAwait(false);
                    }
                    if (!up) throw new InvalidOperationException(Loc.T("Dns_ErrRefused"));

                    foreach (var name in names)
                    {
                        ct.ThrowIfCancellationRequested();
                        var a = new Answer { Name = name };
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            // A dead server comes back as HTTP 500 with the engine's own reason in
                            // the body ("context deadline exceeded") — read it either way.
                            using (var r = await http.GetAsync(api + "/dns/query?name=" + Uri.EscapeDataString(name) + "&type=A", ct)
                                                     .ConfigureAwait(false))
                            {
                                var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                                a.Ms = sw.ElapsedMilliseconds;
                                ParseAnswer(body, a);
                                if (!a.Ok && a.Error.Length == 0) a.Error = "HTTP " + (int)r.StatusCode;
                            }
                        }
                        catch (Exception ex) { a.Ms = sw.ElapsedMilliseconds; a.Error = ex.Message; }
                        result.Add(a);
                        if (!a.Ok) break;   // it isn't answering at all — don't wait out the rest
                    }
                }
            }
            finally
            {
                try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(2000); } } catch { }
                try { p?.Dispose(); } catch { }
                _probeGate.Release();
            }
            return result;
        }

        private static void ParseAnswer(string json, Answer a)
        {
            var msg = Regex.Match(json ?? "", "\"message\"\\s*:\\s*\"([^\"]*)\"");
            if (msg.Success) { a.Error = msg.Groups[1].Value; return; }
            if ((json ?? "").IndexOf("\"Status\"", StringComparison.Ordinal) < 0) return;   // not an answer at all
            a.Ok = true;
            var st = Regex.Match(json, "\"Status\"\\s*:\\s*(\\d+)");
            if (st.Success && st.Groups[1].Value == "3") a.NxDomain = true;
            // {"TTL":135,"data":"8.6.112.7","name":"example.com.","type":1} — A only, not the CNAMEs.
            foreach (Match m in Regex.Matches(json, "\"data\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"name\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"type\"\\s*:\\s*(\\d+)"))
                if (m.Groups[2].Value == "1" || m.Groups[2].Value == "28") a.Addresses.Add(m.Groups[1].Value);
            a.Stub = a.Addresses.Count > 0 && a.Addresses.All(x => x == "0.0.0.0" || x == "::" || x == "127.0.0.1");
        }

        // "доступен за 42 мс · режет рекламу" / "не ответил: …" — one line for a whole check.
        public static string Summarize(List<Answer> answers)
        {
            var main = answers.FirstOrDefault();
            if (main == null) return "";
            if (!main.Ok) return Loc.T("Dns_TestFail", Shorten(main.Error));
            if (main.Addresses.Count == 0) return Loc.T("Dns_TestEmpty", main.Ms);
            var text = Loc.T("Dns_TestOk", main.Ms);
            var ads = answers.Skip(1).FirstOrDefault();
            if (ads != null && ads.Ok && (ads.Stub || ads.NxDomain)) text += " · " + Loc.T("Dns_TestBlocksAds");
            return text;
        }

        public static string Shorten(string error)
        {
            var e = (error ?? "").Trim();
            if (e.IndexOf("deadline", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || e.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
                return Loc.T("Dns_ErrTimeout");
            return e.Length > 140 ? e.Substring(0, 140) + "…" : e;
        }

        // A server addressed by NAME needs an address for itself first. The machine's own resolvers
        // (with our tunnel taken out) — never 1.1.1.1 by default: in some countries that is the
        // first thing the ISP throttles, and the machine's resolver is the one known to work here.
        public static List<string> BootstrapServers()
        {
            var list = MihomoConfigBuilder.CurrentMachineUpstream()
                .Where(s => s != "system")
                .ToList();
            if (list.Count == 0) { list.Add("1.1.1.1"); list.Add("8.8.8.8"); }
            return list;
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // The adapter the internet actually leaves by — the first one that is up, has a gateway,
        // and is not our tunnel.
        private static string PhysicalInterfaceName()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); } catch { continue; }
                    if (props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                                        && a.Address.ToString().StartsWith("198.18."))) continue;
                    if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                          && !g.Address.Equals(IPAddress.Any))) continue;
                    return nic.Name;
                }
            }
            catch { }
            return null;
        }

        // A copy left running by a launcher that died mid-check — ours, unprivileged, harmless,
        // but it holds a port and a file lock on the copy.
        private static void KillStrayProbes()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ProbeExeName)))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }
    }
}
