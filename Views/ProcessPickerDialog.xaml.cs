using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // Pick a running program so the user doesn't have to hunt for its .exe path.
    public partial class ProcessPickerDialog : FluentWindow
    {
        public string SelectedPath { get; private set; }
        public string SelectedName { get; private set; }

        private readonly ObservableCollection<ProcRow> _rows = new ObservableCollection<ProcRow>();
        private List<ProcRow> _all = new List<ProcRow>();

        public ProcessPickerDialog()
        {
            InitializeComponent();
            LstProcs.ItemsSource = _rows;
            Services.UiHelpers.FillLastGridColumn(LstProcs);
            Loaded += (s, e) => LoadProcesses();
        }

        private void LoadProcesses()
        {
            var byPath = new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                string path = null;
                try { path = p.MainModule != null ? p.MainModule.FileName : null; }
                catch { /* protected/elevated process — skip */ }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                // Skip our own process and obvious system noise.
                bool hasWindow = false;
                string title = "";
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; title = p.MainWindowTitle ?? ""; }
                catch { }

                if (!byPath.TryGetValue(path, out var row))
                {
                    row = new ProcRow
                    {
                        ProcessName = SafeName(p),
                        Path = path,
                        Display = !string.IsNullOrWhiteSpace(title) ? title : SafeName(p),
                        HasWindow = hasWindow,
                    };
                    byPath[path] = row;
                }
                else if (hasWindow && !row.HasWindow)
                {
                    row.HasWindow = true;
                    if (!string.IsNullOrWhiteSpace(title)) row.Display = title;
                }
            }

            _all = byPath.Values
                         .OrderByDescending(r => r.HasWindow)
                         .ThenBy(r => r.Display, StringComparer.CurrentCultureIgnoreCase)
                         .ToList();
            ApplyFilter();
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; } catch { return "?"; }
        }

        private void ApplyFilter()
        {
            // The CheckBox's IsChecked="True" fires Checked during InitializeComponent,
            // before controls declared later in XAML exist — bail until everything is up.
            if (TxtSearch == null || ChkWindowedOnly == null || LstProcs == null || LblHint == null) return;

            var q = (TxtSearch.Text ?? "").Trim();
            bool windowedOnly = ChkWindowedOnly.IsChecked == true;

            IEnumerable<ProcRow> items = _all;
            if (windowedOnly) items = items.Where(r => r.HasWindow);
            if (q.Length > 0)
                items = items.Where(r =>
                    (r.Display ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.ProcessName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);

            _rows.Clear();
            foreach (var r in items) _rows.Add(r);
            if (_rows.Count > 0) LstProcs.SelectedIndex = 0;
            LblHint.Text = Loc.T("Dlg_ProcessCountFound", _rows.Count);
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();
        private void OnFilterToggled(object sender, RoutedEventArgs e) => ApplyFilter();
        private void OnReload(object sender, RoutedEventArgs e) => LoadProcesses();

        private void OnPick(object sender, RoutedEventArgs e)
        {
            if (!(LstProcs.SelectedItem is ProcRow row)) return;
            SelectedPath = row.Path;
            SelectedName = string.IsNullOrWhiteSpace(row.Display) ? row.ProcessName : row.Display;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public class ProcRow
        {
            public string Display { get; set; }
            public string ProcessName { get; set; }
            public string Path { get; set; }
            public bool HasWindow { get; set; }
        }
    }
}
