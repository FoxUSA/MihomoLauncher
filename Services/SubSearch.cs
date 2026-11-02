using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace MihomoLauncher.Services
{
    // Search inside a subscription's lists («Подписка» window). The query is one of:
    //   a mask    — "*.nextdns.io", "95.179.*", "*tiktok*": * = any run of characters, ? = one,
    //               matched against what the list says (an entry, its value, "+.x" for suffixes);
    //   a site    — "test.nextdns.io": entries that MENTION it, and above all entries that CATCH
    //               it the way the engine would (+.nextdns.io, DOMAIN-SUFFIX,nextdns.io, a keyword…),
    //               plus address lists, against the addresses the engine itself resolves it to;
    //   an IP     — "95.179.134.211": the CIDRs that hold it.
    public sealed class SubQuery
    {
        public string Text = "";
        public Regex Mask;                  // non-null for a mask
        public string Host;                 // a site name (no mask)
        public IPAddress Ip;                // typed address
        public List<IPAddress> Resolved = new List<IPAddress>();   // the site's addresses, from the engine

        public bool IsEmpty => Text.Length == 0;
        public bool CanCatch => Mask == null && (Host != null || Ip != null);
    }

    public sealed class SubHit
    {
        public int Count;                   // entries that match the text or catch the address
        public string Example;              // one of them, the catching one first
        public string Catch;                // the entry that would actually catch the site/IP
        public IPAddress CatchIp;           // set when it caught by a resolved address
    }

    public static class SubSearch
    {
        public static SubQuery Parse(string text)
        {
            var q = new SubQuery { Text = (text ?? "").Trim() };
            var t = q.Text;
            if (t.Length == 0) return q;
            if (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0)
            {
                q.Mask = new Regex("^" + Regex.Escape(t.ToLowerInvariant()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                                   RegexOptions.CultureInvariant);
                return q;
            }
            // A pasted link: keep the host.
            Uri u;
            if (t.Contains("://") && Uri.TryCreate(t, UriKind.Absolute, out u)) t = u.Host;
            IPAddress ip;
            if (IPAddress.TryParse(t.Trim('[', ']'), out ip)) { q.Ip = ip; return q; }
            if (Regex.IsMatch(t, @"^[\w\-]+(\.[\w\-]+)+\.?$")) q.Host = t.TrimEnd('.').ToLowerInvariant();
            return q;
        }

        public static SubHit Find(List<string> entries, SubQuery q)
        {
            var hit = new SubHit();
            if (entries == null || q.IsEmpty) return hit;
            foreach (var e in entries)
            {
                string type, value;
                Split(e, out type, out value);
                IPAddress via = null;
                bool caught = q.CanCatch && Catches(type, value, NoResolve(e), q, out via);
                bool text = caught || TextMatch(e, type, value, q);
                if (!text) continue;
                hit.Count++;
                if (caught && hit.Catch == null) { hit.Catch = e; hit.CatchIp = via; hit.Example = e; }
                if (hit.Example == null) hit.Example = e;
            }
            return hit;
        }

        // The rows of a list to show in its own view while a search is on.
        public static bool Matches(string entry, SubQuery q)
        {
            if (q.IsEmpty) return true;
            string type, value;
            Split(entry, out type, out value);
            IPAddress via;
            return TextMatch(entry, type, value, q) || (q.CanCatch && Catches(type, value, NoResolve(entry), q, out via));
        }

        // "DOMAIN-SUFFIX,x.com" → (DOMAIN-SUFFIX, x.com); "+.x.com" / "1.2.3.0/24" → (null, itself).
        private static void Split(string entry, out string type, out string value)
        {
            type = null;
            value = (entry ?? "").Trim();
            int c = value.IndexOf(',');
            if (c > 0)
            {
                var head = value.Substring(0, c).Trim();
                if (Regex.IsMatch(head, "^[A-Z][A-Z0-9\\-]+$"))
                {
                    type = head;
                    var rest = value.Substring(c + 1);
                    int c2 = rest.IndexOf(',');
                    value = (c2 >= 0 ? rest.Substring(0, c2) : rest).Trim();
                }
            }
        }

        private static bool TextMatch(string entry, string type, string value, SubQuery q)
        {
            if (q.Mask != null)
            {
                var v = value.ToLowerInvariant();
                if (q.Mask.IsMatch(entry.ToLowerInvariant()) || q.Mask.IsMatch(v)) return true;
                bool suffix = type == "DOMAIN-SUFFIX" || (type == null && v.StartsWith("+."));
                var bare = v.StartsWith("+.") ? v.Substring(2) : v.TrimStart('.');
                return suffix && (q.Mask.IsMatch("+." + bare) || q.Mask.IsMatch("x." + bare));
            }
            return entry.IndexOf(q.Text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Catches(string type, string value, bool noResolve, SubQuery q, out IPAddress via)
        {
            via = null;
            switch (type)
            {
                case "DOMAIN": return q.Host != null && q.Host == value.ToLowerInvariant();
                case "DOMAIN-SUFFIX": return q.Host != null && RouteExplainer.SuffixMatch(q.Host, value);
                case "DOMAIN-KEYWORD": return q.Host != null && q.Host.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
                case "DOMAIN-WILDCARD": return q.Host != null && RouteExplainer.PatternMatch(value, q.Host);
                case "DOMAIN-REGEX":
                    try { return q.Host != null && Regex.IsMatch(q.Host, value, RegexOptions.IgnoreCase); } catch { return false; }
                case "IP-CIDR":
                case "IP-CIDR6":
                case null:
                    if (type != null || value.IndexOf('/') > 0 || IsIp(value)) return CidrHit(value, noResolve, q, out via);
                    return q.Host != null && RouteExplainer.PatternMatch(value, q.Host);
                default: return false;
            }
        }

        // A site meets an address list only through the addresses the engine looks up for it —
        // which it does not do for a "no-resolve" rule.
        private static bool CidrHit(string cidr, bool noResolve, SubQuery q, out IPAddress via)
        {
            via = null;
            if (q.Ip != null) return RouteExplainer.CidrContains(cidr, q.Ip);
            if (noResolve) return false;
            foreach (var ip in q.Resolved)
                if (RouteExplainer.CidrContains(cidr, ip)) { via = ip; return true; }
            return false;
        }

        private static bool NoResolve(string entry) => (entry ?? "").IndexOf("no-resolve", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsIp(string s) { IPAddress ip; return IPAddress.TryParse(s, out ip); }
    }
}
