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

    private CancellationTokenSource? _productLoadCts;
    private CancellationTokenSource? _downloadCts;
    private ProductData? _currentProductInfo;
    private DownloadItem? _activeDownloadItem;
    private bool _isForceInstalling;
    private int _lightboxIndex;
    private string _naturalAction = "Install";
    private string? _overrideAction;

    private static readonly string[] UnpackagedExtensions = [".exe", ".msi"];

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
        InstallButtonFlyout.Opening += OnInstallButtonFlyoutOpening;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _productLoadCts = new CancellationTokenSource();
        _overrideAction = null;

        var (productInfo, productId, installerType) = e.Parameter switch
        {
            ProductData p => (p, (string?)null, InstallerType.Unknown),
            DownloadItem { ProductInfo: not null } d => (
                d.ProductInfo,
                (string?)null,
                InstallerType.Unknown
            ),
            DownloadItem d => (null, d.ProductId, d.InstallerType),
            _ => ((ProductData?)null, (string?)null, InstallerType.Unknown),
        };

        if (productInfo != null)
        {
            LoadProduct(productInfo);
        }
        else if (productId != null)
        {
            await FetchAndLoadProductAsync(productId, installerType);
        }

        UpdateInstallButtonState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _productLoadCts?.Cancel();
        _productLoadCts?.Dispose();
        _productLoadCts = null;

        _isForceInstalling = false;
        _overrideAction = null;
        LightboxOverlay.Visibility = Visibility.Collapsed;
        UpdateService.StopStatusAnimation();
        UnbindFromDownloadItem();
    }

    private void LoadProduct(ProductData productInfo)
    {
        _currentProductInfo = productInfo;

        AppData.SetValues(
            productInfo.ProductId,
            productInfo.Logo,
            productInfo.Screenshots,
            productInfo.RevisionId,
            productInfo.Version,
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
            if (productInfo.RevisionId != null)
            {
                downloadManager.UpdateDownloadRevision(
                    productInfo.ProductId,
                    productInfo.RevisionId
                );
            }

            if (productInfo.InstallerType == InstallerType.Unpackaged)
            {
                downloadManager.UpdateDownloadStoreVersion(
                    productInfo.ProductId,
                    productInfo.Version
                );
            }
        }

        SetLoading(false);
        UpdateInstallButtonState();
    }

    private async Task FetchAndLoadProductAsync(string productId, InstallerType installerType)
    {
        SetLoading(true);

        try
        {
            var product = await Utils.ProductOrBundle(
                productId,
                installerType,
                _productLoadCts?.Token ?? default
            );

            var downloadItem = DownloadManagerService.Instance.GetDownload(productId);
            if (downloadItem != null)
            {
                downloadItem.ProductInfo = product.ProductInfo;
            }

            LoadProduct(product.ProductInfo);
        }
        catch (Exception ex)
        {
            SetLoading(false);
            await ShowErrorDialogAsync(
                "Error loading app",
                $"Could not load app details: {ex.Message}"
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
        var isInstalled = isUnpackaged
            ? IsUnpackagedInstalled(_currentProductInfo)
            : IsPackagedInstalled(_currentProductInfo);
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
        // Only show Retry when the last action itself failed/cancelled — regardless of
        // whether the app is installed (covers failed/cancelled update attempts too).
        else if (
            downloadItem is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed }
        )
        {
            _naturalAction = "Retry";
            SetInstallButtonState(
                content: _overrideAction ?? _naturalAction,
                enabled: true,
                showProgress: false
            );
        }
        else if (isInstalled)
        {
            var shouldShowUpdate = isUnpackaged
                ? IsUnpackagedUpdateAvailable(downloadItem)
                : isUpdateAvailable;

            _naturalAction = shouldShowUpdate ? "Update" : "Open";
            SetInstallButtonState(
                content: _overrideAction ?? _naturalAction,
                enabled: true,
                showProgress: false
            );
        }
        else
        {
            _naturalAction = "Install";
            SetInstallButtonState(
                content: _overrideAction ?? _naturalAction,
                enabled: true,
                showProgress: false
            );
        }
    }

    private static bool IsPackagedInstalled(ProductData product) =>
        PackagedAppDiscovery.IsInstalled(product.PackageFamilyName);

    private static bool IsUnpackagedInstalled(ProductData product) =>
        Win32AppDiscovery.GetInstalledInfo(product.Title).IsInstalled;

    private bool IsUnpackagedUpdateAvailable(DownloadItem? downloadItem)
    {
        if (_currentProductInfo == null)
            return false;

        // Ensure we have the latest installed version from the registry.
        var installedInfo = Win32AppDiscovery.GetInstalledInfo(_currentProductInfo.Title);

        // Prefer the Store version already captured on the download item.
        // If the user hasn't downloaded anything yet, fall back to the currently
        // loaded product's Store version so the UI can still show "Update".
        var storeVersion = downloadItem?.StoreVersion ?? AppData.Version;
        var localVersion = installedInfo.InstalledVersion;

        if (string.IsNullOrWhiteSpace(storeVersion) || string.IsNullOrWhiteSpace(localVersion))
            return false;

        if (
            System.Version.TryParse(storeVersion, out var storeV)
            && System.Version.TryParse(localVersion, out var localV)
        )
        {
            return storeV > localV;
        }

        return !string.Equals(storeVersion, localVersion, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUpdateAvailable(DownloadItem? downloadItem)
    {
        if (_currentProductInfo == null)
            return false;

        var storeVersion = downloadItem?.StoreVersion ?? AppData.Version;
        var installedVersion = PackagedAppDiscovery.GetInstalledVersion(
            _currentProductInfo.PackageFamilyName
        );

        return VersionComparison.IsStoreNewer(storeVersion, installedVersion);
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
                UpdateProgressIndeterminate(item.Status);
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
                    _overrideAction = null;
                    UpdateInstallButtonState();
                }
                else if (item.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
                {
                    UpdateService.StopStatusAnimation();
                    StatusText.Text = item.StatusText;
                    if (item.Status == DownloadStatus.Failed && !_isForceInstalling)
                    {
                        _ = ShowInstallationFailedPopupIfAvailableAsync(item);
                    }
                    UnbindFromDownloadItem();
                    _overrideAction = null;
                    UpdateInstallButtonState();
                }
                break;
        }
    }

    private bool IsCurrentProduct(DownloadItem item) =>
        _currentProductInfo != null
        && string.Equals(
            _currentProductInfo.ProductId,
            item.ProductId,
            StringComparison.OrdinalIgnoreCase
        );

    private async Task ShowInstallationFailedPopupIfAvailableAsync(DownloadItem item)
    {
        if (item.LastInstallError == null)
            return;

        try
        {
            var mainPackagePath = PickMainPackage(item.DownloadedFilePaths);

            if (IsCurrentProduct(item))
            {
                var force = await InstallHelper.ShowInstallationErrorOrForceInstallDialogAsync(
                    this.Content.XamlRoot,
                    "Installation failed",
                    item.LastInstallError
                );

                if (force && !string.IsNullOrWhiteSpace(mainPackagePath))
                {
                    await RetryForceInstallAsync(item, mainPackagePath);
                    return;
                }
            }
        }
        finally
        {
            // prevent repeating the same popup if state updates fire again
            item.LastInstallError = null;
        }
    }

    private async Task RetryForceInstallAsync(DownloadItem item, string mainPackagePath)
    {
        var productId = item.ProductId;
        var downloadManager = DownloadManagerService.Instance;

        _isForceInstalling = true;

        // Ensure we're observing so status/progress changes propagate immediately.
        if (!ReferenceEquals(_activeDownloadItem, item))
        {
            UnbindFromDownloadItem();
            _activeDownloadItem = item;
            BindToDownloadItem(item);
        }

        // Clear any previous install error so status handler can't pick it up.
        item.LastInstallError = null;

        // Reflect install retry in UI + Downloads page.
        downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Installing);
        downloadManager.UpdateDownloadProgress(productId, 0);
        // Use the override for animated dots in the Downloads list.
        downloadManager.UpdateDownloadStatusText(productId, "Installing");
        try
        {
            downloadManager.UpdateDownloadBytes(productId, null, null);
        }
        catch { }

        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        UpdateService.SetProgress(0);
        UpdateService.SetDetails(string.Empty);
        StatusText.Text = "Installing";
        DetailsText.Text = "0%";
        SetInstallButtonState(showProgress: true);
        SetProgressIndeterminate(false);
        UpdateService.StartStatusAnimation("Installing");

        var depPaths = item
            .DownloadedFilePaths.Where(p =>
                !string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && !string.Equals(p, mainPackagePath, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        try
        {
            var installProgress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                var percent = (int)Math.Clamp(p.Percent, 0, 100);
                downloadManager.UpdateDownloadProgress(productId, percent);
            });

            await AppPackageInstaller.InstallAsync(
                mainPackagePath,
                dependencyPackagePaths: depPaths,
                progress: installProgress,
                ignoreVersion: true
            );

            UpdateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, null);
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Completed);
        }
        catch (Exception ex)
        {
            UpdateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, $"Install failed: {ex.Message}");
            downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);

            // Force install already failed: do not re-offer force install.
            await InstallHelper.ShowInstallationErrorDialogAsync(
                this.Content.XamlRoot,
                "Installation failed",
                ex
            );
        }
        finally
        {
            _isForceInstalling = false;
            _overrideAction = null;
            UpdateInstallButtonState();
        }
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
        {
            SetProgressIndeterminate(false);
            UpdateInstallButtonFlyout();
        }

        InstallButton.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)
                Application.Current.Resources["ControlFillColorDisabledBrush"];
    }

    private void UpdateInstallButtonFlyout()
    {
        InstallButtonFlyout.Items.Clear();

        var currentAction = InstallButton.Content?.ToString() ?? _naturalAction;
        IEnumerable<string> options;

        if (string.Equals(currentAction, "Download", StringComparison.OrdinalIgnoreCase))
        {
            // "Retry" is not a user-facing mode name; resolve it to its real equivalent
            // ("Update" if an update is available, otherwise "Install").
            var naturalForDropdown = string.Equals(
                _naturalAction,
                "Retry",
                StringComparison.OrdinalIgnoreCase
            )
                ? ResolveRetryAction()
                : _naturalAction;

            // Show the natural action itself plus its sub-options, excluding "Download"
            options = new[] { naturalForDropdown }.Concat(
                GetFlyoutItemsForAction(naturalForDropdown)
                    .Where(o =>
                        !string.Equals(o, "Download", StringComparison.OrdinalIgnoreCase)
                    )
            );

            // When retrying and the app is still installed, ensure "Open" is offered
            // (e.g. reinstall was cancelled but the old version is untouched).
            if (
                string.Equals(_naturalAction, "Retry", StringComparison.OrdinalIgnoreCase)
                && _currentProductInfo != null
            )
            {
                var isUnp = _currentProductInfo.InstallerType == InstallerType.Unpackaged;
                var stillInstalled = isUnp
                    ? IsUnpackagedInstalled(_currentProductInfo)
                    : IsPackagedInstalled(_currentProductInfo);
                if (
                    stillInstalled
                    && !options.Any(o =>
                        string.Equals(o, "Open", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    options = options.Append("Open");
            }
        }
        else if (
            string.Equals(currentAction, "Open", StringComparison.OrdinalIgnoreCase)
            && _overrideAction != null
        )
        {
            // "Open" is an override (e.g. natural action is "Update"); show the natural action
            // and its sub-options, excluding "Open" itself.
            var naturalForDropdown = string.Equals(
                _naturalAction,
                "Retry",
                StringComparison.OrdinalIgnoreCase
            )
                ? ResolveRetryAction()
                : _naturalAction;

            options = new[] { naturalForDropdown }.Concat(
                GetFlyoutItemsForAction(naturalForDropdown)
                    .Where(o =>
                        !string.Equals(o, "Open", StringComparison.OrdinalIgnoreCase)
                    )
            );
        }
        else if (string.Equals(currentAction, "Retry", StringComparison.OrdinalIgnoreCase))
        {
            // Retry IS the last action, so only offer alternatives.
            // Always append "Open" when the app is still installed (e.g. failed update).
            var retryItem = _currentProductInfo != null
                ? DownloadManagerService.Instance.GetDownload(_currentProductInfo.ProductId)
                : null;
            var isUnpackaged = _currentProductInfo?.InstallerType == InstallerType.Unpackaged;
            var isInstalled =
                _currentProductInfo != null
                && (
                    isUnpackaged
                        ? IsUnpackagedInstalled(_currentProductInfo)
                        : IsPackagedInstalled(_currentProductInfo)
                );

            if (retryItem?.WasDownloadOnly ?? false)
            {
                // WasDownloadOnly=true → retry = download → offer Update/Install, plus Open if installed
                var resolvedAction = ResolveRetryAction();
                options = isInstalled
                    ? new[] { resolvedAction, "Open" }
                    : [resolvedAction];
            }
            else
            {
                // WasDownloadOnly=false → retry = install/update → offer Download, plus Open if installed
                options = isInstalled ? ["Download", "Open"] : ["Download"];
            }
        }
        else
        {
            options = GetFlyoutItemsForAction(currentAction);
            // If Install is a force-reinstall/retry override and the app is currently installed, also offer Open
            if (string.Equals(currentAction, "Install", StringComparison.OrdinalIgnoreCase))
            {
                var shouldOfferOpen = _naturalAction is "Open" or "Update";
                if (
                    !shouldOfferOpen
                    && string.Equals(_naturalAction, "Retry", StringComparison.OrdinalIgnoreCase)
                    && _currentProductInfo != null
                )
                {
                    var isUnp = _currentProductInfo.InstallerType == InstallerType.Unpackaged;
                    shouldOfferOpen = isUnp
                        ? IsUnpackagedInstalled(_currentProductInfo)
                        : IsPackagedInstalled(_currentProductInfo);
                }
                if (shouldOfferOpen)
                    options = options.Prepend("Open");
            }
        }

        var optionList = options.ToList();
        for (var i = 0; i < optionList.Count; i++)
        {
            if (i > 0)
                InstallButtonFlyout.Items.Add(new MenuFlyoutSeparator());

            var option = optionList[i];
            var item = new MenuFlyoutItem
            {
                Text = option,
                MinHeight = 44,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(12, 8, 12, 8),
                FontSize = InstallButton.FontSize,
            };
            var captured = option;
            item.Click += (_, _) => OnInstallDropdownOptionSelected(captured);
            InstallButtonFlyout.Items.Add(item);
        }
    }

    // Resolves the "Retry" natural action to its user-facing equivalent.
    // Returns "Update" when an update is available for the installed app, otherwise "Install".
    private string ResolveRetryAction()
    {
        if (_currentProductInfo == null)
            return "Install";

        var retryItem = DownloadManagerService.Instance.GetDownload(_currentProductInfo.ProductId);
        var isUnpackaged = _currentProductInfo.InstallerType == InstallerType.Unpackaged;
        var hasUpdate = isUnpackaged
            ? IsUnpackagedUpdateAvailable(retryItem)
            : IsUpdateAvailable(retryItem);
        return hasUpdate ? "Update" : "Install";
    }

    private void OnInstallButtonFlyoutOpening(object? sender, object e)
    {
        // ActualWidth is now correct because layout has completed before the flyout opens.
        var width = InstallButton.ActualWidth;
        if (width <= 0)
            return;
        foreach (var item in InstallButtonFlyout.Items.OfType<MenuFlyoutItem>())
            item.MinWidth = width;
    }

    private static IEnumerable<string> GetFlyoutItemsForAction(string action) =>
        action switch
        {
            "Open" => ["Install", "Download"],
            "Update" => ["Open", "Download"],
            "Install" => ["Download"],
            "Retry" => ["Download"],
            _ => [],
        };

    private void OnInstallDropdownOptionSelected(string option)
    {
        _overrideAction = string.Equals(option, _naturalAction, StringComparison.OrdinalIgnoreCase)
            ? null
            : option;

        SetInstallButtonState(
            content: _overrideAction ?? _naturalAction,
            enabled: true,
            showProgress: false
        );
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

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Check Update",
            Content = "Checking for updates...",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
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

    private async void InstallButton_Click(SplitButton sender, SplitButtonClickEventArgs e)
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

        // For Retry, repeat whatever the user last attempted (persisted on the DownloadItem).
        var existingItem = downloadManager.GetDownload(productId);
        var isDownloadOnly =
            string.Equals(action, "Download", StringComparison.OrdinalIgnoreCase)
            || (
                string.Equals(action, "Retry", StringComparison.OrdinalIgnoreCase)
                && (existingItem?.WasDownloadOnly ?? false)
            );

        // If the button is currently acting as "Open", don't ever start install/download.
        // If the user uninstalled the app while staying on this page, just refresh the UI to "Install".
        if (string.Equals(action, "Open", StringComparison.OrdinalIgnoreCase))
        {
            await TryOpenCurrentAppAsync();
            return;
        }

        UpdateService.SetDetails(string.Empty);
        DetailsText.Text = string.Empty;
        if (!string.IsNullOrWhiteSpace(_currentProductInfo.RevisionId))
        {
            downloadManager.UpdateDownloadRevision(productId, _currentProductInfo.RevisionId);
        }

        try
        {
            SetInstallButtonState(showProgress: true);

            downloadManager.AddDownload(_currentProductInfo);

            // Bind to the new download item
            var downloadItem = downloadManager.GetDownload(productId);
            if (downloadItem != null)
            {
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
                // Persist the action mode so Retry survives an app restart.
                downloadItem.WasDownloadOnly = isDownloadOnly;
            }

            // Always use a fresh CTS per download attempt
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            StopButton.IsEnabled = true;

            downloadManager.RegisterCancellationToken(productId, _downloadCts);

            // If cancellation was requested from the Downloads page before we got here,
            // stop early.
            if (
                downloadManager.IsCancellationRequested(productId)
                || _downloadCts.Token.IsCancellationRequested
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
                urls = await GetDownloadUrl.fetch(
                    productId,
                    _currentProductInfo.InstallerType,
                    _downloadCts.Token,
                    ignoreDependencyFilter: IgnoreDependencyFilterToggle.IsChecked
                );
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
                || _downloadCts.Token.IsCancellationRequested
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

            await DownloadHelper.StartDownloadAsync(
                urls,
                productId,
                _downloadCts.Token,
                UpdateService,
                downloadOnly: isDownloadOnly
            );

            downloadManager.UnregisterCancellationToken(productId);

            StopButton.IsEnabled = false;

            var currentItem = downloadManager.GetDownload(productId);
            if (currentItem?.Status == DownloadStatus.Completed)
            {
                if (isUnpackaged && !isDownloadOnly && currentItem != null)
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
            _overrideAction = null;
            UpdateInstallButtonState();
        }
    }

    private async Task TryOpenCurrentAppAsync()
    {
        if (_currentProductInfo == null)
            return;

        if (_currentProductInfo.InstallerType != InstallerType.Unpackaged)
        {
            var launch = await PackagedAppDiscovery.TryLaunchDetailedAsync(
                _currentProductInfo.PackageFamilyName
            );

            if (!launch.Success)
            {
                var msg = launch.FailureReason switch
                {
                    PackagedAppDiscovery.LaunchFailureReason.NotInstalled =>
                        "This app package was not found in the Windows package manager. It may not be installed.",
                    PackagedAppDiscovery.LaunchFailureReason.NoAppEntries =>
                        "The app package is installed, but no launchable app entries were found.",
                    _ => "The app couldn't be opened. Try reinstalling the app.",
                };

                await ShowErrorDialogAsync("Unable to open app", msg);
            }

            return;
        }

        var win32Launch = await Win32AppDiscovery.TryLaunchDetailedAsync(_currentProductInfo.Title);
        if (!win32Launch.Success)
        {
            var title = "Unable to open app";
            var msg = win32Launch.FailureReason switch
            {
                Win32AppDiscovery.LaunchFailureReason.NotFoundInRegistry =>
                    "This app was not found in the Windows uninstall registry. It may not be installed.",
                Win32AppDiscovery.LaunchFailureReason.MissingLaunchTarget =>
                    "The app appears to be installed, but a launch target couldn't be found (Start Menu/DisplayIcon). The app may have been installed incorrectly.",
                Win32AppDiscovery.LaunchFailureReason.LaunchTargetNotFoundOnDisk =>
                    "The app appears to be installed, but its launch file couldn't be found on disk. The app may have been moved or installed incorrectly.",
                _ => "The app couldn't be opened. Try reinstalling the app.",
            };

            if (!string.IsNullOrWhiteSpace(win32Launch.InstalledVersion))
            {
                msg += $"\n\nInstalled version: {win32Launch.InstalledVersion}";
            }

            await ShowErrorDialogAsync(title, msg);
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
                    WorkingDirectory =
                        Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
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

    private void ScreenshotRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args
    )
    {
        if (args.Element is Button btn)
        {
            btn.Tag = args.Index;
            btn.Click -= ScreenshotButton_Click;
            btn.Click += ScreenshotButton_Click;
        }
    }

    private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
            OpenLightbox(index);
    }

    private void OpenLightbox(int index)
    {
        if (AppData.Screenshots.Count == 0)
            return;

        _lightboxIndex = Math.Clamp(index, 0, AppData.Screenshots.Count - 1);
        UpdateLightbox();
        LightboxOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateLightbox()
    {
        var screenshots = AppData.Screenshots;
        var screenshot = screenshots[_lightboxIndex];

        if (!string.IsNullOrWhiteSpace(screenshot.Url))
        {
            LightboxImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri(screenshot.Url)
            );
        }

        LightboxCounter.Text = $"{_lightboxIndex + 1} / {screenshots.Count}";
        LightboxLeftButton.IsEnabled = _lightboxIndex > 0;
        LightboxRightButton.IsEnabled = _lightboxIndex < screenshots.Count - 1;
    }

    private void LightboxClose_Click(object sender, RoutedEventArgs e)
    {
        LightboxOverlay.Visibility = Visibility.Collapsed;
    }

    private void LightboxBackdrop_Tapped(
        object sender,
        Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e
    )
    {
        LightboxOverlay.Visibility = Visibility.Collapsed;
    }

    private void LightboxLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxIndex > 0)
        {
            _lightboxIndex--;
            UpdateLightbox();
        }
    }

    private void LightboxRight_Click(object sender, RoutedEventArgs e)
    {
        if (_lightboxIndex < AppData.Screenshots.Count - 1)
        {
            _lightboxIndex++;
            UpdateLightbox();
        }
    }

    private void LightboxNavArea_Tapped(
        object sender,
        Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e
    )
    {
        e.Handled = true;
    }
}
