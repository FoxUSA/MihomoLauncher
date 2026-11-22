using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The collecting half of the DNS window's «Статистика» (the view is SettingsWindow.DnsStats,
    // the store Services/DnsStats). Lives as long as the launcher: every verdict the engine
    // reports is counted whether or not any window is open.
    public partial class MainWindow
    {
        // host → its IPv4 from the engine's resolver answers (direct traffic only — a VPN tile's
        // name is looked up at the far end and never shows up here). Bounded; a hint, not state.
        private readonly Dictionary<string, string> _statsHostIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string OwnExe = SafeOwnExe();

        private static string SafeOwnExe()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe"; } catch { return VersionInfo.ExeName; }
        }

        private void StartDnsStats()
        {
            EngineLogStore.Instance.EntrySeen += OnStatsEntry;
            EngineLogStore.Instance.DnsAnswerSeen += OnStatsAnswer;
            try { Application.Current.Exit += (s, e) => DnsStats.SaveAndWait(); } catch { }
        }

        private void OnStatsAnswer(string host, string ip)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(ip) || IsPrivate(ip) || ip == "0.0.0.0") return;
            if (_statsHostIp.Count > 8192) _statsHostIp.Clear();
            _statsHostIp[host] = ip;
        }

        private void OnStatsEntry(EngineLogEntry e)
        {
            if (e == null || (e.Kind != EngineLogKind.Allowed && e.Kind != EngineLogKind.Blocked && e.Kind != EngineLogKind.Failed)) return;
            var host = (e.Host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            // No name — no DNS: torrents, games, P2P dial bare addresses.
            if (host.Length == 0 || host.Contains(":") || System.Net.IPAddress.TryParse(host, out _)) return;
            // The launcher's own checks and the engine's own traffic are not the user's.
            var proc = e.Process ?? "";
            if (proc.Equals(OwnExe, StringComparison.OrdinalIgnoreCase) || proc.StartsWith("mihomo", StringComparison.OrdinalIgnoreCase)) return;

            var detail = e.Detail ?? "";
            DnsStats.Kind kind;
            if (e.Kind == EngineLogKind.Blocked) kind = DnsStats.Kind.Rule;
            else if (e.Kind == EngineLogKind.Failed && (detail.Contains("0.0.0.0") || detail.Contains("[::]"))) kind = DnsStats.Kind.Stub;
            else if (e.Kind == EngineLogKind.Failed && detail.IndexOf("dns", StringComparison.OrdinalIgnoreCase) >= 0) kind = DnsStats.Kind.NoAddress;
            else kind = DnsStats.Kind.Allowed;

            // The tile, the same way the speed plates find it (sites of all tiles, then programs, then MATCH).
            if (_speedMapDirty || _speedSiteRules == null) BuildSpeedMap();
            var tile = TileOfConnection(new MihomoController.ConnStat
            {
                Rule = e.RuleType ?? "",
                Payload = e.RulePayload ?? "",
                Process = proc,
                Host = host,
                SniffHost = "",
            }) ?? "";

            // Country and network owner: only where the site's own address is known — a name sent
            // DIRECT, resolved here. Through a VPN the far end looks it up; blocked names have none.
            var target = (e.Target ?? "").Trim();
            bool direct = target.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) || MihomoConfigBuilder.IsNicTarget(target);
            string ip = null, cc = "", ccName = "";
            if (direct && kind == DnsStats.Kind.Allowed) _statsHostIp.TryGetValue(host, out ip);
            if (!string.IsNullOrEmpty(ip))
            {
                var g = GeoIpService.Lookup(ip);
                if (g != null && !string.IsNullOrEmpty(g.IsoCode)) { cc = g.IsoCode; ccName = g.Country ?? g.IsoCode; }
            }
            var company = DnsStats.CompanyOfHost(host);
            if (company == null && !string.IsNullOrEmpty(ip)) company = DnsStats.CompanyOfAsn(GeoIpService.OrgOf(ip, AsnPath));
            company = company ?? "";

            // «No address» through DIRECT: a DNS filter or a name that isn't there? The log can't
            // tell — the same server is asked again (DnsVerdict) and the count waits for the answer.
            // A VPN tile's name is looked up at the far end, out of reach: stays «no address».
            if (kind == DnsStats.Kind.NoAddress && direct)
            {
                DnsStats.Kind known;
                if (!StatsVerdictOf(host, out known))
                {
                    List<Action<DnsStats.Kind>> waiting;
                    if (!_statsPending.TryGetValue(host, out waiting))
                    {
                        if (_statsPending.Count >= 256) { DnsStats.Record(tile, host, kind, cc, ccName, company); return; }
                        _statsPending[host] = waiting = new List<Action<DnsStats.Kind>>();
                    }
                    if (waiting.Count < 2000) waiting.Add(k => DnsStats.Record(tile, host, k, cc, ccName, company));
                    RunStatsProbes();
                    return;
                }
                kind = known;
            }

            DnsStats.Record(tile, host, kind, cc, ccName, company);
        }

        // ---------- «no address»: filter or not (DnsVerdict) ----------

        private sealed class StatsVerdict { public DnsStats.Kind Kind; public DateTime Until; }
        private readonly Dictionary<string, StatsVerdict> _statsVerdicts = new Dictionary<string, StatsVerdict>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action<DnsStats.Kind>>> _statsPending = new Dictionary<string, List<Action<DnsStats.Kind>>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _statsReclassified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _statsProbing;

        private bool StatsVerdictOf(string host, out DnsStats.Kind kind)
        {
            kind = DnsStats.Kind.NoAddress;
            StatsVerdict v;
            if (!_statsVerdicts.TryGetValue(host, out v) || v.Until < DateTime.UtcNow) return false;
            kind = v.Kind;
            return true;
        }

        // One at a time, on the UI thread between awaits; a burst of failures is asked in one go.
        private async void RunStatsProbes()
        {
            if (_statsProbing) return;
            _statsProbing = true;
            try
            {
                await System.Threading.Tasks.Task.Delay(1500);
                while (_statsPending.Count > 0)
                {
                    var names = _statsPending.Keys.Take(16).ToList();
                    Dictionary<string, DnsVerdict.Result> got;
                    try { got = await DnsVerdict.ProbeAsync(_config, _mihomo, names); }
                    catch (Exception ex) { Log.Error("dns-stats-probe", ex); got = new Dictionary<string, DnsVerdict.Result>(); }

                    if (_statsVerdicts.Count > 4096) _statsVerdicts.Clear();
                    foreach (var n in names)
                    {
                        DnsVerdict.Result r;
                        if (!got.TryGetValue(n, out r)) r = new DnsVerdict.Result();
                        // A verdict holds for hours; «couldn't ask» only for minutes.
                        _statsVerdicts[n] = new StatsVerdict { Kind = r.Kind, Until = DateTime.UtcNow.Add(r.Known ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10)) };
                        if (_statsReclassified.Add(n))
                            Log.Info("DNS-статистика: " + n + " — " + (r.Kind == DnsStats.Kind.Stub ? "фильтр DNS" : "нет адреса") + " (" + r.Why + ")");
                        if (r.Kind == DnsStats.Kind.Stub) DnsStats.Reclassify(n);

                        List<Action<DnsStats.Kind>> waiting;
                        if (_statsPending.TryGetValue(n, out waiting))
                        {
                            _statsPending.Remove(n);
                            foreach (var rec in waiting) { try { rec(r.Kind); } catch (Exception ex) { Log.Error("dns-stats-record", ex); } }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("dns-stats-probes", ex); }
            finally { _statsProbing = false; }
        }

        // A tile's picture, small, for the statistics table: its own icon if sharp, else its monogram.
        internal FrameworkElement TileIconSmall(AppEntry entry, double size)
        {
            ImageSource src = null;
            try
            {
                src = IconLoader.Load(entry.IconPath, null, 64) ?? (entry.IsBuiltIn ? null : IconLoader.Fallback(entry));
                if (src != null && !IconLoader.IsSharp(src)) src = null;
            }
            catch { src = null; }
            if (src != null)
            {
                var img = new Image { Width = size, Height = size, Source = src, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return img;
            }
            var mono = MakeMonogram(TileDisplayName(entry), size);
            mono.Margin = new Thickness(0);
            return mono;
        }

        internal string TileDisplayName(AppEntry entry) => entry.IsAllTraffic ? AllTrafficName() : entry.Name;
    }
}
