using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MihomoLauncher.Services
{
    // One pickable rule template (api.byfox.dev/rule/templates.json item): its name, the full
    // Clash classical list to download, a site to test availability with, and the catalog's
    // own words about it (title, description, tags) for the picker and its «?» card.
    public class BlackMatrixService
    {
        public string Name { get; set; }
        public string ClashUrl { get; set; }
        public string CheckUrl { get; set; }

        // Catalog id: the bm7 folder path ("Telegram", "Cloud/AkamaiCloud") or the byfox
        // folder name. Empty for rows from the legacy text formats.
        public string Id { get; set; } = "";
        public string Src { get; set; } = "";
        public string TitleRu { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string DescRu { get; set; } = "";
        public string DescEn { get; set; } = "";
        public string Category { get; set; } = "";
        public string Updated { get; set; } = "";      // yyyy-MM-dd
        public bool Hidden { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Sample { get; set; } = new List<string>();
        public int Domains, Suffixes, Keywords, Cidr, Cidr6, Asn;

        // Every other file of this template the catalog lists (domain-set, its classical rest,
        // other platforms) — what an old saved URL may point at, so it can be moved to ClashUrl.
        public List<string> OtherUrls { get; set; } = new List<string>();

        public string Title
        {
            get
            {
                var t = Loc.Current == AppLang.En ? (TitleEn ?? "") : (TitleRu ?? "");
                if (t.Length == 0) t = (TitleRu ?? "").Length > 0 ? TitleRu : (TitleEn ?? "");
                return t.Length > 0 ? t : (Name ?? "");
            }
        }

        public string Desc
        {
            get
            {
                var d = Loc.Current == AppLang.En ? (DescEn ?? "") : (DescRu ?? "");
                return d.Length > 0 ? d : ((DescRu ?? "").Length > 0 ? DescRu : (DescEn ?? ""));
            }
        }

        // The short name under the title, only when it says something the title doesn't.
        public string NameNote =>
            (Title ?? "").IndexOf(Name ?? "", StringComparison.OrdinalIgnoreCase) >= 0 ? "" : (Name ?? "");
    }

    // The rule-template catalog: api.byfox.dev/rule/templates.json — a mirror of
    // blackmatrix7/ios_rule_script plus our own templates, with RU/EN titles and descriptions.
    // Each template maps to its FULL Clash classical list (item.classical) and, where known,
    // a site to probe for availability (so the tile gates on the real exit IP).
    //
    // Two things the old name-built URLs got wrong, and why paths now come from the catalog:
    // for the 19 big domain-set templates (Apple, Advertising, Global, China…) <Name>.yaml is
    // only the classical REST of the list — Apple: 45 lines of 1604 — and 23 templates live in
    // nested folders (Cloud/AkamaiCloud) that no name-built path reaches.
    public static class BlackMatrixCatalog
    {
        public const string Bm7Base = ByfoxApi.RuleBase + "bm7/";
        public const string ByfoxRulesBase = ByfoxApi.RuleBase + "byfox/";

        // Built-in fallback only (no catalog file at all): bm7/Clash/<Name>/<Name>.yaml.
        public static string ClashUrlFor(string name)
        {
            var n = (name ?? "").Trim();
            return Bm7Base + "Clash/" + Uri.EscapeDataString(n) + "/" + Uri.EscapeDataString(n) + ".yaml";
        }

        // Best-effort availability-check URL. Explicit map first (covers the popular
        // services accurately), then KnownServices, then a "<name>.com" guess.
        public static string CheckUrlFor(string name)
        {
            var key = (name ?? "").Trim();
            if (CheckUrlMap.TryGetValue(key, out var url)) return url;

            var guess = KnownServices.GuessCheckUrl(key, "");
            if (!string.IsNullOrEmpty(guess)) return guess;

            if (key.Length == 0) return "";
            return "https://www." + key.ToLowerInvariant() + ".com/";
        }

        // Downloaded catalog (DataUser survives app updates) and the one shipped in Data\
        // (refreshed by every app update). The newer of the two by its "date" wins.
        public static string TemplatesPath => Path.Combine(VersionInfo.DataUserDir, "templates.json");
        public static string ShippedTemplatesPath => Path.Combine(VersionInfo.InstallRoot, "Data", "templates.json");

        // Pre-2026-11 catalog files, read only when no templates.json exists anywhere.
        private static IEnumerable<string> LegacyCatalogPaths => new[]
        {
            Path.Combine(VersionInfo.DataUserDir, "rules"),
            Path.Combine(VersionInfo.DataUserDir, "blackmatrix7+www.txt"),
            Path.Combine(VersionInfo.InstallRoot, "Data", "blackmatrix7+www.txt"),
        };

        // Built once and reused — ~700 templates, so rebuilding on every picker open is wasted.
        private static IReadOnlyList<BlackMatrixService> _cache;
        private static string _cacheDate = "";

        // Every template, hidden ones included (an old rule may still use one).
        public static IReadOnlyList<BlackMatrixService> All()
        {
            if (_cache != null) return _cache;

            var fromJson = LoadNewestTemplates(out var date);
            if (fromJson != null && fromJson.Count > 0) { _cacheDate = date; return _cache = fromJson; }

            foreach (var p in LegacyCatalogPaths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var legacy = ParseCatalog(File.ReadAllText(p));
                    if (legacy != null && legacy.Count > 0) return _cache = legacy;
                }
                catch { }
            }

            return _cache = Services
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Select(s => new BlackMatrixService
                {
                    Name = s,
                    ClashUrl = ClashUrlFor(s),
                    CheckUrl = CheckUrlFor(s),
                })
                .ToList();
        }

        // What the template picker offers: no hidden templates (kept only for clients that
        // already use them), and not AdGuardSDNSFilter/Direct — an allow-list of exceptions,
        // which the word «реклама» finds next to Advertising and which blocks what should work.
        public static IEnumerable<BlackMatrixService> Pickable() =>
            All().Where(s => !s.Hidden && !string.IsNullOrEmpty(s.ClashUrl)
                             && !string.Equals(s.Id, "AdGuardSDNSFilter/Direct", StringComparison.OrdinalIgnoreCase));

        // The date (yyyy-MM-dd) of the catalog All() came from, "" for legacy/built-in.
        public static string LoadedDate { get { All(); return _cacheDate; } }

        // Drop the cached list so the next All() re-reads the (freshly downloaded) file.
        public static void Reset() { _cache = null; _cacheDate = ""; }

        private static List<BlackMatrixService> LoadNewestTemplates(out string date)
        {
            date = "";
            List<BlackMatrixService> best = null;
            foreach (var p in new[] { TemplatesPath, ShippedTemplatesPath })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var list = ParseTemplates(File.ReadAllText(p, Encoding.UTF8), out var d);
                    if (list == null || list.Count == 0) continue;
                    if (best == null || string.CompareOrdinal(d, date) > 0) { best = list; date = d; }
                }
                catch { }
            }
            return best;
        }

        // templates.json text → templates (sorted by name). Null when it isn't a usable catalog,
        // so the updater can refuse a bad download before it replaces anything.
        public static List<BlackMatrixService> ParseTemplates(string json, out string date)
        {
            date = "";
            if (string.IsNullOrWhiteSpace(json)) return null;
            var root = ByfoxApi.ParseJson(json);
            date = ByfoxApi.Str(root, "date");
            var baseUrl = ByfoxApi.Base + ByfoxApi.Str(root, "base");
            if (!baseUrl.EndsWith("/")) baseUrl += "/";

            var list = new List<BlackMatrixService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in ByfoxApi.Arr(root, "items"))
            {
                var id = ByfoxApi.Str(it, "id");
                var name = ByfoxApi.Str(it, "name");
                if (name.Length == 0) name = id;
                if (id.Length == 0 || !seen.Add(id)) continue;

                // The Clash list to route by: the full classical one. A template without it
                // (none today) falls back to its Clash file when that one is classical.
                var classical = ByfoxApi.Str(it, "classical");
                var files = ByfoxApi.Obj(it, "files");
                var rest = ByfoxApi.Obj(it, "rest");
                if (classical.Length == 0 && !ByfoxApi.Bool(it, "domain_set")) classical = ByfoxApi.Str(files, "clash");

                var svc = new BlackMatrixService
                {
                    Id = id,
                    Name = name,
                    Src = ByfoxApi.Str(it, "src"),
                    ClashUrl = classical.Length > 0 ? baseUrl + classical : "",
                    CheckUrl = ByfoxApi.Str(it, "check"),
                    TitleRu = ByfoxApi.Str(ByfoxApi.Obj(it, "title"), "ru"),
                    TitleEn = ByfoxApi.Str(ByfoxApi.Obj(it, "title"), "en"),
                    DescRu = ByfoxApi.Str(ByfoxApi.Obj(it, "desc"), "ru"),
                    DescEn = ByfoxApi.Str(ByfoxApi.Obj(it, "desc"), "en"),
                    Category = ByfoxApi.Str(it, "category"),
                    Updated = ByfoxApi.Str(it, "updated"),
                    Hidden = ByfoxApi.Bool(it, "hidden"),
                };
                foreach (var t in ByfoxApi.Arr(it, "tags")) if (t is string s && s.Length > 0) svc.Tags.Add(s);
                foreach (var t in ByfoxApi.Arr(it, "sample")) if (t is string s && s.Length > 0) svc.Sample.Add(s);
                var st = ByfoxApi.Obj(it, "stats");
                svc.Domains = ByfoxApi.Int(st, "domains");
                svc.Suffixes = ByfoxApi.Int(st, "suffixes");
                svc.Keywords = ByfoxApi.Int(st, "keywords");
                svc.Cidr = ByfoxApi.Int(st, "cidr");
                svc.Cidr6 = ByfoxApi.Int(st, "cidr6");
                svc.Asn = ByfoxApi.Int(st, "asn");
                foreach (var group in new[] { files, rest })
                    if (group != null)
                        foreach (var kv in group)
                            if (kv.Value is string path && path.Length > 0) svc.OtherUrls.Add(baseUrl + path);

                list.Add(svc);
            }
            if (list.Count == 0) return null;
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // The template a saved list URL belongs to: by its classical URL, else by any other file
        // of it. Null for a URL the catalog doesn't know (a hand-typed list, an old built-in name).
        public static BlackMatrixService ForUrl(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return null;
            var all = All();
            foreach (var s in all)
                if (string.Equals(s.ClashUrl, u, StringComparison.OrdinalIgnoreCase)) return s;
            foreach (var s in all)
                foreach (var o in s.OtherUrls)
                    if (string.Equals(o, u, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // The name to show for a template URL: the catalog's, else the list's file name without
        // the bm7 split suffixes (Apple_Classical.yaml → Apple).
        public static string NameForUrl(string url, string fallback = "")
        {
            var s = ForUrl(url);
            if (s != null) return s.Name;
            try
            {
                var u = (url ?? "").Trim();
                if (u.Length == 0) return fallback;
                var q = u.IndexOfAny(new[] { '?', '#' });
                if (q >= 0) u = u.Substring(0, q);
                var segs = u.TrimEnd('/').Split('/');
                var name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(segs[segs.Length - 1]));
                // byfox: …/byfox/<name>/clash/rules.yaml — the folder is the name.
                if (string.Equals(name, "rules", StringComparison.OrdinalIgnoreCase) && segs.Length >= 3)
                    name = Uri.UnescapeDataString(segs[segs.Length - 3]);
                foreach (var tail in new[] { "_Classical", "_Domain", "_Resolve", "_No_Resolve" })
                    if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - tail.Length);
                return string.IsNullOrWhiteSpace(name) ? fallback : name;
            }
            catch { return fallback; }
        }

        // A list URL saved by an older build, moved to where it lives now:
        //   raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/ → api.byfox.dev/rule/bm7/
        //   byfox.dev/data/awg-panel/rules/                                      → api.byfox.dev/rule/byfox/
        // and then to the template's FULL classical list (a domain-set template's <Name>.yaml
        // is only its rest). Returns the URL unchanged when there is nothing to move.
        public static string MigrateUrl(string url)
        {
            var u = (url ?? "").Trim();
            if (u.Length == 0) return u;
            var moved = SwapPrefix(u, "raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/", Bm7Base)
                        ?? SwapPrefix(u, "cdn.jsdelivr.net/gh/blackmatrix7/ios_rule_script@master/rule/", Bm7Base)
                        ?? SwapPrefix(u, "byfox.dev/data/awg-panel/rules/", ByfoxRulesBase)
                        ?? u;
            var svc = ForUrl(moved);
            return svc != null && !string.IsNullOrEmpty(svc.ClashUrl) ? svc.ClashUrl : moved;
        }

        private static string SwapPrefix(string url, string oldHostPath, string newBase)
        {
            foreach (var scheme in new[] { "https://", "http://", "https://www.", "http://www." })
                if (url.StartsWith(scheme + oldHostPath, StringComparison.OrdinalIgnoreCase))
                    return newBase + url.Substring(scheme.Length + oldHostPath.Length);
            return null;
        }

        // Parse a legacy catalog text into services (deduped case-insensitively, sorted): the
        // 2-column blackmatrix7 format ("Name|CheckUrl") and the merged 3-column DataUser\rules
        // format ("Name|CheckUrl|ClashUrl"). An empty ClashUrl is derived from the name.
        public static IReadOnlyList<BlackMatrixService> ParseCatalog(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            var list = new List<BlackMatrixService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inComment = false;

            foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                // Skip the leading <!-- ... --> comment block (date + notes / example URLs).
                if (!inComment && line.StartsWith("<!--", StringComparison.Ordinal))
                {
                    if (!line.Contains("-->")) inComment = true;
                    continue;
                }
                if (inComment)
                {
                    if (line.Contains("-->")) inComment = false;
                    continue;
                }

                var cols = line.Split('|');
                var name = cols[0].Trim();
                var check = cols.Length > 1 ? cols[1].Trim() : "";
                var clash = cols.Length > 2 ? cols[2].Trim() : "";
                if (name.Length == 0 || !seen.Add(name)) continue;

                list.Add(new BlackMatrixService
                {
                    Name = name,
                    ClashUrl = clash.Length > 0 ? MigrateUrlNoCatalog(clash) : ClashUrlFor(name),
                    CheckUrl = check,   // authoritative — empty means "no availability probe"
                });
            }

            if (list.Count == 0) return null;
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // Prefix swap only — ParseCatalog runs while All() is being built, so no catalog lookup.
        private static string MigrateUrlNoCatalog(string url) =>
            SwapPrefix(url, "raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/", Bm7Base)
            ?? SwapPrefix(url, "byfox.dev/data/awg-panel/rules/", ByfoxRulesBase)
            ?? url;

        // Read a dd.MM.yyyy date from a legacy first line ("<!--10.06.2026"), or a bare
        // dd.MM.yyyy / yyyy-MM-dd. Returns null on anything unparseable.
        public static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            var lt = t.IndexOf("<!--", StringComparison.Ordinal);
            if (lt >= 0) t = t.Substring(lt + 4).Trim();
            if (DateTime.TryParseExact(t.Length >= 10 ? t.Substring(0, 10) : t, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;

            // Keep only the leading dd.MM.yyyy token (digits and dots).
            var sb = new StringBuilder();
            foreach (var c in t)
            {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else break;
            }
            return DateTime.TryParseExact(sb.ToString(), "dd.MM.yyyy", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        // Curated check URLs for the well-known services (the rest fall back to a guess).
        private static readonly Dictionary<string, string> CheckUrlMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Instagram", "https://www.instagram.com/" },
            { "Threads", "https://www.threads.net/" },
            { "Facebook", "https://www.facebook.com/" },
            { "YouTube", "https://www.youtube.com/" },
            { "YouTubeMusic", "https://music.youtube.com/" },
            { "Google", "https://www.google.com/" },
            { "GoogleDrive", "https://drive.google.com/" },
            { "GoogleEarth", "https://earth.google.com/" },
            { "Telegram", "https://web.telegram.org/" },
            { "TelegramNL", "https://web.telegram.org/" },
            { "TelegramSG", "https://web.telegram.org/" },
            { "TelegramUS", "https://web.telegram.org/" },
            { "Whatsapp", "https://web.whatsapp.com/" },
            { "Discord", "https://discord.com/app" },
            { "Twitter", "https://x.com/" },
            { "TikTok", "https://www.tiktok.com/" },
            { "Reddit", "https://www.reddit.com/" },
            { "OpenAI", "https://chatgpt.com/" },
            { "Claude", "https://claude.ai/" },
            { "Anthropic", "https://claude.ai/" },
            { "Civitai", "https://civitai.com/" },
            { "Netflix", "https://www.netflix.com/" },
            { "Disney", "https://www.disneyplus.com/" },
            { "Hulu", "https://www.hulu.com/" },
            { "HBO", "https://www.max.com/" },
            { "Spotify", "https://open.spotify.com/" },
            { "SoundCloud", "https://soundcloud.com/" },
            { "Deezer", "https://www.deezer.com/" },
            { "TIDAL", "https://tidal.com/" },
            { "Twitch", "https://www.twitch.tv/" },
            { "Vimeo", "https://vimeo.com/" },
            { "Dailymotion", "https://www.dailymotion.com/" },
            { "Pinterest", "https://www.pinterest.com/" },
            { "LinkedIn", "https://www.linkedin.com/" },
            { "Tumblr", "https://www.tumblr.com/" },
            { "Pixiv", "https://www.pixiv.net/" },
            { "GitHub", "https://github.com/" },
            { "GitLab", "https://gitlab.com/" },
            { "GitBook", "https://www.gitbook.com/" },
            { "Docker", "https://hub.docker.com/" },
            { "Notion", "https://www.notion.so/" },
            { "Figma", "https://www.figma.com/" },
            { "Slack", "https://slack.com/" },
            { "Zoom", "https://zoom.us/" },
            { "Dropbox", "https://www.dropbox.com/" },
            { "OneDrive", "https://onedrive.live.com/" },
            { "iCloud", "https://www.icloud.com/" },
            { "Microsoft", "https://www.microsoft.com/" },
            { "Apple", "https://www.apple.com/" },
            { "AppStore", "https://apps.apple.com/" },
            { "AppleMusic", "https://music.apple.com/" },
            { "AppleTV", "https://tv.apple.com/" },
            { "AppleNews", "https://apple.news/" },
            { "Amazon", "https://www.amazon.com/" },
            { "AmazonPrimeVideo", "https://www.primevideo.com/" },
            { "eBay", "https://www.ebay.com/" },
            { "PayPal", "https://www.paypal.com/" },
            { "Steam", "https://store.steampowered.com/" },
            { "Speedtest", "https://www.speedtest.net/" },
            { "Wikimedia", "https://www.wikipedia.org/" },
            { "Protonmail", "https://mail.proton.me/" },
            { "Line", "https://line.me/" },
            { "KakaoTalk", "https://www.kakaocorp.com/" },
            { "Naver", "https://www.naver.com/" },
            { "Yandex", "https://ya.ru/" },
            { "VK", "https://vk.com/" },
            { "Mailru", "https://mail.ru/" },
            { "Bahamut", "https://www.gamer.com.tw/" },
            { "BBC", "https://www.bbc.com/" },
            { "CNN", "https://www.cnn.com/" },
            { "NYTimes", "https://www.nytimes.com/" },
            { "Bloomberg", "https://www.bloomberg.com/" },
            { "Duckduckgo", "https://duckduckgo.com/" },
            { "Cloudflare", "https://www.cloudflare.com/" },
            { "Nvidia", "https://www.nvidia.com/" },
            { "Intel", "https://www.intel.com/" },
            { "AMD", "https://www.amd.com/" },
            { "Tesla", "https://www.tesla.com/" },
            { "Jetbrains", "https://www.jetbrains.com/" },
            { "Python", "https://www.python.org/" },
            { "Npmjs", "https://www.npmjs.com/" },
            { "Jsdelivr", "https://www.jsdelivr.com/" },
            { "Ubuntu", "https://ubuntu.com/" },
            { "Mozilla", "https://www.mozilla.org/" },
            { "Opera", "https://www.opera.com/" },
            { "Vercel", "https://vercel.com/" },
            { "Heroku", "https://www.heroku.com/" },
            { "DigitalOcean", "https://www.digitalocean.com/" },
            { "Salesforce", "https://www.salesforce.com/" },
            { "Atlassian", "https://www.atlassian.com/" },
            { "Oracle", "https://www.oracle.com/" },
            { "IBM", "https://www.ibm.com/" },
            { "TeamViewer", "https://www.teamviewer.com/" },
            { "Duolingo", "https://www.duolingo.com/" },
            { "IMDB", "https://www.imdb.com/" },
            { "Imgur", "https://imgur.com/" },
            { "OKX", "https://www.okx.com/" },
            { "Zalo", "https://zalo.me/" },
            { "WeTV", "https://wetv.vip/" },
            { "Emby", "https://emby.media/" },
            { "MEGA", "https://mega.nz/" },
            { "Quora", "https://www.quora.com/" },
            { "Signal", "https://signal.org/" },
            { "Bing", "https://www.bing.com/" },
            { "Copilot", "https://copilot.microsoft.com/" },
            { "MicrosoftEdge", "https://www.microsoft.com/edge" },
            { "iQIYIIntl", "https://www.iq.com/" },
        };

        // Full blackmatrix7 Clash rule list. All() de-duplicates case-insensitively
        // and sorts alphabetically.
        private static readonly string[] Services =
        {
            "115","12306","1337x","17173","178","17zuoye","2KGames","360","36kr","3Type",
            "3dm","4399","4Paradigm","4chan","51Job","51nod","56","58TongCheng","6JianFang","6park",
            "8btc","9News","9to5","ABC","AFP","ALJazeera","AMD","AMP","AOL","APKCombo",
            "ATTWatchTV","Abema","AbemaTV","AcFun","Accuweather","Acer","Acplay","Actalis","AdColony","AdGuardSDNSFilter",
            "AddToAny","Addthis","Adidas","Adobe","AdobeActivation","Advertising","AdvertisingLite","AdvertisingMiTV","AdvertisingTest","Aerogard",
            "Afdian","Agora","AiQiCha","AirChina","AirWick","Akamai","Ali213","AliPay","Alibaba","All4",
            "Amazon","AmazonCN","AmazonIP","AmazonPrimeVideo","AmazonTrust","Americasvoice","AnTianKeJi","Anaconda","AnandTech","Android",
            "Anime","Anjuke","Anonv","Anthropic","Antutu","Apifox","Apkpure","AppLovin","AppStore","Apple",
            "AppleDaily","AppleDev","AppleFirmware","AppleHardware","AppleID","AppleMail","AppleMedia","AppleMusic","AppleNews","AppleProxy",
            "AppleTV","Arphic","Asahi","AsianMedia","Assassin'sCreed","Atlassian","Atomdata","BBC","BMW","BOC",
            "BOCOM","Bahamut","BaiDuTieBa","BaiFenDian","BaiShanYunKeJi","Baidu","BaoFengYingYin","BardAI","Battle","BeStore",
            "Beats","BesTV","Bestbuy","BianFeng","BiliBili","BiliBiliIntl","Binance","Bing","Blizzard","BlockHttpDNS",
            "Bloomberg","Blued","BoXun","Bootcss","BrightCove","BritboxUK","Buypass","ByteDance","CAS","CBS",
            "CCB","CCTV","CEB","CETV","CGB","CHT","CIBN","CKJR","CMB","CNKI",
            "CNN","CNNIC","CSDN","CWSeed","CableTV","CaiNiao","CaiXinChuanMei","Cake","Camera360","Canon",
            "ChengTongWangPan","China","ChinaDNS","ChinaIPs","ChinaIPsBGP","ChinaMax","ChinaMaxNoIP","ChinaMaxNoMedia","ChinaMedia","ChinaMobile",
            "ChinaNews","ChinaNoMedia","ChinaTelecom","ChinaTest","ChinaUnicom","Chromecast","ChuangKeTie","ChunYou","Cisco","Civitai",
            "Classic","Claude","Cloud","Cloudflare","Cloudflarecn","Clubhouse","ClubhouseIP","Cnet","Collabora","Comodo",
            "Contentful","Coolapk","Copilot","Crypto","Cryptocurrency","CyberTrust","DAZN","DMM","DNS","DaMai",
            "Dailymail","Dailymotion","DanDanZan","Dandanplay","DangDang","Dedao","Deepin","Deezer","Dell","Developer",
            "DiDi","DiLianWangLuo","DiSiFanShi","DiabloIII","DianCeWangKe","DigiCert","DigitalOcean","DingTalk","DingXiangYuan","Direct",
            "Discord","DiscoveryPlus","Disney","Disqus","Docker","Domob","Dood","DouBan","DouYin","Douyu",
            "Download","Dropbox","DtDNS","Dubox","Duckduckgo","DuoWan","Duolingo","DynDNS","Dynu","EA",
            "EHGallery","EastMoney","EasyPrivacy","Electron","Eleme","Embl","Emby","Emojipedia","EncoreTVB","Entrust",
            "Epic","Espn","FOXNOW","FOXPlus","Facebook","FanFou","FangZhengDianZi","Faronics","FeiZhu","FengHuangWang",
            "FengXiaWangLuo","Figma","Fiio","FindMy","FitnessPlus","FlipBoard","Flurry","Fox","FreeCodeCamp","FuboTV",
            "Funshion","Game","GaoDe","Garena","Geely","Gettyimages","Gigabyte","GitBook","GitHub","GitLab",
            "Gitee","Global","GlobalMedia","GlobalScholar","GlobalSign","Gog","Google","GoogleDrive","GoogleEarth","GoogleFCM",
            "GoogleSearch","GoogleVoice","GovCN","Gucci","GuiGuDongLi","HBO","HBOAsia","HBOHK","HBOUSA","HKBN",
            "HKOpenTV","HKedcity","HP","HWTV","HaiNanHangKong","HamiVideo","HanYi","HashiCorp","Haveibeenpwned","HeMa",
            "Hearthstone","HeroesoftheStorm","Heroku","HibyMusic","Hijacking","Himalaya","Hkgolden","HoYoverse","Hpplay","HuYa",
            "HuaShuTV","HuanJu","Huawei","Huffpost","Hulu","HuluJP","HuluUSA","HunanTV","Hupu","IBM",
            "ICBC","IKEA","IMDB","IPTVMainland","IPTVOther","ITV","Identrust","Imgur","Instagram","Intel",
            "Intercom","JOOX","Japonx","Jetbrains","Jfrog","JiGuangTuiSong","JianGuoYun","JianShu","JinJiangWenXue","JingDong",
            "Jquery","Jsdelivr","JueJin","Jwplayer","KKBOX","KKTV","KakaoTalk","Kantv","Keep","KingSmith",
            "Kingsoft","KouDaiShiShang","Ku6","KuKeMusic","KuaiDi100","KuaiShou","KuangShi","KugouKuwo","LG","Lan",
            "LanZouYun","LastFM","LastPass","LeJu","LeTV","Lenovo","LiTV","LianMeng","Limelight","Line",
            "LineTV","Linguee","LinkedIn","Linux","LivePerson","Logitech","LondonReal","LuDaShi","LvMiLianChuang","MEGA",
            "MIUIPrivacy","MOMOShop","MOOV","Mail","Mailru","Majsoul","Manorama","Maocloud","Marketing","McDonalds",
            "MeWatch","MeiTu","MeiTuan","MeiZu","MiWu","Microsoft","MicrosoftEdge","Migu","MingLueZhaoHui","Mogujie",
            "Mojitianqi","Movefree","Mozilla","My5","NBC","NGA","NGAA","NTPService","NYPost","NYTimes",
            "NaSDDNS","Naver","NaverTV","NetEase","NetEaseMusic","Netflix","Niconico","Nike","Nikkei","Nintendo",
            "NivodTV","Notion","NowE","Npmjs","Nvidia","OKX","OP","OPPO","Olevod","OneDrive",
            "OnePlus","OpenAI","Opera","Oracle","Oreilly","Origin","OuPeng","Overcast","Overwatch","PBS",
            "PCCW","PChome","PChomeTW","PPTV","PSBC","Pandora","PandoraTV","ParamountPlus","Patreon","PayPal",
            "Peacock","Picacg","Picsee","PikPak","Pinduoduo","PingAn","Pinterest","Pixiv","Pixnet","PlayStation",
            "PotatoChat","PrimeVideo","Privacy","PrivateTracker","Protonmail","Proxy","ProxyLite","Pubmatic","Purikonejp","Python",
            "QiNiuYun","QingCloud","Qobuz","Qualcomm","QuickConnect","Qyyjt","RTHK","Rakuten","Rarbg","Razer",
            "Reabble","Reddit","RemoteDesktop","Riot","Rockstar","RuanMei","SFExpress","SMG","SMZDM","STUN",
            "Salesforce","Samsung","Scaleflex","Scholar","Sectigo","ShangHaiJuXiao","Shanling","Sharethis","ShenMa","ShiJiChaoXing",
            "ShiNongZhiKe","Shopee","Shopify","Sina","Siri","SkyGO","Slack","SlideShare","Sling","SmarTone",
            "Snap","Sohu","Sony","SouFang","SoundCloud","SourceForge","Spark","Speedtest","Spotify","Stackexchange",
            "StarCraftII","Starbucks","Steam","SteamCN","Stripe","SuNing","SublimeText","SuiShiChuanMei","Supercell","Synology",
            "SystemOTA","TCL","TIDAL","TVB","TVer","TaiKang","TaiWanGood","TaiheMusic","TapTap","TeamViewer",
            "Teambition","Teams","Telegram","TelegramNL","TelegramSG","TelegramUS","Tencent","TencentVideo","TeraBox","Tesla",
            "TestFlight","ThomsonReuters","Threads","TianTianKanKan","TianWeiChengXin","TianYaForum","TigerFintech","TikTok","Tmdb","TongCheng",
            "TrustWave","TruthSocial","Tumblr","Twitch","Twitter","U17","UBI","UC","UCloud","UKMedia",
            "UPYun","USMedia","Ubisoft","Ubuntu","Udacity","UnionPay","Unity","VISA","VK","VOA",
            "Vancl","Vercel","Verisign","Verizon","VidolTV","VikACG","Viki","Vimeo","VipShop","ViuTV",
            "Vivo","Voxmedia","W3schools","WIX","WanKaHuanJu","WanMeiShiJie","Wanfang","WangSuKeJi","WangXinKeJi","WeChat",
            "WeTV","WeType","WeiZhiYunDong","Weibo","WenJuanXing","Westerndigital","Whatsapp","WiFiMaster","Wikimedia","Wikipedia",
            "WildRift","WoLai","Wordpress","WorldofWarcraft","Wteam","Xbox","XiamiMusic","XianYu","XiaoGouKeJi","XiaoHongShu",
            "XiaoMi","XiaoYuanKeJi","XieCheng","XingKongWuXian","XueErSi","XueQiu","Xunlei","YYeTs","Yandex","YiChe",
            "YiXiaKeJi","YiZhiBo","YouMengChuangXiang","YouTube","YouTubeMusic","YouZan","Youku","YuanFuDao","YunFanJiaSu","ZDNS",
            "Zalo","Zee","ZeeTV","Zendesk","ZhangYue","ZhiYinManKe","ZhiYunZhong","Zhihu","ZhihuAds","ZhongGuoShiHua",
            "ZhongWeiShiJi","ZhongXingTongXun","ZhongYuanYiShang","ZhuanZhuan","Zoho","aiXcoder","eBay","friDay","iCloud","iCloudPrivateRelay",
            "iFlytek","iQIYI","iQIYIIntl","iTalkBB","ifanr","myTVSUPER","zhanqi",
        };
    }
}
