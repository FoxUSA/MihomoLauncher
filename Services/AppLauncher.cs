using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MihomoLauncher.Models;

namespace MihomoLauncher.Services
{
    public static class AppLauncher
    {
        // Launch a rule: open every site and start every program flagged Launch=true. If the
        // user marked nothing, fall back to "all programs, else all sites" so a click always
        // does something. Returns false with a combined reason if every attempt failed.
        public static (bool ok, string error) Launch(AppEntry rule) => Launch(rule, out _);

        // activatedOnly — nothing was started: every program was already running and only its
        // window was brought forward (the tile needs no "starting…" spinner then).
        public static (bool ok, string error) Launch(AppEntry rule, out bool activatedOnly)
        {
            activatedOnly = false;
            if (rule == null) return (false, Loc.T("Svc_Empty"));

            var programs = (rule.Programs ?? new List<RuleProgram>()).Where(p => p != null).ToList();
            // What each site opens: its own URL, or — for a template, which carries only a rule
            // list — the rule's connection-check address. That's what makes a tile possible for
            // a rule that contains nothing but a blackmatrix7 template.
            var sites = (rule.Sites ?? new List<RuleSite>())
                .Where(s => s != null)
                .Select(s => new { Site = s, Url = LaunchUrl(rule, s) })
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .ToList();

            // A folder with «Плитка» (2026-11-22): the tile opens it in Explorer.
            var folderToOpen = (rule.Folders ?? new List<RuleFolder>())
                .Where(f => f != null && f.Launch && !string.IsNullOrWhiteSpace(f.Path)).ToList();

            var progToRun = programs.Where(p => p.Launch).ToList();
            var siteToRun = sites.Where(x => x.Site.Launch).ToList();
            if (progToRun.Count == 0 && siteToRun.Count == 0 && folderToOpen.Count == 0)
            {
                // Nothing marked — sensible default: all programs, else all sites. Never the ones
                // auto-tracking added: those are helpers, started by their own program.
                var manual = programs.Where(p => !p.Auto).ToList();
                if (manual.Count > 0) progToRun = manual;
                else siteToRun = sites;
            }

            if (progToRun.Count == 0 && siteToRun.Count == 0 && folderToOpen.Count == 0)
                return (false, Loc.T("Svc_RuleNothingToLaunch"));
            // Already running → bring its window forward, don't start a second copy. Only a plain
            // exe without arguments: with arguments (python + a script) the running copy may be a
            // different job. Store apps — Windows already brings a running one forward.
            var toStart = new List<RuleProgram>();
            int activated = 0;
            foreach (var p in progToRun)
            {
                if (!p.IsStoreApp && string.IsNullOrWhiteSpace(p.Arguments) && WindowActivator.TryActivateRunning(p.Path)) activated++;
                else toStart.Add(p);
            }
            activatedOnly = activated > 0 && toStart.Count == 0 && siteToRun.Count == 0 && folderToOpen.Count == 0;
            if (activatedOnly) return (true, null);

            var r = LaunchAll(toStart, siteToRun.Select(x => x.Url).ToList(), folderToOpen);
            if (!r.ok && activated > 0) return (true, null);
            return r;
        }

        // Image names (no ".exe") of the desktop programs a click on this rule starts — what the
        // tile's spinner watches for. Store apps and sites have none to watch.
        public static List<string> LaunchImageNames(AppEntry rule)
        {
            var programs = (rule?.Programs ?? new List<RuleProgram>()).Where(p => p != null).ToList();
            var run = programs.Where(p => p.Launch).ToList();
            bool anySiteMarked = (rule?.Sites ?? new List<RuleSite>()).Any(s => s != null && s.Launch)
                              || (rule?.Folders ?? new List<RuleFolder>()).Any(f => f != null && f.Launch);
            if (run.Count == 0 && !anySiteMarked) run = programs.Where(p => !p.Auto).ToList();   // same default as Launch
            return run.Where(p => !p.IsStoreApp && !string.IsNullOrWhiteSpace(p.Path))
                      .Select(p => { try { return Path.GetFileNameWithoutExtension(p.Path.Trim()); } catch { return ""; } })
                      .Where(n => !string.IsNullOrEmpty(n))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToList();
        }

        private static (bool ok, string error) LaunchAll(List<RuleProgram> progToRun, List<string> urls,
                                                         List<RuleFolder> folders = null)
        {
            var errors = new List<string>();
            bool any = false;

            foreach (var prog in progToRun)
            {
                var r = LaunchProgram(prog);
                if (r.ok) any = true; else errors.Add(r.error);
            }
            foreach (var f in folders ?? new List<RuleFolder>())
            {
                var r = OpenFolder(f.Path);
                if (r.ok) any = true; else errors.Add(r.error);
            }
            foreach (var url in urls)
            {
                var r = OpenUrl(url);
                if (r.ok) any = true; else errors.Add(r.error);
            }

            if (any) return (true, null);
            return (false, string.Join("; ", errors.Distinct()));
        }

        // The address a site opens from the tile. A plain site has its own URL; a template
        // (rule list, no URL) borrows the rule's own address — the check address, else HomeUrl
        // (the service's site the template brought; 2026-11-23 it no longer sits in the check field).
        private static string LaunchUrl(AppEntry rule, RuleSite site)
        {
            if (!string.IsNullOrWhiteSpace(site.Url)) return site.Url.Trim();
            if (site.HasRuleList && rule.OwnUrl.Length > 0) return rule.OwnUrl;
            return "";
        }

        private static (bool ok, string error) LaunchProgram(RuleProgram prog)
        {
            try
            {
                // Store/MSIX app → launch by its stable AUMID via the Apps folder.
                if (prog.IsStoreApp)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "shell:AppsFolder\\" + prog.Aumid,
                        UseShellExecute = true,
                    });
                    return (true, null);
                }

                if (string.IsNullOrWhiteSpace(prog.Path))
                    return (false, Loc.T("Svc_PathNotSet"));

                var psi = new ProcessStartInfo
                {
                    FileName = prog.Path,
                    Arguments = prog.Arguments ?? "",
                    UseShellExecute = true,
                };
                try
                {
                    var dir = Path.GetDirectoryName(prog.Path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        psi.WorkingDirectory = dir;
                }
                catch { }

                Process.Start(psi);
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static (bool ok, string error) OpenFolder(string path)
        {
            try
            {
                var p = (path ?? "").Trim();
                if (!Directory.Exists(p)) return (false, Loc.T("Svc_FolderMissing", p));
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + p + "\"", UseShellExecute = true });
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static (bool ok, string error) OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
