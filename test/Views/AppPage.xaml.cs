using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using test.Helpers;
using test.Models;
using test.Services;
using test.ViewModels;

namespace test.Views;

public sealed partial class AppPage : Page
{
    public AppViewModel ViewModel { get; }
    public AppInfo AppData { get; set; } = new();
    public UIUpdateService UpdateService { get; }

    private CancellationTokenSource? _cts;
    private StoreEdgeFDProduct? _currentProductInfo;
    private DownloadItem? _activeDownloadItem;

    private static readonly string[] UnpackagedExtensions =
    [
        ".exe",
        ".msi",
    ];

    private static readonly string[] InstallableExtensions =
    [
        ".msix",
        ".appx",
        ".msixbundle",
        ".appxbundle",
    ];

    public AppPage()
    {
        ViewModel = App.GetService<AppViewModel>();
        InitializeComponent();
        UpdateService = new UIUpdateService(this.DispatcherQueue);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var (productInfo, productId) = e.Parameter switch
        {
            StoreEdgeFDProduct p => (p, (string?)null),
            DownloadItem { ProductInfo: not null } d => (d.ProductInfo, (string?)null),
            DownloadItem d => (null, d.ProductId),
            _ => ((StoreEdgeFDProduct?)null, (string?)null),
        };

        if (productInfo != null)
        {
            LoadProduct(productInfo);
        }
        else if (productId != null)
        {
            await FetchAndLoadProductAsync(productId);
        }

        // Always update button state in case navigating back to an active install
        UpdateInstallButtonState();
    }

    private string? GetCurrentStoreToken()
    {
        if (_currentProductInfo == null)
            return null;

        return _currentProductInfo.InstallerType == InstallerType.Unpackaged
            ? _currentProductInfo.Version
            : _currentProductInfo.RevisionId;
    }

    private void LoadProduct(StoreEdgeFDProduct productInfo)
    {
        _currentProductInfo = productInfo;

        AppData.SetValues(
            productInfo.ProductId,
            productInfo.Logo,
            productInfo.Screenshots,
            productInfo.RevisionId,
            productInfo.Title,
            productInfo.PublisherName,
            productInfo.Description,
            productInfo.Rating,
            productInfo.RatingCount,
            productInfo.Size
        );

        var downloadManager = DownloadManagerService.Instance;
        var downloadItem = downloadManager.GetDownload(productInfo.ProductId);
        if (downloadItem != null)
        {
            downloadItem.ProductInfo = productInfo;
            downloadManager.UpdateDownloadRevision(productInfo.ProductId, productInfo.RevisionId);

            if (productInfo.InstallerType == InstallerType.Unpackaged)
            {
                downloadManager.UpdateDownloadStoreVersion(productInfo.ProductId, productInfo.Version);
            }
        }
        SetLoading(false);
        UpdateInstallButtonState();
    }

    private async Task FetchAndLoadProductAsync(string productId)
    {
        SetLoading(true);

        var result = await StoreEdgeFDProduct.GetProductAsync(
            productId,
            DeviceFamily.Desktop,
            Market.US,
            Lang.en
        );

        if (result.IsSuccess)
        {
            var downloadItem = DownloadManagerService.Instance.GetDownload(productId);
            if (downloadItem != null)
            {
                downloadItem.ProductInfo = result.Value;
            }

            LoadProduct(result.Value);
        }
        else
        {
            SetLoading(false);
            await ShowErrorDialogAsync(
                "Error loading app",
                $"Could not load app details: {result.Exception?.Message}"
            );
        }
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        DisplayItem.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInstallButtonState()
    {
        if (_currentProductInfo == null)
            return;

        var productId = _currentProductInfo.ProductId;
        var isUnpackaged = _currentProductInfo.InstallerType == InstallerType.Unpackaged;
        var isInstalled = !isUnpackaged
            && PackagedAppDiscovery.IsInstalled(_currentProductInfo.PackageFamilyName);
        var win32KeyName = _currentProductInfo.PackageFamilyName;
        var win32Info = isUnpackaged
            ? Win32AppDiscovery.GetInstalledInfo(_currentProductInfo.Title)
            : null;
        var isUnpackagedInstalled = isUnpackaged && win32Info?.IsInstalled == true;
        var downloadManager = DownloadManagerService.Instance;
        var downloadItem = downloadManager.GetDownload(productId);
        var isUpdateAvailable = IsUpdateAvailable(downloadItem);

        if (downloadManager.HasActiveDownload(productId))
        {
            SetInstallButtonState(showProgress: true);
            if (downloadItem != null)
            {
                // Bind to the active download item for progress updates
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
                UpdateProgressIndeterminate(downloadItem.Status);
            }
        }
        else if (isInstalled || isUnpackagedInstalled)
        {
            var shouldShowUpdate = isInstalled
                ? isUpdateAvailable
                : IsUnpackagedUpdateAvailable(downloadItem);

            var content = shouldShowUpdate ? "Update" : "Open";
            SetInstallButtonState(content: content, enabled: true, showProgress: false);
        }
        // Only show Retry when the last action itself failed/cancelled.
        // If the user simply uninstalled the app, show Install.
        else if (downloadItem is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed } && !isInstalled)
        {
            SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
        }
        else
        {
            SetInstallButtonState(content: "Install", enabled: true, showProgress: false);
        }
    }

