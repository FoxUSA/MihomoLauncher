using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Watching the disk for what the rules depend on (2026-11-22).
    //
    //  · A folder entry: its *.exe files (created / deleted / renamed, below it too). Routing needs
    //    nothing — the engine's regexp already covers a new file — but the firewall kill-switch is
    //    keyed on full exe paths (FirewallGuard.Build lists every exe of the folder), so a new exe
    //    has to reach the firewall: debounce, then `onExesChanged` (→ SyncFirewallSoon).
    //  · The parent of every folder entry, and the parent of every version-looking segment of
    //    every program path ("…\Discord" for "…\Discord\app-1.0.9\Discord.exe"): a directory
    //    created / renamed / deleted there is how an update moves a program → debounce, then
    //    `onPathsMoved` (→ path repair at once, instead of at the next 10-minute scan).
    //
    // Watchers can miss events (buffer overflow, a network drive, a folder that is gone), so the
    // slow safety rescans stay where they were: the 10-minute path scan, and ExesUnder's 10-minute
    // cache. A failing watcher is dropped and recreated with back-off; a missing / unavailable
    // drive is retried quietly. Never something deeper than the entry folder is opened: an open
    // directory handle can block renaming that directory's ancestors, and an updater must stay
    // free to rename a version folder.
    public static class FolderWatch
    {
        private const int MaxWatchers = 64;
        private const int MaxExesPerFolder = 300;
        private const int MaxDirsPerFolder = 5000;
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ExeCacheTtl = TimeSpan.FromMinutes(10);

        // ---------- the exe list of a folder (for the firewall) ----------

        private sealed class ExeList { public List<string> Files; public DateTime At; }
        private static readonly Dictionary<string, ExeList> _exes = new Dictionary<string, ExeList>(StringComparer.OrdinalIgnoreCase);

        // Every *.exe under `folder` (recursive), at most 300, unreadable directories skipped,
        // junctions not followed. Never throws. Cached for 10 minutes or until its watcher reports
        // a change — FirewallGuard.Build asks every 20 s.
        public static List<string> ExesUnder(string folder)
        {
            var key = MihomoConfigBuilder.NormalizeFolder(folder);
            if (key.Length == 0) return new List<string>();
            lock (_exes)
            {
                ExeList hit;
                if (_exes.TryGetValue(key, out hit) && DateTime.UtcNow - hit.At < ExeCacheTtl) return hit.Files;
            }
            var files = Scan(key);
            lock (_exes) _exes[key] = new ExeList { Files = files, At = DateTime.UtcNow };
            return files;
        }

        private static void Invalidate(string folder)
        {
            var key = MihomoConfigBuilder.NormalizeFolder(folder);
            lock (_exes) _exes.Remove(key);
        }

        private static List<string> Scan(string root)
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(root)) return found;
                var stack = new Stack<string>();
                stack.Push(root);
                int dirs = 0;
                bool capped = false;
                while (stack.Count > 0 && !capped)
                {
                    var dir = stack.Pop();
                    if (++dirs > MaxDirsPerFolder) { capped = true; break; }
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
                        {
                            found.Add(f);
                            if (found.Count >= MaxExesPerFolder) { capped = true; break; }
                        }
                        if (capped) break;
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            try
                            {
                                // A junction / symlink may point anywhere (even back up): not followed.
                                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }
                            stack.Push(sub);
                        }
                    }
                    catch { }   // no access, gone meanwhile — that directory only
                }
                if (capped) NoteOnce("cap|" + root, "Папка «" + root + "»: в защиту брандмауэра взяты первые "
                                                       + found.Count + " программ (больше не перебираю).");
            }
            catch { }
            return found;
        }

        // ---------- watchers ----------

        private enum Kind { Exes, Dirs }

        private sealed class Entry
        {
            public string Dir;
            public Kind Kind;
            public FileSystemWatcher W;
            public int Fails;
            public DateTime NextTry = DateTime.MinValue;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static Timer _maintain, _exesTimer, _dirsTimer;
        private static Action _onExesChanged, _onPathsMoved;
        private static bool _stopped;
        private static readonly HashSet<string> _noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void NoteOnce(string key, string line)
        {
            lock (_noted) if (!_noted.Add(key)) return;
            Log.Info(line);
        }

        // The set of watched directories for this config. Called at start and whenever the rules
        // are saved; directories no longer needed are let go, new ones opened.
        public static void Rebuild(AppConfig cfg, Action onExesChanged, Action onPathsMoved)
        {
            try
            {
                var want = Wanted(cfg);
                lock (_lock)
                {
                    if (_stopped) return;
                    _onExesChanged = onExesChanged;
                    _onPathsMoved = onPathsMoved;
                    foreach (var key in _entries.Keys.ToList())
                        if (!want.ContainsKey(key)) { Close(_entries[key]); _entries.Remove(key); }
                    foreach (var kv in want)
                        if (!_entries.ContainsKey(kv.Key)) _entries[kv.Key] = kv.Value;
                    foreach (var e in _entries.Values) if (e.W == null) TryOpen(e);
                    if (_maintain == null) _maintain = new Timer(_ => Maintain(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                }
            }
            catch (Exception ex) { Log.Error("folder-watch-rebuild", ex); }
        }

        public static void StopAll()
        {
            lock (_lock)
            {
                _stopped = true;
                foreach (var e in _entries.Values) Close(e);
                _entries.Clear();
                try { _maintain?.Dispose(); } catch { }
                try { _exesTimer?.Dispose(); } catch { }
                try { _dirsTimer?.Dispose(); } catch { }
                _maintain = _exesTimer = _dirsTimer = null;
            }
        }

        private static Dictionary<string, Entry> Wanted(AppConfig cfg)
        {
            var want = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            void Add(string dir, Kind kind)
            {
                var d = MihomoConfigBuilder.NormalizeFolder(dir);
                if (d.Length == 0) return;
                var key = kind + "|" + d;
                if (want.ContainsKey(key)) return;
                if (want.Count >= MaxWatchers) { skipped++; return; }
                want[key] = new Entry { Dir = d, Kind = kind };
            }
            void AddVersionParents(string path, bool includeOwnParent)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                // Store packages: WindowsApps is not listable for a normal user, healed by AUMID.
                if (path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) return;
                string full;
                try { full = Path.GetFullPath(path.Trim()); } catch { return; }
                var root = Path.GetPathRoot(full) ?? "";
                var segs = full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segs.Length; i++)
                {
                    bool own = includeOwnParent && i == segs.Length - 1;
                    if (!own && !ProgramPathRepair.LooksVersioned(segs[i])) continue;
                    if (i == 0) continue;   // never a whole drive root
                    Add(Path.Combine(root, string.Join("\\", segs.Take(i))), Kind.Dirs);
                }
            }

            foreach (var app in cfg?.Apps ?? new List<AppEntry>())
            {
                if (app == null) continue;
                // Manual folders here; auto folders below (2026-11-23).
                foreach (var f in app.Folders ?? new List<RuleFolder>())
                {
                    if (f == null || f.Auto || string.IsNullOrWhiteSpace(f.Path)) continue;
                    Add(f.Path, Kind.Exes);
                    AddVersionParents(f.Path, includeOwnParent: true);
                }
            }
            foreach (var app in cfg?.Apps ?? new List<AppEntry>())
                foreach (var p in app?.Programs ?? new List<RuleProgram>())
                    if (p != null && !p.IsStoreApp) AddVersionParents(p.Path, includeOwnParent: false);
            // Auto folders (2026-11-23): their exe list matters only to the firewall, so a watcher
            // only where the firewall walls the rule (FirewallGuard.Build's condition) — and no
            // version parents: its program's path already registers those, and with a folder for
            // nearly every tile they would eat the 64 watchers. Last, so they never push a manual
            // folder's watcher out.
            if (cfg != null && cfg.FirewallKillSwitch)
                foreach (var app in (cfg.Apps ?? new List<AppEntry>()).Where(a => a != null && (a.KillSwitch || a.LockNic)))
                    foreach (var f in app.Folders ?? new List<RuleFolder>())
                        if (f != null && f.Auto && !string.IsNullOrWhiteSpace(f.Path)) Add(f.Path, Kind.Exes);
            if (skipped > 0)
                NoteOnce("watch-cap|" + skipped, "Слежение за папками: " + MaxWatchers + " папок — предел, ещё " + skipped
                                                 + " без слежения (их подхватит проверка раз в 10 минут).");
            return want;
        }

        // Under _lock.
        private static void TryOpen(Entry e)
        {
            if (DateTime.UtcNow < e.NextTry) return;
            try
            {
                if (!Directory.Exists(e.Dir)) { Backoff(e); return; }
                var w = new FileSystemWatcher(e.Dir)
                {
                    IncludeSubdirectories = e.Kind == Kind.Exes,
                    Filter = e.Kind == Kind.Exes ? "*.exe" : "*",
                    NotifyFilter = e.Kind == Kind.Exes ? NotifyFilters.FileName : NotifyFilters.DirectoryName,
                    InternalBufferSize = 16 * 1024,
                };
                FileSystemEventHandler on = (s, a) => Changed(e);
                w.Created += on;
                w.Deleted += on;
                w.Renamed += (s, a) => Changed(e);
                w.Error += (s, a) => Failed(e, a.GetException());
                w.EnableRaisingEvents = true;
                e.W = w;
                e.Fails = 0;
            }
            catch (Exception ex)
            {
                Backoff(e);
                if (e.Fails == 1) NoteOnce("open|" + e.Dir, "Слежение за папкой «" + e.Dir + "» не открылось (" + ex.Message + ") — пробую позже.");
            }
        }

        private static void Backoff(Entry e)
        {
            e.Fails++;
            var secs = Math.Min(600, 15 * Math.Pow(2, Math.Min(6, e.Fails - 1)));
            e.NextTry = DateTime.UtcNow.AddSeconds(secs);
        }

        private static void Close(Entry e)
        {
            var w = e.W;
            e.W = null;
            if (w == null) return;
            try { w.EnableRaisingEvents = false; } catch { }
            try { w.Dispose(); } catch { }
        }

        // Buffer overflow, folder gone, drive away: drop it, reopen later; what it may have missed
        // is handled as a change.
        private static void Failed(Entry e, Exception ex)
        {
            lock (_lock)
            {
                if (_stopped) return;
                Close(e);
                Backoff(e);
            }
            NoteOnce("err|" + e.Dir + "|" + (ex?.GetType().Name ?? ""),
                     "Слежение за папкой «" + e.Dir + "» прервалось (" + (ex?.Message ?? "?") + ") — открою заново.");
            Changed(e);
        }

        private static void Maintain()
        {
            try
            {
                lock (_lock)
                {
                    if (_stopped) return;
                    foreach (var e in _entries.Values) if (e.W == null) TryOpen(e);
                }
            }
            catch (Exception ex) { Log.Error("folder-watch-maintain", ex); }
        }

        private static void Changed(Entry e)
        {
            try
            {
                lock (_lock)
                {
                    if (_stopped) return;
                    if (e.Kind == Kind.Exes)
                    {
                        Invalidate(e.Dir);
                        Restart(ref _exesTimer, () => _onExesChanged);
                    }
                    else Restart(ref _dirsTimer, () => _onPathsMoved);
                }
            }
            catch (Exception ex) { Log.Error("folder-watch-event", ex); }
        }

        // One callback per burst: an installer drops hundreds of files, one sync follows.
        private static void Restart(ref Timer t, Func<Action> callback)
        {
            if (t == null)
                t = new Timer(_ =>
                {
                    try { callback()?.Invoke(); } catch (Exception ex) { Log.Error("folder-watch-callback", ex); }
                }, null, Debounce, Timeout.InfiniteTimeSpan);
            else t.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }
}
