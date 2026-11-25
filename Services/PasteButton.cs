using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace MihomoLauncher.Services
{
    // A «paste» icon in an empty link / address field (2026-11-23), where WPF-UI's clear cross sits
    // once there is text: a click puts the clipboard's text in (trimmed), exactly as if it had been
    // typed — the field's TextChanged runs, a binding updates — and focus goes to the field. No text
    // on the clipboard: nothing happens. With text in the field the cross is back, as before.
    //
    //   <ui:TextBox svc:PasteButton.IsEnabled="True" … />
    //
    // How: the button goes into the same cell of the template's grid as the template's own
    // "ClearButton" (WPF-UI 4.3, ui:TextBox), so it takes the cross's place and the text/placeholder
    // column gives way to it like it does to the cross. Visible only while the field is empty,
    // enabled and editable — the cross shows only with text, so the two never meet. Not on a
    // plain TextBox (no ClearButton in its template): nothing happens there.
    //
    // For link / address inputs (VPN link, check address, site URL, DNS servers) and, since
    // 2026-11-25 at the user's word, the speed window's search — not names.
    public static class PasteButton
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(PasteButton), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

        // The button put into this field's template (re-made when the template is rebuilt).
        private static readonly DependencyProperty ButtonProperty = DependencyProperty.RegisterAttached(
            "Button", typeof(Wpf.Ui.Controls.Button), typeof(PasteButton), new PropertyMetadata(null));

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var tb = d as System.Windows.Controls.TextBox;
            if (tb == null || !(bool)e.NewValue) return;
            tb.Loaded += (s, a) => Attach(tb);
            tb.TextChanged += (s, a) => Update(tb);
            tb.IsEnabledChanged += (s, a) => Update(tb);
            if (tb.IsLoaded) Attach(tb);
        }

        private static void Attach(System.Windows.Controls.TextBox tb)
        {
            try
            {
                if (!GetIsEnabled(tb)) return;
                tb.ApplyTemplate();
                var clear = tb.Template?.FindName("ClearButton", tb) as FrameworkElement;
                var host = clear == null ? null : VisualTreeHelper.GetParent(clear) as System.Windows.Controls.Grid;
                if (host == null) return;
                var btn = (Wpf.Ui.Controls.Button)tb.GetValue(ButtonProperty);
                if (btn != null && ReferenceEquals(VisualTreeHelper.GetParent(btn), host)) { Update(tb); return; }

                btn = Make(tb, clear);
                System.Windows.Controls.Grid.SetColumn(btn, System.Windows.Controls.Grid.GetColumn(clear));
                System.Windows.Controls.Grid.SetRow(btn, System.Windows.Controls.Grid.GetRow(clear));
                host.Children.Add(btn);
                tb.SetValue(ButtonProperty, btn);
                Update(tb);
            }
            catch (Exception ex) { try { Log.Error("paste-button", ex); } catch { } }
        }

        // Looks like the template's cross: a flat icon button, the library's own hover fills.
        private static Wpf.Ui.Controls.Button Make(System.Windows.Controls.TextBox tb, FrameworkElement clear)
        {
            var btn = new Wpf.Ui.Controls.Button
            {
                Appearance = ControlAppearance.Secondary,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                MouseOverBorderBrush = Brushes.Transparent,
                PressedBorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 24,
                MinHeight = 0,
                // The cross's margin as the template gives it with text (empty, it is set to 0).
                Margin = new Thickness(0, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Arrow,
                IsTabStop = false,
                Focusable = false,
                Icon = new SymbolIcon { Symbol = SymbolRegular.ClipboardPaste24, FontSize = tb.FontSize },
            };
            btn.SetResourceReference(Wpf.Ui.Controls.Button.MouseOverBackgroundProperty, "SubtleFillColorSecondaryBrush");
            btn.SetResourceReference(Wpf.Ui.Controls.Button.PressedBackgroundProperty, "SubtleFillColorTertiaryBrush");
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextControlButtonForeground");
            // A resource reference, so the tooltip follows the language switch.
            btn.SetResourceReference(FrameworkElement.ToolTipProperty, "Ctl_Paste");
            btn.Click += (s, e) => { e.Handled = true; PasteInto(tb); };
            return btn;
        }

        private static void Update(System.Windows.Controls.TextBox tb)
        {
            var btn = (Wpf.Ui.Controls.Button)tb.GetValue(ButtonProperty);
            if (btn == null) return;
            bool show = string.IsNullOrEmpty(tb.Text) && tb.IsEnabled && !tb.IsReadOnly;
            btn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // As Ctrl+V would: a one-line field takes the first line only, MaxLength is kept.
        private static void PasteInto(System.Windows.Controls.TextBox tb)
        {
            string clip = null;
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); }
            catch { clip = null; }   // the clipboard is busy in another program
            clip = (clip ?? "").Trim();
            if (!tb.AcceptsReturn)
            {
                int nl = clip.IndexOfAny(new[] { '\r', '\n' });
                if (nl >= 0) clip = clip.Substring(0, nl).Trim();
            }
            if (tb.MaxLength > 0 && clip.Length > tb.MaxLength) clip = clip.Substring(0, tb.MaxLength);
            if (clip.Length == 0) return;
            tb.Focus();
            tb.Text = clip;
            tb.CaretIndex = tb.Text.Length;
        }
    }
}
