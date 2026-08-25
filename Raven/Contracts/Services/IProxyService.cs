using System.Net;

using Raven.Models;

namespace Raven.Contracts.Services;

/// <summary>
/// Central store of user-defined proxies plus the currently active one.
/// When a proxy is active every HTTP client the app creates (Store API, FE3,
/// delta downloads, full downloads, GitHub update checks) routes through it.
/// </summary>
public interface IProxyService
{
    /// <summary>All saved proxy entries (persisted across restarts).</summary>
    IReadOnlyList<ProxyEntry> Proxies { get; }

    /// <summary>The currently active proxy, or null when direct connection is used.</summary>
    ProxyEntry? ActiveProxy { get; }

    /// <summary>Raised after any change (add/remove/switch) so listeners can refresh.</summary>
    event EventHandler? Changed;

    Task InitializeAsync();

    Task AddProxyAsync(ProxyEntry entry);

    Task RemoveProxyAsync(string id);

    /// <summary>Sets the active proxy. Pass null to disable proxying.</summary>
    Task SetActiveProxyAsync(ProxyEntry? entry);

    /// <summary>
    /// The WebProxy to use for new HttpClient instances, or null for a direct
    /// system-default connection. Read this when constructing handlers.
    /// </summary>
    IWebProxy? GetWebProxy();
}
