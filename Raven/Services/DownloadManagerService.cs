using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using StoreListings.Library;
using Raven.Models;
using Raven.Services.FilePermissions;

namespace Raven.Services;

public class DownloadManagerService
{
    private static readonly Lazy<DownloadManagerService> _instance = new(() =>
        new DownloadManagerService()
    );
    public static DownloadManagerService Instance => _instance.Value;

    private readonly IPersistentListStore<DownloadItem> _fileStore;
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

    // The persisted list loads on a background task started by the constructor (so first
    // frame isn't blocked on file IO + JSON parse); every public read/mutation path goes
    // through this guarded getter, which blocks only in the rare case the user reaches
    // download state before the load finishes.
    private readonly ObservableCollection<DownloadItem> _downloads = [];
    private readonly Task _loadTask;

    public ObservableCollection<DownloadItem> Downloads
    {
        get { EnsureLoaded(); return _downloads; }
    }

    public HashSet<string> DownloadedProductIds { get; private set; } = [];

    private void EnsureLoaded()
    {
        // Fast path is a single completed-task branch; GetResult never throws because
        // the load task swallows its own exceptions.
        if (!_loadTask.IsCompleted)
            _loadTask.GetAwaiter().GetResult();
    }

    private static bool IsActiveStatus(DownloadStatus? status) =>
        status is DownloadStatus.Downloading
            or DownloadStatus.Pending
            or DownloadStatus.Installing
            or DownloadStatus.Cancelling;

