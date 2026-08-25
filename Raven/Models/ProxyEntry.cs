namespace Raven.Models;

using System.Windows.Input;

using Raven.Services;

/// <summary>
/// A user-defined HTTP(S) proxy entry that can be stored and activated from Settings.
/// </summary>
public class ProxyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Proxy scheme/type: http, socks5, or socks4.</summary>
    public string Scheme { get; set; } = "http";

    /// <summary>Friendly display name chosen by the user (e.g. "Home V2Ray").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Proxy host name or IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Proxy port.</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Optional username for authenticated proxies. Empty when not needed.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Optional password for authenticated proxies. Empty when not needed.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Builds the proxy URI (scheme://[user:pass@]host:port), used both for display
    /// and as input validation feedback. Returns null when the host is not a valid
    /// host name / IP so callers can show a validation error instead of crashing.
    /// </summary>
    public Uri? TryToUri()
    {
        try
        {
            return ToUri();
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    public Uri ToUri()
    {
        var scheme = string.IsNullOrWhiteSpace(Scheme) ? "http" : Scheme.Trim().ToLowerInvariant();
        var builder = new UriBuilder
        {
            Scheme = scheme,
            Host = Host.Trim(),
            Port = Port,
        };

        if (!string.IsNullOrWhiteSpace(Username))
        {
            builder.UserName = Username;
            if (!string.IsNullOrWhiteSpace(Password))
                builder.Password = Password;
        }

        return builder.Uri;
    }

    /// <summary>
    /// Strict host validation: exactly four numeric octets, each 0-255
    /// (the ---.---.---.--- format). Domain names are rejected.
    /// </summary>
    public static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var parts = host.Trim().Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3
                || !part.All(char.IsAsciiDigit)
                || !int.TryParse(part, out var octet)
                || octet > 255)
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Name)
        ? $"{Host}:{Port}"
        : Name;

    /// <summary>"scheme://host:port" (plus user hint) shown under the entry name in the UI.</summary>
    public string AddressText
    {
        get
        {
            var scheme = string.IsNullOrWhiteSpace(Scheme) ? "http" : Scheme.Trim().ToLowerInvariant();
            var baseText = $"{scheme}://{Host}:{Port}";
            return string.IsNullOrWhiteSpace(Username)
                ? baseText
                : $"{baseText} ({Username})";
        }
    }

    /// <summary>Commands wired by ProxyService so the Settings list can activate/remove this entry.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ICommand ActivateCommand => ProxyService.Instance.ActivateEntryCommand;

    [System.Text.Json.Serialization.JsonIgnore]
    public ICommand RemoveCommand => ProxyService.Instance.RemoveEntryCommand;

    /// <summary>UI-only flag: true when this entry is the active proxy. Set by ProxyService.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive { get; set; }
}
