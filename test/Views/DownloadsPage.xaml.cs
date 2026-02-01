using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using test.Contracts.Services;
using test.Models;
using test.Services;
using test.ViewModels;
using System.IO;

namespace test.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsViewModel ViewModel { get; }
    private readonly INavigationService _navigationService;
    private test.Helpers.DownloadItemStatusAnimator? _animator;

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

        _animator ??= new test.Helpers.DownloadItemStatusAnimator(this.DispatcherQueue);

        // When navigating between pages, the per-download animator instances used during download/install
        // may get GC'ed. Restart animations for any active items so the Downloads list doesn't look stuck.
        foreach (var item in ViewModel.Downloads)
        {
            switch (item.Status)
            {
                case DownloadStatus.Pending:
                    StartOrUpdateAnimation(item, fallback: "Fetching download URLs");
                    break;
                case DownloadStatus.Downloading:
                    StartOrUpdateAnimation(item, fallback: "Downloading");
                    break;
                case DownloadStatus.Installing:
                    StartOrUpdateAnimation(item, fallback: "Installing");
                    break;
                case DownloadStatus.Cancelling:
                    StartOrUpdateAnimation(item, fallback: "Cancelling");
                    break;
                default:
                    _animator.Stop(item);
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
            }
        }
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
        var downloadsPath = Path.Combine(AppContext.BaseDirectory, "downloads");
        Directory.CreateDirectory(downloadsPath);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = downloadsPath,
            UseShellExecute = true
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
