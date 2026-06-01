using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class SearchPage : Page
{
    public ObservableCollection<Card> Cards { get; set; } = [];

    public SearchViewModel ViewModel { get; }
    private readonly ILocaleService _localeService;

    public SearchPage()
    {
        ViewModel = App.GetService<SearchViewModel>();
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

        var result = await StoreEdgeFDQuery.GetSearchProduct(
            ViewModel.Query,
            deviceFamily,
            market,
            language,
            ViewModel.CurrentSkipItem,
            ViewModel.MediaType,
            ViewModel.Price
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
        if (e.Parameter is string query)
        {
            if (ViewModel.HasCachedResults && ViewModel.Query == query)
            {
                if (e.NavigationMode == NavigationMode.New)
                {
                    ViewModel.FirstVisibleIndex = 0;
                    // if filter changed
                    if (ViewModel.F1Index != 0 || ViewModel.F2Index != 0)
                    {
                        CardView.SelectedFilterIndex1 = 0;
                        CardView.SelectedFilterIndex2 = 0;
                        await CardView.ApplyFilters();
                        return;
                    }
                }
                CardView.SelectedFilterIndex1 = ViewModel.F1Index;
                CardView.SelectedFilterIndex2 = ViewModel.F2Index;
            }
            else
            {
                ViewModel.Query = query;
                _ = CardView.InitialLoadCards();
            }
        }
        else if (e.Parameter is Card card)
        {
            CardView.NavigateToProductOrBundle(card.ProductId, card.InstallerType);
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
