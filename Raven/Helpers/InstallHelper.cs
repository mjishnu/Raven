using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Raven.Helpers;

public enum RetryInstallAction
{
    Cancel,
    RetryNormal,
    RetryDeferred
}

public static partial class InstallHelper
{
    private const int ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN = unchecked((int)0x80073D28);
    private const int ERROR_INSTALL_CONFLICTING_PACKAGE = unchecked((int)0x80073D06);

    // ──────────────────────────────────────────────────────
    //  COMException extraction
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the underlying deployment <see cref="COMException"/> from an exception.
    /// The deployment engine may throw a raw COMException or
    /// <see cref="AppPackageInstaller"/> may wrap it in an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static COMException? TryGetDeploymentCOMException(Exception? exception) =>
        exception switch
        {
            COMException direct => direct,
            InvalidOperationException { InnerException: COMException inner } => inner,
            _ => null,
        };

    // ──────────────────────────────────────────────────────
    //  HRESULT classification
    // ──────────────────────────────────────────────────────

    public static bool IsPackagedServiceAdminRequired(int hresult) =>
        hresult == ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN;

    public static bool IsNewerOrSameVersionInstalled(int hresult) =>
        hresult == ERROR_INSTALL_CONFLICTING_PACKAGE;

    public static bool IsAdminRequired(Exception? exception)
    {
        var comEx = TryGetDeploymentCOMException(exception);
        return comEx != null && IsPackagedServiceAdminRequired(comEx.HResult);
    }

    public static bool IsForceInstallable(Exception? exception)
    {
        var comEx = TryGetDeploymentCOMException(exception);
        return comEx != null && IsNewerOrSameVersionInstalled(comEx.HResult);
    }

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ──────────────────────────────────────────────────────
    //  Friendly error messages
    // ──────────────────────────────────────────────────────

    public static string? GetFriendlyExtendedError(int extendedHresult)
    {
        const int ERROR_FILE_CORRUPT = unchecked((int)0x80070570);
        const int ERROR_NOT_FOUND = unchecked((int)0x80070490);
        const int ERROR_SIGNATURE_INVALID = unchecked((int)0x80080204);
        const int ERROR_NOT_SIGNED = unchecked((int)0x800B0100);
        const int ERROR_ACCESS_DENIED = unchecked((int)0x80070005);
        const int ERROR_NOT_SUPPORTED = unchecked((int)0x80070032);

        return extendedHresult switch
        {
            ERROR_FILE_CORRUPT => "Install_Error_PackageCorrupt".GetLocalized(),
            ERROR_NOT_FOUND => "Install_Error_MissingDependency".GetLocalized(),
            ERROR_SIGNATURE_INVALID => "Install_Error_SignatureInvalid".GetLocalized(),
            ERROR_NOT_SIGNED => "Install_Error_NotSigned".GetLocalized(),
            ERROR_ACCESS_DENIED => "Install_Error_AccessDenied".GetLocalizedFormat(""),
            ERROR_NOT_SUPPORTED => "Install_Error_NotSupported".GetLocalized(),
            _ => null,
        };
    }

    public static string GetFriendlyMsixError(int hresult, string message, int? extendedHresult = null)
    {
        const int ERROR_DEPLOYMENT_IN_PROGRESS = unchecked((int)0x80073D01);
        const int ERROR_INVALID_PACKAGE = unchecked((int)0x80073CF3);
        const int ERROR_PACKAGE_NOT_FOUND = unchecked((int)0x80073CFA);
        const int ERROR_DEPLOYMENT_FAILURE = unchecked((int)0x80073CF9);

        return hresult switch
        {
            ERROR_INSTALL_CONFLICTING_PACKAGE =>
                "Install_Error_ConflictingVersion".GetLocalized(),
            ERROR_DEPLOYMENT_IN_PROGRESS =>
                "Install_Error_DeploymentInProgress".GetLocalized(),
            ERROR_INVALID_PACKAGE =>
                "Install_Error_InvalidPackage".GetLocalized(),
            ERROR_PACKAGE_NOT_FOUND =>
                "Install_Error_PackageNotFound".GetLocalized(),
            ERROR_DEPLOYMENT_FAILURE =>
                "Install_Error_DeploymentFailure".GetLocalizedFormat(
                    extendedHresult.HasValue ? GetFriendlyExtendedError(extendedHresult.Value) ?? "" : ""
                ).Trim(),
            ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN =>
                "Install_Error_AdminRequired".GetLocalized(),
            _ => "Install_Error_GenericDeployment".GetLocalizedFormat(hresult, message),
        };
    }

    public static string GetFriendlyErrorMessage(Exception exception)
    {
        if (exception is Services.PackageDeploymentException pde)
            return pde.Message;

        var comEx = TryGetDeploymentCOMException(exception);
        return comEx != null ? GetFriendlyMsixError(comEx.HResult, exception.Message) : exception.Message;
    }

