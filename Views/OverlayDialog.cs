using System.Threading.Tasks;
using System.Windows;
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

        private static Task<bool> ShowAsync(Window owner, string title, string body, string okText, string cancelText,
            Wpf.Ui.Controls.ControlAppearance okAppearance = Wpf.Ui.Controls.ControlAppearance.Primary)
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
                Margin = new Thickness(0, 0, 0, 18),
                Foreground = Services.Theme.OverlayBody,
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            void Close(bool result)
            {
                try { rootGrid.Children.Remove(overlay); } catch { }
                tcs.TrySetResult(result);
            }

            var okBtn = new Button
            {
                Content = okText,
                Appearance = okAppearance,
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

            // Click on the dimmed backdrop dismisses (cancel/close); clicks on the
            // card itself are swallowed so they don't bubble to the backdrop.
            overlay.MouseLeftButtonDown += (s, e) => { if (!e.Handled) Close(false); };
            card.MouseLeftButtonDown += (s, e) => e.Handled = true;

            rootGrid.Children.Add(overlay);
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

            void Close(bool ok)
            {
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
            return tcs.Task;
        }
    }
}
