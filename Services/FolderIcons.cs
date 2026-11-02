using System;
using System.IO;

namespace MihomoLauncher.Services
{
    // Gives the install folder and its sub-folders custom icons via desktop.ini, using RELATIVE
    // paths so they survive the folder being moved / copied to another PC. Icon files ship in
    // Data\:  Data\fox.ico → main folder + the exe, Data\data.ico → Data\, Data\user_data.ico →
    // DataUser\. The clickable help / uninstall launchers in the root are tiny EXEs
    // (help.exe / uninstall.exe) built at Release-layout time with help.ico / trash.ico embedded
    // — a .bat can't carry a custom Explorer icon and a .lnk bakes in absolute paths that break
    // on move, so an exe with an embedded icon is the only portable option.
    //
    // Run on every normal startup (idempotent): re-asserts the folder icons (in case an Explorer
    // zip-extraction stripped the desktop.ini/folder attributes) and removes stale .bat/.lnk
    // launchers left by older builds. The launcher exes themselves ship in the archive.
    public static class FolderIcons
    {
        public static void Apply(string installRoot = null)
        {
            var root = (installRoot ?? VersionInfo.InstallRoot).TrimEnd('\\');
            var data = Path.Combine(root, "Data");
            var dataUser = Path.Combine(root, "DataUser");

            TrySetFolderIcon(data, "data.ico");
            TrySetFolderIcon(dataUser, @"..\Data\user_data.ico");
            TrySetFolderIcon(root, @"Data\fox.ico");

            // Drop stale root launchers from older builds (.lnk had absolute paths; .bat had no
            // icon). The real uninstaller stays at Data\uninstall.bat; never touch it.
            foreach (var stale in new[] { "help.lnk", "uninstall.lnk", "help.bat", "uninstall.bat" })
                TryDelete(Path.Combine(root, stale));
        }

        // Write desktop.ini in `folder` pointing at `iconResource` and flag the folder so
        // Explorer honours it (a custom folder icon needs the folder's read-only attribute).
        private static void TrySetFolderIcon(string folder, string iconResource)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                var ini = Path.Combine(folder, "desktop.ini");
                var text = "[.ShellClassInfo]\r\n" +
                           "IconResource=" + iconResource + ",0\r\n" +
                           "[ViewState]\r\nMode=\r\nVid=\r\nFolderType=Generic\r\n";

                // Only touch what's actually wrong, so a normal start doesn't rewrite files —
                // and so we can tell whether anything changed (see the shell notification below).
                bool changed = false;

                string current = null;
                try { if (File.Exists(ini)) current = File.ReadAllText(ini); } catch { }
                if (current != text)
                {
                    // A hidden/system file can't be overwritten by WriteAllText — clear the
                    // attributes first, restore them after.
                    if (File.Exists(ini)) File.SetAttributes(ini, FileAttributes.Normal);
                    File.WriteAllText(ini, text);
                    changed = true;
                }
                var iniAttrs = File.GetAttributes(ini);
                if ((iniAttrs & FileAttributes.Hidden) == 0 || (iniAttrs & FileAttributes.System) == 0)
                {
                    File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
                    changed = true;
                }

                var di = new DirectoryInfo(folder);
                if ((di.Attributes & FileAttributes.ReadOnly) == 0)
                {
                    di.Attributes |= FileAttributes.ReadOnly;
                    changed = true;
                }

                if (changed)
                {
                    Log.Info("Иконка папки восстановлена: " + folder);
                    NotifyShell(folder);
                }
            }
            catch (Exception ex) { try { Log.Error("FolderIcons.icon", ex); } catch { } }
        }

        private const int SHCNE_UPDATEDIR = 0x00001000;
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_FLUSHNOWAIT = 0x2000;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string item1,
            IntPtr item2);

        // THE part that was missing on other people's PCs. A zip doesn't carry the folder's
        // read-only attribute (and most archivers drop the hidden desktop.ini too), so after
        // extraction the folders are plain — which is what Explorer draws and then caches. We
        // fix that on the next start, but until the shell is told, it keeps serving the cached
        // generic folder icon, sometimes for good. Announce the change for the folder itself
        // and for its parent (the parent's window is where this folder's icon is drawn).
        private static void NotifyShell(string folder)
        {
            try
            {
                const uint flags = SHCNF_PATHW | SHCNF_FLUSHNOWAIT;
                SHChangeNotify(SHCNE_UPDATEITEM, flags, folder, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR, flags, folder, IntPtr.Zero);
                var parent = Path.GetDirectoryName(folder.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent))
                    SHChangeNotify(SHCNE_UPDATEDIR, flags, parent, IntPtr.Zero);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