    private bool IsUnpackagedUpdateAvailable(DownloadItem? downloadItem)
    {
        if (_currentProductInfo == null)
            return false;

        // Ensure we have the latest installed version from the registry.
        var installedInfo = Win32AppDiscovery.GetInstalledInfo(
            _currentProductInfo.Title
        );

        // Prefer the Store version already captured on the download item.
        // If the user hasn't downloaded anything yet, fall back to the currently
        // loaded product's Store version so the UI can still show "Update".
        var storeVersion = downloadItem?.StoreVersion ?? _currentProductInfo.Version;
        var localVersion = installedInfo.InstalledVersion;

        if (string.IsNullOrWhiteSpace(storeVersion) || string.IsNullOrWhiteSpace(localVersion))
            return false;

        if (System.Version.TryParse(storeVersion, out var storeV)
            && System.Version.TryParse(localVersion, out var localV))
        {
            return storeV > localV;
        }

        return !string.Equals(storeVersion, localVersion, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUpdateAvailable(DownloadItem? downloadItem)
    {
        if (_currentProductInfo == null)
            return false;

        // Only show "Update" for installed apps when:
        // - We can parse the Store revision date (RevisionId)
        // - We can determine the install timestamp
        // - Installed timestamp is older than the Store revision date
        if (!TryParseStoreRevisionUtc(_currentProductInfo.RevisionId, out var storeRevisionUtc))
            return false;

        var installedUtc = GetInstalledUtc(_currentProductInfo.PackageFamilyName);
        if (installedUtc == null)
            return false;

        return installedUtc.Value < storeRevisionUtc;
    }

    private static bool TryParseStoreRevisionUtc(string? revisionId, out DateTimeOffset revisionUtc)
    {
        revisionUtc = default;
        if (string.IsNullOrWhiteSpace(revisionId))
            return false;

        return DateTimeOffset.TryParse(
            revisionId,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out revisionUtc
        );
    }

    private static DateTimeOffset? GetInstalledUtc(string? packageFamilyName) =>
        PackagedAppDiscovery.GetInstalledUtc(packageFamilyName);

    private bool IsDownloadUpToDate(DownloadItem downloadItem)
    {
        var token = GetCurrentStoreToken();
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (_currentProductInfo?.InstallerType == InstallerType.Unpackaged)
        {
            return string.Equals(downloadItem.StoreVersion, token, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(downloadItem.RevisionId, token, StringComparison.OrdinalIgnoreCase);
    }

    private void BindToDownloadItem(DownloadItem item)
    {
        // Mark that we're observing downloads
        DownloadManagerService.Instance.BeginObserving();

        // Reset and then set initial values directly.
        // This avoids stale UI when reusing the same DownloadItem for a Retry where
        // progress may restart at 0 but not fire PropertyChanged (1% increment logic).
        UpdateService.StopStatusAnimation();
        UpdateService.SetDetails(string.Empty);
        // Do not set UpdateService.SetProgress here; it will be set explicitly after binding

        StatusText.Text = item.StatusText;
        DetailsText.Text = string.IsNullOrWhiteSpace(item.DisplayDetailsText)
            ? string.Empty
            : item.DisplayDetailsText;
        // For installing status, DetailsText should show progress percentage
        if (item.Status == DownloadStatus.Installing)
        {
            DetailsText.Text = $"{(int)Math.Round(item.Progress)}%";
        }
        // Sync progress bar to current value
        UpdateService.SetProgress(item.Progress);
        UpdateProgressIndeterminate(item.Status);

        // Restart animation if installing
        if (item.Status == DownloadStatus.Installing)
        {
            UpdateService.StartStatusAnimation("Installing");
        }

        // Subscribe to property changes for download item
        item.PropertyChanged += OnDownloadItemPropertyChanged;

        // Subscribe to UIUpdateService for status animation
        UpdateService.PropertyChanged += OnUpdateServicePropertyChanged;
    }

    private void UnbindFromDownloadItem()
    {
        if (_activeDownloadItem != null)
        {
            _activeDownloadItem.PropertyChanged -= OnDownloadItemPropertyChanged;
            _activeDownloadItem = null;

            // Stop observing
            DownloadManagerService.Instance.EndObserving();
        }

        // Unsubscribe from UIUpdateService
        UpdateService.PropertyChanged -= OnUpdateServicePropertyChanged;
    }

    private void OnUpdateServicePropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(UIUpdateService.StatusText))
        {
            // Update the StatusText TextBlock when UIUpdateService.StatusText changes (for animation)
            StatusText.Text = UpdateService.StatusText;
        }
    }

    private void OnDownloadItemPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (sender is not DownloadItem item)
            return;

        // Ensure we're on UI thread
        var dispatcherQueue = DispatcherQueue;
        if (dispatcherQueue == null || dispatcherQueue.HasThreadAccess)
        {
            HandleDownloadItemPropertyChange(item, e.PropertyName);
            return;
        }

        dispatcherQueue.TryEnqueue(() => HandleDownloadItemPropertyChange(item, e.PropertyName));
    }

    private void HandleDownloadItemPropertyChange(DownloadItem item, string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(DownloadItem.Progress):
                // Keep progress in sync with the bound UpdateService.
                UpdateService.SetProgress(item.Progress);
                SetProgressIndeterminate(false);
                // Update DetailsText during install progress changes
                if (item.Status == DownloadStatus.Installing)
                {
                    DetailsText.Text = $"{(int)Math.Round(item.Progress)}%";
                }
                break;

            case nameof(DownloadItem.DisplayDetailsText):
                DetailsText.Text = item.DisplayDetailsText;
                break;

            case nameof(DownloadItem.StatusText):
                // Only update StatusText if the animation is NOT running.
                if (!UpdateService.IsStatusAnimationRunning)
                {
                    StatusText.Text = item.StatusText;
                }
                break;

            case nameof(DownloadItem.Status):
                // Clear details when switching phases
                if (item.Status == DownloadStatus.Installing)
                {
                    DetailsText.Text = $"{(int)Math.Round(item.Progress)}%";
                }
                else if (item.Status != DownloadStatus.Downloading)
                {
                    DetailsText.Text = string.Empty;
                }

                UpdateProgressIndeterminate(item.Status);

                if (item.Status is DownloadStatus.Completed)
                {
                    UpdateService.StopStatusAnimation();
                    StatusText.Text = item.StatusText;
                    UnbindFromDownloadItem();
                    UpdateInstallButtonState();
                }
                else if (item.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
                {
                    UpdateService.StopStatusAnimation();
                    StatusText.Text = item.StatusText;
                    UnbindFromDownloadItem();
                    UpdateInstallButtonState();
                }
                break;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UpdateService.StopStatusAnimation();
        UnbindFromDownloadItem();
    }

    private void SetInstallButtonState(
        string content = "Install",
        bool enabled = true,
        bool showProgress = false
    )
    {
        InstallButton.Content = content;
        InstallButton.IsEnabled = enabled;
        InstallButton.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
        ProgressSection.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        if (!showProgress)
            SetProgressIndeterminate(false);

        InstallButton.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["ControlFillColorDisabledBrush"];
    }

    private async Task ShowErrorDialogAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void LeftArrowButton_Click(object sender, RoutedEventArgs e)
    {
        double offset = ScreenshotsScrollViewer.HorizontalOffset - 654;
        ScreenshotsScrollViewer.ChangeView(Math.Max(0, offset), null, null);
    }

    private void RightArrowButton_Click(object sender, RoutedEventArgs e)
    {
        double offset = ScreenshotsScrollViewer.HorizontalOffset + 654;
        ScreenshotsScrollViewer.ChangeView(
            Math.Min(ScreenshotsScrollViewer.ScrollableWidth, offset),
            null,
            null
        );
    }

    private static bool IsInstallablePackage(string path)
    {
        var ext = Path.GetExtension(path);
        return InstallableExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string? PickMainPackage(IEnumerable<string> paths)
    {
        // Prefer bundles first, then single packages.
        var list = paths.Where(IsInstallablePackage).ToList();
        return list.OrderByDescending(p =>
                p.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
            )
            .ThenByDescending(p => p.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.EndsWith(".appx", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static string? PickUnpackagedInstaller(IEnumerable<string> paths)
    {
        var existing = paths
            .Where(p =>
                !string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && UnpackagedExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            )
            .ToList();

        return existing
            .OrderByDescending(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProductInfo == null)
            return;

        var beforeAction = InstallButton.Content?.ToString();

        // Always re-evaluate the current state first.
        // Users can install/uninstall or downloads can complete while staying on this page.
        UpdateInstallButtonState();

        var afterAction = InstallButton.Content?.ToString();
        if (!string.Equals(beforeAction, afterAction, StringComparison.OrdinalIgnoreCase))
        {
            // State changed while the user stayed on this page; only refresh the UI.
            return;
        }

        var productId = _currentProductInfo.ProductId;
        var downloadManager = DownloadManagerService.Instance;
        var isUnpackaged = _currentProductInfo.InstallerType == InstallerType.Unpackaged;

        var action = InstallButton.Content?.ToString();

        // If the button is currently acting as "Open", don't ever start install/download.
        // If the user uninstalled the app while staying on this page, just refresh the UI to "Install".
        if (string.Equals(action, "Open", StringComparison.OrdinalIgnoreCase))
        {
            if (!isUnpackaged)
            {
                await PackagedAppDiscovery.TryLaunchAsync(_currentProductInfo.PackageFamilyName);
                return;
            }

            var launch = await Win32AppDiscovery.TryLaunchDetailedAsync(_currentProductInfo.Title);
            if (!launch.Success)
            {
                var title = "Unable to open app";
                var msg = launch.FailureReason switch
                {
                    Win32AppDiscovery.LaunchFailureReason.NotFoundInRegistry =>
                        "This app was not found in the Windows uninstall registry. It may not be installed.",
                    Win32AppDiscovery.LaunchFailureReason.MissingLaunchTarget =>
                        "The app appears to be installed, but a launch target couldn't be found (Start Menu/DisplayIcon). The app may have been installed incorrectly.",
                    Win32AppDiscovery.LaunchFailureReason.LaunchTargetNotFoundOnDisk =>
                        "The app appears to be installed, but its launch file couldn't be found on disk. The app may have been moved or installed incorrectly.",
                    _ => "The app couldn't be opened. Try reinstalling the app.",
                };

                if (!string.IsNullOrWhiteSpace(launch.InstalledVersion))
                {
                    msg += $"\n\nInstalled version: {launch.InstalledVersion}";
                }

                await ShowErrorDialogAsync(title, msg);
            }
            return;
        }

        var existingDownload = downloadManager.GetDownload(productId);
        UpdateService.SetDetails(string.Empty);
        DetailsText.Text = string.Empty;
        var cacheCandidate = existingDownload;
        if (!string.IsNullOrWhiteSpace(_currentProductInfo.RevisionId))
        {
            downloadManager.UpdateDownloadRevision(productId, _currentProductInfo.RevisionId);
        }

        try
        {
            if (isUnpackaged && cacheCandidate is { HasValidCache: true } && IsDownloadUpToDate(cacheCandidate))
            {
                cacheCandidate.ProductInfo = _currentProductInfo;
                await LaunchUnpackagedInstallerAsync(cacheCandidate);
                return;
            }

            // Always prefer using the on-disk cache when it's up-to-date.
            // This should work even if the current status is Failed/Cancelled after an install attempt.
            if (cacheCandidate != null
                && cacheCandidate.HasValidCache
                && IsDownloadUpToDate(cacheCandidate))
            {
                cacheCandidate.ProductInfo = _currentProductInfo;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                StopButton.IsEnabled = true;

                downloadManager.RegisterCancellationToken(productId, _cts);

                if (
                    downloadManager.IsCancellationRequested(productId)
                    || _cts.Token.IsCancellationRequested
                )
                {
                    HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                    return;
                }

                SetInstallButtonState(showProgress: true);
                var installedFromCache = await TryInstallFromCachedDownloadAsync(cacheCandidate, _cts.Token);

                downloadManager.UnregisterCancellationToken(productId);
                StopButton.IsEnabled = false;

                if (installedFromCache)
                {
                    return;
                }
            }

            SetInstallButtonState(showProgress: true);

            // Only remove cached downloads when the cache is outdated.
            if (existingDownload is { Status: DownloadStatus.Completed } && !IsDownloadUpToDate(existingDownload))
            {
                downloadManager.RemoveDownload(productId);
                existingDownload = null;
            }

            downloadManager.AddDownload(_currentProductInfo);

            // Bind to the new download item
            var downloadItem = downloadManager.GetDownload(productId);
            if (downloadItem != null)
            {
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
            }

            // Always use a fresh CTS per attempt
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            StopButton.IsEnabled = true;

            downloadManager.RegisterCancellationToken(productId, _cts);

            // If cancellation was requested from the Downloads page before we got here,
            // stop early.
            if (
                downloadManager.IsCancellationRequested(productId)
                || _cts.Token.IsCancellationRequested
            )
            {
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            // Clear any leftover details from previous attempts
            UpdateService.SetDetails(string.Empty);
            UpdateService.SetProgress(0);

            // Show fetch phase on both AppPage and DownloadsPage
            SetProgressIndeterminate(true);
            UpdateService.StartStatusAnimation("Fetching download URLs");
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Pending);
            downloadManager.UpdateDownloadProgress(productId, 0);
            downloadManager.UpdateDownloadStatusText(productId, "Fetching download URLs");

            // Smooth dots animation in Downloads list during fetch.
            var fetchAnimator = new test.Helpers.DownloadItemStatusAnimator(
                UpdateService.DispatcherQueue
            );
            if (downloadItem != null)
            {
                fetchAnimator.Start(downloadItem, "Fetching download URLs");
            }

            FileEntry? urls;
            try
            {
                // Ensure the fetch respects cancellation too
                urls = await GetDownloadUrl.fetch(productId, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            // If cancelled during fetch without throwing (e.g. external cancellation request)
            if (
                downloadManager.IsCancellationRequested(productId)
                || _cts.Token.IsCancellationRequested
            )
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
                return;
            }

            if (urls == null)
            {
                UpdateService.StopStatusAnimation();
                if (downloadItem != null)
                {
                    fetchAnimator.Stop(downloadItem);
                }
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                await ShowErrorDialogAsync(
                    "App not supported",
                    "This app isn't supported. Try a different app or check again later"
                );
                return;
            }

            // DownloadHelper manages animation internally; stop current animation first
            UpdateService.StopStatusAnimation();
            if (downloadItem != null)
            {
                fetchAnimator.Stop(downloadItem);
            }

            SetProgressIndeterminate(false);

            await DownloadHelper.StartDownloadAsync(urls, productId, _cts.Token, UpdateService);

            downloadManager.UnregisterCancellationToken(productId);

            StopButton.IsEnabled = false;

            var currentItem = downloadManager.GetDownload(productId);
            if (currentItem?.Status == DownloadStatus.Completed)
            {
                if (isUnpackaged && currentItem != null)
                {
                    await LaunchUnpackagedInstallerAsync(currentItem);
                }
                UnbindFromDownloadItem();
                return;
            }

            if (currentItem?.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
            {
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateService.StopStatusAnimation();
            HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            UpdateService.StopStatusAnimation();
            HandleDownloadError(productId, "Failed to install.", DownloadStatus.Failed);
        }
        finally
        {
            UpdateInstallButtonState();
        }
    }

    private async Task<bool> TryInstallFromCachedDownloadAsync(
        DownloadItem downloadItem,
        CancellationToken token
    )
    {
        if (downloadItem.ProductInfo?.InstallerType == InstallerType.Unpackaged)
            return false;

        var existingFiles = downloadItem
            .DownloadedFilePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .ToList();

        if (existingFiles.Count == 0)
            return false;

        var mainPackagePath = PickMainPackage(existingFiles);
        if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
            return false;

        var depPaths = existingFiles
            .Where(p => !string.Equals(p, mainPackagePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        SetInstallButtonState(showProgress: true);

        _activeDownloadItem = downloadItem;
        BindToDownloadItem(downloadItem);

        UpdateService.SetProgress(0);
        UpdateService.SetDetails(string.Empty);
        DetailsText.Text = string.Empty;
        SetProgressIndeterminate(false);

        var downloadManager = DownloadManagerService.Instance;
        var productId = downloadItem.ProductId;

        downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Installing);
        downloadManager.UpdateDownloadProgress(productId, 0);
        downloadManager.UpdateDownloadStatusText(productId, "Installing");
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);
        downloadManager.UpdateDownloadBytes(productId, null, null);
        UpdateService.StartStatusAnimation("Installing");

        try
        {
            int lastInstallPercent = -1;
            long lastInstallProgressMs = 0;
            const int INSTALL_PROGRESS_THROTTLE_MS = 100;

            var installProgress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                var percent = (int)Math.Clamp(p.Percent, 0, 100);
                var now = Environment.TickCount64;
                if (percent != 100 && percent == lastInstallPercent)
                    return;
                if (percent != 100 && now - lastInstallProgressMs < INSTALL_PROGRESS_THROTTLE_MS)
                    return;

                lastInstallPercent = percent;
                lastInstallProgressMs = now;

                downloadManager.UpdateDownloadProgress(productId, percent);
            });

            await AppPackageInstaller.InstallAsync(
                mainPackagePath,
                dependencyPackagePaths: depPaths,
                progress: installProgress,
                cancellationToken: token
            );

            UpdateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, null);
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Completed);
            return true;
        }
        catch (OperationCanceledException)
        {
            UpdateService.StopStatusAnimation();
            var packageFamilyName = downloadItem.ProductInfo?.PackageFamilyName
                ?? _currentProductInfo?.PackageFamilyName;
            if (PackagedAppDiscovery.IsInstalled(packageFamilyName))
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Completed);
            }
            else
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Cancelled);
            }
            return true;
        }
        catch (Exception ex)
        {
            UpdateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, $"Install failed: {ex.Message}");
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);
            return true;
        }
    }

    private void HandleDownloadError(string productId, string status, DownloadStatus downloadStatus)
    {
        StatusText.Text = status;
        DownloadManagerService.Instance.UpdateDownloadStatus(productId, downloadStatus);
        DownloadManagerService.Instance.UnregisterCancellationToken(productId);
        UnbindFromDownloadItem();
        SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
    }

    private async Task<bool> LaunchUnpackagedInstallerAsync(DownloadItem downloadItem)
    {
        var installerPath = PickUnpackagedInstaller(downloadItem.DownloadedFilePaths);
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            await ShowErrorDialogAsync(
                "Installer missing",
                "The unpackaged installer could not be found on disk."
            );
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = "Manual installation required",
            Content =
                "This app uses an unpackaged installer (EXE/MSI). It will open now and may require additional steps to finish installing.",
            PrimaryButtonText = "Open installer",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return false;

        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
                }
            );
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("Unable to open installer", ex.Message);
            return false;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProductInfo == null)
            return;

        var productId = _currentProductInfo.ProductId;

        // Show cancelling animation on the main status line
        SetProgressIndeterminate(true);
        UpdateService.StartStatusAnimation("Cancelling");

        // Cancel via manager so it works consistently across pages/phases.
        DownloadManagerService.Instance.CancelDownload(productId);

        StopButton.IsEnabled = false;
    }
    private void UpdateProgressIndeterminate(DownloadStatus status)
    {
        SetProgressIndeterminate(status is DownloadStatus.Pending or DownloadStatus.Cancelling);
    }

    private void SetProgressIndeterminate(bool isIndeterminate)
    {
        ProgressBar.IsIndeterminate = isIndeterminate;
    }
}
