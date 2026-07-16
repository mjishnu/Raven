using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace Raven.Services;

public class PackageDeploymentException : InvalidOperationException
{
    public int OuterHResult { get; }
    public int? ExtendedHResult { get; }
    public string? DeploymentErrorText { get; }

    public PackageDeploymentException(int outerHresult, int? extendedHresult, string? deploymentErrorText, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        OuterHResult = outerHresult;
        ExtendedHResult = extendedHresult;
        DeploymentErrorText = deploymentErrorText;
    }
}

public static class AppPackageInstaller
{
    public sealed record InstallProgress(int Percent, string? State, string? Activity);

    private static readonly string[] SupportedExtensions =
    [
        ".msix",
        ".appx",
        ".msixbundle",
        ".appxbundle",
    ];

    private static bool IsPackageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return SupportedExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds an <see cref="AddPackageOptions"/> with the specified flags and dependency URIs.
    /// </summary>
    private static AddPackageOptions BuildAddPackageOptions(
        bool forceAppShutdown,
        bool forceUpdateFromAnyVersion,
        bool deferRegistration,
        IEnumerable<Uri>? dependencyPackageUris = null
    )
    {
        var options = new AddPackageOptions
        {
            ForceAppShutdown = forceAppShutdown,
            ForceUpdateFromAnyVersion = forceUpdateFromAnyVersion,
            DeferRegistrationWhenPackagesAreInUse = deferRegistration,
        };

        if (dependencyPackageUris != null)
        {
            foreach (var dep in dependencyPackageUris)
            {
                options.DependencyPackageUris.Add(dep);
            }
        }

        return options;
    }

    private static async Task AddPackageAsync(
        PackageManager packageManager,
        string packagePath,
        IProgress<InstallProgress>? progress,
        AddPackageOptions addPackageOptions,
        CancellationToken cancellationToken
    )
    {
        var packageUri = new Uri(Path.GetFullPath(packagePath));

        var deploymentOperation = packageManager.AddPackageByUriAsync(
            packageUri,
            addPackageOptions
        );

        deploymentOperation.Progress = (_, p) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var percent = (int)Math.Clamp(p.percentage, 0, 100);
            progress?.Report(new InstallProgress(percent, p.state.ToString(), "Install"));
        };

        try
        {
            var result = await deploymentOperation.AsTask(cancellationToken);

            if (result.ErrorText is { Length: > 0 })
                throw new InvalidOperationException(result.ErrorText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // When the deployment engine fails, the raw COMException only carries an
            // HRESULT (e.g. 0x80073CF9). The actual diagnostic detail — such as missing
            // framework dependencies, disk errors, or signature issues — lives in the
            // DeploymentResult attached to the async operation. Extract it so callers
            // (and logs) see the real reason for the failure.
            string? deploymentErrorText = null;
            int? extendedHResult = null;

            try
            {
                var result = deploymentOperation.GetResults();
                if (result?.ErrorText is { Length: > 0 })
                    deploymentErrorText = result.ErrorText;
                if (result?.ExtendedErrorCode != null)
                    extendedHResult = result.ExtendedErrorCode.HResult;
            }
            catch
            {
                // Best-effort; if we can't read the result, fall through with the original exception.
            }

            if (!string.IsNullOrWhiteSpace(deploymentErrorText))
            {
                var friendlyMessage = Raven.Helpers.InstallHelper.GetFriendlyMsixError(ex.HResult, deploymentErrorText, extendedHResult);
                throw new PackageDeploymentException(ex.HResult, extendedHResult, deploymentErrorText, friendlyMessage, ex);
            }

            throw;
        }
    }

    public static async Task InstallAsync(
        string packagePath,
        IEnumerable<string>? dependencyPackagePaths = null,
        IProgress<InstallProgress>? progress = null,
        bool ignoreVersion = false,
        bool installDependenciesSeparately = false,
        bool deferRegistration = false,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    )
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path is required.", nameof(packagePath));

        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Package file not found.", packagePath);

