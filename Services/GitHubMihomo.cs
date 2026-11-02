using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // Resolves the latest STABLE mihomo release straight from GitHub.
    //
    // GitHub's /releases/latest endpoint by definition returns only the newest
    // release with prerelease=false and draft=false, so the version it gives back is
    // always the working build — never an Alpha/Prerelease. That means we never have
    // to bump a version number by hand: whatever is "latest stable" on GitHub wins.
    public static class GitHubMihomo
    {
        private const string LatestUrl =
            "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";

        // The universal amd64 build, e.g. "mihomo-windows-amd64-v1.19.26.zip".
        // Deliberately matches ONLY the plain build — not the -compatible / -v1/-v2/-v3
        // microarch variants nor the -go12x toolchain variants, which carry extra tokens.
        private static readonly Regex AssetRx =
            new Regex(@"^mihomo-windows-amd64-v\d+\.\d+\.\d+\.zip$",
                      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The single file inside that zip.
        public const string ExeEntryName = "mihomo-windows-amd64.exe";

        public class ReleaseInfo
        {
            public string Version { get; set; }   // dotted, no leading 'v', e.g. "1.19.26"
            public string ZipUrl { get; set; }    // browser_download_url of the .zip asset
        }

        // Query GitHub for the latest stable release and the matching windows-amd64 zip.
        // Returns null if the API is unreachable or no matching asset is found (the caller
        // then falls back to the byfox.dev mirror).
        public static async Task<ReleaseInfo> GetLatestAsync(HttpClient http, CancellationToken ct = default)
        {
            var json = await http.GetStringAsync(LatestUrl).ConfigureAwait(false);
            var rel = Parse(json);
            if (rel == null || rel.Assets == null) return null;

            foreach (var a in rel.Assets)
            {
                if (a == null || string.IsNullOrEmpty(a.Name) || string.IsNullOrEmpty(a.BrowserDownloadUrl))
                    continue;
                if (!AssetRx.IsMatch(a.Name)) continue;
                var ver = (rel.TagName ?? "").Trim().TrimStart('v', 'V');
                if (ver.Length == 0) return null;
                return new ReleaseInfo { Version = ver, ZipUrl = a.BrowserDownloadUrl };
            }
            return null;
        }

        private static GhRelease Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                var ser = new DataContractJsonSerializer(typeof(GhRelease));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (GhRelease)ser.ReadObject(ms);
            }
            catch { return null; }
        }

        // Minimal projection of the GitHub release JSON (unknown fields are ignored).
        [DataContract]
        private class GhRelease
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public GhAsset[] Assets { get; set; }
        }

        [DataContract]
        private class GhAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string BrowserDownloadUrl { get; set; }
        }
    }
}
