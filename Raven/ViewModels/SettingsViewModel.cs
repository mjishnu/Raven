using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Services;
using StoreListings.Library;

namespace Raven.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocaleService _localeService;
    private readonly IArchitectureSelectorService _architectureSelectorService;
    private bool _isInitialized;

    [ObservableProperty]
    private ElementTheme _elementTheme;

    [ObservableProperty]
    private string _versionDescription;

    [ObservableProperty]
    private int _selectedMarketIndex;

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private int _selectedArchitectureIndex;

    private readonly List<(string DisplayName, Market Value)> _marketItems;
    private readonly List<(string DisplayName, Lang Value)> _languageItems;
    private readonly List<(string DisplayName, StoreEdgeFDArch Value)> _architectureItems;

    public IReadOnlyList<string> AllMarketNames { get; }
    public IReadOnlyList<string> AllLanguageNames { get; }
    public IReadOnlyList<string> AllArchitectureNames { get; }

    public ICommand SwitchThemeCommand { get; }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocaleService localeService,
        IArchitectureSelectorService architectureSelectorService
    )
    {
        _themeSelectorService = themeSelectorService;
        _localeService = localeService;
        _architectureSelectorService = architectureSelectorService;
        _elementTheme = _themeSelectorService.Theme;
        _versionDescription = GetVersionDescription();

        _marketItems = Enum.GetValues<Market>()
            .Select(m => (GetMarketDisplayName(m), m))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllMarketNames = _marketItems.Select(x => x.DisplayName).ToList();
        _selectedMarketIndex = Math.Max(
            0,
            _marketItems.FindIndex(x => x.Value == _localeService.Market)
        );

        _languageItems = Enum.GetValues<Lang>()
            .Select(l => (GetLanguageDisplayName(l), l))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllLanguageNames = _languageItems.Select(x => x.DisplayName).ToList();
        _selectedLanguageIndex = Math.Max(
            0,
            _languageItems.FindIndex(x => x.Value == _localeService.Language)
        );

        _architectureItems = Enum.GetValues<StoreEdgeFDArch>()
            .Select(a => (a.ToString(), a))
            .ToList();
        AllArchitectureNames = _architectureItems.Select(x => x.DisplayName).ToList();
        _selectedArchitectureIndex = Math.Max(
            0,
            _architectureItems.FindIndex(x => x.Value == _architectureSelectorService.SelectedStoreEdgeArchitecture)
        );

        SwitchThemeCommand = new RelayCommand<ElementTheme>(
            async (param) =>
            {
                if (ElementTheme != param)
                {
                    ElementTheme = param;
                    await _themeSelectorService.SetThemeAsync(param);
                }
            }
        );

        _isInitialized = true;
    }

    partial void OnSelectedMarketIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _marketItems.Count)
            return;
        var market = _marketItems[value].Value;
        if (market != _localeService.Market)
            _ = _localeService.SetMarketAsync(market);
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _languageItems.Count)
            return;
        var lang = _languageItems[value].Value;
        if (lang != _localeService.Language)
            _ = _localeService.SetLanguageAsync(lang);
    }

    partial void OnSelectedArchitectureIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _architectureItems.Count)
            return;

        var selectedArchitecture = _architectureItems[value].Value;
        if (selectedArchitecture != _architectureSelectorService.SelectedStoreEdgeArchitecture)
            _ = _architectureSelectorService.SetSelectedArchitectureAsync(selectedArchitecture);
    }

    private static string GetMarketDisplayName(Market market)
    {
        try
        {
            return new RegionInfo(market.ToString()).EnglishName;
        }
        catch
        {
            return market.ToString();
        }
    }

    private static string GetLanguageDisplayName(Lang lang)
    {
        try
        {
            return new CultureInfo(lang.ToString()).EnglishName;
        }
        catch
        {
            return lang.ToString();
        }
    }

    public async Task ResetAppToDefaultAsync()
    {
        DownloadManagerService.Instance.ResetAllDownloads(deleteFiles: true);

        await _themeSelectorService.SetThemeAsync(ElementTheme.Default);
        ElementTheme = _themeSelectorService.Theme;

        await _localeService.ResetToDefaultAsync();
        await _architectureSelectorService.ResetToDefaultAsync();

        SelectedMarketIndex = Math.Max(
            0,
            _marketItems.FindIndex(x => x.Value == _localeService.Market)
        );
        SelectedLanguageIndex = Math.Max(
            0,
            _languageItems.FindIndex(x => x.Value == _localeService.Language)
        );
        SelectedArchitectureIndex = Math.Max(
            0,
            _architectureItems.FindIndex(x => x.Value == _architectureSelectorService.SelectedStoreEdgeArchitecture)
        );
    }

    private static string GetVersionDescription()
    {
        System.Version version = Assembly.GetExecutingAssembly().GetName().Version!;

        return $"{"AppDisplayName".GetLocalized()} - {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }
}
