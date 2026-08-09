from pathlib import Path


def replace_exact(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if old not in text:
        raise RuntimeError(f'anchor not found in {path}: {old[:100]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

# 1) If Run is selected and Raven already knows a portable target (or normal package is installed), just launch it.
replace_exact(
    'Raven/Views/AppPage.xaml.cs',
    '''        var action = CurrentActionKey;\n        var isRunAction = string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase);\n\n        // For Retry,''',
    '''        var action = CurrentActionKey;\n        var isRunAction = string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase);\n\n        if (isRunAction &&\n            (PortableLaunchRegistry.Exists(productId) ||\n             (!isUnpackaged && IsPackagedInstalled(_currentProductInfo))))\n        {\n            await TryOpenCurrentAppAsync();\n            return;\n        }\n\n        // For Retry,'''
)

# 2) Pass the app name into the native folder picker. Pre-create the suggested app folder in Raven's standard directory.
replace_exact(
    'Raven/Helpers/PortableMsixLauncher.cs',
    '''        Directory.CreateDirectory(defaultBaseFolder);\n        var installBaseFolder = NativeFilePicker.PickFolder(hwnd, "Choose installation folder", defaultBaseFolder);\n        if (string.IsNullOrWhiteSpace(installBaseFolder))\n            throw new OperationCanceledException("No installation folder was selected.");\n\n        var root = GetPortableRoot(appTitle, packageKey, installBaseFolder);''',
    '''        Directory.CreateDirectory(defaultBaseFolder);\n        var suggestedName = MakeSafeFileName(string.IsNullOrWhiteSpace(appTitle) ? "App" : appTitle);\n        var suggestedFolder = Path.Combine(defaultBaseFolder, suggestedName);\n        Directory.CreateDirectory(suggestedFolder);\n\n        var installBaseFolder = NativeFilePicker.PickFolder(\n            hwnd,\n            "Choose installation folder",\n            defaultBaseFolder,\n            suggestedName);\n        if (string.IsNullOrWhiteSpace(installBaseFolder))\n            throw new OperationCanceledException("No installation folder was selected.");\n\n        var root = string.Equals(\n            Path.GetFullPath(installBaseFolder).TrimEnd(Path.DirectorySeparatorChar),\n            Path.GetFullPath(suggestedFolder).TrimEnd(Path.DirectorySeparatorChar),\n            StringComparison.OrdinalIgnoreCase)\n                ? suggestedFolder\n                : GetPortableRoot(appTitle, packageKey, installBaseFolder);'''
)

# 3) Pre-fill the bottom folder-name edit field while still opening in the standard directory.
replace_exact(
    'Raven/Helpers/NativeFilePicker.cs',
    '''    public static string? PickFolder(IntPtr owner, string? title = null, string? initialFolder = null)\n    {\n        var results = ShowOpenDialog(\n            owner,\n            title,\n            filters: null,\n            FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM,\n            initialFolder);''',
    '''    public static string? PickFolder(\n        IntPtr owner,\n        string? title = null,\n        string? initialFolder = null,\n        string? suggestedFolderName = null)\n    {\n        var results = ShowOpenDialog(\n            owner,\n            title,\n            filters: null,\n            FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM,\n            initialFolder,\n            suggestedFolderName);'''
)

replace_exact(
    'Raven/Helpers/NativeFilePicker.cs',
    '''        FilterSpec[]? filters,\n        uint extraFlags,\n        string? initialFolder = null)''',
    '''        FilterSpec[]? filters,\n        uint extraFlags,\n        string? initialFolder = null,\n        string? suggestedFileName = null)'''
)

replace_exact(
    'Raven/Helpers/NativeFilePicker.cs',
    '''            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))\n            {\n                var iid = typeof(IShellItem).GUID;\n                if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out var folderItem) == 0 && folderItem != null)\n                {\n                    try { dialog.SetFolder(folderItem); dialog.SetDefaultFolder(folderItem); }\n                    finally { Marshal.ReleaseComObject(folderItem); }\n                }\n            }\n\n            if (filters is { Length: > 0 })''',
    '''            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))\n            {\n                var iid = typeof(IShellItem).GUID;\n                if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out var folderItem) == 0 && folderItem != null)\n                {\n                    try { dialog.SetFolder(folderItem); dialog.SetDefaultFolder(folderItem); }\n                    finally { Marshal.ReleaseComObject(folderItem); }\n                }\n            }\n\n            if (!string.IsNullOrWhiteSpace(suggestedFileName))\n                dialog.SetFileName(suggestedFileName);\n\n            if (filters is { Length: > 0 })'''
)
