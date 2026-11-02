using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MihomoLauncher.Services
{
    // One pickable blackmatrix7 service: its rule name, the Clash .yaml URL, and a
    // best-effort site to test availability with.
    public class BlackMatrixService
    {
        public string Name { get; set; }
        public string ClashUrl { get; set; }
        public string CheckUrl { get; set; }
    }

    // The blackmatrix7/ios_rule_script catalog: ready-made Clash rule lists. Mirrors the
    // service list used by awg-panel. Each service maps to a Clash .yaml URL and, where
    // known, a site to probe for availability (so the link tile gates on the real exit IP).
    public static class BlackMatrixCatalog
    {
        // rule/Clash/<Name>/<Name>.yaml
        public static string ClashUrlFor(string name)
        {
            var n = (name ?? "").Trim();
            return "https://raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/rule/Clash/"
                   + Uri.EscapeDataString(n) + "/" + Uri.EscapeDataString(n) + ".yaml";
        }

        // ---- byfox awg-panel folders source ----
        // https://byfox.dev/data/awg-panel/rules/folders.txt is a bare list of folder names; each
        // folder is a rule template AND (when it looks like a domain) its own availability site.
        // The Clash rule list for a folder lives at <base>/<folder>/clash/rules.yaml.
        public const string ByfoxRulesBase = "https://byfox.dev/data/awg-panel/rules/";

        public static string ByfoxClashUrlFor(string folder)
        {
            var f = (folder ?? "").Trim().Trim('/');
            return ByfoxRulesBase + Uri.EscapeDataString(f) + "/clash/rules.yaml";
        }

        // A folder that looks like a hostname (has a dot, no spaces) becomes its own check site.
        // Non-domain folders (e.g. "Block_ads_trackers_quic") get no availability probe.
        public static string ByfoxCheckUrlFor(string folder)
        {
            var f = (folder ?? "").Trim().Trim('/');
            if (f.Length == 0 || f.IndexOf(' ') >= 0 || f.IndexOf('.') < 0) return "";
            return "https://" + f.ToLowerInvariant() + "/";
        }

        // Turn one byfox folder line into a service row (null for blank/comment lines).
        public static BlackMatrixService ByfoxServiceFor(string folder)
        {
            var f = (folder ?? "").Trim();
            if (f.Length == 0 || f.StartsWith("#") || f.StartsWith("<!--") || f.StartsWith("-->"))
                return null;
            return new BlackMatrixService
            {
                Name = f,
                ClashUrl = ByfoxClashUrlFor(f),
                CheckUrl = ByfoxCheckUrlFor(f),
            };
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

        // The merged rule catalog lives in DataUser\rules (survives updates; an update wipes
        // Data\). Legacy blackmatrix7+www.txt copies (DataUser / Data / flat) are read as a
        // fallback until the next refresh writes the canonical DataUser\rules copy.
        public static string CatalogPath
        {
            get
            {
                var canonical = CanonicalCatalogPath;
                if (File.Exists(canonical)) return canonical;
                var root = VersionInfo.InstallRoot;
                foreach (var legacy in new[]
                {
                    Path.Combine(VersionInfo.DataUserDir, "blackmatrix7+www.txt"),
                    Path.Combine(root, "Data", "blackmatrix7+www.txt"),
                    Path.Combine(root, "blackmatrix7+www.txt"),
                })
                    if (File.Exists(legacy)) return legacy;
                return canonical;   // canonical write location even when it doesn't exist yet
            }
        }

        // Where a fresh merge is written (always DataUser\rules, so it survives updates).
        public static string CanonicalCatalogPath =>
            Path.Combine(VersionInfo.DataUserDir, "rules");

        // Built once and reused — ~700 services, so rebuilding on every picker open /
        // template load is wasted work. Prefers the downloaded blackmatrix7+www.txt (name +
        // curated check URL straight from the file); falls back to the built-in list below.
        private static IReadOnlyList<BlackMatrixService> _cache;
        public static IReadOnlyList<BlackMatrixService> All()
        {
            if (_cache != null) return _cache;

            var fromFile = LoadFromFile();
            if (fromFile != null && fromFile.Count > 0) return _cache = fromFile;

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

        // Drop the cached list so the next All() re-reads the (freshly downloaded) file.
        public static void Reset() { _cache = null; }

        // Parse Data\blackmatrix7+www.txt into the service list. Returns null when the file
        // is missing/unreadable so callers fall back to the built-in list. Format: a leading
        // <!-- ... --> comment block (carrying the date + example URLs), then one
        // "Name|CheckUrl" per line (CheckUrl may be empty for rule categories without a site).
        private static IReadOnlyList<BlackMatrixService> LoadFromFile()
        {
            try
            {
                var path = CatalogPath;
                if (!File.Exists(path)) return null;
                return ParseCatalog(File.ReadAllText(path));
            }
            catch { return null; }
        }

        // Parse raw file text into services (deduped case-insensitively, sorted). Public so
        // the updater can validate downloaded content before replacing the local file. Handles
        // both the legacy 2-column blackmatrix7 format ("Name|CheckUrl") and the merged
        // 3-column DataUser\rules format ("Name|CheckUrl|ClashUrl"). When the ClashUrl column
        // is absent/empty the URL is derived from the name (blackmatrix7 github convention).
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
                    ClashUrl = clash.Length > 0 ? clash : ClashUrlFor(name),
                    CheckUrl = check,   // authoritative — empty means "no availability probe"
                });
            }

            if (list.Count == 0) return null;
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // Build the merged DataUser\rules text from the two upstream sources: the blackmatrix7
        // "Name|CheckUrl" list and the byfox "folders.txt" folder list. blackmatrix7 rows win on
        // a name clash (they carry curated check URLs); byfox rows carry an explicit ClashUrl.
        // `dateHeader` is the dd.MM.yyyy stamp written on the first line (the catalog version).
        // Returns null if BOTH sources are empty (so the caller keeps the old file).
        public static string BuildMergedContent(string blackmatrixContent, IEnumerable<string> byfoxFolders, string dateHeader)
        {
            var rows = new List<BlackMatrixService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var bm = ParseCatalog(blackmatrixContent);
            if (bm != null)
                foreach (var s in bm)
                    if (seen.Add(s.Name)) rows.Add(s);

            if (byfoxFolders != null)
                foreach (var folder in byfoxFolders)
                {
                    var svc = ByfoxServiceFor(folder);
                    if (svc != null && seen.Add(svc.Name)) rows.Add(svc);
                }

            if (rows.Count == 0) return null;
            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var sb = new System.Text.StringBuilder();
            sb.Append("<!--").Append((dateHeader ?? "").Trim()).Append('\n');
            sb.Append("# Merged rule catalog — blackmatrix7 (byfox.dev/data/maxmind/blackmatrix7+www.txt)\n");
            sb.Append("# + byfox awg-panel folders (byfox.dev/data/awg-panel/rules/folders.txt)\n");
            sb.Append("# Format: Name|CheckUrl|ClashUrl  (ClashUrl empty => derived from name)\n");
            sb.Append("-->\n");
            foreach (var s in rows)
            {
                // Leave the ClashUrl column empty when it's the derivable blackmatrix7 default
                // (keeps the file compact); write it explicitly for byfox / any custom URL.
                var derived = ClashUrlFor(s.Name);
                var clash = string.Equals(s.ClashUrl ?? "", derived, StringComparison.OrdinalIgnoreCase) ? "" : (s.ClashUrl ?? "");
                sb.Append(s.Name).Append('|').Append(s.CheckUrl ?? "").Append('|').Append(clash).Append('\n');
            }
            return sb.ToString();
        }

        // Read the service-list date from the file's first line, e.g. "<!--10.06.2026" or
        // "<!--10.06.2026-->", and from a bare "dd.MM.yyyy" (the manifest's catalogDate).
        // Returns null on anything unparseable.
        public static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            var lt = t.IndexOf("<!--", StringComparison.Ordinal);
            if (lt >= 0) t = t.Substring(lt + 4).Trim();

            // Keep only the leading dd.MM.yyyy token (digits and dots).
            var sb = new System.Text.StringBuilder();
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
