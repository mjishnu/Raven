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
    private readonly IProxyService _proxyService;
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

    [ObservableProperty]
    private bool _showRelaunchPrompt;

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
        IArchitectureSelectorService architectureSelectorService,
        IProxyService proxyService
    )
    {
        _themeSelectorService = themeSelectorService;
        _localeService = localeService;
        _architectureSelectorService = architectureSelectorService;
        _proxyService = proxyService;
        _proxyService.Changed += (_, _) => RefreshProxySelection();
        _elementTheme = _themeSelectorService.Theme;
        _versionDescription = GetVersionDescription();

        _initialLanguage = _localeService.Language;
        _initialMarket = _localeService.Market;

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

    // ------------------------------------------------------------------
    // Proxy management
    // ------------------------------------------------------------------

    public IReadOnlyList<ProxyEntry> Proxies => _proxyService.Proxies;

    [ObservableProperty]
    private ProxyEntry? _selectedProxy;

    /// <summary>True when the user wants a direct connection (no proxy).</summary>
    [ObservableProperty]
    private bool _isDirectConnectionSelected;

    // New-proxy form fields
    [ObservableProperty]
    private string _newProxyName = string.Empty;

    /// <summary>Index into ProxySchemeNames for the type combo (http/socks5/socks4).</summary>
    [ObservableProperty]
    private int _newProxySchemeIndex;

    [ObservableProperty]
    private string _newProxyHost = string.Empty;

    [ObservableProperty]
    private string _newProxyPort = string.Empty;

    [ObservableProperty]
    private string _newProxyUsername = string.Empty;

    [ObservableProperty]
    private string _newProxyPassword = string.Empty;

    [ObservableProperty]
    private string _newProxyError = string.Empty;

    public bool HasNewProxyError => !string.IsNullOrEmpty(NewProxyError);

    /// <summary>Selectable proxy types.</summary>
    public IReadOnlyList<string> ProxySchemeNames { get; } = ["HTTP", "HTTPS", "SOCKS4", "SOCKS5"];

    partial void OnNewProxyErrorChanged(string value) => OnPropertyChanged(nameof(HasNewProxyError));

    public ICommand AddProxyCommand => field ??= new AsyncRelayCommand(AddProxyAsync);
    public ICommand RemoveProxyCommand => field ??= new AsyncRelayCommand<ProxyEntry>(RemoveProxyAsync!);
    public ICommand ActivateProxyCommand => field ??= new AsyncRelayCommand<ProxyEntry>(ActivateProxyAsync!);
    public ICommand UseDirectConnectionCommand => field ??= new AsyncRelayCommand(UseDirectConnectionAsync);

    private async Task AddProxyAsync()
    {
        NewProxyError = string.Empty;

        var host = NewProxyHost.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            NewProxyError = "Settings_Proxy_Error_HostRequired".GetLocalized();
            return;
        }

        if (!int.TryParse(NewProxyPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            NewProxyError = "Settings_Proxy_Error_PortInvalid".GetLocalized();
            return;
        }

        var scheme = NewProxySchemeIndex >= 0 && NewProxySchemeIndex < ProxySchemeNames.Count
            ? ProxySchemeNames[NewProxySchemeIndex].ToLowerInvariant()
            : "http";

        // Full URI validation up-front: reject hosts Uri would choke on (spaces,
        // invalid chars, "host:port" pasted into the host box, ...) with a friendly
        // error instead of a crash later when the proxy is activated.
        if (!ProxyEntry.IsValidHost(host))
        {
            NewProxyError = "Settings_Proxy_Error_HostInvalid".GetLocalized();
            return;
        }

        var entry = new ProxyEntry
        {
            Name = string.IsNullOrWhiteSpace(NewProxyName) ? $"Proxy {Proxies.Count + 1}" : NewProxyName.Trim(),
            Scheme = scheme,
            Host = host,
            Port = port,
            Username = NewProxyUsername.Trim(),
            Password = NewProxyPassword,
        };

        await _proxyService.AddProxyAsync(entry);

        // Reset the form.
        NewProxyName = NewProxyHost = NewProxyPort = NewProxyUsername = NewProxyPassword = string.Empty;
        NewProxySchemeIndex = 0;
        RefreshProxySelection();
    }

    private async Task RemoveProxyAsync(ProxyEntry entry)
    {
        await _proxyService.RemoveProxyAsync(entry.Id);
        RefreshProxySelection();
    }

    private async Task ActivateProxyAsync(ProxyEntry entry)
    {
        await _proxyService.SetActiveProxyAsync(entry);
        RefreshProxySelection();
    }

    private async Task UseDirectConnectionAsync()
    {
        await _proxyService.SetActiveProxyAsync(null);
        RefreshProxySelection();
    }

    private void RefreshProxySelection()
    {
        OnPropertyChanged(nameof(Proxies));
        IsDirectConnectionSelected = _proxyService.ActiveProxy is null;
        SelectedProxy = _proxyService.ActiveProxy;
    }

    // ------------------------------------------------------------------
    // Download folder
    // ------------------------------------------------------------------

    [ObservableProperty]
    private string _effectiveDownloadFolder =
        DownloadLocationService.Instance.EffectiveDownloadFolder;

    [ObservableProperty]
    private bool _isUsingDefaultDownloadFolder =
        string.IsNullOrWhiteSpace(DownloadLocationService.Instance.ConfiguredDownloadFolder);

    public bool IsUsingDefaultDownloadFolderInverse => !IsUsingDefaultDownloadFolder;

    partial void OnIsUsingDefaultDownloadFolderChanged(bool value) =>
        OnPropertyChanged(nameof(IsUsingDefaultDownloadFolderInverse));

    public ICommand BrowseDownloadFolderCommand => field ??= new AsyncRelayCommand(BrowseDownloadFolderAsync);
    public ICommand ResetDownloadFolderCommand => field ??= new AsyncRelayCommand(ResetDownloadFolderAsync);

    private async Task BrowseDownloadFolderAsync()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var folder = NativeFilePicker.PickFolder(
            hwnd, "Settings_DownloadFolder_PickerTitle".GetLocalized());

        if (string.IsNullOrWhiteSpace(folder))
            return;

        await App.GetService<DownloadLocationService>().SetDownloadFolderAsync(folder);
        EffectiveDownloadFolder = folder;
        IsUsingDefaultDownloadFolder = false;
    }

    private async Task ResetDownloadFolderAsync()
    {
        await App.GetService<DownloadLocationService>().SetDownloadFolderAsync(null);
        EffectiveDownloadFolder = DownloadLocationService.Instance.EffectiveDownloadFolder;
        IsUsingDefaultDownloadFolder = true;
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
