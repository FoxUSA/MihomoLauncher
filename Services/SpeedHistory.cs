using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MihomoLauncher.Services
{
    // The last 48 hours of every tile's speed, one bucket per minute (2026-11-16; 48 h since
    // 2026-11-21 — the «24 часа» tab compares itself with the 24 hours before) — what the speed
    // window draws. Fed once a second by the tile speed poll (MainWindow.TileSpeed), kept in
    // DataUser\speedhist.txt so a restart or an update does not wipe it.
    //
    // A bucket holds the bytes moved in that minute, the seconds actually measured (the engine may
    // have been down for part of it) and the fastest single second each way. Rates are bytes /
    // measured seconds; a minute nobody measured simply has no bucket — the chart leaves it empty.
    public static class SpeedHistory
    {
        public const int KeepMinutes = 7 * 24 * 60;   // the «7 дней» tab (2026-11-25; was 48 h)

        public sealed class Bucket
        {
            public long Minute;        // Unix minutes (UTC)
            public double Down, Up;    // bytes in this minute
            public double Secs;        // seconds measured
            public double PeakDown;    // fastest second, bytes/s
            public double PeakUp;      // fastest second up, bytes/s (2026-11-21)
            // The part of Down / Up that went through a VPN (2026-11-23, «Без VPN / По VPN»).
            // Split = false: a minute recorded before that, its route unknown.
            public double VpnDown, VpnUp;
            public bool Split = true;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<Bucket>> _byTile = new Dictionary<string, List<Bucket>>(StringComparer.Ordinal);

        // All-time per tile (2026-11-16 totals; records 2026-11-21): bytes down / up since `Since`
        // (Unix minutes), the fastest second each way and when, the biggest day (bytes down + up,
        // by local date). The buckets roll off, these do not. Reset by Clear.
        public sealed class Total
        {
            public double Down, Up; public long Since;
            public double PeakDown, PeakUp; public long PeakDownAt, PeakUpAt;   // bytes/s, Unix minutes
            public int Day; public double DayBytes;                             // the day running now, yyyyMMdd
            public int BestDay; public double BestDayBytes;
            public Total Copy() => (Total)MemberwiseClone();
        }
        private static readonly Dictionary<string, Total> _totals = new Dictionary<string, Total>(StringComparer.Ordinal);

        public static Total GetTotal(string tileId)
        {
            EnsureLoaded();
            lock (_lock)
            {
                Total t;
                return !string.IsNullOrEmpty(tileId) && _totals.TryGetValue(tileId, out t) ? t.Copy() : null;
            }
        }

        // «Очистить историю»: the tile's history, its total and its records, gone; written at once.
        public static void Clear(string tileId)
        {
            if (string.IsNullOrEmpty(tileId)) return;
            EnsureLoaded();
            lock (_lock) { _byTile.Remove(tileId); _totals.Remove(tileId); }
            Save();
        }
        // «Очистить у всех плиток» (2026-11-23): every tile's history, totals and records; written at once.
        public static void ClearAll()
        {
            EnsureLoaded();
            lock (_lock) { _byTile.Clear(); _totals.Clear(); }
            Save();
        }

        private static bool _loaded;
        private static DateTime _savedAt = DateTime.UtcNow;

        private static string FilePath => Path.Combine(VersionInfo.DataUserDir, "speedhist.txt");

        public static long MinuteOf(DateTime utc) => (long)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes);
        public static DateTime TimeOf(long minute) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);
        private static int DayOf(long minute) { var d = TimeOf(minute).ToLocalTime(); return d.Year * 10000 + d.Month * 100 + d.Day; }
        public static DateTime DateOfDay(int day) => new DateTime(day / 10000, day / 100 % 100, day % 100);

        // One measured interval of `secs` seconds ending now: bytes/s down and up, and the part of
        // them that went through a VPN.
        public static void Add(string tileId, DateTime utc, double downBps, double upBps, double secs,
                               double vpnDownBps = 0, double vpnUpBps = 0)
        {
            if (string.IsNullOrEmpty(tileId) || secs <= 0) return;
            EnsureLoaded();
            long minute = MinuteOf(utc);
            lock (_lock)
            {
                List<Bucket> list;
                if (!_byTile.TryGetValue(tileId, out list)) _byTile[tileId] = list = new List<Bucket>();
                var last = list.Count > 0 ? list[list.Count - 1] : null;
                if (last == null || last.Minute != minute)
                {
                    last = new Bucket { Minute = minute };
                    list.Add(last);
                    // Older than the kept span: gone (checked once a minute per tile, not every second).
                    long cut = minute - KeepMinutes;
                    int drop = 0;
                    while (drop < list.Count && list[drop].Minute <= cut) drop++;
                    if (drop > 0) list.RemoveRange(0, drop);
                }
                last.Down += downBps * secs;
                last.Up += upBps * secs;
                last.VpnDown += Math.Min(downBps, Math.Max(0, vpnDownBps)) * secs;
                last.VpnUp += Math.Min(upBps, Math.Max(0, vpnUpBps)) * secs;
                last.Secs += secs;
                if (downBps > last.PeakDown) last.PeakDown = downBps;
                if (upBps > last.PeakUp) last.PeakUp = upBps;

                Total t;
                if (!_totals.TryGetValue(tileId, out t)) _totals[tileId] = t = new Total { Since = minute };
                t.Down += downBps * secs;
                t.Up += upBps * secs;
                if (downBps > t.PeakDown) { t.PeakDown = downBps; t.PeakDownAt = minute; }
                if (upBps > t.PeakUp) { t.PeakUp = upBps; t.PeakUpAt = minute; }
                int day = DayOf(minute);
                if (day != t.Day) { t.Day = day; t.DayBytes = 0; }
                t.DayBytes += (downBps + upBps) * secs;
                if (t.DayBytes > t.BestDayBytes) { t.BestDay = day; t.BestDayBytes = t.DayBytes; }
            }
            if ((DateTime.UtcNow - _savedAt).TotalMinutes >= 5) Save();
        }

        // A copy of the last `minutes` minutes of one tile, oldest first; `before` shifts the
        // window back that many minutes (the period before, for the «сдвиг» arrow).
        public static List<Bucket> Get(string tileId, int minutes, int before = 0)
        {
            EnsureLoaded();
            long to = MinuteOf(DateTime.UtcNow) - before;
            long from = to - minutes + 1;
            lock (_lock)
            {
                List<Bucket> list;
                if (string.IsNullOrEmpty(tileId) || !_byTile.TryGetValue(tileId, out list)) return new List<Bucket>();
                return list.Where(b => b.Minute >= from && b.Minute <= to)
                           .Select(b => new Bucket
                           {
                               Minute = b.Minute, Down = b.Down, Up = b.Up, Secs = b.Secs, PeakDown = b.PeakDown, PeakUp = b.PeakUp,
                               VpnDown = b.VpnDown, VpnUp = b.VpnUp, Split = b.Split,
                           })
                           .ToList();
            }
        }

        private static string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
        private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

        // id \t minute,down,up,secs,peak,peakUp;minute,…   — plain text, one tile per line.
        // #total \t id \t down,up,since,peakDown,peakDownAt,peakUp,peakUpAt,day,dayBytes,bestDay,bestDayBytes
        public static void Save()
        {
            _savedAt = DateTime.UtcNow;
            if (!_loaded) return;   // never overwrite a file we have not read
            try
            {
                var sb = new StringBuilder();
                lock (_lock)
                {
                    long cut = MinuteOf(DateTime.UtcNow) - KeepMinutes;
                    foreach (var kv in _byTile)
                    {
                        var rows = kv.Value.Where(b => b.Minute > cut).ToList();
                        if (rows.Count == 0) continue;
                        sb.Append(kv.Key).Append('\t');
                        // A minute with its route split gets two more fields (2026-11-23); one recorded
                        // before keeps six, so it stays "route unknown" after the next start too.
                        sb.Append(string.Join(";", rows.Select(b => string.Join(",",
                            N(b.Minute), N(b.Down), N(b.Up),
                            Math.Round(b.Secs, 1).ToString(CultureInfo.InvariantCulture),
                            N(b.PeakDown), N(b.PeakUp))
                            + (b.Split ? "," + N(b.VpnDown) + "," + N(b.VpnUp) : ""))));
                        sb.Append('\n');
                    }
                    foreach (var kv in _totals)
                    {
                        var t = kv.Value;
                        sb.Append("#total\t").Append(kv.Key).Append('\t').Append(string.Join(",",
                            N(t.Down), N(t.Up), N(t.Since), N(t.PeakDown), N(t.PeakDownAt), N(t.PeakUp), N(t.PeakUpAt),
                            N(t.Day), N(t.DayBytes), N(t.BestDay), N(t.BestDayBytes))).Append('\n');
                    }
                }
                Directory.CreateDirectory(VersionInfo.DataUserDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error("speed-history-save", ex); }
        }

        private static double D(string[] v, int i)
        {
            double d;
            return i < v.Length && double.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(FilePath)) return;
                    long cut = MinuteOf(DateTime.UtcNow) - KeepMinutes;
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        if (line.StartsWith("#total\t", StringComparison.Ordinal))
                        {
                            var f = line.Split('\t');
                            if (f.Length < 3) continue;
                            var v = f[2].Split(',');
                            if (v.Length < 3) continue;
                            _totals[f[1]] = new Total
                            {
                                Down = D(v, 0), Up = D(v, 1), Since = (long)D(v, 2),
                                PeakDown = D(v, 3), PeakDownAt = (long)D(v, 4), PeakUp = D(v, 5), PeakUpAt = (long)D(v, 6),
                                Day = (int)D(v, 7), DayBytes = D(v, 8), BestDay = (int)D(v, 9), BestDayBytes = D(v, 10),
                            };
                            continue;
                        }
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        var list = new List<Bucket>();
                        foreach (var cell in line.Substring(tab + 1).Split(';'))
                        {
                            var p = cell.Split(',');
                            if (p.Length < 5) continue;
                            long m;
                            if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) || m <= cut) continue;
                            list.Add(new Bucket
                            {
                                Minute = m, Down = D(p, 1), Up = D(p, 2), Secs = D(p, 3), PeakDown = D(p, 4), PeakUp = D(p, 5),
                                VpnDown = D(p, 6), VpnUp = D(p, 7), Split = p.Length >= 8,
                            });
                        }
                        if (list.Count > 0) _byTile[line.Substring(0, tab)] = list.OrderBy(b => b.Minute).ToList();
                    }
                    foreach (var kv in _byTile)
                    {
                        if (kv.Value.Count == 0) continue;
                        // A file from before the totals: start each tile's total from what it has.
                        Total t;
                        if (!_totals.TryGetValue(kv.Key, out t))
                            _totals[kv.Key] = t = new Total { Down = kv.Value.Sum(b => b.Down), Up = kv.Value.Sum(b => b.Up), Since = kv.Value[0].Minute };
                        // A file from before the records (2026-11-21): seed them from the kept history.
                        if (t.PeakDown <= 0 && t.BestDay == 0)
                        {
                            foreach (var b in kv.Value)
                            {
                                if (b.PeakDown > t.PeakDown) { t.PeakDown = b.PeakDown; t.PeakDownAt = b.Minute; }
                                double up = b.PeakUp > 0 ? b.PeakUp : (b.Secs > 0 ? b.Up / b.Secs : 0);
                                if (up > t.PeakUp) { t.PeakUp = up; t.PeakUpAt = b.Minute; }
                            }
                            foreach (var g in kv.Value.GroupBy(b => DayOf(b.Minute)))
                            {
                                double bytes = g.Sum(b => b.Down + b.Up);
                                if (bytes > t.BestDayBytes) { t.BestDay = g.Key; t.BestDayBytes = bytes; }
                            }
                            int today = DayOf(MinuteOf(DateTime.UtcNow));
                            t.Day = today;
                            t.DayBytes = kv.Value.Where(b => DayOf(b.Minute) == today).Sum(b => b.Down + b.Up);
                        }
                    }
                }
                catch (Exception ex) { Log.Error("speed-history-load", ex); }
            }
        }
    }
}
