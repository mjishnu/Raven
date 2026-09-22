using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.UI.Xaml.Navigation;

using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Views;

namespace Raven.ViewModels;

public partial class ShellViewModel : ObservableRecipient
{
    [ObservableProperty]
    private bool isBackEnabled;

    [ObservableProperty]
    private object? selected;

    [ObservableProperty]
    private string mainNavigationText = "Home";

    [ObservableProperty]
    private string advancedSearchNavigationText = "Advanced Search";

    [ObservableProperty]
    private string installNavigationText = "Install";

    [ObservableProperty]
    private string updatesNavigationText = "Updates";

    [ObservableProperty]
    private string downloadsNavigationText = "Downloads";

    [ObservableProperty]
    private string settingsNavigationText = "Settings";

    public INavigationService NavigationService
    {
        get;
    }

    public INavigationViewService NavigationViewService
    {
        get;
    }

    public ShellViewModel(INavigationService navigationService, INavigationViewService navigationViewService)
    {
        NavigationService = navigationService;
        NavigationService.Navigated += OnNavigated;
        NavigationViewService = navigationViewService;
    }

    public void RefreshLocalization()
    {
        MainNavigationText = GetLocalizedOrFallback("Shell_Main.Content", "Home");
        AdvancedSearchNavigationText = GetLocalizedOrFallback(
            "Shell_Advanced_Search.Content",
            "Advanced Search"
        );
        InstallNavigationText = GetLocalizedOrFallback("Shell_Install.Content", "Install");
        UpdatesNavigationText = GetLocalizedOrFallback("Shell_Updates.Content", "Updates");
        DownloadsNavigationText = GetLocalizedOrFallback("Shell_Downloads.Content", "Downloads");
        SettingsNavigationText = GetLocalizedOrFallback("Shell_Settings.Content", "Settings");
    }

    private static string GetLocalizedOrFallback(string resourceKey, string fallback) =>
        resourceKey.GetLocalized() is { Length: > 0 } localized ? localized : fallback;

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        IsBackEnabled = NavigationService.CanGoBack;

        var selectedItem = NavigationViewService.GetSelectedItem(e.SourcePageType);
        if (selectedItem != null)
        {
            Selected = selectedItem;
        }
    }
}
