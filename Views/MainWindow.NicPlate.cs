using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // The lowest strip of a tile: which network cards its traffic leaves through (2026-11-14).
    // A drop-down of the machine's cards with numbered boxes — 1 is the main card, 2 the backup,
    // and so on — plus «По умолчанию», which clears the choice and leaves it to Windows. Applied
    // always and hard: on or off, VPN or direct (MihomoConfigBuilder.NicPlan does the routing).
    public partial class MainWindow
    {
        // As tall as the VPN plate above it (asked 2026-11-14: the first 20 px version was too small to read).
        internal const double NicPlateHeight = 32;

        // The card chip is always there (2026-11-18: «всегда показываем значок сети, даже если в
        // системе вообще 1 карта») — it was «two cards or more», and a card switched off and on
        // again left the tiles without it.
        private bool ShowNicPlate => true;

        // The chip's colour by the card's state (Theme.Nic*).
        private static Brush NicStateBrush(NetCard c)
        {
            if (c == null) return Theme.NicOff;
            switch (c.State)
            {
                case NicState.Connected: return c.Wireless ? Theme.NicWifiOk : Theme.NicWiredOk;
                case NicState.Disabled:
                case NicState.Absent: return Theme.NicOff;
                default: return Theme.NicTrouble;
            }
        }

        // What the card chips show, as one string: a change re-draws the tiles (MaybeRefreshNicIps).
        private string NicStatesSig()
        {
            _netCardsCache = null;
            return string.Join("|", CurrentNetCards().Select(c => c.Name + "=" + c.State)) + "#" + _defaultCardName + "#" + _otherWayOut;
        }
        // 2026-11-16, a try-out: the card choice as an icon under the gear (Wi-Fi / cable, coloured
        // like the plate was) instead of the plate. false = the plate again.
        internal const bool NicAsIcon = true;
        // The same gap between every pair of plates (VPN · card · speed), and under the last one.
        internal const double PlateGap = 5;
        private double NicStrip => ShowNicPlate && !NicAsIcon ? NicPlateHeight + PlateGap : 0;   // + the gap above it

        // The card chip under the gear: Wi-Fi or cable — the tile's first card, else the one Windows
        // uses. Grey = Windows decides; green = its own card works; amber = on its backup; red = none
        // of its cards works. A click opens the same picker the plate had.
        private FrameworkElement MakeNicChip(AppEntry entry, bool reconnecting)
        {
            var cards = NetCards.Chosen(entry, CurrentNetCards());
            // A pinned card is the tile's own even when Windows uses it too: never shown as «по умолчанию».
            if (!entry.LockNic && IsJustDefaultCard(cards.Select(c => c.Name))) cards = new List<NetCard>();
            bool clickable = !reconnecting && _reconnectingId == null;

            NetCard shown;
            Brush color;
            string tip;
            // Coloured by the shown card's state (2026-11-18, the user's table): Wi-Fi blue / cable
            // green when it works, amber with no cable / network / internet, grey switched off.
            if (cards.Count == 0)
            {
                // Windows decides: the card it uses; with none working — the first card there is
                // (cable first), so the chip still says what is wrong.
                shown = DefaultCard() ?? CurrentNetCards().FirstOrDefault();
                tip = (shown != null ? shown.Name + " — " + NetCards.StateText(shown) + " · " + Loc.T("Main_NicDefaultShort")
                                     : Loc.T("Main_NicNone"))
                    + "\n\n" + Loc.T("Main_TipPickNic") + "\n" + Loc.T("Main_NicDefaultRow");
            }
            else
            {
                bool firstOk = cards[0].State == NicState.Connected;
                bool anyOk = cards.Any(c => c.State == NicState.Connected);
                // The card the traffic really leaves through: the first that works.
                shown = cards.FirstOrDefault(c => c.State == NicState.Connected) ?? cards[0];
                tip = string.Join("\n", cards.Select((c, i) => (i + 1) + ". " + c.Name + " — " + NicStateText(c)))
                    + "\n\n" + Loc.T("Main_TipPickNic");
                if (!anyOk) tip += "\n\n" + Loc.T("Main_TipNicDead");
                else if (!firstOk) tip += "\n\n" + Loc.T("Main_TipNicBackup");
                if (entry.LockNic) tip = string.Join("\n", cards.Select(c => c.Name + " — " + NicStateText(c)))
                                         + "\n\n" + Loc.T("Main_NicLockedTip");
            }
            color = NicStateBrush(shown);
            bool wifi = shown != null && shown.Wireless;

            Action<Brush> paint;
            var glyph = NicGlyph(wifi, out paint);
            FrameworkElement chip = null;
            chip = MakeCornerChip(glyph, paint,
                new Thickness(5, 31, 0, 0), HorizontalAlignment.Left, tip, !clickable,
                async () =>
                {
                    // Pinned in the rule's settings (2026-11-23): the menu still opens — its card rows
                    // are inactive, but «Приоритет сетевых адаптеров» must stay reachable. Nothing in it
                    // changes the tile then, so no password here (the priority dialog asks on «Применить»).
                    // Which card a tile goes out through is a routing change — behind the access password.
                    if (!entry.LockNic && !await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                    ShowNicPicker(entry, chip);
                },
                color, entry.LockNic);
            return chip;
        }

        // The chip's picture, drawn (picked on the 2026-11-16 mock-up): a bold Wi-Fi fan, or «LAN»
        // in a rounded frame for a cable (no font icon read as «провод»). 24×24 design, scaled.
        private static FrameworkElement NicGlyph(bool wifi, out Action<Brush> paint)
        {
            if (wifi)
            {
                var arcs = new System.Windows.Shapes.Path
                {
                    // Half a unit higher than first drawn (2026-11-20): the fan + dot span 4.6…20.5,
                    // so the ink sat below the 24-box centre; now it is centred in the chip.
                    Data = Geometry.Parse("M2.5,9 A14,14 0 0 1 21.5,9 M5.5,12.3 A9.5,9.5 0 0 1 18.5,12.3 M8.6,15.5 A5,5 0 0 1 15.4,15.5"),
                    StrokeThickness = 2.3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                var dot = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3 };
                Canvas.SetLeft(dot, 10.5);
                Canvas.SetTop(dot, 17);
                var c = new Canvas { Width = 24, Height = 24 };
                c.Children.Add(arcs);
                c.Children.Add(dot);
                paint = b => { arcs.Stroke = b; dot.Fill = b; };
                return new Viewbox { Width = 14, Height = 14, Child = c };
            }
            // Just the word, no frame of its own (2026-11-16) — the chip is the frame.
            var text = new TextBlock
            {
                Text = "LAN",
                FontSize = 7,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // No nudge (2026-11-20): capitals in Segoe UI already sit at the box centre; the
                // 1 px bottom margin lifted «LAN» half a pixel above it.
                IsHitTestVisible = false,
            };
            paint = b => text.Foreground = b;
            return text;
        }

        // The card list is read once per tile redraw, not once per tile.
        private List<NetCard> _netCardsCache;
        private DateTime _netCardsAt;

        // The card Windows itself sends traffic through right now — a working card with a gateway
        // and the lowest gateway + interface metric (NicMetrics). Read with the card list.
        private string _defaultCardName;

        private List<NetCard> CurrentNetCards()
        {
            if (_netCardsCache == null || (DateTime.UtcNow - _netCardsAt).TotalSeconds > 2)
            {
                _netCardsCache = NetCards.List();
                _netCardsAt = DateTime.UtcNow;
                try
                {
                    var metrics = NicMetrics.Read();
                    _defaultCardName = _netCardsCache
                        .Where(c => c.Up && c.HasGateway)
                        .OrderBy(c => { NicMetrics.Current m; return metrics.TryGetValue(c.Name, out m) && m.Has4 ? m.Total4 : long.MaxValue; })
                        .Select(c => c.Name).FirstOrDefault();
                }
                catch { _defaultCardName = null; }
                // Asked only when no listed card works: that is the one case it decides.
                _otherWayOut = !_netCardsCache.Any(NetCards.Usable) && NetCards.OtherWayOut();
            }
            return _netCardsCache;
        }

        // No Wi-Fi / cable card works, yet Windows has another way out (a modem, PPPoE…).
        private bool _otherWayOut;

        // The tile's traffic has no card to leave through (2026-11-19): none of its own cards is
        // there, switched on and connected — the choice is hard, it never moves to another card —
        // or, with no cards of its own, Windows has no working card at all. «Нет интернета» is not
        // this: that card works, and the check behind it can be wrong. `why` — the card to name
        // (null when there are several and none is the tile's own); `tip` — every card's state.
        private bool TileHasNoNetwork(AppEntry entry, out NetCard why, out string tip)
        {
            why = null;
            tip = null;
            var all = CurrentNetCards();
            var own = NetCards.Chosen(entry, all);
            List<NetCard> shown;
            if (own.Count > 0)
            {
                if (own.Any(NetCards.Usable)) return false;
                why = own[0];
                shown = own;
            }
            else
            {
                if (all.Any(NetCards.Usable) || _otherWayOut) return false;
                if (all.Count == 1) why = all[0];
                shown = all;
            }
            tip = Loc.T("Main_TileNoNet")
                + (shown.Count > 0 ? "\n" + string.Join("\n", shown.Select(c => c.Name + " — " + NicStateText(c))) : "\n" + Loc.T("Main_NicNone"))
                + (entry.LockNic ? "\n\n" + Loc.T("Main_NicLockedTip") : "");
            return true;
        }

        // Why that card carries nothing, for the plate's second line — longest variant first.
        private static string[] NoNetWhy(NetCard c)
        {
            if (c == null) return null;
            switch (c.State)
            {
                case NicState.Absent:
                    return new[] { Loc.T("Main_TileNoCard", c.Name), Loc.T("Main_TileNoCard", ShortCardName(c)), Loc.T("Main_TileNoCardShort") };
                case NicState.Disabled:
                    return new[] { Loc.T("Main_TileCardOff", c.Name), Loc.T("Main_TileCardOffShort"), Loc.T("Main_TileCardOffTiny") };
                case NicState.NoCable:
                    return new[] { Loc.T("Main_TileNoCable", c.Name), Loc.T("Main_TileNoCableShort") };
                default:
                    // A card Windows itself calls «Wi-Fi» would read «нет Wi-Fi Wi-Fi».
                    bool plain = c.Name.IndexOf("wi-fi", StringComparison.OrdinalIgnoreCase) >= 0
                              || c.Name.IndexOf("wifi", StringComparison.OrdinalIgnoreCase) >= 0;
                    return plain ? new[] { Loc.T("Main_TileNoWifiShort") }
                                 : new[] { Loc.T("Main_TileNoWifi", c.Name), Loc.T("Main_TileNoWifiShort") };
            }
        }

        private bool IsJustDefaultCard(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).ToList();
            return list.Count == 1 && !string.IsNullOrEmpty(_defaultCardName)
                   && string.Equals(list[0], _defaultCardName, StringComparison.OrdinalIgnoreCase);
        }

        private NetCard DefaultCard()
        {
            var cards = CurrentNetCards();
            return cards.FirstOrDefault(c => string.Equals(c.Name, _defaultCardName, StringComparison.OrdinalIgnoreCase));
        }

        // «Беспроводная сеть 2» → «Wi-Fi 2», «Ethernet 3» stays: the fallback when the system's own
        // name does not fit the plate.
        private static string ShortCardName(NetCard c)
        {
            var name = c?.Name ?? "";
            var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            var kind = Loc.T(c != null && c.Wireless ? "Main_NicWifi" : "Main_NicEthernet");
            return digits.Length > 0 ? kind + " " + digits : kind;
        }

        private static string NicStateText(NetCard c)
        {
            if (!c.Present) return Loc.T("Main_NicAbsent");
            var kind = Loc.T(c.Wireless ? "Main_NicWifi" : "Main_NicWired");
            return kind + " · " + NetCards.StateText(c);
        }

        private FrameworkElement MakeNicPlate(AppEntry entry, bool reconnecting)
        {
            var cards = NetCards.Chosen(entry, CurrentNetCards());
            // Only the card Windows uses anyway, nothing after it = the default choice: grey plate,
            // no colour (2026-11-14). ShowNicPicker stores it as «nothing ticked» on close.
            if (IsJustDefaultCard(cards.Select(c => c.Name))) cards = new List<NetCard>();
            bool clickable = !reconnecting && _reconnectingId == null;

            // Two lines, no icon — the same layout as the VPN plate above it (2026-11-14: one line
            // with an icon cut «сеть: по у…»). Each line lists variants longest first; FitPlateText
            // takes the first that fits.
            // Colours (2026-11-14): «по умолчанию» grey; a chosen card looks exactly like a connected
            // VPN plate — green plate, green top line, grey bottom line; amber on the backup card,
            // red when none of the chosen cards works.
            string[] line1, line2;
            Brush fg;
            PlateTint tint;
            string tip;
            if (cards.Count == 0)
            {
                // No card of its own: the one Windows uses now, by name, marked «по умолчанию».
                var def = DefaultCard();
                line1 = def != null ? new[] { def.Name, ShortCardName(def) } : new[] { Loc.T("Main_NicDefaultShort") };
                line2 = def != null ? new[] { Loc.T("Main_NicDefaultShort") }
                                    : new[] { Loc.T("Main_NicPlateCaption"), Loc.T("Main_NicPlateCaptionShort") };
                fg = Theme.TextSecondary;
                tint = PlateTint.Neutral;
                tip = Loc.T("Main_TipPickNic") + "\n" + Loc.T("Main_NicDefaultRow");
            }
            else
            {
                bool firstOk = cards[0].Present && cards[0].Up;
                bool anyOk = cards.Any(c => c.Present && c.Up);
                fg = !anyOk ? Theme.AccentRed : !firstOk ? Theme.AccentAmber : Theme.AccentGreen;
                tint = !anyOk ? PlateTint.Red : !firstOk ? PlateTint.Amber : PlateTint.Green;
                line1 = new[] { cards[0].Name, ShortCardName(cards[0]) };
                line2 = cards.Count > 1
                    ? new[] { Loc.T("Main_NicBackupOf", cards[1].Name), Loc.T("Main_NicBackupOf", ShortCardName(cards[1])), "+" + (cards.Count - 1) }
                    : new[] { NicStateText(cards[0]), Loc.T(cards[0].Wireless ? "Main_NicWifi" : "Main_NicWired") };
                tip = Loc.T("Main_TipPickNic") + "\n"
                    + string.Join("\n", cards.Select((c, i) => (i + 1) + ". " + c.Name + " — " + NicStateText(c)));
                if (!anyOk) tip += "\n\n" + Loc.T("Main_TipNicDead");
                else if (!firstOk) tip += "\n\n" + Loc.T("Main_TipNicBackup");
            }

            // 100 px of plate, minus its border, padding and the chevron.
            double room = clickable ? 100 - 2 - 9 - 4 - 12 : 100 - 2 - 4 - 4;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = FitPlateText(room, 10, line1),
                FontSize = 10,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = FitPlateText(room, 9.5, line2),
                FontSize = 9.5,
                Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            content.Children.Add(text);
            if (clickable)
            {
                var chev = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
                    FontSize = 10,
                    Foreground = Theme.AccentGrey,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(chev, 1);
                content.Children.Add(chev);
            }

            Brush bg, bd, bgHover, bdHover;
            switch (tint)
            {
                case PlateTint.Green: bg = Theme.PlateGreenBg; bd = Theme.PlateGreenBorder; bgHover = Theme.PlateGreenBgHover; bdHover = Theme.PlateGreenBorderHover; break;
                case PlateTint.Amber: bg = Theme.PlateAmberBg; bd = Theme.PlateAmberBorder; bgHover = Theme.PlateAmberBgHover; bdHover = Theme.PlateAmberBorderHover; break;
                case PlateTint.Red: bg = Theme.PlateRedBg; bd = Theme.PlateRedBorder; bgHover = Theme.PlateRedBgHover; bdHover = Theme.PlateRedBorderHover; break;
                default: bg = Theme.PlateNeutralBg; bd = Theme.PlateNeutralBorder; bgHover = Theme.ChipBgHover; bdHover = Theme.TileBorderHover; break;
            }

            var plate = new Border
            {
                Height = NicPlateHeight,
                // The speed strip (MakeSpeedPlate) takes the bottom under it.
                Margin = new Thickness(5, 0, 5, PlateGap + SpeedStrip),
                Padding = new Thickness(clickable ? 9 : 4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(7),
                Background = bg,
                BorderBrush = bd,
                BorderThickness = new Thickness(1),
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
                ToolTip = tip,
                Child = content,
            };
            plate.MouseLeftButtonUp += async (s, e) =>
            {
                e.Handled = true;   // never the tile's launch
                if (!clickable) return;
                // Pinned: the menu opens with its card rows inactive, without the password (see MakeNicChip).
                // Which card a tile goes out through is a routing change — behind the access password.
                if (!entry.LockNic && !await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                ShowNicPicker(entry, plate);
            };
            if (clickable)
            {
                plate.MouseEnter += (s, e) => { plate.Background = bgHover; plate.BorderBrush = bdHover; };
                plate.MouseLeave += (s, e) => { plate.Background = bg; plate.BorderBrush = bd; };
            }
            return plate;
        }

        // The drop-down: stays open while cards are ticked (each tick takes the next number, an
        // untick renumbers the rest), applied once when it closes — one reload, not one per click.
        private void ShowNicPicker(AppEntry entry, FrameworkElement anchor)
        {
            if (_vpnPicker != null) _vpnPicker.IsOpen = false;
            HookPickerDismiss();

            var original = NetCards.Clean(entry.Interfaces);
            var order = new List<string>(original);
            _netCardsCache = null;   // fresh state for the list
            var present = CurrentNetCards();
            // Chosen cards that are gone from the system stay in the list (unticking them is how
            // they are dropped), after the present ones.
            var all = new List<NetCard>(present);
            foreach (var c in NetCards.Chosen(entry, present).Where(c => !c.Present)) all.Add(c);

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                StaysOpen = true,   // closed by HookPickerDismiss, like the VPN picker
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };

            var list = new StackPanel { Margin = new Thickness(4) };

            // «Привязка к сетевой карте» is on (2026-11-23): the card rows are shown as they are, but
            // inactive — the card is changed only in the tile's settings. The rest of the menu works.
            bool locked = entry.LockNic;

            Border Row(UIElement inner, bool current, Action onClick, bool active = true)
            {
                var cell = new Border
                {
                    MinHeight = 32,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(4),
                    Background = current ? Theme.PickerRowCurrent : Brushes.Transparent,
                    Cursor = active ? Cursors.Hand : Cursors.Arrow,
                    Child = inner,
                };
                if (!active)
                {
                    // The app's disabled look: dimmed, no hover, no click.
                    cell.Opacity = 0.5;
                    return cell;
                }
                var rest = cell.Background;
                cell.MouseEnter += (s, e) => cell.Background = Theme.PickerRowHover;
                cell.MouseLeave += (s, e) => cell.Background = rest;
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
                return cell;
            }

            void Render()
            {
                list.Children.Clear();
                // No hint paragraph on top any more (2026-11-16: «убери это описание») — the box and
                // the row say what they do in their tooltips, and the box lights up on its own.

                // No «По умолчанию» row any more (2026-11-14): untick every card and Windows decides;
                // the card it decides for is marked «по умолчанию» in the list.
                // Pinned: one muted line on top says why the cards below don't react.
                if (locked)
                    list.Children.Add(new TextBlock
                    {
                        Text = Loc.T("Main_NicPickerLocked"),
                        FontSize = 11.5,
                        Foreground = Theme.TextMuted,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300,
                        Margin = new Thickness(10, 6, 10, 4),
                    });
                if (all.Count == 0)
                    list.Children.Add(new TextBlock
                    {
                        Text = Loc.T("Main_NicNone"),
                        FontSize = 12.5,
                        Foreground = Theme.TextMuted,
                        Margin = new Thickness(10, 6, 10, 6),
                    });

                foreach (var card in all)
                {
                    int rank = order.FindIndex(n => string.Equals(n, card.Name, StringComparison.OrdinalIgnoreCase)) + 1;
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var box = new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(5),
                        BorderThickness = new Thickness(1),
                        Background = rank > 0 ? Theme.AccentBlue : Brushes.Transparent,
                        BorderBrush = rank > 0 ? Theme.AccentBlue : Theme.PickerBorder,
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = rank > 0 ? rank.ToString() : "",
                            FontSize = 12.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = Theme.TextOnColor,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                    g.Children.Add(box);
                    var name = card.Name;
                    // The box ticks / unticks (1, 2… order) and keeps the menu open; a click anywhere
                    // else on the row picks just this card and closes the menu (2026-11-14).
                    // Pinned: the box only shows the choice — no tooltip, hover or tick.
                    if (!locked)
                    {
                        box.Cursor = Cursors.Hand;
                        box.ToolTip = Loc.T("Main_NicBoxTip");
                        // Under the mouse the box lights up by itself (blue frame + tint), so it reads as
                        // its own action — «отметить по порядку» — not as the row's quick pick.
                        var boxBg = box.Background;
                        var boxBd = box.BorderBrush;
                        var blue = Theme.AccentBlue.Color;
                        box.MouseEnter += (s, e) =>
                        {
                            box.BorderBrush = Theme.AccentBlue;
                            box.BorderThickness = new Thickness(2);
                            box.Background = new SolidColorBrush(Color.FromArgb(rank > 0 ? (byte)0xCC : (byte)0x40, blue.R, blue.G, blue.B));
                        };
                        box.MouseLeave += (s, e) =>
                        {
                            box.BorderBrush = boxBd;
                            box.BorderThickness = new Thickness(1);
                            box.Background = boxBg;
                        };
                        box.MouseLeftButtonUp += (s, e) =>
                        {
                            e.Handled = true;
                            int at = order.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                            if (at >= 0) order.RemoveAt(at);
                            else order.Add(name);
                            Render();
                        };
                    }
                    var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var title = new StackPanel { Orientation = Orientation.Horizontal };
                    title.Children.Add(new TextBlock
                    {
                        Text = card.Name,
                        FontSize = 13,
                        FontWeight = rank > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = Theme.TextPrimary,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    // The card Windows uses when a tile has none of its own.
                    if (string.Equals(card.Name, _defaultCardName, StringComparison.OrdinalIgnoreCase))
                        title.Children.Add(new Border
                        {
                            Background = Theme.BadgeNeutralBg,
                            BorderBrush = Theme.BadgeNeutralBorder,
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 0, 6, 1),
                            Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = Loc.T("Main_NicDefaultShort"),
                                FontSize = 10.5,
                                Foreground = Theme.BadgeNeutralText,
                            },
                        });
                    names.Children.Add(title);
                    // The state in the chip's colours (connected — muted, as before).
                    var st = card.State;
                    names.Children.Add(new TextBlock
                    {
                        Text = NicStateText(card) + (string.IsNullOrEmpty(card.Description) ? "" : " · " + card.Description),
                        FontSize = 11,
                        Foreground = st == NicState.Connected ? Theme.TextMuted
                                   : st == NicState.Disabled || st == NicState.Absent ? Theme.NicOff : Theme.NicTrouble,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 300,
                    });
                    Grid.SetColumn(names, 1);
                    g.Children.Add(names);

                    var rowCell = Row(g, false, () =>
                    {
                        order.Clear();
                        order.Add(name);
                        popup.IsOpen = false;
                    }, !locked);
                    if (!locked) names.ToolTip = Loc.T("Main_NicRowTip");   // the name part only: the box has its own
                    list.Children.Add(rowCell);
                }

                // Last, under a line (2026-11-23): Windows' own card priority — the dialog that was
                // Settings' «Сеть» tab. The menu closes first (its card choice is applied as on any
                // close), then the dialog opens over the main window.
                list.Children.Add(new Border { Height = 1, Background = Theme.PickerBorder, Margin = new Thickness(6, 4, 6, 4) });
                list.Children.Add(Row(new TextBlock
                {
                    Text = Loc.T("Nic_DlgMenu"),
                    FontSize = 13,
                    Foreground = Theme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                }, false, () =>
                {
                    popup.IsOpen = false;
                    Dispatcher.BeginInvoke(new Action(OpenNicPriority), System.Windows.Threading.DispatcherPriority.Background);
                }));
            }
            Render();

            var shell = new Border
            {
                MinWidth = 260,
                Background = Theme.OverlayCardBg,
                BorderBrush = Theme.PickerBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 12, 14),   // room for the shadow inside the transparent popup
                Focusable = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Theme.ShadowColor, BlurRadius = 14, ShadowDepth = 4, Opacity = 0.9,
                },
                Child = list,
            };
            shell.KeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter) { popup.IsOpen = false; e.Handled = true; } };
            popup.Child = shell;
            popup.Opened += (s, e) => shell.Focus();

            // The same invisible sheet as under the VPN picker: nothing under the menu lights up.
            var sheet = new Grid { Background = Brushes.Transparent };
            var rootGrid = Content as Grid;
            if (rootGrid != null)
            {
                if (rootGrid.RowDefinitions.Count > 1) Grid.SetRowSpan(sheet, rootGrid.RowDefinitions.Count);
                if (rootGrid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(sheet, rootGrid.ColumnDefinitions.Count);
                Panel.SetZIndex(sheet, 900);
                rootGrid.Children.Add(sheet);
            }
            popup.Closed += async (s, e) =>
            {
                if (rootGrid != null) rootGrid.Children.Remove(sheet);
                if (_vpnPicker == popup) _vpnPicker = null;
                FlushPickerRebuild();

                // Pinned: nothing here may change the tile's cards — not even the «default card =
                // no choice» clean-up below, which would drop a pin on the card Windows uses.
                if (locked) return;

                // Just the default card and nothing after it is the default choice — stored as such.
                if (IsJustDefaultCard(order)) order.Clear();
                if (order.SequenceEqual(original, StringComparer.OrdinalIgnoreCase)) return;
                entry.Interfaces = new List<string>(order);
                try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-pick-nic-save", ex); }
                Log.Info("Плитка «" + entry.Name + "»: сетевые карты — "
                         + (order.Count == 0 ? "по умолчанию (выбирает Windows)." : string.Join(" › ", order) + "."));
                await ApplyTileRoutingAsync(entry, "сетевые карты плитки").ConfigureAwait(true);
                RebuildTiles();
            };

            _vpnPicker = popup;
            popup.IsOpen = true;
        }

        // «Приоритет сетевых адаптеров» (Windows' card metrics), modal over the main window. A new
        // order changes which card is "Windows' own" — the chips are redrawn when it was applied.
        private void OpenNicPriority()
        {
            try
            {
                var dlg = new NicPriorityDialog(_config) { Owner = this };
                dlg.ShowDialog();
                if (dlg.Applied)
                {
                    _netCardsCache = null;
                    RebuildTiles();
                }
            }
            catch (Exception ex) { Log.Error("nic-priority-dialog", ex); }
        }
    }
}
