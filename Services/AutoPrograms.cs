using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // «Авто-добавление программ» (2026-11-22): which helper of a tile's programs goes into the
    // tile by itself. A pure function over a process picture (ProcessTracker) and the config, so
    // a probe can feed it synthetic trees; MainWindow.AutoTrack applies what it returns.
    //
    // "Roots" of a tile (AutoTrack on, not «Всё остальное»): its programs, by exe FILE NAME
    // (routing is by name, so is this), and any process running from one of its folders.
    // A process P belongs to tile T when
    //   · tree:        one of P's ancestors is a root of T, or
    //   · install dir: P lies under the directory of one of T's programs, whoever its parent is —
    //                  a launcher stub that starts the real app and quits, an updater started by
    //                  the Task Scheduler. Not when that directory is a shared place (a drive
    //                  root, Windows, the Program Files roots themselves, the profile, Desktop…).
    // and P has actually used the network (the engine saw it, or it had a TCP connection to a
    // non-loopback address): helpers that never go online — ripgrep, language servers, consoles —
    // must not litter the list.
    //
    // Skipped, with the reason in the log (once per exe per session — how problems on other
    // people's PCs get diagnosed): a name already in ANY tile or a path under any tile's folder,
    // a name the person removed from T, anything under Windows, the deny-list of shared binaries
    // below, a standalone installed application outside T's own folder (a Start-menu shortcut, an
    // App Paths entry, the default browser — an app opening a link starts the browser as its
    // child), and an exe that runs right now outside T's tree (a shared helper).
    //
    // Several tiles qualify → the one whose NAME fits the program best; tie → the tile owning the
    // nearest ancestor; still tie → the leftmost tile. An entry never moves once it is in a tile.
    public static class AutoPrograms
    {
        public const int MaxAutoPerRule = 50;

        // Routing is by NAME: put powershell.exe into a tile and every PowerShell on the machine
        // goes with it. This list is the main safety net — one obvious static set, on purpose.
        public static readonly HashSet<string> DenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // shells and consoles
            "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "OpenConsole.exe", "bash.exe", "sh.exe", "wsl.exe", "wslhost.exe",
            // runtimes and interpreters
            "dotnet.exe", "node.exe", "python.exe", "pythonw.exe", "py.exe", "java.exe", "javaw.exe", "ruby.exe", "perl.exe",
            "php.exe", "go.exe", "deno.exe", "bun.exe",
            // dev tools
            "git.exe", "git-remote-https.exe", "ssh.exe", "scp.exe", "sftp.exe", "curl.exe", "wget.exe", "msbuild.exe",
            "VBCSCompiler.exe", "nuget.exe",
            // system
            "explorer.exe", "rundll32.exe", "msiexec.exe", "WerFault.exe", "svchost.exe", "dllhost.exe", "taskhostw.exe",
            // browsers
            "chrome.exe", "msedge.exe", "msedgewebview2.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
            "browser.exe", "iexplore.exe",
        };

        // The shell and the COM launchers: whatever they start was started by the person (or by
        // Windows), not by the program further up — the ancestor walk stops there.
        private static readonly HashSet<string> TreeBarriers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "svchost.exe", "dllhost.exe", "taskhostw.exe", "sihost.exe", "services.exe",
            "wininit.exe", "winlogon.exe", "userinit.exe", "RuntimeBroker.exe",
        };

        public sealed class Env
        {
            // Full path → a standalone installed application (StandaloneApps.Contains).
            public Func<string, bool> IsStandalone = p => false;
            // Full path → its version info words (ProductName, FileDescription, CompanyName).
            public Func<string, string[]> VersionWords = p => null;
            // Exe paths the engine saw on a connection (MihomoController.ConnStat.ProcessPath).
            public ICollection<string> NetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Exe paths already decided and waiting to be applied.
            public ICollection<string> PendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Shared places (null = this machine's) and the Windows folder (null = this machine's).
            public List<string> SharedDirs;
            public string WinDir;
            // The launcher's and the engine's own exe names: never added anywhere.
            public ICollection<string> OwnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class Decision
        {
            public AppEntry Tile;
            public string Path = "", Name = "", AutoFrom = "", Why = "";
        }

        public sealed class Skip
        {
            public string Path = "", Name = "", Reason = "";
        }

        public sealed class Result
        {
            public List<Decision> Add = new List<Decision>();
            public List<Skip> Skipped = new List<Skip>();
        }

        // Does this tile take part? Its switch on, and something to watch.
        public static bool Tracks(AppEntry a)
            => a != null && a.AutoTrack && !a.IsAllTraffic
               && ((a.Programs != null && a.Programs.Any(p => p != null && !string.IsNullOrWhiteSpace(p.Path))) || a.HasFolders);

        public static bool AnyTracking(AppConfig cfg) => (cfg?.Apps ?? new List<AppEntry>()).Any(Tracks);

        // Does `rule` refuse to auto-add this file (AppEntry.AutoIgnored)? An entry with a path is the
        // exact FILE, matched like auto-adding itself (a version bump is still that file); an old
        // entry that is a bare exe name keeps working by name. Per tile: other tiles are not asked.
        public static bool IsBanned(AppEntry rule, string exePath)
        {
            var name = FileName(exePath);
            foreach (var e in rule?.AutoIgnored ?? new List<string>())
            {
                var ban = (e ?? "").Trim();
                if (ban.Length == 0) continue;
                if (ban.IndexOf('\\') >= 0 || ban.IndexOf('/') >= 0)
                {
                    if (MihomoConfigBuilder.IsSameFile(exePath, ban)) return true;
                }
                else if (string.Equals(ban, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // The rule's editor closed (saved or cancelled): its new auto entries have been seen. Only
        // this flag, only on this rule. True when something changed (the caller saves the config).
        public static bool ClearAutoNew(AppConfig cfg, string ruleId)
        {
            var rule = (cfg?.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.Id == ruleId);
            bool changed = false;
            foreach (var p in rule?.Programs ?? new List<RuleProgram>())
                if (p != null && p.AutoNew) { p.AutoNew = false; changed = true; }
            return changed;
        }

        // Exe names of the rule's not-yet-seen auto entries, for the amber gear's tooltip.
        public static List<string> AutoNewNames(AppEntry rule)
            => (rule?.Programs ?? new List<RuleProgram>()).Where(p => p != null && p.AutoNew)
                   .Select(p => FileName(p.Path)).Where(n => n.Length > 0).ToList();

        // Who already has this exe (2026-11-22): a manual entry claims its NAME (it is routed by
        // name), an auto entry only its own FILE (it is routed by that file, version bumps
        // included) — VS Code's codex.exe in «Code» leaves the Store ChatGPT's codex.exe free.
        // Null = nobody.
        public static AppEntry ClaimedBy(AppConfig cfg, string exePath)
        {
            var name = FileName(exePath);
            if (name.Length == 0) return null;
            foreach (var t in (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.IsAllTraffic))
                foreach (var p in t.Programs ?? new List<RuleProgram>())
                {
                    if (p == null) continue;
                    var file = MihomoConfigBuilder.AutoFileRegex(p);
                    if (file.Length > 0)
                    {
                        if (MihomoConfigBuilder.IsSameFile(exePath, p.Path)) return t;
                    }
                    else if (string.Equals(FileName(p.Path), name, StringComparison.OrdinalIgnoreCase)) return t;
                }
            return null;
        }

        private static string FileName(string path)
        {
            try { return System.IO.Path.GetFileName(path ?? "") ?? ""; } catch { return ""; }
        }

        private static string DirOf(string path)
        {
            try { return MihomoConfigBuilder.NormalizeFolder(System.IO.Path.GetDirectoryName(path ?? "") ?? ""); } catch { return ""; }
        }

        // `path` lies in `dir` or below (plain prefix test on normalized paths).
        private static bool Under(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            var d = dir.EndsWith("\\") ? dir : dir + "\\";
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Root
        {
            public AppEntry Tile;
            public int Index;
            public HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Dirs = new List<string>();              // install dirs (not shared)
            public Dictionary<string, string> DirExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Folders = new List<string>();
            public List<string> Files = new List<string>();             // auto entries, by their file
            public int AutoCount;
        }

        private sealed class Hit { public int Depth; public string From = ""; public bool Tree; }

        public static Result Decide(AppConfig cfg, IList<ProcessTracker.Proc> procs, Env env)
        {
            var res = new Result();
            if (cfg?.Apps == null || procs == null) return res;
            env = env ?? new Env();
            var winDir = MihomoConfigBuilder.NormalizeFolder(env.WinDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var shared = new HashSet<string>((env.SharedDirs ?? DefaultSharedDirs()).Select(MihomoConfigBuilder.NormalizeFolder).Where(s => s.Length > 0),
                                             StringComparer.OrdinalIgnoreCase);
            bool IsShared(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return true;
                try { if (string.Equals(System.IO.Path.GetPathRoot(dir)?.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; }
                if (winDir.Length > 0 && (string.Equals(dir, winDir, StringComparison.OrdinalIgnoreCase) || Under(dir, winDir))) return true;
                return shared.Contains(dir);
            }

            var tiles = cfg.Apps.Where(a => a != null && !a.IsAllTraffic).ToList();

            // Everything already claimed (ClaimedBy): folders here, names and files per path below.
            var allFolders = new List<KeyValuePair<string, AppEntry>>();
            foreach (var t in tiles)
            {
                foreach (var f in t.Folders ?? new List<RuleFolder>())
                    if (f != null && !string.IsNullOrWhiteSpace(f.Path)) allFolders.Add(new KeyValuePair<string, AppEntry>(f.Path, t));
            }

            // The roots of every tracking tile. A deny-listed program (chrome.exe, python.exe in a
            // tile of its own) is routed by its name as before, but never spreads: its children and
            // its folder are everyone's.
            var roots = new List<Root>();
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (!Tracks(t)) continue;
                var r = new Root { Tile = t, Index = i };
                foreach (var p in t.Programs ?? new List<RuleProgram>())
                {
                    if (p == null) continue;
                    if (p.Auto) r.AutoCount++;
                    var n = FileName(p.Path);
                    if (n.Length == 0 || DenyList.Contains(n)) continue;
                    // An auto entry is a root by its own file only, like it is routed.
                    if (MihomoConfigBuilder.AutoFileRegex(p).Length > 0) r.Files.Add(p.Path);
                    else r.Names.Add(n);
                    var d = DirOf(p.Path);
                    if (d.Length > 0 && !IsShared(d) && !r.DirExe.ContainsKey(d)) { r.Dirs.Add(d); r.DirExe[d] = n; }
                }
                foreach (var f in t.Folders ?? new List<RuleFolder>())
                    if (f != null && !string.IsNullOrWhiteSpace(f.Path)) r.Folders.Add(f.Path);
                if (r.Names.Count > 0 || r.Files.Count > 0 || r.Folders.Count > 0) roots.Add(r);
            }
            if (roots.Count == 0) return res;

            // pid → every record with that pid (alive and exited: pids are reused).
            var byPid = new Dictionary<int, List<ProcessTracker.Proc>>();
            foreach (var p in procs)
            {
                if (p == null) continue;
                List<ProcessTracker.Proc> l;
                if (!byPid.TryGetValue(p.Pid, out l)) byPid[p.Pid] = l = new List<ProcessTracker.Proc>();
                l.Add(p);
            }
            ProcessTracker.Proc ParentOf(ProcessTracker.Proc c)
            {
                List<ProcessTracker.Proc> l;
                if (c.ParentPid <= 0 || c.ParentPid == c.Pid || !byPid.TryGetValue(c.ParentPid, out l)) return null;
                ProcessTracker.Proc best = null;
                foreach (var x in l)
                {
                    if (ReferenceEquals(x, c)) continue;
                    bool known = x.Created != DateTime.MinValue && c.Created != DateTime.MinValue;
                    // A parent starts before its child, and does not start it after it is gone.
                    if (known && x.Created > c.Created) continue;
                    if (!x.Alive && c.Created != DateTime.MinValue && x.ExitedAt < c.Created.AddSeconds(-5)) continue;
                    if (best == null || x.Created > best.Created) best = x;
                }
                return best;
            }
            bool IsRootOf(Root r, ProcessTracker.Proc a)
            {
                if (r.Names.Contains(a.Name ?? "")) return true;
                if (!string.IsNullOrEmpty(a.Path))
                {
                    foreach (var f in r.Files) if (MihomoConfigBuilder.IsSameFile(a.Path, f)) return true;
                    foreach (var f in r.Folders) if (MihomoConfigBuilder.InFolder(a.Path, f)) return true;
                }
                return false;
            }
            // Nearest ancestor of `p` that is a root of `r`: (depth, its name), or null.
            Hit TreeHit(Root r, ProcessTracker.Proc p)
            {
                var a = ParentOf(p);
                for (int depth = 1; a != null && depth <= 32; depth++)
                {
                    if (IsRootOf(r, a)) return new Hit { Depth = depth, From = a.Name ?? "", Tree = true };
                    if (TreeBarriers.Contains(a.Name ?? "")) return null;
                    a = ParentOf(a);
                }
                return null;
            }

            // Files decided earlier (being applied) or in this pass: each claims its own file, as an
            // auto entry does once it is in the config.
            var decidedPaths = new List<string>(env.PendingPaths ?? new HashSet<string>());

            foreach (var group in procs.Where(p => p != null && !string.IsNullOrEmpty(p.Path))
                                       .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
            {
                var path = group.Key;
                var name = FileName(path);
                if (name.Length == 0) continue;
                if (env.OwnNames != null && env.OwnNames.Contains(name)) continue;
                if (!group.Any(p => p.Net) && (env.NetPaths == null || !env.NetPaths.Contains(path))) continue;   // never went online

                // Which tiles it could belong to at all.
                var hits = new Dictionary<Root, Hit>();
                foreach (var r in roots)
                {
                    Hit best = null;
                    foreach (var p in group)
                    {
                        var h = TreeHit(r, p);
                        if (h != null && (best == null || h.Depth < best.Depth)) best = h;
                    }
                    if (best == null)
                        foreach (var d in r.Dirs)
                            if (Under(path, d)) { best = new Hit { Depth = 1000, From = "folder:" + r.DirExe[d] }; break; }
                    if (best != null) hits[r] = best;
                }
                if (hits.Count == 0) continue;   // nobody's helper: not even worth a log line

                string Via(Hit h) => h.Tree ? "потомок " + h.From : "из папки программы " + h.From.Substring(7);
                var firstHit = hits.OrderBy(kv => kv.Value.Depth).First();
                Skip SkipOf(string reason) => new Skip { Path = path, Name = name, Reason = reason + " (" + Via(firstHit.Value) + ", «" + firstHit.Key.Tile.Name + "»)" };

                // ---- reasons that hold for every tile
                var owner = ClaimedBy(cfg, path);
                if (owner != null) { res.Skipped.Add(SkipOf("уже в плитке «" + owner.Name + "»")); continue; }
                if (decidedPaths.Any(d => MihomoConfigBuilder.IsSameFile(path, d))) continue;   // decided, being applied
                var inFolder = allFolders.FirstOrDefault(f => MihomoConfigBuilder.InFolder(path, f.Key));
                if (inFolder.Value != null) { res.Skipped.Add(SkipOf("лежит в папке плитки «" + inFolder.Value.Name + "»")); continue; }
                if (winDir.Length > 0 && Under(path, winDir)) { res.Skipped.Add(SkipOf("программа Windows")); continue; }
                if (DenyList.Contains(name))
                {
                    res.Skipped.Add(SkipOf("общая программа (оболочка, среда выполнения, браузер…) — по имени в правило ушли бы все её копии на компьютере"));
                    continue;
                }

                // ---- per tile
                var why = new List<string>();
                var left = new List<KeyValuePair<Root, Hit>>();
                foreach (var kv in hits)
                {
                    var r = kv.Key;
                    // A ban drops only THIS tile; the others still qualify and choose as usual.
                    if (IsBanned(r.Tile, path))
                    { why.Add("в «" + r.Tile.Name + "» запрещено добавлять автоматически (убрана вручную)"); continue; }
                    if (r.AutoCount >= MaxAutoPerRule)
                    { why.Add("в «" + r.Tile.Name + "» уже " + MaxAutoPerRule + " авто-программ (предел)"); continue; }
                    bool ownDir = r.Dirs.Any(d => Under(path, d));
                    if (!ownDir && env.IsStandalone != null && env.IsStandalone(path))
                    { why.Add("отдельное приложение (ярлык в «Пуске», App Paths или браузер по умолчанию), не из папки «" + r.Tile.Name + "»"); continue; }
                    if (!ownDir && group.Any(p => p.Alive && TreeHit(r, p) == null))
                    { why.Add("эта же программа работает и вне «" + r.Tile.Name + "» — общая"); continue; }
                    left.Add(kv);
                }
                if (left.Count == 0) { res.Skipped.Add(SkipOf(string.Join("; ", why))); continue; }

                // ---- the best tile: its name in the program; then the nearest ancestor; then leftmost
                var words = env.VersionWords?.Invoke(path);
                var pick = left.OrderByDescending(kv => Score(kv.Key.Tile.Name, path, words))
                               .ThenBy(kv => kv.Value.Depth)
                               .ThenBy(kv => kv.Key.Index)
                               .First();
                res.Add.Add(new Decision
                {
                    Tile = pick.Key.Tile, Path = path, Name = name,
                    AutoFrom = pick.Value.From, Why = Via(pick.Value),
                });
                decidedPaths.Add(path);
                pick.Key.AutoCount++;
            }
            return res;
        }

        // How well a tile's NAME fits a program: each word of the name (letters/digits, 3+ long)
        // found in the exe's file name (strongest), in its version info, in its folders (the
        // nearer the folder, the stronger).
        public static int Score(string tileName, string path, string[] versionWords)
        {
            var tokens = Regex.Split((tileName ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
                              .Where(t => t.Length >= 3).Distinct().ToList();
            if (tokens.Count == 0) return 0;
            string file = "";
            try { file = (System.IO.Path.GetFileNameWithoutExtension(path ?? "") ?? "").ToLowerInvariant(); } catch { }
            var dirs = new List<string>();
            try
            {
                var d = System.IO.Path.GetDirectoryName(path ?? "") ?? "";
                dirs = Enumerable.Reverse(d.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)).Select(s => s.ToLowerInvariant()).ToList();
            }
            catch { }
            var words = (versionWords ?? new string[0]).Where(w => !string.IsNullOrEmpty(w)).Select(w => w.ToLowerInvariant()).ToList();

            int score = 0;
            foreach (var t in tokens)
            {
                if (file.Contains(t)) score += 100;
                if (words.Any(w => w.Contains(t))) score += 40;
                for (int i = 0; i < dirs.Count; i++)
                    if (dirs[i].Contains(t)) { score += Math.Max(1, 30 - 3 * i); break; }
            }
            return score;
        }

        // A drive root, Windows and below, the Program Files roots themselves, the profile and its
        // usual places: an "install dir" there would be everyone's.
        public static List<string> DefaultSharedDirs()
        {
            var list = new List<string>();
            void Add(string p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(p); }
            void AddSpecial(Environment.SpecialFolder f) { try { Add(Environment.GetFolderPath(f)); } catch { } }
            AddSpecial(Environment.SpecialFolder.ProgramFiles);
            AddSpecial(Environment.SpecialFolder.ProgramFilesX86);
            Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            AddSpecial(Environment.SpecialFolder.UserProfile);
            AddSpecial(Environment.SpecialFolder.Desktop);
            AddSpecial(Environment.SpecialFolder.DesktopDirectory);
            AddSpecial(Environment.SpecialFolder.CommonDesktopDirectory);
            AddSpecial(Environment.SpecialFolder.MyDocuments);
            AddSpecial(Environment.SpecialFolder.CommonDocuments);
            AddSpecial(Environment.SpecialFolder.ApplicationData);
            AddSpecial(Environment.SpecialFolder.LocalApplicationData);
            AddSpecial(Environment.SpecialFolder.CommonApplicationData);
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (profile.Length > 0) Add(System.IO.Path.Combine(profile, "Downloads"));
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (local.Length > 0)
                {
                    Add(System.IO.Path.Combine(local, "Programs"));
                    Add(System.IO.Path.Combine(local, "Temp"));
                    Add(System.IO.Path.Combine(local, @"Microsoft\WindowsApps"));   // the exe aliases
                }
            }
            catch { }
            try { Add(System.IO.Path.GetTempPath()); } catch { }
            return list;
        }

        // ProductName / FileDescription / CompanyName, cached per path.
        private static readonly Dictionary<string, string[]> _verCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static string[] VersionWordsOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_verCache)
            {
                string[] hit;
                if (_verCache.TryGetValue(path, out hit)) return hit;
            }
            string[] words = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                words = new[] { vi.ProductName ?? "", vi.FileDescription ?? "", vi.CompanyName ?? "" };
            }
            catch { }
            lock (_verCache) { if (_verCache.Count > 4000) _verCache.Clear(); _verCache[path] = words; }
            return words;
        }
    }

    // Standalone installed applications: targets of Start-menu shortcuts (this user's and all
    // users'), App Paths entries, the default http/https handler. Built once in the background
    // and refreshed every 30 minutes — never on the caller's thread.
    public static class StandaloneApps
    {
        private static HashSet<string> _set;
        private static DateTime _builtAt = DateTime.MinValue;
        private static int _building;
        private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(30);

        public static bool Ready { get { Kick(); return _set != null; } }

        public static bool Contains(string path)
        {
            Kick();
            var s = _set;
            return s != null && !string.IsNullOrEmpty(path) && s.Contains(path);
        }

        private static void Kick()
        {
            if (_set != null && DateTime.UtcNow - _builtAt < Refresh) return;
            if (Interlocked.Exchange(ref _building, 1) == 1) return;
            // WScript.Shell is happiest in a single-threaded apartment.
            var t = new Thread(() =>
            {
                try
                {
                    var set = Build();
                    _set = set;
                    _builtAt = DateTime.UtcNow;
                }
                catch (Exception ex) { Log.Error("standalone-apps", ex); _builtAt = DateTime.UtcNow; if (_set == null) _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
                finally { Interlocked.Exchange(ref _building, 0); }
            }) { IsBackground = true, Name = "StandaloneApps", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static HashSet<string> Build()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    var x = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
                    if (x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) set.Add(Path.GetFullPath(x));
                }
                catch { }
            }

            // Start menu shortcuts.
            object shell = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null) shell = Activator.CreateInstance(type);
            }
            catch { }
            if (shell != null)
            {
                var dirs = new List<string>();
                try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)); } catch { }
                try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)); } catch { }
                int n = 0;
                foreach (var d in dirs.Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x)))
                {
                    IEnumerable<string> links;
                    try { links = Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories).ToList(); }
                    catch { continue; }
                    foreach (var lnk in links)
                    {
                        if (++n > 3000) break;
                        try
                        {
                            dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                            Add((string)sc.TargetPath);
                            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
                        }
                        catch { }
                    }
                }
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }

            // App Paths (machine 64/32-bit, user).
            void AppPaths(RegistryKey baseKey)
            {
                try
                {
                    using (var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                    {
                        if (k == null) return;
                        foreach (var name in k.GetSubKeyNames())
                            try { using (var s = k.OpenSubKey(name)) Add(s?.GetValue("") as string); } catch { }
                    }
                }
                catch { }
            }
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) AppPaths(b); } catch { }
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)) AppPaths(b); } catch { }
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)) AppPaths(b); } catch { }

            // The default browser (http / https handler).
            foreach (var scheme in new[] { "http", "https" })
            {
                try
                {
                    string progId;
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice"))
                        progId = k?.GetValue("ProgId") as string;
                    if (string.IsNullOrEmpty(progId)) continue;
                    using (var k = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    {
                        var cmd = (k?.GetValue("") as string ?? "").Trim();
                        if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); if (end > 1) Add(cmd.Substring(1, end - 1)); }
                        else { int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (exe > 0) Add(cmd.Substring(0, exe + 4)); }
                    }
                }
                catch { }
            }
            return set;
        }
    }
}
