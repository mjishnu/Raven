using CommunityToolkit.Mvvm.ComponentModel;
using Raven.Helpers;

namespace Raven.ViewModels;

public enum SearchType
{
    Url,
    ProductId,
    PackageFamilyName,
}

public partial class Advanced_SearchViewModel : ObservableRecipient
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    [NotifyPropertyChangedFor(nameof(SelectedSearchType))]
    public partial int SelectedTypeIndex { get; set; }

    public SearchType SelectedSearchType => SelectedTypeIndex switch
    {
        1 => SearchType.ProductId,
        2 => SearchType.PackageFamilyName,
        _ => SearchType.Url,
    };

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowInfoCards { get; set; } = true;

    public string PlaceholderText => SelectedSearchType switch
    {
        SearchType.Url => "AdvancedSearch_Placeholder_Url".GetLocalized(),
        SearchType.ProductId => "AdvancedSearch_Placeholder_ProductId".GetLocalized(),
        SearchType.PackageFamilyName => "AdvancedSearch_Placeholder_Pfn".GetLocalized(),
        _ => "AdvancedSearch_Placeholder_Default".GetLocalized(),
    };

    public Advanced_SearchViewModel()
    {
    }
}
