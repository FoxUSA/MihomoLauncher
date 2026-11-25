using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MihomoLauncher.Services
{
    // What became of a card a tile chose that is no longer in the system (2026-11-24).
    public enum NicGone { Unknown, Ejected, Removed }

    // Every physical card the launcher has seen, by its Windows interface GUID, with every name it
    // had (2026-11-24: «TX20E» was renamed «Asus RT-BE88U» in Windows — the tiles store the name
    // the engine needs, lost the card and said «нет карты TX20E»). With the GUID a renamed card is
    // found again; with its PnP id Windows says whether the device was pulled out (it stays known
    // as «not connected») or removed from the system altogether.
    //
    // DataUser\nic-history.txt, one card per line: guid \t pnp-id \t wireless(0/1) \t names… (latest first).
    public static class NicHistory
    {
        private sealed class Entry
        {
            public string Guid = "", PnpId = "";
            public bool Wireless;
            public List<string> Names = new List<string>();
        }

        private static readonly object _lock = new object();
        private static List<Entry> _entries;

        private static string FilePath => Path.Combine(VersionInfo.DataUserDir, "nic-history.txt");

        private static List<Entry> Load()
        {
            if (_entries != null) return _entries;
            var list = new List<Entry>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var p = line.Split('\t');
                        if (p.Length < 4 || p[0].Length == 0) continue;
                        list.Add(new Entry
                        {
                            Guid = p[0], PnpId = p[1], Wireless = p[2] == "1",
                            Names = p.Skip(3).Where(n => n.Length > 0).ToList(),
                        });
                    }
            }
            catch (Exception ex) { Log.Error("nic-history-load", ex); }
            _entries = list;
            return list;
        }

        // The cards present now: their GUID, device and name are remembered (written only on change).
        public static void Note(IEnumerable<NetCard> cards)
        {
            lock (_lock)
            {
                var list = Load();
                bool changed = false;
                foreach (var c in cards ?? Enumerable.Empty<NetCard>())
                {
                    if (c == null || !c.Present || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Name)) continue;
                    var e = list.FirstOrDefault(x => string.Equals(x.Guid, c.Id, StringComparison.OrdinalIgnoreCase));
                    if (e == null) { e = new Entry { Guid = c.Id }; list.Add(e); changed = true; }
                    if (!string.IsNullOrEmpty(c.PnpId) && e.PnpId != c.PnpId) { e.PnpId = c.PnpId; changed = true; }
                    if (e.Wireless != c.Wireless) { e.Wireless = c.Wireless; changed = true; }
                    if (e.Names.Count == 0 || !string.Equals(e.Names[0], c.Name, StringComparison.Ordinal))
                    {
                        e.Names.RemoveAll(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase));
                        e.Names.Insert(0, c.Name);
                        changed = true;
                    }
                }
                if (!changed) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllLines(FilePath, list.Select(e => e.Guid + "\t" + e.PnpId + "\t" + (e.Wireless ? "1" : "0") + "\t"
                                                                + string.Join("\t", e.Names)), new UTF8Encoding(false));
                }
                catch (Exception ex) { Log.Error("nic-history-save", ex); }
            }
        }

        // The card that once carried this name — the latest one to have it.
        private static Entry ByName(string name)
        {
            lock (_lock)
            {
                var list = Load();
                return list.FirstOrDefault(e => e.Names.Count > 0 && string.Equals(e.Names[0], name, StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(e => e.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        // A card gone under this name but present under another one: its name now, else null.
        public static string RenamedTo(string oldName, IEnumerable<NetCard> present)
        {
            var e = ByName(oldName);
            if (e == null) return null;
            var now = (present ?? Enumerable.Empty<NetCard>())
                .FirstOrDefault(c => c.Present && string.Equals(c.Id, e.Guid, StringComparison.OrdinalIgnoreCase));
            return now != null && !string.Equals(now.Name, oldName, StringComparison.OrdinalIgnoreCase) ? now.Name : null;
        }

        public static bool? WasWireless(string name) => ByName(name)?.Wireless;

        // What Windows says about the device behind a card that is gone: pulled out (or prepared for
        // safe removal) — Ejected; not known to Windows at all any more — Removed; never seen — Unknown.
        public static NicGone WhatHappened(string name)
        {
            var e = ByName(name);
            if (e == null || string.IsNullOrEmpty(e.PnpId)) return NicGone.Unknown;
            try
            {
                uint inst;
                if (CM_Locate_DevNodeW(out inst, e.PnpId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
                {
                    uint status, problem;
                    if (CM_Get_DevNode_Status(out status, out problem, inst, 0) == CR_SUCCESS && problem == CM_PROB_HELD_FOR_EJECT)
                        return NicGone.Ejected;
                    return NicGone.Unknown;   // the device is there, its network card is not — can't say why
                }
                if (CM_Locate_DevNodeW(out inst, e.PnpId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS)
                    return NicGone.Ejected;   // known to Windows, not connected
                return NicGone.Removed;
            }
            catch { return NicGone.Unknown; }
        }

        private const int CR_SUCCESS = 0;
        private const uint CM_LOCATE_DEVNODE_NORMAL = 0, CM_LOCATE_DEVNODE_PHANTOM = 1;
        private const uint CM_PROB_HELD_FOR_EJECT = 47;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);
    }
}
