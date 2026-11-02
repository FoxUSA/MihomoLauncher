using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // How far the firewall kill-switch goes.
    public enum FirewallMode
    {
        Off = 0,        // no firewall rules at all — protection lives only inside the engine
        Programs = 1,   // every program of a kill-switch rule may talk ONLY through the tunnel
    }

    // The kill-switch that does NOT depend on the engine being alive.
    //
    // Everything the launcher did until now was a REJECT rule INSIDE mihomo, which means the
    // protection exists exactly as long as the engine process does. Every path that stops it —
    // a crash, our own restart-when-two-engines, an engine repair, a reinstall, the user closing
    // the launcher, a Windows update killing the task — opens a window in which the protected
    // programs reach the internet directly, with the real IP on the wire, and nothing in the UI
    // says so. That is not a kill-switch; it is a routing preference.
    //
    // How everyone else does it: Mullvad ("Lockdown mode") and Proton ("advanced kill switch")
    // install filters in the WFP kernel layer that keep filtering when the application is not
    // running at all. On Windows the reachable equivalent of WFP from user space is the Windows
    // Firewall (its rules ARE WFP filters): they survive a reboot, a crashed app and an app that
    // was never started.
    //
    // THE CONDITION WE FILTER ON — this is the whole trick:
    //
    //   "block this program's traffic whenever its packets are NOT leaving through the tunnel"
    //
    // expressed as: LOCAL address is outside the tunnel's own subnet AND REMOTE address is a
    // public one. While mihomo's TUN is up and carries the routes, every socket of every program
    // gets 198.18.0.1 as its source address, so the rule never matches and nothing is disturbed.
    // The moment the tunnel is gone — engine dead, routes stripped by a sleep/resume, another VPN
    // client taking the default route — the source address becomes the physical adapter's and the
    // same rule blocks the packet.
    //
    // That formulation was chosen over the obvious "block on every adapter except the tunnel"
    // because it needs no list of adapters: an adapter that appears later (a phone tethering, a
    // USB Wi-Fi stick, a second VPN client's tun) is covered automatically, with nothing to
    // refresh and no window in which a brand-new interface is unprotected.
    //
    // What is deliberately NOT blocked: the whole local network (a rule that cuts the LAN would
    // cut file shares, the printer, the router's web page and — the one that must never break —
    // a Remote Desktop session), the loopback, multicast/link-local, the fake-ip range itself,
    // and the address of any live RDP peer. See ExemptRemote.
    //
    // Scope: the programs listed in rules that have BOTH «через VPN» and the kill-switch on.
    // Sites/templates cannot be expressed here (Windows Firewall filters addresses, not domain
    // names, and under fake-ip we don't even know the real address) — for a site rule the
    // protection is what it always was, the engine's REJECT, unless the user also lists the
    // program that opens it, which is the standard advice for per-app VPN products.
    public static class FirewallGuard
    {
        // Every rule we create carries this group, and nothing else on the machine does — it is
        // how we find our own rules again, including ones left by a previous version.
        public const string Group = "MihomoLauncher";

        // Marker written into each rule's description: which plan produced it. Lets the launcher
        // (which cannot create rules — that needs admin) read back what is actually applied from
        // the firewall itself rather than trusting a file we wrote.
        private const string SigPrefix = "MihomoLauncher plan=";

        // The tunnel's own address space. mihomo puts 198.18.0.1/30 on the TUN and the fake-ip
        // pool is 198.18.0.0/16 (MihomoConfigBuilder), so a packet whose source is in here is
        // by definition one the engine is carrying.
        private const string TunnelSpace = "198.18.0.0/16";

        // Never blocked, whatever happens. Local networks (LAN shares, printers, the router),
        // loopback, link-local, CGNAT (some ISPs put the whole subscriber behind it — and an RDP
        // peer can live there), multicast and the reserved top of the space. Plus the fake-ip
        // range: those addresses aren't routable anywhere, so allowing them can't leak, and
        // blocking them could break a connection the engine is in the middle of handling.
        private static readonly string[] ExemptRemoteBase =
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.168.0.0/16", "198.18.0.0/16", "224.0.0.0/4", "240.0.0.0/4",
        };

        // IPv6: we block the globally routable range only. Everything local (::1, fe80::/10 link
        // local, fc00::/7 ULA) is left alone for the same reasons as above.
        private const string V6Global = "2000::/3";

        // ---------- what should be applied ----------

        public sealed class ProgramRef
        {
            public string Label = "";   // rule name + exe, for the firewall UI and our log
            public string Path = "";
        }

        public sealed class Plan
        {
            public FirewallMode Mode = FirewallMode.Off;
            public string LocalV4 = "";      // source addresses that are NOT the tunnel
            public string RemoteV4 = "";     // destinations worth blocking (public IPv4)
            public string RemoteV6 = "";     // destinations worth blocking (public IPv6)
            public List<ProgramRef> Programs = new List<ProgramRef>();
            public string Signature = "";

            public bool IsEmpty { get { return Mode == FirewallMode.Off || Programs.Count == 0; } }
        }

        // Build the plan for this config. `tunAddresses` are the addresses the LIVE tunnel
        // interface carries, if they could be read — normally inside 198.18.0.0/16, in which case
        // they change nothing. They are taken into account only when they fall outside it, so an
        // engine that is currently down cannot make the plan (and therefore the applied rules)
        // flap between two shapes.
        public static Plan Build(AppConfig cfg, IEnumerable<string> tunAddresses = null)
        {
            var plan = new Plan();
            if (cfg == null) return plan;
            plan.Mode = cfg.FirewallKillSwitch ? FirewallMode.Programs : FirewallMode.Off;
            if (plan.Mode == FirewallMode.Off) { plan.Signature = ""; return plan; }

            // Programs of rules that asked for BOTH the VPN and the kill-switch — switched on or
            // not: a kill-switch rule never goes out without the tunnel. A rule that deliberately
            // fails open (kill-switch off) keeps failing open here too.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in (cfg.Apps ?? new List<AppEntry>())
                         .Where(a => a != null && a.RequiresVpn && a.KillSwitch))
            {
                foreach (var prog in app.Programs ?? new List<RuleProgram>())
                {
                    var path = (prog?.Path ?? "").Trim();
                    if (path.Length == 0) continue;
                    if (IsOursOrEngine(cfg, path)) continue;    // never wall off ourselves
                    if (!seen.Add(path)) continue;
                    plan.Programs.Add(new ProgramRef
                    {
                        Label = app.Name + " · " + SafeFileName(path),
                        Path = path,
                    });
                }
            }
            if (plan.Programs.Count == 0) { plan.Signature = ""; return plan; }

            // Source addresses that are not the tunnel.
            var tunnel = new List<string> { TunnelSpace };
            foreach (var a in tunAddresses ?? Enumerable.Empty<string>())
            {
                IPAddress ip;
                if (!IPAddress.TryParse((a ?? "").Trim(), out ip)) continue;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (InTunnelSpace(ip)) continue;                 // already covered
                tunnel.Add(ip + "/32");
            }
            plan.LocalV4 = ComplementText(tunnel, AddressFamily.InterNetwork);

            // Destinations. Everything public, minus what must never be cut.
            var exempt = new List<string>(ExemptRemoteBase);
            var peers6 = new List<string>();
            foreach (var peer in LivePeers())
            {
                if (peer.IndexOf(':') >= 0) peers6.Add(peer);
                else exempt.Add(peer);
            }
            plan.RemoteV4 = ComplementText(exempt, AddressFamily.InterNetwork);
            plan.RemoteV6 = V6RemoteText(peers6);

            plan.Signature = Sign(plan);
            return plan;
        }

        // A live Remote Desktop session must survive everything this class does. The peer's
        // address is excluded from the block exactly as it is excluded from the TUN route table
        // (MihomoConfigBuilder.TunRouteExcludes) — the difference being that this exclusion holds
        // even when the engine is not running, which is precisely when a remote session used to
        // be at the mercy of whatever else was going on.
        private static List<string> LivePeers()
        {
            try
            {
                return (RdpClients.ActivePeerCidrs() ?? new List<string>())
                    .Select(p => (p ?? "").Trim())
                    .Where(p => p.Length > 0)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        private static bool IsOursOrEngine(AppConfig cfg, string path)
        {
            try
            {
                var file = Path.GetFileName(path);
                if (string.Equals(file, "MihomoLauncher.exe", StringComparison.OrdinalIgnoreCase)) return true;
                var engine = (cfg?.MihomoExePath ?? "").Trim();
                if (engine.Length > 0 &&
                    string.Equals(Path.GetFileName(engine), file, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); } catch { return path; }
        }

        private static bool InTunnelSpace(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 && b[0] == 198 && b[1] == 18;
        }

        // ---------- state file (launcher writes, the elevated helper reads) ----------
        //
        // The helper runs as SYSTEM and does no thinking of its own: it applies exactly what is
        // in this file. Everything that requires knowing the config, the rules or the network is
        // computed here, in the user's own process, where it can be logged and inspected.

        public static string StatePath
        {
            get { return Path.Combine(VersionInfo.DataUserDir, "firewall.state"); }
        }

        public static void WriteState(Plan plan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# MihomoLauncher firewall plan — generated, do not edit");
            sb.AppendLine("v=1");
            sb.AppendLine("mode=" + (plan.Mode == FirewallMode.Programs ? "programs" : "off"));
            sb.AppendLine("sig=" + plan.Signature);
            sb.AppendLine("local4=" + plan.LocalV4);
            sb.AppendLine("remote4=" + plan.RemoteV4);
            sb.AppendLine("remote6=" + plan.RemoteV6);
            foreach (var p in plan.Programs)
                sb.AppendLine("prog=" + p.Label.Replace("|", "/") + "|" + p.Path);

            Directory.CreateDirectory(VersionInfo.DataUserDir);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(StatePath)) File.Delete(StatePath);
            File.Move(tmp, StatePath);
        }

        public static Plan ReadState()
        {
            var plan = new Plan();
            if (!File.Exists(StatePath)) return plan;
            foreach (var raw in File.ReadAllLines(StatePath))
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "mode": plan.Mode = val == "programs" ? FirewallMode.Programs : FirewallMode.Off; break;
                    case "sig": plan.Signature = val; break;
                    case "local4": plan.LocalV4 = val; break;
                    case "remote4": plan.RemoteV4 = val; break;
                    case "remote6": plan.RemoteV6 = val; break;
                    case "prog":
                        int bar = val.IndexOf('|');
                        if (bar <= 0) break;
                        plan.Programs.Add(new ProgramRef
                        {
                            Label = val.Substring(0, bar),
                            Path = val.Substring(bar + 1),
                        });
                        break;
                }
            }
            return plan;
        }

        private static string Sign(Plan plan)
        {
            var sb = new StringBuilder();
            sb.Append((int)plan.Mode).Append('\n')
              .Append(plan.LocalV4).Append('\n')
              .Append(plan.RemoteV4).Append('\n')
              .Append(plan.RemoteV6).Append('\n');
            foreach (var p in plan.Programs.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
                sb.Append(p.Path.ToLowerInvariant()).Append('\n');
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2"));
                return hex.ToString();
            }
        }

        // ---------- reading what IS applied (no admin needed) ----------

        // The plan signature carried by the rules currently in the firewall, "" if there are
        // none of ours. Mixed signatures (a half-finished sync) also read as "" so the caller
        // re-applies rather than believing a stale state.
        public static string AppliedSignature()
        {
            string sig = null;
            int count = 0;
            try
            {
                dynamic policy = NewPolicy();
                if (policy == null) return "";
                foreach (dynamic rule in policy.Rules)
                {
                    string group;
                    string desc;
                    try { group = (string)rule.Grouping; desc = (string)rule.Description; }
                    catch { continue; }
                    if (!string.Equals(group, Group, StringComparison.Ordinal)) continue;
                    count++;
                    var s = SigOf(desc);
                    if (sig == null) sig = s;
                    else if (sig != s) return "";
                }
            }
            catch (Exception ex) { try { Log.Error("firewall-read", ex); } catch { } return ""; }
            return count > 0 ? (sig ?? "") : "";
        }

        public static int AppliedRuleCount()
        {
            int count = 0;
            try
            {
                dynamic policy = NewPolicy();
                if (policy == null) return 0;
                foreach (dynamic rule in policy.Rules)
                {
                    try { if (string.Equals((string)rule.Grouping, Group, StringComparison.Ordinal)) count++; }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        private static string SigOf(string description)
        {
            var d = description ?? "";
            int i = d.IndexOf(SigPrefix, StringComparison.Ordinal);
            if (i < 0) return "";
            var rest = d.Substring(i + SigPrefix.Length);
            int sp = rest.IndexOf(' ');
            return (sp > 0 ? rest.Substring(0, sp) : rest).Trim();
        }

        // ---------- applying (the elevated helper: needs admin) ----------

        // Bring the firewall in line with the state file. Adds what is missing FIRST and removes
        // the previous generation afterwards, so a re-sync never opens a moment in which a
        // protected program is unprotected. Returns the number of rules that now exist.
        public static int ApplyFromState()
        {
            var plan = ReadState();
            return Apply(plan);
        }

        public static int Apply(Plan plan)
        {
            dynamic policy = NewPolicy();
            if (policy == null) { Log.Info("Брандмауэр: интерфейс Windows недоступен."); return -1; }

            var wanted = new List<string>();      // names we are (re)creating
            if (!plan.IsEmpty)
            {
                int n = 0;
                foreach (var prog in plan.Programs)
                {
                    n++;
                    if (!string.IsNullOrEmpty(plan.RemoteV4))
                        wanted.Add(AddRule(policy, plan, n, "v4", prog, plan.LocalV4, plan.RemoteV4));
                    if (!string.IsNullOrEmpty(plan.RemoteV6))
                        wanted.Add(AddRule(policy, plan, n, "v6", prog, "*", plan.RemoteV6));
                }
            }

            // Everything of ours that is not part of this generation goes.
            var stale = new List<string>();
            try
            {
                foreach (dynamic rule in policy.Rules)
                {
                    string group, name, desc;
                    try { group = (string)rule.Grouping; name = (string)rule.Name; desc = (string)rule.Description; }
                    catch { continue; }
                    if (!string.Equals(group, Group, StringComparison.Ordinal)) continue;
                    if (plan.IsEmpty || SigOf(desc) != plan.Signature) stale.Add(name);
                }
            }
            catch (Exception ex) { Log.Error("firewall-scan", ex); }

            foreach (var name in stale)
            {
                try { policy.Rules.Remove(name); }
                catch (Exception ex) { Log.Error("firewall-remove:" + name, ex); }
            }

            int live = wanted.Count(w => w != null);
            Log.Info("Брандмауэр: правил создано " + live + ", убрано старых " + stale.Count
                     + (plan.IsEmpty ? " (защита выключена)." : " (план " + plan.Signature + ")."));
            return live;
        }

        // Remove every rule of ours, whatever generation. Used by the uninstaller and when the
        // user switches the feature off.
        public static int ClearAll()
        {
            return Apply(new Plan());
        }

        // Exit path: how long to wait for the helper to report the rules gone. Deliberately
        // short — this runs from OnExit, and a launcher that hangs on the way out is its own bug.
        private const int ReleaseWaitSeconds = 6;

        // Take the kill-switch down on the way out.
        //
        // Each rule blocks its program from everything EXCEPT the tunnel's own address space.
        // That is the whole point while the engine is up — and a trap the moment it isn't: with
        // mihomo stopped and the TUN interface gone there is nothing on the far side of that
        // exemption, so rules left behind do not protect the program, they take it off the
        // network completely, and it stays off until the launcher is started again. Closing the
        // launcher is an explicit "stop doing this", and EngineShutdown already restores the
        // machine's networking on the very same path.
        //
        // Goes through the same elevated helper as everything else here (this process has no
        // rights to remove a firewall rule): an empty plan in the state file means "remove
        // everything", which is exactly what ApplyFromState does with it.
        public static bool ReleaseOnExit()
        {
            try
            {
                if (AppliedRuleCount() <= 0) return true;                   // nothing of ours is live
                if (!MihomoSetup.IsFirewallTaskInstalled()) return false;   // cannot, and cannot ask

                WriteState(new Plan());
                if (!MihomoSetup.RunFirewallTask()) return false;

                // The helper is its own process and would finish even without this wait; the wait
                // is what lets the log say whether the machine was actually left clean.
                var deadline = DateTime.UtcNow.AddSeconds(ReleaseWaitSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(250);
                    if (AppliedRuleCount() <= 0)
                    {
                        Log.Info("Брандмауэр: защита снята при выходе.");
                        return true;
                    }
                }
                Log.Info("Брандмауэр: снять защиту при выходе не удалось — "
                         + "правила останутся до следующего запуска.");
                return false;
            }
            catch (Exception ex) { try { Log.Error("firewall-release", ex); } catch { } return false; }
        }

        private static string AddRule(dynamic policy, Plan plan, int index, string family,
                                      ProgramRef prog, string local, string remote)
        {
            // The signature is part of the NAME as well as the description: two generations of
            // rules coexist for the moment between "add the new" and "remove the old", and a
            // duplicate name would make that fail.
            var name = "MihomoLauncher #" + index.ToString("00") + " " + family + " " + prog.Label
                     + " [" + plan.Signature + "]";
            try
            {
                dynamic rule = NewRule();
                if (rule == null) return null;
                rule.Name = name;
                rule.Description = SigPrefix + plan.Signature
                    + "  — блокировка, когда трафик идёт мимо туннеля (кил-свитч MihomoLauncher)";
                rule.Grouping = Group;
                rule.ApplicationName = prog.Path;
                rule.Direction = 2;              // NET_FW_RULE_DIR_OUT
                rule.Action = 0;                 // NET_FW_ACTION_BLOCK
                rule.Protocol = 256;             // any
                rule.Profiles = 0x7FFFFFFF;      // domain + private + public
                rule.LocalAddresses = local;
                rule.RemoteAddresses = remote;
                rule.Enabled = true;
                policy.Rules.Add(rule);
                return name;
            }
            catch (Exception ex)
            {
                Log.Error("firewall-add:" + prog.Path, ex);
                return null;
            }
        }

        private static object NewPolicy()
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                return t == null ? null : Activator.CreateInstance(t);
            }
            catch (Exception ex) { try { Log.Error("firewall-policy", ex); } catch { } return null; }
        }

        private static object NewRule()
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FWRule");
                return t == null ? null : Activator.CreateInstance(t);
            }
            catch (Exception ex) { try { Log.Error("firewall-rule", ex); } catch { } return null; }
        }

        // ---------- keeping the firewall in step (the launcher side, no admin) ----------

        // The addresses the live tunnel interface carries right now, so a tunnel that ever ends
        // up outside 198.18.0.0/16 is still recognised as the tunnel. `device` is the adapter
        // name the running engine reports; empty/unknown simply yields nothing.
        public static List<string> TunAddresses(string device)
        {
            var list = new List<string>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    bool named = !string.IsNullOrEmpty(device) &&
                                 string.Equals(nic.Name, device, StringComparison.OrdinalIgnoreCase);
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); }
                    catch { continue; }

                    var addrs = props.UnicastAddresses
                        .Select(u => u.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .ToList();
                    if (!named && !addrs.Any(InTunnelSpace)) continue;
                    foreach (var a in addrs) list.Add(a.ToString());
                }
            }
            catch (Exception ex) { try { Log.Error("firewall-tun-addrs", ex); } catch { } }
            return list;
        }

        private static readonly object _syncLock = new object();
        private static string _lastFailedSig = "";

        // Throttles. The watchdog calls this every twenty seconds for the whole session, so the
        // "nothing changed" path must be nearly free: re-reading every firewall rule on the
        // machine that often is wasteful, and asking the scheduler whether a task exists spawns a
        // process each time — on the machines where it ISN'T installed (every machine, until the
        // engine is reinstalled once) that would be a spawn every twenty seconds, forever.
        private static string _verifiedSig = null;
        private static DateTime _verifiedAtUtc = DateTime.MinValue;
        private static DateTime _taskMissingUntilUtc = DateTime.MinValue;
        private static readonly TimeSpan ReverifyAfter = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan RecheckTaskAfter = TimeSpan.FromMinutes(10);

        // What the launcher calls: work out what should be applied, and if that isn't what IS
        // applied, hand the plan to the elevated helper. Cheap and safe to call often — on a
        // machine already in step it enumerates the firewall rules and returns.
        //
        // Returns true when the firewall now matches the plan.
        public static bool SyncIfNeeded(AppConfig cfg, string tunDevice = null)
        {
            lock (_syncLock)
            {
                try
                {
                    var plan = Build(cfg, TunAddresses(tunDevice));
                    var want = plan.IsEmpty ? "" : plan.Signature;

                    // Confirmed in place recently and the plan hasn't moved → nothing to do.
                    // A re-verification still happens every ReverifyAfter, so rules removed by
                    // hand (or by another tool) are put back within a minute.
                    if (want == _verifiedSig && DateTime.UtcNow - _verifiedAtUtc < ReverifyAfter)
                        return true;

                    var applied = AppliedSignature();
                    if (string.Equals(applied, want, StringComparison.Ordinal))
                    {
                        _lastFailedSig = "";
                        _verifiedSig = want;
                        _verifiedAtUtc = DateTime.UtcNow;
                        return true;
                    }
                    _verifiedSig = null;

                    // Without the helper task there is nothing we can do: creating a firewall
                    // rule needs administrator rights, which this process deliberately does not
                    // have. Say it once per plan instead of every twenty seconds.
                    if (DateTime.UtcNow < _taskMissingUntilUtc) return false;
                    if (!MihomoSetup.IsFirewallTaskInstalled())
                    {
                        _taskMissingUntilUtc = DateTime.UtcNow + RecheckTaskAfter;
                        if (_lastFailedSig != want)
                        {
                            _lastFailedSig = want;
                            Log.Info("Брандмауэр: защита не установлена — нужна кнопка «Переустановить движок» "
                                     + "(она регистрирует задачу " + MihomoSetup.FirewallTaskName + ").");
                        }
                        return false;
                    }

                    WriteState(plan);
                    if (!MihomoSetup.RunFirewallTask()) return false;

                    // The scheduler returns as soon as it has accepted the request; the helper
                    // has to start, apply and exit.
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        System.Threading.Thread.Sleep(400);
                        if (string.Equals(AppliedSignature(), want, StringComparison.Ordinal))
                        {
                            _lastFailedSig = "";
                            _verifiedSig = want;
                            _verifiedAtUtc = DateTime.UtcNow;
                            _taskMissingUntilUtc = DateTime.MinValue;
                            Log.Info(plan.IsEmpty
                                ? "Брандмауэр: защита снята."
                                : "Брандмауэр: защита включена для " + plan.Programs.Count + " программ.");
                            return true;
                        }
                    }
                    if (_lastFailedSig != want)
                    {
                        _lastFailedSig = want;
                        Log.Info("Брандмауэр: правила применить не удалось (задача "
                                 + MihomoSetup.FirewallTaskName + " не отчиталась).");
                    }
                    return false;
                }
                catch (Exception ex) { try { Log.Error("firewall-sync", ex); } catch { } return false; }
            }
        }

        // Same, off the UI thread — the enumeration and the wait both block.
        public static System.Threading.Tasks.Task<bool> SyncAsync(AppConfig cfg, string tunDevice = null)
        {
            return System.Threading.Tasks.Task.Run(() => SyncIfNeeded(cfg, tunDevice));
        }

        // ---------- address arithmetic ----------
        //
        // Windows Firewall can say "these addresses", never "all except these", so the exclusion
        // list has to be turned into the ranges that remain. Done exactly, on integers, rather
        // than with a hand-written list of CIDRs that would have to be re-derived by eye every
        // time an exclusion is added.

        private struct Span
        {
            public BigInteger Lo, Hi;
            public Span(BigInteger lo, BigInteger hi) { Lo = lo; Hi = hi; }
        }

        private static string ComplementText(IEnumerable<string> excluded, AddressFamily family)
        {
            int bits = family == AddressFamily.InterNetwork ? 32 : 128;
            var universe = new Span(0, (BigInteger.One << bits) - 1);
            return RangesText(Complement(Parse(excluded, family), universe), family);
        }

        // IPv6 has no tunnel to compare against (our TUN carries no IPv6 at all), so the rule is
        // simply "no global IPv6 for a protected program" — which is also the only way to close
        // the leak an ISP with working IPv6 would otherwise hand us. Local IPv6 is untouched.
        private static string V6RemoteText(List<string> exemptPeers)
        {
            var global = Parse(new[] { V6Global }, AddressFamily.InterNetworkV6);
            if (global.Count == 0) return "";
            var universe = global[0];
            var peers = Parse(exemptPeers, AddressFamily.InterNetworkV6)
                .Where(p => p.Hi >= universe.Lo && p.Lo <= universe.Hi).ToList();
            if (peers.Count == 0) return V6Global;
            return RangesText(Complement(peers, universe), AddressFamily.InterNetworkV6);
        }

        private static List<Span> Parse(IEnumerable<string> items, AddressFamily family)
        {
            var spans = new List<Span>();
            foreach (var raw in items ?? Enumerable.Empty<string>())
            {
                var item = (raw ?? "").Trim();
                if (item.Length == 0) continue;
                int slash = item.IndexOf('/');
                var addr = slash > 0 ? item.Substring(0, slash) : item;
                IPAddress ip;
                if (!IPAddress.TryParse(addr, out ip)) continue;
                if (ip.AddressFamily != family) continue;

                int bits = family == AddressFamily.InterNetwork ? 32 : 128;
                int prefix = bits;
                if (slash > 0 && !int.TryParse(item.Substring(slash + 1), NumberStyles.Integer,
                                               CultureInfo.InvariantCulture, out prefix))
                    continue;
                if (prefix < 0 || prefix > bits) continue;

                var value = ToNumber(ip);
                var mask = prefix == 0 ? BigInteger.Zero
                                       : ((BigInteger.One << prefix) - 1) << (bits - prefix);
                var lo = value & mask;
                var hi = lo | (((BigInteger.One << bits) - 1) ^ mask);
                spans.Add(new Span(lo, hi));
            }
            return spans;
        }

        private static List<Span> Complement(List<Span> spans, Span universe)
        {
            var result = new List<Span>();
            var sorted = spans.Where(s => s.Hi >= universe.Lo && s.Lo <= universe.Hi)
                              .OrderBy(s => s.Lo).ToList();
            var cursor = universe.Lo;
            foreach (var s in sorted)
            {
                var lo = s.Lo < universe.Lo ? universe.Lo : s.Lo;
                var hi = s.Hi > universe.Hi ? universe.Hi : s.Hi;
                if (lo > cursor) result.Add(new Span(cursor, lo - 1));
                if (hi + 1 > cursor) cursor = hi + 1;
            }
            if (cursor <= universe.Hi) result.Add(new Span(cursor, universe.Hi));
            return result;
        }

        private static string RangesText(List<Span> spans, AddressFamily family)
        {
            var parts = new List<string>();
            foreach (var s in spans)
            {
                parts.Add(s.Lo == s.Hi
                    ? ToAddress(s.Lo, family).ToString()
                    : ToAddress(s.Lo, family) + "-" + ToAddress(s.Hi, family));
            }
            return string.Join(",", parts);
        }

        private static BigInteger ToNumber(IPAddress ip)
        {
            BigInteger v = BigInteger.Zero;
            foreach (var b in ip.GetAddressBytes()) v = (v << 8) | b;
            return v;
        }

        private static IPAddress ToAddress(BigInteger value, AddressFamily family)
        {
            int len = family == AddressFamily.InterNetwork ? 4 : 16;
            var bytes = new byte[len];
            for (int i = len - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
            return new IPAddress(bytes);
        }
    }
}
