using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StoreListings.Library;

namespace test.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

public partial class DownloadItem : INotifyPropertyChanged
{
    private string _productId = string.Empty;
    public string ProductId
    {
        get => _productId;
        set { if (_productId != value) { _productId = value; OnPropertyChanged(); } }
    }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    private string? _logoUrl;
    public string? LogoUrl
    {
        get => _logoUrl;
        set { if (_logoUrl != value) { _logoUrl = value; OnPropertyChanged(); } }
    }

    private string _publisherName = string.Empty;
    public string PublisherName
    {
        get => _publisherName;
        set { if (_publisherName != value) { _publisherName = value; OnPropertyChanged(); } }
    }

    private DownloadStatus _status = DownloadStatus.Pending;
    public DownloadStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set 
        { 
            if (Math.Abs(_progress - value) > double.Epsilon) 
            { 
                _progress = value; 
                OnPropertyChanged(); 
                // Also notify StatusText since it depends on Progress when downloading
                if (_status == DownloadStatus.Downloading)
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            } 
        }
    }

    private DateTime _startedAt = DateTime.Now;
    public DateTime StartedAt
    {
        get => _startedAt;
        set { if (_startedAt != value) { _startedAt = value; OnPropertyChanged(); } }
    }

    private DateTime? _completedAt;
    public DateTime? CompletedAt
    {
        get => _completedAt;
        set { if (_completedAt != value) { _completedAt = value; OnPropertyChanged(); } }
    }

    private List<string> _downloadedFilePaths = [];
    /// <summary>
    /// List of file paths that were downloaded for this item
    /// </summary>
    public List<string> DownloadedFilePaths
    {
        get => _downloadedFilePaths;
        set { if (_downloadedFilePaths != value) { _downloadedFilePaths = value; OnPropertyChanged(); } }
    }

    // For storing the original product info to navigate back
    [JsonIgnore]
    public StoreEdgeFDProduct? ProductInfo { get; set; }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        DownloadStatus.Pending => "Pending",
        DownloadStatus.Downloading => $"Downloading... {Progress:F0}%",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Cancelled => "Cancelled",
        _ => "Unknown"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
