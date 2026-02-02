using System.Diagnostics;
using Windows.Management.Deployment;

namespace test.Services;

public static class PackagedAppDiscovery
{
    public static bool IsInstalled(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return false;

        try
        {
            var packageManager = new PackageManager();
            return packageManager.FindPackagesForUser(string.Empty, packageFamilyName).Any();
        }
        catch
        {
            return false;
        }
    }

    public static DateTimeOffset? GetInstalledUtc(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return null;

        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            var path = pkg?.InstalledLocation?.Path;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return null;

            var creationUtc = Directory.GetCreationTimeUtc(path);
            return new DateTimeOffset(creationUtc, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> TryLaunchAsync(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return false;

        try
        {
            var pm = new PackageManager();
            var pkg = pm.FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
            if (pkg == null)
                return false;

            var entries = await pkg.GetAppListEntriesAsync();
            var entry = entries.FirstOrDefault();
            if (entry == null)
                return false;

            await entry.LaunchAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }
}
