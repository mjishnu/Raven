using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace Raven.Services;

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

    private static async Task AddPackageAsync(
        PackageManager packageManager,
        string packagePath,
        IReadOnlyCollection<Uri> dependencyPackageUris,
        IProgress<InstallProgress>? progress,
        DeploymentOptions deploymentOptions,
        CancellationToken cancellationToken
    )
    {
        var packageUri = new Uri(Path.GetFullPath(packagePath));

        var deploymentOperation = packageManager.AddPackageAsync(
            packageUri,
            dependencyPackageUris,
            deploymentOptions
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
            string? extendedErrorCode = null;

            try
            {
                var result = deploymentOperation.GetResults();
                if (result?.ErrorText is { Length: > 0 })
                    deploymentErrorText = result.ErrorText;
                if (result?.ExtendedErrorCode != null)
                    extendedErrorCode = $"0x{result.ExtendedErrorCode.HResult:X8}";
            }
            catch
            {
                // Best-effort; if we can't read the result, fall through with the original exception.
            }

            if (!string.IsNullOrWhiteSpace(deploymentErrorText))
            {
                var message = $"Package deployment failed (HRESULT 0x{ex.HResult:X8}";
                if (extendedErrorCode != null)
                    message += $", Extended: {extendedErrorCode}";
                message += $"): {deploymentErrorText}";

                throw new InvalidOperationException(message, ex);
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

        var options = DeploymentOptions.ForceApplicationShutdown;
        if (ignoreVersion)
            options |= DeploymentOptions.ForceUpdateFromAnyVersion;

        if (installDependenciesSeparately)
        {
            await InstallWithSeparateDependenciesAsync(
                packageManager,
                packagePath,
                dependencyUris,
                progress,
                options,
                cancellationToken,
                logger
            );

            progress?.Report(new InstallProgress(100, "Completed", "Install"));
            return;
        }

        try
        {
            await AddPackageAsync(
                packageManager,
                packagePath,
                dependencyUris,
                progress,
                options,
                cancellationToken
            );
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
        DeploymentOptions options,
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

            try
            {
                var depResult = await packageManager
                    .AddPackageAsync(depUri, siblings, options)
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
        try
        {
            await AddPackageAsync(
                packageManager,
                packagePath,
                Array.Empty<Uri>(),
                progress,
                options,
                cancellationToken
            );
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
