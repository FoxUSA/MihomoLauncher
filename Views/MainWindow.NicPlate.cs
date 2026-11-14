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

        // Settings → «Сеть» → «Показывать плашку сетевой карты»: the room the strip takes at the
        // bottom of a tile (its height + the gap above it), 0 when hidden.
        private bool ShowNicPlate => _config?.ShowNicPlate != false;
        private double NicStrip => ShowNicPlate ? NicPlateHeight + 6 : 0;   // + the gap above it

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
            }
            return _netCardsCache;
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
            var state = !c.Up ? Loc.T("Main_NicDown") : !c.HasGateway ? Loc.T("Main_NicNoGw") : Loc.T("Main_NicUp");
            return kind + " · " + state;
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
                Margin = new Thickness(5, 0, 5, 5),
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
                // Which card a tile goes out through is a routing change — behind the access password.
                if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
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

            Border Row(UIElement inner, bool current, Action onClick)
            {
                var cell = new Border
                {
                    MinHeight = 32,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(4),
                    Background = current ? Theme.PickerRowCurrent : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = inner,
                };
                var rest = cell.Background;
                cell.MouseEnter += (s, e) => cell.Background = Theme.PickerRowHover;
                cell.MouseLeave += (s, e) => cell.Background = rest;
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
                return cell;
            }

            void Render()
            {
                list.Children.Clear();
                list.Children.Add(new TextBlock
                {
                    Text = Loc.T("Main_NicHint"),
                    FontSize = 11.5,
                    Foreground = Theme.TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 300,
                    Margin = new Thickness(10, 4, 10, 6),
                });

                // No «По умолчанию» row any more (2026-11-14): untick every card and Windows decides;
                // the card it decides for is marked «по умолчанию» in the list.
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
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                    g.Children.Add(box);
                    var name = card.Name;
                    // The box ticks / unticks (1, 2… order) and keeps the menu open; a click anywhere
                    // else on the row picks just this card and closes the menu (2026-11-14).
                    box.Cursor = Cursors.Hand;
                    box.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        int at = order.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                        if (at >= 0) order.RemoveAt(at);
                        else order.Add(name);
                        Render();
                    };
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
                    var stateOk = card.Present && card.Up;
                    names.Children.Add(new TextBlock
                    {
                        Text = NicStateText(card) + (string.IsNullOrEmpty(card.Description) ? "" : " · " + card.Description),
                        FontSize = 11,
                        Foreground = stateOk ? Theme.TextMuted : Theme.AccentAmber,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 300,
                    });
                    Grid.SetColumn(names, 1);
                    g.Children.Add(names);

                    list.Children.Add(Row(g, false, () =>
                    {
                        order.Clear();
                        order.Add(name);
                        popup.IsOpen = false;
                    }));
                }
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
    }
}
