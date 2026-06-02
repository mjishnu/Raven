using System.Diagnostics;
using System.Security.Cryptography;

Log.Initialize();
Log.Write($"Updater started. Args: {string.Join(' ', args)}");

if (!TryParseArguments(args, out var options))
{
    Log.Write("Failed to parse arguments. Aborting.");
    return 1;
}

try
{
    var updater = new Updater(options);
    updater.Run();
    Log.Write("Update completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Log.Write($"Update FAILED: {ex}");
    return 1;
}

static bool TryParseArguments(string[] args, out UpdateOptions options)
{
    options = new UpdateOptions(0, string.Empty, string.Empty, string.Empty, string.Empty);

    string? pidArg = null;
    string? sourceDir = null;
    string? targetDir = null;
    string? executablePath = null;
    string? workspaceDir = null;

    for (var i = 0; i < args.Length - 1; i += 2)
    {
        switch (args[i])
        {
            case "--pid":
                pidArg = args[i + 1];
                break;
            case "--source":
                sourceDir = args[i + 1];
                break;
            case "--target":
                targetDir = args[i + 1];
                break;
            case "--exe":
                executablePath = args[i + 1];
                break;
            case "--workspace":
                workspaceDir = args[i + 1];
                break;
        }
    }

    if (!int.TryParse(pidArg, out var pid))
        return false;

    if (string.IsNullOrWhiteSpace(sourceDir)
        || string.IsNullOrWhiteSpace(targetDir)
        || string.IsNullOrWhiteSpace(executablePath)
        || string.IsNullOrWhiteSpace(workspaceDir))
    {
        return false;
    }

    options = new UpdateOptions(pid, sourceDir, targetDir, executablePath, workspaceDir);
    return true;
}

internal sealed record UpdateOptions(
    int ProcessId,
    string SourceDirectory,
    string TargetDirectory,
    string ExecutablePath,
    string WorkspaceDirectory
);

/// <summary>
/// Minimal append-only file logger so update failures are diagnosable. Writes to
/// %LOCALAPPDATA%\Raven\logs\updater.log (falls back to the temp folder). Never throws.
/// </summary>
internal static class Log
{
    private static string? _logPath;

    public static void Initialize()
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Raven",
                "logs"
            );
            Directory.CreateDirectory(baseDir);
            _logPath = Path.Combine(baseDir, "updater.log");
        }
        catch
        {
            try
            {
                _logPath = Path.Combine(Path.GetTempPath(), "raven-updater.log");
            }
            catch
            {
                _logPath = null;
            }
        }
    }

    public static void Write(string message)
    {
        if (_logPath is null)
            return;

        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

internal sealed class Updater
{
    private readonly UpdateOptions _options;

    public Updater(UpdateOptions options)
    {
        _options = options;
    }

    public void Run()
    {
        Log.Write($"Source: {_options.SourceDirectory}");
        Log.Write($"Target: {_options.TargetDirectory}");
        Log.Write($"Exe:    {_options.ExecutablePath}");

        var exited = WaitForProcessExit(_options.ProcessId, TimeSpan.FromMinutes(2));
        Log.Write($"Waited for process {_options.ProcessId} to exit: {(exited ? "exited" : "still running / timed out")}");

        var sourceFiles = Directory
            .GetFiles(_options.SourceDirectory, "*", SearchOption.AllDirectories)
            .ToList();
        Log.Write($"Source file count: {sourceFiles.Count}");

        EnsureSourceIntegrity(sourceFiles);

        var backupRoot = Path.Combine(_options.WorkspaceDirectory, "backup");
        var addedFiles = new List<string>();
        var backedUpFiles = new List<(string target, string backup)>();
        var copied = 0;

        try
        {
            foreach (var sourceFile in sourceFiles)
            {
                var relativePath = Path.GetRelativePath(_options.SourceDirectory, sourceFile);
                var targetFile = Path.Combine(_options.TargetDirectory, relativePath);

                var targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                if (File.Exists(targetFile))
                {
                    var backupFile = Path.Combine(backupRoot, relativePath);
                    var backupDir = Path.GetDirectoryName(backupFile);
                    if (!string.IsNullOrWhiteSpace(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    File.Copy(targetFile, backupFile, overwrite: true);
                    backedUpFiles.Add((targetFile, backupFile));
                }
                else
                {
                    addedFiles.Add(targetFile);
                }

                CopyWithRetry(sourceFile, targetFile, retries: 5);
                ValidateCopiedFile(sourceFile, targetFile);
                copied++;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Copy failed after {copied}/{sourceFiles.Count} files: {ex.Message}. Rolling back.");
            RollBack(addedFiles, backedUpFiles);
            throw;
        }
        finally
        {
            TryDeleteDirectory(backupRoot);
        }

        Log.Write($"Copied {copied} files. Relaunching {_options.ExecutablePath}");

        Process.Start(
            new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = _options.TargetDirectory,
            }
        );

        TryDeleteDirectory(_options.WorkspaceDirectory);
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Process already exited (not found).
            return true;
        }
    }

    private static void EnsureSourceIntegrity(IReadOnlyCollection<string> sourceFiles)
    {
        if (sourceFiles.Count == 0)
            throw new InvalidOperationException("Update package is empty.");
    }

    private static void CopyWithRetry(string sourceFile, string targetFile, int retries)
    {
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                File.Copy(sourceFile, targetFile, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < retries)
            {
                Log.Write($"Copy attempt {attempt} for '{Path.GetFileName(targetFile)}' failed: {ex.Message}. Retrying.");
                Thread.Sleep(300 * attempt);
            }
        }

        File.Copy(sourceFile, targetFile, overwrite: true);
    }

    private static void ValidateCopiedFile(string sourceFile, string targetFile)
    {
        using var sourceStream = File.OpenRead(sourceFile);
        using var targetStream = File.OpenRead(targetFile);

        var sourceHash = SHA256.HashData(sourceStream);
        var targetHash = SHA256.HashData(targetStream);

        if (!sourceHash.AsSpan().SequenceEqual(targetHash))
            throw new IOException($"Integrity check failed for {targetFile}");
    }

    private static void RollBack(
        IEnumerable<string> addedFiles,
        IEnumerable<(string target, string backup)> backedUpFiles
    )
    {
        foreach (var file in addedFiles)
        {
            TryDeleteFile(file);
        }

        foreach (var (target, backup) in backedUpFiles)
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(backup, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
