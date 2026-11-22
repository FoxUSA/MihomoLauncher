using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MihomoLauncher.Services
{
    // «Статистика» in the DNS window (2026-11-17, variant A of .dev\mockups\dns-stats.html):
    // how many times programs reached for sites by NAME, how many of those were cut and by whom,
    // per tile, domain, company and country — for today, 7 or 30 days.
    //
    // What is counted, and why it is not "DNS queries": under the engine every program asks the
    // ENGINE for names (fake-ip), so real queries from programs are invisible. What the engine
    // does report is one line per connection (checked live 2026-11-17: a connection is EITHER
    // «[TCP] … match … using X» OR «[TCP] dial X … error: …», never both):
    //   · using REJECT                         → cut by our rules (kill-switch / «Блокировать»);
    //   · dial error with 0.0.0.0 / [::]        → the resolver's ad/tracker stub (AdGuard, NextDNS);
    //   · dial error «dns resolve failed»       → the resolver gave no address: a block in
    //                                             NXDOMAIN / empty mode (a router with ad-blocking
    //                                             answers like this) or a name that doesn't exist —
    //                                             the line can't tell the two apart;
    //   · anything else                         → went through.
    // Connections to a bare address (torrents, games, P2P) are not counted: no name, no DNS.
    //
    // Kept per day: (tile, host, country, company) → counters. 31 days, DataUser\dnsstats.txt,
    // written every 5 minutes and on exit. «Запоминать домены» off → hosts are not stored
    // (and the ones already stored are dropped); the counters, tiles, companies, countries stay.
    // All calls on the UI thread.
    public static class DnsStats
    {
        public enum Kind { Allowed, Rule, Stub, NoAddress }

        private sealed class Cell { public int N, Rule, Stub, Empty; }

        private const int KeepDays = 31;
        private const int MaxKeysPerDay = 30000;
        private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

        // yyyyMMdd → "tile\thost\tcc\tcompany" → counters.
        private static readonly SortedDictionary<int, Dictionary<string, Cell>> Days = new SortedDictionary<int, Dictionary<string, Cell>>();
        private static readonly Dictionary<string, string> CountryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded, _dirty, _keep = true;
        private static DateTime _savedAt = DateTime.Now;

        public static event Action Changed;

        // Bumped on every change — the view redraws only when it moved.
        public static int Version { get; private set; }

        public static string FilePath => Path.Combine(VersionInfo.DataUserDir, "dnsstats.txt");

        public static bool KeepDomains
        {
            get { EnsureLoaded(); return _keep; }
            set
            {
                EnsureLoaded();
                if (_keep == value) return;
                _keep = value;
                if (!value)
                {
                    // Off = the names go now, not just from now on.
                    foreach (var day in Days.Keys.ToList())
                    {
                        var merged = new Dictionary<string, Cell>(StringComparer.Ordinal);
                        foreach (var kv in Days[day])
                        {
                            var p = kv.Key.Split('\t');
                            var key = p[0] + "\t\t" + p[2] + "\t" + p[3];
                            Cell c;
                            if (!merged.TryGetValue(key, out c)) merged[key] = c = new Cell();
                            Add(c, kv.Value);
                        }
                        Days[day] = merged;
                    }
                }
                Version++;
                _dirty = true;
                Save();
                Changed?.Invoke();
            }
        }

        // The first day anything was counted, null when nothing yet.
        public static DateTime? Since
        {
            get
            {
                EnsureLoaded();
                return Days.Count == 0 ? (DateTime?)null : DayDate(Days.Keys.First());
            }
        }

        public static void Record(string tile, string host, Kind kind, string cc, string ccName, string company)
        {
            EnsureLoaded();
            var now = DateTime.Now;
            int day = DayKey(now);
            Dictionary<string, Cell> cells;
            if (!Days.TryGetValue(day, out cells))
            {
                Days[day] = cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
                Trim(now);
            }
            var key = Clean(tile) + "\t" + (_keep ? Clean(host) : "") + "\t" + Clean(cc) + "\t" + Clean(company);
            Cell cell;
            if (!cells.TryGetValue(key, out cell))
            {
                // A machine that dials thousands of distinct names a day: the extra names fold into
                // «no name» for that tile; the totals stay right.
                if (cells.Count >= MaxKeysPerDay) key = Clean(tile) + "\t\t" + Clean(cc) + "\t" + Clean(company);
                if (!cells.TryGetValue(key, out cell)) cells[key] = cell = new Cell();
            }
            Version++;
            cell.N++;
            if (kind == Kind.Rule) cell.Rule++;
            else if (kind == Kind.Stub) cell.Stub++;
            else if (kind == Kind.NoAddress) cell.Empty++;
            if (!string.IsNullOrEmpty(cc) && !string.IsNullOrEmpty(ccName)) CountryNames[cc] = Clean(ccName);
            _dirty = true;
            if (now - _savedAt > SaveEvery) Save();
        }

        // DnsVerdict found `host` filtered by DNS: what was counted as «no address» before the
        // verdict (older days, an older build that couldn't tell) moves to «blocked by DNS».
        public static void Reclassify(string host)
        {
            EnsureLoaded();
            host = Clean(host);
            if (host.Length == 0) return;
            bool moved = false;
            foreach (var day in Days.Values)
                foreach (var kv in day)
                {
                    if (kv.Value.Empty == 0) continue;
                    var p = kv.Key.Split('	');
                    if (p.Length < 2 || !string.Equals(p[1], host, StringComparison.OrdinalIgnoreCase)) continue;
                    kv.Value.Stub += kv.Value.Empty;
                    kv.Value.Empty = 0;
                    moved = true;
                }
            if (!moved) return;
            Version++;
            _dirty = true;
            Changed?.Invoke();
        }

        public static void Clear()
        {
            EnsureLoaded();
            Days.Clear();
            Version++;
            _dirty = true;
            Save();
            Changed?.Invoke();
        }

        // ---------- reading ----------

        public sealed class Row
        {
            public string Key = "", Name = "";
            public int N, Blocked, Rule, Stub, Empty;
            // The reason most of its blocks had (for the blocked-domains dot).
            public Kind Main => Stub >= Rule && Stub > 0 ? Kind.Stub : Rule > 0 ? Kind.Rule : Kind.Allowed;
        }

        public sealed class Snapshot
        {
            public int N, Rule, Stub, Empty;
            // «No address» is not a block (DnsVerdict told it from a filter): neither blocked nor allowed.
            public int Blocked => Rule + Stub;
            public int Allowed => N - Rule - Stub - Empty;
            public int PeakBlocked;
            public DateTime? PeakDay;
            public List<Row> Domains = new List<Row>(), Tiles = new List<Row>(),
                             Companies = new List<Row>(), Countries = new List<Row>();
            public bool HasNamelessOnly;   // something counted, but no host names kept
        }

        // days: 1 = today, 7, 30. tile: null = all. mask: only names it passes (MaskOf), null = all —
        // with a mask, what was counted without a name can't match and is left out.
        public static Snapshot Query(int days, string tile, Func<string, bool> mask = null)
        {
            EnsureLoaded();
            var s = new Snapshot();
            int from = DayKey(DateTime.Today.AddDays(-(Math.Max(1, days) - 1)));
            var dom = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var til = new Dictionary<string, Row>(StringComparer.Ordinal);
            var com = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            var cty = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in Days.Where(d => d.Key >= from))
            {
                int dayBlocked = 0;
                foreach (var kv in day.Value)
                {
                    var p = kv.Key.Split('\t');
                    if (p.Length < 4) continue;
                    if (tile != null && !string.Equals(p[0], tile, StringComparison.Ordinal)) continue;
                    if (mask != null && (p[1].Length == 0 || !mask(p[1]))) continue;
                    var c = kv.Value;
                    s.N += c.N; s.Rule += c.Rule; s.Stub += c.Stub; s.Empty += c.Empty;
                    dayBlocked += c.Rule + c.Stub;
                    Bump(til, p[0], p[0], c);
                    if (p[1].Length > 0) Bump(dom, p[1], p[1], c);
                    if (p[3].Length > 0) Bump(com, p[3], p[3], c);
                    // A country only for what got through: a blocked name has no address.
                    if (p[2].Length > 0 && c.N - c.Rule - c.Stub - c.Empty > 0)
                    {
                        string name;
                        if (!CountryNames.TryGetValue(p[2], out name)) name = p[2];
                        Row r;
                        if (!cty.TryGetValue(p[2], out r)) cty[p[2]] = r = new Row { Key = p[2], Name = name };
                        r.N += c.N - c.Rule - c.Stub - c.Empty;
                    }
                }
                if (dayBlocked > s.PeakBlocked) { s.PeakBlocked = dayBlocked; s.PeakDay = DayDate(day.Key); }
            }
            s.Domains = Sorted(dom);
            s.Tiles = Sorted(til);
            s.Companies = Sorted(com);
            s.Countries = Sorted(cty);
            s.HasNamelessOnly = s.N > 0 && s.Domains.Count == 0;
            return s;
        }

        // The search box's mask (2026-11-18: «строка поиска по маске — оставляешь только то, что
        // попадает под маску»): «*» = any characters; without one — the text anywhere in the name.
        // A pasted URL counts as its host. null = no mask.
        public static Func<string, bool> MaskOf(string text)
        {
            var t = NormalizeMask(text);
            if (t.Length == 0) return null;
            if (t.IndexOf('*') < 0 && t.IndexOf('?') < 0)
                return h => h.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return h => rx.IsMatch(h);
        }

        public static string NormalizeMask(string text)
        {
            var t = (text ?? "").Trim();
            Uri u;
            if (t.IndexOf("://", StringComparison.Ordinal) > 0 && Uri.TryCreate(t, UriKind.Absolute, out u) && u.Host.Length > 0)
                t = u.Host;
            int slash = t.IndexOf('/');
            if (slash > 0) t = t.Substring(0, slash);
            return t.Trim('[', ']').ToLowerInvariant();
        }

        private static void Bump(Dictionary<string, Row> map, string key, string name, Cell c)
        {
            Row r;
            if (!map.TryGetValue(key, out r)) map[key] = r = new Row { Key = key, Name = name };
            r.N += c.N; r.Rule += c.Rule; r.Stub += c.Stub; r.Empty += c.Empty;
            r.Blocked = r.Rule + r.Stub;
        }

        private static List<Row> Sorted(Dictionary<string, Row> map)
            => map.Values.OrderByDescending(r => r.N).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // ---------- file ----------

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    var p = line.Split('\t');
                    if (line[0] == '#')
                    {
                        if (p[0] == "#keep" && p.Length > 1) _keep = p[1] != "0";
                        else if (p[0] == "#cc" && p.Length > 2) CountryNames[p[1]] = p[2];
                        continue;
                    }
                    int day;
                    if (p.Length < 9 || !int.TryParse(p[0], out day)) continue;
                    Dictionary<string, Cell> cells;
                    if (!Days.TryGetValue(day, out cells)) Days[day] = cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
                    var key = p[1] + "\t" + p[2] + "\t" + p[3] + "\t" + p[4];
                    Cell c;
                    if (!cells.TryGetValue(key, out c)) cells[key] = c = new Cell();
                    c.N += Int(p[5]); c.Rule += Int(p[6]); c.Stub += Int(p[7]); c.Empty += Int(p[8]);
                }
                Trim(DateTime.Now);
            }
            catch (Exception ex) { Log.Error("dns-stats-load", ex); }
        }

        public static void Save()
        {
            if (!_loaded || !_dirty) return;
            _dirty = false;
            _savedAt = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append("#v1\n#keep\t").Append(_keep ? "1" : "0").Append('\n');
            foreach (var kv in CountryNames) sb.Append("#cc\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            foreach (var day in Days)
                foreach (var kv in day.Value)
                {
                    var c = kv.Value;
                    sb.Append(day.Key).Append('\t').Append(kv.Key).Append('\t')
                      .Append(c.N).Append('\t').Append(c.Rule).Append('\t').Append(c.Stub).Append('\t').Append(c.Empty).Append('\n');
                }
            var text = sb.ToString();
            var path = FilePath;
            // Written off the UI thread; at exit (no time for that) the caller waits.
            var t = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                }
                catch (Exception ex) { Log.Error("dns-stats-save", ex); }
            });
            _lastWrite = t;
        }

        private static Task _lastWrite;

        // On exit: save and wait for the file to be written.
        public static void SaveAndWait()
        {
            Save();
            try { _lastWrite?.Wait(3000); } catch { }
        }

        private static void Trim(DateTime now)
        {
            int oldest = DayKey(now.Date.AddDays(-(KeepDays - 1)));
            foreach (var d in Days.Keys.Where(k => k < oldest).ToList()) Days.Remove(d);
        }

        private static void Add(Cell to, Cell c) { to.N += c.N; to.Rule += c.Rule; to.Stub += c.Stub; to.Empty += c.Empty; }
        private static int Int(string s) { int v; return int.TryParse(s, out v) ? v : 0; }
        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        private static int DayKey(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
        private static DateTime DayDate(int k) => new DateTime(k / 10000, k / 100 % 100, k % 100);

        // ---------- whose is a name ----------

        // The company behind a host by its registrable domain (googlevideo.com → Google), the way
        // tracker databases do it; null when the name is not one we know.
        public static string CompanyOfHost(string host)
        {
            var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            while (h.Length > 0)
            {
                string c;
                if (Companies.TryGetValue(h, out c)) return c;
                int dot = h.IndexOf('.');
                if (dot < 0) break;
                h = h.Substring(dot + 1);
            }
            return null;
        }

        // The owner of an address's network (GeoLite2-ASN organisation), tidied: «GOOGLE» → Google,
        // «Hetzner Online GmbH» → Hetzner. "" when unknown.
        public static string CompanyOfAsn(string org)
        {
            var o = (org ?? "").Trim();
            if (o.Length == 0) return "";
            var u = o.ToUpperInvariant();
            foreach (var kv in AsnKeywords)
                if (u.Contains(kv.Key)) return kv.Value;
            foreach (var suffix in new[] { ", INC.", " INC.", ", INC", " INC", " LLC", " LTD.", " LTD", " GMBH", " B.V.", " SAS", " S.A.", " AG", " PLC", " CORP.", " CORPORATION", " LIMITED", " CO." })
                if (u.EndsWith(suffix)) { o = o.Substring(0, o.Length - suffix.Length).TrimEnd(',', ' '); u = o.ToUpperInvariant(); }
            return o;
        }

        private static readonly KeyValuePair<string, string>[] AsnKeywords =
        {
            P("GOOGLE", "Google"), P("AMAZON", "Amazon"), P("CLOUDFLARE", "Cloudflare"), P("MICROSOFT", "Microsoft"),
            P("AKAMAI", "Akamai"), P("FASTLY", "Fastly"), P("YANDEX", "Yandex"), P("FACEBOOK", "Meta"),
            P("VKONTAKTE", "VK"), P("MAIL.RU", "VK"), P("TELEGRAM", "Telegram"), P("APPLE", "Apple"),
            P("HETZNER", "Hetzner"), P("OVH", "OVH"), P("DIGITALOCEAN", "DigitalOcean"), P("ROSTELECOM", "Rostelecom"),
            P("MEGAFON", "MegaFon"), P("VIMPELCOM", "Beeline"), P("MOBILE TELESYSTEMS", "MTS"), P("SELECTEL", "Selectel"),
            P("ORACLE", "Oracle"), P("ALIBABA", "Alibaba"), P("TENCENT", "Tencent"), P("BYTEDANCE", "ByteDance"),
            P("NETFLIX", "Netflix"), P("VALVE", "Valve"), P("EDGECAST", "Edgio"), P("CDN77", "CDN77"),
            P("LEASEWEB", "Leaseweb"), P("LINODE", "Akamai"), P("VULTR", "Vultr"), P("CHOOPA", "Vultr"),
        };

        private static KeyValuePair<string, string> P(string k, string v) => new KeyValuePair<string, string>(k, v);

        private static readonly Dictionary<string, string> Companies = BuildCompanies();

        private static Dictionary<string, string> BuildCompanies()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string company, params string[] domains) { foreach (var x in domains) d[x] = company; }
            Add("Google", "google.com", "google.ru", "googleapis.com", "googlevideo.com", "gstatic.com", "youtube.com", "ytimg.com",
                "youtu.be", "ggpht.com", "googleusercontent.com", "doubleclick.net", "googlesyndication.com", "googleadservices.com",
                "google-analytics.com", "googletagmanager.com", "googletagservices.com", "gvt1.com", "gvt2.com", "gvt3.com",
                "app-measurement.com", "firebaseio.com", "firebase.com", "crashlytics.com", "android.com", "withgoogle.com",
                "gmail.com", "2mdn.net", "admob.com", "google.by", "google.kz", "googlezip.net", "1e100.net", "goo.gl",
                "chromium.org", "googledomains.com", "recaptcha.net", "youtube-nocookie.com", "blogger.com", "blogspot.com");
            Add("Yandex", "yandex.ru", "yandex.net", "yandex.com", "yandex.by", "yandex.kz", "ya.ru", "yastatic.net", "yandexadexchange.net",
                "adfox.ru", "yandex-team.ru", "kinopoisk.ru", "dzen.ru", "turbopages.org", "yandexcloud.net", "yandex.cloud", "auto.ru");
            Add("VK", "vk.com", "vk.ru", "vk.me", "userapi.com", "vkuser.net", "vk-portal.net", "vkontakte.ru", "mail.ru", "ok.ru",
                "mycdn.me", "imgsmail.ru", "my.com", "vkvideo.ru", "rutube.ru", "vk-cdn.net");
            Add("Meta", "facebook.com", "facebook.net", "fbcdn.net", "fb.com", "fbsbx.com", "instagram.com", "cdninstagram.com",
                "whatsapp.com", "whatsapp.net", "messenger.com", "meta.com", "threads.net", "oculus.com");
            Add("Microsoft", "microsoft.com", "msn.com", "live.com", "live.net", "office.com", "office.net", "office365.com", "windows.com",
                "windows.net", "windowsupdate.com", "bing.com", "bing.net", "azure.com", "azureedge.net", "azurefd.net", "msedge.net",
                "skype.com", "xbox.com", "xboxlive.com", "linkedin.com", "licdn.com", "github.com", "githubusercontent.com",
                "githubassets.com", "github.io", "visualstudio.com", "vsassets.io", "microsoftonline.com", "sharepoint.com",
                "onedrive.com", "outlook.com", "sfx.ms", "msftconnecttest.com", "msftncsi.com", "aka.ms", "trafficmanager.net",
                "msauth.net", "msocsp.com", "microsoftstore.com", "s-microsoft.com", "clarity.ms", "vscode-cdn.net", "gvt-msft.com",
                "windowsazure.com", "msecnd.net", "live.org", "nuget.org", "npmjs.com", "npmjs.org");
            Add("Apple", "apple.com", "icloud.com", "mzstatic.com", "apple-cloudkit.com", "cdn-apple.com", "aaplimg.com", "itunes.com",
                "apple.news", "icloud-content.com", "me.com");
            Add("Amazon", "amazon.com", "amazonaws.com", "amazon-adsystem.com", "cloudfront.net", "media-amazon.com",
                "ssl-images-amazon.com", "primevideo.com", "a2z.com", "amazontrust.com", "twitch.tv", "ttvnw.net", "jtvnw.net",
                "amazonvideo.com", "awsstatic.com", "amazon.de", "amazon.co.uk");
            Add("Cloudflare", "cloudflare.com", "cloudflare-dns.com", "cloudflareinsights.com", "workers.dev", "pages.dev",
                "cloudflare.net", "one.one.one.one", "cloudflarestream.com");
            Add("Telegram", "telegram.org", "t.me", "telegram.me", "telesco.pe", "tdesktop.com", "cdn-telegram.org", "telegra.ph", "tg.dev");
            Add("OpenAI", "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com", "sora.com");
            Add("Anthropic", "anthropic.com", "claude.ai", "claude.com", "claudeusercontent.com");
            Add("Samsung", "samsung.com", "samsungcloud.com", "samsungapps.com", "samsungqbe.com", "samsungosp.com", "samsungdm.com");
            Add("Akamai", "akamai.net", "akamaized.net", "akamaihd.net", "akamaiedge.net", "edgekey.net", "edgesuite.net", "akamai.com");
            Add("Fastly", "fastly.net", "fastly.com", "fastlylb.net");
            Add("X", "twitter.com", "x.com", "twimg.com", "t.co");
            Add("ByteDance", "tiktok.com", "tiktokcdn.com", "tiktokv.com", "byteoversea.com", "ibytedtos.com", "bytedance.com", "capcut.com");
            Add("Netflix", "netflix.com", "nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net");
            Add("Spotify", "spotify.com", "scdn.co", "spotifycdn.com");
            Add("Discord", "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media");
            Add("Valve", "steampowered.com", "steamcommunity.com", "steamstatic.com", "steamserver.net", "valvesoftware.com", "steamcontent.com");
            Add("Sber", "sberbank.ru", "sber.ru", "sberbank.com", "sberdevices.ru", "sbermarket.ru", "kuper.ru", "okko.tv");
            Add("Wildberries", "wildberries.ru", "wb.ru", "wbbasket.ru", "wbstatic.net", "wbcontent.net");
            Add("Ozon", "ozon.ru", "ozone.ru", "ozonusercontent.com", "ozon.by", "ozon.kz");
            Add("Avito", "avito.ru", "avito.st");
            Add("Gosuslugi", "gosuslugi.ru", "esia.gosuslugi.ru");
            Add("T-Bank", "tbank.ru", "tinkoff.ru", "tcsbank.ru", "tinkoffjournal.ru");
            Add("Mozilla", "mozilla.org", "mozilla.com", "firefox.com", "mozilla.net");
            Add("Adobe", "adobe.com", "adobe.io", "adobelogin.com", "typekit.net", "adobedtm.com", "omtrdc.net", "demdex.net");
            Add("Zoom", "zoom.us", "zoom.com");
            Add("Dropbox", "dropbox.com", "dropboxapi.com", "dropboxusercontent.com", "dropboxstatic.com");
            Add("Criteo", "criteo.com", "criteo.net");
            Add("AppsFlyer", "appsflyer.com", "appsflyersdk.com");
            Add("Adjust", "adjust.com", "adjust.io");
            Add("Hotjar", "hotjar.com", "hotjar.io");
            Add("comScore", "scorecardresearch.com", "comscore.com");
            Add("Sentry", "sentry.io", "sentry-cdn.com");
            Add("Statsig", "statsig.com", "statsigapi.net", "featuregates.org");
            Add("Datadog", "datadoghq.com", "datadoghq.eu", "browser-intake-datadoghq.com");
            Add("JetBrains", "jetbrains.com", "jetbrains.net");
            Add("LastPass", "lastpass.com", "lastpass.eu");
            Add("Kaspersky", "kaspersky.com", "kaspersky.ru", "kaspersky-labs.com", "kas-labs.com");
            Add("Rostelecom", "rt.ru", "rostelecom.ru", "wink.ru");
            Add("Kinozal", "kinozal.tv", "kinozal.me", "kinozal.guru");
            Add("Wikimedia", "wikipedia.org", "wikimedia.org", "wikidata.org", "wiktionary.org");
            Add("Reddit", "reddit.com", "redd.it", "redditmedia.com", "redditstatic.com");
            Add("Pinterest", "pinterest.com", "pinimg.com");
            Add("Snap", "snapchat.com", "sc-cdn.net", "snapkit.com");
            Add("Duolingo", "duolingo.com");
            return d;
        }

        // A company name in the current language where it differs (Yandex → Яндекс).
        public static string CompanyDisplay(string company)
        {
            if (Loc.Current != AppLang.Ru || string.IsNullOrEmpty(company)) return company ?? "";
            switch (company)
            {
                case "Yandex": return "Яндекс";
                case "Sber": return "Сбер";
                case "Ozon": return "Озон";
                case "Avito": return "Авито";
                case "Gosuslugi": return "Госуслуги";
                case "Rostelecom": return "Ростелеком";
                case "MegaFon": return "МегаФон";
                case "Beeline": return "Билайн";
                case "MTS": return "МТС";
                case "Kaspersky": return "Касперский";
                case "Kinozal": return "Кинозал";
                case "Wildberries": return "Wildberries";
                case "T-Bank": return "Т-Банк";
                default: return company;
            }
        }
    }
}
