using StoreListings.Library;

namespace Raven.Helpers;

/// <summary>
/// Queries the fe3cr endpoint up to the candidate-filtering stage (no URL fetch)
/// to determine the latest available version for a given product.
/// Can be reused from any page that needs version information.
/// </summary>
public static class VersionCheckService
{
    internal sealed record PackagedSelectionContext(
        IReadOnlyList<DCATPackage> Packages,
        IReadOnlyList<FE3Handler.SyncUpdatesResponse.Update> Updates,
        IReadOnlyList<FE3Handler.SyncUpdatesResponse.Update> Candidates,
        FE3Handler.Cookie NewCookie,
        FE3OSArch OsArch,
        StoreListings.Library.Version OsVersion,
        string ArchRid,
        FE3Handler.SyncUpdatesResponse SyncResponse
    );

    internal sealed record UnpackagedSelectionContext(
        string InstallerUrl,
        string FileName,
        string Version,
        string InstallerSha256,
        string Architecture
    );

    private static FE3OSArch GetOsArch(string archRid) =>
        archRid switch
        {
            "arm64" => FE3OSArch.ARM64,
            "x86" => FE3OSArch.X86,
            _ => FE3OSArch.AMD64,
        };

    // FE3 cookies carry an Expiration and are protocol-sanctioned for reuse (SyncUpdates
    // itself chains NewCookie). Caching one per process skips a GetCookie SOAP round trip
    // (~100-400ms) on every install / version check after the first; "Update all" of N apps
    // issues ~1 GetCookie instead of N.
    private static readonly object _cookieLock = new();
    private static FE3Handler.Cookie? _cachedCookie;
    private static DateTimeOffset _cachedCookieExpiryUtc;

    private static FE3Handler.Cookie? TryGetCachedCookie()
    {
        lock (_cookieLock)
        {
            return _cachedCookie is not null
                && DateTimeOffset.UtcNow < _cachedCookieExpiryUtc - TimeSpan.FromMinutes(5)
                ? _cachedCookie
                : null;
        }
    }

    private static void CacheCookie(FE3Handler.Cookie cookie)
    {
        // Unparseable expiration: don't cache — behavior stays exactly as before.
        if (!DateTimeOffset.TryParse(
                cookie.Expiration,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var expiry))
            return;
        lock (_cookieLock)
        {
            _cachedCookie = cookie;
            _cachedCookieExpiryUtc = expiry;
        }
    }

    private static void InvalidateCachedCookie(FE3Handler.Cookie cookie)
    {
        lock (_cookieLock)
        {
            if (ReferenceEquals(_cachedCookie, cookie))
                _cachedCookie = null;
        }
    }

    internal static async Task<PackagedSelectionContext?> GetPackagedSelectionContextAsync(
        string productId,
        CancellationToken cancellationToken = default,
        IEnumerable<DCATPackage>? prefetchedPackages = null,
        DeviceFamily deviceFamily = DeviceFamily.Desktop,
        Market market = Market.US,
        Lang language = Lang.en,
        string flightRing = "Retail",
        string flightingBranchName = "Retail",
        string currentBranch = "ge_release",
        StoreListings.Library.Version? osVersion = null,
        string? archRid = null,
        Action<DownloadUrlFailureReason>? onFailure = null
    )
    {
        var resolvedOsVersion = osVersion ?? SystemInfo.GetExactWindowsVersion();
        var resolvedArchRid = archRid ?? SystemInfo.GetOsArchRid();

        IReadOnlyList<DCATPackage> packages;
        if (prefetchedPackages != null)
        {
            packages = prefetchedPackages.ToList();
        }
        else
        {
            var packageResult = await DCATPackage.GetPackagesAsync(
                productId,
                market,
                language,
                true,
                cancellationToken
            );

            if (!packageResult.IsSuccess)
            {
                onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
                return null;
            }

            packages = packageResult.Value.ToList();
        }

        if (!packages.Any(p => p.PlatformDependencies.Any(pd => pd.MinVersion <= resolvedOsVersion)))
        {
            onFailure?.Invoke(DownloadUrlFailureReason.OsVersionIncompatible);
            return null;
        }

        var osArch = GetOsArch(resolvedArchRid);

        var cachedCookie = TryGetCachedCookie();
        FE3Handler.Cookie fe3Cookie;
        if (cachedCookie is not null)
        {
            fe3Cookie = cachedCookie;
        }
        else
        {
            var cookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
            if (!cookieResult.IsSuccess)
            {
                onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
                return null;
            }
            fe3Cookie = cookieResult.Value;
        }

        var wuCategoryId = packages.First().WuCategoryId;

        var fe3sync = await FE3Handler.SyncUpdatesAsync(
            fe3Cookie,
            wuCategoryId,
            language,
            market,
            currentBranch,
            flightRing,
            flightingBranchName,
            resolvedOsVersion,
            deviceFamily,
            cancellationToken,
            osArch
        );

        // A cached cookie may have been rejected/expired server-side: retry once with a fresh one.
        if (!fe3sync.IsSuccess && cachedCookie is not null)
        {
            // FE3Handler wraps OperationCanceledException into a failed Result, so a user
            // cancel looks identical to a cookie rejection here. Don't discard a still-valid
            // cookie or issue a doomed retry with an already-cancelled token.
            if (cancellationToken.IsCancellationRequested)
            {
                onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
                return null;
            }

            InvalidateCachedCookie(cachedCookie);
            var freshCookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
            if (!freshCookieResult.IsSuccess)
            {
                onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
                return null;
            }
            fe3sync = await FE3Handler.SyncUpdatesAsync(
                freshCookieResult.Value,
                wuCategoryId,
                language,
                market,
                currentBranch,
                flightRing,
                flightingBranchName,
                resolvedOsVersion,
                deviceFamily,
                cancellationToken,
                osArch
            );
        }

        if (!fe3sync.IsSuccess)
        {
            onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
            return null;
        }

        // Roll the cache forward with the server-refreshed cookie from this sync.
        CacheCookie(fe3sync.Value.NewCookie);

        var updates = fe3sync.Value.Updates.ToList();
        var candidates = updates
            .Where(t =>
                !t.IsFramework
                && t.TargetPlatforms.Any(p =>
                    (p.Family == deviceFamily || p.Family == DeviceFamily.Universal)
                    && p.MinVersion <= resolvedOsVersion
                )
            )
            .OrderByDescending(t => t.Version)
            .ToList();

        return new PackagedSelectionContext(
            packages,
            updates,
            candidates,
            fe3sync.Value.NewCookie,
            osArch,
            resolvedOsVersion,
            resolvedArchRid,
            fe3sync.Value
        );
    }

