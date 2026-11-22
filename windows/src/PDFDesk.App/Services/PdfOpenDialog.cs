using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace PDFDesk.Services;

internal static class PdfOpenDialog
{
    internal static Task<List<string>> PickAsync(nint owner)
    {
        var result = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        string filterName = UiText.Translate("PDF documents");
        var thread = new Thread(() =>
        {
            try { result.SetResult(Pick(owner, filterName)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "PDFDesk file picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static unsafe List<string> Pick(nint owner, string filterName)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            FILEOPENDIALOGOPTIONS options;
            dialog.GetOptions(&options);
            dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                                      | FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR);
            fixed (char* name = filterName, pattern = "*.pdf")
            {
                var filter = new COMDLG_FILTERSPEC { pszName = name, pszSpec = pattern };
                dialog.SetFileTypes(1, &filter);
            }
            dialog.Show(new HWND(owner));
            dialog.GetResults(out var results);
            try
            {
                results.GetCount(out uint count);
                List<string> paths = [];
                for (uint index = 0; index < count; index++)
                {
                    results.GetItemAt(index, out var item);
                    try
                    {
                        PWSTR path;
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path);
                        try { paths.Add(path.ToString()); }
                        finally { Marshal.FreeCoTaskMem((nint)path.Value); }
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return paths;
            }
            finally { Marshal.ReleaseComObject(results); }
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x800704C7))
        {
            return [];
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }
}
