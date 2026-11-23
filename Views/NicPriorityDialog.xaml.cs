using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // «Приоритет сетевых адаптеров» — which card Windows itself prefers (interface metrics, applied
    // by the SYSTEM helper, NicMetrics). Was Settings' «Сеть» tab until 2026-11-23; now its own
    // modal dialog, opened from the last item of a tile's network-card menu (MainWindow.NicPlate).
    //
    // The behaviour is the tab's: nothing touches Windows until «Применить» (2026-11-15: a move —
    // drag or ▲▼ — or «По умолчанию» only marks the change); nothing changed = nothing applied
    // («Применить» stays off, as Settings' «Сохранить» did and left the cards alone). After a
    // successful apply the list is read back from Windows and the window stays open.
    public partial class NicPriorityDialog : FluentWindow
    {
        public sealed class NicRow : INotifyPropertyChanged
        {
            private int _number;
            public int Number { get { return _number; } set { _number = value; Notify(nameof(Number)); } }
            private bool _canUp, _canDown;
            public bool CanUp { get { return _canUp; } set { _canUp = value; Notify(nameof(CanUp)); } }
            public bool CanDown { get { return _canDown; } set { _canDown = value; Notify(nameof(CanDown)); } }
            public string Name { get; set; } = "";
            public string State { get; set; } = "";
            public string MetricLabel { get; set; } = "";
            public bool IsAuto { get; set; } = true;   // IPv4 metric left to Windows
            public event PropertyChangedEventHandler PropertyChanged;
            private void Notify(string p) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p)); }
        }

        private readonly AppConfig _config;   // only for the access password
        private readonly ObservableCollection<NicRow> _nicRows = new ObservableCollection<NicRow>();
        private bool _nicBusy, _closed;
        private List<string> _nicOrigOrder = new List<string>();
        private bool _nicAutoPending;

        // True once Windows took a new order (the caller redraws the tiles' card chips).
        public bool Applied { get; private set; }

        public NicPriorityDialog(AppConfig config)
        {
            InitializeComponent();
            _config = config;
            GridNics.ItemsSource = _nicRows;
            // Order = priority: dragging only renumbers; «Применить» touches Windows.
            new GridRowDrag<NicRow>(GridNics, _nicRows, r => false, (order, moved) => OnNicOrderChanged());
            RefreshNicRows();
            Loaded += (s, e) => UiHelpers.FitHeightToContent(this);
            Closed += (s, e) => _closed = true;
        }

        private bool NicOrderChanged =>
            !_nicRows.Select(r => r.Name).SequenceEqual(_nicOrigOrder, StringComparer.OrdinalIgnoreCase);
        private bool NicPending => _nicRows.Count > 0 && (_nicAutoPending || NicOrderChanged);

        private void OnNicUp(object sender, RoutedEventArgs e) { MoveNic(sender, -1); }
        private void OnNicDown(object sender, RoutedEventArgs e) { MoveNic(sender, +1); }

        private void MoveNic(object sender, int delta)
        {
            if (_nicBusy) return;
            var row = (sender as FrameworkElement)?.DataContext as NicRow;
            int i = row == null ? -1 : _nicRows.IndexOf(row), j = i + delta;
            if (i < 0 || j < 0 || j >= _nicRows.Count) return;
            _nicRows.Move(i, j);
            OnNicOrderChanged();
        }

        private void RenumberNics()
        {
            for (int i = 0; i < _nicRows.Count; i++)
            {
                _nicRows[i].Number = i + 1;
                _nicRows[i].CanUp = i > 0;
                _nicRows[i].CanDown = i < _nicRows.Count - 1;
            }
        }

        private void OnNicOrderChanged()
        {
            RenumberNics();
            _nicAutoPending = false;   // an order picked by hand replaces a pending «По умолчанию»
            UpdateNicPending();
        }

        // «Применить» is on only while a change waits; the status line says which.
        private void UpdateNicPending()
        {
            bool pending = NicPending;
            BtnApply.IsEnabled = pending && !_nicBusy;
            SetNicStatus(!pending ? "" : Loc.T(_nicAutoPending ? "Set_NicDefaultHint" : "Set_NicMovedHint"));
        }

        // The status line takes room only while it says something; the window follows its height.
        private void SetNicStatus(string text)
        {
            LblNicStatus.Text = text ?? "";
            var v = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            if (LblNicStatus.Visibility == v) return;
            LblNicStatus.Visibility = v;
            if (IsLoaded) Dispatcher.BeginInvoke(new Action(() => { if (!_closed) UiHelpers.FitHeightToContent(this); }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // The machine's physical cards, in the order Windows prefers them right now (lowest IPv4
        // metric first), each with its metric and whether it is automatic.
        private void RefreshNicRows()
        {
            _nicRows.Clear();
            var metrics = NicMetrics.Read();
            var rows = new List<KeyValuePair<long, NicRow>>();
            // A card switched off in Windows has no metric to order by — not listed here.
            foreach (var c in NetCards.List().Where(x => !x.Disabled))
            {
                NicMetrics.Current m;
                metrics.TryGetValue(c.Name, out m);
                var state = Loc.T(c.Wireless ? "Main_NicWifi" : "Main_NicWired") + " · "
                          + NetCards.StateText(c)
                          + (string.IsNullOrEmpty(c.Description) ? "" : " · " + c.Description);
                var label = m == null || !m.Has4 ? "—"
                          : m.Auto4 ? Loc.T("Set_NicAuto", m.Metric4) : Loc.T("Set_NicManual", m.Metric4);
                // Windows decides by gateway + card metric — a gateway set by hand is shown, since
                // it can outweigh the card order on its own.
                if (m != null && m.HasGw4 && m.Gw4 > 0) label = Loc.T("Set_NicWithGw", label, m.Gw4, m.Total4);
                rows.Add(new KeyValuePair<long, NicRow>(m != null && m.Has4 ? m.Total4 : long.MaxValue,
                    new NicRow { Name = c.Name, State = state, MetricLabel = label, IsAuto = m == null || !m.Has4 || m.Auto4 }));
            }
            foreach (var r in rows.OrderBy(x => x.Key)) _nicRows.Add(r.Value);
            RenumberNics();
            _nicOrigOrder = _nicRows.Select(r => r.Name).ToList();
            _nicAutoPending = false;
            BtnNicDefault.IsEnabled = _nicRows.Count > 0 && !_nicBusy;
            UpdateNicPending();
        }

        // «По умолчанию» only marks it; «Применить» hands it to Windows. Already all automatic →
        // nothing to change: just back to the order Windows has.
        private void OnNicDefault(object sender, RoutedEventArgs e)
        {
            if (_nicBusy || _nicRows.Count == 0) return;
            if (_nicRows.All(r => r.IsAuto)) { RefreshNicRows(); return; }
            _nicAutoPending = true;
            UpdateNicPending();
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (_nicBusy || !NicPending) return;   // nothing changed: Windows is left alone
            if (await ApplyNicAsync(_nicAutoPending).ConfigureAwait(true) && !_closed)
            {
                Applied = true;
                RefreshNicRows();   // what Windows has now (also clears «pending»)
                SetNicStatus(Loc.T("Set_NicDone"));
            }
        }

        // The order on screen → metrics 10, 20, 30… (or all automatic), applied by the SYSTEM
        // helper (NicMetrics). False = not applied (password refused, no helper, helper failed).
        private async Task<bool> ApplyNicAsync(bool auto)
        {
            if (_nicBusy || _nicRows.Count == 0) return false;
            // Which card the whole machine goes out through is routing — behind the access password.
            if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return false;
            if (!MihomoSetup.IsFirewallTaskInstalled())
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Set_NicPrioTitle"), Loc.T("Set_NicNoHelper")).ConfigureAwait(true);
                return false;
            }
            _nicBusy = true;
            BtnNicDefault.IsEnabled = false;
            BtnApply.IsEnabled = false;
            SetNicStatus(Loc.T("Set_NicApplying"));
            string failure = null;
            try
            {
                var plan = _nicRows.Select((r, i) => new KeyValuePair<string, uint>(r.Name, auto ? 0u : (uint)((i + 1) * 10))).ToList();
                Log.Info("Сеть: приоритет карт Windows — " + (auto ? "по умолчанию (автоматическая метрика)"
                         : string.Join(", ", plan.Select(p => p.Key + "=" + p.Value))) + ".");
                var id = NicMetrics.WritePlan(plan);
                bool sent = await Task.Run(() => MihomoSetup.RunFirewallTask()).ConfigureAwait(true);
                if (!sent) failure = Loc.T("Set_NicNoHelper");
                else
                {
                    string res = null;
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    while (DateTime.UtcNow < deadline && res == null)
                    {
                        await Task.Delay(400).ConfigureAwait(true);
                        res = NicMetrics.ReadResult(id);
                    }
                    if (res == null) failure = Loc.T("Set_NicTimeout");
                    else if (res.Length > 0) failure = res;
                }
            }
            catch (Exception ex) { failure = ex.Message; Log.Error("nic-metric-apply-ui", ex); }
            finally
            {
                _nicBusy = false;
                if (!_closed) { BtnNicDefault.IsEnabled = _nicRows.Count > 0; UpdateNicPending(); }
            }

            if (_closed) return false;
            if (failure == null) return true;
            // The order on screen stays as picked, still pending — fix the cause, press again.
            SetNicStatus(Loc.T("Set_NicFailed", failure));
            Log.Info("Сеть: метрики не применены — " + failure);
            await OverlayDialog.MessageAsync(this, Loc.T("Set_NicPrioTitle"), Loc.T("Set_NicFailed", failure)).ConfigureAwait(true);
            return false;
        }

        // Closing while Windows is being asked is fine: the answer is dropped (_closed), the
        // helper finishes the metrics on its own — as Settings' tab behaved.
        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
