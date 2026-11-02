using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Result of converting a VpnSlot source into a mihomo proxy (or proxy-provider).
    public class ParsedProxy
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";

        // Logical proxy/group name referenced by rules (sanitized, unique per slot).
        public string ProxyName { get; set; } = "";

        // The VpnSlot this came from — lets the config builder tie a slot-scoped setting
        // (e.g. "use THIS variant's own DNS") to the proxy that's actually live.
        public string SlotId { get; set; } = "";

        // VPN server host/IP (for geo-locating the endpoint). Empty for subscriptions.
        public string Server { get; set; } = "";

        // For WireGuard / VLESS: YAML lines for one entry under `proxies:` (already
        // indented with 2 spaces, block style). Empty for a subscription.
        public List<string> ProxyYamlLines { get; set; } = new List<string>();

        // DNS servers the VPN server itself handed us — the `DNS =` line of a WireGuard
        // .conf. Usually a private address that only answers inside the tunnel. Empty for
        // VLESS (the share link has no DNS field) and for subscriptions (mihomo imports
        // only `proxies:` from a provider, never its `dns:` section).
        public List<string> ServerDns { get; set; } = new List<string>();

        // For ClashSub: subscription URL exposed as a proxy-provider.
        public bool IsProvider { get; set; }
        public string ProviderUrl { get; set; } = "";

        // For ClashSub: proxy-provider re-download interval in seconds (from the slot's
        // UpdateIntervalHours). Default 48h.
        public int ProviderIntervalSeconds { get; set; } = 48 * 3600;

        // For ShareLink: the link itself. The config builder writes it to
        // providers\<ProxyName>.txt in the work dir and declares a `type: file` provider.
        public string ProviderFileContent { get; set; } = "";
    }

    // Converts VpnSlot definitions (WireGuard .conf path, vless:// link, Clash sub URL)
    // into mihomo proxy/provider YAML. Hand-built YAML, no external dependency.
    public static class ProxyParsers
    {
        public static ParsedProxy Parse(VpnSlot slot, int slotIndex)
        {
            if (slot == null) return Fail(Loc.T("Svc_SlotEmpty"));
            var name = SanitizeName(slot.Name, slotIndex);
            try
            {
                var parsed = ParseCore(slot, name, slotIndex);
                if (parsed != null) parsed.SlotId = slot.Id;
                return parsed;
            }
            catch (Exception ex)
            {
                return Fail(Loc.T("Svc_ParseError", ex.Message));
            }
        }

        private static ParsedProxy ParseCore(VpnSlot slot, string name, int slotIndex)
        {
            switch (slot.Kind)
            {
                case VpnSlotKind.WireGuardConf: return ParseWireGuard(slot.Value, name);
                case VpnSlotKind.Vless:         return ParseVless(slot.Value, name);
                case VpnSlotKind.Hysteria2:     return ParseHysteria2(slot.Value, name);
                case VpnSlotKind.ShareLink:     return ParseShareLink(slot.Value, name);
                case VpnSlotKind.ClashSub:
                {
                    var prov = ParseClashSub(slot.Value, name);
                    if (prov.Ok)
                    {
                        int hours = slot.UpdateIntervalHours > 0 ? slot.UpdateIntervalHours : 48;
                        prov.ProviderIntervalSeconds = hours * 3600;
                    }
                    return prov;
                }
                default: return Fail(Loc.T("Svc_UnknownSlotKind"));
            }
        }

        // ---------- WireGuard .conf ----------

        private static readonly Regex KvRegex = new Regex(@"^\s*(?<k>[A-Za-z0-9]+)\s*=\s*(?<v>.+?)\s*$", RegexOptions.Compiled);

        private static ParsedProxy ParseWireGuard(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Fail(Loc.T("Svc_ConfFileNotFound", path));

            string section = "";
            string priv = "", addr = "", pub = "", psk = "", endpoint = "", dns = "";
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line.ToLowerInvariant(); continue; }
                var m = KvRegex.Match(line);
                if (!m.Success) continue;
                var k = m.Groups["k"].Value.ToLowerInvariant();
                var v = m.Groups["v"].Value.Trim();
                if (section == "[interface]")
                {
                    if (k == "privatekey") priv = v;
                    else if (k == "address") addr = v;
                    else if (k == "dns") dns = v;
                }
                else if (section == "[peer]")
                {
                    if (k == "publickey") pub = v;
                    else if (k == "presharedkey") psk = v;
                    else if (k == "endpoint") endpoint = v;
                }
            }

            if (priv == "" || pub == "" || endpoint == "")
                return Fail(Loc.T("Svc_ConfMissingKeys"));

            // Endpoint host:port (host may be ipv4, [ipv6] or hostname).
            string server; int port;
            if (!SplitHostPort(endpoint, out server, out port))
                return Fail(Loc.T("Svc_BadEndpoint", endpoint));

            // Interface Address: comma-separated; pick first v4 and first v6.
            string ip4 = "", ip6 = "";
            foreach (var part in addr.Split(','))
            {
                var a = part.Trim();
                var slash = a.IndexOf('/');
                if (slash >= 0) a = a.Substring(0, slash);
                if (a.Length == 0) continue;
                if (a.Contains(":")) { if (ip6 == "") ip6 = a; }
                else { if (ip4 == "") ip4 = a; }
            }

            var lines = new List<string>
            {
                "  - name: " + Q(name),
                "    type: wireguard",
                "    server: " + Q(server),
                "    port: " + port,
                "    private-key: " + Q(priv),
                "    public-key: " + Q(pub),
                "    udp: true",
            };
            if (ip4 != "") lines.Add("    ip: " + Q(ip4));
            if (ip6 != "") lines.Add("    ipv6: " + Q(ip6));
            if (psk != "") lines.Add("    pre-shared-key: " + Q(psk));

            // A tunnel whose `Address` has no v6 simply has no IPv6 inside it. Left alone,
            // mihomo still takes the AAAA records its resolver returns and dials them through
            // that tunnel — an instant "network is unreachable" on every dual-stack site. This
            // keeps the node on v4 without touching IPv6 anywhere else on the machine.
            //
            // The value MUST be exactly `ipv4`. mihomo's set is dual/ipv4/ipv6/ipv4-prefer/
            // ipv6-prefer, and it accepts anything else in silence — `-t` passes on a value of
            // `garbage123` — falling back to dual stack, i.e. the very thing this line is here
            // to prevent. Measured on the binary: with `ipv4-only` the node looks up both AAAA
            // and A and dials the v6 address; with `ipv4` it looks up A only.
            if (ip6 == "") lines.Add("    ip-version: ipv4");

            // `DNS =` may also carry search domains — keep only the addresses.
            var serverDns = new List<string>();
            foreach (var part in dns.Split(','))
            {
                var d = part.Trim();
                if (d.Length > 0 && LooksLikeIp(d)) serverDns.Add(d);
            }

            // WireGuard is a plain L3 tunnel: unlike VLESS it carries no hostname, so mihomo
            // would otherwise resolve rule domains HERE and send packets to the IP it got —
            // i.e. locally, exposed to the ISP resolver. Handing the tunnel its own DNS and
            // turning on remote resolution moves that lookup to the far end, where it belongs.
            if (serverDns.Count > 0)
            {
                lines.Add("    remote-dns-resolve: true");
                lines.Add("    dns: [ " + string.Join(", ", serverDns.Select(Q)) + " ]");
            }

            return new ParsedProxy { Ok = true, ProxyName = name, Server = server, ProxyYamlLines = lines, ServerDns = serverDns };
        }

        // ---------- VLESS share link ----------

        private static ParsedProxy ParseVless(string link, string name)
        {
            if (string.IsNullOrWhiteSpace(link)) return Fail(Loc.T("Svc_VlessEmpty"));
            link = link.Trim();
            if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return Fail(Loc.T("Svc_VlessBadScheme"));

            // vless://<uuid>@<host>:<port>?<query>#<fragment>
            var rest = link.Substring("vless://".Length);
            var hashIdx = rest.IndexOf('#');
            if (hashIdx >= 0) rest = rest.Substring(0, hashIdx);

            var qIdx = rest.IndexOf('?');
            var query = "";
            if (qIdx >= 0) { query = rest.Substring(qIdx + 1); rest = rest.Substring(0, qIdx); }

            var at = rest.IndexOf('@');
            if (at < 0) return Fail(Loc.T("Svc_VlessNoUuid"));
            var uuid = rest.Substring(0, at);
            var hostPort = rest.Substring(at + 1);

            string server; int port;
            if (!SplitHostPort(hostPort, out server, out port))
                return Fail(Loc.T("Svc_VlessBadHostPort"));

            var p = ParseQuery(query);
            string Get(string key) { string val; return p.TryGetValue(key, out val) ? val : ""; }

            var security = Get("security").ToLowerInvariant();   // tls | reality | none | ""
            var network = Get("type").ToLowerInvariant();        // tcp | ws | grpc
            if (network == "") network = "tcp";
            var flow = Get("flow");
            var sni = Get("sni"); if (sni == "") sni = Get("peer");
            var fp = Get("fp");
            var pbk = Get("pbk");
            var sid = Get("sid");
            var wsPath = Get("path");
            var wsHost = Get("host");
            var grpcName = Get("serviceName"); if (grpcName == "") grpcName = Get("servicename");

            var lines = new List<string>
            {
                "  - name: " + Q(name),
                "    type: vless",
                "    server: " + Q(server),
                "    port: " + port,
                "    uuid: " + Q(uuid),
                "    network: " + network,
                "    udp: true",
            };
            if (flow != "") lines.Add("    flow: " + flow);

            bool tls = security == "tls" || security == "reality";
            if (tls) lines.Add("    tls: true");
            if (sni != "") lines.Add("    servername: " + Q(sni));
            if (fp != "") lines.Add("    client-fingerprint: " + fp);

            if (security == "reality")
            {
                lines.Add("    reality-opts:");
                if (pbk != "") lines.Add("      public-key: " + Q(pbk));
                if (sid != "") lines.Add("      short-id: " + Q(sid));
            }

            if (network == "ws")
            {
                lines.Add("    ws-opts:");
                lines.Add("      path: " + Q(wsPath == "" ? "/" : wsPath));
                if (wsHost != "")
                {
                    lines.Add("      headers:");
                    lines.Add("        Host: " + Q(wsHost));
                }
            }
            else if (network == "grpc")
            {
                lines.Add("    grpc-opts:");
                lines.Add("      grpc-service-name: " + Q(grpcName));
            }

            return new ParsedProxy { Ok = true, ProxyName = name, Server = server, ProxyYamlLines = lines };
        }

        // ---------- Hysteria2 share link ----------

        // hysteria2://<auth>@<host>:<port>[/path]?<query>#<name>  ("hy2://" is the same scheme).
        // The port may be a hopping spec — "443,5000-6000" — written either in place of the
        // port or in the `mport` parameter; mihomo takes that as `ports:` plus a plain `port:`.
        private static ParsedProxy ParseHysteria2(string link, string name)
        {
            if (string.IsNullOrWhiteSpace(link)) return Fail(Loc.T("Svc_Hy2Empty"));
            link = link.Trim();

            string rest;
            if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase))
                rest = link.Substring("hysteria2://".Length);
            else if (link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
                rest = link.Substring("hy2://".Length);
            else
                return Fail(Loc.T("Svc_Hy2BadScheme"));

            var hashIdx = rest.IndexOf('#');
            if (hashIdx >= 0) rest = rest.Substring(0, hashIdx);

            var qIdx = rest.IndexOf('?');
            var query = "";
            if (qIdx >= 0) { query = rest.Substring(qIdx + 1); rest = rest.Substring(0, qIdx); }

            // A trailing "/" (or "/whatever") is legal in the URI and carries nothing we need.
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest.Substring(0, slash);

            // The auth payload is a free-form string that is often written "user:password",
            // so the host starts after the LAST '@', not the first.
            var at = rest.LastIndexOf('@');
            var auth = at >= 0 ? Uri.UnescapeDataString(rest.Substring(0, at)) : "";
            var hostPort = at >= 0 ? rest.Substring(at + 1) : rest;

            string server, portSpec;
            if (!SplitHostPortSpec(hostPort, out server, out portSpec) || server.Length == 0)
                return Fail(Loc.T("Svc_Hy2BadHostPort"));

            var p = ParseQuery(query);
            string Get(string key) { string val; return p.TryGetValue(key, out val) ? val.Trim() : ""; }

            // Port hopping: from the port itself ("443,5000-6000") or from `mport`.
            var ports = Get("mport"); if (ports == "") ports = Get("ports");
            if (portSpec.IndexOf(',') >= 0 || portSpec.IndexOf('-') >= 0) { if (ports == "") ports = portSpec; }

            int port;
            if (!FirstPort(portSpec, out port) && !FirstPort(ports, out port)) port = 443;

            var lines = new List<string>
            {
                "  - name: " + Q(name),
                "    type: hysteria2",
                "    server: " + Q(server),
                "    port: " + port,
            };
            if (ports != "") lines.Add("    ports: " + Q(ports));

            int hop;
            var hopRaw = Get("hopInterval"); if (hopRaw == "") hopRaw = Get("hop-interval");
            if (int.TryParse(hopRaw, out hop) && hop > 0) lines.Add("    hop-interval: " + hop);

            if (auth != "") lines.Add("    password: " + Q(auth));

            var obfs = Get("obfs");
            if (obfs != "")
            {
                lines.Add("    obfs: " + Q(obfs));
                var obfsPwd = Get("obfs-password"); if (obfsPwd == "") obfsPwd = Get("obfsParam");
                if (obfsPwd != "") lines.Add("    obfs-password: " + Q(obfsPwd));
            }

            var sni = Get("sni"); if (sni == "") sni = Get("peer");
            if (sni != "") lines.Add("    sni: " + Q(sni));

            var insecure = Get("insecure");
            if (insecure == "1" || insecure.Equals("true", StringComparison.OrdinalIgnoreCase))
                lines.Add("    skip-cert-verify: true");

            // Certificate pinning: the URI carries "AA:BB:.." or bare hex, mihomo wants bare hex.
            // It insists on a full SHA-256 and refuses to load the WHOLE config otherwise, so a
            // pin of any other shape is dropped — one odd link must not take the engine down.
            var pin = Get("pinSHA256"); if (pin == "") pin = Get("pinsha256");
            pin = pin.Replace(":", "").Replace(" ", "").ToLowerInvariant();
            if (Regex.IsMatch(pin, "^[0-9a-f]{64}$")) lines.Add("    fingerprint: " + Q(pin));

            var alpn = Get("alpn")
                .Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            if (alpn.Count > 0) lines.Add("    alpn: [ " + string.Join(", ", alpn.Select(Q)) + " ]");

            // Bandwidth is optional — without it hysteria2 uses BBR, which is usually better
            // than a guessed number, so only pass on what the link actually states.
            var up = Bandwidth(Get("up"), Get("upmbps"));
            var down = Bandwidth(Get("down"), Get("downmbps"));
            if (up != "") lines.Add("    up: " + Q(up));
            if (down != "") lines.Add("    down: " + Q(down));

            return new ParsedProxy { Ok = true, ProxyName = name, Server = server, ProxyYamlLines = lines };
        }

        // "100" (plain Mbps) or "100 mbps" — mihomo accepts both as a string.
        private static string Bandwidth(string value, string mbps)
        {
            var v = (value ?? "").Trim();
            if (v == "") v = (mbps ?? "").Trim();
            if (v == "") return "";
            int n;
            return int.TryParse(v, out n) ? (n + " Mbps") : v;
        }

        // ---------- Clash / mihomo subscription ----------

        private static ParsedProxy ParseClashSub(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url)) return Fail(Loc.T("Svc_SubUrlEmpty"));
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Fail(Loc.T("Svc_SubUrlBadScheme"));

            return new ParsedProxy { Ok = true, ProxyName = name, IsProvider = true, ProviderUrl = url };
        }

        // ---------- any other share link (vmess / trojan / ss / tuic / …) ----------

        // Not parsed here at all: the engine converts the link itself (ShareLinks). All this
        // checks is that it is a scheme we hand over, so a typo fails here with a reason
        // instead of as an empty provider at connect time.
        private static ParsedProxy ParseShareLink(string link, string name)
        {
            var info = ShareLinks.Detect(link);
            if (!info.Ok || info.Kind != VpnSlotKind.ShareLink) return Fail(Loc.T("Svc_ShareLinkUnknown"));
            return new ParsedProxy
            {
                Ok = true,
                ProxyName = name,
                IsProvider = true,
                ProviderFileContent = info.Value,
                Server = ShareLinks.HostOf(info.Value),
            };
        }

        // ---------- helpers ----------

        // The proxy/group name a slot maps to (same value as ParsedProxy.ProxyName). The
        // proxy-provider is then named "<ProxyName>_prov" / cached at providers/<ProxyName>.yaml.
        public static string ProxyNameFor(VpnSlot slot, int slotIndex)
        {
            return SanitizeName(slot == null ? null : slot.Name, slotIndex);
        }

        private static ParsedProxy Fail(string err)
        {
            return new ParsedProxy { Ok = false, Error = err };
        }

        // A bare IPv4/IPv6 literal? Used to keep search domains out of a WireGuard `DNS =` line.
        private static bool LooksLikeIp(string s)
        {
            System.Net.IPAddress ip;
            return System.Net.IPAddress.TryParse((s ?? "").Trim(), out ip);
        }

        // YAML double-quote a scalar.
        private static string Q(string s)
        {
            if (s == null) s = "";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string SanitizeName(string name, int slotIndex)
        {
            var baseName = string.IsNullOrWhiteSpace(name) ? ("slot" + slotIndex) : name.Trim();
            var sb = new StringBuilder();
            foreach (var ch in baseName)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                else sb.Append('_');
            }
            var result = sb.ToString();
            return result.Length == 0 ? ("slot" + slotIndex) : ("vpn_" + result);
        }

        // Parse "host:port", "[ipv6]:port".
        private static bool SplitHostPort(string s, out string host, out int port)
        {
            host = ""; port = 0;
            s = (s ?? "").Trim();
            if (s.Length == 0) return false;
            int colon;
            if (s.StartsWith("["))
            {
                var close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                colon = s.IndexOf(':', close);
            }
            else
            {
                colon = s.LastIndexOf(':');
                if (colon < 0) return false;
                host = s.Substring(0, colon);
            }
            if (colon < 0 || colon >= s.Length - 1) return false;
            return int.TryParse(s.Substring(colon + 1), out port) && port > 0 && port <= 65535;
        }

        // Like SplitHostPort, but the port side is kept verbatim: hysteria2 allows a hopping
        // spec ("443,5000-6000") there, and an absent port (defaults applied by the caller).
        private static bool SplitHostPortSpec(string s, out string host, out string portSpec)
        {
            host = ""; portSpec = "";
            s = (s ?? "").Trim();
            if (s.Length == 0) return false;
            int colon;
            if (s.StartsWith("["))
            {
                var close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                colon = s.IndexOf(':', close);
            }
            else
            {
                colon = s.LastIndexOf(':');
                host = colon < 0 ? s : s.Substring(0, colon);
            }
            if (colon >= 0) portSpec = s.Substring(colon + 1).Trim();
            return true;
        }

        // First port number of a spec like "5000-6000,443" — mihomo still wants a plain `port:`.
        private static bool FirstPort(string spec, out int port)
        {
            port = 0;
            var m = Regex.Match(spec ?? "", @"\d+");
            return m.Success && int.TryParse(m.Value, out port) && port > 0 && port <= 65535;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                string k, v;
                if (eq < 0) { k = pair; v = ""; }
                else { k = pair.Substring(0, eq); v = pair.Substring(eq + 1); }
                dict[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v);
            }
            return dict;
        }
    }
}
