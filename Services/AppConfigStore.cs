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

        // Light / dark (2026-11-24): "" = as Windows (the default, follows Windows live),
        // "light" / "dark" once picked from the title-bar theme button. Services/Theme.
        public string UiTheme { get; set; } = "";

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

        // IGNORED since 2026-11-16 (their Settings boxes went): the card icon shows whenever the
        // machine has two cards or more, the speed always. Kept so old config.xml files load as is.
        public bool ShowNicPlate { get; set; } = true;
        public bool ShowNicPlateByUser { get; set; } = false;
        public bool ShowTileSpeed { get; set; } = true;

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

        // Main-window tabs (2026-11-16), in order; the first is always «Основная» (TileTabs.MainId).
        // Apps is kept grouped by them, so the rule order is «Основная» first, then the next tab…
        public List<TileTab> Tabs { get; set; } = new List<TileTab>();
        // The tab last open — reopened at start, and where a rule added in «Правила» lands.
        public string CurrentTabId { get; set; } = "";
        // Tiles per row on the main window (2026-11-17): the user drags the window's edge and it
        // snaps to whole columns and rows; 5 until then (2026-11-18: 6 → 5 — the mode buttons span
        // the grid edge to edge, fits 1366 px at 125%). Narrowed to what the top bar needs.
        public int TileColumns { get; set; } = 5;

        // «Кто и куда» sort (2026-11-25), programs and addresses each: the key's name
        // (MainWindow.WhoSort), «!» after it = the other way round. "" = by time, newest first.
        public string WhoSortProgs { get; set; } = "";
        public string WhoSortAddrs { get; set; } = "";

        // Tiles pulled out of the window (2026-11-25): each in its own always-on-top window, where
        // it was left; reopened at start. Closed by its ✕ or by dropping it back on the window.
        public List<DetachedTile> DetachedTiles { get; set; } = new List<DetachedTile>();
        // How many times the «a tile can be pulled out» hint was shown (the first 3 grabs).
        public int TileDetachHints { get; set; }

        // Access password (see AccessLock): salted PBKDF2 hash, its salt, and the hashed place
        // (PC + Windows user + launcher copy) the data was locked in. All empty = no password.
        public string LockHash { get; set; } = "";
        public string LockSalt { get; set; } = "";
        public string LockPlace { get; set; } = "";
    }

    // A tile in a window of its own: the rule's Id and where the window's top-left is (screen DIP).
    public class DetachedTile
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
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

        // The install folder was moved or renamed (2026-11-19: Work\MihomoLauncher → Work\Mihomo
        // Launcher): config.xml keeps ABSOLUTE paths into the old folder's DataUser\ / Data\ —
        // the engine, its work dir, tile icons — and they point at nothing. The engine looked
        // «не установлен» and the app offered to download it although it sat in DataUser\.
        // Such a path is moved onto this install when the same file / folder exists here.
        private static bool RebaseMovedInstall(AppConfig cfg)
        {
            // Whatever the folder is called now and wherever it sits («хоть в папке Вася»): what
            // decides is that a path leads into ANOTHER install's Data\ / DataUser\ — gone (moved,
            // renamed) or still there (a copy: it must use its own files, not the original's).
            bool changed = false;
            var exe = RebaseToThisInstall(cfg.MihomoExePath, mustExist: true);
            // The engine path leads nowhere at all (another PC's %LocalAppData%, a deleted
            // folder), but this install has one in DataUser\ (the full archive ships it).
            if ((string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) && File.Exists(DefaultMihomoExeTarget))
                exe = DefaultMihomoExeTarget;
            if (!string.Equals(exe, cfg.MihomoExePath, StringComparison.Ordinal)) { cfg.MihomoExePath = exe; changed = true; }

            // The work dir is created on demand, so it is moved here whether it exists yet or not.
            var wd = RebaseToThisInstall(cfg.MihomoWorkDir, mustExist: false);
            if (!string.Equals(wd, cfg.MihomoWorkDir, StringComparison.Ordinal))
            {
                // The default one is kept empty (= DefaultMihomoWorkDir), as on a fresh install.
                cfg.MihomoWorkDir = string.Equals(wd.TrimEnd('\\'), DefaultMihomoWorkDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : wd;
                changed = true;
            }

            foreach (var a in cfg.Apps ?? new List<AppEntry>())
            {
                var icon = RebaseToThisInstall(a.IconPath, mustExist: true);
                if (!string.Equals(icon, a.IconPath, StringComparison.Ordinal)) { a.IconPath = icon; changed = true; }
            }
            if (changed) try { Log.Info("Папка программы перенесена: пути движка и иконок переведены на " + VersionInfo.InstallRoot); } catch { }
            return changed;
        }

        // <another folder>\DataUser\x or \Data\x → <this install>\DataUser\x (when it exists here,
        // if mustExist).
        private static string RebaseToThisInstall(string path, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var root = VersionInfo.InstallRoot.TrimEnd('\\');
                foreach (var seg in new[] { "\\DataUser\\", "\\Data\\" })
                {
                    int i = path.LastIndexOf(seg, StringComparison.OrdinalIgnoreCase);
                    if (i <= 0) continue;
                    if (string.Equals(path.Substring(0, i).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)) return path;
                    var here = root + path.Substring(i);
                    return !mustExist || File.Exists(here) || Directory.Exists(here) ? here : path;
                }
            }
            catch { }
            return path;
        }

        // A full, independent copy — through the same serializer that writes config.xml, so it
        // carries every field that is saved (a field-by-field copy silently lost new ones).
        public static AppConfig Clone(AppConfig c)
        {
            if (c == null) return null;
            var ser = new XmlSerializer(typeof(AppConfig));
            using (var w = new StringWriter())
            {
                ser.Serialize(w, c);
                using (var r = new StringReader(w.ToString()))
                    return (AppConfig)ser.Deserialize(r);
            }
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
                    NormalizeCheckAddresses(migrated.Apps);
                    NormalizeSlots(migrated.VpnSlots);
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
                if (RebaseMovedInstall(loaded)) dirty = true;
                if (NormalizeRules(loaded.Apps)) dirty = true;
                if (EnsureAllTraffic(loaded.Apps)) dirty = true;
                if (NormalizeDirectWithTicks(loaded)) dirty = true;
                // «Блокировать» switched off (grey, blocked nothing) from before 2026-11-24, when
                // block mode lost its on/off: it would start blocking now — it was not, so it goes.
                foreach (var a in loaded.Apps.Where(a => a != null && a.Block && !a.AutoConnect))
                {
                    a.Block = false;
                    dirty = true;
                }
                // After NormalizeRules: the check address is judged on the entry as the old connect
                // saw it (legacy fields already folded in).
                if (NormalizeCheckAddresses(loaded.Apps)) dirty = true;
                if (NormalizeSlots(loaded.VpnSlots)) dirty = true;
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
            // Every write, not only at load (2026-11-16): a «без VPN» tile given a VPN in its card
            // was saved as «без VPN» with Turkey ticked — the plate said «без VPN» until a restart.
            // Ticked VPNs = a VPN tile (switched off: first VPN in grey with ▷), whatever path set them.
            try { NormalizeDirectWithTicks(config); } catch { }
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
                if (a.Folders == null) a.Folders = new List<RuleFolder>();
                if (a.AutoIgnored == null) a.AutoIgnored = new List<string>();
                if (a.IsAllTraffic) continue;   // built in, nothing to fold

                // Already a rule (has contents) — just clear any stale legacy fields.
                if (a.Programs.Count > 0 || a.Sites.Count > 0 || a.Folders.Count > 0)
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
        // VPNs the person actually ticked in a tile's card (2026-11-16): something is allowed AND the
        // choice was made by hand (an order, or some unticked). A tile whose lists were never
        // touched (both empty — old configs, mom's) counts as «nothing ticked».
        public static bool HasTickedVpn(AppEntry e, IEnumerable<VpnSlot> slots)
        {
            if (e == null) return false;
            var order = e.VpnSlotOrder ?? new List<string>();
            var off = new HashSet<string>(e.DisabledVpnSlotIds ?? new List<string>());
            if (order.Count == 0 && off.Count == 0) return false;
            return (slots ?? Enumerable.Empty<VpnSlot>()).Any(s => s != null && !off.Contains(s.Id));
        }

        // «Напрямую» on a tile with VPNs ticked keeps it a VPN tile, switched off (2026-11-16: «если
        // впн отмечены, не пиши без VPN — укажи первый»): the plate then shows that first VPN in grey
        // with its ▷. Configs saved before that carry RequiresVpn=false with ticks — put right here;
        // off and without a kill-switch it goes direct exactly as before.
        public static bool NormalizeDirectWithTicks(AppConfig cfg)
        {
            bool changed = false;
            foreach (var a in (cfg?.Apps ?? new List<AppEntry>()).Where(a => a != null && !a.RequiresVpn && !a.Block))
            {
                if (!HasTickedVpn(a, cfg.VpnSlots)) continue;
                a.RequiresVpn = true;
                if (a.AutoConnect) a.AutoConnect = false;
                changed = true;
            }
            return changed;
        }

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

            // RequiresVpn is NOT pinned any more: «Напрямую» on its plate (2026-11-16) sets it false,
            // and pinning it back at every start undid that choice («выбор не сохраняется»).
            if (keep.AutoIcon) { keep.AutoIcon = false; changed = true; }
            if (keep.IconPath != AppEntry.AllTrafficIcon) { keep.IconPath = AppEntry.AllTrafficIcon; changed = true; }
            if (keep.Programs == null || keep.Programs.Count > 0) { keep.Programs = new List<RuleProgram>(); changed = true; }
            if (keep.Sites == null || keep.Sites.Count > 0) { keep.Sites = new List<RuleSite>(); changed = true; }
            if (keep.Folders == null || keep.Folders.Count > 0) { keep.Folders = new List<RuleFolder>(); changed = true; }
            if (!string.IsNullOrEmpty(keep.CheckUrl)) { keep.CheckUrl = ""; changed = true; }
            // «Глобально» (2026-11-12, removed the same week): drop it from a config that has it.
            if (apps.RemoveAll(a => a != null && a.IsGlobal) > 0) changed = true;
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

        // «Не проверять» is gone (2026-11-23): the check address is the switch — filled = checked,
        // empty = not checked. Until now the field filled itself (a known service guessed from the
        // rule's name, a template's own site) and almost always sat under a ticked «Не проверять».
        // Brought over so that no rule changes what it does:
        //  - not checked, address filled → the address moves to HomeUrl. Routed exactly as before
        //    and still what a template's tile opens (AppEntry.OwnUrl), only no longer in the field,
        //    so the rule does not start being checked;
        //  - checked, field empty → the address the old connect really probed goes in
        //    (OldProbeUrl); there is none → it never was checked: SkipCheck = true, as it behaved.
        // Idempotent (once SkipCheck == "CheckUrl is empty" nothing changes), so it runs on every
        // load: a config written by an older version is brought over the same way.
        // True if anything changed (caller saves).
        internal static bool NormalizeCheckAddresses(List<AppEntry> apps)
        {
            if (apps == null) return false;
            bool changed = false;
            foreach (var a in apps)
            {
                if (a == null) continue;
                if (a.HomeUrl == null) a.HomeUrl = "";
                var check = (a.CheckUrl ?? "").Trim();
                if (a.SkipCheck && check.Length > 0)
                {
                    a.HomeUrl = check;
                    a.CheckUrl = "";
                    changed = true;
                    try { Log.Info("Правило «" + a.Name + "»: адрес " + check + " не проверялся — убран из поля проверки (маршрут и плитка как были)."); } catch { }
                }
                else if (!a.SkipCheck && check.Length == 0)
                {
                    var probed = OldProbeUrl(a);
                    if (probed.Length > 0) a.CheckUrl = probed;
                    else a.SkipCheck = true;   // nothing to probe: the old connect let it through unchecked
                    changed = true;
                }
            }
            return changed;
        }

        // What the connect before 2026-11-23 probed (MainWindow.ResolveCheckUrl of that time): the
        // check address, else a URL in the legacy Path, else one in the legacy launch arguments.
        internal static string OldProbeUrl(AppEntry a)
        {
            if (a == null) return "";
            if (!string.IsNullOrWhiteSpace(a.CheckUrl)) return a.CheckUrl.Trim();
            var p = (a.Path ?? "").Trim();
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return p;
            return ExtractUrl(a.Arguments);
        }

        // Every VPN configuration is in use since 2026-11-23 (the «use when cycling» box is gone):
        // one switched off in an older version comes back on. A rule that must not use it says so
        // with its own numbers (DisabledVpnSlotIds), which a switched-off one kept untouched.
        internal static bool NormalizeSlots(List<VpnSlot> slots)
        {
            bool changed = false;
            foreach (var s in slots ?? new List<VpnSlot>())
            {
                if (s == null || s.Enabled) continue;
                s.Enabled = true;
                changed = true;
                try { Log.Info("VPN-конфигурация «" + s.Name + "» была выключена — теперь используются все."); } catch { }
            }
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
