using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MihomoLauncher.Services
{
    // Light or dark: «Как в Windows» (the default), or chosen in the title bar (2026-11-24).
    public enum ThemeMode { System, Light, Dark }

    // The names of MihomoLauncher's colours — and NO colour values. Each theme keeps all of its
    // colours in its own folder:
    //   Themes\Dark\Palette.xaml, Themes\Light\Palette.xaml — every colour, key "Theme.<name>";
    //   Themes\Dark\Styles.xaml,  Themes\Light\Styles.xaml  — styles for that theme only;
    //   Themes\Common\*.xaml — the shared styles: shapes and sizes, colours only by key.
    // Editing one theme's folder cannot change a pixel of the other theme.
    //
    // XAML: {DynamicResource Theme.CardBg} (Apply puts every palette key into the application's
    // resources, so a switch repaints on the spot). Code-behind: Theme.CardBg — one live brush per
    // name, recoloured in place on a switch, so whatever already holds it repaints too. Never
    // Freeze() these brushes and never put them into a Style/Template from code (a sealed style
    // freezes its values and the brush could no longer follow the theme). They belong to the UI
    // thread: Init runs first thing in App.OnStartup. Colours derived in code (new brushes from
    // Theme.XxxColor) don't follow a switch: rebuild them on Theme.Changed.
    public static class Theme
    {
        private static SolidColorBrush L() => new SolidColorBrush(Colors.Transparent);

        public const int MonogramCount = 8;

        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        public static bool IsLight { get; private set; }
        // Raised on the UI thread after a switch, once every brush has its new colour.
        public static event Action Changed;

        private static bool _applied;
        private static ResourceDictionary _themeStyles;

        public static ThemeMode ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "light": return ThemeMode.Light;
                case "dark": return ThemeMode.Dark;
                default: return ThemeMode.System;
            }
        }

        public static string ModeToString(ThemeMode m) =>
            m == ThemeMode.Light ? "light" : m == ThemeMode.Dark ? "dark" : "";

        // App.OnStartup, on the UI thread, before any window: the saved choice (config Theme).
        public static void Init(string savedMode)
        {
            Mode = ParseMode(savedMode);
            Apply(Resolve(Mode));
            try { SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged; } catch { }
        }

        public static void SetMode(ThemeMode mode)
        {
            Mode = mode;
            Apply(Resolve(mode));
        }

        private static bool Resolve(ThemeMode m) =>
            m == ThemeMode.Light || (m == ThemeMode.System && SystemUsesLight());

        // Windows' «Режим приложений по умолчанию». No value (old Windows) = light, as Windows.
        public static bool SystemUsesLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k?.GetValue("AppsUseLightTheme");
                    if (v is int i) return i != 0;
                }
            }
            catch { }
            return true;
        }

        // «Как в Windows» follows Windows live: the switch in Windows Settings repaints the app.
        private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Mode != ThemeMode.System) return;
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { if (Mode == ThemeMode.System) Apply(SystemUsesLight()); }
                catch (Exception ex) { try { Log.Error("theme-system", ex); } catch { } }
            }));
        }

        private static void Apply(bool light)
        {
            if (_applied && light == IsLight) return;
            var app = Application.Current;
            if (app == null) return;
            string name = light ? "Light" : "Dark";

            var pal = (ResourceDictionary)Application.LoadComponent(
                new Uri("/MihomoLauncher;component/Themes/" + name + "/Palette.xaml", UriKind.Relative));

            // 1. Every palette key straight into the application's resources: direct entries win
            //    over the merged library theme (WPF-UI's own keys are redirected the same way) and
            //    survive ApplicationThemeManager.Apply, which swaps that merged dictionary.
            foreach (object key in pal.Keys) app.Resources[key] = pal[key];

            // 2. The theme's own styles, merged last (after the common ones) so they win.
            try
            {
                var styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/" + name + "/Styles.xaml") };
                var md = app.Resources.MergedDictionaries;
                var old = _themeStyles;
                _themeStyles = styles;   // first: a throw while the tree re-resolves must not leave two
                if (old != null) md.Remove(old);
                md.Add(styles);
            }
            catch (Exception ex) { try { Log.Error("theme-styles", ex); } catch { } }

            // 3. The live brushes code-behind holds: recoloured in place.
            foreach (var f in typeof(Theme).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(SolidColorBrush)) continue;
                var live = (SolidColorBrush)f.GetValue(null);
                if (pal["Theme." + f.Name] is SolidColorBrush src)
                {
                    if (!live.IsFrozen) live.Color = src.Color;
                    else try { Log.Info("Тема: кисть Theme." + f.Name + " заморожена — не перекрашена"); } catch { }
                }
                else try { Log.Info("Тема " + name + ": нет ключа Theme." + f.Name); } catch { }
            }
            AccentColor = C(pal, "AccentColor");
            ShadowColor = C(pal, "ShadowColor");
            ShadowBase = C(pal, "ShadowBase");
            ChartUpColor = C(pal, "ChartUpColor");
            for (int i = 0; i < MonogramCount; i++) MonogramColors[i] = C(pal, "Monogram" + i);

            IsLight = light;
            _applied = true;

            // 4. The library: its controls, then the one accent (all four of its shades — with only
            //    the base it derives lighter, greyer fills and the app had two blues, 2026-11-22).
            //    Backdrop None: no window may be tinted by the wallpaper (no Mica).
            var libTheme = light ? Wpf.Ui.Appearance.ApplicationTheme.Light : Wpf.Ui.Appearance.ApplicationTheme.Dark;
            try { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(libTheme, Wpf.Ui.Controls.WindowBackdropType.None, false); }
            catch (Exception ex) { try { Log.Error("theme-lib", ex); } catch { } }
            try
            {
                var a = AccentColor;
                Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(a, a, a, a);
            }
            catch (Exception ex) { try { Log.Error("theme-accent", ex); } catch { } }

            // 5. The windows already open: their frame (DWM dark mode) follows, and everything
            //    drawn in OnRender with colours mixed from the palette is drawn again.
            foreach (Window w in app.Windows) { ApplyFrame(w); Redraw(w); }

            try { Changed?.Invoke(); } catch (Exception ex) { try { Log.Error("theme-changed", ex); } catch { } }
        }

        private static void Redraw(DependencyObject d)
        {
            if (d is UIElement u) u.InvalidateVisual();
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(d); } catch { }
            for (int i = 0; i < n; i++) Redraw(VisualTreeHelper.GetChild(d, i));
        }

        // The window in the theme: its frame / system menu (DWM dark mode) and its background.
        // The library paints a window with its own colour when it is shown (after Loaded) and on
        // every switch, over the XAML Background. So the background is watched: whenever it is
        // not the palette's Theme.WindowFace it is put back — by reference, so it follows the next
        // switch by itself. Called on every window's Loaded (App.OnStartup) and by Apply.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> _watched =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();
        private static readonly System.ComponentModel.DependencyPropertyDescriptor _bgProp =
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Control.BackgroundProperty, typeof(Window));

        public static void ApplyFrame(Window w)
        {
            try
            {
                if (IsLight) Wpf.Ui.Appearance.WindowBackgroundManager.RemoveDarkThemeFromWindow(w);
                else Wpf.Ui.Appearance.WindowBackgroundManager.ApplyDarkThemeToWindow(w);
            }
            catch { }
            SetFace(w);
            object dummy;
            if (_bgProp != null && !_watched.TryGetValue(w, out dummy))
            {
                _watched.Add(w, null);
                _bgProp.AddValueChanged(w, OnWindowBackgroundChanged);
                w.Closed += (s, e) => { try { _bgProp.RemoveValueChanged(w, OnWindowBackgroundChanged); } catch { } };
            }
        }

        private static void SetFace(Window w)
        {
            try { w.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Theme.WindowFace"); } catch { }
        }

        private static void OnWindowBackgroundChanged(object sender, EventArgs e)
        {
            var w = sender as Window;
            var face = Application.Current?.Resources["Theme.WindowFace"];
            if (w == null || face == null || ReferenceEquals(w.Background, face)) return;
            w.Dispatcher.BeginInvoke(new Action(() => { if (!ReferenceEquals(w.Background, Application.Current?.Resources["Theme.WindowFace"])) SetFace(w); }));
        }

        private static Color C(ResourceDictionary pal, string name)
        {
            if (pal["Theme." + name] is Color c) return c;
            try { Log.Info("Тема: нет цвета Theme." + name); } catch { }
            return Colors.Transparent;
        }

        // ===== The colour standard (2026-11-22: «у каждого окна свой набор цветов») =====
        // The values are in Themes\Dark\Palette.xaml and Themes\Light\Palette.xaml (key "Theme.<name>");
        // hex values quoted below are the dark theme's.
        // One neutral charcoal with a slight cool tint — the tiles' palette — so the coloured state
        // accents read against it. Every surface is OPAQUE: no window uses Mica (it tinted the same
        // window differently over different wallpapers), and the library's own semi-transparent
        // card / popup / window brushes are redirected to these (the end of each Palette.xaml).
        //   window WindowBg · raised CardBg · recessed well ListBg · floating OverlayCardBg with
        //   PickerBorder · outline CardBorder (1 px, radius 8).
        // One interactive blue (Accent) for hover borders, selection, focus, primary buttons,
        // checked boxes and switches; green / amber / red / violet / blue-grey mean STATE only.

        // ----- Surfaces -----
        public static readonly SolidColorBrush WindowBg = L();   // every window's background
        public static readonly SolidColorBrush CardBg = L();     // cards, tiles, panels, ui:Card
        public static readonly SolidColorBrush CardBgHover = L();
        public static readonly SolidColorBrush CardBorder = L(); // the outline of cards, wells, frames
        public static readonly SolidColorBrush CardBorderHover = L();

        // ----- The one accent -----
        public static Color AccentColor { get; private set; }
        public static readonly SolidColorBrush Accent = L();
        // The accent at 30 % over the well, made opaque: a selected table row, and the main
        // window's action buttons (they were #323D51 — the same colour within a hair).
        public static readonly SolidColorBrush AccentSoft = L();
        public static readonly SolidColorBrush AccentSoftHover = L();

        // ----- Tables (DataGrid, the code-drawn tables, the implicit ListView style) -----
        public static readonly SolidColorBrush ListBg = L();      // recessed well: tables, lists, viewers
        public static readonly SolidColorBrush SelectionBg = L(); // accent, semi-transparent (pills, chips)
        public static readonly SolidColorBrush TableHeaderBg = L();
        public static readonly SolidColorBrush TableLine = L();    // inner hairlines, quieter than the outline
        public static readonly SolidColorBrush TableRowAlt = L();
        public static readonly SolidColorBrush TableRowHover = L();
        public static readonly SolidColorBrush TableRowSelected = L(); // with TextPrimary, focused or not

        // ----- Buttons: default, primary (library + Accent), «add» (AccentGreen text), danger -----
        // Danger is muted on purpose: a rare action must not be the loudest thing in the window.
        public static readonly SolidColorBrush DangerBg = L();
        public static readonly SolidColorBrush DangerBgHover = L();

        // ----- Tiles -----
        public static readonly SolidColorBrush TileBg = L();
        public static readonly SolidColorBrush TileBgHover = L();
        public static readonly SolidColorBrush TileBorder = L();
        public static readonly SolidColorBrush TileBorderHover = L();
        public static readonly SolidColorBrush TileDisabledBg = L();
        public static readonly SolidColorBrush TileDisabledBorder = L();

        // Tile corner chips (gear / reconnect / disconnect): same shape as the VPN plate.
        public static readonly SolidColorBrush ChipBg = L();
        public static readonly SolidColorBrush ChipBorder = L();
        public static readonly SolidColorBrush ChipBgHover = L();

        // The VPN plate at the bottom of a tile, tinted by the tile's state.
        public static readonly SolidColorBrush PlateGreenBg = L();
        public static readonly SolidColorBrush PlateGreenBorder = L();
        public static readonly SolidColorBrush PlateGreenBgHover = L();
        public static readonly SolidColorBrush PlateGreenBorderHover = L();
        public static readonly SolidColorBrush PlateAmberBg = L();
        public static readonly SolidColorBrush PlateAmberBorder = L();
        public static readonly SolidColorBrush PlateAmberBgHover = L();
        public static readonly SolidColorBrush PlateAmberBorderHover = L();
        public static readonly SolidColorBrush PlateRedBg = L();
        public static readonly SolidColorBrush PlateRedBorder = L();
        public static readonly SolidColorBrush PlateRedBgHover = L();
        public static readonly SolidColorBrush PlateRedBorderHover = L();
        public static readonly SolidColorBrush PlateNeutralBg = L();
        public static readonly SolidColorBrush PlateNeutralBorder = L();

        // The VPN dropdown that opens from the plate.
        public static readonly SolidColorBrush PickerBorder = L();
        public static readonly SolidColorBrush PickerRowHover = L();
        public static readonly SolidColorBrush PickerRowCurrent = L();

        // ----- Text -----
        public static readonly SolidColorBrush TextPrimary = L();
        public static readonly SolidColorBrush TextSecondary = L();
        public static readonly SolidColorBrush TextMuted = L();
        public static readonly SolidColorBrush TextDisabled = L();

        // ----- Accents (tile sub-labels, status text) -----
        public static readonly SolidColorBrush AccentGreen = L();   // muted sage
        public static readonly SolidColorBrush AccentRed = L();
        // The log button's icon while tiles conflict: red, but dimmed — a hint, not an alarm.
        public static readonly SolidColorBrush ConflictIcon = L();
        public static readonly SolidColorBrush AccentAmber = L();
        // The "auto-tracking added something" notice: the tile's gear, the tile's frame and the
        // frame of the new rows in the rule editor. The plain AccentAmber again: once the whole
        // tile is framed, a brighter amber was no longer needed (2026-11-22).
        public static readonly SolidColorBrush NoticeAmber = L();
        // The tile's frame for the same notice: the same amber at 55 %. 1 px is 1.5 px at 150 %
        // scaling, and solid it read as a thick line («тоньше бордюр»); see GuardBorder for the
        // same trick on the chips.
        public static readonly SolidColorBrush NoticeTileBorder = L();
        public static readonly SolidColorBrush AccentBlue = L();
        public static readonly SolidColorBrush AccentGrey = L();

        // Speed classes (tile speed plate + its 24-hour chart, 2026-11-16): brighter than the
        // muted accents on purpose — the first, muted version could not be seen at all.
        public static readonly SolidColorBrush SpeedRed = L();     // < 1 Mbit/s
        public static readonly SolidColorBrush SpeedAmber = L();   // 1–10
        public static readonly SolidColorBrush SpeedGreen = L();   // 10–50
        public static readonly SolidColorBrush SpeedBlue = L();    // 50+
        public static readonly SolidColorBrush SpeedTrack = L();   // the empty part of the level bar
        // The network-card chip's icon when Windows decides: light, not the dim chip grey.
        public static readonly SolidColorBrush ChipIconBright = L();
        // The card chip by the card's state (2026-11-18, the user's table): connected — Wi-Fi blue,
        // cable green; no cable / no network / no internet — amber; switched off — grey.
        public static readonly SolidColorBrush NicWifiOk = L();
        public static readonly SolidColorBrush NicWiredOk = L();
        public static readonly SolidColorBrush NicTrouble = L();
        public static readonly SolidColorBrush NicOff = L();
        // The frame of a tile chip whose protection is on (2026-11-20, mock-up A): kill-switch on
        // ▷/⏸, binding to a card on the card chip. Violet — no other state uses it. 1 px at 60 %
        // (the first 1.5 px solid one was too loud: «уже толщину рамки и тусклее»).
        public static readonly SolidColorBrush GuardBorder = L();
        // The same violet inside the rule editor's Kill-switch / «Привязка» switches when on:
        // Color keys Theme.GuardSwitchFill / ...Hover, used only by GuardToggleSwitchStyle
        // (Themes\<theme>\Styles.xaml).

        // ----- Banner: info (idle) — calm slate-blue -----
        public static readonly SolidColorBrush BannerInfoBg = L();
        public static readonly SolidColorBrush BannerInfoBorder = L();
        public static readonly SolidColorBrush BannerInfoText = L();

        // ----- Banner: success — subtle, not a wall of green -----
        public static readonly SolidColorBrush BannerOkBg = L();
        public static readonly SolidColorBrush BannerOkBorder = L();
        public static readonly SolidColorBrush BannerOkText = L();

        // ----- Banner: warning -----
        public static readonly SolidColorBrush BannerWarnBg = L();
        public static readonly SolidColorBrush BannerWarnBorder = L();
        public static readonly SolidColorBrush BannerWarnText = L();

        // ----- Badge: neutral (VPN not in use) -----
        public static readonly SolidColorBrush BadgeNeutralBg = L();
        public static readonly SolidColorBrush BadgeNeutralBorder = L();
        public static readonly SolidColorBrush BadgeNeutralText = L();

        // ----- Placeholder glyph (no icon) -----
        public static readonly SolidColorBrush PlaceholderBg = L();
        public static readonly SolidColorBrush PlaceholderText = L();

        // ----- Overlays (in-window dialogs) -----
        public static readonly SolidColorBrush OverlayScrim = L();        // connecting overlay (opaque-ish dark)
        public static readonly SolidColorBrush OverlayBackdrop = L();   // dimmed backdrop for dialog overlays
        // Floating: dropdowns, context menus, tooltips, flyouts, in-window dialogs — border PickerBorder.
        public static readonly SolidColorBrush OverlayCardBg = L();
        public static readonly SolidColorBrush OverlayBody = L();

        // ----- Shadow -----
        public static Color ShadowColor { get; private set; }
        // Drop shadows under floating cards (their Opacity sets the strength).
        public static Color ShadowBase { get; private set; }

        // ----- Text on coloured fills -----
        // The main window's action buttons and the VPN priority squares: a cool white.
        public static readonly SolidColorBrush TextOnAction = L();
        // Initials on a monogram / badge, the chart's peak markers: plain white on saturated colour.
        public static readonly SolidColorBrush TextOnColor = L();
        // The cross that closes a tile tab: on the tab itself, not on a saturated colour —
        // white in the dark theme (as it was), dark in the light one.
        public static readonly SolidColorBrush TabCloseGlyph = L();

        // ----- Main window: the action row (named 2026-11-22; values as they were) -----
        public static readonly SolidColorBrush ActionDangerHover = L();   // «ВЫКЛ» under the pointer
        // The RDP safe-mode box: the same fill as the «APP» / «DNS» buttons beside it (= AccentSoft).
        // Its old dark wine #3A2F36 sat unnoticed on Mica, but on the solid window background it
        // read as a red button (2026-11-22: «такой же как у кнопок»).
        public static readonly SolidColorBrush SafeModeBg = L();
        // Its text: the same as on the «APP» / «DNS» buttons (= TextOnAction), not the old pink.
        public static readonly SolidColorBrush SafeModeCountdown = L();
        public static readonly SolidColorBrush SafeModeLabel = L();
        // Its switch when on: the red accent at 60 % — dull, like the violet guard switches.
        // Color keys Theme.SafeModeSwitchFill / ...Hover, used only by SafeModeToggleSwitchStyle.
        public static readonly SolidColorBrush EngineBtnBg = L();         // «install the engine» — attention
        public static readonly SolidColorBrush EngineBtnHover = L();
        public static readonly SolidColorBrush EngineBtnText = L();
        // A tile chip's bin under the pointer.
        public static readonly SolidColorBrush ChipDangerBgHover = L();
        public static readonly SolidColorBrush ChipDangerBorderHover = L();

        // ----- VPN priority squares (rule editor, routing matrix) -----
        public static readonly SolidColorBrush RankOnBg = L();       // a VPN in use: green = ok
        public static readonly SolidColorBrush RankOnBorder = L();
        public static readonly SolidColorBrush RankOffBorder = L();
        // The matrix's kill-switch cell when on: violet = protection on (was a brown no other
        // place used). AccentViolet at ~35 % over CardBg, framed with GuardBorder.
        public static readonly SolidColorBrush GuardCellBg = L();
        public static readonly SolidColorBrush AccentViolet = L();

        // ----- Identity colours: a tile's monogram, a company badge in DNS statistics -----
        // Filled in place by Apply (callers cache the array itself).
        public static readonly Color[] MonogramColors = new Color[MonogramCount];
        public static readonly SolidColorBrush BadgeGreyBg = L();    // a «?» / country badge

        // ----- Lists drawn on a card (DNS statistics): stripes and rules over CardBg -----
        public static readonly SolidColorBrush ListStripeOnCard = L();
        public static readonly SolidColorBrush ListLineOnCard = L();

        // ----- The speed chart -----
        public static readonly SolidColorBrush ChartGrid = L();
        public static readonly SolidColorBrush ChartZeroLine = L();
        public static readonly SolidColorBrush ChartGuide = L();    // a peak's drop line, the pointer's line
        public static Color ChartUpColor { get; private set; }
    }
}
