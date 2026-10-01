using System.ComponentModel;
using System.Runtime.CompilerServices;
using Raven.Helpers;

namespace Raven.Models;

// Bound with {Binding} in the Updates item templates; this generates the property
// accessors that reflection used to provide (unavailable when trimmed/NativeAOT).
[WinRT.GeneratedBindableCustomProperty]
public partial class UpdateItem : INotifyPropertyChanged
{
    private string _packageFamilyName = string.Empty;
    public string PackageFamilyName
    {
        get => _packageFamilyName;
        set
        {
            if (_packageFamilyName != value) { _packageFamilyName = value; OnPropertyChanged(); }
        }
    }

    private string _productId = string.Empty;
    public string ProductId
    {
        get => _productId;
        set
        {
            if (_productId != value) { _productId = value; OnPropertyChanged(); }
        }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value) { _title = value; OnPropertyChanged(); }
        }
    }

    private string? _logoUrl;
    public string? LogoUrl
    {
        get => _logoUrl;
        set
        {
            var normalized = string.IsNullOrEmpty(value) ? null : value;
            if (_logoUrl != normalized) { _logoUrl = normalized; OnPropertyChanged(); }
        }
    }

    private string _publisherName = string.Empty;
    public string PublisherName
    {
        get => _publisherName;
        set
        {
            if (_publisherName != value) { _publisherName = value; OnPropertyChanged(); }
        }
    }

    private string _installedVersion = string.Empty;
    public string InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (_installedVersion != value) { _installedVersion = value; OnPropertyChanged(); }
        }
    }

    private string _storeVersion = string.Empty;
    public string StoreVersion
    {
        get => _storeVersion;
        set
        {
            if (_storeVersion != value) { _storeVersion = value; OnPropertyChanged(); }
        }
    }

    private string? _revisionId;
    public string? RevisionId
    {
        get => _revisionId;
        set
        {
            if (_revisionId != value) { _revisionId = value; OnPropertyChanged(); }
        }
    }

    private bool _isBundle;
    public bool IsBundle
    {
        get => _isBundle;
        set
        {
            if (_isBundle != value) { _isBundle = value; OnPropertyChanged(); }
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); }
        }
    }

    private DownloadStatus? _status;
    public DownloadStatus? Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsVersionVisible));
                OnPropertyChanged(nameof(IsProgressVisible));
                OnPropertyChanged(nameof(IsCheckboxEnabled));
                OnPropertyChanged(nameof(IsCancelButtonVisible));
            }
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            int oldPercent = (int)_progress;
            int newPercent = (int)value;
            _progress = value;
            if (oldPercent != newPercent)
                OnPropertyChanged();
        }
    }

    private string? _statusTextOverride;
    public string? StatusTextOverride
    {
        get => _statusTextOverride;
        set
        {
            if (_statusTextOverride != value)
            {
                _statusTextOverride = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private string _displayDetailsText = string.Empty;
    public string DisplayDetailsText
    {
        get => _displayDetailsText;
        set
        {
            if (_displayDetailsText != value) { _displayDetailsText = value; OnPropertyChanged(); }
        }
    }

    public string StatusText => _status switch
    {
        null => string.Empty,
        DownloadStatus.Pending => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Status_Pending".GetLocalized(),
        DownloadStatus.Downloading => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Status_Downloading".GetLocalized(),
        DownloadStatus.Installing => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Status_Installing".GetLocalized(),
        DownloadStatus.Completed => "Status_Updated".GetLocalized(),
        DownloadStatus.Cancelling => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Status_Cancelling".GetLocalized(),
        DownloadStatus.Failed => "Status_Failed".GetLocalized(),
        DownloadStatus.Cancelled => "Status_Cancelled".GetLocalized(),
        _ => string.Empty,
    };

    /// <summary>True when the version text row should be visible (no active download).</summary>
    public bool IsVersionVisible => _status is null
        or DownloadStatus.Completed
        or DownloadStatus.Failed
        or DownloadStatus.Cancelled;

    /// <summary>True when the progress row should be visible (active download).</summary>
    public bool IsProgressVisible => _status is
        DownloadStatus.Pending
        or DownloadStatus.Downloading
        or DownloadStatus.Installing
        or DownloadStatus.Cancelling;

    /// <summary>True when the checkbox can be interacted with (item is not currently being updated).</summary>
    public bool IsCheckboxEnabled => !IsProgressVisible;

    /// <summary>True when the cancel button should be shown (active but not yet cancelling).</summary>
    public bool IsCancelButtonVisible => _status is
        DownloadStatus.Pending
        or DownloadStatus.Downloading
        or DownloadStatus.Installing;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
