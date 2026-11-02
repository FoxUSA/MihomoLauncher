using System;
using System.Globalization;
using System.Windows;

namespace MihomoLauncher.Services
{
    public enum AppLang { Ru, En }

    // Tiny localization layer. All translatable strings live in a per-language
    // ResourceDictionary (Strings\Strings.ru.xaml / Strings.en.xaml):
    //   * XAML reads them via {DynamicResource Key} — swapping the dictionary updates the
    //     UI live, no restart;
    //   * code-behind reads them via Loc.T("Key") / Loc.T("Key", arg0, arg1).
    // The active dictionary is merged into Application.Current.Resources, so both paths hit
    // the same source. Code-driven UI (tiles, banners built in C#) re-renders on
    // LanguageChanged.
    public static class Loc
    {
        // Marker key present in every Strings dictionary so we can find + remove the active
        // one when swapping languages.
        private const string Marker = "__StringsDict";

        public static AppLang Current { get; private set; } = AppLang.Ru;

        // Raised after the language (and merged dictionary) changes — UI that's built in code
        // subscribes to rebuild itself.
        public static event Action LanguageChanged;

        public static string Code => Current == AppLang.En ? "en" : "ru";

        // Decide the initial language: an explicit saved value ("ru"/"en") wins; otherwise
        // fall back to the system UI culture (Russian → Ru, everything else → En).
        public static AppLang Resolve(string saved)
        {
            if (string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase)) return AppLang.En;
            if (string.Equals(saved, "ru", StringComparison.OrdinalIgnoreCase)) return AppLang.Ru;
            try
            {
                var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return two.Equals("ru", StringComparison.OrdinalIgnoreCase) ? AppLang.Ru : AppLang.En;
            }
            catch { return AppLang.En; }
        }

        // Merge the dictionary for `lang`, drop the previous one, and notify. Safe to call
        // before any window exists (used once at startup).
        public static void Apply(AppLang lang)
        {
            Current = lang;
            SwapDictionary(lang);
            try { LanguageChanged?.Invoke(); } catch { }
        }

        private static void SwapDictionary(AppLang lang)
        {
            var app = Application.Current;
            if (app == null) return;
            var dicts = app.Resources.MergedDictionaries;
            for (int i = dicts.Count - 1; i >= 0; i--)
            {
                try { if (dicts[i].Contains(Marker)) dicts.RemoveAt(i); } catch { }
            }
            var name = lang == AppLang.En ? "en" : "ru";
            var uri = new Uri("pack://application:,,,/Strings/Strings." + name + ".xaml", UriKind.Absolute);
            dicts.Add(new ResourceDictionary { Source = uri });
        }

        // Code-behind lookup. Returns the localized string, or the key itself if missing.
        public static string T(string key)
        {
            try
            {
                if (Application.Current?.Resources[key] is string s) return s;
            }
            catch { }
            return key;
        }

        // Parameterized lookup: the stored string is a composite-format template ("{0}…{1}").
        public static string T(string key, params object[] args)
        {
            var fmt = T(key);
            try { return string.Format(fmt, args); }
            catch { return fmt; }
        }
    }
}
