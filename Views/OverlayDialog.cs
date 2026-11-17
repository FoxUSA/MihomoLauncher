using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // In-window themed overlay (dimmed backdrop + centered card) used instead of
    // separate popup windows. Avoids the FluentWindow SizeToContent sizing bug and
    // keeps everything in the app's own theme. Returns a Task that completes when the
    // user clicks a button (or the backdrop).
    public static class OverlayDialog
    {
        public static Task<bool> ConfirmAsync(Window owner, string title, string body, string okText = null, string cancelText = null,
            Wpf.Ui.Controls.ControlAppearance okAppearance = Wpf.Ui.Controls.ControlAppearance.Primary)
            => ShowAsync(owner, title, body, okText ?? Services.Loc.T("Dlg_Yes"), cancelText ?? Services.Loc.T("Dlg_Cancel"), okAppearance);

        // Same, but the caller can take the dialog down itself (as "cancel") — for a question
        // that stops being a question, e.g. "reinstall the engine?" once the engine answers.
        public static Task<bool> ConfirmAsync(Window owner, string title, string body, string okText, string cancelText,
            System.Threading.CancellationToken dismiss)
            => ShowAsync(owner, title, body, okText, cancelText, Wpf.Ui.Controls.ControlAppearance.Primary, null, 400, dismiss);

        // Result of a confirm dialog that carries an extra toggle switch.
        public class ToggleConfirm
        {
            public bool Ok;
            public bool Toggle;
        }

        // Like ConfirmAsync, but shows a ToggleSwitch between the body and the buttons. The
        // toggle's final state is returned alongside the button result (regardless of which
        // button closed it).
        public static Task<ToggleConfirm> ConfirmWithToggleAsync(Window owner, string title, string body,
            string okText, string cancelText, string toggleLabel, bool toggleInitial)
            => ShowAsync(owner, title, body, okText, cancelText, toggleLabel, toggleInitial);

        public static async Task MessageAsync(Window owner, string title, string body, string okText = "OK")
            => await ShowAsync(owner, title, body, okText, null).ConfigureAwait(true);

        // Like ConfirmAsync, with a ready-made body (several blocks, badges) instead of one text,
        // on a wider card. cancelText null = a single button.
        // buttonHost: a panel inside the content that takes the buttons instead of a row of their
        // own under it (the speed window puts «Закрыть» on the totals line).
        public static Task<bool> ConfirmContentAsync(Window owner, string title, UIElement content, string okText,
            string cancelText, double width = 480, Panel buttonHost = null)
            => ShowAsync(owner, title, null, okText, cancelText, Wpf.Ui.Controls.ControlAppearance.Primary, content, width,
                default(System.Threading.CancellationToken), buttonHost);

        private static Task<bool> ShowAsync(Window owner, string title, string body, string okText, string cancelText,
            Wpf.Ui.Controls.ControlAppearance okAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            UIElement content = null, double width = 400, System.Threading.CancellationToken dismiss = default(System.Threading.CancellationToken),
            Panel buttonHost = null)
        {
            var tcs = new TaskCompletionSource<bool>();

            var rootGrid = owner != null ? owner.Content as Grid : null;
            if (rootGrid == null) { tcs.SetResult(false); return tcs.Task; }

            var overlay = new Grid { Background = Services.Theme.OverlayBackdrop };
            if (rootGrid.RowDefinitions.Count > 1) Grid.SetRowSpan(overlay, rootGrid.RowDefinitions.Count);
            if (rootGrid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(overlay, rootGrid.ColumnDefinitions.Count);
            Panel.SetZIndex(overlay, 1000);

            var card = new Border
            {
                Width = width,
                Background = Services.Theme.OverlayCardBg,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 20, 22, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35 },
            };

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = Services.Theme.TextPrimary,
            });
            if (content != null)
            {
                if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, buttonHost != null ? 0 : 18);
                sp.Children.Add(content);
            }
            else
                sp.Children.Add(new TextBlock
                {
                    Text = body,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 18),
                    Foreground = Services.Theme.OverlayBody,
                });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            System.Action unhook = null;
            void Close(bool result)
            {
                unhook?.Invoke();
                try { rootGrid.Children.Remove(overlay); } catch { }
                tcs.TrySetResult(result);
            }

            var okBtn = new Button
            {
                Content = okText,
                Appearance = okAppearance,
                FontSize = 14,
                Padding = new Thickness(20, 8, 20, 8),
                Margin = buttonHost != null && cancelText == null ? new Thickness(0) : new Thickness(0, 0, 8, 0),
            };
            okBtn.Click += (s, e) => Close(true);
            btnRow.Children.Add(okBtn);

            if (cancelText != null)
            {
                var cancelBtn = new Button
                {
                    Content = cancelText,
                    FontSize = 14,
                    Padding = new Thickness(20, 8, 20, 8),
                };
                cancelBtn.Click += (s, e) => Close(false);
                btnRow.Children.Add(cancelBtn);
            }

            if (buttonHost != null) buttonHost.Children.Add(btnRow);
            else sp.Children.Add(btnRow);
            card.Child = sp;
            overlay.Children.Add(card);

            // Click on the dimmed backdrop dismisses (cancel/close); clicks on the
            // card itself are swallowed so they don't bubble to the backdrop.
            overlay.MouseLeftButtonDown += (s, e) => { if (!e.Handled) Close(false); };
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;

            rootGrid.Children.Add(overlay);
            unhook = HookKeys(owner, overlay, Close);
            // A plain question: the main button takes the focus, so the key that opened the
            // dialog's owner button can't press that button again underneath.
            if (content == null) overlay.Dispatcher.BeginInvoke(new System.Action(() => okBtn.Focus()),
                System.Windows.Threading.DispatcherPriority.Loaded);
            if (dismiss.CanBeCanceled)
                dismiss.Register(() => owner.Dispatcher.BeginInvoke(new System.Action(() => Close(false))));
            return tcs.Task;
        }

        // Three answers (2026-11-17, deleting a tab with tiles): the main (safe) button, a red one
        // standing apart on the left, and cancel. 1 = main, 2 = red, 0 = cancel. Enter = main,
        // Esc / the backdrop = cancel — never the red one by accident.
        public static Task<int> ChoiceAsync(Window owner, string title, string body,
            string mainText, string dangerText, string cancelText)
        {
            var tcs = new TaskCompletionSource<int>();
            var rootGrid = owner != null ? owner.Content as Grid : null;
            if (rootGrid == null) { tcs.SetResult(0); return tcs.Task; }

            var overlay = new Grid { Background = Services.Theme.OverlayBackdrop };
            if (rootGrid.RowDefinitions.Count > 1) Grid.SetRowSpan(overlay, rootGrid.RowDefinitions.Count);
            if (rootGrid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(overlay, rootGrid.ColumnDefinitions.Count);
            Panel.SetZIndex(overlay, 1000);

            var card = new Border
            {
                Width = 460,
                Background = Services.Theme.OverlayCardBg,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 20, 22, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35 },
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8), Foreground = Services.Theme.TextPrimary,
            });
            sp.Children.Add(new TextBlock
            {
                Text = body, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18), Foreground = Services.Theme.OverlayBody,
            });

            System.Action unhook = null;
            void Done(int r)
            {
                unhook?.Invoke();
                try { rootGrid.Children.Remove(overlay); } catch { }
                tcs.TrySetResult(r);
            }

            var row = new DockPanel { LastChildFill = false };
            var danger = new Button
            {
                Content = dangerText, Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                FontSize = 14, Padding = new Thickness(16, 8, 16, 8),
            };
            danger.Click += (s, e) => Done(2);
            DockPanel.SetDock(danger, Dock.Left);
            row.Children.Add(danger);
            var cancel = new Button { Content = cancelText, FontSize = 14, Padding = new Thickness(20, 8, 20, 8) };
            cancel.Click += (s, e) => Done(0);
            DockPanel.SetDock(cancel, Dock.Right);
            row.Children.Add(cancel);
            var main = new Button
            {
                Content = mainText, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                FontSize = 14, Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(0, 0, 8, 0),
            };
            main.Click += (s, e) => Done(1);
            DockPanel.SetDock(main, Dock.Right);
            row.Children.Add(main);
            sp.Children.Add(row);
            card.Child = sp;
            overlay.Children.Add(card);

            overlay.MouseLeftButtonDown += (s, e) => { if (!e.Handled) Done(0); };
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;
            rootGrid.Children.Add(overlay);
            unhook = HookKeys(owner, overlay, ok => Done(ok ? 1 : 0));
            overlay.Dispatcher.BeginInvoke(new System.Action(() => main.Focus()), System.Windows.Threading.DispatcherPriority.Loaded);
            return tcs.Task;
        }

        // Variant of ShowAsync with an extra ToggleSwitch between the body and the buttons.
        private static Task<ToggleConfirm> ShowAsync(Window owner, string title, string body,
            string okText, string cancelText, string toggleLabel, bool toggleInitial)
        {
            var tcs = new TaskCompletionSource<ToggleConfirm>();

            var rootGrid = owner != null ? owner.Content as Grid : null;
            if (rootGrid == null) { tcs.SetResult(new ToggleConfirm { Ok = false, Toggle = toggleInitial }); return tcs.Task; }

            var overlay = new Grid { Background = Services.Theme.OverlayBackdrop };
            if (rootGrid.RowDefinitions.Count > 1) Grid.SetRowSpan(overlay, rootGrid.RowDefinitions.Count);
            if (rootGrid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(overlay, rootGrid.ColumnDefinitions.Count);
            Panel.SetZIndex(overlay, 1000);

            var card = new Border
            {
                Width = 400,
                Background = Services.Theme.OverlayCardBg,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 20, 22, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35 },
            };

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = Services.Theme.TextPrimary,
            });
            sp.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
                Foreground = Services.Theme.OverlayBody,
            });

            var toggle = new Wpf.Ui.Controls.ToggleSwitch
            {
                Content = toggleLabel,
                IsChecked = toggleInitial,
                Margin = new Thickness(0, 0, 0, 16),
            };
            sp.Children.Add(toggle);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            System.Action unhook = null;
            void Close(bool ok)
            {
                unhook?.Invoke();
                try { rootGrid.Children.Remove(overlay); } catch { }
                tcs.TrySetResult(new ToggleConfirm { Ok = ok, Toggle = toggle.IsChecked == true });
            }

            var okBtn = new Button
            {
                Content = okText,
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                FontSize = 14,
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
            };
            okBtn.Click += (s, e) => Close(true);
            btnRow.Children.Add(okBtn);

            if (cancelText != null)
            {
                var cancelBtn = new Button
                {
                    Content = cancelText,
                    FontSize = 14,
                    Padding = new Thickness(20, 8, 20, 8),
                };
                cancelBtn.Click += (s, e) => Close(false);
                btnRow.Children.Add(cancelBtn);
            }

            sp.Children.Add(btnRow);
            card.Child = sp;
            overlay.Children.Add(card);

            overlay.MouseLeftButtonDown += (s, e) => { if (!e.Handled) Close(false); };
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;

            rootGrid.Children.Add(overlay);
            unhook = HookKeys(owner, overlay, Close);
            return tcs.Task;
        }

        // Enter = the main button, Esc = cancel — in every in-window confirmation (2026-11-13:
        // «подтверждения должны работать и при нажатии Enter»). Taken at the window before the
        // control under the dimmed backdrop sees the key; only the topmost overlay of a window
        // answers (a password prompt over a dialog). A multi-line text box keeps its Enter.
        private static readonly List<KeyValuePair<Window, Grid>> _open = new List<KeyValuePair<Window, Grid>>();

        private static System.Action HookKeys(Window owner, Grid overlay, System.Action<bool> close)
        {
            if (owner == null) return () => { };
            var entry = new KeyValuePair<Window, Grid>(owner, overlay);
            _open.Add(entry);
            KeyEventHandler handler = (s, e) =>
            {
                var top = _open.LastOrDefault(p => p.Key == owner);
                if (top.Value != overlay) return;
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    close(false);
                }
                else if (e.Key == Key.Enter)
                {
                    if (Keyboard.FocusedElement is System.Windows.Controls.TextBox tb && tb.AcceptsReturn) return;
                    e.Handled = true;
                    close(true);
                }
            };
            owner.PreviewKeyDown += handler;
            bool done = false;
            return () =>
            {
                if (done) return;
                done = true;
                owner.PreviewKeyDown -= handler;
                _open.Remove(entry);
            };
        }
    }
}
