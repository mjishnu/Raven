using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Helpers;

namespace Raven.ViewModels;

public partial class SearchViewModel : ObservableRecipient, ICardViewModel
{
    public string Query = "";
    public int F1Index = 0;
    public int F2Index = 0;
    public MediaTypeSearch MediaType = MediaTypeSearch.All;
    public PriceType Price = PriceType.All;

    public ObservableCollection<Card> Cards { get; set; } = [];

    public int CurrentSkipItem { get; set; }

    public int FirstVisibleIndex { get; set; }

    public bool HasMoreItems { get; set; }

    public bool HasCachedResults { get; set; }

    public string HeaderText
    {
        get
        {
            if (!string.IsNullOrEmpty(Query))
            {
                return $"\"{Query}\"";
            }
            return "";
        }
    }

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
        get => Price;
        set
        {
            if (value is int index)
            {
                Price = PriceTypePairs[index];
                F2Index = index;
            }
        }
    }
    [ObservableProperty]
    private List<string> itemSourceFilter1 = [];

    [ObservableProperty]
    private List<string> itemSourceFilter2 = [];
    private static readonly Dictionary<int, MediaTypeSearch> MediaTypePairs = new()
    {
        { 0, MediaTypeSearch.All },
        { 1, MediaTypeSearch.Apps },
        { 2, MediaTypeSearch.Games },
        { 3, MediaTypeSearch.Fonts },
        { 4, MediaTypeSearch.Themes },
    };

    private static readonly Dictionary<int, PriceType> PriceTypePairs = new()
    {
        { 0, PriceType.All },
        { 1, PriceType.Free },
        { 2, PriceType.Paid },
    };

    public SearchViewModel(ILocaleService localeService)
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
            "Search_Filter_AllDepartments".GetLocalized(),
            "Filter_Apps".GetLocalized(),
            "Filter_Games".GetLocalized(),
            "Search_Filter_Fonts".GetLocalized(),
            "Search_Filter_Themes".GetLocalized(),
        ];
        ItemSourceFilter2 =
        [
            "Search_Filter_AllTypes".GetLocalized(),
            "Search_Filter_Free".GetLocalized(),
            "Search_Filter_Paid".GetLocalized(),
        ];
    }

    private void ClearCache()
    {
        Cards.Clear();
        HasCachedResults = false;
        CurrentSkipItem = 0;
        FirstVisibleIndex = 0;
        Query = string.Empty;
        F1Index = 0;
        F2Index = 0;
    }
}
