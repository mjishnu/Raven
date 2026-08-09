$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) {
        throw "Patch anchor not found in $Path`n--- anchor ---`n$Old"
    }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$appPage = 'Raven/Views/AppPage.xaml.cs'
$portable = 'Raven/Helpers/PortableMsixLauncher.cs'

Replace-Exact $appPage @'
        // Portable mode: packaged apps are downloaded, unpacked, and launched instead of registered.
        "Install" => "Download & Run",
        "Update" => "Update & Run",
        "Open" => "AppPage_Btn_Open".GetLocalized(),
'@ @'
        "Install" => "Install",
        "Run" => "Run",
        "Update" => "Update",
        "Open" => "AppPage_Btn_Open".GetLocalized(),
'@

Replace-Exact $appPage @'
    private static IEnumerable<string> GetFlyoutItemsForAction(string action) =>
        action switch
        {
            "Open" => ["Install", "Download"],
            "Update" => ["Open", "Download"],
            "Install" => ["Download"],
            "Retry" => ["Download"],
            _ => [],
        };
'@ @'
    private static IEnumerable<string> GetFlyoutItemsForAction(string action) =>
        action switch
        {
            "Open" => ["Install", "Run", "Download"],
            "Update" => ["Open", "Run", "Download"],
            "Install" => ["Run", "Download"],
            "Run" => ["Install", "Download"],
            "Retry" => ["Run", "Download"],
            _ => [],
        };
'@

Replace-Exact $appPage @'
        var action = CurrentActionKey;

        // For Retry, repeat whatever the user last attempted (persisted on the DownloadItem).
'@ @'
        var action = CurrentActionKey;
        var isRunAction = string.Equals(action, "Run", StringComparison.OrdinalIgnoreCase);

        // For Retry, repeat whatever the user last attempted (persisted on the DownloadItem).
'@

Replace-Exact $appPage @'
                else if (!isUnpackaged && !isDownloadOnly && currentItem != null)
                {
                    var mainPackagePath = PickMainPackage(currentItem.DownloadedFiles);
                    if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
                    {
                        await ShowErrorDialogAsync(
                            "Portable launch failed",
                            "The Microsoft Store package was downloaded, but Raven could not identify the main MSIX/AppX file."
                        );
                    }
                    else
                    {
                        try
                        {
                            UpdateService.SetDetails("Unpacking package...");
                            DetailsText.Text = "Unpacking package...";

                            var dependencyPaths = currentItem.DownloadedFiles
                                .Where(f => !string.Equals(f.Path, mainPackagePath, StringComparison.OrdinalIgnoreCase))
                                .Select(f => f.Path)
                                .ToList();

                            var result = await PortableMsixLauncher.ExtractAndLaunchAsync(
                                mainPackagePath,
                                dependencyPaths,
                                _currentProductInfo.Title,
                                productId,
                                _downloadCts.Token
                            );

                            UpdateService.SetDetails($"Portable folder: {result.ExtractDirectory}");
                            DetailsText.Text = $"Portable folder: {result.ExtractDirectory}";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Portable extraction/launch failed | ProductId={ProductId} | Package={Package}",
                                productId,
                                mainPackagePath
                            );

                            await ShowErrorDialogAsync(
                                "Portable launch failed",
                                ex.Message
                            );
                        }
                    }
                }
