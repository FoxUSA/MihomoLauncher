using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // A tile's speed: «Сейчас» and the last 1 / 6 / 12 / 24 hours. Laid out after the «Здоровье
    // сервера» page (2026-11-21, .dev\mockups\speed-window-v2.html, the person's pick: plain scale
    // with headroom, colour by speed, peaks as diamonds): the speed now in big figures, the route,
    // a mirrored chart (download up, upload down from zero), under it «среднее · пик · объём» with
    // the shift to the period before, beside it the tile's records. «Кто и куда» below.
    // Own movable window (SpeedWindow), refreshed while open. Data: SpeedHistory.
    public partial class MainWindow
    {
        private async Task ShowSpeedHistoryAsync(AppEntry entry)
        {
            if (entry == null) return;
            // 0 = «Сейчас» (2026-11-20): first and opened by default — live, the chart over the last
            // hour, the figures and «кто и куда» over the last minute. 1 / 6 / 12 / 24 = the archive
            // of that period, read once when the tab is clicked and then still.
            int hours = 0;
            Action<int> whoSetHours = null;

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            // The speed now, big: ↓ in its speed colour, ↑ plain.
            var heroDown = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
            var heroUp = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextSecondary, Margin = new Thickness(18, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom };
            var hero = new StackPanel { Orientation = Orientation.Horizontal };
            hero.Children.Add(heroDown);
            hero.Children.Add(heroUp);
            var sub = new TextBlock { FontSize = 12.5, Foreground = Theme.TextSecondary, Margin = new Thickness(0, 2, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis };

            var chart = new SpeedDayChart { Height = 230 };
            var table = new Grid { Margin = new Thickness(4, 8, 4, 2) };
            var chartInner = new StackPanel();
            chartInner.Children.Add(chart);
            chartInner.Children.Add(new Border { Height = 1, Background = Theme.BadgeNeutralBorder, Margin = new Thickness(4, 6, 4, 0) });
            chartInner.Children.Add(table);
            var chartBox = SpeedPanel(chartInner, new Thickness(6, 8, 6, 6));

            var records = new StackPanel();
            var recordsBox = SpeedPanel(records, new Thickness(12, 10, 12, 8));
            recordsBox.Margin = new Thickness(10, 0, 0, 0);

            var middle = new Grid();
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            middle.Children.Add(chartBox);
            Grid.SetColumn(recordsBox, 1);
            middle.Children.Add(recordsBox);

            // The note + «Очистить» + «Закрыть» on one line.
            var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var note = new TextBlock { FontSize = 12, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            footer.Children.Add(note);
            // A real button like «Закрыть» (same size): the muted danger role (App.xaml), a destructive action.
            var clear = new Wpf.Ui.Controls.Button
            {
                Content = Loc.T("Main_SpeedClear"),
                Style = (Style)Application.Current.FindResource("DangerButtonStyle"),
                FontSize = 14,
                Padding = new Thickness(20, 8, 20, 8),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            buttons.Children.Add(clear);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);

            // «Кто и куда» (MainWindow.SpeedWho): the exit, the programs, their addresses.
            Window whoOwner = null;
            Action whoStop;
            var whoHolder = new ContentControl { Margin = new Thickness(0, 10, 0, 0) };
            var body = new StackPanel();
            body.Children.Add(tabs);
            body.Children.Add(hero);
            body.Children.Add(sub);
            body.Children.Add(middle);
            body.Children.Add(whoHolder);
            body.Children.Add(footer);

            string Volume(double bytes)
            {
                double mb = bytes / (1024 * 1024);
                if (mb >= 1024) return (mb / 1024).ToString(mb / 1024 < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedGB");
                return mb.ToString(mb < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Loc.T("Main_SpeedMB");
            }

            // Where the tile's traffic goes now: the VPN (and where it exits) / direct / blocked.
            string Route()
            {
                if (entry.IsBlockMode) return Loc.T("Main_SpeedBlocked");
                string proxy;
                ProxySlotInfo info;
                if (_assignment.TryGetValue(entry.Id, out proxy) && _proxySlot.TryGetValue(proxy, out info) && info != null)
                {
                    string geo;
                    return _proxyGeo.TryGetValue(proxy, out geo) && !string.IsNullOrEmpty(geo)
                        ? Loc.T("Main_SpeedViaGeo", info.Name, geo) : Loc.T("Main_SpeedVia", info.Name);
                }
                return Loc.T("Main_SpeedDirect");
            }

            // The figures this second, on every tab (2026-11-20: «сквозная»).
            void UpdateNow()
            {
                double[] cur;
                double d = 0, u = 0;
                if (_speedNow.TryGetValue(entry.Id, out cur)) { d = cur[0]; u = cur.Length > 1 ? cur[1] : 0; }
                double dm = d * 8 / 1e6;
                heroDown.Inlines.Clear();
                heroDown.Inlines.Add(new System.Windows.Documents.Run("↓ " + Mbit(d)));
                heroDown.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T("Main_SpeedMbit")) { FontSize = 13, FontWeight = FontWeights.Normal, Foreground = Theme.TextSecondary });
                heroDown.Foreground = dm >= 0.005 ? SpeedBrush(dm) : Theme.TextPrimary;
                heroUp.Inlines.Clear();
                heroUp.Inlines.Add(new System.Windows.Documents.Run("↑ " + Mbit(u)));
                heroUp.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T("Main_SpeedMbit")) { FontSize = 13, FontWeight = FontWeights.Normal });
            }

            void Refresh()
            {
                int span = hours == 0 ? 1 : hours;
                int minutes = span * 60;
                var data = SpeedHistory.Get(entry.Id, minutes);
                chart.Set(data, minutes, span == 24 ? 5 : span == 12 ? 3 : span == 6 ? 2 : 1, span == 24 ? 180 : span == 12 ? 120 : span == 6 ? 60 : 10);
                UpdateNow();

                string period;
                double avgD, avgU, volD, volU, pkD, pkU;
                string pkDAt, pkUAt, shiftD = null, shiftU = null;
                if (hours == 0)
                {
                    // «Сейчас»: the figures over the last 60 s, the same minute as «кто и куда».
                    var sec = SpeedLastMinute(entry.Id);
                    double ms = sec.Sum(x => x.Secs);
                    volD = sec.Sum(x => x.Down * x.Secs);
                    volU = sec.Sum(x => x.Up * x.Secs);
                    avgD = ms > 0 ? volD / ms : 0;
                    avgU = ms > 0 ? volU / ms : 0;
                    var tD = sec.OrderByDescending(x => x.Down).FirstOrDefault();
                    var tU = sec.OrderByDescending(x => x.Up).FirstOrDefault();
                    pkD = tD?.Down ?? 0; pkU = tU?.Up ?? 0;
                    pkDAt = tD == null ? "" : tD.At.ToLocalTime().ToString("HH:mm:ss");
                    pkUAt = tU == null ? "" : tU.At.ToLocalTime().ToString("HH:mm:ss");
                    period = Loc.T("Main_SpeedPerMin");
                    note.Text = Loc.T("Main_SpeedNote");
                }
                else
                {
                    double secs = data.Sum(b => b.Secs);
                    volD = data.Sum(b => b.Down);
                    volU = data.Sum(b => b.Up);
                    avgD = secs > 0 ? volD / secs : 0;
                    avgU = secs > 0 ? volU / secs : 0;
                    var bD = data.OrderByDescending(b => b.PeakDown).FirstOrDefault();
                    var bU = data.OrderByDescending(b => UpPeak(b)).FirstOrDefault();
                    string fmt = span >= 12 ? "dd.MM HH:mm" : "HH:mm";
                    pkD = bD?.PeakDown ?? 0; pkU = bU == null ? 0 : UpPeak(bU);
                    pkDAt = bD == null ? "" : SpeedHistory.TimeOf(bD.Minute).ToLocalTime().ToString(fmt);
                    pkUAt = bU == null ? "" : SpeedHistory.TimeOf(bU.Minute).ToLocalTime().ToString(fmt);
                    // The same span just before: the average's shift, when that span was measured enough.
                    var prev = SpeedHistory.Get(entry.Id, minutes, minutes);
                    double ps = prev.Sum(b => b.Secs);
                    if (ps >= minutes * 60 * 0.1)
                    {
                        shiftD = Shift(avgD, prev.Sum(b => b.Down) / ps);
                        shiftU = Shift(avgU, prev.Sum(b => b.Up) / ps);
                    }
                    period = Loc.T(span == 1 ? "Main_SpeedPer1" : span == 6 ? "Main_SpeedPer6" : span == 12 ? "Main_SpeedPer12" : "Main_SpeedPer24");
                    note.Text = Loc.T("Main_SpeedNote") + " " + Loc.T(span == 1 ? "Main_SpeedShift1" : "Main_SpeedShiftN", span);
                }

                sub.Text = Route() + " · " + Loc.T("Main_SpeedSummary", period,
                    hours == 0 ? WhoVolume(volD) : Volume(volD), hours == 0 ? WhoVolume(volU) : Volume(volU));
                FillTable(table, Loc.T("Main_SpeedFor", period),
                    avgD, shiftD, pkD, pkDAt, hours == 0 ? WhoVolume(volD) : Volume(volD),
                    avgU, shiftU, pkU, pkUAt, hours == 0 ? WhoVolume(volU) : Volume(volU));
                FillRecords(records, SpeedHistory.GetTotal(entry.Id), Volume);

                tabs.Children.Clear();
                foreach (var h in new[] { 0, 1, 6, 12, 24 })
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
                            Text = Loc.T(h == 0 ? "Main_SpeedTabNow" : h == 1 ? "Main_SpeedTab1" : h == 6 ? "Main_SpeedTab6" : h == 12 ? "Main_SpeedTab12" : "Main_SpeedTab24"),
                            FontSize = 12.5,
                            Foreground = on ? Theme.TextPrimary : Theme.TextSecondary,
                        },
                    };
                    int pick = h;
                    tab.MouseLeftButtonUp += (s, e) => { e.Handled = true; hours = pick; Refresh(); whoSetHours?.Invoke(pick); };
                    tabs.Children.Add(tab);
                }
            }
            Refresh();

            // A window of its own, movable off the launcher (2026-11-16: «залочено внутри утилиты»).
            // Not modal: the tiles stay usable while it is open.
            var win = new SpeedWindow(Loc.T("Main_SpeedTitle", entry.IsAllTraffic ? AllTrafficName() : entry.Name), body) { Owner = this };
            whoOwner = win;
            whoHolder.Content = BuildSpeedWho(entry, whoOwner, false, null, 0, out whoStop, out whoSetHours);
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
                await Task.Run(() => ConnArchive.Clear(entry.Id)).ConfigureAwait(true);
                Log.Info("Скорость: история плитки «" + name + "» очищена (график, рекорды и соединения).");
                Refresh();
                whoSetHours?.Invoke(hours);
            };

            // Every second: the big figures, like the tile's plate; every 5 s the whole «Сейчас»
            // tab. The archive tabs stay still apart from the big figures.
            int ticks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                if (hours == 0 && ++ticks % 5 == 0) { Refresh(); return; }
                UpdateNow();
            };
            timer.Start();
            win.Closed += (s, e) => timer.Stop();
            win.Show();
            await Task.CompletedTask.ConfigureAwait(true);
        }

        // A minute's fastest second up; buckets from before 2026-11-21 have none — their average then.
        private static double UpPeak(SpeedHistory.Bucket b) => b.PeakUp > 0 ? b.PeakUp : (b.Secs > 0 ? b.Up / b.Secs : 0);

        // «▲ 14%» / «▼ 6%» / «≈» — neutral grey: more or less traffic is neither good nor bad.
        private static string Shift(double now, double before)
        {
            if (before <= 0) return null;
            double pct = (now - before) / before * 100;
            if (Math.Abs(pct) < 1) return "≈";
            return (pct > 0 ? "▲ " : "▼ ") + Math.Round(Math.Abs(pct)).ToString("0", CultureInfo.CurrentCulture) + "%";
        }

        // A grouped block: the raised surface (2026-11-22 standard; was the badge grey).
        private static Border SpeedPanel(UIElement child, Thickness padding) => new Border
        {
            Background = Theme.CardBg,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = padding,
            Child = child,
        };

        // «За 24 часа | среднее | ◆ пик | объём», a row each for ↓ and ↑.
        private static void FillTable(Grid g, string head,
            double avgD, string shiftD, double pkD, string pkDAt, string volD,
            double avgU, string shiftU, double pkU, string pkUAt, string volU)
        {
            g.Children.Clear();
            if (g.ColumnDefinitions.Count == 0)
            {
                foreach (var w in new[] { 1.1, 1.3, 1.7, 1.0 })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
                for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            void Cell(int r, int c, string value, string detail, bool head2)
            {
                var t = new TextBlock { FontSize = head2 ? 11.5 : 12.5, Foreground = head2 ? Theme.TextMuted : Theme.TextPrimary, Margin = new Thickness(0, 0, 6, 3), TextTrimming = TextTrimming.CharacterEllipsis };
                t.Inlines.Add(new System.Windows.Documents.Run(value) { FontWeight = !head2 && c > 0 ? FontWeights.SemiBold : FontWeights.Normal });
                if (!string.IsNullOrEmpty(detail))
                    t.Inlines.Add(new System.Windows.Documents.Run("  " + detail) { FontSize = 11.5, Foreground = Theme.TextMuted });
                Grid.SetRow(t, r);
                Grid.SetColumn(t, c);
                g.Children.Add(t);
            }
            Cell(0, 0, head, null, true);
            Cell(0, 1, Loc.T("Main_SpeedColAvg"), null, true);
            Cell(0, 2, Loc.T("Main_SpeedColPeak"), null, true);
            Cell(0, 3, Loc.T("Main_SpeedColVol"), null, true);
            Cell(1, 0, Loc.T("Main_SpeedRowDown"), null, false);
            Cell(1, 1, Mbit(avgD), shiftD, false);
            Cell(1, 2, pkD > 0 ? Mbit(pkD) : "—", pkD > 0 ? pkDAt : null, false);
            Cell(1, 3, volD, null, false);
            Cell(2, 0, Loc.T("Main_SpeedRowUp"), null, false);
            Cell(2, 1, Mbit(avgU), shiftU, false);
            Cell(2, 2, pkU > 0 ? Mbit(pkU) : "—", pkU > 0 ? pkUAt : null, false);
            Cell(2, 3, volU, null, false);
        }

        // «Рекорды · с 12.11, за всё время»: the fastest second each way, the biggest day, the total.
        private static void FillRecords(StackPanel p, SpeedHistory.Total t, Func<double, string> volume)
        {
            p.Children.Clear();
            var head = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary, Margin = new Thickness(0, 0, 0, 6) };
            head.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Main_SpeedRecords")));
            if (t != null)
                head.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T("Main_SpeedRecordsSince", SpeedHistory.TimeOf(t.Since).ToLocalTime().ToString("dd.MM")))
                    { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Theme.TextMuted });
            p.Children.Add(head);
            if (t == null)
            {
                p.Children.Add(new TextBlock { Text = Loc.T("Main_SpeedRecNone"), FontSize = 12.5, Foreground = Theme.TextSecondary, TextWrapping = TextWrapping.Wrap });
                return;
            }
            void Row(string label, string value, string when)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                var a = new TextBlock { Text = label, FontSize = 12.5, Foreground = Theme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis };
                var b = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary, Margin = new Thickness(8, 0, 0, 0) };
                var c = new TextBlock { Text = when, FontSize = 11.5, Foreground = Theme.TextMuted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(b, 1);
                Grid.SetColumn(c, 2);
                g.Children.Add(a); g.Children.Add(b); g.Children.Add(c);
                p.Children.Add(new Border { BorderBrush = Theme.BadgeNeutralBorder, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 6), Child = g });
            }
            string At(long minute) => SpeedHistory.TimeOf(minute).ToLocalTime().ToString("dd.MM HH:mm");
            Row(Loc.T("Main_SpeedRecDown"), t.PeakDown > 0 ? Mbit(t.PeakDown) : "—", t.PeakDown > 0 ? At(t.PeakDownAt) : "");
            Row(Loc.T("Main_SpeedRecUp"), t.PeakUp > 0 ? Mbit(t.PeakUp) : "—", t.PeakUp > 0 ? At(t.PeakUpAt) : "");
            Row(Loc.T("Main_SpeedRecDay"), t.BestDayBytes > 0 ? volume(t.BestDayBytes) : "—",
                t.BestDay > 0 ? SpeedHistory.DateOfDay(t.BestDay).ToString("dd.MM") : "");
            int days = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - SpeedHistory.TimeOf(t.Since)).TotalDays));
            Row(Loc.T("Main_SpeedRecTotal"), volume(t.Down) + " / " + volume(t.Up), Loc.T("Main_SpeedRecDays", days));
        }

        // The chart: download up from the zero line, upload down from it (2026-11-21, the «сеть» card
        // of «Здоровье сервера»), each on a plain scale with headroom over its own highest point. The
        // download line and fill take the colour of the speed class they reach; the upload is grey.
        // The fastest second of the period each way is a diamond on the line with its figure and time.
        internal sealed class SpeedDayChart : FrameworkElement
        {
            private sealed class Slot { public DateTime Start; public double Down, Up, Secs, PeakDown, PeakUp; public long PeakDownAt, PeakUpAt; }
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
                    if (b.PeakDown > sl.PeakDown) { sl.PeakDown = b.PeakDown; sl.PeakDownAt = b.Minute; }
                    double pu = UpPeak(b);
                    if (pu > sl.PeakUp) { sl.PeakUp = pu; sl.PeakUpAt = b.Minute; }
                }
                _slots = slots;
                InvalidateVisual();
            }

            // A round top a bit above `v`: 1 / 2 / 2,5 / 5 × 10ⁿ.
            private static double Nice(double v)
            {
                v = Math.Max(v * 1.15, 0.1);
                double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
                foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * p >= v) return m * p;
                return 10 * p;
            }

            private static string Num(double mbit) => mbit >= 10 || mbit == Math.Round(mbit)
                ? Math.Round(mbit).ToString("0", CultureInfo.CurrentCulture)
                : mbit.ToString(mbit < 1 ? "0.##" : "0.#", CultureInfo.CurrentCulture);

            // A smooth curve through the points (Catmull-Rom as cubic Béziers), kept between `top`
            // and `floor`; `area` closes it to `baseY` for the fill.
            private static StreamGeometry Smooth(List<Point> p, double top, double floor, bool area, double baseY)
            {
                double lo = Math.Min(top, floor), hi = Math.Max(top, floor);
                double Cy(double v) => Math.Max(lo, Math.Min(hi, v));
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

            // Red low → amber → green → blue high along the download half, pinned to its pixels: the
            // colours sit inside their classes (< 1, 1–10, 10–50, 50+) so the change is soft.
            private static LinearGradientBrush ClassGradient(double zeroY, double topY, double top, byte alpha)
            {
                Color A(SolidColorBrush b) => Color.FromArgb(alpha, b.Color.R, b.Color.G, b.Color.B);
                var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, zeroY), EndPoint = new Point(0, topY) };
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedRed), 0.3 / top));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedAmber), 3.2 / top));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedGreen), 22 / top));
                g.GradientStops.Add(new GradientStop(A(Theme.SpeedBlue), 100 / top));
                return g;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double W = ActualWidth, H = ActualHeight;
                if (W <= 0 || H <= 0) return;
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                FormattedText Txt(string s, double size, Brush b) => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, dpi);

                const double L = 40, R = 8, T = 6, B = 18;
                double cw = W - L - R, ch = H - T - B, bottom = T + ch;
                double mid = T + ch * 0.62;   // download gets the bigger half
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

                int n = _slots.Count;
                double Mb(double bytes, double secs) => secs > 0 ? bytes / secs * 8 / 1e6 : 0;
                double maxD = n == 0 ? 0 : _slots.Max(s => Mb(s.Down, s.Secs));
                double maxU = n == 0 ? 0 : _slots.Max(s => Mb(s.Up, s.Secs));
                double topD = Nice(maxD), topU = Nice(maxU);
                double Yd(double v) => mid - Math.Min(1, Math.Max(0, v) / topD) * (mid - T);
                double Yu(double v) => mid + Math.Min(1, Math.Max(0, v) / topU) * (bottom - mid);

                // Grid + scale: half and full on the download side, full on the upload side, zero.
                var gridPen = new Pen(Theme.ChartGrid, 1);
                void GridLine(double y, string label)
                {
                    y = Math.Round(y) + 0.5;
                    dc.DrawLine(gridPen, new Point(L, y), new Point(W - R, y));
                    var t = Txt(label, 10.5, Theme.TextMuted);
                    dc.DrawText(t, new Point(L - 8 - t.Width, y - t.Height / 2));
                }
                GridLine(Yd(topD), Num(topD));
                GridLine(Yd(topD / 2), Num(topD / 2));
                GridLine(Yu(topU), Num(topU));
                double zy = Math.Round(mid) + 0.5;
                dc.DrawLine(new Pen(Theme.ChartZeroLine, 1), new Point(L, zy), new Point(W - R, zy));
                var z = Txt("0", 10.5, Theme.TextMuted);
                dc.DrawText(z, new Point(L - 8 - z.Width, zy - z.Height / 2));
                dc.DrawText(Txt(Loc.T("Main_SpeedAxisDown"), 10.5, Theme.TextSecondary), new Point(L + 6, T + 1));
                var ua = Txt(Loc.T("Main_SpeedAxisUp"), 10.5, Theme.TextSecondary);
                dc.DrawText(ua, new Point(L + 6, bottom - ua.Height - 1));

                if (n == 0) { dc.Pop(); return; }
                double bw = cw / n;
                double X(int i) => L + (i + 0.5) * bw;

                // One unbroken line each way (2026-11-19 «без прерывания»): a slot nobody measured
                // counts as zero; the hover still says «нет данных» for it.
                var down = new List<Point>(n);
                var up = new List<Point>(n);
                for (int i = 0; i < n; i++)
                {
                    down.Add(new Point(X(i), Yd(Mb(_slots[i].Down, _slots[i].Secs))));
                    up.Add(new Point(X(i), Yu(Mb(_slots[i].Up, _slots[i].Secs))));
                }
                bool any = _slots.Any(s => s.Secs > 0);
                if (any && n > 1)
                {
                    dc.DrawGeometry(ClassGradient(mid, T, topD, 60), null, Smooth(down, T, mid, true, mid));
                    dc.DrawGeometry(null, new Pen(ClassGradient(mid, T, topD, 255), 1.8) { LineJoin = PenLineJoin.Round }, Smooth(down, T, mid, false, mid));
                    var upColor = Theme.ChartUpColor;
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(30, upColor.R, upColor.G, upColor.B)), null, Smooth(up, mid, bottom, true, mid));
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, upColor.R, upColor.G, upColor.B)), 1.3) { LineJoin = PenLineJoin.Round }, Smooth(up, mid, bottom, false, mid));

                    // The fastest second each way: a diamond on the line, its figure and time beside.
                    string fmt = _per >= 3 ? "dd.MM HH:mm" : "HH:mm";
                    void Peak(int i, double bps, long at, bool isDown)
                    {
                        if (i < 0 || bps <= 0) return;
                        var p = isDown ? down[i] : up[i];
                        dc.DrawLine(new Pen(Theme.ChartGuide, 1), new Point(p.X, zy), p);
                        var dia = new StreamGeometry();
                        using (var c = dia.Open())
                        {
                            c.BeginFigure(new Point(p.X, p.Y - 5), true, true);
                            c.LineTo(new Point(p.X + 5, p.Y), true, false);
                            c.LineTo(new Point(p.X, p.Y + 5), true, false);
                            c.LineTo(new Point(p.X - 5, p.Y), true, false);
                        }
                        dia.Freeze();
                        dc.DrawGeometry(Theme.TextPrimary, null, dia);
                        var t = Txt(Mbit(bps) + " · " + SpeedHistory.TimeOf(at).ToLocalTime().ToString(fmt), 11, Theme.TextPrimary);
                        bool left = p.X + 10 + t.Width > W - R;
                        double tx = left ? p.X - 10 - t.Width : p.X + 10;
                        double ty = isDown ? Math.Max(T, p.Y - t.Height - 2) : Math.Min(bottom - t.Height, p.Y + 2);
                        dc.DrawText(t, new Point(tx, ty));
                    }
                    int pd = -1, pu = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (_slots[i].PeakDown > 0 && (pd < 0 || _slots[i].PeakDown > _slots[pd].PeakDown)) pd = i;
                        if (_slots[i].PeakUp > 0 && (pu < 0 || _slots[i].PeakUp > _slots[pu].PeakUp)) pu = i;
                    }
                    if (pd >= 0) Peak(pd, _slots[pd].PeakDown, _slots[pd].PeakDownAt, true);
                    if (pu >= 0) Peak(pu, _slots[pu].PeakUp, _slots[pu].PeakUpAt, false);
                }

                // Time ticks.
                var tickPen = new Pen(Theme.TextMuted, 1);
                for (int i = 0; i < n; i++)
                {
                    var t0 = _slots[i].Start;
                    int mins = t0.Hour * 60 + t0.Minute;
                    if (mins % _tickEvery >= _per) continue;
                    double x = L + i * bw;
                    dc.DrawLine(tickPen, new Point(x, bottom), new Point(x, bottom + 3));
                    var t = Txt(t0.ToString("HH:mm"), 10.5, Theme.TextMuted);
                    double tx = Math.Max(L, Math.Min(W - R - t.Width, x - t.Width / 2));
                    dc.DrawText(t, new Point(tx, bottom + 3));
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
                    double x = X(i);
                    dc.DrawLine(new Pen(Theme.ChartGuide, 1), new Point(x, T), new Point(x, bottom));
                    if (s.Secs > 0)
                    {
                        double dm = Mb(s.Down, s.Secs);
                        dc.DrawEllipse(SpeedBrush(Math.Max(0.006, dm)), new Pen(Theme.TextOnColor, 1.5), new Point(x, Yd(dm)), 3.5, 3.5);
                    }
                    string time = s.Start.ToString("HH:mm") + (_per > 1 ? "–" + s.Start.AddMinutes(_per).ToString("HH:mm") : "");
                    string text = s.Secs <= 0 ? time + " · " + Loc.T("Main_SpeedNoDataShort")
                        : time + " · ↓ " + Mbit(s.Down / s.Secs) + " · ↑ " + Mbit(s.Up / s.Secs) + " " + Loc.T("Main_SpeedMbit")
                          + (_per > 1 ? " · " + Loc.T("Main_SpeedPeak").ToLowerInvariant() + " " + Mbit(s.PeakDown) : "");
                    var t = Txt(text, 11.5, Theme.TextPrimary);
                    double bx = Math.Max(2, Math.Min(W - t.Width - 14, x + 10));
                    double by = mid + 4;
                    var r = new Rect(bx, by, t.Width + 12, t.Height + 6);
                    // Floating, like every tooltip (2026-11-22): OverlayCardBg + PickerBorder (it was a near-black #151518).
                    dc.DrawRoundedRectangle(Theme.OverlayCardBg, new Pen(Theme.PickerBorder, 1), r, 5, 5);
                    dc.DrawText(t, new Point(bx + 6, by + 3));
                }
                dc.Pop();
            }
        }
    }
}
