using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // «Куда пойдёт этот адрес и кто его резолвит» — the search line of the traffic log.
    //
    // Answered from the LIVE config.yaml, not from the rules as the launcher meant them: the file
    // is what the engine was handed, so the answer is the engine's answer. The rules are walked
    // in order with first-match semantics, the way mihomo walks them, including what a subscription
    // brings along (SUB-RULE → its sub-rules block → its rule lists on disk). A rule that depends on
    // something only a live connection knows — which program opened it, which country an IP is in
    // — is not guessed at: it becomes its own branch («если открывает chrome.exe…», «возможно…»).
    public static class RouteExplainer
    {
        // ---------- the live config, read back ----------

        private sealed class Provider
        {
            public string Behavior = "classical";
            public string Format = "yaml";
            public string Path = "";
            public List<string> Inline;
        }

        private sealed class ProxyInfo
        {
            public string Type = "";
            public bool RemoteDns;
            public List<string> Dns = new List<string>();
        }

        private sealed class Live
        {
            public string WorkDir = "";
            public List<string> Rules = new List<string>();
            public Dictionary<string, List<string>> SubRules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public Dictionary<string, Provider> Providers = new Dictionary<string, Provider>(StringComparer.Ordinal);
            public Dictionary<string, ProxyInfo> Proxies = new Dictionary<string, ProxyInfo>(StringComparer.Ordinal);
            public HashSet<string> Groups = new HashSet<string>(StringComparer.Ordinal);
            public List<string> Nameserver = new List<string>();
            public List<string> DirectNs = new List<string>();
            public List<string> FakeIpFilter = new List<string>();
            public List<string> RouteExclude = new List<string>();
            public bool FakeIp;
        }

        private static string ConfigPath(AppConfig cfg)
            => System.IO.Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), "config.yaml");

        private static Live Read(AppConfig cfg)
        {
            var path = ConfigPath(cfg);
            if (!File.Exists(path)) return null;
            var live = new Live { WorkDir = AppConfigStore.ResolveMihomoWorkDir(cfg) };

            string section = null, sub = null, list = null;
            Provider prov = null;
            ProxyInfo proxy = null;
            bool inPayload = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                int ind = line.Length - line.TrimStart().Length;
                var t = line.Trim();

                if (ind == 0)
                {
                    int c = t.IndexOf(':');
                    section = c > 0 ? t.Substring(0, c) : t;
                    sub = null; list = null; prov = null; proxy = null; inPayload = false;
                    continue;
                }

                switch (section)
                {
                    case "rules":
                        if (t.StartsWith("- ")) live.Rules.Add(Unquote(t.Substring(2)));
                        break;

                    case "sub-rules":
                        if (ind == 2 && t.EndsWith(":")) { sub = Unquote(t.TrimEnd(':')); live.SubRules[sub] = new List<string>(); }
                        else if (sub != null && t.StartsWith("- ")) live.SubRules[sub].Add(Unquote(t.Substring(2)));
                        break;

                    case "rule-providers":
                        if (ind == 2)
                        {
                            int c = t.IndexOf(':');
                            if (c <= 0) break;
                            var key = Unquote(t.Substring(0, c));
                            prov = new Provider();
                            live.Providers[key] = prov;
                            inPayload = false;
                            var rest = t.Substring(c + 1).Trim();
                            if (rest.StartsWith("{"))
                            {
                                foreach (var kv in SplitTop(rest.Trim('{', '}'), ','))
                                    SetProviderField(prov, kv);
                                prov = null;
                            }
                        }
                        else if (prov != null)
                        {
                            if (t.StartsWith("payload:")) { inPayload = true; prov.Inline = new List<string>(); continue; }
                            if (inPayload && t.StartsWith("- ")) { prov.Inline.Add(Unquote(t.Substring(2))); continue; }
                            if (ind == 4) { inPayload = false; SetProviderField(prov, t); }
                        }
                        break;

                    case "proxies":
                        if (t.StartsWith("- name:"))
                        {
                            proxy = new ProxyInfo();
                            live.Proxies[Unquote(t.Substring(7))] = proxy;
                        }
                        else if (proxy != null)
                        {
                            if (t.StartsWith("type:")) proxy.Type = Unquote(t.Substring(5)).ToLowerInvariant();
                            else if (t.StartsWith("remote-dns-resolve:")) proxy.RemoteDns = t.EndsWith("true");
                            else if (t.StartsWith("dns:"))
                                proxy.Dns = t.Substring(4).Trim().Trim('[', ']').Split(',')
                                             .Select(Unquote).Where(x => x.Length > 0).ToList();
                        }
                        break;

                    case "proxy-groups":
                        if (t.StartsWith("- name:")) live.Groups.Add(Unquote(t.Substring(7)));
                        break;

                    case "dns":
                        if (ind == 2)
                        {
                            list = t.EndsWith(":") ? t.TrimEnd(':') : null;
                            if (t.StartsWith("enhanced-mode:")) live.FakeIp = t.Contains("fake-ip");
                        }
                        else if (list != null && t.StartsWith("- "))
                        {
                            var v = Unquote(t.Substring(2));
                            if (list == "nameserver") live.Nameserver.Add(v);
                            else if (list == "direct-nameserver") live.DirectNs.Add(v);
                            else if (list == "fake-ip-filter") live.FakeIpFilter.Add(v);
                        }
                        break;

                    case "tun":
                        if (ind == 2) list = t.EndsWith(":") ? t.TrimEnd(':') : null;
                        else if (list == "route-exclude-address" && t.StartsWith("- ")) live.RouteExclude.Add(Unquote(t.Substring(2)));
                        break;
                }
            }
            return live;
        }

        private static void SetProviderField(Provider p, string kv)
        {
            int c = kv.IndexOf(':');
            if (c <= 0) return;
            var k = kv.Substring(0, c).Trim();
            var v = Unquote(kv.Substring(c + 1));
            if (k == "behavior") p.Behavior = v.ToLowerInvariant();
            else if (k == "format") p.Format = v.ToLowerInvariant();
            else if (k == "path") p.Path = v;
        }

        private static string Unquote(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                var q = s[0];
                s = s.Substring(1, s.Length - 2);
                s = q == '\'' ? s.Replace("''", "'") : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }

        // Split on `sep` outside parentheses: "AND,((A,x),(B,y)),target" → 3 parts.
        private static List<string> SplitTop(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                else if (ch == sep && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static string StripParens(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("(") && s.EndsWith(")")) s = s.Substring(1, s.Length - 2);
            return s;
        }

        // ---------- the question ----------

        private sealed class Ctx
        {
            public string Host;          // a name, or null
            public IPAddress Ip;         // an address, or null
            public int Port;             // 0 = not given
            public string Process;       // null = any program
            public Live Live;
            public List<string> ResolveRules = new List<string>();   // IP rules that make the engine look the name up
            public string ListLabel;     // the list being read, for those notes
            public int Depth;
            public Ctx With(string process) => new Ctx
            {
                Host = Host, Ip = Ip, Port = Port, Process = process, Live = Live,
                ResolveRules = ResolveRules, Depth = Depth + 1,
            };
        }

        private enum CK { Never, Always, Maybe, IfProc }

        private sealed class Cond
        {
            public CK K;
            public HashSet<string> Procs;
            public string Why = "";      // for Maybe: what it depends on
            public string Hit = "";      // what matched: the rule, or the list entry
            public bool IpOnly;          // depends only on the address a name resolves to
            public static Cond Never => new Cond { K = CK.Never };
            public static Cond Always(string hit) => new Cond { K = CK.Always, Hit = hit };
            public static Cond Maybe(string why, string hit) => new Cond { K = CK.Maybe, Why = why, Hit = hit };
        }

        public sealed class Branch
        {
            public string Who;            // the program it depends on; null = any
            public bool Uncertain;
            public string UncertainWhy = "";
            public string Target = "";
            public string Rule = "";      // the config line that decided it
            public string Hit = "";       // the list entry inside it, when different
            public string Sub;            // the subscription block it was decided in
        }

        private static Cond Eval(string type, string payload, bool noResolve, Ctx ctx, string ruleText)
        {
            type = (type ?? "").Trim().ToUpperInvariant();
            payload = (payload ?? "").Trim();
            var host = ctx.Host;

            switch (type)
            {
                case "MATCH":
                    return Cond.Always(ruleText);

                case "DOMAIN":
                    return host != null && host.Equals(payload, StringComparison.OrdinalIgnoreCase) ? Cond.Always(ruleText) : Cond.Never;
                case "DOMAIN-SUFFIX":
                    return host != null && SuffixMatch(host, payload) ? Cond.Always(ruleText) : Cond.Never;
                case "DOMAIN-KEYWORD":
                    return host != null && host.IndexOf(payload, StringComparison.OrdinalIgnoreCase) >= 0 ? Cond.Always(ruleText) : Cond.Never;
                case "DOMAIN-WILDCARD":
                    return host != null && PatternMatch(payload, host) ? Cond.Always(ruleText) : Cond.Never;
                case "DOMAIN-REGEX":
                    if (host == null) return Cond.Never;
                    try { return Regex.IsMatch(host, payload, RegexOptions.IgnoreCase) ? Cond.Always(ruleText) : Cond.Never; }
                    catch { return Cond.Maybe(Loc.T("Ex_WhyRegex"), ruleText); }
                case "GEOSITE":
                    return host == null ? Cond.Never : Cond.Maybe(Loc.T("Ex_WhyGeosite", payload), ruleText);

                case "IP-CIDR":
                case "IP-CIDR6":
                    if (ctx.Ip != null) return CidrContains(payload, ctx.Ip) ? Cond.Always(ruleText) : Cond.Never;
                    return NeedsAddress(noResolve, ctx, ruleText, Loc.T("Ex_WhyCidr", payload));
                case "IP-ASN":
                {
                    if (ctx.Ip == null) return NeedsAddress(noResolve, ctx, ruleText, Loc.T("Ex_WhyAsn", payload));
                    var asn = AsnOf(ctx.Live, ctx.Ip);
                    if (asn == null) return Cond.Maybe(Loc.T("Ex_WhyAsn", payload), ruleText);
                    return asn == payload.TrimStart('A', 'S', 'a', 's') ? Cond.Always(type + "," + payload) : Cond.Never;
                }
                case "IP-SUFFIX":
                    if (ctx.Ip == null) return NeedsAddress(noResolve, ctx, ruleText, Loc.T("Ex_WhyIp", type + "," + payload));
                    return Cond.Maybe(Loc.T("Ex_WhyIp", type + "," + payload), ruleText);
                case "GEOIP":
                    if (ctx.Ip != null)
                    {
                        if (payload.Equals("LAN", StringComparison.OrdinalIgnoreCase) || payload.Equals("private", StringComparison.OrdinalIgnoreCase))
                            return IsPrivate(ctx.Ip) ? Cond.Always(ruleText) : Cond.Never;
                        string country = "";
                        try { country = GeoIpService.Lookup(ctx.Ip.ToString())?.Display ?? ""; } catch { }
                        return Cond.Maybe(Loc.T("Ex_WhyGeoip", payload, country.Length > 0 ? country : "?"), ruleText);
                    }
                    return NeedsAddress(noResolve, ctx, ruleText, Loc.T("Ex_WhyGeoip", payload, "?"));

                case "PROCESS-NAME":
                case "PROCESS-PATH":
                case "PROCESS-NAME-REGEX":
                case "PROCESS-PATH-REGEX":
                {
                    var exe = payload;
                    if (type == "PROCESS-PATH") { try { exe = System.IO.Path.GetFileName(payload); } catch { } }
                    if (type.EndsWith("-REGEX"))
                    {
                        if (ctx.Process == null) return Cond.Maybe(Loc.T("Ex_WhyProcRegex", payload), ruleText);
                        try { return Regex.IsMatch(ctx.Process, payload, RegexOptions.IgnoreCase) ? Cond.Always(ruleText) : Cond.Never; }
                        catch { return Cond.Maybe(Loc.T("Ex_WhyProcRegex", payload), ruleText); }
                    }
                    if (ctx.Process != null)
                        return ctx.Process.Equals(exe, StringComparison.OrdinalIgnoreCase) ? Cond.Always(ruleText) : Cond.Never;
                    return new Cond { K = CK.IfProc, Procs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe }, Hit = ruleText };
                }

                case "DST-PORT":
                    if (ctx.Port <= 0) return Cond.Maybe(Loc.T("Ex_WhyPort", payload), ruleText);
                    return PortMatch(payload, ctx.Port) ? Cond.Always(ruleText) : Cond.Never;
                case "NETWORK":
                    return payload.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? Cond.Always(ruleText)
                         : Cond.Maybe(Loc.T("Ex_WhyUdp"), ruleText);

                case "RULE-SET":
                    return EvalRuleSet(payload, noResolve, ctx, ruleText);

                case "AND":
                case "OR":
                case "NOT":
                {
                    var inner = SplitTop(StripParens(payload), ',')
                        .Select(x => StripParens(x))
                        .Where(x => x.Length > 0)
                        .Select(x =>
                        {
                            var p = SplitTop(x, ',');
                            bool nr = p.Skip(2).Any(o => o.Trim().Equals("no-resolve", StringComparison.OrdinalIgnoreCase));
                            return Eval(p[0], p.Count > 1 ? p[1] : "", nr, ctx, ruleText);
                        }).ToList();
                    if (type == "OR") return Or(inner, ruleText);
                    if (type == "AND") return And(inner, ruleText);
                    var one = inner.FirstOrDefault() ?? Cond.Never;
                    return one.K == CK.Always ? Cond.Never : one.K == CK.Never ? Cond.Always(ruleText)
                         : Cond.Maybe(one.Why.Length > 0 ? one.Why : Loc.T("Ex_WhyProgram"), ruleText);
                }

                default:
                    return Cond.Maybe(Loc.T("Ex_WhyOther", type), ruleText);
            }
        }

        // An IP rule asked about a NAME: with `no-resolve` it is skipped; without it the engine
        // looks the name up first — a second lookup, by a different resolver, that people never
        // expect. Noted so the answer can say so.
        private static Cond NeedsAddress(bool noResolve, Ctx ctx, string ruleText, string why)
        {
            if (ctx.Host == null || noResolve) return Cond.Never;
            var label = ctx.ListLabel ?? ruleText;
            if (!ctx.ResolveRules.Contains(label)) ctx.ResolveRules.Add(label);
            var c = Cond.Maybe(why, ruleText);
            c.IpOnly = true;
            return c;
        }

        private static Cond Or(List<Cond> all, string ruleText)
        {
            var hit = all.FirstOrDefault(c => c.K == CK.Always);
            if (hit != null) return Cond.Always(hit.Hit.Length > 0 ? hit.Hit : ruleText);
            var maybe = all.FirstOrDefault(c => c.K == CK.Maybe && !c.IpOnly) ?? all.FirstOrDefault(c => c.K == CK.Maybe);
            if (maybe != null) { var m = Cond.Maybe(maybe.Why, maybe.Hit); m.IpOnly = maybe.IpOnly; return m; }
            var procs = new HashSet<string>(all.Where(c => c.K == CK.IfProc).SelectMany(c => c.Procs), StringComparer.OrdinalIgnoreCase);
            if (procs.Count > 0) return new Cond { K = CK.IfProc, Procs = procs, Hit = ruleText };
            return Cond.Never;
        }

        private static Cond And(List<Cond> all, string ruleText)
        {
            if (all.Count == 0 || all.Any(c => c.K == CK.Never)) return Cond.Never;
            if (all.All(c => c.K == CK.Always)) return Cond.Always(ruleText);
            var maybe = all.FirstOrDefault(c => c.K == CK.Maybe);
            if (maybe != null) return Cond.Maybe(maybe.Why, ruleText);
            HashSet<string> procs = null;
            foreach (var c in all.Where(c => c.K == CK.IfProc))
            {
                if (procs == null) procs = new HashSet<string>(c.Procs, StringComparer.OrdinalIgnoreCase);
                else procs.IntersectWith(c.Procs);
            }
            return procs == null || procs.Count == 0 ? Cond.Never : new Cond { K = CK.IfProc, Procs = procs, Hit = ruleText };
        }

        // A rule list on disk: domains, addresses, or whole rules (classical).
        private static Cond EvalRuleSet(string name, bool noResolve, Ctx ctx, string ruleText)
        {
            Provider p;
            if (!ctx.Live.Providers.TryGetValue(name, out p)) return Cond.Maybe(Loc.T("Ex_WhyNoList", name), ruleText);
            if (p.Format == "mrs") return Cond.Maybe(Loc.T("Ex_WhyMrs", ListTitle(name)), ruleText);

            var items = p.Inline ?? ReadList(ctx.Live, p);
            if (items == null) return Cond.Maybe(Loc.T("Ex_WhyNotDownloaded", ListTitle(name)), ruleText);

            if (p.Behavior == "domain")
            {
                if (ctx.Host == null) return Cond.Never;
                foreach (var it in items)
                    if (PatternMatch(it, ctx.Host)) return Cond.Always(it);
                return Cond.Never;
            }
            if (p.Behavior == "ipcidr")
            {
                if (ctx.Ip == null)
                {
                    var savedLabel = ctx.ListLabel;
                    ctx.ListLabel = Loc.T("Ex_ResolveList", ListTitle(name));
                    try { return NeedsAddress(noResolve, ctx, ruleText, Loc.T("Ex_WhyList", ListTitle(name))); }
                    finally { ctx.ListLabel = savedLabel; }
                }
                foreach (var it in items)
                    if (CidrContains(it, ctx.Ip)) return Cond.Always(it);
                return Cond.Never;
            }

            // classical: each line is a rule without a target
            var conds = new List<Cond>();
            var outerLabel = ctx.ListLabel;
            ctx.ListLabel = Loc.T("Ex_ResolveList", ListTitle(name));
            try
            {
                foreach (var it in items)
                {
                    var parts = SplitTop(it, ',');
                    if (parts.Count < 2) continue;
                    bool nr = noResolve || parts.Skip(2).Any(o => o.Trim().Equals("no-resolve", StringComparison.OrdinalIgnoreCase));
                    var c = Eval(parts[0], parts[1], nr, ctx, ruleText);
                    if (c.K == CK.Always) return Cond.Always(it);
                    if (c.K != CK.Never) { if (c.Hit == ruleText) c.Hit = it; conds.Add(c); }
                }
            }
            finally { ctx.ListLabel = outerLabel; }
            return conds.Count == 0 ? Cond.Never : Or(conds, ruleText);
        }

        private static readonly Dictionary<string, Tuple<DateTime, List<string>>> _listCache =
            new Dictionary<string, Tuple<DateTime, List<string>>>(StringComparer.OrdinalIgnoreCase);

        private static List<string> ReadList(Live live, Provider p)
        {
            if (string.IsNullOrEmpty(p.Path)) return null;
            var rel = p.Path.StartsWith("./") ? p.Path.Substring(2) : p.Path;
            var full = System.IO.Path.Combine(live.WorkDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;
            var stamp = File.GetLastWriteTimeUtc(full);
            lock (_listCache)
            {
                Tuple<DateTime, List<string>> hit;
                if (_listCache.TryGetValue(full, out hit) && hit.Item1 == stamp) return hit.Item2;
            }
            var items = new List<string>();
            bool yaml = p.Format != "text", inPayload = !yaml;
            foreach (var raw in File.ReadLines(full))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                if (yaml)
                {
                    if (!inPayload) { inPayload = t.StartsWith("payload:"); continue; }
                    if (!t.StartsWith("-")) continue;
                    t = t.Substring(1).Trim();
                    int hash = t.IndexOf(" #", StringComparison.Ordinal);
                    if (hash >= 0) t = t.Substring(0, hash).Trim();
                    t = Unquote(t);
                }
                if (t.Length > 0) items.Add(t);
            }
            lock (_listCache) _listCache[full] = Tuple.Create(stamp, items);
            return items;
        }

        // ---------- matching helpers ----------

        internal static bool SuffixMatch(string host, string suffix)
        {
            suffix = suffix.TrimStart('.');
            return host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        }

        // mihomo's domain patterns: "+.x" = x and below, ".x" = below only, "*" = one label.
        internal static bool PatternMatch(string pattern, string host)
        {
            var pat = (pattern ?? "").Trim().ToLowerInvariant();
            var h = host.ToLowerInvariant().TrimEnd('.');
            if (pat.Length == 0) return false;
            if (pat.StartsWith("+.")) return SuffixMatch(h, pat.Substring(2));
            if (pat.StartsWith(".")) return h.EndsWith(pat);
            if (pat.IndexOf('*') >= 0)
            {
                var rx = "^" + Regex.Escape(pat).Replace("\\*", "[^.]+") + "$";
                return Regex.IsMatch(h, rx);
            }
            return h == pat;
        }

        internal static bool CidrContains(string cidr, IPAddress ip)
        {
            try
            {
                var c = (cidr ?? "").Trim();
                int slash = c.IndexOf('/');
                IPAddress net;
                int bits;
                if (slash < 0) { if (!IPAddress.TryParse(c, out net)) return false; bits = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128; }
                else
                {
                    if (!IPAddress.TryParse(c.Substring(0, slash), out net)) return false;
                    if (!int.TryParse(c.Substring(slash + 1), out bits)) return false;
                }
                if (net.AddressFamily != ip.AddressFamily) return false;
                var a = net.GetAddressBytes();
                var b = ip.GetAddressBytes();
                for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
                {
                    int mask = bits >= 8 ? 0xff : (0xff << (8 - bits)) & 0xff;
                    if ((a[i] & mask) != (b[i] & mask)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsPrivate(IPAddress ip)
        {
            foreach (var c in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16",
                                      "100.64.0.0/10", "fc00::/7", "fe80::/10", "::1/128" })
                if (CidrContains(c, ip)) return true;
            return false;
        }

        private static bool PortMatch(string spec, int port)
        {
            foreach (var part in spec.Split('/', ','))
            {
                var p = part.Trim();
                int dash = p.IndexOf('-');
                int a, b;
                if (dash > 0 && int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b))
                { if (port >= a && port <= b) return true; }
                else if (int.TryParse(p, out a) && a == port) return true;
            }
            return false;
        }

        private static MaxMind.GeoIP2.DatabaseReader _asnReader;
        private static string _asnPath;

        // The engine's own ASN.mmdb (GeoLite2-ASN) in its work dir — the same base IP-ASN rules use.
        private static string AsnOf(Live live, IPAddress ip)
        {
            try
            {
                var path = System.IO.Path.Combine(live.WorkDir, "ASN.mmdb");
                if (!File.Exists(path)) return null;
                lock (_listCache)
                {
                    if (_asnReader == null || _asnPath != path)
                    {
                        _asnReader?.Dispose();
                        _asnReader = new MaxMind.GeoIP2.DatabaseReader(path);
                        _asnPath = path;
                    }
                    MaxMind.GeoIP2.Responses.AsnResponse r;
                    return _asnReader.TryAsn(ip, out r) && r.AutonomousSystemNumber.HasValue
                        ? r.AutonomousSystemNumber.Value.ToString() : null;
                }
            }
            catch { return null; }
        }

        // ---------- walking the rules ----------

        private static List<Branch> Walk(List<string> lines, Ctx ctx, string sub)
        {
            var res = new List<Branch>();
            var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (lines == null || ctx.Depth > 4) return res;

            foreach (var rule in lines)
            {
                var parts = SplitTop(rule, ',');
                if (parts.Count == 0) continue;
                var type = parts[0].Trim().ToUpperInvariant();

                if (type == "MATCH")
                {
                    res.Add(new Branch { Target = parts.Count > 1 ? parts[1].Trim() : "DIRECT", Rule = rule, Sub = sub });
                    return res;
                }

                if (type == "SUB-RULE")
                {
                    if (parts.Count < 3) continue;
                    var inner = SplitTop(StripParens(parts[1]), ',');
                    bool nr = inner.Skip(2).Any(o => o.Trim().Equals("no-resolve", StringComparison.OrdinalIgnoreCase));
                    var c = Eval(inner[0], inner.Count > 1 ? inner[1] : "", nr, ctx, rule);
                    var name = parts[2].Trim();
                    List<string> block;
                    ctx.Live.SubRules.TryGetValue(name, out block);
                    if (c.K == CK.Never) continue;
                    if (c.K == CK.IfProc)
                    {
                        foreach (var p in c.Procs)
                        {
                            if (!decided.Add(p)) continue;
                            foreach (var b in Walk(block, ctx.With(p), name)) { b.Who = p; res.Add(b); }
                        }
                        continue;
                    }
                    var inside = Walk(block, ctx.With(ctx.Process), name);
                    if (c.K == CK.Maybe && c.IpOnly && ctx.Host != null) continue;
                    if (c.K == CK.Maybe)
                    {
                        foreach (var b in inside) { b.Uncertain = true; b.UncertainWhy = c.Why; b.Who = b.Who ?? ctx.Process; res.Add(b); }
                        continue;
                    }
                    foreach (var b in inside) { b.Who = b.Who ?? (ctx.Depth > 0 ? ctx.Process : null); res.Add(b); }
                    if (inside.Any(b => !b.Uncertain && b.Who == null)) return res;
                    continue;
                }

                if (parts.Count < 3) continue;
                var target = parts[2].Trim();
                if (target.Equals("PASS", StringComparison.OrdinalIgnoreCase)) continue;
                bool noResolve = parts.Skip(3).Any(o => o.Trim().Equals("no-resolve", StringComparison.OrdinalIgnoreCase));
                var cond = Eval(type, parts[1], noResolve, ctx, rule);
                switch (cond.K)
                {
                    case CK.Never: break;
                    case CK.Maybe:
                        if (cond.IpOnly && ctx.Host != null) break;   // said once, in the pre-resolve note
                        res.Add(new Branch { Uncertain = true, UncertainWhy = cond.Why, Target = target, Rule = rule,
                                             Hit = cond.Hit == rule ? "" : cond.Hit, Sub = sub });
                        break;
                    case CK.IfProc:
                        foreach (var p in cond.Procs)
                            if (decided.Add(p))
                                res.Add(new Branch { Who = p, Target = target, Rule = rule, Sub = sub });
                        break;
                    case CK.Always:
                        res.Add(new Branch { Target = target, Rule = rule, Hit = cond.Hit == rule ? "" : cond.Hit, Sub = sub });
                        return res;
                }
            }
            // A block or a config with no MATCH: whatever is left goes direct.
            res.Add(new Branch { Target = "DIRECT", Rule = "", Sub = sub });
            return res;
        }

        // ---------- the answer, in words ----------

        public sealed class Result
        {
            public string Text = "";
            public string Host;                     // what to ask a resolver about, or null
            public List<string> DirectServers = new List<string>();   // who answers it if it goes DIRECT
            public bool DirectIsOwn;                // …and whether that is the user's own DNS
        }

        public static Result Explain(AppConfig cfg, string query)
        {
            var res = new Result();
            var live = Read(cfg);
            if (live == null) { res.Text = Loc.T("Ex_NoConfig"); return res; }

            var ctx = new Ctx { Live = live };
            if (!ParseQuery(query, ctx)) { res.Text = Loc.T("Ex_BadQuery"); return res; }

            var sb = new StringBuilder();
            var machine = string.Join(", ", live.Nameserver);
            var own = string.Join(", ", live.DirectNs);

            if (ctx.Ip != null)
            {
                if (live.RouteExclude.Any(c => CidrContains(c, ctx.Ip)))
                {
                    res.Text = Loc.T("Ex_IpExcluded", ctx.Ip);
                    return res;
                }
                sb.AppendLine(Loc.T("Ex_HeadIp", ctx.Ip));
                sb.AppendLine("   " + Loc.T("Ex_IpSniff"));
            }
            else if (ctx.Host != null)
            {
                sb.AppendLine(Loc.T("Ex_HeadHost", ctx.Host + (ctx.Port > 0 ? ":" + ctx.Port : "")));
                bool filtered = live.FakeIpFilter.Any(f => PatternMatch(f, ctx.Host));
                sb.AppendLine("   " + (filtered ? Loc.T("Ex_FakeIpFiltered", machine) : live.FakeIp ? Loc.T("Ex_FakeIp") : Loc.T("Ex_NoFakeIp", machine)));
            }
            else sb.AppendLine(Loc.T("Ex_HeadProc", ctx.Process));

            var branches = Walk(live.Rules, ctx, null);
            var owners = EngineLog.BuildOwnerIndex(cfg);
            bool anyProc = branches.Any(b => b.Who != null);
            var allName = (cfg.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.IsAllTraffic)?.Name;
            if (string.IsNullOrWhiteSpace(allName)) allName = Loc.T("Ex_AllTraffic");

            // A program with a tile of its own takes its traffic there whatever the address, so
            // every such program yields a branch — a dozen near-identical lines. Branches that end
            // the same way are folded into one, with the programs listed; and a subscription's
            // country/IP conditions are capped per program, the decisive line is what matters.
            const int MaxMaybePerProgram = 2;
            var maybeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hidden = 0;
            var groups = new List<KeyValuePair<string, List<string>>>();   // body → programs (null entry = no program)
            foreach (var b in branches)
            {
                if (b.Uncertain && b.Who != null)
                {
                    int n;
                    maybeCount.TryGetValue(b.Who, out n);
                    maybeCount[b.Who] = ++n;
                    if (n > MaxMaybePerProgram) { hidden++; continue; }
                }
                var body = new StringBuilder();
                body.Append(b.Uncertain ? Loc.T("Ex_Maybe", b.UncertainWhy) + " " : "").Append(TargetText(live, b.Target));
                body.Append("\n   ").Append(HowText(live, owners, b, ctx, allName));
                if (ctx.Process == null || ctx.Host != null)
                {
                    var dns = DnsText(live, b, ctx, machine, own);
                    if (dns.Length > 0) body.Append("\n   ").Append(dns);
                }
                var key = body.ToString();
                var g = b.Who == null ? -1 : groups.FindIndex(x => x.Key == key && x.Value[0] != null);
                if (g >= 0) groups[g].Value.Add(b.Who);
                else groups.Add(new KeyValuePair<string, List<string>>(key, new List<string> { b.Who }));
            }

            sb.AppendLine();
            foreach (var g in groups)
            {
                string who;
                if (g.Value[0] != null)
                {
                    var progs = g.Value.Select(p => p + " («" + OwnerName(owners, p) + "»)").ToList();
                    who = Loc.T("Ex_WhoProc", progs.Count > 6 ? string.Join(", ", progs.Take(6)) + " …+" + (progs.Count - 6)
                                                              : string.Join(", ", progs));
                }
                else who = ctx.Process != null ? Loc.T("Ex_WhoThis", ctx.Process)
                         : anyProc ? Loc.T("Ex_WhoOthers") : Loc.T("Ex_WhoAny");
                sb.AppendLine("• " + who + ": " + g.Key.Replace("\n", Environment.NewLine));
            }
            if (hidden > 0) sb.AppendLine("   " + Loc.T("Ex_MoreMaybe", hidden));

            if (ctx.ResolveRules.Count > 0 && ctx.Host != null)
            {
                sb.AppendLine();
                sb.AppendLine(Loc.T("Ex_PreResolve", machine, string.Join(", ", ctx.ResolveRules.Take(6))
                                                          + (ctx.ResolveRules.Count > 6 ? " …+" + (ctx.ResolveRules.Count - 6) : "")));
            }

            if (ctx.Host != null && branches.Any(b => IsDirect(b.Target)))
            {
                res.Host = ctx.Host;
                res.DirectIsOwn = live.DirectNs.Count > 0;
                res.DirectServers = res.DirectIsOwn ? live.DirectNs : live.Nameserver.Where(n => n != "system").ToList();
            }
            res.Text = sb.ToString().TrimEnd();
            return res;
        }

        private static bool ParseQuery(string query, Ctx ctx)
        {
            var q = (query ?? "").Trim().Trim('"', '\'');
            if (q.Length == 0) return false;
            if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && q.IndexOf('/') < 0)
            {
                try { ctx.Process = System.IO.Path.GetFileName(q); } catch { ctx.Process = q; }
                return true;
            }
            if (q.IndexOf("://", StringComparison.Ordinal) < 0) q = "http://" + q;
            Uri u;
            if (!Uri.TryCreate(q, UriKind.Absolute, out u) || string.IsNullOrEmpty(u.Host)) return false;
            var host = u.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (!u.IsDefaultPort || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ctx.Port = u.Port;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) ctx.Ip = ip;
            else if (host.IndexOf('.') > 0) ctx.Host = host;
            else return false;
            return true;
        }

        private static bool IsDirect(string t) => (t ?? "").Trim().Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
        private static bool IsBlock(string t) => (t ?? "").Trim().StartsWith("REJECT", StringComparison.OrdinalIgnoreCase);

        private static string SlotName(string proxy)
        {
            try
            {
                var r = VpnStatus.Routes.FirstOrDefault(x => x.ProxyName == proxy && !string.IsNullOrEmpty(x.SlotName));
                if (r != null) return r.SlotName;
            }
            catch { }
            return proxy.StartsWith("vpn_") ? proxy.Substring(4) : proxy;
        }

        private static string TargetText(Live live, string target)
        {
            var t = (target ?? "").Trim();
            if (IsDirect(t)) return Loc.T("Ex_TDirect");
            if (IsBlock(t)) return Loc.T("Ex_TBlock");
            if (live.Groups.Contains(t) && !live.Proxies.ContainsKey(t)) return Loc.T("Ex_TSub", t);
            return Loc.T("Ex_TVpn", SlotName(t));
        }

        private static string OwnerName(Dictionary<string, EngineLog.RuleHit> owners, string key)
        {
            var hit = EngineLog.FindOwner(owners, key, null);
            return hit != null ? hit.RuleName : "?";
        }

        // Which line decided it, in words people use: the rule's name and the item inside it.
        private static string HowText(Live live, Dictionary<string, EngineLog.RuleHit> owners, Branch b, Ctx ctx, string allName)
        {
            if (string.IsNullOrEmpty(b.Rule))
                return b.Sub != null ? Loc.T("Ex_HowSubEnd", b.Sub) : Loc.T("Ex_HowNoRule");

            var parts = SplitTop(b.Rule, ',');
            var type = parts[0].Trim().ToUpperInvariant();
            var payload = parts.Count > 1 ? parts[1].Trim() : "";
            var item = b.Hit.Length > 0 && b.Hit != b.Rule ? b.Hit : type == "MATCH" ? "" : type + "," + payload;

            if (b.Sub != null)
            {
                if (type == "MATCH") return Loc.T("Ex_HowSubMatch", b.Sub);
                var list = type == "RULE-SET" ? ListTitle(payload) : null;
                return list != null ? Loc.T("Ex_HowSubList", b.Sub, list, item) : Loc.T("Ex_HowSubRule", b.Sub, item);
            }

            if (type == "MATCH")
                return IsDirect(parts.Count > 1 ? parts[1] : "") ? Loc.T("Ex_HowMatchDirect") : Loc.T("Ex_HowMatchAll", allName);

            // The program's own tile — named in the "who" part already, so the reason stays generic
            // and identical outcomes fold together.
            if (type.StartsWith("PROCESS-", StringComparison.Ordinal))
            {
                var tg = parts.Count > 2 ? parts[2] : "";
                return IsBlock(tg) ? Loc.T("Ex_HowProcKill") : IsDirect(tg) ? Loc.T("Ex_HowProcDirect") : Loc.T("Ex_HowProc");
            }

            var owner = EngineLog.FindOwner(owners, payload, ctx.Host);
            if (owner == null) return Loc.T("Ex_HowBare", item);
            if (IsBlock(parts.Count > 2 ? parts[2] : "") && owner.KillSwitch)
                return Loc.T("Ex_HowKill", owner.RuleName, item);
            if (IsDirect(parts.Count > 2 ? parts[2] : "") && owner.RequiresVpn)
                return Loc.T("Ex_HowRuleOff", owner.RuleName, item);
            return Loc.T("Ex_HowRule", owner.RuleName, item);
        }

        // Who turns the name into an address on THIS branch — the part people never see.
        private static string DnsText(Live live, Branch b, Ctx ctx, string machine, string own)
        {
            if (ctx.Host == null) return ctx.Ip != null ? Loc.T("Ex_DnsNoneIp") : "";
            var t = (b.Target ?? "").Trim();
            if (IsBlock(t)) return Loc.T("Ex_DnsBlock");
            if (IsDirect(t)) return own.Length > 0 ? Loc.T("Ex_DnsDirectOwn", own) : Loc.T("Ex_DnsDirectMachine", machine);
            ProxyInfo p;
            if (live.Proxies.TryGetValue(t, out p) && p.Type == "wireguard")
                return p.RemoteDns && p.Dns.Count > 0 ? Loc.T("Ex_DnsWg", SlotName(t), string.Join(", ", p.Dns))
                                                      : Loc.T("Ex_DnsWgLocal", machine);
            return Loc.T("Ex_DnsRemote", live.Groups.Contains(t) && !live.Proxies.ContainsKey(t) ? t : SlotName(t));
        }

        // "MySub__Google" → "Google": lists are renamed into a subscription's namespace.
        private static string ListTitle(string key)
        {
            var k = key ?? "";
            int i = k.IndexOf("__", StringComparison.Ordinal);
            return i >= 0 ? k.Substring(i + 2) : k;
        }

        // ---------- «Что сейчас идёт напрямую» for the DNS tab ----------

        public static string DirectSummary(AppConfig cfg)
        {
            var live = Read(cfg);
            if (live == null) return Loc.T("Dns_SumNoConfig");
            var sb = new StringBuilder();

            // The catch-all decides most of it: everything no tile claims.
            var last = live.Rules.LastOrDefault() ?? "MATCH,DIRECT";
            var catchAllSub = live.Rules.FirstOrDefault(r => r.StartsWith("SUB-RULE," + SubscriptionPolicy.CatchAllMatcher, StringComparison.Ordinal));
            if (catchAllSub != null)
                sb.AppendLine("• " + Loc.T("Dns_SumRestSub", SplitTop(catchAllSub, ',').Last().Trim()));
            else if (IsDirect(SplitTop(last, ',').Skip(1).FirstOrDefault()))
                sb.AppendLine("• " + Loc.T("Dns_SumRestDirect"));
            else
                sb.AppendLine("• " + Loc.T("Dns_SumRestVpn"));

            // Tiles: where each one's items go under the live config — first line with that
            // matcher wins, as in the engine; an item with no line of its own falls to the catch-all.
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string catchAll = "DIRECT";
            foreach (var r in live.Rules)
            {
                var p = SplitTop(r, ',');
                if (p.Count == 0) continue;
                var type = p[0].Trim().ToUpperInvariant();
                if (type == "MATCH") { if (catchAllSub == null && p.Count > 1) catchAll = p[1].Trim(); continue; }
                if (type == "SUB-RULE" && p.Count >= 3)
                {
                    var inner = SplitTop(StripParens(p[1]), ',');
                    if (inner.Count >= 2 && !effective.ContainsKey(inner[0].Trim() + "," + inner[1].Trim()))
                        effective[inner[0].Trim() + "," + inner[1].Trim()] = "SUB:" + p[2].Trim();
                    continue;
                }
                if (p.Count >= 3 && !effective.ContainsKey(p[0].Trim() + "," + p[1].Trim()))
                    effective[p[0].Trim() + "," + p[1].Trim()] = p[2].Trim();
            }
            if (catchAllSub != null) catchAll = "SUB";

            var direct = new List<string>();
            foreach (var app in (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.IsAllTraffic))
            {
                var mine = new List<RuleDef>();
                MihomoConfigBuilder.AddAppRules(mine, app, "?");
                bool goesDirect = mine.Any(r =>
                {
                    string tg;
                    return IsDirect(effective.TryGetValue(r.Type + "," + r.Value, out tg) ? tg : catchAll);
                });
                if (goesDirect && !string.IsNullOrWhiteSpace(app.Name)) direct.Add(app.Name);
            }
            if (direct.Count > 0) sb.AppendLine("• " + Loc.T("Dns_SumTiles", string.Join(", ", direct.Distinct())));

            // Subscriptions: what their own rules send direct.
            foreach (var kv in live.SubRules)
            {
                var names = kv.Value
                    .Select(r => SplitTop(r, ','))
                    .Where(p => p.Count >= 3 && IsDirect(p[2]))
                    .Select(p => p[0].Trim().Equals("RULE-SET", StringComparison.OrdinalIgnoreCase) ? ListTitle(p[1].Trim()) : p[1].Trim())
                    .Distinct().ToList();
                var final = kv.Value.LastOrDefault(r => r.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase));
                bool restDirect = final != null && IsDirect(SplitTop(final, ',').Skip(1).FirstOrDefault());
                if (names.Count == 0 && !restDirect) continue;
                var listText = names.Count == 0 ? "" : string.Join(", ", names.Take(8)) + (names.Count > 8 ? " …" : "");
                sb.AppendLine("• " + (!restDirect ? Loc.T("Dns_SumSubLists", kv.Key, listText)
                    : listText.Length > 0 ? Loc.T("Dns_SumSubRest", kv.Key, listText)
                    : Loc.T("Dns_SumSubRestOnly", kv.Key)));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
