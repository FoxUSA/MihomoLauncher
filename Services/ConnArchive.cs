using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MihomoLauncher.Services
{
    // Every connection of the last 24 hours (2026-11-20: «логировать и хранить 24 часа, кнопки 1 / 6 /
    // 12 показывают отрезки»). When a connection leaves the engine's /connections (MainWindow.WhoSweep)
    // one line goes to DataUser\connlog\conn-yyyyMMdd-HH.txt (UTC hour of its end): tile, end, program,
    // host, address, port, TCP/UDP, bytes down / up. Buffered in memory and appended every 30 s (and
    // on exit); hour files older than a day are deleted. Nothing is kept in memory but that buffer —
    // the speed window reads the files when an archive tab is clicked and drops them on close.
    //
    // A connection that opened and closed between two one-second polls is never seen, so never here.
    public static class ConnArchive
    {
        public sealed class Rec
        {
            public string Tile = "", Process = "", Path = "", Host = "", Sniff = "", Ip = "", Port = "", Network = "";
            public long End;           // Unix seconds (UTC), the last poll it was open at
            public long Down, Up;      // bytes
        }

        private const int KeepHours = 24;
        private static readonly object _buf = new object();
        private static readonly object _io = new object();
        private static List<Rec> _pending = new List<Rec>();
        private static DateTime _flushedAt = DateTime.UtcNow, _prunedAt = DateTime.MinValue;

        private static string Dir => Path.Combine(VersionInfo.DataUserDir, "connlog");
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long SecOf(DateTime utc) => (long)(utc - Epoch).TotalSeconds;
        public static DateTime TimeOf(long sec) => Epoch.AddSeconds(sec);

        public static Rec From(string tile, MihomoController.ConnStat c, long down, long up, DateTime endUtc) => new Rec
        {
            Tile = tile ?? "", Process = c.Process ?? "", Path = c.ProcessPath ?? "", Host = c.Host ?? "", Sniff = c.SniffHost ?? "",
            Ip = c.DestIp ?? "", Port = c.Port ?? "", Network = c.Network ?? "",
            End = SecOf(endUtc), Down = Math.Max(0, down), Up = Math.Max(0, up),
        };

        public static void Add(Rec r)
        {
            if (r == null || r.Tile.Length == 0) return;
            lock (_buf) _pending.Add(r);
        }

        // From the speed poll: every 30 s, off the UI thread.
        public static void FlushIfDue()
        {
            if ((DateTime.UtcNow - _flushedAt).TotalSeconds < 30) return;
            _flushedAt = DateTime.UtcNow;
            System.Threading.Tasks.Task.Run(() => Flush());
        }

        public static void Flush()
        {
            List<Rec> take;
            lock (_buf) { take = _pending; _pending = new List<Rec>(); }
            lock (_io)
            {
                try
                {
                    if (take.Count > 0)
                    {
                        Directory.CreateDirectory(Dir);
                        foreach (var g in take.GroupBy(r => FileOf(TimeOf(r.End))))
                        {
                            var sb = new StringBuilder();
                            foreach (var r in g) Line(sb, r);
                            File.AppendAllText(g.Key, sb.ToString(), new UTF8Encoding(false));
                        }
                    }
                    if ((DateTime.UtcNow - _prunedAt).TotalMinutes >= 10) { _prunedAt = DateTime.UtcNow; Prune(); }
                }
                catch (Exception ex) { Log.Error("conn-archive-flush", ex); }
            }
        }

        // One tile's connections that ended at or after `fromUtc`, oldest file first. Streams the
        // lines: the caller adds them up as they come, nothing is held.
        public static IEnumerable<Rec> Read(string tile, DateTime fromUtc)
        {
            if (string.IsNullOrEmpty(tile)) yield break;
            Flush();   // what closed in the last 30 s too
            long from = SecOf(fromUtc);
            var hourFrom = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
            var prefix = tile + "\t";
            foreach (var f in Files().Where(x => x.Value >= hourFrom).OrderBy(x => x.Value))
            {
                string[] lines;
                lock (_io)
                {
                    try { lines = File.ReadAllLines(f.Key, Encoding.UTF8); }
                    catch (Exception ex) { Log.Error("conn-archive-read", ex); continue; }
                }
                foreach (var line in lines)
                {
                    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var r = Parse(line);
                    if (r != null && r.End >= from) yield return r;
                }
            }
        }

        // «Очистить историю»: the tile's lines gone from every hour file.
        public static void Clear(string tile)
        {
            if (string.IsNullOrEmpty(tile)) return;
            lock (_buf) _pending.RemoveAll(r => r.Tile == tile);
            var prefix = tile + "\t";
            lock (_io)
            {
                foreach (var f in Files())
                {
                    try
                    {
                        var lines = File.ReadAllLines(f.Key, Encoding.UTF8);
                        var keep = lines.Where(l => !l.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                        if (keep.Length == lines.Length) continue;
                        if (keep.Length == 0) File.Delete(f.Key);
                        else File.WriteAllLines(f.Key, keep, new UTF8Encoding(false));
                    }
                    catch (Exception ex) { Log.Error("conn-archive-clear", ex); }
                }
            }
        }

        private static string FileOf(DateTime utc) => Path.Combine(Dir, "conn-" + utc.ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture) + ".txt");

        // Hour file → its UTC hour.
        private static List<KeyValuePair<string, DateTime>> Files()
        {
            var list = new List<KeyValuePair<string, DateTime>>();
            try
            {
                if (!Directory.Exists(Dir)) return list;
                foreach (var f in Directory.GetFiles(Dir, "conn-*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(f).Substring(5);
                    DateTime h;
                    if (DateTime.TryParseExact(name, "yyyyMMdd-HH", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out h))
                        list.Add(new KeyValuePair<string, DateTime>(f, h));
                }
            }
            catch (Exception ex) { Log.Error("conn-archive-list", ex); }
            return list;
        }

        private static void Prune()
        {
            var cut = DateTime.UtcNow.AddHours(-(KeepHours + 1));
            foreach (var f in Files().Where(x => x.Value < cut))
                try { File.Delete(f.Key); } catch { }
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        // tile \t end \t process \t path \t host \t sniff \t ip \t port \t network \t down \t up
        private static void Line(StringBuilder sb, Rec r)
        {
            sb.Append(Clean(r.Tile)).Append('\t')
              .Append(r.End.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(Clean(r.Process)).Append('\t')
              .Append(Clean(r.Path)).Append('\t')
              .Append(Clean(r.Host)).Append('\t')
              .Append(Clean(r.Sniff)).Append('\t')
              .Append(Clean(r.Ip)).Append('\t')
              .Append(Clean(r.Port)).Append('\t')
              .Append(Clean(r.Network)).Append('\t')
              .Append(r.Down.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Up.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static Rec Parse(string line)
        {
            var p = line.Split('\t');
            if (p.Length < 11) return null;
            long end, down, up;
            if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out end)) return null;
            long.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out down);
            long.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out up);
            return new Rec
            {
                Tile = p[0], End = end, Process = p[2], Path = p[3], Host = p[4], Sniff = p[5],
                Ip = p[6], Port = p[7], Network = p[8], Down = down, Up = up,
            };
        }
    }
}
