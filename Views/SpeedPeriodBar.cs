using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace MihomoLauncher.Views
{
    // What a speed view shows: live, or a stretch of the history (UTC; ToUtc null = up to now).
    internal sealed class SpeedSpan
    {
        public bool Live;
        public DateTime FromUtc;
        public DateTime? ToUtc;
        public static SpeedSpan Now() => new SpeedSpan { Live = true, FromUtc = DateTime.UtcNow.AddMinutes(-1) };
        public static SpeedSpan Last(int minutes) => new SpeedSpan { FromUtc = DateTime.UtcNow.AddMinutes(-minutes) };
    }

    // The period row of the speed window (2026-11-25, .dev\mockups\speed-period.html, variant D —
    // the person's pick, replacing 11 tabs): «Сейчас» · 1 / 6 / 12 / 24 часа · a day counter
    // «− N дн +» · two date plates «ДД.ММ.ГГГГ ЧЧ:ММ» – «ДД.ММ.ГГГГ ЧЧ:ММ».
    //
    // Only what means something can be picked («переключаем только на имеющие значение»): an hour
    // button that adds no traffic to the one before it, or starts past the history once an earlier
    // one already took all of it, is inactive; the day counter steps only to days that add traffic,
    // its last step is the whole history (labelled as whole days) and «+» is inactive after it.
    // A click on a date plate puts the caret in it and opens the calendar under it: only days with
    // traffic inside the bounds (the history's start … now; «по» not before «с», «с» not after
    // «по») can be clicked, a strip under each shows its hours with traffic; the time is typed from
    // the start (hours, then minutes) or picked on a grid of hours and 5-minute steps, the wheel over
    // it moves ±1 h / ±1 min. Picking the minutes closes the calendar; under «по» it also shows the
    // period, as do «Показать» and Enter. The plates light up only once a period is shown from them.
    internal sealed class SpeedPeriodBar
    {
        public enum Kind { Now, Hours, Days, Range }

        public Kind Mode { get; private set; } = Kind.Now;
        public int SpanMin { get; private set; }          // Hours / Days: the last this many minutes
        public DateTime FromUtc { get; private set; }      // Range
        public DateTime? ToUtc { get; private set; }       // Range; null = up to now
        public event Action Changed;
        public FrameworkElement View => _view;

        private readonly string _tile;
        private Window _owner;
        private readonly StackPanel _row = new StackPanel { Orientation = Orientation.Horizontal };
        // Under the row (as in the mockup): what is shown — «Показано: 23.11.2026 15:02 – 25.11.2026 17:05 (2 дн 2 ч)».
        private readonly TextBlock _shown = new TextBlock { FontSize = 12.5, Margin = new Thickness(2, 8, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel _view = new StackPanel();
        private static readonly FontFamily Mono = new FontFamily("Consolas");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string PlateFmt = "dd.MM.yyyy HH:mm";
        private const double H = 28;   // every element of the row

        // ---- what the history holds ----
        private DateTime? _start;                       // UTC, the first measured minute
        private long[] _traffic = new long[0];          // minutes (Unix) with traffic, ascending
        private Dictionary<DateTime, bool[]> _dayHours = new Dictionary<DateTime, bool[]>();   // local date → hours with traffic

        private static readonly int[] HourSpans = { 60, 360, 720, 1440 };
        private readonly Border _nowChip;
        private readonly Border[] _hourChips = new Border[4];
        private readonly Border _stepper, _dayMinus, _dayPlus;
        private readonly TextBlock _dayVal;
        private List<int> _daySteps = new List<int>();
        private bool _lastIsTail;
        private int _dayIdx;
        private bool _dayWasTail;
        private readonly Border _fromPlate, _toPlate;
        private readonly TextBox _fromBox, _toBox;
        private bool _fromBad, _toBad;
        // Both plates hold a whole date and time: the period shows by itself, a moment after the
        // last change (2026-11-25: «сразу подставляй значение, когда указаны в обоих плашках, не
        // надо ждать команды») — typed or picked in the calendar alike.
        private System.Windows.Threading.DispatcherTimer _autoApply;
        private bool _applying;   // Apply rewriting the plates: not a change of the person's

        public SpeedPeriodBar(string tileId)
        {
            _tile = tileId;
            _nowChip = Chip(Loc.T("Main_SpeedTabNow"), () => Select(Kind.Now, 0));
            _row.Children.Add(_nowChip);
            for (int i = 0; i < HourSpans.Length; i++)
            {
                int span = HourSpans[i];
                _hourChips[i] = Chip(HoursText(span / 60), () => Select(Kind.Hours, span));
                _row.Children.Add(_hourChips[i]);
            }

            // − N дн + : fixed width (the longest label), the value itself a click target too.
            _dayMinus = StepButton("−", () => StepDays(-1));
            _dayPlus = StepButton("+", () => StepDays(1));
            _dayVal = new TextBlock { FontSize = 12.5, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var valCell = new Border
            {
                Width = 64, Child = _dayVal, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1, 0, 1, 0),
            };
            valCell.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_daySteps.Count > 0) Select(Kind.Days, _daySteps[Math.Min(_dayIdx, _daySteps.Count - 1)]);
            };
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            sg.Children.Add(_dayMinus);
            Grid.SetColumn(valCell, 1); sg.Children.Add(valCell);
            Grid.SetColumn(_dayPlus, 2); sg.Children.Add(_dayPlus);
            _stepper = new Border { Height = H, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = sg, ClipToBounds = true };
            _stepper.MouseWheel += (s, e) => { e.Handled = true; StepDays(e.Delta > 0 ? 1 : -1); };
            _row.Children.Add(_stepper);

            _row.Children.Add(new Border { Width = 1, Height = 20, Background = Theme.CardBorder, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            _fromPlate = Plate(false, out _fromBox);
            _row.Children.Add(_fromPlate);
            _row.Children.Add(new TextBlock { Text = "–", Foreground = Theme.TextSecondary, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            _toPlate = Plate(true, out _toBox);
            _row.Children.Add(_toPlate);

            _view.Children.Add(_row);
            _view.Children.Add(_shown);
            BuildCalendar();
            Paint();
        }

        // The window the row sits in (made after the row): the calendar closes on a click outside,
        // on a move, when it loses the focus or closes; its Esc closes the calendar first.
        public void Attach(Window owner)
        {
            _owner = owner;
            owner.PreviewMouseDown += (s, e) =>
            {
                if (!_pop.IsOpen) return;
                var plate = _calTo ? _toPlate : _fromPlate;
                if (!IsWithin(e.OriginalSource as DependencyObject, plate)) ClosePop();
            };
            owner.LocationChanged += (s, e) => ClosePop();
            // Losing the focus to another window closes it; not to the calendar itself (its time field).
            owner.Deactivated += (s, e) => owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                var card = _pop.Child as FrameworkElement;
                if (card == null || !card.IsKeyboardFocusWithin) ClosePop();
            }), System.Windows.Threading.DispatcherPriority.Input);
            owner.Closed += (s, e) => ClosePop();
            var sw = owner as SpeedWindow;
            if (sw != null) sw.EscapeFirst = () => { if (!_pop.IsOpen) return false; ClosePop(); return true; };
        }

        // ---- what the consumers read ----

        // The stretch to draw: «Сейчас» is the last hour (its figures are the last minute).
        public void Stretch(out DateTime fromUtc, out DateTime toUtc)
        {
            var now = DateTime.UtcNow;
            if (Mode == Kind.Now) { fromUtc = now.AddMinutes(-60); toUtc = now; }
            else if (Mode == Kind.Range) { fromUtc = FromUtc; toUtc = ToUtc ?? now; }
            else { fromUtc = now.AddMinutes(-SpanMin); toUtc = now; }
        }

        public SpeedSpan Span()
        {
            if (Mode == Kind.Now) return SpeedSpan.Now();
            if (Mode == Kind.Range) return new SpeedSpan { FromUtc = FromUtc, ToUtc = ToUtc };
            return SpeedSpan.Last(SpanMin);
        }

        // «час» / «6 часов» … for «За …»; «3 дня»; «25.11.2026 09:00 – 13:30».
        public string Label
        {
            get
            {
                if (Mode == Kind.Hours)
                {
                    int h = SpanMin / 60;
                    return Loc.T(h == 1 ? "Main_SpeedPer1" : h == 6 ? "Main_SpeedPer6" : h == 12 ? "Main_SpeedPer12" : "Main_SpeedPer24");
                }
                if (Mode == Kind.Days) return DaysText(DaysOf(SpanMin));
                if (Mode == Kind.Range) return RangeText(FromUtc.ToLocalTime(), ToUtc?.ToLocalTime());
                return Loc.T("Main_SpeedPerMin");
            }
        }

        public static string RangeText(DateTime from, DateTime? to)
        {
            if (to == null) return from.ToString(PlateFmt, Inv) + " – " + Loc.T("Main_SpeedUntilNow");
            return from.ToString(PlateFmt, Inv) + " – " + (to.Value.Date == from.Date ? to.Value.ToString("HH:mm", Inv) : to.Value.ToString(PlateFmt, Inv));
        }

        public static string HoursText(int h) => Loc.T(h == 1 ? "Main_SpeedTab1" : h == 6 ? "Main_SpeedTab6" : h == 12 ? "Main_SpeedTab12" : "Main_SpeedTab24");

        public static string DaysText(int d)
        {
            int m10 = d % 10, m100 = d % 100;
            string key = m10 == 1 && m100 != 11 ? "Main_SpeedDays1"
                       : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "Main_SpeedTabDays2" : "Main_SpeedTabDays5";
            return Loc.T(key, d);
        }

        private static int DaysOf(int minutes) => Math.Max(1, (int)Math.Ceiling(minutes / 1440.0));

        // ---- the history ----

        private static long NowMin => SpeedHistory.MinuteOf(DateTime.UtcNow);

        private void LoadAvail()
        {
            var all = SpeedHistory.Get(_tile, SpeedHistory.KeepMinutes + 1440);
            long first = long.MaxValue;
            var t = new List<long>();
            foreach (var b in all)
            {
                if (b.Secs > 0 && b.Minute < first) first = b.Minute;
                if (b.Down + b.Up > 0) t.Add(b.Minute);
            }
            t.Sort();
            _traffic = t.ToArray();
            _start = first == long.MaxValue ? (DateTime?)null : SpeedHistory.TimeOf(first);
            var days = new Dictionary<DateTime, bool[]>();
            foreach (var m in _traffic)
            {
                var l = SpeedHistory.TimeOf(m).ToLocalTime();
                bool[] hrs;
                if (!days.TryGetValue(l.Date, out hrs)) days[l.Date] = hrs = new bool[24];
                hrs[l.Hour] = true;
            }
            _dayHours = days;
        }

        // Minutes with traffic in [from, to] (Unix minutes).
        private int TrafficIn(long from, long to)
        {
            int a = LowerBound(from), b = LowerBound(to + 1);
            return Math.Max(0, b - a);
        }
        private int LowerBound(long v)
        {
            int lo = 0, hi = _traffic.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (_traffic[mid] < v) lo = mid + 1; else hi = mid; }
            return lo;
        }
        private int DataInLast(int minutes) { long now = NowMin; return TrafficIn(now - minutes + 1, now); }
        private int TailMin => _start == null ? 0 : (int)Math.Min(SpeedHistory.KeepMinutes, NowMin - SpeedHistory.MinuteOf(_start.Value) + 1);

        private void BuildDaySteps()
        {
            var steps = new List<int>();
            // Every whole day inside the history, then the whole history; nothing past its start
            // (empty days inside it count as days — 2026-11-25, as the hour buttons).
            int tail = TailMin;
            for (int c = 1440; c <= 7 * 1440; c += 1440)
                if (c < tail) steps.Add(c);
            _lastIsTail = false;
            if (tail >= 1440)
            {
                steps.Add(tail);
                _lastIsTail = true;
            }
            _daySteps = steps;
        }

        // ---- the row ----

        private void Select(Kind kind, int span)
        {
            Mode = kind;
            SpanMin = span;
            if (kind == Kind.Days)
            {
                int i = _daySteps.IndexOf(span);
                if (i >= 0) _dayIdx = i;
                _dayWasTail = _lastIsTail && _dayIdx == _daySteps.Count - 1;
            }
            Paint();
            Changed?.Invoke();
        }

        private void StepDays(int d)
        {
            if (_daySteps.Count == 0) return;
            int i = Math.Max(0, Math.Min(_daySteps.Count - 1, _dayIdx + d));
            if (i == _dayIdx && Mode == Kind.Days) return;
            _dayIdx = i;
            Select(Kind.Days, _daySteps[i]);
        }

        // Re-read what the history holds and repaint: the consumer calls it on its own refresh.
        public void Paint()
        {
            LoadAvail();
            int tail = TailMin;

            PaintChip(_nowChip, Mode == Kind.Now, false, null);
            int last = 0;
            bool done = false;
            for (int i = 0; i < HourSpans.Length; i++)
            {
                // A button goes dark only past the start of the history (2026-11-25: «покажи за 24
                // часа» — whatever there is in them, a night with nothing included); the first one
                // that covers the whole history stays. Hours with no traffic do not count.
                int span = HourSpans[i], n = DataInLast(span);
                bool off = done || tail <= 0;
                string tip = off ? Loc.T("Main_SpeedNoEarlier") : null;
                PaintChip(_hourChips[i], Mode == Kind.Hours && SpanMin == span, off, tip);
                if (!off) { last = n; if (span >= tail) done = true; }
            }

            BuildDaySteps();
            if (Mode == Kind.Days)
            {
                // the whole history grows by the minute: stay on it
                if (_dayWasTail && _lastIsTail) { _dayIdx = _daySteps.Count - 1; SpanMin = _daySteps[_dayIdx]; }
                else
                {
                    int i = _daySteps.IndexOf(SpanMin);
                    if (i >= 0) _dayIdx = i;
                }
            }
            if (_dayIdx >= _daySteps.Count) _dayIdx = Math.Max(0, _daySteps.Count - 1);
            bool dayOn = Mode == Kind.Days;
            bool any = _daySteps.Count > 0;
            _dayVal.Text = DaysText(any ? DaysOf(_daySteps[_dayIdx]) : 1);
            _dayVal.Foreground = dayOn ? Theme.TextPrimary : any ? Theme.TextSecondary : Theme.TextDisabled;
            _stepper.Background = dayOn ? Theme.ChipBgHover : Theme.BadgeNeutralBg;
            _stepper.BorderBrush = dayOn ? Theme.TileBorderHover : Theme.BadgeNeutralBorder;
            ((Border)_dayVal.Parent).BorderBrush = Theme.BadgeNeutralBorder;
            PaintStep(_dayMinus, any && _dayIdx > 0, null);
            PaintStep(_dayPlus, any && _dayIdx < _daySteps.Count - 1, any && _dayIdx >= _daySteps.Count - 1 ? Loc.T("Main_SpeedNoEarlier") : null);

            PaintPlate(_fromPlate, _fromBad);
            PaintPlate(_toPlate, _toBad);
            PaintShown();
        }

        private void PaintShown()
        {
            _shown.Inlines.Clear();
            _shown.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Main_SpeedShown") + " ") { Foreground = Theme.TextSecondary });
            if (Mode == Kind.Now)
            {
                _shown.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Main_SpeedShownNow")) { Foreground = Theme.TextPrimary, FontWeight = FontWeights.SemiBold });
                return;
            }
            DateTime f, t;
            Stretch(out f, out t);
            var lf = FloorMin(f.ToLocalTime());
            var lt = FloorMin(t.ToLocalTime());
            if (Mode == Kind.Days && _start != null && f <= _start.Value) lf = HistLo();   // the whole history: from where it starts
            string text = lf.ToString(PlateFmt, Inv) + " – " + (lt.Date == lf.Date ? lt.ToString("HH:mm", Inv) : lt.ToString(PlateFmt, Inv));
            _shown.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = Theme.TextPrimary, FontWeight = FontWeights.SemiBold });
            _shown.Inlines.Add(new System.Windows.Documents.Run("  (" + DurText(lt - lf) + ")") { Foreground = Theme.TextSecondary });
        }

        // «2 дн 2 ч», «4 ч 30 мин», «45 мин».
        public static string DurText(TimeSpan d)
        {
            int m = (int)Math.Round(Math.Max(0, d.TotalMinutes));
            int days = m / 1440, h = m % 1440 / 60, mm = m % 60;
            var parts = new List<string>();
            if (days > 0) parts.Add(days + " " + Loc.T("Main_SpeedDurD"));
            if (h > 0) parts.Add(h + " " + Loc.T("Main_SpeedDurH"));
            if (mm > 0 && days == 0) parts.Add(mm + " " + Loc.T("Main_SpeedDurM"));
            return parts.Count > 0 ? string.Join(" ", parts) : "0 " + Loc.T("Main_SpeedDurM");
        }

        private Border Chip(string text, Action click)
        {
            var b = new Border
            {
                Height = H, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center },
            };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!(b.Tag is bool) || !(bool)b.Tag) click(); };
            return b;
        }

        private static void PaintChip(Border b, bool on, bool off, string tip)
        {
            b.Tag = off;
            b.Background = on ? Theme.ChipBgHover : Theme.BadgeNeutralBg;
            b.BorderBrush = on ? Theme.TileBorderHover : Theme.BadgeNeutralBorder;
            ((TextBlock)b.Child).Foreground = on ? Theme.TextPrimary : Theme.TextSecondary;
            b.Opacity = off && !on ? 0.4 : 1;
            b.Cursor = off ? null : Cursors.Hand;
            b.ToolTip = tip;
        }

        private static Border StepButton(string glyph, Action click)
        {
            var t = new TextBlock { Text = glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
            var b = new Border { Background = Brushes.Transparent, Child = t };
            b.MouseEnter += (s, e) => { if (b.IsEnabled) b.Background = Theme.PickerRowHover; };
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (b.IsEnabled) click(); };
            return b;
        }

        private static void PaintStep(Border b, bool enabled, string tip)
        {
            b.IsEnabled = enabled;
            b.Opacity = enabled ? 1 : 0.3;
            b.Cursor = enabled ? Cursors.Hand : null;
            ((TextBlock)b.Child).Foreground = Theme.TextSecondary;
            if (!enabled) b.Background = Brushes.Transparent;
            ToolTipService.SetShowOnDisabled(b, true);
            b.ToolTip = tip;
        }

        // ---- the date plates ----

        private Border Plate(bool isTo, out TextBox box)
        {
            var tb = new TextBox
            {
                Style = null,   // not the library's input: a plain field inside our plate
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                FontFamily = Mono, FontSize = 12.5, Foreground = Theme.TextPrimary, CaretBrush = Theme.TextPrimary,
                SelectionBrush = Theme.Accent, MaxLength = 16, VerticalAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
            };
            box = tb;
            tb.Width = TextWidth("00.00.0000 00:00", Mono, 12.5) + 3;
            var ph = new TextBlock
            {
                Text = Loc.T("Main_SpeedDatePh"), FontFamily = Mono, FontSize = 12.5, Foreground = Theme.TextMuted,
                IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0),
            };
            var g = new Grid();
            g.Children.Add(ph);
            g.Children.Add(tb);
            var plate = new Border
            {
                Height = H, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0), Child = g, Cursor = Cursors.IBeam,
                ToolTip = Loc.T(isTo ? "Main_SpeedToTip" : "Main_SpeedFromTip"),
            };
            tb.TextChanged += (s, e) =>
            {
                if (!_applying)
                {
                    if (_autoApply == null)
                    {
                        _autoApply = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                        _autoApply.Tick += (s2, e2) =>
                        {
                            _autoApply.Stop();
                            if (ParsePlate(_fromBox.Text) != null && _toBox.Text.Trim().Length > 0 && ParsePlate(_toBox.Text) != null) Apply();
                        };
                    }
                    _autoApply.Stop();
                    _autoApply.Start();
                }
                ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (isTo) { if (_toBad) { _toBad = false; PaintPlate(plate, false); } }
                else if (_fromBad) { _fromBad = false; PaintPlate(plate, false); }
            };
            // A click anywhere on the plate: the caret in the field and the calendar under it.
            plate.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!IsWithin(e.OriginalSource as DependencyObject, tb))
                {
                    // on the plate's edge: the caret goes in after the window's own «click drops
                    // the focus» has run (SpeedWindow), so it stays
                    e.Handled = true;
                    tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.CaretIndex = tb.Text.Length; }), System.Windows.Threading.DispatcherPriority.Input);
                }
                OpenPop(isTo);
            };
            tb.GotKeyboardFocus += (s, e) => OpenPop(isTo);
            tb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; ClosePop(); Apply(); }
            };
            return plate;
        }

        private void PaintPlate(Border plate, bool bad)
        {
            bool on = Mode == Kind.Range;
            plate.Background = on ? Theme.ChipBgHover : Theme.BadgeNeutralBg;
            plate.BorderBrush = bad ? Theme.AccentRed : on ? Theme.TileBorderHover : Theme.BadgeNeutralBorder;
        }

        private static readonly Regex FullRx = new Regex(@"^(\d{1,2})\.(\d{1,2})\.(\d{4})\s+(\d{1,2}):(\d{2})$");
        private static readonly Regex TimeRx = new Regex(@"^(\d{1,2}):(\d{2})$");

        // «ДД.ММ.ГГГГ ЧЧ:ММ», or «ЧЧ:ММ» for today; local time; null = not understood.
        private static DateTime? ParsePlate(string s)
        {
            s = (s ?? "").Trim();
            try
            {
                var m = FullRx.Match(s);
                if (m.Success)
                    return new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value),
                                        int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0, DateTimeKind.Local);
                m = TimeRx.Match(s);
                if (m.Success)
                {
                    var d = DateTime.Now.Date;
                    return new DateTime(d.Year, d.Month, d.Day, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0, DateTimeKind.Local);
                }
            }
            catch (ArgumentOutOfRangeException) { }
            return null;
        }

        private static DateTime FloorMin(DateTime d) => new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Kind);
        private static DateTime Clamp(DateTime d, DateTime lo, DateTime hi) => d < lo ? lo : d > hi ? hi : d;

        // The history's bounds, local, whole minutes: its first measured minute … now.
        private DateTime HistLo()
        {
            if (_start == null) return FloorMin(DateTime.Now);
            var l = _start.Value.ToLocalTime();
            return FloorMin(l) < l ? FloorMin(l).AddMinutes(1) : FloorMin(l);
        }
        private static DateTime HistHi() => FloorMin(DateTime.Now);

        // Shows the period typed in the plates: «с» is needed, an empty «по» means up to now; both
        // are pulled inside the history, «по» after «с»; the plates then say what is shown.
        private void Apply()
        {
            var f = ParsePlate(_fromBox.Text);
            if (f == null) { _fromBad = true; PaintPlate(_fromPlate, true); return; }
            string toText = _toBox.Text.Trim();
            DateTime? t = toText.Length == 0 ? null : ParsePlate(toText);
            if (toText.Length > 0 && t == null) { _toBad = true; PaintPlate(_toPlate, true); return; }
            LoadAvail();
            var lo = HistLo();
            var hi = HistHi();
            if (lo > hi) lo = hi;
            var from = Clamp(f.Value, lo, hi);
            DateTime? to = t == null ? (DateTime?)null : Clamp(t.Value, lo, hi);
            if (to != null && to.Value <= from) to = from.AddMinutes(1) >= hi ? (DateTime?)null : from.AddMinutes(1);
            _applying = true;
            try
            {
                string fs = from.ToString(PlateFmt, Inv);
                if (_fromBox.Text != fs) _fromBox.Text = fs;
                if (to != null) { string ts = to.Value.ToString(PlateFmt, Inv); if (_toBox.Text != ts) _toBox.Text = ts; }
            }
            finally { _applying = false; }
            Mode = Kind.Range;
            FromUtc = from.ToUniversalTime();
            ToUtc = to?.ToUniversalTime();
            Paint();
            Changed?.Invoke();
        }

        // ---- the calendar ----

        private Popup _pop;
        private bool _calTo;                 // the calendar of «по» (else «с»)
        private DateTime _pick;              // local, whole minutes
        private DateTime _month;             // the first of the month shown
        private TextBlock _monthText;
        private Border _prevMonth, _nextMonth;
        private UniformGrid _days;
        private TextBox _timeBox;
        private StackPanel _timePick;
        private UniformGrid _hoursGrid, _minsGrid;
        private FrameworkElement _okRow;
        private int _tpos;                   // the time digit typed next: 0,1 = hours, 2,3 = minutes

        private void BuildCalendar()
        {
            var root = new StackPanel();

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            _nextMonth = MonthArrow("›", 1);
            _prevMonth = MonthArrow("‹", -1);
            DockPanel.SetDock(_nextMonth, Dock.Right);
            DockPanel.SetDock(_prevMonth, Dock.Right);
            head.Children.Add(_nextMonth);
            head.Children.Add(_prevMonth);
            _monthText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(_monthText);
            root.Children.Add(head);

            var dow = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
            var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
            int fd = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
            {
                var n = names[(fd + i) % 7];
                dow.Children.Add(new TextBlock
                {
                    Text = n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n,
                    FontSize = 11, Foreground = Theme.TextMuted, TextAlignment = TextAlignment.Center,
                });
            }
            root.Children.Add(dow);
            _days = new UniformGrid { Columns = 7 };
            root.Children.Add(_days);

            // time: the field, then (once it is clicked) the grids of hours and minutes
            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            timeRow.Children.Add(new TextBlock { Text = Loc.T("Main_SpeedTime"), FontSize = 12, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _timeBox = new TextBox
            {
                Style = null,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                FontFamily = Mono, FontSize = 12.5, Foreground = Theme.TextPrimary, CaretBrush = Theme.TextPrimary,
                SelectionBrush = Theme.Accent, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null, MaxLength = 5,
            };
            var timeFrame = new Border
            {
                Width = 64, Height = H, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = Theme.ListBg, BorderBrush = Theme.BadgeNeutralBorder, Child = _timeBox, Cursor = Cursors.IBeam,
            };
            _timeBox.GotKeyboardFocus += (s, e) => { timeFrame.BorderBrush = Theme.Accent; _tpos = 0; ShowTimePick(); _timeBox.Dispatcher.BeginInvoke(new Action(() => _timeBox.SelectAll())); };
            _timeBox.LostKeyboardFocus += (s, e) => timeFrame.BorderBrush = Theme.BadgeNeutralBorder;
            timeFrame.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!_timeBox.IsKeyboardFocusWithin) { _timeBox.Focus(); e.Handled = true; }
                ShowTimePick();
            };
            _timeBox.PreviewKeyDown += TimeKey;
            _timeBox.PreviewTextInput += (s, e) => e.Handled = true;   // digits go through TimeKey only
            DataObject.AddPastingHandler(_timeBox, (s, e) => e.CancelCommand());
            timeFrame.MouseWheel += (s, e) =>
            {
                e.Handled = true;
                bool hours = e.GetPosition(timeFrame).X < timeFrame.ActualWidth / 2;
                int t = _pick.Hour * 60 + _pick.Minute + (hours ? 60 : 1) * (e.Delta > 0 ? 1 : -1);
                t = Math.Max(0, Math.Min(1439, t));
                ShowTimePick();
                SetTime(t / 60, t % 60);
            };
            timeRow.Children.Add(timeFrame);
            root.Children.Add(timeRow);

            _timePick = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
            _timePick.Children.Add(Cap(Loc.T("Main_SpeedHoursCap")));
            _hoursGrid = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 6) };
            _timePick.Children.Add(_hoursGrid);
            _timePick.Children.Add(Cap(Loc.T("Main_SpeedMinutesCap")));
            _minsGrid = new UniformGrid { Columns = 6 };
            _timePick.Children.Add(_minsGrid);
            root.Children.Add(_timePick);

            var ok = new Wpf.Ui.Controls.Button
            {
                Content = Loc.T("Main_SpeedShow"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                FontSize = 12.5, Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right,
            };
            ok.Click += (s, e) => { FillPlate(); ClosePop(); Apply(); };
            _okRow = new Border { Margin = new Thickness(0, 10, 0, 0), Child = ok };
            root.Children.Add(_okRow);

            var card = new Border
            {
                Background = Theme.OverlayCardBg, BorderBrush = Theme.PickerBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Width = 260, Child = root,
                Margin = new Thickness(8),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
            };
            _pop = new Popup
            {
                Child = card, AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Bottom,
                VerticalOffset = -2, HorizontalOffset = -8, PopupAnimation = PopupAnimation.None,
            };
            card.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; ClosePop(); } };
        }

        private static TextBlock Cap(string s) => new TextBlock { Text = s, FontSize = 10.5, Foreground = Theme.TextMuted, Margin = new Thickness(1, 0, 0, 3) };

        private Border MonthArrow(string glyph, int dir)
        {
            var b = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, FontSize = 14, Foreground = Theme.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 2) },
            };
            b.MouseEnter += (s, e) => b.Background = Theme.PickerRowHover;
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; _month = _month.AddMonths(dir); PaintCal(); };
            return b;
        }

        // What the open calendar may pick: the history's bounds, narrowed by the other plate.
        private DateTime CalLo()
        {
            var lo = HistLo();
            if (_calTo) { var f = ParsePlate(_fromBox.Text); if (f != null && f.Value > lo) lo = f.Value; }
            return lo;
        }
        private DateTime CalHi()
        {
            var hi = HistHi();
            if (!_calTo) { var t = ParsePlate(_toBox.Text); if (t != null && t.Value < hi) hi = t.Value; }
            return hi;
        }
        private bool DayOk(DateTime day)
        {
            return _dayHours.ContainsKey(day.Date) && day.Date >= CalLo().Date && day.Date <= CalHi().Date;
        }

        private void OpenPop(bool isTo)
        {
            if (_pop.IsOpen && _calTo == isTo) return;
            _calTo = isTo;
            LoadAvail();
            var lo = CalLo();
            var hi = CalHi();
            if (lo > hi) lo = hi;
            var own = ParsePlate((isTo ? _toBox : _fromBox).Text);
            _pick = own != null ? Clamp(own.Value, lo, hi) : (isTo ? hi : lo);
            _month = new DateTime(_pick.Year, _pick.Month, 1);
            // The hours and minutes always open with it (2026-11-25: «сразу держать часы открытыми»).
            _timePick.Visibility = Visibility.Visible;
            _okRow.Visibility = isTo ? Visibility.Visible : Visibility.Collapsed;
            _timeBox.Text = _pick.ToString("HH:mm", Inv);
            _pop.PlacementTarget = isTo ? _toPlate : _fromPlate;
            PaintCal();
            PaintTime();
            _pop.IsOpen = true;
        }

        private void ClosePop()
        {
            if (_pop != null && _pop.IsOpen) _pop.IsOpen = false;
        }

        private void PaintCal()
        {
            var title = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            _monthText.Text = title.Length > 0 ? char.ToUpper(title[0]) + title.Substring(1) : title;
            bool prev = _dayHours.Keys.Any(d => d < _month && DayOk(d));
            bool next = _dayHours.Keys.Any(d => d >= _month.AddMonths(1) && DayOk(d));
            _prevMonth.Visibility = prev || next ? Visibility.Visible : Visibility.Collapsed;
            _nextMonth.Visibility = _prevMonth.Visibility;
            _prevMonth.IsEnabled = prev; _prevMonth.Opacity = prev ? 1 : 0.3;
            _nextMonth.IsEnabled = next; _nextMonth.Opacity = next ? 1 : 0.3;

            _days.Children.Clear();
            int fd = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            int lead = ((int)_month.DayOfWeek - fd + 7) % 7;
            for (int i = 0; i < lead; i++) _days.Children.Add(new Border());
            int n = DateTime.DaysInMonth(_month.Year, _month.Month);
            for (int d = 1; d <= n; d++) _days.Children.Add(DayCell(new DateTime(_month.Year, _month.Month, d)));
        }

        private Border DayCell(DateTime day)
        {
            bool ok = DayOk(day);
            bool picked = ok && day.Date == _pick.Date;
            var num = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0),
                Foreground = ok ? Theme.TextPrimary : Theme.TextDisabled,
            };
            var g = new Grid();
            g.Children.Add(num);
            if (ok)
            {
                // its hours with traffic, a 3-px strip at the bottom
                var hrs = _dayHours[day.Date];
                var strip = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 3), Background = Theme.PickerRowHover };
                for (int k = 0; k < 24; k++)
                {
                    strip.ColumnDefinitions.Add(new ColumnDefinition());
                    if (!hrs[k]) continue;
                    var seg = new Border { Background = Theme.Accent };
                    Grid.SetColumn(seg, k);
                    strip.Children.Add(seg);
                }
                g.Children.Add(strip);
            }
            var cell = new Border
            {
                Height = 26, Margin = new Thickness(1), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Child = g, Opacity = ok ? 1 : 0.6,
            };
            void Paint(bool hover)
            {
                cell.Background = !ok ? Brushes.Transparent : picked ? Theme.AccentSoft : hover ? Theme.AccentSoftHover : Theme.ChipBg;
                cell.BorderBrush = !ok ? Brushes.Transparent : picked ? Theme.Accent : Theme.BadgeNeutralBorder;
            }
            Paint(false);
            if (ok)
            {
                cell.Cursor = Cursors.Hand;
                cell.MouseEnter += (s, e) => Paint(true);
                cell.MouseLeave += (s, e) => Paint(false);
                cell.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    _pick = Clamp(new DateTime(day.Year, day.Month, day.Day, _pick.Hour, _pick.Minute, 0, DateTimeKind.Local), CalLo(), CalHi());
                    _timeBox.Text = _pick.ToString("HH:mm", Inv);
                    FillPlate();
                    PaintCal();
                    if (_timePick.Visibility == Visibility.Visible) PaintTime();
                };
            }
            else cell.ToolTip = _dayHours.ContainsKey(day.Date) ? Loc.T("Main_SpeedOutOfRange") : null;
            return cell;
        }

        private void FillPlate()
        {
            (_calTo ? _toBox : _fromBox).Text = _pick.ToString(PlateFmt, Inv);
        }

        // The picked day's allowed minutes: [mn, mx] of the day.
        private void DayRange(out int mn, out int mx)
        {
            var lo = CalLo();
            var hi = CalHi();
            mn = lo.Date == _pick.Date ? lo.Hour * 60 + lo.Minute : 0;
            mx = hi.Date == _pick.Date ? hi.Hour * 60 + hi.Minute : 1439;
        }

        private void SetTime(int h, int m)
        {
            int mn, mx;
            DayRange(out mn, out mx);
            int t = Math.Max(mn, Math.Min(mx, h * 60 + m));
            _pick = new DateTime(_pick.Year, _pick.Month, _pick.Day, t / 60, t % 60, 0, DateTimeKind.Local);
            _timeBox.Text = _pick.ToString("HH:mm", Inv);
            FillPlate();
            if (_timePick.Visibility == Visibility.Visible) PaintTime();
        }

        private void ShowTimePick()
        {
            if (_timePick.Visibility != Visibility.Visible) _timePick.Visibility = Visibility.Visible;
            PaintTime();
        }

        private void PaintTime()
        {
            int mn, mx;
            DayRange(out mn, out mx);
            _hoursGrid.Children.Clear();
            for (int k = 0; k < 24; k++)
            {
                int hour = k;
                bool ok = k * 60 + 59 >= mn && k * 60 <= mx;
                _hoursGrid.Children.Add(TimeCell(hour.ToString("00", Inv), ok, hour == _pick.Hour, () => SetTime(hour, _pick.Minute)));
            }
            _minsGrid.Children.Clear();
            for (int k = 0; k < 60; k += 5)
            {
                int min = k;
                bool ok = false;
                for (int q = k; q < k + 5; q++) { int t = _pick.Hour * 60 + q; if (t >= mn && t <= mx) ok = true; }
                _minsGrid.Children.Add(TimeCell(min.ToString("00", Inv), ok, _pick.Minute >= k && _pick.Minute < k + 5,
                                                () => { SetTime(_pick.Hour, min); Finish(); }));
            }
        }

        private static Border TimeCell(string text, bool ok, bool sel, Action click)
        {
            var b = new Border
            {
                Height = 22, Margin = new Thickness(1), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = text, FontFamily = Mono, FontSize = 11.5, Foreground = Theme.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Opacity = ok ? 1 : 0.3,
            };
            void Paint(bool hover)
            {
                b.Background = !ok ? Brushes.Transparent : sel ? Theme.AccentSoft : hover ? Theme.AccentSoftHover : Theme.ChipBg;
                b.BorderBrush = !ok ? Theme.BadgeNeutralBorder : sel ? Theme.Accent : Theme.BadgeNeutralBorder;
            }
            Paint(false);
            if (ok)
            {
                b.Cursor = Cursors.Hand;
                b.MouseEnter += (s, e) => Paint(true);
                b.MouseLeave += (s, e) => Paint(false);
                b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            }
            return b;
        }

        // Date, hour and minutes picked: the calendar closes; under «по» the period is shown.
        private void Finish()
        {
            FillPlate();
            bool to = _calTo;
            ClosePop();
            if (to) Apply();
            _owner?.Focus();
        }

        // The time field: everything selected on entry; digits overwrite from the start — the hours,
        // the colon stepped over, the minutes. 3–9 as the first hour digit is 03–09 (6–9 as the
        // first minute digit 06–09), so one press is enough; over 23 / 59 is not typed. Backspace
        // steps back; Enter finishes.
        private void TimeKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab) return;
            e.Handled = true;
            if (e.Key == Key.Enter) { SetTime(_pick.Hour, _pick.Minute); Finish(); return; }
            if (e.Key == Key.Escape) { ClosePop(); return; }
            int[] at = { 0, 1, 3, 4 };
            if (e.Key == Key.Back) { _tpos = Math.Max(0, _tpos - 1); _timeBox.Select(at[_tpos], 1); return; }
            int d = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0 : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
            if (d < 0 || _tpos > 3) return;
            var v = (Regex.IsMatch(_timeBox.Text, @"^\d\d:\d\d$") ? _timeBox.Text : _pick.ToString("HH:mm", Inv)).ToCharArray();
            char c = (char)('0' + d);
            if (_tpos == 0 && d > 2) { v[0] = '0'; v[1] = c; _tpos = 2; }
            else if (_tpos == 1 && v[0] == '2' && d > 3) { v[1] = '3'; _tpos = 2; }
            else if (_tpos == 2 && d > 5) { v[3] = '0'; v[4] = c; _tpos = 4; }
            else { v[at[_tpos]] = c; _tpos++; }
            if (v[0] == '2' && v[1] > '3') v[1] = '3';
            _timeBox.Text = new string(v);
            if (_tpos > 3)
            {
                SetTime((v[0] - '0') * 10 + (v[1] - '0'), (v[3] - '0') * 10 + (v[4] - '0'));
                _timeBox.Select(5, 0);
            }
            else _timeBox.Select(at[_tpos], 1);
        }

        // ---- helpers ----

        private static double TextWidth(string s, FontFamily family, double size)
        {
            var tf = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
#pragma warning disable CS0618
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, Brushes.Black).WidthIncludingTrailingWhitespace;
#pragma warning restore CS0618
        }

        private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
        {
            while (d != null)
            {
                if (ReferenceEquals(d, ancestor)) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }
    }
}
