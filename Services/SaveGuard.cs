using System;
using System.Collections;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MihomoLauncher.Services
{
    // «Сохранить» is active only when the window holds something different from what it opened
    // with (2026-11-10: «кнопка сохранить не активна, если нет изменений — везде, где она есть»).
    //
    // How it knows: a fingerprint of the window — the caller's model (what Save would write:
    // lists, ranks, rows built in code) plus the value of every input control in the window's
    // LOGICAL tree (so fields on tabs that aren't shown count too). Taken once the window has
    // settled after opening; recomputed after anything the user can do — typing, a tick, a
    // pick, a click, a key, coming back from a child dialog. Changing something back to what it
    // was turns the button off again: it compares content, not "was touched".
    //
    // Read-only text boxes are skipped (status/diagnostic text changes by itself), and so is
    // anything tagged "nosave".
    public sealed class SaveGuard
    {
        private readonly Window _w;
        private readonly UIElement _save;
        private readonly Func<string> _model;
        private string _baseline;
        private bool _pending;

        // Set while the window's own Save runs (it disables the button itself meanwhile).
        public bool Suspended { get; set; }


        // «Добавить» dialogs: there is no "as it was" — something pre-filled (a link from the
        // clipboard) is exactly what is about to be saved. The button is on whenever there is
        // something to save, by this test instead of the comparison.
        private readonly Func<bool> _hasContent;

        private SaveGuard(Window w, UIElement save, Func<string> model, Func<bool> hasContent)
        {
            _w = w; _save = save; _model = model; _hasContent = hasContent;
        }

        public static SaveGuard Attach(Window w, UIElement save, Func<string> model = null, Func<bool> hasContent = null)
        {
            var g = new SaveGuard(w, save, model, hasContent);
            save.IsEnabled = false;

            w.Loaded += (s, e) =>
                // After everything queued for the opening (fills, async status, layout) has run.
                w.Dispatcher.BeginInvoke(new Action(g.Rebaseline), DispatcherPriority.ApplicationIdle);

            RoutedEventHandler any = (s, e) => g.Schedule();
            w.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => g.Schedule()), true);
            w.AddHandler(ToggleButton.CheckedEvent, any, true);
            w.AddHandler(ToggleButton.UncheckedEvent, any, true);
            w.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => g.Schedule()), true);
            w.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler((s, e) => g.Schedule()), true);
            w.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((s, e) => g.Schedule()), true);
            w.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) => g.Schedule()), true);
            w.AddHandler(UIElement.DropEvent, new DragEventHandler((s, e) => g.Schedule()), true);
            w.Activated += (s, e) => g.Schedule();   // back from a child dialog that edited the model

            // Safety net for any path the events above miss (a model edit made in code right
            // after a child dialog returns, a drag finished outside the window): a greyed-out
            // «Сохранить» over a real change would be worse than one recompute a second.
            var timer = new DispatcherTimer(DispatcherPriority.Background, w.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => g.Refresh();
            w.Loaded += (s, e) => timer.Start();
            w.Closed += (s, e) => timer.Stop();
            return g;
        }

        // The current content becomes "unchanged" (e.g. after an apply-in-place).
        public void Rebaseline()
        {
            _baseline = Fingerprint();
            Refresh();
        }

        // Coalesced: one recompute per burst of events, after bindings have pushed their values.
        public void Schedule()
        {
            if (_pending) return;
            _pending = true;
            _w.Dispatcher.BeginInvoke(new Action(() => { _pending = false; Refresh(); }), DispatcherPriority.Background);
        }

        public void Refresh()
        {
            if (_baseline == null || Suspended) return;
            try { _save.IsEnabled = _hasContent != null ? _hasContent() : Fingerprint() != _baseline; }
            catch { _save.IsEnabled = true; }   // never lock the user out of saving
        }

        private string Fingerprint()
        {
            var sb = new StringBuilder();
            try { if (_model != null) sb.Append(_model()); } catch { sb.Append("<model?>"); }
            sb.Append('\u0001');
            Walk(_w.Content as DependencyObject, sb, 0);
            return sb.ToString();
        }

        private static void Walk(DependencyObject node, StringBuilder sb, int depth)
        {
            if (node == null || depth > 200) return;
            var fe = node as FrameworkElement;
            if (fe != null && "nosave".Equals(fe.Tag as string)) return;

            var name = fe != null ? fe.Name : "";
            var tb = node as TextBox;
            if (tb != null)
            {
                if (!tb.IsReadOnly) sb.Append(name).Append('=').Append(tb.Text).Append('\u0002');
            }
            else if (node is PasswordBox)
            {
                // never fingerprint secrets; such fields are tracked through the model
            }
            else if (node is ToggleButton tg)
            {
                sb.Append(name).Append('=').Append(tg.IsChecked.HasValue ? (tg.IsChecked.Value ? "1" : "0") : "-").Append('\u0002');
            }
            else if (node is ComboBox cb)
            {
                sb.Append(name).Append('=').Append(cb.SelectedIndex).Append('|').Append(cb.IsEditable ? cb.Text : "").Append('\u0002');
            }
            else if (node is RangeBase rb)
            {
                sb.Append(name).Append('=').Append(rb.Value).Append('\u0002');
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                var d = child as DependencyObject;
                if (d != null) Walk(d, sb, depth + 1);
            }
        }

        // Helpers for model fingerprints.
        public static string Join(IEnumerable items, Func<object, string> one)
        {
            var sb = new StringBuilder();
            if (items == null) return "";
            foreach (var o in items) sb.Append(one(o)).Append('\u0003');
            return sb.ToString();
        }

        public static string Xml<T>(T obj)
        {
            if (obj == null) return "";
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(T));
            using (var w = new System.IO.StringWriter())
            {
                ser.Serialize(w, obj);
                return w.ToString();
            }
        }
    }
}
