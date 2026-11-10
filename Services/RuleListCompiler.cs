using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MihomoLauncher.Services
{
    // A subscription's rule lists in the engine's fast form.
    //
    // The engine re-reads every rule list on every reload. A big one as text — blackmatrix7's
    // Advertising domains, 290 000 entries — costs ~2 s on a fast PC and 6-8 s on PAPA2, per
    // reload, several reloads per connect: that was the whole «долго подключается» (2026-11-09).
    // The same list as the engine's binary .mrs loads in 0.04 s (measured on the binary). That is
    // how mihomo's own rule sets ship (MetaCubeX meta-rules-dat); .mrs only knows two kinds,
    // domains and IP ranges, so a list is split:
    //
    //   <list>.d.mrs     DOMAIN / DOMAIN-SUFFIX (classical) or a domain-behavior list as is
    //   <list>.ip.mrs    IP-CIDR / IP-CIDR6     (classical) or an ipcidr-behavior list as is
    //   <list>.rest.yaml everything else (DOMAIN-KEYWORD, PROCESS-NAME…) — classical, small
    //
    // A RULE-SET of the list becomes the same rule for each part, in that order, with the same
    // target — one match in the union is one match in the list, exactly as before.
    //
    // The source file stays as it is (the «Подписка» window reads it), and the config falls back
    // to it whenever the parts are missing or older than it: a failed conversion costs speed,
    // never routing. Converted by the engine itself — as a COPY under its own name, because the
    // watchdog restarts the live engine when it sees a second process of the engine's name.
    public static class RuleListCompiler
    {
        // Below this a list loads fast enough as it is; three files instead of one buy nothing.
        private const int MinEntries = 1000;

        private const string ConverterExeName = "mihomo-convert.exe";
        private static readonly object Gate = new object();

        public sealed class Parts
        {
            public string DomainRel, IpRel, RestRel;   // ./providers/rules/…, null = no such part
            public bool IpNoResolve;                   // every IP entry of the list said no-resolve
        }

        private static string Full(string workDir, string rel)
            => Path.Combine(workDir, rel.Substring(2).Replace('/', Path.DirectorySeparatorChar));

        private static string Base(string rel)
        {
            var dot = rel.LastIndexOf('.');
            return dot > rel.LastIndexOf('/') ? rel.Substring(0, dot) : rel;
        }

        // What the stamp file says about the list's current text: size and time of the source.
        private static string SourceStamp(string full)
        {
            var fi = new FileInfo(full);
            return fi.Exists ? fi.Length + ":" + fi.LastWriteTimeUtc.Ticks : "";
        }

        // The converted parts of the list at `rel`, if they are there and made from the source as
        // it is now; null otherwise (the caller then uses the source). Cheap: one small file read.
        public static Parts Lookup(string workDir, string rel)
        {
            try
            {
                if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(rel) || !rel.StartsWith("./")) return null;
                var stampPath = Full(workDir, Base(rel) + ".compiled");
                if (!File.Exists(stampPath)) return null;
                var lines = File.ReadAllLines(stampPath);
                if (lines.Length < 2 || lines[0] != SourceStamp(Full(workDir, rel))) return null;

                var parts = new Parts();
                foreach (var kv in lines.Skip(1).Select(l => l.Split(new[] { '=' }, 2)).Where(a => a.Length == 2))
                {
                    if (kv[0] == "d") parts.DomainRel = kv[1];
                    else if (kv[0] == "ip") parts.IpRel = kv[1];
                    else if (kv[0] == "rest") parts.RestRel = kv[1];
                    else if (kv[0] == "noresolve") parts.IpNoResolve = kv[1] == "1";
                }
                foreach (var p in new[] { parts.DomainRel, parts.IpRel, parts.RestRel })
                    if (p != null && !File.Exists(Full(workDir, p))) return null;
                return parts.DomainRel == null && parts.IpRel == null ? null : parts;
            }
            catch { return null; }
        }

        // Convert the list unless its parts are already current. False = left as it is (too
        // small, a kind .mrs can't hold, or the conversion failed — said in the log).
        public static bool Compile(AppConfig cfg, string rel, string behavior, string format, CancellationToken ct = default)
        {
            var workDir = AppConfigStore.ResolveMihomoWorkDir(cfg);
            behavior = (behavior ?? "classical").Trim().ToLowerInvariant();
            format = (format ?? "yaml").Trim().ToLowerInvariant();
            if (format == "mrs" || (format != "yaml" && format != "text")) return false;
            if (behavior != "classical" && behavior != "domain" && behavior != "ipcidr") return false;
            if (Lookup(workDir, rel) != null) return true;

            var src = Full(workDir, rel);
            if (!File.Exists(src)) return false;

            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                var entries = ReadEntries(src, format);
                var domains = new List<string>();
                var ips = new List<string>();
                var ipsNoResolve = new List<string>();
                var rest = new List<string>();

                foreach (var e in entries)
                {
                    if (behavior == "domain") { domains.Add(e); continue; }
                    if (behavior == "ipcidr") { ips.Add(e); continue; }
                    var f = e.Split(',').Select(x => x.Trim()).ToArray();
                    var type = f[0].ToUpperInvariant();
                    if ((type == "DOMAIN" || type == "DOMAIN-SUFFIX") && f.Length == 2 && f[1].Length > 0)
                        domains.Add(type == "DOMAIN" ? f[1] : "+." + f[1]);
                    else if ((type == "IP-CIDR" || type == "IP-CIDR6") && f.Length >= 2 && f.Length <= 3 && f[1].Length > 0)
                    {
                        bool nr = f.Length == 3 && f[2].Equals("no-resolve", StringComparison.OrdinalIgnoreCase);
                        if (f.Length == 3 && !nr) rest.Add(e);
                        else (nr ? ipsNoResolve : ips).Add(f[1]);
                    }
                    else rest.Add(e);
                }

                // .mrs takes no-resolve per RULE-SET, not per entry. All alike → one IP part with
                // the flag (or without); mixed → the minority keeps its own flag in the rest.
                bool noResolve = false;
                if (ips.Count == 0 && ipsNoResolve.Count > 0) { ips = ipsNoResolve; noResolve = true; }
                else rest.AddRange(ipsNoResolve.Select(c => (c.Contains(":") ? "IP-CIDR6," : "IP-CIDR,") + c + ",no-resolve"));

                if (domains.Count + ips.Count < MinEntries) return false;

                var b = Base(rel);
                var parts = new Parts { IpNoResolve = noResolve };
                var sw = Stopwatch.StartNew();
                try
                {
                    if (domains.Count > 0)
                    {
                        parts.DomainRel = b + ".d.mrs";
                        Convert(cfg, "domain", domains, Full(workDir, parts.DomainRel));
                    }
                    if (ips.Count > 0)
                    {
                        parts.IpRel = b + ".ip.mrs";
                        Convert(cfg, "ipcidr", ips, Full(workDir, parts.IpRel));
                    }
                    if (rest.Count > 0)
                    {
                        parts.RestRel = b + ".rest.yaml";
                        var sb = new StringBuilder("payload:\n");
                        foreach (var r in rest) sb.Append("  - '").Append(r.Replace("'", "''")).Append("'\n");
                        File.WriteAllText(Full(workDir, parts.RestRel), sb.ToString(), new UTF8Encoding(false));
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Список правил " + Path.GetFileName(src) + " в быстрый формат не переведён — " + ex.Message
                             + ". Остаётся как есть.");
                    return false;
                }

                // Written last: until it is there, Lookup keeps answering "use the source".
                var stamp = new StringBuilder(SourceStamp(src)).Append('\n');
                if (parts.DomainRel != null) stamp.Append("d=").Append(parts.DomainRel).Append('\n');
                if (parts.IpRel != null) stamp.Append("ip=").Append(parts.IpRel).Append('\n');
                if (parts.RestRel != null) stamp.Append("rest=").Append(parts.RestRel).Append('\n');
                stamp.Append("noresolve=").Append(parts.IpNoResolve ? "1" : "0").Append('\n');
                File.WriteAllText(Full(workDir, b + ".compiled"), stamp.ToString(), new UTF8Encoding(false));

                Log.Info("Список правил " + Path.GetFileName(src) + " переведён в быстрый формат: доменов "
                         + domains.Count + ", IP " + ips.Count + ", прочих " + rest.Count + " (" + sw.ElapsedMilliseconds + " мс).");
                return true;
            }
        }

        // The list's entries: `payload:` items of a YAML list, or the lines of a text one.
        internal static List<string> ReadEntries(string path, string format)
        {
            var result = new List<string>();
            bool yaml = format != "text";
            bool inPayload = !yaml;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (yaml)
                {
                    if (!raw.StartsWith(" ") && !raw.StartsWith("-")) { inPayload = line.StartsWith("payload:"); continue; }
                    if (!inPayload || !line.StartsWith("-")) continue;
                    line = Unquote(StripComment(line.Substring(1).Trim()));
                }
                else line = StripComment(line);
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        private static string StripComment(string s)
        {
            int i = s.IndexOf(" #", StringComparison.Ordinal);
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && ((s[0] == '\'' && s[s.Length - 1] == '\'') || (s[0] == '"' && s[s.Length - 1] == '"')))
            {
                var q = s[0];
                s = s.Substring(1, s.Length - 2);
                s = q == '\'' ? s.Replace("''", "'") : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }

        // `convert-ruleset <behavior> text <in> <out>` on the converter copy of the engine.
        private static void Convert(AppConfig cfg, string behavior, List<string> items, string outPath)
        {
            var exe = ConverterExe(cfg);
            var txt = outPath + ".src.txt";
            var tmp = outPath + ".new";
            File.WriteAllText(txt, string.Join("\n", items) + "\n", new UTF8Encoding(false));
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "convert-ruleset " + behavior + " text \"" + txt + "\" \"" + tmp + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync();
                    var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new TimeoutException("конвертер не уложился в 2 минуты"); }
                    p.WaitForExit();
                    var fi = new FileInfo(tmp);
                    if (p.ExitCode != 0 || !fi.Exists || fi.Length == 0)
                    {
                        var text = ((se.Result ?? "") + " " + (so.Result ?? "")).Trim();
                        throw new InvalidOperationException("конвертер: код " + p.ExitCode
                            + (text.Length > 0 ? ", " + (text.Length > 200 ? text.Substring(0, 200) : text) : ""));
                    }
                }
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);
            }
            finally
            {
                try { File.Delete(txt); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static string ConverterExe(AppConfig cfg)
        {
            var src = (cfg?.MihomoExePath ?? "").Trim();
            if (src.Length == 0 || !File.Exists(src)) throw new FileNotFoundException("нет движка для перевода");
            var dir = Path.Combine(VersionInfo.DataUserDir, "dnscheck");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, ConverterExeName);
            var si = new FileInfo(src);
            var di = new FileInfo(exe);
            if (!di.Exists || di.Length != si.Length || di.LastWriteTimeUtc != si.LastWriteTimeUtc)
            {
                File.Copy(src, exe, true);
                File.SetLastWriteTimeUtc(exe, si.LastWriteTimeUtc);
            }
            return exe;
        }

        // ---------- what goes into config.yaml ----------

        // The providers for a converted list, at the indent of `rule-providers:` entries.
        public static IEnumerable<string> ProviderLines(string key, Parts p)
        {
            if (p.DomainRel != null) yield return "  " + key + ".d: {type: file, behavior: domain, format: mrs, path: " + p.DomainRel + "}";
            if (p.IpRel != null) yield return "  " + key + ".ip: {type: file, behavior: ipcidr, format: mrs, path: " + p.IpRel + "}";
            if (p.RestRel != null) yield return "  " + key + ".rest: {type: file, behavior: classical, format: yaml, path: " + p.RestRel + "}";
        }

        // `RULE-SET,key,target[,opts]` → the same rule for each part. Logical rules get the parts
        // as an OR in place of the one RULE-SET. Anything else comes back unchanged.
        public static IEnumerable<string> ExpandRule(string rule, IDictionary<string, Parts> compiled)
        {
            if (compiled == null || compiled.Count == 0) { yield return rule; yield break; }

            var m = Regex.Match(rule, @"^\s*RULE-SET\s*,\s*([^,]+?)\s*,(.*)$", RegexOptions.IgnoreCase);
            Parts p;
            if (m.Success && compiled.TryGetValue(m.Groups[1].Value, out p))
            {
                var key = m.Groups[1].Value;
                var tail = m.Groups[2].Value;   // target and options, as they were
                if (p.DomainRel != null) yield return "RULE-SET," + key + ".d," + tail;
                if (p.RestRel != null) yield return "RULE-SET," + key + ".rest," + tail;
                if (p.IpRel != null)
                    yield return "RULE-SET," + key + ".ip," + tail
                        + (p.IpNoResolve && tail.IndexOf("no-resolve", StringComparison.OrdinalIgnoreCase) < 0 ? ",no-resolve" : "");
                yield break;
            }

            yield return Regex.Replace(rule, @"\(\s*RULE-SET\s*,\s*([^,)]+?)\s*\)", x =>
            {
                Parts q;
                if (!compiled.TryGetValue(x.Groups[1].Value, out q)) return x.Value;
                var k = x.Groups[1].Value;
                var items = new List<string>();
                if (q.DomainRel != null) items.Add("(RULE-SET," + k + ".d)");
                if (q.RestRel != null) items.Add("(RULE-SET," + k + ".rest)");
                if (q.IpRel != null) items.Add("(RULE-SET," + k + ".ip)");
                return items.Count == 1 ? items[0] : "(OR,(" + string.Join(",", items) + "))";
            });
        }
    }
}
