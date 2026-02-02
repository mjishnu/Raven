using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StoreListings.Library;

namespace test.Models;

public enum DownloadStatus
{
    Pending = 0,
    Downloading = 1,
    Installing = 2,
    Failed = 4,
    Cancelling = 5,
    Cancelled = 6,
    Completed = 7,
}

public partial class DownloadItem : INotifyPropertyChanged
{
    private string? _storeVersion;
    public string? StoreVersion
    {
        get => _storeVersion;
        set
        {
            if (_storeVersion != value)
            {
                _storeVersion = value;
                OnPropertyChanged();
            }
        }
    }


    private string _productId = string.Empty;
    public string ProductId
    {
        get => _productId;
        set
        {
            if (_productId != value)
            {
                _productId = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _revisionId;
    public string? RevisionId
    {
        get => _revisionId;
        set
        {
            if (_revisionId != value)
            {
                _revisionId = value;
                OnPropertyChanged();
            }
        }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _logoUrl;
    public string? LogoUrl
    {
        get => _logoUrl;
        set
        {
            if (_logoUrl != value)
            {
                _logoUrl = value;
                OnPropertyChanged();
            }
        }
    }

    private string _publisherName = string.Empty;
    public string PublisherName
    {
        get => _publisherName;
        set
        {
            if (_publisherName != value)
            {
                _publisherName = value;
                OnPropertyChanged();
            }
        }
    }

    private DownloadStatus _status = DownloadStatus.Pending;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }


    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            // Only fire PropertyChanged when the visible percentage changes (1% increments)
            // This reduces UI updates from potentially thousands per second to ~100 max
            int oldPercent = (int)_progress;
            int newPercent = (int)value;
            _progress = value;
            
            if (oldPercent != newPercent)
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Update progress without firing PropertyChanged. Use when nobody is observing.
    /// </summary>
    public void SetProgressSilent(double value) => _progress = value;

    private DateTime _startedAt = DateTime.Now;
    public DateTime StartedAt
    {
        get => _startedAt;
        set
        {
            if (_startedAt != value)
            {
                _startedAt = value;
                OnPropertyChanged();
            }
        }
    }

    private DateTime? _completedAt;
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (_completedAt != value)
            {
                _completedAt = value;
                OnPropertyChanged();
            }
        }
    }

    private List<string> _downloadedFilePaths = [];

    private bool _hasValidCache;

    /// <summary>
    /// True when we have a verified on-disk cache for this item (download completed).
    /// This persists even if the install later fails or is cancelled.
    /// </summary>
    public bool HasValidCache
    {
        get => _hasValidCache;
        set
        {
            if (_hasValidCache != value)
            {
                _hasValidCache = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// List of file paths that were downloaded for this item
    /// </summary>
    public List<string> DownloadedFilePaths
    {
        get => _downloadedFilePaths;
        set
        {
            if (_downloadedFilePaths != value)
            {
                _downloadedFilePaths = value;
                OnPropertyChanged();
            }
        }
    }

    // For storing the original product info to navigate back
    [JsonIgnore]
    public StoreEdgeFDProduct? ProductInfo { get; set; }

    private string? _statusTextOverride;

    [JsonIgnore]
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

    [JsonIgnore]
    public string StatusText =>
        Status switch
        {
            DownloadStatus.Pending => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Pending",
            DownloadStatus.Downloading => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Downloading",
            DownloadStatus.Installing => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Installing",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Cancelling => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Cancelling",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Cancelled => "Cancelled",
            _ => "Unknown",
        };

    private long? _receivedBytes;
    [JsonIgnore]
    public long? ReceivedBytes
    {
        get => _receivedBytes;
        set
        {
            if (_receivedBytes != value)
            {
                _receivedBytes = value;
                // Don't fire PropertyChanged for bytes - it's too frequent.
                // ProgressDetailsText is computed on-demand when needed.
            }
        }
    }

    private long? _totalBytes;
    [JsonIgnore]
    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes != value)
            {
                _totalBytes = value;
                // Don't fire PropertyChanged for bytes - it's too frequent.
            }
        }
    }

    [JsonIgnore]
    public string ProgressDetailsText
    {
        get
        {
            if (TotalBytes is null or <= 0 || ReceivedBytes is null or < 0)
                return string.Empty;

            return $" • {FormatBytes((long)ReceivedBytes)} / {FormatBytes((long)TotalBytes)}";
        }
    }

    // Stores the full details text (e.g., "45% • 500 MB / 1.2 GB") for display.
    // Updated by DownloadHelper; survives page navigation.
    private string _displayDetailsText = string.Empty;

    [JsonIgnore]
    public string DisplayDetailsText
    {
        get => _displayDetailsText;
        set
        {
            if (_displayDetailsText != value)
            {
                _displayDetailsText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Update display details text without firing PropertyChanged. Use when nobody is observing.
    /// </summary>
    public void SetDisplayDetailsTextSilent(string value) => _displayDetailsText = value;

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;

        if (bytes >= GB)
            return $"{bytes / GB:0.#} GB";
        if (bytes >= MB)
            return $"{bytes / MB:0.#} MB";
        if (bytes >= KB)
            return $"{bytes / KB:0.#} KB";
        return $"{bytes} B";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
