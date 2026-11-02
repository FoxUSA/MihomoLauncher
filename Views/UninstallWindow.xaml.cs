using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The uninstaller progress window (shown when MihomoLauncher.exe runs with --uninstall, which
    // uninstall.exe launches). Replaces the translit cmd console: a real modal with a per-step
    // checklist + progress bar showing exactly what was stopped / removed.
    public partial class UninstallWindow : Window
    {
        public class StepVM : INotifyPropertyChanged
        {
            public string Title { get; set; }
            private string _glyph = "•";
            private string _detail = "";
            private Brush _brush = Theme.TextMuted;

            public string Glyph { get => _glyph; set { _glyph = value; OnChanged(nameof(Glyph)); } }
            public string Detail { get => _detail; set { _detail = value; OnChanged(nameof(Detail)); } }
            public Brush GlyphBrush { get => _brush; set { _brush = value; OnChanged(nameof(GlyphBrush)); } }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            public void Running() { Glyph = "…"; GlyphBrush = Theme.AccentBlue; }
            public void Done(string detail) { Glyph = "✓"; GlyphBrush = Theme.AccentGreen; Detail = detail; }
            public void Fail(string detail) { Glyph = "✕"; GlyphBrush = Theme.AccentRed; Detail = detail; }
        }

        private readonly ObservableCollection<StepVM> _steps = new ObservableCollection<StepVM>();
        private bool _running;
        private bool _done;

        public UninstallWindow()
        {
            InitializeComponent();
            StepsList.ItemsSource = _steps;
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            _running = true;
            BtnRun.IsEnabled = false;
            BtnClose.IsEnabled = false;   // can't bail out mid-removal

            var sClose = Add(Loc.T("Uni_StepClose"));
            var sMihomo = Add(Loc.T("Uni_StepMihomo"));
            var sAuto = Add(Loc.T("Uni_StepAutostart"));
            var sTask = Add(Loc.T("Uni_StepTask"));
            var sData = Add(Loc.T("Uni_StepData"));

            int total = _steps.Count, done = 0;
            void Tick() { done++; Bar.Value = (double)done / total * 100; }

            try
            {
                sClose.Running();
                sClose.Done(await UninstallService.CloseLauncherAsync(CancellationToken.None));
                Tick();

                sMihomo.Running();
                sMihomo.Done(await UninstallService.StopMihomoAsync(CancellationToken.None));
                Tick();

                sAuto.Running();
                sAuto.Done(UninstallService.RemoveAutostart());
                Tick();

                sTask.Running();
                var taskRes = await UninstallService.DeleteTaskAsync(CancellationToken.None);
                if (taskRes.StartsWith(Loc.T("Uni_TaskFailPrefix"))) sTask.Fail(taskRes); else sTask.Done(taskRes);
                Tick();

                sData.Running();
                var d = UninstallService.CleanServiceData();
                if (d.StartsWith(Loc.T("Uni_ErrorPrefix"))) sData.Fail(d); else sData.Done(d);
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("UninstallWindow", ex);
            }

            Bar.Value = 100;
            _done = true;
            DonePanel.Visibility = Visibility.Visible;
            BtnClose.IsEnabled = true;
            BtnClose.Content = Loc.T("Uni_BtnDoneClose");
        }

        private StepVM Add(string title)
        {
            var s = new StepVM { Title = title };
            _steps.Add(s);
            return s;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Block the X while uninstalling so steps aren't left half-done.
            if (_running && !_done) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
