using System.Windows;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    public partial class ConfigViewerDialog : FluentWindow
    {
        public ConfigViewerDialog(string path, string yaml)
        {
            InitializeComponent();
            LblPath.Text = path ?? "";
            TxtConfig.Text = yaml ?? "";
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(TxtConfig.Text ?? ""); } catch { }
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
