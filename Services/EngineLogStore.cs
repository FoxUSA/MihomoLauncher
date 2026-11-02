using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // The collected verdicts, kept for as long as the LAUNCHER runs — not for as long as the
    // log window is open. Closing the window must not throw away what it caught (the whole
    // point is to reopen it and see what happened while you were reproducing the problem),
    // and listening only while it's open would miss everything that broke before you looked.
    // So the feed starts with the app and the window is just a view onto this list.
    public class EngineLogStore
    {
        private static EngineLogStore _instance;
        public static EngineLogStore Instance
        {
            get { return _instance ?? (_instance = new EngineLogStore()); }
        }

        // A busy machine repeats the same verdict thousands of times; rows are deduped, so this
        // caps DISTINCT problems, not events.
        private const int MaxRows = 2000;

        public ObservableCollection<LogRow> Rows { get; } = new ObservableCollection<LogRow>();

        private readonly Dictionary<string, LogRow> _index = new Dictionary<string, LogRow>(StringComparer.Ordinal);

        // host -> the program last seen talking to it. The engine names the process on the
        // "match" line but usually not on the "dial … error" line that follows for the SAME
        // connection, so a failure arrived with an empty «Программа» column — the user then
        // has an unexplained IP and no idea who dialled it. Bounded; it's a hint, not state.
        private const int MaxProcMemory = 512;
        private readonly Dictionary<string, string> _procByHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, EngineLog.RuleHit> _owners = new Dictionary<string, EngineLog.RuleHit>();
        private EngineLog _log;

        public event Action<string> Status;
        public string LastStatus { get; private set; } = "";

        public bool IsRunning { get { return _log != null && _log.IsRunning; } }

        // Start (or restart) the feed for this config. Safe to call again after a settings
        // change — the rule index is rebuilt so new rules are named correctly from then on.
        public async void Start(AppConfig cfg)
        {
            if (cfg == null) return;
            _owners = EngineLog.BuildOwnerIndex(cfg);

            if (_log != null) { _log.Stop(); _log = null; }
            var log = new EngineLog(cfg);
            log.Entry += OnEntry;
            log.Status += s =>
            {
                LastStatus = s ?? "";
                var h = Status; if (h != null) h(LastStatus);
            };
            _log = log;
            try { await log.StartAsync().ConfigureAwait(false); } catch { }
        }

        // Rules were edited: relabel future entries without dropping the feed (or the history).
        public void RefreshRules(AppConfig cfg)
        {
            if (cfg == null) return;
            _owners = EngineLog.BuildOwnerIndex(cfg);
        }

        public void Stop()
        {
            var log = _log;
            _log = null;
            if (log != null) log.Stop();
        }

        public void Clear()
        {
            OnUi(() => { Rows.Clear(); _index.Clear(); });
        }

        // Put a verdict of OUR OWN into the feed.
        //
        // The window reads mihomo's /logs, which can only ever report what the engine saw. The
        // firewall kill-switch blocks the packet before the engine exists in the story at all —
        // so at the exact moment the protection does its job, the log would fall silent and the
        // user would be left with "the program has no internet and nothing says why", which is
        // the state this window was built to abolish. These rows fill that gap.
        public void Note(LogCategory category, string verdict, string why,
                         string process = "", string host = "", string rule = "")
        {
            OnUi(() =>
            {
                var row = new LogRow(category, verdict, why, process, host, rule);
                LogRow existing;
                PersistProblem(row);
                if (_index.TryGetValue(row.Key, out existing)) { existing.Bump(DateTime.Now); return; }
                _index[row.Key] = row;
                Rows.Add(row);
                while (Rows.Count > MaxRows)
                {
                    _index.Remove(Rows[0].Key);
                    Rows.RemoveAt(0);
                }
            });
        }

        private void OnEntry(EngineLogEntry e)
        {
            if (e == null) return;
            OnUi(() =>
            {
                RememberOrRecallProcess(e);
                var owner = EngineLog.FindOwner(_owners, e.RulePayload, e.Host);
                var row = new LogRow(e, owner);
                PersistProblem(row);

                LogRow existing;
                if (_index.TryGetValue(row.Key, out existing)) { existing.Bump(e.Time); return; }

                _index[row.Key] = row;
                Rows.Add(row);
                while (Rows.Count > MaxRows)
                {
                    _index.Remove(Rows[0].Key);
                    Rows.RemoveAt(0);
                }
            });
        }

        // Row key → when it last went to problems.log. A blocked page repeats the same verdict
        // hundreds of times a minute; once per ten minutes still shows when it started and that it
        // was still going on, without the file filling up with copies.
        private readonly Dictionary<string, DateTime> _problemLoggedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private void PersistProblem(LogRow row)
        {
            if (row == null || row.Category != LogCategory.Problem) return;
            DateTime at;
            if (_problemLoggedAt.TryGetValue(row.Key, out at) && DateTime.Now - at < TimeSpan.FromMinutes(10)) return;
            if (_problemLoggedAt.Count > MaxRows) _problemLoggedAt.Clear();
            _problemLoggedAt[row.Key] = DateTime.Now;

            var who = string.IsNullOrEmpty(row.ProcessName) ? "?" : row.ProcessName;
            var rule = string.IsNullOrEmpty(row.RuleText) ? "" : " · правило «" + row.RuleText + "»";
            Log.Problem(row.VerdictText + " · " + who + " → " + row.Host + rule + " · " + row.WhyText);
        }

        // Learn "this host belongs to that program" from every line that names one, and fill the
        // blank on the lines that don't. The engine emits the match (with the program) before
        // the dial error for the same connection, so by the time a failure shows up the answer
        // is usually already here.
        private void RememberOrRecallProcess(EngineLogEntry e)
        {
            if (string.IsNullOrEmpty(e.Host)) return;

            if (!string.IsNullOrEmpty(e.Process))
            {
                if (_procByHost.Count >= MaxProcMemory) _procByHost.Clear();
                _procByHost[e.Host] = e.Process;
                return;
            }

            string proc;
            if (_procByHost.TryGetValue(e.Host, out proc)) e.Process = proc;
        }

        private static void OnUi(Action action)
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
    }

    // Which tab of the log window a row belongs to. The split exists because three very
    // different things used to sit in one list: what the launcher itself did, what the user's
    // ad-filtering DNS did, and failures of programs the launcher never touched.
    public enum LogCategory
    {
        Problem,    // ours: a rule, the kill-switch, a dead node, a lookup that failed
        AdBlock,    // the DNS server's 0.0.0.0 stub — a tracker killed on purpose
        Outside,    // a direct connection under no rule of ours that the far side refused
        Passed,     // routed fine
    }

    // One line of the log. Repeats of the same host+reason fold into it, so Time is the LAST
    // time it happened and Count says how often.
    public class LogRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        // A row the LAUNCHER writes, not the engine — see EngineLogStore.Note.
        public LogRow(LogCategory category, string verdict, string why,
                      string process, string host, string rule)
        {
            Time = DateTime.Now;
            TimeText = Time.ToString("HH:mm:ss");
            Category = category;
            IsProblem = category == LogCategory.Problem;
            VerdictText = verdict ?? "";
            WhyText = why ?? "";
            ProcessName = process ?? "";
            Host = host ?? "";
            RuleText = rule ?? "";
            Key = "own|" + VerdictText + "|" + ProcessName + "|" + Host + "|" + WhyText;
        }

        public LogRow(EngineLogEntry e, EngineLog.RuleHit owner)
        {
            Time = e.Time;
            TimeText = e.Time.ToString("HH:mm:ss");
            Host = e.Host;
            ProcessName = e.Process;

            // A dial to 0.0.0.0 / [::] is the blocklist stub an ad-filtering resolver
            // (NextDNS, AdGuard DNS, a Pi-hole) hands back — the tracker was killed exactly
            // as the user asked for. It is NOT a fault, and counting it as one buried the
            // real failures: a single page can produce sixty of these in a row.
            bool adBlocked = e.Kind == EngineLogKind.Failed &&
                             (e.Detail.Contains("0.0.0.0") || e.Detail.Contains("[::]"));

            // Under TUN the engine carries the WHOLE machine's traffic, not just what our rules
            // claim: anything unmatched falls through to MATCH,DIRECT and the engine still dials
            // it — so a failure of some program that has nothing to do with the launcher (a
            // peer-to-peer attempt, a dead server) landed here looking like the launcher broke
            // it. It is ours to report only if a rule of ours matched, or if the route went
            // through one of our nodes. Everything else is somebody else's connection.
            //
            // Failed LOOKUPS used to be excluded from this and always counted as ours, on the
            // grounds that under TUN every lookup passes through the engine. It passes through,
            // but it is ANSWERED by the machine's own resolver — and the launcher hasn't chosen
            // resolvers since the DNS feature was removed. So a name the router refused to
            // resolve, for a host no rule of ours claims, on a DIRECT route, was landing in
            // «Проблемы» under the words «режет DNS-сервер» and read as the launcher cutting
            // traffic. A lookup failure is ours only when a rule owns the host or the route ran
            // through one of our nodes — both of which are checked below.
            //
            // With «DNS для прямого трафика» on, that changes for DIRECT routes: the resolver that
            // refused is the one the USER chose in the launcher, so it is ours to report, by name —
            // "the site is cut by your DNS, not by the VPN" is exactly what this tab must be able
            // to say.
            var ownDns = MihomoConfigBuilder.LastDirectDns;
            bool ownDnsOn = ownDns != null && ownDns.Count > 0;
            bool directRoute = (e.Target ?? "").Trim().Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
            bool ownDnsFailed = ownDnsOn && directRoute && e.Kind == EngineLogKind.Failed && !adBlocked
                                && e.Detail.IndexOf("dns", StringComparison.OrdinalIgnoreCase) >= 0;
            bool ownDnsStub = ownDnsOn && directRoute && adBlocked;

            bool foreignDirect = e.Kind == EngineLogKind.Failed && !adBlocked && !ownDnsFailed && owner == null
                                 && !IsProxyTarget(e.Target);

            IsProblem = !adBlocked && !foreignDirect &&
                        (e.Kind == EngineLogKind.Blocked || e.Kind == EngineLogKind.Failed);
            Category = adBlocked ? LogCategory.AdBlock
                     : foreignDirect ? LogCategory.Outside
                     : IsProblem ? LogCategory.Problem
                     : LogCategory.Passed;

            if (adBlocked || foreignDirect)
            {
                VerdictText = Loc.T(adBlocked ? "Log_VerdictAdBlock" : "Log_VerdictOutside");
                bool lookupFailed = !adBlocked &&
                                    e.Detail.IndexOf("dns", StringComparison.OrdinalIgnoreCase) >= 0;
                WhyText = ownDnsStub ? Loc.T("Log_WhyOwnDnsStub", string.Join(", ", ownDns))
                        : adBlocked ? Loc.T("Log_WhyDnsStub")
                        : lookupFailed ? Loc.T("Log_WhyForeignDns", e.Detail)   // nothing was dialled
                        : Loc.T("Log_WhyForeignDirect", e.Detail);
                RuleText = owner != null ? owner.RuleName : "";
                Key = ((int)e.Kind) + "|" + Host + "|" + ProcessName + "|" + WhyText;
                return;
            }

            switch (e.Kind)
            {
                case EngineLogKind.Blocked:
                    VerdictText = Loc.T("Log_VerdictBlocked");
                    WhyText = BlockedWhy(e, owner);
                    break;

                case EngineLogKind.Failed:
                    VerdictText = Loc.T("Log_VerdictFailed");
                    // The engine's own error text says who is at fault. "dns resolve failed:
                    // couldn't find ip" means no answer came back at all — from the machine's
                    // resolver for a DIRECT route, or from the tunnel's own resolver when the
                    // route goes through a node whose far end does the lookup. (The 0.0.0.0
                    // stub is handled above, before this switch.)
                    if (ownDnsFailed)
                    {
                        VerdictText = Loc.T("Log_VerdictOwnDns");
                        WhyText = Loc.T("Log_WhyOwnDnsFailed", string.Join(", ", ownDns), e.Detail);
                    }
                    else if (e.Detail.IndexOf("dns", StringComparison.OrdinalIgnoreCase) >= 0)
                        WhyText = IsProxyTarget(e.Target)
                            ? Loc.T("Log_WhyDnsViaNode", e.Target, e.Detail)
                            : Loc.T("Log_WhyDnsBlocked", e.Detail);
                    else
                        WhyText = Loc.T("Log_WhyFailed", e.Target, e.Detail);
                    break;

                case EngineLogKind.Dns:
                    VerdictText = Loc.T("Log_VerdictDns");
                    WhyText = IsOwnServer(e.Detail)
                        ? Loc.T("Log_WhyOwnDnsEmpty", e.Detail)
                        : Loc.T("Log_WhyDnsEmpty", e.Detail);
                    break;

                default:
                    VerdictText = Loc.T("Log_VerdictAllowed");
                    WhyText = owner == null
                        ? Loc.T("Log_WhyAllowedBare", e.Target, MatchText(e))
                        : Loc.T("Log_WhyAllowed", e.Target, owner.RuleName);
                    break;
            }

            RuleText = owner != null ? owner.RuleName : "";
            Key = ((int)e.Kind) + "|" + Host + "|" + ProcessName + "|" + WhyText;
        }

        // Grouping key for the optional per-application collapse. One program hammering fifty
        // different hosts with the same verdict is one fact, not fifty — and while it spams, the
        // single line that matters scrolls away. Falls back to the rule name, then to the
        // verdict alone, so a row with no process still lands somewhere sensible.
        public string AppKey
        {
            get
            {
                var who = !string.IsNullOrEmpty(ProcessName) ? ProcessName
                        : !string.IsNullOrEmpty(RuleText) ? RuleText
                        : "";
                return ((int)Category) + "|" + who + "|" + VerdictText;
            }
        }

        // How many further rows this one stands for while the collapse is on (0 = showing
        // itself only). Set by the window, which owns the grouping.
        public int CollapsedCount
        {
            get { return _collapsed; }
            set
            {
                if (_collapsed == value) return;
                _collapsed = value;
                Raise("CollapsedCount"); Raise("HostText");
            }
        }
        private int _collapsed;

        // What the «Куда» column shows: the host, plus how many others are folded into it.
        public string HostText
        {
            get { return _collapsed > 0 ? Host + "  +" + _collapsed : Host; }
        }

        // The rule, under the program in the «Куда» cell: "· Msedge"; empty when no rule owns it.
        public string RuleLine
        {
            get { return string.IsNullOrEmpty(RuleText) ? "" : "· " + RuleText; }
        }

        // Everything we know about a REJECT, in one sentence: whose rule, which item of that
        // rule claimed it, and — the part that was missing — that it is the kill-switch doing
        // the rejecting, because the rule has no working node right now.
        private static string BlockedWhy(EngineLogEntry e, EngineLog.RuleHit owner)
        {
            if (owner == null) return Loc.T("Log_WhyBlockedBare", MatchText(e));

            string item;
            switch (owner.Kind)
            {
                case EngineLog.RuleItemKind.Template:
                    item = Loc.T("Log_ItemTemplate", owner.ItemLabel, MatchText(e)); break;
                case EngineLog.RuleItemKind.Program:
                    item = Loc.T("Log_ItemProgram", owner.ItemLabel); break;
                case EngineLog.RuleItemKind.CheckUrl:
                    item = Loc.T("Log_ItemCheckUrl", owner.ItemLabel); break;
                default:
                    item = Loc.T("Log_ItemSite", owner.ItemLabel, MatchText(e)); break;
            }

            // A rule whose kill-switch is OFF never reaches REJECT (it falls through to DIRECT),
            // so a REJECT on a known rule IS the kill-switch — say so plainly.
            return owner.KillSwitch
                ? Loc.T("Log_WhyKillSwitch", owner.RuleName, item)
                : Loc.T("Log_WhyBlockedRule", owner.RuleName, item);
        }

        // Was this connection handed to one of OUR VPN nodes? Then a failed lookup happened at
        // the far end of that tunnel, not on this machine's DNS — blaming the local resolver
        // (as every such line used to) sends the user hunting the wrong problem.
        private static bool IsProxyTarget(string target)
        {
            var t = (target ?? "").Trim();
            if (t.Length == 0) return false;
            return !t.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
                && !t.StartsWith("REJECT", StringComparison.OrdinalIgnoreCase)
                && !t.Equals("PASS", StringComparison.OrdinalIgnoreCase)
                && !t.Equals("COMPATIBLE", StringComparison.OrdinalIgnoreCase);
        }

        // "udp://1.1.1.1:53", "https://dns.google:443/dns-query" — the engine's spelling of a
        // server; is it one of the user's own direct-traffic resolvers?
        private static bool IsOwnServer(string server)
        {
            var hosts = MihomoConfigBuilder.LastDirectDnsHosts;
            if (hosts == null || hosts.Count == 0 || string.IsNullOrEmpty(server)) return false;
            var s = server.ToLowerInvariant();
            return hosts.Any(h => !string.IsNullOrEmpty(h) && s.Contains(h.ToLowerInvariant()));
        }

        private static string MatchText(EngineLogEntry e)
        {
            return string.IsNullOrEmpty(e.RulePayload) ? e.RuleType : e.RuleType + "(" + e.RulePayload + ")";
        }

        public void Bump(DateTime when)
        {
            Count++;
            Time = when;
            TimeText = when.ToString("HH:mm:ss");
            // "Time" first: the window sorts live on it, so the row moves to where its new
            // timestamp belongs instead of sitting wherever it first appeared.
            Raise("Time"); Raise("Count"); Raise("CountText"); Raise("TimeText");
        }

        public string Key { get; private set; }
        // Sort key. TimeText is only its HH:mm:ss rendering — a run crossing midnight would
        // sort wrong on the string.
        public DateTime Time { get; private set; }
        public string TimeText { get; private set; }
        public int Count { get; private set; } = 1;
        public string CountText { get { return Count > 1 ? "×" + Count : ""; } }
        public string VerdictText { get; private set; }
        public string RuleText { get; private set; }
        public string Host { get; private set; }
        public string ProcessName { get; private set; }
        public string WhyText { get; private set; }
        public bool IsProblem { get; private set; }
        public LogCategory Category { get; private set; }

        // Colour of the «Итог» pill: red = cut, amber = DNS trouble, grey = not the launcher's
        // business (ads killed, somebody else's connection), green = went through.
        public System.Windows.Media.Brush VerdictBrush
        {
            get
            {
                if (VerdictText == Loc.T("Log_VerdictOwnDns") || VerdictText == Loc.T("Log_VerdictDns")) return Theme.AccentAmber;
                switch (Category)
                {
                    case LogCategory.Problem: return Theme.AccentRed;
                    case LogCategory.AdBlock:
                    case LogCategory.Outside: return Theme.AccentGrey;
                    default: return Theme.AccentGreen;
                }
            }
        }
    }
}
