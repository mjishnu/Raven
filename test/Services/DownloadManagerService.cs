using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using StoreListings.Library;
using test.Models;

namespace test.Services;

public class DownloadManagerService
{
    private static readonly Lazy<DownloadManagerService> _instance = new(() => new DownloadManagerService());
    public static DownloadManagerService Instance => _instance.Value;

    private readonly string _downloadDataPath;
    private readonly object _lock = new();
    private DispatcherQueue? _dispatcherQueue;

    // Store cancellation tokens for active downloads
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();

    public ObservableCollection<DownloadItem> Downloads { get; } = [];
    public HashSet<string> DownloadedProductIds { get; private set; } = [];

    private DownloadManagerService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raven"
        );
        Directory.CreateDirectory(appDataPath);
        _downloadDataPath = Path.Combine(appDataPath, "downloads.json");
        
        LoadDownloads();
    }

    /// <summary>
    /// Initialize the dispatcher queue for UI thread operations.
    /// Call this from the main window/app initialization.
    /// </summary>
    public void Initialize(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    private void RunOnUIThread(Action action)
    {
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    public void AddDownload(StoreEdgeFDProduct productInfo)
    {
        lock (_lock)
        {
            // Check if already exists
            if (Downloads.Any(d => d.ProductId == productInfo.ProductId))
                return;

            var item = new DownloadItem
            {
                ProductId = productInfo.ProductId,
                Title = productInfo.Title,
                LogoUrl = productInfo.Logo?.Url,
                PublisherName = productInfo.PublisherName,
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.Now,
                ProductInfo = productInfo,
                DownloadedFilePaths = []
            };

            RunOnUIThread(() => Downloads.Insert(0, item));
            SaveDownloads();
        }
    }

    public void RegisterCancellationToken(string productId, CancellationTokenSource cts)
    {
        _cancellationTokens[productId] = cts;
    }

    public void UnregisterCancellationToken(string productId)
    {
        _cancellationTokens.TryRemove(productId, out _);
    }

    public void CancelDownload(string productId)
    {
        if (_cancellationTokens.TryGetValue(productId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Token already disposed
            }
        }
        UpdateDownloadStatus(productId, DownloadStatus.Cancelled);
    }

    public DownloadItem? GetDownload(string productId)
    {
        lock (_lock)
        {
            return Downloads.FirstOrDefault(d => d.ProductId == productId);
        }
    }

    public void UpdateDownloadProgress(string productId, double progress)
    {
        var item = GetDownload(productId);
        if (item != null)
        {
            // Update on UI thread to ensure PropertyChanged works correctly
            RunOnUIThread(() => item.Progress = progress);
        }
    }

    public void AddDownloadedFilePath(string productId, string filePath)
    {
        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            if (item != null && !item.DownloadedFilePaths.Contains(filePath))
            {
                item.DownloadedFilePaths.Add(filePath);
            }
        }
    }

    public void UpdateDownloadStatus(string productId, DownloadStatus status)
    {
        var item = GetDownload(productId);
        if (item != null)
        {
            // Update on UI thread
            RunOnUIThread(() =>
            {
                item.Status = status;
                if (status == DownloadStatus.Completed)
                {
                    item.CompletedAt = DateTime.Now;
                    item.Progress = 100;
                    lock (_lock)
                    {
                        DownloadedProductIds.Add(productId);
                    }
                }
            });
            SaveDownloads();
        }
    }

    public bool IsDownloaded(string productId)
    {
        lock (_lock)
        {
            return DownloadedProductIds.Contains(productId);
        }
    }

    public bool HasActiveDownload(string productId)
    {
        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            return item != null && (item.Status == DownloadStatus.Downloading || item.Status == DownloadStatus.Pending);
        }
    }

    private void LoadDownloads()
    {
        try
        {
            if (File.Exists(_downloadDataPath))
            {
                var json = File.ReadAllText(_downloadDataPath);
                var items = JsonSerializer.Deserialize<List<DownloadItem>>(json);
                if (items != null)
                {
                    Downloads.Clear();
                    DownloadedProductIds.Clear();
                    foreach (var item in items)
                    {
                        // Reset any "Downloading" status to "Cancelled" on app restart
                        // since the download won't continue
                        if (item.Status == DownloadStatus.Downloading || item.Status == DownloadStatus.Pending)
                        {
                            item.Status = DownloadStatus.Cancelled;
                        }
                        Downloads.Add(item);
                        if (item.Status == DownloadStatus.Completed)
                        {
                            DownloadedProductIds.Add(item.ProductId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading downloads: {ex.Message}");
        }
    }

    private void SaveDownloads()
    {
        try
        {
            List<DownloadItem> itemsToSave;
            lock (_lock)
            {
                itemsToSave = Downloads.ToList();
            }
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(itemsToSave, options);
            File.WriteAllText(_downloadDataPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving downloads: {ex.Message}");
        }
    }

    public void RemoveDownload(string productId, bool deleteFiles = true)
    {
        DownloadItem? item;
        lock (_lock)
        {
            item = Downloads.FirstOrDefault(d => d.ProductId == productId);
        }

        if (item != null)
        {
            // Cancel if still downloading
            if (item.Status == DownloadStatus.Downloading || item.Status == DownloadStatus.Pending)
            {
                CancelDownload(productId);
            }

            // Delete downloaded files from disk
            if (deleteFiles)
            {
                DeleteDownloadedFiles(item);
            }

            RunOnUIThread(() =>
            {
                lock (_lock)
                {
                    Downloads.Remove(item);
                    DownloadedProductIds.Remove(productId);
                }
            });
            SaveDownloads();
        }
    }

    private static void DeleteDownloadedFiles(DownloadItem item)
    {
        foreach (var filePath in item.DownloadedFilePaths)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Debug.WriteLine($"Deleted file: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting file {filePath}: {ex.Message}");
            }
        }
    }
}
