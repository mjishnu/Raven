using StoreListings.Library;

namespace test.Helpers;

/// <summary>
/// Queries the fe3cr endpoint up to the candidate-filtering stage (no URL fetch)
/// to determine the latest available version for a given product.
/// Can be reused from any page that needs version information.
/// </summary>
public static class VersionCheckService
{
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
                IEnumerable<DCATPackage> packages;
                if (prefetchedPackages != null)
                {
                    packages = prefetchedPackages;
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
                        return null;

                    packages = packageResult.Value;
                }

                if (
                    !packages.Any(p =>
                        p.PlatformDependencies.Any(pd => pd.MinVersion <= osVersion)
                    )
                )
                    return null;

                var cookieResult = await FE3Handler.GetCookieAsync(cancellationToken);
                if (!cookieResult.IsSuccess)
                    return null;

                var osArch = archRid switch
                {
                    "arm64" => FE3OSArch.ARM64,
                    "x86" => FE3OSArch.X86,
                    "arm" => FE3OSArch.ARM,
                    _ => FE3OSArch.AMD64,
                };

                var fe3sync = await FE3Handler.SyncUpdatesAsync(
                    cookieResult.Value,
                    packages.First().WuCategoryId,
                    language,
                    market,
                    currentBranch,
                    flightRing,
                    flightingBranchName,
                    osVersion,
                    deviceFamily,
                    cancellationToken,
                    osArch
                );

                if (!fe3sync.IsSuccess)
                    return null;

                var priorities = Utils.GetArchPriorities(archRid, isPackaged: true);
                var candidates = fe3sync.Value.Updates
                    .Where(t =>
                        !t.IsFramework
                        && t.TargetPlatforms.Any(p =>
                            (p.Family == deviceFamily || p.Family == DeviceFamily.Universal)
                            && p.MinVersion <= osVersion
                        )
                    )
                    .OrderByDescending(t => t.Version)
                    .ToList();

                foreach (var archPref in priorities)
                {
                    var match = candidates.FirstOrDefault(c =>
                        Utils.ParseArchString(
                            c.FileName ?? c.PackageIdentityName,
                            isPackaged: true
                        ) == archPref
                    );

                    if (match != null)
                        return match.Version.ToString();
                }

                return candidates.FirstOrDefault()?.Version.ToString();
            }

            case InstallerType.Unpackaged:
            {
                var unpackagedResult = await StoreEdgeFDProduct.GetUnpackagedInstall(
                    productId,
                    market,
                    language,
                    cancellationToken
                );

                if (
                    !unpackagedResult.IsSuccess
                    || unpackagedResult.Value == null
                    || !unpackagedResult.Value.Any()
                )
                    return null;

                var priorities = Utils.GetArchPriorities(archRid, isPackaged: false);

                foreach (var prefArch in priorities)
                {
                    var matchingCandidates = unpackagedResult.Value
                        .Where(i =>
                            Utils.ParseArchString(i.architecture, isPackaged: false) == prefArch
                        )
                        .ToList();

                    if (matchingCandidates.Any())
                    {
                        return matchingCandidates
                            .OrderByDescending(c =>
                                System.Version.TryParse(c.Version, out var v)
                                    ? v
                                    : new System.Version(0, 0)
                            )
                            .First()
                            .Version;
                    }
                }

                return null;
            }

            default:
                return null;
        }
    }
}
