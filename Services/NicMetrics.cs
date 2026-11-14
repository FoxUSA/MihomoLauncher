using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace MihomoLauncher.Services
{
    // Which network card Windows itself prefers (Settings → «Сеть», 2026-11-14).
    //
    // Windows sends traffic that no more specific route claims through the default route with the
    // lowest metric = route metric + INTERFACE metric. The interface metric is either automatic
    // (Windows derives it from the link speed, so a 2.5 Gbit cable beats a 300 Mbit Wi-Fi) or set
    // by hand in the adapter's IPv4 → Advanced dialog («Автоматическое назначение метрики»). The
    // author's own machine had it set by hand — Wi-Fi 10, Ethernet 500 — so Wi-Fi won, which is
    // what started this.
    //
    // Reading needs no rights (MSFT_NetIPInterface, root\StandardCimv2). Writing needs an
    // administrator, which the launcher deliberately is not: the plan goes to DataUser\nicmetric.plan
    // and the launcher's SYSTEM helper (--firewall-sync, run by the engine service's command 133 —
    // no UAC, no new service build) applies it once and deletes it, then writes .result.
    //
    // The same thing Set-NetIPInterface -InterfaceMetric / -AutomaticMetric does (a CIM
    // ModifyInstance of MSFT_NetIPInterface), for IPv4 and IPv6 alike.
    public static class NicMetrics
    {
        public sealed class Current
        {
            public string Alias = "";
            public bool Auto4 = true;
            public uint Metric4;
            public bool Auto6 = true;
            public uint Metric6;
            public bool Has4, Has6;
            // Metric of the card's IPv4 default route (its gateway). Windows compares route +
            // interface metric, so a gateway set by hand to 500 loses to one at 10 whatever the
            // interface metrics say — exactly the author's machine (2026-11-14: Wi-Fi 10+20=30 beat
            // Ethernet 500+10=510 after «Применить порядок»).
            public bool HasGw4;
            public uint Gw4;
            public long Total4 => (HasGw4 ? Gw4 : 0) + (Has4 ? Metric4 : 0);
        }

        private const string Ns = @"\\.\root\StandardCimv2";
        private const ushort IPv4 = 2, IPv6 = 23;

        public static string PlanPath => Path.Combine(VersionInfo.DataUserDir, "nicmetric.plan");
        public static string ResultPath => Path.Combine(VersionInfo.DataUserDir, "nicmetric.result");

        // ---------- read (any user) ----------

        public static Dictionary<string, Current> Read()
        {
            var map = new Dictionary<string, Current>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher(Ns, "SELECT InterfaceAlias, AddressFamily, InterfaceMetric, AutomaticMetric FROM MSFT_NetIPInterface"))
                using (var all = s.Get())
                    foreach (ManagementObject mo in all)
                        using (mo)
                        {
                            var alias = mo["InterfaceAlias"] as string ?? "";
                            if (alias.Length == 0) continue;
                            Current c;
                            if (!map.TryGetValue(alias, out c)) map[alias] = c = new Current { Alias = alias };
                            var fam = Convert.ToUInt16(mo["AddressFamily"] ?? 0);
                            var metric = Convert.ToUInt32(mo["InterfaceMetric"] ?? 0);
                            var auto = Convert.ToUInt32(mo["AutomaticMetric"] ?? 1) == 1;
                            if (fam == IPv4) { c.Has4 = true; c.Metric4 = metric; c.Auto4 = auto; }
                            else if (fam == IPv6) { c.Has6 = true; c.Metric6 = metric; c.Auto6 = auto; }
                        }
            }
            catch (Exception ex) { Log.Error("nic-metric-read", ex); }
            try
            {
                using (var s = new ManagementObjectSearcher(Ns, "SELECT InterfaceAlias, RouteMetric FROM MSFT_NetRoute WHERE DestinationPrefix='0.0.0.0/0'"))
                using (var all = s.Get())
                    foreach (ManagementObject mo in all)
                        using (mo)
                        {
                            var alias = mo["InterfaceAlias"] as string ?? "";
                            Current c;
                            if (alias.Length == 0 || !map.TryGetValue(alias, out c)) continue;
                            var m = Convert.ToUInt32(mo["RouteMetric"] ?? 0);
                            if (!c.HasGw4 || m < c.Gw4) { c.Gw4 = m; c.HasGw4 = true; }
                        }
            }
            catch (Exception ex) { Log.Error("nic-route-read", ex); }
            return map;
        }

        // ---------- plan (the launcher, as the user) ----------

        // cards in priority order; metric 0 = automatic. Returns the plan's id.
        public static string WritePlan(IList<KeyValuePair<string, uint>> cards)
        {
            var id = Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            sb.AppendLine("# MihomoLauncher network-card metrics — applied once by the SYSTEM helper, then deleted");
            sb.AppendLine("id=" + id);
            foreach (var kv in cards)
            {
                var alias = (kv.Key ?? "").Replace("\r", "").Replace("\n", "").Replace("|", "");
                if (alias.Length == 0) continue;
                sb.AppendLine("nic=" + (kv.Value == 0 ? "auto" : kv.Value.ToString()) + "|" + alias);
            }
            Directory.CreateDirectory(VersionInfo.DataUserDir);
            try { if (File.Exists(ResultPath)) File.Delete(ResultPath); } catch { }
            var tmp = PlanPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(PlanPath)) File.Delete(PlanPath);
            File.Move(tmp, PlanPath);
            return id;
        }

        // The helper's report for that plan: null while it hasn't run; "" = everything applied;
        // otherwise what went wrong.
        public static string ReadResult(string id)
        {
            try
            {
                if (!File.Exists(ResultPath)) return null;
                var lines = File.ReadAllLines(ResultPath);
                if (lines.Length == 0 || lines[0] != "id=" + id) return null;
                return string.Join("\n", lines.Skip(1).Where(l => l.Length > 0));
            }
            catch { return null; }
        }

        // ---------- apply (the SYSTEM helper) ----------

        // Called by --firewall-sync after the firewall plan. Nothing to do unless a plan is waiting:
        // the helper runs on every firewall change too, and must not re-touch the cards each time.
        public static void ApplyFromPlan()
        {
            if (!File.Exists(PlanPath)) return;
            string id = "";
            var errors = new List<string>();
            try
            {
                var lines = File.ReadAllLines(PlanPath);
                try { File.Delete(PlanPath); } catch { }
                foreach (var raw in lines)
                {
                    var line = (raw ?? "").Trim();
                    if (line.StartsWith("id=")) { id = line.Substring(3); continue; }
                    if (!line.StartsWith("nic=")) continue;
                    var v = line.Substring(4);
                    int bar = v.IndexOf('|');
                    if (bar <= 0) continue;
                    var what = v.Substring(0, bar);
                    var alias = v.Substring(bar + 1);
                    uint metric = 0;
                    if (what != "auto" && (!uint.TryParse(what, out metric) || metric == 0 || metric > 9999)) continue;
                    var err = Apply(alias, metric);
                    if (err != null) errors.Add(alias + ": " + err);
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }

            try
            {
                File.WriteAllText(ResultPath, "id=" + id + "\r\n" + string.Join("\r\n", errors), new UTF8Encoding(false));
            }
            catch { }
            try { Log.Info("Сеть: метрики карт " + (errors.Count == 0 ? "применены." : "— ошибки: " + string.Join("; ", errors))); } catch { }
        }

        // metric 0 = automatic. Both address families of that card; null = done.
        private static string Apply(string alias, uint metric)
        {
            int touched = 0;
            try
            {
                var wql = "SELECT * FROM MSFT_NetIPInterface WHERE InterfaceAlias='"
                          + alias.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
                using (var s = new ManagementObjectSearcher(Ns, wql))
                using (var all = s.Get())
                    foreach (ManagementObject mo in all)
                        using (mo)
                        {
                            var fam = Convert.ToUInt16(mo["AddressFamily"] ?? 0);
                            if (fam != IPv4 && fam != IPv6) continue;
                            if (metric == 0) SetProp(mo, "AutomaticMetric", 1);
                            else
                            {
                                SetProp(mo, "AutomaticMetric", 0);
                                SetProp(mo, "InterfaceMetric", metric);
                            }
                            // UpdateOnly: the default Put is "update or create", which this
                            // provider refuses outright («Не поддерживается», measured 2026-11-14);
                            // UpdateOnly is the ModifyInstance Set-NetIPInterface itself sends.
                            mo.Put(new PutOptions { Type = PutType.UpdateOnly });
                            touched++;
                        }
            }
            catch (Exception ex) { return ex.Message; }
            if (touched == 0) return "карта не найдена";

            // Persist it the way the adapter's own IPv4/IPv6 → Advanced dialog does: the
            // ModifyInstance above changes the live value only — the registry still said 10 / 500
            // after it, and the old order would have come back with the next reboot (2026-11-14).
            // 0 = automatic.
            try
            {
                var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Name, alias, StringComparison.OrdinalIgnoreCase));
                if (nic != null)
                    foreach (var svc in new[] { "Tcpip", "Tcpip6" })
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Services\" + svc + @"\Parameters\Interfaces\" + nic.Id, true))
                            k?.SetValue("InterfaceMetric", (int)metric, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch (Exception ex) { return "реестр: " + ex.Message; }

            // The gateway's own metric back to automatic (0, what DHCP gives it): Windows adds it
            // to the interface metric, and one set by hand (500 on the author's Ethernet) outweighs
            // any interface order.
            try
            {
                var wql = "SELECT * FROM MSFT_NetRoute WHERE InterfaceAlias='"
                          + alias.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
                using (var s = new ManagementObjectSearcher(Ns, wql))
                using (var all = s.Get())
                    foreach (ManagementObject mo in all)
                        using (mo)
                        {
                            var prefix = mo["DestinationPrefix"] as string ?? "";
                            if (prefix != "0.0.0.0/0" && prefix != "::/0") continue;
                            if (Convert.ToUInt32(mo["RouteMetric"] ?? 0) == 0) continue;
                            SetProp(mo, "RouteMetric", 0);
                            mo.Put(new PutOptions { Type = PutType.UpdateOnly });
                        }
            }
            catch (Exception ex) { return "шлюз: " + ex.Message; }
            return null;
        }

        // The CIM types differ (AutomaticMetric is an 8-bit enum, InterfaceMetric 32-bit) and WMI
        // refuses a value of the wrong width.
        private static void SetProp(ManagementObject mo, string name, long value)
        {
            var p = mo.Properties[name];
            switch (p.Type)
            {
                case CimType.UInt8: mo[name] = (byte)value; break;
                case CimType.UInt16: mo[name] = (ushort)value; break;
                case CimType.SInt32: mo[name] = (int)value; break;
                case CimType.UInt64: mo[name] = (ulong)value; break;
                default: mo[name] = (uint)value; break;
            }
        }
    }
}
