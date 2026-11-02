using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // The routing a Clash subscription carries with it, made usable by ONE tile.
    //
    // A subscription is not a server. It is a profile: nodes, groups, rule lists, and the rules
    // that say which sites go through the node, which go direct and which are blocked — with a
    // MATCH at the end for everything else. Until 2026-11-02 the launcher used it as a
    // proxy-provider only, and a proxy-provider by definition reads `proxies:` and nothing else.
    // The rules were thrown away, so a tile pointed at a subscription sent EVERYTHING of its
    // program through the node: «хром извернулся и пошёл в интернет на все сайты», while the
    // issuer's panel said "Direct for whatever isn't listed". Every Clash client applies the
    // profile's rules — Clash Verge, Mihomo Party, and Clash Meta for Android, whose per-app
    // "access control" is exactly our tile: the app enters the tunnel, the profile decides.
    //
    // mihomo has the piece for this built in: `sub-rules`. The subscription's rules become a
    // named rule block, its rule lists are copied under our own names, and the tile's matchers
    // become `SUB-RULE,(PROCESS-NAME,chrome.exe),<name>`. Measured on the 1.19.32 binary: the
    // block runs for that program only, its MATCH ends it, `(IP-CIDR,…,no-resolve)` is accepted,
    // `(MATCH)` is not (hence the NETWORK pair for the catch-all tile).
    //
    // Groups are NOT copied: nodes come from the provider, which can't be named member by member.
    // Every group the rules name collapses to our one group over all the subscription's nodes —
    // unless its default is DIRECT or REJECT, which is what it then stays. VPN / direct / block
    // survive exactly; only "which node for which service" inside one subscription is lost.
    //
    // A subscription that is just a list of links (base64 / Shadowrocket) has no rules and stays
    // a server list: the whole tile through the node, same as a vless link.
    public sealed class SubPolicy
    {
        public string Name { get; set; } = "";                       // = the proxy group = the sub-rules block
        public List<string> ProviderLines { get; } = new List<string>();  // lines under `rule-providers:`
        public List<string> RuleLines { get; } = new List<string>();      // the block's rules, already quoted
        public List<RuleListFile> Lists { get; } = new List<RuleListFile>();
        public List<string> Dropped { get; } = new List<string>();        // what could not be carried over, for the log
        public string Final { get; set; } = "DIRECT";                     // where the block's MATCH sends the rest
        public string Signature { get; set; } = "";                       // changes iff the emitted text changes

        // For people, not the engine: the rules in order, one row per list (the issuer's panel
        // shows them the same way), plus how many servers the subscription brings.
        public List<SubRow> Rows { get; } = new List<SubRow>();
        public int NodeCount { get; set; }
        public SubAction FinalAction { get; set; }
    }

    // One line of «what this subscription does»: a rule list, or a run of individual rules.
    public sealed class SubRow
    {
        public string Title { get; set; }          // null = individual rules (the window names them)
        public SubAction Action { get; set; }
        public string RelPath { get; set; }       // the list's file in the work dir; null = Items below
        public string Format { get; set; } = "yaml";
        public List<string> Items { get; set; }   // individual rules / an inline list
    }

    public enum SubAction { Vpn, Direct, Block, Pass }

    public enum SubStage { Downloading, Checking, Done }

    public sealed class SubProgress
    {
        public SubStage Stage { get; set; }
        public int Step { get; set; }              // 0 = the subscription, 1..Steps-1 = its lists
        public int Steps { get; set; }
        public string Title { get; set; }          // the list being fetched; null for the subscription
        public long Bytes { get; set; }
        public long Total { get; set; }            // -1 = the server didn't say
        public double BytesPerSecond { get; set; }
        public double Overall { get; set; }        // 0..1
        public double EtaSeconds { get; set; }     // -1 = not known yet
    }

    public sealed class RuleListFile
    {
        public string Key { get; set; } = "";
        public string Url { get; set; } = "";
        public string RelPath { get; set; } = "";   // as written in the config: ./providers/rules/…
    }

    public static class SubscriptionPolicy
    {
        // What mihomo sends when it fetches a provider. The issuer may answer differently per
        // client, and the cached file has to be the same text the engine would have stored.
        private const string ClashUserAgent = "clash.meta";

        static SubscriptionPolicy()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
        }

        // providers\<ProxyName>.yaml — the file the engine's http provider downloads into, so
        // the launcher and the engine read the same copy.
        public static string CachePath(AppConfig cfg, string proxyName)
            => Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), "providers", proxyName + ".yaml");

        // ---------- before connecting ----------

        // Make sure the subscription is on disk, readable by the engine, and its rules usable.
        // Returns null when the variant can be used, else the reason it can't — a subscription
        // whose routing is unknown must not be connected, or the tile goes back to sending its
        // whole program through the node, which is the bug this class exists to fix.
        // Awaits resume on the caller's (UI) thread: the reasons come from Loc.
        public static async Task<string> PrepareAsync(AppConfig cfg, ParsedProxy proxy, CancellationToken ct)
        {
            if (proxy == null || !proxy.IsProvider || string.IsNullOrEmpty(proxy.ProviderUrl)) return null;
            var path = CachePath(cfg, proxy.ProxyName);

            if (!File.Exists(path))
            {
                try { await DownloadToAsync(proxy.ProviderUrl, path, null, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Info("Подписка «" + proxy.ProxyName + "»: не скачалась — " + ex.Message);
                    return Loc.T("Svc_SubDownloadFailed", ex.Message);
                }
            }

            string content;
            try { content = File.ReadAllText(path); }
            catch (Exception ex) { return Loc.T("Svc_SubDownloadFailed", ex.Message); }

            var foreign = ForeignFormat(content);
            if (foreign != null)
            {
                // Left on disk the engine would keep "loading" it into an empty group.
                try { File.Delete(path); } catch { }
                Log.Info("Подписка «" + proxy.ProxyName + "»: формат " + foreign + " — движок такой не читает.");
                return Loc.T("Svc_SubFormatUnsupported", foreign);
            }

            var policy = For(cfg, proxy.ProxyName);
            if (policy == null) return null;   // a plain server list: nothing to carry over

            await FetchListsAsync(cfg, policy, all: false, report: null, ct: ct);

            var why = await Task.Run(() => Validate(cfg, policy), ct);
            if (why != null)
            {
                Log.Info("Подписка «" + proxy.ProxyName + "»: её правила движок не принимает — " + why);
                return Loc.T("Svc_SubRulesRejected", why);
            }
            return null;
        }

        // «Обновить». Fetched here, not by the engine: the engine holds a subscription only while
        // some tile rides it, so asking it answered "not loaded" for every variant not in use —
        // after the old button had already deleted the cached copy. Every Clash client's «Update»
        // downloads the profile itself, connected or not. The copy is replaced only by a good
        // download; the rule lists are fetched again too. Null = done, else the reason.
        public static async Task<string> RefreshAsync(AppConfig cfg, string proxyName, string url,
                                                      IProgress<SubProgress> progress, CancellationToken ct)
        {
            var report = new ProgressReporter(progress);
            var path = CachePath(cfg, proxyName);
            var fresh = path + ".new";

            report.Begin(0, 1, null);
            try { await DownloadToAsync((url ?? "").Trim(), fresh, report, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Loc.T("Svc_SubDownloadFailed", ex.Message); }

            var foreign = ForeignFormat(File.ReadAllText(fresh));
            if (foreign != null)
            {
                try { File.Delete(fresh); } catch { }
                return Loc.T("Svc_SubFormatUnsupported", foreign);
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(fresh, path);
            var policy = For(cfg, proxyName);
            if (policy == null) { report.Finish(); return null; }   // a plain server list

            await FetchListsAsync(cfg, policy, all: true, report: report, ct: ct);
            report.Checking();
            var why = await Task.Run(() => Validate(cfg, policy), ct);
            report.Finish();
            if (why != null) return Loc.T("Svc_SubRulesRejected", why);
            return report.Failed.Count > 0 ? Loc.T("Sub_ListsFailed", string.Join(", ", report.Failed)) : null;
        }

        // Streamed to a ".part" next to the target and moved into place only when complete, so a
        // broken download never replaces a good copy. Off the UI thread: a list can be 8 MB.
        private static async Task DownloadToAsync(string url, string path, ProgressReporter report, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var part = path + ".part";
            await Task.Run(async () =>
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(ClashUserAgent);
                    using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? -1;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var dst = File.Create(part))
                        {
                            var buf = new byte[64 * 1024];
                            long done = 0;
                            int n;
                            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                                done += n;
                                report?.Bytes(done, total);
                            }
                        }
                    }
                }
            }, ct).ConfigureAwait(false);
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
        }

        // The engine fetches rule lists itself, but only once it runs the config, through its own
        // rules, and a list that failed stays EMPTY until its next interval (a day): every site in
        // it then quietly falls to the block's MATCH — Telegram direct instead of through the
        // node, measured on the binary. Fetched here first, a failure at least has a name.
        // all = also the lists already on disk («Обновить»); otherwise only the missing ones.
        private static async Task FetchListsAsync(AppConfig cfg, SubPolicy policy, bool all,
                                                  ProgressReporter report, CancellationToken ct)
        {
            var workDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
            var failed = new List<string>();
            var todo = policy.Lists
                .Select(l => new { List = l, Full = Path.Combine(workDir, l.RelPath.Substring(2).Replace('/', Path.DirectorySeparatorChar)) })
                .Where(x => all || !File.Exists(x.Full)).ToList();
            for (int i = 0; i < todo.Count; i++)
            {
                var title = policy.Rows.FirstOrDefault(r => r.RelPath == todo[i].List.RelPath)?.Title ?? todo[i].List.Key;
                report?.Begin(i + 1, todo.Count + 1, title);
                try { await DownloadToAsync(todo[i].List.Url, todo[i].Full, report, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add(title); Log.Info("Подписка «" + policy.Name + "»: список " + title + " не скачался — " + ex.Message); }
            }
            if (failed.Count > 0)
                Log.Info("Подписка «" + policy.Name + "»: пока движок не скачает " + string.Join(", ", failed)
                         + " сам, сайты из них пойдут по концу правил подписки (" + policy.Final + ").");
            report?.Failed.AddRange(failed);
        }

        // ---------- progress ----------

        // Where an «Обновить» is: step 0 is the subscription itself, steps 1..N its lists, then the
        // engine's check. Overall is the share of steps done plus the current file's share of its
        // step; the time left is extrapolated from that share, like any download dialog does.
        private sealed class ProgressReporter
        {
            private readonly IProgress<SubProgress> _sink;
            private readonly System.Diagnostics.Stopwatch _all = System.Diagnostics.Stopwatch.StartNew();
            private System.Diagnostics.Stopwatch _file = new System.Diagnostics.Stopwatch();
            private int _step, _steps = 1;
            private string _title;
            private long _lastTick;
            public readonly List<string> Failed = new List<string>();

            public ProgressReporter(IProgress<SubProgress> sink) { _sink = sink; }

            public void Begin(int step, int steps, string title)
            {
                _step = step; _steps = Math.Max(1, steps); _title = title;
                _file = System.Diagnostics.Stopwatch.StartNew();
                Emit(SubStage.Downloading, 0, -1, true);
            }

            public void Bytes(long done, long total) => Emit(SubStage.Downloading, done, total, false);
            public void Checking() => Emit(SubStage.Checking, 0, -1, true);
            public void Finish() => Emit(SubStage.Done, 0, -1, true);

            private void Emit(SubStage stage, long done, long total, bool force)
            {
                if (_sink == null) return;
                var now = _all.ElapsedMilliseconds;
                if (!force && now - _lastTick < 100) return;   // ten updates a second is plenty
                _lastTick = now;

                // The subscription is small and the number of lists is unknown until it's read, so
                // it gets the first 5% and the lists share the rest — the bar never runs backwards.
                double fileShare = total > 0 ? Math.Min(1.0, (double)done / total) : 0;
                double overall = stage == SubStage.Done ? 1.0
                               : stage == SubStage.Checking ? 0.98
                               : _step == 0 ? 0.05 * fileShare
                               : 0.05 + 0.92 * (_step - 1 + fileShare) / Math.Max(1, _steps - 1);
                double secs = _file.Elapsed.TotalSeconds;
                double eta = overall > 0.05 && stage == SubStage.Downloading
                    ? _all.Elapsed.TotalSeconds * (1 - overall) / overall : -1;
                _sink.Report(new SubProgress
                {
                    Stage = stage,
                    Step = _step,
                    Steps = _steps,
                    Title = _title,
                    Bytes = done,
                    Total = total,
                    BytesPerSecond = secs > 0.2 ? done / secs : 0,
                    Overall = overall,
                    EtaSeconds = eta,
                });
            }
        }

        // One `mihomo -t` per distinct rule set. A config the engine refuses is refused WHOLE —
        // every tile, not just this one — so a subscription's rules are checked on their own,
        // before they can get anywhere near config.yaml.
        private static readonly Dictionary<string, string> _verdicts = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string Validate(AppConfig cfg, SubPolicy policy)
        {
            lock (_verdicts)
            {
                string cached;
                if (_verdicts.TryGetValue(policy.Signature, out cached)) return cached;
            }

            var sb = new StringBuilder();
            sb.AppendLine("proxy-groups:");
            sb.AppendLine("  - name: " + policy.Name);
            sb.AppendLine("    type: select");
            sb.AppendLine("    proxies: [DIRECT]");
            AppendBlocks(sb, new[] { policy });
            sb.AppendLine("rules:");
            sb.AppendLine("  - SUB-RULE," + CatchAllMatcher + "," + policy.Name);
            sb.AppendLine("  - MATCH,DIRECT");

            string result = null;
            try
            {
                var dir = Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), "cfgtest");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "subscription-check.yaml");
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                string why;
                // null = couldn't ask: not a verdict, and not a reason to block the variant.
                if (MihomoBinary.TestConfig(cfg?.MihomoExePath, file, dir, out why) == false) result = why;
                try { File.Delete(file); } catch { }
            }
            catch (Exception ex) { Log.Error("subscription-check", ex); }

            lock (_verdicts) _verdicts[policy.Signature] = result;
            return result;
        }

        // ---------- what goes into config.yaml ----------

        // Matches every connection: `SUB-RULE,(MATCH),x` is refused by the engine.
        public const string CatchAllMatcher = "(OR,((NETWORK,TCP),(NETWORK,UDP)))";

        // `rule-providers:` and `sub-rules:` for the subscriptions some rule actually points at.
        public static void AppendBlocks(StringBuilder sb, IEnumerable<SubPolicy> policies)
        {
            var list = policies.Where(p => p != null).ToList();
            if (list.Count == 0) return;
            if (list.Any(p => p.ProviderLines.Count > 0))
            {
                sb.AppendLine("rule-providers:");
                foreach (var p in list)
                    foreach (var line in p.ProviderLines) sb.AppendLine(line);
                sb.AppendLine();
            }
            sb.AppendLine("sub-rules:");
            foreach (var p in list)
            {
                sb.AppendLine("  " + p.Name + ":");
                foreach (var rule in p.RuleLines) sb.AppendLine("    - " + rule);
            }
            sb.AppendLine();
        }

        // ---------- reading the subscription ----------

        private sealed class Cached { public string Stamp; public SubPolicy Policy; }
        private static readonly Dictionary<string, Cached> _cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        // The subscription's routing as cached on disk right now; null when the file is missing
        // or carries no rules (a plain list of links). Re-read only when the file changes — the
        // engine rewrites it on every refresh of the provider.
        public static SubPolicy For(AppConfig cfg, string proxyName)
        {
            if (string.IsNullOrEmpty(proxyName)) return null;
            var path = CachePath(cfg, proxyName);
            string stamp;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    // «Обновить» deletes the copy before the engine fetches a new one. A rebuild
                    // in that gap must not fall back to "whole program through the node".
                    lock (_cache) { Cached last; return _cache.TryGetValue(path, out last) ? last.Policy : null; }
                }
                stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
            }
            catch { return null; }

            lock (_cache)
            {
                Cached c;
                if (_cache.TryGetValue(path, out c) && c.Stamp == stamp) return c.Policy;
            }

            SubPolicy policy = null;
            try
            {
                var text = File.ReadAllText(path);
                if (ForeignFormat(text) == null) policy = Parse(proxyName, text);
            }
            catch (Exception ex) { Log.Error("subscription-read " + proxyName, ex); }

            if (policy != null && policy.Dropped.Count > 0)
                Log.Info("Подписка «" + proxyName + "»: пропущено то, что движок у себя повторить не может — "
                         + string.Join("; ", policy.Dropped));

            lock (_cache) _cache[path] = new Cached { Stamp = stamp, Policy = policy };
            return policy;
        }

        // Signatures of the subscriptions the last config was built with — the watchdog compares
        // against them, so a subscription the engine re-downloaded with NEW rules is applied
        // without anyone pressing anything. Only a change of the rules counts: a refresh that
        // brings the same text back must not cost the machine a reload.
        private static Dictionary<string, string> _built = new Dictionary<string, string>(StringComparer.Ordinal);

        public static void NoteBuilt(AppConfig cfg, IEnumerable<string> providerNames)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in providerNames) d[n] = For(cfg, n)?.Signature ?? "";
            lock (_cache) _built = d;
        }

        public static string ChangedSinceBuilt(AppConfig cfg)
        {
            Dictionary<string, string> built;
            lock (_cache) built = _built;
            foreach (var kv in built)
                if ((For(cfg, kv.Key)?.Signature ?? "") != kv.Value) return kv.Key;
            return null;
        }

        // Loon / Surge / Quantumult profiles are INI-like ([Proxy], [Rule]…). mihomo reads
        // neither their nodes nor their rules, and as a provider it would just stay empty.
        private static string ForeignFormat(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"^\s*\[(General|Proxy|Proxy Group|Rule|Remote Rule|Remote Proxy|server_local|filter_remote|Script)\]\s*$",
                                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (Regex.IsMatch(text, @"^\s*\[(Remote Rule|Remote Proxy|Plugin)\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)) return "Loon";
            if (Regex.IsMatch(text, @"^\s*\[(server_local|filter_remote)\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)) return "Quantumult X";
            return "Surge/Loon";
        }

        private static readonly HashSet<string> Builtin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DIRECT", "REJECT", "REJECT-DROP", "PASS", "COMPATIBLE" };

        // Hand-read YAML, the same way as the rest of the launcher (no YAML dependency): only the
        // three sections that matter, in both block and flow style.
        internal static SubPolicy Parse(string name, string text)
        {
            var sections = TopLevelSections(text);
            List<string> rulesBody;
            if (!sections.TryGetValue("rules", out rulesBody)) return null;
            var ruleItems = ListItems(rulesBody).Select(i => Unquote(StripComment(i))).Where(i => i.Length > 0).ToList();
            if (ruleItems.Count == 0) return null;

            var policy = new SubPolicy { Name = name };

            // Group → where its default actually leads.
            var groups = new Dictionary<string, GroupInfo>(StringComparer.Ordinal);
            List<string> groupsBody;
            if (sections.TryGetValue("proxy-groups", out groupsBody))
                foreach (var g in ReadGroups(groupsBody))
                    if (!string.IsNullOrEmpty(g.Name)) groups[g.Name] = g;

            string MapTarget(string target, int depth = 0)
            {
                if (Builtin.Contains(target)) return target.ToUpperInvariant() == "COMPATIBLE" ? "DIRECT" : target.ToUpperInvariant();
                GroupInfo g;
                if (depth > 8 || !groups.TryGetValue(target, out g)) return name;   // a node, or a group of nodes
                var t = (g.Type ?? "select").ToLowerInvariant();
                if (g.Members.Count == 0 || g.UsesProviders) return name;
                // A select group starts on its first member; any other kind picks among real nodes
                // unless all it holds is DIRECT/REJECT.
                if (t == "select" || g.Members.All(m => Builtin.Contains(m)))
                    return MapTarget(g.Members[0], depth + 1);
                return name;
            }

            List<string> proxiesBody;
            if (sections.TryGetValue("proxies", out proxiesBody)) policy.NodeCount = ItemBlocks(proxiesBody).Count;

            SubAction ActionOf(string mapped)
                => mapped == name ? SubAction.Vpn
                 : mapped.StartsWith("REJECT", StringComparison.Ordinal) ? SubAction.Block
                 : mapped == "PASS" ? SubAction.Pass : SubAction.Direct;

            // Consecutive individual rules with one action make one row; a list always its own.
            void AddRow(SubRow row)
            {
                var last = policy.Rows.LastOrDefault();
                if (row.RelPath == null && row.Items != null && last != null && last.RelPath == null
                    && last.Items != null && last.Title == row.Title && last.Action == row.Action)
                    last.Items.AddRange(row.Items);
                else policy.Rows.Add(row);
            }
            var rowOf = new Dictionary<string, SubRow>(StringComparer.Ordinal);   // list key → its row template

            // Rule lists, renamed into our namespace: two subscriptions can both have "google".
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> provBody;
            if (sections.TryGetValue("rule-providers", out provBody))
                foreach (var prov in ReadProviders(provBody))
                {
                    var type = (prov.Get("type") ?? "").ToLowerInvariant();
                    if (type == "file")
                    {
                        policy.Dropped.Add("список «" + prov.Key + "» (лежит на чужом компьютере)");
                        continue;
                    }
                    var key = UniqueKey(name + "__" + SafeKey(prov.Key), renamed.Values);
                    renamed[prov.Key] = key;
                    string relPath = null;
                    var format = (prov.Get("format") ?? "yaml").ToLowerInvariant();
                    var url = prov.Get("url");
                    if (type == "http")
                    {
                        var ext = format == "text" ? ".txt" : format == "mrs" ? ".mrs" : ".yaml";
                        relPath = "./providers/rules/" + key + ext;
                        if (!string.IsNullOrEmpty(url))
                            policy.Lists.Add(new RuleListFile { Key = prov.Key, Url = url, RelPath = relPath });
                    }
                    policy.ProviderLines.AddRange(prov.Emit(key, relPath));
                    rowOf[prov.Key] = new SubRow
                    {
                        Title = ListTitle(prov.Key, url),
                        RelPath = relPath,
                        Format = format,
                        Items = relPath == null ? prov.InlinePayload() : null,
                    };
                }

            bool hasMatch = false;
            foreach (var item in ruleItems)
            {
                var parts = SplitTop(item, ',');
                if (parts.Count == 0) continue;
                var type = parts[0].Trim().ToUpperInvariant();

                if (type == "MATCH" || type == "FINAL")
                {
                    if (parts.Count < 2) continue;
                    policy.Final = MapTarget(parts[1].Trim());
                    policy.RuleLines.Add(Quote("MATCH," + policy.Final));
                    hasMatch = true;
                    break;   // nothing after MATCH is ever reached
                }
                if (parts.Count < 3) { policy.Dropped.Add(item); continue; }
                if (type == "SUB-RULE" || type == "SCRIPT") { policy.Dropped.Add(item); continue; }

                var payload = parts[1].Trim();
                var mappedTarget = MapTarget(parts[2].Trim());
                if (type == "RULE-SET")
                {
                    string k;
                    if (!renamed.TryGetValue(payload, out k)) { policy.Dropped.Add(item); continue; }
                    var tpl = rowOf[payload];
                    AddRow(new SubRow { Title = tpl.Title, RelPath = tpl.RelPath, Format = tpl.Format,
                                        Items = tpl.Items, Action = ActionOf(mappedTarget) });
                    payload = k;
                }
                else if (type == "AND" || type == "OR" || type == "NOT")
                {
                    bool missing = false;
                    payload = Regex.Replace(payload, @"\(\s*RULE-SET\s*,\s*([^,)]+)", m =>
                    {
                        string k;
                        if (renamed.TryGetValue(m.Groups[1].Value.Trim(), out k)) return "(RULE-SET," + k;
                        missing = true;
                        return m.Value;
                    });
                    if (missing) { policy.Dropped.Add(item); continue; }
                }

                if (type != "RULE-SET")
                    AddRow(new SubRow { Title = null, Action = ActionOf(mappedTarget),
                                        Items = new List<string> { parts[0].Trim() + "," + parts[1].Trim() } });

                var rest = parts.Skip(3).Select(x => x.Trim());
                var line = parts[0].Trim() + "," + payload + "," + mappedTarget;
                foreach (var opt in rest) line += "," + opt;
                policy.RuleLines.Add(Quote(line));
            }
            // A Clash profile with no MATCH sends the rest direct.
            if (!hasMatch) policy.RuleLines.Add(Quote("MATCH,DIRECT"));
            policy.FinalAction = ActionOf(policy.Final);

            policy.Signature = Sha(string.Join("\n", policy.ProviderLines) + "\n--\n" + string.Join("\n", policy.RuleLines));
            return policy;
        }

        // ---------- the small YAML reader ----------

        private static int Indent(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        private static bool IsBlank(string line)
        {
            var t = line.Trim();
            return t.Length == 0 || t.StartsWith("#");
        }

        // top-level key → the lines below it. A "- " at column 0 still belongs to the key above.
        private static Dictionary<string, List<string>> TopLevelSections(string text)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> current = null;
            foreach (var raw in (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (IsBlank(line)) continue;
                if (Indent(line) == 0 && !line.StartsWith("-"))
                {
                    var m = Regex.Match(line, @"^(""?)([^"":#]+)\1\s*:\s*(.*)$");
                    if (!m.Success) { current = null; continue; }
                    current = new List<string>();
                    result[m.Groups[2].Value.Trim()] = current;
                    var inline = m.Groups[3].Value.Trim();
                    if (inline.StartsWith("[")) current.Add(inline);   // flow list on the key line
                    continue;
                }
                current?.Add(line);
            }
            return result;
        }

        // Items of a block list ("- a" lines at the list's own indent) or of a flow list "[a, b]".
        private static List<string> ListItems(List<string> body)
        {
            var items = new List<string>();
            if (body == null || body.Count == 0) return items;
            if (body[0].Trim().StartsWith("["))
            {
                var inner = body[0].Trim();
                inner = inner.Substring(1, Math.Max(0, inner.LastIndexOf(']') - 1));
                items.AddRange(SplitTop(inner, ',').Select(s => s.Trim()).Where(s => s.Length > 0));
                return items;
            }
            int ind = -1;
            foreach (var line in body)
            {
                var t = line.TrimStart();
                if (!t.StartsWith("-")) continue;
                if (ind < 0) ind = Indent(line);
                if (Indent(line) != ind) continue;
                items.Add(t.Substring(1).Trim());
            }
            return items;
        }

        private sealed class GroupInfo
        {
            public string Name;
            public string Type;
            public List<string> Members = new List<string>();
            public bool UsesProviders;
        }

        private static IEnumerable<GroupInfo> ReadGroups(List<string> body)
        {
            foreach (var block in ItemBlocks(body))
            {
                var g = new GroupInfo();
                var first = block[0].TrimStart().Substring(1).Trim();
                if (first.StartsWith("{"))
                {
                    var map = FlowMap(first);
                    string v;
                    if (map.TryGetValue("name", out v)) g.Name = Unquote(v);
                    if (map.TryGetValue("type", out v)) g.Type = Unquote(v);
                    if (map.TryGetValue("proxies", out v)) g.Members.AddRange(FlowList(v));
                    g.UsesProviders = map.ContainsKey("use") || map.ContainsKey("include-all")
                                      || map.ContainsKey("include-all-proxies") || map.ContainsKey("include-all-providers");
                    yield return g;
                    continue;
                }
                // Block style: the item's own lines, the first one with its "- " removed.
                var lines = new List<string> { new string(' ', Indent(block[0]) + 2) + first };
                lines.AddRange(block.Skip(1));
                int keyIndent = Indent(lines[0]);
                for (int i = 0; i < lines.Count; i++)
                {
                    if (Indent(lines[i]) != keyIndent) continue;
                    var m = Regex.Match(lines[i].Trim(), @"^([A-Za-z0-9_-]+)\s*:\s*(.*)$");
                    if (!m.Success) continue;
                    var key = m.Groups[1].Value.ToLowerInvariant();
                    var val = StripComment(m.Groups[2].Value).Trim();
                    if (key == "name") g.Name = Unquote(val);
                    else if (key == "type") g.Type = Unquote(val);
                    else if (key == "use" || key.StartsWith("include-all")) g.UsesProviders = true;
                    else if (key == "proxies")
                    {
                        if (val.StartsWith("[")) g.Members.AddRange(FlowList(val));
                        else
                            for (int j = i + 1; j < lines.Count && Indent(lines[j]) > keyIndent; j++)
                            {
                                var t = lines[j].Trim();
                                if (t.StartsWith("-")) g.Members.Add(Unquote(StripComment(t.Substring(1)).Trim()));
                            }
                    }
                }
                yield return g;
            }
        }

        // A list's items, each with the lines that belong to it.
        private static List<List<string>> ItemBlocks(List<string> body)
        {
            var blocks = new List<List<string>>();
            if (body == null) return blocks;
            int ind = -1;
            List<string> cur = null;
            foreach (var line in body)
            {
                if (ind < 0 && line.TrimStart().StartsWith("-")) ind = Indent(line);
                if (ind >= 0 && Indent(line) == ind && line.TrimStart().StartsWith("-"))
                {
                    cur = new List<string> { line };
                    blocks.Add(cur);
                }
                else if (cur != null && Indent(line) > ind) cur.Add(line);
            }
            return blocks;
        }

        private sealed class ProviderDef
        {
            public string Key;
            public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Body = new List<string>();   // block style: the lines under the key
            public string Flow;                               // flow style: "{…}"

            public string Get(string k) { string v; return Fields.TryGetValue(k, out v) ? Unquote(v) : null; }

            // `type: inline` keeps its entries in the subscription itself, under `payload:`.
            public List<string> InlinePayload()
            {
                var items = new List<string>();
                string flowPayload;
                if (Fields.TryGetValue("payload", out flowPayload) && flowPayload.Trim().StartsWith("["))
                    return FlowList(flowPayload);
                bool inPayload = false;
                foreach (var line in Body)
                {
                    var t = line.Trim();
                    if (Indent(line) == Indent(Body[0])) { inPayload = t.StartsWith("payload:"); continue; }
                    if (inPayload && t.StartsWith("-")) items.Add(Unquote(StripComment(t.Substring(1))));
                }
                return items;
            }

            // Re-emitted under our key at our indent. `path` is replaced (ours, inside the work dir
            // — the engine refuses paths outside it); `proxy` is dropped (it names a group of the
            // subscription, which doesn't exist here; the list downloads through the rules).
            public IEnumerable<string> Emit(string key, string relPath)
            {
                if (Flow != null)
                {
                    var inner = Flow.Trim();
                    inner = inner.Substring(1, Math.Max(0, inner.LastIndexOf('}') - 1));
                    var entries = SplitTop(inner, ',').Select(e => e.Trim())
                        .Where(e => e.Length > 0 && !Regex.IsMatch(e, @"^(path|proxy)\s*:", RegexOptions.IgnoreCase)).ToList();
                    if (relPath != null) entries.Add("path: " + relPath);
                    yield return "  " + key + ": {" + string.Join(", ", entries) + "}";
                    yield break;
                }
                yield return "  " + key + ":";
                if (Body.Count == 0) yield break;
                int baseIndent = Indent(Body[0]);
                bool skipping = false;
                foreach (var line in Body)
                {
                    int ind = Indent(line);
                    if (ind <= baseIndent) skipping = false;
                    if (ind == baseIndent && Regex.IsMatch(line.Trim(), @"^(path|proxy)\s*:", RegexOptions.IgnoreCase))
                    {
                        skipping = true;   // and anything nested under it
                        continue;
                    }
                    if (skipping) continue;
                    yield return "    " + line.Substring(Math.Min(baseIndent, line.Length));
                }
                if (relPath != null) yield return "    path: " + relPath;
            }
        }

        private static IEnumerable<ProviderDef> ReadProviders(List<string> body)
        {
            if (body == null || body.Count == 0) yield break;
            int keyIndent = Indent(body[0]);
            ProviderDef cur = null;
            foreach (var line in body)
            {
                if (Indent(line) == keyIndent)
                {
                    if (cur != null) yield return cur;
                    cur = null;
                    var m = Regex.Match(line.Trim(), @"^(""?)([^"":]+)\1\s*:\s*(.*)$");
                    if (!m.Success) continue;
                    cur = new ProviderDef { Key = m.Groups[2].Value.Trim() };
                    var rest = m.Groups[3].Value.Trim();
                    if (rest.StartsWith("{"))
                    {
                        cur.Flow = rest;
                        foreach (var kv in FlowMap(rest)) cur.Fields[kv.Key] = kv.Value;
                    }
                    continue;
                }
                if (cur == null || cur.Flow != null) continue;
                cur.Body.Add(line);
                var f = Regex.Match(line.Trim(), @"^([A-Za-z0-9_-]+)\s*:\s*(.*)$");
                if (f.Success && Indent(line) == Indent(cur.Body[0]))
                    cur.Fields[f.Groups[1].Value] = StripComment(f.Groups[2].Value).Trim();
            }
            if (cur != null) yield return cur;
        }

        private static Dictionary<string, string> FlowMap(string flow)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inner = flow.Trim();
            if (inner.StartsWith("{")) inner = inner.Substring(1, Math.Max(0, inner.LastIndexOf('}') - 1));
            foreach (var entry in SplitTop(inner, ','))
            {
                int c = entry.IndexOf(':');
                if (c <= 0) continue;
                map[Unquote(entry.Substring(0, c).Trim())] = entry.Substring(c + 1).Trim();
            }
            return map;
        }

        private static List<string> FlowList(string v)
        {
            v = v.Trim();
            if (v.StartsWith("[")) v = v.Substring(1, Math.Max(0, v.LastIndexOf(']') - 1));
            return SplitTop(v, ',').Select(s => Unquote(s.Trim())).Where(s => s.Length > 0).ToList();
        }

        // Split on `sep` outside quotes and brackets — logic rules carry commas in parentheses.
        private static List<string> SplitTop(string s, char sep)
        {
            var parts = new List<string>();
            int depth = 0;
            char quote = '\0';
            var cur = new StringBuilder();
            foreach (var ch in s ?? "")
            {
                if (quote != '\0') { if (ch == quote) quote = '\0'; cur.Append(ch); continue; }
                if (ch == '"' || ch == '\'') { quote = ch; cur.Append(ch); continue; }
                if (ch == '(' || ch == '[' || ch == '{') depth++;
                else if (ch == ')' || ch == ']' || ch == '}') depth--;
                if (ch == sep && depth == 0) { parts.Add(cur.ToString()); cur.Clear(); continue; }
                cur.Append(ch);
            }
            parts.Add(cur.ToString());
            return parts;
        }

        private static string StripComment(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                if (ch == '"' || ch == '\'') { quote = ch; continue; }
                if (ch == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t')) return s.Substring(0, i).TrimEnd();
            }
            return s;
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

        // A rule as a YAML scalar: plain when it is safe, single-quoted when it isn't.
        private static string Quote(string rule)
        {
            bool plain = rule.Length > 0 && rule.IndexOf(": ", StringComparison.Ordinal) < 0
                         && rule.IndexOf(" #", StringComparison.Ordinal) < 0
                         && "!&*?|>'\"%@`{[-#".IndexOf(rule[0]) < 0;
            return plain ? rule : "'" + rule.Replace("'", "''") + "'";
        }

        // What people call a list: blackmatrix7's folder/file name ("Advertising" for
        // …/Advertising/Advertising_Domain.yaml), which is also what issuers' panels show; the
        // subscription's own key when the address says nothing.
        private static string ListTitle(string key, string url)
        {
            try
            {
                if (!string.IsNullOrEmpty(url))
                {
                    var file = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
                    file = Regex.Replace(file, @"_(Domain|No_Resolve|Resolve|IP|Classical)$", "", RegexOptions.IgnoreCase);
                    if (file.Length > 0) return file;
                }
            }
            catch { }
            return key;
        }

        // The entries of one row, for the «Состав» window: what a list holds, one per line.
        // null = can't be shown (mrs is binary, or the list isn't downloaded yet).
        public static List<string> ReadEntries(AppConfig cfg, SubRow row)
        {
            if (row == null) return null;
            if (row.RelPath == null) return row.Items ?? new List<string>();
            if (row.Format == "mrs") return null;
            var full = Path.Combine(AppConfigStore.ResolveMihomoWorkDir(cfg), row.RelPath.Substring(2).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return null;

            var items = new List<string>();
            bool yaml = row.Format != "text", inPayload = !yaml;
            foreach (var raw in File.ReadLines(full))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                if (yaml)
                {
                    if (!inPayload) { inPayload = t.StartsWith("payload:"); continue; }
                    if (!t.StartsWith("-")) continue;
                    t = Unquote(StripComment(t.Substring(1)));
                }
                if (t.Length > 0) items.Add(t);
            }
            return items;
        }

        private static string SafeKey(string key)
        {
            var sb = new StringBuilder();
            foreach (var ch in key ?? "")
                sb.Append(ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') ? ch : '_');
            var s = sb.ToString().Trim('_');
            return s.Length == 0 ? "list" : s;
        }

        private static string UniqueKey(string key, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(key)) return key;
            for (int i = 2; ; i++) if (!set.Contains(key + "_" + i)) return key + "_" + i;
        }

        private static string Sha(string s)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "");
        }
    }
}
