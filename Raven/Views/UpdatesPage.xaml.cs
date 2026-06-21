using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Models;
using Raven.Services;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;

    public UpdatesPage()
    {
        ViewModel = App.GetService<UpdatesViewModel>();
        ViewModel.DispatcherQueue = this.DispatcherQueue;
        _navigationService = App.GetService<INavigationService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DownloadManagerService.Instance.BeginObserving();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Sever this transient page's x:Bind subscriptions to the singleton ViewModel,
        // which would otherwise root the page forever. Matches the MainPage/SearchPage pattern.
        Bindings.StopTracking();

        // StopTracking only unhooks the page's x:Bind listeners; the ListViews stay subscribed
        // to the singleton VM collections' CollectionChanged, which roots them (and via their
        // ItemClick handlers this whole page) for the app's lifetime. Detach them explicitly —
        // same bug class as CardViewControl.Cleanup's CardRepeater detach.
        // Must run AFTER StopTracking so the OneWay x:Bind cannot re-assert the values.
        UpdatesList.ItemsSource = null;
        CompletedUpdatesList.ItemsSource = null;

        // LAST: while the observer count is still >0, background updates are marshaled to
        // the UI thread instead of mutating shared collections inline from worker threads
        // (matches DownloadsPage's teardown ordering).
        DownloadManagerService.Instance.EndObserving();
    }

    private void ItemCheckBox_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is UpdateItem item)
            item.IsSelected = cb.IsChecked == true;
    }

    private void UpdateItem_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not UpdateItem item)
            return;

        var navItem = new DownloadItem
        {
            ProductId = item.ProductId,
            InstallerType = InstallerType.Packaged,
            LogoUrl = item.LogoUrl
        };
        _navigationService.NavigateTo(typeof(AppViewModel).FullName!, navItem);
    }

    private void StopCheckButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCheckCommand.Execute(null);
    }

    private void CancelUpdateButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string productId })
        {
            DownloadManagerService.Instance.CancelDownload(productId);

            // For items queued as Pending but not yet started (DownloadItem not yet created),
            // CancelDownload cannot update the visual state. Set Cancelling directly so the
            // user sees immediate feedback.
            if (DownloadManagerService.Instance.GetDownload(productId) is null)
            {
                var updateItem = ViewModel.AvailableUpdates.FirstOrDefault(x => x.ProductId == productId);
                if (updateItem is { Status: DownloadStatus.Pending })
                    updateItem.Status = DownloadStatus.Cancelling;
            }
        }
    }

    private void ActionButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.CheckForUpdatesOrUpdateCommand.Execute(null);
    }

    private void SelectAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.ToggleSelectAllCommand.Execute(null);
    }

    private void FailedItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DownloadItem info) return;
        
        _navigationService.NavigateTo(typeof(AppViewModel).FullName!, info.ProductId);
    }

    private void ClearAllFailed_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllFailedUpdatesCommand.Execute(null);
    }

    private void DismissFailedItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string productId })
        {
            var item = ViewModel.FailedUpdates.FirstOrDefault(f => f.ProductId == productId);
            if (item != null)
                ViewModel.ClearFailedUpdateCommand.Execute(item);
        }
    }
}

