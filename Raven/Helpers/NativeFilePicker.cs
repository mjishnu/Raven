using System.Runtime.InteropServices;

namespace Raven.Helpers;

/// <summary>
/// Native file and folder picker using the modern IFileOpenDialog COM interface.
/// Works correctly under elevation (unlike the WinUI FileOpenPicker / FolderPicker), and
/// under NativeAOT, since the interop is source-generated (see <see cref="ShellInterop"/>).
/// </summary>
public static class NativeFilePicker
{
    /// <summary>
    /// Shows a file-open dialog filtered to app-package extensions.
    /// Returns the selected file path(s), or an empty list if cancelled.
    /// </summary>
    public static IReadOnlyList<string> PickPackageFiles(
        IntPtr owner, bool allowMultiple, string title)
    {
        var filters = new[]
        {
            new FilterSpec(
                "InstallationsPage_Filter_AppPackages".GetLocalized(),
                "*.msix;*.appx;*.msixbundle;*.appxbundle"),
            new FilterSpec(
                "InstallationsPage_Filter_AllFiles".GetLocalized(),
                "*.*"),
        };

        uint flags = FOS_FILEMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR;
        if (allowMultiple)
            flags |= FOS_ALLOWMULTISELECT;

        return ShowOpenDialog(owner, title, filters, flags);
    }

    /// <summary>
    /// Shows a folder-picker dialog.
    /// Returns the selected folder path, or <c>null</c> if cancelled.
    /// </summary>
    public static string? PickFolder(IntPtr owner, string? title = null)
    {
        var results = ShowOpenDialog(
            owner,
            title,
            filters: null,
            FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);

        return results.Count > 0 ? results[0] : null;
    }

    // ---------------------------------------------------------------
    //  Shared IFileOpenDialog implementation
    // ---------------------------------------------------------------

    private readonly record struct FilterSpec(string Name, string Pattern);

    private static IReadOnlyList<string> ShowOpenDialog(
        IntPtr owner,
        string? title,
        FilterSpec[]? filters,
        uint extraFlags)
    {
        var dialog = (IFileOpenDialog)ShellInterop.CreateComObject(ShellInterop.CLSID_FileOpenDialog);
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | extraFlags);

            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            if (filters is { Length: > 0 })
                SetFilters(dialog, filters);

            var hr = dialog.Show(owner);
            if (hr != 0)
                return Array.Empty<string>(); // user cancelled

            // Multi-select: use GetResults; single-select: use GetResult
            if ((extraFlags & FOS_ALLOWMULTISELECT) != 0)
                return GetMultipleResults(dialog);

            dialog.GetResult(out var item);
            item.GetDisplayName(ShellInterop.SIGDN_FILESYSPATH, out var path);
            return string.IsNullOrEmpty(path)
                ? Array.Empty<string>()
                : new[] { path };
        }
        finally
        {
            ShellInterop.Release(dialog);
        }
    }

    private static void SetFilters(IFileOpenDialog dialog, FilterSpec[] filters)
    {
        // COMDLG_FILTERSPEC is two LPWStr pointers — marshal as an array of IntPtr pairs.
        var nativeFilters = new COMDLG_FILTERSPEC[filters.Length];
        var pins = new GCHandle[filters.Length * 2];
        try
        {
            for (var i = 0; i < filters.Length; i++)
            {
                nativeFilters[i].pszName = Marshal.StringToCoTaskMemUni(filters[i].Name);
                nativeFilters[i].pszSpec = Marshal.StringToCoTaskMemUni(filters[i].Pattern);
            }

            var handle = GCHandle.Alloc(nativeFilters, GCHandleType.Pinned);
            try
            {
                dialog.SetFileTypes(
                    (uint)filters.Length,
                    handle.AddrOfPinnedObject());
                dialog.SetFileTypeIndex(1); // 1-based
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            foreach (var f in nativeFilters)
            {
                if (f.pszName != IntPtr.Zero) Marshal.FreeCoTaskMem(f.pszName);
                if (f.pszSpec != IntPtr.Zero) Marshal.FreeCoTaskMem(f.pszSpec);
            }
        }
    }

    private static IReadOnlyList<string> GetMultipleResults(IFileOpenDialog dialog)
    {
        dialog.GetResults(out var shellItemArray);
        shellItemArray.GetCount(out var count);
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            shellItemArray.GetItemAt(i, out var item);
            item.GetDisplayName(ShellInterop.SIGDN_FILESYSPATH, out var path);
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
        return paths;
    }

    // ---------------------------------------------------------------
    //  Constants
    // ---------------------------------------------------------------

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_ALLOWMULTISELECT = 0x00000200;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_NOCHANGEDIR = 0x00000008;

    // ---------------------------------------------------------------
    //  Interop declarations (the COM interfaces live in ShellInterop)
    // ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        public IntPtr pszName;
        public IntPtr pszSpec;
    }
}
