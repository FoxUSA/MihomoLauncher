using System;
using System.Collections.Generic;
using System.Linq;

namespace MihomoLauncher.Services
{
    // One app routed through the VPN: which variant (slot) carries it and whether
    // that node is currently up.
    public class VpnRoute
    {
        public string AppName { get; set; } = "";
        public string ProxyName { get; set; } = "";   // mihomo proxy name
        public int SlotNumber { get; set; }            // 1-based ordinal among configured variants
        public string SlotName { get; set; } = "";
        public string Target { get; set; } = "";       // domain routed through the tunnel
        public bool NodeUp { get; set; } = true;
    }

    // Process-wide snapshot of the current VPN routing, written by the launcher's
    // check flow / node monitor and read by the main-window badge and the
    // diagnostics tab. Raises Changed on every update (already on the UI thread,
    // since the launcher updates it from the dispatcher).
    public static class VpnStatus
    {
        private static readonly object _lock = new object();
        private static List<VpnRoute> _routes = new List<VpnRoute>();

        public static event Action Changed;

        public static DateTime UpdatedUtc { get; private set; }

        public static List<VpnRoute> Routes
        {
            get { lock (_lock) return _routes.ToList(); }
        }

        // Any app is currently routed through a VPN node that is up.
        public static bool Active
        {
            get { lock (_lock) return _routes.Any(r => r.NodeUp); }
        }

        public static void SetRoutes(IEnumerable<VpnRoute> routes)
        {
            lock (_lock)
            {
                _routes = (routes ?? Enumerable.Empty<VpnRoute>()).ToList();
                UpdatedUtc = DateTime.UtcNow;
            }
            Raise();
        }

        public static void UpdateNodeUp(string proxyName, bool up)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var r in _routes.Where(r => r.ProxyName == proxyName && r.NodeUp != up))
                {
                    r.NodeUp = up;
                    changed = true;
                }
                if (changed) UpdatedUtc = DateTime.UtcNow;
            }
            if (changed) Raise();
        }

        public static void Clear() => SetRoutes(null);

        private static void Raise()
        {
            try { Changed?.Invoke(); } catch { }
        }
    }
}