'@ @'
                else if (!isUnpackaged && !isDownloadOnly && currentItem != null)
                {
                    var mainPackagePath = PickMainPackage(currentItem.DownloadedFiles);
                    if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
                    {
                        await ShowErrorDialogAsync(
                            isRunAction ? "Portable launch failed" : "Installation failed",
                            "The Microsoft Store package was downloaded, but Raven could not identify the main MSIX/AppX file."
                        );
                    }
                    else
                    {
                        var dependencyPaths = currentItem.DownloadedFiles
                            .Where(f => !string.Equals(f.Path, mainPackagePath, StringComparison.OrdinalIgnoreCase))
                            .Select(f => f.Path)
                            .Where(File.Exists)
                            .ToList();

                        if (isRunAction)
                        {
                            try
                            {
                                UpdateService.SetDetails("Choose install folder...");
                                DetailsText.Text = "Choose install folder...";

                                var result = await PortableMsixLauncher.ExtractAndLaunchAsync(
                                    mainPackagePath,
                                    dependencyPaths,
                                    _currentProductInfo.Title,
                                    productId,
                                    _downloadCts.Token
                                );

                                UpdateService.SetDetails($"Portable folder: {result.ExtractDirectory}");
                                DetailsText.Text = $"Portable folder: {result.ExtractDirectory}";
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    ex,
                                    "Portable extraction/launch failed | ProductId={ProductId} | Package={Package}",
                                    productId,
                                    mainPackagePath
                                );
                                await ShowErrorDialogAsync("Portable launch failed", ex.Message);
                            }
                        }
                        else
                        {
                            try
                            {
                                UpdateService.SetDetails("Installing package...");
                                DetailsText.Text = "Installing package...";
                                var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
                                    downloadManager.UpdateDownloadProgress(productId, Math.Clamp(p.Percent, 0, 100)));
                                await AppPackageInstaller.InstallAsync(
                                    mainPackagePath,
                                    dependencyPackagePaths: dependencyPaths,
                                    progress: progress,
                                    installDependenciesSeparately: InstallDependenciesSeparatelyToggle.IsChecked
                                );
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Package installation failed | ProductId={ProductId}", productId);
                                await InstallHelper.ShowInstallationErrorDialogAsync(
                                    this.Content.XamlRoot,
                                    "Install_Dialog_Title".GetLocalized(),
                                    ex
                                );
                            }
                        }
                    }
                }
'@

Replace-Exact $appPage @'
    private void MoreOptionsFlyout_Opening(object? sender, object e)
    {
        var width = MoreOptionsButton.ActualWidth;
        if (width <= 0)
            return;
        if (sender is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
                item.MinWidth = width;
        }
    }
'@ @'
    private void MoreOptionsFlyout_Opening(object? sender, object e)
    {
        var width = MoreOptionsButton.ActualWidth;
        if (sender is MenuFlyout flyout)
        {
            if (!flyout.Items.OfType<MenuFlyoutItem>().Any(i => Equals(i.Tag, "RavenOpenFolder")))
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                var openFolderItem = new MenuFlyoutItem
                {
                    Text = "Open Folder",
                    Tag = "RavenOpenFolder",
                    MinHeight = 44,
                };
                openFolderItem.Click += (_, _) => OpenPortableShortcutFolder();
                flyout.Items.Add(openFolderItem);
            }

            if (width > 0)
            {
                foreach (var item in flyout.Items.OfType<MenuFlyoutItem>())
                    item.MinWidth = width;
            }
        }
    }

    private static void OpenPortableShortcutFolder()
    {
        var folder = PortableMsixLauncher.GetShortcutFolder();
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }
'@

Replace-Exact $portable @'
        var root = GetPortableRoot(appTitle, packageKey);
        var appDir = Path.Combine(root, "App");
'@ @'
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var installBaseFolder = NativeFilePicker.PickFolder(hwnd, "Choose installation folder");
        if (string.IsNullOrWhiteSpace(installBaseFolder))
            throw new OperationCanceledException("No installation folder was selected.");

        var root = GetPortableRoot(appTitle, packageKey, installBaseFolder);
        var appDir = Path.Combine(root, "App");
'@

Replace-Exact $portable @'
    public static string GetPortableRoot(string appTitle, string packageKey)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raven",
            "PortableApps"
        );

        var name = MakeSafeFileName(string.IsNullOrWhiteSpace(appTitle) ? "App" : appTitle);
        var key = MakeSafeFileName(string.IsNullOrWhiteSpace(packageKey) ? "local" : packageKey);
        return Path.Combine(baseDir, $"{name}_{key}");
    }
'@ @'
    public static string GetPortableRoot(string appTitle, string packageKey, string? baseFolder = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Raven",
                "PortableApps")
            : Path.GetFullPath(baseFolder);

        var name = MakeSafeFileName(string.IsNullOrWhiteSpace(appTitle) ? "App" : appTitle);
        var key = MakeSafeFileName(string.IsNullOrWhiteSpace(packageKey) ? "local" : packageKey);
        return Path.Combine(baseDir, $"{name}_{key}");
    }

    public static string GetShortcutFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        "Raven Portable Apps");
'@

Replace-Exact $portable @'
        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "Raven Portable Apps"
        );
'@ @'
        var programs = GetShortcutFolder();
'@

Write-Host 'Feature patch applied successfully.'
