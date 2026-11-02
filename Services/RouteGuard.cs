using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MihomoLauncher.Services
{
    // Does traffic ACTUALLY go through the tunnel right now?
    //
    // Asking the engine `tun.enable` answers a different question: it reports the flag from the
    // config we ourselves wrote, which is always true. The interface can be up and that flag can
    // read true while Windows no longer has a single route pointing at it — a sleep/resume, an
    // adapter reset, a DHCP renew, another VPN client or a network change is enough. In that
    // state every packet leaves through the physical adapter: no rules, no kill-switch, the real
    // IP on the wire — and the launcher, having asked only the flag, reports everything green.
    // That is the "со временем всё слетает, а утилита пишет что подключено" the user hit.
    //
    // So ask Windows the routing question directly: for a public address, which interface would
    // carry the packet? GetBestRoute returns the exact route the stack would pick, which is the
    // only answer that can't be stale.
    public static class RouteGuard
    {
        // The tunnel's own address (mihomo puts 198.18.0.1/30 on the TUN, gateway 198.18.0.2 —
        // the base of the fake-ip range in MihomoConfigBuilder).
        private const string TunAddress = "198.18.0.1";

        // Probes: public, never inside the LAN/RDP ranges we deliberately exclude from the
        // tunnel, and never actually contacted — GetBestRoute only consults the route table.
        private static readonly string[] ProbeTargets = { "1.1.1.1", "8.8.8.8" };

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpForwardRow
        {
            public uint dwForwardDest;
            public uint dwForwardMask;
            public uint dwForwardPolicy;
            public uint dwForwardNextHop;
            public uint dwForwardIfIndex;
            public uint dwForwardType;
            public uint dwForwardProto;
            public uint dwForwardAge;
            public uint dwForwardNextHopAS;
            public uint dwForwardMetric1;
            public uint dwForwardMetric2;
            public uint dwForwardMetric3;
            public uint dwForwardMetric4;
            public uint dwForwardMetric5;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MibIpForwardRow pBestRoute);

        // Interface index of OUR tunnel, or 0 when there is none (engine down, or it never
        // raised the interface). 0 is "no verdict", NOT "leaking".
        //
        // `device` is the adapter name the running engine reports for its own TUN. It matters:
        // a second engine puts the SAME 198.18.0.1 on its own adapter, so matching by address
        // alone can lock onto the wrong tunnel and cheerfully report "all through the VPN"
        // while the traffic leaves by the other one. The address stays as a fallback for when
        // the engine can't be asked.
        public static int TunInterfaceIndex(string device = null)
        {
            try
            {
                int byAddress = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }

                    int index;
                    try { index = props.GetIPv4Properties().Index; }
                    catch { continue; }

                    if (!string.IsNullOrEmpty(device) &&
                        string.Equals(nic.Name, device, StringComparison.OrdinalIgnoreCase))
                        return index;                      // named by the engine itself — exact

                    if (byAddress != 0) continue;
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (addr.Address.ToString().Equals(TunAddress, StringComparison.Ordinal))
                        {
                            byAddress = index;
                            break;
                        }
                    }
                }
                return byAddress;
            }
            catch (Exception ex) { try { Log.Error("routeguard-tun-index", ex); } catch { } }
            return 0;
        }

        // Three-state on purpose. A missing tunnel is a different problem with a different
        // repair (the engine is down — the watchdog's existing path), and answering "leaking"
        // there would send it to the wrong fix.
        public enum Verdict
        {
            Unknown,        // no tunnel interface, or the route table couldn't be read
            ThroughTun,     // public traffic is carried by the tunnel — as intended
            Bypassing,      // the tunnel is up but the stack routes public traffic around it
        }

        public static Verdict Check(string device = null)
        {
            int tunIndex = TunInterfaceIndex(device);
            if (tunIndex == 0) return Verdict.Unknown;

            bool any = false;
            foreach (var target in ProbeTargets)
            {
                int ifIndex = BestRouteInterface(target);
                if (ifIndex == 0) continue;          // couldn't ask — don't guess
                any = true;
                if (ifIndex != tunIndex) return Verdict.Bypassing;
            }
            return any ? Verdict.ThroughTun : Verdict.Unknown;
        }

        private static int BestRouteInterface(string destination)
        {
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(destination, out ip)) return 0;
                if (ip.AddressFamily != AddressFamily.InterNetwork) return 0;

                uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);   // in_addr order
                MibIpForwardRow row;
                if (GetBestRoute(dest, 0, out row) != 0) return 0;            // NO_ERROR == 0
                return (int)row.dwForwardIfIndex;
            }
            catch (Exception ex) { try { Log.Error("routeguard-best-route", ex); } catch { } return 0; }
        }
    }
}
