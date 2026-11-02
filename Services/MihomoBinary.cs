using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MihomoLauncher.Services
{
    // Asks the engine binary what version it is, instead of trusting bookkeeping.
    //
    // The installed version used to be remembered in updates.json and written after each
    // in-place update. Any path that changes the exe without going through that write — a
    // folder copied from another machine, a manually dropped binary, a restored backup — left
    // the record stale, and the launcher then offered an "update" to a version already on
    // disk. The binary itself is the only source that can't drift.
    public static class MihomoBinary
    {
        // `mihomo -v` prints e.g.
        //   Mihomo Meta v1.19.29 windows amd64 with go1.26.5 Sat Jul 18 12:22:01 UTC 2026
        private static readonly Regex VersionRegex = new Regex(@"v(\d+\.\d+\.\d+)", RegexOptions.Compiled);

        private static readonly object _lock = new object();
        private static string _cacheKey = "";
        private static string _cacheVersion = "";

        // Version string ("1.19.29") reported by the exe, or "" if it can't be read. Cached on
        // the file's identity (path + size + write time), so replacing the binary invalidates
        // it by itself and the common case never spawns a process.
        public static string VersionOf(string exePath)
        {
            var path = (exePath ?? "").Trim();
            if (path.Length == 0) return "";

            string key;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return "";
                key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { return ""; }

            lock (_lock)
            {
                if (key == _cacheKey) return _cacheVersion;
            }

            var version = Probe(path);

            lock (_lock)
            {
                _cacheKey = key;
                _cacheVersion = version;
            }
            return version;
        }

        // Will the engine take this config? `mihomo -t` parses it exactly as a start or a
        // reload would and exits 1 on anything it would refuse — a bad address, a broken rule,
        // a malformed proxy, whatever the next unforeseen value turns out to be. ~50 ms.
        //   true  = accepted
        //   false = refused, `reason` is the engine's own sentence
        //   null  = could not ask (no binary, hung, crashed) — callers go ahead as before:
        //           our inability to check must never be what stops a config being written.
        public static bool? TestConfig(string exePath, string configPath, string testDir, out string reason)
        {
            reason = "";
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
                Directory.CreateDirectory(testDir);
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    // Its own empty home, never the live work dir: the running engine holds its
                    // cache database there, and a test has no business touching it.
                    Arguments = "-t -d \"" + testDir + "\" -f \"" + configPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var so = p.StandardOutput.ReadToEndAsync();
                    var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    p.WaitForExit();                 // let the async readers drain
                    if (p.ExitCode == 0) return true;

                    var text = (so.Result ?? "") + "\n" + (se.Result ?? "");
                    var m = Regex.Match(text, @"level=error msg=""((?:\\.|[^""\\])*)""");
                    reason = m.Success
                        ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
                        : (text.Trim().Split('\n').LastOrDefault() ?? "").Trim();
                    if (reason.Length > 300) reason = reason.Substring(0, 300) + "…";
                    return false;
                }
            }
            catch (Exception ex)
            {
                try { Log.Error("mihomo -t", ex); } catch { }
                return null;
            }
        }

        private static string Probe(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-v",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "";
                    // Read before waiting so a chatty child can't deadlock on a full pipe.
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    var text = (so ?? "") + "\n" + (se ?? "");
                    var m = VersionRegex.Match(text);
                    return m.Success ? m.Groups[1].Value : "";
                }
            }
            catch (Exception ex)
            {
                try { Log.Error("mihomo -v", ex); } catch { }
                return "";
            }
        }
    }
}
