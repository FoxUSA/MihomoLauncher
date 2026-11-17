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

        // The TextBox's own (empty) scroller would swallow the wheel: scroll the outer one.
        private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
            e.Handled = true;
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
