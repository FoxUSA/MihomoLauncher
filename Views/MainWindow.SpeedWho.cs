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
    // размера»): the header, cell padding, hairlines and row colours of Themes\Common\DataGridStyles.xaml,
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
        // Lines that stand side by side in two columns must be one height whatever is in them
        // (2026-11-25: the place's first line holds the 17 px country badge, the name's does not —
        // the IP under the place sat lower than the time under the name): the first line 18, every
        // small line under it 13, Consolas or Segoe alike — their natural heights (18 / 14 spread
        // the lines apart, 2026-11-25); the country badge is 16 to fit the first line.
        private const double WhoLine1H = 16, WhoLineSmallH = 13;
        private static TextBlock SmallLine(TextBlock t)
        {
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.LineHeight = WhoLineSmallH;
            return t;
        }
        // A PROGRAM row with the clock's time: three lines — name / date / time (its connections go
        // under the figure). An address row stays WhoRowH: name / time | place / IP is two lines
        // (2026-11-25: 50 for both left the address rows half empty). The boxes never change height
        // («окно менять нельзя по высоте»): 8 × 36 = 288 holds exactly 6 program rows of 48.
        private const double WhoRowH3 = 48;
        private bool _whoShowTime;            // the clock button, for this session
        private bool _whoShowIp = true;       // the «IP» button, for this session
        private static readonly FontFamily WhoMono = new FontFamily("Consolas");

        // The model table's metrics (Themes\Common\DataGridStyles.xaml): header 34 high, 13 semibold; cells
        // 12 with 8 px sides; hairlines right and bottom of every cell, the first column also left.
        private const double WhoHeadH = 34;
        private const double WhoHeadFont = 13;
        private const double WhoCellFont = 12;
        private const double WhoCellPad = 8;
        // What a cell adds around its text: both side paddings + its right hairline (+1 more for the
        // first column's left one — not counted, it is within the «air» added on top).
        private const double WhoCellChrome = 2 * WhoCellPad + 1;

        private string AsnPath => _mihomo == null ? null : System.IO.Path.Combine(_mihomo.WorkDir, "ASN.mmdb");

        // ---- sorting (2026-11-25) ----
        // A click on a column title sorts by what that column shows NOW («сортировка сортирует то,
        // что сейчас есть в его столбике»): «N соед.» is under the name, or under the figure while
        // the clock shows; the IP under the address name, or under the place. A column holding
        // several things steps through them and says which under its title («по времени»); one
        // holding one thing (provider, ↓ DL, ↑ UL) turns it round on a second click, no caption.
        // Time — the default — always belongs to the name column (its line, when shown, is there).
        internal enum WhoSort { Time, Name, Conns, Down, Up, Ip, Place, Isp }

        private sealed class WhoSortState
        {
            public WhoSort Key = WhoSort.Time;
            public bool Rev;   // the other way from the key's natural order

            public static WhoSortState Parse(string s)
            {
                var st = new WhoSortState();
                s = (s ?? "").Trim();
                if (s.EndsWith("!")) { st.Rev = true; s = s.Substring(0, s.Length - 1); }
                WhoSort k;
                if (Enum.TryParse(s, out k)) st.Key = k; else st.Rev = false;
                return st;
            }
            public override string ToString() => Key == WhoSort.Time && !Rev ? "" : Key + (Rev ? "!" : "");
        }

        // Natural order: time, figures, connections — biggest / newest first; words and IPs — A to Z.
        private static bool WhoSortDesc(WhoSort k) => k == WhoSort.Time || k == WhoSort.Conns || k == WhoSort.Down || k == WhoSort.Up;

        private static string WhoSortCaption(WhoSort k, bool live)
        {
            switch (k)
            {
                case WhoSort.Time: return Loc.T("Main_WhoSortTime");
                case WhoSort.Name: return Loc.T("Main_WhoSortName");
                case WhoSort.Conns: return Loc.T("Main_WhoSortConns");
                case WhoSort.Ip: return Loc.T("Main_WhoSortIp");
                case WhoSort.Place: return Loc.T("Main_WhoSortPlace");
                case WhoSort.Down: return Loc.T("Main_WhoSortDown");
                default: return "";
            }
        }

        // Compares by `key` (null = has none: always last, whichever way), then `tie`.
        private static int WhoCmp<T>(T a, T b, Func<T, IComparable> key, bool desc, Comparison<T> tie)
        {
            var ka = key(a); var kb = key(b);
            if (ka == null && kb != null) return 1;
            if (ka != null && kb == null) return -1;
            if (ka != null)
            {
                int c = ka is string sa ? string.Compare(sa, (string)kb, StringComparison.CurrentCultureIgnoreCase) : ka.CompareTo(kb);
                if (c != 0) return desc ? -c : c;
            }
            return tie(a, b);
        }

        // An IP as text that sorts in address order: IPv4 first, each byte as two hex digits.
        private static string WhoIpKey(string ip)
        {
            System.Net.IPAddress a;
            if (string.IsNullOrEmpty(ip) || !System.Net.IPAddress.TryParse(ip, out a)) return null;
            var b = a.GetAddressBytes();
            return (b.Length == 4 ? "4" : "6") + BitConverter.ToString(b);
        }

        private sealed class WhoProg
        {
            public string Key, Name, Path;
            public List<WhoConn> Conns;
            public int Open;           // of Conns, still open
            public double Down, Up;    // live: bytes/s; archive: bytes
            public long Bytes;
            public int Count;          // archive: connections in the period
            public List<WhoAddr> Addrs; // archive: already added up
            public DateTime First = DateTime.MinValue, Last = DateTime.MinValue;   // as WhoAddr's
            public void Saw(DateTime start, DateTime end) => WhoSaw(ref First, ref Last, start, end);
        }

        // The earliest start / latest end seen (UTC; MinValue = unknown; a missing start counts as the end).
        private static void WhoSaw(ref DateTime first, ref DateTime last, DateTime start, DateTime end)
        {
            if (start == DateTime.MinValue) start = end;
            if (start != DateTime.MinValue && (first == DateTime.MinValue || start < first)) first = start;
            if (end > last) last = end;
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
                var rs = r.Start > 0 ? ConnArchive.TimeOf(r.Start) : DateTime.MinValue;
                a.Saw(rs, ConnArchive.TimeOf(r.End));
                p.Saw(rs, ConnArchive.TimeOf(r.End));
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
            // UTC: the earliest of its connections opened / the latest was last seen (MinValue = unknown).
            public DateTime First = DateTime.MinValue, Last = DateTime.MinValue;
            public void Saw(DateTime start, DateTime end) => WhoSaw(ref First, ref Last, start, end);
        }

        // When, in the system's date and time format, whole — nothing cut (2026-11-25: «25.11.2026
        // 10:18 – 13:12» did not fit the program column and ended in «…»). The date on one line,
        // the time under it: one connection — when it opened, to the second («25.11.2026» /
        // «10:43:15»); several — from the first to the last («25.11.2026» / «10:18 – 13:12»;
        // over midnight both dates: «24.11.2026 – 25.11.2026» / «23:50 – 00:10»).
        // twoLines: the date over the time — for the narrow program column only; the addresses
        // have room for one line (2026-11-25).
        private static string WhoWhen(int count, DateTime first, DateTime last, bool twoLines)
        {
            if (first == DateTime.MinValue && last == DateTime.MinValue) return "—";
            var f = (first != DateTime.MinValue ? first : last).ToLocalTime();
            var l = (last != DateTime.MinValue ? last : first).ToLocalTime();
            var c = CultureInfo.CurrentCulture;
            string br = twoLines ? "\n" : " ";
            if (count <= 1 || (l - f).TotalSeconds < 60) return f.ToString("d", c) + br + f.ToString("T", c);
            if (f.Date == l.Date) return f.ToString("d", c) + br + f.ToString("t", c) + " – " + l.ToString("t", c);
            return twoLines
                ? f.ToString("d", c) + " – " + l.ToString("d", c) + "\n" + f.ToString("t", c) + " – " + l.ToString("t", c)
                : f.ToString("d", c) + " " + f.ToString("t", c) + " – " + l.ToString("d", c) + " " + l.ToString("t", c);
        }

        // The «кто и куда» block and its refresh. Registers the tile for collection; the caller
        // calls the returned Action on close.
        // detached: the same view in a window of its own the user sizes (the ⧉ in the corner of the
        // addresses, 2026-11-16) — the tables then fill whatever height and width it is given.
        //
        // span: live = «Сейчас» (the last minute, refreshed every 2 s); else the archive of that
        // stretch (ConnArchive + what is open right now when it runs up to now), read once when
        // chosen and then left still — no rows jumping while it is looked at. setPeriod switches it.
        private UIElement BuildSpeedWho(AppEntry entry, Window win, bool detached, string initialSelected, SpeedSpan initialSpan,
                                        string initialMask, out Action stop, out Action<SpeedSpan> setPeriod)
        {
            string selected = initialSelected;
            Wpf.Ui.Controls.TextBox search = null;   // made below, read by Refresh
            // The addresses' own pick (2026-11-25): a click on an address selects it and the arrows
            // then move through the addresses; a click on a program gives them back to the programs.
            bool addrPane = false;
            string selAddr = null;
            List<WhoAddr> shownAddrs = new List<WhoAddr>();
            string AddrId(WhoAddr a) => !string.IsNullOrEmpty(a.Host) ? a.Host : a.Ip ?? "";
            // The clock button (2026-11-25): when — a third line in every row of both tables, the
            // rows that much taller. Remembered for the session (the next window opens the same).
            bool showTime = _whoShowTime;
            bool showIp = _whoShowIp;   // the «IP» button: the address line under each name
            var progSort = WhoSortState.Parse(_config.WhoSortProgs);
            var addrSort = WhoSortState.Parse(_config.WhoSortAddrs);
            double RowH() => showTime ? WhoRowH3 : WhoRowH;   // a program row; an address row is always WhoRowH
            // Program rows that fill the box (it stays WhoRows × WhoRowH tall): 8, or 6 with the time.
            int ProgMinRows() => (int)Math.Round(WhoRows * WhoRowH / RowH());
            var progWhen = new List<TextBlock>();
            var aWhen = new List<TextBlock>();
            TextBlock WhenCell() => SmallLine(new TextBlock
            {
                // Two lines for a span (WhoWhen), never cut.
                FontSize = 11, Foreground = Theme.TextMuted, FontFamily = WhoMono,
                Visibility = showTime ? Visibility.Visible : Visibility.Collapsed,
            });
            List<WhoProg> progs = new List<WhoProg>();
            Action refresh = null;   // the row clicks are wired before Refresh exists
            SpeedSpan span = initialSpan ?? SpeedSpan.Now();
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
            var progSubR = new List<TextBlock>();   // the same, under the figure while the time shows
            var progPad = new List<Border>();       // and an empty line under it
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
                RevealTrimmed(name);
                name.Height = WhoLine1H;
                var sub = SmallLine(Cell(11, Theme.TextMuted));
                var when = WhenCell();
                var down = Num();
                down.Height = WhoLine1H;
                // With the time shown, «89 соед.» moves under the figure (2026-11-25): the name
                // column then holds name / date / time.
                var subR = SmallLine(Cell(11, Theme.TextMuted));
                subR.TextAlignment = TextAlignment.Right;
                subR.HorizontalAlignment = HorizontalAlignment.Right;
                sub.Visibility = showTime ? Visibility.Collapsed : Visibility.Visible;
                subR.Visibility = showTime ? Visibility.Visible : Visibility.Collapsed;
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(name);
                stack.Children.Add(sub);
                stack.Children.Add(when);
                down.HorizontalAlignment = HorizontalAlignment.Right;
                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(down);
                right.Children.Add(subR);
                // An empty third line opposite the time's second: both columns are three lines
                // tall, so «89 соед.» stands level with the date.
                var pad = new Border { Height = WhoLineSmallH, Visibility = subR.Visibility };
                right.Children.Add(pad);
                progPad.Add(pad);
                var row = Row(progCols, i, -1, stack, right);
                row.Height = RowH();
                progWhen.Add(when);
                progSubR.Add(subR);
                int idx = i;
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonDown += (s, e) =>
                {
                    if (idx >= progs.Count) return;
                    var p = progs[idx];
                    e.Handled = true;
                    if (e.ClickCount >= 2) { ShowInExplorer(win, p); return; }
                    if (selected != p.Key) selAddr = null;
                    selected = p.Key;
                    addrPane = false;
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
                progTable.Children.Add(StripedUnder(progScroll, () => RowH(), out progStripes));
            }
            var leftStack = new DockPanel();
            leftStack.Children.Add(leftHead);
            leftStack.Children.Add(WhoWell(progTable));
            var left = Pane(leftStack, new Thickness(6));

            // ---- right: where the selected program goes ----
            var rightHead = PaneTitle();
            var rightHeadRow = new DockPanel();
            DockPanel.SetDock(rightHeadRow, Dock.Top);
            Action toggleTime = null, toggleIp = null;   // set once both tables exist
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
                detach.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenSpeedWhoWindow(entry, selected, span, search?.Text); };
                DockPanel.SetDock(detach, Dock.Right);
                rightHeadRow.Children.Add(detach);
            }
            // Switches left of ⧉ (2026-11-25), lit while on: the clock — when, a third line in both
            // tables; «IP» — the address line under each name (on by default).
            void AddSwitch(UIElement content, Func<bool> isOn, Action flip, string tip, double rightMargin)
            {
                var b = new Border
                {
                    Width = 28, Height = 24, CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(4, 0, rightMargin, 4), Cursor = Cursors.Hand, ToolTip = tip,
                    Child = content,
                };
                void Paint(bool hover)
                {
                    bool on = isOn();
                    b.Background = on ? Theme.ChipBg : hover ? Theme.ChipBgHover : Brushes.Transparent;
                    b.BorderBrush = Theme.CardBorder;
                    b.BorderThickness = new Thickness(on ? 1 : 0);
                    var fg = on || hover ? Theme.TextPrimary : Theme.TextSecondary;
                    if (content is Wpf.Ui.Controls.SymbolIcon si) si.Foreground = fg;
                    else if (content is TextBlock tb) tb.Foreground = fg;
                }
                Paint(false);
                b.MouseEnter += (s, e) => Paint(true);
                b.MouseLeave += (s, e) => Paint(false);
                b.MouseLeftButtonUp += (s, e) => { e.Handled = true; flip?.Invoke(); Paint(b.IsMouseOver); };
                DockPanel.SetDock(b, Dock.Right);
                rightHeadRow.Children.Add(b);
            }
            AddSwitch(new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Clock24, FontSize = 16 },
                      () => showTime, () => toggleTime?.Invoke(), Loc.T("Main_WhoTimeTip"), detached ? 2 : 0);
            AddSwitch(new TextBlock { Text = "IP", FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                      () => showIp, () => toggleIp?.Invoke(), Loc.T("Main_WhoIpTip"), 0);
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
            var aIpR = new List<TextBlock>();   // the same line under the place (while the time shows)
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
            double[] leastBase = { 120, 110, 90 };
            double[] least = (double[])leastBase.Clone();   // never under what the column's title needs
            double dlW = 66, ulW = 60;   // ↓ / ↑: 66 / 60, or what their titles need (the sort mark)
            void LayoutAddr()
            {
                double view = addrScroll.ViewportWidth > 0 ? addrScroll.ViewportWidth : addrScroll.ActualWidth;
                if (view <= 0) return;
                // ↓ + ↑ + gutter are fixed; the three text columns share the rest.
                var w = WhoFit(want, least, view - dlW - ulW - Gutter);
                foreach (var g in aGrids)
                {
                    for (int k = 0; k < 3; k++) g.ColumnDefinitions[k].Width = new GridLength(w[k]);
                    g.ColumnDefinitions[4].Width = new GridLength(dlW);
                    g.ColumnDefinitions[5].Width = new GridLength(ulW);
                }
                SyncGutter(addrScroll, (Grid)addrHeader.Child);
            }
            addrScroll.SizeChanged += (s, e) => LayoutAddr();
            if (detached)
            {
                addrTable.Children.Remove(addrScroll);
                addrTable.Children.Add(StripedUnder(addrScroll, () => WhoRowH, out addrStripes));
            }
            addrScroll.ScrollChanged += (s, e) => { if (e.ViewportWidthChange != 0) LayoutAddr(); };
            void AddAddrRow()
            {
                int i = aRows.Count;
                var host = Cell(WhoCellFont, Theme.TextPrimary, FontWeights.SemiBold);
                RevealTrimmed(host);
                host.Height = WhoLine1H;
                var ip = SmallLine(Cell(11, Theme.TextMuted));
                ip.FontFamily = WhoMono;
                // With the time shown the IP moves under the place (ipR), as «89 соед.» moves under
                // the figure for a program (2026-11-25): name / time | place / IP.
                var ipR = SmallLine(Cell(11, Theme.TextMuted));
                ipR.FontFamily = WhoMono;
                var when = WhenCell();
                var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                hs.Children.Add(host);
                hs.Children.Add(ip);
                hs.Children.Add(when);
                aWhen.Add(when);

                var cc = CountryBadge("");
                cc.Margin = new Thickness(0, 0, 6, 0);
                var ccText = (TextBlock)cc.Child;
                var place = Cell(12, Theme.TextPrimary);
                RevealTrimmed(place);
                var geo = new DockPanel { LastChildFill = true, Height = WhoLine1H };
                DockPanel.SetDock(cc, Dock.Left);
                geo.Children.Add(cc);
                geo.Children.Add(place);
                var geoCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                geoCell.Children.Add(geo);
                geoCell.Children.Add(ipR);
                aIpR.Add(ipR);

                var isp = Cell(12, Theme.TextSecondary);
                RevealTrimmed(isp);
                var down = Num();
                var up = Num();
                up.Foreground = Theme.TextSecondary;
                var arow = Row(addrCols, i, 2, hs, geoCell, isp, new Border(), down, up);   // WhoRowH tall, always
                int idx = i;
                arow.MouseLeftButtonDown += (s, e) =>
                {
                    if (idx >= shownAddrs.Count) return;
                    e.Handled = true;
                    selAddr = AddrId(shownAddrs[idx]);
                    addrPane = true;
                    refresh?.Invoke();
                };
                // Right click (2026-11-25): «Скопировать строку» — the address, IP:port, place and
                // provider on one line; no figures.
                arow.MouseRightButtonUp += (s, e) =>
                {
                    if (idx >= shownAddrs.Count) return;
                    e.Handled = true;
                    var a = shownAddrs[idx];
                    var item = new MenuItem { Header = Loc.T("Main_WhoCopyRow") };
                    item.Click += (s2, e2) => CopyAddrRow(a);
                    var menu = new ContextMenu { PlacementTarget = arow };
                    menu.Items.Add(item);
                    menu.IsOpen = true;
                };
                addrBody.Children.Add(arow);
                aRows.Add(arow);
                aGrids.Add((Grid)arow.Child);
                aHost.Add(host); aIp.Add(ip); aCc.Add(cc); aCcText.Add(ccText); aPlace.Add(place); aIsp.Add(isp); aDown.Add(down); aUp.Add(up);
                PlaceIp(i);
            }
            // Where the IP line is: under the name, or under the place while the time shows; none with «IP» off.
            void PlaceIp(int i)
            {
                aIp[i].Visibility = showIp && !showTime ? Visibility.Visible : Visibility.Collapsed;
                aIpR[i].Visibility = showIp && showTime ? Visibility.Visible : Visibility.Collapsed;
            }
            for (int i = 0; i < WhoRows; i++) AddAddrRow();
            toggleTime = () =>
            {
                showTime = !showTime;
                _whoShowTime = showTime;
                var vis = showTime ? Visibility.Visible : Visibility.Collapsed;
                foreach (var r in progRows) r.Height = RowH();
                foreach (var t in progWhen) t.Visibility = vis;
                foreach (var t in aWhen) t.Visibility = vis;
                foreach (var t in progSubR) t.Visibility = vis;
                foreach (var b in progPad) b.Visibility = vis;
                foreach (var t in progSub) t.Visibility = showTime ? Visibility.Collapsed : Visibility.Visible;
                for (int i = 0; i < aIp.Count; i++) PlaceIp(i);
                // The boxes keep their height (the window does not change): fewer, taller program rows.
                refresh?.Invoke();
            };
            toggleIp = () =>
            {
                showIp = !showIp;
                _whoShowIp = showIp;
                for (int i = 0; i < aIp.Count; i++) PlaceIp(i);
                refresh?.Invoke();   // the address column's width follows what is shown
            };

            // ---- sorting: what each column holds right now ----
            WhoSort[] ProgColKeys(int col)
            {
                if (col == 0) return showTime ? new[] { WhoSort.Time, WhoSort.Name } : new[] { WhoSort.Time, WhoSort.Name, WhoSort.Conns };
                if (col == 1) return showTime ? new[] { WhoSort.Down, WhoSort.Conns } : new[] { WhoSort.Down };
                return new WhoSort[0];
            }
            WhoSort[] AddrColKeys(int col)
            {
                switch (col)
                {
                    case 0: return showIp && !showTime ? new[] { WhoSort.Time, WhoSort.Name, WhoSort.Ip, WhoSort.Conns } : new[] { WhoSort.Time, WhoSort.Name, WhoSort.Conns };
                    case 1: return showIp && showTime ? new[] { WhoSort.Place, WhoSort.Ip } : new[] { WhoSort.Place };
                    case 2: return new[] { WhoSort.Isp };
                    case 4: return new[] { WhoSort.Down };
                    case 5: return new[] { WhoSort.Up };
                    default: return new WhoSort[0];
                }
            }
            int[] progSortCols = { 0, 1 }, addrSortCols = { 0, 1, 2, 4, 5 };
            // The column the sort is in now; -1 = its value is shown nowhere (the IP with «IP» off):
            // then by time until it shows again — the choice itself is kept.
            int SortCol(WhoSortState st, Func<int, WhoSort[]> keys, int[] cols)
            {
                foreach (int c in cols) if (keys(c).Contains(st.Key)) return c;
                return -1;
            }
            WhoSortState Effective(WhoSortState st, Func<int, WhoSort[]> keys, int[] cols)
                => SortCol(st, keys, cols) >= 0 ? st : new WhoSortState();

            void SortClick(WhoSortState st, Func<int, WhoSort[]> keys, int[] cols, int col)
            {
                var k = keys(col);
                if (k.Length == 0) return;
                var eff = Effective(st, keys, cols);
                int at = Array.IndexOf(k, eff.Key);
                if (at < 0) { st.Key = k[0]; st.Rev = false; }
                else if (k.Length == 1) { st.Key = k[0]; st.Rev = !eff.Rev; }
                else { st.Key = k[(at + 1) % k.Length]; st.Rev = false; }
                _config.WhoSortProgs = progSort.ToString();
                _config.WhoSortAddrs = addrSort.ToString();
                try { AppConfigStore.Save(_config); }
                catch (Exception ex) { Log.Error("who-sort-save", ex); }
                refresh?.Invoke();
            }
            void WireSort(Border header, WhoSortState st, Func<int, WhoSort[]> keys, int[] cols)
            {
                foreach (int c in cols)
                {
                    var cell = (Border)((Grid)header.Child).Children[c];
                    int col = c;
                    cell.Cursor = Cursors.Hand;
                    cell.ToolTip = Loc.T("Main_WhoSortTip");
                    cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; SortClick(st, keys, cols, col); };
                }
            }
            WireSort(progHeader, progSort, ProgColKeys, progSortCols);
            WireSort(addrHeader, addrSort, AddrColKeys, addrSortCols);

            // A title: the sorted column in the accent; one that holds several things says under its
            // title which one (a second small line, inside the same header height); one that holds
            // one thing shows only which way (▾ biggest / newest first, ▴ smallest / A first).
            void PaintHead(Border header, int col, string title, WhoSortState st, Func<int, WhoSort[]> keys, int[] cols, bool live)
            {
                var t = WhoHeadText(header, col);
                t.Inlines.Clear();
                var eff = Effective(st, keys, cols);
                bool on = SortCol(st, keys, cols) == col || (SortCol(st, keys, cols) < 0 && col == 0);
                var k = keys(col);
                t.Foreground = on ? Theme.Accent : Theme.TextPrimary;
                t.Inlines.Add(new Run(title));
                if (on && k.Length > 1)
                {
                    t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                    t.LineHeight = 15;
                    t.Inlines.Add(new LineBreak());
                    t.Inlines.Add(new Run(WhoSortCaption(eff.Key, live)) { FontSize = 11, FontWeight = FontWeights.Normal });
                }
                else
                {
                    t.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                    t.ClearValue(TextBlock.LineHeightProperty);
                    if (on) t.Inlines.Add(new Run((WhoSortDesc(eff.Key) != eff.Rev) ? " ▾" : " ▴") { FontSize = 11 });
                }
            }
            void PaintHeads(bool live)
            {
                PaintHead(progHeader, 0, Loc.T("Main_WhoColProgram"), progSort, ProgColKeys, progSortCols, live);
                PaintHead(progHeader, 1, Loc.T(live ? "Main_WhoColDown" : "Main_WhoColVol"), progSort, ProgColKeys, progSortCols, live);
                PaintHead(addrHeader, 0, Loc.T("Main_WhoColAddr"), addrSort, AddrColKeys, addrSortCols, live);
                PaintHead(addrHeader, 1, Loc.T("Main_WhoColWhere"), addrSort, AddrColKeys, addrSortCols, live);
                PaintHead(addrHeader, 2, Loc.T("Main_WhoColIsp"), addrSort, AddrColKeys, addrSortCols, live);
                PaintHead(addrHeader, 4, Loc.T(live ? "Main_WhoColDown" : "Main_WhoColVol"), addrSort, AddrColKeys, addrSortCols, live);
                PaintHead(addrHeader, 5, Loc.T("Main_WhoColUp"), addrSort, AddrColKeys, addrSortCols, live);
            }

            // Ties: the bigger traffic first, then by name — rows never swap at random.
            int ProgTie(WhoProg a, WhoProg b)
            {
                int c = b.Bytes.CompareTo(a.Bytes);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            }
            int AddrTie(WhoAddr a, WhoAddr b)
            {
                int c = b.Bytes.CompareTo(a.Bytes);
                return c != 0 ? c : string.Compare(AddrId(a), AddrId(b), StringComparison.OrdinalIgnoreCase);
            }
            // Time: when it started (the date line, then the time in it), then when last seen —
            // the start does not move while a connection lasts, so the live list does not jump.
            IComparable TimeKey(DateTime first, DateTime last)
            {
                var f = first != DateTime.MinValue ? first : last;
                if (f == DateTime.MinValue) return null;
                return f.Ticks.ToString("D19") + (last != DateTime.MinValue ? last : f).Ticks.ToString("D19");
            }
            void SortProgs(List<WhoProg> list, bool live)
            {
                var st = Effective(progSort, ProgColKeys, progSortCols);
                bool desc = WhoSortDesc(st.Key) != st.Rev;
                Func<WhoProg, IComparable> key;
                switch (st.Key)
                {
                    case WhoSort.Name: key = p => p.Name; break;
                    case WhoSort.Conns: key = p => live ? p.Open : p.Count; break;
                    case WhoSort.Down: key = p => p.Down; break;
                    case WhoSort.Up: key = p => p.Up; break;
                    default: key = p => TimeKey(p.First, p.Last); break;
                }
                list.Sort((a, b) => WhoCmp(a, b, key, desc, ProgTie));
            }
            void SortAddrs(List<WhoAddr> list, string asnPath)
            {
                var st = Effective(addrSort, AddrColKeys, addrSortCols);
                bool desc = WhoSortDesc(st.Key) != st.Rev;
                Func<WhoAddr, IComparable> key;
                switch (st.Key)
                {
                    case WhoSort.Name: key = a => !string.IsNullOrEmpty(a.Host) ? a.Host : string.IsNullOrEmpty(a.Ip) ? null : a.Ip; break;
                    case WhoSort.Conns: key = a => a.Count; break;
                    case WhoSort.Down: key = a => a.Down; break;
                    case WhoSort.Up: key = a => a.Up; break;
                    case WhoSort.Ip: key = a => WhoIpKey(a.Ip); break;
                    case WhoSort.Place:
                        key = a =>
                        {
                            if (string.IsNullOrEmpty(a.Ip)) return null;
                            var g = GeoIpService.Lookup(a.Ip);
                            var place = GeoIpService.PlaceOf(g);
                            if (place.Length == 0 && IsPrivate(a.Ip)) place = Loc.T("Main_WhoLocal");
                            var iso = g?.IsoCode ?? "";
                            return iso.Length == 0 && place.Length == 0 ? null : iso + " " + place;
                        };
                        break;
                    case WhoSort.Isp:
                        key = a =>
                        {
                            var org = string.IsNullOrEmpty(a.Ip) ? "" : GeoIpService.OrgOf(a.Ip, asnPath);
                            return org.Length > 0 ? org : null;
                        };
                        break;
                    default: key = a => TimeKey(a.First, a.Last); break;
                }
                list.Sort((a, b) => WhoCmp(a, b, key, desc, AddrTie));
            }
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

            // ---- search (2026-11-25): one line over both tables, so the two still start level ----
            // A mask over everything the tables show — program, address, IP, port, place, country
            // code, provider — within the period tab that is open. Empty: a «paste» icon where the
            // cross sits once there is text.
            search = new Wpf.Ui.Controls.TextBox
            {
                PlaceholderText = Loc.T("Main_WhoSearchHint"),
                ToolTip = Loc.T("Main_WhoSearchTip"),
                ClearButtonEnabled = true,
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Search16 },
                FontSize = 13,
                MinHeight = 0,
                Height = 32,
                Padding = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                // Our look, not the library's input fill (2026-11-25: «не в нашем стиле»): the
                // field is see-through and the frame around it is a block like «Выход в интернет».
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Text = initialMask ?? "",
            };
            PasteButton.SetIsEnabled(search, true);
            // The library's own underline (its accent line on focus) — our frame says it instead.
            search.Loaded += (s, e) => HideNamedParts(search, "AccentBorder");
            var searchBox = new Border
            {
                Background = Theme.CardBg,
                BorderBrush = Theme.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 10, 0, 0),
                Cursor = Cursors.IBeam,
                Child = search,
            };
            // Exactly as tall as the «Выход в интернет» line above it (2026-11-25).
            exitBox.SizeChanged += (s, e) =>
            {
                if (!e.HeightChanged || exitBox.ActualHeight <= 2) return;
                searchBox.Height = exitBox.ActualHeight;
                search.Height = exitBox.ActualHeight - 2;
            };
            void PaintSearchBox()
            {
                searchBox.Background = searchBox.IsMouseOver ? Theme.CardBgHover : Theme.CardBg;
                searchBox.BorderBrush = search.IsKeyboardFocusWithin ? Theme.TileBorderHover : Theme.CardBorder;
            }
            searchBox.MouseEnter += (s, e) => PaintSearchBox();
            searchBox.MouseLeave += (s, e) => PaintSearchBox();
            search.IsKeyboardFocusWithinChanged += (s, e) => PaintSearchBox();
            DockPanel.SetDock(searchBox, Dock.Top);
            var searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            searchTimer.Tick += (s, e) => { searchTimer.Stop(); refresh?.Invoke(); };
            search.TextChanged += (s, e) => { searchTimer.Stop(); searchTimer.Start(); };

            var root = new DockPanel();
            root.Children.Add(exitBox);
            root.Children.Add(searchBox);
            root.Children.Add(split);

            List<WhoAddr> AddrsOf(WhoProg p) => p.Addrs ?? p.Conns
                .GroupBy(w => AddrKey(w.C), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var f = g.First().C;
                    var a = new WhoAddr
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
                    foreach (var w in g) a.Saw(w.C.Start, w.Closed ? w.Seen : DateTime.UtcNow);
                    return a;
                })
                .OrderByDescending(a => a.Bytes).ThenBy(a => a.Host, StringComparer.OrdinalIgnoreCase).ToList();

            // Everything a row of the addresses' table shows (the geo / provider lookups are cached).
            bool AddrHit(WhoAddr a, Func<string, bool> m, string asnPath)
            {
                string host = a.Host ?? "", ip = a.Ip ?? "", port = a.Port ?? "";
                if (m(host) || m(ip) || m(a.Network ?? "")) return true;
                if (port.Length > 0 && (m(port) || m(ip + ":" + port))) return true;
                if (ip.Length == 0) return false;
                var g = GeoIpService.Lookup(ip);
                var place = GeoIpService.PlaceOf(g);
                if (place.Length == 0 && IsPrivate(ip)) place = Loc.T("Main_WhoLocal");
                if (m(place) || m(g?.IsoCode ?? "")) return true;
                return m(GeoIpService.OrgOf(ip, asnPath));
            }

            void Refresh()
            {
                RenderExit(entry, exitText);
                bool live = span.Live;

                // Column titles: speed now, or the volume over the period; the sort on them.
                PaintHeads(live);

                List<WhoConn> conns;
                if (!live || !_whoNow.TryGetValue(entry.Id, out conns) || conns == null) conns = new List<WhoConn>();
                progs = !live ? (archive ?? new List<WhoProg>()) : conns.GroupBy(w => ProgKey(w.C), StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var wp = new WhoProg
                        {
                            Key = g.Key,
                            Name = g.First().C.Process.Length > 0 ? g.First().C.Process : Loc.T("Main_WhoUnknownProc"),
                            Path = g.Select(w => w.C.ProcessPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                            Conns = g.ToList(),
                            Open = g.Count(w => !w.Closed),
                            Down = g.Sum(w => w.Down),
                            Up = g.Sum(w => w.Up),
                            Bytes = g.Sum(w => w.C.Down + w.C.Up),
                        };
                        foreach (var w in g) wp.Saw(w.C.Start, w.Closed ? w.Seen : DateTime.UtcNow);
                        return wp;
                    })
                    // By what they carried, not by this second's speed: rows don't jump around.
                    .OrderByDescending(p => p.Bytes).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

                // The search: a program stays if its name / path matches (then with all its
                // addresses) or any of its addresses does (then with those only).
                string asn = AsnPath;
                int allCount = progs.Count;
                var mask = DnsStats.MaskOf(search.Text);
                Dictionary<string, List<WhoAddr>> found = null;
                if (mask != null)
                {
                    found = new Dictionary<string, List<WhoAddr>>(StringComparer.OrdinalIgnoreCase);
                    var kept = new List<WhoProg>();
                    foreach (var p in progs)
                    {
                        var list = AddrsOf(p);
                        if (!mask(p.Name ?? "") && !mask(p.Path ?? "")) list = list.Where(a => AddrHit(a, mask, asn)).ToList();
                        if (list.Count == 0 && !mask(p.Name ?? "") && !mask(p.Path ?? "")) continue;
                        kept.Add(p);
                        found[p.Key] = list;
                    }
                    progs = kept;
                }
                SortProgs(progs, live);
                if (selected == null || !progs.Any(p => p.Key == selected)) selected = progs.Count > 0 ? progs[0].Key : null;

                leftHead.Text = Loc.T("Main_WhoPrograms")
                              + (mask != null ? " · " + Loc.T("Main_WhoFound", progs.Count, allCount) : progs.Count > 0 ? " · " + progs.Count : "")
                              + (live || archive == null ? "" : " · " + Loc.T("Main_WhoSnapshot", archiveAt.ToString("HH:mm")));
                int progShow = Math.Max(ProgMinRows(), progs.Count);
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
                    progSubR[i].Text = progSub[i].Text;
                    progWhen[i].Text = p == null ? "" : WhoWhen(live ? p.Conns.Count : p.Count, p.First, p.Last, true);
                    // Only closed ones left (within the last minute): dimmed, still there to see.
                    progRows[i].Opacity = live && p != null && p.Open == 0 ? 0.5 : 1;
                    if (live) SetSpeed(progDown[i], p == null ? (double?)null : p.Down);
                    else SetVolume(progDown[i], p == null ? (double?)null : p.Down);
                    WhoPaint(progRows[i], i, p == null ? (bool?)null : p.Key == selected);
                    progRows[i].ToolTip = p == null ? null : (p.Path.Length > 0 ? p.Path + "\n\n" : "") + Loc.T("Main_WhoTipOpen");
                    progRows[i].Cursor = p == null ? null : Cursors.Hand;
                }

                var sel = progs.FirstOrDefault(p => p.Key == selected);
                List<WhoAddr> addrs;
                if (sel == null) addrs = new List<WhoAddr>();
                else if (found == null || !found.TryGetValue(sel.Key, out addrs)) addrs = AddrsOf(sel);
                SortAddrs(addrs, asn);

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

                shownAddrs = addrs;
                if (selAddr != null && !addrs.Any(a => AddrId(a) == selAddr)) selAddr = null;
                int addrShow = Math.Max(WhoRows, addrs.Count);
                addrStripes?.Invoke(addrShow);
                while (aRows.Count < addrShow) AddAddrRow();
                for (int i = 0; i < aRows.Count; i++)
                {
                    aRows[i].Visibility = i < addrShow ? Visibility.Visible : Visibility.Collapsed;
                    var a = i < addrs.Count ? addrs[i] : null;
                    if (a == null)
                    {
                        aHost[i].Text = aIp[i].Text = aIpR[i].Text = aPlace[i].Text = aIsp[i].Text = "";
                        aWhen[i].Text = "";
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
                    bool picked = selAddr != null && AddrId(a) == selAddr;
                    // The picked address is lit like the picked program while the arrows are its.
                    WhoPaint(aRows[i], i, picked && addrPane);
                    // Named: «173.194.182.8:443». A bare address is already the top line, so below it
                    // only «порт 59453 · UDP» (was a dangling «:59453»).
                    bool udp = string.Equals(a.Network, "udp", StringComparison.OrdinalIgnoreCase);
                    if (named)
                        aIp[i].Text = a.Ip + (a.Ip.Length > 0 && a.Port.Length > 0 ? ":" + a.Port : "") + (udp ? " · UDP" : "");
                    else
                        aIp[i].Text = (a.Port.Length > 0 ? Loc.T("Main_WhoPort", a.Port) : "") + (udp ? (a.Port.Length > 0 ? " · " : "") + "UDP" : "");
                    aIpR[i].Text = aIp[i].Text;
                    aWhen[i].Text = WhoWhen(a.Count, a.First, a.Last, false);

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

                // What each text column needs: its header — whole, with the sort caption or mark
                // (2026-11-25: «нигде не должно быть обрезаний в заголовках»; «по вр…» under an
                // empty table's «Адрес») — then its longest line among the rows. A column is never
                // squeezed under its header, the ↓ / ↑ ones widen for it too.
                for (int k = 0; k < 3; k++)
                {
                    want[k] = HeadWidth(addrHeader, k);
                    least[k] = Math.Max(leastBase[k], want[k] + WhoCellChrome);
                }
                dlW = Math.Max(66, HeadWidth(addrHeader, 4) + WhoCellChrome);
                ulW = Math.Max(60, HeadWidth(addrHeader, 5) + WhoCellChrome);
                double progDl = Math.Max(72, HeadWidth(progHeader, 1) + WhoCellChrome);
                foreach (var g in new[] { (Grid)progHeader.Child }.Concat(progRows.Select(r => (Grid)r.Child)))
                    g.ColumnDefinitions[1].Width = new GridLength(progDl);
                for (int i = 0; i < addrs.Count; i++)
                {
                    want[0] = Math.Max(want[0], Math.Max(WhoTextWidth(aHost[i].Text, WhoCellFont, FontWeights.SemiBold, null),
                                                         Math.Max(showIp && !showTime ? WhoTextWidth(aIp[i].Text, 11, FontWeights.Normal, WhoMono) : 0,
                                                                  showTime ? WhoTextWidth(aWhen[i].Text, 11, FontWeights.Normal, WhoMono) : 0)));
                    double badge = aCc[i].Visibility == Visibility.Visible ? BadgeWidth + 6 : 0;
                    want[1] = Math.Max(want[1], Math.Max(badge + WhoTextWidth(aPlace[i].Text, 12, FontWeights.Normal, null),
                                                         showIp && showTime ? WhoTextWidth(aIpR[i].Text, 11, FontWeights.Normal, WhoMono) : 0));
                    want[2] = Math.Max(want[2], WhoTextWidth(aIsp[i].Text, 12, FontWeights.Normal, null));
                }
                for (int k = 0; k < 3; k++) want[k] += WhoCellChrome + 8;   // the cell's padding and hairline + a little air
                LayoutAddr();

                if (sel != null) empty.Text = "";
                else if (mask != null && allCount > 0) empty.Text = Loc.T("Main_WhoNoMatch");
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
                var sp = span;
                archive = null;
                Refresh();
                var now = DateTime.UtcNow;
                var open = new List<ConnArchive.Rec>();
                Dictionary<string, WhoConn> map;
                if (sp.ToUtc == null && _whoRecent.TryGetValue(entry.Id, out map))
                    foreach (var w in map.Values)
                        if (!w.Closed) open.Add(ConnArchive.From(entry.Id, w.C, w.C.Down - w.ArchDown, w.C.Up - w.ArchUp, now));
                List<WhoProg> result;
                var unknown = Loc.T("Main_WhoUnknownProc");
                try
                {
                    result = await System.Threading.Tasks.Task.Run(() =>
                    {
                        long to = sp.ToUtc == null ? long.MaxValue : ConnArchive.SecOf(sp.ToUtc.Value);
                        return WhoAggregate(ConnArchive.Read(entry.Id, sp.FromUtc).Where(r => r.End <= to).Concat(open), unknown);
                    }).ConfigureAwait(true);
                }
                catch (Exception ex) { Log.Error("conn-archive-load", ex); result = new List<WhoProg>(); }
                if (gen != loadGen) return;
                archive = result;
                archiveAt = DateTime.Now;
                Refresh();
            }

            refresh = Refresh;
            if (span.Live) Refresh(); else LoadArchive();
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) => { if (span.Live) Refresh(); };
            timer.Start();
            // ↑ / ↓ move the selected program (2026-11-25), the list scrolls to it.
            KeyEventHandler keys = (s, e) =>
            {
                if (e.Key != Key.Up && e.Key != Key.Down) return;
                // In the search box too (it is one line: the arrows mean nothing else there).
                var focused = Keyboard.FocusedElement;
                if (focused is System.Windows.Controls.Primitives.TextBoxBase && !ReferenceEquals(focused, search)) return;
                if (addrPane && shownAddrs.Count > 0)
                {
                    // The addresses have the arrows (an address was clicked).
                    e.Handled = true;
                    int ca = shownAddrs.FindIndex(a => AddrId(a) == selAddr);
                    int na = ca < 0 ? 0 : Math.Max(0, Math.Min(shownAddrs.Count - 1, ca + (e.Key == Key.Down ? 1 : -1)));
                    if (na == ca) return;
                    selAddr = AddrId(shownAddrs[na]);
                    Refresh();
                    if (na < aRows.Count) aRows[na].BringIntoView();
                    return;
                }
                if (progs.Count == 0) return;
                e.Handled = true;
                int cur = progs.FindIndex(p => p.Key == selected);
                int next = cur < 0 ? 0 : Math.Max(0, Math.Min(progs.Count - 1, cur + (e.Key == Key.Down ? 1 : -1)));
                if (next == cur) return;
                selected = progs[next].Key;
                selAddr = null;
                Refresh();
                if (next < progRows.Count) progRows[next].BringIntoView();
            };
            if (win != null) win.PreviewKeyDown += keys;
            stop = () => { timer.Stop(); searchTimer.Stop(); loadGen++; if (win != null) win.PreviewKeyDown -= keys; };
            setPeriod = p =>
            {
                span = p ?? SpeedSpan.Now();
                if (span.Live) { archive = null; loadGen++; Refresh(); }
                else LoadArchive();
            };
            return root;
        }

        // One address row as text: «host · 1.2.3.4:443 · DE Франкфурт, ФРГ · Akamai International B.V.»
        // (a bare address: «1.2.3.4:443 · …»); parts it has none of are left out.
        private void CopyAddrRow(WhoAddr a)
        {
            var parts = new List<string>();
            string ip = a.Ip ?? "", port = a.Port ?? "";
            if (!string.IsNullOrEmpty(a.Host)) parts.Add(a.Host);
            if (ip.Length > 0) parts.Add(ip + (port.Length > 0 ? ":" + port : ""));
            else if (port.Length > 0) parts.Add(Loc.T("Main_WhoPort", port));
            if (ip.Length > 0)
            {
                var g = GeoIpService.Lookup(ip);
                var place = GeoIpService.PlaceOf(g);
                if (place.Length == 0 && IsPrivate(ip)) place = Loc.T("Main_WhoLocal");
                var iso = g?.IsoCode ?? "";
                string where = (iso + " " + place).Trim();
                if (where.Length > 0) parts.Add(where);
                var org = GeoIpService.OrgOf(ip, AsnPath);
                if (!string.IsNullOrEmpty(org)) parts.Add(org);
            }
            if (parts.Count == 0) return;
            try { Clipboard.SetText(string.Join(" · ", parts)); }
            catch (Exception ex) { Log.Error("who-copy-row", ex); }
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

        // A cell cut with «…»: under the pointer its whole text shows right over it, like Explorer's
        // names (2026-11-25: «при наведении показывай полный адрес, который обрезан»). Drawn in the
        // window itself (an adorner, not hit-testable): a popup window of its own flickered — the
        // pointer was then over it, not the cell — and stayed on top of other programs. After a
        // short pause, so a pass over the rows does not flash them. A cell that fits is left alone
        // (its tooltip as before); while the whole text shows, the tooltip is held back.
        private static RevealAdorner _reveal;
        private static System.Windows.Threading.DispatcherTimer _revealTimer;
        private static TextBlock _revealFor;
        private static FrameworkElement _revealCell;
        private const string WhoCellMark = "who-cell";   // WhoCellBox's Tag: the whole cell is the hover area

        // The pointer anywhere in the cell, not only on the text (2026-11-25: «наводить могу не
        // только на сам путь, но и на всю ячейку»). The cell is found once the row is in the tree.
        private void RevealTrimmed(TextBlock t)
        {
            bool hooked = false;
            t.Loaded += (s, e) =>
            {
                if (hooked) return;
                DependencyObject d = t;
                while (d != null && !(d is Border bd && Equals(bd.Tag, WhoCellMark))) d = VisualTreeHelper.GetParent(d);
                var cell = (d as FrameworkElement) ?? t;
                hooked = true;
                cell.MouseEnter += (s2, e2) => StartReveal(t, cell);
                cell.MouseLeave += (s2, e2) => { if (ReferenceEquals(_revealCell, cell)) HideReveal(); };
            };
            t.Unloaded += (s, e) => { if (ReferenceEquals(_revealFor, t)) HideReveal(); };
        }

        private void StartReveal(TextBlock t, FrameworkElement cell)
        {
            HideReveal();
            ToolTipService.SetIsEnabled(t, true);
            if (string.IsNullOrEmpty(t.Text) || t.ActualWidth <= 0 || !t.IsVisible) return;
            if (WhoTextWidth(t.Text, t.FontSize, t.FontWeight, t.FontFamily) <= t.ActualWidth + 0.5) return;
            ToolTipService.SetIsEnabled(t, false);
            _revealFor = t;
            _revealCell = cell;
            if (_revealTimer == null)
            {
                _revealTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _revealTimer.Tick += (s2, e2) =>
                {
                    _revealTimer.Stop();
                    var target = _revealFor;
                    if (target == null || _revealCell == null || !_revealCell.IsMouseOver || !target.IsVisible) return;
                    var layer = AdornerLayer.GetAdornerLayer(target);
                    if (layer == null) { Log.Info("Кто и куда: нет слоя для полного текста ячейки."); return; }
                    _reveal = new RevealAdorner(target);
                    layer.Add(_reveal);
                };
            }
            _revealTimer.Stop();
            _revealTimer.Start();
        }

        private static void HideReveal()
        {
            _revealTimer?.Stop();
            _revealFor = null;
            _revealCell = null;
            if (_reveal == null) return;
            AdornerLayer.GetAdornerLayer(_reveal.AdornedElement)?.Remove(_reveal);
            _reveal = null;
        }

        // The whole text of a cut cell, on a small plate laid exactly over the cell's text.
        private sealed class RevealAdorner : Adorner
        {
            private readonly Border _box;

            public RevealAdorner(TextBlock t) : base(t)
            {
                IsHitTestVisible = false;
                var text = new TextBlock
                {
                    Text = t.Text, FontSize = t.FontSize, FontWeight = t.FontWeight, FontFamily = t.FontFamily,
                    Foreground = Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center,
                };
                _box = new Border
                {
                    Background = Theme.OverlayCardBg, BorderBrush = Theme.PickerBorder, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 6, 0), Child = text,
                    MinHeight = t.ActualHeight + 2, IsHitTestVisible = false,
                };
                AddVisualChild(_box);
            }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => _box;
            protected override Size MeasureOverride(Size constraint)
            {
                _box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return _box.DesiredSize;
            }
            protected override Size ArrangeOverride(Size finalSize)
            {
                _box.Arrange(new Rect(new Point(-5, -1), _box.DesiredSize));
                return finalSize;
            }
        }

        // ---- table pieces ----

        // «RU», «EE», «UA» in boxes of one width, the letters centred (2026-11-16: «кубики …
        // одинаковые» — sized by their letters, «EE» came out narrower than «RU»).
        private const double BadgeWidth = 26;

        private static Border CountryBadge(string iso) => new Border
        {
            Width = BadgeWidth,
            Height = 16,   // = WhoLine1H (was 17 — it made the place's line taller than the name's)
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
        // 2026-11-25: 12 — the gutter is the well now (not striped), and 22 read as a third column.
        private const double Gutter = 12;

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
        // rowH: the rows' height now (taller while the clock shows the time line).
        private static Grid StripedUnder(ScrollViewer sv, Func<double> rowH, out Action<int> setShown)
        {
            const int Spare = 80;   // 80 × 36 — taller than any screen
            var fill = new StackPanel { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
            // Short of the scroll bar's gutter, like the rows.
            for (int i = 0; i < Spare; i++) fill.Children.Add(new Border { Height = WhoRowH, Margin = new Thickness(0, 0, Gutter, 0) });
            var host = new Grid { ClipToBounds = true };
            host.Children.Add(fill);
            host.Children.Add(sv);
            int shown = -1;
            double shownH = WhoRowH;
            void Apply()
            {
                fill.Visibility = sv.ScrollableHeight > 0.5 ? Visibility.Collapsed : Visibility.Visible;
            }
            sv.ScrollChanged += (s, e) => Apply();
            setShown = n =>
            {
                double h = rowH();
                if (n != shown || h != shownH)
                {
                    shown = n;
                    shownH = h;
                    foreach (Border b in fill.Children) b.Height = h;
                    fill.Margin = new Thickness(0, n * h, 0, 0);
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
        private void OpenSpeedWhoWindow(AppEntry entry, string selected, SpeedSpan span, string mask)
        {
            var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var win = new SpeedWindow(Loc.T("Main_WhoWinTitle", entry.IsAllTraffic ? AllTrafficName() : entry.Name), holder, resizable: true) { Owner = this };
            Action stop;
            Action<SpeedSpan> setPeriod;   // the detached window keeps the period it was opened with
            holder.Content = BuildSpeedWho(entry, win, true, selected, span, mask, out stop, out setPeriod);
            win.Closed += (s, e) => stop();
            win.Show();
        }

        // Collapses the template parts of that name (a library control's own decoration).
        private static void HideNamedParts(DependencyObject root, string name)
        {
            if (root == null) return;
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var fe = child as FrameworkElement;
                if (fe != null && fe.Name == name) fe.Visibility = Visibility.Collapsed;
                HideNamedParts(child, name);
            }
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
                // see-through but there for the pointer: the whole cell is a hover area (RevealTrimmed)
                Background = Brushes.Transparent,
                Tag = WhoCellMark,
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

        // How wide header cell i's title is, whole: its widest line (title + sort mark, the caption
        // under it), each run in its own size and weight. The text only, without the cell's padding.
        private double HeadWidth(Border header, int i)
        {
            var t = WhoHeadText(header, i);
            double line = 0, widest = 0;
            foreach (var inline in t.Inlines)
            {
                if (inline is LineBreak) { widest = Math.Max(widest, line); line = 0; continue; }
                if (inline is Run r) line += WhoTextWidth(r.Text, r.FontSize, r.FontWeight, null);
            }
            return Math.Ceiling(Math.Max(widest, line)) + 2;
        }

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
            // The scroll bar's gutter is the well, not part of the row (2026-11-25: stripes and the
            // selection ran on into it and it read as an empty third column).
            var gutter = new Border { Background = Theme.ListBg };
            Grid.SetColumn(gutter, cols.Length - 1);
            g.Children.Add(gutter);
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
