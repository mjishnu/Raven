using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace test.Models;

public class UpdateItem : INotifyPropertyChanged
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
            ? StatusTextOverride : "Pending",
        DownloadStatus.Downloading => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Downloading",
        DownloadStatus.Installing => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Installing",
        DownloadStatus.Completed => "Updated",
        DownloadStatus.Cancelling => !string.IsNullOrWhiteSpace(StatusTextOverride)
            ? StatusTextOverride : "Cancelling",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Cancelled => "Cancelled",
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
