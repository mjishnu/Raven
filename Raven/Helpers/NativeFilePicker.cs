using System.Runtime.InteropServices;
using System.Text;

namespace Raven.Helpers;

/// <summary>
/// Native common-dialog file picker (GetOpenFileName). Used instead of the WinUI
/// FileOpenPicker because that fails when the process runs elevated (Raven can relaunch
/// as admin). Supports single- and multi-select for app package files.
/// </summary>
public static class NativeFilePicker
{
    public static IReadOnlyList<string> PickPackageFiles(IntPtr owner, bool allowMultiple, string title)
    {
        const int bufferChars = 64 * 1024; // large enough to hold many multi-selected paths

        var filter =
            $"{"InstallationsPage_Filter_AppPackages".GetLocalized()}\0*.msix;*.appx;*.msixbundle;*.appxbundle\0{"InstallationsPage_Filter_AllFiles".GetLocalized()}\0*.*\0\0";

        IntPtr filterPtr = IntPtr.Zero, filePtr = IntPtr.Zero, titlePtr = IntPtr.Zero;
        try
        {
            filterPtr = Marshal.StringToHGlobalUni(filter);
            titlePtr = Marshal.StringToHGlobalUni(title);
            filePtr = Marshal.AllocHGlobal(bufferChars * sizeof(char));
            for (var i = 0; i < bufferChars; i++)
                Marshal.WriteInt16(filePtr, i * sizeof(char), 0);

            const int OFN_EXPLORER = 0x00080000;
            const int OFN_FILEMUSTEXIST = 0x00001000;
            const int OFN_PATHMUSTEXIST = 0x00000800;
            const int OFN_NOCHANGEDIR = 0x00000008;
            const int OFN_ALLOWMULTISELECT = 0x00000200;

            var flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
            if (allowMultiple)
                flags |= OFN_ALLOWMULTISELECT;

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                lpstrFile = filePtr,
                nMaxFile = bufferChars,
                lpstrTitle = titlePtr,
                Flags = flags,
            };

            if (!GetOpenFileName(ref ofn))
            {
                var err = CommDlgExtendedError();
                if (err != 0)
                    throw new InvalidOperationException(
                        string.Format("InstallationsPage_FilePicker_CommDlgError".GetLocalized(), err));
                return Array.Empty<string>(); // user cancelled
            }

            return ParseResult(filePtr, bufferChars);
        }
        finally
        {
            if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
            if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    // Multi-select buffer layout: "dir\0file1\0file2\0...\0\0". Single select: "fullpath\0\0".
    private static IReadOnlyList<string> ParseResult(IntPtr buffer, int maxChars)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < maxChars; i++)
        {
            var ch = (char)Marshal.ReadInt16(buffer, i * sizeof(char));
            if (ch == '\0')
            {
                if (sb.Length == 0)
                    break; // double-null terminator => end
                tokens.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        if (tokens.Count == 0)
            return Array.Empty<string>();
        if (tokens.Count == 1)
            return new[] { tokens[0] }; // single fully-qualified path
        var dir = tokens[0];
        return tokens.Skip(1).Select(name => Path.Combine(dir, name)).ToList();
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
