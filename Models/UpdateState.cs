using System.Runtime.Serialization;

namespace MihomoLauncher.Models
{
    // Live update-tracking state, persisted to DataUser\updates.json (survives app updates —
    // an update wipes Data\ + MihomoLauncher.exe but never DataUser\). Replaces the old
    // manifest_geo.json as the WRITABLE source of truth for installed dates / versions and the
    // per-item refresh periods; manifest_geo.json now only survives as a first-run seed.
    //
    // For each of the four updatable things we keep whatever is meaningful:
    //   * Geo (GeoLite2-City DB) — its own data date (yyyyMMdd) + when we last checked + period.
    //   * Templates (merged rule catalog) — content date (dd.MM.yyyy) + last check + period.
    //   * Engine (mihomo) — installed version + last check + period. The version lives here (not
    //     manifest.json) so it survives an app update that wipes Data\ (the engine exe lives in
    //     DataUser\ and survives too, so its version marker must travel with it).
    //   * App (the launcher) — last check + period only; the installed version is authoritative
    //     from the shipped Data\manifest.json (replaced on every self-update).
    //
    // Dates are stored as plain strings so a missing field just means "unknown". Periods default
    // via UpdateStateStore when 0/absent. EmitDefaultValue=false keeps the JSON tidy.
    [DataContract]
    public class UpdateState
    {
        // ----- Geo (GeoLite2-City) -----
        [DataMember(Name = "geoDataDate", EmitDefaultValue = false)]
        public string GeoDataDate { get; set; }        // yyyyMMdd — the DB's own date

        [DataMember(Name = "geoLastCheck", EmitDefaultValue = false)]
        public string GeoLastCheck { get; set; }       // yyyy-MM-dd — when we last checked the server

        [DataMember(Name = "geoPeriodDays", EmitDefaultValue = false)]
        public int GeoPeriodDays { get; set; }

        // ----- Templates (merged rule catalog) -----
        [DataMember(Name = "templatesDate", EmitDefaultValue = false)]
        public string TemplatesDate { get; set; }      // dd.MM.yyyy — the catalog content date

        [DataMember(Name = "templatesLastCheck", EmitDefaultValue = false)]
        public string TemplatesLastCheck { get; set; } // yyyy-MM-dd

        [DataMember(Name = "templatesPeriodDays", EmitDefaultValue = false)]
        public int TemplatesPeriodDays { get; set; }

        // ----- Engine (mihomo) -----
        [DataMember(Name = "engineVersion", EmitDefaultValue = false)]
        public string EngineVersion { get; set; }      // installed engine version, e.g. "1.19.26"

        [DataMember(Name = "engineLastCheck", EmitDefaultValue = false)]
        public string EngineLastCheck { get; set; }    // yyyy-MM-dd

        [DataMember(Name = "enginePeriodDays", EmitDefaultValue = false)]
        public int EnginePeriodDays { get; set; }

        // ----- App (launcher) -----
        [DataMember(Name = "appLastCheck", EmitDefaultValue = false)]
        public string AppLastCheck { get; set; }       // yyyy-MM-dd

        [DataMember(Name = "appPeriodDays", EmitDefaultValue = false)]
        public int AppPeriodDays { get; set; }
    }
}
