using System;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MihomoLauncher.Services
{
    // Access password (2026-11-12). Not encryption — a lock against the ordinary person at the
    // keyboard (the user's decision: «это защита от обычного пользователя»).
    //
    //   • No password set → everything works exactly as before, nothing is checked anywhere.
    //   • Password set → the window keeps only ▷ / ⟳ / ✕, tile launch and connect/disconnect;
    //     every way to see or change settings, VPNs, rules and logs asks for it first.
    //   • The data is bound to the place it was locked in (this PC + Windows user + this copy of
    //     the launcher). A DataUser carried to another copy or another PC does not start there
    //     without the password; with it, it re-binds to the new place and works.
    //
    // Only a salted PBKDF2 hash is stored, never the password itself.
    public static class AccessLock
    {
        private const int Iterations = 100000;
        // Unlocked stays unlocked this long after the last protected action, then locks again.
        private static readonly TimeSpan IdleRelock = TimeSpan.FromMinutes(10);

        private static bool _open;
        private static DateTime _lastUseUtc;

        public static bool IsSet(AppConfig cfg) => cfg != null && !string.IsNullOrEmpty(cfg.LockHash);

        // May the protected part of the UI be used right now?
        public static bool IsOpen(AppConfig cfg)
        {
            if (!IsSet(cfg)) return true;
            if (_open && DateTime.UtcNow - _lastUseUtc < IdleRelock) return true;
            _open = false;
            return false;
        }

        // A protected action just ran: the idle timer starts over.
        public static void Touch() { if (_open) _lastUseUtc = DateTime.UtcNow; }

        public static void Lock() { _open = false; }

        // Checks the password; on success the UI stays open until idle or Lock().
        public static bool TryOpen(AppConfig cfg, string password)
        {
            if (!IsSet(cfg)) return true;
            if (!Verify(cfg, password)) return false;
            _open = true;
            _lastUseUtc = DateTime.UtcNow;
            return true;
        }

        public static bool Verify(AppConfig cfg, string password)
        {
            if (!IsSet(cfg)) return true;
            try
            {
                var salt = Convert.FromBase64String(cfg.LockSalt ?? "");
                var want = Convert.FromBase64String(cfg.LockHash);
                var got = Derive(password ?? "", salt, want.Length);
                int diff = want.Length ^ got.Length;
                for (int i = 0; i < Math.Min(want.Length, got.Length); i++) diff |= want[i] ^ got[i];
                return diff == 0;
            }
            catch { return false; }
        }

        // Sets (or changes) the password and binds the data to this place.
        public static void Set(AppConfig cfg, string password)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            cfg.LockSalt = Convert.ToBase64String(salt);
            cfg.LockHash = Convert.ToBase64String(Derive(password ?? "", salt, 32));
            cfg.LockPlace = HerePlace();
            _open = true;
            _lastUseUtc = DateTime.UtcNow;
        }

        public static void Clear(AppConfig cfg)
        {
            cfg.LockHash = "";
            cfg.LockSalt = "";
            cfg.LockPlace = "";
        }

        // Is this data where it was locked? (Always true without a password.)
        public static bool BoundHere(AppConfig cfg) =>
            !IsSet(cfg) || string.Equals(cfg.LockPlace ?? "", HerePlace(), StringComparison.Ordinal);

        public static void BindHere(AppConfig cfg) { if (IsSet(cfg)) cfg.LockPlace = HerePlace(); }

        // This PC + this Windows user + this copy of the launcher, hashed.
        public static string HerePlace()
        {
            string machine = "", user = "", root = "";
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                          .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    machine = k?.GetValue("MachineGuid") as string ?? "";
            }
            catch { }
            if (machine.Length == 0) machine = Environment.MachineName;
            try { user = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { }
            if (user.Length == 0) user = Environment.UserName;
            try { root = (VersionInfo.InstallRoot ?? "").TrimEnd('\\').ToLowerInvariant(); } catch { }
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(machine + "|" + user + "|" + root)));
        }

        private static byte[] Derive(string password, byte[] salt, int length)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(length);
        }
    }
}
