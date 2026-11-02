using System.Windows.Media;

namespace MihomoLauncher.Services
{
    // Single source of truth for MihomoLauncher's colours. The whole app uses a dark
    // theme (WPF-UI ThemesDictionary="Dark" handles the framework controls; these
    // brushes cover the custom-drawn tiles, banners, badges and overlays).
    //
    // Used both from XAML (via {x:Static svc:Theme.Xxx}) and from code-behind, so
    // there is exactly one place to tweak the look. All brushes are frozen for
    // cheap reuse across the visual tree.
    public static class Theme
    {
        private static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // ----- Surfaces -----
        public static readonly SolidColorBrush WindowBg = B("#FF1B1B1F");   // solid fallback behind Mica
        public static readonly SolidColorBrush CardBg = B("#FF2A2A30");
        public static readonly SolidColorBrush CardBgHover = B("#FF34343C");
        public static readonly SolidColorBrush CardBorder = B("#FF3C3C46");
        public static readonly SolidColorBrush CardBorderHover = B("#FF5E82C4");

        // ----- Tables (native ListView / GridView) -----
        public static readonly SolidColorBrush ListBg = B("#FF1F1F24");      // recessed surface
        public static readonly SolidColorBrush SelectionBg = B("#4D5E82C4"); // accent, semi-transparent

        // ----- Tiles -----
        public static readonly SolidColorBrush TileBg = B("#FF2A2A30");
        public static readonly SolidColorBrush TileBgHover = B("#FF313A4E");
        public static readonly SolidColorBrush TileBorder = B("#FF3C3C46");
        public static readonly SolidColorBrush TileBorderHover = B("#FF5E82C4");
        public static readonly SolidColorBrush TileDisabledBg = B("#FF222226");
        public static readonly SolidColorBrush TileDisabledBorder = B("#FF2E2E34");

        // ----- Text -----
        public static readonly SolidColorBrush TextPrimary = B("#FFF1F1F4");
        public static readonly SolidColorBrush TextSecondary = B("#FFA2A2AC");
        public static readonly SolidColorBrush TextMuted = B("#FF74747E");
        public static readonly SolidColorBrush TextDisabled = B("#FF6E6E78");

        // ----- Accents (tile sub-labels, status text) -----
        public static readonly SolidColorBrush AccentGreen = B("#FF8FB89B");   // muted sage
        public static readonly SolidColorBrush AccentRed = B("#FFD98C84");
        public static readonly SolidColorBrush AccentAmber = B("#FFD9BC8C");
        public static readonly SolidColorBrush AccentBlue = B("#FFAAB9D6");
        public static readonly SolidColorBrush AccentGrey = B("#FF8A909A");

        // ----- Banner: info (idle) — calm slate-blue -----
        public static readonly SolidColorBrush BannerInfoBg = B("#FF20242E");
        public static readonly SolidColorBrush BannerInfoBorder = B("#FF333B49");
        public static readonly SolidColorBrush BannerInfoText = B("#FFAAB9D6");

        // ----- Banner: success — subtle, not a wall of green -----
        public static readonly SolidColorBrush BannerOkBg = B("#FF1C241F");
        public static readonly SolidColorBrush BannerOkBorder = B("#FF323D34");
        public static readonly SolidColorBrush BannerOkText = B("#FFA0C0A9");

        // ----- Banner: warning -----
        public static readonly SolidColorBrush BannerWarnBg = B("#FF2E2719");
        public static readonly SolidColorBrush BannerWarnBorder = B("#FF564A2A");
        public static readonly SolidColorBrush BannerWarnText = B("#FFD9BC8C");

        // ----- Badge: neutral (VPN not in use) -----
        public static readonly SolidColorBrush BadgeNeutralBg = B("#FF26262C");
        public static readonly SolidColorBrush BadgeNeutralBorder = B("#FF3C3C46");
        public static readonly SolidColorBrush BadgeNeutralText = B("#FFA2A2AC");

        // ----- Placeholder glyph (no icon) -----
        public static readonly SolidColorBrush PlaceholderBg = B("#FF26344E");
        public static readonly SolidColorBrush PlaceholderText = B("#FF93ABDA");

        // ----- Overlays (in-window dialogs) -----
        public static readonly SolidColorBrush OverlayScrim = B("#1B1B1F");        // connecting overlay (opaque-ish dark)
        public static readonly SolidColorBrush OverlayBackdrop = B("#99000000");   // dimmed backdrop for dialog overlays
        public static readonly SolidColorBrush OverlayCardBg = B("#FF2C2C33");
        public static readonly SolidColorBrush OverlayBody = B("#FFC9C9D2");

        // ----- Shadow -----
        public static readonly Color ShadowColor = (Color)ColorConverter.ConvertFromString("#66000000");
    }
}
