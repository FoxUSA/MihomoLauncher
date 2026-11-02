using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // Outcome of a single site probe — Available plus a human-readable reason and
    // the HTTP status code (0 if no response).
    public class SiteProbeResult
    {
        public bool Available { get; set; }
        public int StatusCode { get; set; }
        public string Reason { get; set; } = "";
    }

    // Verifies that a target site actually loads from the current (VPN) exit IP,
    // not just that a TCP/TLS connection succeeds. This is the C# port of the
    // PowerShell probe we settled on:
    //
    //   curl -s -L --max-time 10 -A "<browser UA>" <url>
    //   body matches "Unable to load site|cf-error-details|..."  -> BLOCKED
    //   body non-empty                                           -> OK
    //   nothing                                                  -> NO RESPONSE
    //
    // The status code alone is useless here: Cloudflare answers a bare HTTP
    // client with 403 even when the site works fine in a real browser. The
    // discriminator is the response BODY — a banned exit IP gets the OpenAI /
    // Cloudflare "Unable to load site" interstitial, a working IP gets the real
    // page (or a "Just a moment..." challenge, which still counts as reachable).
    public static class SiteChecker
    {
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

        // Substrings that mark a block / ban interstitial rather than the real site.
        // Matched case-insensitively against the response body.
        private static readonly string[] BlockMarkers =
        {
            "unable to load site",
            "cf-error-details",
            "sorry, you have been blocked",
            "access denied",
            "you have been blocked",
        };

        static SiteChecker()
        {
            try
            {
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            }
            catch
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }

            // .NET Framework allows TWO concurrent connections per endpoint, and when a proxy
            // is used the endpoint is the PROXY — so every rule probed in parallel shares one
            // pair of slots through mihomo's mixed port. The wait for a free slot happens
            // inside SendAsync, i.e. it burns the probe's own timeout: with several rules the
            // slowest one (Cloudflare through a distant node) ran out of time queued, was
            // declared broken, and then connected instantly when retried alone from its tile.
            // Measured 2026-08-07: six parallel probes 807 ms at the default, 502 ms at 64.
            try
            {
                if (ServicePointManager.DefaultConnectionLimit < 64)
                    ServicePointManager.DefaultConnectionLimit = 64;
            }
            catch { }
        }

        // True iff the site loads (real page or challenge), false if blocked,
        // unreachable, or timed out. Never throws.
        //
        // When proxy is non-null, the request is routed through it — used to test a
        // site "through the VPN" by pointing at mihomo's mixed-port (http://127.0.0.1:7890).
        public static async Task<bool> IsAvailableAsync(string url, int timeoutSeconds = 10, CancellationToken ct = default, IWebProxy proxy = null)
        {
            var r = await ProbeAsync(url, timeoutSeconds, ct, proxy).ConfigureAwait(false);
            return r.Available;
        }

        // Detailed probe — same logic, but returns what actually happened so the
        // Settings "Проверить" button can show the user the response. Never throws.
        public static async Task<SiteProbeResult> ProbeAsync(string url, int timeoutSeconds = 10, CancellationToken ct = default, IWebProxy proxy = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                return new SiteProbeResult { Available = false, Reason = Loc.T("Svc_EmptyAddress") };

            var first = await SendAsync(url, timeoutSeconds, ct, proxy, withUserAgent: true).ConfigureAwait(false);

            // The site answered 4xx and gave us nothing to read — web.whatsapp.com's anti-bot
            // check: measured 2026-08-07 through a live node, claiming to be Chrome (any
            // version) gets an empty 400, while curl with NO User-Agent gets the real page
            // and HTTP 200. Worth one more try without the disguise; when even that stays
            // empty (our client sends more than curl does), the status rule below decides.
            if (first.HttpCode >= 400 && first.HttpCode < 500 && string.IsNullOrEmpty(first.Body))
            {
                var bare = await SendAsync(url, timeoutSeconds, ct, proxy, withUserAgent: false).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(bare.Body)) first = bare;
            }

            if (first.Error != null)
                return new SiteProbeResult { Available = false, Reason = first.Error };

            if (!string.IsNullOrEmpty(first.Body))
            {
                var lower = first.Body.ToLowerInvariant();
                foreach (var marker in BlockMarkers)
                    if (lower.Contains(marker))
                        return new SiteProbeResult { Available = false, StatusCode = first.HttpCode, Reason = Loc.T("Svc_LikelyBlocked", marker) };

                return new SiteProbeResult { Available = true, StatusCode = first.HttpCode, Reason = Loc.T("Svc_Available", first.HttpCode) };
            }

            // No body at all. An HTTP status still came back, which means the packets went
            // there and back through this exit — the one thing the probe exists to establish.
            // Only the server saying "I am broken" (5xx) is treated as unusable; an empty 400
            // or 403 used to sink the whole rule and leave the kill-switch holding it shut.
            if (first.HttpCode > 0 && first.HttpCode < 500)
                return new SiteProbeResult { Available = true, StatusCode = first.HttpCode, Reason = Loc.T("Svc_AvailableNoBody", first.HttpCode) };

            return new SiteProbeResult { Available = false, StatusCode = first.HttpCode, Reason = Loc.T("Svc_EmptyResponse", first.HttpCode) };
        }

        // One GET. Returns the status + body, or a ready-made reason when nothing came back.
        private class Attempt
        {
            public int HttpCode;
            public string Body;
            public string Error;   // non-null => no HTTP answer at all
        }

        private static async Task<Attempt> SendAsync(string url, int timeoutSeconds, CancellationToken ct, IWebProxy proxy, bool withUserAgent)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseProxy = proxy != null,
                Proxy = proxy,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) })
            {
                if (withUserAgent) http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
                http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                    {
                        var a = new Attempt { HttpCode = (int)resp.StatusCode };
                        try { a.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                        catch { a.Body = null; }
                        return a;
                    }
                }
                catch (OperationCanceledException)
                {
                    return new Attempt { Error = Loc.T("Svc_Timeout") };
                }
                catch (Exception ex)
                {
                    return new Attempt { Error = Loc.T("Svc_NoResponse", ex.Message) };
                }
            }
        }
    }
}
