using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // What a pasted link (or a picked file) turned out to be.
    public sealed class ShareLinkInfo
    {
        public bool Ok;
        public VpnSlotKind Kind;
        public string Label = "";    // "VLESS", "Trojan", "Clash", "WireGuard" — also the auto-name stem
        public string Remark = "";   // the name the provider put into the link ("#🇩🇪 Germany"), if any
        public string Value = "";    // the cleaned-up link / path to store in the slot
    }

    // "Paste a link, we figure out the rest" — the way v2rayN / Hiddify / Clash Verge import.
    //
    // vless:// and hysteria2:// keep their own parsers (proven, and they give the server for the
    // tile's country). Every other scheme mihomo understands goes in as VpnSlotKind.ShareLink and
    // is handed to the engine verbatim as a one-line file provider: the engine converts it with
    // the same code it uses for base64 subscriptions (verified on the 1.19.32 binary 2026-10-10:
    // trojan / vmess / ss / tuic / anytls all came out as proxies, names taken from the links).
    public static class ShareLinks
    {
        // scheme -> label, for the schemes that become ShareLink.
        private static readonly Dictionary<string, string> Generic =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "vmess", "VMess" },
                { "trojan", "Trojan" },
                { "ss", "Shadowsocks" },
                { "ssr", "ShadowsocksR" },
                { "hysteria", "Hysteria" },
                { "tuic", "TUIC" },
                { "anytls", "AnyTLS" },
                { "socks", "SOCKS5" },
                { "socks5", "SOCKS5" },
            };

        public static ShareLinkInfo Detect(string raw)
        {
            var info = new ShareLinkInfo();
            var s = (raw ?? "").Trim().Trim('"').Trim();
            if (s.Length == 0) return info;
            info.Value = s;

            // A WireGuard .conf — picked with «Обзор», dragged in, or its path pasted.
            if (s.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && s.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                info.Ok = true;
                info.Kind = VpnSlotKind.WireGuardConf;
                info.Label = "WireGuard";
                try { info.Remark = Path.GetFileNameWithoutExtension(s); } catch { }
                return info;
            }

            int sep = s.IndexOf("://", StringComparison.Ordinal);
            if (sep <= 0) return info;
            var scheme = s.Substring(0, sep).ToLowerInvariant();

            string label;
            if (scheme == "vless") { info.Kind = VpnSlotKind.Vless; info.Label = "VLESS"; }
            else if (scheme == "hysteria2" || scheme == "hy2") { info.Kind = VpnSlotKind.Hysteria2; info.Label = "Hysteria2"; }
            else if (scheme == "http" || scheme == "https") { info.Kind = VpnSlotKind.ClashSub; info.Label = "Clash"; }
            else if (Generic.TryGetValue(scheme, out label)) { info.Kind = VpnSlotKind.ShareLink; info.Label = label; }
            else return info;

            info.Ok = true;
            // A subscription URL's "#..." is not a name, and its host says little to a person.
            if (info.Kind != VpnSlotKind.ClashSub) info.Remark = RemarkOf(s, scheme);
            return info;
        }

        // Protocol label of a stored ShareLink slot (for the Settings list), "" if unknown.
        public static string LabelOf(string link)
        {
            var i = Detect(link);
            return i.Ok ? i.Label : "";
        }

        // The name the link carries: vmess keeps it in the JSON ("ps"), everyone else after '#'.
        private static string RemarkOf(string link, string scheme)
        {
            try
            {
                if (scheme == "vmess")
                {
                    var json = DecodeBase64(link.Substring("vmess://".Length));
                    var m = Regex.Match(json ?? "", "\"ps\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    return m.Success ? Clean(Regex.Unescape(m.Groups[1].Value)) : "";
                }
                int hash = link.IndexOf('#');
                if (hash < 0 || hash == link.Length - 1) return "";
                return Clean(Uri.UnescapeDataString(link.Substring(hash + 1).Replace('+', ' ')));
            }
            catch { return ""; }
        }

        private static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 40 ? s.Substring(0, 40).Trim() : s;
        }

        // The server's host, for the tile's country — the provider itself never tells us.
        public static string HostOf(string link)
        {
            try
            {
                var s = (link ?? "").Trim();
                int sep = s.IndexOf("://", StringComparison.Ordinal);
                if (sep <= 0) return "";
                var scheme = s.Substring(0, sep).ToLowerInvariant();
                var body = s.Substring(sep + 3);

                if (scheme == "vmess")
                {
                    var json = DecodeBase64(body);
                    var m = Regex.Match(json ?? "", "\"add\"\\s*:\\s*\"([^\"]*)\"");
                    return m.Success ? m.Groups[1].Value.Trim() : "";
                }
                if (scheme == "ssr")
                {
                    // ssr://base64(host:port:proto:method:obfs:base64pass/?params)
                    var plain = DecodeBase64(body) ?? "";
                    int c = plain.IndexOf(':');
                    return c > 0 ? plain.Substring(0, c) : "";
                }

                int cut = body.IndexOfAny(new[] { '#', '?', '/' });
                if (cut >= 0) body = body.Substring(0, cut);
                int at = body.LastIndexOf('@');
                if (at >= 0) body = body.Substring(at + 1);
                else if (scheme == "ss")
                {
                    // Old form: ss://base64(method:password@host:port)
                    var plain = DecodeBase64(body) ?? "";
                    int a2 = plain.LastIndexOf('@');
                    if (a2 < 0) return "";
                    body = plain.Substring(a2 + 1);
                }

                if (body.StartsWith("["))
                {
                    int close = body.IndexOf(']');
                    return close > 0 ? body.Substring(1, close - 1) : "";
                }
                int colon = body.LastIndexOf(':');
                return colon > 0 ? body.Substring(0, colon) : body;
            }
            catch { return ""; }
        }

        private static string DecodeBase64(string s)
        {
            try
            {
                s = (s ?? "").Trim();
                int cut = s.IndexOfAny(new[] { '#', '?' });
                if (cut >= 0) s = s.Substring(0, cut);
                s = s.Replace('-', '+').Replace('_', '/');
                while (s.Length % 4 != 0) s += "=";
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch { return null; }
        }

        // The name to give a new variant: the link's own, else "VLESS #1", "VLESS #2", … —
        // made unique against what already exists (two variants with names that come out as the
        // same proxy name would collapse into one in the engine, see DistinctByName).
        public static string AutoName(ShareLinkInfo info, IEnumerable<string> taken)
        {
            var used = new HashSet<string>((taken ?? Enumerable.Empty<string>()).Select(Key), StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(info?.Remark))
            {
                var name = info.Remark.Trim();
                if (!used.Contains(Key(name))) return name;
                for (int n = 2; ; n++)
                    if (!used.Contains(Key(name + " " + n))) return name + " " + n;
            }

            var stem = string.IsNullOrEmpty(info?.Label) ? "VPN" : info.Label;
            for (int n = 1; ; n++)
                if (!used.Contains(Key(stem + " #" + n))) return stem + " #" + n;
        }

        // Same key the engine will see (ProxyParsers.SanitizeName), so "A!" and "A?" count as one.
        private static string Key(string name) => ProxyParsers.ProxyNameFor(new VpnSlot { Name = name ?? "" }, 0);
    }
}
