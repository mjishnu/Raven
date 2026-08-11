using System.IO.Compression;
using System.Runtime.InteropServices;
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
    ExecutableNotFound,
    Generic,
}

public sealed class CustomInstallException : Exception
{
    public CustomInstallError Reason
    {
        get;
    }
    public string? FolderName
    {
        get;
    }

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
/// optionally strips the signature, and either registers from the loose AppxManifest.xml
/// or leaves the package unregistered and creates a Start Menu shortcut.
/// </summary>
public static class CustomAppPackageInstaller
{
    private static readonly string[] BundleExtensions = [".appxbundle", ".msixbundle"];

    public static async Task InstallLooseAsync(
        string packagePath,
        string targetParentFolder,
        bool removeSignature,
        bool skipRegistration,
        bool createStartMenuShortcut,
        bool createDesktopShortcut,
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
            ExtractPackageToDirectory(packagePath, outerDir);
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
                    selected.FileName,
                    selected.Architecture,
                    archRid);

                var innerPkgPath = Path.Combine(outerDir, selected.FileName);
                if (!File.Exists(innerPkgPath))
                    throw new CustomInstallException(
                        CustomInstallError.Generic,
                        $"Inner package '{selected.FileName}' was listed in the bundle manifest but is not present in the archive.");

                var innerDir = Path.Combine(workRoot, "inner");
                Directory.CreateDirectory(innerDir);
                ExtractPackageToDirectory(innerPkgPath, innerDir);
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

                var siblingDeps = dependencyUris.Where((_, idx) => idx != i).ToList();
                try
                {
                    var depResult = await packageManager
                        .AddPackageAsync(depUri, siblingDeps, DeploymentOptions.ForceApplicationShutdown)
                        .AsTask(cancellationToken);
                    if (depResult.ErrorText is { Length: > 0 })
                    {
                        logger?.LogWarning(
                            "Custom install: dependency add reported an error for {Dep}: {Error}",
                            depUri.LocalPath, depResult.ErrorText);
                    }
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

            if (skipRegistration)
            {
                var manifestXml = await File.ReadAllTextAsync(
                    Path.Combine(target, "AppxManifest.xml"), cancellationToken);

                var exePath = FindExecutable(target, manifestXml);
                if (string.IsNullOrEmpty(exePath))
                {
                    logger?.LogWarning("Custom install: executable not found in {Folder}; unregistered app may fail to run.", target);
                    throw new CustomInstallException(
                        CustomInstallError.ExecutableNotFound,
                        "Unable to find executable. This app might not be able to run unregistered.");
                }

                if (createStartMenuShortcut || createDesktopShortcut)
                {
                    progress?.Report(new AppPackageInstaller.InstallProgress(85, "Creating shortcut", "Install"));
                    CreateAppShortcuts(target, appName, exePath, createStartMenuShortcut, createDesktopShortcut, logger);
                }

                logger?.LogInformation(
                    "Custom install completed without package registration | Folder={Folder} | StartMenuShortcut={StartMenu} | DesktopShortcut={Desktop}",
                    target, createStartMenuShortcut, createDesktopShortcut);
                progress?.Report(new AppPackageInstaller.InstallProgress(100, "Completed", "Install"));
                return;
            }

            var manifestUri = new Uri(Path.Combine(target, "AppxManifest.xml"));
            var op = packageManager.RegisterPackageAsync(
                manifestUri,
                [],
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
                "Custom install failed | Path={Path} | Folder={Folder} | RemoveSig={RemoveSig} | SkipRegistration={SkipRegistration}",
                packagePath, targetParentFolder, removeSignature, skipRegistration);
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

    private static string? FindExecutable(string appFolder, string manifestXml)
    {
        var exeName = ExtractExecutableFromManifest(manifestXml);
        if (string.IsNullOrEmpty(exeName))
            return null;

        var cleanExeName = exeName.TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var exePath = Path.GetFullPath(Path.Combine(appFolder, cleanExeName));
        if (File.Exists(exePath))
            return exePath;

        return null;
    }

    private static void CreateAppShortcuts(
        string appFolder,
        string appName,
        string exePath,
        bool createStartMenuShortcut,
        bool createDesktopShortcut,
        ILogger? logger)
    {
        if (!createStartMenuShortcut && !createDesktopShortcut)
            return;

        var workingDir = Path.GetDirectoryName(exePath) ?? appFolder;

        if (createStartMenuShortcut)
            CreateSingleShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs), appName, exePath, workingDir, "Start Menu", logger);

        if (createDesktopShortcut)
            CreateSingleShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), appName, exePath, workingDir, "Desktop", logger);
    }

    private static void CreateSingleShortcut(
        string targetFolder, string appName, string exePath, string workingDir, string locationName, ILogger? logger)
    {
        if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            return;

        var cleanName = string.Join("_", appName.Split(Path.GetInvalidFileNameChars())).Trim();
        var safeAppName = string.IsNullOrEmpty(cleanName) ? "App" : cleanName;

        var shortcutPath = Path.Combine(targetFolder, $"{safeAppName}.lnk");
        var counter = 1;
        while (File.Exists(shortcutPath))
        {
            shortcutPath = Path.Combine(targetFolder, $"{safeAppName} ({counter++}).lnk");
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = appName;
            shortcut.Save();

            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            logger?.LogInformation(
                "Custom install: created {Location} shortcut at {Path}", locationName, shortcutPath);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Custom install: {Location} shortcut creation failed (ignored)", locationName);
        }
    }

    private static string? ExtractExecutableFromManifest(string manifestXml)
    {
        var doc = XDocument.Parse(manifestXml);
        var app = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
        return (string?)app?.Attribute("Executable");
    }

    private static void ExtractPackageToDirectory(string zipPath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var fullDestDir = Path.GetFullPath(destinationDir);
        if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar))
            fullDestDir += Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var decodedName = Uri.UnescapeDataString(entry.FullName).Replace('/', Path.DirectorySeparatorChar);
            var destPath = Path.GetFullPath(Path.Combine(destinationDir, decodedName));

            if (!destPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
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
