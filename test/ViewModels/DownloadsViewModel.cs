using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using test.Contracts.Services;
using test.Models;
using test.Services;

namespace test.ViewModels;

public partial class DownloadsViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    public ObservableCollection<DownloadItem> Downloads => DownloadManagerService.Instance.Downloads;

    public DownloadsViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    [RelayCommand]
    private void NavigateToApp(DownloadItem? item)
    {
        if (item?.ProductInfo != null)
        {
            _navigationService.NavigateTo(typeof(AppViewModel).FullName!, item.ProductInfo);
        }
    }

    [RelayCommand]
    private void RemoveDownload(DownloadItem? item)
    {
        if (item != null)
        {
            DownloadManagerService.Instance.RemoveDownload(item.ProductId);
        }
    }

    [RelayCommand]
    private void CancelDownload(DownloadItem? item)
    {
        if (item != null)
        {
            DownloadManagerService.Instance.CancelDownload(item.ProductId);
        }
    }
}