    // ──────────────────────────────────────────────────────
    //  Error dialog flow
    //
    //  Priority order (highest → lowest):
    //    1. Admin required  → "Run as Admin" prompt
    //    2. Version conflict → "Force Install" prompt
    //    3. Everything else  → plain error dialog
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Shows the appropriate error dialog for an install failure.
    /// Handles admin elevation, version conflicts, access denied, and generic errors.
    /// </summary>
    public static async Task ShowInstallationErrorDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        // 1. Admin required → offer elevation
        if (IsAdminRequired(exception) && !IsRunningAsAdministrator())
        {
            await ShowAdminRequiredDialogAsync(xamlRoot, title, TryGetDeploymentCOMException(exception)!);
            return;
        }

        // 2. Everything else → plain error
        var content = exception switch
        {
            UnauthorizedAccessException ua =>
                "Install_Error_AccessDenied".GetLocalizedFormat(ua.Message),
            _ => GetFriendlyErrorMessage(exception),
        };

        await ShowDialogAsync(xamlRoot, title, content);
    }

    /// <summary>
    /// Shows an error dialog that offers "Force Install" when the error is a version conflict.
    /// Returns <c>true</c> if the user chose to force-install.
    /// Falls back to <see cref="ShowInstallationErrorDialogAsync"/> for all other errors.
    /// </summary>
    public static async Task<bool> ShowInstallationErrorOrForceInstallDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        if (IsForceInstallable(exception))
        {
            var comEx = TryGetDeploymentCOMException(exception)!;
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = GetFriendlyMsixError(comEx.HResult, exception.Message),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                PrimaryButtonText = "Install_Btn_ForceInstall".GetLocalized(),
                CloseButtonText = "Common_OK".GetLocalized(),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        await ShowInstallationErrorDialogAsync(xamlRoot, title, exception);
        return false;
    }

    // ──────────────────────────────────────────────────────
    //  Retry / "app in use" dialog
    // ──────────────────────────────────────────────────────

    public static List<string> ParseBlockingProcesses(Exception exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
            return [];

        var results = new List<string>();


        // Extract package full names from the error message. Windows lists blocking
        // packages by their full name (Name_Version_Arch__PublisherId) in 0x80073D02
        // errors.
        var pkgMatches = PackageFullNameRegex().Matches(message);

        foreach (Match m in pkgMatches)
        {
            var fullName = m.Groups[1].Value;
            var nameEnd = fullName.IndexOf('_');
            var displayName = nameEnd > 0 ? fullName[..nameEnd] : fullName;
            results.Add(displayName);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<RetryInstallAction> ShowUpdateFailedRetryDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        var content = new StackPanel { Spacing = 8 };
        var blockingProcs = ParseBlockingProcesses(exception);
        bool isAppInUseError = blockingProcs.Count > 0;
        if (!isAppInUseError)
        {
            var comEx = TryGetDeploymentCOMException(exception);
            isAppInUseError = comEx != null && comEx.HResult == unchecked((int)0x80073D02);
        }

        if (isAppInUseError)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Install_Error_AppInUse".GetLocalized(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });

            if (blockingProcs.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Install_Error_AppInUse_Processes".GetLocalized(),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                    IsTextSelectionEnabled = true
                });

                var procList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4, 0, 0) };
                foreach (var proc in blockingProcs)
                {
                    procList.Children.Add(new TextBlock
                    {
                        Text = $"• {proc}",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        IsTextSelectionEnabled = true
                    });
                }
                content.Children.Add(procList);
            }

            content.Children.Add(new TextBlock
            {
                Text = "Install_Error_DeferredInstallDescription".GetLocalized(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = true
            });
        }
        else
        {
            // Always show the actual error message, as the main text (if not app-in-use)
            content.Children.Add(new TextBlock
            {
                Text = GetFriendlyErrorMessage(exception),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Common_Cancel".GetLocalized(),
            XamlRoot = xamlRoot,
        };

        if (isAppInUseError)
        {
            dialog.PrimaryButtonText = "Install_Btn_DeferredInstall".GetLocalized();
            dialog.DefaultButton = ContentDialogButton.Primary;
        }
        else
        {
            dialog.PrimaryButtonText = "Install_Btn_Retry".GetLocalized();
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            return isAppInUseError ? RetryInstallAction.RetryDeferred : RetryInstallAction.RetryNormal;
        }

        return RetryInstallAction.Cancel;
    }

    // ──────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────

    private static async Task ShowAdminRequiredDialogAsync(
        XamlRoot xamlRoot,
        string title,
        COMException cex
    )
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = GetFriendlyMsixError(cex.HResult, cex.Message),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            PrimaryButtonText = "Install_Btn_RunAsAdmin".GetLocalized(),
            CloseButtonText = "Common_OK".GetLocalized(),
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (
                ElevationHelper.TryRelaunchAsAdministrator(
                    Environment.GetCommandLineArgs().Skip(1).ToArray()
                )
            )
            {
                // Exit current instance; the elevated instance will continue.
                Environment.Exit(0);
            }
        }
    }

    public static async Task ShowDialogAsync(XamlRoot xamlRoot, string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = "Common_OK".GetLocalized(),
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }


    [GeneratedRegex(@"([A-Za-z0-9\.\-]+_[\d\.]+_[a-z0-9]+__[a-z0-9]{13})", RegexOptions.IgnoreCase)]
    private static partial Regex PackageFullNameRegex();
}
