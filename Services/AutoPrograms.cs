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
    // "Roots" of a tile (AutoTrack on, not «Всё остальное»): its programs (manual entries by exe
    // FILE NAME — routing is by name, so is this; auto entries by their own FILE), and any
    // process running from one of its folders.
    // A process P belongs to tile T when
    //   · tree:        one of P's ancestors is a root of T and NO process between them is a
    //                  "runs-anything" launcher — a shell, a runtime, a terminal, a debugger, a
    //                  tree barrier, anything from the Windows folder. A real helper is started by
    //                  its program directly (codex.exe <- Code.exe); what a shell inside the
    //                  program started (tool.exe <- powershell.exe <- cmd.exe <- claude.exe)
    //                  is the person's own tool, not the program's helper. Checked in this order:
    //                  "is it a root?" first, then "is it a launcher?".
    //   · install dir: P lies under the directory of one of T's MANUAL programs, whoever its
    //                  parent is — a launcher stub that starts the real app and quits, an updater
    //                  started by the Task Scheduler. Not when that directory is a shared place (a
    //                  drive root, Windows, the Program Files roots themselves, the profile,
    //                  Desktop…). An auto entry never makes its folder a program folder: one wrong
    //                  addition must not pull its whole folder in after it.
    // and P has actually used the network (the engine saw it, or it had a TCP connection to a
    // non-loopback address): helpers that never go online — ripgrep, language servers, consoles —
    // must not litter the list; and the evidence is fresh: one of P's processes is alive or
    // exited at most 60 s ago (the tracker remembers exited processes for 10 minutes — enough to
    // tie a child to a parent that is gone, too long to re-add what the person just removed).
    //
    // Skipped, with the reason in the log (once per exe per session — how problems on other
    // people's PCs get diagnosed): a name already in ANY tile or a path under any tile's folder,
    // a name the person removed from T, anything under Windows, anything under %TEMP% (a rule on
    // such a path is useless), the deny-list of shared binaries below, a standalone installed
    // application outside T's own folder (a Start-menu shortcut, an App Paths entry, the default
    // browser — an app opening a link starts the browser as its child), a program outside T's own
    // folder that has (or had this session) an application window of its own (Env.WindowedPaths —
    // a helper has none; inside T's own folder a window is fine), an exe that runs right now
    // outside T's tree (a shared helper), and — one line — a program whose only way to T was cut
    // by a launcher in between.
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

        // Terminals, debuggers and task runners: like the shells of the deny-list, they start
        // whatever the person asks — what runs under them is not the program's helper.
        private static readonly HashSet<string> Launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal.exe", "wt.exe", "vsdbg.exe", "vsdbg-ui.exe", "msvsmon.exe", "gdb.exe", "lldb.exe",
            "dlv.exe", "cargo.exe", "npm.exe", "npx.exe", "mintty.exe", "tmux.exe",
        };

        // How long after its last process exited a program can still be added.
        public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);

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
            // Exe paths that have (or had this session) an application window (AppWindows).
            public ICollection<string> WindowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Temp folders (null = this machine's: Path.GetTempPath() and %LocalAppData%\Temp).
            public List<string> TempDirs;
            // "Now" for the freshness test (a probe sets its own).
            public DateTime NowUtc = DateTime.UtcNow;
            // The launcher's own folder (null = VersionInfo.InstallRoot): never an auto folder.
            public string InstallRoot;
        }

        public sealed class Decision
        {
            public AppEntry Tile;
            public string Path = "", Name = "", AutoFrom = "", Why = "";
            // It lies in the folder of one of the tile's manual programs (a window does not matter there).
            public bool OwnDir;
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

        // Does this tile take part? Its switch on, and something to watch (an auto folder is a
        // folder like any other, 2026-11-23).
        public static bool Tracks(AppEntry a)
            => a != null && a.AutoTrack && !a.IsAllTraffic
               && ((a.Programs != null && a.Programs.Any(p => p != null && !string.IsNullOrWhiteSpace(p.Path))) || a.HasFolders);

        public static bool AnyTracking(AppConfig cfg) => (cfg?.Apps ?? new List<AppEntry>()).Any(Tracks);

        // Does `rule` refuse to auto-add this file (AppEntry.AutoIgnored)? An entry with a path is the
        // exact FILE, matched like auto-adding itself (a version bump is still that file); an old
        // entry that is a bare exe name keeps working by name. Per tile: other tiles are not asked.
        // An entry ending with a separator is a FOLDER ban (IsFolderBanned) and is not asked here.
        public static bool IsBanned(AppEntry rule, string exePath)
        {
            var name = FileName(exePath);
            foreach (var e in rule?.AutoIgnored ?? new List<string>())
            {
                var ban = (e ?? "").Trim();
                if (ban.Length == 0) continue;
                if (IsFolderBanKey(ban)) continue;
                if (ban.IndexOf('\\') >= 0 || ban.IndexOf('/') >= 0)
                {
                    if (MihomoConfigBuilder.IsSameFile(exePath, ban)) return true;
                }
                else if (string.Equals(ban, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // A folder ban (2026-11-23): what the editor's ✕ on an auto folder stores in
        // AppEntry.AutoIgnored — NormalizeFolder(path) + "\". Told apart from a file ban by the
        // trailing separator.
        public static bool IsFolderBanKey(string ban)
        {
            var b = (ban ?? "").Trim();
            return b.Length > 1 && (b.EndsWith("\\") || b.EndsWith("/"));
        }

        public static string FolderBanKey(string dir)
        {
            var d = MihomoConfigBuilder.NormalizeFolder(dir);
            return d.Length == 0 ? "" : (d.EndsWith("\\") ? d : d + "\\");
        }

        // Does `rule` refuse this folder as an auto folder? A folder ban for the same folder (a
        // version bump is still that folder, MihomoConfigBuilder.SameFolder). Per tile.
        public static bool IsFolderBanned(AppEntry rule, string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            foreach (var e in rule?.AutoIgnored ?? new List<string>())
            {
                var ban = (e ?? "").Trim();
                if (!IsFolderBanKey(ban)) continue;
                if (MihomoConfigBuilder.SameFolder(ban, dir)) return true;
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
            foreach (var f in rule?.Folders ?? new List<RuleFolder>())
                if (f != null && f.AutoNew) { f.AutoNew = false; changed = true; }
            return changed;
        }

        // ---------- the notice's clock (2026-11-23) ----------
        //
        // A rule's amber notice goes out by itself once the main window has been on screen for an
        // hour in total since the last automatic addition to it (AppEntry.AutoNoticeShownSec; the
        // caller passes only on-screen time). Pure: the caller logs, ends the notice and saves.
        public const int NoticeLimitSec = 3600;

        public sealed class NoticeTickResult
        {
            public List<string> Expired = new List<string>();   // rule ids, AutoNew already cleared
            public bool Changed;
        }

        public static NoticeTickResult NoticeTick(AppConfig cfg, int addSeconds)
        {
            var res = new NoticeTickResult();
            foreach (var rule in cfg?.Apps ?? new List<AppEntry>())
            {
                if (rule == null) continue;
                if (AutoNewNames(rule).Count == 0)
                {
                    // Nothing unseen: no clock running.
                    if (rule.AutoNoticeShownSec != 0) { rule.AutoNoticeShownSec = 0; res.Changed = true; }
                    continue;
                }
                if (addSeconds <= 0) continue;
                rule.AutoNoticeShownSec = Math.Max(0, rule.AutoNoticeShownSec) + addSeconds;
                res.Changed = true;
                if (rule.AutoNoticeShownSec < NoticeLimitSec) continue;
                ClearAutoNew(cfg, rule.Id);
                rule.AutoNoticeShownSec = 0;
                res.Expired.Add(rule.Id);
            }
            return res;
        }

        // Names of the rule's not-yet-seen auto entries, for the amber gear's tooltip: auto folders
        // first, as their leaf name + "\" («Microsoft VS Code\»), then exe names.
        public static List<string> AutoNewNames(AppEntry rule)
        {
            var list = new List<string>();
            foreach (var f in rule?.Folders ?? new List<RuleFolder>())
            {
                if (f == null || !f.AutoNew) continue;
                var leaf = LeafOf(f.Path);
                if (leaf.Length > 0) list.Add(leaf + "\\");
            }
            list.AddRange((rule?.Programs ?? new List<RuleProgram>()).Where(p => p != null && p.AutoNew)
                              .Select(p => FileName(p.Path)).Where(n => n.Length > 0));
            return list;
        }

        // "C:\Program Files\Microsoft VS Code" → "Microsoft VS Code"; "" if unusable.
        public static string LeafOf(string dir)
        {
            try
            {
                var trimmed = (dir ?? "").Trim().TrimEnd('\\', '/');
                var leaf = System.IO.Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
            }
            catch { return ""; }
        }

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

        private sealed class Hit { public int Depth; public string From = "", Chain = ""; public bool Tree; }

        public static List<string> DefaultTempDirs()
        {
            var list = new List<string>();
            try { list.Add(System.IO.Path.GetTempPath()); } catch { }
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (local.Length > 0) list.Add(System.IO.Path.Combine(local, "Temp"));
            }
            catch { }
            return list;
        }

        // The machine's places, as Decide and SyncAutoFolders read them from an Env (2026-11-23:
        // taken out of Decide unchanged, so both use the very same "shared place" test).
        private sealed class Places
        {
            public string WinDir = "";
            public HashSet<string> Shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<string> TempDirs = new List<string>();

            // A drive root, Windows and below, one of the shared dirs themselves.
            public bool IsShared(string dir)
            {
                if (string.IsNullOrEmpty(dir)) return true;
                try { if (string.Equals(System.IO.Path.GetPathRoot(dir)?.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; }
                if (WinDir.Length > 0 && (string.Equals(dir, WinDir, StringComparison.OrdinalIgnoreCase) || Under(dir, WinDir))) return true;
                return Shared.Contains(dir);
            }
        }

        private static Places PlacesOf(Env env)
        {
            var p = new Places();
            p.WinDir = MihomoConfigBuilder.NormalizeFolder(env.WinDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            p.Shared = new HashSet<string>((env.SharedDirs ?? DefaultSharedDirs()).Select(MihomoConfigBuilder.NormalizeFolder).Where(s => s.Length > 0),
                                           StringComparer.OrdinalIgnoreCase);
            p.TempDirs = (env.TempDirs ?? DefaultTempDirs()).Select(MihomoConfigBuilder.NormalizeFolder)
                                                            .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return p;
        }

        public static Result Decide(AppConfig cfg, IList<ProcessTracker.Proc> procs, Env env)
        {
            var res = new Result();
            if (cfg?.Apps == null || procs == null) return res;
            env = env ?? new Env();
            var places = PlacesOf(env);
            var winDir = places.WinDir;
            bool IsShared(string dir) => places.IsShared(dir);
            var tempDirs = places.TempDirs;
            var now = env.NowUtc;

            var tiles = cfg.Apps.Where(a => a != null && !a.IsAllTraffic).ToList();

            // Everything already claimed (ClaimedBy): folders here, names and files per path below.
            // An auto folder too (2026-11-23): what lies in it is already the tile's — no row of its
            // own («лежит в папке плитки»).
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
                    // Only a manual entry makes its folder a program folder: an auto entry that got
                    // in by mistake must not bring everything next to it along.
                    if (p.Auto) continue;
                    var d = DirOf(p.Path);
                    if (d.Length > 0 && !IsShared(d) && !r.DirExe.ContainsKey(d)) { r.Dirs.Add(d); r.DirExe[d] = n; }
                }
                // An auto folder is a root too (2026-11-23): an updater started from it by the Task
                // Scheduler still makes the helpers it starts elsewhere the tile's.
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
            // A process that starts whatever it is asked to: a shell, a runtime, a terminal, a
            // debugger, a tree barrier, anything from the Windows folder (path may be unknown).
            bool IsLauncher(ProcessTracker.Proc a)
            {
                var n = a.Name ?? "";
                if (TreeBarriers.Contains(n) || DenyList.Contains(n) || Launchers.Contains(n)) return true;
                return winDir.Length > 0 && !string.IsNullOrEmpty(a.Path) && Under(a.Path, winDir);
            }
            // Nearest ancestor of `p` that is a root of `r` with no launcher in between: (depth, its
            // name, the chain), or null. `cutBy`: the launcher that cut the way to a root of `r`
            // further up (not past a tree barrier), for the log; null when there was none.
            Hit TreeHit(Root r, ProcessTracker.Proc p, out string cutBy)
            {
                cutBy = null;
                string launcher = null;
                var chain = new List<string> { p.Name ?? "" };
                var a = ParentOf(p);
                for (int depth = 1; a != null && depth <= 32; depth++)
                {
                    if (IsRootOf(r, a))
                    {
                        if (launcher != null) { cutBy = launcher; return null; }
                        chain.Add(a.Name ?? "");
                        chain.Reverse();
                        return new Hit { Depth = depth, From = a.Name ?? "", Chain = string.Join(" → ", chain), Tree = true };
                    }
                    if (TreeBarriers.Contains(a.Name ?? "")) return null;
                    if (launcher == null && IsLauncher(a)) launcher = a.Name ?? "";
                    chain.Add(a.Name ?? "");
                    a = ParentOf(a);
                }
                return null;
            }
            Hit TreeHitOf(Root r, ProcessTracker.Proc p) { string cut; return TreeHit(r, p, out cut); }

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
                // Fresh evidence only: alive, or gone at most a minute ago.
                if (!group.Any(p => p.Alive || now - p.ExitedAt <= FreshFor)) continue;

                // Which tiles it could belong to at all.
                var hits = new Dictionary<Root, Hit>();
                string cutBy = null; Root cutTile = null;
                foreach (var r in roots)
                {
                    Hit best = null;
                    foreach (var p in group)
                    {
                        string cut;
                        var h = TreeHit(r, p, out cut);
                        if (h != null && (best == null || h.Depth < best.Depth)) best = h;
                        if (h == null && cut != null && cutBy == null) { cutBy = cut; cutTile = r; }
                    }
                    if (best == null)
                        foreach (var d in r.Dirs)
                            if (Under(path, d)) { best = new Hit { Depth = 1000, From = "folder:" + r.DirExe[d] }; break; }
                    if (best != null) hits[r] = best;
                }
                if (hits.Count == 0)
                {
                    // Nobody's helper: not even worth a log line — unless a launcher cut its way to a
                    // tile (otherwise «не добавилась» on someone else's PC can't be told apart).
                    if (cutBy != null)
                        res.Skipped.Add(new Skip
                        {
                            Path = path, Name = name,
                            Reason = "запущена через " + cutBy + " (оболочка, среда выполнения, отладчик) — не помощник «" + cutTile.Tile.Name + "»",
                        });
                    continue;
                }

                string Via(Hit h) => h.Tree ? "потомок " + h.From + " (" + h.Chain + ")" : "из папки программы " + h.From.Substring(7);
                var firstHit = hits.OrderBy(kv => kv.Value.Depth).First();
                Skip SkipOf(string reason) => new Skip { Path = path, Name = name, Reason = reason + " (" + Via(firstHit.Value) + ", «" + firstHit.Key.Tile.Name + "»)" };

                // ---- reasons that hold for every tile
                var owner = ClaimedBy(cfg, path);
                if (owner != null) { res.Skipped.Add(SkipOf("уже в плитке «" + owner.Name + "»")); continue; }
                if (decidedPaths.Any(d => MihomoConfigBuilder.IsSameFile(path, d))) continue;   // decided, being applied
                var inFolder = allFolders.FirstOrDefault(f => MihomoConfigBuilder.InFolder(path, f.Key));
                if (inFolder.Value != null) { res.Skipped.Add(SkipOf("лежит в папке плитки «" + inFolder.Value.Name + "»")); continue; }
                if (winDir.Length > 0 && Under(path, winDir)) { res.Skipped.Add(SkipOf("программа Windows")); continue; }
                if (tempDirs.Any(t => Under(path, t))) { res.Skipped.Add(SkipOf("временный файл — правило по такому пути бесполезно")); continue; }
                if (DenyList.Contains(name))
                {
                    res.Skipped.Add(SkipOf("общая программа (оболочка, среда выполнения, браузер…) — по имени в правило ушли бы все её копии на компьютере"));
                    continue;
                }

                // ---- per tile
                var why = new List<string>();
                var left = new List<KeyValuePair<Root, Hit>>();
                var ownDirOf = new Dictionary<Root, bool>();
                foreach (var kv in hits)
                {
                    var r = kv.Key;
                    // A ban drops only THIS tile; the others still qualify and choose as usual.
                    if (IsBanned(r.Tile, path))
                    { why.Add("в «" + r.Tile.Name + "» запрещено добавлять автоматически (убрана вручную)"); continue; }
                    if (r.AutoCount >= MaxAutoPerRule)
                    { why.Add("в «" + r.Tile.Name + "» уже " + MaxAutoPerRule + " авто-программ (предел)"); continue; }
                    bool ownDir = r.Dirs.Any(d => Under(path, d));
                    ownDirOf[r] = ownDir;
                    if (!ownDir && env.IsStandalone != null && env.IsStandalone(path))
                    { why.Add("отдельное приложение (ярлык в «Пуске», App Paths или браузер по умолчанию), не из папки «" + r.Tile.Name + "»"); continue; }
                    if (!ownDir && env.WindowedPaths != null && env.WindowedPaths.Contains(path))
                    { why.Add("самостоятельная программа со своим окном, запущена из «" + r.Tile.Name + "»"); continue; }
                    if (!ownDir && group.Any(p => p.Alive && TreeHitOf(r, p) == null))
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
                    OwnDir = ownDirOf[pick.Key],
                });
                decidedPaths.Add(path);
                pick.Key.AutoCount++;
            }
            return res;
        }

        // ---------- «Авто-папка программы» (2026-11-23) ----------
        //
        // Auto-adding (Decide) catches a helper only after it has gone online, and the engine then
        // needs a reload: its first connections go the default way — possibly DIRECT, the home IP.
        // A folder rule is in the engine before the helper starts. So a tile with a MANUAL program
        // gets that program's install folder too, as an auto folder (RuleFolder.Auto) — when the
        // folder is the program's own (by name, FolderNamedForProgram), not a shared place, not
        // the launcher's, not refused by the person (a folder ban, or a file of it they removed),
        // and no other tile has anything in it, around it or inside it (the engine would split
        // that folder between tiles). To Decide an auto folder is a folder like a manual one: what
        // lies in it gets no row of its own, and a process started from it is a root of the tile
        // (its helpers OUTSIDE the folder are still found).
        //
        // Upkeep, whatever the tile's AutoTrack: an auto folder with «Плитка» is the person's and
        // stays; one whose program left the tile, one another tile now touches, one a manual folder
        // of the tile already covers, one the person refused (a folder ban, or a file of it removed
        // from the tile) — goes. Otherwise it stays (the other add conditions are not asked again),
        // following its program's folder through a version bump.
        // Then the tile's auto programs inside its auto folders go (user, 2026-11-23: a tile lists
        // the folder OR its helpers, never both) — not a manual one, not one with «Плитка», never
        // another tile's; the folder sends them the same way.
        // Pure: no log, no disk (VersionWords is the Env's). Mutates `cfg`; idempotent.

        public sealed class FolderAdded
        {
            public AppEntry Tile;
            public string Path = "", Exe = "";
        }

        public sealed class FolderRemoved
        {
            public AppEntry Tile;
            public string Path = "", Reason = "";
        }

        // An auto program taken out because the tile's auto folder already covers it.
        public sealed class ProgramDropped
        {
            public AppEntry Tile;
            public string Path = "", Folder = "";
        }

        public sealed class FolderSync
        {
            public bool Changed;
            public List<FolderAdded> Added = new List<FolderAdded>();
            public List<FolderRemoved> Removed = new List<FolderRemoved>();
            public List<ProgramDropped> DroppedPrograms = new List<ProgramDropped>();
        }

        public static FolderSync SyncAutoFolders(AppConfig cfg, Env env)
        {
            var res = new FolderSync();
            if (cfg?.Apps == null) return res;
            env = env ?? new Env();
            var places = PlacesOf(env);
            string installRoot = "";
            try { installRoot = MihomoConfigBuilder.NormalizeFolder(env.InstallRoot ?? VersionInfo.InstallRoot); } catch { }

            var tiles = cfg.Apps.Where(a => a != null && !a.IsAllTraffic).ToList();

            // The other tile that has something in `dir`, around it or inside it; null = none.
            AppEntry TouchedBy(AppEntry self, string dir)
            {
                foreach (var u in tiles)
                {
                    if (ReferenceEquals(u, self)) continue;
                    foreach (var p in u.Programs ?? new List<RuleProgram>())
                        if (p != null && !string.IsNullOrWhiteSpace(p.Path) && MihomoConfigBuilder.InFolder(p.Path.Trim(), dir)) return u;
                    foreach (var f in u.Folders ?? new List<RuleFolder>())
                    {
                        var fp = MihomoConfigBuilder.NormalizeFolder(f?.Path);
                        if (fp.Length == 0) continue;
                        if (MihomoConfigBuilder.SameFolder(fp, dir)
                            || MihomoConfigBuilder.InFolder(fp + "\\", dir)      // F inside D
                            || MihomoConfigBuilder.InFolder(dir + "\\", fp))     // D inside F
                            return u;
                    }
                }
                return null;
            }
            // `dir` is `folder` or lies inside it.
            bool Covers(string folder, string dir)
            {
                var fp = MihomoConfigBuilder.NormalizeFolder(folder);
                return fp.Length > 0 && (MihomoConfigBuilder.SameFolder(fp, dir) || MihomoConfigBuilder.InFolder(dir + "\\", fp));
            }
            List<RuleProgram> ManualProgs(AppEntry t)
                => (t.Programs ?? new List<RuleProgram>()).Where(p => p != null && !p.Auto && !string.IsNullOrWhiteSpace(p.Path)).ToList();

            // ---- upkeep of the auto folders there are
            foreach (var t in tiles)
            {
                if (t.Folders == null) continue;
                foreach (var e in t.Folders.ToList())
                {
                    if (e == null || !e.Auto) continue;
                    if (e.Launch) continue;   // the person gave it the tile: theirs now
                    var path = MihomoConfigBuilder.NormalizeFolder(e.Path);
                    string reason = null;
                    var manual = ManualProgs(t);
                    if (path.Length == 0 || !manual.Any(p => MihomoConfigBuilder.InFolder(p.Path.Trim(), path)))
                        reason = "её программы больше нет в плитке";
                    else
                    {
                        var other = TouchedBy(t, path);
                        if (other != null) reason = "в ней или рядом есть программа или папка плитки «" + other.Name + "»";
                        else if ((t.Folders ?? new List<RuleFolder>()).Any(m => m != null && !m.Auto && Covers(m.Path, path)))
                            reason = "эта папка уже есть в плитке вручную";
                        // A ✕ in the editor must keep meaning "not through this tile": a helper the
                        // person removed would otherwise still be routed by the folder rule.
                        else if (IsFolderBanned(t, path))
                            reason = "папка убрана из плитки вручную";
                        else if (HasFileBanIn(t, path))
                            reason = "в ней есть программа, убранная из плитки вручную";
                    }
                    if (reason != null)
                    {
                        t.Folders.Remove(e);
                        res.Removed.Add(new FolderRemoved { Tile = t, Path = e.Path ?? "", Reason = reason });
                        res.Changed = true;
                        continue;
                    }
                    // Kept. A version bump of its program's folder: follow it (the same folder).
                    var dirs = manual.Select(p => DirOf(p.Path.Trim())).Where(d => d.Length > 0 && MihomoConfigBuilder.SameFolder(d, path)).ToList();
                    if (dirs.Count > 0 && !dirs.Any(d => string.Equals(d, (e.Path ?? "").Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Path = dirs[0];
                        res.Changed = true;
                    }
                }
            }

            // ---- additions
            foreach (var t in tiles)
            {
                if (!t.AutoTrack) continue;
                var want = new List<KeyValuePair<string, string>>();   // dir → exe name
                foreach (var p in ManualProgs(t))
                {
                    var exePath = p.Path.Trim();
                    var exe = FileName(exePath);
                    if (exe.Length == 0 || DenyList.Contains(exe)) continue;
                    if (env.OwnNames != null && env.OwnNames.Contains(exe)) continue;
                    var d = DirOf(exePath);
                    if (d.Length == 0 || places.IsShared(d)) continue;
                    if (places.TempDirs.Any(x => string.Equals(d, x, StringComparison.OrdinalIgnoreCase) || Under(d, x))) continue;
                    if (installRoot.Length > 0 && (string.Equals(d, installRoot, StringComparison.OrdinalIgnoreCase) || Under(d, installRoot))) continue;
                    if (IsFolderBanned(t, d) || HasFileBanIn(t, d)) continue;
                    if (TouchedBy(t, d) != null) continue;
                    if ((t.Folders ?? new List<RuleFolder>()).Any(f => f != null && Covers(f.Path, d))) continue;
                    if (want.Any(w => MihomoConfigBuilder.SameFolder(w.Key, d))) continue;
                    // The name guard last, the exe's name first: its ProductName means reading the
                    // file (a dead network path would stall the UI thread), so only when the name
                    // alone does not do and everything else already holds.
                    if (!FolderNamedForProgram(exePath, null, places.Shared))
                    {
                        string[] words = null;
                        try { words = env.VersionWords?.Invoke(exePath); } catch { }
                        var product = words != null && words.Length > 0 ? words[0] : null;
                        if (product == null || !FolderNamedForProgram(exePath, product, places.Shared)) continue;
                    }
                    want.Add(new KeyValuePair<string, string>(d, exe));
                }
                // One inside another: the outer one only.
                var outer = want.Where(w => !want.Any(o => !MihomoConfigBuilder.SameFolder(o.Key, w.Key)
                                                           && MihomoConfigBuilder.InFolder(w.Key + "\\", o.Key))).ToList();
                foreach (var w in outer)
                {
                    if (t.Folders == null) t.Folders = new List<RuleFolder>();
                    t.Folders.Add(new RuleFolder { Name = "", Path = w.Key, Auto = true, AutoFrom = w.Value, AutoNew = true, Launch = false });
                    res.Added.Add(new FolderAdded { Tile = t, Path = w.Key, Exe = w.Value });
                    t.AutoNoticeShownSec = 0;   // a new addition: the notice's hour starts over
                    res.Changed = true;
                }
            }

            // ---- the tile's own auto rows its auto folders already cover
            foreach (var t in tiles)
            {
                if (t.Programs == null || t.Folders == null) continue;
                var autoDirs = t.Folders.Where(f => f != null && f.Auto && !string.IsNullOrWhiteSpace(f.Path)).ToList();
                if (autoDirs.Count == 0) continue;
                foreach (var p in t.Programs.ToList())
                {
                    if (p == null || !p.Auto || p.Launch || string.IsNullOrWhiteSpace(p.Path)) continue;
                    var f = autoDirs.FirstOrDefault(x => MihomoConfigBuilder.InFolder(p.Path.Trim(), x.Path));
                    if (f == null) continue;
                    // Kept when another tile names the same exe: the engine writes an auto file line
                    // above every program by name, a folder line below them — without the row this
                    // very file would go to that tile (VS Code's codex.exe vs ChatGPT's codex.exe).
                    var exe = FileName(p.Path.Trim());
                    if (tiles.Any(u => !ReferenceEquals(u, t) && (u.Programs ?? new List<RuleProgram>()).Any(q =>
                            q != null && !q.Auto && string.Equals(FileName((q.Path ?? "").Trim()), exe, StringComparison.OrdinalIgnoreCase))))
                        continue;
                    t.Programs.Remove(p);
                    res.DroppedPrograms.Add(new ProgramDropped { Tile = t, Path = p.Path ?? "", Folder = f.Path ?? "" });
                    res.Changed = true;
                }
            }
            return res;
        }

        // A file the person removed from this tile (a file ban: a path, not a folder) lying in `dir`:
        // a folder rule would bring it back. Old bare-name bans are not asked.
        private static bool HasFileBanIn(AppEntry rule, string dir)
        {
            foreach (var e in rule?.AutoIgnored ?? new List<string>())
            {
                var ban = (e ?? "").Trim();
                if (ban.Length == 0 || IsFolderBanKey(ban)) continue;
                if (ban.IndexOf('\\') < 0 && ban.IndexOf('/') < 0) continue;
                if (MihomoConfigBuilder.InFolder(ban, dir)) return true;
            }
            return false;
        }

        // Is the folder of `exePath` the program's OWN, by name? A folder rule takes everything
        // in the folder: ~\.local\bin\claude.exe, D:\Portable\telegram.exe, scoop\shims must not
        // turn into rules for whatever else lies next to them. The exe's name (no extension) or
        // its ProductName, as letters and digits in lower case (2+ long), against each segment of
        // the folder BELOW the deepest shared place that holds it (none: below the drive root):
        // equal, or — both 4+ long — one contains the other. `sharedDirs`: normalized folders.
        public static bool FolderNamedForProgram(string exePath, string productName, IEnumerable<string> sharedDirs)
        {
            var dir = DirOf(exePath);
            if (dir.Length == 0) return false;
            string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

            var a = new List<string>();
            try { a.Add(Norm(System.IO.Path.GetFileNameWithoutExtension(exePath ?? ""))); } catch { }
            a.Add(Norm(productName));
            a = a.Where(x => x.Length >= 2).Distinct().ToList();
            if (a.Count == 0) return false;

            // The deepest shared place that holds the folder; else the drive root.
            string root;
            try { root = (System.IO.Path.GetPathRoot(dir) ?? "").TrimEnd('\\'); } catch { return false; }
            string bas = root;
            foreach (var s in sharedDirs ?? Enumerable.Empty<string>())
            {
                var sd = MihomoConfigBuilder.NormalizeFolder(s);
                if (sd.Length == 0) continue;
                if (Under(dir, sd) && sd.TrimEnd('\\').Length > bas.Length) bas = sd.TrimEnd('\\');
            }
            if (dir.Length <= bas.Length) return false;
            var below = dir.Substring(bas.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(Norm).Where(x => x.Length > 0).ToList();

            foreach (var x in a)
                foreach (var y in below)
                {
                    if (x == y) return true;
                    if (x.Length >= 4 && y.Length >= 4 && (x.Contains(y) || y.Contains(x))) return true;
                }
            return false;
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
