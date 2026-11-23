using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MihomoLauncher.Views
{
    // A folder picker for .NET Framework WPF (2026-11-22, the rule editor's «Папка»): the shell's own
    // modern dialog (IFileOpenDialog with FOS_PICKFOLDERS) — the same one Explorer and every current
    // program use. WinForms' FolderBrowserDialog is only the fallback: its tiny tree is hard to use.
    internal static class FolderPicker
    {
        // The chosen folder, or null when the person cancelled.
        public static string Pick(Window owner, string title)
        {
            IntPtr hwnd = IntPtr.Zero;
            try { if (owner != null) hwnd = new WindowInteropHelper(owner).Handle; } catch { }

            IFileDialog dlg = null;
            try
            {
                dlg = (IFileDialog)new FileOpenDialogRcw();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                int hr = dlg.Show(hwnd);
                if (hr != 0) return null;   // ERROR_CANCELLED and anything else: nothing chosen
                IShellItem item;
                dlg.GetResult(out item);
                if (item == null) return null;
                IntPtr psz;
                item.GetDisplayName(SIGDN_FILESYSPATH, out psz);
                try { return Marshal.PtrToStringUni(psz); }
                finally { Marshal.FreeCoTaskMem(psz); Marshal.ReleaseComObject(item); }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                try { MihomoLauncher.Services.Log.Error("folder-picker", ex); } catch { }
            }
            finally
            {
                if (dlg != null) try { Marshal.ReleaseComObject(dlg); } catch { }
            }

            try
            {
                using (var fb = new System.Windows.Forms.FolderBrowserDialog { Description = title ?? "", ShowNewFolderButton = false })
                    return fb.ShowDialog() == System.Windows.Forms.DialogResult.OK ? fb.SelectedPath : null;
            }
            catch { return null; }
        }

        private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        // IModalWindow + IFileDialog, in vtable order. Only the members used are typed.
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr specs);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IntPtr item);
            void SetFolder(IntPtr item);
            void GetFolder(out IntPtr item);
            void GetCurrentSelection(out IntPtr item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IntPtr item, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, out IntPtr name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
