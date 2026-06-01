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
    private readonly HashSet<string> _subscribedProductIds = new(StringComparer.OrdinalIgnoreCase);

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

        foreach (var updateItem in ViewModel.AvailableUpdates)
            SubscribeToUpdateItemIfNeeded(updateItem);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DownloadManagerService.Instance.EndObserving();

        foreach (var updateItem in ViewModel.AvailableUpdates)
            updateItem.PropertyChanged -= OnUpdateItemPropertyChanged;

        _subscribedProductIds.Clear();

        // Sever this transient page's x:Bind subscriptions to the singleton ViewModel,
        // which would otherwise root the page forever. Matches the MainPage/SearchPage pattern.
        Bindings.StopTracking();
    }

    private void SubscribeToUpdateItemIfNeeded(UpdateItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProductId))
            return;

        if (_subscribedProductIds.Contains(item.ProductId))
            return;

        item.PropertyChanged -= OnUpdateItemPropertyChanged;
        item.PropertyChanged += OnUpdateItemPropertyChanged;
        _subscribedProductIds.Add(item.ProductId);
    }

    private void OnUpdateItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { }

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
            LogoUrl = item.LogoUrl,
        };
        _navigationService.NavigateTo(typeof(AppViewModel).FullName!, navItem);
    }

    private void StopCheckButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCheck();
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
}

