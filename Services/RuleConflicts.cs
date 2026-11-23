using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    public enum ConflictKind
    {
        Same,       // the same address in two tiles — the right one never works
        Covered,    // the left tile's address covers the right one's (youtube.com ⊃ music.youtube.com)
        Partial,    // the left tile takes a PART of the right one's (music.youtube.com ⊂ youtube.com)
        Keyword,    // a keyword of the left tile may catch the right one's address
        Ip,         // overlapping IP ranges
        Program,    // the same program in two tiles
        // A program of the left tile inside a folder of the right one, or a folder inside a wider
        // folder (2026-11-22): the more specific entry wins by design (MihomoConfigBuilder.Build),
        // whatever the order of the tiles — harmless, like Partial.
        FolderPartial,
        // An auto entry's exact file in the left tile vs the same exe NAME in the right one: the file
        // line sits above every program by name, so that one file goes left, every other copy right —
        // harmless by design, like FolderPartial.
        FilePartial,
    }

    public sealed class RuleConflict
    {
        public ConflictKind Kind;
        public string WinnerItem = "";   // "youtube.com"
        public string LoserItem = "";
        public bool Folder;              // a folder entry is involved (the same folder in two tiles: Same)
    }

    // Two tiles whose lists collide, and everything that collides between them.
    public sealed class ConflictGroup
    {
        public AppEntry Winner;
        public AppEntry Loser;
        public List<RuleConflict> Items = new List<RuleConflict>();
        // Both tiles send their traffic the same way right now (RuleConflicts.RouteOf) — whoever
        // wins, nothing changes. Still listed, but no alarm.
        public bool SameRoute;
        // Only «a narrower address of the left tile takes its part from a wider one of the right»
        // — what the narrower line was written for.
        public bool PartialOnly => Items.Count > 0 && Items.All(i => i.Kind == ConflictKind.Partial || i.Kind == ConflictKind.FolderPartial
                                                                     || i.Kind == ConflictKind.FilePartial);
        // Worth the red icon: something listed really goes elsewhere than its tile says.
        public bool Real => !SameRoute && !PartialOnly;
    }

    // Where two tiles claim the same traffic (2026-11-13). The engine takes the FIRST matching
    // line, and the config puts every tile's sites/templates above every tile's programs, each
    // block in the order of the tiles (MihomoConfigBuilder.Build). So between two tiles' sites —
    // or two tiles' programs — the tile further left wins, and the other one's entry silently
    // does nothing. This finds those places, from exactly the lines AddAppRules writes.
    //
    // Cheap: one pass with hash lookups over a few thousand lines (each domain walks its own
    // parent labels), IP ranges pairwise (there are few of them). Milliseconds.
    public static class RuleConflicts
    {
        private sealed class Item
        {
            public int Order;
            public AppEntry Tile;
            public string Type;
            public string Value;
            public bool Site;
            public bool Folder;
        }

        public static List<ConflictGroup> Find(AppConfig cfg)
        {
            var result = new List<ConflictGroup>();
            try
            {
                var tiles = (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.IsAllTraffic).ToList();
                // A tile that writes no lines (no VPN, no kill-switch) loses to anyone: its sites
                // just fall to «Всё остальное». It goes after every tile that does write.
                var ordered = tiles.Where(Writes).Concat(tiles.Where(a => !Writes(a))).ToList();

                var items = new List<Item>();
                int order = 0;
                foreach (var tile in ordered)
                {
                    var mine = new List<RuleDef>();
                    MihomoConfigBuilder.AddAppRules(mine, tile, "?");
                    foreach (var r in mine)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Value)) continue;
                        var type = (r.Type ?? "").Trim().ToUpperInvariant();
                        // Sites/templates of the tile, and its programs. The check address is a
                        // probe, not something the person listed — not reported.
                        if (!r.Site && type != "PROCESS-NAME") continue;
                        items.Add(new Item { Order = order++, Tile = tile, Type = type, Value = r.Value.Trim().ToLowerInvariant(), Site = r.Site });
                    }
                }
                // Sites come before programs in the config, whatever the tile order.
                items = items.OrderBy(i => i.Site ? 0 : 1).ThenBy(i => i.Order).ToList();
                for (int i = 0; i < items.Count; i++) items[i].Order = i;

                var found = new List<Tuple<Item, Item, ConflictKind>>();   // (winner, loser, kind)
                var seenPairs = new HashSet<long>();
                void Add(Item a, Item b, ConflictKind kind)
                {
                    if (a.Tile == b.Tile) return;
                    var w = a.Order < b.Order ? a : b;
                    var l = a.Order < b.Order ? b : a;
                    long key = ((long)w.Order << 32) | (uint)l.Order;
                    if (!seenPairs.Add(key)) return;
                    found.Add(Tuple.Create(w, l, kind));
                }

                // --- domains: DOMAIN / DOMAIN-SUFFIX
                var suffixIdx = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
                var exactIdx = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
                foreach (var it in items.Where(i => i.Site))
                {
                    if (it.Type == "DOMAIN-SUFFIX") Bucket(suffixIdx, it.Value).Add(it);
                    else if (it.Type == "DOMAIN") Bucket(exactIdx, it.Value).Add(it);
                }
                foreach (var x in items.Where(i => i.Site && (i.Type == "DOMAIN-SUFFIX" || i.Type == "DOMAIN")))
                {
                    // Every suffix rule that covers x: its own value and each parent label.
                    foreach (var p in SelfAndParents(x.Value))
                    {
                        List<Item> hits;
                        if (!suffixIdx.TryGetValue(p, out hits)) continue;
                        foreach (var y in hits)
                        {
                            if (y == x || y.Tile == x.Tile) continue;
                            // y (suffix p) covers x entirely. Whoever is earlier wins: y earlier →
                            // x never works; x earlier → x takes its part away from y.
                            bool same = p == x.Value && x.Type == y.Type;
                            Add(x, y, same ? ConflictKind.Same : y.Order < x.Order ? ConflictKind.Covered : ConflictKind.Partial);
                        }
                    }
                    if (x.Type == "DOMAIN")
                    {
                        List<Item> hits;
                        if (exactIdx.TryGetValue(x.Value, out hits))
                            foreach (var y in hits)
                                if (y != x && y.Tile != x.Tile) Add(x, y, ConflictKind.Same);
                    }
                }

                // --- keywords: only a keyword that comes FIRST can take anything away
                var domains = items.Where(i => i.Site && (i.Type == "DOMAIN-SUFFIX" || i.Type == "DOMAIN")).ToList();
                foreach (var k in items.Where(i => i.Site && i.Type == "DOMAIN-KEYWORD" && i.Value.Length > 0))
                {
                    foreach (var d in domains)
                        if (d.Tile != k.Tile && k.Order < d.Order && d.Value.Contains(k.Value))
                            Add(k, d, ConflictKind.Keyword);
                    foreach (var k2 in items.Where(i => i.Site && i.Type == "DOMAIN-KEYWORD" && i.Tile != k.Tile && i.Value == k.Value))
                        Add(k, k2, ConflictKind.Same);
                }

                // --- IP ranges, pairwise (few)
                var nets = items.Where(i => i.Site && (i.Type == "IP-CIDR" || i.Type == "IP-CIDR6"))
                                .Select(i => new { It = i, Net = Cidr.Parse(i.Value) })
                                .Where(n => n.Net != null).Take(4000).ToList();
                for (int a = 0; a < nets.Count; a++)
                    for (int b = a + 1; b < nets.Count; b++)
                    {
                        if (nets[a].It.Tile == nets[b].It.Tile) continue;
                        var na = nets[a].Net; var nb = nets[b].Net;
                        if (!na.Overlaps(nb)) continue;
                        var first = nets[a].It.Order < nets[b].It.Order ? nets[a] : nets[b];
                        var second = first == nets[a] ? nets[b] : nets[a];
                        Add(first.It, second.It, first.Net.Contains(second.Net) ? ConflictKind.Ip : ConflictKind.Partial);
                    }

                // --- programs
                foreach (var g in items.Where(i => !i.Site && i.Type == "PROCESS-NAME").GroupBy(i => i.Value))
                {
                    var list = g.OrderBy(i => i.Order).ToList();
                    for (int i = 1; i < list.Count; i++) Add(list[0], list[i], ConflictKind.Program);
                }

                // --- folders (2026-11-22), below every program in the config. The same folder in
                // two tiles: the left one wins (Same). Nested folders, or a program of one tile
                // inside another tile's folder: the more specific one wins by design (FolderPartial).
                var foundFolders = new List<Tuple<Item, Item, ConflictKind>>();
                var folderItems = new List<Item>();
                foreach (var tile in ordered)
                    foreach (var f in tile.Folders ?? new List<RuleFolder>())
                    {
                        var norm = MihomoConfigBuilder.NormalizeFolder(f?.Path);
                        if (norm.Length == 0) continue;
                        folderItems.Add(new Item { Order = items.Count + folderItems.Count, Tile = tile, Type = "FOLDER", Value = norm, Folder = true });
                    }
                if (folderItems.Count > 0)
                {
                    for (int a = 0; a < folderItems.Count; a++)
                        for (int b = a + 1; b < folderItems.Count; b++)
                        {
                            var x = folderItems[a]; var y = folderItems[b];
                            if (x.Tile == y.Tile) continue;
                            if (string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase))
                                foundFolders.Add(Tuple.Create(x, y, ConflictKind.Same));
                            else if (MihomoConfigBuilder.InFolder(x.Value + "\\", y.Value))
                                foundFolders.Add(Tuple.Create(x, y, ConflictKind.FolderPartial));   // x inside y
                            else if (MihomoConfigBuilder.InFolder(y.Value + "\\", x.Value))
                                foundFolders.Add(Tuple.Create(y, x, ConflictKind.FolderPartial));
                        }
                    int extra = items.Count + folderItems.Count;
                    foreach (var tile in ordered)
                        foreach (var p in tile.Programs ?? new List<RuleProgram>())
                        {
                            var path = (p?.Path ?? "").Trim();
                            if (path.Length == 0) continue;
                            foreach (var fi in folderItems)
                            {
                                if (fi.Tile == tile || !MihomoConfigBuilder.InFolder(path, fi.Value)) continue;
                                string exe;
                                try { exe = System.IO.Path.GetFileName(path).ToLowerInvariant(); } catch { continue; }
                                var prog = new Item { Order = extra++, Tile = tile, Type = "PROCESS-NAME", Value = exe };
                                foundFolders.Add(Tuple.Create(prog, fi, ConflictKind.FolderPartial));
                            }
                        }
                }

                // --- auto entries by their exact file (2026-11-22), right below the sites in the
                // config. The same file auto-claimed by two tiles: the left one wins (Same). A file
                // vs another tile's program by the same NAME: that file goes with its tile, every
                // other copy with the named one — harmless by design (FilePartial).
                int extraFiles = items.Count + folderItems.Count + 100000;
                var fileItems = new List<Item>();
                foreach (var tile in ordered)
                    foreach (var p in tile.Programs ?? new List<RuleProgram>())
                        if (MihomoConfigBuilder.AutoFileRegex(p).Length > 0)
                            fileItems.Add(new Item { Order = extraFiles++, Tile = tile, Type = "FILE", Value = p.Path.Trim() });
                for (int a = 0; a < fileItems.Count; a++)
                    for (int b = a + 1; b < fileItems.Count; b++)
                    {
                        var x = fileItems[a]; var y = fileItems[b];
                        if (x.Tile != y.Tile && MihomoConfigBuilder.IsSameFile(y.Value, x.Value))
                            foundFolders.Add(Tuple.Create(x, y, ConflictKind.Same));
                    }
                foreach (var fi in fileItems)
                {
                    string exe;
                    try { exe = System.IO.Path.GetFileName(fi.Value).ToLowerInvariant(); } catch { continue; }
                    foreach (var named in items.Where(i => !i.Site && i.Type == "PROCESS-NAME" && i.Tile != fi.Tile && i.Value == exe))
                        foundFolders.Add(Tuple.Create(fi, named, ConflictKind.FilePartial));
                }

                // --- group by the pair of tiles, in the order the conflicts appear
                foreach (var f in found.OrderBy(f => f.Item1.Order).ThenBy(f => f.Item2.Order).Concat(foundFolders))
                {
                    var grp = result.FirstOrDefault(g => g.Winner == f.Item1.Tile && g.Loser == f.Item2.Tile);
                    if (grp == null) { grp = new ConflictGroup { Winner = f.Item1.Tile, Loser = f.Item2.Tile }; result.Add(grp); }
                    grp.Items.Add(new RuleConflict
                    {
                        Kind = f.Item3, WinnerItem = f.Item1.Value, LoserItem = f.Item2.Value,
                        Folder = f.Item1.Folder || f.Item2.Folder || f.Item1.Type == "FILE" || f.Item2.Type == "FILE",
                    });
                }

                var routeOf = RouteOf;
                if (routeOf != null)
                    foreach (var g in result)
                    {
                        string a = null, b = null;
                        try { a = routeOf(g.Winner); b = routeOf(g.Loser); } catch { }
                        g.SameRoute = a != null && string.Equals(a, b, StringComparison.Ordinal);
                    }
            }
            catch (Exception ex) { Log.Error("rule-conflicts", ex); }
            return result;
        }

        // Does the tile write lines at all? A VPN tile always does; a «без VPN» one only under its
        // kill-switch (MainWindow.BuildVpnRules / MihomoConfigBuilder.BuildProbe).
        public static bool Writes(AppEntry a) => a != null && (a.RequiresVpn || a.KillSwitch);

        // How a tile's traffic goes right now, as a comparable key (MainWindow.RouteKey); null =
        // unknown. Two conflicting tiles with the same key route identically: no harm (2026-11-22).
        public static Func<AppEntry, string> RouteOf;

        private static List<Item> Bucket(Dictionary<string, List<Item>> d, string key)
        {
            List<Item> l;
            if (!d.TryGetValue(key, out l)) { l = new List<Item>(); d[key] = l; }
            return l;
        }

        private static IEnumerable<string> SelfAndParents(string host)
        {
            var h = host;
            while (!string.IsNullOrEmpty(h))
            {
                yield return h;
                int dot = h.IndexOf('.');
                if (dot < 0) yield break;
                h = h.Substring(dot + 1);
            }
        }

        private sealed class Cidr
        {
            private byte[] _addr;
            private int _bits;

            public static Cidr Parse(string s)
            {
                try
                {
                    var parts = (s ?? "").Split('/');
                    IPAddress ip;
                    if (!IPAddress.TryParse(parts[0], out ip)) return null;
                    var bytes = ip.GetAddressBytes();
                    int bits = parts.Length > 1 ? int.Parse(parts[1]) : bytes.Length * 8;
                    if (bits < 0 || bits > bytes.Length * 8) return null;
                    return new Cidr { _addr = bytes, _bits = bits };
                }
                catch { return null; }
            }

            // Same family and the shorter prefix matches on its own length.
            public bool Overlaps(Cidr o) => o != null && _addr.Length == o._addr.Length
                                            && PrefixEqual(_addr, o._addr, Math.Min(_bits, o._bits));

            public bool Contains(Cidr o) => o != null && _addr.Length == o._addr.Length && _bits <= o._bits
                                            && PrefixEqual(_addr, o._addr, _bits);

            private static bool PrefixEqual(byte[] a, byte[] b, int bits)
            {
                int full = bits / 8, rest = bits % 8;
                for (int i = 0; i < full; i++) if (a[i] != b[i]) return false;
                if (rest == 0) return true;
                int mask = 0xFF << (8 - rest) & 0xFF;
                return (a[full] & mask) == (b[full] & mask);
            }
        }
    }
}
