using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MaxMind.GeoIP2;

namespace MihomoLauncher.Services
{
    public class GeoInfo
    {
        public string Country { get; set; } = "";
        public string City { get; set; } = "";

        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Country) && !string.IsNullOrEmpty(City))
                    return Country + ", " + City;
                if (!string.IsNullOrEmpty(Country)) return Country;
                if (!string.IsNullOrEmpty(City)) return City;
                return "";
            }
        }
    }

    public static class GeoIpService
    {
        private static readonly object _lock = new object();
        private static DatabaseReader _reader;
        private static bool _searched;
        private static string _dbPath;

        // host/ip -> resolved geo (cached so tiles don't re-resolve every rebuild).
        private static readonly Dictionary<string, GeoInfo> _hostCache =
            new Dictionary<string, GeoInfo>(StringComparer.OrdinalIgnoreCase);

        // Geo data lives in DataUser\maxmind (survives updates; an update wipes Data\). The DB
        // is GeoLite2-City.mmdb; its installed date (yyyyMMdd) is kept in updates.json
        // (VersionInfo.GeoDate), not a separate geolite2_data.txt.
        public static string MaxmindDir => Path.Combine(VersionInfo.DataUserDir, "maxmind");
        public static string DbPath => Path.Combine(MaxmindDir, "GeoLite2-City.mmdb");

        // Legacy location (before the DataUser split) — read as a fallback so an existing DB
        // is still found until the next refresh writes it to the canonical DataUser path.
        private static string LegacyDbPath =>
            Path.Combine(AppContext.BaseDirectory, "Data", "maxmind", "GeoLite2-City.mmdb");

        // Installed DB date = the date we recorded when we downloaded it (updates.json), i.e. the
        // very same marker the server publishes in geolite2_data.txt. Those two must be compared
        // like for like: the mirror's release date and the .mmdb's own build date differ by a few
        // days (release 31.07 carries a DB built 28.07), so comparing the marker against the
        // metadata made every check see "newer online" and re-download 66 MB forever.
        // The .mmdb build date stays as the fallback — it's self-describing, so a DB shipped in
        // the release (no recorded marker yet) still reports a sane date.
        public static DateTime? LocalDate()
        {
            var recorded = (DateTime?)null;
            try { recorded = ParseDate(VersionInfo.GeoDate); } catch { }

            // A release ships the DB together with the marker file it came with, while
            // updates.json is USER data an install never overwrites. So a freshly installed
            // build would keep claiming the date of whatever the user had before — and ask to
            // update a DB that is actually newer. The shipped marker wins when it is newer,
            // and is recorded so the comparison stays like-for-like from then on.
            var shipped = ShippedMarkerDate();
            if (shipped != null && (recorded == null || shipped.Value > recorded.Value))
            {
                try { VersionInfo.UpdateLocalGeoDate(shipped.Value.ToString("yyyyMMdd")); } catch { }
                return shipped;
            }
            if (recorded != null) return recorded;

            try
            {
                var r = GetReader();
                if (r != null) return r.Metadata.BuildDate.Date;
            }
            catch { }
            return null;
        }

        // geolite2_data.txt next to the DB — the publisher's own release marker, copied into
        // the build by layout-release.ps1 and rewritten whenever the app downloads a new DB.
        public static string MarkerPath => Path.Combine(MaxmindDir, "geolite2_data.txt");

        private static DateTime? ShippedMarkerDate()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return null;
                return ParseDate(File.ReadAllText(MarkerPath));
            }
            catch { return null; }
        }

        public static DateTime? ParseDate(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length < 8) return null;
            s = s.Substring(0, 8);
            return DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        // Drop the open DB + caches so the next lookup reopens the (freshly updated) file.
        public static void Reset()
        {
            lock (_lock)
            {
                try { _reader?.Dispose(); } catch { }
                _reader = null;
                _searched = false;
                _dbPath = null;
                _hostCache.Clear();
            }
        }

        // Locate the DB: Data\maxmind (canonical), then a couple of legacy fallbacks.
        private static string FindDatabase()
        {
            try
            {
                if (File.Exists(DbPath)) return DbPath;

                // Legacy: Data\maxmind (pre-DataUser layout).
                if (File.Exists(LegacyDbPath)) return LegacyDbPath;

                var alt = Path.Combine(AppContext.BaseDirectory, "maxmind", "GeoLite2-City.mmdb");
                if (File.Exists(alt)) return alt;

                // Legacy layout: <base>\GeoLite2-City\[GeoLite2-City_*]\GeoLite2-City.mmdb
                var root = Path.Combine(AppContext.BaseDirectory, "GeoLite2-City");
                if (Directory.Exists(root))
                {
                    var direct = Path.Combine(root, "GeoLite2-City.mmdb");
                    if (File.Exists(direct)) return direct;
                    var sub = Directory.GetDirectories(root)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (sub != null)
                    {
                        var nested = Path.Combine(sub, "GeoLite2-City.mmdb");
                        if (File.Exists(nested)) return nested;
                    }
                }
            }
            catch { }
            return null;
        }

        private static DatabaseReader GetReader()
        {
            if (_reader != null) return _reader;
            lock (_lock)
            {
                if (_reader != null) return _reader;
                if (_searched && _reader == null) return null;
                _searched = true;

                _dbPath = FindDatabase();
                if (_dbPath == null) return null;
                try { _reader = new DatabaseReader(_dbPath); }
                catch { _reader = null; }
                return _reader;
            }
        }

        public static GeoInfo Lookup(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return null;
            var reader = GetReader();
            if (reader == null) return null;
            try
            {
                var resp = reader.City(ip);
                var lang = Loc.Code;   // follow the UI language ("ru"/"en")
                return new GeoInfo
                {
                    Country = PickName(resp?.Country?.Names, lang, resp?.Country?.Name),
                    City = PickName(resp?.City?.Names, lang, resp?.City?.Name),
                };
            }
            catch { return null; }
        }

        // Localized place name: the current language, else English, else the DB default.
        private static string PickName(IReadOnlyDictionary<string, string> names, string lang, string fallback)
        {
            if (names != null)
            {
                if (names.TryGetValue(lang, out var n) && !string.IsNullOrEmpty(n)) return n;
                if (names.TryGetValue("en", out var en) && !string.IsNullOrEmpty(en)) return en;
            }
            return fallback ?? "";
        }

        // Drop the per-host cache (e.g. after a language change) so names re-resolve.
        public static void ClearCache()
        {
            lock (_lock) { _hostCache.Clear(); }
        }

        // Resolve a VPN server host (hostname or IP) to its country/city. Cached per host.
        public static async Task<GeoInfo> LookupHostAsync(string hostOrIp)
        {
            if (string.IsNullOrWhiteSpace(hostOrIp)) return null;
            hostOrIp = hostOrIp.Trim();
            lock (_lock)
            {
                if (_hostCache.TryGetValue(hostOrIp, out var cached)) return cached;
            }

            GeoInfo info = null;
            try
            {
                string ip;
                if (IPAddress.TryParse(hostOrIp, out var parsed))
                {
                    ip = parsed.ToString();
                }
                else
                {
                    var addrs = await Dns.GetHostAddressesAsync(hostOrIp).ConfigureAwait(false);
                    var pick = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                               ?? addrs.FirstOrDefault();
                    ip = pick?.ToString();
                }
                if (!string.IsNullOrEmpty(ip)) info = Lookup(ip);
            }
            catch { info = null; }

            lock (_lock) { _hostCache[hostOrIp] = info; }
            return info;
        }
    }
}
