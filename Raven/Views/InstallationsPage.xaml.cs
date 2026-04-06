using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Raven.Helpers;
using Raven.Services;
using Windows.Storage;
using WinRT.Interop;

namespace Raven.Views;

public sealed partial class InstallationsPage : Page
{
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private readonly ILogger _installLogger;
    public UIUpdateService UpdateService { get; }

    public InstallationsPage()
        : this(App.GetService<ILoggerFactory>())
    {
    }

    public InstallationsPage(ILoggerFactory loggerFactory)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        UpdateService = new UIUpdateService(this.DispatcherQueue);
        _installLogger = loggerFactory.CreateLogger("Raven.Install");
        UpdateService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(UIUpdateService.StatusText))
            {
                ProgressStatusText.Text = UpdateService.StatusText;
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutForViewport(ActualHeight);
        UpdateDropZoneTypography(ActualWidth);
        // Reset to default content on load
        InstallButton.Content = "InstallationsPage_InstallLabel".GetLocalized();
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
        ProgressPercentText.Text = string.Empty;
        ProgressStatusText.Text = string.Empty;
        UpdateService.StopStatusAnimation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutForViewport(e.NewSize.Height);
        UpdateDropZoneTypography(e.NewSize.Width);
    }

    private void UpdateLayoutForViewport(double viewportHeight)
    {
        var headerAllowance = 200; // approximate title + progress + path row
        var desired = Math.Max(300, (viewportHeight - headerAllowance) * 2.0 / 3.0);
        DropZoneButton.MinHeight = desired;
    }

    private void UpdateDropZoneTypography(double width)
    {
        // Simple responsive sizing based on width tiers
        double iconSize;
        double textSize;
        if (width < 500)
        {
            iconSize = 28;
            textSize = 16;
        }
        else if (width < 900)
        {
            iconSize = 36;
            textSize = 18;
        }
        else
        {
            iconSize = 44;
            textSize = 20;
        }

        DropZoneIcon.FontSize = iconSize;
        DropZoneText.FontSize = textSize;
    }

    private void SetSelectedFile(string? path)
    {
        SelectedFileText.Text = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        InstallButton.Content = "InstallationsPage_InstallLabel".GetLocalized();
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
        ClearButton.Visibility = string.IsNullOrWhiteSpace(SelectedFileText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectedFile(null);
        ProgressPanel.Visibility = Visibility.Collapsed;
        InstallProgressBar.Value = 0;
        ProgressPercentText.Text = string.Empty;
        ProgressStatusText.Text = string.Empty;
        UpdateService.StopStatusAnimation();
    }

    private void DropZoneButton_Click(object sender, RoutedEventArgs e)
    {
        const int bufferChars = 4096;

        // Filter must be pairs: display\0pattern\0 ... \0\0
        var filter =
            $"{"InstallationsPage_Filter_AppPackages".GetLocalized()}\0*.msix;*.appx;*.msixbundle;*.appxbundle\0{"InstallationsPage_Filter_AllFiles".GetLocalized()}\0*.*\0\0";

        IntPtr filterPtr = IntPtr.Zero;
        IntPtr filePtr = IntPtr.Zero;
        IntPtr titlePtr = IntPtr.Zero;

        try
        {
            filterPtr = Marshal.StringToHGlobalUni(filter);
            titlePtr = Marshal.StringToHGlobalUni("InstallationsPage_FilePicker_Title".GetLocalized());

            // Allocate buffer for selected file path
            filePtr = Marshal.AllocHGlobal(bufferChars * sizeof(char));
            for (var i = 0; i < bufferChars; i++)
                Marshal.WriteInt16(filePtr, i * sizeof(char), 0);

            const int OFN_EXPLORER = 0x00080000;
            const int OFN_FILEMUSTEXIST = 0x00001000;
            const int OFN_PATHMUSTEXIST = 0x00000800;
            const int OFN_NOCHANGEDIR = 0x00000008;

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = WindowNative.GetWindowHandle(App.MainWindow),
                lpstrFilter = filterPtr,
                lpstrFile = filePtr,
                nMaxFile = bufferChars,
                lpstrTitle = titlePtr,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (GetOpenFileName(ref ofn))
            {
                var selected = Marshal.PtrToStringUni(ofn.lpstrFile);
                if (!string.IsNullOrWhiteSpace(selected))
                    SetSelectedFile(selected);

                return;
            }

            var err = CommDlgExtendedError();
            if (err != 0)
            {
                _ = InstallHelper.ShowDialogAsync(
                    this.Content.XamlRoot,
                    "InstallationsPage_FilePicker_Error".GetLocalized(),
                    string.Format("InstallationsPage_FilePicker_CommDlgError".GetLocalized(), err)
                );
            }
        }
        catch (Exception ex)
        {
            _ = InstallHelper.ShowDialogAsync(
                this.Content.XamlRoot,
                "InstallationsPage_FilePicker_Error".GetLocalized(),
                ex.Message
            );
        }
        finally
        {
            if (filterPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(filterPtr);
            if (titlePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(titlePtr);
            if (filePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(filePtr);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "InstallationsPage_DragCaption".GetLocalized();
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (
            e.DataView.Contains(
                Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems
            )
        )
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file != null)
            {
                var ext = Path.GetExtension(file.Path).ToLowerInvariant();
                if (ext is ".appx" or ".msix" or ".appxbundle" or ".msixbundle")
                {
                    SetSelectedFile(file.Path);
                }
            }
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedFileText.Text;
        if (string.IsNullOrWhiteSpace(path))
            return;

        await PerformInstallAsync(path, false);
    }

    private async Task PerformInstallAsync(string path, bool ignoreVersion)
    {
        InstallButton.IsEnabled = false;
        DropZoneButton.IsEnabled = false; // disable drag box during install
        ProgressPanel.Visibility = Visibility.Visible;
        InstallProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";
        UpdateService.StartStatusAnimation("Install_Status_Installing".GetLocalized());
        var shouldForceRetry = false;

        _installLogger.LogInformation(
            "Install start | Path={Path} | IgnoreVersion={IgnoreVersion}",
            path,
            ignoreVersion
        );

        var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
        {
            var percent = Math.Clamp(p.Percent, 0, 100);
            InstallProgressBar.Value = percent;
            ProgressPercentText.Text = $"{percent}%";
            // Leave status animation to UpdateService
        });

        var succeeded = false;
        Exception? installException = null;
        try
        {
            Debug.WriteLine($"Starting installation with ignoreVersion={ignoreVersion}");
            await AppPackageInstaller.InstallAsync(
                path,
                dependencyPackagePaths: null,
                progress,
                ignoreVersion: ignoreVersion,
                logger: _installLogger
            );
            UpdateService.StopStatusAnimation();
            ProgressStatusText.Text = "Install_Status_Installed".GetLocalized();
            ProgressPercentText.Text = "100%";
            succeeded = true;
            _installLogger.LogInformation(
                "Install success | Path={Path} | IgnoreVersion={IgnoreVersion}",
                path,
                ignoreVersion
            );
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            UpdateService.StopStatusAnimation();
            installException = ex;
            ProgressStatusText.Text = "Install_Status_Error".GetLocalized();
            _installLogger.LogError(
                ex,
                "Install failed | Path={Path} | IgnoreVersion={IgnoreVersion} | Failure=PermissionOrCOM",
                path,
                ignoreVersion
            );
        }
        catch (Exception ex)
        {
            UpdateService.StopStatusAnimation();
            installException = ex;
            ProgressStatusText.Text = "Install_Status_Error".GetLocalized();
            _installLogger.LogError(
                ex,
                "Install failed | Path={Path} | IgnoreVersion={IgnoreVersion}",
                path,
                ignoreVersion
            );
        }
        finally
        {
            // Hide progress
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallProgressBar.Value = 0;
            ProgressPercentText.Text = string.Empty;
            DropZoneButton.IsEnabled = true; // re-enable drag box

            if (succeeded)
            {
                // Show tick and keep disabled until next selection
                InstallButton.Content = new SymbolIcon { Symbol = Symbol.Accept };
                InstallButton.IsEnabled = false;
                // Clear text after success
                SelectedFileText.Text = string.Empty;
                ClearButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Show cross icon for failure until next selection
                InstallButton.Content = new SymbolIcon { Symbol = Symbol.Cancel };
                InstallButton.IsEnabled = false;
                // Clear text after failure too
                SelectedFileText.Text = string.Empty;
                ClearButton.Visibility = Visibility.Collapsed;

                if (installException != null)
                {
                    if (
                        !ignoreVersion
                        && installException is COMException comEx
                        && InstallHelper.IsNewerOrSameVersionInstalled(comEx.HResult)
                    )
                    {
                        shouldForceRetry =
                            await InstallHelper.ShowInstallationErrorOrForceInstallDialogAsync(
                                this.Content.XamlRoot,
                                "Install_Dialog_Title".GetLocalized(),
                                installException
                            );
                    }
                    else
                    {
                        await InstallHelper.ShowInstallationErrorDialogAsync(
                            this.Content.XamlRoot,
                            "Install_Dialog_Title".GetLocalized(),
                            installException
                        );
                    }
                }
            }
        }

        if (shouldForceRetry)
        {
            // Retry with ignore-version.
            ProgressStatusText.Text = string.Empty;
            ProgressPercentText.Text = "0%";
            InstallProgressBar.Value = 0;
            await PerformInstallAsync(path, ignoreVersion: true);
        }
    }
}
