using Microsoft.Extensions.Logging;
using StoreListings.Library;
using Raven.Models;

namespace Raven.Helpers;

/// <summary>
/// Specific reason a download URL could not be resolved for a product, so the UI
/// can show an actionable message instead of a generic "not supported" error and
/// the logs record exactly which stage failed.
/// </summary>
public enum DownloadUrlFailureReason
{
    /// <summary>Querying the Store catalog / update service failed (often transient or network related).</summary>
    StoreQueryFailed,

    /// <summary>The app requires a newer Windows version than this device is running.</summary>
    OsVersionIncompatible,

    /// <summary>No build matches this device's CPU architecture.</summary>
    ArchitectureIncompatible,

    /// <summary>No installable package/installer is published for this app.</summary>
    NoInstallerAvailable,

    /// <summary>A download link for the package or one of its dependencies could not be retrieved.</summary>
    DownloadInfoUnavailable,

    /// <summary>The product uses an installer type Raven does not support.</summary>
    UnsupportedInstallerType,
}

public static class GetDownloadUrl
{
    private static bool IsLikelyResourceOnlyPackage(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var n = fileName;
        return n.Contains("language-", StringComparison.OrdinalIgnoreCase)
            || n.Contains("_language", StringComparison.OrdinalIgnoreCase)
            || n.Contains("scale-", StringComparison.OrdinalIgnoreCase)
            || n.Contains("_scale", StringComparison.OrdinalIgnoreCase)
            || n.Contains("localization", StringComparison.OrdinalIgnoreCase)
            || n.Contains(".resources", StringComparison.OrdinalIgnoreCase)
            || n.Contains("resources", StringComparison.OrdinalIgnoreCase)
            || n.Contains("resource", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses a framework's versioned PackageIdentityName to a stable "family" key by removing
    /// dotted numeric segments, so different releases of the same framework
    /// (e.g. Microsoft.WindowsAppRuntime.1.4 / .1.5 / .1.7 -> microsoft.windowsappruntime) group
    /// together and only the latest is selected. VCLibs (one version) stays its own family.
    /// </summary>
    private static string GetFrameworkFamilyKey(string? packageIdentityName)
    {
        if (string.IsNullOrWhiteSpace(packageIdentityName))
            return string.Empty;

        var segments = packageIdentityName
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.All(char.IsDigit));

        return string.Join('.', segments).ToLowerInvariant();
    }

    /// <summary>
    /// Picks the framework dependency file(s) for a single framework family (every architecture and
    /// version variant grouped under one family key), honouring the architecture priority for the
    /// chosen main.
    /// <para>
    /// Architectures outside the priority list are never returned — an x64 install must not receive
    /// arm64 packages, regardless of mode.
    /// </para>
    /// <para>
    /// Normal mode returns a single file: the highest-priority architecture that exists (x64, then
    /// x86, then neutral for an x64 system), at its latest version. Bypass mode
    /// (<paramref name="includeAllSupportedArchs"/>) returns <i>every</i> supported-architecture
    /// build of <i>every</i> version FE3 returned (older releases included), so the installer can try
    /// them all.
    /// </para>
    /// </summary>
    private static IEnumerable<FE3Handler.SyncUpdatesResponse.Update> SelectFrameworkDependencies(
        IReadOnlyList<FE3Handler.SyncUpdatesResponse.Update> frameworkGroup,
        string archRid,
        bool includeAllSupportedArchs
    )
    {
        if (frameworkGroup.Count == 0)
            return Array.Empty<FE3Handler.SyncUpdatesResponse.Update>();

        // Prefer non-resource packages.
        var nonResource = frameworkGroup
            .Where(u => !IsLikelyResourceOnlyPackage(u.FileName))
            .ToList();

        var candidates = nonResource.Count > 0 ? nonResource : frameworkGroup.ToList();
        var priorities = Utils.GetArchPriorities(archRid, isPackaged: true);

        if (includeAllSupportedArchs)
        {
            // Bypass mode: keep every supported-architecture build of every version. Architectures
            return candidates
                .Where(u =>
                    priorities.Contains(
                        Utils.ParseArchString(
                            u.FileName ?? u.PackageIdentityName,
                            isPackaged: true
                        )
                    )
                )
                .ToList();
        }

        // Normal mode: a single file — the highest-priority architecture that exists, latest version.
        foreach (var pref in priorities)
        {
            var matches = candidates
                .Where(u =>
                    Utils.ParseArchString(
                        u.FileName ?? u.PackageIdentityName,
                        isPackaged: true
                    ) == pref
                )
                .ToList();

            if (matches.Count == 0)
                continue;

            // Latest version of this architecture; shortest filename to avoid edge-case variants.
            return new[]
            {
                matches
                    .OrderByDescending(u => u.Version)
                    .ThenBy(u => (u.FileName ?? string.Empty).Length)
                    .First(),
            };
        }

        // No architecturally-compatible build for the chosen main; contribute no dependency
        return Array.Empty<FE3Handler.SyncUpdatesResponse.Update>();
    }

    public static async Task<FileEntry?> fetch(
        string productId,
        InstallerType installerType,
        CancellationToken cancellationToken = default,
        DeviceFamily deviceFamily = DeviceFamily.Desktop,
        Market market = Market.US,
        Lang language = Lang.en,
        string flightRing = "Retail",
        string flightingBranchName = "Retail",
        string currentBranch = "ge_release",
        bool ignoreDependencyFilter = false,
        Action<DownloadUrlFailureReason>? onFailure = null
    )
    {
        var OSVersion = SystemInfo.GetExactWindowsVersion();
        var archRid = SystemInfo.GetOsArchRid();

        var logger = App.GetService<ILoggerFactory>().CreateLogger(typeof(GetDownloadUrl).FullName!);

        void Fail(DownloadUrlFailureReason reason)
        {
            logger.LogWarning(
                "Download URL resolution failed | Reason={Reason} | ProductId={ProductId} | InstallerType={InstallerType} | OSVersion={OSVersion} | Arch={Arch}",
                reason,
                productId,
                installerType,
                OSVersion,
                archRid
            );
            onFailure?.Invoke(reason);
        }

        switch (installerType)
        {
            case InstallerType.Packaged:
                {
                    var contextFailure = DownloadUrlFailureReason.StoreQueryFailed;
                    var selectionContext = await VersionCheckService.GetPackagedSelectionContextAsync(
                        productId,
                        cancellationToken,
                        prefetchedPackages: null,
                        deviceFamily,
                        market,
                        language,
                        flightRing,
                        flightingBranchName,
                        currentBranch,
                        OSVersion,
                        archRid,
                        onFailure: r => contextFailure = r
                    );

                    if (selectionContext is null)
                    {
                        Fail(contextFailure);
                        return null;
                    }

                    var updates = selectionContext.Updates;
                    var priorities = Utils.GetArchPriorities(selectionContext.ArchRid, isPackaged: true);

                    var frameworkGroups = updates
                        .Where(d =>
                            d.IsFramework
                            && d.TargetPlatforms.Any(tp =>
                                tp.MinVersion <= selectionContext.OsVersion
                                && (
                                    tp.Family == DeviceFamily.Universal
                                    || tp.Family == deviceFamily
                                )
                            )
                        )
                        .GroupBy(d => GetFrameworkFamilyKey(d.PackageIdentityName))
                        .Select(g => g.ToList())
                        .ToList();

                    // Helper func to fetch the download URL + blockmap for one update and package it into a FileEntry
                    async Task<FileEntry?> ResolveDownloadEntry(
                        FE3Handler.SyncUpdatesResponse.Update update,
                        IReadOnlyList<FileEntry> dependencies
                    )
                    {
                        var info = await FE3Handler.GetPackageDownloadInfo(
                            selectionContext.NewCookie,
                            update.UpdateID,
                            update.RevisionNumber,
                            update.Digest,
                            language,
                            market,
                            currentBranch,
                            flightRing,
                            flightingBranchName,
                            selectionContext.OsVersion,
                            deviceFamily,
                            cancellationToken,
                            selectionContext.OsArch
                        );

                        if (!info.IsSuccess)
                            return null;

                        update.SetDownloadInfoPackageDigest(info.Value.Package.Digest);
                        update.SetDownloadInfoBlockmapUrl(info.Value.BlockmapCab?.Url);
                        update.SetDownloadInfoBlockmapDigest(info.Value.BlockmapCab?.Digest);

                        return new FileEntry(
                            FileName: update.FileName,
                            Url: info.Value.Package.Url,
                            Dependencies: dependencies,
                            Digest: update.GetDownloadInfoPackageDigest(),
                            BlockmapUrl: update.GetDownloadInfoBlockmapUrl(),
                            BlockmapCabFileDigest: update.GetDownloadInfoBlockmapDigest()
                        );
                    }

                    var anyArchMatch = false;

                    // Iterate through the archs assume equal priority for main and dependencies
                    foreach (var archPref in priorities)
                    {
                        // check main exist for this arch
                        var archCandidates = selectionContext
                            .Candidates.Where(c =>
                                Utils.ParseArchString(
                                    c.FileName ?? c.PackageIdentityName,
                                    isPackaged: true
                                ) == archPref
                            )
                            .ToList();

                        // if no main then skip this arch
                        if (archCandidates.Count == 0)
                            continue;

                        anyArchMatch = true;

                        var depArch = archPref == "neutral" ? selectionContext.ArchRid : archPref;

                        var requiredDepUpdates = frameworkGroups
                            .SelectMany(group =>
                                SelectFrameworkDependencies(
                                    group,
                                    depArch,
                                    includeAllSupportedArchs: ignoreDependencyFilter
                                )
                            )
                            .Distinct()
                            .ToList();

                        var depEntries = new List<FileEntry>();
                        var depsResolved = true;

                        //  Get url for all dependencies for this arch
                        foreach (var depUpdate in requiredDepUpdates)
                        {
                            var depEntry = await ResolveDownloadEntry(
                                depUpdate, 
                                Array.Empty<FileEntry>()
                            );

                            if (depEntry is null)
                            {
                                depsResolved = false;
                                break;
                            }

                            depEntries.Add(depEntry);
                        }

                        if (!depsResolved)
                            continue; // A dependency URL failed; try the next architecture.

                        // Get url for main file for this arch
                        foreach (var main in archCandidates)
                        {
                            var mainEntry = await ResolveDownloadEntry(main, depEntries);

                            if (mainEntry is null)
                                continue; // Main URL failed; try the next candidate.

                            return mainEntry;
                        }
                    }

                    Fail(anyArchMatch ? DownloadUrlFailureReason.DownloadInfoUnavailable : DownloadUrlFailureReason.ArchitectureIncompatible);
                    return null;
                }
            case InstallerType.Unpackaged:
                {
                    var contextFailure = DownloadUrlFailureReason.NoInstallerAvailable;
                    var selectionContext = await VersionCheckService.GetUnpackagedSelectionContextAsync(
                        productId,
                        cancellationToken,
                        market,
                        language,
                        archRid,
                        onFailure: r => contextFailure = r
                    );

                    if (selectionContext is null)
                    {
                        Fail(contextFailure);
                        return null;
                    }

                    return new FileEntry(
                        FileName: selectionContext.FileName,
                        Url: selectionContext.InstallerUrl,
                        Dependencies: Array.Empty<FileEntry>(),
                        Sha256: selectionContext.InstallerSha256
                    );
                }

            default:
                Fail(DownloadUrlFailureReason.UnsupportedInstallerType);
                return null;
        }
    }
}
