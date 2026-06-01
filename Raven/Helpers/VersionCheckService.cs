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
        string ArchRid
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

        var cookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
        if (!cookieResult.IsSuccess)
        {
            onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
            return null;
        }

        var osArch = GetOsArch(resolvedArchRid);

        var fe3sync = await FE3Handler.SyncUpdatesAsync(
            cookieResult.Value,
            packages.First().WuCategoryId,
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

        if (!fe3sync.IsSuccess)
        {
            onFailure?.Invoke(DownloadUrlFailureReason.StoreQueryFailed);
            return null;
        }

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
            resolvedArchRid
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

        var priorities = Utils.GetArchPriorities(resolvedArchRid, isPackaged: false);

        foreach (var prefArch in priorities)
        {
            var matchingCandidates = unpackagedResult.Value
                .Where(i => Utils.ParseArchString(i.architecture, isPackaged: false) == prefArch)
                .ToList();

            if (matchingCandidates.Any())
            {
                var bestCandidate = matchingCandidates
                    .OrderByDescending(c =>
                        System.Version.TryParse(c.Version, out var v) ? v : new System.Version(0, 0)
                    )
                    .First();

                return new UnpackagedSelectionContext(
                    bestCandidate.InstallerUrl,
                    bestCandidate.FileName,
                    bestCandidate.Version,
                    bestCandidate.InstallerSha256,
                    bestCandidate.architecture
                );
            }
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

                var priorities = Utils.GetArchPriorities(selectionContext.ArchRid, isPackaged: true);

                foreach (var archPref in priorities)
                {
                    var match = selectionContext.Candidates.FirstOrDefault(c =>
                        Utils.ParseArchString(c.FileName ?? c.PackageIdentityName, isPackaged: true)
                        == archPref
                    );

                    if (match != null)
                        return match.Version.ToString();
                }

                return selectionContext.Candidates.FirstOrDefault()?.Version.ToString();
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
