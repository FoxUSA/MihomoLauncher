using System;
using System.Windows;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The title-bar theme button (2026-11-24), left of the gear: the icon is the theme on screen
    // (moon — dark, sun — light); a click switches to the other one. The choice goes to
    // config.xml (UiTheme) and applies at once (Services/Theme).
    public partial class MainWindow
    {
        private void InitThemeButton()
        {
            UpdateThemeButton();
            Theme.Changed += OnThemeChanged;
        }

        private void DetachThemeButton()
        {
            try { Theme.Changed -= OnThemeChanged; } catch { }
        }

        private void UpdateThemeButton()
        {
            try
            {
                IcoTheme.Symbol = Theme.IsLight ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
                                                : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24;
            }
            catch { }
        }

        // One click = the other theme, at once, and remembered (2026-11-24: «без меню»). Until the
        // first click the theme is Windows' and follows it.
        private void OnThemeButton(object sender, RoutedEventArgs e)
        {
            PickTheme(Theme.IsLight ? ThemeMode.Dark : ThemeMode.Light);
        }

        private void PickTheme(ThemeMode mode)
        {
            _config.UiTheme = Theme.ModeToString(mode);
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("theme-save", ex); }
            try { Theme.SetMode(mode); } catch (Exception ex) { Log.Error("theme-apply", ex); }
            UpdateThemeButton();   // also when the mode changed but the colours did not
        }

        private void OnThemeChanged()
        {
            // Brushes and DynamicResources have already repainted; rebuilt here are the parts
            // made in code from colours mixed out of the palette (tile shadows, plates).
            try
            {
                UpdateThemeButton();
                RebuildTiles();
                RenderStats();
            }
            catch (Exception ex) { Log.Error("theme-changed-main", ex); }
        }
    }
}
