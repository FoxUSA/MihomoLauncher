using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // "Which templates cover this domain / IP / word?" — answered by api.byfox.dev's reverse
    // index split into parts (rule/index/): 256 domain shards keyed by sha1(domain)[:2], plus
    // keyword, cidr, asn and names files, each numbering templates through index/ids.json.
    //
    // Only the parts a query needs are fetched, and each is cached in DataUser\rule-index under
    // its own sha256 from rule/manifest.json. Parts of different builds must never be mixed
    // (the numbers shift), and naming the cache by hash makes that impossible: a file is used
    // only while the manifest in hand still lists exactly that hash.
    public static class TemplateIndex
    {
        public sealed class Hit
        {
            public string Id;
            public string Why;   // the key that matched: "telegram.org", "91.108.4.0/22", "нейросети"
        }

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static Dictionary<string, string> _hashes;
        private static DateTime _hashesAt;
        private static readonly Dictionary<string, object> _parsed = new Dictionary<string, object>(StringComparer.Ordinal);

        private static string CacheDir => Path.Combine(VersionInfo.DataUserDir, "rule-index");
        private static string ManifestCache => Path.Combine(CacheDir, "manifest.json");

        // ---- queries ----

        // Templates whose lists hold the host: DOMAIN for the host itself, DOMAIN-SUFFIX for it
        // and every parent, DOMAIN-KEYWORD contained in it.
        public static async Task<List<Hit>> FindDomainAsync(string host, CancellationToken ct)
        {
            host = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            var hits = new List<Hit>();
            if (host.Length == 0) return hits;
            var ids = await IdsAsync(ct).ConfigureAwait(false);

            var parts = host.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                var key = string.Join(".", parts, i, parts.Length - i);
                var shard = await PartAsync("index/domain/" + Shard(key) + ".json.gz", ct).ConfigureAwait(false);
                if (i == 0) Collect(hits, ids, ByfoxApi.Obj(shard, "exact"), key, key);
                Collect(hits, ids, ByfoxApi.Obj(shard, "suffix"), key, key);
            }

            var kw = await PartAsync("index/keyword.json.gz", ct).ConfigureAwait(false);
            var map = ByfoxApi.Obj(kw, "keyword") ?? kw as IDictionary<string, object>;
            if (map != null)
                foreach (var k in map)
                    if (k.Key.Length > 0 && host.IndexOf(k.Key, StringComparison.Ordinal) >= 0)
                        AddNums(hits, ids, k.Value, "*" + k.Key + "*");
            return Distinct(hits);
        }

        // Templates whose IP-CIDR / IP-CIDR6 networks contain the address.
        public static async Task<List<Hit>> FindIpAsync(IPAddress ip, CancellationToken ct)
        {
            var hits = new List<Hit>();
            if (ip == null) return hits;
            var ids = await IdsAsync(ct).ConfigureAwait(false);
            var cidr = await PartAsync("index/cidr.json.gz", ct).ConfigureAwait(false);
            var key = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "cidr6" : "cidr";
            foreach (var row in ByfoxApi.Arr(cidr, key))
            {
                var pair = row as List<object>;
                if (pair == null || pair.Count < 2 || !(pair[0] is string net)) continue;
                if (RouteExplainer.CidrContains(net, ip)) AddNums(hits, ids, pair[1], net);
            }
            return Distinct(hits);
        }

        // Templates indexed under a word starting with the query word (names, tags, title words
        // and category words in RU/EN: «нейро» → нейросети → OpenAI, Claude…).
        public static async Task<List<Hit>> FindWordAsync(string word, CancellationToken ct)
        {
            word = (word ?? "").Trim().ToLowerInvariant();
            var hits = new List<Hit>();
            if (word.Length < 2) return hits;
            var ids = await IdsAsync(ct).ConfigureAwait(false);
            var names = await PartAsync("index/names.json.gz", ct).ConfigureAwait(false);
            var map = ByfoxApi.Obj(names, "names") ?? names as IDictionary<string, object>;
            if (map != null)
                foreach (var k in map)
                    if (k.Key.StartsWith(word, StringComparison.Ordinal)) AddNums(hits, ids, k.Value, k.Key);
            return Distinct(hits);
        }

        // ---- plumbing ----

        public static string Shard(string key)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return h[0].ToString("x2");
            }
        }

        private static void Collect(List<Hit> hits, List<string> ids, IDictionary<string, object> map, string key, string why)
        {
            if (map != null && map.TryGetValue(key, out var nums)) AddNums(hits, ids, nums, why);
        }

        private static void AddNums(List<Hit> hits, List<string> ids, object nums, string why)
        {
            if (!(nums is List<object> list)) return;
            foreach (var n in list)
            {
                int i;
                try { i = Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture); } catch { continue; }
                if (i >= 0 && i < ids.Count) hits.Add(new Hit { Id = ids[i], Why = why });
            }
        }

        private static List<Hit> Distinct(List<Hit> hits)
            => hits.GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        private static async Task<List<string>> IdsAsync(CancellationToken ct)
        {
            var root = await PartAsync("index/ids.json", ct).ConfigureAwait(false);
            return ByfoxApi.Arr(root, "ids").Cast<object>().Select(o => o as string ?? "").ToList();
        }

        // rule/manifest.json's path → sha256, refreshed once a day; offline, the cached copy.
        private static async Task<Dictionary<string, string>> HashesAsync(CancellationToken ct)
        {
            if (_hashes != null && (DateTime.UtcNow - _hashesAt).TotalHours < 24) return _hashes;
            try
            {
                var text = await ByfoxApi.GetStringAsync(ByfoxApi.RuleManifestUrl, 30, ct).ConfigureAwait(false);
                var fresh = ByfoxApi.FileHashes(ByfoxApi.ParseJson(text));
                if (fresh.Count > 0)
                {
                    bool changed = _hashes == null || !_hashes.OrderBy(k => k.Key).SequenceEqual(fresh.OrderBy(k => k.Key));
                    _hashes = fresh;
                    _hashesAt = DateTime.UtcNow;
                    if (changed) _parsed.Clear();
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(ManifestCache, text, Encoding.UTF8);
                        Prune(fresh);
                    }
                    catch { }
                    return _hashes;
                }
            }
            catch { }
            if (_hashes == null && File.Exists(ManifestCache))
            {
                try { _hashes = ByfoxApi.FileHashes(ByfoxApi.ParseJson(File.ReadAllText(ManifestCache, Encoding.UTF8))); }
                catch { }
            }
            if (_hashes == null || _hashes.Count == 0) throw new IOException(Loc.T("Tpl_IndexOffline"));
            return _hashes;
        }

        // One index part, parsed: from memory, else the hash-named cache file, else downloaded
        // and checked against its hash before it is kept.
        private static async Task<object> PartAsync(string path, CancellationToken ct)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var hashes = await HashesAsync(ct).ConfigureAwait(false);
                if (!hashes.TryGetValue(path, out var sha) || sha.Length == 0)
                    throw new IOException("rule/manifest.json: нет " + path);
                if (_parsed.TryGetValue(path, out var hit)) return hit;

                var file = Path.Combine(CacheDir, sha);
                byte[] raw = null;
                try { if (File.Exists(file)) raw = File.ReadAllBytes(file); } catch { }
                if (raw == null || !string.Equals(ByfoxApi.Sha256(raw), sha, StringComparison.OrdinalIgnoreCase))
                {
                    raw = await ByfoxApi.GetBytesAsync(ByfoxApi.RuleBase + path, 60, ct).ConfigureAwait(false);
                    if (!string.Equals(ByfoxApi.Sha256(raw), sha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(path + ": sha256 не совпал с rule/manifest.json");
                    try { Directory.CreateDirectory(CacheDir); File.WriteAllBytes(file, raw); } catch { }
                }

                var bytes = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? ByfoxApi.Gunzip(raw) : raw;
                var parsed = ByfoxApi.ParseJson(Encoding.UTF8.GetString(bytes));
                _parsed[path] = parsed;
                return parsed;
            }
            finally { Gate.Release(); }
        }

        // Cached parts the current manifest no longer lists are of an older build — drop them.
        private static void Prune(Dictionary<string, string> hashes)
        {
            var keep = new HashSet<string>(hashes.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(CacheDir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || keep.Contains(name)) continue;
                try { File.Delete(f); } catch { }
            }
        }
    }
}
