using System.Diagnostics;
using Microsoft.Win32;

namespace Raven.Helpers;

public static class PortableLaunchRegistry
{
    private const string BaseKey = @"Software\Raven\PortableApps";

    public static void Save(string productId, string executablePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(executablePath))
            return;

        using var key = Registry.CurrentUser.CreateSubKey($@"{BaseKey}\{SanitizeKey(productId)}");
        key?.SetValue("ExecutablePath", Path.GetFullPath(executablePath), RegistryValueKind.String);
        key?.SetValue("RootPath", Path.GetFullPath(rootPath), RegistryValueKind.String);
    }

    public static bool Exists(string productId)
    {
        return TryGetExecutable(productId, out _);
    }

    public static bool TryLaunch(string productId)
    {
        if (!TryGetExecutable(productId, out var executable) || executable == null)
            return false;

        var workingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
        });

        return process != null;
    }

    public static bool TryGetExecutable(string productId, out string? executablePath)
    {
        executablePath = null;
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        using var key = Registry.CurrentUser.OpenSubKey($@"{BaseKey}\{SanitizeKey(productId)}");
        var stored = key?.GetValue("ExecutablePath") as string;
        if (string.IsNullOrWhiteSpace(stored))
            return false;

        try
        {
            stored = Path.GetFullPath(stored);
        }
        catch
        {
            return false;
        }

        if (!File.Exists(stored))
            return false;

        executablePath = stored;
        return true;
    }

    private static string SanitizeKey(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');
        return value.Replace('\\', '_').Replace('/', '_');
    }
}
