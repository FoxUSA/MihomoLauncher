using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The progress window shown by the freshly extracted launcher while it applies an update
    // (see UpdateApplier). Runs the whole swap and relaunches the installed launcher, then
    // shuts itself down. Closing is blocked while the swap is in progress so the install can't
    // be left half-applied.
    public partial class UpdaterWindow : Window
    {
        private readonly UpdateApplier.Options _opt;
        private bool _finished;

        public UpdaterWindow(UpdateApplier.Options opt)
        {
            InitializeComponent();
            _opt = opt;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await UpdateApplier.RunAsync(_opt, SetStatus, SetPercent, CancellationToken.None)
                                   .ConfigureAwait(true);
                _finished = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("UpdaterWindow", ex);
                _finished = true;
                SetStatus(Loc.T("Upd_InstallFailedStatus", ex.Message));
                SetPercent(-1);
                try
                {
                    MessageBox.Show(this,
                        Loc.T("Upd_InstallFailedDialog", ex.Message),
                        Loc.T("Upd_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                Application.Current.Shutdown();
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            // Don't let the user abort mid-swap and end up with a broken install.
            if (!_finished) e.Cancel = true;
        }

        private void SetStatus(string text)
        {
            if (Dispatcher.CheckAccess()) StatusText.Text = text;
            else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = text));
        }

        // percent < 0 → indeterminate (a waiting phase); 0..100 → determinate.
        private void SetPercent(int percent)
        {
            if (Dispatcher.CheckAccess()) Apply(percent);
            else Dispatcher.BeginInvoke(new Action(() => Apply(percent)));

            void Apply(int p)
            {
                if (p < 0)
                {
                    Bar.IsIndeterminate = true;
                    PercentText.Text = "";
                }
                else
                {
                    Bar.IsIndeterminate = false;
                    Bar.Value = p;
                    PercentText.Text = p + "%";
                }
            }
        }
    }
}