    public static string GetDownloadsRootFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Raven",
        "Downloads"
    );

    private DownloadItem? TouchDownload(string productId)
    {
        DownloadItem? item;
        lock (_lock)
        {
            item = Downloads.FirstOrDefault(d => d.ProductId == productId);
        }

        if (item == null)
            return null;

        var now = DateTime.Now;

        // Active downloads keep their alphabetical slot, and LastAccessedAt is only read
        // for sort order at load — update it silently with no UI dispatch. This is the
        // per-progress-tick hot path during a download.
        if (IsActiveStatus(item.Status))
        {
            item.SetLastAccessedAtSilent(now);
            return item;
        }

        RunOnUIThread(() =>
        {
            item.LastAccessedAt = now;

            // Re-check: status may have changed before the closure ran. Active downloads
            // keep their alphabetical slot; only inactive items bubble to the top of the
            // inactive section on access.
            if (IsActiveStatus(item.Status))
                return;

            lock (_lock)
            {
                var index = Downloads.IndexOf(item);
                int activeCount = Downloads.Count(d => IsActiveStatus(d.Status));
                if (index > activeCount)
                    Downloads.Move(index, activeCount);
            }
        });

        return item;
    }

    public void ClearDownloadedFileHash(string productId, string filePath)
    {
        var changed = false;

        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            if (item == null)
                return;

            var existing = item.DownloadedFiles.FirstOrDefault(f =>
                string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is null)
                return;

            if (!string.IsNullOrWhiteSpace(existing.Hash))
            {
                existing.Hash = null;
                changed = true;
            }
        }

        if (changed)
        {
            SaveDownloadsThrottled();
        }
    }

    public void RemoveDownloadedFileEntry(string productId, string filePath)
    {
        var changed = false;

        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            if (item == null)
                return;

            var index = item.DownloadedFiles.FindIndex(f =>
                string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase)
            );
            if (index < 0)
                return;

            item.DownloadedFiles.RemoveAt(index);
            changed = true;
        }

        if (changed)
        {
            SaveDownloadsThrottled();
        }
    }

    private DownloadManagerService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raven/ApplicationData"
        );

        _downloadDataPath = Path.Combine(appDataDir, "Downloads.json");

        _fileStore = new PersistentListStore<DownloadItem>(_downloadDataPath);

        // Load off-thread: Instance is first touched in OnLaunched, and a synchronous file
        // read + JSON parse there delays the first frame. Saves don't depend on the
        // directory existing here — PersistAsync creates it before every write.
        _loadTask = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(appDataDir);
                await LoadDownloadsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading downloads: {ex.Message}");
            }
        });
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

    public DownloadItem AddDownload(ProductData productInfo)
    {
        lock (_lock)
        {
            // Check if already exists
            var existing = Downloads.FirstOrDefault(d => d.ProductId == productInfo.ProductId);
            if (existing != null)
                return existing;

            var item = new DownloadItem
            {
                ProductId = productInfo.ProductId,
                Title = productInfo.Title,
                LogoUrl = productInfo.Logo?.Url,
                PublisherName = productInfo.PublisherName,
                RevisionId = productInfo.RevisionId,
                InstallerType = productInfo.InstallerType,
                PackageFamilyName = productInfo.PackageFamilyName,
                StoreVersion = null,
                Status = DownloadStatus.Downloading,
                LastAccessedAt = DateTime.Now,
                ProductInfo = productInfo,
                DownloadedFiles = [],
            };

            RunOnUIThread(() =>
            {
                // Insert in alphabetical order within the active section.
                int insertAt = 0;
                for (int i = 0; i < Downloads.Count; i++)
                {
                    if (!IsActiveStatus(Downloads[i].Status))
                        break;
                    if (string.Compare(Downloads[i].Title, item.Title, StringComparison.OrdinalIgnoreCase) <= 0)
                        insertAt = i + 1;
                    else
                        break;
                }
                Downloads.Insert(insertAt, item);
            });
            SaveDownloads();
            return item;
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

    public void ClearDownloadError(string productId)
    {
        var item = GetDownload(productId);
        if (item == null)
            return;

        if (item.LastInstallError == null)
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.LastInstallError = null);
        }
        else
        {
            item.LastInstallError = null;
        }

        SaveDownloads();
    }

    public void UpdateDownloadPath(string productId, string downloadPath)
    {
        var item = GetDownload(productId);
        if (item == null)
            return;

        if (string.Equals(item.DownloadPath, downloadPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsAnyoneObserving)
        {
            RunOnUIThread(() => item.DownloadPath = downloadPath);
        }
        else
        {
            item.DownloadPath = downloadPath;
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
        TouchDownload(productId);
        var item = GetDownload(productId);
        if (item?.Status == DownloadStatus.Completed)
        {
            return;
        }

        // Cancelling during the install phase should not immediately force the final status.
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
            // ReceivedBytes/TotalBytes setters are intentionally silent (no PropertyChanged)
            // and nothing binds to them, so no UI-thread marshaling is needed. The values are
            // only read back on the 250ms-throttled progress tick to format the details string.
            // (Marshaling here flooded the dispatcher with one work item per 64KB block.)
            item.ReceivedBytes = receivedBytes;
            item.TotalBytes = totalBytes;
        }
    }

    public void AddDownloadedFilePath(string productId, string filePath, bool isMainPackage = false) =>
        AddDownloadedFile(productId, filePath, hash: null, isMainPackage);

    public void AddDownloadedFile(string productId, string filePath, string? hash, bool isMainPackage = false)
    {
        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            if (item == null)
                return;

            var existing = item.DownloadedFiles.FirstOrDefault(f =>
                string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is null)
            {
                item.DownloadedFiles.Add(
                    new DownloadItem.DownloadedFile { Path = filePath, Hash = hash, IsMainPackage = isMainPackage }
                );
            }
            else
            {
                existing.IsMainPackage = isMainPackage;
                if (!string.IsNullOrWhiteSpace(hash))
                    existing.Hash = hash;
            }
        }

        // Persist hashes to disk so integrity checks survive restart, but only once the file
        // has completed (or can be resumed via delta on next run). Callers must only provide
        // a non-null hash after a successful download/delta apply.
        if (!string.IsNullOrWhiteSpace(hash))
        {
            SaveDownloadsThrottled();
        }
    }

    public string? TryGetDownloadedFileHash(string productId, string filePath)
    {
        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            var entry = item?.DownloadedFiles.FirstOrDefault(f =>
                string.Equals(f.Path, filePath, StringComparison.OrdinalIgnoreCase)
            );
            return entry?.Hash;
        }
    }

    public void RemoveObsoleteJsonEntries(string productId, HashSet<string> validPaths)
    {
        lock (_lock)
        {
            var item = Downloads.FirstOrDefault(d => d.ProductId == productId);
            if (item == null)
                return;

            int removed = item.DownloadedFiles.RemoveAll(f => !validPaths.Contains(f.Path));
            if (removed > 0)
            {
                SaveDownloadsThrottled();
            }
        }
    }

    public void UpdateDownloadStatusText(string productId, string? statusTextOverride)
    {
        var item = TouchDownload(productId);
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
        var item = TouchDownload(productId);
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

    public void UpdateDownloadLastError(string productId, Exception? ex)
    {
        var item = TouchDownload(productId);
        if (item != null)
        {
            if (IsAnyoneObserving)
            {
                RunOnUIThread(() => item.LastInstallError = ex);
            }
            else
            {
                item.LastInstallError = ex;
            }
        }
    }

    public void UpdateDownloadStatus(string productId, DownloadStatus status)
    {
        TouchDownload(productId);
        var item = GetDownload(productId);
        if (item != null)
        {
            // A terminal status ends the flow a remembered cancel request belonged to.
            // Letting the flag outlive its flow would auto-cancel the product's NEXT
            // download the moment it registers its CTS.
            if (status is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Failed)
            {
                _cancellationRequested.TryRemove(productId, out _);
            }

            void ApplyStatus()
            {
                var previousStatus = item.Status;
                item.Status = status;
                if (status is DownloadStatus.Completed)
                {
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
                else if (status is DownloadStatus.Cancelled or DownloadStatus.Failed)
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
                    // No dedicated cache flag; the presence of downloaded file paths can be used as a hint.
                }

                bool wasActive = IsActiveStatus(previousStatus);
                bool isNowActive = IsActiveStatus(status);

                if (!wasActive && isNowActive)
                {
                    // Inactive → active: move into alphabetical position within the active section.
                    lock (_lock)
                    {
                        var idx = Downloads.IndexOf(item);
                        int targetIdx = 0;
                        for (int i = 0; i < Downloads.Count; i++)
                        {
                            if (Downloads[i] == item || !IsActiveStatus(Downloads[i].Status))
                                break;
                            if (string.Compare(Downloads[i].Title, item.Title, StringComparison.OrdinalIgnoreCase) <= 0)
                                targetIdx = i + 1;
                            else
                                break;
                        }
                        if (idx >= 0 && idx != targetIdx)
                            Downloads.Move(idx, targetIdx);
                    }
                }
                else if (wasActive && !isNowActive)
                {
                    // Active → inactive: move to the top of the inactive section.
                    lock (_lock)
                    {
                        var idx = Downloads.IndexOf(item);
                        int activeCount = Downloads.Count(d => IsActiveStatus(d.Status));
                        if (idx >= 0 && idx != activeCount)
                            Downloads.Move(idx, activeCount);
                    }
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
        // The only public member that reads persisted state without going through the
        // guarded Downloads getter.
        EnsureLoaded();
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

    // NOTE: runs inside _loadTask; must use the _downloads backing field, never the
    // Downloads property — the guarded getter would self-deadlock waiting on _loadTask.
    private async Task LoadDownloadsAsync()
    {
        var items = await _fileStore.LoadAsync();

        _downloads.Clear();
        DownloadedProductIds.Clear();
        
        foreach (var item in items)
        {
            if (item.LastAccessedAt == default)
            {
                item.LastAccessedAt = DateTime.Now;
            }

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

            // Helper to detect if files are present (legacy list or new folder path)
            bool HasFiles()
            {
                if (item.DownloadedFiles.Count > 0)
                    return true;
                if (
                    !string.IsNullOrWhiteSpace(item.DownloadPath)
                    && Directory.Exists(item.DownloadPath)
                )
                {
                    return Directory
                        .EnumerateFileSystemEntries(item.DownloadPath)
                        .Any();
                }
                return false;
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
            else if (item.Status == DownloadStatus.Cancelling && HasFiles())
            {
                item.Status = DownloadStatus.Completed;
            }
            else if (item.Status == DownloadStatus.Cancelling)
            {
                item.Status = DownloadStatus.Cancelled;
            }
            else if (item.Status == DownloadStatus.Installing)
            {
                item.Status = HasFiles()
                    ? DownloadStatus.Completed
                    : DownloadStatus.Cancelled;
            }
            _downloads.Add(item);
            if (item.Status is DownloadStatus.Completed)
            {
                DownloadedProductIds.Add(item.ProductId);
            }
        }

        var sorted = _downloads.OrderByDescending(d => d.LastAccessedAt).ToList();
        _downloads.Clear();
        foreach (var d in sorted)
        {
            _downloads.Add(d);
        }
    }

    private async Task SaveDownloadsAsync()
    {
        List<DownloadItem> itemsToSave;
        lock (_lock)
        {
            itemsToSave = Downloads.ToList();
        }

        await _fileStore.SaveAsync(itemsToSave);
    }

    // Keep sync API for existing callers.
    private void SaveDownloads() => SaveDownloadsThrottled(force: true);

    private readonly object _saveGate = new();
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
        TouchDownload(productId);
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

    public void ResetAllDownloads(bool deleteFiles = true)
    {
        List<DownloadItem> snapshot;
        lock (_lock)
        {
            snapshot = Downloads.ToList();
        }

        foreach (var item in snapshot)
        {
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Pending or DownloadStatus.Installing)
            {
                CancelDownload(item.ProductId);
            }

            if (deleteFiles)
            {
                DeleteDownloadedFiles(item);
            }
        }

        RunOnUIThread(() =>
        {
            lock (_lock)
            {
                Downloads.Clear();
                DownloadedProductIds.Clear();
            }
        });

        _cancellationTokens.Clear();
        _cancellationRequested.Clear();

        DeleteDownloadMetadataFile();
        DeleteDownloadsRootFolder();
        SaveDownloadsThrottled(force: true);
    }

    private void DeleteDownloadMetadataFile()
    {
        try
        {
            if (File.Exists(_downloadDataPath))
            {
                File.Delete(_downloadDataPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting download metadata file: {ex.Message}");
        }
    }

    private static void DeleteDownloadsRootFolder()
    {
        try
        {
            var baseDownloadsDir = GetDownloadsRootFolder();
            if (Directory.Exists(baseDownloadsDir))
            {
                Directory.Delete(baseDownloadsDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting downloads root folder: {ex.Message}");
        }
    }

    private static void DeleteDownloadedFiles(DownloadItem item)
    {
        // Prefer deleting the explicit download folder if set.
        // This handles cases where individual file paths are missing or stale.
        try
        {
            var baseDownloadsDir = GetDownloadsRootFolder();

            if (
                !string.IsNullOrWhiteSpace(item.DownloadPath) && Directory.Exists(item.DownloadPath)
            )
            {
                Directory.Delete(item.DownloadPath, recursive: true);
                Debug.WriteLine($"Deleted app folder: {item.DownloadPath}");

                // Cleanup empty downloads root if needed
                if (
                    Directory.Exists(baseDownloadsDir)
                    && !Directory.EnumerateFileSystemEntries(baseDownloadsDir).Any()
                )
                {
                    Directory.Delete(baseDownloadsDir, recursive: false);
                }
                return;
            }

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
                    && parent.FullName.Equals(
                        baseDownloadsDir,
                        StringComparison.OrdinalIgnoreCase
                    )
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
