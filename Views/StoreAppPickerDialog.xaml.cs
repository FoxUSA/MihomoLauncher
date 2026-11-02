using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // Pick an installed Store/MSIX app (ChatGPT, Claude…). Returns the stable AUMID so
    // the launcher can start it regardless of the versioned install path, plus the
    // current exe path (for the process-name rule + icon).
    public partial class StoreAppPickerDialog : FluentWindow
    {
        public string SelectedName { get; private set; }
        public string SelectedAumid { get; private set; }
        public string SelectedExe { get; private set; }

        private readonly ObservableCollection<StoreRow> _rows = new ObservableCollection<StoreRow>();
        private List<StoreRow> _all = new List<StoreRow>();

        public StoreAppPickerDialog()
        {
            InitializeComponent();
            LstApps.ItemsSource = _rows;
            UiHelpers.FillLastGridColumn(LstApps);
            Loaded += async (s, e) => await LoadAppsAsync();
        }

        private async Task LoadAppsAsync()
        {
            LoadingPanel.Visibility = Visibility.Visible;
            try
            {
                var list = await Task.Run(() => StoreApps.Query());
                _all = list.Select(a => new StoreRow { Name = a.Name, Aumid = a.Aumid, Exe = a.Exe })
                           .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch { _all = new List<StoreRow>(); }
            LoadingPanel.Visibility = Visibility.Collapsed;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (TxtSearch == null || LstApps == null || LblHint == null) return;

            var q = (TxtSearch.Text ?? "").Trim();
            IEnumerable<StoreRow> items = _all;
            if (q.Length > 0)
                items = items.Where(r =>
                    (r.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Aumid ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            _rows.Clear();
            foreach (var r in items) _rows.Add(r);
            if (_rows.Count > 0) LstApps.SelectedIndex = 0;
            LblHint.Text = Loc.T("Dlg_StoreCountFound", _rows.Count);
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();
        private async void OnReload(object sender, RoutedEventArgs e) => await LoadAppsAsync();

        private void OnPick(object sender, RoutedEventArgs e)
        {
            if (!(LstApps.SelectedItem is StoreRow row)) return;
            SelectedName = row.Name;
            SelectedAumid = row.Aumid;
            SelectedExe = row.Exe;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public class StoreRow
        {
            public string Name { get; set; }
            public string Aumid { get; set; }
            public string Exe { get; set; }
        }
    }
}
