using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Raven.Services;

/// <summary>
/// Checks whether UWP/MSIX sideloading is enabled on the current device.
///
/// Detection logic:
///   1. Check Group Policy override key first (takes precedence over local setting).
///   2. Fall back to the local AppModelUnlock key.
///   3. If neither key is present, infer from OS build:
///      - Build < 19041 (pre-2004): treat as Disabled (sideloading was off by default).
///      - Build >= 19041 (2004+):   treat as Enabled (sideloading on by default, toggle removed).
/// </summary>
public static class SideloadingCheckService
{
    private const string PolicyKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Appx";
    private const string AppModelUnlockKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string ValueName = "AllowAllTrustedApps";
    private const string DeveloperModeValueName = "AllowDevelopmentWithoutDevLicense";

    // Build 19041 = Windows 10 2004, where sideloading became enabled by default.
    private const int SideloadingDefaultEnabledBuild = 19041;

    /// <summary>
    /// Returns <c>true</c> if sideloading is enabled, <c>false</c> if disabled.
    /// On any unexpected error, returns <c>true</c> to avoid blocking the user.
    /// </summary>
    public static bool IsSideloadingEnabled(ILogger? logger = null)
    {
        try
        {
            // Step 1: Check Group Policy key (highest precedence).
            var policyValue = Registry.GetValue(PolicyKey, ValueName, null);
            if (policyValue is int policyInt)
            {
                var policyEnabled = policyInt == 1;
                logger?.LogInformation(
                    "Sideloading: Group Policy key present, AllowAllTrustedApps={Value} → {State}",
                    policyInt, policyEnabled ? "Enabled" : "Disabled");
                return policyEnabled;
            }

            // Step 2: Check local AppModelUnlock key.
            var localValue = Registry.GetValue(AppModelUnlockKey, ValueName, null);
            if (localValue is int localInt)
            {
                var localEnabled = localInt == 1;
                logger?.LogInformation(
                    "Sideloading: AppModelUnlock key present, AllowAllTrustedApps={Value} → {State}",
                    localInt, localEnabled ? "Enabled" : "Disabled");
                return localEnabled;
            }

            // Step 3: Neither key present — infer from OS build.
            var osBuild = Environment.OSVersion.Version.Build;
            var defaultEnabled = osBuild >= SideloadingDefaultEnabledBuild;

            logger?.LogInformation(
                "Sideloading: No registry key found. OS Build={Build} → treating as {State}",
                osBuild, defaultEnabled ? "Enabled" : "Disabled");

            return defaultEnabled;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sideloading: Failed to read registry, assuming enabled");
            return true;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if full Developer Mode is enabled (required for loose-file
    /// RegisterPackageAsync with DevelopmentMode). Reads AllowDevelopmentWithoutDevLicense,
    /// which is distinct from AllowAllTrustedApps (sideloading).
    ///   1. Group Policy key (highest precedence).
    ///   2. Local AppModelUnlock key.
    ///   3. Neither present -> Developer Mode is OFF by default -> false.
    /// On unexpected error, returns <c>true</c> to avoid blocking the user.
    /// </summary>
    public static bool IsDeveloperModeEnabled(ILogger? logger = null)
    {
        try
        {
            var policyValue = Registry.GetValue(PolicyKey, DeveloperModeValueName, null);
            if (policyValue is int policyInt)
            {
                var enabled = policyInt == 1;
                logger?.LogInformation(
                    "DeveloperMode: Group Policy key present, {Value} → {State}",
                    DeveloperModeValueName, enabled ? "Enabled" : "Disabled");
                return enabled;
            }

            var localValue = Registry.GetValue(AppModelUnlockKey, DeveloperModeValueName, null);
            if (localValue is int localInt)
            {
                var enabled = localInt == 1;
                logger?.LogInformation(
                    "DeveloperMode: AppModelUnlock key present, {Value} → {State}",
                    DeveloperModeValueName, enabled ? "Enabled" : "Disabled");
                return enabled;
            }

            logger?.LogInformation("DeveloperMode: No registry key found → Disabled");
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "DeveloperMode: Failed to read registry, assuming enabled");
            return true;
        }
    }
}
