using System;
using System.Windows;
using System.Windows.Input;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // Stand-alone password prompt for when there is no main window to draw an overlay in: at
    // startup when the data was carried here from another place, and in the uninstaller.
    public partial class UnlockWindow : FluentWindow
    {
        private readonly AppConfig _cfg;
        public bool Opened { get; private set; }

        public UnlockWindow(AppConfig cfg, string reason, string cancelText = null)
        {
            InitializeComponent();
            _cfg = cfg;
            LblReason.Text = reason ?? "";
            if (!string.IsNullOrEmpty(cancelText)) BtnCancel.Content = cancelText;
            Loaded += (s, e) =>
            {
                UiHelpers.FitHeightToContent(this);
                TxtPassword.Focus();
                try { Activate(); } catch { }
            };
        }

        private void OnPasswordKey(object sender, KeyEventArgs e)
        {
            LblError.Visibility = Visibility.Collapsed;
            if (e.Key == Key.Enter) { e.Handled = true; OnOk(sender, null); }
            else if (e.Key == Key.Escape) { e.Handled = true; OnCancel(sender, null); }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (!AccessLock.TryOpen(_cfg, TxtPassword.Password))
            {
                LblError.Visibility = Visibility.Visible;
                TxtPassword.Clear();
                TxtPassword.Focus();
                UiHelpers.FitHeightToContent(this);
                return;
            }
            Opened = true;
            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        }
    }
}
