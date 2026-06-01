using CommunityToolkit.Mvvm.ComponentModel;

namespace Raven.ViewModels;

/// <summary>
/// Session-scoped state for the install page's advanced (custom/loose) install mode.
/// Registered as a singleton, so this survives navigating away and back and resets only
/// when the app closes. NOT persisted to disk (matches the AppPage toggle behavior).
/// </summary>
public partial class InstallationsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _advancedInstallEnabled;

    [ObservableProperty]
    private string? _customInstallFolder;

    [ObservableProperty]
    private bool _removeSignature;

    /// <summary>True while an install is running. Observable so any page instance reflects the
    /// in-progress state (progress survives navigating away and back) and so a second concurrent
    /// install can be prevented.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Current install progress (0–100). Observable so a page navigated back to mid-install
    /// shows live progress.</summary>
    [ObservableProperty]
    private double _progressPercent;

    public IList<string> DependencyPaths { get; } = new List<string>();

    /// <summary>The selected app-package path, kept here so it survives navigating away and back.</summary>
    public string? SelectedPackagePath { get; set; }

    /// <summary>Result of the most recently finished install, so the completion indicator
    /// (tick / cross) can be reflected on whichever page instance is shown.</summary>
    public InstallResultState InstallResult { get; set; }
}

public enum InstallResultState
{
    None,
    Success,
    Failure,
}
