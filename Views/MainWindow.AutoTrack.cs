using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // «Авто-добавление программ» on the live config (2026-11-22). ProcessTracker watches the
    // machine, AutoPrograms decides, this applies — on the UI thread, which owns _config.
    //
    // Config reloads are disruptive (the engine rejects new connections while it rereads, and
    // reload storms have taken the whole machine offline before), so additions are BATCHED: what
    // is found is collected for ~5 s, written in one save and one reload, and an auto-triggered
    // reload happens at most once per 15 s. Nothing of this ever opens a window: failures and
    // every decision go to app.log only.
    //
    // Held while something else owns a copy of the rules or is comparing "before / after": the rule
    // editor, Settings, the VPN table (each would either overwrite the addition with its copy or
    // read it as an edit and drop a live rule off its node), a live edit in flight, a connect /
    // reconnect / update.
    //
    // Known, accepted: the very first connection of a never-seen helper goes the default way for
    // the second or two before the rule is applied; a folder entry is the strict tool for that.
    // Since 2026-11-23 the launcher adds that folder itself where it safely can — the install
    // folder of a tile's manual program as an auto folder (AutoPrograms.SyncAutoFolders). To the
    // program auto-adding above it is a folder like any other: what lies in it gets no row of its
    // own (the sync drops the rows already there), what a process from it starts elsewhere is
    // still found.
    public partial class MainWindow
    {
        private ProcessTracker _procTracker;
        private DispatcherTimer _autoTimer;
        // Exe paths the engine saw on a connection → when (kept 10 minutes).
        private readonly Dictionary<string, DateTime> _autoNetPaths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private sealed class AutoPending { public AutoPrograms.Decision D; public DateTime FoundAt; }
        private readonly List<AutoPending> _autoPending = new List<AutoPending>();
        private readonly HashSet<string> _autoLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Exe paths seen with an application window of their own this session (AppWindows) —
        // sticky: a window hidden to the tray later is still that program's.
        private readonly HashSet<string> _autoWindowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _autoLastReloadUtc = DateTime.MinValue;

        private static readonly TimeSpan AutoCollect = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AutoReloadGap = TimeSpan.FromSeconds(15);

        private void StartAutoTrack()
        {
            if (_autoTimer != null) return;
            _autoTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            _autoTimer.Tick += (s, e) => AutoTrackTick();
            _autoTimer.Start();
            try { Application.Current.Exit += (s, e) => StopAutoTrack(); } catch { }

            // The notice (amber gear) for what was added before the last exit and not seen yet.
            foreach (var a in (_config?.Apps ?? new List<AppEntry>()).Where(a => a != null && AutoPrograms.AutoNewNames(a).Count > 0))
                _autoNotice.Add(a.Id);
            if (_autoNotice.Count > 0) RebuildTiles();
            AutoTrackTick();
        }

        // ---------- the notice: an amber gear on a rule that got something automatically ----------
        //
        // In memory, per rule: starts when an entry is auto-added (and at start for every rule that
        // still has AutoNew entries); ends ONLY for that rule — when its gear is clicked or its
        // editor closes (saved or cancelled). A new addition starts it again. Its end does NOT clear
        // AutoNew — only the rule's editor closing does. No animation.
        // 2026-11-23: no more "ends after 5 presses anywhere on the main window" — after an update
        // eight tiles lit up at once, and the presses spent on the first few put out the gears of
        // the tiles nobody had looked at yet. Instead it also goes out by itself after the main
        // window has been ON SCREEN for an hour since the last automatic addition to the rule
        // (minimized / in the tray does not count; the count survives a restart) — AutoNew is
        // cleared then, the entries stay. See NoticeClockTick.
        private readonly HashSet<string> _autoNotice = new HashSet<string>(StringComparer.Ordinal);

        private bool AutoNoticeOn(AppEntry entry) => entry != null && _autoNotice.Contains(entry.Id);

        // «Добавлено автоматически: codex.exe, Microsoft.VisualStudio.Code.Server.exe +2».
        private string AutoNoticeTip(AppEntry entry)
        {
            var names = AutoPrograms.AutoNewNames(entry);
            if (names.Count == 0) return Loc.T("Main_TipEditRule");
            const int shown = 3;
            var list = string.Join(", ", names.Take(shown)) + (names.Count > shown ? " +" + (names.Count - shown) : "");
            return Loc.T("Main_TipAutoAdded", list);
        }

        private void EndAutoNotice(string ruleId)
        {
            if (ruleId == null || !_autoNotice.Remove(ruleId)) return;
            // Deferred: the press that ended it may be on the very chip being redrawn.
            Dispatcher.BeginInvoke(new Action(RebuildTiles));
        }

        // A rule's editor closed — saved or cancelled, from the tile or the rules list: its new
        // auto entries have been seen. Only AutoNew is written (a cancelled edit stays discarded).
        internal void AutoNewSeen(string ruleId)
        {
            try
            {
                if (AutoPrograms.ClearAutoNew(_config, ruleId)) AppConfigStore.Save(_config);
            }
            catch (Exception ex) { Log.Error("auto-new-seen", ex); }
            EndAutoNotice(ruleId);
        }

        // ---------- the notice's clock (2026-11-23) ----------
        //
        // Counted on the 5 s tick: the real time since the previous tick (at most 10 s — a sleep /
        // resume or a stalled UI thread adds no jump), only while a person can see the tiles.
        // AutoPrograms.NoticeTick does the arithmetic. The counters live in _config, so any save
        // writes them; besides, a flush every 5 counted minutes, at once on an expiry, and at exit —
        // never while something holds a copy of the rules (tried again on a later tick).
        private const int NoticeFlushSec = 300;
        private DateTime _noticeLastTickUtc = DateTime.MinValue;
        private double _noticeCarry;          // the fraction of a second not counted yet
        private int _noticeUnsavedSec;
        private bool _noticeDirty, _noticeSaveNow;

        // On screen = the tiles can be seen: shown (not hidden to the tray — Hide() on close,
        // StartHiddenInTray at autostart; _hidden says the same from the app's side) and not
        // minimized to the taskbar.
        private bool MainWindowOnScreen()
            => IsVisible && !_hidden && WindowState != WindowState.Minimized;

        private void NoticeClockTick()
        {
            try
            {
                var now = DateTime.UtcNow;
                int add = 0;
                if (_noticeLastTickUtc != DateTime.MinValue && MainWindowOnScreen())
                {
                    var dt = (now - _noticeLastTickUtc).TotalSeconds;
                    if (dt > 0) _noticeCarry += Math.Min(dt, 10);
                    add = (int)_noticeCarry;
                    _noticeCarry -= add;
                }
                _noticeLastTickUtc = now;

                var r = AutoPrograms.NoticeTick(_config, add);
                if (r.Changed) { _noticeDirty = true; _noticeUnsavedSec += add; }
                foreach (var id in r.Expired)
                {
                    var rule = (_config?.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.Id == id);
                    Log.Info("Авто: уведомление плитки «" + (rule?.Name ?? id) + "» погашено — окно было на экране час");
                    EndAutoNotice(id);
                    _noticeSaveNow = true;
                }
                if ((_noticeSaveNow || _noticeUnsavedSec >= NoticeFlushSec) && !AutoTrackHeld()) FlushNoticeClock();
            }
            catch (Exception ex) { Log.Error("auto-notice-clock", ex); }
        }

        private void FlushNoticeClock()
        {
            try
            {
                AppConfigStore.Save(_config);
                _noticeDirty = _noticeSaveNow = false;
                _noticeUnsavedSec = 0;
            }
            catch (Exception ex) { Log.Error("auto-notice-save", ex); }
        }

        private void StopAutoTrack()
        {
            try { if ((_noticeDirty || _noticeSaveNow) && !AutoTrackHeld()) FlushNoticeClock(); } catch { }
            try { _autoTimer?.Stop(); } catch { }
            try { _procTracker?.Stop(); } catch { }
            try { FolderWatch.StopAll(); } catch { }
        }

        // The engine's connections, from the speed poll (once a second): who went online through it.
        private void NoteAutoTrackConnections(List<MihomoController.ConnStat> conns)
        {
            if (conns == null || _procTracker == null || !_procTracker.Running) return;
            var now = DateTime.UtcNow;
            foreach (var c in conns)
                if (!string.IsNullOrEmpty(c?.ProcessPath)) _autoNetPaths[c.ProcessPath] = now;
        }

        // Watchers follow the rules: called at start and after every save of the rules.
        private void RebuildFolderWatch()
        {
            try
            {
                FolderWatch.Rebuild(_config,
                    onExesChanged: () => Dispatcher.BeginInvoke(new Action(SyncFirewallSoon)),
                    onPathsMoved: () => Dispatcher.BeginInvoke(new Action(() => RepairProgramPaths("папка изменилась", force: true))));
            }
            catch (Exception ex) { Log.Error("folder-watch", ex); }
        }

        // What the watchers depend on — rebuilt when it changes, whichever window saved the rules.
        private string _watchSig;

        private static string WatchSignature(AppConfig cfg)
        {
            var sb = new System.Text.StringBuilder();
            // Whether the firewall walls a rule decides whether its auto folders are watched
            // (FolderWatch, 2026-11-23).
            sb.Append(cfg?.FirewallKillSwitch == true ? "W1|" : "W0|");
            foreach (var a in cfg?.Apps ?? new List<AppEntry>())
            {
                if (a == null) continue;
                sb.Append(a.KillSwitch || a.LockNic ? "K1|" : "K0|");
                foreach (var f in a.Folders ?? new List<RuleFolder>()) sb.Append('F').Append(f?.Path).Append('|');
                foreach (var p in a.Programs ?? new List<RuleProgram>()) sb.Append('P').Append(p?.Path).Append('|');
            }
            return sb.ToString();
        }

        private void AutoTrackTick()
        {
            NoticeClockTick();   // first, its own try/catch: no early return below may skip it
            try
            {
                var sig = WatchSignature(_config);
                if (sig != _watchSig) { _watchSig = sig; RebuildFolderWatch(); }

                // «Авто-папка программы» (2026-11-23): the catch-all for every save of the rules that
                // did not sync itself (the rules list, Settings, an import). Not while something holds
                // a copy of the rules, and never closer than a reload gap to the last auto reload.
                if (!AutoTrackHeld() && DateTime.UtcNow - _autoLastReloadUtc >= AutoReloadGap)
                {
                    var fs = SyncAutoFoldersNow();
                    if (fs != null && fs.Changed)
                    {
                        try { AppConfigStore.Save(_config); }
                        catch (Exception ex) { Log.Error("auto-folder-save", ex); }
                        NoticeAutoFolders(fs);
                        ApplyAutoChange("авто-папка программы", AutoFolderCutFiles(fs), DateTime.UtcNow);
                    }
                }

                if (!AutoPrograms.AnyTracking(_config))
                {
                    if (_procTracker != null && _procTracker.Running) _procTracker.Stop();
                    _autoNetPaths.Clear();
                    return;
                }
                if (_procTracker == null) _procTracker = new ProcessTracker();
                if (!_procTracker.Running) { _procTracker.Start(); return; }   // its first picture comes next tick

                var now = DateTime.UtcNow;
                foreach (var k in _autoNetPaths.Where(kv => now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                    _autoNetPaths.Remove(k);

                // Who has a window of their own right now (pid → path from the tracker's picture).
                var snapshot = _procTracker.Snapshot();
                var windowPids = AppWindows.Pids();
                if (windowPids.Count > 0)
                    foreach (var p in snapshot)
                        if (p.Alive && !string.IsNullOrEmpty(p.Path) && windowPids.Contains(p.Pid)) _autoWindowed.Add(p.Path);

                // The standalone-app list is built in the background on first use; until it is there
                // nothing is decided (it is what keeps Word, opened from a messenger, out of its tile).
                if (StandaloneApps.Ready)
                {
                    var own = OwnExeNames();
                    var installRoot = MihomoConfigBuilder.NormalizeFolder(VersionInfo.InstallRoot);

                    var env = new AutoPrograms.Env
                    {
                        IsStandalone = StandaloneApps.Contains,
                        VersionWords = AutoPrograms.VersionWordsOf,
                        NetPaths = new HashSet<string>(_autoNetPaths.Keys, StringComparer.OrdinalIgnoreCase),
                        PendingPaths = new HashSet<string>(_autoPending.Select(p => p.D.Path), StringComparer.OrdinalIgnoreCase),
                        OwnNames = own,
                        WindowedPaths = new HashSet<string>(_autoWindowed, StringComparer.OrdinalIgnoreCase),
                        NowUtc = now,
                    };
                    // Nothing from the launcher's own folder (its engine, service, probes) — ever.
                    var procs = snapshot
                        .Where(p => string.IsNullOrEmpty(p.Path) || installRoot.Length == 0
                                    || !p.Path.StartsWith(installRoot + "\\", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var res = AutoPrograms.Decide(_config, procs, env);
                    foreach (var s in res.Skipped)
                        if (_autoLogged.Add("skip|" + s.Path + "|" + s.Reason))
                            Log.Info("Авто: пропущена " + s.Name + " — " + s.Reason + ". " + s.Path);
                    foreach (var d in res.Add)
                        _autoPending.Add(new AutoPending { D = d, FoundAt = now });
                }
                ApplyAutoPending();
            }
            catch (Exception ex) { Log.Error("auto-track", ex); }
        }

        // Somebody holds a copy of the rules or is about to compare before / after.
        private bool AutoTrackHeld()
        {
            if (_state == UiState.Checking || _reconnectingId != null || _updating) return true;
            if (_liveEditGate.CurrentCount == 0) return true;
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is EditRuleDialog || w is SettingsWindow || w is VpnMatrixWindow) return true;
            }
            catch { }
            return false;
        }

        private void ApplyAutoPending()
        {
            if (_autoPending.Count == 0) return;
            var now = DateTime.UtcNow;
            // Collect for a few seconds: a program starting its helpers starts several at once.
            if (now - _autoPending.Min(p => p.FoundAt) < AutoCollect) return;
            if (now - _autoLastReloadUtc < AutoReloadGap) return;
            if (AutoTrackHeld()) return;

            var batch = _autoPending.ToList();
            _autoPending.Clear();
            var added = new List<string>();
            foreach (var p in batch)
            {
                var d = p.D;
                // Looked up again by id: the rule may have been edited (replaced) or deleted meanwhile.
                var tile = (_config?.Apps ?? new List<AppEntry>()).FirstOrDefault(a => a != null && a.Id == d.Tile?.Id);
                if (tile == null || !tile.AutoTrack) continue;
                // Taken meanwhile? A manual entry claims its NAME, an auto entry only its own FILE.
                if (AutoPrograms.ClaimedBy(_config, d.Path) != null) continue;
                if (AutoPrograms.IsBanned(tile, d.Path)) continue;   // banned meanwhile (the editor's third section)
                // Its own window may have shown up while the batch was collected.
                if (!d.OwnDir && _autoWindowed.Contains(d.Path))
                {
                    if (_autoLogged.Add("skip|" + d.Path + "|window"))
                        Log.Info("Авто: пропущена " + d.Name + " — самостоятельная программа со своим окном, запущена из «" + tile.Name + "» (" + d.Why + "). " + d.Path);
                    continue;
                }
                if ((tile.Programs ?? new List<RuleProgram>()).Count(x => x != null && x.Auto) >= AutoPrograms.MaxAutoPerRule)
                {
                    if (_autoLogged.Add("cap|" + tile.Id))
                        Log.Info("Авто: в «" + tile.Name + "» уже " + AutoPrograms.MaxAutoPerRule + " авто-программ — больше не добавляю.");
                    continue;
                }
                if (tile.Programs == null) tile.Programs = new List<RuleProgram>();
                tile.Programs.Add(new RuleProgram
                {
                    Name = SafeNameNoExt(d.Path),
                    Path = d.Path,
                    Auto = true,
                    AutoFrom = d.AutoFrom ?? "",
                    Launch = false,
                    AutoNew = true,
                });
                added.Add(d.Path);
                _autoNotice.Add(tile.Id);   // the notice, again
                tile.AutoNoticeShownSec = 0;   // and its hour starts over
                Log.Info("Авто: в «" + tile.Name + "» добавлена " + d.Name + " (" + d.Why + "). " + d.Path);
            }
            if (added.Count == 0) return;

            try { AppConfigStore.Save(_config); }
            catch (Exception ex) { Log.Error("auto-track-save", ex); }
            ApplyAutoChange("авто-добавление программ", added, now);
        }

        // What follows a saved automatic change of the rules (auto programs, auto folders): one
        // reload for the whole batch, only if the engine's text really changed; the open
        // connections of `cutFiles` are cut after it, so they redial through their tile.
        private void ApplyAutoChange(string reason, List<string> cutFiles, DateTime now)
        {
            _autoLastReloadUtc = now;
            try { EngineLogStore.Instance.RefreshRules(_config); } catch { }
            _speedMapDirty = true;
            try
            {
                if (_hasChecked && _assignment.Count > 0)
                    ApplyFinalConfig(CurrentAssignedProxies(), reason, cutFiles: cutFiles);
                else
                    _ = ArmKillSwitchAsync(CancellationToken.None, cutFiles);
            }
            catch (Exception ex) { Log.Error("auto-track-apply", ex); }
            SyncFirewallSoon();
            try { UpdateConflictIcon(); } catch { }
            RebuildTiles();   // the amber gear
        }

        // The launcher's and the engine's own exe names: never added anywhere.
        private HashSet<string> OwnExeNames()
        {
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                VersionInfo.ExeName, VersionInfo.LegacyExeName, "mihomo-dnscheck.exe",
            };
            try { var eng = System.IO.Path.GetFileName((_config?.MihomoExePath ?? "").Trim()); if (!string.IsNullOrEmpty(eng)) own.Add(eng); } catch { }
            return own;
        }

        // ---------- «Авто-папка программы» (2026-11-23) ----------
        //
        // AutoPrograms.SyncAutoFolders on the live config, one log line per addition / removal.
        // Called at start, when the tile's rule editor or the new-rule tile saves, and on the tick.
        // Never a window: a failure goes to app.log and changes nothing. Null on failure.
        private AutoPrograms.FolderSync SyncAutoFoldersNow()
        {
            try
            {
                var env = new AutoPrograms.Env
                {
                    VersionWords = AutoPrograms.VersionWordsOf,
                    OwnNames = OwnExeNames(),
                    InstallRoot = VersionInfo.InstallRoot,
                };
                var fs = AutoPrograms.SyncAutoFolders(_config, env);
                foreach (var a in fs.Added)
                    Log.Info("Авто: в «" + a.Tile?.Name + "» добавлена папка программы " + a.Exe + " — " + a.Path);
                foreach (var r in fs.Removed)
                    Log.Info("Авто: из «" + r.Tile?.Name + "» убрана авто-папка " + r.Path + " — " + r.Reason);
                foreach (var d in fs.DroppedPrograms)
                {
                    string exe;
                    try { exe = System.IO.Path.GetFileName(d.Path ?? "") ?? ""; } catch { exe = d.Path ?? ""; }
                    Log.Info("Авто: из «" + d.Tile?.Name + "» убрана строка " + exe + " — входит в авто-папку " + d.Folder);
                }
                return fs;
            }
            catch (Exception ex) { Log.Error("auto-folders", ex); return null; }
        }

        // The amber gear for every tile that got a folder.
        private void NoticeAutoFolders(AutoPrograms.FolderSync fs)
        {
            foreach (var a in fs?.Added ?? new List<AutoPrograms.FolderAdded>())
                if (a?.Tile != null) _autoNotice.Add(a.Tile.Id);
        }

        // The programs already online from an added folder, cut after the reload so they redial
        // through it — never one a tile already claims (the tile's own program is routed already;
        // a file another tile has keeps its connections).
        private List<string> AutoFolderCutFiles(AutoPrograms.FolderSync fs)
        {
            var cut = new List<string>();
            foreach (var a in fs?.Added ?? new List<AutoPrograms.FolderAdded>())
                foreach (var path in _autoNetPaths.Keys)
                    if (MihomoConfigBuilder.InFolder(path, a.Path) && AutoPrograms.ClaimedBy(_config, path) == null
                        && !cut.Contains(path, StringComparer.OrdinalIgnoreCase))
                        cut.Add(path);
            return cut;
        }

        private static string SafeNameNoExt(string path)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(path ?? "") ?? ""; } catch { return ""; }
        }

        // Cut the open engine connections of exactly these program files (an auto entry is routed by
        // its file, so a same-named exe elsewhere keeps its connections), off the UI thread.
        private void CloseProcessConnections(List<string> files)
        {
            if (files == null || files.Count == 0 || _mihomo == null) return;
            var ctl = _mihomo;
            _ = Task.Run(async () =>
            {
                try { await ctl.CloseConnectionsAsync(null, null, files: files).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("auto-track-cut", ex); }
            });
        }
    }
}