    internal static async Task<UnpackagedSelectionContext?> GetUnpackagedSelectionContextAsync(
        string productId,
        CancellationToken cancellationToken = default,
        Market market = Market.US,
        Lang language = Lang.en,
        string? archRid = null,
        Action<DownloadUrlFailureReason>? onFailure = null
    )
    {
        var resolvedArchRid = archRid ?? SystemInfo.GetOsArchRid();

        var unpackagedResult = await StoreEdgeFDProduct.GetUnpackagedInstall(
            productId,
            market,
            language,
            cancellationToken
        );

        if (!unpackagedResult.IsSuccess || unpackagedResult.Value == null || !unpackagedResult.Value.Any())
        {
            onFailure?.Invoke(DownloadUrlFailureReason.NoInstallerAvailable);
            return null;
        }

        var bestCandidate = Utils.OrderCandidatesByVersionAndArch(
            unpackagedResult.Value,
            i => i.architecture,
            i => StoreListings.Library.Version.TryParse(i.Version, null, out var v) ? v : default,
            resolvedArchRid,
            isPackaged: false
        ).FirstOrDefault();

        if (!string.IsNullOrEmpty(bestCandidate.InstallerUrl))
        {
            return new UnpackagedSelectionContext(
                bestCandidate.InstallerUrl,
                bestCandidate.FileName,
                bestCandidate.Version,
                bestCandidate.InstallerSha256,
                bestCandidate.architecture
            );
        }

        onFailure?.Invoke(DownloadUrlFailureReason.ArchitectureIncompatible);
        return null;
    }

    /// <summary>
    /// Returns the latest available version string for the given product,
    /// or <c>null</c> if the version could not be determined.
    /// </summary>
    public static async Task<string?> GetLatestVersionAsync(
        string productId,
        InstallerType installerType,
        CancellationToken cancellationToken = default,
        IEnumerable<DCATPackage>? prefetchedPackages = null,
        DeviceFamily deviceFamily = DeviceFamily.Desktop,
        Market market = Market.US,
        Lang language = Lang.en,
        string flightRing = "Retail",
        string flightingBranchName = "Retail",
        string currentBranch = "ge_release"
    )
    {
        var osVersion = SystemInfo.GetExactWindowsVersion();
        var archRid = SystemInfo.GetOsArchRid();

        switch (installerType)
        {
            case InstallerType.Packaged:
            {
                var selectionContext = await GetPackagedSelectionContextAsync(
                    productId,
                    cancellationToken,
                    prefetchedPackages,
                    deviceFamily,
                    market,
                    language,
                    flightRing,
                    flightingBranchName,
                    currentBranch,
                    osVersion,
                    archRid
                );

                if (selectionContext is null)
                    return null;

                var bestMatch = Utils.OrderCandidatesByVersionAndArch(
                    selectionContext.Candidates,
                    c => c.FileName ?? c.PackageIdentityName,
                    c => c.Version,
                    selectionContext.ArchRid,
                    isPackaged: true
                ).FirstOrDefault();

                return bestMatch?.Version.ToString();
            }

            case InstallerType.Unpackaged:
            {
                var selectionContext = await GetUnpackagedSelectionContextAsync(
                    productId,
                    cancellationToken,
                    market,
                    language,
                    archRid
                );

                return selectionContext?.Version;
            }

            default:
                return null;
        }
    }
}
