using System.Diagnostics;
using Microsoft.Win32;

namespace test.Services;

public static class Win32AppDiscovery
{
    public sealed record InstalledAppInfo(bool IsInstalled, string? InstalledVersion);

    public enum LaunchFailureReason
    {
        None = 0,
        AppNameMissing,
        NotFoundInRegistry,
        MissingLaunchTarget,
        LaunchTargetNotFoundOnDisk,
        LaunchFailed,
    }

    public sealed record Win32LaunchResult(
        bool Success,
        string? InstalledVersion,
        string? LaunchedPath,
        LaunchFailureReason FailureReason
    );

    public static InstalledAppInfo GetInstalledInfo(string? appName)
    {
        var info = TryGetRegistryInstallInfo(appName);
        return info.IsInstalled
            ? new InstalledAppInfo(true, info.DisplayVersion)
            : new InstalledAppInfo(false, null);
    }

    public static async Task<bool> TryLaunchAsync(string? appName)
    {
        var result = await TryLaunchDetailedAsync(appName);
        return result.Success;
    }

    public static async Task<Win32LaunchResult> TryLaunchDetailedAsync(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return new Win32LaunchResult(
                Success: false,
                InstalledVersion: null,
                LaunchedPath: null,
                FailureReason: LaunchFailureReason.AppNameMissing
            );
        }

        // Step 1: Fuzzy search registry (to confirm install + get version)
        var reg = TryGetRegistryInstallInfo(appName);
        if (!reg.IsInstalled)
        {
            return new Win32LaunchResult(
                Success: false,
                InstalledVersion: null,
                LaunchedPath: null,
                FailureReason: LaunchFailureReason.NotFoundInRegistry
            );
        }

        // Step 2: Prefer launching via Start Menu (shell:AppsFolder) similar to how Windows does it.
        var startMenuLaunch = TryLaunchFromAppsFolder(appName);
        if (startMenuLaunch.Success)
        {
            return new Win32LaunchResult(
                Success: true,
                InstalledVersion: reg.DisplayVersion,
                LaunchedPath: startMenuLaunch.LaunchedItemName,
                FailureReason: LaunchFailureReason.None
            );
        }

        // Step 3: Fallback to DisplayIcon for launching (if present)
        var displayIconExe = CleanExePath(reg.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(displayIconExe))
        {
            if (!File.Exists(displayIconExe))
            {
                return new Win32LaunchResult(
                    Success: false,
                    InstalledVersion: reg.DisplayVersion,
                    LaunchedPath: displayIconExe,
                    FailureReason: LaunchFailureReason.LaunchTargetNotFoundOnDisk
                );
            }

            var ok = await TryStartProcessAsync(displayIconExe);
            return ok
                ? new Win32LaunchResult(
                    true,
                    reg.DisplayVersion,
                    displayIconExe,
                    LaunchFailureReason.None
                )
                : new Win32LaunchResult(
                    false,
                    reg.DisplayVersion,
                    displayIconExe,
                    LaunchFailureReason.LaunchFailed
                );
        }

        // Could not determine launch target
        return new Win32LaunchResult(
            Success: false,
            InstalledVersion: reg.DisplayVersion,
            LaunchedPath: null,
            FailureReason: LaunchFailureReason.MissingLaunchTarget
        );
    }

    private static Task<bool> TryStartProcessAsync(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return Task.FromResult(false);

        try
        {
            if (!File.Exists(exePath))
                return Task.FromResult(false);

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Empty,
                    UseShellExecute = true,
                    WorkingDirectory =
                        Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                }
            );
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            return Task.FromResult(false);
        }
    }

    private sealed record RegistryInstallInfo(
        bool IsInstalled,
        string? DisplayVersion,
        string? DisplayIcon
    );

    private static RegistryInstallInfo TryGetRegistryInstallInfo(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return new RegistryInstallInfo(false, null, null);

        string? displayVersion = null;
        string? displayIcon = null;

        var found = TryFindInstalledWin32AppByRegistryPredicate(
            p => ContainsAppName(p, appName),
            appName,
            out _,
            out displayVersion,
            out displayIcon
        );

        return new RegistryInstallInfo(found, displayVersion, displayIcon);
    }

    private static bool TryFindInstalledWin32AppByRegistryPredicate(
        Func<RegistryKey, bool> match,
        string? appName,
        out string? exePath,
        out string? displayVersion,
        out string? displayIcon
    )
    {
        exePath = null;
        displayVersion = null;
        displayIcon = null;

        var hives = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };

        foreach (var (hive, view) in hives)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall"
                );
                if (uninstall == null)
                    continue;

                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(subName);
                    if (sub == null)
                        continue;

                    if (!match(sub))
                        continue;

                    // Don't attempt to derive an exe from InstallLocation. Launch is based on DisplayIcon
                    // (preferred) or Start Menu shortcuts.
                    exePath = null;
                    displayVersion = sub.GetValue("DisplayVersion") as string;
                    displayIcon = sub.GetValue("DisplayIcon") as string;

                    // Only treat as installed if DisplayVersion exists.
                    if (!string.IsNullOrWhiteSpace(displayVersion))
                    {
                        Debug.WriteLine(
                            $"[Win32AppDiscovery] App found in registry: '{appName}' (version='{displayVersion ?? "?"}')"
                        );
                        return true;
                    }
                }
            }
            catch
            {
                // ignore access and format issues
            }
        }

        return false;
    }

    private static bool ContainsAppName(RegistryKey key, string appName)
    {
        try
        {
            var displayName = key.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName))
                return false;

            return IsFuzzyAppNameMatch(appName, displayName);
        }
        catch
        {
            return false;
        }
    }

    private sealed record AppsFolderLaunchResult(bool Success, string? LaunchedItemName);

    private static AppsFolderLaunchResult TryLaunchFromAppsFolder(string appName)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return new AppsFolderLaunchResult(false, null);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic appsFolder = shell.NameSpace("shell:AppsFolder");
            if (appsFolder == null)
                return new AppsFolderLaunchResult(false, null);

            dynamic items = appsFolder.Items();
            if (items == null)
                return new AppsFolderLaunchResult(false, null);

            var count = (int)items.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                if (item == null)
                    continue;

                string? name = item.Name as string;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!IsFuzzyAppNameMatch(appName, name))
                    continue;

                item.InvokeVerb("Open");
                return new AppsFolderLaunchResult(true, name);
            }

            return new AppsFolderLaunchResult(false, null);
        }
        catch
        {
            return new AppsFolderLaunchResult(false, null);
        }
    }

    private static string? CleanExePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            var endQuote = s.IndexOf('"', 1);
            if (endQuote > 1)
            {
                s = s.Substring(1, endQuote - 1);
            }
        }

        var commaIdx = s.IndexOf(',');
        if (commaIdx > 0)
            s = s.Substring(0, commaIdx);

        return s.Trim().Trim('"');
    }

    private static bool IsFuzzyAppNameMatch(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            return false;

        return actual.Contains(expected, StringComparison.OrdinalIgnoreCase)
            || expected.Contains(actual, StringComparison.OrdinalIgnoreCase);
    }
}
