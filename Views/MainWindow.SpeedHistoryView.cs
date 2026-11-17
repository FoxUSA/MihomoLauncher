using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // A tile's speed over the last 1 / 6 / 24 hours (2026-11-16, from the mock-up): now · peak (with
    // its time) · average · downloaded · uploaded, and the chart — one bar per 1 / 2 / 5 minutes,
    // coloured by speed class over faint class bands, upload as a thin line, hover for the exact
    // figures. Own movable window (SpeedWindow), refreshed every few seconds while open. Data: SpeedHistory.
    public partial class MainWindow
    {
        private async Task ShowSpeedHistoryAsync(AppEntry entry)
        {
            if (entry == null) return;
            int hours = 24;

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var stats = new UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 10) };
            // Its own window now (2026-11-16), no longer squeezed into the main window's height.
            var chart = new SpeedDayChart { Height = 220 };
            var chartBox = new Border
            {
                Background = Theme.BadgeNeutralBg,
                BorderBrush = Theme.BadgeNeutralBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 6, 6, 2),
                Child = chart,
            };
            var legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var kv in new[]
            {
                new KeyValuePair<Brush, string>(Theme.SpeedRed, Loc.T("Main_SpeedLegendRed")),
                new KeyValuePair<Brush, string>(Theme.SpeedAmber, Loc.T("Main_SpeedLegendAmber")),
                new KeyValuePair<Brush, string>(Theme.SpeedGreen, Loc.T("Main_SpeedLegendGreen")),
                new KeyValuePair<Brush, string>(Theme.SpeedBlue, Loc.T("Main_SpeedLegendBlue")),
            })
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2) };
                item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = kv.Key, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = kv.Value, FontSize = 11.5, Foreground = Theme.TextSecondary });
                legend.Children.Add(item);
            }
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2) };
                item.Children.Add(new Border { Width = 10, Height = 2, Background = Theme.TextSecondary, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = Loc.T("Main_SpeedLegendUp"), FontSize = 11.5, Foreground = Theme.TextSecondary });
                legend.Children.Add(item);
            }

            // All time + «Очистить историю» (2026-11-16): the period figures are in the cards above.
            var totalRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            totalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var totalText = new TextBlock { FontSize = 12.5, Foreground = Theme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            totalRow.Children.Add(totalText);
            // A real button like «Закрыть» (same size), red as a destructive action.
            var clear = new Wpf.Ui.Controls.Button
            {
                Content = Loc.T("Main_SpeedClear"),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                FontSize = 14,
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // «Очистить историю» + «Закрыть» on the totals line, not a row of their own (2026-11-16).
            // Bottom-aligned: the button's lower edge meets the last line («Отдано»).
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            buttons.Children.Add(clear);
            Grid.SetColumn(buttons, 1);
            totalRow.Children.Add(buttons);

            // «Кто и куда» (MainWindow.SpeedWho): the exit, the programs, their addresses.
            Window whoOwner = null;
            Action whoStop;
            var whoHolder = new ContentControl();
            var whoView = whoHolder;
            var body = new StackPanel();
            body.Children.Add(tabs);
            body.Children.Add(stats);
            body.Children.Add(chartBox);
            body.Children.Add(legend);
            body.Children.Add(whoView);
            body.Children.Add(totalRow);

            UIElement Stat(string key, string value, string unit, Brush valueBrush = null)
            {
                var b = new Border
                {
                    Background = Theme.BadgeNeutralBg,
                    BorderBrush = Theme.BadgeNeutralBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 6, 9, 7),
                    Margin = new Thickness(0, 0, 6, 0),
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = key, FontSize = 11.5, Foreground = Theme.TextMuted });
                var v = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = valueBrush ?? Theme.TextPrimary, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                v.Inlines.Add(new System.Windows.Documents.Run(value));
                if (!string.IsNullOrEmpty(unit))
                    v.Inlines.Add(new System.Windows.Documents.Run(" " + unit) { FontSize = 10.5, FontWeight = FontWeights.Normal, Foreground = Theme.TextSecondary });
                sp.Children.Add(v);
                b.Child = sp;
                return b;
            }

            string Volume(double bytes)
            {
                double mb = bytes / (1024 * 1024);
                if (mb >= 1024) return (mb / 1024).ToString(mb / 1024 < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedGB");
                return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedMB");
            }

            void Refresh()
            {
                int minutes = hours * 60;
                var data = SpeedHistory.Get(entry.Id, minutes);
                chart.Set(data, minutes, hours == 24 ? 5 : hours == 6 ? 2 : 1, hours == 24 ? 180 : hours == 6 ? 60 : 10);

                double[] cur;
                double nowDown = _speedNow.TryGetValue(entry.Id, out cur) ? cur[0] : 0;
                var peak = data.OrderByDescending(b => b.PeakDown).FirstOrDefault();
                double secs = data.Sum(b => b.Secs), down = data.Sum(b => b.Down), up = data.Sum(b => b.Up);
                stats.Children.Clear();
                stats.Children.Add(Stat(Loc.T("Main_SpeedNow"), Mbit(nowDown), Loc.T("Main_SpeedMbit"), nowDown * 8 / 1e6 >= 0.005 ? SpeedBrush(nowDown * 8 / 1e6) : null));   // «0» plain, not the track grey
                stats.Children.Add(Stat(Loc.T("Main_SpeedPeak"), peak == null ? "—" : Mbit(peak.PeakDown),
                    peak == null ? "" : "· " + SpeedHistory.TimeOf(peak.Minute).ToLocalTime().ToString("HH:mm")));
                stats.Children.Add(Stat(Loc.T("Main_SpeedAvgLong"), secs > 0 ? Mbit(down / secs) : "—", Loc.T("Main_SpeedMbit")));
                stats.Children.Add(Stat(Loc.T("Main_SpeedDown"), Volume(down), ""));
                stats.Children.Add(Stat(Loc.T("Main_SpeedUp"), Volume(up), ""));

                var total = SpeedHistory.GetTotal(entry.Id);
                totalText.Text = total == null ? Loc.T("Main_SpeedTotalNone")
                    // Three lines, built here: a &#10; in the .xaml string collapses to a space.
                    : Loc.T("Main_SpeedTotal", SpeedHistory.TimeOf(total.Since).ToLocalTime().ToString("dd.MM.yyyy HH:mm"))
                      + "\n" + Loc.T("Main_SpeedTotalDown", Volume(total.Down))
                      + "\n" + Loc.T("Main_SpeedTotalUp", Volume(total.Up));

                tabs.Children.Clear();
                foreach (var h in new[] { 1, 6, 24 })
                {
                    bool on = h == hours;
                    var tab = new Border
                    {
                        Background = on ? Theme.ChipBgHover : Theme.BadgeNeutralBg,
                        BorderBrush = on ? Theme.TileBorderHover : Theme.BadgeNeutralBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12, 4, 12, 5),
                        Margin = new Thickness(0, 0, 6, 0),
                        Cursor = Cursors.Hand,
                        Child = new TextBlock
                        {
                            Text = Loc.T(h == 1 ? "Main_SpeedTab1" : h == 6 ? "Main_SpeedTab6" : "Main_SpeedTab24"),
                            FontSize = 12.5,
                            Foreground = on ? Theme.TextPrimary : Theme.TextSecondary,
                        },
                    };
                    int pick = h;
                    tab.MouseLeftButtonUp += (s, e) => { e.Handled = true; hours = pick; Refresh(); };
                    tabs.Children.Add(tab);
                }
            }
            Refresh();

            // A window of its own, movable off the launcher (2026-11-16: «залочено внутри утилиты»).
            // Not modal: the tiles stay usable while it is open.
            var win = new SpeedWindow(Loc.T("Main_SpeedTitle", entry.IsAllTraffic ? AllTrafficName() : entry.Name), body) { Owner = this };
            whoOwner = win;
            whoHolder.Content = BuildSpeedWho(entry, whoOwner, false, null, out whoStop);
            win.Closed += (s, e) => whoStop();

            var close = new Wpf.Ui.Controls.Button
            {
                Content = Loc.T("Dlg_Close"),
                FontSize = 14,
                Padding = new Thickness(20, 8, 20, 8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.Click += (s, e) => win.Close();
            buttons.Children.Add(close);

            clear.Click += async (s, e) =>
            {
                var name = entry.IsAllTraffic ? AllTrafficName() : entry.Name;
                if (!await OverlayDialog.ConfirmAsync(win, Loc.T("Main_SpeedClearTitle"), Loc.T("Main_SpeedClearBody", name),
                        Loc.T("Main_SpeedClearOk"), Loc.T("Set_BtnCancel"), Wpf.Ui.Controls.ControlAppearance.Danger).ConfigureAwait(true)) return;
                SpeedHistory.Clear(entry.Id);
                Log.Info("Скорость: история плитки «" + name + "» очищена.");
                Refresh();
            };

            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) => Refresh();
            timer.Start();
            win.Closed += (s, e) => timer.Stop();
            win.Show();
            await Task.CompletedTask.ConfigureAwait(true);
        }

        // The chart itself: log scale 0,01…200 Mbit/s, the four class bands faint behind, grid at
        // 0,1 / 1 / 10 / 50 / 100, a bar per slot, upload as a line, time ticks along the bottom.
        internal sealed class SpeedDayChart : FrameworkElement
        {
            private sealed class Slot { public DateTime Start; public double Down, Up, Secs, Peak; }
            private List<Slot> _slots = new List<Slot>();
            private int _per = 5, _tickEvery = 180;
            private double _hoverX = -1;

            public SpeedDayChart()
            {
                MouseMove += (s, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
                MouseLeave += (s, e) => { _hoverX = -1; InvalidateVisual(); };
                Cursor = Cursors.Cross;
            }

            public void Set(List<SpeedHistory.Bucket> data, int minutes, int perMinutes, int tickEveryMinutes)
            {
                _per = perMinutes;
                _tickEvery = tickEveryMinutes;
                long end = SpeedHistory.MinuteOf(DateTime.UtcNow);
                long start = end - minutes + 1;
                // Slots aligned to round times, so «12:00» lands on a slot edge.
                start -= ((start % perMinutes) + perMinutes) % perMinutes;
                int count = (int)((end - start) / perMinutes) + 1;
                var slots = new List<Slot>(count);
                for (int i = 0; i < count; i++) slots.Add(new Slot { Start = SpeedHistory.TimeOf(start + (long)i * perMinutes).ToLocalTime() });
                foreach (var b in data)
                {
                    int i = (int)((b.Minute - start) / perMinutes);
                    if (i < 0 || i >= count) continue;
                    var sl = slots[i];
                    sl.Down += b.Down; sl.Up += b.Up; sl.Secs += b.Secs;
                    if (b.PeakDown > sl.Peak) sl.Peak = b.PeakDown;
                }
                _slots = slots;
                InvalidateVisual();
            }

            private const double Lo = -2, Hi = 2.30103;   // log10(0.01) … log10(200)

            // Red low → amber → green → blue high, laid on the chart's own pixels (Absolute), the
            // colours centred inside their bands so the change between them is soft.
            private static LinearGradientBrush LevelGradient(Func<double, double> y, byte alpha)
            {
                double top = y(200), bottom = y(0.01), span = bottom - top;
                double At(double mbit) => Math.Max(0, Math.Min(1, (y(mbit) - top) / span));
                Color A(SolidColorBrush b) => Color.FromArgb(alpha, b.Color.R, b.Color.G, b.Color.B);
                var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, top), EndPoint = new Point(0, bottom) };
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedBlue), At(100)));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedGreen), At(22)));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedAmber), At(3.2)));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedRed), At(0.3)));
                return g;
            }

            // A smooth curve through the points (Catmull-Rom as cubic Béziers), kept inside the
            // chart vertically; `area` closes it down to the floor for the fill.
            private static StreamGeometry Smooth(List<Point> p, double top, double floor, bool area, double baseY)
            {
                double Cy(double v) => Math.Max(top, Math.Min(floor, v));
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    if (area)
                    {
                        ctx.BeginFigure(new Point(p[0].X, baseY), true, true);
                        ctx.LineTo(p[0], false, false);
                    }
                    else ctx.BeginFigure(p[0], false, false);
                    for (int i = 0; i < p.Count - 1; i++)
                    {
                        var p0 = i > 0 ? p[i - 1] : p[i];
                        var p1 = p[i];
                        var p2 = p[i + 1];
                        var p3 = i + 2 < p.Count ? p[i + 2] : p2;
                        var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Cy(p1.Y + (p2.Y - p0.Y) / 6));
                        var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Cy(p2.Y - (p3.Y - p1.Y) / 6));
                        ctx.BezierTo(c1, c2, p2, true, true);
                    }
                    if (area) ctx.LineTo(new Point(p[p.Count - 1].X, baseY), false, false);
                }
                g.Freeze();
                return g;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double W = ActualWidth, H = ActualHeight;
                if (W <= 0 || H <= 0) return;
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                FormattedText Txt(string s, double size, Brush b) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, dpi);

                const double L = 40, R = 6, T = 6, B = 18;
                double cw = W - L - R, ch = H - T - B;
                double Y(double mbit) => T + ch - (Math.Log10(Math.Max(0.01, Math.Min(200, mbit))) - Lo) / (Hi - Lo) * ch;
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

                // Grid: faint solid lines, the class boundaries (1 / 10 / 50) a touch stronger.
                foreach (var v in new[] { 0.1, 1, 10, 50, 100 })
                {
                    double y = Math.Round(Y(v)) + 0.5;
                    bool edge = v == 1 || v == 10 || v == 50;
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(edge ? (byte)34 : (byte)18, 255, 255, 255)), 1), new Point(L, y), new Point(W - R, y));
                    var t = Txt(v.ToString(CultureInfo.CurrentCulture), 10.5, Theme.TextMuted);
                    dc.DrawText(t, new Point(L - 8 - t.Width, y - t.Height / 2));
                }

                int n = _slots.Count;
                if (n == 0) { dc.Pop(); return; }
                double bw = cw / n;

                // The speed colour by height (2026-11-16 «график посимпатичнее»): one vertical
                // gradient pinned to the chart — red at the bottom, amber, green, blue at the top —
                // so the line and its fill take the colour of the level they reach.
                var stroke = LevelGradient(Y, 255);
                var fill = LevelGradient(Y, 70);
                var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(40, 0, 0, 0), 1));

                // Runs of measured slots; a slot nobody measured breaks the line.
                var runs = new List<List<Point>>();
                var upRuns = new List<List<Point>>();
                List<Point> run = null, upRun = null;
                for (int i = 0; i < n; i++)
                {
                    var s = _slots[i];
                    if (s.Secs <= 0) { run = null; upRun = null; continue; }
                    if (run == null) { runs.Add(run = new List<Point>()); upRuns.Add(upRun = new List<Point>()); }
                    double x = L + (i + 0.5) * bw;
                    run.Add(new Point(x, Y(s.Down / s.Secs * 8 / 1e6)));
                    upRun.Add(new Point(x, Y(s.Up / s.Secs * 8 / 1e6)));
                }
                bool any = runs.Count > 0;
                double floor = T + ch;
                foreach (var r in runs)
                {
                    if (r.Count == 1)
                    {
                        dc.DrawEllipse(stroke, null, r[0], 2.2, 2.2);
                        continue;
                    }
                    var area = Smooth(r, T, floor, true, floor);
                    dc.PushOpacityMask(fade);
                    dc.DrawGeometry(fill, null, area);
                    dc.Pop();
                    dc.DrawGeometry(null, new Pen(stroke, 1.8) { LineJoin = PenLineJoin.Round }, Smooth(r, T, floor, false, floor));
                }
                var upPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0xDA, 0xDA, 0xE0)), 1.1) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) };
                foreach (var r in upRuns)
                    if (r.Count > 1) dc.DrawGeometry(null, upPen, Smooth(r, T, floor, false, floor));
                // Time ticks.
                var tickPen = new Pen(Theme.TextMuted, 1);
                for (int i = 0; i < n; i++)
                {
                    var t0 = _slots[i].Start;
                    int mins = t0.Hour * 60 + t0.Minute;
                    if (mins % _tickEvery >= _per) continue;
                    double x = L + i * bw;
                    dc.DrawLine(tickPen, new Point(x, T + ch), new Point(x, T + ch + 3));
                    var t = Txt(t0.ToString("HH:mm"), 10.5, Theme.TextMuted);
                    double tx = Math.Max(L, Math.Min(W - R - t.Width, x - t.Width / 2));
                    dc.DrawText(t, new Point(tx, T + ch + 3));
                }
                if (!any)
                {
                    var t = Txt(Loc.T("Main_SpeedNoData"), 12.5, Theme.TextSecondary);
                    dc.DrawText(t, new Point(L + (cw - t.Width) / 2, T + (ch - t.Height) / 2));
                }

                // Hover: a line and the figures of that slot.
                if (_hoverX >= L && _hoverX < W - R)
                {
                    int i = Math.Min(n - 1, (int)((_hoverX - L) / bw));
                    var s = _slots[i];
                    double x = L + (i + 0.5) * bw;
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1), new Point(x, T), new Point(x, T + ch));
                    if (s.Secs > 0)
                    {
                        double dm = s.Down / s.Secs * 8 / 1e6;
                        dc.DrawEllipse(SpeedBrush(Math.Max(0.006, dm)), new Pen(Brushes.White, 1.5), new Point(x, Y(dm)), 3.5, 3.5);
                    }
                    string time = s.Start.ToString("HH:mm") + (_per > 1 ? "–" + s.Start.AddMinutes(_per).ToString("HH:mm") : "");
                    string text = s.Secs <= 0 ? time + " · " + Loc.T("Main_SpeedNoDataShort")
                        : time + " · ↓ " + Mbit(s.Down / s.Secs) + " · ↑ " + Mbit(s.Up / s.Secs) + " " + Loc.T("Main_SpeedMbit")
                          + (_per > 1 ? " · " + Loc.T("Main_SpeedPeak").ToLowerInvariant() + " " + Mbit(s.Peak) : "");
                    var t = Txt(text, 11.5, Theme.TextPrimary);
                    double bx = Math.Max(2, Math.Min(W - t.Width - 14, x + 10));
                    var r = new Rect(bx, T + 2, t.Width + 12, t.Height + 6);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 0x15, 0x15, 0x18)), new Pen(Theme.PickerBorder, 1), r, 5, 5);
                    dc.DrawText(t, new Point(bx + 6, T + 5));
                }
                dc.Pop();
            }
        }
    }
}
