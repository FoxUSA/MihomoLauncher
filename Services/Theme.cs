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

        // ===== The colour standard (2026-11-22: «у каждого окна свой набор цветов») =====
        // One neutral charcoal with a slight cool tint — the tiles' palette — so the coloured state
        // accents read against it. Every surface is OPAQUE: no window uses Mica (it tinted the same
        // window differently over different wallpapers), and the library's own semi-transparent
        // card / popup / window brushes are redirected to these (App.xaml, direct entries).
        //   window WindowBg · raised CardBg · recessed well ListBg · floating OverlayCardBg with
        //   PickerBorder · outline CardBorder (1 px, radius 8).
        // One interactive blue (Accent) for hover borders, selection, focus, primary buttons,
        // checked boxes and switches; green / amber / red / violet / blue-grey mean STATE only.

        // ----- Surfaces -----
        public static readonly SolidColorBrush WindowBg = B("#FF1B1B1F");   // every window's background
        public static readonly SolidColorBrush CardBg = B("#FF2A2A30");     // cards, tiles, panels, ui:Card
        public static readonly SolidColorBrush CardBgHover = B("#FF34343C");
        public static readonly SolidColorBrush CardBorder = B("#FF3C3C46"); // the outline of cards, wells, frames
        public static readonly SolidColorBrush CardBorderHover = B("#FF5E82C4");

        // ----- The one accent -----
        public static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#FF5E82C4");
        public static readonly SolidColorBrush Accent = B("#FF5E82C4");
        // The accent at 30 % over the well, made opaque: a selected table row, and the main
        // window's action buttons (they were #323D51 — the same colour within a hair).
        public static readonly SolidColorBrush AccentSoft = B("#FF323D54");
        public static readonly SolidColorBrush AccentSoftHover = B("#FF3E4B64");

        // ----- Tables (DataGrid, the code-drawn tables, the implicit ListView style) -----
        public static readonly SolidColorBrush ListBg = B("#FF1F1F24");      // recessed well: tables, lists, viewers
        public static readonly SolidColorBrush SelectionBg = B("#4D5E82C4"); // accent, semi-transparent (pills, chips)
        public static readonly SolidColorBrush TableHeaderBg = B("#FF26262C");
        public static readonly SolidColorBrush TableLine = B("#FF303038");    // inner hairlines, quieter than the outline
        public static readonly SolidColorBrush TableRowAlt = B("#FF232329");
        public static readonly SolidColorBrush TableRowHover = B("#FF2B2B33");
        public static readonly SolidColorBrush TableRowSelected = AccentSoft; // with TextPrimary, focused or not

        // ----- Buttons: default, primary (library + Accent), «add» (AccentGreen text), danger -----
        // Danger is muted on purpose: a rare action must not be the loudest thing in the window.
        public static readonly SolidColorBrush DangerBg = B("#FF7D3A3A");
        public static readonly SolidColorBrush DangerBgHover = B("#FF914444");

        // ----- Tiles -----
        public static readonly SolidColorBrush TileBg = B("#FF2A2A30");
        public static readonly SolidColorBrush TileBgHover = B("#FF313A4E");
        public static readonly SolidColorBrush TileBorder = B("#FF3C3C46");
        public static readonly SolidColorBrush TileBorderHover = B("#FF5E82C4");
        public static readonly SolidColorBrush TileDisabledBg = B("#FF222226");
        public static readonly SolidColorBrush TileDisabledBorder = B("#FF2E2E34");

        // Tile corner chips (gear / reconnect / disconnect): same shape as the VPN plate.
        public static readonly SolidColorBrush ChipBg = B("#09FFFFFF");
        public static readonly SolidColorBrush ChipBorder = B("#17FFFFFF");
        public static readonly SolidColorBrush ChipBgHover = B("#2E5E82C4");

        // The VPN plate at the bottom of a tile, tinted by the tile's state.
        public static readonly SolidColorBrush PlateGreenBg = B("#1A8FB89B");
        public static readonly SolidColorBrush PlateGreenBorder = B("#428FB89B");
        public static readonly SolidColorBrush PlateGreenBgHover = B("#338FB89B");
        public static readonly SolidColorBrush PlateGreenBorderHover = B("#808FB89B");
        public static readonly SolidColorBrush PlateAmberBg = B("#1AD9BC8C");
        public static readonly SolidColorBrush PlateAmberBorder = B("#4DD9BC8C");
        public static readonly SolidColorBrush PlateAmberBgHover = B("#33D9BC8C");
        public static readonly SolidColorBrush PlateAmberBorderHover = B("#8CD9BC8C");
        public static readonly SolidColorBrush PlateRedBg = B("#1AD98C84");
        public static readonly SolidColorBrush PlateRedBorder = B("#4DD98C84");
        public static readonly SolidColorBrush PlateRedBgHover = B("#33D98C84");
        public static readonly SolidColorBrush PlateRedBorderHover = B("#8CD98C84");
        public static readonly SolidColorBrush PlateNeutralBg = B("#08FFFFFF");
        public static readonly SolidColorBrush PlateNeutralBorder = B("#0FFFFFFF");

        // The VPN dropdown that opens from the plate.
        public static readonly SolidColorBrush PickerBorder = B("#FF45454F");
        public static readonly SolidColorBrush PickerRowHover = B("#0FFFFFFF");
        public static readonly SolidColorBrush PickerRowCurrent = B("#0DFFFFFF");

        // ----- Text -----
        public static readonly SolidColorBrush TextPrimary = B("#FFF1F1F4");
        public static readonly SolidColorBrush TextSecondary = B("#FFA2A2AC");
        public static readonly SolidColorBrush TextMuted = B("#FF74747E");
        public static readonly SolidColorBrush TextDisabled = B("#FF6E6E78");

        // ----- Accents (tile sub-labels, status text) -----
        public static readonly SolidColorBrush AccentGreen = B("#FF8FB89B");   // muted sage
        public static readonly SolidColorBrush AccentRed = B("#FFD98C84");
        // The log button's icon while tiles conflict: red, but dimmed — a hint, not an alarm.
        public static readonly SolidColorBrush ConflictIcon = B("#FFB06A64");
        public static readonly SolidColorBrush AccentAmber = B("#FFD9BC8C");
        // The "auto-tracking added something" notice: the tile's gear, the tile's frame and the
        // frame of the new rows in the rule editor. The plain AccentAmber again: once the whole
        // tile is framed, a brighter amber was no longer needed (2026-11-22).
        public static readonly SolidColorBrush NoticeAmber = B("#FFD9BC8C");
        // The tile's frame for the same notice: the same amber at 55 %. 1 px is 1.5 px at 150 %
        // scaling, and solid it read as a thick line («тоньше бордюр»); see GuardBorder for the
        // same trick on the chips.
        public static readonly SolidColorBrush NoticeTileBorder = B("#8CD9BC8C");
        public static readonly SolidColorBrush AccentBlue = B("#FFAAB9D6");
        public static readonly SolidColorBrush AccentGrey = B("#FF8A909A");

        // Speed classes (tile speed plate + its 24-hour chart, 2026-11-16): brighter than the
        // muted accents on purpose — the first, muted version could not be seen at all.
        public static readonly SolidColorBrush SpeedRed = B("#FFEF6B6B");     // < 1 Mbit/s
        public static readonly SolidColorBrush SpeedAmber = B("#FFE9B64E");   // 1–10
        public static readonly SolidColorBrush SpeedGreen = B("#FF6FCF8F");   // 10–50
        public static readonly SolidColorBrush SpeedBlue = B("#FF5AA9FF");    // 50+
        public static readonly SolidColorBrush SpeedTrack = B("#FF45454A");   // the empty part of the level bar
        // The network-card chip's icon when Windows decides: light, not the dim chip grey.
        public static readonly SolidColorBrush ChipIconBright = B("#FFD6D6DA");
        // The card chip by the card's state (2026-11-18, the user's table): connected — Wi-Fi blue,
        // cable green; no cable / no network / no internet — amber; switched off — grey.
        public static readonly SolidColorBrush NicWifiOk = B("#FF5B86E5");
        public static readonly SolidColorBrush NicWiredOk = B("#FF7FD39B");
        public static readonly SolidColorBrush NicTrouble = B("#FFEC8A4B");
        public static readonly SolidColorBrush NicOff = B("#FFCFC8E3");
        // The frame of a tile chip whose protection is on (2026-11-20, mock-up A): kill-switch on
        // ▷/⏸, binding to a card on the card chip. Violet — no other state uses it. 1 px at 60 %
        // (the first 1.5 px solid one was too loud: «уже толщину рамки и тусклее»).
        public static readonly SolidColorBrush GuardBorder = B("#99A78BFA");
        // The same violet inside the rule editor's Kill-switch / «Привязка» switches when on.
        public static readonly SolidColorBrush GuardSwitchFill = B("#99A78BFA");
        public static readonly SolidColorBrush GuardSwitchFillHover = B("#BFA78BFA");

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
        // Floating: dropdowns, context menus, tooltips, flyouts, in-window dialogs — border PickerBorder.
        public static readonly SolidColorBrush OverlayCardBg = B("#FF2C2C33");
        public static readonly SolidColorBrush OverlayBody = B("#FFC9C9D2");

        // ----- Shadow -----
        public static readonly Color ShadowColor = (Color)ColorConverter.ConvertFromString("#66000000");
        // Drop shadows under floating cards (their Opacity sets the strength).
        public static readonly Color ShadowBase = Colors.Black;

        // ----- Text on coloured fills -----
        // The main window's action buttons and the VPN priority squares: a cool white.
        public static readonly SolidColorBrush TextOnAction = B("#FFEAF1FF");
        // Initials on a monogram / badge, the chart's peak markers: plain white on saturated colour.
        public static readonly SolidColorBrush TextOnColor = B("#FFFFFFFF");

        // ----- Main window: the action row (named 2026-11-22; values as they were) -----
        public static readonly SolidColorBrush ActionDangerHover = B("#FF6E3A3A");   // «ВЫКЛ» under the pointer
        // The RDP safe-mode box: the same fill as the «APP» / «DNS» buttons beside it (= AccentSoft).
        // Its old dark wine #3A2F36 sat unnoticed on Mica, but on the solid window background it
        // read as a red button (2026-11-22: «такой же как у кнопок»).
        public static readonly SolidColorBrush SafeModeBg = B("#FF323D54");
        // Its text: the same as on the «APP» / «DNS» buttons (= TextOnAction), not the old pink.
        public static readonly SolidColorBrush SafeModeCountdown = B("#FFEAF1FF");
        public static readonly SolidColorBrush SafeModeLabel = B("#FFEAF1FF");
        // Its switch when on: the red accent at 60 % — dull, like the violet guard switches.
        public static readonly SolidColorBrush SafeModeSwitchFill = B("#99D98C84");
        public static readonly SolidColorBrush SafeModeSwitchFillHover = B("#BFD98C84");
        public static readonly SolidColorBrush EngineBtnBg = B("#FF6E5A33");         // «install the engine» — attention
        public static readonly SolidColorBrush EngineBtnHover = B("#FF80683B");
        public static readonly SolidColorBrush EngineBtnText = B("#FFF3E7CC");
        // A tile chip's bin under the pointer.
        public static readonly SolidColorBrush ChipDangerBgHover = B("#FF5C3030");
        public static readonly SolidColorBrush ChipDangerBorderHover = B("#FF804444");

        // ----- VPN priority squares (rule editor, routing matrix) -----
        public static readonly SolidColorBrush RankOnBg = B("#FF3C6149");       // a VPN in use: green = ok
        public static readonly SolidColorBrush RankOnBorder = B("#FF588A69");
        public static readonly SolidColorBrush RankOffBorder = B("#FF555F77");
        // The matrix's kill-switch cell when on: violet = protection on (was a brown no other
        // place used). AccentViolet at ~35 % over CardBg, framed with GuardBorder.
        public static readonly SolidColorBrush GuardCellBg = B("#FF564C77");
        public static readonly SolidColorBrush AccentViolet = B("#FFA78BFA");

        // ----- Identity colours: a tile's monogram, a company badge in DNS statistics -----
        public static readonly Color[] MonogramColors =
        {
            Color.FromRgb(0x3F, 0x6F, 0xD8), Color.FromRgb(0x2F, 0x9A, 0x6B), Color.FromRgb(0xC2, 0x6A, 0x2E),
            Color.FromRgb(0x87, 0x5C, 0xCF), Color.FromRgb(0xC0, 0x4B, 0x5C), Color.FromRgb(0x2A, 0x8E, 0xA8),
            Color.FromRgb(0x9C, 0x7C, 0x2C), Color.FromRgb(0x5E, 0x6B, 0x84),
        };
        public static readonly SolidColorBrush BadgeGreyBg = B("#FF3A3A42");    // a «?» / country badge

        // ----- Lists drawn on a card (DNS statistics): stripes and rules over CardBg -----
        public static readonly SolidColorBrush ListStripeOnCard = B("#0AFFFFFF");
        public static readonly SolidColorBrush ListLineOnCard = B("#1CFFFFFF");

        // ----- The speed chart -----
        public static readonly SolidColorBrush ChartGrid = B("#12FFFFFF");
        public static readonly SolidColorBrush ChartZeroLine = B("#38FFFFFF");
        public static readonly SolidColorBrush ChartGuide = B("#46FFFFFF");    // a peak's drop line, the pointer's line
        public static readonly Color ChartUpColor = (Color)ColorConverter.ConvertFromString("#FFC9CBD6");
    }
}
