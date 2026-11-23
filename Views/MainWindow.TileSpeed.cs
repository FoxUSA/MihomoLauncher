using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // The speed plate at the very bottom of a tile (2026-11-16, variant «E» of the mock-up): this
    // tile's current speed — «↓ 15,2 Мбит/с ↑ 0,65» — over a level bar coloured by how fast it is
    // (red < 1 Mbit/s · amber 1–10 · green 10–50 · blue 50+, grey = nothing moving). A click opens
    // the last 24 hours (ShowSpeedHistory). Hidden by Settings → «Сеть».
    //
    // Counted from the engine (GET /connections once a second): every open connection carries the
    // rule that routed it (`rule` + `rulePayload`, e.g. ProcessName chrome.exe / DomainSuffix
    // telegram.org / Match) and the bytes it has moved so far. The rule says which tile it is —
    // exactly the way the engine itself decided — and the difference between two polls is the
    // speed. Only what passes the engine is seen (LAN / RDP are routed past it), payload only
    // (no packet headers / VPN overhead), and a connection that opened and closed between two
    // polls is missed — so a little under the card's own counter, never someone else's traffic.
    //
    // Polled while the window is hidden too (the day's history must not have holes), drawn only
    // while it is seen.
    public partial class MainWindow
    {
        internal const double SpeedPlateHeight = 32;   // as tall as the VPN plate
        private const double LevelBarWidth = 84;

        // Always (2026-11-16: the «Показывать скорость» box went; AppConfig.ShowTileSpeed is ignored).
        private bool ShowTileSpeed => true;
        private double SpeedStrip => ShowTileSpeed ? SpeedPlateHeight + PlateGap : 0;   // + the gap above it

        private sealed class SpeedView { public TextBlock Text; public Border Fill; }

        // Tile Id → its plate's parts, re-filled in place every second (no tile rebuild).
        private readonly Dictionary<string, SpeedView> _speedViews = new Dictionary<string, SpeedView>(StringComparer.Ordinal);
        // Tile Id → smoothed {down, up} bytes per second right now.
        private readonly Dictionary<string, double[]> _speedNow = new Dictionary<string, double[]>(StringComparer.Ordinal);
        // Tile Id → its measured polls of the last 60 s, unsmoothed (2026-11-20): the «Сейчас» tab's
        // cards — peak / average / down / up over the same minute as «кто и куда».
        internal sealed class SpeedSec { public DateTime At; public double Down, Up, Secs; }   // bytes/s, seconds measured
        private readonly Dictionary<string, Queue<SpeedSec>> _speedLastMin = new Dictionary<string, Queue<SpeedSec>>(StringComparer.Ordinal);

        private List<SpeedSec> SpeedLastMinute(string tileId)
        {
            Queue<SpeedSec> q;
            var cut = DateTime.UtcNow.AddSeconds(-60);
            return tileId != null && _speedLastMin.TryGetValue(tileId, out q) ? q.Where(s => s.At > cut).ToList() : new List<SpeedSec>();
        }
        private Dictionary<string, long[]> _connPrev;   // connection id → {up, down} at the last poll
        private DateTime _connPrevAt;
        private DispatcherTimer _speedTimer;
        private bool _speedBusy;

        // Which tile a rule belongs to — rebuilt after every tile redraw (the config may have changed).
        private Dictionary<string, string> _speedSiteRules, _speedOtherRules;
        private string _speedMatchTile;
        private bool _speedMapDirty = true;

        private FrameworkElement MakeSpeedPlate(AppEntry entry)
        {
            EnsureSpeedTimer();
            _speedMapDirty = true;

            var view = new SpeedView
            {
                // Not hit-testable: a press must start at the plate, never at one of its Runs.
                Text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false },
                Fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 },
            };
            _speedViews[entry.Id] = view;

            var track = new Border
            {
                Width = LevelBarWidth,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Theme.SpeedTrack,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = view.Fill,
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(view.Text);
            stack.Children.Add(track);
            RenderSpeed(entry.Id, view);

            var plate = new Border
            {
                Height = SpeedPlateHeight,
                Margin = new Thickness(5, 0, 5, PlateGap),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(7),
                Background = Theme.PlateNeutralBg,
                BorderBrush = Theme.PlateNeutralBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("Main_TipTileSpeed"),
                Child = stack,
            };
            plate.MouseEnter += (s, e) => { plate.Background = Theme.ChipBgHover; plate.BorderBrush = Theme.TileBorderHover; };
            plate.MouseLeave += (s, e) => { plate.Background = Theme.PlateNeutralBg; plate.BorderBrush = Theme.PlateNeutralBorder; };
            plate.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // never the tile's launch
                _ = ShowSpeedHistoryAsync(entry);
            };
            return plate;
        }

        private void EnsureSpeedTimer()
        {
            if (_speedTimer != null) return;
            try { Application.Current.Exit += (s, e) => { SpeedHistory.Save(); WhoArchiveOpenAndFlush(); }; } catch { }
            _speedTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _speedTimer.Tick += async (s, e) =>
            {
                if (_speedBusy) return;
                if (!ShowTileSpeed || _mihomo == null) { _connPrev = null; return; }
                _speedBusy = true;
                try { await PollSpeedAsync().ConfigureAwait(true); }
                catch (Exception ex) { Log.Error("tile-speed", ex); }
                finally { _speedBusy = false; }
            };
            _speedTimer.Start();
        }

        private bool SpeedOnScreen => IsVisible && WindowState != WindowState.Minimized;

        private async System.Threading.Tasks.Task PollSpeedAsync()
        {
            var conns = await _mihomo.GetConnectionStatsAsync().ConfigureAwait(true);
            var now = DateTime.UtcNow;
            // The same poll tells the rules' auto-tracking who went online (MainWindow.AutoTrack).
            NoteAutoTrackConnections(conns);
            if (conns == null)
            {
                // Engine down / restarting: nothing moves through it.
                _connPrev = null;
                WhoSweep(now);   // its connections are gone: they stay listed as closed for a minute
                if (_speedNow.Count > 0) { _speedNow.Clear(); if (SpeedOnScreen) RenderAllSpeeds(); }
                return;
            }
            if (_speedMapDirty) BuildSpeedMap();

            double secs = _connPrev == null ? 0 : (now - _connPrevAt).TotalSeconds;
            bool measure = _connPrev != null && secs > 0.3 && secs < 10;
            var cur = new Dictionary<string, long[]>(conns.Count, StringComparer.Ordinal);
            var moved = new Dictionary<string, long[]>(StringComparer.Ordinal);   // tile → {down, up} bytes since last poll
            foreach (var c in conns)
            {
                cur[c.Id] = new[] { c.Up, c.Down };
                if (!measure) continue;
                long[] p;
                // A connection new since the last poll: everything it carried is this second's.
                long du = _connPrev.TryGetValue(c.Id, out p) ? c.Up - p[0] : c.Up;
                long dd = p != null ? c.Down - p[1] : c.Down;
                var tile = TileOfConnection(c);
                if (tile == null) continue;
                // «Кто и куда» (MainWindow.SpeedWho): kept for EVERY tile all the time, idle
                // connections too — collecting only once the window opened showed «Собираю…» while
                // a short download was already over (2026-11-16).
                WhoKeep(tile, c, Math.Max(0, dd) / secs, Math.Max(0, du) / secs, now);
                if (du <= 0 && dd <= 0) continue;
                if (c.Process.Length > 0) _whoLast[tile] = new WhoSeen { At = now, Process = c.Process };
                long[] m;
                if (!moved.TryGetValue(tile, out m)) moved[tile] = m = new long[2];
                m[0] += Math.Max(0, dd);
                m[1] += Math.Max(0, du);
            }
            _connPrev = cur;
            _connPrevAt = now;
            if (!measure) return;
            WhoSweep(now);
            ConnArchive.FlushIfDue();

            // Every tile of the config, shown or not: the day's history is per tile.
            foreach (var app in (_config?.Apps ?? new List<AppEntry>()).Where(a => a != null))
            {
                long[] m;
                moved.TryGetValue(app.Id, out m);
                double down = m == null ? 0 : m[0] / secs, up = m == null ? 0 : m[1] / secs;
                SpeedHistory.Add(app.Id, now, down, up, secs);
                Queue<SpeedSec> q;
                if (!_speedLastMin.TryGetValue(app.Id, out q)) _speedLastMin[app.Id] = q = new Queue<SpeedSec>();
                q.Enqueue(new SpeedSec { At = now, Down = down, Up = up, Secs = secs });
                while (q.Count > 0 && (now - q.Peek().At).TotalSeconds > 60) q.Dequeue();
                // Light smoothing for the plate: one burst does not make the number jump around.
                double[] old;
                if (_speedNow.TryGetValue(app.Id, out old)) { down = old[0] * 0.35 + down * 0.65; up = old[1] * 0.35 + up * 0.65; }
                _speedNow[app.Id] = new[] { down, up };
            }
            if (SpeedOnScreen) RenderAllSpeeds();
        }

        // The same order the engine checks: every tile's sites above every tile's programs, the
        // earlier tile first for a shared address; «Всё остальное» is MATCH.
        private void BuildSpeedMap()
        {
            _speedMapDirty = false;
            var sites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var other = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<KeyValuePair<string, string>>();
            var autoFiles = new List<KeyValuePair<string, string>>();
            string match = null;
            foreach (var app in (_config?.Apps ?? new List<AppEntry>()).Where(a => a != null))
            {
                foreach (var f in app.Folders ?? new List<RuleFolder>())
                    if (f != null && !string.IsNullOrWhiteSpace(f.Path))
                        folders.Add(new KeyValuePair<string, string>(f.Path, app.Id));
                foreach (var p in app.Programs ?? new List<RuleProgram>())
                    if (MihomoConfigBuilder.AutoFileRegex(p).Length > 0)
                        autoFiles.Add(new KeyValuePair<string, string>(p.Path, app.Id));
                var rules = new List<RuleDef>();
                try { MihomoConfigBuilder.AddAppRules(rules, app, app.Id); } catch { continue; }
                foreach (var r in rules)
                {
                    if (string.Equals(r.Type, "MATCH", StringComparison.OrdinalIgnoreCase)) { if (match == null) match = app.Id; continue; }
                    var key = RuleKey(r.Type, r.Value);
                    var map = r.Site ? sites : other;
                    if (!map.ContainsKey(key)) map[key] = app.Id;
                }
            }
            _speedSiteRules = sites;
            _speedOtherRules = other;
            _speedMatchTile = match;
            // The deeper folder first, as in the config (stable: equal depth keeps the tile order).
            _speedFolders = folders.OrderByDescending(f => MihomoConfigBuilder.NormalizeFolder(f.Key).Length).ToList();
            _speedAutoFiles = autoFiles;
        }

        private List<KeyValuePair<string, string>> _speedFolders = new List<KeyValuePair<string, string>>();
        // Auto entries by their exact file (in tile order, as in the config).
        private List<KeyValuePair<string, string>> _speedAutoFiles = new List<KeyValuePair<string, string>>();

        // "DOMAIN-SUFFIX" (config) and "DomainSuffix" (engine) → "DOMAINSUFFIX"; IP-CIDR6 is IPCIDR to the engine.
        private static string RuleKey(string type, string value)
        {
            var t = (type ?? "").Replace("-", "").ToUpperInvariant();
            if (t == "IPCIDR6") t = "IPCIDR";
            return t + "|" + (value ?? "").Trim().ToLowerInvariant();
        }

        private string TileOfConnection(MihomoController.ConnStat c)
        {
            if (_speedSiteRules == null) return null;
            string tile;
            bool match = c.Rule.Equals("Match", StringComparison.OrdinalIgnoreCase);
            if (c.Rule.Length > 0 && !match)
            {
                var key = RuleKey(c.Rule, c.Payload);
                if (_speedSiteRules.TryGetValue(key, out tile) || _speedOtherRules.TryGetValue(key, out tile)) return tile;
            }
            // Who and where, in the engine's order (sites, then programs, then the rest): for a
            // subscription's own rules (SUB-RULE), and for MATCH — a «напрямую» tile without a
            // kill-switch writes no line of its own while everything else goes direct anyway, so
            // its traffic arrives as MATCH and was counted to «Всё остальное» (Claude, 2026-11-16).
            tile = HostTile(c, _speedSiteRules);
            if (tile != null) return tile;
            // An auto-tracked file sits right below the sites, above every program by name.
            if (!string.IsNullOrEmpty(c.ProcessPath))
                foreach (var f in _speedAutoFiles ?? new List<KeyValuePair<string, string>>())
                    if (MihomoConfigBuilder.IsSameFile(c.ProcessPath, f.Key)) return f.Value;
            if (c.Process.Length > 0 && _speedOtherRules.TryGetValue(RuleKey("PROCESS-NAME", c.Process), out tile)) return tile;
            tile = HostTile(c, _speedOtherRules);
            if (tile != null) return tile;
            // A tile's folder (PROCESS-PATH-REGEX, below every program in the config): by the
            // program's full path — the case a subscription's SUB-RULE or MATCH carried it.
            if (!string.IsNullOrEmpty(c.ProcessPath))
                foreach (var f in _speedFolders ?? new List<KeyValuePair<string, string>>())
                    if (MihomoConfigBuilder.InFolder(c.ProcessPath, f.Key)) return f.Value;
            return match ? _speedMatchTile : null;
        }

        // The tile whose DOMAIN-SUFFIX covers the connection's host (or a parent domain of it).
        private static string HostTile(MihomoController.ConnStat c, Dictionary<string, string> map)
        {
            foreach (var host in new[] { c.SniffHost, c.Host })
            {
                var h = host;
                while (!string.IsNullOrEmpty(h))
                {
                    string tile;
                    if (map.TryGetValue(RuleKey("DOMAIN-SUFFIX", h), out tile)) return tile;
                    int dot = h.IndexOf('.');
                    h = dot < 0 ? null : h.Substring(dot + 1);
                }
            }
            return null;
        }

        private void RenderAllSpeeds()
        {
            foreach (var kv in _speedViews) RenderSpeed(kv.Key, kv.Value);
        }

        // «↓ 15,2 Мбит/с ↑ 0,65» + the bar: its length on a log scale (0,1 Mbit/s still shows,
        // ~200 fills it), its colour the speed class.
        private void RenderSpeed(string id, SpeedView v)
        {
            double[] s;
            bool known = _speedNow.TryGetValue(id, out s);
            double down = known ? s[0] : 0, up = known ? s[1] : 0;
            double mbit = down * 8 / 1e6;

            v.Text.Inlines.Clear();
            v.Text.Inlines.Add(new Run("↓ " + (known ? Mbit(down) : "—")) { FontSize = 10.5, Foreground = mbit >= 0.005 ? Theme.TextPrimary : Theme.TextSecondary });
            v.Text.Inlines.Add(new Run(" " + Loc.T("Main_SpeedMbit")) { FontSize = 9, Foreground = Theme.TextMuted });
            // ↑ only while it fits (a long «↓ 150 Мбит/с» leaves no room).
            var upText = "  ↑ " + (known ? Mbit(up) : "—");
            double width = TextWidth("↓ " + (known ? Mbit(down) : "—"), 10.5, FontWeights.Normal)
                         + TextWidth(" " + Loc.T("Main_SpeedMbit") + upText, 9, FontWeights.Normal);
            if (width <= 102 - 2 - 6) v.Text.Inlines.Add(new Run(upText) { FontSize = 9, Foreground = Theme.TextMuted });

            double frac = mbit < 0.005 ? 0 : Math.Min(1, Math.Log10(1 + mbit * 10) / Math.Log10(1 + 2000));
            v.Fill.Width = Math.Round(frac * LevelBarWidth, 1);
            v.Fill.Background = SpeedBrush(mbit);
        }

        // Bytes per second → Mbit/s, never K: «0,05» / «3,10» / «12,3» / «150»; nothing at all = «0».
        internal static string Mbit(double bytesPerSec)
        {
            double m = Math.Max(0, bytesPerSec) * 8 / 1e6;
            if (m < 0.005) return "0";
            if (m < 10) return m.ToString("0.00", CultureInfo.CurrentCulture);
            if (m < 100) return m.ToString("0.0", CultureInfo.CurrentCulture);
            return m.ToString("0", CultureInfo.CurrentCulture);
        }

        // Download speed class, by colour: slow / medium / good / fast.
        internal static Brush SpeedBrush(double mbit)
        {
            if (mbit < 0.005) return Theme.SpeedTrack;
            if (mbit < 1) return Theme.SpeedRed;      // плохая: under 1 Mbit/s
            if (mbit < 10) return Theme.SpeedAmber;   // средняя: 1–10
            if (mbit < 50) return Theme.SpeedGreen;   // хорошая: 10–50
            return Theme.SpeedBlue;                   // быстрая: 50 and more
        }
    }
}
