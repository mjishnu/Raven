using System.Net;

using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Models;

namespace Raven.Services;

/// <summary>
/// Persists the user's proxy list in LocalSettings.json and exposes the active
/// proxy to every HTTP client factory in the app. The active proxy is stored by
/// id so renaming an entry keeps it selected.
/// </summary>
public class ProxyService : IProxyService
{
    private const string ProxiesSettingsKey = "SavedProxies";
    private const string ActiveProxyIdKey = "ActiveProxyId";

    /// <summary>Static access for code paths outside DI (DownloadHelper).</summary>
    public static ProxyService Instance => _instance.Value;

    private static readonly Lazy<ProxyService> _instance = new(() =>
        new ProxyService(App.GetService<ILocalSettingsService>())
    );

    private readonly ILocalSettingsService _localSettingsService;
    private readonly object _lock = new();

    private List<ProxyEntry> _proxies = [];
    private string? _activeProxyId;

    public event EventHandler? Changed;

    public ProxyService(ILocalSettingsService localSettingsService)
    {
        _localSettingsService = localSettingsService;
    }

    public IReadOnlyList<ProxyEntry> Proxies
    {
        get { lock (_lock) return _proxies.ToList(); }
    }

    public ProxyEntry? ActiveProxy
    {
        get
        {
            lock (_lock)
            {
                return _activeProxyId is null
                    ? null
                    : _proxies.FirstOrDefault(p => p.Id == _activeProxyId);
            }
        }
    }

    public async Task InitializeAsync()
    {
        var saved = await _localSettingsService.ReadSettingAsync<List<ProxyEntry>>(ProxiesSettingsKey);
        if (saved is not null)
        {
            lock (_lock)
                _proxies = saved.Where(p => !string.IsNullOrWhiteSpace(p?.Host)).ToList();
        }

        var activeId = await _localSettingsService.ReadSettingAsync<string>(ActiveProxyIdKey);
        lock (_lock)
        {
            // Older builds double-serialized the id (stored as "\"id\""); trim stray quotes.
            activeId = activeId?.Trim().Trim('"');

            // Only keep the saved selection when it still points at a known entry.
            _activeProxyId = activeId is not null && _proxies.Any(p => p.Id == activeId)
                ? activeId
                : null;
        }

        RefreshActiveFlags();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddProxyAsync(ProxyEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Host))
            return;

        // First proxy added becomes active immediately — the user adding one clearly
        // wants it used; further adds stay passive until switched to.
        bool shouldActivate;
        lock (_lock)
        {
            entry.Name = string.IsNullOrWhiteSpace(entry.Name)
                ? $"Proxy {_proxies.Count + 1}"
                : entry.Name.Trim();
            _proxies.Add(entry);
            shouldActivate = _activeProxyId is null;
            if (shouldActivate)
                _activeProxyId = entry.Id;
        }

        await PersistAsync();
        if (!shouldActivate)
            Changed?.Invoke(this, EventArgs.Empty);
        else
            await SetActiveProxyAsync(GetById(_activeProxyId));
    }

    public async Task RemoveProxyAsync(string id)
    {
        bool wasActive;
        lock (_lock)
        {
            wasActive = _activeProxyId == id;
            _proxies.RemoveAll(p => p.Id == id);
            if (wasActive)
                _activeProxyId = null;
        }

        await PersistAsync();

        if (wasActive)
            await ApplyActiveProxyAsync(null);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the active entry so radio buttons in the UI show the right state.</summary>
    private void RefreshActiveFlags()
    {
        var active = ActiveProxy;
        lock (_lock)
        {
            foreach (var p in _proxies)
                p.IsActive = active is not null && ReferenceEquals(p, active);
        }
    }

    public async Task SetActiveProxyAsync(ProxyEntry? entry)
    {
        lock (_lock)
            _activeProxyId = entry?.Id;

        await PersistAsync();
        await ApplyActiveProxyAsync(GetWebProxy());
    }

    public IWebProxy? GetWebProxy()
    {
        var proxy = ActiveProxy;
        if (proxy is null)
            return null;

        var uri = proxy.TryToUri();
        if (uri is null)
            return null;

        var webProxy = new WebProxy(uri);

        // SOCKS auth must go through Credentials (user/pass in the URL is ignored).
        if (!string.IsNullOrWhiteSpace(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        return webProxy;
    }

    /// <summary>Shared commands the Settings list binds per-entry via x:Bind.</summary>
    public System.Windows.Input.ICommand ActivateEntryCommand { get; } =
        new CommunityToolkit.Mvvm.Input.RelayCommand<ProxyEntry>(async entry =>
        {
            if (entry is not null)
                await Instance.SetActiveProxyAsync(entry);
        });

    public System.Windows.Input.ICommand RemoveEntryCommand { get; } =
        new CommunityToolkit.Mvvm.Input.RelayCommand<ProxyEntry>(entry =>
        {
            // Temp debug logging (remove once confirmed).
            try
            {
                System.IO.File.AppendAllText(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Raven", "proxy-debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} Remove clicked id={entry?.Id ?? "<null>"}\n");
            }
            catch { }

            if (entry is not null)
                _ = Instance.RemoveProxyAsync(entry.Id);
        });

    private ProxyEntry? GetById(string? id)
    {
        if (id is null)
            return null;
        lock (_lock)
            return _proxies.FirstOrDefault(p => p.Id == id);
    }

    private async Task ApplyActiveProxyAsync(IWebProxy? webProxy)
    {
        // Push the new proxy into every shared client the app owns, then notify UI.
        StoreListings.Library.Internal.ProxyManager.SetProxy(webProxy);
        DownloadHelper.ApplyProxy(webProxy);
        GitHubUpdaterService.ApplyProxy(webProxy);

        RefreshActiveFlags();
        Changed?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    private async Task PersistAsync()
    {
        List<ProxyEntry> snapshot;
        string? activeId;
        lock (_lock)
        {
            snapshot = _proxies.ToList();
            activeId = _activeProxyId;
        }

        await _localSettingsService.SaveSettingAsync(ProxiesSettingsKey, snapshot);
        await _localSettingsService.SaveSettingAsync(ActiveProxyIdKey, activeId is null
            ? null
            : activeId.Trim().Trim('"'));
    }
}
