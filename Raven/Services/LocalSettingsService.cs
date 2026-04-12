using System.Text.Json;

using Microsoft.Extensions.Options;

using Raven.Contracts.Services;
using Raven.Models;

namespace Raven.Services;

public class LocalSettingsService : ILocalSettingsService
{
    private const string _defaultApplicationDataFolder = "Raven/ApplicationData";
    private const string _defaultLocalSettingsFile = "LocalSettings.json";

    private readonly LocalSettingsOptions _options;

    private readonly string _localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string _applicationDataFolder;
    private readonly string _localsettingsFile;

    private Dictionary<string, string> _settings;

    private bool _isInitialized;

    public LocalSettingsService(IOptions<LocalSettingsOptions> options)
    {
        _options = options.Value;

        _applicationDataFolder = Path.Combine(_localApplicationData, _options.ApplicationDataFolder ?? _defaultApplicationDataFolder);
        _localsettingsFile = _options.LocalSettingsFile ?? _defaultLocalSettingsFile;

        _settings = new Dictionary<string, string>();
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        var path = Path.Combine(_applicationDataFolder, _localsettingsFile);
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            _settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }

        _isInitialized = true;
    }

    public async Task<T?> ReadSettingAsync<T>(string key)
    {
        await InitializeAsync();

        if (_settings.TryGetValue(key, out var obj))
        {
            return JsonSerializer.Deserialize<T>(obj);
        }

        return default;
    }

    public async Task SaveSettingAsync<T>(string key, T value)
    {
        await InitializeAsync();

        _settings[key] = JsonSerializer.Serialize(value);

        Directory.CreateDirectory(_applicationDataFolder);
        await File.WriteAllTextAsync(
            Path.Combine(_applicationDataFolder, _localsettingsFile),
            JsonSerializer.Serialize(_settings)
        );
    }
}
