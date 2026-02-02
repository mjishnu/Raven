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
    private static readonly Lazy<DownloadManagerService> _instance = new(() =>
        new DownloadManagerService()
    );
    public static DownloadManagerService Instance => _instance.Value;

    private readonly string _downloadDataPath;
    private readonly object _lock = new();
    private DispatcherQueue? _dispatcherQueue;

    // Store cancellation tokens for active downloads
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens =
        new();

    // Track cancellation requests even when CTS isn't registered yet.
    private readonly ConcurrentDictionary<string, bool> _cancellationRequested = new();

    // Simple observer count - when > 0, someone is viewing downloads (DownloadsPage or AppPage)
    private int _observerCount = 0;

    public ObservableCollection<DownloadItem> Downloads { get; } = [];
    public HashSet<string> DownloadedProductIds { get; private set; } = [];

    private DownloadManagerService()
    {
        _downloadDataPath = Path.Combine(AppContext.BaseDirectory, "downloads.json");
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

    public bool HasDispatcherQueue => _dispatcherQueue is not null;

    /// <summary>
    /// Call when a page starts displaying download progress (DownloadsPage or AppPage).
    /// </summary>
    public void BeginObserving() => Interlocked.Increment(ref _observerCount);

    /// <summary>
    /// Call when a page stops displaying download progress.
    /// </summary>
    public void EndObserving()
    {
        var count = Interlocked.Decrement(ref _observerCount);
        if (count < 0)
        {
            Interlocked.Exchange(ref _observerCount, 0);
        }
    }

    /// <summary>
    /// Returns true if any page is currently displaying download progress.
    /// </summary>
    public bool IsAnyoneObserving => Volatile.Read(ref _observerCount) > 0;

    /// <summary>
    /// Run an action on the UI thread immediately.
    /// </summary>
    public void RunOnUIThread(Action action)
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
                RevisionId = productInfo.RevisionId,
                StoreVersion = productInfo.Version,
                Status = DownloadStatus.Downloading,
                StartedAt = DateTime.Now,
                ProductInfo = productInfo,
                DownloadedFilePaths = [],
            };

            RunOnUIThread(() => Downloads.Insert(0, item));
            SaveDownloads();
        }
    }

    public void UpdateDownloadStoreVersion(string productId, string? storeVersion)
    {
        if (string.IsNullOrWhiteSpace(storeVersion))
            return;

        var item = GetDownload(productId);
        if (
            item == null
            || string.Equals(item.StoreVersion, storeVersion, StringComparison.OrdinalIgnoreCase)
        )
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.StoreVersion = storeVersion);
        }
        else
        {
            item.StoreVersion = storeVersion;
        }

        SaveDownloads();
    }

    public void RegisterCancellationToken(string productId, CancellationTokenSource cts)
    {
        // If user already requested cancellation from elsewhere (e.g., DownloadsPage)
        // before the CTS existed on AppPage, cancel immediately.
        if (_cancellationRequested.TryRemove(productId, out var wasRequested) && wasRequested)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
        }

        _cancellationTokens[productId] = cts;
    }

    public void UnregisterCancellationToken(string productId)
    {
        _cancellationTokens.TryRemove(productId, out _);
        _cancellationRequested.TryRemove(productId, out _);
    }

    public void CancelDownload(string productId)
    {
        var item = GetDownload(productId);
        if (item?.Status == DownloadStatus.Completed)
        {
            return;
        }

        // Cancelling during the install phase should not immediately force the final state.
        // Near 100% the install may still complete; let the install operation determine
        // whether the app ended up installed (Completed) or not (Cancelled).
        if (item?.Status is DownloadStatus.Installing)
        {
            // Best-effort cancel if an install is in progress.
            if (_cancellationTokens.TryGetValue(productId, out var installCts))
            {
                try
                {
                    installCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }
            }

            // Clear any remembered cancellation request so future installs/downloads are not auto-cancelled.
            _cancellationRequested.TryRemove(productId, out _);

            UpdateDownloadDetailsText(productId, string.Empty);
            // Keep status as Installing; the install operation will finalize to Completed or Cancelled.
            UpdateDownloadStatusText(productId, "Cancelling");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    var current = GetDownload(productId);
                    if (current is { Status: DownloadStatus.Installing })
                    {
                        RunOnUIThread(() =>
                        {
                            if (current.Status == DownloadStatus.Installing)
                                current.StatusTextOverride = null;
                        });
                    }
                }
                catch
                {
                    // ignore
                }
            });
            return;
        }

        if (item?.Status is DownloadStatus.Completed)
        {
            // Not an active operation; keep it as completed.
            return;
        }

        // Always remember the request so phases that haven't registered CTS yet
        // (URL fetch, install) still get cancelled.
        _cancellationRequested[productId] = true;

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

        if (
            item?.Status == DownloadStatus.Installing
            || item?.Status == DownloadStatus.Completed
            || (item?.Status == DownloadStatus.Cancelling && item.DownloadedFilePaths.Count > 0)
        )
        {
            UpdateDownloadStatusText(productId, null);
            UpdateDownloadStatus(productId, DownloadStatus.Completed);
            return;
        }

        UpdateDownloadStatusText(productId, "Cancelling");
        UpdateDownloadStatus(productId, DownloadStatus.Cancelling);
    }

    public bool IsCancellationRequested(string productId)
    {
        if (_cancellationTokens.TryGetValue(productId, out var cts))
        {
            try
            {
                return cts.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        return _cancellationRequested.TryGetValue(productId, out var requested) && requested;
    }

    public DownloadItem? GetDownload(string productId)
    {
        lock (_lock)
        {
            return Downloads.FirstOrDefault(d => d.ProductId == productId);
        }
    }

    public void UpdateDownloadRevision(string productId, string? revisionId)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
            return;

        var item = GetDownload(productId);
        if (
            item == null
            || string.Equals(item.RevisionId, revisionId, StringComparison.OrdinalIgnoreCase)
        )
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.RevisionId = revisionId);
        }
        else
        {
            item.RevisionId = revisionId;
        }

        SaveDownloads();
    }

    public void UpdateDownloadProgress(string productId, double progress)
    {
        var item = GetDownload(productId);
        if (item == null)
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.Progress = progress);
        }
        else
        {
            item.SetProgressSilent(progress);
        }
    }

    public void UpdateDownloadBytes(string productId, long? receivedBytes, long? totalBytes)
    {
        var item = GetDownload(productId);
        if (item != null)
        {
            if (IsAnyoneObserving)
            {
                RunOnUIThread(() =>
                {
                    item.ReceivedBytes = receivedBytes;
                    item.TotalBytes = totalBytes;
                });
            }
            else
            {
                item.ReceivedBytes = receivedBytes;
                item.TotalBytes = totalBytes;
            }
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

    public void UpdateDownloadStatusText(string productId, string? statusTextOverride)
    {
        var item = GetDownload(productId);
        if (item != null)
        {
            if (IsAnyoneObserving)
            {
                RunOnUIThread(() => item.StatusTextOverride = statusTextOverride);
            }
            else
            {
                item.StatusTextOverride = statusTextOverride;
            }
        }
    }

    public void UpdateDownloadDetailsText(string productId, string detailsText)
    {
        var item = GetDownload(productId);
        if (item == null)
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.DisplayDetailsText = detailsText);
        }
        else
        {
            item.SetDisplayDetailsTextSilent(detailsText);
        }
    }

    public void UpdateDownloadStatus(string productId, DownloadStatus status)
    {
        var item = GetDownload(productId);
        if (item != null)
        {
            void ApplyStatus()
            {
                item.Status = status;
                if (status is DownloadStatus.Completed)
                {
                    item.CompletedAt = DateTime.Now;
                    item.HasValidCache = true;
                    if (IsAnyoneObserving)
                    {
                        item.Progress = 100;
                    }
                    else
                    {
                        item.SetProgressSilent(100);
                    }
                    item.StatusTextOverride = null;
                    lock (_lock)
                    {
                        DownloadedProductIds.Add(productId);
                    }
                }
                else if (
                    status
                    is DownloadStatus.Cancelled
                        or DownloadStatus.Failed
                        or DownloadStatus.Completed
                )
                {
                    item.StatusTextOverride = null;
                }
                else if (status == DownloadStatus.Cancelling)
                {
                    item.StatusTextOverride = null;
                }
                else if (status == DownloadStatus.Installing)
                {
                    // Installing implies download phase completed and files should be available on disk.
                    item.HasValidCache = item.DownloadedFilePaths.Count > 0;
                }
            }

            if (IsAnyoneObserving)
            {
                RunOnUIThread(ApplyStatus);
            }
            else
            {
                ApplyStatus();
            }
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
            return item != null
                && (
                    item.Status == DownloadStatus.Downloading
                    || item.Status == DownloadStatus.Pending
                    || item.Status == DownloadStatus.Installing
                    || item.Status == DownloadStatus.Cancelling
                );
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
                        // Migrate legacy states.
                        // - Older builds used "Downloaded" for "files present on disk".
                        // - Some builds used "Completed".
                        var statusText = item.Status.ToString();
                        if (
                            statusText.Equals("Downloaded", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            item.Status = DownloadStatus.Completed;
                        }

                        // Infer cache presence if it wasn't persisted in older versions.
                        if (!item.HasValidCache)
                        {
                            item.HasValidCache =
                                item.Status == DownloadStatus.Completed
                                || item.DownloadedFilePaths.Count > 0;
                        }

                        // Reset any "Downloading" status to "Cancelled" on app restart
                        // since the download won't continue
                        if (
                            item.Status == DownloadStatus.Downloading
                            || item.Status == DownloadStatus.Pending
                        )
                        {
                            item.Status = DownloadStatus.Cancelled;
                        }
                        else if (
                            item.Status == DownloadStatus.Cancelling
                            && item.DownloadedFilePaths.Count > 0
                        )
                        {
                            item.Status = DownloadStatus.Completed;
                        }
                        else if (item.Status == DownloadStatus.Cancelling)
                        {
                            item.Status = DownloadStatus.Cancelled;
                        }
                        else if (item.Status == DownloadStatus.Installing)
                        {
                            item.Status =
                                item.DownloadedFilePaths.Count > 0
                                    ? DownloadStatus.Completed
                                    : DownloadStatus.Cancelled;
                        }
                        Downloads.Add(item);
                        if (item.Status is DownloadStatus.Completed)
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

    private async Task SaveDownloadsAsync()
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

            // Write to temp file then replace to avoid partial/corrupt writes.
            var tmpPath = _downloadDataPath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json).ConfigureAwait(false);

            try
            {
                File.Copy(tmpPath, _downloadDataPath, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving downloads: {ex.Message}");
        }
    }

    // Keep sync API for existing callers.
    private void SaveDownloads() => SaveDownloadsThrottled(force: true);

    private readonly object _saveGate = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private Task _saveTask = Task.CompletedTask;
    private System.Threading.Timer? _debounceTimer;
    private volatile bool _savePending;

    /// <summary>
    /// Debounced + queued persistence: collapses frequent save requests into a single disk write.
    /// </summary>
    public void SaveDownloadsThrottled(bool force = false)
    {
        lock (_saveGate)
        {
            _savePending = true;

            // Ensure timer exists
            _debounceTimer ??= new System.Threading.Timer(
                _ => FlushPendingSave(),
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );

            var dueMs = force ? 0 : 1500; // coarse debounce to cut disk bandwidth
            _debounceTimer.Change(dueMs, Timeout.Infinite);
        }
    }

    private void FlushPendingSave()
    {
        lock (_saveGate)
        {
            if (!_savePending)
                return;

            _savePending = false;

            // Queue behind previous save; never run concurrently.
            _saveTask = _saveTask
                .ContinueWith(
                    _ => SaveDownloadsAsync(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                )
                .Unwrap();
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
            if (
                item.Status == DownloadStatus.Downloading
                || item.Status == DownloadStatus.Pending
                || item.Status == DownloadStatus.Installing
            )
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
        // Prefer deleting the whole app folder under `<base>\\downloads\\<AppFolderName>`.
        // This matches how downloads are laid out in `DownloadHelper`.
        try
        {
            var baseDownloadsDir = Path.Combine(AppContext.BaseDirectory, "downloads");

            // Find the deepest directory that still lives under the downloads root.
            string? appDir = null;
            foreach (var filePath in item.DownloadedFilePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                var dir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                // e.g. `<base>\\downloads\\AppName` or `<base>\\downloads\\AppName\\Dependencies`
                var parent = Directory.GetParent(dir);
                if (
                    parent != null
                    && parent.FullName.Equals(baseDownloadsDir, StringComparison.OrdinalIgnoreCase)
                )
                {
                    appDir = dir;
                    break;
                }

                // If we were in `...\\AppName\\Dependencies`, parent is `...\\AppName`.
                var grandParent = parent?.Parent;
                if (
                    grandParent != null
                    && grandParent.FullName.Equals(
                        baseDownloadsDir,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    appDir = parent!.FullName;
                    break;
                }
            }

            // Fallback: delete individual files if the folder can't be determined.
            if (string.IsNullOrWhiteSpace(appDir) || !Directory.Exists(appDir))
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

                return;
            }

            Directory.Delete(appDir, recursive: true);
            Debug.WriteLine($"Deleted app folder: {appDir}");

            // If `downloads` becomes empty, remove it as well.
            if (
                Directory.Exists(baseDownloadsDir)
                && !Directory.EnumerateFileSystemEntries(baseDownloadsDir).Any()
            )
            {
                Directory.Delete(baseDownloadsDir, recursive: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting app folder/files for {item.ProductId}: {ex.Message}");
        }
    }
}
