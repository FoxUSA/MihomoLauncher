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

        [XmlIgnore]
        public bool IsStoreApp => !string.IsNullOrEmpty(Aumid);
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

        // ----- Rule contents -----
        public List<RuleProgram> Programs { get; set; } = new List<RuleProgram>();
        public List<RuleSite> Sites { get; set; } = new List<RuleSite>();

        // Standalone connectivity probe: the tile is enabled only while this URL actually
        // loads from the current exit IP (see SiteChecker). Empty -> falls back to the first
        // site's Url, else the plain RequiresVpn gate.
        public string CheckUrl { get; set; } = "";

        // Skip the availability probe entirely ("Не проверять"): the rule is put through the
        // VPN as soon as a variant's node is up, without asking its CheckUrl whether it can be
        // reached. Default ON — a site that answers slowly, rate-limits the probe or is dead
        // from every exit used to leave its tile red for hours while the tunnel itself was
        // perfectly fine, and the person at the keyboard cannot be expected to debug that.
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

        // The built-in «Весь трафик» rule: always present, always first, cannot be deleted. It
        // has no programs or sites — it IS the catch-all: when switched on, everything no other
        // rule claims goes through its VPN (MATCH,<proxy> instead of MATCH,DIRECT). The other
        // rules sit above it in config.yaml and keep living their own lives: on → their own VPN,
        // off → their own DIRECT/REJECT, never swallowed by this one.
        public bool IsAllTraffic { get; set; } = false;

        public const string AllTrafficId = "all-traffic";
        public const string AllTrafficIcon = "Data/ico/all_trafic.png";

        // Does this rule launch anything from a tile? True only if at least one program or site
        // has Launch=true (the «Плитка» checkbox). If none is checked the rule is routing-only
        // (its ranges still go through the VPN) and shows NO tile on the main screen.
        // «Весь трафик» launches nothing but always has its tile.
        [XmlIgnore]
        public bool HasTileTarget =>
            IsAllTraffic ||
            (Programs != null && Programs.Exists(p => p != null && p.Launch)) ||
            (Sites != null && Sites.Exists(s => s != null && s.Launch));

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
