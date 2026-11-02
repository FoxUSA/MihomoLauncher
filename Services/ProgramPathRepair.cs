using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    // Programs that keep each version in its own folder (Squirrel-style "app-1.15.0" →
    // "app-1.16.0", Store packages "Name_1.2.3.0_x64__hash", "Tool 2024.1"…) leave a rule
    // pointing at a path that is gone after an update — or, when the old folder is kept, at
    // the OLD version. The tile then can't launch it, and worse, the firewall kill-switch
    // (which is keyed on the full exe path) protects a file nobody runs any more.
    //
    // Repair: for every path segment (the file name included) that contains a number, look
    // at its siblings with the same shape — same text once the numbers are taken out — that
    // contain the same rest of the path, and take the newest version. Nothing else is guessed:
    // a program that was really uninstalled stays as it is.
    public static class ProgramPathRepair
    {
        // "app-1.15.0" → "app-#", "Gemini 1.15" → "Gemini #": the shape that must match.
        private static readonly Regex VersionRun = new Regex(@"\d+(?:[._]\d+)*", RegexOptions.Compiled);

        private static readonly Regex LooksLikeVersion = new Regex(@"\d+[._]\d+", RegexOptions.Compiled);

        private static string Shape(string segment) => VersionRun.Replace(segment ?? "", "#").ToLowerInvariant();

        private static int[] VersionOf(string segment)
            => Regex.Matches(segment ?? "", @"\d+").Cast<Match>()
                    .Select(m => int.TryParse(m.Value, out var n) ? n : int.MaxValue).ToArray();

        private static int CompareVersions(int[] a, int[] b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        // The newest existing variant of `path`, or null when `path` is already the newest one
        // (or nothing better can be found).
        public static string FindNewer(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = Path.GetFullPath(path.Trim()); } catch { return null; }
            var root = Path.GetPathRoot(full) ?? "";
            var segments = full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            bool exists = File.Exists(full);

            // Closest-to-the-file segment first: "app-1.15\Gemini.exe" is fixed at "app-1.15"
            // without touching anything above it.
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var seg = segments[i];
                if (!VersionRun.IsMatch(seg)) continue;
                // While the file is still there, only a real dotted version may move it on —
                // "bin32" → "bin64" or "x86" are not versions.
                if (exists && !LooksLikeVersion.IsMatch(seg)) continue;
                var parent = Path.Combine(root, string.Join("\\", segments.Take(i)));
                var rest = string.Join("\\", segments.Skip(i + 1));
                bool isLeaf = i == segments.Length - 1;
                if (!Directory.Exists(parent)) continue;

                string[] siblings;
                try
                {
                    siblings = isLeaf ? Directory.GetFiles(parent) : Directory.GetDirectories(parent);
                }
                catch { continue; }   // e.g. WindowsApps: not listable for a normal user

                var shape = Shape(seg);
                var mine = VersionOf(seg);
                string best = null;
                int[] bestVer = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (var sib in siblings)
                {
                    var name = Path.GetFileName(sib);
                    if (Shape(name) != shape) continue;
                    var candidate = isLeaf ? sib : Path.Combine(sib, rest);
                    if (!File.Exists(candidate)) continue;
                    var ver = VersionOf(name);
                    DateTime t;
                    try { t = File.GetLastWriteTimeUtc(candidate); } catch { t = DateTime.MinValue; }
                    int cmp = bestVer == null ? 1 : CompareVersions(ver, bestVer);
                    if (cmp > 0 || (cmp == 0 && t > bestTime)) { best = candidate; bestVer = ver; bestTime = t; }
                }
                if (best == null) continue;
                // Already the newest at this level → maybe a level above carries the version.
                if (string.Equals(best, full, StringComparison.OrdinalIgnoreCase)) continue;
                if (exists && CompareVersions(bestVer, mine) <= 0) continue;
                return best;
            }
            return null;
        }

        public sealed class Fix
        {
            public string Rule, OldPath, NewPath;
        }

        // Repairs every program path in the config in place. Returns what was changed.
        public static List<Fix> RepairAll(AppConfig cfg, Func<AppEntry, bool> only = null)
        {
            var fixes = new List<Fix>();
            if (cfg?.Apps == null) return fixes;
            foreach (var rule in cfg.Apps)
            {
                if (rule?.Programs == null || (only != null && !only(rule))) continue;
                foreach (var prog in rule.Programs)
                {
                    if (prog == null || string.IsNullOrWhiteSpace(prog.Path)) continue;
                    // Store apps are healed by their package family (MainWindow.AutoHealStoreAppsAsync).
                    if (prog.Path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string newer;
                    try { newer = FindNewer(prog.Path); } catch { newer = null; }
                    if (newer == null) continue;
                    fixes.Add(new Fix { Rule = rule.Name, OldPath = prog.Path, NewPath = newer });
                    prog.Path = newer;
                }
            }
            return fixes;
        }

        // Any configured program whose file is missing — cheap enough to ask every tick.
        public static bool AnyMissing(AppConfig cfg)
        {
            if (cfg?.Apps == null) return false;
            foreach (var rule in cfg.Apps)
                foreach (var prog in rule?.Programs ?? Enumerable.Empty<RuleProgram>())
                {
                    if (prog == null || string.IsNullOrWhiteSpace(prog.Path) || prog.IsStoreApp) continue;
                    try { if (!File.Exists(prog.Path)) return true; } catch { }
                }
            return false;
        }
    }
}
