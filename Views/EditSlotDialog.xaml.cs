using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Views
{
    // Add / edit a VPN variant. The user pastes a link (or picks a WireGuard .conf) and that is
    // all: the type is recognised from the link (ShareLinks.Detect) and the name comes from the
    // link itself, else "VLESS #1", "Clash #1", … — the way v2rayN / Hiddify / Clash Verge import.
    // When adding, several links (one per line) become several variants in one go.
    public partial class EditSlotDialog : FluentWindow
    {
        // The variant(s) to store. Result is the first one (editing always yields exactly one).
        public List<VpnSlot> Results { get; private set; } = new List<VpnSlot>();
        public VpnSlot Result => Results.FirstOrDefault();

        private readonly VpnSlot _editing;
        private readonly List<string> _taken;   // names of the OTHER variants
        private string _autoName;               // the name we filled in last; replaced while untouched
        private bool _loaded;

        public EditSlotDialog(VpnSlot existing, IEnumerable<string> otherNames = null)
        {
            InitializeComponent();
            _editing = existing;
            _taken = (otherNames ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            Title = existing == null ? Loc.T("Slot_TitleAdd") : Loc.T("Slot_TitleEdit");
            Bar.Title = Title;

            if (existing != null)
            {
                TxtName.Text = existing.Name;
                TxtValue.Text = existing.Value ?? "";
                ChkEnabled.IsChecked = existing.Enabled;
                TxtInterval.Text = (existing.UpdateIntervalHours > 0 ? existing.UpdateIntervalHours : 48).ToString();
            }
            else
            {
                ChkEnabled.IsChecked = true;
                TxtInterval.Text = "48";
                // A link already copied is almost always the one about to be pasted.
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var clip = Clipboard.GetText() ?? "";
                        if (Lines(clip).Any(l => ShareLinks.Detect(l).Ok)) TxtValue.Text = clip.Trim();
                    }
                }
                catch { }
            }
            Refresh();

            Loaded += (s, e) => { Services.UiHelpers.FitHeightToContent(this); _loaded = true; };
        }

        private static List<string> Lines(string text) =>
            (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Re-fit the window height after the content changed. First let WPF natively grow the
        // window to the new content, THEN snap to the exact height to remove the FluentWindow
        // SizeToContent gap.
        private void RefitHeight()
        {
            if (!_loaded) return;
            SizeToContent = SizeToContent.Height;
            Dispatcher.BeginInvoke(new Action(() =>
                Services.UiHelpers.FitHeightToContent(this)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnValueChanged(object sender, RoutedEventArgs e) => Refresh();

        // What the field holds right now: one variant, several, or nothing usable.
        private void Refresh()
        {
            if (LblDetected == null || PnlInterval == null || PnlName == null) return;
            var lines = Lines(TxtValue.Text);
            var infos = lines.Select(KindOf).ToList();
            bool anySub = infos.Any(i => i.Ok && i.Kind == VpnSlotKind.ClashSub);

            if (lines.Count == 0)
            {
                LblDetected.Visibility = Visibility.Collapsed;
                PnlName.Visibility = Visibility.Visible;
            }
            else if (lines.Count == 1)
            {
                var info = infos[0];
                LblDetected.Visibility = Visibility.Visible;
                LblDetected.Foreground = info.Ok ? Theme.AccentGreen : Theme.AccentRed;
                LblDetected.Text = info.Ok ? Loc.T("Slot_Detected", info.Label) : Loc.T("Slot_NotRecognized");
                PnlName.Visibility = Visibility.Visible;

                // Fill the name while the user has not typed their own.
                var current = (TxtName.Text ?? "").Trim();
                if (info.Ok && (current.Length == 0 || current == _autoName))
                {
                    _autoName = ShareLinks.AutoName(info, _taken);
                    TxtName.Text = _autoName;
                }
            }
            else
            {
                // Several links: each gets its own name, so there is no single name to edit.
                int bad = infos.Count(i => !i.Ok);
                var kinds = infos.Where(i => i.Ok).GroupBy(i => i.Label)
                                 .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key);
                LblDetected.Visibility = Visibility.Visible;
                LblDetected.Foreground = bad == 0 ? Theme.AccentGreen : Theme.BannerWarnText;
                LblDetected.Text = Loc.T("Slot_DetectedMany", infos.Count - bad, string.Join(", ", kinds))
                                   + (bad > 0 ? " " + Loc.T("Slot_DetectedBad", bad) : "");
                PnlName.Visibility = _editing == null ? Visibility.Collapsed : Visibility.Visible;
            }

            PnlInterval.Visibility = anySub ? Visibility.Visible : Visibility.Collapsed;
            PnlSubButtons.Visibility = ShowSubscription != null && _editing != null
                                       && _editing.Kind == VpnSlotKind.ClashSub
                                       && lines.Count == 1 && lines[0] == (_editing.Value ?? "").Trim()
                ? Visibility.Visible : Visibility.Collapsed;
            RefitHeight();
        }

        // «Обновить» / «Состав» of the stored subscription; set by Settings (it knows the
        // engine-side name). Refresh runs again once it's set, to show the buttons.
        private Action<bool> _showSubscription;
        public Action<bool> ShowSubscription
        {
            get => _showSubscription;
            set { _showSubscription = value; Refresh(); }
        }

        private void OnRefreshSub(object sender, RoutedEventArgs e) => ShowSubscription?.Invoke(true);
        private void OnSubContents(object sender, RoutedEventArgs e) => ShowSubscription?.Invoke(false);

        // The stored value of the slot being edited keeps its stored kind even if it no longer
        // reads as one of ours (an old .conf path without the extension, say).
        private ShareLinkInfo KindOf(string line)
        {
            var info = ShareLinks.Detect(line);
            if (!info.Ok && _editing != null && line == (_editing.Value ?? "").Trim())
            {
                info.Ok = true;
                info.Kind = _editing.Kind;
                info.Label = _editing.Kind.ToString();
            }
            return info;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("Slot_PickConfTitle"),
                Filter = Loc.T("Slot_PickConfFilter"),
            };
            if (ofd.ShowDialog(this) == true)
                TxtValue.Text = ofd.FileName;
        }

        private async void OnSave(object sender, RoutedEventArgs e)
        {
            var lines = Lines(TxtValue.Text);
            if (lines.Count == 0)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Slot_NotFilled"), Loc.T("Slot_NeedValue")).ConfigureAwait(true);
                TxtValue.Focus();
                return;
            }
            if (_editing != null && lines.Count > 1)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Slot_NotFilled"), Loc.T("Slot_OneWhenEditing")).ConfigureAwait(true);
                TxtValue.Focus();
                return;
            }

            var infos = lines.Select(KindOf).ToList();
            var bad = lines.Where((l, i) => !infos[i].Ok).ToList();
            if (bad.Count > 0)
            {
                await OverlayDialog.MessageAsync(this, Loc.T("Slot_NotRecognizedTitle"),
                    Loc.T("Slot_NotRecognizedList", string.Join("\n• ", bad.Select(Short)))).ConfigureAwait(true);
                TxtValue.Focus();
                return;
            }

            int hours;
            if (!int.TryParse((TxtInterval.Text ?? "").Trim(), out hours) || hours <= 0) hours = 48;
            bool enabled = ChkEnabled.IsChecked == true;
            var taken = new List<string>(_taken);
            var results = new List<VpnSlot>();

            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string name = lines.Count == 1 ? (TxtName.Text ?? "").Trim() : "";
                if (name.Length == 0) name = ShareLinks.AutoName(info, taken);
                taken.Add(name);

                results.Add(new VpnSlot
                {
                    Id = _editing != null ? _editing.Id : Guid.NewGuid().ToString("N"),
                    Name = name,
                    Kind = info.Kind,
                    Value = info.Value,
                    Enabled = enabled,
                    UpdateIntervalHours = hours,
                });
            }

            Results = results;
            DialogResult = true;
            Close();
        }

        private static string Short(string s) => s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
