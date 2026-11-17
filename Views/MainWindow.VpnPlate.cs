using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    // The bottom of a tile: a plate showing which VPN the rule rides (or its status), tinted by
    // that status. On a VPN rule it is a button — it drops down every enabled variant, and
    // picking one reconnects the tile through exactly that variant. Plus the small corner
    // chips (gear / ⟳ / ✕), drawn in the same shape as the plate.
    public partial class MainWindow
    {
        private Popup _vpnPicker;
        private bool _pickerHooked;
        // The press that closed the picker (its input timestamp — the same on every event WPF
        // derives from one press): it and its release must not reach anything.
        private bool _pickerSwallowUp;
        private int _pickerDismissPress;

        // A transparent-hit-test Border, NOT a ui:Button — a custom button template swallowed
        // Click. MouseLeftButtonUp is handled so it doesn't fall through to the tile's launch.
        private FrameworkElement MakeCornerChip(Wpf.Ui.Controls.SymbolRegular symbol, double iconSize,
            Thickness margin, HorizontalAlignment side, string tip, bool busy, Action onClick, Brush iconBrush = null)
        {
            var icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = symbol,
                FontSize = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return MakeCornerChip(icon, b => icon.Foreground = b, margin, side, tip, busy, onClick, iconBrush);
        }

        // The same chip around a drawn icon (the network card's LAN / Wi-Fi glyphs): `paint` colours it.
        private FrameworkElement MakeCornerChip(FrameworkElement icon, Action<Brush> paint,
            Thickness margin, HorizontalAlignment side, string tip, bool busy, Action onClick, Brush iconBrush = null)
        {
            // iconBrush: a chip that tells a state by colour (the network-card one) — kept on hover too.
            var rest = iconBrush ?? Theme.AccentGrey;
            paint(rest);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var chip = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Theme.ChipBg,
                BorderBrush = Theme.ChipBorder,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = side,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = margin,
                Cursor = busy ? Cursors.Arrow : Cursors.Hand,
                ToolTip = tip,
                Opacity = busy ? 0.3 : 1.0,
                Child = icon,
            };
            if (!busy)
            {
                chip.MouseEnter += (s, e) => { chip.Background = Theme.ChipBgHover; chip.BorderBrush = Theme.TileBorderHover; if (iconBrush == null) paint(Theme.TextPrimary); };
                chip.MouseLeave += (s, e) => { chip.Background = Theme.ChipBg; chip.BorderBrush = Theme.ChipBorder; paint(rest); };
                chip.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            }
            else
            {
                chip.MouseLeftButtonUp += (s, e) => e.Handled = true;
            }
            return chip;
        }

        private enum PlateTint { Neutral, Green, Amber, Red }

        // The first variant that fits `room` px at this size; else the last one (TextTrimming cuts it).
        private string FitPlateText(double room, double size, string[] variants)
        {
            var list = (variants ?? new string[0]).Where(v => !string.IsNullOrEmpty(v)).ToList();
            if (list.Count == 0) return "";
            foreach (var v in list)
                if (TextWidth(v, size, FontWeights.Normal) <= room - 2) return v;   // 2 px of slack for DPI rounding
            return list[list.Count - 1];
        }

        internal double TextWidth(string s, double size, FontWeight weight)
        {
            try
            {
                var tf = new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
                return new FormattedText(s ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    tf, size, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
            }
            catch { return 0; }
        }

        // The VPN line of a connected tile: «№2 Имя», else just «Имя».
        private string[] PlateVpnLabels(AppEntry e)
        {
            if (e == null || !_assignment.TryGetValue(e.Id, out var proxy)) return new[] { "" };
            _proxySlot.TryGetValue(proxy, out var info);
            var name = info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : proxy;
            var num = info != null && info.Number > 0 ? "\u2116" + info.Number + " " : "";
            return num.Length > 0 ? new[] { num + name, name } : new[] { name };
        }

        // blocks: the plate says «Блокирует» or «Заблокировано · kill-switch» — the ONLY two cases a
        // tile is dimmed (2026-11-16), decided here so the tile's look can't drift from its plate.
        private FrameworkElement MakeVpnPlate(AppEntry entry, bool enabled, bool reconnecting, bool notChecked, out bool blocks)
        {
            // Each line is a list of variants, longest first: the first one that fits the plate is
            // shown (FitPlateText), the last one is cut with «…» if even that does not fit — a
            // VPN or city name from the user's own list can be any length (2026-11-13).
            string[] line1, line2 = null;
            Brush fg;
            PlateTint tint;
            // The kill-switch is at work whenever the tile is not carrying its traffic through a
            // VPN right now (2026-11-13): say so on the plate, not only on «без VPN» tiles.
            bool killOn = entry.KillSwitch && !entry.IsBlocking && !reconnecting;

            if (entry.IsBlockMode)
            {
                // «Блокировать»: on → red «Блокирует», off → plain «Блокировать».
                line1 = new[] { Loc.T(entry.AutoConnect ? "Main_TileBlocking" : "Main_TileBlock") };
                fg = entry.AutoConnect ? Theme.AccentRed : Theme.TextMuted;
                tint = entry.AutoConnect ? PlateTint.Red : PlateTint.Neutral;
                if (!entry.AutoConnect && killOn) { line1 = new[] { Loc.T("Main_TileKillBlocked") }; line2 = new[] { "kill-switch" }; fg = Theme.AccentRed; tint = PlateTint.Red; }
            }
            else if (reconnecting)
            {
                line1 = new[] { Loc.T("Main_TileReconnecting") }; fg = Theme.AccentGreen; tint = PlateTint.Neutral;
            }
            else if (!entry.RequiresVpn)
            {
                // «Без VPN» + kill-switch = no internet at all (the kill-switch works always).
                if (killOn) { line1 = new[] { Loc.T("Main_TileKillBlocked") }; line2 = new[] { "kill-switch" }; fg = Theme.AccentRed; tint = PlateTint.Red; }
                else { line1 = new[] { Loc.T("Main_TileDirect") }; fg = Theme.AccentGrey; tint = PlateTint.Neutral; }
            }
            else if (!enabled || IsVpnTileGoingDirect(entry))
            {
                // The first VPN the rule's own settings rank — what it will use when switched on.
                var first = FirstRuleSlot(entry);
                bool noVpn = HasNoAllowedVpnVariant(entry) || first == null;
                // Switched off by hand (tile ✕, or simply never started) is a decision, not a
                // fault: red "не работает" is for a rule that was tried and no node opened it.
                bool stopped = !notChecked && !noVpn && !entry.AutoConnect;
                bool failed = !noVpn && !notChecked && !stopped;
                if (killOn && !failed)
                {
                    // Not started (or no VPN chosen) and the kill-switch holds it: blocked. The block
                    // wins over \u00ABVPN \u043D\u0435 \u0432\u044B\u0431\u0440\u0430\u043D\u00BB (2026-11-14): what the person needs to see first is
                    // that this tile has no internet; the missing VPN is the reason, second line.
                    line1 = new[] { Loc.T("Main_TileKillBlocked") };
                    line2 = noVpn ? new[] { "kill-switch \u00B7 " + Loc.T("Main_TileNoVpnSelected"), "kill-switch" } : new[] { "kill-switch" };
                    fg = Theme.AccentRed;
                    tint = PlateTint.Red;
                }
                else if (failed)
                {
                    line1 = new[] { Loc.T("Main_TileNotWorking") };
                    fg = Theme.AccentRed;
                    tint = PlateTint.Red;
                    // Switched on but no node opened it: still blocked by its kill-switch.
                    if (killOn) line2 = new[] { Loc.T("Main_TileKillBlocked") + " \u00B7 kill-switch", "kill-switch" };
                }
                else if (noVpn)
                {
                    // Nothing ticked in the rule's settings and no kill-switch: its traffic simply
                    // goes without a VPN — say exactly that, like a «без VPN» tile (2026-11-16: «у нас
                    // без впн и впн не выбран одно и тоже? Если да оставь только без впн»).
                    line1 = new[] { Loc.T("Main_TileDirect") };
                    fg = Theme.AccentGrey;
                    tint = PlateTint.Neutral;
                }
                else
                {
                    // Not switched on (or not tried yet): the VPN it WILL use \u2014 the first one its
                    // settings rank \u2014 in grey, instead of a bare \u00AB\u043E\u0442\u043A\u043B\u044E\u0447\u0435\u043D\u043E\u00BB (2026-11-14). Green
                    // only once it actually rides it.
                    var label = SlotLabel(first);
                    line1 = label != first.Name ? new[] { label, first.Name } : new[] { first.Name };
                    var geo = SlotGeo(first.Id);
                    if (!string.IsNullOrEmpty(geo)) line2 = new[] { geo, geo.Split(',')[0].Trim() };
                    fg = Theme.TextSecondary;
                    tint = PlateTint.Neutral;
                }
            }
            else
            {
                // Which variant this rule rides, and the country/city its server exits in. No shield
                // icon here: the green plate already says «through a VPN», and it took a third of the width.
                line1 = PlateVpnLabels(entry); fg = Theme.AccentGreen; tint = PlateTint.Green;
                if (_assignment.TryGetValue(entry.Id, out var proxyName)
                    && _proxyGeo.TryGetValue(proxyName, out var geo) && !string.IsNullOrEmpty(geo))
                    line2 = new[] { geo, geo.Split(',')[0].Trim() };    // «Страна, Город» → «Страна»
            }

            // A plate is a button whenever it is not mid-reconnect, while no other tile is
            // reconnecting either (same as its ⟳) — on a «напрямую» tile too: VPN or «Напрямую» is
            // picked here since the card's «Использовать VPN» switch went (2026-11-14).
            string blockingText = Loc.T("Main_TileBlocking"), killText = Loc.T("Main_TileKillBlocked");
            blocks = (line1 != null && line1.Length > 0 && (line1[0] == blockingText || line1[0] == killText))
                  || (line2 != null && line2.Length > 0 && line2[0] != null && line2[0].StartsWith(killText, StringComparison.Ordinal));

            bool clickable = !reconnecting && _reconnectingId == null;
            // Room for the text: the plate is 100 px, minus its border, padding and the chevron.
            double room = clickable ? 100 - 2 - 9 - 4 - 12 : 100 - 2 - 4 - 4;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = FitPlateText(room, 10, line1),
                FontSize = 10,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (line2 != null && line2.Length > 0 && !string.IsNullOrEmpty(line2[0]))
                text.Children.Add(new TextBlock
                {
                    Text = FitPlateText(room, 9.5, line2),
                    FontSize = 9.5,
                    Foreground = Theme.TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
                Height = 32,
                // The network-card plate (MakeNicPlate) takes the strip under it.
                Margin = new Thickness(5, 5, 5, PlateGap + NicStrip + SpeedStrip),
                Padding = new Thickness(clickable ? 9 : 4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(7),
                Background = bg,
                BorderBrush = bd,
                BorderThickness = new Thickness(1),
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
                Child = content,
            };
            // The full, uncut text (the longest variant of each line).
            if (line2 != null && line2.Length > 0 && !string.IsNullOrEmpty(line2[0])) plate.ToolTip = line1[0] + " — " + line2[0];
            else if (line1.Length > 1) plate.ToolTip = line1[0];

            if (clickable)
            {
                if (plate.ToolTip == null) plate.ToolTip = Loc.T("Main_TipPickVpn");
                plate.MouseEnter += (s, e) => { plate.Background = bgHover; plate.BorderBrush = bdHover; };
                plate.MouseLeave += (s, e) => { plate.Background = bg; plate.BorderBrush = bd; };
                plate.MouseLeftButtonUp += async (s, e) =>
                {
                    e.Handled = true;
                    // Picking a VPN is a routing change — behind the access password.
                    if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                    ShowVpnPicker(entry, plate);
                };
            }
            return plate;
        }

        // The rule's first VPN by its own settings (ticked, in its order; enabled variants only) —
        // not health-sorted: this is what the person chose, shown on a plate that isn't connected.
        private VpnSlot FirstRuleSlot(AppEntry e)
        {
            if (e == null) return null;
            var order = e.VpnSlotOrder ?? new List<string>();
            var disabled = new HashSet<string>(e.DisabledVpnSlotIds ?? new List<string>());
            return (_config.VpnSlots ?? new List<VpnSlot>())
                .Where(s => s != null && s.Enabled && !disabled.Contains(s.Id))
                .OrderBy(s => { int i = order.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; })
                .FirstOrDefault();
        }

        // "№2 Дом" — the same number + name the tile shows for the variant.
        private string SlotLabel(VpnSlot slot)
        {
            int n = (_config.VpnSlots?.IndexOf(slot) ?? -1) + 1;
            return (n > 0 ? "№" + n + " " : "") + slot.Name;
        }

        // Country/city of a variant, if any of its proxies has been located this session.
        private string SlotGeo(string slotId)
        {
            foreach (var kv in _proxySlot)
                if (kv.Value?.Id == slotId && _proxyGeo.TryGetValue(kv.Key, out var geo) && !string.IsNullOrEmpty(geo))
                    return geo;
            return "";
        }

        private void ShowVpnPicker(AppEntry entry, FrameworkElement anchor)
        {
            if (_vpnPicker != null) _vpnPicker.IsOpen = false;
            HookPickerDismiss();

            var slots = (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null && s.Enabled).ToList();
            // Nothing to pick: no VPN to move to, and the tile is already where «Напрямую» would
            // put it (the built-in tiles still have «Блокировать»).
            if (slots.Count == 0 && !entry.IsBuiltIn && !entry.RequiresVpn)
            {
                _ = ShowDialog(Loc.T("Main_NoVariantsTitle"), Loc.T("Main_NoVariantsBody"));
                return;
            }

            string currentSlotId = null;
            if (entry.RequiresVpn && !entry.IsNoNodeMode && _assignment.TryGetValue(entry.Id, out var proxy) && _proxySlot.TryGetValue(proxy, out var info))
                currentSlotId = info?.Id;

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                // Closed by us (HookPickerDismiss), not by the popup itself: its own "close on a
                // click outside" let that click go on to the tile, ⟳, ✕ or button under it.
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };

            var list = new StackPanel { Margin = new Thickness(4) };
            // The rule's own VPNs first, in the order its settings rank them; a divider; then the
            // rest in the Settings order (2026-11-14). A pick from either part connects once and
            // changes nothing in the rule (PickVpnForRuleAsync).
            var rank = entry.VpnSlotOrder ?? new List<string>();
            var off = new HashSet<string>(entry.DisabledVpnSlotIds ?? new List<string>());
            var chosen = slots.Where(s => !off.Contains(s.Id))
                .OrderBy(s => { int i = rank.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; }).ToList();
            var others = slots.Where(s => off.Contains(s.Id)).ToList();
            var ordered = new List<VpnSlot>(chosen);
            ordered.AddRange(others);
            foreach (var slot in ordered)
            {
                if (others.Count > 0 && chosen.Count > 0 && ReferenceEquals(slot, others[0]))
                    list.Children.Add(new Border { Height = 1, Background = Theme.PickerBorder, Margin = new Thickness(6, 4, 6, 4) });
                bool current = slot.Id == currentSlotId;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = SlotLabel(slot),
                    FontSize = 13,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Theme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var geo = new TextBlock
                {
                    Text = SlotGeo(slot.Id),
                    FontSize = 12,
                    Foreground = Theme.TextMuted,
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(geo, 1);
                row.Children.Add(geo);
                if (current)
                    row.Children.Add(new Border
                    {
                        Width = 3,
                        CornerRadius = new CornerRadius(2),
                        Background = Theme.AccentGreen,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(-10, 9, 0, 9),
                    });

                var cell = new Border
                {
                    Height = 32,
                    Padding = new Thickness(12, 0, 10, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = current ? Theme.PickerRowCurrent : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = row,
                };
                var rest = cell.Background;
                cell.MouseEnter += (s, e) => cell.Background = Theme.PickerRowHover;
                cell.MouseLeave += (s, e) => cell.Background = rest;
                var picked = slot;
                cell.MouseLeftButtonUp += async (s, e) =>
                {
                    e.Handled = true;
                    popup.IsOpen = false;
                    await PickVpnForRuleAsync(entry, picked, current).ConfigureAwait(true);
                };
                list.Children.Add(cell);
            }

            // «Напрямую» — every tile, «Всё остальное» too (2026-11-16: «как у других плиток»),
            // under the VPNs. Replaces the card's «Использовать VPN» switch (2026-11-14).
            {
                // Going without a VPN right now: a «без VPN» tile, or a VPN tile switched off with
                // no kill-switch holding it.
                bool current = !entry.IsBlockMode && (!entry.RequiresVpn
                    || (!entry.AutoConnect && !entry.KillSwitch && !_workingApps.Contains(entry.Id) && !_assignment.ContainsKey(entry.Id)));
                var row = new Grid();
                row.Children.Add(new TextBlock
                {
                    Text = Loc.T("Main_PickDirect"),
                    FontSize = 13,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Theme.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (current)
                    row.Children.Add(new Border
                    {
                        Width = 3,
                        CornerRadius = new CornerRadius(2),
                        Background = Theme.AccentGrey,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(-10, 9, 0, 9),
                    });
                var cell = new Border
                {
                    Height = 32,
                    Padding = new Thickness(12, 0, 10, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = current ? Theme.PickerRowCurrent : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = row,
                };
                if (slots.Count > 0)
                    list.Children.Add(new Border { Height = 1, Background = Theme.PickerBorder, Margin = new Thickness(6, 4, 6, 4) });
                var rest = cell.Background;
                cell.MouseEnter += (s, e) => cell.Background = Theme.PickerRowHover;
                cell.MouseLeave += (s, e) => cell.Background = rest;
                cell.MouseLeftButtonUp += async (s, e) =>
                {
                    e.Handled = true;
                    popup.IsOpen = false;
                    await PickDirectForRuleAsync(entry).ConfigureAwait(true);
                };
                list.Children.Add(cell);
            }

            // «Блокировать» — on every tile (2026-11-16; was «Всё остальное» only), under «Напрямую».
            {
                bool current = entry.IsBlockMode;
                var row = new Grid();
                row.Children.Add(new TextBlock
                {
                    Text = "\u26D4 " + Loc.T("Main_TileBlock"),
                    FontSize = 13,
                    FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Theme.AccentRed,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (current)
                    row.Children.Add(new Border
                    {
                        Width = 3,
                        CornerRadius = new CornerRadius(2),
                        Background = Theme.AccentRed,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(-10, 9, 0, 9),
                    });
                var cell = new Border
                {
                    Height = 32,
                    Padding = new Thickness(12, 0, 10, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = current ? Theme.PickerRowCurrent : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = row,
                };
                if (slots.Count > 0)
                    list.Children.Add(new Border { Height = 1, Background = Theme.PickerBorder, Margin = new Thickness(6, 4, 6, 4) });
                var rest = cell.Background;
                cell.MouseEnter += (s, e) => cell.Background = Theme.PickerRowHover;
                cell.MouseLeave += (s, e) => cell.Background = rest;
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    popup.IsOpen = false;
                    PickBlockForRule(entry);
                };
                list.Children.Add(cell);
            }

            var box = new Border
            {
                MinWidth = 200,
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
            box.KeyDown += (s, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
            popup.Child = box;
            popup.Opened += (s, e) => box.Focus();
            // An invisible sheet over the whole window while the menu is open — like a Windows menu,
            // nothing under it lights up on hover as if it were about to be pressed (2026-11-10).
            // Presses on it are taken by HookPickerDismiss like anywhere else in the window; the
            // menu itself is its own window, above the sheet. Same trick as OverlayDialog.
            var sheet = new Grid { Background = Brushes.Transparent };
            var rootGrid = Content as Grid;
            if (rootGrid != null)
            {
                if (rootGrid.RowDefinitions.Count > 1) Grid.SetRowSpan(sheet, rootGrid.RowDefinitions.Count);
                if (rootGrid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(sheet, rootGrid.ColumnDefinitions.Count);
                Panel.SetZIndex(sheet, 900);
                rootGrid.Children.Add(sheet);
            }
            popup.Closed += (s, e) =>
            {
                if (rootGrid != null) rootGrid.Children.Remove(sheet);
                if (_vpnPicker == popup) _vpnPicker = null;
                FlushPickerRebuild();
            };

            _vpnPicker = popup;
            popup.IsOpen = true;
        }

        // While the picker is open, ANY press anywhere in the window only closes it: the press is
        // taken at the window before anything under it sees it, and so is its release — no tile
        // launches, no ⟳/✕, no button, whichever mouse button (2026-11-10: «любой клик просто
        // закрывает это меню и всё»). The picker is its own window, so presses inside it never
        // come here. Leaving the window (another app, minimise) or moving it closes it too.
        // A RebuildTiles that came while a plate menu was open (it waits — see RebuildTiles).
        private bool _pickerRebuildPending;
        private void FlushPickerRebuild()
        {
            if (!_pickerRebuildPending) return;
            _pickerRebuildPending = false;
            RebuildTiles();
        }

        private void HookPickerDismiss()
        {
            if (_pickerHooked) return;
            _pickerHooked = true;
            // Down: the generic event and the left/right ones WPF derives from it, all taken.
            MouseButtonEventHandler down = (s, e) =>
            {
                if (_vpnPicker != null && _vpnPicker.IsOpen)
                {
                    _vpnPicker.IsOpen = false;
                    _pickerSwallowUp = true;
                    _pickerDismissPress = e.Timestamp;
                    e.Handled = true;
                }
                else if (_pickerSwallowUp && e.Timestamp == _pickerDismissPress) e.Handled = true;   // same press, another form of it
                else _pickerSwallowUp = false;                                                         // a new press: back to normal
            };
            // Up: every release until the next new press — the generic one and its left/right forms.
            MouseButtonEventHandler up = (s, e) => { if (_pickerSwallowUp) e.Handled = true; };
            AddHandler(PreviewMouseDownEvent, down, true);
            AddHandler(PreviewMouseLeftButtonDownEvent, down, true);
            AddHandler(PreviewMouseRightButtonDownEvent, down, true);
            AddHandler(PreviewMouseUpEvent, up, true);
            AddHandler(PreviewMouseLeftButtonUpEvent, up, true);
            AddHandler(PreviewMouseRightButtonUpEvent, up, true);
            Deactivated += (s, e) => { if (_vpnPicker != null) _vpnPicker.IsOpen = false; };
            LocationChanged += (s, e) => { if (_vpnPicker != null) _vpnPicker.IsOpen = false; };
        }

        // «Блокировать» picked on a built-in tile's plate: remembered, and switched on at once (like
        // picking a VPN connects it). ✕ switches it off; picking a VPN replaces it.
        private void PickBlockForRule(AppEntry entry)
        {
            if (entry == null) return;
            if (_reconnectingId != null || _state == UiState.Checking) return;
            if (entry.IsBlocking) return;
            entry.Block = true;
            // From «Напрямую»: back to a VPN-kind tile (as PickVpnForRuleAsync does), so the block
            // is written like it always was. The kill-switch is not touched — only the person sets
            // or clears it (2026-11-16).
            if (!entry.RequiresVpn) entry.RequiresVpn = true;
            RestoreKillAfterDirect(entry);   // a kill-switch an older build took off on «Напрямую»
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-pick-block-save", ex); }
            StartBlock(entry);
        }

        // Picked from the plate: from now on this rule prefers that variant (first in its own
        // order, and allowed if it was not — the rest of its allow-list stays as it was), and
        // it reconnects through exactly that one right away.
        private async Task PickVpnForRuleAsync(AppEntry entry, VpnSlot slot, bool alreadyOnIt)
        {
            if (entry == null || slot == null) return;
            // The VPN it is on already, picked again = reconnect through it (2026-11-17: the tile's ⟳
            // is gone — «переподключиться можно выбрав в плашке ещё раз это же впн»).
            if (_reconnectingId != null) return;   // another tile is mid-reconnect; a running full connect is waited out in ReconnectOneAsync

            // A pick on the plate is "connect through this one now" — NOT an edit of the rule. The
            // VPNs ticked and ranked in the rule's card are what the launcher remembers and uses on
            // its own (restart, auto-retry); the plate leaves them alone (2026-11-14: «если выбрали
            // впн через плашку не надо ставить галочку на впн в настройках»).
            bool changed = false;
            if (entry.Block) { entry.Block = false; changed = true; }   // a VPN picked: no longer «Блокировать»
            if (!entry.RequiresVpn) { entry.RequiresVpn = true; changed = true; }
            // A kill-switch an older build took off on «Напрямую» comes back (it was the person's).
            if (RestoreKillAfterDirect(entry)) changed = true;
            if (changed) try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-pick-vpn-save", ex); }
            Log.Info("Плитка «" + entry.Name + "»: выбран VPN «" + slot.Name + "» (разово, настройки правила не менялись).");
            _platePick[entry.Id] = slot.Id;   // the tile's ⟳ goes through it when the rule ticks no VPN

            await ReconnectOneAsync(entry, manual: true, onlySlotId: slot.Id).ConfigureAwait(true);
        }

        // Legacy: until 2026-11-16 «Напрямую» took the kill-switch off and remembered it in
        // KillSwitchBeforeDirect. A tile saved in that state gets it back the next time it goes to a
        // VPN or «Блокировать». Nothing sets the flag any more. True if anything changed.
        private bool RestoreKillAfterDirect(AppEntry entry)
        {
            if (entry == null || !entry.KillSwitchBeforeDirect) return false;
            entry.KillSwitchBeforeDirect = false;
            if (!entry.KillSwitch) { entry.KillSwitch = true; SyncFirewallSoon(); }
            return true;
        }

        // «Напрямую» picked on the plate (2026-11-14, replaces the card's «Использовать VPN»):
        // the tile leaves its VPN and goes straight out — through its network cards, if it has
        // any. The kill-switch is NOT touched (2026-11-16: «мы не снимаем килл свитч, это может
        // сделать только юзер»): direct + kill-switch = blocked, and the plate says so.
        private async Task PickDirectForRuleAsync(AppEntry entry)
        {
            if (entry == null || (!entry.RequiresVpn && !entry.IsBlockMode)) return;
            if (_reconnectingId != null || _state == UiState.Checking) return;

            // «Всё остальное» set to «Блокировать» → «Напрямую»: the block goes too.
            entry.Block = false;
            RestoreKillAfterDirect(entry);   // a kill-switch an older build took off — back first
            // VPNs ticked in its card: it stays a VPN tile, just switched off — the plate shows its
            // first VPN in grey with ▷ (2026-11-16). Nothing ticked: a «без VPN» tile.
            entry.RequiresVpn = AppConfigStore.HasTickedVpn(entry, _config.VpnSlots);
            entry.AutoConnect = false;   // nothing to keep connected: the auto-retry must leave it be
            _assignment.Remove(entry.Id);
            _workingApps.Remove(entry.Id);
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-pick-direct-save", ex); }
            Log.Info("Плитка «" + entry.Name + "»: выбрано «Напрямую»" + (entry.KillSwitch ? " (кил-свитч остаётся включённым — трафик заблокирован)." : "."));
            SyncFirewallSoon();   // its programs leave the firewall kill-switch
            await ApplyTileRoutingAsync(entry, "плитка напрямую").ConfigureAwait(true);
            RebuildTiles();
            if (_state == UiState.Done) UpdateDoneBanner();
            _ = ResolveProxyGeosAsync();
        }

        // A tile's routing changed by hand (its network cards, «Напрямую»): into the engine now,
        // and what the tile has open is cut after the reload, so it re-dials the new way — open
        // sockets otherwise keep their old path for as long as the program holds them. Mid-connect
        // it is left alone: that connect writes the final config from _config itself.
        private async Task ApplyTileRoutingAsync(AppEntry entry, string reason)
        {
            try
            {
                EngineLogStore.Instance.RefreshRules(_config);
                if (_state == UiState.Checking || _reconnectingId != null) return;
                if (!_hasChecked)
                {
                    await ArmKillSwitchAsync(CancellationToken.None).ConfigureAwait(true);
                    CloseConnectionsFor(new[] { entry });
                }
                else ApplyFinalConfig(CurrentAssignedProxies(), reason, force: true, alsoCut: new[] { entry });
            }
            catch (Exception ex) { Log.Error("apply-tile-routing", ex); }
        }
    }
}
