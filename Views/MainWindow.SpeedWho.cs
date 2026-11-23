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
    // «Кто и куда» in a tile's speed window (2026-11-16, variant C of .dev\mockups\speed-who*.html):
    //   · «Выход в интернет» — what the tile goes out through (its VPN, its card, or Windows'
    //     default card), that exit's public address, place and provider;
    //   · left, the programs whose connections fell to this tile, by the traffic they carried;
    //     a double click shows the exe in Explorer;
    //   · right, where the selected program goes: name, address:port, country / city, provider,
    //     speed down / up.
    // Everything per connection comes from the engine's /connections (PollSpeedAsync gathers it
    // only while such a window is open); the place from our GeoLite2-City, the provider from the
    // engine's GeoLite2-ASN — nothing is asked of the internet. Names are the engine's own: the
    // name a program asked its DNS for (fake-ip) or read from the TLS/HTTP start (sniffer); an
    // address with no name was dialled as a bare address.
    //
    // Tables with fixed columns and a fixed number of rows, striped (2026-11-16: «таблицей с
    // разным цветом строчек … ровные столбики, чтобы не скакали»): figures right-aligned in
    // tabular digits; the window keeps its height whatever is open.
    //
    // Drawn like the rules list (ListsWindow.GridApps, 2026-11-22: «все таблицы одного стиля и
    // размера»): the header, cell padding, hairlines and row colours of Themes\DataGridStyles.xaml,
    // taken from the same Theme table tokens, in the same well with the outline. The one difference is the row height: these cells hold two
    // lines (name + connections, host + address), which 26 px cannot, so rows stay WhoRowH.
    public partial class MainWindow
    {
        internal sealed class WhoConn
        {
            public MihomoController.ConnStat C;
            public double Down, Up;    // bytes/s at the last poll; 0 once closed
            public DateTime Seen;      // last poll it was open at
            public bool Closed;        // gone from the engine — listed, dimmed, for WhoKeepClosed
            // Bytes already written to ConnArchive: a connection that «closed» on an API blip and
            // came back is written again only with what it carried since (no double count).
            public long ArchDown, ArchUp;
        }

        // Into the 24-hour archive (ConnArchive): what it carried since it was last written.
        private static void WhoArchive(string tile, WhoConn w)
        {
            long dd = w.C.Down - w.ArchDown, du = w.C.Up - w.ArchUp;
            if (dd <= 0 && du <= 0 && (w.ArchDown > 0 || w.ArchUp > 0)) return;
            ConnArchive.Add(ConnArchive.From(tile, w.C, dd, du, w.Seen));
            w.ArchDown = w.C.Down; w.ArchUp = w.C.Up;
        }

        // On exit: what is still open goes to the archive too, then the buffer to disk.
        private void WhoArchiveOpenAndFlush()
        {
            try
            {
                foreach (var kv in _whoRecent)
                    foreach (var w in kv.Value.Values)
                        if (!w.Closed) WhoArchive(kv.Key, w);
            }
            catch (Exception ex) { Log.Error("conn-archive-exit", ex); }
            ConnArchive.Flush();
        }
        private sealed class WhoSeen { public DateTime At; public string Process; }

        // Kept always, for every tile (2026-11-16): opening the window must show what is already
        // going on, and a connection that just ended is still worth seeing for a minute.
        private static readonly TimeSpan WhoKeepClosed = TimeSpan.FromSeconds(60);
        // Tile Id → connection Id → it.
        private readonly Dictionary<string, Dictionary<string, WhoConn>> _whoRecent = new Dictionary<string, Dictionary<string, WhoConn>>(StringComparer.Ordinal);
        // Tile Id → its connections as of the last poll (a fresh list each poll; the view only reads).
        private Dictionary<string, List<WhoConn>> _whoNow = new Dictionary<string, List<WhoConn>>(StringComparer.Ordinal);
        private bool _whoMeasured;   // one measured poll done — before that the view says «Собираю…»

        private void WhoKeep(string tile, MihomoController.ConnStat c, double down, double up, DateTime now)
        {
            Dictionary<string, WhoConn> map;
            if (!_whoRecent.TryGetValue(tile, out map)) _whoRecent[tile] = map = new Dictionary<string, WhoConn>(StringComparer.Ordinal);
            WhoConn w;
            if (!map.TryGetValue(c.Id, out w)) map[c.Id] = w = new WhoConn();
            w.C = c; w.Down = down; w.Up = up; w.Seen = now; w.Closed = false;
        }

        // After a poll: what was not seen in it is closed; closed for over a minute — forgotten.
        private void WhoSweep(DateTime now)
        {
            var snap = new Dictionary<string, List<WhoConn>>(StringComparer.Ordinal);
            foreach (var tile in _whoRecent.Keys.ToList())
            {
                var map = _whoRecent[tile];
                foreach (var id in map.Keys.ToList())
                {
                    var w = map[id];
                    if (w.Seen >= now) continue;
                    if (!w.Closed) WhoArchive(tile, w);   // just ended: one line in the archive
                    if (now - w.Seen > WhoKeepClosed) { map.Remove(id); continue; }
                    w.Closed = true; w.Down = 0; w.Up = 0;
                }
                if (map.Count == 0) { _whoRecent.Remove(tile); continue; }
                snap[tile] = map.Values.ToList();
            }
            _whoNow = snap;
            _whoMeasured = true;
        }
        // Tile Id → the last time some program moved bytes through it (this run).
        private readonly Dictionary<string, WhoSeen> _whoLast = new Dictionary<string, WhoSeen>(StringComparer.Ordinal);

        private const int WhoRows = 8;
        private const double WhoRowH = 36;
        private static readonly FontFamily WhoMono = new FontFamily("Consolas");

        // The model table's metrics (Themes\DataGridStyles.xaml): header 34 high, 13 semibold; cells
        // 12 with 8 px sides; hairlines right and bottom of every cell, the first column also left.
        private const double WhoHeadH = 34;
        private const double WhoHeadFont = 13;
        private const double WhoCellFont = 12;
        private const double WhoCellPad = 8;
        // What a cell adds around its text: both side paddings + its right hairline (+1 more for the
        // first column's left one — not counted, it is within the «air» added on top).
        private const double WhoCellChrome = 2 * WhoCellPad + 1;

        private string AsnPath => _mihomo == null ? null : System.IO.Path.Combine(_mihomo.WorkDir, "ASN.mmdb");

        private sealed class WhoProg
        {
            public string Key, Name, Path;
            public List<WhoConn> Conns;
            public int Open;           // of Conns, still open
            public double Down, Up;    // live: bytes/s; archive: bytes
            public long Bytes;
            public int Count;          // archive: connections in the period
            public List<WhoAddr> Addrs; // archive: already added up
        }

        private static string RecProgKey(ConnArchive.Rec r) => r.Path.Length > 0 ? r.Path : r.Process.Length > 0 ? r.Process : "?";
        private static string RecAddrKey(ConnArchive.Rec r) => r.Host.Length > 0 ? r.Host : r.Sniff.Length > 0 ? r.Sniff : r.Ip;

        // The archive of a period, added up by program → address as the lines stream in (off the UI
        // thread): only the groups stay in memory, never the lines.
        // unknownName: Loc.T("Main_WhoUnknownProc"), looked up on the UI thread by the caller.
        private static List<WhoProg> WhoAggregate(IEnumerable<ConnArchive.Rec> recs, string unknownName)
        {
            var progs = new Dictionary<string, WhoProg>(StringComparer.OrdinalIgnoreCase);
            var addrs = new Dictionary<string, Dictionary<string, WhoAddr>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in recs)
            {
                var pk = RecProgKey(r);
                WhoProg p;
                if (!progs.TryGetValue(pk, out p))
                {
                    progs[pk] = p = new WhoProg { Key = pk, Name = r.Process.Length > 0 ? r.Process : unknownName, Path = r.Path };
                    addrs[pk] = new Dictionary<string, WhoAddr>(StringComparer.OrdinalIgnoreCase);
                }
                p.Count++; p.Down += r.Down; p.Up += r.Up; p.Bytes += r.Down + r.Up;
                if (p.Path.Length == 0 && r.Path.Length > 0) p.Path = r.Path;
                var map = addrs[pk];
                var ak = RecAddrKey(r);
                WhoAddr a;
                if (!map.TryGetValue(ak, out a))
                    map[ak] = a = new WhoAddr { Host = r.Host.Length > 0 ? r.Host : r.Sniff, Ip = r.Ip, Port = r.Port, Network = r.Network };
                if (a.Ip.Length == 0 && r.Ip.Length > 0) a.Ip = r.Ip;
                a.Count++; a.Down += r.Down; a.Up += r.Up; a.Bytes += r.Down + r.Up;
            }
            foreach (var p in progs.Values)
                p.Addrs = addrs[p.Key].Values.OrderByDescending(a => a.Bytes).ThenBy(a => a.Host, StringComparer.OrdinalIgnoreCase).ToList();
            return progs.Values.OrderByDescending(p => p.Bytes).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // «12 КБ» / «3,4 МБ» / «123 МБ» / «1,23 ГБ» — a table cell's volume.
        internal static string WhoVolume(double bytes)
        {
            double kb = Math.Max(0, bytes) / 1024, mb = kb / 1024;
            if (mb >= 1024) return (mb / 1024).ToString(mb / 1024 < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedGB");
            if (mb >= 1) return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedMB");
            return Math.Round(kb).ToString(CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedKB");
        }

        private static void SetVolume(TextBlock t, double? bytes)
        {
            if (bytes == null) { t.Text = ""; return; }
            t.Text = WhoVolume(bytes.Value);
            t.Opacity = bytes.Value < 1024 ? 0.45 : 1;
        }

        private sealed class WhoAddr
        {
            public string Host, Ip, Port, Network;
            public int Count, Open;
            public double Down, Up;
            public long Bytes;
        }

        // The «кто и куда» block and its refresh. Registers the tile for collection; the caller
        // calls the returned Action on close.
        // detached: the same view in a window of its own the user sizes (the ⧉ in the corner of the
        // addresses, 2026-11-16) — the tables then fill whatever height and width it is given.
        //
        // hours: 0 = «Сейчас» (live, the last minute, refreshed every 2 s); 1 / 6 / 12 / 24 = the
        // archive of that period (ConnArchive + what is open right now), read once when chosen and
        // then left still — no rows jumping while it is looked at. setHours switches it.
        private UIElement BuildSpeedWho(AppEntry entry, Window win, bool detached, string initialSelected, int initialHours,
                                        out Action stop, out Action<int> setHours)
        {
            string selected = initialSelected;
            List<WhoProg> progs = new List<WhoProg>();
            Action refresh = null;   // the row clicks are wired before Refresh exists
            int hours = initialHours;
            List<WhoProg> archive = null;   // the period's snapshot; null while it loads
            DateTime archiveAt = DateTime.Now;
            int loadGen = 0;

            // ---- exit line ----
            var exitText = new TextBlock { FontSize = 12.5, Foreground = Theme.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var exitBox = Pane(exitText, new Thickness(10, 7, 10, 7));
            exitBox.Margin = new Thickness(0, detached ? 0 : 10, 0, 0);
            DockPanel.SetDock(exitBox, Dock.Top);

            // ---- left: programs ----
            var leftHead = PaneTitle();
            DockPanel.SetDock(leftHead, Dock.Top);
            var progCols = new[] { new GridLength(1, GridUnitType.Star), new GridLength(72), new GridLength(Gutter) };
            var progTable = new DockPanel();
            var progHeader = HeaderRow(progCols, 1, -1, Loc.T("Main_WhoColProgram"), Loc.T("Main_WhoColDown"));
            DockPanel.SetDock(progHeader, Dock.Top);
            progTable.Children.Add(progHeader);
            var progRows = new List<Border>();
            var progName = new List<TextBlock>();
            var progSub = new List<TextBlock>();
            var progDown = new List<TextBlock>();
            // All of them (2026-11-16: «показываем все»): the rows scroll inside a box exactly
            // WhoRows tall, so the window keeps its height; rows are added as needed, never rebuilt.
            var progBody = new StackPanel();
            var progScroll = ScrollBox(progBody, !detached);
            progTable.Children.Add(progScroll);
            progScroll.ScrollChanged += (s, e) => { if (e.ViewportWidthChange != 0) SyncGutter(progScroll, (Grid)progHeader.Child); };
            void AddProgRow()
            {
                int i = progRows.Count;
                var name = Cell(WhoCellFont, Theme.TextPrimary, FontWeights.SemiBold);
                var sub = Cell(11, Theme.TextMuted);
                var down = Num();
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(name);
                stack.Children.Add(sub);
                var row = Row(progCols, i, -1, stack, down);
                int idx = i;
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (idx >= progs.Count) return;
                    var p = progs[idx];
                    e.Handled = true;
                    if (e.ClickCount >= 2) { ShowInExplorer(win, p); return; }
                    selected = p.Key;
                    refresh?.Invoke();
                };
                progRows.Add(row); progName.Add(name); progSub.Add(sub); progDown.Add(down);
                progBody.Children.Add(row);
            }
            for (int i = 0; i < WhoRows; i++) AddProgRow();
            // Striped down to the bottom in the detached window (2026-11-17: «строки разноцветные
            // для всего окна»): empty stripes drawn UNDER the table, continuing after its rows.
            Action<int> progStripes = null, addrStripes = null;
            if (detached)
            {
                progTable.Children.Remove(progScroll);
                progTable.Children.Add(StripedUnder(progScroll, out progStripes));
            }
            var leftStack = new DockPanel();
            leftStack.Children.Add(leftHead);
            leftStack.Children.Add(WhoWell(progTable));
            var left = Pane(leftStack, new Thickness(6));

            // ---- right: where the selected program goes ----
            var rightHead = PaneTitle();
            var rightHeadRow = new DockPanel();
            DockPanel.SetDock(rightHeadRow, Dock.Top);
            if (!detached)
            {
                var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Open24, FontSize = 16, Foreground = Theme.TextSecondary };
                var detach = new Border
                {
                    Width = 28, Height = 24, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
                    Margin = new Thickness(4, 0, 2, 4), Cursor = Cursors.Hand, ToolTip = Loc.T("Main_WhoDetachTip"),
                    Child = icon,
                };
                detach.MouseEnter += (s, e) => { detach.Background = Theme.ChipBgHover; icon.Foreground = Theme.TextPrimary; };
                detach.MouseLeave += (s, e) => { detach.Background = Brushes.Transparent; icon.Foreground = Theme.TextSecondary; };
                detach.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenSpeedWhoWindow(entry, selected, hours); };
                DockPanel.SetDock(detach, Dock.Right);
                rightHeadRow.Children.Add(detach);
            }
            rightHeadRow.Children.Add(rightHead);

            // Columns: address · place · provider take what their longest text needs (WhoFit),
            // the spare width goes to the empty column 3 — so a wider window shows what was cut,
            // never stretches what was already whole. Then ↓, ↑ and the scroll bar's gutter.
            var addrCols = new[]
            {
                new GridLength(160), new GridLength(160), new GridLength(120), new GridLength(1, GridUnitType.Star),
                new GridLength(66), new GridLength(60), new GridLength(Gutter),
            };
            var aGrids = new List<Grid>();
            var addrTable = new DockPanel();
            var addrHeader = HeaderRow(addrCols, 4, 2, Loc.T("Main_WhoColAddr"), Loc.T("Main_WhoColWhere"), Loc.T("Main_WhoColIsp"), "",
                                       Loc.T("Main_WhoColDown"), Loc.T("Main_WhoColUp"));
            DockPanel.SetDock(addrHeader, Dock.Top);
            addrTable.Children.Add(addrHeader);
            aGrids.Add((Grid)addrHeader.Child);
            var aHost = new List<TextBlock>();
            var aIp = new List<TextBlock>();
            var aCc = new List<Border>();
            var aCcText = new List<TextBlock>();
            var aPlace = new List<TextBlock>();
            var aIsp = new List<TextBlock>();
            var aDown = new List<TextBlock>();
            var aUp = new List<TextBlock>();
            var aRows = new List<Border>();
            var addrBody = new StackPanel();
            var addrScroll = ScrollBox(addrBody, !detached);
            addrTable.Children.Add(addrScroll);
            double[] want = { 160, 160, 120 };
            double[] least = { 120, 110, 90 };
            void LayoutAddr()
            {
                double view = addrScroll.ViewportWidth > 0 ? addrScroll.ViewportWidth : addrScroll.ActualWidth;
                if (view <= 0) return;
                // ↓ + ↑ + gutter are fixed; the three text columns share the rest.
                var w = WhoFit(want, least, view - 66 - 60 - Gutter);
                foreach (var g in aGrids)
                    for (int k = 0; k < 3; k++) g.ColumnDefinitions[k].Width = new GridLength(w[k]);
                SyncGutter(addrScroll, (Grid)addrHeader.Child);
            }
            addrScroll.SizeChanged += (s, e) => LayoutAddr();
            if (detached)
            {
                addrTable.Children.Remove(addrScroll);
                addrTable.Children.Add(StripedUnder(addrScroll, out addrStripes));
            }
            addrScroll.ScrollChanged += (s, e) => { if (e.ViewportWidthChange != 0) LayoutAddr(); };
            void AddAddrRow()
            {
                int i = aRows.Count;
                var host = Cell(WhoCellFont, Theme.TextPrimary, FontWeights.SemiBold);
                var ip = Cell(11, Theme.TextMuted);
                ip.FontFamily = WhoMono;
                var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                hs.Children.Add(host);
                hs.Children.Add(ip);

                var cc = CountryBadge("");
                cc.Margin = new Thickness(0, 0, 6, 0);
                var ccText = (TextBlock)cc.Child;
                var place = Cell(12, Theme.TextPrimary);
                var geo = new DockPanel { VerticalAlignment = VerticalAlignment.Center, LastChildFill = true };
                DockPanel.SetDock(cc, Dock.Left);
                geo.Children.Add(cc);
                geo.Children.Add(place);

                var isp = Cell(12, Theme.TextSecondary);
                var down = Num();
                var up = Num();
                up.Foreground = Theme.TextSecondary;
                var arow = Row(addrCols, i, 2, hs, geo, isp, new Border(), down, up);
                addrBody.Children.Add(arow);
                aRows.Add(arow);
                aGrids.Add((Grid)arow.Child);
                aHost.Add(host); aIp.Add(ip); aCc.Add(cc); aCcText.Add(ccText); aPlace.Add(place); aIsp.Add(isp); aDown.Add(down); aUp.Add(up);
            }
            for (int i = 0; i < WhoRows; i++) AddAddrRow();
            var empty = new TextBlock
            {
                FontSize = 12.5, Foreground = Theme.TextSecondary, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 20, 0),
                LineHeight = 20,
            };
            var tableHost = new Grid();
            tableHost.Children.Add(WhoWell(addrTable));
            tableHost.Children.Add(empty);
            var rightStack = new DockPanel();
            rightStack.Children.Add(rightHeadRow);
            rightStack.Children.Add(tableHost);
            var right = Pane(rightStack, new Thickness(6));

            var split = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            split.Children.Add(left);
            Grid.SetColumn(right, 2);
            split.Children.Add(right);

            var root = new DockPanel();
            root.Children.Add(exitBox);
            root.Children.Add(split);

            void Refresh()
            {
                RenderExit(entry, exitText);
                bool live = hours == 0;

                // Column titles: speed now, or the volume over the period.
                WhoHeadText(progHeader, 1).Text = Loc.T(live ? "Main_WhoColDown" : "Main_WhoColVol");
                WhoHeadText(addrHeader, 4).Text = Loc.T(live ? "Main_WhoColDown" : "Main_WhoColVol");

                List<WhoConn> conns;
                if (!live || !_whoNow.TryGetValue(entry.Id, out conns) || conns == null) conns = new List<WhoConn>();
                progs = !live ? (archive ?? new List<WhoProg>()) : conns.GroupBy(w => ProgKey(w.C), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new WhoProg
                    {
                        Key = g.Key,
                        Name = g.First().C.Process.Length > 0 ? g.First().C.Process : Loc.T("Main_WhoUnknownProc"),
                        Path = g.Select(w => w.C.ProcessPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                        Conns = g.ToList(),
                        Open = g.Count(w => !w.Closed),
                        Down = g.Sum(w => w.Down),
                        Up = g.Sum(w => w.Up),
                        Bytes = g.Sum(w => w.C.Down + w.C.Up),
                    })
                    // By what they carried, not by this second's speed: rows don't jump around.
                    .OrderByDescending(p => p.Bytes).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (selected == null || !progs.Any(p => p.Key == selected)) selected = progs.Count > 0 ? progs[0].Key : null;

                leftHead.Text = Loc.T("Main_WhoPrograms") + (progs.Count > 0 ? " · " + progs.Count : "")
                              + (live || archive == null ? "" : " · " + Loc.T("Main_WhoSnapshot", archiveAt.ToString("HH:mm")));
                int progShow = Math.Max(WhoRows, progs.Count);
                progStripes?.Invoke(progShow);
                while (progRows.Count < progShow) AddProgRow();
                for (int i = 0; i < progRows.Count; i++)
                {
                    // At least progMin striped rows (an empty table still looks like one); past that,
                    // exactly as many as there are programs.
                    progRows[i].Visibility = i < progShow ? Visibility.Visible : Visibility.Collapsed;
                    var p = i < progs.Count ? progs[i] : null;
                    progName[i].Text = p?.Name ?? "";
                    progSub[i].Text = p == null ? "" : !live ? Loc.T("Main_WhoConns", p.Count) : p.Open > 0 ? Loc.T("Main_WhoConns", p.Open) : Loc.T("Main_WhoClosed");
                    // Only closed ones left (within the last minute): dimmed, still there to see.
                    progRows[i].Opacity = live && p != null && p.Open == 0 ? 0.5 : 1;
                    if (live) SetSpeed(progDown[i], p == null ? (double?)null : p.Down);
                    else SetVolume(progDown[i], p == null ? (double?)null : p.Down);
                    WhoPaint(progRows[i], i, p == null ? (bool?)null : p.Key == selected);
                    progRows[i].ToolTip = p == null ? null : (p.Path.Length > 0 ? p.Path + "\n\n" : "") + Loc.T("Main_WhoTipOpen");
                    progRows[i].Cursor = p == null ? null : Cursors.Hand;
                }

                var sel = progs.FirstOrDefault(p => p.Key == selected);
                var addrs = sel == null ? new List<WhoAddr>() : sel.Addrs ?? sel.Conns
                    .GroupBy(w => AddrKey(w.C), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var f = g.First().C;
                        return new WhoAddr
                        {
                            Host = f.Host.Length > 0 ? f.Host : f.SniffHost,
                            Ip = g.Select(w => w.C.DestIp).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "",
                            Port = f.Port,
                            Network = f.Network,
                            Count = g.Count(),
                            Open = g.Count(w => !w.Closed),
                            Down = g.Sum(w => w.Down),
                            Up = g.Sum(w => w.Up),
                            Bytes = g.Sum(w => w.C.Down + w.C.Up),
                        };
                    })
                    .OrderByDescending(a => a.Bytes).ThenBy(a => a.Host, StringComparer.OrdinalIgnoreCase).ToList();

                rightHead.Inlines.Clear();
                if (sel != null)
                {
                    rightHead.Inlines.Add(new Run(sel.Name) { Foreground = Theme.TextPrimary });
                    rightHead.Inlines.Add(new Run(live
                        ? "  ·  " + (sel.Open > 0 ? Loc.T("Main_WhoConns", sel.Open) : Loc.T("Main_WhoClosed")) + "  ·  ↓ " + Mbit(sel.Down) + "  ↑ " + Mbit(sel.Up) + " " + Loc.T("Main_SpeedMbit")
                        : "  ·  " + Loc.T("Main_WhoConns", sel.Count) + "  ·  ↓ " + WhoVolume(sel.Down) + "  ↑ " + WhoVolume(sel.Up))
                    { Foreground = Theme.TextSecondary, FontWeight = FontWeights.Normal });   // was Muted — too faint
                }
                else rightHead.Inlines.Add(new Run(Loc.T("Main_WhoWhere")) { Foreground = Theme.TextPrimary });

                string asn = AsnPath;
                int addrShow = Math.Max(WhoRows, addrs.Count);
                addrStripes?.Invoke(addrShow);
                while (aRows.Count < addrShow) AddAddrRow();
                for (int i = 0; i < aRows.Count; i++)
                {
                    aRows[i].Visibility = i < addrShow ? Visibility.Visible : Visibility.Collapsed;
                    var a = i < addrs.Count ? addrs[i] : null;
                    if (a == null)
                    {
                        aHost[i].Text = aIp[i].Text = aPlace[i].Text = aIsp[i].Text = "";
                        aCc[i].Visibility = Visibility.Collapsed;
                        aRows[i].Opacity = 1;
                        WhoPaint(aRows[i], i, null);
                        SetSpeed(aDown[i], null); SetSpeed(aUp[i], null);
                        aHost[i].ToolTip = null;
                        continue;
                    }
                    bool named = !string.IsNullOrEmpty(a.Host);
                    aHost[i].Text = (named ? a.Host : (a.Ip.Length > 0 ? a.Ip : "—")) + (a.Count > 1 ? "  ×" + a.Count : "");
                    aHost[i].ToolTip = (named ? a.Host : Loc.T("Main_WhoNoName")) + (live && a.Open == 0 ? "\n" + Loc.T("Main_WhoClosedTip") : "");
                    aRows[i].Opacity = live && a.Open == 0 ? 0.5 : 1;
                    WhoPaint(aRows[i], i, false);
                    // Named: «173.194.182.8:443». A bare address is already the top line, so below it
                    // only «порт 59453 · UDP» (was a dangling «:59453»).
                    bool udp = string.Equals(a.Network, "udp", StringComparison.OrdinalIgnoreCase);
                    if (named)
                        aIp[i].Text = a.Ip + (a.Ip.Length > 0 && a.Port.Length > 0 ? ":" + a.Port : "") + (udp ? " · UDP" : "");
                    else
                        aIp[i].Text = (a.Port.Length > 0 ? Loc.T("Main_WhoPort", a.Port) : "") + (udp ? (a.Port.Length > 0 ? " · " : "") + "UDP" : "");

                    var g = a.Ip.Length > 0 ? GeoIpService.Lookup(a.Ip) : null;
                    var place = GeoIpService.PlaceOf(g);
                    if (place.Length == 0) place = a.Ip.Length == 0 ? "—" : IsPrivate(a.Ip) ? Loc.T("Main_WhoLocal") : "—";
                    aPlace[i].Text = place;
                    aPlace[i].ToolTip = place;
                    var iso = g?.IsoCode ?? "";
                    aCcText[i].Text = iso;
                    aCc[i].Visibility = iso.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    var org = a.Ip.Length > 0 ? GeoIpService.OrgOf(a.Ip, asn) : "";
                    aIsp[i].Text = org.Length > 0 ? org : "—";
                    aIsp[i].ToolTip = org.Length > 0 ? org : null;
                    if (live) { SetSpeed(aDown[i], a.Down); SetSpeed(aUp[i], a.Up); }
                    else { SetVolume(aDown[i], a.Down); SetVolume(aUp[i], a.Up); }
                }

                // What each text column needs: its header, then its longest line among the rows.
                want[0] = WhoTextWidth(Loc.T("Main_WhoColAddr"), WhoHeadFont, FontWeights.SemiBold, null);
                want[1] = WhoTextWidth(Loc.T("Main_WhoColWhere"), WhoHeadFont, FontWeights.SemiBold, null);
                want[2] = WhoTextWidth(Loc.T("Main_WhoColIsp"), WhoHeadFont, FontWeights.SemiBold, null);
                for (int i = 0; i < addrs.Count; i++)
                {
                    want[0] = Math.Max(want[0], Math.Max(WhoTextWidth(aHost[i].Text, WhoCellFont, FontWeights.SemiBold, null),
                                                         WhoTextWidth(aIp[i].Text, 11, FontWeights.Normal, WhoMono)));
                    double badge = aCc[i].Visibility == Visibility.Visible ? BadgeWidth + 6 : 0;
                    want[1] = Math.Max(want[1], badge + WhoTextWidth(aPlace[i].Text, 12, FontWeights.Normal, null));
                    want[2] = Math.Max(want[2], WhoTextWidth(aIsp[i].Text, 12, FontWeights.Normal, null));
                }
                for (int k = 0; k < 3; k++) want[k] += WhoCellChrome + 8;   // the cell's padding and hairline + a little air
                LayoutAddr();

                if (sel != null) empty.Text = "";
                else if (!live) empty.Text = Loc.T(archive == null ? "Main_WhoLoading" : "Main_WhoNonePeriod");
                else if (_mihomo == null) empty.Text = Loc.T("Main_WhoNone");
                else if (!_whoMeasured) empty.Text = Loc.T("Main_WhoWait");
                else
                {
                    WhoSeen seen;
                    empty.Text = Loc.T("Main_WhoNone") + (_whoLast.TryGetValue(entry.Id, out seen)
                        ? "\n" + Loc.T("Main_WhoLast", WhenText(seen.At), seen.Process) : "");
                }
                empty.Visibility = empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // The period's snapshot: the archive files + what is open right now (its bytes since
            // last written), added up off the UI thread; a newer click wins over an older load.
            async void LoadArchive()
            {
                int gen = ++loadGen;
                int h = hours;
                archive = null;
                Refresh();
                var now = DateTime.UtcNow;
                var open = new List<ConnArchive.Rec>();
                Dictionary<string, WhoConn> map;
                if (_whoRecent.TryGetValue(entry.Id, out map))
                    foreach (var w in map.Values)
                        if (!w.Closed) open.Add(ConnArchive.From(entry.Id, w.C, w.C.Down - w.ArchDown, w.C.Up - w.ArchUp, now));
                List<WhoProg> result;
                var unknown = Loc.T("Main_WhoUnknownProc");
                try
                {
                    result = await System.Threading.Tasks.Task.Run(() =>
                        WhoAggregate(ConnArchive.Read(entry.Id, now.AddHours(-h)).Concat(open), unknown)).ConfigureAwait(true);
                }
                catch (Exception ex) { Log.Error("conn-archive-load", ex); result = new List<WhoProg>(); }
                if (gen != loadGen) return;
                archive = result;
                archiveAt = DateTime.Now;
                Refresh();
            }

            refresh = Refresh;
            if (hours == 0) Refresh(); else LoadArchive();
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) => { if (hours == 0) Refresh(); };
            timer.Start();
            stop = () => { timer.Stop(); loadGen++; };
            setHours = h =>
            {
                hours = h;
                if (h == 0) { archive = null; loadGen++; Refresh(); }
                else LoadArchive();
            };
            return root;
        }

        // «Выход в интернет: напрямую — карта Windows по умолчанию «Wi-Fi» · 91.197.105.120 · [RU] Сочи, Россия · Radist LTD»
        private void RenderExit(AppEntry e, TextBlock tb)
        {
            string via, ip = "", place = "", org = "", iso = "";
            string proxy;
            if (e.Block) via = Loc.T("Main_WhoBlocked");
            else if (_assignment.TryGetValue(e.Id, out proxy) && proxy != null)
            {
                ProxySlotInfo info;
                _proxySlot.TryGetValue(proxy, out info);
                via = Loc.T("Main_WhoViaVpn", info?.Name ?? proxy);
                var server = info?.Server ?? "";
                System.Net.IPAddress parsed;
                if (System.Net.IPAddress.TryParse(server, out parsed))
                {
                    ip = server;
                    var g = GeoIpService.Lookup(ip);
                    iso = g?.IsoCode ?? "";
                    place = GeoIpService.PlaceOf(g);
                    org = GeoIpService.OrgOf(ip, AsnPath);
                }
                else ip = server;   // a host name: its place came with the VPN's own lookup
                string pg;
                if (place.Length == 0 && _proxyGeo.TryGetValue(proxy, out pg)) place = pg ?? "";
            }
            else if (e.RequiresVpn) via = Loc.T("Main_WhoVpnOff");
            else
            {
                var all = CurrentNetCards();
                var chosen = NetCards.Chosen(e, all);
                bool own = chosen.Count > 0 && !IsJustDefaultCard(chosen.Select(c => c.Name));
                NetCard card = own ? chosen.FirstOrDefault(c => c.Present && c.Up) : DefaultCard();
                if (card == null)
                    via = own ? Loc.T("Main_WhoCardDown", string.Join(" › ", chosen.Select(c => c.Name))) : Loc.T("Main_WhoDirect");
                else
                {
                    via = Loc.T(own ? "Main_WhoViaCard" : "Main_WhoViaDefault", card.Name);
                    NicIpLookup.Result r;
                    if (_nicIp.TryGetValue(card.Name, out r) && r != null && r.Ok)
                    {
                        ip = r.Ip;
                        iso = GeoIpService.Lookup(ip)?.IsoCode ?? "";
                        place = PlaceOf(r);
                        org = r.Org ?? "";
                        if (org.Length == 0) org = GeoIpService.OrgOf(ip, AsnPath);
                    }
                }
            }

            tb.Inlines.Clear();
            tb.Inlines.Add(new Run(Loc.T("Main_WhoExit") + " "));
            tb.Inlines.Add(new Run(via) { Foreground = Theme.TextPrimary, FontWeight = FontWeights.SemiBold });
            if (ip.Length > 0)
            {
                tb.Inlines.Add(new Run("  ·  "));
                tb.Inlines.Add(new Run(ip) { Foreground = Theme.TextPrimary, FontFamily = WhoMono });
            }
            if (place.Length > 0 || iso.Length > 0)
            {
                tb.Inlines.Add(new Run("  ·  "));
                if (iso.Length > 0)
                {
                    var badge = CountryBadge(iso);
                    badge.Margin = new Thickness(0, 0, 5, 0);
                    // Centred on the line, not sat on the baseline with a guessed nudge: the
                    // old -3 left it riding above the text (2026-11-17).
                    tb.Inlines.Add(new InlineUIContainer(badge) { BaselineAlignment = BaselineAlignment.Center });
                }
                tb.Inlines.Add(new Run(place));
            }
            if (org.Length > 0) tb.Inlines.Add(new Run("  ·  " + org));
        }

        private async void ShowInExplorer(Window win, WhoProg p)
        {
            if (!string.IsNullOrEmpty(p.Path) && System.IO.File.Exists(p.Path))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p.Path + "\"");
                    return;
                }
                catch (Exception ex) { Log.Error("who-explorer", ex); }
            }
            await OverlayDialog.MessageAsync(win, Loc.T("Main_WhoNoPathTitle"),
                string.IsNullOrEmpty(p.Path) ? Loc.T("Main_WhoNoPath", p.Name) : Loc.T("Main_WhoPathGone", p.Path)).ConfigureAwait(true);
        }

        private static string ProgKey(MihomoController.ConnStat c)
            => c.ProcessPath.Length > 0 ? c.ProcessPath : c.Process.Length > 0 ? c.Process : "?";

        private static string AddrKey(MihomoController.ConnStat c)
        {
            var host = c.Host.Length > 0 ? c.Host : c.SniffHost;
            return host.Length > 0 ? host : c.DestIp;
        }

        private static bool IsPrivate(string ip)
        {
            System.Net.IPAddress a;
            if (!System.Net.IPAddress.TryParse(ip, out a)) return false;
            if (System.Net.IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes();
            if (b.Length == 4)
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                       || (b[0] == 169 && b[1] == 254) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        }

        private static string WhenText(DateTime utc)
        {
            var t = utc.ToLocalTime();
            return t.Date == DateTime.Now.Date ? t.ToString("HH:mm") : t.ToString("dd.MM HH:mm");
        }

        // ---- table pieces ----

        // «RU», «EE», «UA» in boxes of one width, the letters centred (2026-11-16: «кубики …
        // одинаковые» — sized by their letters, «EE» came out narrower than «RU»).
        private const double BadgeWidth = 26;

        private static Border CountryBadge(string iso) => new Border
        {
            Width = BadgeWidth,
            Height = 17,
            Background = Theme.ChipBg,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = iso ?? "", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Theme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // A grouped block: the raised surface (2026-11-22 standard; was the badge grey).
        private static Border Pane(UIElement child, Thickness padding) => new Border
        {
            Background = Theme.CardBg,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding,
            Child = child,
        };

        private static TextBlock PaneTitle() => new TextBlock
        {
            FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary,
            Margin = new Thickness(6, 2, 6, 6), TextTrimming = TextTrimming.CharacterEllipsis,
        };

        private static TextBlock Cell(double size, Brush fg, FontWeight? weight = null) => new TextBlock
        {
            FontSize = size, Foreground = fg, FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };

        // A right-aligned figure in tabular digits: the columns stay put as the numbers change.
        private static TextBlock Num()
        {
            var t = new TextBlock { FontSize = WhoCellFont, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.TextPrimary };
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        private static void SetSpeed(TextBlock t, double? bytesPerSec)
        {
            if (bytesPerSec == null) { t.Text = ""; return; }
            t.Text = Mbit(bytesPerSec.Value);
            var mbit = bytesPerSec.Value * 8 / 1e6;
            t.Opacity = mbit < 0.005 ? 0.45 : 1;
        }

        // Right of the last column: room for the scroll bar, which WPF-UI lays OVER the rows — it
        // covered the ↑ figures (2026-11-16). 14 was not enough: the bar sits a few px off the edge.
        private const double Gutter = 22;

        // The rows' box: exactly WhoRows rows tall in the speed window (fixedHeight), filling the
        // detached window; scrolling past that.
        private static ScrollViewer ScrollBox(UIElement rows, bool fixedHeight)
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows,
            };
            if (fixedHeight) sv.Height = WhoRows * WhoRowH;
            return sv;
        }

        // Empty stripes under a table, starting right after its `shown` rows and in step with them
        // (odd = striped, like Row). Only while nothing scrolls: then the rows start at the top of
        // the box and the stripes line up; once they scroll, the rows fill the box themselves.
        private static Grid StripedUnder(ScrollViewer sv, out Action<int> setShown)
        {
            const int Spare = 80;   // 80 × 36 — taller than any screen
            var fill = new StackPanel { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
            for (int i = 0; i < Spare; i++) fill.Children.Add(new Border { Height = WhoRowH });
            var host = new Grid { ClipToBounds = true };
            host.Children.Add(fill);
            host.Children.Add(sv);
            int shown = -1;
            void Apply()
            {
                fill.Visibility = sv.ScrollableHeight > 0.5 ? Visibility.Collapsed : Visibility.Visible;
            }
            sv.ScrollChanged += (s, e) => Apply();
            setShown = n =>
            {
                if (n != shown)
                {
                    shown = n;
                    fill.Margin = new Thickness(0, n * WhoRowH, 0, 0);
                    for (int i = 0; i < Spare; i++)
                        WhoPaint((Border)fill.Children[i], n + i, null);
                }
                Apply();
            };
            return host;
        }

        // A scroll bar that takes room of its own (not overlaid) narrows the rows but not the
        // header above them: widen the header's gutter by as much, so the columns stay in line.
        private static void SyncGutter(ScrollViewer sv, Grid header)
        {
            if (sv == null || header == null || header.ColumnDefinitions.Count == 0) return;
            double bar = sv.ViewportWidth > 0 ? Math.Max(0, sv.ActualWidth - sv.ViewportWidth) : 0;
            header.ColumnDefinitions[header.ColumnDefinitions.Count - 1].Width = new GridLength(Gutter + bar);
        }

        // Widths for columns that want `want`: all of it when it fits; otherwise the narrow ones
        // keep theirs and the wide ones share what is left evenly (never under `least`).
        private static double[] WhoFit(double[] want, double[] least, double avail)
        {
            int n = want.Length;
            var w = new double[n];
            if (want.Sum() <= avail) { Array.Copy(want, w, n); return w; }
            double left = Math.Max(0, avail);
            int remaining = n;
            foreach (int i in Enumerable.Range(0, n).OrderBy(k => want[k]))
            {
                double share = left / remaining;
                w[i] = Math.Max(least[i], Math.Min(want[i], share));
                left = Math.Max(0, left - w[i]);
                remaining--;
            }
            return w;
        }

        private double WhoTextWidth(string s, double size, FontWeight weight, FontFamily family)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            try
            {
                var tf = new Typeface(family ?? FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
                return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, Brushes.Black,
                                         VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
            }
            catch { return 0; }
        }

        // The ⧉ in the addresses' corner: the same view in a window of its own, sized by the user.
        private void OpenSpeedWhoWindow(AppEntry entry, string selected, int hours)
        {
            var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var win = new SpeedWindow(Loc.T("Main_WhoWinTitle", entry.IsAllTraffic ? AllTrafficName() : entry.Name), holder, resizable: true) { Owner = this };
            Action stop;
            Action<int> setHours;   // the detached window keeps the period it was opened with
            holder.Content = BuildSpeedWho(entry, win, true, selected, hours, out stop, out setHours);
            win.Closed += (s, e) => stop();
            win.Show();
        }

        private static Grid Columns(GridLength[] cols)
        {
            var g = new Grid();
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        // One cell of the model table: 8 px sides, a hairline right and bottom (the first column
        // also left; the header row also on top). joinRight: no right hairline — the column runs
        // on into the empty spare column beside it, so the two read as one.
        private static Border WhoCellBox(UIElement content, int col, bool head, bool joinRight)
        {
            var b = new Border
            {
                Padding = new Thickness(WhoCellPad, 0, WhoCellPad, 0),
                BorderThickness = new Thickness(col == 0 ? 1 : 0, head ? 1 : 0, joinRight ? 0 : 1, 1),
                Child = content,
            };
            b.BorderBrush = Theme.TableLine;
            if (head) b.Background = Theme.TableHeaderBg;
            Grid.SetColumn(b, col);
            return b;
        }

        // A table in a well with the outline, like every table (2026-11-22). The table is pushed 1 px
        // out on three sides and clipped back to the frame's inside (FrameClip): the frame is its
        // only outline.
        private static Border WhoWell(FrameworkElement table)
        {
            table.Margin = new Thickness(-1, -1, -1, 0);
            FrameClip.SetRadius(table, 7);
            return new Border
            {
                Background = Theme.ListBg,
                BorderBrush = Theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = table,
            };
        }

        // The title in header cell i (the cells are WhoCellBox borders).
        private static TextBlock WhoHeadText(Border header, int i) => (TextBlock)((Border)((Grid)header.Child).Children[i]).Child;

        // Titles from column 0; those from `rightFrom` on (the figures) aligned right. The last
        // column (the scroll bar's gutter) gets no cell.
        private static Border HeaderRow(GridLength[] cols, int rightFrom, int joinRight, params string[] titles)
        {
            var g = Columns(cols);
            for (int i = 0; i < titles.Length && i < cols.Length - 1; i++)
            {
                var t = new TextBlock
                {
                    Text = titles[i], FontSize = WhoHeadFont, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Theme.TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = i >= rightFrom ? TextAlignment.Right : TextAlignment.Left,
                };
                g.Children.Add(WhoCellBox(t, i, true, i == joinRight));
            }
            return new Border { Height = WhoHeadH, Child = g };
        }

        private static Border Row(GridLength[] cols, int index, int joinRight, params UIElement[] cells)
        {
            var g = Columns(cols);
            for (int i = 0; i < cells.Length && i < cols.Length - 1; i++)
            {
                var fe = cells[i] as FrameworkElement;
                if (fe != null) fe.Margin = new Thickness(0);
                g.Children.Add(WhoCellBox(cells[i], i, false, i == joinRight));
            }
            var row = new Border { Height = WhoRowH, Child = g };
            WhoPaint(row, index, null);
            // Hover only on a row that shows something: an empty placeholder row stays still.
            row.MouseEnter += (s, e) => WhoPaint(row, index, row.Tag as bool?);
            row.MouseLeave += (s, e) => WhoPaint(row, index, row.Tag as bool?);
            return row;
        }

        // A row's background, by the model's AppDataGridRowStyle: selected → TableRowSelected,
        // under the pointer → TableRowHover, odd rows → TableRowAlt (Theme).
        // state: null = an empty row (stripe only), false = a row with data, true = it is selected.
        // Kept in Tag so the hover handlers know it.
        private static void WhoPaint(Border row, int index, bool? state)
        {
            row.Tag = state;
            if (state == true) row.Background = Theme.TableRowSelected;
            else if (state != null && row.IsMouseOver) row.Background = Theme.TableRowHover;
            else if (index % 2 == 1) row.Background = Theme.TableRowAlt;
            else row.Background = Brushes.Transparent;
        }
    }
}
