using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // Tiles pulled out of the window (2026-11-25): a tile dragged past the window's edge and let go
    // there opens in a TileWindow of its own at that spot — on top of everything, 1 in 1 the tile,
    // its bin a ✕ that closes only that window. The main window keeps the tile. One window per
    // tile (pulling it out again moves it). Dragged back over the main window, the window lights
    // up («сюда можно бросить»; the tiles do not move) and the drop closes it. Where they were is
    // remembered and they reopen at start. The first 3 grabs of a tile say at the bottom of the
    // window that a tile can be pulled out.
    public partial class MainWindow
    {
        private readonly Dictionary<string, TileWindow> _tileWins = new Dictionary<string, TileWindow>(StringComparer.Ordinal);
        private readonly Dictionary<string, SpeedView> _speedViewsDetached = new Dictionary<string, SpeedView>(StringComparer.Ordinal);
        private bool _buildingDetached;      // CreateTile for a TileWindow: its speed plate registers there
        private bool _detachedRestored;
        // A press on a pulled-out tile: a dialog that follows in the main window (a confirmation,
        // the password) brings the main window up even from the tray — else it would wait unseen.
        private DateTime _detachedTouchedAt = DateTime.MinValue;

        // The end of RebuildTiles: the pulled-out tiles redrawn as well (once: reopened from the config).
        private void RebuildDetachedTiles()
        {
            if (!_detachedRestored)
            {
                _detachedRestored = true;
                foreach (var d in (_config.DetachedTiles ?? new List<DetachedTile>()).ToList())
                    if (d != null) OpenTileWindow(d.Id, d.X, d.Y, false);
            }
            foreach (var kv in _tileWins.ToList())
            {
                var entry = _config.Apps.FirstOrDefault(a => a != null && a.Id == kv.Key);
                if (entry == null || !entry.HasTileTarget) { CloseTileWindow(kv.Key, true); continue; }
                kv.Value.SetTile(CreateDetachedTile(entry));
            }
        }

        private FrameworkElement CreateDetachedTile(AppEntry entry)
        {
            _buildingDetached = true;
            try { return CreateTile(entry, true); }
            finally { _buildingDetached = false; }
        }

        // x, y: the window's top-left, screen DIP.
        private void OpenTileWindow(string id, double x, double y, bool save)
        {
            var entry = _config.Apps.FirstOrDefault(a => a != null && a.Id == id);
            if (entry == null || !entry.HasTileTarget) { ForgetDetached(id); return; }
            TileWindow w;
            if (_tileWins.TryGetValue(id, out w))
            {
                w.Left = x; w.Top = y;
                UiHelpers.KeepOnScreen(w);
                if (save)
                {
                    RememberDetached(w);
                    Log.Info("Плитка «" + (entry.IsAllTraffic ? AllTrafficName() : entry.Name) + "» вынесена в отдельное окно.");
                }
                return;
            }
            w = new TileWindow(id) { Left = x, Top = y, WindowStartupLocation = WindowStartupLocation.Manual };
            w.IsOverMain = OverMainPx;
            w.DropHint = ShowDetachDropHint;
            w.Home = () => CloseTileWindow(id, true);
            w.Moved = RememberDetached;
            w.Touched = () => _detachedTouchedAt = DateTime.UtcNow;
            w.Closed += (s, e) =>
            {
                _tileWins.Remove(id);
                _speedViewsDetached.Remove(id);
                if (w.UserClosed) ForgetDetached(id);
            };
            w.SetTile(CreateDetachedTile(entry));
            _tileWins[id] = w;
            try
            {
                w.Show();
                UiHelpers.KeepOnScreen(w);   // a screen gone since: back onto one that is there
            }
            catch (Exception ex) { Log.Error("tile-window", ex); }
            if (save)
            {
                RememberDetached(w);
                Log.Info("Плитка «" + (entry.IsAllTraffic ? AllTrafficName() : entry.Name) + "» вынесена в отдельное окно.");
            }
        }

        // user: the ✕ / dropped home — forgotten; else only closed (the app exits).
        private void CloseTileWindow(string id, bool user)
        {
            TileWindow w;
            if (_tileWins.TryGetValue(id, out w))
            {
                w.UserClosed = user;
                try { w.Close(); } catch (Exception ex) { Log.Error("tile-window-close", ex); }
            }
            else if (user) ForgetDetached(id);
        }

        private void RememberDetached(TileWindow w)
        {
            try
            {
                if (_config.DetachedTiles == null) _config.DetachedTiles = new List<DetachedTile>();
                var d = _config.DetachedTiles.FirstOrDefault(x => x != null && x.Id == w.TileId);
                if (d == null) _config.DetachedTiles.Add(d = new DetachedTile { Id = w.TileId });
                d.X = Math.Round(w.Left, 1);
                d.Y = Math.Round(w.Top, 1);
                AppConfigStore.Save(_config);
            }
            catch (Exception ex) { Log.Error("tile-window-save", ex); }
        }

        private void ForgetDetached(string id)
        {
            try
            {
                if (_config.DetachedTiles == null) return;
                if (_config.DetachedTiles.RemoveAll(x => x == null || x.Id == id) > 0) AppConfigStore.Save(_config);
            }
            catch (Exception ex) { Log.Error("tile-window-save", ex); }
        }

        // Is this screen point (device pixels) over the main window (shown)?
        private bool OverMainPx(Point px)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return false;
            try
            {
                var tl = PointToScreen(new Point(0, 0));
                var br = PointToScreen(new Point(ActualWidth, ActualHeight));
                return px.X >= tl.X && px.X < br.X && px.Y >= tl.Y && px.Y < br.Y;
            }
            catch { return false; }
        }

        // A screen point in device pixels → DIP (the main window's scale).
        private Point PxToDip(Point px)
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(px) : px;
        }

        // A dialog about to show in the main window: up from the tray if a pulled-out tile was
        // pressed a moment ago (OverlayDialog asks). Background dialogs stay as they were.
        internal void SurfaceForDetachedAction()
        {
            if ((DateTime.UtcNow - _detachedTouchedAt).TotalSeconds > 60) return;
            if (IsVisible && WindowState != WindowState.Minimized) return;
            ShowFromTray();
        }

        // ---- the main window lit while a pulled-out tile is over it ----

        private Border _detachDropHint;

        private void ShowDetachDropHint(bool on)
        {
            var root = Content as Grid;
            if (root == null) return;
            if (!on)
            {
                if (_detachDropHint != null) root.Children.Remove(_detachDropHint);
                return;
            }
            if (_detachDropHint == null)
            {
                _detachDropHint = new Border
                {
                    BorderBrush = Theme.Accent,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(8),
                    Background = Theme.SelectionBg,
                    Margin = new Thickness(6),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = Loc.T("Main_DetachDropHere"),
                        FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20),
                    },
                };
                Grid.SetRowSpan(_detachDropHint, 99);
                Grid.SetColumnSpan(_detachDropHint, 99);
                Panel.SetZIndex(_detachDropHint, 1000);
            }
            if (!root.Children.Contains(_detachDropHint)) root.Children.Add(_detachDropHint);
        }

        // ---- the hint: the first 3 grabs of a tile ----

        // A line of the bottom plate (2026-11-25: «не новой плашкой, а в уже имеющейся, в режиме
        // предупреждения, чтобы видно было»): the warning colour, first in the plate, for 7 s.
        private TextBlock _detachHintLine;
        private System.Windows.Threading.DispatcherTimer _detachHintTimer;
        private bool DetachHintOn => _detachHintLine != null && _detachHintLine.Visibility == Visibility.Visible;

        private void MaybeShowDetachHint()
        {
            if (_config.TileDetachHints >= 3) return;
            _config.TileDetachHints++;
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("detach-hint", ex); }
            if (StatsLines == null) return;
            if (_detachHintLine == null)
            {
                _detachHintLine = new TextBlock
                {
                    FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Theme.BannerWarnText,
                    TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                };
                StatsLines.Children.Insert(0, _detachHintLine);
            }
            _detachHintLine.Text = Loc.T("Main_DetachHint");
            _detachHintLine.Visibility = Visibility.Visible;
            RenderStats();
            if (_detachHintTimer == null)
            {
                _detachHintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                _detachHintTimer.Tick += (s, e) =>
                {
                    _detachHintTimer.Stop();
                    if (_detachHintLine != null) _detachHintLine.Visibility = Visibility.Collapsed;
                    RenderStats();
                };
            }
            _detachHintTimer.Stop();
            _detachHintTimer.Start();
        }

        // ---- the tile itself outside the window ----

        // Past the window's edge the tile's own window follows the cursor (2026-11-25: «призрака
        // не надо» — no picture of it, the real thing); back inside, it goes away again (or back
        // where it stood, if it was out already). Let go outside: it stays there (EndTileDrag).
        private TileWindow _pullWin;
        private bool _pullWasOpen;
        private double _pullOldX, _pullOldY;

        private void ShowPullWindow(Point cursorDip)
        {
            var tile = _dragTile;
            var entry = tile?.Tag as AppEntry;
            if (entry == null) return;
            double x = cursorDip.X - _dragGrab.X - tile.Margin.Left;
            double y = cursorDip.Y - _dragGrab.Y - tile.Margin.Top;
            if (_pullWin == null)
            {
                TileWindow w;
                _pullWasOpen = _tileWins.TryGetValue(entry.Id, out w);
                if (_pullWasOpen) { _pullOldX = w.Left; _pullOldY = w.Top; }
                else
                {
                    OpenTileWindow(entry.Id, x, y, false);
                    _tileWins.TryGetValue(entry.Id, out w);
                }
                _pullWin = w;
                if (w == null) return;
            }
            _pullWin.Left = x;
            _pullWin.Top = y;
        }

        // Back inside / the drag ended without a drop outside.
        private void HidePullWindow()
        {
            if (_pullWin == null) return;
            var w = _pullWin;
            _pullWin = null;
            if (_pullWasOpen) { w.Left = _pullOldX; w.Top = _pullOldY; }
            else CloseTileWindow(w.TileId, false);   // never let go outside: not in the config
        }
    }
}
