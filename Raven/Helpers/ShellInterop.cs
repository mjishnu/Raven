using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Raven.Helpers;

/// <summary>
/// Source-generated COM interop (<see cref="GeneratedComInterfaceAttribute"/>) for the Windows
/// Shell APIs Raven uses: the file/folder picker, .lnk shortcuts and <c>shell:AppsFolder</c>.
/// Built-in COM (<c>[ComImport]</c>, <c>dynamic</c>/IDispatch late binding) is unavailable once
/// the app is trimmed and NativeAOT-compiled; this keeps working there.
/// </summary>
internal static partial class ShellInterop
{
    public static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid FOLDERID_AppsFolder = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");

    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IEnumShellItems = new("70629033-E363-4A28-A567-0DB78006E6D7");
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOWNORMAL = 1;

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    /// <summary>
    /// Creates an in-process COM object. Cast the result to the generated interface you need,
    /// and pass it to <see cref="Release"/> when done.
    /// </summary>
    public static object CreateComObject(in Guid clsid)
    {
        Marshal.ThrowExceptionForHR(
            CoCreateInstance(clsid, 0, CLSCTX_INPROC_SERVER, IID_IUnknown, out var unknown));
        return Adopt<object>(unknown);
    }

    /// <summary>
    /// Releases a COM object from <see cref="CreateComObject"/> now instead of when the GC collects
    /// it. Objects handed out through interface out-parameters are left to the GC (a no-op here).
    /// </summary>
    public static void Release(object? comObject) => (comObject as ComObject)?.FinalRelease();

    /// <summary>
    /// Opens the first item of a known folder whose display name satisfies <paramref name="match"/>,
    /// the same way activating it in Explorer does. Returns that item's display name, or
    /// <c>null</c> when nothing matched. Call on an STA thread: shell verbs can require one.
    /// </summary>
    public static string? OpenFirstKnownFolderItem(in Guid folderId, Func<string, bool> match)
    {
        Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(folderId, 0, 0, IID_IShellItem, out var folderPointer));
        var folder = Adopt<IShellItem>(folderPointer);
        IEnumShellItems? children = null;
        try
        {
            folder.BindToHandler(0, BHID_EnumItems, IID_IEnumShellItems, out var childrenPointer);
            children = Adopt<IEnumShellItems>(childrenPointer);

            while (children.Next(1, out var childPointer, out var fetched) == 0 && fetched == 1)
            {
                var child = Adopt<IShellItem>(childPointer);
                try
                {
                    child.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
                    if (string.IsNullOrWhiteSpace(name) || !match(name))
                        continue;

                    Open(child);
                    return name;
                }
                finally
                {
                    Release(child);
                }
            }

            return null;
        }
        finally
        {
            Release(children);
            Release(folder);
        }
    }

    // Invokes the item's "open" verb through its ID list (what FolderItem.InvokeVerb("Open") did).
    private static unsafe void Open(IShellItem item)
    {
        Marshal.ThrowExceptionForHR(SHGetIDListFromObject(item, out var idList));
        try
        {
            fixed (char* verb = "open")
            {
                var info = new SHELLEXECUTEINFOW
                {
                    cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                    fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI,
                    lpVerb = verb,
                    lpIDList = idList,
                    nShow = SW_SHOWNORMAL,
                };

                if (!ShellExecuteExW(&info))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(idList);
        }
    }

    // Wraps an interface pointer the caller owns (e.g. from an out-parameter) in its own managed
    // object, so Release() can free it deterministically, and drops the caller's reference.
    private static T Adopt<T>(nint pointer) where T : class
    {
        try
        {
            return (T)ComWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderItem(in Guid rfid, uint flags, nint hToken, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetIDListFromObject(IShellItem punk, out nint ppidl);

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ShellExecuteExW(SHELLEXECUTEINFOW* pExecInfo);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SHELLEXECUTEINFOW
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public char* lpVerb;
        public char* lpFile;
        public char* lpParameters;
        public char* lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public char* lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }
}

// ---------------------------------------------------------------
//  COM interfaces. Every method is declared in vtable order (base
//  interfaces flattened in); unused slots only keep their position.
// ---------------------------------------------------------------

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
internal partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[GeneratedComInterface]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
internal partial interface IShellItemArray
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppvOut);
    void GetPropertyStore(int flags, in Guid riid, out nint ppv);
    void GetPropertyDescriptionList(nint keyType, in Guid riid, out nint ppv);
    void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    void GetCount(out uint pdwNumItems);
    void GetItemAt(uint dwIndex, out IShellItem ppsi);
    void EnumItems(out nint ppenumShellItems);
}

[GeneratedComInterface]
[Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
internal partial interface IEnumShellItems
{
    // Returns S_OK (0) while items are fetched and S_FALSE (1) at the end.
    [PreserveSig] int Next(uint celt, out nint rgelt, out uint pceltFetched);
    void Skip(uint celt);
    void Reset();
    void Clone(out nint ppenum);
}

/// <summary>IFileOpenDialog, with its IModalWindow and IFileDialog bases flattened in.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
internal partial interface IFileOpenDialog
{
    // IModalWindow. Returns HRESULT_FROM_WIN32(ERROR_CANCELLED) when the user cancels.
    [PreserveSig] int Show(nint parent);

    // IFileDialog
    void SetFileTypes(uint cFileTypes, nint rgFilterSpec);
    void SetFileTypeIndex(uint iFileType);
    void GetFileTypeIndex(out uint piFileType);
    void Advise(nint pfde, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOptions(uint fos);
    void GetOptions(out uint pfos);
    void SetDefaultFolder(IShellItem psi);
    void SetFolder(IShellItem psi);
    void GetFolder(out IShellItem ppsi);
    void GetCurrentSelection(out IShellItem ppsi);
    void SetFileName(string pszName);
    void GetFileName(out string pszName);
    void SetTitle(string pszTitle);
    void SetOkButtonLabel(string pszText);
    void SetFileNameLabel(string pszLabel);
    void GetResult(out IShellItem ppsi);
    void AddPlace(IShellItem psi, int fdap);
    void SetDefaultExtension(string pszDefaultExtension);
    void Close(int hr);
    void SetClientGuid(in Guid guid);
    void ClearClientData();
    void SetFilter(nint pFilter);

    // IFileOpenDialog
    void GetResults(out IShellItemArray ppenum);
    void GetSelectedItems(out IShellItemArray ppsai);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint pszFile, int cch, nint pfd, uint fFlags);
    void GetIDList(out nint ppidl);
    void SetIDList(nint pidl);
    void GetDescription(nint pszName, int cch);
    void SetDescription(string pszName);
    void GetWorkingDirectory(nint pszDir, int cch);
    void SetWorkingDirectory(string pszDir);
    void GetArguments(nint pszArgs, int cch);
    void SetArguments(string pszArgs);
    void GetHotkey(out ushort pwHotkey);
    void SetHotkey(ushort wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation(nint pszIconPath, int cch, out int piIcon);
    void SetIconLocation(string pszIconPath, int iIcon);
    void SetRelativePath(string pszPathRel, uint dwReserved);
    void Resolve(nint hwnd, uint fFlags);
    void SetPath(string pszFile);
}

/// <summary>IPersistFile, with its IPersist base flattened in.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    // IPersist
    void GetClassID(out Guid pClassID);

    // IPersistFile
    [PreserveSig] int IsDirty();
    void Load(string pszFileName, uint dwMode);
    void Save(string pszFileName, int fRemember);
    void SaveCompleted(string pszFileName);
    void GetCurFile(out string ppszFileName);
}
