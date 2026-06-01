using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Models;
using Raven.Services;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel { get; }
    private readonly INavigationService _navigationService;
    private Raven.Helpers.DownloadItemStatusAnimator? _animator;
    private readonly HashSet<string> _subscribedProductIds = new(StringComparer.OrdinalIgnoreCase);

    public DownloadsPage()
    {
        ViewModel = App.GetService<DownloadsViewModel>();
        _navigationService = App.GetService<INavigationService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DownloadManagerService.Instance.BeginObserving();

        _animator ??= new Raven.Helpers.DownloadItemStatusAnimator(this.DispatcherQueue);

        // When navigating between pages, the per-download animator instances used during download/install
        // may get GC'ed. Restart animations for any active items so the Downloads list doesn't look stuck.
        foreach (var item in ViewModel.Downloads)
        {
            SubscribeToItemIfNeeded(item);
            switch (item.Status)
            {
                case DownloadStatus.Pending:
                    // Reset any stale override from other pages/flows so our animator can take over.
                    item.StatusTextOverride = null;
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Fetching".GetLocalized());
                    break;
                case DownloadStatus.Downloading:
                    item.StatusTextOverride = null;
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Downloading".GetLocalized());
                    break;
                case DownloadStatus.Installing:
                    item.StatusTextOverride = null;
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Installing".GetLocalized());
                    break;
                case DownloadStatus.Cancelling:
                    item.StatusTextOverride = null;
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Cancelling".GetLocalized());
                    break;
                default:
                    _animator.Stop(item);
                    break;
            }
        }
    }

    private void SubscribeToItemIfNeeded(DownloadItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProductId))
            return;

        if (_subscribedProductIds.Contains(item.ProductId))
            return;

        item.PropertyChanged -= OnDownloadItemPropertyChanged;
        item.PropertyChanged += OnDownloadItemPropertyChanged;
        _subscribedProductIds.Add(item.ProductId);
    }

    private void OnDownloadItemPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (sender is not DownloadItem item)
            return;

        if (
            e.PropertyName is nameof(DownloadItem.Status) or nameof(DownloadItem.StatusTextOverride)
        )
        {
            switch (item.Status)
            {
                case DownloadStatus.Pending:
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Fetching".GetLocalized());
                    break;
                case DownloadStatus.Downloading:
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Downloading".GetLocalized());
                    break;
                case DownloadStatus.Installing:
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Installing".GetLocalized());
                    break;
                case DownloadStatus.Cancelling:
                    StartOrUpdateAnimation(item, fallback: "Download_Status_Cancelling".GetLocalized());
                    break;
                default:
                    _animator?.Stop(item);
                    break;
            }
        }
    }

    private void StartOrUpdateAnimation(DownloadItem item, string fallback)
    {
        if (_animator == null)
            return;

        var baseText = GetAnimatorBaseText(item, fallback);

        // If we're already showing the same base text, don't restart the timer.
        // Restarting can temporarily clear the override and cause visible flicker.
        if (!string.IsNullOrWhiteSpace(item.StatusTextOverride))
        {
            var existingBase = item.StatusTextOverride.TrimEnd('.', ' ');
            if (string.Equals(existingBase, baseText, StringComparison.OrdinalIgnoreCase))
            {
                _animator.UpdateBase(item, baseText);
                return;
            }
        }

        _animator.Start(item, baseText);
    }

    private static string GetAnimatorBaseText(DownloadItem item, string fallback)
    {
        // Prefer the override when present; otherwise fall back to the computed StatusText.
        // This preserves more specific state like "Downloading (1/4) files...".
        var text = item.StatusTextOverride ?? item.StatusText;
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        var trimmed = text.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DownloadManagerService.Instance.EndObserving();

        if (_animator != null)
        {
            foreach (var item in ViewModel.Downloads)
            {
                _animator.Stop(item);
                item.PropertyChanged -= OnDownloadItemPropertyChanged;
            }
        }

        _subscribedProductIds.Clear();

        // Sever this transient page's x:Bind subscriptions to the singleton ViewModel
        // (the ListView's ItemsSource is the singleton DownloadManagerService collection),
        // which would otherwise root the page forever. Matches the MainPage/SearchPage pattern.
        Bindings.StopTracking();
    }

    private void DownloadsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DownloadItem item)
        {
            // Pass the DownloadItem - AppPage will handle fetching product if needed
            _navigationService.NavigateTo(typeof(AppViewModel).FullName!, item);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var downloadsPath = DownloadManagerService.GetDownloadsRootFolder();
        Directory.CreateDirectory(downloadsPath);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = downloadsPath,
            UseShellExecute = true,
        };

        System.Diagnostics.Process.Start(psi);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string productId)
        {
            DownloadManagerService.Instance.CancelDownload(productId);
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string productId)
        {
            DownloadManagerService.Instance.RemoveDownload(productId);
        }
    }
}
