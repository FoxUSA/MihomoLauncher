using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MihomoLauncher.Services;

namespace MihomoLauncher.Views
{
    // The window is sized by the tiles grid (2026-11-17: «дай возможность увеличивать высоту окна
    // до любого количества рядов… не давай оттянуть так, чтобы было лишнее пространство»). Any edge
    // can be dragged; while it moves (WM_SIZING) the frame snaps to whole columns × rows — a side
    // edge picks the column count by width, the top/bottom edge picks it by the height closest to
    // the one dragged to (more rows = fewer columns). From one long row of every tile to one
    // column, as far as the top bar and the screen allow. The count is kept in config
    // (TileColumns, 5 by default); FitWidthToTopBar holds the width to it, RecalcHeight the height
    // to the content. No maximize: a maximized window would be all blank space.
    public partial class MainWindow
    {
        private const int WM_SIZING = 0x0214, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_SYSCOMMAND = 0x0112;
        private const int SC_MAXIMIZE = 0xF030;
        // The frame's sides: TilesArea margins 10.5 each side + the tiles' own 3.5 = 14 to the edge
        // (2026-11-16: «14 дипов слева и справа»; the window has no frame of its own).
        private const double GridSides = 21;

        private bool _inSizeMove;
        private bool _sizeColsChanged;
        private double _sizeFixedH;   // the window's height without tiles and tabs, at the drag's start

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private void InitWindowSizing()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) HwndSource.FromHwnd(hwnd)?.AddHook(SizingHook);
            else SourceInitialized += (s, e) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SizingHook);
            // Aero Snap's drag to the top edge, Win+↑: back to normal at once.
            StateChanged += (s, e) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        }

        private double GridWindowWidth(int cols) => cols * TileOuter + GridSides + FrameW;

        // What the window takes off its content's width on this machine (a frame / DPI rounding):
        // 0 on the author's PC, a few px on mom's ZenBook (2026-11-21) — added so whole columns
        // still fit (SyncTileGrid).
        private double FrameW
        {
            get
            {
                double d = ActualWidth - ResultRoot.ActualWidth;
                return ActualWidth > 0 && ResultRoot.ActualWidth > 0 && d > 0.5 && d < 40 ? Math.Ceiling(d) : 0;
            }
        }

        // Down to one column (2026-11-17: «не закрывай юзеру возможность… хоть в один ряд плитки по
        // вертикали»): the chrome gives way instead (ApplyChromeFit).
        private int MinTileCols() => 1;

        // No wider than the tiles need (2026-11-20: dragged wide, the window kept a blank strip to the
        // right of 8 tiles): the fullest tab's tiles + the «new rule» tile in one row, and no more
        // than the screen holds. Max over every tab — switching tabs never changes the width.
        // The saved TileColumns is left as the person set it: with tiles added back the window
        // widens to it again.
        // Never below the default 5 by that rule (2026-11-21, a fresh install with no tiles: the
        // window shrank to 2 columns and the «set up the engine» question was cut off — «сужается
        // при отсутствии плиток»). Narrower only when the person drags it so.
        private const int DefaultTileCols = 5;

        private int MaxTileCols()
        {
            int c = (int)Math.Floor((SystemParameters.WorkArea.Width - GridSides - FrameW) / TileOuter);
            return Math.Max(MinTileCols(), Math.Min(c, Math.Max(DefaultTileCols, UsefulTileCols())));
        }

        private int UsefulTileCols()
        {
            try
            {
                var tabs = _config?.Tabs;
                if (tabs == null || tabs.Count == 0 || _config.Apps == null) return int.MaxValue;
                int most = tabs.Max(t => _config.Apps.Count(a => a != null && a.HasTileTarget && TileTabs.OnTab(_config, a, t)));
                return Math.Max(2, most + 1);   // + «Добавить»; an empty tab: «Добавить» + its hint
            }
            catch { return int.MaxValue; }
        }

        private int ClampTileCols(int c) => Math.Max(MinTileCols(), Math.Min(MaxTileCols(), c));

        private int WantedTileCols() => ClampTileCols(_config.TileColumns > 0 ? _config.TileColumns : DefaultTileCols);

        // Rows a tab's tiles take at `cols` columns, laid out as RebuildTiles does: the tiles, the
        // «new rule» tile when the last row has room, and on an empty tab the hint beside it (under
        // it in a single column).
        private int TabTileRows(TileTab tab, int cols)
        {
            cols = Math.Max(1, cols);
            int count = _config.Apps.Count(a => a != null && a.HasTileTarget && TileTabs.OnTab(_config, a, tab));
            if (count == 0) return cols == 1 ? 2 : 1;
            // The «new rule» tile counted only in a free place of the last row: on another tab it
            // may also start a row of its own (ShowsAddTile), but only inside «Основная»'s height —
            // which OpenTabTileRows takes anyway, so that row never makes the window taller.
            int items = count + (count % cols != 0 ? 1 : 0);
            return (items + cols - 1) / cols;
        }

        // Does the «new rule» outline go at the end of this tab's tiles (2026-11-23)? Whenever it
        // fits without making the window taller: a free place in the last row; or, on a tab other
        // than «Основная» (its tiles area is at least «Основная»'s height — RebuildTiles' MinHeight),
        // a new row that still lies inside that height. Only a full last row with no spare height
        // gets none — «Основная» itself never has spare height, so there it is as before.
        // Re-asked on every rebuild, which a column change (SyncTileGrid) and a resize both cause.
        private bool ShowsAddTile(TileTab tab, int count, int cols)
        {
            cols = Math.Max(1, cols);
            if (count == 0 || count % cols != 0) return true;
            var main = _config.Tabs != null && _config.Tabs.Count > 0 ? _config.Tabs[0] : null;
            if (main == null || tab == null || tab.Id == TileTabs.MainId) return false;
            return count / cols + 1 <= TabTileRows(main, cols);
        }

        private double TileRowHeight => TileHeight + 7;

        // Rows of the open tab: never fewer than «Основная»'s (the window keeps its height on
        // every tab), more if this tab has more.
        private int OpenTabTileRows(int cols)
        {
            var main = _config.Tabs[0];
            var cur = TileTabs.Current(_config);
            int rows = TabTileRows(main, cols);
            return cur == main ? rows : Math.Max(rows, TabTileRows(cur, cols));
        }

        // The tabs row wraps like the tiles; «+» never takes a row of its own (RenderTabs).
        private int TabRows(int cols) => (Math.Max(1, _config.Tabs.Count) + cols - 1) / Math.Max(1, cols);

        private double PredictHeight(int cols) =>
            _sizeFixedH + OpenTabTileRows(cols) * TileRowHeight + TabRows(cols) * (TabHeight + 7);

        private IntPtr SizingHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                switch (msg)
                {
                    case WM_SYSCOMMAND:
                        // Includes the title bar's double click (SC_MAXIMIZE | HTCAPTION).
                        if (((int)wParam & 0xFFF0) == SC_MAXIMIZE) handled = true;
                        break;
                    case WM_ENTERSIZEMOVE:
                        _inSizeMove = true;
                        _sizeColsChanged = false;
                        _sizeFixedH = ActualHeight - TilesPanel.ActualHeight - TabsPanel.ActualHeight;
                        break;
                    case WM_EXITSIZEMOVE:
                        _inSizeMove = false;
                        if (_sizeColsChanged)
                        {
                            _sizeColsChanged = false;
                            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tile-cols-save", ex); }
                            Log.Info("Окно: плиток в ряду — " + _config.TileColumns + ".");
                        }
                        RecalcHeight();
                        break;
                    case WM_SIZING:
                        SnapSizingRect((int)wParam, lParam);
                        handled = true;
                        return new IntPtr(1);
                }
            }
            catch (Exception ex) { Log.Error("window-sizing", ex); }
            return IntPtr.Zero;
        }

        // wParam: 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right.
        private void SnapSizingRect(int edge, IntPtr lParam)
        {
            var r = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;

            double w, h;
            if (ResultRoot.Visibility != Visibility.Visible)
            {
                // Not the tiles screen: it is sized by its content alone.
                w = ActualWidth; h = ActualHeight;
            }
            else
            {
                int minC = MinTileCols(), maxC = MaxTileCols();
                int c;
                if (edge == 3 || edge == 6)
                {
                    // Top / bottom edge: the column count whose height is nearest to the dragged
                    // one. Several counts give the same rows (10 tiles: 5…9 columns are all two
                    // rows) — then the one nearest to now, so the width doesn't jump (it went to 9).
                    double target = (r.Bottom - r.Top) / sy;
                    int now = Math.Max(minC, Math.Min(maxC, _tileCols));
                    c = now;
                    double best = Math.Abs(PredictHeight(now) - target);
                    for (int k = minC; k <= maxC; k++)
                    {
                        double d = Math.Abs(PredictHeight(k) - target);
                        if (d < best - 0.5 || (d <= best + 0.5 && Math.Abs(k - now) < Math.Abs(c - now))) { best = d; c = k; }
                    }
                }
                else
                {
                    double width = (r.Right - r.Left) / sx;
                    c = (int)Math.Round((width - GridSides - FrameW) / TileOuter);
                }
                c = Math.Max(minC, Math.Min(maxC, c));
                // Taller than the screen — fewer rows, i.e. more columns.
                double wa = SystemParameters.WorkArea.Height;
                while (c < maxC && PredictHeight(c) > wa) c++;
                if (c != _config.TileColumns) { _config.TileColumns = c; _sizeColsChanged = true; }
                w = GridWindowWidth(c);
                h = Math.Min(PredictHeight(c), wa);
            }

            int pw = (int)Math.Round(w * sx), ph = (int)Math.Round(h * sy);
            if (edge == 1 || edge == 4 || edge == 7) r.Left = r.Right - pw; else r.Right = r.Left + pw;
            if (edge == 3 || edge == 4 || edge == 5) r.Top = r.Bottom - ph; else r.Bottom = r.Top + ph;
            Marshal.StructureToPtr(r, lParam, false);
        }

        // ---- the chrome on a narrow window ----

        private bool _topIconsOnly, _toolsInBody;
        private double _topRowFull;   // the top row with its words, measured while they are shown
        private Tuple<System.Windows.Controls.Button, string, string>[] _topLabels;
        private Thickness[] _topMargins;

        // Narrower than the top row → its buttons keep only their icons (and the RDP box only its
        // switch) and wrap into as many rows as needed. Narrower than the title bar → the caption
        // goes first, then the title bar's buttons move down above the top row. True when anything
        // changed (the height must be measured again).
        private bool ApplyChromeFit(double width)
        {
            if (double.IsNaN(width) || width <= 0) return false;
            bool changed = false;

            if (_topLabels == null)
            {
                _topLabels = new[]
                {
                    Tuple.Create((System.Windows.Controls.Button)BtnDisconnect, "Main_Disconnect", (string)null),
                    Tuple.Create((System.Windows.Controls.Button)BtnVpnMatrix, "Main_Routing", (string)null),
                    Tuple.Create((System.Windows.Controls.Button)BtnVpnList, (string)null, "VPN"),
                    Tuple.Create((System.Windows.Controls.Button)BtnRulesList, (string)null, "APP"),
                    Tuple.Create((System.Windows.Controls.Button)BtnDns, (string)null, "DNS"),
                };
                _topMargins = TopButtons.Children.OfType<FrameworkElement>().Select(f => f.Margin).ToArray();
            }
            if (!_topIconsOnly)
            {
                double row = TopRowWidth();
                if (row > 0) _topRowFull = row;
            }
            bool icons = _topRowFull > 0 && width < _topRowFull + 36;   // + the row's margins 18/18
            if (icons != _topIconsOnly)
            {
                _topIconsOnly = icons;
                foreach (var t in _topLabels)
                {
                    if (icons) t.Item1.Content = null;
                    else if (t.Item2 != null) t.Item1.SetResourceReference(ContentControl.ContentProperty, t.Item2);
                    else t.Item1.Content = t.Item3;
                }
                // Wrapped rows need a gap between them too.
                var kids = TopButtons.Children.OfType<FrameworkElement>().ToArray();
                for (int i = 0; i < kids.Length && i < _topMargins.Length; i++)
                    kids[i].Margin = icons ? new Thickness(2) : _topMargins[i];
                bool on = _config?.RemoteDesktopSafeMode == true;
                KillLabel.Visibility = on || icons ? Visibility.Collapsed : Visibility.Visible;
                changed = true;
            }

            // Title bar: inset 18 + fox 18 + «—» «✕» + a little air.
            double bare = 18 + 18 + CaptionButtonsWidth + 8;
            double tools = TitleToolsNeed();
            bool toolsUp = width >= bare + tools;
            // The longest caption that fits: «… / Mihomo 1.19.34», else «Mihomo Launcher 3.1.1»;
            // neither — no caption. Measured against the real «—» «✕» and no air: on two columns the
            // estimate above left 115 px for a 124 px «Mihomo Launcher 3.1.1» and the caption
            // vanished, while the bar really had ~127 free (2026-11-20).
            double room = width - 18 - 18 - CaptionButtonsActual() - (toolsUp ? tools : 0);
            var fit = CaptionVariants().FirstOrDefault(v => CaptionNeed(v, toolsUp) <= room);
            var capMargin = new Thickness(8, 0, toolsUp ? 12 : 0, 0);   // nothing to its right then
            if (TxtCaption.Margin != capMargin) { TxtCaption.Margin = capMargin; changed = true; }
            if (fit != null && TxtCaption.Text != fit) { TxtCaption.Text = fit; changed = true; }
            var capVis = fit != null ? Visibility.Visible : Visibility.Collapsed;
            if (TxtCaption.Visibility != capVis) { TxtCaption.Visibility = capVis; changed = true; }
            if (toolsUp == _toolsInBody)
            {
                _toolsInBody = !toolsUp;
                if (toolsUp)
                {
                    BodyTools.Content = null;
                    BodyTools.Visibility = Visibility.Collapsed;
                    TitleTools.HorizontalAlignment = HorizontalAlignment.Right;
                    TitleBarMain.TrailingContent = TitleTools;
                }
                else
                {
                    TitleBarMain.TrailingContent = null;
                    TitleTools.HorizontalAlignment = HorizontalAlignment.Center;
                    BodyTools.Content = TitleTools;
                    BodyTools.Visibility = Visibility.Visible;
                }
                changed = true;
            }
            return changed;
        }
    }
}
