using System.Runtime.Serialization;

namespace MihomoLauncher.Models
{
    // Schema of manifest.json — both the local copy shipped with the app and the
    // remote one at https://byfox.dev/data/mihomolauncher/manifest.json.
    //
    //   { "appVersion": "1.0.0",
    //     "geoDate": "20260528", "geoPeriodDays": 180,
    //
    // No engine version: neither the local nor the byfox manifest carries one. The installed
    // version is read from mihomo-windows-amd64.exe; the latest one comes from GitHub.
    //     "blackmatrix7_version": "10.06.2026", "blackmatrix7_update_interval_days": 7 }
    //
    // geoDate is the installed GeoLite2-City DB date (yyyyMMdd). It lives in the LOCAL
    // manifest (replaces the old Data\maxmind\geolite2_data.txt marker); the remote geo
    // date is still a standalone file at https://byfox.dev/data/maxmind/geolite2_data.txt.
    // geoPeriodDays is how often (in days) to check for a newer geo DB — editable by hand;
    // 0/missing falls back to the built-in default (see VersionInfo.GeoPeriodDays).
    //
    // blackmatrix7_version is the installed service-list date (dd.MM.yyyy) — the same date
    // carried on the first line of Data\blackmatrix7+www.txt. The remote list lives at
    // https://byfox.dev/data/maxmind/blackmatrix7+www.txt; when its first-line date is newer
    // the whole local file is replaced. blackmatrix7_update_interval_days is how often (in
    // days) to check — 0/missing falls back to the built-in default (see VersionInfo.CatalogPeriodDays).
    // The same type backs BOTH manifests; each file only populates its own fields. With
    // EmitDefaultValue=false an in-place rewrite of one file doesn't pollute it with the
    // other's empty fields (e.g. a mihomo-version bump won't add geoDate:null to manifest.json).
    [DataContract]
    public class UpdateManifest
    {
        [DataMember(Name = "appVersion", EmitDefaultValue = false)]
        public string AppVersion { get; set; }

        [DataMember(Name = "geoDate", EmitDefaultValue = false)]
        public string GeoDate { get; set; }

        [DataMember(Name = "geoPeriodDays", EmitDefaultValue = false)]
        public int GeoPeriodDays { get; set; }

        [DataMember(Name = "blackmatrix7_version", EmitDefaultValue = false)]
        public string CatalogDate { get; set; }

        [DataMember(Name = "blackmatrix7_update_interval_days", EmitDefaultValue = false)]
        public int CatalogPeriodDays { get; set; }
    }
}
