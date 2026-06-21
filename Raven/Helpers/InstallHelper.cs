using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.RegularExpressions;

namespace Raven.Helpers;

public static class InstallHelper
{
    private const int ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN = unchecked((int)0x80073D28);
    private const int ERROR_INSTALL_CONFLICTING_PACKAGE = unchecked((int)0x80073D06);

    public static string GetFriendlyMsixError(int hresult, string message)
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
                "Install_Error_DeploymentFailure".GetLocalized(),
            ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN =>
                "Install_Error_AdminRequired".GetLocalized(),
            _ => "Install_Error_GenericDeployment".GetLocalizedFormat(hresult, message),
        };
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

    public static bool IsPackagedServiceAdminRequired(int hresult) =>
        hresult == ERROR_PACKAGED_SERVICE_REQUIRES_ADMIN;

    public static bool IsNewerOrSameVersionInstalled(int hresult) =>
        hresult == ERROR_INSTALL_CONFLICTING_PACKAGE;

    public static async Task ShowInstallationErrorDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        if (
            exception is COMException cex
            && IsPackagedServiceAdminRequired(cex.HResult)
            && !IsRunningAsAdministrator()
        )
        {
            await ShowAdminRequiredDialogAsync(xamlRoot, title, cex);
            return;
        }

        var content = exception switch
        {
            COMException => GetFriendlyErrorMessage(exception),
            InvalidOperationException { InnerException: COMException } => GetFriendlyErrorMessage(exception),
            UnauthorizedAccessException ua =>
                "Install_Error_AccessDenied".GetLocalizedFormat(ua.Message),
            _ => "Install_Error_Generic".GetLocalizedFormat(exception.Message),
        };

        await ShowDialogAsync(xamlRoot, title, content);
    }

    public static async Task<bool> ShowInstallationErrorOrForceInstallDialogAsync(
        XamlRoot xamlRoot,
        string title,
        Exception exception
    )
    {
        bool isForceInstallable = false;
        int hresult = 0;
        string message = string.Empty;

        if (exception is COMException comEx && IsNewerOrSameVersionInstalled(comEx.HResult))
        {
            isForceInstallable = true;
            hresult = comEx.HResult;
            message = comEx.Message;
        }
        else if (exception is InvalidOperationException { InnerException: COMException inner } && IsNewerOrSameVersionInstalled(inner.HResult))
        {
            isForceInstallable = true;
            hresult = inner.HResult;
            message = exception.Message;
        }

        if (isForceInstallable)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = GetFriendlyMsixError(hresult, message),
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

    public static string GetFriendlyErrorMessage(Exception exception)
    {
        if (exception is COMException comEx)
            return GetFriendlyMsixError(comEx.HResult, comEx.Message);
        
        if (exception is InvalidOperationException { InnerException: COMException inner })
            return GetFriendlyMsixError(inner.HResult, exception.Message);

        return exception.Message;
    }

    public static List<string> ParseBlockingProcesses(Exception exception)
    {
        var processes = new List<string>();
        var message = exception.Message;
        
        if (string.IsNullOrWhiteSpace(message))
            return processes;

        // Match patterns like "The following processes need to be closed: spotify.exe (PID: 1234)"
        // or just extracted process names. This regex looks for `.exe` followed optionally by `(PID:`
        var match = Regex.Match(message, @"processes need to be closed:?\s*(.*?)(?:\r|\n|$)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var procListStr = match.Groups[1].Value;
            var procMatches = Regex.Matches(procListStr, @"([a-zA-Z0-9_\-\.]+?\.exe)", RegexOptions.IgnoreCase);
            foreach (Match procMatch in procMatches)
            {
                if (procMatch.Success)
                    processes.Add(procMatch.Groups[1].Value);
            }
        }
        
        return processes.Distinct().ToList();
    }

    public static async Task<bool> ShowUpdateFailedRetryDialogAsync(
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
            if (exception is COMException comEx && comEx.HResult == unchecked((int)0x80073D02))
                isAppInUseError = true;
            else if (exception is InvalidOperationException { InnerException: COMException inner } && inner.HResult == unchecked((int)0x80073D02))
                isAppInUseError = true;
        }

        if (isAppInUseError)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Install_Error_AppInUse".GetLocalized(),
                TextWrapping = TextWrapping.Wrap
            });

            if (blockingProcs.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Install_Error_AppInUse_Processes".GetLocalized(),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                var procList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 4, 0, 0) };
                foreach (var proc in blockingProcs)
                {
                    procList.Children.Add(new TextBlock
                    {
                        Text = $"• {proc}",
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                }
                content.Children.Add(procList);
            }
        }
        
        // Always show the actual error message, either as the main text (if not app-in-use) 
        // or as secondary text (if it is app-in-use) just in case it contains more context.
        content.Children.Add(new TextBlock
        {
            Text = GetFriendlyErrorMessage(exception),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, isAppInUseError ? 8 : 0, 0, 0),
            Foreground = isAppInUseError ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        });

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "Install_Btn_Retry".GetLocalized(),
            CloseButtonText = "Common_OK".GetLocalized(),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static async Task ShowAdminRequiredDialogAsync(
        XamlRoot xamlRoot,
        string title,
        COMException cex
    )
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = GetFriendlyMsixError(cex.HResult, cex.Message),
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
            Content = content,
            CloseButtonText = "Common_OK".GetLocalized(),
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
