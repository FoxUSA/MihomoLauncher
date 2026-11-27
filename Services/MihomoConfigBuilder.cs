using System;
using System.Collections.Generic;
using System.IO;
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
        // The launcher's own probe rule (its exe through the node being checked): always first.
        public bool Probe { get; set; }
        // A site or template the person put into a tile (2026-11-13): every tile's sites go above
        // every tile's programs — the specific rule (this site) beats the general one (all of this
        // program), wherever the tiles stand. Tile order decides only between two tiles that
        // list the same address.
        public bool Site { get; set; }
        // The tile's network cards (NicKey of its AppEntry), null = Windows decides. Build turns
        // the target into "the same, through these cards" — see NicPlan.
        public string Nic { get; set; }
        // A folder of a tile (PROCESS-PATH-REGEX, 2026-11-22): written below every tile's programs —
        // a program named in any tile is more specific than any tile's folder — and among folders
        // the deeper one first (FolderLen = the folder path's length), whatever the tile order.
        public bool Folder { get; set; }
        public int FolderLen { get; set; }
        // An auto-tracked program by its exact file (PROCESS-PATH-REGEX anchored at both ends):
        // written right below the sites, above every program by name — the more specific wins.
        public bool AutoFile { get; set; }
        public RuleDef() { }
        public RuleDef(string type, string value, string target) { Type = type; Value = value; Target = target; }
    }

    // Assembles a complete mihomo config.yaml from the current proxies and rules.
    // Hand-built YAML (fixed schema) so there is no external YAML dependency.
    public static class MihomoConfigBuilder
    {
        private const string HealthCheckUrl = "https://www.gstatic.com/generate_204";

        // Addresses kept OUT of the TUN route table. Local/LAN ranges are always excluded
        // (file shares, router, printers stay direct) — that is networking, not RDP.
        // Live RDP peers (local AND external) are excluded ONLY while the «RDP» mode is on
        // (2026-11-16, the user's rule: «нам вообще не нужны никакие условия в коде для рдп» —
        // nothing the person doesn't know about). The mode = an explicit «let me in»: the engine
        // is stopped every 60 s and the remote desktop is let through whatever the tiles say,
        // «Блокировать» on «Всё остальное» included.
        private static IEnumerable<string> TunRouteExcludes(AppConfig cfg)
        {
            var list = new List<string>
            {
                "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",  // RFC1918 LAN
                "100.64.0.0/10",                                   // CGNAT
                "169.254.0.0/16",                                  // link-local
                "fc00::/7", "fe80::/10",                           // IPv6 ULA + link-local
            };
            if (cfg?.RemoteDesktopSafeMode == true)
            {
                var peers = new List<string>();
                try { peers = RdpClients.ActivePeerCidrs() ?? new List<string>(); } catch { }
                LastRdpExcludes = peers;
                list.AddRange(peers);
            }
            else LastRdpExcludes = null;
            return list.Distinct();
        }

        // The RDP peers the most recent build actually excluded — null when the «RDP» mode is off.
        //
        // This list is a snapshot taken while the config was being written, and a remote session
        // that appears LATER isn't in it: its return traffic gets captured by TUN's auto-route
        // and the session drops, with nothing to rebuild the config because everything is
        // already connected and healthy. That is the hole the "Remote desktop session" switch
        // papers over — by killing the engine every 60 s, which also takes the kill-switch down
        // with it. The watchdog compares this against the live peers instead.
        public static List<string> LastRdpExcludes { get; private set; }

        private static bool IsCidrRule(string type) => type.StartsWith("IP-CIDR", StringComparison.OrdinalIgnoreCase);
        private static bool IsAsnRule(string type) => type.Equals("IP-ASN", StringComparison.OrdinalIgnoreCase);
        // Rules matched on the destination IP — under fake-ip they need ",no-resolve".
        private static bool IsIpRule(string type) => IsCidrRule(type) || IsAsnRule(type);

        // An IP-ASN line makes the engine load ASN.mmdb while it reads the config, downloading it
        // if absent — and a failed download refuses the WHOLE config. So these lines are written
        // only once the file is there (UpdateService.EnsureAsnDbAsync puts it in place).
        private static bool AsnDbReady(AppConfig cfg)
        {
            try { return File.Exists(Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), "ASN.mmdb")); }
            catch { return false; }
        }

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
            // The kill-switch works ALWAYS (user, 2026-11-13): switched on or off, through a VPN or
            // «без VPN» — and on «Всё остальное» too (then everything no tile claims is blocked
            // until its VPN is up). «Всё остальное» set to «Блокировать» and on blocks as well.
            foreach (var app in (cfg.Apps ?? new List<AppEntry>()).Where(a => a != null && (a.IsBlocking || a.KillSwitch)))
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
            var nic = NicKey(app);

            // «Весь трафик» claims nothing by name: it is the catch-all. Build moves it to the
            // very end, so every other rule still wins for what it lists.
            if (app.IsAllTraffic)
            {
                rules.Add(new RuleDef("MATCH", "", target) { Nic = nic });
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // `raw`: what the person named themselves — a site, a template, the check address —
            // goes straight through the target even when the target is a Clash subscription with
            // rules of its own. Only a whole PROGRAM follows the subscription's rules (they decide
            // per destination). Otherwise a site the subscription doesn't list — or lists in a
            // list that failed to download — fell to the subscription's MATCH (DIRECT) and the
            // template "didn't work" (2026-11-03: Google template + api.xbyfox.com on a
            // subscription went direct).
            void AddRule(string type, string value, bool raw, bool site = false)
            {
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(value)) return;
                if (!seen.Add(type + "|" + value)) return;
                rules.Add(new RuleDef(type, value, target) { Raw = raw, Site = site, Nic = nic });
            }

            // Sites before programs: a rule's own sites must win even for its own program's
            // traffic, which would otherwise be handed to the subscription first.
            foreach (var site in app.Sites ?? new List<RuleSite>())
            {
                if (site == null) continue;
                AddRule("DOMAIN-SUFFIX", HostOf(site.Url), true, site: true);

                // Cached rule list (domains/IPs) — route all of it through the same target so
                // the whole service goes via the VPN, not just the probe host. "TYPE,VALUE".
                foreach (var item in site.RuleItems ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    int c = item.IndexOf(',');
                    if (c <= 0 || c >= item.Length - 1) continue;
                    AddRule(item.Substring(0, c).Trim(), item.Substring(c + 1).Trim(), true, site: true);
                }
            }

            // The check address is the tile's own probe, not its list of sites: it stays with the
            // programs (below every tile's sites), so it can't outrank another tile's program.
            AddRule("DOMAIN-SUFFIX", HostOf(RouteUrl(app)), true);

            foreach (var prog in app.Programs ?? new List<RuleProgram>())
            {
                // An auto entry is the exact FILE auto-tracking detected (by its process tree), and
                // the rule is as precise as the detection (2026-11-22): a same-named exe elsewhere —
                // VS Code's codex.exe vs the Store ChatGPT's codex.exe — is not this tile's. No usable
                // path → by name, like any program.
                var file = AutoFileRegex(prog);
                if (file.Length > 0)
                {
                    if (seen.Add("PROCESS-PATH-REGEX|" + file))
                        rules.Add(new RuleDef("PROCESS-PATH-REGEX", file, target) { Nic = nic, AutoFile = true });
                    continue;
                }
                var exe = ProcessImageName(prog);
                if (!string.IsNullOrEmpty(exe)) AddRule("PROCESS-NAME", exe, false);
            }

            // Folders: everything started from the folder or below. Not raw — like a program, the
            // whole of it follows a subscription's own rules.
            foreach (var f in app.Folders ?? new List<RuleFolder>())
            {
                var rx = FolderRegex(f?.Path);
                if (string.IsNullOrEmpty(rx) || !seen.Add("PROCESS-PATH-REGEX|" + rx)) continue;
                rules.Add(new RuleDef("PROCESS-PATH-REGEX", rx, target)
                {
                    Nic = nic, Folder = true, FolderLen = NormalizeFolder(f.Path).Length,
                });
            }
        }

        // "D:\Apps\Tool\" / "d:/apps/tool" → "D:\Apps\Tool" (full, no trailing slash); "" if unusable.
        public static string NormalizeFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                var full = Path.GetFullPath(path.Trim().Replace('/', '\\'));
                var root = Path.GetPathRoot(full) ?? "";
                return full.Length > root.Length ? full.TrimEnd('\\') : full;
            }
            catch { return ""; }
        }

        // The engine's regexp for "started from this folder or below" (PROCESS-PATH-REGEX).
        //
        // The flavour is dlclark/regexp2 (.NET-compatible), case-insensitive by the engine itself
        // (measured 2026-11-22 on 1.19.34: a lookahead passes `-t`, RE2 would refuse it). Anchored at
        // the start, the folder plus a backslash, anything after it — the end is left open.
        //
        // Written WITHOUT a single parenthesis or comma: a rule line is split at commas, and a
        // subscription's SUB-RULE,(…) wraps the matcher in parentheses — a value with either can't
        // go there (Build). So those, and '#' (a YAML comment after a space), are \xHH escapes.
        //
        // A path segment that carries a dotted version ("ms-dotnettools.csdevkit-3.40.215-win32-x64",
        // "app-1.15.0" — ProgramPathRepair's notion) gets its version digits as a pattern, so the rule
        // survives the update that renames that folder.
        public static string FolderRegex(string path)
        {
            var full = NormalizeFolder(path);
            if (full.Length == 0) return "";
            var sb = new StringBuilder("^").Append(PathPattern(full));
            // A drive root ("D:\") already ends with its separator.
            if (!full.EndsWith("\\")) sb.Append(@"\\");
            return sb.ToString();
        }

        // The regexp for exactly this FILE (an auto entry, 2026-11-22): the same escaping and version
        // masking as a folder, anchored at both ends — the file itself, never a longer path that
        // merely starts with it. "" when the path is unusable.
        public static string FileRegex(string path)
        {
            var full = NormalizeFolder(path);
            if (full.Length == 0 || full.EndsWith("\\")) return "";
            return "^" + PathPattern(full) + "$";
        }

        // The file line of an auto entry, "" when it is a by-name entry (manual, or no usable path).
        public static string AutoFileRegex(RuleProgram prog)
        {
            if (prog == null || !prog.Auto || string.IsNullOrWhiteSpace(prog.Path)) return "";
            return FileRegex(prog.Path);
        }

        // Is `exePath` exactly the file of `file` (the same file after a version bump too)?
        public static bool IsSameFile(string exePath, string file)
            => !string.IsNullOrEmpty(file) && MatchCached("F|" + file, () => FileRegex(file), exePath);

        // Escaped path, segment by segment, dotted versions as a digits pattern.
        private static string PathPattern(string full)
        {
            var sb = new StringBuilder();
            var parts = full.Split('\\');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(@"\\");
                var seg = parts[i];
                if (ProgramPathRepair.LooksVersioned(seg))
                {
                    int at = 0;
                    foreach (System.Text.RegularExpressions.Match m in ProgramPathRepair.VersionRuns(seg))
                    {
                        sb.Append(RegexLiteral(seg.Substring(at, m.Index - at)));
                        // Only a dotted run is a version: "win32" / "x64" next to it stay as they are.
                        sb.Append(m.Value.IndexOfAny(new[] { '.', '_' }) >= 0 ? "[0-9][0-9._]*" : m.Value);
                        at = m.Index + m.Length;
                    }
                    sb.Append(RegexLiteral(seg.Substring(at)));
                }
                else sb.Append(RegexLiteral(seg));
            }
            return sb.ToString();
        }

        private static string RegexLiteral(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "")
            {
                if (c == '(' || c == ')' || c == ',' || c == '#') sb.Append("\\x").Append(((int)c).ToString("x2"));
                else if (@"\.+*?|[]{}^$".IndexOf(c) >= 0) sb.Append('\\').Append(c);
                else if (c < ' ') continue;   // no control characters in a path; never in a rule line
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Is `exePath` inside `folder` (or below)? The launcher's own reading of a folder entry —
        // the same "folder\…" test the engine's regexp makes, version segments included.
        public static bool InFolder(string exePath, string folder)
            => !string.IsNullOrEmpty(folder) && MatchCached("D|" + folder, () => FolderRegex(folder), exePath);

        // Are these the same folder for the engine (2026-11-23)? The same regexp — so a version
        // bump of a segment ("app-1.0.9" → "app-1.0.10") is still the same folder. Both usable.
        public static bool SameFolder(string a, string b)
        {
            var x = FolderRegex(a);
            var y = FolderRegex(b);
            return x.Length > 0 && y.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchCached(string key, Func<string> pattern, string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            System.Text.RegularExpressions.Regex rx;
            if (!_folderRx.TryGetValue(key, out rx))
            {
                var text = pattern();
                try
                {
                    rx = text.Length == 0 ? null : new System.Text.RegularExpressions.Regex(text,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                }
                catch { rx = null; }
                if (_folderRx.Count > 4000) _folderRx.Clear();
                _folderRx[key] = rx;
            }
            try { return rx != null && rx.IsMatch(exePath.Replace('/', '\\')); }
            catch { return false; }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> _folderRx =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex>(StringComparer.OrdinalIgnoreCase);

        // The address ROUTED with a rule as its own (and named in the routing explanations): the
        // check address, else its HomeUrl (AppEntry.OwnUrl), else the first site's Url. Not what is
        // checked — that is the check address alone (MainWindow.ResolveCheckUrl). Before 2026-11-23
        // the check address was here alone; whatever the loader moved out of it into HomeUrl
        // (NormalizeCheckAddresses) is picked up here, so the engine gets the same rule lines.
        public static string RouteUrl(AppEntry app)
        {
            if (app == null) return "";
            var own = app.OwnUrl;
            if (own.Length > 0) return own;
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
            bool catchAllSeen = false;
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
                    if (!catchAllSeen && !catchAll.StartsWith("SUB:", StringComparison.Ordinal)) catchAll = parts[1].Trim();
                    catchAllSeen = true;   // the engine stops at the first MATCH
                    continue;
                }
                if (parts.Length < 3) continue;
                var key = parts[0].Trim() + "," + parts[1].Trim();
                if (!effective.ContainsKey(key)) effective[key] = parts[2].Trim();
            }

            var described = new List<string>();
            foreach (var app in (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && (a.RequiresVpn || a.KillSwitch || a.HasInterfaces)))
            {
                string Where(string target)
                {
                    if (target.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return "напрямую";
                    if (target.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase))
                        return app.IsBlocking ? "заблокировано («Блокировать»)"
                             : app.KillSwitch ? "заблокировано (кил-свитч)" : "ЗАБЛОКИРОВАНО, хотя кил-свитч выключен";
                    bool sub = target.StartsWith("SUB:", StringComparison.Ordinal);
                    var name = sub ? target.Substring(4) : target;
                    var cards = NicLabelOf(name);
                    var via = cards != null ? " через карту " + cards + (app.LockNic ? " (привязка)" : "") : "";
                    if (IsNicTarget(name)) return "напрямую" + via;
                    return (sub ? "по правилам подписки «" : "VPN «") + BaseProxyName(name) + "»" + via;
                }

                if (app.IsAllTraffic)
                {
                    // Only worth a word when it is actually doing something.
                    if (catchAll.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;
                    described.Add(app.Name + " → " + Where(catchAll));
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

        // The launcher's own server (2026-11-13), straight out, always, and for the launcher's
        // process only: updates, templates, the geo base and the engine mirror live there. A
        // tile's kill-switch, «Блокировать» on «Всё остальное» or a dead VPN must not cut the
        // app off from its own updates — the way VPN clients keep their own API reachable under
        // a kill-switch. A browser going to byfox.dev still follows the tiles like anything else.
        public const string OwnServerSuffix = "byfox.dev";

        private static void AppendOwnServer(StringBuilder sb)
        {
            sb.AppendLine("  - AND,((PROCESS-NAME," + LauncherImageName() + "),(DOMAIN-SUFFIX," + OwnServerSuffix + ")),DIRECT");
        }

        private static string LauncherImageName()
        {
            try
            {
                var name = System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName;
                if (!string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { ',', '(', ')' }) < 0) return name;
            }
            catch { }
            return VersionInfo.ExeName;
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

        // The internet names among FakeIpFilter, in nameserver-policy syntax — resolved by the
        // user's own DNS when one is chosen (AppendDns).
        private static readonly string[] ExternalFilterNames =
        {
            "+.msftconnecttest.com", "+.msftncsi.com", "time.windows.com", "time.nist.gov",
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
                // The fake-ip-filter names are answered by `nameserver` (the machine's resolvers),
                // not by direct-nameserver. The local ones (*.lan, *.local, wpad…) must stay there —
                // only the router knows them — but the INTERNET ones (Windows' connectivity probe,
                // NTP) are ordinary direct traffic: with the user's DNS chosen, they go to it too.
                sb.AppendLine("  nameserver-policy:");
                sb.AppendLine("    \"" + string.Join(",", ExternalFilterNames) + "\":");
                foreach (var e in direct)
                    sb.AppendLine("      - \"" + e.Engine.Replace("\"", "") + "\"");
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

        // ---------- network cards per tile (2026-11-14) ----------
        //
        // A tile may name the cards its traffic leaves through (AppEntry.Interfaces). The engine
        // does it natively, the way every Clash client does it by hand in YAML:
        //   one card      →  `- name: nic-<h>  type: direct  interface-name: "<card>"`
        //   several cards →  a `fallback` group over those, in the tile's order (first that answers)
        //   «напрямую»    →  the tile's DIRECT lines point at that card / group
        //   through a VPN →  a copy of the variant, `<proxy>.via-<h>`, with `dialer-proxy:` = the
        //                    card / group: the VPN's own connection goes out through the card
        // Hard, as asked: a card that is down or gone gives «interface not found» and nothing goes
        // out — never through another card. Measured on the binary 2026-11-14.
        //
        // Names: our proxies are always vpn_<letters/digits/_/-> (ProxyParsers.SanitizeName), so
        // "nic-", "nics-" and the "." in ".via-" can never collide with one.
        public const string NicPrefix = "nic-";
        public const string NicsPrefix = "nics-";
        public const string ViaMarker = ".via-";

        // engine name → the cards in words ("Ethernet › Беспроводная сеть 2"), for the log and
        // the traffic window. Filled by every Build; names are hashes of the card list, so an entry
        // never changes meaning and is never removed.
        private static readonly Dictionary<string, string> _nicLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        public static bool IsNicTarget(string target)
        {
            var t = (target ?? "").Trim();
            return t.StartsWith(NicPrefix, StringComparison.Ordinal) || t.StartsWith(NicsPrefix, StringComparison.Ordinal);
        }

        // "vpn_Turkey.via-1a2b3c4d" → "vpn_Turkey": the variant behind a copy dialled through a card.
        // A cascade copy ("vpn_KG.chain-…", below) → its own variant, the hop that exits.
        public static string BaseProxyName(string target)
        {
            var t = (target ?? "").Trim();
            if (IsChain(t)) t = ChainHops(t).LastOrDefault() ?? t;
            int i = t.IndexOf(ViaMarker, StringComparison.Ordinal);
            if (i > 0) t = t.Substring(0, i);
            i = t.IndexOf(ChainMarker, StringComparison.Ordinal);
            return i > 0 ? t.Substring(0, i) : t;
        }

        // ---------- cascade (2026-11-27) ----------
        //
        // A tile with «Каскадный VPN» rides 2–3 variants one inside the other: you → hop 1 → hop 2
        // → hop 3 → the site. The engine does it natively with `dialer-proxy`, the way it already
        // dials a VPN through a network card (NicPlan): hop 2 is a copy of its variant whose own
        // connection is dialled through hop 1, hop 3 a copy dialled through that copy. The tile's
        // cards, if any, carry hop 1.
        //
        // In the launcher a cascade target is one string, the hops' proxy names joined by ChainSep
        // ("vpn_Turkey>vpn_ekb_home>vpn_KG_pc") — what _assignment holds for such a tile and what
        // its RuleDefs point at. Build turns it into the copies; ChainNodeName names the last copy
        // the way Build does, for the node checks. Proxy names are vpn_<letters/digits/_/->, so
        // neither ">" nor ".chain-" can collide with one.
        public const string ChainSep = ">";
        public const string ChainMarker = ".chain-";

        public static bool IsChain(string target) => (target ?? "").IndexOf(ChainSep, StringComparison.Ordinal) > 0;

        public static string ChainTarget(IEnumerable<string> proxyNames)
            => string.Join(ChainSep, (proxyNames ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)));

        // The proxy names a target stands for: the hops of a cascade, else the target itself.
        public static List<string> ChainHops(string target)
        {
            var t = (target ?? "").Trim();
            if (t.Length == 0) return new List<string>();
            return t.Split(new[] { ChainSep }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        }

        // The engine name of the copy that carries hop `i` (0-based, ≥ 1) of this chain for a tile
        // with these cards (null = Windows decides).
        private static string ChainCloneName(List<string> hops, int i, string nicKey)
            => hops[i] + ChainMarker + Hash8(string.Join(ChainSep, hops.Take(i + 1)) + "\n" + (nicKey ?? ""));

        // What a node check of this target has to ask the engine about: the last copy of a cascade,
        // else the variant (through the tile's cards — ViaName).
        public static string ChainNodeName(string target, string nicKey)
        {
            if (!IsChain(target)) return ViaName(target, nicKey);
            var hops = ChainHops(target);
            return hops.Count == 1 ? ViaName(hops[0], nicKey) : ChainCloneName(hops, hops.Count - 1, nicKey);
        }

        // The cards behind an engine name (a card, a group of cards, or a VPN copy through them);
        // null for anything else.
        public static string NicLabelOf(string target)
        {
            var t = (target ?? "").Trim();
            lock (_nicLabels)
            {
                string label;
                if (_nicLabels.TryGetValue(t, out label)) return label;
            }
            return null;
        }

        // The engine name a variant runs under for a tile tied to these cards — the `.via-` copy
        // Build makes (NicPlan.Route) — or the variant itself when the key is null. What a check
        // of "is this node up FOR THIS TILE" has to ask the engine about.
        public static string ViaName(string proxyName, string nicKey)
            => string.IsNullOrEmpty(nicKey) || string.IsNullOrEmpty(proxyName) ? proxyName : proxyName + ViaMarker + Hash8(nicKey);

        // The tile's cards as one key ("A\nB"), null when it leaves the choice to Windows.
        public static string NicKey(AppEntry app)
        {
            if (app == null || !app.HasInterfaces) return null;
            var cards = NetCards.Clean(app.Interfaces);
            return cards.Count == 0 ? null : string.Join("\n", cards);
        }

        private static string Hash8(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string YQ(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // The REJECT net under subscription groups (see the proxy-providers block).
        private const string NoNodeProvider = "ml_no_node";
        private const string NoNodeName = "ML-NO-NODE";

        // A node name as a literal inside a Go (RE2) regexp. Not Regex.Escape: .NET also escapes
        // spaces and '#', and RE2 refuses escapes of non-punctuation.
        private static string GoRegexEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "")
            {
                if (@"\.+*?()|[]{}^$".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private sealed class NicPlan
        {
            private readonly AppConfig _cfg;
            private readonly Dictionary<string, ParsedProxy> _direct;
            private readonly Dictionary<string, ParsedProxy> _providers;
            public readonly List<string> Cards = new List<string>();                          // nic-<h> per card, in first-use order
            public readonly Dictionary<string, string> CardOf = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<KeyValuePair<string, List<string>>> Groups = new List<KeyValuePair<string, List<string>>>();
            public readonly List<ParsedProxy> DirectClones = new List<ParsedProxy>();
            public readonly List<ParsedProxy> ProviderClones = new List<ParsedProxy>();
            private readonly HashSet<string> _made = new HashSet<string>(StringComparer.Ordinal);

            public NicPlan(AppConfig cfg, List<ParsedProxy> direct, List<ParsedProxy> providers)
            {
                _cfg = cfg;
                _direct = new Dictionary<string, ParsedProxy>(StringComparer.Ordinal);
                foreach (var p in direct) if (!string.IsNullOrEmpty(p.ProxyName)) _direct[p.ProxyName] = p;
                _providers = new Dictionary<string, ParsedProxy>(StringComparer.Ordinal);
                foreach (var p in providers) if (!string.IsNullOrEmpty(p.ProxyName)) _providers[p.ProxyName] = p;
            }

            public bool Any => Cards.Count > 0;

            private static void Label(string name, string text) { lock (_nicLabels) _nicLabels[name] = text; }

            // The card (or the fallback group over the cards) for this key.
            private string Outbound(string key)
            {
                var cards = key.Split('\n');
                var members = new List<string>();
                foreach (var card in cards)
                {
                    var n = NicPrefix + Hash8(card);
                    if (_made.Add(n)) { Cards.Add(n); CardOf[n] = card; Label(n, "«" + card + "»"); }
                    members.Add(n);
                }
                if (members.Count == 1) return members[0];
                var g = NicsPrefix + Hash8(key);
                if (_made.Add(g))
                {
                    Groups.Add(new KeyValuePair<string, List<string>>(g, members));
                    Label(g, string.Join(" › ", cards.Select(c => "«" + c + "»")));
                }
                return g;
            }

            // Where a line pointed at `target` goes when its tile is tied to the cards in `key`.
            // A cascade target (IsChain) becomes its chain of copies whatever the cards.
            public string Route(string target, string key)
            {
                if (string.IsNullOrEmpty(target)) return target;
                if (IsChain(target)) return Chain(target, key);
                if (string.IsNullOrEmpty(key)) return target;
                if (target.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)) return target;
                var via = Outbound(key);
                if (target.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return via;

                var clone = target + ViaMarker + Hash8(key);
                if (_made.Contains(clone)) return clone;
                return Clone(target, clone, via, NicLabelOf(via)) ? clone : target;   // not one of ours: as it was
            }

            // hop 1 (through the tile's cards, if any) → a copy of hop 2 dialled through it → a copy
            // of hop 3 dialled through that copy. Returns the last one — what the tile's lines point at.
            private string Chain(string target, string key)
            {
                var hops = ChainHops(target);
                if (hops.Count == 0) return target;
                var prev = Route(hops[0], key);
                for (int i = 1; i < hops.Count; i++)
                {
                    var name = ChainCloneName(hops, i, key);
                    if (!_made.Contains(name) && !Clone(hops[i], name, prev, null))
                        return "REJECT";   // a hop the config doesn't carry: nothing goes out rather than around it
                    prev = name;
                }
                return prev;
            }

            // A copy of the variant `target` named `clone`, its own connection dialled through `via`.
            private bool Clone(string target, string clone, string via, string label)
            {
                ParsedProxy p;
                if (_direct.TryGetValue(target, out p))
                {
                    var lines = new List<string>(p.ProxyYamlLines);
                    if (lines.Count == 0) return false;
                    lines[0] = "  - name: " + YQ(clone);
                    lines.Add("    dialer-proxy: " + YQ(via));
                    _made.Add(clone);
                    DirectClones.Add(new ParsedProxy
                    {
                        Ok = true, ProxyName = clone, SlotId = p.SlotId, Server = p.Server,
                        ProxyYamlLines = lines, ServerDns = p.ServerDns,
                    });
                    if (label != null) Label(clone, label);
                    return true;
                }
                if (_providers.TryGetValue(target, out p))
                {
                    _made.Add(clone);
                    // A subscription's copy reads its own cached file: start it from the one the
                    // launcher already has, so the policy (SubscriptionPolicy.For) is there at once.
                    if (string.IsNullOrEmpty(p.ProviderFileContent))
                    {
                        try
                        {
                            var src = SubscriptionPolicy.CachePath(_cfg, target);
                            var dst = SubscriptionPolicy.CachePath(_cfg, clone);
                            if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                        }
                        catch (Exception ex) { Log.Error("nic-sub-copy", ex); }
                    }
                    ProviderClones.Add(new ParsedProxy
                    {
                        Ok = true, IsProvider = true, ProxyName = clone, SlotId = p.SlotId,
                        ProviderUrl = p.ProviderUrl, ProviderIntervalSeconds = p.ProviderIntervalSeconds,
                        ProviderFileContent = p.ProviderFileContent, DialerProxy = via,
                        SubNode = p.SubNode, SubCacheName = p.SubCacheName,
                    });
                    if (label != null) Label(clone, label);
                    return true;
                }
                return false;
            }

            // Every copy of every prefix of a cascade (hop 1; 1→2; 1→2→3) in the config, used by a
            // rule or not: the connect checks the hops one by one through the engine's API, and an
            // unreferenced proxy is never dialled by itself.
            public void AllPrefixes(string target, string key)
            {
                var hops = ChainHops(target);
                for (int n = 1; n <= hops.Count; n++) Route(ChainTarget(hops.Take(n)), key);
            }
        }

        // chainPrefixes: cascade targets (key) and the cards of their tile (value, null = none) whose
        // every prefix must be in the config — the cascade connect checks hop by hop.
        public static string Build(AppConfig cfg, List<ParsedProxy> proxies, List<RuleDef> rules,
                                   IEnumerable<KeyValuePair<string, string>> chainPrefixes = null)
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
            // The level the launcher's log feed keeps the engine at anyway (EngineLog sets it on
            // start and re-asserts it every 20 s). "warning" here only made every reload switch
            // the engine quiet for up to 20 s — exactly while it reloaded, so the lines that say
            // why a reload took seconds never came out (slow-reload diagnostics, 2026-11-09).
            sb.AppendLine("log-level: debug");
            // Always resolve the owning process so PROCESS-NAME rules (the kill-switch)
            // reliably match under TUN.
            sb.AppendLine("find-process-mode: always");
            sb.AppendLine("external-controller: " + api);
            if (!string.IsNullOrEmpty(cfg.MihomoSecret))
                sb.AppendLine("secret: \"" + cfg.MihomoSecret.Replace("\"", "\\\"") + "\"");
            // When the engine needs its ASN base (IP-ASN in a subscription's lists) and has none,
            // it downloads one — from GitHub releases by default, often blocked from RU. Ours is
            // the same GeoLite2-ASN. The other geo bases keep their defaults: nothing we write uses them.
            sb.AppendLine("geox-url:");
            sb.AppendLine("  asn: \"" + ByfoxApi.GeoAsnUrl + "\"");
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
            // strict-route ON (2026-11-13). On Windows it does NOT force everything into the tunnel
            // (sing-tun v0.4.29 tun_windows.go: the block-all IPv4 filter is commented out), so the
            // LAN/RDP excludes below keep working. What it does, through a dynamic WFP session that
            // dies with the engine: (1) blocks port 53 for every process except the engine on any
            // interface but ours — Windows asks ALL adapters' resolvers in parallel, and the Wi-Fi
            // one (the router, inside the excluded LAN) was getting every name and passing it to the
            // ISP (seen on dnscheck.tools); (2) blocks all IPv6 outside the tunnel (it has no IPv6),
            // which would otherwise bypass VPN and kill-switch alike. Inbound (RDP) is not filtered.
            // Side effect: the DNS probe copy (DirectDns.QueryAsync) can't use plain port-53 DNS.
            sb.AppendLine("  strict-route: true");
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

            // Network cards (NicPlan): every line of a tile tied to cards is re-pointed through them,
            // switched on or off, VPN or direct. A copy of each RuleDef — the caller's list is not
            // ours to change.
            // The launcher's own probe rule too, when the tile under test is tied to cards
            // (2026-11-16): its check has to cross the same card the tile will, or a VPN that answers
            // only through that card (ekb_home: through Wi-Fi yes, through the Turkish Ethernet no)
            // was judged dead and the tile thrown back to «напрямую».
            var nic = new NicPlan(cfg, directProxies, providers);
            // A cascade target becomes its chain of copies too (NicPlan.Chain), cards or not.
            rules = rules.Where(r => r != null)
                .Select(r => r.Nic == null && !IsChain(r.Target) ? r
                    : new RuleDef(r.Type, r.Value, nic.Route(r.Target, r.Nic))
                      { Raw = r.Raw, Site = r.Site, Probe = r.Probe, Folder = r.Folder, FolderLen = r.FolderLen, AutoFile = r.AutoFile })
                .ToList();
            // A cascade being connected: each prefix of it as well, for the hop-by-hop check.
            foreach (var c in chainPrefixes ?? Enumerable.Empty<KeyValuePair<string, string>>())
                if (IsChain(c.Key)) nic.AllPrefixes(c.Key, c.Value);
            // rules: split entries first, the catch-all last — DIRECT, unless «Весь трафик» is
            // on, in which case its target (a proxy, or REJECT under its kill-switch). The engine
            // stops at the first MATCH; the last one given is what the loop below used to keep.
            var matchRule = rules.LastOrDefault(r => "MATCH".Equals(r.Type, StringComparison.OrdinalIgnoreCase));
            string catchAll = matchRule != null && !string.IsNullOrEmpty(matchRule.Target) ? matchRule.Target : "DIRECT";
            // «Всё остальное» writes no MATCH of its own when it is off without a kill-switch — its
            // cards still hold: what no tile claims leaves through them.
            var allTile = (cfg.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.IsAllTraffic);
            if (matchRule == null && allTile != null && allTile.HasInterfaces)
                catchAll = nic.Route("DIRECT", NicKey(allTile));

            // With the catch-all pointed anywhere but DIRECT, a rule that wrote NO line of its own
            // (a «напрямую» rule, or one switched off without a kill-switch) would be swept into
            // «Весь трафик». It isn't its business: every tile lives its own life, so whatever
            // is left of them is pinned DIRECT explicitly, just above the catch-all. A tile tied to
            // cards gets those lines always — through its cards, not Windows' one.
            var pinned = new List<RuleDef>();
            {
                bool catchAllDirect = catchAll.Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
                var written = new HashSet<string>(
                    rules.Where(r => !string.IsNullOrEmpty(r.Type)).Select(r => r.Type + "|" + r.Value),
                    StringComparer.OrdinalIgnoreCase);
                // Once any tile has a folder, a «без VPN» tile's programs and folders need their lines
                // even with the catch-all DIRECT: its program may lie inside another tile's folder, its
                // folder inside a wider one — and the more specific entry has to win (2026-11-22).
                bool anyFolder = (cfg.Apps ?? new List<AppEntry>()).Any(a => a != null && !a.IsAllTraffic && a.HasFolders);
                foreach (var app in (cfg.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.IsAllTraffic))
                {
                    var mine = new List<RuleDef>();
                    AddAppRules(mine, app, "DIRECT");
                    foreach (var r in mine)
                    {
                        // Catch-all DIRECT, no cards: the tile's PROGRAMS need no line (they end up
                        // DIRECT anyway). Its SITES do (2026-11-16): without a line, another tile's
                        // program (chrome.exe → VPN / Wi-Fi) took kinozal.tv away from the «без VPN»
                        // tile that lists it. Sites beat programs whatever the tiles' modes.
                        // An auto file line always: it sits above every program by name, so another
                        // tile naming the same exe must not take this very file away (2026-11-22).
                        bool processLine = r.Folder || string.Equals(r.Type, "PROCESS-NAME", StringComparison.OrdinalIgnoreCase);
                        if (catchAllDirect && !app.HasInterfaces && !r.Site && !r.AutoFile && !(anyFolder && processLine)) continue;
                        if (written.Add(r.Type + "|" + r.Value))
                            pinned.Add(new RuleDef(r.Type, r.Value, nic.Route("DIRECT", r.Nic))
                                       { Site = r.Site, Folder = r.Folder, FolderLen = r.FolderLen, AutoFile = r.AutoFile });
                    }
                }
            }
            directProxies.AddRange(nic.DirectClones);
            providers.AddRange(nic.ProviderClones);

            if (directProxies.Count > 0 || nic.Any)
            {
                sb.AppendLine("proxies:");
                foreach (var p in directProxies)
                    foreach (var line in p.ProxyYamlLines)
                        sb.AppendLine(line);
                foreach (var n in nic.Cards)
                {
                    sb.AppendLine("  - name: " + YQ(n));
                    sb.AppendLine("    type: direct");
                    sb.AppendLine("    interface-name: " + YQ(nic.CardOf[n]));
                    sb.AppendLine("    udp: true");
                }
                sb.AppendLine();
            }

            // proxy-providers: (Clash subscriptions)
            if (providers.Count > 0)
            {
                sb.AppendLine("proxy-providers:");
                // The safety net under every subscription group: one REJECT "node", listed after the
                // subscription's own. A group whose filter matches nothing would otherwise get the
                // engine's COMPATIBLE — i.e. DIRECT, the tile's traffic out in the open (measured on
                // the binary 2026-11-22). `proxies: [REJECT]` can't do it: those come FIRST in the
                // group and would be the default.
                WriteProviderFile(cfg, NoNodeProvider + ".yaml",
                    "proxies:\n  - {name: " + YQ(NoNodeName) + ", type: reject}");
                sb.AppendLine("  " + NoNodeProvider + ":");
                sb.AppendLine("    type: file");
                sb.AppendLine("    path: ./providers/" + NoNodeProvider + ".yaml");
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
                        // Same User-Agent as the launcher's own download (SubscriptionPolicy).
                        sb.AppendLine("    header:");
                        sb.AppendLine("      User-Agent:");
                        sb.AppendLine("        - " + YQ(ByfoxApi.SubscriptionUserAgent));
                        // Exactly one node: the user's pick, else the author's default. No copy on
                        // disk yet = no filter; the engine then starts on the first node.
                        var node = SubscriptionPolicy.ResolveNode(cfg,
                            string.IsNullOrEmpty(p.SubCacheName) ? p.ProxyName : p.SubCacheName, p.SubNode);
                        if (node != null && node.IndexOf('`') < 0)   // ` splits mihomo filters
                            sb.AppendLine("    filter: " + YQ("^" + GoRegexEscape(node) + "$"));
                    }
                    sb.AppendLine("    health-check:");
                    sb.AppendLine("      enable: true");
                    sb.AppendLine("      url: " + HealthCheckUrl);
                    sb.AppendLine("      interval: 300");
                    // A tile tied to network cards: every node of this copy is dialled through them.
                    if (!string.IsNullOrEmpty(p.DialerProxy))
                    {
                        sb.AppendLine("    override:");
                        sb.AppendLine("      dialer-proxy: " + YQ(p.DialerProxy));
                    }
                }
                sb.AppendLine();
            }

            // proxy-groups: providers need a group — a plain select over the one node the provider
            // lets through, then the REJECT net (nothing measured, nothing switches by itself;
            // url-test used to move a VLESS-first subscription onto Hysteria by ping); several
            // network cards of one tile are a fallback group — the first card in the tile's order
            // that answers.
            // Direct proxies are referenced by name in rules, no group needed.
            if (providers.Count > 0 || nic.Groups.Count > 0)
            {
                sb.AppendLine("proxy-groups:");
                foreach (var g in nic.Groups)
                {
                    sb.AppendLine("  - name: " + YQ(g.Key));
                    sb.AppendLine("    type: fallback");
                    sb.AppendLine("    url: " + HealthCheckUrl);
                    sb.AppendLine("    interval: 30");
                    sb.AppendLine("    timeout: 3000");
                    sb.AppendLine("    lazy: false");
                    sb.AppendLine("    proxies: [" + string.Join(", ", g.Value.Select(YQ)) + "]");
                }
                foreach (var p in providers)
                {
                    sb.AppendLine("  - name: " + p.ProxyName);
                    sb.AppendLine("    type: select");
                    sb.AppendLine("    use:");
                    sb.AppendLine("      - " + p.ProxyName + "_prov");
                    sb.AppendLine("      - " + NoNodeProvider);
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
            SubscriptionPolicy.AppendBlocks(sb, policies.Values, AppConfigStore.ResolveMihomoWorkDir(cfg));

            bool asnReady = AsnDbReady(cfg);
            // The engine stops at the first match: the launcher's probe first, then the sites and
            // templates of every tile, then the programs (and check addresses) of every tile —
            // each block in the order of the tiles (stable). «Всё остальное» (MATCH) is last.
            // The pinned lines (DIRECT / card tiles, see `pinned` above) take part in the same split
            // (2026-11-16): they used to trail after everything in tile order, so a «без VPN» tile's
            // site (kinozal.tv → LAN) sat below another tile's program (chrome.exe → Wi-Fi) and the
            // browser took it. Sites of every tile above programs of every tile — pinned or not.
            // Folders (2026-11-22) go below every program: a program named in any tile beats any
            // tile's folder, and among folders the deeper path wins, whatever the tile order (stable
            // sort, so equal lengths keep the order of the tiles).
            // Auto-tracked files (2026-11-22) right below the sites: an exact file beats any tile's
            // program by name. Order: sites → auto files → programs by name → folders → MATCH.
            rules = rules.Where(r => r != null && r.Probe)
                .Concat(rules.Where(r => r != null && !r.Probe && r.Site))
                .Concat(pinned.Where(r => r.Site))
                .Concat(rules.Where(r => r != null && !r.Probe && !r.Site && r.AutoFile))
                .Concat(pinned.Where(r => !r.Site && r.AutoFile))
                .Concat(rules.Where(r => r != null && !r.Probe && !r.Site && !r.Folder && !r.AutoFile))
                .Concat(pinned.Where(r => !r.Site && !r.Folder && !r.AutoFile))
                .Concat(rules.Where(r => r != null && !r.Probe && !r.Site && r.Folder)
                             .Concat(pinned.Where(r => !r.Site && r.Folder))
                             .OrderByDescending(r => r.FolderLen)).ToList();
            sb.AppendLine("rules:");
            bool ownWritten = false;
            foreach (var r in rules)
            {
                if (r == null || string.IsNullOrEmpty(r.Type) || string.IsNullOrEmpty(r.Target)) continue;
                if (!r.Probe && !ownWritten) { AppendOwnServer(sb); ownWritten = true; }
                if (r.Type.Equals("MATCH", StringComparison.OrdinalIgnoreCase)) continue;   // catchAll, above
                if (IsAsnRule(r.Type) && !asnReady) continue;
                // IP rules must skip DNS resolution under fake-ip, or they never match.
                bool ipRule = IsIpRule(r.Type);
                if (IsCidrRule(r.Type) && !IsValidPrefix(r.Value)) { NoteDroppedPrefix(r.Type, r.Value); continue; }
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
            if (!ownWritten) AppendOwnServer(sb);
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