        var deps = (dependencyPackagePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Where(IsPackageFile)
            .ToList();

        var dependencyUris = deps.Select(p => new Uri(Path.GetFullPath(p))).ToList();

        progress?.Report(new InstallProgress(0, "Starting", "Install"));

        var packageManager = new PackageManager();

        if (installDependenciesSeparately)
        {
            await InstallWithSeparateDependenciesAsync(
                packageManager,
                packagePath,
                dependencyUris,
                progress,
                forceUpdateFromAnyVersion: ignoreVersion,
                deferRegistration,
                cancellationToken,
                logger
            );

            progress?.Report(new InstallProgress(100, "Completed", "Install"));
            return;
        }

        var addPackageOptions = BuildAddPackageOptions(
            forceAppShutdown: true,
            forceUpdateFromAnyVersion: ignoreVersion,
            deferRegistration,
            dependencyUris
        );

        try
        {
            await AddPackageAsync(
                packageManager,
                packagePath,
                progress,
                addPackageOptions,
                cancellationToken
            );
        }
        catch (PackageDeploymentException pde)
        {
            logger?.LogError(
                pde,
                "Package install failed | Path={PackagePath} | Dependencies={DependencyCount} | Error={Error} | HRESULT=0x{HResult:X8} | Extended=0x{ExtendedHResult:X8}",
                packagePath,
                dependencyUris.Count,
                pde.Message,
                pde.OuterHResult,
                pde.ExtendedHResult ?? 0
            );
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Package install failed | Path={PackagePath} | Dependencies={DependencyCount} | IgnoreVersion={IgnoreVersion}",
                packagePath,
                dependencyUris.Count,
                ignoreVersion
            );
            throw;
        }

        progress?.Report(new InstallProgress(100, "Completed", "Install"));
    }

    /// <summary>
    /// Installs each dependency as its own standalone package (best-effort), then installs the main
    /// package without forcing its dependency array. Used in bypass mode where the dependency set may
    /// contain extra architectures or superseded framework versions: those install (or fail) on their
    /// own and the ones that don't apply are simply skipped, instead of failing the whole main install
    /// — which is what happens when an incompatible package is passed as one of the main's required
    /// dependencies.
    /// </summary>
    private static async Task InstallWithSeparateDependenciesAsync(
        PackageManager packageManager,
        string packagePath,
        IReadOnlyList<Uri> dependencyUris,
        IProgress<InstallProgress>? progress,
        bool forceUpdateFromAnyVersion,
        bool deferRegistration,
        CancellationToken cancellationToken,
        ILogger? logger
    )
    {
        // Install dependencies first, each as an independent package. Per-package failures are logged
        // and skipped so an incompatible-arch or older variant never aborts the run.
        for (var i = 0; i < dependencyUris.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var depUri = dependencyUris[i];

            // Offer the other selected packages as available dependencies so a framework that depends
            // on another selected framework still resolves regardless of install order.
            var siblings = dependencyUris.Where((_, idx) => idx != i).ToList();

            var depOptions = BuildAddPackageOptions(
                forceAppShutdown: true,
                forceUpdateFromAnyVersion,
                deferRegistration,
                siblings
            );

            try
            {
                var depResult = await packageManager
                    .AddPackageByUriAsync(depUri, depOptions)
                    .AsTask(cancellationToken);

                if (depResult.ErrorText is { Length: > 0 })
                    logger?.LogWarning(
                        "Bypass install: dependency add reported an error for {Dep}: {Error}",
                        depUri.LocalPath,
                        depResult.ErrorText
                    );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "Bypass install: dependency add failed or skipped for {Dep}",
                    depUri.LocalPath
                );
            }

            // Reserve the last slice of the bar for the main package.
            var pct = (int)((i + 1) / (double)(dependencyUris.Count + 1) * 100);
            progress?.Report(new InstallProgress(pct, "Dependencies", "Install"));
        }

        // Install the main last with no forced dependency array: the frameworks it needs have just
        // been registered above, so the deployment engine resolves them from the installed set.
        var mainOptions = BuildAddPackageOptions(
            forceAppShutdown: true,
            forceUpdateFromAnyVersion,
            deferRegistration
        );

        try
        {
            await AddPackageAsync(
                packageManager,
                packagePath,
                progress,
                mainOptions,
                cancellationToken
            );
        }
        catch (PackageDeploymentException pde)
        {
            logger?.LogError(
                pde,
                "Package install failed (bypass / separate dependencies) | Path={PackagePath} | Dependencies={DependencyCount} | Error={Error} | HRESULT=0x{HResult:X8} | Extended=0x{ExtendedHResult:X8}",
                packagePath,
                dependencyUris.Count,
                pde.Message,
                pde.OuterHResult,
                pde.ExtendedHResult ?? 0
            );
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Package install failed (bypass / separate dependencies) | Path={PackagePath} | Dependencies={DependencyCount}",
                packagePath,
                dependencyUris.Count
            );
            throw;
        }
    }
}
