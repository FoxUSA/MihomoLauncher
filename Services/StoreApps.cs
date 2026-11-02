using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace MihomoLauncher.Services
{
    public class StoreApp
    {
        public string Name { get; set; } = "";
        public string Aumid { get; set; } = "";   // PackageFamilyName!AppId
        public string Exe { get; set; } = "";      // current resolved exe path

        // Stable identity (no version): the part of the AUMID before '!'.
        public string Family => string.IsNullOrEmpty(Aumid) ? "" : Aumid.Split('!')[0];
    }

    // Enumerates installed Store/MSIX apps with their stable AUMID and current exe path,
    // and maps a versioned WindowsApps path back to its package family — so the launcher
    // can resolve apps regardless of the version baked into their install folder.
    public static class StoreApps
    {
        // Enumerate the apps the user actually sees in Start (Get-StartApps) and keep the
        // Store/MSIX ones (AUMID contains '!'). This is FAST and ROBUST: ~30 entries, not a
        // manifest parse of every installed package — the old "loop all Get-AppxPackage and
        // read each manifest" took too long on a slow laptop and was killed by the timeout
        // before it ever reached ChatGPT/Claude (enumeration order isn't alphabetical), so
        // the picker came up empty. Here the manifest is read only for the few Start apps,
        // just to resolve the current exe path (for routing + icon); if that fails the app
        // is still listed (launch works via AUMID regardless).
        //
        // Each output line is Base64(UTF-8("Name<TAB>AUMID<TAB>Exe")) — pure ASCII on the
        // wire, so it needs NO console encoding (a GUI app has no console; setting
        // [Console]::OutputEncoding in a CreateNoWindow child throws and kills the script).
        // Streams are read async so a full stderr buffer can never deadlock us.
        public static List<StoreApp> Query()
        {
            const string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$ProgressPreference='SilentlyContinue';" +
                "$starts = @(Get-StartApps) | Where-Object { $_.AppID -like '*!*' };" +
                "$pkgs = @{};" +
                "foreach ($p in Get-AppxPackage) { if ($p.PackageFamilyName) { $pkgs[$p.PackageFamilyName] = $p } };" +
                "foreach ($s in $starts) {" +
                "  $aumid = [string]$s.AppID; $name = [string]$s.Name; $exe = '';" +
                "  $bang = $aumid.IndexOf('!');" +
                "  if ($bang -gt 0) {" +
                "    $fam = $aumid.Substring(0, $bang); $appId = $aumid.Substring($bang + 1);" +
                "    $pkg = $pkgs[$fam];" +
                "    if ($pkg) { try {" +
                "      $m = Get-AppxPackageManifest $pkg;" +
                "      $a = @($m.Package.Applications.Application) | Where-Object { $_.Id -eq $appId } | Select-Object -First 1;" +
                "      if ($a -and $a.Executable) { $exe = Join-Path $pkg.InstallLocation $a.Executable }" +
                "    } catch {} }" +
                "  }" +
                "  $line = $name + [char]9 + $aumid + [char]9 + $exe;" +
                "  [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))" +
                "}";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var rows = new List<StoreApp>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var outLines = new List<string>();
                using (var p = Process.Start(psi))
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (outLines) outLines.Add(e.Data); };
                    p.ErrorDataReceived += (s, e) => { /* drain so the pipe never blocks */ };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (p.WaitForExit(25000)) p.WaitForExit(); // flush async readers
                    else { try { p.Kill(); } catch { } }
                }

                List<string> snapshot;
                lock (outLines) snapshot = new List<string>(outLines);
                foreach (var b64 in snapshot)
                {
                    if (string.IsNullOrWhiteSpace(b64)) continue;
                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
                        var parts = decoded.Split('\t');
                        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) continue;
                        rows.Add(new StoreApp
                        {
                            Name = parts[0].Trim(),
                            Aumid = parts[1].Trim(),
                            Exe = parts.Length > 2 ? parts[2].Trim() : "",
                        });
                    }
                    catch { /* skip a malformed line */ }
                }
            }
            catch { }

            return rows.GroupBy(r => r.Aumid, StringComparer.OrdinalIgnoreCase)
                       .Select(g => g.First())
                       .ToList();
        }

        // "...\WindowsApps\Claude_1.9659.2.0_x64__pzs8sxrjxfjjc\app\claude.exe"
        //   -> "Claude_pzs8sxrjxfjjc"  (Name_PublisherHash, version/arch dropped)
        // Returns "" if the path is not a WindowsApps install.
        public static string FamilyFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            const string marker = "\\WindowsApps\\";
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var rest = path.Substring(idx + marker.Length);
            var folder = rest.Split('\\')[0]; // <Name>_<Version>_<Arch>__<PublisherHash>
            var dd = folder.IndexOf("__", StringComparison.Ordinal);
            if (dd < 0) return "";
            var hash = folder.Substring(dd + 2);
            var underscore = folder.IndexOf('_');
            if (underscore <= 0 || string.IsNullOrEmpty(hash)) return "";
            var name = folder.Substring(0, underscore);
            return name + "_" + hash;
        }
    }
}
