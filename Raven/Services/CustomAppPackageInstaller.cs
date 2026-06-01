using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Raven.Contracts.Services;
using Raven.Helpers;
using Windows.Management.Deployment;

namespace Raven.Services;

public enum CustomInstallError
{
    FolderExists,
    NoCompatibleArch,
    ManifestMissing,
    Generic,
}

public sealed class CustomInstallException : Exception
{
    public CustomInstallError Reason { get; }
    public string? FolderName { get; }

    public CustomInstallException(CustomInstallError reason, string message, string? folderName = null)
        : base(message)
    {
        Reason = reason;
        FolderName = folderName;
    }
}

/// <summary>
/// Loose-file ("developer mode") installer. Extracts a package/bundle, selects the
/// correct architecture from a bundle, moves the loose files to a user-chosen folder,
/// optionally strips the signature, and registers from the loose AppxManifest.xml.
/// The chosen folder is the app's PERMANENT home (loose registration references files
/// in place). Kept separate from <see cref="AppPackageInstaller"/> (the signed path).
/// </summary>
public static class CustomAppPackageInstaller
{
    private static readonly string[] BundleExtensions = [".appxbundle", ".msixbundle"];

    public static async Task InstallLooseAsync(
        string packagePath,
        string targetParentFolder,
        bool removeSignature,
        IEnumerable<string>? dependencyPackagePaths,
        IProgress<AppPackageInstaller.InstallProgress>? progress,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            throw new FileNotFoundException("Package file not found.", packagePath);
        if (string.IsNullOrWhiteSpace(targetParentFolder) || !Directory.Exists(targetParentFolder))
            throw new DirectoryNotFoundException($"Install folder not found: {targetParentFolder}");

        var ext = Path.GetExtension(packagePath).ToLowerInvariant();
        var isBundle = BundleExtensions.Contains(ext);

        var workRoot = Path.Combine(
            Path.GetTempPath(), "Raven", "custom-install", Guid.NewGuid().ToString("N"));
        var outerDir = Path.Combine(workRoot, "outer");
        Directory.CreateDirectory(outerDir);

        try
        {
            progress?.Report(new AppPackageInstaller.InstallProgress(0, "Extracting", "Install"));
            ZipFile.ExtractToDirectory(packagePath, outerDir);
            progress?.Report(new AppPackageInstaller.InstallProgress(30, "Extracting", "Install"));

            string looseDir;
            if (isBundle)
            {
                var bundleManifestPath = Path.Combine(outerDir, "AppxMetadata", "AppxBundleManifest.xml");
                if (!File.Exists(bundleManifestPath))
                    throw new CustomInstallException(
                        CustomInstallError.ManifestMissing, "AppxBundleManifest.xml not found.");

                var packages = LoosePackageInspector.ParseBundleApplicationPackages(
                    await File.ReadAllTextAsync(bundleManifestPath, cancellationToken));
                var archRid = App.GetService<IArchitectureSelectorService>().SelectedArchRid;
                var selected = LoosePackageInspector.SelectApplicationPackage(packages, archRid)
                    ?? throw new CustomInstallException(
                        CustomInstallError.NoCompatibleArch, "No compatible architecture in bundle.");

                logger?.LogInformation(
                    "Custom install: selected bundle package {File} (arch {Arch}) for {Rid}",
                    selected.FileName, selected.Architecture, archRid);

                var innerPkgPath = Path.Combine(outerDir, selected.FileName);
                if (!File.Exists(innerPkgPath))
                    throw new CustomInstallException(
                        CustomInstallError.Generic,
                        $"Inner package '{selected.FileName}' was listed in the bundle manifest but is not present in the archive.");

                var innerDir = Path.Combine(workRoot, "inner");
                Directory.CreateDirectory(innerDir);
                ZipFile.ExtractToDirectory(innerPkgPath, innerDir);
                looseDir = innerDir;
            }
            else
            {
                looseDir = outerDir;
            }

            progress?.Report(new AppPackageInstaller.InstallProgress(45, "Preparing", "Install"));

            var appManifestPath = Path.Combine(looseDir, "AppxManifest.xml");
            if (!File.Exists(appManifestPath))
                throw new CustomInstallException(
                    CustomInstallError.ManifestMissing, "AppxManifest.xml not found in package.");

            var appName = LoosePackageInspector.ExtractAppName(
                await File.ReadAllTextAsync(appManifestPath, cancellationToken));
            var folderName = LoosePackageInspector.SanitizeFolderName(appName);
            var target = Path.Combine(targetParentFolder, folderName);

            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new CustomInstallException(
                    CustomInstallError.FolderExists, $"Target folder already exists: {target}", folderName);

            // The guard above guarantees target is absent or empty; remove an empty
            // leftover so Directory.Move can create it cleanly (same-volume path).
            if (Directory.Exists(target))
                Directory.Delete(target);
            MoveDirectory(looseDir, target);
            progress?.Report(new AppPackageInstaller.InstallProgress(65, "Preparing", "Install"));

            if (removeSignature)
            {
                try
                {
                    var sig = Path.Combine(target, "AppxSignature.p7x");
                    if (File.Exists(sig))
                        File.Delete(sig);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Custom install: signature removal failed (ignored)");
                }
            }

            var dependencyUris = (dependencyPackagePaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Select(p => new Uri(Path.GetFullPath(p)))
                .ToList();

            var packageManager = new PackageManager();

            if (dependencyUris.Count > 0)
                progress?.Report(new AppPackageInstaller.InstallProgress(66, "Dependencies", "Install"));

            for (var i = 0; i < dependencyUris.Count; i++)
            {
                var depUri = dependencyUris[i];

                if (IsDependencyAlreadyInstalled(packageManager, depUri.LocalPath, logger))
                    continue;

                // Offer the other selected packages as available dependencies so a framework that
                // depends on another selected framework resolves regardless of pick order.
                var siblingDeps = dependencyUris.Where((_, idx) => idx != i).ToList();
                try
                {
                    var depResult = await packageManager
                        .AddPackageAsync(depUri, siblingDeps, DeploymentOptions.ForceApplicationShutdown)
                        .AsTask(cancellationToken);
                    if (depResult.ErrorText is { Length: > 0 })
                        logger?.LogWarning(
                            "Custom install: dependency add reported an error for {Dep}: {Error}",
                            depUri.LocalPath, depResult.ErrorText);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(
                        ex, "Custom install: dependency add failed or skipped for {Dep}", depUri.LocalPath);
                }
            }

            var manifestUri = new Uri(Path.Combine(target, "AppxManifest.xml"));
            var op = packageManager.RegisterPackageAsync(
                manifestUri,
                Array.Empty<Uri>(),
                DeploymentOptions.DevelopmentMode | DeploymentOptions.ForceApplicationShutdown);

            op.Progress = (_, p) =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                var pct = 70 + (int)Math.Clamp(p.percentage * 0.30, 0, 30);
                progress?.Report(new AppPackageInstaller.InstallProgress(pct, p.state.ToString(), "Install"));
            };

            try
            {
                var result = await op.AsTask(cancellationToken);
                if (result.ErrorText is { Length: > 0 })
                    throw new InvalidOperationException(result.ErrorText);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The raw COMException only carries an HRESULT; the real diagnostic
                // (missing dependency, signature, disk error) lives in the DeploymentResult.
                string? deploymentErrorText = null;
                string? extendedErrorCode = null;
                try
                {
                    var failure = op.GetResults();
                    if (failure?.ErrorText is { Length: > 0 })
                        deploymentErrorText = failure.ErrorText;
                    if (failure?.ExtendedErrorCode != null)
                        extendedErrorCode = $"0x{failure.ExtendedErrorCode.HResult:X8}";
                }
                catch
                {
                    // Best-effort; fall through with the original exception.
                }

                if (!string.IsNullOrWhiteSpace(deploymentErrorText))
                {
                    var message = $"Package registration failed (HRESULT 0x{ex.HResult:X8}";
                    if (extendedErrorCode != null)
                        message += $", Extended: {extendedErrorCode}";
                    message += $"): {deploymentErrorText}";
                    throw new InvalidOperationException(message, ex);
                }

                throw;
            }

            progress?.Report(new AppPackageInstaller.InstallProgress(100, "Completed", "Install"));
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Custom install failed | Path={Path} | Folder={Folder} | RemoveSig={RemoveSig}",
                packagePath, targetParentFolder, removeSignature);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workRoot))
                    Directory.Delete(workRoot, recursive: true);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Custom install: temp cleanup failed (ignored)");
            }
        }
    }

    private static bool IsDependencyAlreadyInstalled(
        PackageManager packageManager, string depPackagePath, ILogger? logger)
    {
        try
        {
            string name, publisher, archStr, versionStr;
            using (var zip = ZipFile.OpenRead(depPackagePath))
            {
                var entry = zip.GetEntry("AppxManifest.xml");
                if (entry is null)
                    return false;
                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                var identity = doc.Descendants().FirstOrDefault(el => el.Name.LocalName == "Identity");
                if (identity is null)
                    return false;
                name = (string?)identity.Attribute("Name") ?? string.Empty;
                publisher = (string?)identity.Attribute("Publisher") ?? string.Empty;
                versionStr = (string?)identity.Attribute("Version") ?? "0.0.0.0";
                archStr = (string?)identity.Attribute("ProcessorArchitecture") ?? string.Empty;
            }

            if (name.Length == 0 || publisher.Length == 0)
                return false;
            if (!Version.TryParse(versionStr, out var requiredVersion))
                return false;

            foreach (var pkg in packageManager.FindPackagesForUser(string.Empty, name, publisher))
            {
                // Architecture must match (a neutral dependency matches anything).
                if (archStr.Length > 0
                    && !archStr.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                    && !pkg.Id.Architecture.ToString().Equals(archStr, StringComparison.OrdinalIgnoreCase))
                    continue;

                var v = pkg.Id.Version;
                var installed = new Version(v.Major, v.Minor, v.Build, v.Revision);
                if (installed >= requiredVersion)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Fail open: if we can't determine installed state, attempt the install.
            logger?.LogDebug(ex, "Custom install: dependency-installed check failed for {Dep}", depPackagePath);
            return false;
        }
    }

    private static void MoveDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
        }
        catch (IOException)
        {
            // Cross-volume move is not supported by Directory.Move; copy then delete.
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var destFile = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, overwrite: true);
            }
            Directory.Delete(source, recursive: true);
        }
    }
}
