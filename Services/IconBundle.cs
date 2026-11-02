using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Resources;

namespace MihomoLauncher.Services
{
    public static class IconBundle
    {
        public static readonly string BundledDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MihomoLauncher", "bundled-icons");

        public static void EnsureExtracted()
        {
            try
            {
                foreach (var entry in EnumerateBundled())
                {
                    var fileName = Path.GetFileName(entry.Replace('\\', '/'));
                    if (string.IsNullOrEmpty(fileName)) continue;

                    var dest = Path.Combine(BundledDir, fileName);
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(BundledDir);   // lazy — only if there's something to extract

                    var uri = new Uri("pack://application:,,,/" + entry, UriKind.Absolute);
                    StreamResourceInfo info;
                    try { info = Application.GetResourceStream(uri); }
                    catch { continue; }
                    if (info == null) continue;

                    using (info.Stream)
                    using (var fs = File.Create(dest))
                    {
                        info.Stream.CopyTo(fs);
                    }
                }
            }
            catch
            {
            }
        }

        public static string ResolveBundled(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            var name = Path.GetFileName(relativePath.Replace('\\', '/'));
            if (string.IsNullOrEmpty(name)) return null;
            var p = Path.Combine(BundledDir, name);
            return File.Exists(p) ? p : null;
        }

        private static IEnumerable<string> EnumerateBundled()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetName().Name + ".g.resources";
            Stream stream;
            try { stream = asm.GetManifestResourceStream(resName); }
            catch { yield break; }
            if (stream == null) yield break;

            using (stream)
            using (var reader = new ResourceReader(stream))
            {
                foreach (System.Collections.DictionaryEntry entry in reader)
                {
                    var key = entry.Key as string;
                    if (string.IsNullOrEmpty(key)) continue;
                    if (key.StartsWith("ico/", StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith("ico\\", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return key;
                    }
                }
            }
        }
    }
}
