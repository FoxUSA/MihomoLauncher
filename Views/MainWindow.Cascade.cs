using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MihomoLauncher.Models;
using MihomoLauncher.Services;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MihomoLauncher.Views
{
    // «Каскадный VPN» on the main window (2026-11-27, agreed on the mock-ups .dev\mockups\cascade-*.html).
    //
    // A tile with the switch on rides its ticked configurations one inside the other (Services.Cascade,
    // MihomoConfigBuilder ChainSep). Its plate shows flags instead of a name — your country › hop 1 ›
    // hop 2 › hop 3, nothing written — and the words go to the bottom plate of the window.
    //
    // Connecting: ONE config holding every prefix of the chain, ONE reload, then the hops checked in
    // turn through the engine's API (hop 1; 1→2; 1→2→3). The hop being checked turns like a coin on
    // the plate; each gets up to three tries, repeated only while it is silent — a hop that answered
    // «no» is not asked again. A hop that fails goes grey and the bottom plate says which and why; the
    // auto-retry (90 s, doubling to an hour, never giving up) comes back to THAT hop — the ones before
    // it are not checked again (2026-11-27: «весь круг повторять не надо»).
    public partial class MainWindow
    {
        private const int CascadeTriesPerHop = 3;

        // rule id → the hop being checked now (1-based), while a cascade connects.
        private readonly Dictionary<string, int> _cascadeSpin = new Dictionary<string, int>();
        // rule id → the hop that failed (1-based); 0 = every hop up but the check site did not open.
        private readonly Dictionary<string, int> _cascadeFail = new Dictionary<string, int>();
        // rule id → the hop the next try starts at (1-based).
        private readonly Dictionary<string, int> _cascadeResume = new Dictionary<string, int>();
        // rule id → the bottom plate's line about it.
        private readonly Dictionary<string, string> _cascadeWhy = new Dictionary<string, string>();
        // A cascade tile sent through ONE VPN from its plate this session (the pick changes nothing
        // in the rule, like any pick on the plate); the cascade row of the menu brings the chain back.
        private readonly HashSet<string> _cascadeSingle = new HashSet<string>();

        // The rule's routing changed (editor, Routing table, Settings): what this session learned
        // about its chain no longer applies.
        private void ForgetCascadeState(string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId)) return;
            _cascadeSpin.Remove(ruleId);
            _cascadeFail.Remove(ruleId);
            _cascadeResume.Remove(ruleId);
            _cascadeWhy.Remove(ruleId);
            _cascadeSingle.Remove(ruleId);
        }

        private bool UsesCascade(AppEntry e)
            => e != null && !_cascadeSingle.Contains(e.Id) && Services.Cascade.IsOn(e, _config.VpnSlots);

        // The VPN a plate pick made this tile ride this session, when that is how it rides — for
        // the auto-retry, which otherwise goes back to the rule's own settings.
        private string CascadeSingleSlot(AppEntry e)
            => e != null && _cascadeSingle.Contains(e.Id) && _platePick.TryGetValue(e.Id, out var id) ? id : null;

        // The proxy a target exits through: the last hop of a cascade, else itself.
        private static string ExitProxy(string target)
            => MihomoConfigBuilder.IsChain(target) ? MihomoConfigBuilder.ChainHops(target).LastOrDefault() : target;

        // ---------- connect ----------

        // Connects ONE cascade tile; true when it rides its chain. Writes the config with the chain's
        // prefixes and the launcher's check through the whole chain; the caller writes the final one.
        private async Task<bool> ConnectCascadeAsync(AppEntry entry, CancellationToken ct)
        {
            var slots = (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList();
            var hops = Services.Cascade.Hops(entry, slots);
            int n = hops.Count;
            if (n < 2) return false;

            _cascadeFail.Remove(entry.Id);
            _cascadeWhy.Remove(entry.Id);
            int start;
            if (!_cascadeResume.TryGetValue(entry.Id, out start) || start < 1 || start > n) start = 1;
            _cascadeSpin[entry.Id] = start;
            RefreshCascadeUi();

            try
            {
                // Every hop has to be readable before anything is written.
                var parsed = new List<ParsedProxy>();
                for (int i = 0; i < n; i++)
                {
                    int number = (_config.VpnSlots?.IndexOf(hops[i]) ?? -1) + 1;
                    var p = await ParseSlotAsync(hops[i], number, ct).ConfigureAwait(true);
                    if (!p.Ok)
                    {
                        CascadeFailed(entry, i + 1, hops[i], Loc.T("Main_CascadeWhyUnreadable", p.Error));
                        return false;
                    }
                    _proxySlot[p.ProxyName] = new ProxySlotInfo { Id = hops[i].Id, Number = number, Name = hops[i].Name, Server = p.Server };
                    _parsedProxy[p.ProxyName] = p;
                    parsed.Add(p);
                }
                var names = parsed.Select(p => p.ProxyName).ToList();
                var target = MihomoConfigBuilder.ChainTarget(names);
                var nicKey = MihomoConfigBuilder.NicKey(entry);

                var proxies = CurrentAssignedProxies();
                foreach (var p in parsed)
                    if (!proxies.Any(x => x.ProxyName == p.ProxyName)) proxies.Add(p);
                var vpnApps = _config.Apps.Where(a => a.RequiresVpn).ToList();
                var rules = BuildVpnRules(vpnApps, _assignment, new List<AppEntry> { entry }, target);
                rules.Insert(0, new RuleDef("PROCESS-NAME", LauncherProcessName(), target) { Raw = true, Probe = true, Nic = nicKey });
                _mihomo.WriteConfig(MihomoConfigBuilder.Build(_config, proxies, rules,
                    new[] { new KeyValuePair<string, string>(target, nicKey) }));
                if (!await _mihomo.ReloadAsync(ct, "каскад «" + entry.Name + "»").ConfigureAwait(true))
                {
                    CascadeFailed(entry, start, hops[start - 1], Loc.T("Main_CascadeWhyApply"));
                    return false;
                }

                for (int k = start; k <= n; k++)
                {
                    if (ct.IsCancellationRequested || !entry.AutoConnect) return false;
                    _cascadeSpin[entry.Id] = k;
                    RefreshCascadeUi();   // the turning flag says it — no line in the bottom plate

                    var node = MihomoConfigBuilder.ChainNodeName(MihomoConfigBuilder.ChainTarget(names.Take(k)), nicKey);
                    var res = await CheckHopAsync(node, ct).ConfigureAwait(true);
                    if (ct.IsCancellationRequested || !entry.AutoConnect) return false;
                    if (res == MihomoController.NodeCheck.Up)
                    {
                        if (k == 1 && nicKey == null) SlotHealth.Set(hops[0].Id, true);
                        continue;
                    }

                    // Which hop is it really? A hop further down can't answer through one that has
                    // died since it was last seen — so the ones before are asked once, nearest first.
                    int bad = k;
                    while (bad > 1)
                    {
                        var before = MihomoConfigBuilder.ChainNodeName(MihomoConfigBuilder.ChainTarget(names.Take(bad - 1)), nicKey);
                        if (await _mihomo.CheckNodeAsync(before, ct).ConfigureAwait(true) == MihomoController.NodeCheck.Up) break;
                        bad--;
                        res = MihomoController.NodeCheck.Silent;
                    }
                    if (bad == 1 && nicKey == null) SlotHealth.Set(hops[0].Id, false);
                    CascadeFailed(entry, bad, hops[bad - 1], res == MihomoController.NodeCheck.Refused
                        ? Loc.T("Main_CascadeWhyRefused") : Loc.T("Main_CascadeWhySilent"));
                    return false;
                }

                // The whole chain is up: the rule's own check, through all of it.
                var r = await ProbeAppsAsync(new List<AppEntry> { entry }, null, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || !entry.AutoConnect) return false;
                if (!(r.TryGetValue(entry.Id, out var ok) && ok))
                {
                    CascadeFailed(entry, 0, null, null);
                    _cascadeResume[entry.Id] = n;
                    return false;
                }

                _assignment[entry.Id] = target;
                _workingApps.Add(entry.Id);
                _cascadeResume.Remove(entry.Id);
                Log.Info("Каскад «" + entry.Name + "» собран: " + string.Join(" › ", hops.Select(h => h.Name)) + ".");
                return true;
            }
            finally
            {
                _cascadeSpin.Remove(entry.Id);
                RefreshCascadeUi();
            }
        }

        // Up to CascadeTriesPerHop tries — again only while the hop stays silent.
        private async Task<MihomoController.NodeCheck> CheckHopAsync(string node, CancellationToken ct)
        {
            var res = MihomoController.NodeCheck.Silent;
            for (int t = 1; t <= CascadeTriesPerHop; t++)
            {
                res = await _mihomo.CheckNodeAsync(node, ct).ConfigureAwait(true);
                if (res != MihomoController.NodeCheck.Silent || ct.IsCancellationRequested) return res;
                if (t < CascadeTriesPerHop)
                {
                    try { await Task.Delay(1000, ct).ConfigureAwait(true); }
                    catch (OperationCanceledException) { return res; }
                }
            }
            return res;
        }

        // hop = 1-based; 0 = the chain is up, the check site isn't. why = null for that case.
        private void CascadeFailed(AppEntry entry, int hop, VpnSlot slot, string why)
        {
            _cascadeFail[entry.Id] = hop;
            if (hop > 0) _cascadeResume[entry.Id] = hop;
            var line = hop > 0
                ? Loc.T("Main_CascadeHopFailed", entry.Name, hop, slot?.Name ?? "?", why)
                : Loc.T("Main_CascadeSiteFailed", entry.Name);
            _cascadeWhy[entry.Id] = line;
            Log.Info("Каскад: " + line);
            RefreshCascadeUi();
        }

        // ---------- the bottom plate's line ----------

        private TextBlock _cascadeLine;
        private bool CascadeLineOn => _cascadeLine != null && _cascadeLine.Visibility == Visibility.Visible;

        // The tiles' flags and the line under them, without waiting for anything else.
        private void RefreshCascadeUi()
        {
            try
            {
                RebuildTiles();
                RenderCascadeLine();
            }
            catch (Exception ex) { Log.Error("cascade-ui", ex); }
        }

        // The bottom plate says nothing about a cascade that is still being connected (2026-11-27:
        // «не надо гасить и писать уведомление» — the launcher keeps at it, the turning flag is the
        // whole message; why a hop failed goes to the log and the plate's tooltip). Kept as the one
        // place such a line would be drawn, so it can't come back by accident elsewhere.
        private void RenderCascadeLine()
        {
            if (StatsLines == null) return;
            var lines = new List<string>();
            if (_cascadeLine == null)
            {
                if (lines.Count == 0) return;
                _cascadeLine = new TextBlock
                {
                    FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Theme.AccentRed,
                    TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                };
                StatsLines.Children.Insert(0, _cascadeLine);
            }
            var text = string.Join("\n", lines);
            var vis = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_cascadeLine.Text == text && _cascadeLine.Visibility == vis) return;
            _cascadeLine.Text = text;
            _cascadeLine.Visibility = vis;
            RenderStats();
        }

        // ---------- the hop boxes and the globe ----------

        // Data\flags holds only the globe at the end of the chain (site-<px>.png, one per pixel size
        // FlagMinPx..FlagMaxPx). The country flags it used to hold went 2026-11-27 — the hops are
        // letters now (CountryChip): flags at 13–20 DIP stayed blurry whatever the set.
        private const int FlagMinPx = 12, FlagMaxPx = 48;

        private static string FlagDir => Path.Combine(AppContext.BaseDirectory, "Data", "flags");

        // The pixel size an icon of `dip` device-independent units takes at this scale.
        internal static int FlagPx(double dip, double scale)
            => Math.Max(FlagMinPx, Math.Min(FlagMaxPx, (int)Math.Round(dip * (scale > 0 ? scale : 1))));

        // A country as its two letters in an outlined box (2026-11-27: flags at 13–20 DIP stayed
        // blurry whatever the set, «может буквами писать?» → mockup .dev\mockups\cascade-letters.html,
        // variant 4). `h` = the box's height in DIP; width h + 1; «?» when the country isn't known.
        // The outline is one device pixel per whole scale step, so it stays crisp (as Chrome draws it).
        internal static FrameworkElement CountryChip(string iso, double h, bool grey, double scale)
        {
            if (scale <= 0) scale = 1;
            iso = (iso ?? "").Trim().ToUpperInvariant();
            double line = Math.Max(1, Math.Floor(scale)) / scale;
            return new Border
            {
                Width = h + 1, Height = h,
                BorderThickness = new Thickness(line),
                BorderBrush = Theme.TextMuted,
                CornerRadius = new CornerRadius(Math.Max(2, h * 0.19)),
                SnapsToDevicePixels = true, UseLayoutRounding = true,
                Child = new TextBlock
                {
                    Text = iso.Length > 0 ? iso : "?",
                    FontSize = Math.Round(h * 0.6 * 2) / 2,
                    FontWeight = FontWeights.Bold,
                    Foreground = grey ? Theme.TextMuted : Theme.TextPrimary,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0),
                },
            };
        }

        // The end of the chain — «the site» — as the globe icon (
        // 2026-11-27), pre-scaled to every pixel size like the flags (flags\site-<px>.png), so it is
        // exactly a flag's height and drawn 1:1.
        private static readonly Dictionary<int, BitmapSource> _siteIcons = new Dictionary<int, BitmapSource>();

        internal static FrameworkElement SiteVisual(double dip, double scale)
        {
            if (scale <= 0) scale = 1;
            int px = FlagPx(dip, scale);
            double size = px / scale;
            BitmapSource img;
            if (!_siteIcons.TryGetValue(px, out img))
            {
                img = null;
                try
                {
                    var path = Path.Combine(FlagDir, "site-" + px + ".png");
                    if (File.Exists(path))
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.UriSource = new Uri(path, UriKind.Absolute);
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        img = b;
                    }
                }
                catch (Exception ex) { Log.Error("site-icon", ex); }
                _siteIcons[px] = img;
            }
            if (img == null)
                return new TextBlock { Text = "\U0001F310", FontSize = size * 0.85, VerticalAlignment = VerticalAlignment.Center };
            var im = new Image { Source = img, Width = size, Height = size, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.NearestNeighbor);
            return im;
        }

        private double ScreenScale()
        {
            try { return VisualTreeHelper.GetDpi(this).DpiScaleX; }
            catch { return 1; }
        }

        // Where the tile is from: the country of the address its card shows (the stats plate's
        // own lookup) — the tile's card, else (no tile: a rule being added) the one Windows uses.
        // The same card the plate uses, so the editor's chain starts with the plate's first flag
        // (2026-11-27: the editor took the first card it found — Ethernet/Turkey for a Wi-Fi tile).
        private string UserIso(AppEntry e)
        {
            try
            {
                var card = e != null ? NetCards.CurrentOf(e) : NetCards.DefaultName(NetCards.List());
                NicIpLookup.Result r;
                if (card != null && _nicIp.TryGetValue(card, out r) && r.Ok && !string.IsNullOrEmpty(r.CountryCode)) return r.CountryCode;
                var any = _nicIp.Values.FirstOrDefault(x => x != null && x.Ok && !string.IsNullOrEmpty(x.CountryCode));
                return any?.CountryCode ?? "";
            }
            catch { return ""; }
        }

        // The person's own badge and place for the rule editor's «Вы» (2026-11-27): the same card
        // as UserIso — the tile's, else the one Windows uses — ISO and «Город, Страна» as the speed
        // window writes them (PlaceOf). ("", "") — not known yet.
        internal Tuple<string, string> UserGeo(AppEntry e)
        {
            try
            {
                var card = e != null ? NetCards.CurrentOf(e) : NetCards.DefaultName(NetCards.List());
                NicIpLookup.Result r;
                if (card == null || !_nicIp.TryGetValue(card, out r) || r == null || !r.Ok)
                    r = _nicIp.Values.FirstOrDefault(x => x != null && x.Ok && !string.IsNullOrEmpty(x.CountryCode));
                if (r == null) return Tuple.Create("", "");
                return Tuple.Create(r.CountryCode ?? "", PlaceOf(r) ?? "");
            }
            catch { return Tuple.Create("", ""); }
        }

        // slot id → ISO country of its server (ResolveSlotGeosAsync); "" for a subscription.
        private readonly Dictionary<string, string> _slotIso = new Dictionary<string, string>();

        private string SlotIso(string slotId) => slotId != null && _slotIso.TryGetValue(slotId, out var iso) ? iso : "";

        // One hop: its country's letters in an outlined box (CountryChip), `size` = the box's height.
        private FrameworkElement FlagBox(string iso, double size, bool grey, double opacity, string tip)
        {
            var inner = CountryChip(iso, size, grey, ScreenScale());
            var box = new Border
            {
                Width = inner.Width, Height = inner.Height, Child = inner, Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            if (!string.IsNullOrEmpty(tip)) box.ToolTip = tip;
            return box;
        }

        // The coin: the flag flips round its vertical axis for as long as it is shown.
        private static void Spin(FrameworkElement el)
        {
            var st = new ScaleTransform(1, 1);
            el.RenderTransform = st;
            var a = new DoubleAnimation(1, -1, TimeSpan.FromMilliseconds(550))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        }

        // The flags of a cascade tile: you › hop 1 › hop 2 › hop 3.
        //   spin  — the hop being connected (1-based), 0 = none; hops after it are still grey. A hop
        //           that didn't answer keeps turning while the launcher keeps connecting it
        //           (2026-11-27: «крути флагом пока соединяешь, не надо его гасить»).
        //   dim   — the tile is switched off: the flags a little faded.
        //   left  — flush left (the plate menu's row), else centred (the plate).
        //   maxSize / arrowW — the largest flag and the arrow's width, in DIP.
        private FrameworkElement CascadeFlags(AppEntry e, double room, int spin, bool dim, bool left = false,
                                              double maxSize = 15, double arrowW = 7)
        {
            var hops = Services.Cascade.Hops(e, (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList());
            int count = hops.Count + 1;
            // size = a letter box's height in DIP (width size + 1): the largest whole size, at most
            // maxSize, whose row fits `room` (2026-11-27: a row 2 px wider than the plate got its ends
            // clipped). On the plate: 4 boxes 16 high (17 wide) + 3 arrows = 86 of 88.
            double size = Math.Floor(Math.Min(maxSize, (room - arrowW * (count - 1)) / count - 1));
            if (size < 10) size = 10;
            const int fail = 0;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                UseLayoutRounding = true, SnapsToDevicePixels = true,
            };
            var userIso = UserIso(e);
            // Switched off (▷ in the corner): every flag grey and faded — nothing runs (2026-11-27:
            // «флаги яркие, глуши их, впн не запущен»).
            const double offOpacity = 0.45;
            row.Children.Add(FlagBox(userIso, size, dim, dim ? offOpacity : 1, Loc.T("Main_CascadeYou")));
            for (int i = 0; i < hops.Count; i++)
            {
                int hop = i + 1;
                row.Children.Add(new TextBlock
                {
                    Text = "›", FontSize = 10, Width = arrowW, TextAlignment = TextAlignment.Center,
                    Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -2, 0, 0),
                });
                bool waiting = spin > 0 && hop > spin;
                bool failed = fail > 0 && hop == fail;
                var geo = SlotGeo(hops[i].Id);
                var tip = hop + " · " + hops[i].Name + (string.IsNullOrEmpty(geo) ? "" : " — " + geo);
                var flag = FlagBox(SlotIso(hops[i].Id), size, waiting || failed || dim,
                    failed ? 0.45 : waiting ? 0.4 : dim ? offOpacity : 1, tip);
                if (spin == hop) Spin(flag);
                row.Children.Add(flag);
            }
            return row;
        }

        // The whole chain in words, for the plate's tooltip.
        private string CascadeTip(AppEntry e)
        {
            var hops = Services.Cascade.Hops(e, (_config.VpnSlots ?? new List<VpnSlot>()).Where(s => s != null).ToList());
            return Loc.T("Main_CascadeYou") + " › " + string.Join(" › ", hops.Select(h =>
            {
                var geo = SlotGeo(h.Id);
                return h.Name + (string.IsNullOrEmpty(geo) ? "" : " (" + geo + ")");
            }));
        }
    }
}
