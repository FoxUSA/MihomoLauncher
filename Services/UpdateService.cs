using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    public class UpdateCheckResult
    {
        public UpdateManifest Remote { get; set; }
        public bool AppUpdate { get; set; }
        public bool MihomoUpdate { get; set; }
        public bool GeoUpdate { get; set; }
        public string LocalApp { get; set; }
        public string LocalMihomo { get; set; }
        public string RemoteApp { get; set; }
        public string RemoteMihomo { get; set; }
        public string LocalGeo { get; set; }
        public string RemoteGeo { get; set; }

        // Where to fetch the mihomo engine from. When set, it's the GitHub release zip
        // (preferred); when empty, fall back to the single exe on byfox.dev.
        public string MihomoZipUrl { get; set; }

        public bool Any => AppUpdate || MihomoUpdate || GeoUpdate;
    }

    // Resolved mihomo engine source: a version plus where to get the binary.
    // ZipUrl set => GitHub release zip; ZipUrl empty => byfox.dev single exe.
    // Version is known only for GitHub. The mirror publishes no engine version: it is only
    // there for an install when GitHub can't be reached, and whatever it holds will do — the
    // installed version is then read from the exe itself.
    public class MihomoSource
    {
        public string Version { get; set; }
        public string ZipUrl { get; set; }
        public string Label => string.IsNullOrWhiteSpace(Version) ? "(byfox)" : Version;
    }

    // Online update: reads the remote manifest, and updates the launcher (zip self-update)
    // and/or the mihomo engine (single exe) from https://byfox.dev/data/mihomolauncher/.
    public static class UpdateService
    {
        private const string BaseUrl = "https://byfox.dev/data/mihomolauncher/";
        private const string ManifestUrl = BaseUrl + "manifest.json";
        // The launcher self-update archive. It contains MihomoLauncher.exe + Data\ (and optionally
        // help/uninstall shortcuts) but NOT DataUser\ — everything in it is applied as-is.
        private const string ZipUrl = BaseUrl + "update.zip";
        private const string MihomoUrl = BaseUrl + "mihomo-windows-amd64.exe";

        // Geo (MaxMind GeoLite2-City) and the template catalog both come from api.byfox.dev
        // (ByfoxApi). The installed geo date + refresh period live in updates.json; the default
        // geo period is ~half a year (see CheckGeoAsync). The catalog is checked daily against
        // the API's root manifest and refreshed SILENTLY (no prompt, no button) — see
        // TryUpdateCatalogSilentlyAsync — and on demand from Settings → «Обновление».
        private const string GeoDateUrl = ByfoxApi.GeoDateUrl;
        private const string GeoDbUrl = ByfoxApi.GeoCityUrl;

        private const string ZipName = "update.zip";

        // Staging folder the update archive is extracted into; the freshly extracted
        // MihomoLauncher.exe runs from here (in --apply-update mode) to swap files into the
        // install root. Kept stable so a normal startup can clean up a stale copy.
        public static string StageDir => Path.Combine(Path.GetTempPath(), "MihomoLauncherUpdate");

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.TryParseAdd("MihomoLauncher/" + VersionInfo.AppVersion);
            return h;
        }

        // ---- Check ----

        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var res = new UpdateCheckResult
            {
                LocalApp = VersionInfo.AppVersion,
                LocalMihomo = VersionInfo.MihomoVersion,
            };

            var state = UpdateStateStore.State;

            // Launcher self-update still comes from byfox.dev's manifest (the launcher is
            // our app, not on GitHub). Checked at most once per app period. Offline / not
            // deployed / 404 => no launcher update.
            if (UpdateStateStore.DueForCheck(state.AppLastCheck, UpdateStateStore.AppPeriodDays))
            {
                try
                {
                    var json = await Http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
                    var remote = VersionInfo.ParseManifest(json);
                    if (remote != null)
                    {
                        res.Remote = remote;
                        res.RemoteApp = remote.AppVersion;
                        res.AppUpdate = VersionInfo.IsNewer(remote.AppVersion, res.LocalApp);
                        UpdateStateStore.MarkAppChecked();
                    }
                }
                catch { }
            }

            // mihomo engine: GitHub is the source of truth (latest STABLE release); the
            // byfox.dev mirror is the fallback when GitHub is unreachable. Checked at most once
            // per engine period. See ResolveMihomoSourceAsync.
            if (UpdateStateStore.DueForCheck(state.EngineLastCheck, UpdateStateStore.EnginePeriodDays))
            {
                var src = await ResolveMihomoSourceAsync(ct).ConfigureAwait(false);
                if (src != null)
                {
                    res.RemoteMihomo = src.Version;
                    res.MihomoZipUrl = src.ZipUrl;
                    res.MihomoUpdate = VersionInfo.IsNewer(src.Version, res.LocalMihomo);
                    UpdateStateStore.MarkEngineChecked();
                }
            }

            await CheckGeoAsync(res, ct).ConfigureAwait(false);

            // The template catalog updates quietly in the background on its own period — it never
            // surfaces in the update button / prompt.
            await TryUpdateCatalogSilentlyAsync(false, ct).ConfigureAwait(false);
            return res;
        }

        // Template-catalog refresh from api.byfox.dev. Silent path (force=false) runs when due
        // (daily) and downloads templates.json only when the API's rule.date is newer than the
        // catalog in use; the manual path (force=true, Settings → «Обновление») always downloads.
        // The file is checked against rule/manifest.json's sha256 and must parse before it
        // replaces DataUser\templates.json. Returns the template count in use afterwards, or -1
        // when nothing was checked (not due / offline). onPercent reports 0..100 (may be null).
        public static async Task<int> UpdateTemplatesAsync(bool force, Action<int> onPercent, CancellationToken ct)
        {
            var state = UpdateStateStore.State;
            if (!force && !UpdateStateStore.DueForCheck(state.TemplatesLastCheck, UpdateStateStore.TemplatesPeriodDays))
                return -1;

            onPercent?.Invoke(force ? 10 : 0);
            try
            {
                var root = await ByfoxApi.RootManifestAsync(ct).ConfigureAwait(false);
                var local = BlackMatrixCatalog.LoadedDate;
                bool newer = root.RuleDate.Length > 0 && string.CompareOrdinal(root.RuleDate, local) > 0;
                if (!force && !newer)
                {
                    UpdateStateStore.MarkTemplatesChecked();
                    StampCatalogDate(local);
                    onPercent?.Invoke(100);
                    return BlackMatrixCatalog.All().Count;
                }

                onPercent?.Invoke(30);
                var hashes = await ByfoxApi.RuleFileHashesAsync(ct).ConfigureAwait(false);
                var bytes = ByfoxApi.Gunzip(await ByfoxApi.GetBytesAsync(ByfoxApi.TemplatesUrl + ".gz", 120, ct).ConfigureAwait(false));
                if (hashes.TryGetValue("templates.json", out var want) && want.Length > 0
                    && !string.Equals(ByfoxApi.Sha256(bytes), want, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("templates.json: sha256 не совпал с rule/manifest.json");

                onPercent?.Invoke(70);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                var parsed = BlackMatrixCatalog.ParseTemplates(json, out var date);
                if (parsed == null || parsed.Count == 0) throw new IOException("templates.json: каталог пуст");

                var path = BlackMatrixCatalog.TemplatesPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".new";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                BlackMatrixCatalog.Reset();
                DeleteLegacyCatalogs();

                UpdateStateStore.MarkTemplatesChecked();
                StampCatalogDate(date);
                Log.Info("Каталог шаблонов обновлён с api.byfox.dev: " + parsed.Count + " шт., от " + date + ".");
                onPercent?.Invoke(100);
                return parsed.Count;
            }
            catch (Exception ex)
            {
                if (force) throw new IOException(Loc.T("Upd_ErrCatalogNotDownloaded") + " " + ex.Message, ex);
                return -1;   // silent + offline: retry later, don't stamp lastCheck
            }
        }

        // updates.json keeps the catalog date the way Settings shows it (dd.MM.yyyy).
        private static void StampCatalogDate(string isoDate)
        {
            var d = BlackMatrixCatalog.ParseDate(isoDate);
            if (d == null) return;
            var shown = d.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            if (!string.Equals(VersionInfo.CatalogDate, shown, StringComparison.Ordinal))
                VersionInfo.UpdateLocalCatalogDate(shown);
        }

        // The pre-api catalog files: superseded once DataUser\templates.json exists.
        private static void DeleteLegacyCatalogs()
        {
            var root = VersionInfo.InstallRoot;
            foreach (var legacy in new[]
            {
                Path.Combine(VersionInfo.DataUserDir, "rules"),
                Path.Combine(VersionInfo.DataUserDir, "blackmatrix7+www.txt"),
                Path.Combine(root, "Data", "blackmatrix7+www.txt"),
                Path.Combine(root, "blackmatrix7+www.txt"),
            })
                try { if (File.Exists(legacy)) File.Delete(legacy); } catch { }
        }

        // The engine's ASN database (GeoLite2-ASN, ~9 MB) beside its config, from api.byfox.dev
        // instead of GitHub releases (often blocked from RU). Template lists carry IP-ASN rules
        // (Telegram: 5), and the engine refuses a config with IP-ASN while it has no ASN.mmdb and
        // can't download one — so the config builder writes those rules only once this file
        // exists. Refreshed when older than 90 days. Returns true when a file was (re)written.
        public static async Task<bool> EnsureAsnDbAsync(string workDir, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return false;
            var target = Path.Combine(workDir, "ASN.mmdb");
            try
            {
                if (File.Exists(target) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(target)).TotalDays < 90) return false;

                var geo = ByfoxApi.ParseJson(await ByfoxApi.GetStringAsync(ByfoxApi.GeoBase + "manifest.json", 30, ct).ConfigureAwait(false));
                var want = ByfoxApi.Str(ByfoxApi.Obj(ByfoxApi.Obj(ByfoxApi.Obj(geo, "geolite2"), "files"), "GeoLite2-ASN.mmdb"), "sha256");
                var bytes = await ByfoxApi.GetBytesAsync(ByfoxApi.GeoAsnUrl, 300, ct).ConfigureAwait(false);
                if (want.Length > 0 && !string.Equals(ByfoxApi.Sha256(bytes), want, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info("ASN.mmdb с api.byfox.dev не совпал по sha256 — оставляю прежний.");
                    return false;
                }

                Directory.CreateDirectory(workDir);
                var tmp = target + ".new";
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                }
                catch (Exception ex)
                {
                    // The engine holds the file open while it uses it — the next run retries.
                    try { File.Delete(tmp); } catch { }
                    Log.Info("ASN.mmdb занят движком, обновлю позже: " + ex.Message);
                    return false;
                }
                Log.Info("ASN.mmdb скачан с api.byfox.dev (" + bytes.Length / 1024 + " КБ).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("ASN.mmdb скачать не удалось: " + ex.Message);
                return false;
            }
        }

        private static async Task TryUpdateCatalogSilentlyAsync(bool force, CancellationToken ct)
        {
            try { await UpdateTemplatesAsync(force, null, ct).ConfigureAwait(false); }
            catch { }
        }

        // Resolve the mihomo engine to install: GitHub's latest stable release first, then
        // the byfox.dev mirror. Returns null only if BOTH are unreachable. Used by the
        // update check and by the first-run "engine missing" install flow.
        public static async Task<MihomoSource> ResolveMihomoSourceAsync(CancellationToken ct = default)
        {
            try
            {
                var gh = await GitHubMihomo.GetLatestAsync(Http, ct).ConfigureAwait(false);
                if (gh != null && !string.IsNullOrWhiteSpace(gh.Version))
                {
                    Log.Info("Источник движка: GitHub " + gh.Version);
                    return new MihomoSource { Version = gh.Version, ZipUrl = gh.ZipUrl };
                }
                Log.Info("Источник движка: GitHub без подходящего ассета.");
            }
            catch (Exception ex) { Log.Error("resolve-engine/github", ex); }

            // Fallback: the single exe on the byfox mirror (ZipUrl empty), version unknown. Only
            // checked for reachability, so a dead mirror still reads as "no connection".
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                using (var req = new HttpRequestMessage(HttpMethod.Head, MihomoUrl))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode)
                        {
                            Log.Info("Источник движка: зеркало byfox (версия станет известна из exe).");
                            return new MihomoSource { Version = "", ZipUrl = "" };
                        }
                        Log.Info("Источник движка: зеркало byfox ответило " + (int)resp.StatusCode + ".");
                    }
                }
            }
            catch (Exception ex) { Log.Error("resolve-engine/byfox", ex); }

            Log.Info("Источник движка не определён (GitHub и byfox недоступны).");
            return null;
        }

        // Geo refresh policy: the DATA's own age decides, not how long ago we last looked.
        // A city database ages slowly and weighs 66 MB, so being a few days behind the mirror
        // is not worth a banner — the user is asked only once the installed DB is older than
        // geoPeriodDays (default 180). Only then do we go online and confirm a newer one is
        // actually published. (Settings → «Обновление» still checks and updates on demand,
        // whatever the age.)
        private static async Task CheckGeoAsync(UpdateCheckResult res, CancellationToken ct)
        {
            try
            {
                var local = GeoIpService.LocalDate();
                res.LocalGeo = local?.ToString("yyyyMMdd") ?? "";
                bool dbMissing = !System.IO.File.Exists(GeoIpService.DbPath);

                // Missing DB or an unknown date: we can't reason about age, so check.
                // Otherwise: only when the data itself has gone stale.
                bool stale = dbMissing || local == null
                             || (DateTime.Today - local.Value).TotalDays >= UpdateStateStore.GeoPeriodDays;
                if (!stale) return;

                var txt = await Http.GetStringAsync(GeoDateUrl).ConfigureAwait(false);
                var remote = GeoIpService.ParseDate(txt);
                if (remote == null) return;
                UpdateStateStore.MarkGeoChecked();
                res.RemoteGeo = remote.Value.ToString("yyyyMMdd");

                // Offer when the DB is missing or the online DB is newer than ours.
                if (dbMissing || local == null || remote.Value > local.Value)
                    res.GeoUpdate = true;
            }
            catch { }
        }

        // ---- Manual checks (Settings → «Обновление»): fetch the remote marker, no side effects ----

        // Remote launcher version from byfox.dev's manifest, or null when unreachable.
        public static async Task<string> LatestAppVersionAsync(CancellationToken ct = default)
        {
            try
            {
                var json = await Http.GetStringAsync(ManifestUrl).ConfigureAwait(false);
                return VersionInfo.ParseManifest(json)?.AppVersion;
            }
            catch { return null; }
        }

        // Remote GeoLite2 DB date, or null when unreachable/unparseable.
        public static async Task<DateTime?> LatestGeoDateAsync(CancellationToken ct = default)
        {
            try
            {
                var txt = await Http.GetStringAsync(GeoDateUrl).ConfigureAwait(false);
                return GeoIpService.ParseDate(txt);
            }
            catch { return null; }
        }

        // ---- Download helper (reports 0..100) ----

        private static async Task DownloadAsync(string url, string destPath, Action<int> onPercent, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int last = -1, read;
                    while ((read = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, read, ct).ConfigureAwait(false);
                        done += read;
                        if (total > 0 && onPercent != null)
                        {
                            var pct = (int)(done * 100 / total);
                            if (pct != last) { last = pct; onPercent(pct); }
                        }
                    }
                }
            }
        }

        // ---- mihomo engine install / update (places the exe at cfg.MihomoExePath) ----

        // Download the engine and put it at cfg.MihomoExePath. Works both for a first-run
        // install (no existing exe) and for an in-place update (stops the running engine,
        // swaps the exe). When zipUrl is set, downloads the GitHub release zip and extracts
        // the single mihomo-windows-amd64.exe from it; otherwise downloads the byfox.dev exe.
        public static async Task<bool> UpdateMihomoAsync(AppConfig cfg, string newVersion, string zipUrl, Action<int> onPercent, CancellationToken ct)
        {
            var target = (cfg?.MihomoExePath ?? "").Trim();
            if (target.Length == 0) { Log.Info("Установка движка прервана: не задан путь к exe."); return false; }
            Log.Info("Установка движка → " + target + " (" + (string.IsNullOrWhiteSpace(newVersion) ? "версия неизвестна" : "v" + newVersion) + ", "
                     + (string.IsNullOrWhiteSpace(zipUrl) ? "byfox exe" : "github zip") + ")");

            // Stop the running engine so the exe isn't locked (no-op on first install).
            try { MihomoSetup.EndTask(); } catch { }

            var dir = Path.GetDirectoryName(target);
            Directory.CreateDirectory(dir);
            var tmpExe = Path.Combine(dir, "mihomo-windows-amd64.new.exe");

            bool gotExe = false;
            if (!string.IsNullOrWhiteSpace(zipUrl))
            {
                // GitHub: download the release zip, extract the single exe, drop the zip.
                var tmpZip = Path.Combine(dir, "mihomo-windows-amd64.new.zip");
                try
                {
                    await DownloadAsync(zipUrl, tmpZip, onPercent, ct).ConfigureAwait(false);
                    ExtractMihomoExe(tmpZip, tmpExe);
                    gotExe = true;
                }
                catch (Exception ex)
                {
                    // api.github.com resolves the version fine, but the release binary is served
                    // from GitHub's CDN (objects.githubusercontent.com), which is frequently
                    // reset/blocked by regional DPI even when the API host works. Don't fail —
                    // fall back to the byfox.dev mirror's single exe below.
                    Log.Error("engine-download/github-zip", ex);
                    Log.Info("Скачивание zip с GitHub не удалось — перехожу на зеркало byfox.");
                    try { if (File.Exists(tmpExe)) File.Delete(tmpExe); } catch { }
                }
                finally
                {
                    try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
                }
            }

            if (!gotExe)
            {
                // byfox.dev single exe (the mirror the dev keeps in sync). Reachable in regions
                // where GitHub's download CDN is blocked, so this is the reliable path there.
                Log.Info("Скачивание движка с зеркала byfox: " + MihomoUrl);
                onPercent?.Invoke(0);
                await DownloadAsync(MihomoUrl, tmpExe, onPercent, ct).ConfigureAwait(false);
            }

            // Swap the new exe in (retry a few times while the old handle releases).
            Exception lastErr = null;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmpExe, target);
                    // Record what actually landed: the GitHub zip may have failed over to the
                    // mirror, whose exe can be older (or of unknown version) — ask the binary.
                    var installed = MihomoBinary.VersionOf(target);
                    if (installed.Length == 0) installed = (newVersion ?? "").Trim();
                    VersionInfo.UpdateLocalMihomoVersion(installed);
                    Log.Info("Движок установлен: " + (installed.Length > 0 ? "v" + installed : "версия не определена"));
                    return true;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            try { if (File.Exists(tmpExe)) File.Delete(tmpExe); } catch { }
            var swapErr = lastErr ?? new IOException(Loc.T("Upd_ErrMihomoReplaceFailed"));
            Log.Error("engine-swap", swapErr);
            throw swapErr;
        }

        // Extract mihomo-windows-amd64.exe from a downloaded release zip to destExe.
        private static void ExtractMihomoExe(string zipPath, string destExe)
        {
            using (var za = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                System.IO.Compression.ZipArchiveEntry entry = null;
                foreach (var e in za.Entries)
                {
                    if (string.Equals(e.Name, GitHubMihomo.ExeEntryName, StringComparison.OrdinalIgnoreCase))
                    { entry = e; break; }
                }
                if (entry == null)
                {
                    // Be lenient if the asset layout ever changes: take the first .exe.
                    foreach (var e in za.Entries)
                        if (e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { entry = e; break; }
                }
                if (entry == null) throw new IOException(Loc.T("Upd_ErrNoExeInMihomoZip"));
                if (File.Exists(destExe)) File.Delete(destExe);
                entry.ExtractToFile(destExe, true);
            }
        }

        // ---- geo database update (download the mmdb + record its date) ----

        public static async Task<bool> UpdateGeoAsync(string newDate, Action<int> onPercent, CancellationToken ct)
        {
            var dir = GeoIpService.MaxmindDir;
            System.IO.Directory.CreateDirectory(dir);
            var tmp = System.IO.Path.Combine(dir, "GeoLite2-City.new.mmdb");

            await DownloadAsync(GeoDbUrl, tmp, onPercent, ct).ConfigureAwait(false);
            if (!System.IO.File.Exists(tmp) || new System.IO.FileInfo(tmp).Length == 0)
                throw new System.IO.IOException(Loc.T("Upd_ErrGeoNotDownloaded"));

            // mihomo doesn't hold this file, so a plain replace is fine.
            GeoIpService.Reset();
            var target = GeoIpService.DbPath;
            if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
            System.IO.File.Move(tmp, target);

            if (!string.IsNullOrWhiteSpace(newDate))
            {
                VersionInfo.UpdateLocalGeoDate(newDate);
                // Keep the marker beside the DB in step with it. It used to be written only by
                // the release layout, so a downloaded DB left the shipped marker in place and
                // the folder described a vintage it no longer held.
                try { System.IO.File.WriteAllText(GeoIpService.MarkerPath, newDate.Trim()); }
                catch (Exception ex) { Log.Error("geo-marker-write", ex); }
            }
            GeoIpService.Reset();
            return true;
        }

        // ---- launcher self-update ----
        // Downloads update.zip, extracts it to a staging folder, then launches the freshly
        // extracted MihomoLauncher.exe in "--apply-update" mode. That staged instance shows its
        // own progress window and does the actual swap (wait for us to exit → stop processes →
        // delete Data\ + MihomoLauncher.exe → copy the archive in → relaunch). DataUser\ and any
        // root files NOT present in the archive are left untouched. Returns true once the
        // updater has been launched — the caller must then shut down so the swap can proceed.
        //
        //   onStatus  — high-level step text ("Скачиваю обновление…", "Распаковываю…").
        //   onPercent — 0..100 for the current step (download, then extraction).
        public static async Task<bool> StartAppUpdateAsync(Action<string> onStatus, Action<int> onPercent, CancellationToken ct)
        {
            var root = VersionInfo.InstallRoot;
            var stage = StageDir;
            var newDir = Path.Combine(stage, "new");       // extracted archive (the new files)
            var runnerDir = Path.Combine(stage, "runner"); // a copy of the CURRENT install, runs the UI
            var zip = Path.Combine(stage, ZipName);

            try { if (Directory.Exists(stage)) ForceDeleteDir(stage); } catch { }
            Directory.CreateDirectory(stage);

            onStatus?.Invoke(Loc.T("Upd_Downloading"));
            await DownloadAsync(ZipUrl, zip, onPercent, ct).ConfigureAwait(false);
            if (!File.Exists(zip) || new FileInfo(zip).Length == 0)
                throw new IOException(Loc.T("Upd_ErrZipNotDownloaded"));

            onStatus?.Invoke(Loc.T("Upd_Extracting"));
            onPercent?.Invoke(0);
            Directory.CreateDirectory(newDir);
            ExtractZipWithProgress(zip, newDir, onPercent, ct);
            try { File.Delete(zip); } catch { }

            // The archive may be flat (MihomoLauncher.exe + Data\ at the root) or nested in a
            // parent folder — locate the exe and treat its directory as the source root.
            var newExe = Directory.GetFiles(newDir, "MihomoLauncher.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (newExe == null)
                throw new IOException(Loc.T("Upd_ErrLauncherNotInZip"));
            var srcRoot = Path.GetDirectoryName(newExe);

            // Files extracted from a downloaded zip can carry Mark-of-the-Web; strip it so the
            // installed copy isn't load-blocked on the user's machine.
            UnblockTree(srcRoot);

            // Run the updater UI from a COPY OF THE CURRENT (already-working) install, NOT from
            // the just-downloaded exe — the current install's binaries are known-good and load
            // reliably, so the progress window always shows even if the download were tainted.
            onStatus?.Invoke(Loc.T("Upd_PreparingInstaller"));
            onPercent?.Invoke(-1);
            Directory.CreateDirectory(runnerDir);
            CopyFileSafe(Path.Combine(root, "MihomoLauncher.exe"), Path.Combine(runnerDir, "MihomoLauncher.exe"));
            CopyDir(Path.Combine(root, "Data"), Path.Combine(runnerDir, "Data"));
            var runnerExe = Path.Combine(runnerDir, "MihomoLauncher.exe");
            if (!File.Exists(runnerExe))   // fallback: run the downloaded exe directly
            {
                runnerExe = newExe;
                runnerDir = srcRoot;
            }

            onStatus?.Invoke(Loc.T("Upd_StartingInstaller"));
            var pid = Process.GetCurrentProcess().Id;
            var psi = new ProcessStartInfo
            {
                FileName = runnerExe,
                Arguments = "--apply-update " + pid + " \"" + root + "\" \"" + srcRoot + "\"",
                UseShellExecute = true,
                WorkingDirectory = runnerDir,
            };
            Process.Start(psi);
            return true;
        }

        // Remove the Zone.Identifier (Mark-of-the-Web) stream from every file under dir.
        private static void UnblockTree(string dir)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.Delete(f + ":Zone.Identifier"); } catch { }
            }
            catch { }
        }

        private static void CopyFileSafe(string from, string to)
        {
            try
            {
                if (!File.Exists(from)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to, true);
            }
            catch { }
        }

        private static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = f.Substring(from.Length).TrimStart('\\', '/');
                var dst = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                try { File.Copy(f, dst, true); } catch { }
            }
        }

        // Recursively clear attributes + delete a directory (used to reset the stage folder).
        private static void ForceDeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            catch { }
            Directory.Delete(dir, true);
        }

        // Extract every entry of a zip into destDir, reporting 0..100 by entry count.
        private static void ExtractZipWithProgress(string zipPath, string destDir, Action<int> onPercent, CancellationToken ct)
        {
            using (var za = ZipFile.OpenRead(zipPath))
            {
                var entries = za.Entries;
                int total = entries.Count, done = 0, last = -1;
                foreach (var e in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(destDir, e.FullName.Replace('/', '\\'));
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\") || string.IsNullOrEmpty(e.Name))
                    {
                        Directory.CreateDirectory(target);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, overwrite: true);
                    }
                    done++;
                    if (total > 0 && onPercent != null)
                    {
                        var pct = (int)((long)done * 100 / total);
                        if (pct != last) { last = pct; onPercent(pct); }
                    }
                }
            }
        }
    }
}
