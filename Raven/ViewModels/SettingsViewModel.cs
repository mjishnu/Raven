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
    public partial ElementTheme ElementTheme { get; set; }

    [ObservableProperty]
    public partial string VersionDescription { get; set; }

    [ObservableProperty]
    public partial int SelectedMarketIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedArchitectureIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowRelaunchPrompt { get; set; }

    // Language/Market that were active when the app started. A relaunch is needed only when the
    // current selection differs from these, because already-loaded XAML strings don't re-localize.
    private readonly Lang _initialLanguage;
    private readonly Market _initialMarket;

    private readonly List<(string DisplayName, Market Value)> _marketItems;
    private readonly List<(string DisplayName, Lang Value)> _languageItems;
    private readonly List<(string DisplayName, StoreEdgeFDArch Value)> _architectureItems;

    public IReadOnlyList<string> AllMarketNames
    {
        get;
    }
    public IReadOnlyList<string> AllLanguageNames
    {
        get;
    }
    public IReadOnlyList<string> AllArchitectureNames
    {
        get;
    }

    public ICommand SwitchThemeCommand
    {
        get;
    }

    public ICommand RelaunchCommand
    {
        get;
    }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocaleService localeService,
        IArchitectureSelectorService architectureSelectorService
    )
    {
        _themeSelectorService = themeSelectorService;
        _localeService = localeService;
        _architectureSelectorService = architectureSelectorService;
        // Property setters are safe before _isInitialized is set: the On*Changed handlers
        // return early until then, and nothing is subscribed to PropertyChanged yet.
        ElementTheme = _themeSelectorService.Theme;
        VersionDescription = GetVersionDescription();

        _initialLanguage = _localeService.Language;
        _initialMarket = _localeService.Market;

        _marketItems = Enum.GetValues<Market>()
            .Select(m => (GetMarketDisplayName(m), m))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllMarketNames = _marketItems.Select(x => x.DisplayName).ToList();
        SelectedMarketIndex = Math.Max(
            0,
            _marketItems.FindIndex(x => x.Value == _localeService.Market)
        );

        _languageItems = Enum.GetValues<Lang>()
            .Select(l => (GetLanguageDisplayName(l), l))
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AllLanguageNames = _languageItems.Select(x => x.DisplayName).ToList();
        SelectedLanguageIndex = Math.Max(
            0,
            _languageItems.FindIndex(x => x.Value == _localeService.Language)
        );

        _architectureItems = Enum.GetValues<StoreEdgeFDArch>()
            .Select(a => (a.ToString(), a))
            .ToList();
        AllArchitectureNames = _architectureItems.Select(x => x.DisplayName).ToList();
        SelectedArchitectureIndex = Math.Max(
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

        RelaunchCommand = new RelayCommand(() =>
            Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty)
        );

        _isInitialized = true;
    }

    // Show the relaunch prompt whenever the live language differs from what was active at
    // startup; hide it again if the user reverts to the original values.
    private void UpdateRelaunchPrompt() =>
        ShowRelaunchPrompt = _localeService.Language != _initialLanguage;

    partial void OnSelectedMarketIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _marketItems.Count)
            return;
        var market = _marketItems[value].Value;
        if (market != _localeService.Market)
            _ = _localeService.SetMarketAsync(market);
        UpdateRelaunchPrompt();
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _languageItems.Count)
            return;
        var lang = _languageItems[value].Value;
        if (lang != _localeService.Language)
            _ = _localeService.SetLanguageAsync(lang);
        UpdateRelaunchPrompt();
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

        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
    }

    private static string GetVersionDescription()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // Strip the leading 'v' prefix if present (e.g. "v1.0.0.1-beta" → "1.0.0.1-beta")
        // Also strip build metadata appended by the .NET SDK (e.g. "+ebd1faf..." → drop it)
        var versionText = informationalVersion.StartsWith('v')
            ? informationalVersion[1..]
            : informationalVersion;

        var plusIndex = versionText.IndexOf('+');
        if (plusIndex > 0)
            versionText = versionText[..plusIndex];

        return $"{"AppDisplayName".GetLocalized()} - {versionText}";
    }
}
