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
        public bool Disabled { get; set; }            // switched off in Windows («Отключить» in Network Connections)

        // One state for the chip's colour and the picker's words (2026-11-18):
        // Connected — blue Wi-Fi / green cable; NoCable / NoNetwork / NoInternet — amber;
        // Disabled / Absent — grey.
        public NicState State
        {
            get
            {
                if (!Present) return NicState.Absent;
                if (Disabled) return NicState.Disabled;
                if (!Up) return Wireless ? NicState.NoNetwork : NicState.NoCable;
                if (!HasGateway || NetCards.InternetFailed(Name)) return NicState.NoInternet;
                return NicState.Connected;
            }
        }
    }

    public enum NicState { Connected, NoCable, NoNetwork, NoInternet, Disabled, Absent }

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

        // Cards through which ip.byfox.dev did not answer (MainWindow.NicIps): connected to a
        // network, yet no internet — «Нет интернета».
        private static readonly HashSet<string> _noInternet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void SetInternet(string card, bool ok)
        {
            lock (_noInternet) { if (ok) _noInternet.Remove(card ?? ""); else _noInternet.Add(card ?? ""); }
        }

        public static bool InternetFailed(string card)
        {
            lock (_noInternet) return _noInternet.Contains(card ?? "");
        }

        // Can the engine send through this card at all — there, switched on, connected, with an
        // address. «Нет интернета» still can: the card works, what is past it does not.
        public static bool Usable(NetCard c) => c != null && c.Present && !c.Disabled && c.Up;

        // A way out that is none of the listed cards — a phone modem, a mobile-broadband card, a
        // dial-up / PPPoE connection: up, with an IPv4 address, not ours and not virtual. With one
        // of those Windows still has a network while every Wi-Fi / cable card is down, so a tile
        // «по умолчанию» must not say «нет сети» (2026-11-19).
        public static bool OtherWayOut()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var t = nic.NetworkInterfaceType;
                    if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) continue;
                    if (t == NetworkInterfaceType.Wireless80211 || t == NetworkInterfaceType.Ethernet
                        || t == NetworkInterfaceType.GigabitEthernet || t == NetworkInterfaceType.FastEthernetT
                        || t == NetworkInterfaceType.FastEthernetFx || t == NetworkInterfaceType.Ethernet3Megabit) continue;   // List() has them
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    var desc = (nic.Description ?? "") + " " + (nic.Name ?? "");
                    if (VirtualMarks.Any(m => desc.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    bool addr = nic.GetIPProperties().UnicastAddresses.Any(a =>
                        a?.Address != null && a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                    if (addr) return true;
                }
            }
            catch { return true; }   // can't tell → never claim «нет сети» on a guess
            return false;
        }

        // The card Windows itself sends traffic through: a working card with a gateway and the lowest
        // gateway + interface metric (NicMetrics). Null when there is none.
        public static string DefaultName(List<NetCard> cards)
        {
            try
            {
                var metrics = NicMetrics.Read();
                return (cards ?? new List<NetCard>())
                    .Where(c => c.Up && c.HasGateway)
                    .OrderBy(c => { NicMetrics.Current m; return metrics.TryGetValue(c.Name, out m) && m.Has4 ? m.Total4 : long.MaxValue; })
                    .Select(c => c.Name).FirstOrDefault();
            }
            catch { return null; }
        }

        // The card a tile's traffic leaves through right now: the first of its own cards that works,
        // else (no cards of its own) the one Windows uses. Null when nothing works — what «Не менять
        // сетевую карту» pins the tile to.
        public static string CurrentOf(AppEntry entry)
        {
            var now = List();
            var own = Chosen(entry, now);
            if (own.Count > 0) return own.FirstOrDefault(Usable)?.Name;
            var def = DefaultName(now);
            return now.Any(c => Usable(c) && string.Equals(c.Name, def, StringComparison.OrdinalIgnoreCase)) ? def : null;
        }

        // The words for a state, as the picker and Settings → «Сеть» show them.
        public static string StateText(NetCard c)
        {
            switch (c.State)
            {
                case NicState.Absent: return Loc.T("Main_NicAbsent");
                case NicState.Disabled: return Loc.T("Main_NicDown");
                case NicState.NoCable: return Loc.T("Main_NicNoCable");
                case NicState.NoNetwork: return Loc.T("Main_NicNoNet");
                case NicState.NoInternet: return Loc.T("Main_NicNoGw");
                default: return Loc.T("Main_NicUp");
            }
        }

        // Every physical card Windows has, switched off ones too (2026-11-18: a card disabled by
        // hand vanished from the list — .NET's NetworkInterface does not report disabled adapters
        // — and after switching it back on the tiles still showed one card). The switched-off ones
        // come from MSFT_NetAdapter (no rights needed, ~20 ms warm); cached for 3 s.
        public static List<NetCard> List()
        {
            var list = new List<NetCard>();
            var adapters = Adapters();
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

            // Switched off in Windows: only WMI still knows them.
            foreach (var a in adapters)
            {
                if (!a.Disabled || !a.Physical) continue;
                if (list.Any(c => string.Equals(c.Name, a.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var desc = a.Description + " " + a.Name;
                if (VirtualMarks.Any(m => desc.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                list.Add(new NetCard { Name = a.Name, Description = a.Description, Wireless = a.Wireless, Disabled = true });
            }

            return list.Where(c => c.Name.Length > 0)
                       .OrderBy(c => c.Wireless ? 1 : 0).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        private sealed class Adapter
        {
            public string Name = "", Description = "";
            public bool Wireless, Disabled, Physical;
        }

        private static List<Adapter> _adapters = new List<Adapter>();
        private static DateTime _adaptersAt = DateTime.MinValue;

        private static List<Adapter> Adapters()
        {
            if ((DateTime.UtcNow - _adaptersAt).TotalSeconds < 3) return _adapters;
            var list = new List<Adapter>();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"\\.\root\StandardCimv2",
                    "SELECT Name, InterfaceDescription, State, InterfaceAdminStatus, NdisPhysicalMedium, HardwareInterface, Virtual FROM MSFT_NetAdapter"))
                using (var all = s.Get())
                    foreach (System.Management.ManagementObject mo in all)
                        using (mo)
                        {
                            // State: 1 = present but not started, 2 = started, 3 = disabled by hand.
                            // Only 3 is «switched off» (2026-11-21: a USB Wi-Fi pulled out stays
                            // behind as State 1 + admin down, «held for eject» — Windows' own
                            // «Сетевые подключения» hides it, the launcher listed it as «выключена»).
                            uint state = Convert.ToUInt32(mo["State"] ?? 0u);
                            uint medium = Convert.ToUInt32(mo["NdisPhysicalMedium"] ?? 0u);   // 1, 9 = Wi-Fi; 14 = 802.3
                            list.Add(new Adapter
                            {
                                Name = mo["Name"] as string ?? "",
                                Description = mo["InterfaceDescription"] as string ?? "",
                                Wireless = medium == 1 || medium == 9,
                                Disabled = state == 3,
                                Physical = (mo["HardwareInterface"] as bool? ?? false) && !(mo["Virtual"] as bool? ?? false)
                                           && (medium == 1 || medium == 9 || medium == 14 || medium == 0),
                            });
                        }
            }
            catch (Exception ex) { Log.Error("netcards-wmi", ex); list = _adapters; }
            _adapters = list;
            _adaptersAt = DateTime.UtcNow;
            return list;
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
