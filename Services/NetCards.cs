using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // One network card of this machine, as a tile's «сетевая карта» plate offers it.
    public sealed class NetCard
    {
        public string Name { get; set; } = "";         // what Windows calls it (adapter alias) = mihomo's interface-name
        public string Description { get; set; } = "";
        public bool Wireless { get; set; }
        public bool Up { get; set; }                  // connected, with an address
        public bool HasGateway { get; set; }          // can reach anything past the LAN
        public bool Present { get; set; } = true;     // false: chosen earlier, not in the system now
    }

    // The machine's physical cards (Wi-Fi and wired), for the per-tile card choice (2026-11-14).
    //
    // The choice is applied by the engine itself: a `type: direct` outbound with `interface-name`
    // binds the socket to that card (IP_UNICAST_IF on Windows), past the routing table. Measured on
    // the binary 2026-11-14: bound to Ethernet → goes out; bound to a disabled Wi-Fi or a name that
    // does not exist → «interface not found», nothing leaves (that is the "hard" choice the user
    // asked for); a `fallback` group over two cards takes the first that answers.
    //
    // The name is the adapter's alias ("Ethernet", "Беспроводная сеть 2") — that is what Go's
    // net.Interfaces() reports on Windows, so it is what the engine matches.
    public static class NetCards
    {
        // Our own tunnel and the usual virtual adapters: binding to one of these is never what a
        // person choosing "which card" means.
        private static readonly string[] VirtualMarks =
        {
            "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "tap ", "wintun", "wireguard",
            "meta tunnel", "loopback", "bluetooth", "wan miniport", "npcap", "kernel debug",
            "teredo", "isatap", "zerotier", "tailscale", "openvpn", "vpn", "pseudo", "wi-fi direct",
        };

        public static List<NetCard> List()
        {
            var list = new List<NetCard>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var t = nic.NetworkInterfaceType;
                    bool wireless = t == NetworkInterfaceType.Wireless80211;
                    bool wired = t == NetworkInterfaceType.Ethernet || t == NetworkInterfaceType.GigabitEthernet
                                 || t == NetworkInterfaceType.FastEthernetT || t == NetworkInterfaceType.FastEthernetFx
                                 || t == NetworkInterfaceType.Ethernet3Megabit;
                    if (!wireless && !wired) continue;
                    var desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
                    if (VirtualMarks.Any(m => desc.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                    bool up = nic.OperationalStatus == OperationalStatus.Up;
                    bool gw = false;
                    try { gw = up && nic.GetIPProperties().GatewayAddresses.Any(g => g?.Address != null && !g.Address.ToString().StartsWith("0.")); }
                    catch { }
                    list.Add(new NetCard
                    {
                        Name = nic.Name ?? "",
                        Description = nic.Description ?? "",
                        Wireless = wireless,
                        Up = up,
                        HasGateway = gw,
                    });
                }
            }
            catch (Exception ex) { Log.Error("netcards", ex); }
            return list.Where(c => c.Name.Length > 0)
                       .OrderBy(c => c.Wireless ? 1 : 0).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        // The cards a rule chose, in its order, each with its state right now — a card that has
        // gone from the system (a USB Wi-Fi pulled out) is listed too, marked absent: the choice is
        // hard, and the plate has to say why nothing goes out.
        public static List<NetCard> Chosen(AppEntry entry, List<NetCard> present = null)
        {
            var now = present ?? List();
            var result = new List<NetCard>();
            foreach (var name in Clean(entry?.Interfaces))
            {
                var c = now.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                result.Add(c ?? new NetCard { Name = name, Present = false });
            }
            return result;
        }

        // The rule's card list as the engine gets it: trimmed, no blanks, no repeats, in order.
        public static List<string> Clean(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var n in names ?? Enumerable.Empty<string>())
            {
                var v = (n ?? "").Trim().Replace("\"", "");
                if (v.Length > 0 && seen.Add(v)) list.Add(v);
            }
            return list;
        }
    }
}
