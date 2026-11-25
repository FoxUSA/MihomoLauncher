using System;
using System.Collections.Generic;
using System.Linq;
using MihomoLauncher.Models;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // A tile's chosen card left the system (2026-11-24, the user's rules):
    //  • renamed in Windows (same interface GUID, new name) — every tile follows it, bound or not:
    //    it is the same card, only the name the engine binds to changed;
    //  • gone, tile NOT bound to it — the card is dropped from the tile, which goes on through its
    //    other cards, or through the one Windows uses («по умолчанию») when it had only that one;
    //  • gone, tile bound to it («Привязка к сетевой карте») — left as it is; the plate and the
    //    menu say the card is not there, what became of it (извлечена / удалена) and that the tile
    //    is bound to it.
    public partial class MainWindow
    {
        // Name of a gone card → how many checks in a row (5 s apart) it has been gone. A card is
        // dropped from unbound tiles on the second: one card list read in the middle of a change
        // is not a removal.
        private readonly Dictionary<string, int> _nicGoneSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool _followingCards;

        // Once a second: did a card get renamed, come or go (names / GUIDs / up-down from .NET, a few
        // ms, no WMI)? Then everything is read anew and followed at once — the 5-s stats tick and
        // the card caches made a rename take up to ~10 s (2026-11-24).
        private System.Windows.Threading.DispatcherTimer _cardWatch;
        private string _cardWatchSig;

        private void HookCardWatch()
        {
            if (_cardWatch != null) return;
            _cardWatchSig = NetCards.QuickSig();
            _cardWatch = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromSeconds(1) };
            _cardWatch.Tick += (s, e) =>
            {
                var sig = NetCards.QuickSig();
                if (sig == _cardWatchSig) return;
                _cardWatchSig = sig;
                NetCards.Invalidate();
                _netCardsCache = null;
                _nicIpCardsCheckedAt = DateTime.UtcNow;
                CheckNicStates();   // redraws the chips, follows a renamed card into the tiles and the engine
            };
            _cardWatch.Start();
            Closed += (s, e) => { try { _cardWatch.Stop(); } catch { } };
        }

        private void FollowCardChanges()
        {
            if (_followingCards || _config?.Apps == null) return;
            var present = CurrentNetCards();
            // No card read at all: can't tell a removed card from a failed read — never guess.
            if (!present.Any(c => c.Present)) return;
            bool IsPresent(string n) => present.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));

            // Gone (not renamed) cards of unbound tiles, counted.
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _config.Apps.Where(a => a != null && !a.LockNic))
                foreach (var n in NetCards.Clean(a.Interfaces))
                    if (!IsPresent(n) && NetCards.RenamedTo(a, n, present) == null) gone.Add(n);
            foreach (var k in _nicGoneSeen.Keys.Where(k => !gone.Contains(k)).ToList()) _nicGoneSeen.Remove(k);
            foreach (var n in gone) { int s; _nicGoneSeen.TryGetValue(n, out s); _nicGoneSeen[n] = s + 1; }

            var changed = new List<AppEntry>();
            bool idsChanged = false;
            foreach (var a in _config.Apps.Where(a => a != null))
            {
                var names = NetCards.Clean(a.Interfaces);
                if (names.Count == 0 || names.All(IsPresent))
                {
                    // Every card there: remember their GUIDs (a tile saved before 2026-11-24 has none).
                    if (NetCards.RememberIds(a, present)) idsChanged = true;
                    continue;
                }
                var next = new List<string>();
                var notes = new List<string>();
                foreach (var n in names)
                {
                    if (IsPresent(n)) { next.Add(n); continue; }
                    var renamed = NetCards.RenamedTo(a, n, present);
                    if (renamed != null)
                    {
                        next.Add(renamed);
                        notes.Add("карта «" + n + "» переименована в Windows в «" + renamed + "» — плитка переведена на новое имя");
                        continue;
                    }
                    int seen;
                    if (a.LockNic || !_nicGoneSeen.TryGetValue(n, out seen) || seen < 2) { next.Add(n); continue; }
                    notes.Add("карты «" + n + "» нет в системе (" + NetCards.GoneText(NetCards.Missing(n)) + ") — снята с плитки");
                }
                next = NetCards.Clean(next);
                if (next.SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
                {
                    if (NetCards.RememberIds(a, present)) idsChanged = true;
                    continue;
                }
                a.Interfaces = next;
                NetCards.RememberIds(a, present);   // the new name gets the card's GUID, a dropped one loses it
                changed.Add(a);
                Log.Info("Плитка «" + a.Name + "»: " + string.Join("; ", notes) + "; теперь — "
                         + (next.Count == 0 ? "по умолчанию (выбирает Windows)." : string.Join(" › ", next) + "."));
            }
            if (changed.Count == 0)
            {
                // Only GUIDs learnt: written, nothing to route anew.
                if (idsChanged) try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("nic-ids-save", ex); }
                return;
            }

            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("nic-follow-save", ex); }
            ApplyCardChanges(changed);
        }

        private async void ApplyCardChanges(List<AppEntry> changed)
        {
            _followingCards = true;
            try
            {
                EngineLogStore.Instance.RefreshRules(_config);
                if (_state != UiState.Checking && _reconnectingId == null)
                {
                    if (!_hasChecked)
                    {
                        await ArmKillSwitchAsync(System.Threading.CancellationToken.None).ConfigureAwait(true);
                        CloseConnectionsFor(changed);
                    }
                    else ApplyFinalConfig(CurrentAssignedProxies(), "сетевая карта пропала или переименована", force: true, alsoCut: changed);
                }
                // Mid-connect: that connect writes the final config from _config itself.
            }
            catch (Exception ex) { Log.Error("nic-follow-apply", ex); }
            finally { _followingCards = false; }
            RebuildTiles();
        }
    }
}
