using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace MihomoLauncher.Views
{
    public partial class LinkTemplatePickerDialog : FluentWindow
    {
        public BlackMatrixService Selected { get; private set; }

        // Templates already in the rule (by ClashUrl) — hidden so the same one can't be added twice.
        public HashSet<string> ExcludeClashUrls { get; set; }

        private const int Columns = 3;

        private readonly List<BlackMatrixService> _all;

        public LinkTemplatePickerDialog()
        {
            InitializeComponent();
            _all = BlackMatrixCatalog.All().ToList();
            // Build the list once, on load — by then the caller has set ExcludeClashUrls, and
            // the list is virtualized so only the visible rows are realized.
            Loaded += (s, e) => { ApplyFilter(TxtSearch.Text ?? ""); TxtSearch.Focus(); };
        }

        private void OnSearchChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter(TxtSearch.Text ?? "");
        }

        private void ApplyFilter(string query)
        {
            query = (query ?? "").Trim();
            IEnumerable<BlackMatrixService> view = _all;
            if (ExcludeClashUrls != null && ExcludeClashUrls.Count > 0)
                view = view.Where(s => !ExcludeClashUrls.Contains(s.ClashUrl ?? ""));
            if (query.Length > 0)
                view = view.Where(s => s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            // Chunk into rows of 3 so the list shows 3 columns yet virtualizes by row.
            var list = view.ToList();
            var rows = new List<List<BlackMatrixService>>();
            for (int i = 0; i < list.Count; i += Columns)
                rows.Add(list.GetRange(i, Math.Min(Columns, list.Count - i)));
            ItemsHost.ItemsSource = rows;
        }

        private void OnPick(object sender, RoutedEventArgs e)
        {
            var svc = (sender as Button)?.Tag as BlackMatrixService;
            if (svc == null) return;
            Selected = svc;
            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
