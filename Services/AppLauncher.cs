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
        public static (bool ok, string error) Launch(AppEntry rule)
        {
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

            var progToRun = programs.Where(p => p.Launch).ToList();
            var siteToRun = sites.Where(x => x.Site.Launch).ToList();
            if (progToRun.Count == 0 && siteToRun.Count == 0)
            {
                // Nothing marked — sensible default: all programs, else all sites.
                if (programs.Count > 0) progToRun = programs;
                else siteToRun = sites;
            }

            if (progToRun.Count == 0 && siteToRun.Count == 0)
                return (false, Loc.T("Svc_RuleNothingToLaunch"));

            var errors = new List<string>();
            bool any = false;

            foreach (var prog in progToRun)
            {
                var r = LaunchProgram(prog);
                if (r.ok) any = true; else errors.Add(r.error);
            }
            foreach (var site in siteToRun)
            {
                var r = OpenUrl(site.Url);
                if (r.ok) any = true; else errors.Add(r.error);
            }

            if (any) return (true, null);
            return (false, string.Join("; ", errors.Distinct()));
        }

        // The address a site opens from the tile. A plain site has its own URL; a template
        // (rule list, no URL) borrows the rule's CheckUrl — the service's own site, which the
        // template picker fills in automatically.
        private static string LaunchUrl(AppEntry rule, RuleSite site)
        {
            if (!string.IsNullOrWhiteSpace(site.Url)) return site.Url.Trim();
            if (site.HasRuleList && !string.IsNullOrWhiteSpace(rule.CheckUrl)) return rule.CheckUrl.Trim();
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
