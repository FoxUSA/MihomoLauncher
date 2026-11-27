using System.Collections.Generic;
using System.Linq;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // «Каскадный VPN» (2026-11-27): which configurations a cascade tile chains, in order.
    //
    // The same ticks the rule editor and the Routing table always had — with the switch on, their
    // numbers are the order of the hops (1 = nearest to you, the last = where the site sees you),
    // not an order of preference. Never re-ordered by health: a chain is the path the person drew.
    public static class Cascade
    {
        public const int MaxHops = 3;

        // The cascade's own hops (AppEntry.CascadeHops — its memory apart from the ordinary ticks),
        // in order, the configurations that still exist, at most MaxHops.
        public static List<VpnSlot> Hops(AppEntry a, List<VpnSlot> slots)
        {
            if (a == null) return new List<VpnSlot>();
            var all = (slots ?? new List<VpnSlot>()).Where(s => s != null).ToList();
            return (a.CascadeHops ?? new List<string>())
                .Select(id => all.FirstOrDefault(s => s.Id == id))
                .Where(s => s != null)
                .Distinct()
                .Take(MaxHops)
                .ToList();
        }

        // The ordinary ticks in the rule's own order (VpnSlotOrder first, the rest in the Settings
        // order) — where a config saved before CascadeHops existed kept its hops.
        public static List<string> OrdinaryTicks(AppEntry a, List<VpnSlot> slots)
        {
            if (a == null) return new List<string>();
            var order = a.VpnSlotOrder ?? new List<string>();
            var off = new HashSet<string>(a.DisabledVpnSlotIds ?? new List<string>());
            return (slots ?? new List<VpnSlot>())
                .Where(s => s != null && !off.Contains(s.Id))
                .OrderBy(s => { int i = order.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; })
                .Select(s => s.Id)
                .ToList();
        }

        // A cascade for real: the switch on and at least two hops ticked.
        public static bool IsOn(AppEntry a, List<VpnSlot> slots)
            => a != null && a.Cascade && a.RequiresVpn && !a.IsNoNodeMode && Hops(a, slots).Count >= 2;
    }
}
