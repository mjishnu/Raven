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
            _ => ((StoreEdgeFDProduct?)null, (string?)null)
        };

        if (productInfo != null)
        {
            LoadProduct(productInfo);
        }
        else if (productId != null)
        {
            await FetchAndLoadProductAsync(productId);
        }
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
            await ShowErrorDialogAsync("Error loading app", $"Could not load app details: {result.Exception?.Message}");
        }
    }

    private void SetLoading(bool isLoading)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        DisplayItem.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateInstallButtonState()
    {
        if (_currentProductInfo == null) return;

        var productId = _currentProductInfo.ProductId;
        var downloadManager = DownloadManagerService.Instance;
        var downloadItem = downloadManager.GetDownload(productId);

        if (downloadManager.IsDownloaded(productId))
        {
            SetInstallButtonState(content: "Installed", enabled: false, showProgress: false);
        }
        else if (downloadManager.HasActiveDownload(productId))
        {
            SetInstallButtonState(showProgress: true);
            if (downloadItem != null)
            {
                // Bind to the active download item for progress updates
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
            }
        }
        else if (downloadItem is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed })
        {
            SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
        }
    }

    private void BindToDownloadItem(DownloadItem item)
    {
        // Set initial values
        ProgressBar.Value = item.Progress;
        StatusText.Text = item.StatusText;
        
        // Subscribe to property changes
        item.PropertyChanged += OnDownloadItemPropertyChanged;
    }

    private void UnbindFromDownloadItem()
    {
        if (_activeDownloadItem != null)
        {
            _activeDownloadItem.PropertyChanged -= OnDownloadItemPropertyChanged;
            _activeDownloadItem = null;
        }
    }

    private void OnDownloadItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(DownloadItem.Progress):
                    ProgressBar.Value = item.Progress;
                    break;
                case nameof(DownloadItem.StatusText):
                    StatusText.Text = item.StatusText;
                    break;
                case nameof(DownloadItem.Status):
                    if (item.Status == DownloadStatus.Completed)
                    {
                        UnbindFromDownloadItem();
                        SetInstallButtonState(content: "Installed", enabled: false, showProgress: false);
                    }
                    else if (item.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
                    {
                        UnbindFromDownloadItem();
                        SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                    }
                    break;
            }
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnbindFromDownloadItem();
    }

    private void SetInstallButtonState(string content = "Install", bool enabled = true, bool showProgress = false)
    {
        InstallButton.Content = content;
        InstallButton.IsEnabled = enabled;
        InstallButton.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
        ProgressSection.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        InstallButton.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDisabledBrush"];
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
        ScreenshotsScrollViewer.ChangeView(Math.Min(ScreenshotsScrollViewer.ScrollableWidth, offset), null, null);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProductInfo == null) return;

        var productId = _currentProductInfo.ProductId;
        var downloadManager = DownloadManagerService.Instance;

        try
        {
            SetInstallButtonState(showProgress: true);

            var existingDownload = downloadManager.GetDownload(productId);
            if (existingDownload is { Status: DownloadStatus.Cancelled or DownloadStatus.Failed })
            {
                downloadManager.RemoveDownload(productId);
            }

            downloadManager.AddDownload(_currentProductInfo);
            
            // Bind to the new download item
            var downloadItem = downloadManager.GetDownload(productId);
            if (downloadItem != null)
            {
                _activeDownloadItem = downloadItem;
                BindToDownloadItem(downloadItem);
            }

            UpdateService.StartStatusAnimation("Fetching download URLs");
            StatusText.Text = "Fetching download URLs...";
            
            var urls = await GetDownloadUrl.fetch(productId);

            if (urls == null)
            {
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Failed);
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
                await ShowErrorDialogAsync("App not supported", "This app isn't supported. Try a different app or check again later");
                return;
            }

            StatusText.Text = "Preparing download...";

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            StopButton.IsEnabled = true;

            downloadManager.RegisterCancellationToken(productId, _cts);

            await DownloadHelper.StartDownloadAsync(urls, productId, _cts.Token, UpdateService);

            downloadManager.UnregisterCancellationToken(productId);

            if (!_cts.Token.IsCancellationRequested)
            {
                downloadManager.UpdateDownloadStatus(productId, DownloadStatus.Completed);
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Installed", enabled: false, showProgress: false);
            }
            else
            {
                UnbindFromDownloadItem();
                SetInstallButtonState(content: "Retry", enabled: true, showProgress: false);
            }
        }
        catch (OperationCanceledException)
        {
            HandleDownloadError(productId, "Operation canceled.", DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            HandleDownloadError(productId, "Failed to install.", DownloadStatus.Failed);
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

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        StopButton.IsEnabled = false;
    }
}
