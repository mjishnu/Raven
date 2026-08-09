using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Raven.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }
    private readonly ILocaleService _localeService;

    public MainPage()
    {
        ViewModel = App.GetService<MainViewModel>();
        _localeService = App.GetService<ILocaleService>();
        InitializeComponent();
        CardView.ViewModel = ViewModel;
        CardView.LoadCardsMethod = LoadCards;
    }

    private async Task LoadCards()
    {
        ViewModel.HasMoreItems = true;

        var deviceFamily = DeviceFamily.Desktop;
        var market = _localeService.Market;
        var language = _localeService.Language;

        var result = await StoreEdgeFDQuery.GetRecommendations(
            ViewModel.Category,
            deviceFamily,
            market,
            language,
            ViewModel.MediaType,
            ViewModel.CurrentSkipItem
        );

        if (result.IsSuccess)
        {
            if (result.Value.Cards.Count == 0)
            {
                ViewModel.HasMoreItems = false;
            }
            for (var i = 0; i < result.Value.Cards.Count; i++)
            {
                var card = result.Value.Cards[i];
                ViewModel.Cards.Add(card);
            }
            ViewModel.HasCachedResults = true;
        }
        else
        {
            throw result.Exception;
        }
    }

    private async void OpenPortablePackageButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".msix");
        picker.FileTypeFilter.Add(".appx");
        picker.FileTypeFilter.Add(".msixbundle");
        picker.FileTypeFilter.Add(".appxbundle");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        OpenPortablePackageButton.IsEnabled = false;

        try
        {
            var title = Path.GetFileNameWithoutExtension(file.Name);
            var result = await PortableMsixLauncher.ExtractAndLaunchAsync(
                file.Path,
                dependencyPackagePaths: null,
                appTitle: title,
                packageKey: "local",
                cancellationToken: default,
                addToUserPath: true,
                createStartMenuShortcut: true
            );

            var pathText = result.AddedToUserPath
                ? "The executable folder was added to your user PATH."
                : "The executable folder was already present in your user PATH.";

            var shortcutText = string.IsNullOrWhiteSpace(result.StartMenuShortcut)
                ? string.Empty
                : $"\n\nWindows Start/Search entry:\n{result.StartMenuShortcut}";

            var dialog = new ContentDialog
            {
                Title = "Portable app started",
                Content =
                    $"Executable:\n{result.ExecutablePath}\n\n{pathText}{shortcutText}\n\nPortable folder:\n{result.ExtractDirectory}",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Portable launch failed",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            OpenPortablePackageButton.IsEnabled = true;
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Check if we have cached results to restore
        if (ViewModel.HasCachedResults)
        {
            CardView.SelectedFilterIndex1 = ViewModel.F1Index;
            CardView.SelectedFilterIndex2 = ViewModel.F2Index;
        }
        else
        {
            CardView.SelectedFilterIndex1 = 0;
            CardView.SelectedFilterIndex2 = 0;
            await CardView.ApplyFilters();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Deterministic teardown on the reliable navigation path (Unloaded is not guaranteed).
        // Detaches the CardView's ItemsView from the singleton VM's CollectionChanged...
        CardView.Cleanup();
        // ...and severs this transient page's x:Bind subscriptions to the singleton ViewModel,
        // which otherwise keep the page (and CardView) alive forever.
        Bindings.StopTracking();
    }
}
