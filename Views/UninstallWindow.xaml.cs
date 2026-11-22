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
    // The uninstaller window (the launcher run with --uninstall, which uninstall.exe launches).
    // Opens with what is actually there (UninstallService.Scan): app / engine running, autostart,
    // service + tasks + firewall rules, out-of-folder data, and the folder to delete by hand
    // (2026-11-19: «пиши что уже есть в системе, а то не понятно что мы удалять будем»). The same
    // rows then turn into the per-step checklist while «Удалить» runs.
    public partial class UninstallWindow : Wpf.Ui.Controls.FluentWindow
    {
        public class StepVM : INotifyPropertyChanged
        {
            public string Title { get; set; }
            private string _glyph = "•";
            private string _detail = "";
            private Brush _brush = Theme.TextMuted;
            private Brush _detailBrush = Theme.TextMuted;

            public string Glyph { get => _glyph; set { _glyph = value; OnChanged(nameof(Glyph)); } }
            public string Detail { get => _detail; set { _detail = value; OnChanged(nameof(Detail)); } }
            public Brush GlyphBrush { get => _brush; set { _brush = value; OnChanged(nameof(GlyphBrush)); } }
            public Brush DetailBrush { get => _detailBrush; set { _detailBrush = value; OnChanged(nameof(DetailBrush)); } }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            // Found: something to remove (bright) or nothing (muted dash).
            public void Found(bool present, string detail)
            {
                Glyph = present ? "•" : "–";
                GlyphBrush = present ? Theme.AccentBlue : Theme.TextMuted;
                DetailBrush = present ? Theme.TextPrimary : Theme.TextMuted;
                Detail = detail;
            }
            public void Running() { Glyph = "…"; GlyphBrush = Theme.AccentBlue; }
            public void Done(string detail) { Glyph = "✓"; GlyphBrush = Theme.AccentGreen; DetailBrush = Theme.TextMuted; Detail = detail; }
            public void Fail(string detail) { Glyph = "✕"; GlyphBrush = Theme.AccentRed; DetailBrush = Theme.AccentRed; Detail = detail; }
        }

        private readonly ObservableCollection<StepVM> _steps = new ObservableCollection<StepVM>();
        private readonly StepVM _sApp, _sEngine, _sAuto, _sService, _sData;
        private bool _running;
        private bool _done;

        public UninstallWindow()
        {
            InitializeComponent();
            StepsList.ItemsSource = _steps;
            _sApp = Add(Loc.T("Uni_ItemApp"));
            _sEngine = Add(Loc.T("Uni_ItemEngine"));
            _sAuto = Add(Loc.T("Uni_ItemAutostart"));
            _sService = Add(Loc.T("Uni_ItemService"));
            _sData = Add(Loc.T("Uni_ItemData"));
            foreach (var s in _steps) s.Detail = Loc.T("Uni_Scanning");
            TxtFolder.Text = VersionInfo.InstallRoot;
            Loaded += async (s, e) => { FitToContent(); await ScanAsync(); };
        }

        private async Task ScanAsync()
        {
            UninstallService.Inventory inv;
            try { inv = await Task.Run(() => UninstallService.Scan()); }
            catch (Exception ex) { Log.Error("UninstallWindow.scan", ex); inv = new UninstallService.Inventory(); }
            if (_running) return;

            _sApp.Found(inv.AppRunning, Loc.T(inv.AppRunning ? "Uni_FoundAppRunning" : "Uni_FoundAppNot"));
            _sEngine.Found(inv.EngineRunning, Loc.T(inv.EngineRunning ? "Uni_FoundEngineRunning" : "Uni_FoundEngineNot"));
            _sAuto.Found(inv.Autostart, Loc.T(inv.Autostart ? "Uni_FoundOn" : "Uni_FoundNone"));

            var svc = new List<string>();
            if (inv.Service) svc.Add(Loc.T("Uni_FoundService"));
            if (inv.Tasks > 0) svc.Add(Loc.T("Uni_FoundTasks", inv.Tasks));
            if (inv.FirewallRules > 0) svc.Add(Loc.T("Uni_FoundFw", inv.FirewallRules));
            _sService.Found(svc.Count > 0, svc.Count > 0 ? string.Join(" · ", svc) : Loc.T("Uni_FoundNone"));

            _sData.Found(inv.DataDirs.Count > 0,
                inv.DataDirs.Count > 0 ? string.Join(", ", inv.DataDirs) : Loc.T("Uni_FoundNone"));

            BtnRun.IsEnabled = true;
            Refit();
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            _running = true;
            BtnRun.IsEnabled = false;
            BtnClose.IsEnabled = false;   // can't bail out mid-removal
            Bar2.Visibility = Visibility.Visible;
            Refit();

            int total = _steps.Count, done = 0;
            void Tick() { done++; Bar2.Value = (double)done / total * 100; }

            try
            {
                _sApp.Running();
                _sApp.Done(await UninstallService.CloseLauncherAsync(CancellationToken.None));
                Tick();

                _sEngine.Running();
                _sEngine.Done(await UninstallService.StopMihomoAsync(CancellationToken.None));
                Tick();

                _sAuto.Running();
                _sAuto.Done(UninstallService.RemoveAutostart());
                Tick();

                _sService.Running();
                var taskRes = await UninstallService.DeleteTaskAsync(CancellationToken.None);
                if (taskRes.StartsWith(Loc.T("Uni_TaskFailPrefix"))) _sService.Fail(taskRes); else _sService.Done(taskRes);
                Tick();

                _sData.Running();
                var d = UninstallService.CleanServiceData();
                if (d.StartsWith(Loc.T("Uni_ErrorPrefix"))) _sData.Fail(d); else _sData.Done(d);
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("UninstallWindow", ex);
            }

            Bar2.Value = 100;
            _done = true;
            DonePanel.Visibility = Visibility.Visible;
            BtnClose.IsEnabled = true;
            BtnClose.Content = Loc.T("Uni_BtnDoneClose");
            Refit();
        }

        // The window is as tall as its content; content that appears later (progress bar, the
        // «Готово» panel, a wrapped list line) grows it.
        private void Refit()
        {
            Dispatcher.BeginInvoke(new Action(FitToContent), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // As SpeedWindow.FitToContent: measured at infinite height, so the window can also GROW.
        private void FitToContent()
        {
            try
            {
                var root = Content as FrameworkElement;
                if (root == null) return;
                double width = root.ActualWidth > 0 ? root.ActualWidth : (ActualWidth > 0 ? ActualWidth : Width);
                root.Measure(new Size(width, double.PositiveInfinity));
                double h = Math.Min(root.DesiredSize.Height + 12, SystemParameters.WorkArea.Height);
                if (h <= 120) return;
                SizeToContent = SizeToContent.Manual;
                Height = h;
            }
            catch { }
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
