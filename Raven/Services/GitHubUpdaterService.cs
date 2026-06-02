using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Raven.Services;

public sealed class GitHubUpdaterService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<GitHubReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient.GetAsync(
            $"https://api.github.com/repos/mjishnu/raven/releases/latest",
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var latestVersion = TryParseVersion(tagName);

        var assets = root.GetProperty("assets").EnumerateArray();
        var releaseAsset = SelectZipAsset(assets);
        if (releaseAsset is null)
            throw new InvalidOperationException("No zip asset found in the latest release.");

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

        return new GitHubReleaseInfo(
            tagName,
            latestVersion,
            currentVersion,
            releaseAsset.Value.name,
            releaseAsset.Value.downloadUrl
        );
    }

    public async Task StartUpdateAsync(CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        await StartUpdateAsync(release, progress: null, cancellationToken);
    }

    public async Task StartUpdateAsync(
        GitHubReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default
    )
    {
        if (release.IsUpToDate)
            return;

        var process = Process.GetCurrentProcess();
        var executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Failed to locate the current executable.");

        var applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installedUpdaterExecutablePath = Path.Combine(applicationDirectory, "Raven.Updater.exe");
        if (!File.Exists(installedUpdaterExecutablePath))
            throw new FileNotFoundException("Updater executable was not found.", installedUpdaterExecutablePath);

        var updateRoot = Path.Combine(Path.GetTempPath(), "RavenUpdater", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(updateRoot, release.AssetName);
        var extractPath = Path.Combine(updateRoot, "extract");
        var runnerRoot = Path.Combine(updateRoot, "runner");

        Directory.CreateDirectory(updateRoot);
        Directory.CreateDirectory(extractPath);
        Directory.CreateDirectory(runnerRoot);

        await DownloadAssetAsync(release.DownloadUrl, zipPath, progress, cancellationToken);
        ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

        var sourceDirectory = ResolveSourceDirectory(extractPath);

        foreach (var updaterFile in Directory.EnumerateFiles(applicationDirectory, "Raven.Updater*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(runnerRoot, Path.GetFileName(updaterFile));
            File.Copy(updaterFile, destination, overwrite: true);
        }

        var updaterExecutablePath = Path.Combine(runnerRoot, "Raven.Updater.exe");
        if (!File.Exists(updaterExecutablePath))
            throw new FileNotFoundException("Temporary updater executable was not found.", updaterExecutablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExecutablePath,
            UseShellExecute = true,
            WorkingDirectory = runnerRoot,
        };

        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(process.Id.ToString());
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(sourceDirectory);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(applicationDirectory);
        startInfo.ArgumentList.Add("--exe");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(updateRoot);

        Process.Start(startInfo);
    }

    private static async Task DownloadAssetAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    )
    {
        using var response = await HttpClient.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;

        await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await inputStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (contentLength.HasValue && contentLength.Value > 0)
            {
                progress?.Report(Math.Clamp((double)totalRead / contentLength.Value, 0d, 1d));
            }
        }

        progress?.Report(1d);
    }

    private static string ResolveSourceDirectory(string extractPath)
    {
        // Some archives wrap their payload in a single top-level folder, in which case
        // the real files live one level down. But a build can also legitimately have a
        // single top-level directory (e.g. Assets) sitting next to the app files at the
        // root. Only descend when that directory is the *sole* entry (no files beside
        // it); otherwise the extract root itself is the payload.
        var entries = Directory.GetFileSystemEntries(extractPath);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];

        return extractPath;
    }

    private const string SelfContainedToken = "self-contained";

    private static (string name, string downloadUrl)? SelectZipAsset(JsonElement.ArrayEnumerator assets)
    {
        var preferredArchitectureToken = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => string.Empty,
        };

        var isSelfContained = IsSelfContainedDeployment();
        
        (string name, string downloadUrl)? archAndVariantMatch = null;
        (string name, string downloadUrl)? archMatch = null;
        (string name, string downloadUrl)? anyZip = null;

        foreach (var asset in assets)
        {
            var name = asset.GetProperty("name").GetString();
            var downloadUrl = asset.GetProperty("browser_download_url").GetString();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                continue;

            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            anyZip ??= (name, downloadUrl);

            var archMatches = string.IsNullOrEmpty(preferredArchitectureToken)
                || name.Contains(preferredArchitectureToken, StringComparison.OrdinalIgnoreCase);
            if (!archMatches)
                continue;

            archMatch ??= (name, downloadUrl);

            var assetIsSelfContained = name.Contains(SelfContainedToken, StringComparison.OrdinalIgnoreCase);
            if (assetIsSelfContained == isSelfContained)
                archAndVariantMatch ??= (name, downloadUrl);
        }

        return archAndVariantMatch ?? archMatch ?? anyZip;
    }

    /// <summary>
    /// Detects whether the running instance is a self-contained deployment.
    /// Self-contained builds ship the .NET runtime alongside the app, so
    /// <c>coreclr.dll</c> sits next to the executable; framework-dependent
    /// builds resolve it from the shared runtime and do not.
    /// </summary>
    private static bool IsSelfContainedDeployment() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    private static Version? TryParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        return Version.TryParse(trimmed, out var parsed) ? parsed : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Raven-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public sealed record GitHubReleaseInfo(
    string TagName,
    Version? LatestVersion,
    Version? CurrentVersion,
    string AssetName,
    string DownloadUrl
)
{
    public bool IsUpToDate =>
        LatestVersion is not null
        && CurrentVersion is not null
        && LatestVersion <= CurrentVersion;
}
