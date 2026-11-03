using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // What happened to one connection, as the engine saw it.
    public enum EngineLogKind
    {
        Blocked,    // our rule sent it to REJECT — the kill-switch or an explicit block
        Failed,     // it was allowed, but the connection never happened (DNS gave nothing, etc.)
        Allowed,    // routed DIRECT or through a node
        Dns,        // a resolver answered with nothing for a name
    }

    public class EngineLogEntry
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public EngineLogKind Kind { get; set; }
        public string Host { get; set; } = "";      // where it was going
        public string RulePayload { get; set; } = "";  // what the engine matched on ("anthropic.com")
        public string RuleType { get; set; } = "";     // "DomainSuffix", "Process", "Match", …
        public string Target { get; set; } = "";       // REJECT / DIRECT / <proxy name>
        public string Detail { get; set; } = "";       // engine's error text, verbatim
        public string Process { get; set; } = "";      // the app that opened the connection
        public string Raw { get; set; } = "";          // the whole line, for «Копировать»

        // Filled in by the UI: which of OUR rules produced the matched entry.
        public string OwnerRule { get; set; } = "";
    }

    // Live feed of the engine's own decisions, straight from mihomo's `/logs` endpoint.
    //
    // Why this exists: from the outside, "the site doesn't open" looks identical whether the
    // kill-switch rejected it, a rule sent it down a dead node, or the DNS server we resolve
    // through refuses to answer. The engine distinguishes all three — it just normally logs at
    // `warning`, where the interesting lines never appear. So while the window is open we raise
    // the level over the API (no restart, config.yaml untouched) and put it back on close.
    //
    // Lines we parse (all verified against a live mihomo v1.19.29 over this very endpoint):
    //   info    [TCP] 127.0.0.1:52938(powershell.exe) --> host:443 match DomainSuffix(youtube.com) using REJECT
    //   warning [TCP] dial DIRECT (match Match/) 127.0.0.1:54856 --> host:443 error: dns resolve failed: couldn't find ip
    //   debug   [DNS] name --> [] A from udp://1.1.1.1:53
    //
    // NOTE the payload arrives JSON-ESCAPED — the arrow comes over the wire as `-->`, so
    // the \uXXXX form has to be decoded before any of these patterns can match. That was the
    // bug that made the window show nothing at all while the engine was rejecting away.
    public class EngineLog
    {
        private readonly AppConfig _cfg;
        private CancellationTokenSource _cts;

        public event Action<EngineLogEntry> Entry;
        public event Action<string> Status;

        public EngineLog(AppConfig cfg)
        {
            _cfg = cfg;
        }

        // Read fresh on every attempt, NOT captured once in the constructor. The engine's
        // control port isn't fixed for the life of the launcher: PortGuard moves it when
        // somebody else is squatting on 9090, updating this very config object in place.
        // A feed holding the old address kept knocking on the abandoned port forever and
        // reported «движок не отвечает» while everything else had already followed the
        // engine to its new one — the launcher connected, rules worked, only this window
        // insisted the engine was gone.
        private string ApiBase
        {
            get
            {
                var addr = string.IsNullOrWhiteSpace(_cfg?.MihomoApiAddr) ? "127.0.0.1:9090" : _cfg.MihomoApiAddr.Trim();
                return "http://" + addr;
            }
        }

        private string Secret { get { return _cfg?.MihomoSecret ?? ""; } }

        private HttpClient NewClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            var secret = Secret;
            if (!string.IsNullOrEmpty(secret))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return http;
        }

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        public async Task StartAsync()
        {
            Stop();
            _cts = new CancellationTokenSource();
            await SetLogLevelAsync("debug").ConfigureAwait(false);
            var ct = _cts.Token;
            _ = Task.Run(() => PumpAsync(ct));
            _ = Task.Run(() => KeepLogLevelAsync(ct));
        }

        // The level has to be re-asserted, not just set once. Every hot-reload makes the engine
        // re-read config.yaml, and the file says `log-level: warning` — so each connect the
        // launcher performs puts the engine back to quiet, and this feed goes silent while still
        // looking perfectly connected. Re-stating it costs one tiny local call per interval.
        private async Task KeepLogLevelAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(20000, ct).ConfigureAwait(false); }
                catch { return; }
                if (ct.IsCancellationRequested) return;
                await SetLogLevelAsync("debug").ConfigureAwait(false);
            }
        }

        public void Stop()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            // Put the engine back to its quiet level — a permanently chatty engine costs CPU
            // and disk for nothing. Fire and forget: the window is closing either way.
            _ = SetLogLevelAsync("warning");
        }

        // PATCH /configs — takes effect immediately and does NOT touch config.yaml, so the
        // level reverts on its own if we ever fail to restore it (next engine start rereads
        // the file, where it stays "warning").
        private async Task SetLogLevelAsync(string level)
        {
            try
            {
                using (var http = NewClient(TimeSpan.FromSeconds(5)))
                using (var body = new StringContent("{\"log-level\":\"" + level + "\"}", Encoding.UTF8, "application/json"))
                using (var req = new HttpRequestMessage(new HttpMethod("PATCH"), ApiBase + "/configs") { Content = body })
                    await http.SendAsync(req).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // No read timeout: this response never ends on its own.
                    using (var http = NewClient(Timeout.InfiniteTimeSpan))
                    using (var resp = await http.GetAsync(ApiBase + "/logs?level=debug",
                               HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        Status?.Invoke("");
                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream))
                        {
                            while (!ct.IsCancellationRequested)
                            {
                                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;               // engine went away
                                var entry = Parse(line);
                                if (entry != null) Entry?.Invoke(entry);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Only OUR cancellation ends the feed. A TaskCanceledException can also come
                    // out of an aborted request, and treating that as "we were told to stop" left
                    // the pump dead for the rest of the session — the window then showed the last
                    // error forever, with nothing left running to ever clear it.
                    if (ct.IsCancellationRequested) return;
                    Status?.Invoke(ex.Message);
                }

                if (ct.IsCancellationRequested) return;
                // The engine may be restarting (reconnect, don't spin).
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        // ---- parsing ----

        // {"type":"info","payload":"[TCP] 127.0.0.1:1 --> host:443 match Rule(value) using TARGET"}
        private static readonly Regex JsonLine =
            new Regex("\"type\"\\s*:\\s*\"(?<type>[^\"]*)\"\\s*,\\s*\"payload\"\\s*:\\s*\"(?<payload>.*)\"\\s*}\\s*$",
                RegexOptions.Compiled);

        // "127.0.0.1:52938(powershell.exe)" — the source, with the process name when the engine
        // could resolve it (find-process-mode: always).
        private static readonly Regex Matched =
            new Regex(@"^\[(?<net>TCP|UDP)\]\s+\S+?(\((?<proc>[^)]*)\))?\s+-->\s+(?<host>\S+)\s+match\s+(?<rule>[^\s(]+)(\((?<payload>[^)]*)\))?\s+using\s+(?<target>.+)$",
                RegexOptions.Compiled);

        // Same shape as Matched, and the source may carry "(proc.exe)" here too — worth taking
        // when it does: a failure line with no program next to it tells the user nothing.
        private static readonly Regex DialFailed =
            new Regex(@"^\[(?<net>TCP|UDP)\]\s+dial\s+(?<target>\S+)\s+\(match\s+(?<rule>[^/)]*)/(?<payload>[^)]*)\)\s+\S+?(\((?<proc>[^)]*)\))?\s+-->\s+(?<host>\S+)\s+error:\s+(?<err>.+)$",
                RegexOptions.Compiled);

        private static readonly Regex EmptyDns =
            new Regex(@"^\[DNS\]\s+(?<host>\S+)\s+-->\s+\[\]\s+(?<qtype>A|AAAA)\s+from\s+(?<server>\S+)",
                RegexOptions.Compiled);

        public static EngineLogEntry Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var payload = line;
            var m = JsonLine.Match(line);
            if (m.Success) payload = Unescape(m.Groups["payload"].Value);
            payload = payload.Trim();

            var hit = Matched.Match(payload);
            if (hit.Success)
            {
                var target = hit.Groups["target"].Value.Trim();
                return new EngineLogEntry
                {
                    Kind = target.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                           target.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)
                        ? EngineLogKind.Blocked : EngineLogKind.Allowed,
                    Host = StripPort(hit.Groups["host"].Value),
                    RuleType = hit.Groups["rule"].Value,
                    RulePayload = hit.Groups["payload"].Value,
                    Target = target,
                    Process = hit.Groups["proc"].Value,
                    Raw = payload,
                };
            }

            hit = DialFailed.Match(payload);
            if (hit.Success)
            {
                return new EngineLogEntry
                {
                    Kind = EngineLogKind.Failed,
                    Host = StripPort(hit.Groups["host"].Value),
                    RuleType = hit.Groups["rule"].Value.Trim(),
                    RulePayload = hit.Groups["payload"].Value.Trim(),
                    Target = hit.Groups["target"].Value.Trim(),
                    Process = hit.Groups["proc"].Value,
                    Detail = hit.Groups["err"].Value.Trim(),
                    Raw = payload,
                };
            }

            // An empty A answer with a non-empty AAAA one (or vice versa) is normal, so this
            // alone isn't "blocked" — it's shown as context next to the failed connection.
            hit = EmptyDns.Match(payload);
            if (hit.Success && hit.Groups["qtype"].Value == "A")
            {
                return new EngineLogEntry
                {
                    Kind = EngineLogKind.Dns,
                    Host = hit.Groups["host"].Value.TrimEnd('.'),
                    Detail = hit.Groups["server"].Value,
                    Raw = payload,
                };
            }

            return null;
        }

        private static string StripPort(string hostPort)
        {
            var s = (hostPort ?? "").Trim();
            var colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon) s = s.Substring(0, colon);   // not IPv6
            return s;
        }

        private static readonly Regex Unicode = new Regex(@"\\u(?<hex>[0-9a-fA-F]{4})", RegexOptions.Compiled);

        // The endpoint hands out real JSON, so `>` travels as > and the arrow in
        // "--> host" is not literal. Decode \uXXXX first, then the plain escapes.
        private static string Unescape(string s)
        {
            var t = (s ?? "");
            if (t.IndexOf("\\u", StringComparison.Ordinal) >= 0)
                t = Unicode.Replace(t, m => ((char)Convert.ToInt32(m.Groups["hex"].Value, 16)).ToString());
            return t.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", " ").Replace("\\t", " ");
        }

        // What kind of thing inside a rule claimed the traffic — so the log can say «шаблон
        // "YouTube"» or «программа chrome.exe» rather than just quoting the raw match.
        public enum RuleItemKind { Template, Site, Program, CheckUrl }

        // One entry of the index: which rule owns a value, through which of its items, and
        // whether that rule fails closed (kill-switch) — everything needed to explain a REJECT.
        public class RuleHit
        {
            public string RuleName = "";
            public RuleItemKind Kind;
            public string ItemLabel = "";      // template name, site URL, exe name…
            public bool KillSwitch;
            public bool RequiresVpn;
        }

        // value -> who put it into the config. Built from the live config, so a blocked host is
        // reported as «kill-switch правила "YouTube", шаблон "YouTube"» instead of a bare domain.
        public static Dictionary<string, RuleHit> BuildOwnerIndex(AppConfig cfg)
        {
            var map = new Dictionary<string, RuleHit>(StringComparer.OrdinalIgnoreCase);

            foreach (var app in (cfg?.Apps ?? new List<AppEntry>()))
            {
                if (app == null) continue;

                void Put(string key, RuleItemKind kind, string label)
                {
                    var k = (key ?? "").Trim().TrimStart('+', '*', '.');
                    if (k.Length == 0 || string.IsNullOrWhiteSpace(app.Name)) return;
                    if (map.ContainsKey(k)) return;              // first rule wins, as in the config
                    map[k] = new RuleHit
                    {
                        RuleName = app.Name,
                        Kind = kind,
                        ItemLabel = label ?? "",
                        KillSwitch = app.KillSwitch,
                        RequiresVpn = app.RequiresVpn,
                    };
                }

                foreach (var prog in app.Programs ?? new List<RuleProgram>())
                {
                    if (prog == null) continue;
                    var exe = "";
                    try { exe = string.IsNullOrEmpty(prog.Path) ? "" : Path.GetFileName(prog.Path); } catch { }
                    var label = string.IsNullOrWhiteSpace(prog.Name) ? exe : prog.Name;
                    Put(exe, RuleItemKind.Program, string.IsNullOrEmpty(exe) ? label : exe);
                }

                foreach (var site in app.Sites ?? new List<RuleSite>())
                {
                    if (site == null) continue;
                    bool isTemplate = !string.IsNullOrWhiteSpace(site.ClashRuleUrl);
                    var templateName = TemplateNameOf(site.ClashRuleUrl, app.Name);

                    Put(MihomoConfigBuilder.HostOf(site.Url), RuleItemKind.Site, site.Url);

                    // Everything the template dragged in — this is what usually matches, and
                    // what the user can't otherwise connect back to anything they typed.
                    foreach (var item in site.RuleItems ?? new List<string>())
                    {
                        var c = (item ?? "").IndexOf(',');
                        if (c <= 0 || c >= item.Length - 1) continue;
                        Put(item.Substring(c + 1),
                            isTemplate ? RuleItemKind.Template : RuleItemKind.Site,
                            isTemplate ? templateName : site.Url);
                    }
                }

                Put(MihomoConfigBuilder.HostOf(MihomoConfigBuilder.ResolveCheckUrl(app)),
                    RuleItemKind.CheckUrl, MihomoConfigBuilder.ResolveCheckUrl(app));
            }
            return map;
        }

        // A template carries no name of its own — it IS its list URL; the catalog knows whose
        // (Apple_Classical.yaml is «Apple», byfox/<name>/clash/rules.yaml is «<name>»).
        private static string TemplateNameOf(string clashUrl, string fallback)
            => BlackMatrixCatalog.NameForUrl(clashUrl, fallback);

        // Find the owner of a match. The engine reports the rule's own payload, which is exactly
        // what we wrote into the config, so an exact hit is the normal case; the suffix pass
        // covers a host that came in via a broader entry (a.b.youtube.com under youtube.com).
        public static RuleHit FindOwner(Dictionary<string, RuleHit> index, string payload, string host)
        {
            if (index == null || index.Count == 0) return null;
            RuleHit hit;
            var key = (payload ?? "").Trim().TrimStart('+', '*', '.');
            if (key.Length > 0 && index.TryGetValue(key, out hit)) return hit;

            var h = (host ?? "").Trim().TrimEnd('.');
            if (h.Length == 0) return null;
            if (index.TryGetValue(h, out hit)) return hit;

            RuleHit best = null;
            int bestLen = -1;
            foreach (var kv in index)
            {
                var k = kv.Key;
                if (k.Length <= bestLen) continue;
                if (h.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                    h.EndsWith("." + k, StringComparison.OrdinalIgnoreCase))
                {
                    best = kv.Value;
                    bestLen = k.Length;
                }
            }
            return best;
        }
    }
}
