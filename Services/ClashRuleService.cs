using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Fetches and parses a blackmatrix7 Clash rule list (.yaml) into mihomo-native rule
    // fragments WITHOUT a target, e.g. "DOMAIN-SUFFIX,instagram.com", "IP-CIDR,1.2.3.0/24".
    // The connect flow appends the proxy / REJECT target (and "no-resolve" for IP rules).
    //
    // Port of awg-panel's _parse_clash_payload: no YAML dependency — we scan the
    // `payload:` block line by line for "- ITEM" entries.
    public static class ClashRuleService
    {
        // Re-fetch the rule list at most once a week; cached in the config the rest of the time.
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        static ClashRuleService()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
        }

        // Download <url> and parse it into rule fragments. Throws on network/HTTP errors
        // so callers can surface the reason; returns an empty list for an empty payload.
        public static async Task<List<string>> FetchRuleItemsAsync(string url, int timeoutSeconds = 20, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException(Loc.T("Svc_EmptyRuleListAddress"));

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
            {
                // Our own API counts its consumers by the app's name; elsewhere look like a browser.
                http.DefaultRequestHeaders.UserAgent.ParseAdd(ByfoxApi.IsOurs(url) ? ByfoxApi.UserAgent : BrowserUserAgent);
                using (var resp = await http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParsePayload(text);
                }
            }
        }

        // Fetch + write the parsed items and the timestamp back into the site. Returns
        // the parsed count. Throws on failure (leaves the old cache untouched).
        public static async Task<int> RefreshAsync(RuleSite site, int timeoutSeconds = 20, CancellationToken ct = default)
        {
            if (site == null) return 0;
            var items = await FetchRuleItemsAsync(site.ClashRuleUrl, timeoutSeconds, ct).ConfigureAwait(false);
            site.RuleItems = items;
            site.RuleFetchedUtc = DateTime.UtcNow.ToString("o");
            return items.Count;
        }

        // True if this site has a rule URL whose cache is missing, older than a week, or older
        // than the day the catalog says the template last changed.
        public static bool ShouldRefresh(RuleSite site)
        {
            if (site == null || string.IsNullOrWhiteSpace(site.ClashRuleUrl)) return false;
            if (site.RuleItems == null || site.RuleItems.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(site.RuleFetchedUtc)) return true;
            if (!DateTime.TryParse(site.RuleFetchedUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var when)) return true;
            var updated = BlackMatrixCatalog.ParseDate(BlackMatrixCatalog.ForUrl(site.ClashRuleUrl)?.Updated);
            if (updated != null && when.ToUniversalTime().Date < updated.Value) return true;
            return DateTime.UtcNow - when.ToUniversalTime() >= RefreshInterval;
        }

        private static readonly Regex PayloadHeader = new Regex(@"^payload\s*:\s*$", RegexOptions.Compiled);
        private static readonly Regex CidrLike = new Regex(@"^[0-9a-fA-F:.]+/\d+$", RegexOptions.Compiled);
        private static readonly Regex Ipv4Like = new Regex(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.Compiled);

        // Parse a Clash yaml rule file into "TYPE,VALUE" fragments (no target/options).
        public static List<string> ParsePayload(string text)
        {
            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return items;

            bool inPayload = false;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                var stripped = line.Trim();

                if (!inPayload)
                {
                    if (PayloadHeader.IsMatch(stripped)) inPayload = true;
                    continue;
                }

                // End of payload: a non-empty, non-list line at column 0.
                if (line.Length > 0 && !line.StartsWith(" ") && !line.StartsWith("\t") &&
                    !line.StartsWith("-") && stripped != "..." )
                {
                    inPayload = false;
                    continue;
                }
                if (stripped.Length == 0 || stripped.StartsWith("#")) continue;
                if (!stripped.StartsWith("-")) continue;

                var item = stripped.TrimStart('-').Trim().Trim('\'', '"').Trim();
                if (item.Length == 0) continue;

                var frag = Normalize(item);
                if (frag != null && seen.Add(frag)) items.Add(frag);
            }
            return items;
        }

        // Turn one payload entry into a canonical "TYPE,VALUE" fragment, or null to skip.
        private static string Normalize(string item)
        {
            var upper = item.ToUpperInvariant();
            string TypeValue(string type)
            {
                var rest = item.Substring(item.IndexOf(',') + 1).Trim();
                // Drop any trailing options (",no-resolve", policy, etc.).
                var comma = rest.IndexOf(',');
                if (comma >= 0) rest = rest.Substring(0, comma).Trim();
                rest = rest.Trim('\'', '"');
                return rest.Length == 0 ? null : type + "," + rest;
            }

            if (upper.StartsWith("DOMAIN-SUFFIX,")) return TypeValue("DOMAIN-SUFFIX");
            if (upper.StartsWith("DOMAIN-KEYWORD,")) return TypeValue("DOMAIN-KEYWORD");
            if (upper.StartsWith("DOMAIN,")) return TypeValue("DOMAIN");
            if (upper.StartsWith("IP-CIDR6,")) return TypeValue("IP-CIDR6");
            if (upper.StartsWith("IP-CIDR,")) return TypeValue("IP-CIDR");
            // Kept since 2026-11-03 (Telegram has 5): the config builder writes them only once
            // the engine has its ASN.mmdb (UpdateService.EnsureAsnDbAsync).
            if (upper.StartsWith("IP-ASN,")) return TypeValue("IP-ASN");
            // We only route by domain/IP — skip PROCESS-NAME, URL-REGEX, RULE-SET, etc.
            if (upper.Contains(",")) return null;

            // Bare value (no type prefix). Domain-set style "+.example.com" / ".example.com"
            // loses its marker; every bare name is taken as a suffix, as before.
            if (CidrLike.IsMatch(item))
                return (item.Contains(":") ? "IP-CIDR6," : "IP-CIDR,") + item;
            if (Ipv4Like.IsMatch(item))
                return "IP-CIDR," + item + "/32";
            var host = item.ToLowerInvariant();
            if (host.StartsWith("+.")) return "DOMAIN-SUFFIX," + host.Substring(2);
            if (host.StartsWith(".")) return "DOMAIN-SUFFIX," + host.Substring(1);
            return "DOMAIN-SUFFIX," + host;
        }
    }
}
