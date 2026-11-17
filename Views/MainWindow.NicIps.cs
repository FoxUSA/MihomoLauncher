using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // The stats plate's network-card line (2026-11-14): every connected card with the public IP
    // the internet sees through it and that IP's city/country — «Ethernet 93.180.133.144 Стамбул,
    // Турция · Wi-Fi 2 91.197.105.120 Сочи, Россия». Shown only while the card plates on the tiles
    // are (Settings → Сеть), since it is what tells which card is which.
    //
    // Asked of api.byfox.dev through each card itself (NicIpLookup); the place name comes from
    // our own GeoLite2 base in the UI language, the API's English as the fallback.
    // Re-asked: at start, a few seconds after Windows reports an address change, every 10 min;
    // a card that failed — after a minute.
    public partial class MainWindow
    {
        private const int NicIpRefreshMinutes = 10;
        private const int NicIpRetrySeconds = 60;

        private readonly Dictionary<string, NicIpLookup.Result> _nicIp =
            new Dictionary<string, NicIpLookup.Result>(StringComparer.OrdinalIgnoreCase);
        private List<NetCard> _nicIpCards = new List<NetCard>();
        private DateTime _nicIpDue = DateTime.MinValue;
        private bool _nicIpBusy;
        private bool _nicIpHooked;
        private bool _nicIpChangedWhileBusy;

        private void HookNicIpChanges()
        {
            if (_nicIpHooked) return;
            _nicIpHooked = true;
            NetworkChange.NetworkAddressChanged += (s, e) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _nicIpDue = DateTime.UtcNow.AddSeconds(3);   // let DHCP settle
                        // Mid-lookup the finally below would overwrite that due time with
                        // +10 min, and a card that got its address meanwhile stayed off the line
                        // (TX20E switching Wi-Fi networks, 2026-11-16) — so remember it.
                        if (_nicIpBusy) _nicIpChangedWhileBusy = true;
                    }));
                }
                catch { }
            };
        }

        // Cheap: called on every stats tick; does the network work only when due.
        private void MaybeRefreshNicIps()
        {
            HookNicIpChanges();
            if (!ShowNicPlate || _nicIpBusy) return;
            // Safety net for an address event Windows never sent: every 5 s compare which cards are
            // up with an address against the line — a card that came or went is asked about now.
            if (DateTime.UtcNow >= _nicIpCardsCheckedAt.AddSeconds(5))
            {
                _nicIpCardsCheckedAt = DateTime.UtcNow;
                try
                {
                    var now = NetCards.List().Where(c => c.Up && NicIpLookup.CardAddress(c.Name) != null).Select(c => c.Name);
                    if (!now.SequenceEqual(_nicIpCards.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
                        _nicIpDue = DateTime.MinValue;
                }
                catch { }
            }
            if (DateTime.UtcNow < _nicIpDue) return;
            RefreshNicIps();
        }

        private DateTime _nicIpCardsCheckedAt = DateTime.MinValue;

        private async void RefreshNicIps()
        {
            _nicIpBusy = true;
            bool anyFailed = false;
            try
            {
                var cards = NetCards.List().Where(c => c.Up && NicIpLookup.CardAddress(c.Name) != null).ToList();
                _nicIpCards = cards;
                foreach (var gone in _nicIp.Keys.Where(k => !cards.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
                    _nicIp.Remove(gone);
                RenderStats();
                if (cards.Count == 0) return;

                var servers = await NicIpLookup.ServerAddressesAsync(_mihomo, CancellationToken.None).ConfigureAwait(true);
                var tasks = cards.Select(c => NicIpLookup.LookupAsync(c.Name, servers, _mihomo, CancellationToken.None)).ToList();
                await System.Threading.Tasks.Task.WhenAll(tasks).ConfigureAwait(true);
                foreach (var t in tasks)
                {
                    var r = t.Result;
                    NicIpLookup.Result old;
                    _nicIp.TryGetValue(r.Card, out old);
                    _nicIp[r.Card] = r;
                    if (!r.Ok) anyFailed = true;
                    if (old == null || old.Ok != r.Ok || old.Ip != r.Ip)
                        Log.Info("Сетевая карта «" + r.Card + "» (" + r.LocalIp + "): "
                                 + (r.Ok ? "внешний адрес " + r.Ip + " · " + PlaceOf(r) + (r.Org.Length > 0 ? " · " + r.Org : "")
                                         : "внешний адрес не определён — " + r.Error));
                }
            }
            catch (Exception ex) { anyFailed = true; Log.Error("nic-ip", ex); }
            finally
            {
                _nicIpBusy = false;
                _nicIpDue = _nicIpChangedWhileBusy ? DateTime.UtcNow.AddSeconds(3)
                          : anyFailed ? DateTime.UtcNow.AddSeconds(NicIpRetrySeconds)
                          : DateTime.UtcNow.AddMinutes(NicIpRefreshMinutes);
                _nicIpChangedWhileBusy = false;
                RenderStats();
            }
        }

        // "Сочи, Россия" — the local base in the UI language first, then the API's English.
        private static string PlaceOf(NicIpLookup.Result r)
        {
            string city = "", country = "";
            try
            {
                var g = GeoIpService.Lookup(r.Ip);
                if (g != null) { city = g.City ?? ""; country = g.Country ?? ""; }
            }
            catch { }
            if (city.Length == 0) city = r.City ?? "";
            if (country.Length == 0) country = r.Country ?? "";
            if (city.Length > 0 && country.Length > 0) return city + ", " + country;
            return city.Length > 0 ? city : country;
        }

        private bool ShowNicIps => ShowNicPlate && _nicIpCards.Count > 0;

        private string NicIpSig()
        {
            if (!ShowNicIps) return "";
            return string.Join("|", _nicIpCards.Select(c =>
            {
                NicIpLookup.Result r;
                return c.Name + "=" + (_nicIp.TryGetValue(c.Name, out r) ? (r.Ok ? r.Ip : "x") : "?");
            }));
        }

        private void RenderNicIps()
        {
            StatsNics.Children.Clear();
            if (!ShowNicIps) return;

            StatsNics.Children.Add(new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Globe24,
                FontSize = 14,
                Foreground = Theme.TextSecondary,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            for (int i = 0; i < _nicIpCards.Count; i++)
                StatsNics.Children.Add(NicIpChip(_nicIpCards[i], i < _nicIpCards.Count - 1));
        }

        // "Wi-Fi 2 91.197.105.120 Сочи, Россия" — the address bright, the place quieter.
        private UIElement NicIpChip(NetCard card, bool separator)
        {
            NicIpLookup.Result r;
            bool measured = _nicIp.TryGetValue(card.Name, out r) && r != null;

            var tb = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            tb.Inlines.Add(new Run(ShortCardName(card) + " ") { Foreground = Theme.TextSecondary });
            if (!measured)
                tb.Inlines.Add(new Run(Loc.T("Main_StatsMeasuring")) { Foreground = Theme.TextMuted });
            else if (!r.Ok)
                tb.Inlines.Add(new Run(Loc.T("Main_NicIpNone")) { Foreground = Theme.AccentRed });
            else
            {
                tb.Inlines.Add(new Run(r.Ip) { Foreground = Theme.TextPrimary, FontWeight = FontWeights.SemiBold });
                var place = PlaceOf(r);
                if (place.Length > 0) tb.Inlines.Add(new Run(" " + place) { Foreground = Theme.TextSecondary });
            }
            if (separator) tb.Inlines.Add(new Run("   ·   ") { Foreground = Theme.TextMuted });

            var tip = card.Name;
            if (measured && r.LocalIp.Length > 0) tip += "\n" + Loc.T("Main_NicIpLocal", r.LocalIp);
            if (measured && r.Ok && r.Org.Length > 0) tip += "\n" + Loc.T("Main_NicIpOrg", r.Org);
            if (measured && !r.Ok) tip += "\n" + Loc.T("Main_NicIpNoneTip");
            tip += "\n\n" + Loc.T("Main_NicIpTip");
            tb.ToolTip = tip;
            return tb;
        }
    }
}
