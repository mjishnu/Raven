using Raven.Contracts.Services;

namespace Raven.Services;

/// <summary>
/// Persists the user's preferred download root folder. When unset, downloads go to
/// %USERPROFILE%\Downloads\Raven (the app default).
/// </summary>
public class DownloadLocationService
{
    private const string DownloadFolderKey = "CustomDownloadFolder";

    /// <summary>Static access for code paths outside DI (DownloadManagerService).</summary>
    public static DownloadLocationService Instance => _instance.Value;

    // Single shared instance used both by DI and the static accessor, so state set via
    // SetDownloadFolderAsync is always visible to GetDownloadsRootFolder().
    private static readonly Lazy<DownloadLocationService> _instance = new(() =>
        new DownloadLocationService(App.GetService<ILocalSettingsService>())
    );

    private readonly ILocalSettingsService _localSettingsService;

    public DownloadLocationService(ILocalSettingsService localSettingsService)
    {
        _localSettingsService = localSettingsService;
    }

    /// <summary>The configured custom folder, or null when using the default location.</summary>
    public string? ConfiguredDownloadFolder { get; private set; }

    /// <summary>The folder new downloads will land in right now (resolved default included).</summary>
    public string EffectiveDownloadFolder =>
        string.IsNullOrWhiteSpace(ConfiguredDownloadFolder)
            ? DownloadManagerService.GetDefaultDownloadsRootFolder()
            : ConfiguredDownloadFolder;

    public async Task InitializeAsync()
    {
        ConfiguredDownloadFolder = await _localSettingsService.ReadSettingAsync<string>(DownloadFolderKey);
    }

    public async Task SetDownloadFolderAsync(string? folder)
    {
        ConfiguredDownloadFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        await _localSettingsService.SaveSettingAsync(DownloadFolderKey, ConfiguredDownloadFolder);
    }
}
