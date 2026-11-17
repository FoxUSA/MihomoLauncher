using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MihomoLauncher.Services
{
    // The last 24 hours of every tile's speed, one bucket per minute (2026-11-16) — what the
    // «скорость за 24 часа» window draws. Fed once a second by the tile speed poll (MainWindow.
    // TileSpeed), kept in DataUser\speedhist.txt so a restart or an update does not wipe the day.
    //
    // A bucket holds the bytes moved in that minute, the seconds actually measured (the engine may
    // have been down for part of it) and the fastest single second. Rates are bytes / measured
    // seconds; a minute nobody measured simply has no bucket — the chart leaves it empty.
    public static class SpeedHistory
    {
        public const int KeepMinutes = 24 * 60;

        public sealed class Bucket
        {
            public long Minute;        // Unix minutes (UTC)
            public double Down, Up;    // bytes in this minute
            public double Secs;        // seconds measured
            public double PeakDown;    // fastest second, bytes/s
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, List<Bucket>> _byTile = new Dictionary<string, List<Bucket>>(StringComparer.Ordinal);

        // All-time totals per tile (2026-11-16): bytes down / up since `Since` (Unix minutes) —
        // the day's buckets roll off, these do not. Reset by Clear.
        public sealed class Total { public double Down, Up; public long Since; }
        private static readonly Dictionary<string, Total> _totals = new Dictionary<string, Total>(StringComparer.Ordinal);

        public static Total GetTotal(string tileId)
        {
            EnsureLoaded();
            lock (_lock)
            {
                Total t;
                return !string.IsNullOrEmpty(tileId) && _totals.TryGetValue(tileId, out t)
                    ? new Total { Down = t.Down, Up = t.Up, Since = t.Since } : null;
            }
        }

        // «Очистить историю»: the tile's day and its all-time total, gone; written at once.
        public static void Clear(string tileId)
        {
            if (string.IsNullOrEmpty(tileId)) return;
            EnsureLoaded();
            lock (_lock) { _byTile.Remove(tileId); _totals.Remove(tileId); }
            Save();
        }
        private static bool _loaded;
        private static DateTime _savedAt = DateTime.UtcNow;

        private static string FilePath => Path.Combine(VersionInfo.DataUserDir, "speedhist.txt");

        public static long MinuteOf(DateTime utc) => (long)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes);
        public static DateTime TimeOf(long minute) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute);

        // One measured interval of `secs` seconds ending now: bytes/s down and up.
        public static void Add(string tileId, DateTime utc, double downBps, double upBps, double secs)
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
                    // Older than a day: gone (checked once a minute per tile, not every second).
                    long cut = minute - KeepMinutes;
                    int drop = 0;
                    while (drop < list.Count && list[drop].Minute <= cut) drop++;
                    if (drop > 0) list.RemoveRange(0, drop);
                }
                last.Down += downBps * secs;
                last.Up += upBps * secs;
                last.Secs += secs;
                if (downBps > last.PeakDown) last.PeakDown = downBps;

                Total t;
                if (!_totals.TryGetValue(tileId, out t)) _totals[tileId] = t = new Total { Since = minute };
                t.Down += downBps * secs;
                t.Up += upBps * secs;
            }
            if ((DateTime.UtcNow - _savedAt).TotalMinutes >= 5) Save();
        }

        // A copy of the last `minutes` minutes of one tile, oldest first.
        public static List<Bucket> Get(string tileId, int minutes)
        {
            EnsureLoaded();
            long from = MinuteOf(DateTime.UtcNow) - minutes + 1;
            lock (_lock)
            {
                List<Bucket> list;
                if (string.IsNullOrEmpty(tileId) || !_byTile.TryGetValue(tileId, out list)) return new List<Bucket>();
                return list.Where(b => b.Minute >= from)
                           .Select(b => new Bucket { Minute = b.Minute, Down = b.Down, Up = b.Up, Secs = b.Secs, PeakDown = b.PeakDown })
                           .ToList();
            }
        }

        // id \t minute,down,up,secs,peak;minute,…   — plain text, one tile per line.
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
                        sb.Append(string.Join(";", rows.Select(b => string.Join(",",
                            b.Minute.ToString(CultureInfo.InvariantCulture),
                            Math.Round(b.Down).ToString(CultureInfo.InvariantCulture),
                            Math.Round(b.Up).ToString(CultureInfo.InvariantCulture),
                            Math.Round(b.Secs, 1).ToString(CultureInfo.InvariantCulture),
                            Math.Round(b.PeakDown).ToString(CultureInfo.InvariantCulture)))));
                        sb.Append('\n');
                    }
                    // #total \t id \t down,up,since
                    foreach (var kv in _totals)
                        sb.Append("#total\t").Append(kv.Key).Append('\t')
                          .Append(Math.Round(kv.Value.Down).ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(Math.Round(kv.Value.Up).ToString(CultureInfo.InvariantCulture)).Append(',')
                          .Append(kv.Value.Since.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                Directory.CreateDirectory(VersionInfo.DataUserDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error("speed-history-save", ex); }
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
                            double td, tu; long since;
                            if (v.Length < 3
                                || !double.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out td)
                                || !double.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out tu)
                                || !long.TryParse(v[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out since)) continue;
                            _totals[f[1]] = new Total { Down = td, Up = tu, Since = since };
                            continue;
                        }
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        var list = new List<Bucket>();
                        foreach (var cell in line.Substring(tab + 1).Split(';'))
                        {
                            var p = cell.Split(',');
                            if (p.Length < 5) continue;
                            long m; double d, u, s, pk;
                            if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) || m <= cut) continue;
                            double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out u);
                            double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s);
                            double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out pk);
                            list.Add(new Bucket { Minute = m, Down = d, Up = u, Secs = s, PeakDown = pk });
                        }
                        if (list.Count > 0) _byTile[line.Substring(0, tab)] = list.OrderBy(b => b.Minute).ToList();
                    }
                    // A file from before the totals: start each tile's total from the day it has.
                    foreach (var kv in _byTile)
                        if (!_totals.ContainsKey(kv.Key) && kv.Value.Count > 0)
                            _totals[kv.Key] = new Total { Down = kv.Value.Sum(b => b.Down), Up = kv.Value.Sum(b => b.Up), Since = kv.Value[0].Minute };
                }
                catch (Exception ex) { Log.Error("speed-history-load", ex); }
            }
        }
    }
}
