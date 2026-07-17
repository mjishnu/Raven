using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Raven.Helpers;
using Raven.Services;
using Raven.ViewModels;
using Windows.Storage;
using WinRT.Interop;

namespace Raven.Views;

public sealed partial class InstallationsPage : Page
{
    private readonly ILogger _installLogger;
    public UIUpdateService UpdateService
    {
        get;
    }
    public InstallationsViewModel ViewModel
    {
        get;
    }

    public InstallationsPage()
        : this(App.GetService<ILoggerFactory>())
    {
    }

    public InstallationsPage(ILoggerFactory loggerFactory)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        UpdateService = new UIUpdateService(this.DispatcherQueue);
        ViewModel = App.GetService<InstallationsViewModel>();
        _installLogger = loggerFactory.CreateLogger("Raven.Install");
        UpdateService.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(UIUpdateService.StatusText))
            {
                ProgressStatusText.Text = UpdateService.StatusText;
            }
        };
    }

    private static XamlRoot? GetDialogXamlRoot() => App.MainWindow?.Content?.XamlRoot;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayoutForViewport(ActualHeight);
        UpdateDropZoneTypography(ActualWidth);
        SyncInputsFromViewModel();

        InstallButton.Content = "InstallationsPage_InstallLabel".GetLocalized();
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
        ProgressPercentText.Text = string.Empty;
        ProgressStatusText.Text = string.Empty;
        UpdateService.StopStatusAnimation();

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (ViewModel.IsInstalling)
            ApplyInstallingState(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Reliable teardown: Unloaded is NOT guaranteed to fire on navigation, so detach from
        // the singleton ViewModel here. Also stop the status-animation timer — a running
        // DispatcherQueueTimer is rooted by the dispatcher and would root this page via the
        // UpdateService.PropertyChanged handler. (This page uses no x:Bind, so no StopTracking.)
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        UpdateService.StopStatusAnimation();
    }

    private void SyncInputsFromViewModel()
    {
        SelectedFileText.Text = ViewModel.SelectedPackagePath ?? string.Empty;
        ClearButton.Visibility = string.IsNullOrWhiteSpace(SelectedFileText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        AdvancedInstallToggle.IsChecked = ViewModel.AdvancedInstallEnabled;
        AdvancedPanel.Visibility = ViewModel.AdvancedInstallEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveSignatureCheckBox.IsChecked = ViewModel.RemoveSignature;

        CustomFolderText.Text = ViewModel.CustomInstallFolder ?? string.Empty;
        ClearFolderButton.Visibility = string.IsNullOrWhiteSpace(CustomFolderText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateDependenciesCount();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallationsViewModel.IsInstalling))
        {
            ApplyInstallingState(ViewModel.IsInstalling);
        }
        else if (e.PropertyName == nameof(InstallationsViewModel.ProgressPercent))
        {
            if (ViewModel.IsInstalling)
            {
                var percent = (int)Math.Clamp(ViewModel.ProgressPercent, 0, 100);
                InstallProgressBar.Value = percent;
                ProgressPercentText.Text = $"{percent}%";
            }
        }
    }

    private void ApplyInstallingState(bool installing)
    {
        if (installing)
        {
            // Move focus to the page itself before disabling the active button.
            // This prevents WinUI from automatically advancing focus to the CustomFolderText box.
            this.Focus(FocusState.Programmatic);

            var percent = (int)Math.Clamp(ViewModel.ProgressPercent, 0, 100);
            ProgressPanel.Visibility = Visibility.Visible;
            InstallProgressBar.Value = percent;
            ProgressPercentText.Text = $"{percent}%";
            UpdateService.StartStatusAnimation("Install_Status_Installing".GetLocalized());

            InstallButton.IsEnabled = false;
            DropZoneButton.IsEnabled = false;
            BrowseFolderButton.IsEnabled = false;
            SelectDependenciesButton.IsEnabled = false;
            AdvancedInstallToggle.IsEnabled = false;

            ClearButton.Visibility = Visibility.Collapsed;
            ClearFolderButton.Visibility = Visibility.Collapsed;
            ClearDependenciesButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateService.StopStatusAnimation();
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallProgressBar.Value = 0;
            ProgressPercentText.Text = string.Empty;

            SyncInputsFromViewModel();

            DropZoneButton.IsEnabled = true;
            BrowseFolderButton.IsEnabled = true;
            SelectDependenciesButton.IsEnabled = true;
            AdvancedInstallToggle.IsEnabled = true;
            switch (ViewModel.InstallResult)
            {
                case InstallResultState.Success:
                    InstallButton.Content = new SymbolIcon { Symbol = Symbol.Accept };
                    InstallButton.IsEnabled = false;
                    break;
                case InstallResultState.Failure:
                    InstallButton.Content = new SymbolIcon { Symbol = Symbol.Cancel };
                    InstallButton.IsEnabled = false;
                    break;
                default:
                    InstallButton.Content = "InstallationsPage_InstallLabel".GetLocalized();
                    InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(SelectedFileText.Text);
                    break;
            }
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutForViewport(e.NewSize.Height);
        UpdateDropZoneTypography(e.NewSize.Width);
    }

    private void UpdateLayoutForViewport(double viewportHeight)
    {
        var headerAllowance = 200; // approximate title + progress + path row
        var desired = Math.Max(120, (viewportHeight - headerAllowance) * 2.0 / 3.0);
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
        var value = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        SelectedFileText.Text = value;
        ViewModel.SelectedPackagePath = value.Length == 0 ? null : value;
        // A fresh selection clears any prior install result indicator.
        ViewModel.InstallResult = InstallResultState.None;
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
        try
        {
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            var files = NativeFilePicker.PickPackageFiles(
                hwnd, allowMultiple: false, "InstallationsPage_FilePicker_Title".GetLocalized());
            if (files.Count > 0 && !string.IsNullOrWhiteSpace(files[0]))
                SetSelectedFile(files[0]);
        }
        catch (Exception ex)
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot is not null)
                _ = InstallHelper.ShowDialogAsync(
                    xamlRoot,
                    "InstallationsPage_FilePicker_Error".GetLocalized(),
                    ex.Message);
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
        if (ViewModel.IsInstalling)
            return;

        var path = SelectedFileText.Text;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (ViewModel.AdvancedInstallEnabled)
            await PerformCustomInstallAsync(path);
        else
            await PerformInstallAsync(path, false);
    }

    private void MoreOptionsFlyout_Opening(object? sender, object e)
    {
        var width = MoreOptionsButton.ActualWidth;
        if (width <= 0)
            return;
        if (sender is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<MenuFlyoutItemBase>())
                item.MinWidth = width;
        }
    }

    private void AdvancedInstallToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AdvancedInstallEnabled = AdvancedInstallToggle.IsChecked;
        AdvancedPanel.Visibility = AdvancedInstallToggle.IsChecked
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!ViewModel.AdvancedInstallEnabled)
        {
            ViewModel.RemoveSignature = false;
            RemoveSignatureCheckBox.IsChecked = false;
        }

        UpdateInstallDependenciesSeparatelyState();
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        var folder = NativeFilePicker.PickFolder(
            hwnd, "InstallationsPage_FolderPicker_Title".GetLocalized());
        if (!string.IsNullOrWhiteSpace(folder))
        {
            ViewModel.CustomInstallFolder = folder;
            CustomFolderText.Text = folder;
            ClearFolderButton.Visibility = Visibility.Visible;
        }
    }

    private void ClearFolderButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CustomInstallFolder = null;
        CustomFolderText.Text = string.Empty;
        ClearFolderButton.Visibility = Visibility.Collapsed;
    }

    private void RemoveSignatureCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveSignature = RemoveSignatureCheckBox.IsChecked == true;
    }

    private void SelectDependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        IReadOnlyList<string> files;
        try
        {
            files = NativeFilePicker.PickPackageFiles(
                hwnd, allowMultiple: true, "InstallationsPage_DependencyPicker_Title".GetLocalized());
        }
        catch (Exception ex)
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot is not null)
                _ = InstallHelper.ShowDialogAsync(
                    xamlRoot,
                    "InstallationsPage_FilePicker_Error".GetLocalized(),
                    ex.Message);
            return;
        }

        if (files.Count > 0)
        {
            // Append to the existing selection rather than replacing it; use the
            // Clear (X) button to start over. Skip paths already selected so adding
            // the same dependency twice still shows it only once (paths are
            // compared case-insensitively, matching Windows file-system semantics).
            foreach (var f in files)
            {
                if (!ViewModel.DependencyPaths.Any(p => string.Equals(p, f, StringComparison.OrdinalIgnoreCase)))
                    ViewModel.DependencyPaths.Add(f);
            }
            UpdateDependenciesCount();
        }
    }

    private void ClearDependenciesButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.DependencyPaths.Clear();
        UpdateDependenciesCount();
    }

    private void UpdateDependenciesCount()
    {
        var count = ViewModel.DependencyPaths.Count;
        DependenciesCountText.Text = count == 0
            ? string.Empty
            : "InstallationsPage_Dependencies_Count".GetLocalizedFormat(count);
        ClearDependenciesButton.Visibility = count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        InstallDependenciesSeparatelyCheckBox.Visibility = count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateInstallDependenciesSeparatelyState();
    }

    private void UpdateInstallDependenciesSeparatelyState()
    {
        if (ViewModel.AdvancedInstallEnabled)
        {
            InstallDependenciesSeparatelyCheckBox.IsChecked = true;
            InstallDependenciesSeparatelyCheckBox.IsEnabled = false;
        }
        else
        {
            InstallDependenciesSeparatelyCheckBox.IsChecked = false;
            InstallDependenciesSeparatelyCheckBox.IsEnabled = true;
        }
    }

    private async Task PerformCustomInstallAsync(string path)
    {
        var dialogTitle = "Install_Dialog_Title".GetLocalized();

        if (!SideloadingCheckService.IsDeveloperModeEnabled(_installLogger))
        {
            await ShowDeveloperModeRequiredDialogAsync();
            return;
        }

        var folder = ViewModel.CustomInstallFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot is not null)
                await InstallHelper.ShowDialogAsync(
                    xamlRoot, dialogTitle, "Install_Error_NoFolderSelected".GetLocalized());
            return;
        }

        if (_installLogger.IsEnabled(LogLevel.Information))
        {
            _installLogger.LogInformation(
                "Custom install start | Path={Path} | Folder={Folder} | RemoveSig={RemoveSig} | Deps={Deps}",
                path, folder, ViewModel.RemoveSignature, ViewModel.DependencyPaths.Count);
        }

        // Drive the install through the VM; the page handler reflects progress + completion onto
        // whichever page instance is shown (so it survives navigation).
        ViewModel.ProgressPercent = 0;
        ViewModel.InstallResult = InstallResultState.None;
        ViewModel.IsInstalling = true;

        var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            ViewModel.ProgressPercent = Math.Clamp(p.Percent, 0, 100));

        // Snapshot the inputs on the UI thread; the install itself runs on a background thread.
        var removeSignature = ViewModel.RemoveSignature;
        var dependencyPaths = ViewModel.DependencyPaths.ToList();

        var succeeded = false;
        Exception? installException = null;
        try
        {
            await Task.Run(() => CustomAppPackageInstaller.InstallLooseAsync(
                path,
                folder!,
                removeSignature,
                dependencyPaths,
                progress,
                CancellationToken.None,
                _installLogger));
            succeeded = true;
        }
        catch (Exception ex)
        {
            installException = ex;
        }
        finally
        {
            ViewModel.InstallResult = succeeded ? InstallResultState.Success : InstallResultState.Failure;
            ViewModel.SelectedPackagePath = null;
            ViewModel.CustomInstallFolder = null;
            ViewModel.RemoveSignature = false;
            ViewModel.DependencyPaths.Clear();
            ViewModel.IsInstalling = false;
        }

        if (!succeeded && installException != null)
            await ShowCustomInstallErrorAsync(dialogTitle, installException);
    }

    private static async Task ShowCustomInstallErrorAsync(string title, Exception ex)
    {
        var xamlRoot = GetDialogXamlRoot();
        if (xamlRoot is null)
            return;

        if (ex is CustomInstallException cix)
        {
            var message = cix.Reason switch
            {
                CustomInstallError.FolderExists =>
                    "Install_Error_FolderExists".GetLocalizedFormat(cix.FolderName),
                CustomInstallError.NoCompatibleArch =>
                    "Install_Error_NoInstallableArch".GetLocalized(),
                CustomInstallError.ManifestMissing =>
                    "Install_Error_ManifestMissing".GetLocalized(),
                _ => "Install_Error_Generic".GetLocalizedFormat(cix.Message),
            };
            await InstallHelper.ShowDialogAsync(xamlRoot, title, message);
            return;
        }

        await InstallHelper.ShowInstallationErrorDialogAsync(xamlRoot, title, ex);
    }

    private static async Task ShowDeveloperModeRequiredDialogAsync()
    {
        var xamlRoot = GetDialogXamlRoot();
        if (xamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            Title = "DeveloperMode_DisabledTitle".GetLocalized(),
            Content = "DeveloperMode_DisabledMessage".GetLocalized(),
            PrimaryButtonText = "DeveloperMode_EnableButton".GetLocalized(),
            CloseButtonText = "DeveloperMode_CancelButton".GetLocalized(),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
    }

    private async Task PerformInstallAsync(string path, bool ignoreVersion, bool deferRegistration = false, bool installDependenciesSeparatelyRetry = false, IReadOnlyList<string>? savedDependencies = null)
    {
        if (_installLogger.IsEnabled(LogLevel.Information))
        {
            _installLogger.LogInformation(
                "Install start | Path={Path} | IgnoreVersion={IgnoreVersion}",
                path,
                ignoreVersion
            );
        }

        // Drive the install through the VM; the page handler reflects progress + completion.
        ViewModel.ProgressPercent = 0;
        ViewModel.InstallResult = InstallResultState.None;
        ViewModel.IsInstalling = true;

        var progress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            ViewModel.ProgressPercent = Math.Clamp(p.Percent, 0, 100));

        var dependencyPaths = savedDependencies ?? ViewModel.DependencyPaths.ToList();

        var succeeded = false;
        Exception? installException = null;
        try
        {
            await AppPackageInstaller.InstallAsync(
                path,
                dependencyPackagePaths: dependencyPaths,
                progress,
                ignoreVersion: ignoreVersion,
                installDependenciesSeparately: InstallDependenciesSeparatelyCheckBox.IsChecked == true || installDependenciesSeparatelyRetry,
                deferRegistration: deferRegistration,
                logger: _installLogger
            );
            succeeded = true;
            if (_installLogger.IsEnabled(LogLevel.Information))
            {
                _installLogger.LogInformation(
                    "Install success | Path={Path} | IgnoreVersion={IgnoreVersion}",
                    path,
                    ignoreVersion
                );
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            installException = ex;
            _installLogger.LogError(
                ex,
                "Install failed | Path={Path} | IgnoreVersion={IgnoreVersion} | Failure=PermissionOrCOM",
                path,
                ignoreVersion
            );
        }
        catch (Exception ex)
        {
            installException = ex;
            _installLogger.LogError(
                ex,
                "Install failed | Path={Path} | IgnoreVersion={IgnoreVersion}",
                path,
                ignoreVersion
            );
        }
        finally
        {
            // Record result + reset inputs in the VM; the handler reflects onto the current page.
            ViewModel.InstallResult = succeeded ? InstallResultState.Success : InstallResultState.Failure;
            ViewModel.SelectedPackagePath = null;
            ViewModel.CustomInstallFolder = null;
            ViewModel.RemoveSignature = false;
            ViewModel.DependencyPaths.Clear();
            ViewModel.IsInstalling = false;
        }

        if (succeeded || installException is null)
            return;

        var xamlRoot = GetDialogXamlRoot();
        if (xamlRoot is null)
            return;
        var retryAction = await InstallHelper.HandleInstallExceptionAsync(
            xamlRoot,
            "Install_Dialog_Title".GetLocalized(),
            installException,
            isAlreadyForceInstalling: ignoreVersion
        );

        if (retryAction == RetryInstallAction.RetryForce)
        {
            await PerformInstallAsync(path, ignoreVersion: true, deferRegistration: false, false, dependencyPaths);
        }
        else if (retryAction == RetryInstallAction.RetryNormal || retryAction == RetryInstallAction.RetryDeferred)
        {
            bool nextDeferRegistration = retryAction == RetryInstallAction.RetryDeferred;
            await PerformInstallAsync(path, ignoreVersion, nextDeferRegistration, false, dependencyPaths);
        }
        else if (retryAction == RetryInstallAction.RetryInstallDependenciesSeparately)
        {
            await PerformInstallAsync(path, ignoreVersion, false, true, dependencyPaths);
        }
    }
}
