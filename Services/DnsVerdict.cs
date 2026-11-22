using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Why a name gave no address (2026-11-17): a DNS filter, or a name that simply isn't there?
    //
    // The engine's log says only «dns resolve failed: couldn't find ip» either way. Filters don't
    // all answer with a 0.0.0.0 stub (AdGuard Home's default, NextDNS) — routers, Pi-hole in
    // NXDOMAIN / NODATA mode, Quad9 answer with NOTHING. So the name is asked again, of the same
    // server, and the answer itself tells:
    //   • 0.0.0.0 / :: / 127.0.0.1                → filter (a stub);
    //   • EDE 15/16/17 (Blocked/Censored/Filtered, RFC 8914) → filter — the server says so;
    //   • REFUSED for one name while others resolve → filter (AdGuard Home's «REFUSED» mode);
    //   • no address AND no SOA in Authority      → filter: a real «no such name» / «no address»
    //     always carries the zone's SOA (RFC 2308, it is what negative caching runs on); a blocker
    //     makes the answer up and has no zone to put there. AdGuard Home does add a SOA, a fake
    //     one — «fake-for-negative-caching.adguard.com»;
    //   • no address WITH a real SOA              → the name really has no address / doesn't exist;
    //   • an address now, or no answer at all     → not a filter (a slow or failed lookup).
    // Checked on the author's router 2026-11-17: mc.yandex.ru — NOERROR, empty, EDE 15, no SOA;
    // a made-up .com — NXDOMAIN + com. SOA; _dmarc.google.com — NOERROR, empty + google.com SOA.
    // The engine's cache keeps the SOA (and drops the EDE), so a cached answer reads the same.
    public static class DnsVerdict
    {
        public sealed class Result
        {
            public DnsStats.Kind Kind = DnsStats.Kind.NoAddress;
            public bool Known;        // the server answered; false = couldn't ask, decide later
            public string Why = "";
        }

        private static readonly Regex StatusRx = new Regex("\"Status\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex RecordRx = new Regex("\"data\"\\s*:\\s*\"(?<d>[^\"]*)\"\\s*,\\s*\"name\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"type\"\\s*:\\s*(?<t>\\d+)", RegexOptions.Compiled);
        private static readonly Regex AuthorityRx = new Regex("\"Authority\"\\s*:\\s*\\[(?<a>.*?)\\]", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex EdeRx = new Regex("EDE:\\s*(\\d+)", RegexOptions.Compiled);

        public static Result Classify(string json)
        {
            var r = new Result();
            json = json ?? "";
            var st = StatusRx.Match(json);
            if (!st.Success) { r.Why = "нет ответа"; return r; }   // {"message":…} or nothing
            r.Known = true;
            int status = int.Parse(st.Groups[1].Value);

            foreach (Match m in EdeRx.Matches(json))
            {
                var code = m.Groups[1].Value;
                if (code == "15" || code == "16" || code == "17") { r.Kind = DnsStats.Kind.Stub; r.Why = "EDE " + code; return r; }
            }

            // Addresses only up to the Authority section — records there are not answers.
            var answerPart = json;
            int auth = json.IndexOf("\"Authority\"", StringComparison.Ordinal);
            if (auth >= 0) answerPart = json.Substring(0, auth);
            var addrs = RecordRx.Matches(answerPart).Cast<Match>()
                               .Where(m => m.Groups["t"].Value == "1" || m.Groups["t"].Value == "28")
                               .Select(m => m.Groups["d"].Value).ToList();
            if (addrs.Count > 0)
            {
                if (addrs.All(a => a == "0.0.0.0" || a == "::" || a == "127.0.0.1" || a == "::1"))
                { r.Kind = DnsStats.Kind.Stub; r.Why = "заглушка " + addrs[0]; return r; }
                r.Why = "сейчас адрес есть";      // the miss was a slow / failed lookup
                return r;
            }

            if (status == 5) { r.Kind = DnsStats.Kind.Stub; r.Why = "REFUSED"; return r; }
            if (status != 0 && status != 3) { r.Why = "Status " + status; return r; }   // SERVFAIL etc.

            var am = AuthorityRx.Match(json);
            bool soa = am.Success && Regex.IsMatch(am.Groups["a"].Value, "\"type\"\\s*:\\s*6\\b");
            if (!soa) { r.Kind = DnsStats.Kind.Stub; r.Why = (status == 3 ? "NXDOMAIN" : "пусто") + " без SOA"; return r; }
            if (am.Groups["a"].Value.IndexOf("fake-for-negative-caching", StringComparison.OrdinalIgnoreCase) >= 0)
            { r.Kind = DnsStats.Kind.Stub; r.Why = "SOA AdGuard"; return r; }
            r.Why = (status == 3 ? "NXDOMAIN" : "нет A") + " + SOA";
            return r;
        }

        // Ask the server DIRECT traffic actually uses. The machine's DNS = the engine's main resolver
        // (/dns/query); a `direct-nameserver` of the user's = a throwaway engine copy pointed at it
        // (DirectDns.QueryAsync — the live engine can't be told to use another server for one query).
        public static async Task<Dictionary<string, Result>> ProbeAsync(AppConfig cfg, MihomoController ctl, IList<string> names)
        {
            var res = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);
            var direct = MihomoConfigBuilder.LastDirectDns;
            if (direct == null || direct.Count == 0)
            {
                if (ctl == null) return res;
                foreach (var n in names)
                    res[n] = Classify(await ctl.DnsQueryRawAsync(n).ConfigureAwait(false));
                return res;
            }
            try
            {
                var answers = await DirectDns.QueryAsync(cfg, direct[0], names, CancellationToken.None).ConfigureAwait(false);
                foreach (var a in answers)
                    res[a.Name] = a.Ok ? Classify(a.Raw) : new Result { Why = a.Error };
            }
            catch (Exception ex) { Log.Info("DNS-статистика: не спросить " + direct[0] + " — " + ex.Message); }
            return res;
        }
    }
}
