using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // Talks to a running mihomo instance via its external-controller RESTful API,
    // and owns the config.yaml the launcher rewrites. mihomo itself is started
    // elevated by a Scheduled Task (see MihomoSetup); this class never needs admin.
    public class MihomoController
    {
        private readonly string _apiAddr;
        private readonly int _mixedPort;
        private readonly string _workDir;
        private readonly AppConfig _cfg;

        // Several connectivity-check targets: a node counts as up if ANY of them is
        // reachable through it. A single blocked/unrouted domain (some exits block
        // gstatic) must not make a working node look dead. Cloudflare goes first —
        // plain HTTP, no TLS round-trip, and the least often blocked of the four.
        private static readonly string[] NodeProbeUrls =
        {
            "http://cp.cloudflare.com/generate_204",
            "https://www.gstatic.com/generate_204",
            "https://www.google.com/generate_204",
            "http://www.msftconnecttest.com/connecttest.txt",
        };

        // How long the ENGINE is given to carry one probe through the node.
        //
        // This is a liveness test, not a speed test, and the two want opposite timeouts. A
        // genuinely dead node fails fast — connection refused, TLS reject, no such host —
        // so a generous limit costs nothing on it; the only thing extra seconds buy is a
        // slow-but-working node, and calling that one dead is the expensive mistake. The
        // old 3 s was well below what any Clash dashboard uses for the same button (5-10 s).
        private const int NodeProbeTimeoutMs = 15000;

        static MihomoController()
        {
            // Every probe URL for a node is fired at once, and every node at once — with the
            // framework default of 2 connections per endpoint they would queue up against
            // 127.0.0.1:9090 and the parallelism would be a fiction.
            try
            {
                if (ServicePointManager.DefaultConnectionLimit < 64)
                    ServicePointManager.DefaultConnectionLimit = 64;
            }
            catch { }
        }

        public MihomoController(AppConfig cfg)
        {
            _apiAddr = string.IsNullOrWhiteSpace(cfg.MihomoApiAddr) ? "127.0.0.1:9090" : cfg.MihomoApiAddr.Trim();
            _mixedPort = cfg.MixedPort > 0 ? cfg.MixedPort : 7890;
            _workDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
            _cfg = cfg;
        }

        public string WorkDir => _workDir;
        public string ConfigPath => Path.Combine(_workDir, "config.yaml");

        // Proxy pointing at mihomo's mixed-port — used to probe sites "through the VPN".
        public IWebProxy MixedPortProxy => new WebProxy("http://127.0.0.1:" + _mixedPort);

        private string ApiBase => "http://" + _apiAddr;

        private HttpClient NewClient(int timeoutSeconds)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            // The secret is read live, not copied in the constructor: first-run setup creates
            // the controller before EnsureSecret generates the key, and a copied (empty) key
            // made the brand-new engine answer 401. The launcher then took its own engine for
            // a stale one, killed it, moved the port and ended up with two tunnels (Gluk,
            // 2026-11-03) — only «Переустановить движок», which re-creates the controller, fixed it.
            var secret = _cfg?.MihomoSecret ?? "";
            if (secret.Length > 0)
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            return http;
        }

        // How the external controller answered: nothing at all, or "you're not who I expect".
        public enum ApiState
        {
            Down,           // no answer — mihomo isn't running (or not listening yet)
            Unauthorized,   // it IS running, but with a different secret than our config has
            Up,
        }

        // GET /version — is the API (and therefore mihomo) up?
        public async Task<bool> IsApiReachableAsync(CancellationToken ct = default)
            => await ProbeApiAsync(ct).ConfigureAwait(false) == ApiState.Up;

        // Same probe, but keeping the difference between "down" and "wrong secret". An engine
        // left over from an older config.yaml answers 401 to every call, so the launcher can
        // neither reload nor route — and from the outside it looks exactly like "the engine
        // won't start". The caller recovers by rewriting config.yaml and restarting the task.
        public async Task<ApiState> ProbeApiAsync(CancellationToken ct = default)
        {
            try
            {
                using (var http = NewClient(3))
                using (var resp = await http.GetAsync(ApiBase + "/version", ct).ConfigureAwait(false))
                {
                    if (resp.IsSuccessStatusCode) return ApiState.Up;
                    var code = (int)resp.StatusCode;
                    return (code == 401 || code == 403) ? ApiState.Unauthorized : ApiState.Down;
                }
            }
            catch { return ApiState.Down; }
        }

        // GET /version — returns the mihomo version string, or "" if unreachable.
        public async Task<string> GetVersionAsync(CancellationToken ct = default)
        {
            try
            {
                using (var http = NewClient(3))
                using (var resp = await http.GetAsync(ApiBase + "/version", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return "";
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var m = System.Text.RegularExpressions.Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value : "?";
                }
            }
            catch { return ""; }
        }

        // GET /dns/query — the engine's main resolver, i.e. what it uses itself to check a name
        // against address lists (real addresses, never fake-ip). Empty on any failure.
        public async Task<List<System.Net.IPAddress>> ResolveAsync(string name, CancellationToken ct = default)
        {
            var list = new List<System.Net.IPAddress>();
            try
            {
                using (var http = NewClient(5))
                using (var resp = await http.GetAsync(ApiBase + "/dns/query?name=" + Uri.EscapeDataString(name) + "&type=A", ct)
                                            .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return list;
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                 json, "\"data\"\\s*:\\s*\"([^\"]+)\"[^}]*?\"type\"\\s*:\\s*1\\b"))
                        if (System.Net.IPAddress.TryParse(m.Groups[1].Value, out var ip)) list.Add(ip);
                }
            }
            catch { }
            return list;
        }

        // Wait until the API responds, up to timeoutSeconds.
        public async Task<bool> WaitApiReachableAsync(int timeoutSeconds, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return false;
                if (await IsApiReachableAsync(ct).ConfigureAwait(false)) return true;
                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        // Write config.yaml into the work dir (atomically). Returns false when the file
        // already held exactly this text.
        //
        // The return value is what lets a caller skip the reload, and skipping matters: under
        // TUN with auto-route the engine carries the WHOLE machine, so every reload is a few
        // seconds with no internet for every program — arming the kill-switch over a config
        // the engine literally just loaded at startup bought nothing and cost an outage at
        // every logon. Byte-identical text means the running engine is already on it: the
        // launcher is the only writer, and the engine reads this same file when it starts.
        // The one rule this method keeps: config.yaml on disk is ALWAYS a file the engine takes.
        //
        // It used to be written first and judged later — by the reload (which, on refusal, left
        // the refused file behind) or by the next start (which simply died on it). One bad value
        // from anywhere — an address, a rule, a proxy — and the engine could not start at all:
        // "движок не отвечает", reinstall, same binary, same file, same death, for ever
        // (prof.urfo, 2026-09-30). Now the engine is asked BEFORE the file is replaced; a
        // refused config is logged with the engine's own reason and never reaches the disk.
        public bool WriteConfig(string yaml)
        {
            Directory.CreateDirectory(_workDir);
            var path = ConfigPath;
            try
            {
                if (File.Exists(path) &&
                    string.Equals(File.ReadAllText(path), yaml, StringComparison.Ordinal))
                    return false;
            }
            catch { }   // unreadable → fall through and rewrite, never skip on doubt
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, yaml, new UTF8Encoding(false));

            string why;
            if (MihomoBinary.TestConfig(_cfg?.MihomoExePath, tmp, TestDir, out why) == false)
            {
                try { File.Delete(tmp); } catch { }
                NoteRefusal("Конфиг не применён: движок его не примет — " + why
                            + ". Остаётся прежний, рабочий.");
                EnsureStartableConfig();
                return false;
            }

            string previous = null;
            try { if (File.Exists(path)) previous = File.ReadAllText(path); } catch { }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            lock (PrevLock) PreviousByPath[path] = previous;
            return true;
        }

        private string TestDir => Path.Combine(_workDir, "cfgtest");

        // What config.yaml held before the last write, per file, until the engine accepts the
        // new one. A reload it refuses puts this back — see ReloadAsync.
        private static readonly object PrevLock = new object();
        private static readonly Dictionary<string, string> PreviousByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A config that can't be written will be offered again on every rebuild; say so once
        // per distinct reason, not once per attempt.
        private static string _lastRefusal;
        private static void NoteRefusal(string line)
        {
            if (line == _lastRefusal) return;
            _lastRefusal = line;
            Log.Info(line);
        }

        // The file left on disk must at least START the engine. Normally it does (it passed this
        // same check when it was written). It may not if it predates the check — an older
        // version, a hand edit. Then the minimum is written: no proxies, no rules, only the
        // tunnel and DNS every build shares. The engine runs; the firewall kill-switch keeps
        // guarding the rule programs meanwhile; the log says why.
        private void EnsureStartableConfig()
        {
            try
            {
                var path = ConfigPath;
                string why;
                if (File.Exists(path) &&
                    MihomoBinary.TestConfig(_cfg?.MihomoExePath, path, TestDir, out why) != false) return;

                var minimal = MihomoConfigBuilder.Build(_cfg, new List<ParsedProxy>(), new List<RuleDef>());
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, minimal, new UTF8Encoding(false));
                if (MihomoBinary.TestConfig(_cfg?.MihomoExePath, tmp, TestDir, out why) == false)
                {
                    try { File.Delete(tmp); } catch { }
                    Log.Info("Даже минимальный конфиг движок не принимает — " + why + ".");
                    return;
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                Log.Info("Прежний config.yaml движок тоже не принял бы — записан минимальный "
                         + "(без правил), чтобы движок хотя бы запускался.");
            }
            catch (Exception ex) { Log.Error("config-minimal", ex); }
        }

        // Put back the config the engine is actually running, after it refused a new one.
        private void RestorePreviousConfig()
        {
            try
            {
                var path = ConfigPath;
                string prev;
                lock (PrevLock)
                {
                    if (!PreviousByPath.TryGetValue(path, out prev)) return;
                    PreviousByPath.Remove(path);
                }
                if (prev == null) return;
                File.WriteAllText(path, prev, new UTF8Encoding(false));
                Log.Info("Вернул на диск конфиг, который движок принял последним — иначе следующий "
                         + "запуск движка упал бы на отвергнутом.");
            }
            catch (Exception ex) { Log.Error("config-restore", ex); }
        }

        // Will the engine start on config.yaml as it is on disk? null = couldn't ask.
        public bool? ConfigOnDiskAccepted(out string why)
            => MihomoBinary.TestConfig(_cfg?.MihomoExePath, ConfigPath, TestDir, out why);

        // Diagnostic for the "engine won't start" line: does the file on disk pass?
        public string DescribeConfigVerdict()
        {
            string why;
            var v = MihomoBinary.TestConfig(_cfg?.MihomoExePath, ConfigPath, TestDir, out why);
            return v == true ? "движок его принимает"
                 : v == false ? "движок его НЕ принимает: " + why
                 : "проверить не удалось";
        }

        // GET /configs — is TUN currently up? null when the engine didn't answer at all.
        //
        // "The API answers" is NOT the same as "traffic flows": TUN is what carries every
        // rule, the kill-switch and (unless DNS is left to the system) the machine's own
        // resolver. An engine running with TUN down answers /version cheerfully while
        // nothing whatsoever goes through it — the exact state a failed shutdown can leave
        // behind (see EngineShutdown), and the one the watchdog has to catch.
        public async Task<bool?> IsTunEnabledAsync(CancellationToken ct = default)
        {
            try
            {
                using (var http = NewClient(3))
                using (var resp = await http.GetAsync(ApiBase + "/configs", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // {"tun":{"enable":true,"device":"Mihomo",...},...}
                    var m = System.Text.RegularExpressions.Regex.Match(
                        json, "\"tun\"\\s*:\\s*\\{[^}]*?\"enable\"\\s*:\\s*(true|false)");
                    if (!m.Success) return null;
                    return m.Groups[1].Value == "true";
                }
            }
            catch { return null; }
        }

        // GET /configs — the name of the TUN adapter THIS engine raised ("Meta", "Meta 2", …).
        // Needed to tell our tunnel from a second engine's: both put 198.18.0.1 on their
        // interface, so the address alone can identify the wrong one. "" when unknown.
        public async Task<string> GetTunDeviceAsync(CancellationToken ct = default)
        {
            try
            {
                using (var http = NewClient(3))
                using (var resp = await http.GetAsync(ApiBase + "/configs", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return "";
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var m = System.Text.RegularExpressions.Regex.Match(
                        json, "\"tun\"\\s*:\\s*\\{[^}]*?\"device\"\\s*:\\s*\"([^\"]*)\"");
                    return m.Success ? m.Groups[1].Value : "";
                }
            }
            catch { return ""; }
        }

        // PUT /configs?force=true {"path": "<abs config.yaml>"} — hot-reload.
        //
        // ALWAYS logged, and this is not decoration. Under TUN with auto-route the engine
        // carries the WHOLE machine's traffic — a process with no rule of ours still goes
        // through it as MATCH,DIRECT — so a reload is a few seconds with no internet for
        // Chrome and everything else, not just for the rules. Every report of "the launcher
        // killed my internet" has to be matchable against a line in app.log; until this was
        // added the call was `_ = ReloadAsync()` inside a `catch {}`, so neither the reload
        // nor its failure left any trace and every outage looked like it came from nowhere.
        // `reason` says who asked, because the frequency is the actual problem.
        public async Task<bool> ReloadAsync(CancellationToken ct = default, string reason = null)
        {
            var why = string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")";
            try
            {
                var jsonPath = ConfigPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var body = "{\"path\":\"" + jsonPath + "\"}";
                using (var http = NewClient(10))
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var resp = await http.PutAsync(ApiBase + "/configs?force=true", content, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        // The engine says WHY in the body ({"message":"proxy Turkey is the
                        // duplicate name"}) and without it the log only ever said "HTTP 400",
                        // which names no cause and points at no config line. Quoting it turns a
                        // silent failed connect into one readable sentence.
                        string detail = "";
                        try
                        {
                            var reply = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            // A JSON string, escapes and all: the engine quotes the offending
                            // value (netip.ParsePrefix(\"…\")), and the old [^"]* stopped at that
                            // first \" — the log read «netip.ParsePrefix(\» and never named it.
                            var m = System.Text.RegularExpressions.Regex.Match(
                                reply ?? "", @"""message""\s*:\s*""((?:\\.|[^""\\])*)""");
                            var text = m.Success
                                ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
                                : (reply ?? "").Trim();
                            if (text.Length > 300) text = text.Substring(0, 300) + "…";
                            if (!string.IsNullOrEmpty(text)) detail = " — " + text;
                        }
                        catch { }
                        Log.Info("Движок: перезагрузка конфига отклонена" + why
                                 + " — HTTP " + (int)resp.StatusCode + detail
                                 + ", продолжает работать СТАРЫЙ конфиг.");
                        RestorePreviousConfig();
                        return false;   // 204 No Content on success
                    }
                    lock (PrevLock) PreviousByPath.Remove(ConfigPath);   // accepted: nothing to undo
                    Log.Info("Движок: конфиг перезагружен" + why
                             + " — трафик всей машины прерывается на время перезагрузки.");
                    LogRouting();
                    // A reload re-reads everything, routing included, so whatever Windows cached
                    // a moment ago was decided under rules that no longer apply — and under
                    // fake-ip that cache is full of 198.18.x.x addresses whose meaning is set by
                    // the engine. `store-fake-ip` keeps those addresses meaning the same thing
                    // across the reload; this drops the answers whose ROUTE just changed. The two
                    // together are what stop "the site stopped opening until I restarted it".
                    // Local call, microseconds, can't fail in a way worth reporting.
                    EngineShutdown.FlushDns();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("Движок: перезагрузка конфига не удалась" + why + " — " + ex.Message
                         + ", продолжает работать СТАРЫЙ конфиг.");
                return false;
            }
        }

        // Routing line → when it was last written. Static: the window recreates the controller on
        // every settings change. A connect alternates trial and final configs, and the auto-retry
        // repeats that every 90 s, so the same line is not repeated for ten minutes.
        private static readonly Dictionary<string, DateTime> _routingLoggedAt = new Dictionary<string, DateTime>();

        private void LogRouting()
        {
            try
            {
                if (_cfg == null) return;
                var text = MihomoConfigBuilder.DescribeRouting(_cfg, File.ReadAllText(ConfigPath));
                if (string.IsNullOrEmpty(text)) return;
                lock (_routingLoggedAt)
                {
                    DateTime at;
                    if (_routingLoggedAt.TryGetValue(text, out at) && DateTime.Now - at < TimeSpan.FromMinutes(10)) return;
                    if (_routingLoggedAt.Count > 64) _routingLoggedAt.Clear();
                    _routingLoggedAt[text] = DateTime.Now;
                }
                Log.Info(text);
            }
            catch { }
        }

        // Cut the connections that are ALREADY open for these hosts / these programs.
        //
        // Why this has to exist: a rule change only decides where the NEXT connection goes.
        // Everything already established keeps flowing — and a browser keeps its sockets alive
        // for minutes (keep-alive, HTTP/2 multiplexing, QUIC), so after switching a rule to
        // REJECT the site went on loading through the pipe it opened earlier. From the outside
        // that is indistinguishable from "the kill-switch doesn't work": the user presses ✕, the
        // config says REJECT, and the page still opens.
        //
        // mihomo carries every connection under TUN, so it can close them: GET /connections
        // lists them with their host and owning process, DELETE /connections/{id} drops one.
        // Matching is on the host suffix (so api.ru.dog also takes www.api.ru.dog) and on the
        // process image name, which together are exactly what a rule is made of.
        //
        // Best-effort by design: an engine that is down has no connections to close anyway.
        // Returns how many were dropped.
        public async Task<int> CloseConnectionsAsync(IEnumerable<string> hostSuffixes,
                                                     IEnumerable<string> processNames,
                                                     IEnumerable<string> ipCidrs = null,
                                                     bool catchAll = false,
                                                     CancellationToken ct = default)
        {
            // catchAll: also close whatever the final MATCH carried — «Весь трафик» names no
            // host or program, its connections are recognised by the rule that routed them.
            var hosts = (hostSuffixes ?? Enumerable.Empty<string>())
                .Select(h => (h ?? "").Trim().TrimStart('.').ToLowerInvariant())
                .Where(h => h.Length > 0).ToList();
            var procs = new HashSet<string>(
                (processNames ?? Enumerable.Empty<string>())
                    .Select(p => (p ?? "").Trim()).Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            // A template is not only domains: blackmatrix7 lists carry IP-CIDR entries too, and a
            // connection routed by one of those has no name to match on. Without this, closing a
            // template-backed rule left exactly those connections alive.
            var nets = (ipCidrs ?? Enumerable.Empty<string>())
                .Select(ParseCidr).Where(n => n != null).ToList();
            if (hosts.Count == 0 && procs.Count == 0 && nets.Count == 0 && !catchAll) return 0;

            try
            {
                string json;
                using (var http = NewClient(6))
                using (var resp = await http.GetAsync(ApiBase + "/connections", ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return 0;
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }

                int closed = 0;
                foreach (var conn in SplitConnections(json))
                {
                    var id = JsonField(conn, "id");
                    if (id.Length == 0) continue;

                    // Both are worth checking: `host` is what the rules matched on, `sniffHost`
                    // is what the sniffer recovered when something else did the resolving.
                    var host = JsonField(conn, "host").ToLowerInvariant();
                    var sniff = JsonField(conn, "sniffHost").ToLowerInvariant();
                    var proc = ImageNameOf(JsonField(conn, "process"));

                    bool hit = procs.Contains(proc)
                            || hosts.Any(h => HostMatches(host, h) || HostMatches(sniff, h));
                    if (!hit && nets.Count > 0)
                    {
                        var dest = JsonField(conn, "destinationIP");
                        hit = dest.Length > 0 && nets.Any(n => n.Contains(dest));
                    }
                    if (!hit && catchAll)
                        hit = JsonField(conn, "rule").Equals("Match", StringComparison.OrdinalIgnoreCase);
                    if (!hit) continue;

                    using (var http = NewClient(6))
                    using (var del = await http.DeleteAsync(
                               ApiBase + "/connections/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false))
                        if (del.IsSuccessStatusCode) closed++;
                }
                if (closed > 0) Log.Info("Разорвано уже открытых соединений: " + closed + ".");
                return closed;
            }
            catch (Exception ex) { try { Log.Error("close-connections", ex); } catch { } return 0; }
        }

        // One IP-CIDR from a template, kept as first/last so a destination is one comparison.
        private sealed class IpNet
        {
            public System.Net.Sockets.AddressFamily Family;
            public System.Numerics.BigInteger Lo, Hi;

            public bool Contains(string address)
            {
                IPAddress ip;
                if (!IPAddress.TryParse((address ?? "").Trim(), out ip)) return false;
                if (ip.AddressFamily != Family) return false;
                var v = ToNumber(ip);
                return v >= Lo && v <= Hi;
            }
        }

        private static System.Numerics.BigInteger ToNumber(IPAddress ip)
        {
            System.Numerics.BigInteger v = 0;
            foreach (var b in ip.GetAddressBytes()) v = (v << 8) | b;
            return v;
        }

        private static IpNet ParseCidr(string cidr)
        {
            var item = (cidr ?? "").Trim();
            if (item.Length == 0) return null;
            int slash = item.IndexOf('/');
            IPAddress ip;
            if (!IPAddress.TryParse(slash > 0 ? item.Substring(0, slash) : item, out ip)) return null;

            int bits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            int prefix = bits;
            if (slash > 0 && !int.TryParse(item.Substring(slash + 1), out prefix)) return null;
            if (prefix < 0 || prefix > bits) return null;

            var all = (System.Numerics.BigInteger.One << bits) - 1;
            var mask = prefix == 0 ? System.Numerics.BigInteger.Zero
                                   : ((System.Numerics.BigInteger.One << prefix) - 1) << (bits - prefix);
            var lo = ToNumber(ip) & mask;
            return new IpNet { Family = ip.AddressFamily, Lo = lo, Hi = lo | (all ^ mask) };
        }

        private static bool HostMatches(string host, string suffix)
        {
            if (host.Length == 0) return false;
            return host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);
        }

        // "C:\...\chrome.exe" or "chrome.exe" → "chrome.exe" (mihomo reports either form).
        private static string ImageNameOf(string process)
        {
            var p = (process ?? "").Trim();
            if (p.Length == 0) return "";
            try { return System.IO.Path.GetFileName(p); } catch { return p; }
        }

        // The connections payload is {"connections":[{...},{...}]}. Split it into the objects
        // without a JSON library — same hand-rolled approach as the rest of this class, and the
        // only structure that matters here is brace depth inside the array.
        private static IEnumerable<string> SplitConnections(string json)
        {
            var key = "\"connections\"";
            int i = (json ?? "").IndexOf(key, StringComparison.Ordinal);
            if (i < 0) yield break;
            i = json.IndexOf('[', i);
            if (i < 0) yield break;

            int depth = 0, start = -1;
            bool inStr = false, esc = false;
            for (int p = i + 1; p < json.Length; p++)
            {
                char ch = json[p];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '{') { if (depth++ == 0) start = p; }
                else if (ch == '}') { if (--depth == 0 && start >= 0) yield return json.Substring(start, p - start + 1); }
                else if (ch == ']' && depth == 0) yield break;
            }
        }

        // Value of a string field anywhere in one connection object (they're flat enough that
        // the first match is the right one; "process" only ever appears inside metadata).
        private static string JsonField(string obj, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                obj, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\/", "/");
        }

        // PATCH /configs {"tun":{"enable":<on>}} — raise or drop the TUN interface on a
        // RUNNING engine, the same call every Clash GUI makes for its TUN switch.
        //
        // Used on shutdown (off), so the engine removes its own routes and stops hijacking
        // :53 in an orderly way instead of leaving Windows to clean up after a killed
        // process — that cleanup is what the user sees as "the internet dropped for a few
        // seconds". And used to put it back (on) if the kill then fails: an engine that is
        // alive with TUN down resolves nothing and holds no kill-switch, which is strictly
        // worse than the outage we were trying to avoid. Best-effort — false means the
        // engine didn't take the call (down, or a different secret).
        public async Task<bool> SetTunAsync(bool on, int timeoutSeconds = 3, CancellationToken ct = default)
        {
            try
            {
                using (var http = NewClient(timeoutSeconds))
                using (var req = new HttpRequestMessage(new HttpMethod("PATCH"), ApiBase + "/configs"))
                {
                    req.Content = new StringContent(
                        "{\"tun\":{\"enable\":" + (on ? "true" : "false") + "}}", Encoding.UTF8, "application/json");
                    using (var resp = await http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            Log.Info("Движок: не принял команду " + (on ? "поднять" : "опустить")
                                     + " TUN — HTTP " + (int)resp.StatusCode + ".");
                        return resp.IsSuccessStatusCode; // 204 No Content on success
                    }
                }
            }
            catch (Exception ex)
            {
                // Every caller ignores the return value, so without this line a tunnel that
                // refused to come back up leaves no trace at all — and a machine whose TUN is
                // down resolves nothing and holds no kill-switch.
                Log.Info("Движок: команда " + (on ? "поднять" : "опустить") + " TUN не прошла — "
                         + ex.Message);
                return false;
            }
        }

        // PUT /providers/proxies/{name} — force mihomo to re-download this proxy-provider
        // (Clash subscription) right now, instead of waiting for its interval. Returns true
        // on success; false if mihomo is down or the provider isn't currently loaded (404).
        public async Task<bool> RefreshProviderAsync(string providerName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(providerName)) return false;
            try
            {
                using (var http = NewClient(30))
                using (var resp = await http.PutAsync(
                    ApiBase + "/providers/proxies/" + Uri.EscapeDataString(providerName), null, ct).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode; // 204 No Content on success
            }
            catch { return false; }
        }

        // GET /proxies/{name}/delay — is this node/group actually reachable?
        //
        // All probe URLs are fired AT ONCE and the first success wins. In turn they can't be:
        // four targets × the timeout above would take a minute on a dead node and overrun the
        // caller's own deadline, which is exactly how a long (correct) timeout used to be
        // unaffordable. In parallel one round costs one timeout no matter how many targets.
        //
        // Connectivity only — does NOT inspect the page body; site-content checks go through
        // SiteChecker via MixedPortProxy. The latency the engine reports is deliberately
        // thrown away: the answer is "did the node answer", never "how fast".
        public async Task<bool> IsNodeUpAsync(string proxyName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(proxyName)) return false;

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var pending = NodeProbeUrls.Select(u => ProbeNodeOnceAsync(proxyName, u, linked.Token)).ToList();
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);

                    bool ok;
                    try { ok = await done.ConfigureAwait(false); }
                    catch { ok = false; }

                    if (ok)
                    {
                        try { linked.Cancel(); } catch { }   // the losing probes are of no interest
                        return true;                          // this target works -> node is up
                    }
                    if (ct.IsCancellationRequested) return false;
                }
            }
            return false; // none of the targets were reachable through the node
        }

        private async Task<bool> ProbeNodeOnceAsync(string proxyName, string probeUrl, CancellationToken ct)
        {
            try
            {
                var url = ApiBase + "/proxies/" + Uri.EscapeDataString(proxyName)
                          + "/delay?timeout=" + NodeProbeTimeoutMs + "&url=" + Uri.EscapeDataString(probeUrl);
                // The engine answers on its own once its timeout expires; ours only has to
                // outlast it, never to cut it short — a client-side cut looks identical to a
                // dead node and would put the 3-second problem straight back.
                using (var http = NewClient(NodeProbeTimeoutMs / 1000 + 5))
                using (var resp = await http.GetAsync(url, ct).ConfigureAwait(false))
                    return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // The "test all" of any Clash dashboard: same check, whole list of nodes, all at
        // once — wall clock is one probe rather than one per node. Returns name -> answered;
        // names the engine doesn't know (not in the loaded config) simply come back false.
        public async Task<Dictionary<string, bool>> CheckNodesAsync(
            IEnumerable<string> proxyNames, CancellationToken ct = default)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (proxyNames == null) return result;

            var names = proxyNames.Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 0) return result;

            var tasks = names.Select(n => IsNodeUpAsync(n, ct)).ToList();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* a cancelled/faulted probe just counts as "didn't answer" below */ }

            for (int i = 0; i < names.Count; i++)
                result[names[i]] = tasks[i].Status == TaskStatus.RanToCompletion && tasks[i].Result;
            return result;
        }
    }
}
