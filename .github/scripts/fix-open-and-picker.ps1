$ErrorActionPreference='Stop'
function R([string]$p,[string]$o,[string]$n){$t=[IO.File]::ReadAllText($p);if(-not $t.Contains($o)){throw "anchor not found in $p"};[IO.File]::WriteAllText($p,$t.Replace($o,$n))}

$p='Raven/Views/AppPage.xaml.cs'
R $p @'
        var isInstalled = isUnpackaged
            ? IsUnpackagedInstalled(_currentProductInfo)
            : IsPackagedInstalled(_currentProductInfo);
'@ @'
        var isInstalled = isUnpackaged
            ? IsUnpackagedInstalled(_currentProductInfo)
            : IsPackagedInstalled(_currentProductInfo) || PortableLaunchRegistry.Exists(productId);
'@
R $p @'
                                var result = await PortableMsixLauncher.ExtractAndLaunchAsync(
                                    mainPackagePath,
                                    dependencyPaths,
                                    _currentProductInfo.Title,
                                    productId,
                                    _downloadCts.Token
                                );

                                UpdateService.SetDetails($"Portable folder: {result.ExtractDirectory}");
'@ @'
                                var result = await PortableMsixLauncher.ExtractAndLaunchAsync(
                                    mainPackagePath,
                                    dependencyPaths,
                                    _currentProductInfo.Title,
                                    productId,
                                    _downloadCts.Token
                                );
                                PortableLaunchRegistry.Save(productId, result.ExecutablePath, result.ExtractDirectory);

                                UpdateService.SetDetails($"Portable folder: {result.ExtractDirectory}");
'@
R $p @'
        if (_currentProductInfo.InstallerType != InstallerType.Unpackaged)
        {
            var launch = await PackagedAppDiscovery.TryLaunchDetailedAsync(
'@ @'
        if (_currentProductInfo.InstallerType != InstallerType.Unpackaged)
        {
            if (PortableLaunchRegistry.TryLaunch(_currentProductInfo.ProductId))
                return;

            var launch = await PackagedAppDiscovery.TryLaunchDetailedAsync(
'@

$p='Raven/Helpers/PortableMsixLauncher.cs'
R $p @'
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var installBaseFolder = NativeFilePicker.PickFolder(hwnd, "Choose installation folder");
'@ @'
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var defaultBaseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raven",
            "PortableApps");
        Directory.CreateDirectory(defaultBaseFolder);
        var installBaseFolder = NativeFilePicker.PickFolder(hwnd, "Choose installation folder", defaultBaseFolder);
'@

$p='Raven/Helpers/NativeFilePicker.cs'
R $p @'
    public static string? PickFolder(IntPtr owner, string? title = null)
    {
        var results = ShowOpenDialog(
            owner,
            title,
            filters: null,
            FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
'@ @'
    public static string? PickFolder(IntPtr owner, string? title = null, string? initialFolder = null)
    {
        var results = ShowOpenDialog(
            owner,
            title,
            filters: null,
            FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM,
            initialFolder);
'@
R $p @'
        FilterSpec[]? filters,
        uint extraFlags)
'@ @'
        FilterSpec[]? filters,
        uint extraFlags,
        string? initialFolder = null)
'@
R $p @'
            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            if (filters is { Length: > 0 })
'@ @'
            if (!string.IsNullOrEmpty(title))
                dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out var folderItem) == 0 && folderItem != null)
                {
                    try { dialog.SetFolder(folderItem); dialog.SetDefaultFolder(folderItem); }
                    finally { Marshal.ReleaseComObject(folderItem); }
                }
            }

            if (filters is { Length: > 0 })
'@
R $p @'
    // ---------------------------------------------------------------
    //  Constants
'@ @'
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    // ---------------------------------------------------------------
    //  Constants
'@
