using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The in-window password prompt (an OverlayDialog card) in front of every protected action:
    // the VPN list, the VPN plate on a tile, a rule's editor, dragging tiles. With no password
    // set — or already opened within the last minutes — it lets the action through at once.
    public static class LockPrompt
    {
        public static async Task<bool> RequireAsync(Window owner, AppConfig cfg)
        {
            if (AccessLock.IsOpen(cfg)) { AccessLock.Touch(); return true; }
            bool wrong = false;
            while (true)
            {
                var pb = new PasswordBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                sp.Children.Add(new TextBlock
                {
                    Text = Loc.T("Lock_Ask"),
                    FontSize = 13.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Theme.OverlayBody,
                    Margin = new Thickness(0, 0, 0, 10),
                });
                sp.Children.Add(pb);
                if (wrong)
                    sp.Children.Add(new TextBlock
                    {
                        Text = Loc.T("Lock_Wrong"),
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
                        Margin = new Thickness(0, 6, 0, 0),
                    });
                var okText = Loc.T("Lock_BtnOpen");
                pb.Loaded += (s, e) => pb.Focus();
                pb.KeyDown += (s, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    e.Handled = true;
                    var btn = FindButton(CardOf(pb), okText);
                    btn?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                };
                var ok = await OverlayDialog.ConfirmContentAsync(owner, Loc.T("Lock_Title"), sp, okText,
                    Loc.T("Set_BtnCancel"), 380).ConfigureAwait(true);
                if (!ok) return false;
                if (AccessLock.TryOpen(cfg, pb.Password)) return true;
                wrong = true;
            }
        }

        // The overlay card the box sits in (the outermost Border on the way up).
        private static DependencyObject CardOf(DependencyObject d)
        {
            DependencyObject card = null;
            while (d != null)
            {
                if (d is Border) card = d;
                if (d is Window) break;
                d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
                if (d is Grid g && g.Parent is Window) break;
            }
            return card;
        }

        private static Button FindButton(DependencyObject root, string content)
        {
            if (root == null) return null;
            if (root is Button b && Equals(b.Content as string, content)) return b;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FindButton(VisualTreeHelper.GetChild(root, i), content);
                if (r != null) return r;
            }
            return null;
        }
    }
}
