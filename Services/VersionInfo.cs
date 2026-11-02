using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Reads the LOCAL app manifest and exposes the installed versions/dates. There are two
    // stores behind this now:
    //   manifest.json  (Data\)       — appVersion only. Wiped & replaced on update. The engine
    //                                  version is never shipped: it is read from the exe itself.
    //   updates.json   (DataUser\)   — the WRITABLE state: installed geo DB date, template-catalog
    //                                  date, the actually-installed engine version, and the four
    //                                  refresh periods. Lives in DataUser so it SURVIVES an update.
    //                                  See UpdateStateStore. (Replaces the old manifest_geo.json,
    //                                  which is now only a first-run seed.)
    public static class VersionInfo
    {
        private const string DefaultAppVersion = "1.0.0";

        private static UpdateManifest _local;

        // Folder the running MihomoLauncher.exe lives in (the install root). The launcher
        // may sit anywhere, so always derive this from the actual exe location.
        public static string InstallRoot
        {
            get
            {
                try { return AppContext.BaseDirectory.TrimEnd('\\'); }
                catch { return Environment.CurrentDirectory; }
            }
        }

        // Per-user, update-surviving data folder beside the exe. Holds the config, geo DB,
        // service list and updates.json. An update wipes Data\ + MihomoLauncher.exe but
        // never touches DataUser\.
        public static string DataUserDir => Path.Combine(InstallRoot, "DataUser");

        // Local app manifest path. Release puts it in Data\; Debug runs flat next to the exe.
        public static string LocalManifestPath
        {
            get
            {
                var inData = Path.Combine(InstallRoot, "Data", "manifest.json");
                if (File.Exists(inData)) return inData;
                return Path.Combine(InstallRoot, "manifest.json");
            }
        }

        public static UpdateManifest Local
        {
            get
            {
                if (_local != null) return _local;
                _local = ReadManifest(LocalManifestPath) ?? new UpdateManifest();
                if (string.IsNullOrWhiteSpace(_local.AppVersion)) _local.AppVersion = DefaultAppVersion;
                return _local;
            }
        }

        // The installed launcher version is authoritative from the shipped manifest.json.
        public static string AppVersion => Local.AppVersion;

        // Path of the engine exe, so the version can be read from the binary itself. Set once
        // the config is loaded (and again whenever it changes); empty until then.
        public static string EngineExePath { get; set; } = "";

        // The installed engine version. ASK THE BINARY first — it's the only answer that can't
        // go stale. updates.json is just bookkeeping (last value the binary gave), and any change
        // to the exe that doesn't run through our updater (a copied folder, a hand-placed binary)
        // leaves it lying. No exe and no record => "" (engine not installed), never a made-up
        // number — manifest.json doesn't carry an engine version at all.
        public static string MihomoVersion
        {
            get
            {
                var probed = MihomoBinary.VersionOf(EngineExePath);
                if (probed.Length > 0) return probed;
                return (UpdateStateStore.State.EngineVersion ?? "").Trim();
            }
        }

        // Bring updates.json back in line with the binary on disk, so the Updates tab and the
        // next check agree with reality. Returns true if the record was wrong and got fixed.
        public static bool SyncEngineVersionFromBinary()
        {
            var probed = MihomoBinary.VersionOf(EngineExePath);
            if (probed.Length == 0) return false;
            var stored = (UpdateStateStore.State.EngineVersion ?? "").Trim();
            if (string.Equals(stored, probed, StringComparison.OrdinalIgnoreCase)) return false;
            UpdateLocalMihomoVersion(probed);
            try { Log.Info("Версия движка по факту " + probed + " (в updates.json было \"" + stored + "\") — запись исправлена."); } catch { }
            return true;
        }

        // Installed GeoLite2 DB date (yyyyMMdd) from updates.json, or "" if not set.
        public static string GeoDate => (UpdateStateStore.State.GeoDataDate ?? "").Trim();

        // How often (days) to check for a newer geo DB. From updates.json; default via the store.
        public static int GeoPeriodDays => UpdateStateStore.GeoPeriodDays;

        // Installed template-catalog date (dd.MM.yyyy) from updates.json — mirrors the first
        // line of DataUser\rules.
        public static string CatalogDate => (UpdateStateStore.State.TemplatesDate ?? "").Trim();

        // How often (days) to check for a newer template catalog. From updates.json; store default.
        public static int CatalogPeriodDays => UpdateStateStore.TemplatesPeriodDays;

        // Title-bar caption: "Mihomo Launcher 1.0.0  /  Mihomo 1.19.26".
        public static string TitleCaption
        {
            get
            {
                var engine = MihomoVersion;
                return "Mihomo Launcher " + AppVersion + (engine.Length > 0 ? "  /  Mihomo " + engine : "");
            }
        }

        public static UpdateManifest ReadManifest(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return ParseManifest(File.ReadAllText(path));
            }
            catch { return null; }
        }

        public static UpdateManifest ParseManifest(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                var ser = new DataContractJsonSerializer(typeof(UpdateManifest));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (UpdateManifest)ser.ReadObject(ms);
                }
            }
            catch { return null; }
        }

        // Record the installed mihomo version after the engine is updated in place (updates.json).
        public static void UpdateLocalMihomoVersion(string newVersion)
        {
            UpdateStateStore.State.EngineVersion = (newVersion ?? "").Trim();
            UpdateStateStore.Save();
        }

        // Record the geo DB date (yyyyMMdd) after the GeoLite2 DB is updated in place (updates.json).
        public static void UpdateLocalGeoDate(string newDate)
        {
            UpdateStateStore.State.GeoDataDate = (newDate ?? "").Trim();
            UpdateStateStore.Save();
        }

        // Record the template-catalog date (dd.MM.yyyy) after DataUser\rules is refreshed (updates.json).
        public static void UpdateLocalCatalogDate(string newDate)
        {
            UpdateStateStore.State.TemplatesDate = (newDate ?? "").Trim();
            UpdateStateStore.Save();
        }

        // Compare dotted versions ("1.19.26"). Returns true if `remote` is strictly newer.
        public static bool IsNewer(string remote, string local)
        {
            var r = Parse(remote);
            var l = Parse(local);
            if (r == null) return false;       // no/garbage remote => never "newer"
            if (l == null) return true;
            return r > l;
        }

        private static Version Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Tolerate a leading 'v' and trailing junk; keep only digits and dots.
            var t = s.Trim().TrimStart('v', 'V');
            var sb = new StringBuilder();
            foreach (var c in t)
            {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else break;
            }
            return Version.TryParse(sb.ToString(), out var v) ? v : null;
        }
    }
}
