using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    [XmlRoot("Config")]
    public class AppConfig
    {
        public const int CurrentVersion = 13;

        [XmlAttribute]
        public int Version { get; set; } = CurrentVersion;

        // UI language: "ru" / "en". Empty => auto-detect from the system UI culture on first
        // run (see Loc.Resolve). Persisted once the user picks via the title-bar toggle.
        public string Language { get; set; } = "";

        // "Remote desktop session" safe mode: when on, the launcher watches the mihomo engine
        // and force-stops it every 60s it's running, so a bad VPN config can't lock you out
        // over RDP/AnyDesk. Off by default. Toggled from the main screen, Settings, or the
        // engine-install prompt.
        public bool RemoteDesktopSafeMode { get; set; }

        // Firewall kill-switch: the programs of a kill-switch rule are blocked by WINDOWS itself
        // whenever their traffic is not leaving through the tunnel — so the protection survives
        // the engine crashing, being restarted, being repaired, or the launcher not running at
        // all. See FirewallGuard. On by default: a rule's kill-switch already means "no internet
        // for this program without the VPN", and until now that promise quietly lapsed every time
        // the engine went down. Takes effect only once MihomoLauncher_fw is registered (the
        // «Переустановить движок» button does it, one UAC prompt).
        public bool FirewallKillSwitch { get; set; } = true;

        // ----- mihomo (Clash.Meta) runtime -----
        // Empty until the engine is installed; a fresh config gets AppConfigStore's writable
        // default target, and the engine is downloaded there on first run (engine-missing flow).
        public string MihomoExePath { get; set; } = "";
        // Working dir mihomo runs from (-d). Empty => %LocalAppData%\MihomoLauncher\mihomo.
        // The launcher (non-admin) writes config.yaml here; mihomo (elevated) reads it.
        public string MihomoWorkDir { get; set; } = "";
        public string MihomoApiAddr { get; set; } = "127.0.0.1:9090";
        // External-controller secret — generated once on first save, reused after.
        public string MihomoSecret { get; set; } = "";
        public int MixedPort { get; set; } = 7890;

        // The DNS-for-the-whole-machine settings (v13) lived here and were removed on
        // 2026-08-15: choosing the PC's resolvers never helped the rules (fake-ip hands rule
        // domains to the proxy, which resolves them on the far side) and could make the whole
        // machine crawl. Old config.xml files still carry the elements — XmlSerializer just
        // ignores what it doesn't know, so no migration is needed.

        // «DNS для прямого трафика» (2026-11-02): a resolver for what the engine sends DIRECT and
        // nothing else — see DirectDns. Kept as typed (primary first); recognised at build time.
        // New element names on purpose, so nothing of the removed feature can come back to life.
        public bool DirectDnsEnabled { get; set; }
        public List<string> DirectDns { get; set; } = new List<string>();
        // Whose servers: "" = the ones typed above; a VpnSlot.Id = the DNS that Clash
        // subscription sends in its own `dns:` block (DirectDns.Offered). Falls back to the typed
        // ones when that subscription stops sending DNS or is deleted.
        public string DirectDnsSource { get; set; } = "";

        // Configurable VPN sources tried in order (slots 2-5; step 1 is always direct).
        public List<VpnSlot> VpnSlots { get; set; } = new List<VpnSlot>();

        public List<AppEntry> Apps { get; set; } = new List<AppEntry>();
    }

    public static class AppConfigStore
    {
        // Legacy per-user data dir (%LocalAppData%\MihomoLauncher). Still used for the mihomo
        // engine exe + working dir; the config/icons moved into DataUser beside the exe.
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MihomoLauncher");

        // All user settings now live in DataUser\ beside the exe, so they SURVIVE an update
        // (which wipes Data\ + MihomoLauncher.exe but never DataUser\) and travel with the
        // portable one-folder install. Migrated once from the legacy %LocalAppData% copy.
        public static string DataUserDir => VersionInfo.DataUserDir;
        private static string LegacyConfigPath => Path.Combine(ConfigDir, "config.xml");
        private static string LegacyIconsDir => Path.Combine(ConfigDir, "icons");

        // Default config location. The user may relocate the config file (Settings →
        // «Настройки»); a small pointer file records the chosen path so it survives restarts.
        public static string DefaultConfigPath => Path.Combine(DataUserDir, "config.xml");
        private static string ConfigPointerPath => Path.Combine(DataUserDir, "config-location.txt");
        private static string _configPathCache;

        // Effective config path: the user-chosen one (pointer) if set, else the default.
        public static string ConfigPath
        {
            get
            {
                if (_configPathCache != null) return _configPathCache;
                try
                {
                    if (File.Exists(ConfigPointerPath))
                    {
                        var p = (File.ReadAllText(ConfigPointerPath) ?? "").Trim();
                        if (p.Length > 0) { _configPathCache = p; return p; }
                    }
                }
                catch { }
                _configPathCache = DefaultConfigPath;
                return _configPathCache;
            }
        }

        // Point the launcher at a different config file. Writes/clears the pointer and brings
        // the current config over to the new location so nothing is lost. Empty / the default
        // path reverts to the default (pointer removed). Subsequent Save()s use the new path.
        public static void SetConfigPath(string newPath)
        {
            var oldPath = ConfigPath;
            newPath = (newPath ?? "").Trim();
            try
            {
                Directory.CreateDirectory(DataUserDir);   // the pointer file lives in DataUser
                if (newPath.Length == 0 || string.Equals(newPath, DefaultConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(ConfigPointerPath)) File.Delete(ConfigPointerPath);
                    _configPathCache = DefaultConfigPath;
                }
                else
                {
                    var dir = Path.GetDirectoryName(newPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(ConfigPointerPath, newPath);
                    _configPathCache = newPath;
                }
                if (!string.Equals(oldPath, _configPathCache, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(oldPath) && !File.Exists(_configPathCache))
                {
                    File.Copy(oldPath, _configPathCache);
                }
            }
            catch (Exception ex)
            {
                // Without this the settings quietly keep living at the old path while the UI
                // shows the new one, and the next edit appears to vanish.
                Log.Error("config-path-relocate", ex);
            }
        }

        public static string IconsDir => Path.Combine(DataUserDir, "icons");

        // Default install location for the mihomo engine on a fresh setup: DataUser\ beside the
        // exe, so it lives with the rest of the user data, survives updates, and is removed by
        // the uninstaller's data wipe. The engine is downloaded here on first run when no exe is
        // configured yet. (Legacy default was %LocalAppData%\MihomoLauncher — existing configs that
        // still point there keep working; only fresh configs use DataUser.)
        public static string DefaultMihomoExeTarget =>
            Path.Combine(DataUserDir, "mihomo-windows-amd64.exe");

        // Default mihomo working dir (where the launcher writes config.yaml): DataUser\mihomo
        // beside the exe, so it's self-contained/portable, survives updates with the rest of the
        // user data, and is removed by the uninstaller's data wipe.
        public static string DefaultMihomoWorkDir => Path.Combine(DataUserDir, "mihomo");

        // Old defaults, auto-migrated to DefaultMihomoWorkDir on load (cleared so they resolve
        // to the new default): the %LocalAppData%\MihomoLauncher\mihomo location and the standard
        // ~/.config/mihomo (= %USERPROFILE%\.config\mihomo) used before the DataUser move.
        private static readonly string LegacyMihomoWorkDir = Path.Combine(ConfigDir, "mihomo");
        private static readonly string LegacyStdMihomoWorkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mihomo");

        // Effective working dir for a (possibly empty) config value.
        public static string ResolveMihomoWorkDir(AppConfig cfg)
        {
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.MihomoWorkDir))
                return cfg.MihomoWorkDir;
            return DefaultMihomoWorkDir;
        }

        private static bool IsLegacyWorkDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            var d = dir.TrimEnd('\\');
            return string.Equals(d, LegacyMihomoWorkDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(d, LegacyStdMihomoWorkDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static AppConfig Load()
        {
            EnsureDirs();
            MigrateLegacyUserDataIfNeeded();
            if (!File.Exists(ConfigPath))
            {
                var defaults = CreateDefaults();
                EnsureAllTraffic(defaults.Apps);
                Save(defaults);
                return defaults;
            }
            try
            {
                // Read + deserialize, then CLOSE the file before any Save() — Save deletes
                // and replaces ConfigPath, which fails with a sharing violation if the read
                // stream is still open on the same file.
                AppConfig loaded;
                var ser = new XmlSerializer(typeof(AppConfig));
                using (var fs = File.OpenRead(ConfigPath))
                    loaded = (AppConfig)ser.Deserialize(fs);

                if (loaded.Version < AppConfig.CurrentVersion)
                {
                    var migrated = MigrateOrReset(loaded);
                    EnsureAllTraffic(migrated.Apps);
                    Save(migrated);
                    return migrated;
                }
                if (loaded.Apps == null) loaded.Apps = new List<AppEntry>();
                bool dirty = false;
                // Migrate the old workdirs (%LocalAppData%\MihomoLauncher\mihomo or
                // ~/.config/mihomo) to the new DataUser\mihomo default by clearing them
                // (empty => DefaultMihomoWorkDir).
                if (IsLegacyWorkDir(loaded.MihomoWorkDir))
                {
                    loaded.MihomoWorkDir = "";
                    dirty = true;
                }
                if (NormalizeRules(loaded.Apps)) dirty = true;
                if (EnsureAllTraffic(loaded.Apps)) dirty = true;
                if (dirty) Save(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                // Don't silently overwrite a real config with defaults — that's data loss.
                // Log it, back the unreadable file aside so it isn't lost, and start fresh.
                try { Log.Error("AppConfigStore.Load", ex); } catch { }
                try
                {
                    var bad = ConfigPath + ".unreadable";
                    if (File.Exists(ConfigPath)) File.Copy(ConfigPath, bad, true);
                }
                catch { }
                var fresh = CreateDefaults();
                EnsureAllTraffic(fresh.Apps);
                return fresh;
            }
        }

        public static void Save(AppConfig config)
        {
            EnsureDirs();
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch { }
            config.Version = AppConfig.CurrentVersion;
            var ser = new XmlSerializer(typeof(AppConfig));
            var tmp = ConfigPath + ".tmp";
            using (var fs = File.Create(tmp))
            {
                ser.Serialize(fs, config);
            }
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            File.Move(tmp, ConfigPath);
        }

        public static string CopyIconToStore(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return "";
            Directory.CreateDirectory(IconsDir);   // created lazily — only when a custom icon is actually stored
            var ext = Path.GetExtension(sourcePath);
            var dest = Path.Combine(IconsDir, Guid.NewGuid().ToString("N") + ext);
            File.Copy(sourcePath, dest, overwrite: true);
            return dest;
        }

        private static AppConfig MigrateOrReset(AppConfig old)
        {
            var defaults = CreateDefaults();
            if (old != null)
            {
                // Carry over mihomo settings if a previous v9+ config had them.
                if (!string.IsNullOrEmpty(old.MihomoExePath)) defaults.MihomoExePath = old.MihomoExePath;
                if (!string.IsNullOrEmpty(old.MihomoWorkDir) && !IsLegacyWorkDir(old.MihomoWorkDir)) defaults.MihomoWorkDir = old.MihomoWorkDir;
                if (!string.IsNullOrEmpty(old.MihomoApiAddr)) defaults.MihomoApiAddr = old.MihomoApiAddr;
                if (!string.IsNullOrEmpty(old.MihomoSecret)) defaults.MihomoSecret = old.MihomoSecret;
                if (old.MixedPort > 0) defaults.MixedPort = old.MixedPort;
                if (old.VpnSlots != null && old.VpnSlots.Count > 0) defaults.VpnSlots = old.VpnSlots;
                // Keep the user's apps; classification + rule-splitting happen below.
                if (old.Apps != null && old.Apps.Count > 0)
                {
                    defaults.Apps = old.Apps;
                }
            }
            // v10→v11: the "forced variant" pin + "force connect" toggle are gone. Translate
            // any pin into the new allow-list (only that variant allowed), then clear them.
            TranslateForcedToAllowed(defaults.VpnSlots, defaults.Apps);
            // v11→v12: fold each old single Program/Link into a Rule with one Program/Site.
            NormalizeRules(defaults.Apps);
            return defaults;
        }

        // Convert the obsolete ForcedVpnSlotId pin into DisabledVpnSlotIds: a pin to slot X
        // becomes "every other slot disabled" (so auto-pick only ever uses X), matching the
        // old behaviour. Then clear ForcedVpnSlotId / ForceConnect on every entry.
        private static void TranslateForcedToAllowed(List<VpnSlot> slots, List<AppEntry> apps)
        {
            if (apps == null) return;
            var ids = (slots ?? new List<VpnSlot>()).Where(s => s != null).Select(s => s.Id).ToList();
            foreach (var a in apps)
            {
                if (a == null) continue;
                if (!string.IsNullOrEmpty(a.ForcedVpnSlotId) && ids.Contains(a.ForcedVpnSlotId)
                    && (a.DisabledVpnSlotIds == null || a.DisabledVpnSlotIds.Count == 0))
                {
                    a.DisabledVpnSlotIds = ids.Where(id => id != a.ForcedVpnSlotId).ToList();
                }
                a.ForcedVpnSlotId = "";
                a.ForceConnect = false;
            }
        }

        // Ensure every entry is a proper Rule: if it has no Programs/Sites yet, fold its old
        // single-item fields (Path/Aumid/Arguments for a program, or a URL + cached Clash rule
        // list for a link) into one Program or one Site (flagged Launch=true so the tile keeps
        // launching it). Idempotent — a no-op once an entry already has rule contents. Returns
        // true if anything changed (caller should save).
        private static bool NormalizeRules(List<AppEntry> apps)
        {
            if (apps == null) return false;
            bool changed = false;
            foreach (var a in apps)
            {
                if (a == null) continue;
                if (a.Programs == null) a.Programs = new List<RuleProgram>();
                if (a.Sites == null) a.Sites = new List<RuleSite>();
                if (a.IsAllTraffic) continue;   // empty by design, nothing to fold

                // Already a rule (has contents) — just clear any stale legacy fields.
                if (a.Programs.Count > 0 || a.Sites.Count > 0)
                {
                    if (ClearLegacy(a)) changed = true;
                    continue;
                }

                // A URL in Path, an explicit Link kind, or a "browser exe + site in args"
                // pattern → one Site. Otherwise → one Program.
                var url = a.LegacyIsUrl ? a.Path.Trim()
                        : (a.Kind == AppEntryKind.Link ? a.Path.Trim() : ExtractUrl(a.Arguments));
                if (!string.IsNullOrEmpty(url))
                {
                    a.Sites.Add(new RuleSite
                    {
                        Url = url,
                        ClashRuleUrl = a.ClashRuleUrl ?? "",
                        RuleItems = a.RuleItems ?? new List<string>(),
                        RuleFetchedUtc = a.RuleFetchedUtc ?? "",
                        Launch = true,
                    });
                    if (string.IsNullOrWhiteSpace(a.CheckUrl)) a.CheckUrl = url;
                }
                else if (!string.IsNullOrEmpty(a.Path) || !string.IsNullOrEmpty(a.Aumid))
                {
                    a.Programs.Add(new RuleProgram
                    {
                        Name = a.Name,
                        Path = a.Path ?? "",
                        Arguments = a.Arguments ?? "",
                        Aumid = a.Aumid ?? "",
                        Launch = true,
                    });
                }
                ClearLegacy(a);
                changed = true;
            }
            return changed;
        }

        // The built-in «Весь трафик» rule: exactly one, first in the list, with the fields that
        // make it what it is pinned (a VPN rule with no contents and its own icon). Everything
        // the user may change — variants, their order, kill-switch, on/off — is left alone.
        // Returns true if anything changed (caller should save).
        public static bool EnsureAllTraffic(List<AppEntry> apps)
        {
            if (apps == null) return false;
            bool changed = false;

            var all = apps.Where(a => a != null && a.IsAllTraffic).ToList();
            var keep = all.FirstOrDefault();
            foreach (var extra in all.Skip(1)) { apps.Remove(extra); changed = true; }

            if (keep == null)
            {
                keep = new AppEntry
                {
                    Id = AppEntry.AllTrafficId,
                    Name = "Всё остальное",
                    IsAllTraffic = true,
                    RequiresVpn = true,
                    // Off by default: blocking the WHOLE machine whenever the VPN is down is a
                    // decision the user has to make on purpose, not one they discover.
                    KillSwitch = false,
                    SkipCheck = true,
                    AutoConnect = false,
                };
                apps.Insert(0, keep);
                changed = true;
            }
            else if (apps.IndexOf(keep) != 0)
            {
                apps.Remove(keep);
                apps.Insert(0, keep);
                changed = true;
            }

            if (!keep.RequiresVpn) { keep.RequiresVpn = true; changed = true; }
            if (keep.AutoIcon) { keep.AutoIcon = false; changed = true; }
            if (keep.IconPath != AppEntry.AllTrafficIcon) { keep.IconPath = AppEntry.AllTrafficIcon; changed = true; }
            if (keep.Programs == null || keep.Programs.Count > 0) { keep.Programs = new List<RuleProgram>(); changed = true; }
            if (keep.Sites == null || keep.Sites.Count > 0) { keep.Sites = new List<RuleSite>(); changed = true; }
            if (!string.IsNullOrEmpty(keep.CheckUrl)) { keep.CheckUrl = ""; changed = true; }
            return changed;
        }

        // Wipe the obsolete single-item fields once their content lives in Programs/Sites.
        private static bool ClearLegacy(AppEntry a)
        {
            bool changed = false;
            if (!string.IsNullOrEmpty(a.Path)) { a.Path = ""; changed = true; }
            if (!string.IsNullOrEmpty(a.Arguments)) { a.Arguments = ""; changed = true; }
            if (!string.IsNullOrEmpty(a.Aumid)) { a.Aumid = ""; changed = true; }
            if (!string.IsNullOrEmpty(a.ClashRuleUrl)) { a.ClashRuleUrl = ""; changed = true; }
            if (a.RuleItems != null && a.RuleItems.Count > 0) { a.RuleItems = new List<string>(); changed = true; }
            if (!string.IsNullOrEmpty(a.RuleFetchedUtc)) { a.RuleFetchedUtc = ""; changed = true; }
            if (a.Kind != AppEntryKind.Program) { a.Kind = AppEntryKind.Program; changed = true; }
            return changed;
        }

        private static string ExtractUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            foreach (var tok in text.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries))
                if (tok.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    tok.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return tok;
            return "";
        }

        private static void EnsureDirs()
        {
            Directory.CreateDirectory(DataUserDir);
            // ConfigDir (%LocalAppData%) and IconsDir are created lazily, only when something is
            // actually written there (SetConfigPath / CopyIconToStore) — no empty folders.
        }

        // One-time migration of user settings from the legacy %LocalAppData%\MihomoLauncher
        // location into DataUser\ beside the exe. Runs only when DataUser has no config yet
        // and the legacy one exists (i.e. the first launch after upgrading to the DataUser
        // layout). Copies (never deletes) so a rollback to an older build still works.
        private static void MigrateLegacyUserDataIfNeeded()
        {
            try
            {
                // Don't migrate if the user has pointed the config elsewhere, or it's already here.
                if (File.Exists(ConfigPointerPath)) return;
                if (File.Exists(DefaultConfigPath)) return;
                if (!File.Exists(LegacyConfigPath)) return;

                Directory.CreateDirectory(DataUserDir);
                File.Copy(LegacyConfigPath, DefaultConfigPath, overwrite: false);

                // Bring the custom-icon store over too, if any.
                if (Directory.Exists(LegacyIconsDir))
                {
                    Directory.CreateDirectory(IconsDir);
                    foreach (var f in Directory.GetFiles(LegacyIconsDir))
                    {
                        var dest = Path.Combine(IconsDir, Path.GetFileName(f));
                        if (!File.Exists(dest)) File.Copy(f, dest, overwrite: false);
                    }
                }
                try { Log.Info("Перенёс настройки из %LocalAppData%\\MihomoLauncher в DataUser."); } catch { }
            }
            catch (Exception ex) { try { Log.Error("MigrateLegacyUserData", ex); } catch { } }
        }

        // Fresh install starts EMPTY — no bundled tiles (the old defaults were the developer's
        // personal app set with machine-specific exe paths). The user adds their own programs /
        // sites via Settings. Only the mihomo engine target is pre-filled (writable default).
        private static AppConfig CreateDefaults()
        {
            return new AppConfig
            {
                MihomoExePath = DefaultMihomoExeTarget,
                MihomoWorkDir = "",   // empty => DefaultMihomoWorkDir (~/.config/mihomo)
                VpnSlots = new List<VpnSlot>(),
                Apps = new List<AppEntry>(),
            };
        }
    }
}
