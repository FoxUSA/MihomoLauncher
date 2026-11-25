using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace MihomoLauncher.Models
{
    // A "Rule": a named bundle of programs and/or sites that all obey ONE set of VPN
    // settings (RequiresVpn / kill-switch / allowed variants) and share one tile + icon.
    // Each contained program/site can be flagged Launch=true to fire from the tile click.
    //
    // The class is still named AppEntry (and the collection still <Apps>) so existing
    // XML configs keep deserializing; the old single-item fields below are read only by
    // the v11->v12 migration (NormalizeRules) and then left empty.

    // Kept ONLY so old <AppEntry Kind="..."> configs still deserialize during migration.
    public enum AppEntryKind
    {
        Program = 0,
        Link = 1,
    }

    // One program inside a rule: a local exe or a Store/MSIX app (by stable AUMID).
    public class RuleProgram
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Arguments { get; set; } = "";
        // Store/MSIX AUMID (PackageFamilyName!AppId). When set, launched via shell:AppsFolder;
        // Path still holds the current exe (for its image name in routing + icon).
        public string Aumid { get; set; } = "";
        // When true, this program is launched when the rule's tile is clicked.
        public bool Launch { get; set; } = false;

        // Added by auto-tracking (2026-11-22, AutoPrograms): a helper of the rule's own programs
        // that went online. Otherwise an ordinary program with Launch=false — routing, the
        // firewall, conflicts and path repair treat it like any other, with no code of their own.
        // Dragged into the manual part of the editor, it becomes a manual entry (false).
        public bool Auto { get; set; } = false;
        // Why it was added: the ancestor's exe name ("Code.exe"), or "folder:<exe>" when it lies in
        // the install folder of that program of the rule. Shown in the editor's tooltip.
        public string AutoFrom { get; set; } = "";
        // Added by auto-tracking and not seen yet (2026-11-22): the rule's gear turns amber
        // (MainWindow.AutoTrack) and the editor outlines the row. Saved, so it survives a restart;
        // cleared on all of a rule's entries when its editor closes, saved or cancelled.
        public bool AutoNew { get; set; } = false;

        [XmlIgnore]
        public bool IsStoreApp => !string.IsNullOrEmpty(Aumid);
    }

    // A whole folder inside a rule (2026-11-22): every program started from it or below goes the
    // rule's way (PROCESS-PATH-REGEX), its files are never listed. A separate list, not a flag on
    // RuleProgram, on purpose: every existing `foreach (prog in app.Programs)` assumes an exe path
    // (the firewall's ApplicationName, the image name in routing, path repair, icons) and must go
    // on ignoring folders.
    public class RuleFolder
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        // The «Плитка» box: the tile opens this folder in Explorer.
        public bool Launch { get; set; } = false;

        // «Авто-папка программы» (2026-11-23, AutoPrograms.SyncAutoFolders): the install folder of
        // one of the rule's MANUAL programs, added by the launcher itself, so that helpers started
        // from it go the rule's way from their very first connection — not only after auto-adding
        // has seen them online and the engine has reloaded. Routed, auto-tracked (what lies in it
        // gets no row of its own; the rule's auto rows inside it are dropped) and walled by the
        // firewall like any folder; watched only when the firewall walls its rule, and its path is
        // kept by the sync, not by path repair. It goes away by itself when its program leaves
        // the rule or another tile claims something in it. Dragged into the manual part of the
        // editor, it becomes a manual folder (false). Same meaning as on RuleProgram.
        public bool Auto { get; set; } = false;
        // The exe file name of the program whose folder it is ("Code.exe"), for the tooltip.
        public string AutoFrom { get; set; } = "";
        // Added and not seen yet: the amber gear / the outlined row, as for RuleProgram.AutoNew.
        public bool AutoNew { get; set; } = false;
    }

    // One site inside a rule: a URL to open/route, optionally carrying a cached blackmatrix7
    // Clash rule list (domains/IPs) that is routed through the same VPN target as the rule.
    public class RuleSite
    {
        public string Url { get; set; } = "";

        // blackmatrix7 .yaml rule list URL (optional). Its parsed payload is cached below.
        public string ClashRuleUrl { get; set; } = "";
        // Cached "TYPE,VALUE" fragments (no target), e.g. "DOMAIN-SUFFIX,instagram.com".
        public List<string> RuleItems { get; set; } = new List<string>();
        // When RuleItems were last fetched (round-trip ISO-8601, UTC). Empty = never.
        public string RuleFetchedUtc { get; set; } = "";

        // When true, this site is opened when the rule's tile is clicked.
        public bool Launch { get; set; } = false;

        [XmlIgnore]
        public bool HasRuleList => !string.IsNullOrWhiteSpace(ClashRuleUrl);
    }

    public class AppEntry
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "";

        // The main-window tab the tile lives on (2026-11-16): a TileTab.Id; "" / unknown = «Основная».
        // Also its place in the rule order — see TileTabs.
        public string TabId { get; set; } = "";

        // ----- Rule contents -----
        public List<RuleProgram> Programs { get; set; } = new List<RuleProgram>();
        public List<RuleSite> Sites { get; set; } = new List<RuleSite>();
        public List<RuleFolder> Folders { get; set; } = new List<RuleFolder>();

        // «Авто-добавление программ» (2026-11-22): the launcher watches this rule's programs and
        // adds their helpers that go online (child processes, programs of the same install folder)
        // by itself — see AutoPrograms. ON by default, for existing configs too (absent → true).
        // Switched off, tracking stops; what was already added stays.
        public bool AutoTrack { get; set; } = true;
        // Auto entries the person deleted (✕): never auto-added to THIS rule again — the editor's
        // third section «Не добавлять автоматически». A full path = that exact file (a version bump
        // is still that file, AutoPrograms.IsBanned); a bare exe name (older configs) = by name.
        public List<string> AutoIgnored { get; set; } = new List<string>();
        // The amber notice's clock (2026-11-23, AutoPrograms.NoticeTick): seconds the main window
        // has been ON SCREEN while this rule had unseen auto entries, since the last automatic
        // addition to it. At an hour the notice goes out by itself (AutoNew cleared; the entries
        // stay). Saved, so a restart does not start it over; 0 when nothing is unseen.
        public int AutoNoticeShownSec { get; set; } = 0;

        // «Сайт для проверки подключения»: the rule is put through a VPN configuration only once
        // this URL loads from that exit (see SiteChecker). Since 2026-11-23 the field IS the switch:
        // filled = checked, empty = not checked (the «Не проверять» box is gone) — and nothing is
        // ever written here by itself any more. Its host is also routed with the rule (RouteUrl).
        public string CheckUrl { get; set; } = "";

        // The rule's own address, never checked and not shown (2026-11-23): what the check field
        // used to hold under «Не проверять» — a template's site, a known service guessed from the
        // name — moved here by AppConfigStore.NormalizeCheckAddresses, and a template's site when
        // one is added. It keeps doing what it did there: its host is routed with the rule and a
        // template's tile opens it (OwnUrl). Both jobs, without making the rule wait for a check.
        public string HomeUrl { get; set; } = "";

        // The address that stands for the rule when one is needed — opened by a template's tile,
        // routed with the rule, used to find its icon: the check address, else HomeUrl.
        [XmlIgnore]
        public string OwnUrl => !string.IsNullOrWhiteSpace(CheckUrl) ? CheckUrl.Trim() : (HomeUrl ?? "").Trim();

        // Skip the availability probe entirely: the rule is put through the VPN as soon as a
        // configuration's node is up. Still the stored truth the connect reads (MainWindow
        // ProbeAppsAsync, the routing signature), but since 2026-11-23 it is no box of its own:
        // it is always "CheckUrl is empty" — the editor saves it so and the loader keeps it so.
        // Default ON — a site that answers slowly, rate-limits the probe or is dead from every exit
        // used to leave its tile red for hours while the tunnel itself was perfectly fine.
        public bool SkipCheck { get; set; } = true;

        // ----- Shared VPN + icon settings (apply to the whole rule) -----
        public bool RequiresVpn { get; set; } = false;

        // Is this rule switched ON? Persisted, so a rule keeps its state across restarts: the
        // launcher brings back exactly what was connected when it last closed, and nothing else.
        //
        // This is the difference between "never turned on" and "supposed to be on", which the
        // launcher previously had no way to express. RequiresVpn only says a rule CAN go through
        // a tunnel; the auto-retry read that as "ought to be connected" and switched every such
        // rule on by itself, one every 90 seconds, whether or not anyone had asked for it. The
        // only way to keep a rule off was to press the tile's X once per session - that is, to
        // switch off something that was never on, and to do it again after every restart.
        //
        // Turned ON by the tile's reconnect icon and by the connect-all button; turned OFF by
        // the tile's X and by disconnect-all. Default OFF: a rule that was merely added is not a
        // rule anyone asked to connect. Absent from an older config.xml, which deserialises to
        // false - so every existing rule starts switched off after the update, by design.
        public bool AutoConnect { get; set; } = false;

        // Auto-icon: fetch the tile icon from the server, recognised from the rule (see IconFetcher).
        public bool AutoIcon { get; set; } = true;
        public string IconPath { get; set; } = "";

        // Kill-switch (fail-closed). With RequiresVpn: when true the rule is BLOCKED (REJECT)
        // until connected to a working node; when false it "fails open" to DIRECT.
        public bool KillSwitch { get; set; } = true;

        // VPN variants this rule may NOT use during auto-pick (each value is a VpnSlot.Id).
        // Empty = every variant allowed (storing the EXCLUDED set auto-allows new variants).
        public List<string> DisabledVpnSlotIds { get; set; } = new List<string>();

        // This rule's own order of preference over the variants (VpnSlot.Id, best first).
        // The global Settings order is only a default: one rule may work best through Germany
        // and another through the Netherlands, and before this the first rule in the list
        // decided for everyone. Ids missing here keep the Settings order, after the listed
        // ones — so a newly added variant never silently jumps the queue.
        public List<string> VpnSlotOrder { get; set; } = new List<string>();

        // Network cards this rule's traffic leaves through (adapter names as Windows shows them),
        // in order of preference: the first one that works carries it, the next takes over when it
        // fails (2026-11-14). Empty = Windows decides, as before. Applied HARD and ALWAYS: switched
        // on or off, through a VPN (the VPN's own connection is dialled through the card) or direct,
        // and when none of the chosen cards works nothing goes out — never through another card.
        public List<string> Interfaces { get; set; } = new List<string>();

        // Each chosen card's Windows interface GUID, as "name=guid" (2026-11-24: a card renamed in
        // Windows was lost — the tile knew only the name, which is what the engine binds to). Kept
        // up to date by MainWindow.NicFollow; a name gone from the system is found again by its GUID.
        public List<string> InterfaceIds { get; set; } = new List<string>();

        // «Привязка к сетевой карте» (2026-11-19): the tile is pinned to ONE card (Interfaces holds
        // exactly it) and its traffic never moves to another — a cable pulled or the card switched
        // off and nothing goes out, instead of Windows carrying on through Wi-Fi with the home IP.
        // The engine does the blocking itself (`interface-name`: «interface not found»), the card
        // can't be changed on the tile while this is on, and the tile's programs are walled by the
        // firewall like a kill-switch's, so a stopped engine doesn't send them out through Windows.
        public bool LockNic { get; set; } = false;

        // The kill-switch as it was when «Напрямую» was picked on the tile's VPN plate (which has
        // to turn it off — direct + kill-switch = no internet). Picking a VPN on the plate again
        // puts back exactly this, never a forced «on» (2026-11-14: RDP got a kill-switch it never
        // had, and ✕ then blocked it).
        public bool KillSwitchBeforeDirect { get; set; }

        [XmlIgnore]
        public bool HasInterfaces => Interfaces != null && Interfaces.Exists(n => !string.IsNullOrWhiteSpace(n));

        // The built-in «Весь трафик» rule: always present, always first, cannot be deleted. It
        // has no programs or sites — it IS the catch-all: when switched on, everything no other
        // rule claims goes through its VPN (MATCH,<proxy> instead of MATCH,DIRECT). The other
        // rules sit above it in config.yaml and keep living their own lives: on → their own VPN,
        // off → their own DIRECT/REJECT, never swallowed by this one.
        public bool IsAllTraffic { get; set; } = false;

        public const string AllTrafficId = "all-traffic";
        public const string AllTrafficIcon = "Data/ico/all_trafic.png";

        // Legacy: the built-in «Глобально» tile existed for one day (2026-11-12) and was removed —
        // a tile placed first already outranks the others. Kept only so EnsureAllTraffic can find
        // and drop that entry from a config that has it.
        public bool IsGlobal { get; set; } = false;

        // «Блокировать» picked on the plate of «Всё остальное» (2026-11-12) instead of a VPN:
        // switched on, everything no other tile claims is blocked (MATCH,REJECT). The tiles keep
        // living their own lives. Switched off (✕), it blocks nothing. Picking a VPN clears it.
        public bool Block { get; set; } = false;

        // Any tile since 2026-11-16 («добавь сюда пункт Блокировать»): switched on, everything the
        // tile claims (its programs, sites, templates) is REJECT — no VPN node involved.
        [XmlIgnore]
        public bool IsBlockMode => Block;

        // Blocking right now. Since 2026-11-24 block mode has no on/off: picked on the plate it
        // blocks, always — no ▷/⏸, untouched by «OFF», a restart or the rule editor; only picking
        // a VPN / «Напрямую» on the plate ends it (the user: «никто и ничего не может отключить»).
        // It was «block mode AND switched on», and ⏸ left a grey «Блокировать» that blocked nothing.
        [XmlIgnore]
        public bool IsBlocking => IsBlockMode;

        // Needs no VPN node.
        [XmlIgnore]
        public bool IsNoNodeMode => IsBlockMode;

        // Built-in tile (not deletable, not draggable, launches nothing).
        [XmlIgnore]
        public bool IsBuiltIn => IsAllTraffic;

        // Does this rule launch anything from a tile? True only if at least one program or site
        // has Launch=true (the «Плитка» checkbox). If none is checked the rule is routing-only
        // (its ranges still go through the VPN) and shows NO tile on the main screen.
        // «Весь трафик» launches nothing but always has its tile.
        [XmlIgnore]
        public bool HasTileTarget =>
            IsAllTraffic ||
            (Programs != null && Programs.Exists(p => p != null && p.Launch)) ||
            (Sites != null && Sites.Exists(s => s != null && s.Launch)) ||
            (Folders != null && Folders.Exists(f => f != null && f.Launch));

        [XmlIgnore]
        public bool HasFolders => Folders != null && Folders.Exists(f => f != null && !string.IsNullOrWhiteSpace(f.Path));

        // ----- Legacy single-item fields: read ONLY by the v11->v12 migration -----
        [XmlAttribute]
        public AppEntryKind Kind { get; set; } = AppEntryKind.Program;
        public string Path { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Aumid { get; set; } = "";
        public string ForcedVpnSlotId { get; set; } = "";
        public bool ForceConnect { get; set; } = false;
        public string ClashRuleUrl { get; set; } = "";
        public List<string> RuleItems { get; set; } = new List<string>();
        public string RuleFetchedUtc { get; set; } = "";

        // True if the legacy Path holds a URL (migration helper only).
        [XmlIgnore]
        public bool LegacyIsUrl =>
            !string.IsNullOrEmpty(Path) &&
            (Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
