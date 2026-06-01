using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Contracts.ViewModels;
using Raven.Helpers;

namespace Raven.ViewModels;

public partial class MainViewModel : ObservableRecipient, INavigationAware, ICardViewModel
{
    public int F1Index = 0;
    public int F2Index = 0;
    public MediaTypeRecommendation MediaType = MediaTypeRecommendation.Apps;
    public Category Category = Category.TopFree;

    [ObservableProperty]
    private string headerText = "";

    public ObservableCollection<Card> Cards { get; set; } = [];

    public int CurrentSkipItem { get; set; }

    public int FirstVisibleIndex { get; set; }

    public bool HasMoreItems { get; set; }

    public bool HasCachedResults { get; set; }

    public object Filter1
    {
        get => MediaType;
        set
        {
            if (value is int index)
            {
                MediaType = MediaTypePairs[index];
                F1Index = index;
            }
        }
    }

    public object Filter2
    {
        get => Category;
        set
        {
            if (value is int index)
            {
                Category = CategoryTypePairs[index];
                F2Index = index;

                if (index >= 0 && index < ItemSourceFilter2.Count)
                {
                    HeaderText = ItemSourceFilter2[index];
                }
            }
        }
    }

    [ObservableProperty]
    private List<string> itemSourceFilter1 = [];

    [ObservableProperty]
    private List<string> itemSourceFilter2 = [];

    private static readonly Dictionary<int, MediaTypeRecommendation> MediaTypePairs = new()
    {
        { 0, MediaTypeRecommendation.Apps },
        { 1, MediaTypeRecommendation.Games },
    };

    private static readonly Dictionary<int, Category> CategoryTypePairs = new()
    {
        { 0, Category.TopFree },
        { 1, Category.TopPaid },
        { 2, Category.TopTrending },
        { 3, Category.Deal },
        { 4, Category.TopGrossing },
    };

    public MainViewModel(ILocaleService localeService)
    {
        localeService.LocaleChanged += (_, _) =>
        {
            RefreshLocalizedFilters();
            ClearCache();
        };

        RefreshLocalizedFilters();
    }

    private void RefreshLocalizedFilters()
    {
        ItemSourceFilter1 =
        [
            "Filter_Apps".GetLocalized(),
            "Filter_Games".GetLocalized(),
        ];
        ItemSourceFilter2 =
        [
            "Filter_TopFree".GetLocalized(),
            "Filter_TopPaid".GetLocalized(),
            "Filter_TopTrending".GetLocalized(),
            "Filter_Specials".GetLocalized(),
            "Filter_BestSelling".GetLocalized(),
        ];

        if (ItemSourceFilter2.Count > 0)
        {
            var selectedIndex = Math.Clamp(F2Index, 0, ItemSourceFilter2.Count - 1);
            HeaderText = ItemSourceFilter2[selectedIndex];
        }
    }

    private void ClearCache()
    {
        Cards.Clear();
        HasCachedResults = false;
        CurrentSkipItem = 0;
        FirstVisibleIndex = 0;
        F1Index = 0;
        F2Index = 0;
        MediaType = MediaTypeRecommendation.Apps;
        Category = Category.TopFree;
    }

    public void OnNavigatedTo(object parameter)
    {
        var frame = App.GetService<INavigationService>().Frame;
        frame?.BackStack.Clear();
    }

    public void OnNavigatedFrom() { }
}
