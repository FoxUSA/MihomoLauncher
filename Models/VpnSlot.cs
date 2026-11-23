using System;
using System.Xml.Serialization;

namespace MihomoLauncher.Models
{
    // A configurable VPN source tried during the stepped search (slots 2-5).
    // The launcher converts each slot into a mihomo proxy / proxy-provider.
    public enum VpnSlotKind
    {
        WireGuardConf,  // Value = path to a WireGuard *.conf file
        Vless,          // Value = vless://... share link
        ClashSub,       // Value = URL of a Clash/mihomo subscription
        Hysteria2,      // Value = hysteria2:// (or hy2://) share link
        ShareLink,      // Value = any other share link mihomo converts itself (vmess/trojan/ss/tuic/…),
                        // handed to the engine as a one-line file provider — see ShareLinks
    }

    public class VpnSlot
    {
        [XmlAttribute]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "";
        public VpnSlotKind Kind { get; set; } = VpnSlotKind.WireGuardConf;
        public string Value { get; set; } = "";
        // Always true since 2026-11-23: the «Использовать этот вариант при переборе» box is gone and
        // every configuration takes part (a rule says which ones it uses with its own numbers).
        // Kept so old config.xml files load; the loader turns a stored false into true
        // (AppConfigStore.NormalizeSlots) and nothing reads it to skip a configuration.
        public bool Enabled { get; set; } = true;

        // ClashSub only: how often (hours) mihomo re-downloads the subscription
        // (the proxy-provider `interval`). Default 48h; ignored for other kinds.
        public int UpdateIntervalHours { get; set; } = 48;

        // ClashSub only: the subscription node to ride, chosen by the user; "" = the one the
        // subscription's author put first. Nothing measures or switches it (2026-11-22).
        public string SubNode { get; set; } = "";
    }
}
