using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // A single mihomo routing rule, e.g. PROCESS-NAME,Claude.exe,vpn_kg  or
    // DOMAIN-SUFFIX,youtube.com,vpn_kg.
    public class RuleDef
    {
        public string Type { get; set; }    // PROCESS-NAME | DOMAIN-SUFFIX | ...
        public string Value { get; set; }   // process image name or domain
        public string Target { get; set; }  // proxy/group name, or DIRECT
        // Straight to Target even when Target is a subscription with rules of its own — the
        // launcher's probe rule: a check has to cross the node, not the issuer's policy.
        public bool Raw { get; set; }
        public RuleDef() { }
        public RuleDef(string type, string value, string target) { Type = type; Value = value; Target = target; }
    }

    // Assembles a complete mihomo config.yaml from the current proxies and rules.
    // Hand-built YAML (fixed schema) so there is no external YAML dependency.
    public static class MihomoConfigBuilder
    {
        private const string HealthCheckUrl = "https://www.gstatic.com/generate_204";

        // Addresses kept OUT of the TUN route table. Local/LAN ranges are always excluded
        // (RDP-over-LAN, file shares, router, printers stay direct). The live RDP *peer* is
        // excluded too — UNLESS the user put an RDP client (mstsc) into the VPN list, which
        // means they deliberately route RDP through the tunnel on this machine, so we must
        // NOT auto-protect it. On mom's PC (no mstsc listed) the incoming session is shielded;
        // on the admin's PC (mstsc listed) the RDP client rides the VPN via its PROCESS-NAME rule.
        private static IEnumerable<string> TunRouteExcludes(AppConfig cfg)
        {
            var list = new List<string>
            {
                "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",  // RFC1918 LAN
                "100.64.0.0/10",                                   // CGNAT
                "169.254.0.0/16",                                  // link-local
                "fc00::/7", "fe80::/10",                           // IPv6 ULA + link-local
            };
            if (!RdpClientIsRouted(cfg))
            {
                var peers = new List<string>();
                try { peers = RdpClients.ActivePeerCidrs() ?? new List<string>(); } catch { }
                LastRdpExcludes = peers;
                list.AddRange(peers);
            }
            else LastRdpExcludes = null;
            return list.Distinct();
        }

        // The RDP peers the most recent build actually excluded — null when it deliberately
        // excluded none (the user routes an RDP client through the VPN on purpose).
        //
        // This list is a snapshot taken while the config was being written, and a remote session
        // that appears LATER isn't in it: its return traffic gets captured by TUN's auto-route
        // and the session drops, with nothing to rebuild the config because everything is
        // already connected and healthy. That is the hole the "Remote desktop session" switch
        // papers over — by killing the engine every 60 s, which also takes the kill-switch down
        // with it. The watchdog compares this against the live peers instead.
        public static List<string> LastRdpExcludes { get; private set; }

        // One malformed address anywhere in the file and mihomo refuses the WHOLE config — at
        // reload (HTTP 400, "netip.ParsePrefix") and, worse, at start: the engine exits at once,
        // the launcher reports "движок не отвечает", and reinstalling the binary can never help
        // because the binary was never the problem. That was prof.urfo, 2026-09-30, over and
        // over. So every prefix is checked the way the engine will check it, and a bad one costs
        // that single line instead of every rule on the machine.
        internal static bool IsValidPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            int slash = v.IndexOf('/');
            if (slash <= 0 || slash != v.LastIndexOf('/')) return false;
            var addr = v.Substring(0, slash);
            if (addr.IndexOf('%') >= 0) return false;       // IPv6 zone: not allowed in a prefix
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip)) return false;
            // TryParse is lenient ("1" → 0.0.0.1, "1.2" → 1.0.0.2); netip is not.
            if (ip.AddressFamily == AddressFamily.InterNetwork && addr.Count(c => c == '.') != 3) return false;
            int bits;
            if (!int.TryParse(v.Substring(slash + 1), out bits)) return false;
            int max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            return bits >= 0 && bits <= max;
        }

        private static readonly HashSet<string> _droppedLogged = new HashSet<string>(StringComparer.Ordinal);

        private static void NoteDroppedPrefix(string where, string value)
        {
            lock (_droppedLogged)
                if (!_droppedLogged.Add(where + "|" + value)) return;   // once per value per run
            Log.Info("Конфиг: пропущен неверный адрес «" + value + "» (" + where + ") — "
                     + "с ним движок отказался бы от конфига целиком.");
        }

        // True if the user routes an RDP client through the VPN (mstsc/msrdc is a VPN-flagged
        // app). On such a machine we don't auto-exclude RDP peers — RDP is meant to go via VPN.
        private static bool RdpClientIsRouted(AppConfig cfg)
        {
            var rdp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mstsc.exe", "msrdc.exe" };
            return (cfg?.Apps ?? new List<AppEntry>())
                .Where(a => a != null && a.RequiresVpn)
                .SelectMany(a => a.Programs ?? new List<RuleProgram>())
                .Any(p => p != null && rdp.Contains(ProcessImageName(p)));
        }

        // Base config with no proxies — everything goes DIRECT. Used only as a neutral
        // fallback; prefer BuildArmed so VPN apps are never allowed out directly.
        public static string BuildBase(AppConfig cfg)
        {
            return Build(cfg, new List<ParsedProxy>(), new List<RuleDef>());
        }

        // "Armed" kill-switch config: every VPN-flagged app/address is routed to REJECT
        // (blocked) with no proxies. This is the idle state — mihomo starts in it (the
        // launcher writes it at startup), so VPN apps have NO internet until "Подключить"
        // assigns them a working node. Whatever no variant unblocks stays blocked here.
        public static string BuildArmed(AppConfig cfg)
        {
            return BuildProbe(cfg, new List<ParsedProxy>());
        }

        // The armed config PLUS every variant under `proxies:` — what the pre-connect
        // liveness check runs on. No rule points at those proxies, and mihomo dials a proxy
        // only when something routes to it, so they cost nothing until /proxies/{name}/delay
        // asks each one directly. That's what lets all variants be checked after ONE reload
        // instead of one reload per variant, and the kill-switch stays armed throughout —
        // a VPN app must not slip out directly just because we're busy measuring.
        public static string BuildProbe(AppConfig cfg, List<ParsedProxy> proxies)
        {
            var rules = new List<RuleDef>();
            // Only apps that opted into the kill-switch are blocked while idle. A VPN app
            // with KillSwitch=false "fails open" — it has no rule here, so it falls through
            // to MATCH,DIRECT until «Подключить» routes it through a node.
            //
            // Regardless of AutoConnect: the kill-switch means "this rule never reaches the
            // internet without the VPN". A rule that is switched off is not on a node, so it is
            // blocked — exactly the leak the switch exists to prevent (user's decision 2026-09-14).
            //
            // The one exception is «Весь трафик»: switched off it must block nothing, or turning it
            // off would take the whole machine's internet with it.
            foreach (var app in (cfg.Apps ?? new List<AppEntry>()).Where(a => a != null && a.RequiresVpn && a.KillSwitch
                                                                             && (!a.IsAllTraffic || a.AutoConnect)))
                AddAppRules(rules, app, "REJECT");
            return Build(cfg, proxies ?? new List<ParsedProxy>(), rules);
        }

        // The same variant may reach Build from two directions at once — once because a rule is
        // already connected through it, once because the sweep is trying it for the rules that
        // are not — and the second copy is what the engine refuses. First one wins; order is
        // preserved, since the rules that follow name proxies in this order.
        private static List<ParsedProxy> DistinctByName(List<ParsedProxy> proxies)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ParsedProxy>();
            foreach (var p in proxies)
            {
                if (p == null) continue;
                if (!string.IsNullOrEmpty(p.ProxyName) && !seen.Add(p.ProxyName)) continue;
                result.Add(p);
            }
            return result;
        }

        // Add the routing rules for a whole RULE pointed at `target` (a proxy name, or
        // REJECT/DIRECT): every contained program routes by process image name, every
        // contained site routes by its domain + its cached blackmatrix7 rule list, and the
        // standalone CheckUrl host is routed too. Deduped so repeated hosts don't pile up.
        public static void AddAppRules(List<RuleDef> rules, AppEntry app, string target)
        {
            if (app == null || string.IsNullOrEmpty(target)) return;

            // «Весь трафик» claims nothing by name: it is the catch-all. Build moves it to the
            // very end, so every other rule still wins for what it lists.
            if (app.IsAllTraffic)
            {
                rules.Add(new RuleDef("MATCH", "", target));
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRule(string type, string value)
            {
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(value)) return;
                if (!seen.Add(type + "|" + value)) return;
                rules.Add(new RuleDef(type, value, target));
            }

            foreach (var prog in app.Programs ?? new List<RuleProgram>())
            {
                var exe = ProcessImageName(prog);
                if (!string.IsNullOrEmpty(exe)) AddRule("PROCESS-NAME", exe);
            }

            foreach (var site in app.Sites ?? new List<RuleSite>())
            {
                if (site == null) continue;
                AddRule("DOMAIN-SUFFIX", HostOf(site.Url));

                // Cached rule list (domains/IPs) — route all of it through the same target so
                // the whole service goes via the VPN, not just the probe host. "TYPE,VALUE".
                foreach (var item in site.RuleItems ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    int c = item.IndexOf(',');
                    if (c <= 0 || c >= item.Length - 1) continue;
                    AddRule(item.Substring(0, c).Trim(), item.Substring(c + 1).Trim());
                }
            }

            AddRule("DOMAIN-SUFFIX", HostOf(ResolveCheckUrl(app)));
        }

        // The address used to verify (and route) a rule: the explicit CheckUrl, else the
        // first site's Url.
        public static string ResolveCheckUrl(AppEntry app)
        {
            if (app == null) return "";
            if (!string.IsNullOrWhiteSpace(app.CheckUrl)) return app.CheckUrl.Trim();
            var firstSite = (app.Sites ?? new List<RuleSite>()).FirstOrDefault(s => s != null && !string.IsNullOrWhiteSpace(s.Url));
            return firstSite != null ? firstSite.Url.Trim() : "";
        }

        // One log line: where every VPN rule actually goes under THIS config text — read back from
        // what the engine was handed, not from what the launcher meant to write. "Заблокировал
        // правило без кил-свитча" could not be traced before: app.log said only "конфиг
        // перезагружен", and config.yaml is overwritten on the next reload. A REJECT on a rule whose
        // kill-switch is off must never happen, so it is spelled out as the anomaly it is.
        public static string DescribeRouting(AppConfig cfg, string yaml)
        {
            // "TYPE,VALUE" → target of the FIRST rule with that key: the engine stops at the first
            // match, so an item shared by two rules goes wherever the earlier one sends it.
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string catchAll = "DIRECT";
            bool inRules = false;
            foreach (var raw in (yaml ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("rules:")) { inRules = true; continue; }
                if (!inRules) continue;
                var t = line.Trim();
                if (!t.StartsWith("- ")) continue;
                // SUB-RULE,(TYPE,VALUE[,no-resolve]),name — the rule follows that subscription.
                if (t.StartsWith("- SUB-RULE,(", StringComparison.Ordinal))
                {
                    int close = t.LastIndexOf("),", StringComparison.Ordinal);
                    if (close < 0) continue;
                    var inner = t.Substring(12, close - 12);
                    var sub = "SUB:" + t.Substring(close + 2).Trim();
                    if (inner == SubscriptionPolicy.CatchAllMatcher.Substring(1, SubscriptionPolicy.CatchAllMatcher.Length - 2))
                    { catchAll = sub; continue; }
                    var ip = inner.Split(',');
                    if (ip.Length >= 2 && !effective.ContainsKey(ip[0].Trim() + "," + ip[1].Trim()))
                        effective[ip[0].Trim() + "," + ip[1].Trim()] = sub;
                    continue;
                }
                var parts = t.Substring(2).Split(',');
                if (parts.Length == 2 && parts[0].Trim() == "MATCH")
                {
                    if (!catchAll.StartsWith("SUB:", StringComparison.Ordinal)) catchAll = parts[1].Trim();
                    continue;
                }
                if (parts.Length < 3) continue;
                var key = parts[0].Trim() + "," + parts[1].Trim();
                if (!effective.ContainsKey(key)) effective[key] = parts[2].Trim();
            }

            var described = new List<string>();
            foreach (var app in (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && a.RequiresVpn))
            {
                if (app.IsAllTraffic)
                {
                    // Only worth a word when it is actually doing something.
                    if (catchAll.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;
                    described.Add(app.Name + " → " + (catchAll.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)
                        ? (app.KillSwitch ? "заблокировано (кил-свитч)" : "ЗАБЛОКИРОВАНО, хотя кил-свитч выключен")
                        : catchAll.StartsWith("SUB:", StringComparison.Ordinal)
                        ? "по правилам подписки «" + catchAll.Substring(4) + "»"
                        : "VPN «" + catchAll + "»"));
                    continue;
                }
                var mine = new List<RuleDef>();
                AddAppRules(mine, app, "?");
                if (mine.Count == 0) continue;

                var groups = mine
                    .Select(r => { string tg; return effective.TryGetValue(r.Type + "," + r.Value, out tg) ? tg : "DIRECT"; })
                    .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                string Where(string target)
                {
                    if (target.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return "напрямую";
                    if (target.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase))
                        return app.KillSwitch ? "заблокировано (кил-свитч)" : "ЗАБЛОКИРОВАНО, хотя кил-свитч выключен";
                    if (target.StartsWith("SUB:", StringComparison.Ordinal))
                        return "по правилам подписки «" + target.Substring(4) + "»";
                    return "VPN «" + target + "»";
                }

                described.Add(groups.Count == 1
                    ? app.Name + " → " + Where(groups[0].Key)
                    : app.Name + " → " + string.Join(", ", groups.Select(g => Where(g.Key) + " (" + g.Count() + " из " + mine.Count + ")")));
            }
            return described.Count == 0 ? "" : "Маршруты: " + string.Join("; ", described);
        }

        // Rewritten only when the text differs, so building a config twice touches nothing.
        private static void WriteProviderFile(AppConfig cfg, string fileName, string content)
        {
            try
            {
                var dir = System.IO.Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), "providers");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, fileName);
                var text = content.Trim() + "\n";
                if (System.IO.File.Exists(path) && System.IO.File.ReadAllText(path) == text) return;
                System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("provider-file " + fileName, ex); }
        }

        public static string HostOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try { return new Uri(url).Host; } catch { return ""; }
        }

        private static string ProcessImageName(RuleProgram prog)
        {
            if (prog == null || string.IsNullOrEmpty(prog.Path)) return "";
            try { return System.IO.Path.GetFileName(prog.Path); } catch { return ""; }
        }

        private static bool IsUrl(string s)
        {
            return !string.IsNullOrEmpty(s) &&
                   (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExtractUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            foreach (var tok in text.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries))
                if (IsUrl(tok)) return tok;
            return "";
        }

        // Names that must NEVER be answered from the fake-ip pool. A fake 198.18.x for these
        // breaks things that have nothing to do with the VPN: the router/NAS/printer by name,
        // mDNS, WPAD, and — most visibly — Windows' own connectivity probe, which decides the
        // machine is offline and puts the globe on the tray icon. `route-exclude-address`
        // can't help here: it filters by IP, and the fake address isn't in those ranges.
        private static readonly string[] FakeIpFilter =
        {
            "*.lan", "*.local", "+.local", "+.home.arpa", "+.internal",
            "*.msftconnecttest.com", "*.msftncsi.com",     // Windows connectivity check
            "time.windows.com", "time.nist.gov",           // NTP
            "wpad.*", "localhost.*",
        };

        // The launcher does NOT choose this machine's resolvers (the "DNS" feature was removed
        // 2026-08-15). Everything the rules don't claim is forwarded to whatever Windows was
        // already using — so a DoH host the ISP throttles, or a resolver that only answers
        // inside a dead tunnel, can no longer make the WHOLE machine feel slow while we run.
        // The dns: block itself stays: under TUN, fake-ip is the only way mihomo can recover
        // a hostname from an intercepted packet, i.e. the only way domain rules match at all.
        private static void AppendDns(StringBuilder sb, AppConfig cfg, List<ParsedProxy> proxies)
        {
            sb.AppendLine("dns:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  ipv6: false");
            sb.AppendLine("  enhanced-mode: fake-ip");
            sb.AppendLine("  fake-ip-range: 198.18.0.1/16");
            sb.AppendLine("  fake-ip-filter:");
            foreach (var f in FakeIpFilter)
                sb.AppendLine("    - \"" + f + "\"");
            // The machine's real resolvers, with our own tunnel taken out — see
            // MachineNameservers. Rule domains never get here: fake-ip answers them from the
            // pool and the name goes to the proxy, which resolves it on the far side.
            var upstream = MachineNameservers();
            LastUpstream = upstream;
            LogUpstreamIfChanged(upstream);
            sb.AppendLine("  nameserver:");
            foreach (var ns in upstream)
                sb.AppendLine("    - \"" + ns + "\"");
            // Resolving the node's own address can't go through the tunnel that address is
            // needed to build — same servers, stated explicitly so a future edit can't break it.
            sb.AppendLine("  proxy-server-nameserver:");
            foreach (var ns in upstream)
                sb.AppendLine("    - \"" + ns + "\"");

            // The user's own resolver — for DIRECT connections ONLY (DirectDns explains why the
            // whole-machine `nameserver` above stays the machine's). Rule domains routed to a node
            // never reach it, so VPN traffic can't be held up by it.
            var direct = DirectDns.Active(cfg);
            LastDirectDns = direct.Select(e => e.Engine).ToList();
            LastDirectDnsHosts = direct.Select(e => e.Host).ToList();
            LogDirectDnsIfChanged(direct);
            if (direct.Count > 0)
            {
                sb.AppendLine("  direct-nameserver:");
                foreach (var e in direct)
                    sb.AppendLine("    - \"" + e.Engine.Replace("\"", "") + "\"");
                // A resolver addressed by NAME is dead without a bootstrap (measured — see the DNS
                // gotchas): the machine's own servers find its address.
                if (direct.Any(e => !e.IsIp))
                {
                    sb.AppendLine("  default-nameserver:");
                    foreach (var ns in DirectDns.BootstrapServers())
                        sb.AppendLine("    - \"" + ns + "\"");
                }
            }
            sb.AppendLine();
        }

        // What the last config handed to `direct-nameserver` — empty when the user has none. The
        // traffic log reads it to say "your DNS refused this", not "the machine's DNS".
        public static List<string> LastDirectDns { get; private set; } = new List<string>();
        public static List<string> LastDirectDnsHosts { get; private set; } = new List<string>();

        private static string _loggedDirect;

        private static void LogDirectDnsIfChanged(List<DnsEntry> direct)
        {
            try
            {
                var text = string.Join(", ", direct.Select(e => e.Engine));
                if (text == _loggedDirect) return;
                _loggedDirect = text;
                Log.Info(direct.Count == 0
                    ? "DNS: для прямого трафика — DNS компьютера (своего не задано)."
                    : "DNS: прямой трафик резолвится через " + text + " (только он; VPN и остальные запросы — как прежде).");
            }
            catch { }
        }

        // The resolvers this machine actually uses, with OUR OWN tunnel removed.
        //
        // `nameserver: "system"` reads like the safe choice and is a trap. mihomo puts its own
        // DNS address (198.18.0.2) on the TUN adapter it raises; Windows then lists that
        // adapter among the machine's resolvers; and "system" therefore hands the engine its
        // own address back. Lookups loop inside the engine and NOTHING on the machine resolves
        // — browser, mail, and the launcher itself — for as long as the engine runs. Closing
        // the launcher appears to "fix" it only because the TUN disappears with it, which is
        // exactly how this was reported: «не работает, пока не закрою лаунчер».
        //
        // Measured on the live machine 2026-08-31:
        //     Meta      198.18.0.2      <- the engine's own DNS
        //     Ethernet  192.168.100.1   <- the only real one
        // and in the same log, with no reload anywhere near it:
        //     [ERROR] Невозможно разрешить удаленное имя: 'api.github.com'
        //
        // 127.0.0.1 is deliberately KEPT: that is a local filtering resolver the user installed
        // on purpose (NextDNS, AdGuard, Pi-hole) and it has to keep working. Only the fake-ip
        // range is dropped, because an address in it can only ever be us.
        // The resolvers written into the most recent config, null until one is built. The
        // list is read ONCE, at build time, and the machine does not hold still: plug in a
        // cable, join another Wi-Fi, a DHCP renew — the engine goes on asking a server that
        // is no longer there, and under fake-ip that is every name on the machine, "напрямую"
        // included (measured 2026-09-30: config said 10.206.67.136, adapter said
        // 192.168.100.1, the old one timed out). The watchdog compares against this.
        public static List<string> LastUpstream { get; private set; }

        // What a config built right now would get, without the fallback's log line — the
        // watchdog asks every 20 s and must not repeat it each time.
        public static List<string> CurrentMachineUpstream() => MachineNameservers(quiet: true);

        private static List<string> MachineNameservers(bool quiet = false)
        {
            var list = new List<string>();
            bool droppedOurOwn = false;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }
                    foreach (var dns in props.DnsAddresses)
                    {
                        // dns.ipv6 is false above, so a v6 resolver would only add latency.
                        if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (InFakeIpRange(dns)) { droppedOurOwn = true; continue; }   // that server IS us
                        var text = dns.ToString();
                        if (!list.Contains(text)) list.Add(text);
                    }
                }
            }
            catch { }
            if (list.Count > 0) return list;

            // Nothing left. WHY it is empty decides what is safe here, and the two cases are
            // opposites:
            //
            //   we dropped one of ours  -> every resolver Windows knows about is the engine.
            //       "system" would hand us straight back into the loop this method exists to
            //       break, so it is the one answer that is guaranteed wrong. Public resolvers
            //       are a worse default for privacy but they are the only ones that answer.
            //   we dropped nothing      -> we simply could not read any adapter (no link yet at
            //       logon, an exotic stack). No tunnel of ours is up, so "system" cannot point
            //       at us, and the machine's own choice stays the machine's own choice.
            if (droppedOurOwn)
            {
                if (!quiet)
                Log.Info("DNS: единственным резолвером машины оказался наш же туннель — "
                         + "так браузер не откроет ни одной страницы, пока работает движок. "
                         + "Беру запасные 1.1.1.1 и 8.8.8.8.");
                list.Add("1.1.1.1");
                list.Add("8.8.8.8");
            }
            else list.Add("system");
            return list;
        }

        // What the machine will actually resolve through, written to app.log whenever it
        // CHANGES (a config is built far too often to log every time).
        //
        // This line exists because its absence cost weeks. The engine-asks-itself bug lived in
        // the generated config, and the log described everything EXCEPT the config: whether TUN
        // was up, which routes were rewritten, which rules connected — never which resolvers
        // were handed to the engine. The symptom («ничего не открывается, пока не закрою
        // лаунчер») was therefore unattributable from the log alone, and was found only by
        // interrogating a live machine's adapters. One line here would have shown it at once.
        private static string _loggedUpstream;

        private static void LogUpstreamIfChanged(List<string> upstream)
        {
            try
            {
                var text = string.Join(", ", upstream);
                if (text == _loggedUpstream) return;
                _loggedUpstream = text;
                Log.Info("DNS: имена для всей машины резолвятся через " + text
                         + (upstream.Contains("system")
                            ? "  (адаптеры прочитать не удалось — отдаю на откуп системе)"
                            : ""));
            }
            catch { }
        }

        private static bool InFakeIpRange(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 && b[0] == 198 && b[1] == 18;
        }

        public static string Build(AppConfig cfg, List<ParsedProxy> proxies, List<RuleDef> rules)
        {
            if (proxies == null) proxies = new List<ParsedProxy>();
            if (rules == null) rules = new List<RuleDef>();

            // One name, one entry. Measured on the binary: two proxies with the same name make
            // mihomo refuse the WHOLE config — «proxy Turkey is the duplicate name» — so PUT
            // /configs answers 400 and the engine goes on running the previous one. Nothing is
            // lost by collapsing them: the same variant parsed twice yields identical YAML, and
            // rules reference it by name either way. Done here, in the one place every config
            // goes through, because a caller that accidentally lists a variant twice must not be
            // able to take the entire connect down with it.
            proxies = DistinctByName(proxies);

            string api = string.IsNullOrWhiteSpace(cfg.MihomoApiAddr) ? "127.0.0.1:9090" : cfg.MihomoApiAddr.Trim();
            int mixed = cfg.MixedPort > 0 ? cfg.MixedPort : 7890;

            var sb = new StringBuilder();
            sb.AppendLine("# Generated by MihomoLauncher — do not edit by hand.");
            sb.AppendLine("mixed-port: " + mixed);
            sb.AppendLine("allow-lan: false");
            sb.AppendLine("mode: rule");
            sb.AppendLine("log-level: warning");
            // Always resolve the owning process so PROCESS-NAME rules (the kill-switch)
            // reliably match under TUN.
            sb.AppendLine("find-process-mode: always");
            sb.AppendLine("external-controller: " + api);
            if (!string.IsNullOrEmpty(cfg.MihomoSecret))
                sb.AppendLine("secret: \"" + cfg.MihomoSecret.Replace("\"", "\\\"") + "\"");
            sb.AppendLine();

            // TUN — intercept traffic; rules decide DIRECT vs proxy. We DON'T route local
            // networks or a live Remote Desktop peer into TUN (route-exclude-address): the
            // kill-switch/VPN only needs the listed apps' PUBLIC traffic, and capturing the
            // LAN/RDP path otherwise drops the remote-desktop session and glitches the LAN.
            sb.AppendLine("tun:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  stack: mixed");
            sb.AppendLine("  auto-route: true");
            sb.AppendLine("  auto-detect-interface: true");
            sb.AppendLine("  strict-route: false");
            sb.AppendLine("  dns-hijack:");
            sb.AppendLine("    - any:53");
            sb.AppendLine("  route-exclude-address:");
            foreach (var cidr in TunRouteExcludes(cfg))
                if (IsValidPrefix(cidr)) sb.AppendLine("    - " + cidr);
                else NoteDroppedPrefix("route-exclude-address", cidr);
            sb.AppendLine();

            AppendDns(sb, cfg, proxies);

            // Keep the fake-ip table across reloads — the single most damaging thing this file
            // used to get wrong, and it had nothing to do with the rules.
            //
            // Under fake-ip EVERY name gets a 198.18.x.x answer (not just the ones our rules
            // claim — only the fake-ip-filter list above is exempt), and Windows and the browser
            // cache those answers. The table mapping them back to names lived in the engine's
            // memory alone, so every hot-reload threw it away — and a connect performs one per
            // variant. After that, a cached 198.18.x either belonged to nothing or, worse, to a
            // DIFFERENT name: the site simply stopped opening, with no error anywhere and no
            // relation to whether it was in a rule at all. It came back only when something
            // flushed the DNS cache — in practice, restarting the launcher.
            //
            // With this on, the table is persisted (cache.db in the work dir) and survives both
            // reloads and engine restarts. Every Clash GUI ships it on for exactly this reason.
            // This does NOT choose resolvers or change what answers a query: the DNS feature is
            // gone, `nameserver: system` above stands.
            sb.AppendLine("profile:");
            sb.AppendLine("  store-fake-ip: true");
            sb.AppendLine();

            // Recover the domain from the CONNECTION, not from DNS.
            //
            // Every domain rule above depends on the engine knowing which name a packet is going
            // to, and until now that knowledge came from one place: our own fake-ip answer. Any
            // program that resolves names by itself therefore walked straight past the rules —
            // the engine saw a bare IP, matched nothing, and MATCH,DIRECT sent it out unprotected
            // while the launcher reported everything connected. That is not an exotic case:
            // NextDNS/AdGuard desktop apps replace the system resolver machine-wide, and Chrome
            // and Firefox ship DoH switched on. One user's machine had exactly this and NOTHING
            // in the rules worked.
            //
            // Sniffing reads the name out of the TLS ClientHello (SNI) and the HTTP Host header,
            // so rules match whoever did the resolving. `parse-pure-ip` is the half that covers
            // the case above — a connection opened straight to an IP with no name behind it;
            // `override-destination` rewrites the destination to the sniffed name so the rules
            // (and the traffic log) see it. The standard answer in this ecosystem: mihomo,
            // sing-box and every Clash GUI ship it on.
            sb.AppendLine("sniffer:");
            sb.AppendLine("  enable: true");
            sb.AppendLine("  parse-pure-ip: true");
            sb.AppendLine("  force-dns-mapping: true");
            sb.AppendLine("  override-destination: true");
            sb.AppendLine("  sniff:");
            sb.AppendLine("    HTTP:");
            sb.AppendLine("      ports: [80, 8080, 8880]");
            sb.AppendLine("    TLS:");
            sb.AppendLine("      ports: [443, 8443]");
            sb.AppendLine("    QUIC:");
            sb.AppendLine("      ports: [443, 8443]");
            sb.AppendLine();

            // proxies: (WireGuard / VLESS entries)
            var directProxies = new List<ParsedProxy>();
            var providers = new List<ParsedProxy>();
            foreach (var p in proxies)
            {
                if (p == null || !p.Ok) continue;
                if (p.IsProvider) providers.Add(p);
                else directProxies.Add(p);
            }

            if (directProxies.Count > 0)
            {
                sb.AppendLine("proxies:");
                foreach (var p in directProxies)
                    foreach (var line in p.ProxyYamlLines)
                        sb.AppendLine(line);
                sb.AppendLine();
            }

            // proxy-providers: (Clash subscriptions)
            if (providers.Count > 0)
            {
                sb.AppendLine("proxy-providers:");
                foreach (var p in providers)
                {
                    sb.AppendLine("  " + p.ProxyName + "_prov:");
                    if (!string.IsNullOrEmpty(p.ProviderFileContent))
                    {
                        // A single share link: a local one-line file the engine converts itself.
                        // Relative to the work dir — mihomo refuses provider paths outside it.
                        WriteProviderFile(cfg, p.ProxyName + ".txt", p.ProviderFileContent);
                        sb.AppendLine("    type: file");
                        sb.AppendLine("    path: ./providers/" + p.ProxyName + ".txt");
                    }
                    else
                    {
                        sb.AppendLine("    type: http");
                        sb.AppendLine("    url: \"" + p.ProviderUrl.Replace("\"", "\\\"") + "\"");
                        sb.AppendLine("    path: ./providers/" + p.ProxyName + ".yaml");
                        sb.AppendLine("    interval: " + (p.ProviderIntervalSeconds > 0 ? p.ProviderIntervalSeconds : 48 * 3600));
                    }
                    sb.AppendLine("    health-check:");
                    sb.AppendLine("      enable: true");
                    sb.AppendLine("      url: " + HealthCheckUrl);
                    sb.AppendLine("      interval: 300");
                }
                sb.AppendLine();
            }

            // proxy-groups: only providers need a group (url-test over the sub).
            // Direct proxies are referenced by name in rules, no group needed.
            if (providers.Count > 0)
            {
                sb.AppendLine("proxy-groups:");
                foreach (var p in providers)
                {
                    sb.AppendLine("  - name: " + p.ProxyName);
                    sb.AppendLine("    type: url-test");
                    sb.AppendLine("    url: " + HealthCheckUrl);
                    sb.AppendLine("    interval: 300");
                    sb.AppendLine("    use:");
                    sb.AppendLine("      - " + p.ProxyName + "_prov");
                }
                sb.AppendLine();
            }

            // A tile pointed at a subscription follows the subscription's own rules — VPN, direct
            // or blocked, as the issuer set them — not "everything through the node". See
            // SubscriptionPolicy. Only for subscriptions some tile actually points at.
            var subscribed = providers
                .Where(p => string.IsNullOrEmpty(p.ProviderFileContent)
                            && rules.Any(r => r != null && !r.Raw && r.Target == p.ProxyName))
                .Select(p => p.ProxyName).ToList();
            var policies = new Dictionary<string, SubPolicy>(StringComparer.Ordinal);
            foreach (var n in subscribed)
            {
                var pol = SubscriptionPolicy.For(cfg, n);
                if (pol != null) policies[n] = pol;
            }
            SubscriptionPolicy.NoteBuilt(cfg, subscribed);
            SubscriptionPolicy.AppendBlocks(sb, policies.Values);

            // rules: split entries first, the catch-all last — DIRECT, unless «Весь трафик» is
            // on, in which case its target (a proxy, or REJECT under its kill-switch).
            string catchAll = "DIRECT";
            sb.AppendLine("rules:");
            foreach (var r in rules)
            {
                if (r == null || string.IsNullOrEmpty(r.Type) || string.IsNullOrEmpty(r.Target)) continue;
                if (r.Type.Equals("MATCH", StringComparison.OrdinalIgnoreCase)) { catchAll = r.Target; continue; }
                // IP rules must skip DNS resolution under fake-ip, or they never match.
                bool ipRule = r.Type.StartsWith("IP-CIDR", StringComparison.OrdinalIgnoreCase);
                if (ipRule && !IsValidPrefix(r.Value)) { NoteDroppedPrefix(r.Type, r.Value); continue; }
                var suffix = ipRule ? ",no-resolve" : "";
                // The matcher sits in parentheses there, so a value with its own brackets or commas
                // can't be put into one; it goes through the node as before, and says so.
                if (!r.Raw && policies.ContainsKey(r.Target))
                {
                    if (r.Value.IndexOfAny(new[] { '(', ')', ',' }) < 0)
                    {
                        sb.AppendLine("  - SUB-RULE,(" + r.Type + "," + r.Value + suffix + ")," + r.Target);
                        continue;
                    }
                    lock (_droppedLogged)
                        if (_droppedLogged.Add("sub|" + r.Value))
                            Log.Info("Конфиг: «" + r.Value + "» не помещается в правило подписки «" + r.Target
                                     + "» (скобки или запятая в имени) — идёт целиком через VPN.");
                }
                sb.AppendLine("  - " + r.Type + "," + r.Value + "," + r.Target + suffix);
            }
            // With the catch-all pointed anywhere but DIRECT, a rule that wrote NO line of its own
            // (a «напрямую» rule, or one switched off without a kill-switch) would be swept into
            // «Весь трафик». It isn't its business: every tile lives its own life, so whatever
            // is left of them is pinned DIRECT explicitly, just above the catch-all.
            if (!catchAll.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
            {
                var written = new HashSet<string>(
                    rules.Where(r => r != null && !string.IsNullOrEmpty(r.Type)).Select(r => r.Type + "|" + r.Value),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var app in (cfg.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.IsAllTraffic))
                {
                    var mine = new List<RuleDef>();
                    AddAppRules(mine, app, "DIRECT");
                    foreach (var r in mine)
                    {
                        if (!written.Add(r.Type + "|" + r.Value)) continue;
                        bool ipRule = r.Type.StartsWith("IP-CIDR", StringComparison.OrdinalIgnoreCase);
                        if (ipRule && !IsValidPrefix(r.Value)) { NoteDroppedPrefix(r.Type, r.Value); continue; }
                        sb.AppendLine("  - " + r.Type + "," + r.Value + ",DIRECT" + (ipRule ? ",no-resolve" : ""));
                    }
                }
            }
            // «Весь трафик» on a subscription: everything left over follows the subscription.
            // `SUB-RULE,(MATCH),…` is refused by the engine; TCP-or-UDP is what matches all.
            if (policies.ContainsKey(catchAll))
            {
                sb.AppendLine("  - SUB-RULE," + SubscriptionPolicy.CatchAllMatcher + "," + catchAll);
                catchAll = "DIRECT";
            }
            sb.AppendLine("  - MATCH," + catchAll);

            return sb.ToString();
        }
    }
}
