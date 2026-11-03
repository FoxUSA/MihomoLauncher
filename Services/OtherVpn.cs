using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MihomoLauncher.Services
{
    // Another VPN client running beside the launcher. Two tunnels fight over the route table,
    // DNS and the firewall (Windscribe's "Firewall: Always on", for one, blocks everything that
    // isn't its own tunnel), and from our side that looks exactly like "the engine is broken" —
    // so the failure message names the other client instead of offering a reinstall that can't
    // help. Only consulted when the engine has already failed; never closes anything itself.
    public static class OtherVpn
    {
        // Process name (no .exe, lower case) → name to show. Clash-family clients are here too:
        // they ship their own mihomo/clash core under a name that isn't ours.
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "windscribe", "Windscribe" },
            { "windscribeservice", "Windscribe" },
            { "protonvpn", "Proton VPN" },
            { "protonvpn.client", "Proton VPN" },
            { "nordvpn", "NordVPN" },
            { "expressvpn", "ExpressVPN" },
            { "expressvpnd", "ExpressVPN" },
            { "surfshark", "Surfshark" },
            { "kaspersky vpn", "Kaspersky VPN" },
            { "ksde", "Kaspersky VPN" },
            { "hotspotshield", "Hotspot Shield" },
            { "amneziavpn", "AmneziaVPN" },
            { "amneziawg", "AmneziaWG" },
            { "wireguard", "WireGuard" },
            { "openvpn", "OpenVPN" },
            { "openvpn-gui", "OpenVPN" },
            { "outline", "Outline" },
            { "outline-client", "Outline" },
            { "hiddify", "Hiddify" },
            { "hiddifycli", "Hiddify" },
            { "v2rayn", "v2rayN" },
            { "nekoray", "NekoRay" },
            { "nekobox", "NekoBox" },
            { "throne", "Throne" },
            { "clash-verge", "Clash Verge" },
            { "verge-mihomo", "Clash Verge" },
            { "clash for windows", "Clash for Windows" },
            { "flclash", "FlClash" },
            { "happ", "Happ" },
            { "sing-box", "sing-box" },
            { "xray", "Xray" },
            { "adguardvpn", "AdGuard VPN" },
            { "planetvpn", "Planet VPN" },
        };

        // The running ones, comma-separated, or "" when there are none.
        public static string Running()
        {
            var found = new List<string>();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string show;
                        if (Known.TryGetValue(p.ProcessName, out show) && !found.Contains(show)) found.Add(show);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return string.Join(", ", found);
        }
    }
}
