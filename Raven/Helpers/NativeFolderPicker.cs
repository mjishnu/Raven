using System.Runtime.InteropServices;
using System.Text;

namespace Raven.Helpers;

/// <summary>
/// Native folder picker (SHBrowseForFolder). Used instead of the WinUI FolderPicker
/// because that fails when the process runs elevated. Returns the selected folder path
/// or <c>null</c> if cancelled.
/// </summary>
public static class NativeFolderPicker
{
    public static string? PickFolder(IntPtr owner, string? title = null)
    {
        const int MAX_PATH = 260;
        var displayBuffer = Marshal.AllocHGlobal(MAX_PATH * sizeof(char));
        try
        {
            var bi = new BROWSEINFO
            {
                hwndOwner = owner,
                pszDisplayName = displayBuffer,
                lpszTitle = title,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            };

            var pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero)
                return null;

            try
            {
                var sb = new StringBuilder(MAX_PATH);
                return SHGetPathFromIDList(pidl, sb) ? sb.ToString() : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayBuffer);
        }
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_NEWDIALOGSTYLE = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName; // [out] buffer (MAX_PATH chars)
        public string? lpszTitle;     // [in]
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);
}
