using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StoreListings.Library;
using Raven.Helpers;

namespace Raven.Models;

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
    public sealed class DownloadedFile
    {
        public string Path { get; set; } = string.Empty;
        public string? Hash { get; set; }
    }

    private DateTime _lastAccessedAt = DateTime.Now;
    public DateTime LastAccessedAt
    {
        get => _lastAccessedAt;
        set
        {
            if (_lastAccessedAt != value)
            {
                _lastAccessedAt = value;
                OnPropertyChanged();
            }
        }
    }

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

    [JsonIgnore]
    public double Progress
    {
        get => _progress;
        set
        {
            // Only fire PropertyChanged when the visible percentage changes (1% increments)
            int oldPercent = (int)_progress;
            int newPercent = (int)value;
            _progress = value;

            if (oldPercent != newPercent)
            {
                OnPropertyChanged();
            }
        }
    }

    public void SetProgressSilent(double value) => _progress = value;

    private List<DownloadedFile> _downloadedFiles = [];

    /// <summary>
    /// List of downloaded files with optional computed hash.
    /// </summary>
    public List<DownloadedFile> DownloadedFiles
    {
        get => _downloadedFiles;
        set
        {
            if (_downloadedFiles != value)
            {
                _downloadedFiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadedFilePaths));
            }
        }
    }

    [JsonIgnore]
    public List<string> DownloadedFilePaths
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DownloadPath) && Directory.Exists(DownloadPath))
            {
                try
                {
                    return Directory.GetFiles(DownloadPath, "*", SearchOption.AllDirectories).ToList();
                }
                catch
                {
                   // Fallback to memory list if directory access fails
                }
            }
            return DownloadedFiles.Select(f => f.Path).ToList();
        }
    }

    public InstallerType InstallerType { get; set; }

    /// <summary>
    /// True when the last user-initiated action for this item was "Download only".
    /// Persisted so Retry can repeat the correct action after an app restart.
    /// </summary>
    public bool WasDownloadOnly { get; set; }

    /// <summary>
    /// True when this item was started with the "bypass dependency filter" option, meaning every
    /// supported-architecture/version dependency was fetched and each must be installed as its own
    /// standalone package. Persisted so a force-install retry repeats the same install strategy.
    /// </summary>
    public bool InstallDependenciesSeparately { get; set; }

    public string? PackageFamilyName { get; set; }

    /// <summary>
    /// The root directory where files for this app are downloaded.
    /// Stores the full path.
    /// </summary>
    public string? DownloadPath { get; set; }

    // For storing the original product info to navigate back
    [JsonIgnore]
    public ProductData? ProductInfo { get; set; }

    [JsonIgnore]
    public Exception? LastInstallError { get; set; }

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
                : "Status_Pending".GetLocalized(),
            DownloadStatus.Downloading => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Status_Downloading".GetLocalized(),
            DownloadStatus.Installing => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Status_Installing".GetLocalized(),
            DownloadStatus.Completed => "Status_Completed".GetLocalized(),
            DownloadStatus.Cancelling => !string.IsNullOrWhiteSpace(StatusTextOverride)
                ? StatusTextOverride
                : "Status_Cancelling".GetLocalized(),
            DownloadStatus.Failed => "Status_Failed".GetLocalized(),
            DownloadStatus.Cancelled => "Status_Cancelled".GetLocalized(),
            _ => "Status_Unknown".GetLocalized(),
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

            return $" � {FormatBytes((long)ReceivedBytes)} / {FormatBytes((long)TotalBytes)}";
        }
    }

    // Stores the full details text (e.g., "45% � 500 MB / 1.2 GB") for display.
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
