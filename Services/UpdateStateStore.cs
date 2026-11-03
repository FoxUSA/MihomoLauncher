using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Loads/saves the live update-tracking state (DataUser\updates.json). This is the single
    // writable home for installed geo/engine dates+version and the four per-item refresh periods
    // (geo / templates / engine / app). See UpdateState for the shape.
    //
    // On first run (no updates.json yet) it seeds itself from the legacy manifest_geo.json (geo
    // date + catalog date + their periods) and from manifest.json (the shipped mihomo version),
    // so an upgrade from an older build keeps its installed markers.
    public static class UpdateStateStore
    {
        // Built-in defaults (days) used when a period field is 0/absent.
        public const int DefaultGeoPeriodDays = 180;
        // Daily: api.byfox.dev asks consumers to look at its manifest once a day (a 1–2 KB file;
        // the catalog itself is downloaded only when its date moves).
        public const int DefaultTemplatesPeriodDays = 1;
        // The weekly default before 2026-11-03 — a stored 7 is that old default, not a choice.
        private const int LegacyTemplatesPeriodDays = 7;
        public const int DefaultEnginePeriodDays = 7;
        public const int DefaultAppPeriodDays = 7;

        private static readonly object _lock = new object();
        private static UpdateState _state;

        public static string StatePath => Path.Combine(VersionInfo.DataUserDir, "updates.json");

        public static UpdateState State
        {
            get
            {
                if (_state != null) return _state;
                lock (_lock)
                {
                    if (_state != null) return _state;
                    _state = Read(StatePath);
                    if (_state == null)
                    {
                        _state = new UpdateState();
                        SeedFromLegacy(_state);
                        Save();   // materialize updates.json so the seed sticks
                    }
                    return _state;
                }
            }
        }

        // Effective, defaulted period accessors.
        public static int GeoPeriodDays => State.GeoPeriodDays > 0 ? State.GeoPeriodDays : DefaultGeoPeriodDays;
        public static int TemplatesPeriodDays =>
            State.TemplatesPeriodDays > 0 && State.TemplatesPeriodDays != LegacyTemplatesPeriodDays
                ? State.TemplatesPeriodDays : DefaultTemplatesPeriodDays;
        public static int EnginePeriodDays => State.EnginePeriodDays > 0 ? State.EnginePeriodDays : DefaultEnginePeriodDays;
        public static int AppPeriodDays => State.AppPeriodDays > 0 ? State.AppPeriodDays : DefaultAppPeriodDays;

        // "yyyy-MM-dd" for today (UTC) — the format we stamp lastCheck fields with.
        public static string TodayStamp() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Parse a "yyyy-MM-dd" (or longer) lastCheck stamp back to a date; null if absent/garbage.
        public static DateTime? ParseStamp(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            if (t.Length >= 10) t = t.Substring(0, 10);
            return DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        // True when the last check is missing or older than `periodDays` — i.e. it's time to
        // check again. A periodDays <= 0 means "always due".
        public static bool DueForCheck(string lastCheck, int periodDays)
        {
            if (periodDays <= 0) return true;
            var last = ParseStamp(lastCheck);
            if (last == null) return true;
            return (DateTime.UtcNow - last.Value).TotalDays >= periodDays;
        }

        public static void MarkGeoChecked() { State.GeoLastCheck = TodayStamp(); Save(); }
        public static void MarkTemplatesChecked() { State.TemplatesLastCheck = TodayStamp(); Save(); }
        public static void MarkEngineChecked() { State.EngineLastCheck = TodayStamp(); Save(); }
        public static void MarkAppChecked() { State.AppLastCheck = TodayStamp(); Save(); }

        public static void Save()
        {
            lock (_lock)
            {
                if (_state == null) return;
                try
                {
                    var ser = new DataContractJsonSerializer(typeof(UpdateState));
                    Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                    using (var ms = new MemoryStream())
                    {
                        ser.WriteObject(ms, _state);
                        File.WriteAllBytes(StatePath, ms.ToArray());
                    }
                }
                catch { }
            }
        }

        private static UpdateState Read(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var ser = new DataContractJsonSerializer(typeof(UpdateState));
                using (var fs = File.OpenRead(path))
                    return (UpdateState)ser.ReadObject(fs);
            }
            catch { return null; }
        }

        // First-run seed: pull the installed markers from the legacy manifest_geo.json (geo DB
        // date + catalog date + their periods) and the shipped manifest.json (mihomo version), so
        // an upgrade doesn't re-download everything just because updates.json didn't exist yet.
        private static void SeedFromLegacy(UpdateState s)
        {
            try
            {
                var geoManifest = ReadManifestFile(LegacyGeoManifestPath());
                if (geoManifest != null)
                {
                    if (!string.IsNullOrWhiteSpace(geoManifest.GeoDate)) s.GeoDataDate = geoManifest.GeoDate.Trim();
                    if (geoManifest.GeoPeriodDays > 0) s.GeoPeriodDays = geoManifest.GeoPeriodDays;
                    if (!string.IsNullOrWhiteSpace(geoManifest.CatalogDate)) s.TemplatesDate = geoManifest.CatalogDate.Trim();
                    if (geoManifest.CatalogPeriodDays > 0) s.TemplatesPeriodDays = geoManifest.CatalogPeriodDays;
                }
            }
            catch { }
            // The engine version is not seeded: it comes from the exe (VersionInfo.MihomoVersion).

            // manifest_geo.json is retired: once its data is folded into updates.json, drop any
            // lingering copies so it doesn't hang around in DataUser\.
            foreach (var p in new[]
            {
                Path.Combine(VersionInfo.DataUserDir, "manifest_geo.json"),
                Path.Combine(VersionInfo.InstallRoot, "manifest_geo.json"),
            })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // Legacy geo manifest location (DataUser first, flat next to exe as a fallback).
        private static string LegacyGeoManifestPath()
        {
            var inUser = Path.Combine(VersionInfo.DataUserDir, "manifest_geo.json");
            if (File.Exists(inUser)) return inUser;
            var flat = Path.Combine(VersionInfo.InstallRoot, "manifest_geo.json");
            if (File.Exists(flat)) return flat;
            return inUser;
        }

        private static UpdateManifest ReadManifestFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var ser = new DataContractJsonSerializer(typeof(UpdateManifest));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                    return (UpdateManifest)ser.ReadObject(ms);
            }
            catch { return null; }
        }
    }
}
