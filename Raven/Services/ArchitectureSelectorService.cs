using Raven.Contracts.Services;
using Raven.Helpers;
using StoreListings.Library;

namespace Raven.Services;

public class ArchitectureSelectorService : IArchitectureSelectorService
{
    private const string ArchitectureSettingsKey = "PreferredArchitectureRid";

    private readonly ILocalSettingsService _localSettingsService;

    public string SelectedArchRid { get; private set; } = SystemInfo.GetSystemArchRid();

    public StoreEdgeFDArch SelectedStoreEdgeArchitecture => SystemInfo.ToStoreEdgeFDArch(SelectedArchRid);

    public ArchitectureSelectorService(ILocalSettingsService localSettingsService)
    {
        _localSettingsService = localSettingsService;
    }

    public async Task InitializeAsync()
    {
        var savedArchRid = await _localSettingsService.ReadSettingAsync<string>(ArchitectureSettingsKey);

        if (TryNormalizeArchRid(savedArchRid, out var normalizedSavedArch))
        {
            SelectedArchRid = normalizedSavedArch;
            return;
        }

        SelectedArchRid = SystemInfo.GetSystemArchRid();
        await _localSettingsService.SaveSettingAsync(ArchitectureSettingsKey, SelectedArchRid);
    }

    public async Task SetSelectedArchitectureAsync(StoreEdgeFDArch architecture)
    {
        var archRid = SystemInfo.ToArchRid(architecture);
        if (string.Equals(SelectedArchRid, archRid, StringComparison.OrdinalIgnoreCase))
            return;

        SelectedArchRid = archRid;
        await _localSettingsService.SaveSettingAsync(ArchitectureSettingsKey, SelectedArchRid);
    }

    public async Task ResetToDefaultAsync()
    {
        SelectedArchRid = SystemInfo.GetSystemArchRid();
        await _localSettingsService.SaveSettingAsync(ArchitectureSettingsKey, SelectedArchRid);
    }

    private static bool TryNormalizeArchRid(string? archRid, out string normalized)
    {
        normalized = archRid?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized is "x64" or "x86" or "arm" or "arm64")
            return true;

        normalized = string.Empty;
        return false;
    }
}
