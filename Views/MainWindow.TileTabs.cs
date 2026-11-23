using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace MihomoLauncher.Views
{
    // Tabs under the tiles (2026-11-16, .dev\mockups\tile-tabs.html): «Основная» first, «+» after
    // the last one; each tab exactly a tile wide on the tiles' own grid. Click — open it; double
    // click — rename it in place; right click — rename / delete (its tiles go back to «Основная»);
    // drag a tab — reorder (not «Основная»); drag a TILE onto a tab — it moves there, onto «+» —
    // into a new tab. The tabs' order is the rule order (TileTabs), so whatever changes it is
    // behind the access password and rewrites the engine's config at once, like a tile drag.
    public partial class MainWindow
    {
        private static readonly object PlusTabTag = new object();
        private const double TabHeight = 30;

        private object _tabDrop;            // TileTab / PlusTabTag under a dragged tile (or tab)
        private Border _tabPressed;         // a tab pressed, not yet clicked or dragged
        private Point _tabPressPos;
        private TileTab _tabDragging;       // a tab being dragged to a new place
        private TileTab _tabRenaming;

        // ---- the grid ----

        // Tiles and tabs share one grid exactly N tiles wide (as many as fit), left-aligned: a tab
        // stands under a tile column, a tab with two tiles does not shift them to the middle.
        private void SyncTileGrid()
        {
            double avail = ResultRoot.ActualWidth - TilesArea.Margin.Left - TilesArea.Margin.Right;
            if (double.IsNaN(avail) || avail <= 0) return;
            // The window is always sized to whole columns (GridWindowWidth), so the nearest count
            // is the one meant — never a pixel-exact fit (2026-11-21, mom's ZenBook at 250%: the
            // floor lost a column whenever the content came out a pixel narrower than the window).
            int cols = Math.Max(1, (int)Math.Round(avail / TileOuter));
            double w = cols * TileOuter;
            int meant = (int)Math.Round((ActualWidth - GridSides - FrameW) / TileOuter);
            if (cols != meant && !_inSizeMove && _gridMismatchLogged++ < 3)
            {
                var src = PresentationSource.FromVisual(this);
                double dpi = src?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                Log.Info("Окно: ширина " + ActualWidth.ToString("0.##") + " (на " + meant + " столбц.), содержимое "
                         + ResultRoot.ActualWidth.ToString("0.##") + ", сетка " + cols + " столбц., масштаб "
                         + (dpi * 100).ToString("0") + "%.");
            }
            // The panels wrap by pixels, and at a fractional scale the tiles' margins round up
            // (3.5 → 3.6 at 250%): seven tiles came out 1.4 wider than seven columns and the
            // seventh went to the next row, a blank column beside. So each panel is GridSlack wider
            // inside than its place (the negative right margin keeps its place exactly w): any
            // rounding fits, one more tile never does. The row count is the column count's, not
            // the screen's.
            _gridW = w;
            SetGridPanelWidth(TilesPanel, w, 0);
            SetGridPanelWidth(TabsPanel, w, 4);
            foreach (var fe in TilesPanel.Items.OfType<FrameworkElement>())
                if (fe.Tag == null && fe.Name == "EmptyTab") fe.Width = EmptyHintWidth(w);
            if (cols != _tileCols && _tabRenaming == null)
            {
                // «+» beside the last tab, the «new rule» tile in the last row — both depend on the
                // column count. The nested rebuild finds the count settled and stops there.
                _tileCols = cols;
                RebuildTiles();
            }
        }

        private int _tileCols = 6;
        private int _gridMismatchLogged;
        private double _gridW = double.NaN;
        private const double GridSlack = TileOuter / 2 - 1;

        private static void SetGridPanelWidth(FrameworkElement panel, double w, double top)
        {
            if (double.IsNaN(panel.Width) || Math.Abs(panel.Width - (w + GridSlack)) > 0.5) panel.Width = w + GridSlack;
            var m = new Thickness(0, top, -GridSlack, 0);
            if (panel.Margin != m) panel.Margin = m;
        }

        // Beside the «new rule» tile on the same row; on a one-column window — under it.
        private static double EmptyHintWidth(double grid) =>
            grid >= 2 * TileOuter ? grid - TileOuter - 7 : grid - 7;

        private FrameworkElement EmptyTabHint()
        {
            var grid = new Grid { Name = "EmptyTab", Height = TileHeight, Margin = new Thickness(3.5) };
            grid.Children.Add(new TextBlock
            {
                Text = Loc.T("Main_TabEmpty"), FontSize = 12.5, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 20, 0), MaxWidth = 320,
            });
            if (!double.IsNaN(_gridW)) grid.Width = EmptyHintWidth(_gridW);
            return grid;
        }

        // ---- the «new rule» tile ----

        // The last place in the row: the outline of a tile with «+» where the icon would be
        // (2026-11-17: «силуэт новой плитки… при клике сразу добавление нового правила»). Click =
        // the rule editor for a new rule, which lands on the open tab. Not draggable, no drop.
        internal static readonly object AddTileTag = new object();

        private FrameworkElement AddRuleTile()
        {
            double h = TileHeight;
            var frame = new Rectangle
            {
                Stroke = Theme.TileBorder, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 5, 4 },
                RadiusX = 9, RadiusY = 9, Fill = Brushes.Transparent,
            };
            // Where a tile has its icon: the same 40 px at the same height, a dashed circle with «+».
            var ring = new Ellipse
            {
                Width = 40, Height = 40, Stroke = Theme.TileBorder, StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            };
            var plus = new TextBlock
            {
                Text = "+", FontSize = 24, FontWeight = FontWeights.Light, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -3, 0, 0),
            };
            var badge = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
            badge.Children.Add(ring);
            badge.Children.Add(plus);
            var label = new TextBlock
            {
                Text = Loc.T("Main_AddTile"), FontSize = 13, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(badge);
            stack.Children.Add(label);

            var root = new Grid { Width = TileSizePx, Height = h, Margin = new Thickness(3.5), Cursor = Cursors.Hand, Tag = AddTileTag,
                                  ToolTip = Loc.T("Main_AddTileTip"), Background = Brushes.Transparent };
            var fill = new Border { CornerRadius = new CornerRadius(9), Background = Theme.TileBg, Opacity = 0 };
            root.Children.Add(fill);
            root.Children.Add(frame);
            root.Children.Add(stack);

            void Paint(bool hot)
            {
                fill.Opacity = hot ? 0.55 : 0;
                frame.Stroke = ring.Stroke = hot ? Theme.TileBorderHover : Theme.TileBorder;
                plus.Foreground = hot ? Theme.AccentGreen : Theme.TextMuted;
                label.Foreground = hot ? Theme.TextSecondary : Theme.TextMuted;
            }
            root.MouseEnter += (s, e) => Paint(true);
            root.MouseLeave += (s, e) => Paint(false);
            root.MouseLeftButtonDown += (s, e) => e.Handled = true;   // not the start of a tile drag
            root.MouseLeftButtonUp += (s, e) => { e.Handled = true; AddRuleFromTile(); };
            return root;
        }

        private async void AddRuleFromTile()
        {
            try
            {
                if (!await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                var dlg = new EditRuleDialog(null, _config.VpnSlots) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Result == null) return;
                var added = dlg.Result;
                added.TabId = TileTabs.CurrentIdForNew(_config);   // the tab open now
                await CommitLiveEditAsync(() =>
                {
                    _config.Apps.Add(added);
                    TileTabs.Normalize(_config);
                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(true);
                Log.Info("Правило «" + added.Name + "» добавлено с плитки «Новое правило».");
                RebuildTiles();
            }
            catch (Exception ex)
            {
                try { await OverlayDialog.MessageAsync(this, Loc.T("Set_Error"), ex.Message).ConfigureAwait(true); } catch { }
            }
        }

        // ---- the tabs row ----

        private void RenderTabs()
        {
            TileTabs.Ensure(_config);
            TabsPanel.Children.Clear();
            var cur = TileTabs.Current(_config);
            // The tabs fill their last row exactly → «+» would wrap onto a row of its own. Instead
            // the last tab gives up room for it: tab + «+» together are one tile wide (2026-11-17).
            int n = _config.Tabs.Count;
            bool squeeze = n > 0 && n % Math.Max(1, _tileCols) == 0;
            for (int i = 0; i < n; i++)
            {
                var t = _config.Tabs[i];
                double w = squeeze && i == n - 1 ? TileSizePx - TabHeight - 7 : TileSizePx;
                TabsPanel.Children.Add(t == _tabRenaming ? RenameBox(t, w) : TabChip(t, t == cur, w));
            }
            TabsPanel.Children.Add(PlusChip());
        }

        private Border TabChip(TileTab t, bool on, double width)
        {
            var name = new TextBlock
            {
                Text = TileTabs.NameOf(t), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = on ? Theme.TextPrimary : Theme.TextSecondary, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var body = new Grid();
            body.Children.Add(name);
            var chip = new Border
            {
                Tag = t, Width = width, Height = TabHeight, Margin = new Thickness(3.5),
                CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 0),
                Cursor = Cursors.Hand, Child = body, ToolTip = Loc.T("Main_TabTip"),
            };
            // «✕» in the top-right corner while the pointer is on the tab (2026-11-17, instead of the
            // right-click menu); red under the pointer. «Основная» has none — it can't be deleted.
            Border close = null;
            if (t.Id != TileTabs.MainId)
            {
                close = TabCloseButton(t);
                body.Children.Add(close);
            }
            PaintTab(chip, false);
            chip.MouseEnter += (s, e) =>
            {
                if (_tabDrop == null) chip.BorderBrush = Theme.TileBorderHover;
                if (close != null && _tabDragging == null && !TileDragActive) close.Visibility = Visibility.Visible;
            };
            chip.MouseLeave += (s, e) =>
            {
                PaintTab(chip, _tabDrop == (object)t);
                if (close != null) close.Visibility = Visibility.Collapsed;
            };
            chip.MouseLeftButtonDown += OnTabDown;
            chip.MouseMove += OnTabMove;
            chip.MouseLeftButtonUp += OnTabUp;
            return chip;
        }

        private Border TabCloseButton(TileTab t)
        {
            var cross = new Path
            {
                Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"),
                Stroke = Theme.TextOnColor, StrokeThickness = 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var close = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, -5, 0), Child = cross, Visibility = Visibility.Collapsed,
                ToolTip = Loc.T("Main_TabDeleteTip"),
            };
            // The same dull red as the tile's bin (2026-11-17), not the bright Windows close red.
            close.MouseEnter += (s, e) => close.Background = TileBinHotBg;
            close.MouseLeave += (s, e) => close.Background = Brushes.Transparent;
            // Its own press and release: not a click on the tab, not the start of a tab drag.
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; DeleteTab(t); };
            return close;
        }

        // An empty tab goes at once. One with tiles asks (2026-11-17): «Перенести» — they move to
        // «Основная»; «Удалить всё» — the tab goes with its tiles and their rules; «Отмена».
        // Rules on it → the rule order (or the rules) change → password.
        private async void DeleteTab(TileTab t)
        {
            try
            {
                var name = TileTabs.NameOf(t);
                var mainName = TileTabs.NameOf(_config.Tabs[0]);
                var rules = _config.Apps.Where(a => a != null && TileTabs.OnTab(_config, a, t)).ToList();
                int tiles = rules.Count(a => a.HasTileTarget);
                bool dropRules = false;
                if (tiles > 0)
                {
                    int answer = await OverlayDialog.ChoiceAsync(this,
                        Loc.T("Main_TabDelTitle", name),
                        Loc.T("Main_TabDelBody", tiles, mainName),
                        Loc.T("Main_TabDelMove"), Loc.T("Main_TabDelAll"), Loc.T("Dlg_Cancel")).ConfigureAwait(true);
                    if (answer == 0) return;
                    dropRules = answer == 2;
                }
                if (rules.Count > 0 && !await LockPrompt.RequireAsync(this, _config).ConfigureAwait(true)) return;
                if (_config.Tabs.IndexOf(t) < 0) return;   // gone meanwhile
                if (dropRules)
                {
                    // The rules go first (a routing change, like the tile's bin), then the empty tab.
                    var doomed = rules.Where(a => !a.IsBuiltIn).ToList();
                    await CommitLiveEditAsync(() =>
                    {
                        foreach (var a in doomed) _config.Apps.Remove(a);
                        return System.Threading.Tasks.Task.CompletedTask;
                    }).ConfigureAwait(true);
                    foreach (var a in doomed) _platePick.Remove(a.Id);
                    TileTabs.Delete(_config, t);
                    SaveTabs("Вкладки: удалена «" + name + "» вместе с правилами: " + string.Join(", ", doomed.Select(a => a.Name)) + ".", orderChanged: true);
                    return;
                }
                TileTabs.Delete(_config, t);
                SaveTabs(rules.Count > 0
                    ? "Вкладки: удалена «" + name + "», её правила (" + rules.Count + ") — в «" + mainName + "»."
                    : "Вкладки: удалена пустая «" + name + "».", orderChanged: rules.Count > 0);
            }
            catch (Exception ex)
            {
                Log.Error("tab-delete", ex);
                try { await OverlayDialog.MessageAsync(this, Loc.T("Set_Error"), ex.Message).ConfigureAwait(true); } catch { }
            }
        }

        private Border PlusChip()
        {
            // Drawn like the «Добавить» tile (2026-11-17): dashed outline, light «+», on hover a soft
            // fill, a lighter outline and a green «+». A tile dragged over it still turns it green.
            var fill = new Border { CornerRadius = new CornerRadius(7), Background = Theme.TileBg, Opacity = 0 };
            var frame = new Rectangle
            {
                Stroke = Theme.TileBorder, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 },
                RadiusX = 7, RadiusY = 7, Fill = Brushes.Transparent,
            };
            var plus = new TextBlock
            {
                Text = "+", FontSize = 18, FontWeight = FontWeights.Light, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0),
            };
            var body = new Grid();
            body.Children.Add(fill);
            body.Children.Add(frame);
            body.Children.Add(plus);
            var chip = new Border
            {
                Tag = PlusTabTag, Width = TabHeight, Height = TabHeight, Margin = new Thickness(3.5),
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = Loc.T("Main_TabAddTip"), Child = body,
            };
            PaintTab(chip, false);
            chip.MouseEnter += (s, e) => { if (_tabDrop == null) PaintPlus(chip, false, true); };
            chip.MouseLeave += (s, e) => PaintTab(chip, _tabDrop == PlusTabTag);
            chip.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                // Just made — not opened, not asking for a name (2026-11-16: «просто создаём и всё»);
                // a double click renames it whenever wanted.
                var tab = TileTabs.Add(_config);
                SaveTabs("Вкладки: добавлена «" + TileTabs.NameOf(tab) + "».", orderChanged: false);
            };
            return chip;
        }

        // Normal / open / a tile (or tab) hovering over it to be dropped.
        private void PaintTab(Border chip, bool drop)
        {
            if (chip.Tag == PlusTabTag) { PaintPlus(chip, drop, false); return; }
            var t = chip.Tag as TileTab;
            bool on = t != null && t == TileTabs.Current(_config);
            chip.Background = drop ? Theme.PlateGreenBgHover : on ? Theme.ChipBgHover : Theme.PlateNeutralBg;
            chip.BorderBrush = drop ? Theme.PlateGreenBorderHover : on ? Theme.TileBorderHover : Theme.CardBorder;
        }

        // «+»: fill / dashed outline / the sign inside its Grid.
        private static void PaintPlus(Border chip, bool drop, bool hot)
        {
            var g = chip.Child as Grid;
            if (g == null || g.Children.Count < 3) return;
            var fill = (Border)g.Children[0];
            var frame = (Rectangle)g.Children[1];
            var plus = (TextBlock)g.Children[2];
            fill.Background = drop ? Theme.PlateGreenBgHover : Theme.TileBg;
            fill.Opacity = drop ? 1 : hot ? 0.55 : 0;
            frame.Stroke = drop ? Theme.PlateGreenBorderHover : hot ? Theme.TileBorderHover : Theme.TileBorder;
            plus.Foreground = drop || hot ? Theme.AccentGreen : Theme.TextMuted;
        }

        // ---- rename in place ----

        private FrameworkElement RenameBox(TileTab t, double width)
        {
            var box = new TextBox
            {
                Text = TileTabs.NameOf(t), FontSize = 12.5, MinHeight = 0, Height = TabHeight, Width = width,
                Margin = new Thickness(3.5), Padding = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 40,
            };
            bool done = false;
            // A click anywhere else in the window ends the rename and keeps what was typed
            // (2026-11-17): clicking empty space moves no keyboard focus, so LostKeyboardFocus
            // alone never fired and the box stayed open.
            MouseButtonEventHandler outside = null;
            outside = (s, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, box)) return;
                Finish(true);
            };
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                PreviewMouseDown -= outside;
                Deactivated -= OnDeactivated;
                _tabRenaming = null;
                var name = (box.Text ?? "").Trim();
                if (keep && name.Length > 0 && name != TileTabs.NameOf(t))
                {
                    var old = TileTabs.NameOf(t);
                    t.Name = name;
                    SaveTabs("Вкладки: «" + old + "» → «" + name + "».", orderChanged: false);
                }
                else RenderTabs();
            }
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
            };
            void OnDeactivated(object s, EventArgs e) => Finish(true);   // switched to another window
            box.LostKeyboardFocus += (s, e) => Finish(true);
            box.Loaded += (s, e) =>
            {
                if (done) return;
                PreviewMouseDown += outside;
                Deactivated += OnDeactivated;
                box.Focus();
                box.SelectAll();
            };
            return box;
        }

        private static bool IsInside(DependencyObject d, DependencyObject root)
        {
            while (d != null)
            {
                if (d == root) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        // ---- click / double click / drag a tab ----

        private void OnTabDown(object sender, MouseButtonEventArgs e)
        {
            var chip = sender as Border;
            var t = chip?.Tag as TileTab;
            if (t == null) return;
            e.Handled = true;
            if (e.ClickCount >= 2) { _tabPressed = null; _tabRenaming = t; RenderTabs(); return; }
            _tabPressed = chip;
            _tabPressPos = e.GetPosition(TabsPanel);
        }

        private void OnTabMove(object sender, MouseEventArgs e)
        {
            if (_tabDragging != null)
            {
                SetTabDrop(TabUnder(e, _tabDragging));
                return;
            }
            if (_tabPressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            var t = _tabPressed.Tag as TileTab;
            var p = e.GetPosition(TabsPanel);
            if (Math.Abs(p.X - _tabPressPos.X) < TileDragThreshold && Math.Abs(p.Y - _tabPressPos.Y) < TileDragThreshold) return;
            if (t == null || t.Id == TileTabs.MainId) return;   // «Основная» stays first
            if (!AccessLock.IsOpen(_config)) { _tabPressed = null; _ = LockPrompt.RequireAsync(this, _config); return; }
            AccessLock.Touch();
            _tabDragging = t;
            _tabPressed.Opacity = 0.5;
            Mouse.Capture(_tabPressed);
        }

        private void OnTabUp(object sender, MouseButtonEventArgs e)
        {
            var chip = sender as Border;
            var t = chip?.Tag as TileTab;
            e.Handled = true;
            if (_tabDragging != null)
            {
                var moving = _tabDragging;
                var target = _tabDrop as TileTab;
                _tabDragging = null;
                Mouse.Capture(null);
                SetTabDrop(null);
                int to = target == null ? -1 : _config.Tabs.IndexOf(target);
                if (to > 0 && target != moving)
                {
                    _config.Tabs.Remove(moving);
                    _config.Tabs.Insert(to, moving);
                    TileTabs.Normalize(_config);
                    SaveTabs("Вкладки: новый порядок — " + string.Join(", ", _config.Tabs.Select(TileTabs.NameOf)), orderChanged: true);
                }
                else RenderTabs();
                return;
            }
            if (_tabPressed != chip || t == null) { _tabPressed = null; return; }
            _tabPressed = null;
            if (_config.CurrentTabId == t.Id || (t.Id == TileTabs.MainId && TileTabs.Current(_config) == t)) return;
            _config.CurrentTabId = t.Id;
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tab-switch", ex); }
            RebuildTiles();
        }

        // ---- a tile dropped onto a tab (MainWindow.TileDrag) ----

        // The tab (or «+») under the cursor, other than `except` and the tab open now — dropping a
        // tile onto its own tab means nothing.
        private object TabUnder(MouseEventArgs e, TileTab except = null)
        {
            var cur = TileTabs.Current(_config);
            foreach (var fe in TabsPanel.Children.OfType<FrameworkElement>())
            {
                var p = e.GetPosition(fe);
                if (p.X < 0 || p.Y < 0 || p.X > fe.ActualWidth || p.Y > fe.ActualHeight) continue;
                if (fe.Tag == PlusTabTag) return except == null ? PlusTabTag : null;   // a tab can't go onto «+»
                var t = fe.Tag as TileTab;
                if (t == null || t == except) return null;
                if (except == null && t == cur) return null;
                return t;
            }
            return null;
        }

        private void SetTabDrop(object target)
        {
            if (target == _tabDrop) return;
            _tabDrop = target;
            foreach (var chip in TabsPanel.Children.OfType<Border>())
                PaintTab(chip, chip.Tag != null && chip.Tag == target);
        }

        private void MoveTileToTab(AppEntry entry, object target)
        {
            try
            {
                if (entry == null || entry.IsBuiltIn) return;
                var tab = target as TileTab;
                bool made = tab == null;
                if (made) tab = TileTabs.Add(_config);
                TileTabs.MoveTo(_config, entry, tab);
                SaveTabs("Плитка «" + entry.Name + "» → вкладка «" + TileTabs.NameOf(tab) + "»" + (made ? " (новая)" : "") + ".", orderChanged: true);
            }
            catch (Exception ex) { Log.Error("tile-to-tab", ex); }
        }

        // Persist, log, redraw; when the rule order changed — rewrite the engine's config now.
        private void SaveTabs(string log, bool orderChanged)
        {
            try { AppConfigStore.Save(_config); } catch (Exception ex) { Log.Error("tabs-save", ex); }
            if (!string.IsNullOrEmpty(log)) Log.Info(log);
            if (orderChanged) ApplyRuleOrder();
            RebuildTiles();
        }
    }
}
