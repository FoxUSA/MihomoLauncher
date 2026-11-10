using System;
using System.Collections.Generic;
using System.Linq;
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
            Thickness margin, HorizontalAlignment side, string tip, bool busy, Action onClick)
        {
            var icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = symbol,
                FontSize = iconSize,
                Foreground = Theme.AccentGrey,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
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
                chip.MouseEnter += (s, e) => { chip.Background = Theme.ChipBgHover; chip.BorderBrush = Theme.TileBorderHover; icon.Foreground = Theme.TextPrimary; };
                chip.MouseLeave += (s, e) => { chip.Background = Theme.ChipBg; chip.BorderBrush = Theme.ChipBorder; icon.Foreground = Theme.AccentGrey; };
                chip.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };
            }
            else
            {
                chip.MouseLeftButtonUp += (s, e) => e.Handled = true;
            }
            return chip;
        }

        private enum PlateTint { Neutral, Green, Amber, Red }

        private FrameworkElement MakeVpnPlate(AppEntry entry, bool enabled, bool reconnecting, bool notChecked)
        {
            string line1, line2 = "";
            Brush fg;
            PlateTint tint;

            if (reconnecting)
            {
                line1 = Loc.T("Main_TileReconnecting"); fg = Theme.AccentGreen; tint = PlateTint.Neutral;
            }
            else if (!enabled)
            {
                // All VPN variants unchecked for this rule → it can never connect; show that
                // explicitly (amber) instead of the generic "нужен VPN" / "не работает".
                bool noVpn = HasNoAllowedVpnVariant(entry);
                // Switched off by hand (tile ✕, or simply never started) is a decision, not a
                // fault: red "не работает" is for a rule that was tried and no node opened it.
                bool stopped = !notChecked && !noVpn && !entry.AutoConnect;
                line1 = noVpn ? Loc.T("Main_TileNoVpnSelected")
                      : notChecked ? Loc.T("Main_TileNeedVpn")
                      : stopped ? Loc.T("Main_TileStopped")
                      : Loc.T("Main_TileNotWorking");
                fg = noVpn ? Theme.AccentAmber : (notChecked || stopped) ? Theme.TextMuted : Theme.AccentRed;
                tint = noVpn ? PlateTint.Amber : (notChecked || stopped) ? PlateTint.Neutral : PlateTint.Red;
            }
            else if (!entry.RequiresVpn)
            {
                line1 = Loc.T("Main_TileDirect"); fg = Theme.AccentGrey; tint = PlateTint.Neutral;
            }
            else
            {
                // Which variant this rule rides, and the country/city its server exits in.
                line1 = TileVpnLabel(entry); fg = Theme.AccentGreen; tint = PlateTint.Green;
                if (_assignment.TryGetValue(entry.Id, out var proxyName)
                    && _proxyGeo.TryGetValue(proxyName, out var geo) && !string.IsNullOrEmpty(geo))
                    line2 = geo;
            }

            // A plate is a button only where picking a VPN means something: a VPN rule that is
            // not mid-reconnect, while no other tile is reconnecting either (same as its ⟳).
            bool clickable = entry.RequiresVpn && !reconnecting && _reconnectingId == null;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = line1,
                FontSize = 10,
                Foreground = fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrEmpty(line2))
                text.Children.Add(new TextBlock
                {
                    Text = line2,
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
                Margin = new Thickness(5),
                Padding = new Thickness(clickable ? 9 : 4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(7),
                Background = bg,
                BorderBrush = bd,
                BorderThickness = new Thickness(1),
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
                Child = content,
            };
            if (!string.IsNullOrEmpty(line2)) plate.ToolTip = line1.Replace("🛡 ", "") + " — " + line2;

            if (clickable)
            {
                if (plate.ToolTip == null) plate.ToolTip = Loc.T("Main_TipPickVpn");
                plate.MouseEnter += (s, e) => { plate.Background = bgHover; plate.BorderBrush = bdHover; };
                plate.MouseLeave += (s, e) => { plate.Background = bg; plate.BorderBrush = bd; };
                plate.MouseLeftButtonUp += (s, e) => { e.Handled = true; ShowVpnPicker(entry, plate); };
            }
            return plate;
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
            if (slots.Count == 0)
            {
                _ = ShowDialog(Loc.T("Main_NoVariantsTitle"), Loc.T("Main_NoVariantsBody"));
                return;
            }

            string currentSlotId = null;
            if (_assignment.TryGetValue(entry.Id, out var proxy) && _proxySlot.TryGetValue(proxy, out var info))
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
            foreach (var slot in slots)
            {
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
            };

            _vpnPicker = popup;
            popup.IsOpen = true;
        }

        // While the picker is open, ANY press anywhere in the window only closes it: the press is
        // taken at the window before anything under it sees it, and so is its release — no tile
        // launches, no ⟳/✕, no button, whichever mouse button (2026-11-10: «любой клик просто
        // закрывает это меню и всё»). The picker is its own window, so presses inside it never
        // come here. Leaving the window (another app, minimise) or moving it closes it too.
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

        // Picked from the plate: from now on this rule prefers that variant (first in its own
        // order, and allowed if it was not — the rest of its allow-list stays as it was), and
        // it reconnects through exactly that one right away.
        private async Task PickVpnForRuleAsync(AppEntry entry, VpnSlot slot, bool alreadyOnIt)
        {
            if (entry == null || slot == null) return;
            if (alreadyOnIt && _workingApps.Contains(entry.Id)) return;
            if (_reconnectingId != null) return;   // another tile is mid-reconnect; a running full connect is waited out in ReconnectOneAsync

            entry.DisabledVpnSlotIds?.Remove(slot.Id);
            var order = entry.VpnSlotOrder ?? new List<string>();
            order.Remove(slot.Id);
            order.Insert(0, slot.Id);
            entry.VpnSlotOrder = order;
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-pick-vpn-save", ex); }
            Log.Info("Плитка «" + entry.Name + "»: выбран VPN «" + slot.Name + "».");

            await ReconnectOneAsync(entry, manual: true, onlySlotId: slot.Id).ConfigureAwait(true);
        }
    }
}
