using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Models;
using Raven.Services;
using StoreListings.Library;

namespace Raven.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocaleService _localeService;
    private readonly IArchitectureSelectorService _architectureSelectorService;
    private readonly ILocalSettingsService _localSettingsService;
    private bool _isInitialized;
    private bool _downloadConnectionModeLoaded;
    private bool _proxySettingsLoaded;

    private const string DownloadConnectionModeSettingsKey = "DownloadConnectionMode";
    private const string ProxyModeSettingsKey = "ProxyMode";
    private const string ProxyUriSettingsKey = "ProxyUri";

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

    [ObservableProperty]
    private int _selectedDownloadConnectionIndex;

    [ObservableProperty]
    private int _selectedProxyModeIndex;

    [ObservableProperty]
    private string _proxyUri = string.Empty;

    [ObservableProperty]
    private string _proxyValidationMessage = string.Empty;

    private readonly List<(string DisplayName, Market Value)> _marketItems;
    private readonly List<(string DisplayName, Lang Value)> _languageItems;
    private readonly List<(string DisplayName, StoreEdgeFDArch Value)> _architectureItems;
    private readonly List<(string DisplayName, DownloadConnectionMode Value)> _downloadConnectionItems;
    private readonly List<(string DisplayName, ProxyMode Value)> _proxyModeItems;

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

    public IReadOnlyList<string> AllDownloadConnectionNames
    {
        get;
    }

    public IReadOnlyList<string> AllProxyModeNames
    {
        get;
    }

    public bool IsCustomProxy =>
        _selectedProxyModeIndex >= 0
        && _selectedProxyModeIndex < _proxyModeItems.Count
        && _proxyModeItems[_selectedProxyModeIndex].Value == ProxyMode.Custom;

    public ICommand SwitchThemeCommand
    {
        get;
    }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocaleService localeService,
        IArchitectureSelectorService architectureSelectorService,
        ILocalSettingsService localSettingsService
    )
    {
        _themeSelectorService = themeSelectorService;
        _localeService = localeService;
        _architectureSelectorService = architectureSelectorService;
        _localSettingsService = localSettingsService;
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

        _downloadConnectionItems = Enum.GetValues<DownloadConnectionMode>()
            .Select(mode => (GetDownloadConnectionDisplayName(mode), mode))
            .ToList();
        AllDownloadConnectionNames = _downloadConnectionItems.Select(x => x.DisplayName).ToList();
        _selectedDownloadConnectionIndex = 0;

        _proxyModeItems = Enum.GetValues<ProxyMode>()
            .Select(mode => (GetProxyModeDisplayName(mode), mode))
            .ToList();
        AllProxyModeNames = _proxyModeItems.Select(x => x.DisplayName).ToList();
        _selectedProxyModeIndex = 0;

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
        _ = LoadDownloadConnectionModeAsync();
        _ = LoadProxySettingsAsync();
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

    partial void OnSelectedDownloadConnectionIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _downloadConnectionItems.Count)
            return;

        _downloadConnectionModeLoaded = true;
        _ = _localSettingsService.SaveSettingAsync(
            DownloadConnectionModeSettingsKey,
            _downloadConnectionItems[value].Value.ToString()
        );
    }

    partial void OnSelectedProxyModeIndexChanged(int value)
    {
        if (!_isInitialized || value < 0 || value >= _proxyModeItems.Count)
            return;

        OnPropertyChanged(nameof(IsCustomProxy));
        ProxyValidationMessage = IsCustomProxy && !TryParseProxyUri(ProxyUri, out _)
            ? "Settings_ProxyInvalidUri".GetLocalized()
            : string.Empty;
        if (!_proxySettingsLoaded)
            return;

        var mode = _proxyModeItems[value].Value;
        if (mode == ProxyMode.Custom && !TryParseProxyUri(ProxyUri, out _))
            return;

        _ = _localSettingsService.SaveSettingAsync(ProxyModeSettingsKey, mode.ToString());
    }

    partial void OnProxyUriChanged(string value)
    {
        if (!_isInitialized || !IsCustomProxy)
            return;

        var isValid = TryParseProxyUri(value, out _);
        ProxyValidationMessage = isValid ? string.Empty : "Settings_ProxyInvalidUri".GetLocalized();
        if (!_proxySettingsLoaded || !isValid)
            return;

        _ = _localSettingsService.SaveSettingAsync(ProxyUriSettingsKey, value.Trim());
        _ = _localSettingsService.SaveSettingAsync(ProxyModeSettingsKey, ProxyMode.Custom.ToString());
    }

    private async Task LoadDownloadConnectionModeAsync()
    {
        try
        {
            var saved = await _localSettingsService
                .ReadSettingAsync<string>(DownloadConnectionModeSettingsKey)
                .ConfigureAwait(true);

            if (
                !_downloadConnectionModeLoaded
                && Enum.TryParse(saved, ignoreCase: true, out DownloadConnectionMode mode)
                && Enum.IsDefined(typeof(DownloadConnectionMode), mode)
            )
            {
                var index = _downloadConnectionItems.FindIndex(x => x.Value == mode);
                if (index >= 0)
                    SelectedDownloadConnectionIndex = index;
            }
        }
        catch
        {
            // Keep the safe Auto default if the preference cannot be read.
        }
    }

    private static string GetDownloadConnectionDisplayName(DownloadConnectionMode mode) =>
        mode switch
        {
            DownloadConnectionMode.Auto => "Settings_ParallelConnections_Auto".GetLocalized(),
            DownloadConnectionMode.One => "Settings_ParallelConnections_One".GetLocalized(),
            DownloadConnectionMode.Two => "Settings_ParallelConnections_Two".GetLocalized(),
            DownloadConnectionMode.Four => "Settings_ParallelConnections_Four".GetLocalized(),
            DownloadConnectionMode.Eight => "Settings_ParallelConnections_Eight".GetLocalized(),
            _ => mode.ToString(),
        };

    private async Task LoadProxySettingsAsync()
    {
        try
        {
            var savedMode = await _localSettingsService
                .ReadSettingAsync<string>(ProxyModeSettingsKey)
                .ConfigureAwait(true);
            var savedUri = await _localSettingsService
                .ReadSettingAsync<string>(ProxyUriSettingsKey)
                .ConfigureAwait(true);

            var mode = Enum.TryParse(savedMode, ignoreCase: true, out ProxyMode parsedMode)
                && Enum.IsDefined(typeof(ProxyMode), parsedMode)
                ? parsedMode
                : ProxyMode.System;
            if (mode == ProxyMode.Custom && !TryParseProxyUri(savedUri, out _))
                mode = ProxyMode.System;

            _proxyUri = savedUri ?? string.Empty;
            _selectedProxyModeIndex = _proxyModeItems.FindIndex(x => x.Value == mode);
            if (_selectedProxyModeIndex < 0)
                _selectedProxyModeIndex = 0;
            _proxySettingsLoaded = true;
            ProxyValidationMessage = IsCustomProxy && !TryParseProxyUri(ProxyUri, out _)
                ? "Settings_ProxyInvalidUri".GetLocalized()
                : string.Empty;
            OnPropertyChanged(nameof(IsCustomProxy));
        }
        catch
        {
            _proxySettingsLoaded = true;
            ProxyValidationMessage = string.Empty;
        }
    }

    private static string GetProxyModeDisplayName(ProxyMode mode) =>
        mode switch
        {
            ProxyMode.System => "Settings_ProxyMode_System".GetLocalized(),
            ProxyMode.Direct => "Settings_ProxyMode_Direct".GetLocalized(),
            ProxyMode.Custom => "Settings_ProxyMode_Custom".GetLocalized(),
            _ => mode.ToString(),
        };

    private static bool TryParseProxyUri(string? value, out Uri? uri)
    {
        uri = null;
        if (
            !Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            || parsed is null
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host)
        )
            return false;

        uri = parsed;
        return true;
    }

    private static string GetMarketDisplayName(Market market)
    {
        try
        {
            return new RegionInfo(market.ToString()).DisplayName;
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
            return new CultureInfo(lang.ToString()).NativeName;
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
        await _localSettingsService.SaveSettingAsync(
            DownloadConnectionModeSettingsKey,
            DownloadConnectionMode.Auto.ToString()
        );
        SelectedDownloadConnectionIndex = 0;
        await _localSettingsService.SaveSettingAsync(ProxyModeSettingsKey, ProxyMode.System.ToString());
        await _localSettingsService.SaveSettingAsync(ProxyUriSettingsKey, string.Empty);
        SelectedProxyModeIndex = 0;
        ProxyUri = string.Empty;

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
