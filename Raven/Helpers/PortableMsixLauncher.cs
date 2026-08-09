using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Raven.Helpers;

public sealed record PortableLaunchResult(
    string ExtractDirectory,
    string ExecutablePath,
    bool AddedToUserPath,
    string? StartMenuShortcut
);

public static class PortableMsixLauncher
{
    private static readonly string[] PackageExtensions =
    [
        ".msix",
        ".appx",
        ".msixbundle",
        ".appxbundle",
    ];

    private const uint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nint result
    );

    public static async Task<PortableLaunchResult> ExtractAndLaunchAsync(
        string mainPackagePath,
        IEnumerable<string>? dependencyPackagePaths,
        string appTitle,
        string packageKey,
        CancellationToken cancellationToken = default,
        bool addToUserPath = true,
        bool createStartMenuShortcut = true
    )
    {
        if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
            throw new FileNotFoundException("The selected package could not be found.", mainPackagePath);

        if (!IsPackageFile(mainPackagePath))
            throw new InvalidDataException("Select an .msix, .appx, .msixbundle or .appxbundle file.");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var defaultBaseFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raven",
            "PortableApps");
        Directory.CreateDirectory(defaultBaseFolder);
        var suggestedName = MakeSafeFileName(string.IsNullOrWhiteSpace(appTitle) ? "App" : appTitle);
        var suggestedFolder = Path.Combine(defaultBaseFolder, suggestedName);
        Directory.CreateDirectory(suggestedFolder);

        var installBaseFolder = NativeFilePicker.PickFolder(
            hwnd,
            "Choose installation folder",
            defaultBaseFolder,
            suggestedName);
        if (string.IsNullOrWhiteSpace(installBaseFolder))
            throw new OperationCanceledException("No installation folder was selected.");

        var root = string.Equals(
            Path.GetFullPath(installBaseFolder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(suggestedFolder).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
                ? suggestedFolder
                : GetPortableRoot(appTitle, packageKey, installBaseFolder);
        var appDir = Path.Combine(root, "App");
        var depsDir = Path.Combine(root, "Dependencies");

        if (Directory.Exists(root))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"The existing portable folder could not be replaced. Close the portable app if it is still running and try again. Folder: {root}",
                    ex
                );
            }
        }

        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(depsDir);

        cancellationToken.ThrowIfCancellationRequested();
        await ExtractPackageOrBundleAsync(mainPackagePath, appDir, cancellationToken);

        var dependencyRoots = new List<string>();
        foreach (var dep in (dependencyPackagePaths ?? [])
                     .Where(File.Exists)
                     .Where(IsPackageFile)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var depTarget = Path.Combine(depsDir, MakeSafeFileName(Path.GetFileNameWithoutExtension(dep)));
            Directory.CreateDirectory(depTarget);

            try
            {
                await ExtractPackageOrBundleAsync(dep, depTarget, cancellationToken);
                dependencyRoots.Add(depTarget);
            }
            catch
            {
            }
        }

        var executable = FindLaunchExecutable(appDir, appTitle);
        if (executable == null)
        {
            throw new InvalidOperationException(
                "The package was unpacked, but no directly runnable EXE could be identified. " +
                "The application may require normal MSIX registration or package identity. " +
                $"Extracted folder: {appDir}"
            );
        }

        var exeDir = Path.GetDirectoryName(executable) ?? appDir;
        var addedToPath = addToUserPath && AddDirectoryToUserPath(exeDir);
        string? shortcutPath = null;

        if (createStartMenuShortcut)
            shortcutPath = CreateStartMenuShortcut(appTitle, executable, exeDir);

        cancellationToken.ThrowIfCancellationRequested();
        Launch(executable, appDir, dependencyRoots);

        return new PortableLaunchResult(root, executable, addedToPath, shortcutPath);
    }

    public static string GetPortableRoot(string appTitle, string packageKey, string? baseFolder = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseFolder)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Raven",
                "PortableApps")
            : Path.GetFullPath(baseFolder);

        var name = MakeSafeFileName(string.IsNullOrWhiteSpace(appTitle) ? "App" : appTitle);
        var key = MakeSafeFileName(string.IsNullOrWhiteSpace(packageKey) ? "local" : packageKey);
        return Path.Combine(baseDir, $"{name}_{key}");
    }

    public static string GetShortcutFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        "Programs",
        "Raven Portable Apps");

    private static bool IsPackageFile(string path)
    {
        var ext = Path.GetExtension(path);
        return PackageExtensions.Any(x => ext.Equals(x, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ExtractPackageOrBundleAsync(
        string packagePath,
        string destination,
        CancellationToken cancellationToken
    )
    {
        var ext = Path.GetExtension(packagePath);
        if (ext.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractMainPackageFromBundleAsync(packagePath, destination, cancellationToken);
            return;
        }

        await ExtractZipSafelyAsync(packagePath, destination, cancellationToken);
    }

    private static async Task ExtractMainPackageFromBundleAsync(
        string bundlePath,
        string destination,
        CancellationToken cancellationToken
    )
    {
        using var archive = ZipFile.OpenRead(bundlePath);
        var manifestEntry = archive.GetEntry("AppxMetadata/AppxBundleManifest.xml")
            ?? archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry == null)
            throw new InvalidDataException("The bundle does not contain AppxBundleManifest.xml.");

        XDocument manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }

        var packages = manifest.Descendants()
            .Where(x => x.Name.LocalName.Equals("Package", StringComparison.OrdinalIgnoreCase))
            .Select(x => new
            {
                FileName = x.Attributes().FirstOrDefault(a => a.Name.LocalName == "FileName")?.Value,
                Type = x.Attributes().FirstOrDefault(a => a.Name.LocalName == "Type")?.Value,
                Architecture = x.Attributes().FirstOrDefault(a => a.Name.LocalName == "Architecture")?.Value,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.FileName))
            .ToList();

        var currentArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        static int TypeScore(string? type) =>
            string.Equals(type, "application", StringComparison.OrdinalIgnoreCase) ? 0 : 10;

        int ArchScore(string? arch)
        {
            if (string.IsNullOrWhiteSpace(arch) || arch.Equals("neutral", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (arch.Equals(currentArch, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (currentArch == "x64" && arch.Equals("x86", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 10;
        }

        var selected = packages
            .OrderBy(x => TypeScore(x.Type))
            .ThenBy(x => ArchScore(x.Architecture))
            .FirstOrDefault(x => TypeScore(x.Type) < 10 && ArchScore(x.Architecture) < 10)
            ?? packages.OrderBy(x => TypeScore(x.Type)).ThenBy(x => ArchScore(x.Architecture)).FirstOrDefault();

        if (selected?.FileName == null)
            throw new InvalidDataException("No application package was found inside the bundle.");

        var nested = archive.GetEntry(selected.FileName)
            ?? archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals(selected.FileName, StringComparison.OrdinalIgnoreCase));

        if (nested == null)
            throw new InvalidDataException($"The bundle references '{selected.FileName}', but that package is missing.");

        var tempPackage = Path.Combine(
            Path.GetTempPath(),
            $"RavenPortable_{Guid.NewGuid():N}{Path.GetExtension(selected.FileName)}"
        );

        try
        {
            await using (var source = nested.Open())
            await using (var target = new FileStream(
                tempPackage,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            await ExtractZipSafelyAsync(tempPackage, destination, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPackage))
                    File.Delete(tempPackage);
            }
            catch { }
        }
    }

    private static async Task ExtractZipSafelyAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var outputPath = Path.GetFullPath(Path.Combine(destination, relative));
            if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in package: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            await using var input = entry.Open();
            await using var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true
            );
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static string? FindLaunchExecutable(string appDir, string appTitle)
    {
        var manifestPath = Path.Combine(appDir, "AppxManifest.xml");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = XDocument.Load(manifestPath);
                foreach (var app in manifest.Descendants().Where(x => x.Name.LocalName == "Application"))
                {
                    var exe = app.Attributes().FirstOrDefault(a => a.Name.LocalName == "Executable")?.Value;
                    if (string.IsNullOrWhiteSpace(exe))
                        continue;

                    exe = Environment.ExpandEnvironmentVariables(exe)
                        .Replace('/', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar);

                    var candidate = Path.GetFullPath(Path.Combine(appDir, exe));
                    if (candidate.StartsWith(Path.GetFullPath(appDir), StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
            }
        }

        var titleToken = NormalizeToken(appTitle);
        var exes = Directory.EnumerateFiles(appDir, "*.exe", SearchOption.AllDirectories).ToList();
        if (exes.Count == 0)
            return null;

        static bool LooksSecondary(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            string[] tokens =
            [
                "unins", "uninstall", "setup", "install", "update", "updater",
                "crash", "report", "elevat", "service", "broker", "background", "helper"
            ];
            return tokens.Any(name.Contains);
        }

        return exes
            .OrderBy(file =>
            {
                var score = 0;
                var name = NormalizeToken(Path.GetFileNameWithoutExtension(file));
                if (LooksSecondary(file)) score += 1000;
                if (!string.IsNullOrWhiteSpace(titleToken) && name.Contains(titleToken, StringComparison.OrdinalIgnoreCase))
                    score -= 100;
                if (!file.Contains($"{Path.DirectorySeparatorChar}VFS{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    score -= 10;
                score += file.Count(c => c == Path.DirectorySeparatorChar);
                return score;
            })
            .ThenBy(file => file.Length)
            .FirstOrDefault();
    }

    private static bool AddDirectoryToUserPath(string directory)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var parts = userPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ).Select(p => p.TrimEnd(Path.DirectorySeparatorChar)).ToList();

        var alreadyPresent = parts.Any(p => string.Equals(p, directory, StringComparison.OrdinalIgnoreCase));
        if (!alreadyPresent)
        {
            var newUserPath = string.IsNullOrWhiteSpace(userPath)
                ? directory
                : userPath.TrimEnd(Path.PathSeparator) + Path.PathSeparator + directory;

            Environment.SetEnvironmentVariable("Path", newUserPath, EnvironmentVariableTarget.User);

            try
            {
                _ = SendMessageTimeout(
                    (nint)HwndBroadcast,
                    WmSettingChange,
                    0,
                    "Environment",
                    SmtoAbortIfHung,
                    2000,
                    out _
                );
            }
            catch { }
        }

        var processPath = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        var processParts = processPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        if (!processParts.Any(p => string.Equals(
                p.TrimEnd(Path.DirectorySeparatorChar),
                directory,
                StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable(
                "Path",
                processPath.TrimEnd(Path.PathSeparator) + Path.PathSeparator + directory,
                EnvironmentVariableTarget.Process
            );
        }

        return !alreadyPresent;
    }

    private static string CreateStartMenuShortcut(
        string appTitle,
        string executable,
        string workingDirectory
    )
    {
        var programs = GetShortcutFolder();
        Directory.CreateDirectory(programs);

        var shortcutPath = Path.Combine(programs, MakeSafeFileName(appTitle) + ".lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create Windows Script Host.");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = executable;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = executable + ",0";
            shortcut.Description = $"Portable app created by Raven: {appTitle}";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject != null && Marshal.IsComObject(shortcutObject))
                Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject != null && Marshal.IsComObject(shellObject))
                Marshal.FinalReleaseComObject(shellObject);
        }

        return shortcutPath;
    }

    private static void Launch(
        string executable,
        string appRoot,
        IEnumerable<string> dependencyRoots
    )
    {
        var exeDir = Path.GetDirectoryName(executable) ?? appRoot;
        var searchRoots = new List<string> { exeDir, appRoot };

        foreach (var depRoot in dependencyRoots)
        {
            searchRoots.Add(depRoot);
            try
            {
                searchRoots.AddRange(Directory.EnumerateDirectories(depRoot, "*", SearchOption.AllDirectories).Take(64));
            }
            catch { }
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = exeDir,
            UseShellExecute = false,
        };

        var inheritedPath = Environment.GetEnvironmentVariable("Path") ?? string.Empty;
        psi.Environment["Path"] = string.Join(
            Path.PathSeparator,
            searchRoots.Distinct(StringComparer.OrdinalIgnoreCase)
        ) + Path.PathSeparator + inheritedPath;
        psi.Environment["RAVEN_PORTABLE_ROOT"] = appRoot;

        if (Process.Start(psi) == null)
            throw new InvalidOperationException($"Windows could not start: {executable}");
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "App" : result;
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
