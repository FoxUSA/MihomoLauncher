using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // One main-window tab (2026-11-16).
    public class TileTab
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        // As typed; "" on «Основная» = its name in the UI language.
        public string Name { get; set; } = "";
    }

    // Tabs on the main window (2026-11-16, .dev\mockups\tile-tabs.html). A tile belongs to one
    // tab (AppEntry.TabId); «Основная» is first and can't be deleted; «Всё остальное» lives there.
    //
    // The rule order stays what it always was — the order of cfg.Apps — only now Apps is kept
    // grouped by tab: every rule of «Основная», then of the second tab, and so on, each group in
    // its tiles' order. Normalize restores that after anything that moved rules around, so the
    // engine's config builder needs to know nothing about tabs.
    public static class TileTabs
    {
        public const string MainId = "main";

        public static void Ensure(AppConfig cfg)
        {
            if (cfg == null) return;
            if (cfg.Tabs == null) cfg.Tabs = new List<TileTab>();
            cfg.Tabs.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Id));
            var main = cfg.Tabs.FirstOrDefault(t => t.Id == MainId);
            if (main == null) cfg.Tabs.Insert(0, new TileTab { Id = MainId });
            else if (cfg.Tabs[0] != main) { cfg.Tabs.Remove(main); cfg.Tabs.Insert(0, main); }
        }

        public static string NameOf(TileTab t)
        {
            if (t == null) return "";
            if (string.IsNullOrWhiteSpace(t.Name)) return t.Id == MainId ? Loc.T("Main_TabMain") : "—";
            return t.Name;
        }

        // The tab a rule is on; «Всё остальное» and anything pointing nowhere — «Основная».
        public static int IndexOf(AppConfig cfg, AppEntry a)
        {
            if (cfg?.Tabs == null || a == null || a.IsAllTraffic || string.IsNullOrEmpty(a.TabId)) return 0;
            int i = cfg.Tabs.FindIndex(t => t.Id == a.TabId);
            return i < 0 ? 0 : i;
        }

        public static TileTab Current(AppConfig cfg)
        {
            Ensure(cfg);
            return cfg.Tabs.FirstOrDefault(t => t.Id == cfg.CurrentTabId) ?? cfg.Tabs[0];
        }

        // The id a new rule gets: the tab open right now ("" for «Основная»).
        public static string CurrentIdForNew(AppConfig cfg)
        {
            var t = Current(cfg);
            return t.Id == MainId ? "" : t.Id;
        }

        public static bool OnTab(AppConfig cfg, AppEntry a, TileTab tab) => cfg.Tabs.IndexOf(tab) == IndexOf(cfg, a);

        // Apps grouped by tab, keeping the order inside each group. True when anything moved.
        public static bool Normalize(AppConfig cfg)
        {
            if (cfg?.Apps == null) return false;
            Ensure(cfg);
            var sorted = cfg.Apps.Select((a, i) => new { a, i, t = IndexOf(cfg, a) })
                                 .OrderBy(x => x.t).ThenBy(x => x.i).Select(x => x.a).ToList();
            if (sorted.SequenceEqual(cfg.Apps)) return false;
            cfg.Apps.Clear();
            cfg.Apps.AddRange(sorted);
            return true;
        }

        // Move a rule to `tab`, at the end of that tab's group.
        public static void MoveTo(AppConfig cfg, AppEntry a, TileTab tab)
        {
            Ensure(cfg);
            int ti = Math.Max(0, cfg.Tabs.IndexOf(tab));
            a.TabId = tab.Id == MainId ? "" : tab.Id;
            cfg.Apps.Remove(a);
            int at = 0;
            for (int i = 0; i < cfg.Apps.Count; i++)
                if (IndexOf(cfg, cfg.Apps[i]) <= ti) at = i + 1;
            cfg.Apps.Insert(at, a);
            Normalize(cfg);
        }

        public static TileTab Add(AppConfig cfg)
        {
            Ensure(cfg);
            int n = cfg.Tabs.Count + 1;
            while (cfg.Tabs.Any(t => NameOf(t) == Loc.T("Main_TabNew", n))) n++;
            var tab = new TileTab { Name = Loc.T("Main_TabNew", n) };
            cfg.Tabs.Add(tab);
            return tab;
        }

        // Its tiles go back to «Основная» (at the end) — nothing is lost.
        public static void Delete(AppConfig cfg, TileTab tab)
        {
            Ensure(cfg);
            if (tab == null || tab.Id == MainId) return;
            var main = cfg.Tabs[0];
            foreach (var a in cfg.Apps.Where(x => x != null && x.TabId == tab.Id).ToList()) MoveTo(cfg, a, main);
            cfg.Tabs.Remove(tab);
            if (cfg.CurrentTabId == tab.Id) cfg.CurrentTabId = "";
            Normalize(cfg);
        }
    }
}
