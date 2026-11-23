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
    public partial class MainWindow
    {
        private ProcessTracker _procTracker;
        private DispatcherTimer _autoTimer;
        // Exe paths the engine saw on a connection → when (kept 10 minutes).
        private readonly Dictionary<string, DateTime> _autoNetPaths = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private sealed class AutoPending { public AutoPrograms.Decision D; public DateTime FoundAt; }
        private readonly List<AutoPending> _autoPending = new List<AutoPending>();
        private readonly HashSet<string> _autoLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                _autoNotice[a.Id] = AutoNoticeClicks;
            // Every press on the main window counts toward ending it, whatever it hits.
            AddHandler(PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler((s, e) => CountAutoNoticeClick()), true);
            if (_autoNotice.Count > 0) RebuildTiles();
            AutoTrackTick();
        }

        // ---------- the notice: an amber gear on a rule that got something automatically ----------
        //
        // In memory, per rule: starts when an entry is auto-added (and at start for every rule that
        // still has AutoNew entries); ends when that gear is clicked, or after 5 presses anywhere on
        // the main window, or when the rule's editor closes. A new addition starts it again with a
        // fresh 5. Its end does NOT clear AutoNew — only the rule's editor closing does. No animation.
        private const int AutoNoticeClicks = 5;
        private readonly Dictionary<string, int> _autoNotice = new Dictionary<string, int>(StringComparer.Ordinal);

        private bool AutoNoticeOn(AppEntry entry) => entry != null && _autoNotice.ContainsKey(entry.Id);

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

        private void CountAutoNoticeClick()
        {
            if (_autoNotice.Count == 0) return;
            bool ended = false;
            foreach (var id in _autoNotice.Keys.ToList())
            {
                if (--_autoNotice[id] > 0) continue;
                _autoNotice.Remove(id);
                ended = true;
            }
            if (ended) Dispatcher.BeginInvoke(new Action(RebuildTiles));
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

        private void StopAutoTrack()
        {
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
            foreach (var a in cfg?.Apps ?? new List<AppEntry>())
            {
                if (a == null) continue;
                foreach (var f in a.Folders ?? new List<RuleFolder>()) sb.Append('F').Append(f?.Path).Append('|');
                foreach (var p in a.Programs ?? new List<RuleProgram>()) sb.Append('P').Append(p?.Path).Append('|');
            }
            return sb.ToString();
        }

        private void AutoTrackTick()
        {
            try
            {
                var sig = WatchSignature(_config);
                if (sig != _watchSig) { _watchSig = sig; RebuildFolderWatch(); }

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

                // The standalone-app list is built in the background on first use; until it is there
                // nothing is decided (it is what keeps Word, opened from a messenger, out of its tile).
                if (StandaloneApps.Ready)
                {
                    var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        VersionInfo.ExeName, VersionInfo.LegacyExeName, "mihomo-dnscheck.exe",
                    };
                    try { var eng = System.IO.Path.GetFileName((_config?.MihomoExePath ?? "").Trim()); if (!string.IsNullOrEmpty(eng)) own.Add(eng); } catch { }
                    var installRoot = MihomoConfigBuilder.NormalizeFolder(VersionInfo.InstallRoot);

                    var env = new AutoPrograms.Env
                    {
                        IsStandalone = StandaloneApps.Contains,
                        VersionWords = AutoPrograms.VersionWordsOf,
                        NetPaths = new HashSet<string>(_autoNetPaths.Keys, StringComparer.OrdinalIgnoreCase),
                        PendingPaths = new HashSet<string>(_autoPending.Select(p => p.D.Path), StringComparer.OrdinalIgnoreCase),
                        OwnNames = own,
                    };
                    // Nothing from the launcher's own folder (its engine, service, probes) — ever.
                    var procs = _procTracker.Snapshot()
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
                _autoNotice[tile.Id] = AutoNoticeClicks;   // a fresh notice, a fresh 5
                Log.Info("Авто: в «" + tile.Name + "» добавлена " + d.Name + " (" + d.Why + "). " + d.Path);
            }
            if (added.Count == 0) return;

            try { AppConfigStore.Save(_config); }
            catch (Exception ex) { Log.Error("auto-track-save", ex); }
            _autoLastReloadUtc = now;
            try { EngineLogStore.Instance.RefreshRules(_config); } catch { }
            _speedMapDirty = true;
            // One reload for the whole batch, only if the engine's text really changed; the
            // helpers' open connections are cut after it, so they redial through their tile.
            try
            {
                if (_hasChecked && _assignment.Count > 0)
                    ApplyFinalConfig(CurrentAssignedProxies(), "авто-добавление программ", cutFiles: added);
                else
                    _ = ArmKillSwitchAsync(CancellationToken.None, added);
            }
            catch (Exception ex) { Log.Error("auto-track-apply", ex); }
            SyncFirewallSoon();
            try { UpdateConflictIcon(); } catch { }
            RebuildTiles();   // the amber gear
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
