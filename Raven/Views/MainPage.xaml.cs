using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.ViewModels;

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
