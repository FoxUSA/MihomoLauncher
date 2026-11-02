using System;
using System.Collections.Generic;

namespace MihomoLauncher.Services
{
    public enum SlotState
    {
        Unknown,   // never checked this session — behaves exactly as before
        Alive,     // answered a connectivity probe through the node
        Dead,      // the engine got nothing back from it
    }

    // Last known "does this variant answer at all", per VPN slot, for the current session.
    //
    // Written by the preflight the connect flow runs before its sweep (and refreshed by the
    // sweep itself, which learns the same thing first-hand); read by the connect ordering and
    // by the slot list in Settings.
    //
    // In memory only, and deliberately: this is an observation about right now, not a setting.
    // A "dead" written to disk would outlive the outage that produced it and quietly demote a
    // perfectly healthy variant on the next launch — the exact failure this whole feature is
    // supposed to prevent, only harder to notice.
    public static class SlotHealth
    {
        private class Entry
        {
            public SlotState State;
            public DateTime When;   // local time, for the "checked at" hint in Settings
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _map =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static void Set(string slotId, bool alive)
        {
            if (string.IsNullOrEmpty(slotId)) return;
            lock (_lock)
                _map[slotId] = new Entry { State = alive ? SlotState.Alive : SlotState.Dead, When = DateTime.Now };
        }

        public static SlotState Get(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return SlotState.Unknown;
            lock (_lock)
            {
                Entry e;
                return _map.TryGetValue(slotId, out e) ? e.State : SlotState.Unknown;
            }
        }

        // When the state above was observed — null if this slot was never checked.
        public static DateTime? CheckedAt(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return null;
            lock (_lock)
            {
                Entry e;
                return _map.TryGetValue(slotId, out e) ? e.When : (DateTime?)null;
            }
        }

        public static void Clear()
        {
            lock (_lock) _map.Clear();
        }
    }
}
