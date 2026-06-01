using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Downloader;
using StoreListings.Library;
using Raven.Models;
using Raven.Services;

namespace Raven.Helpers;

public sealed class DownloadHelper
{
    private static async Task DownloadAsync(
        DownloadConfiguration config,
        string url,
        string destinationPath,
        CancellationToken token,
        Action<long>? onTotalBytesKnown = null,
        Action<long>? onBytesReceived = null,
        Action<double>? onProgressChanged = null
    )
    {
        using var svc = new DownloadService(config);

        long lastReceivedBytes = 0;
        svc.DownloadProgressChanged += (_, e) =>
        {
            if (e.TotalBytesToReceive > 0)
                onTotalBytesKnown?.Invoke(e.TotalBytesToReceive);

            // e.ReceivedBytesSize is cumulative bytes received.
            if (e.ReceivedBytesSize >= 0 && e.ReceivedBytesSize != lastReceivedBytes)
            {
                lastReceivedBytes = e.ReceivedBytesSize;
                onBytesReceived?.Invoke(e.ReceivedBytesSize);
            }

            onProgressChanged?.Invoke(e.ProgressPercentage);
        };

        await svc.DownloadFileTaskAsync(url, destinationPath, token).ConfigureAwait(false);
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024d;
        const double MB = KB * 1024d;
        const double GB = MB * 1024d;
        const double TB = GB * 1024d;

        if (bytes >= TB)
            return $"{bytes / TB:0.#} TB";
        if (bytes >= GB)
            return $"{bytes / GB:0.#} GB";
        if (bytes >= MB)
            return $"{bytes / MB:0.#} MB";
        if (bytes >= KB)
            return $"{bytes / KB:0.#} KB";
        return $"{bytes} B";
    }

    public static async Task StartDownloadAsync(
        FileEntry entry,
        string productId,
        CancellationToken token,
        UIUpdateService updateService,
        bool downloadOnly = false
    )
    {
        const int MAX_RETRIES_PER_FILE = 5;
        const int NO_PROGRESS_TIMEOUT_MS = 60_000;
        const int MAX_BACKOFF_MS = 30_000;

        // Simple throttle: update UI at most every 250ms
        const int UI_THROTTLE_MS = 250;

        var downloadManager = DownloadManagerService.Instance;
        var installLogger = App.GetService<ILogger<DownloadHelper>>();

        // Clear any leftover details from previous attempts
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        // Per-file progress tracking state
        int lastWholePercent = -1;
        long lastUIUpdateMs = 0;
        long lastProgressTicks = 0;
        long startTicks = Environment.TickCount64;

        var config = new DownloadConfiguration
        {
            // Best practice for large files: avoid up-front reservation / pre-allocation.
            // Pre-allocating multi-GB files can look like a hang due to long disk writes.
            ReserveStorageSpaceBeforeStartingDownload = false,

            // CRITICAL for large files: Disable parallel chunking.
            // With ParallelDownload=true, the library holds chunk data in memory before merging.
            // For a 2GB file with 2 chunks, that's 2x1GB buffers causing severe memory pressure.
            // Sequential download uses much less memory and avoids the merge step entirely.
            ParallelDownload = false,
            ChunkCount = 1,
            ParallelCount = 1,

            // Smaller buffer reduces memory footprint per download.
            // 64KB is a good balance between throughput and memory usage.
            BufferBlockSize = 64 * 1024,

            MaximumBytesPerSecond = 0,
        };

        static string SanitizeFolderName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(
                name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()
            ).Trim();

            // Avoid super-long paths.
            return cleaned.Length <= 80 ? cleaned : cleaned[..80];
        }

        var downloadItem = downloadManager.GetDownload(productId);
        if (downloadItem is null)
        {
            return;
        }

        var isUnpackaged = downloadItem.InstallerType == InstallerType.Unpackaged;

        // Ensure the Downloads list is in the correct phase.
        // AppPage sets Status=Pending during URL fetch; once we start transferring bytes we must be Downloading.
        downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Downloading);

        var animator = new DownloadItemStatusAnimator(updateService.DispatcherQueue);

        var appFolderName = SanitizeFolderName(downloadItem.Title);
        if (string.IsNullOrWhiteSpace(appFolderName))
            appFolderName = productId;

        var baseDownloadDir = Path.Combine(
            DownloadManagerService.GetDownloadsRootFolder(),
            appFolderName
        );
        downloadManager.UpdateDownloadPath(productId, baseDownloadDir);

        var depsDownloadDir = Path.Combine(baseDownloadDir, "Dependencies");

        // Track file index/total for consistent text in DownloadItem.StatusText
        int totalFiles = 1;
        int currentFileIndex = 1;
        string FilesLabel() => totalFiles == 1 ? "Download_File".GetLocalized() : "Download_Files".GetLocalized();

        // Flatten dependencies (dependencies first), skipping duplicates by URL
        var flattened = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(FileEntry node)
        {
            if (node.Dependencies != null)
            {
                foreach (var dep in node.Dependencies)
                    Visit(dep);
            }

            if (seen.Add(node.Url))
                flattened.Add(node);
        }

        Visit(entry);

        totalFiles = Math.Max(1, flattened.Count);
        currentFileIndex = 1;

        var mainUrl = entry.Url;

        var baseStatus = string.Format("Download_Status_DownloadingFiles".GetLocalized(), currentFileIndex, totalFiles, FilesLabel());

        // Single continuous animation for the page status (AppPage only)
        updateService.StartStatusAnimation(baseStatus);

        // Animated dots in the Downloads list
        animator.Start(downloadItem, baseStatus);

        bool cancelled = false;
        bool hadError = false;

        for (int i = 0; i < flattened.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            currentFileIndex = i + 1;

            baseStatus = string.Format("Download_Status_DownloadingFiles".GetLocalized(), currentFileIndex, totalFiles, FilesLabel());
            updateService.UpdateAnimatedStatusBase(baseStatus);
            animator.UpdateBase(downloadItem, baseStatus);

            var file = flattened[i];

            var isMain = file.Url.Equals(mainUrl, StringComparison.OrdinalIgnoreCase);
            var targetDir = isMain ? baseDownloadDir : depsDownloadDir;

            string destinationPath = Path.Combine(targetDir, Path.GetFileName(file.FileName));

            var localExists = File.Exists(destinationPath);
            var localMatches = false;

            string? expectedHash = null;
            bool HashMatches(string storedHash) =>
                !string.IsNullOrWhiteSpace(expectedHash)
                && string.Equals(
                    storedHash.Trim(),
                    expectedHash.Trim(),
                    StringComparison.OrdinalIgnoreCase
                );

            if (isUnpackaged)
                expectedHash = file.Sha256;
            else
                expectedHash = file.Digest;

            // Hash-based caching:
            // - Packaged: compare FE3 SHA1 digest (existing behavior)
            // - Unpackaged: compare SHA256 when provided
            if (isUnpackaged)
            {
                if (localExists && !string.IsNullOrWhiteSpace(file.Sha256))
                {
                    var storedHash = downloadItem
                        .DownloadedFiles.FirstOrDefault(f =>
                            string.Equals(
                                f.Path,
                                destinationPath,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        ?.Hash;

                    localMatches =
                        !string.IsNullOrWhiteSpace(storedHash) && HashMatches(storedHash);
                    if (localMatches)
                    {
                        downloadManager.AddDownloadedFilePath(productId, destinationPath);
                        continue;
                    }
                }
            }
            else if (localExists && !string.IsNullOrWhiteSpace(file.Digest))
            {
                var storedHash = downloadItem
                    .DownloadedFiles.FirstOrDefault(f =>
                        string.Equals(f.Path, destinationPath, StringComparison.OrdinalIgnoreCase)
                    )
                    ?.Hash;

                localMatches =
                    !string.IsNullOrWhiteSpace(storedHash)
                    && string.Equals(
                        storedHash.Trim(),
                        file.Digest.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    );

                if (localMatches)
                {
                    downloadManager.AddDownloadedFilePath(productId, destinationPath);
                    continue;
                }
            }

            Debug.WriteLine(destinationPath);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);

            bool downloaded = false;

            for (int attempt = 1; attempt <= MAX_RETRIES_PER_FILE; attempt++)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var attemptToken = attemptCts.Token;

                // Reset per-file progress tracking before starting each attempt
                lastWholePercent = -1;
                lastUIUpdateMs = 0;
                startTicks = Environment.TickCount64;
                lastProgressTicks = 0;

                // Show retry status if not first attempt
                if (attempt > 1)
                {
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        string.Format("Download_Status_Retry".GetLocalized(), attempt, MAX_RETRIES_PER_FILE)
                    );
                }

                using var cancellationRegistration = token.Register(() =>
                {
                    try
                    {
                        updateService.UpdateAnimatedStatusBase("Status_Cancelling".GetLocalized());
                        // Best-effort cancel; DownloadService is created per attempt in the actual download branch.
                    }
                    catch
                    {
                        // ignore
                    }
                });

                using var stallTimer = new System.Threading.Timer(
                    _ =>
                    {
                        try
                        {
                            var now = Environment.TickCount64;
                            var elapsed = now - startTicks;
                            var last = Interlocked.Read(ref lastProgressTicks);
                            if (elapsed - last >= NO_PROGRESS_TIMEOUT_MS)
                            {
                                downloadManager.UpdateDownloadDetailsText(
                                    productId,
                                    "Download_Status_NoProgress".GetLocalized()
                                );
                                attemptCts.Cancel();
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    },
                    null,
                    NO_PROGRESS_TIMEOUT_MS,
                    NO_PROGRESS_TIMEOUT_MS
                );

                // Cache item reference
                var cachedItem = downloadItem!;

                // Progress + cancellation detection are now handled by the concrete download implementation
                // (Downloader for full download, blockmap progress for delta).

                try
                {
                    // If we're about to (re)download/patch this file, clear any previously
                    // persisted hash. Otherwise a past successful hash can survive into a
                    // new partial/failed attempt and cause a false cache hit later.
                    downloadManager.ClearDownloadedFileHash(productId, destinationPath);
                    downloadManager.RemoveDownloadedFileEntry(productId, destinationPath);

                    var canUseBlockmapDelta =
                        !localMatches
                        && !string.IsNullOrWhiteSpace(file.BlockmapUrl)
                        && !string.IsNullOrWhiteSpace(file.BlockmapCabFileDigest);

                    // Delta helper is also used as the fallback full-download path when no local file exists yet.
                    // This ensures blockmap/cab cache is written to temp and allows retries to resume.
                    if (canUseBlockmapDelta)
                    {
                        using var http = new HttpClient();

                        var deltaProgress = new Progress<(long bytesDownloaded, long totalBytes)>(
                            p =>
                            {
                                long tickNow = Environment.TickCount64;

                                int wholePercent =
                                    p.totalBytes > 0
                                        ? (int)
                                            Math.Clamp(
                                                p.bytesDownloaded * 100 / p.totalBytes,
                                                0,
                                                100
                                            )
                                        : 0;
                                bool isComplete = wholePercent == 100;

                                if (
                                    !isComplete
                                    && tickNow - Volatile.Read(ref lastUIUpdateMs) < UI_THROTTLE_MS
                                )
                                    return;

                                lastWholePercent = wholePercent;
                                Volatile.Write(ref lastUIUpdateMs, tickNow);
                                Interlocked.Exchange(ref lastProgressTicks, tickNow - startTicks);

                                var receivedText = FormatBytes(p.bytesDownloaded);
                                var totalText = FormatBytes(p.totalBytes);
                                var detailsString =
                                    $"{wholePercent}% • {receivedText} / {totalText}";

                                if (!downloadManager.IsAnyoneObserving)
                                {
                                    cachedItem.SetProgressSilent(wholePercent);
                                    cachedItem.ReceivedBytes = p.bytesDownloaded;
                                    cachedItem.TotalBytes = p.totalBytes;
                                    cachedItem.SetDisplayDetailsTextSilent(detailsString);
                                    return;
                                }

                                downloadManager.RunOnUIThread(() =>
                                {
                                    cachedItem.Progress = wholePercent;
                                    cachedItem.ReceivedBytes = p.bytesDownloaded;
                                    cachedItem.TotalBytes = p.totalBytes;
                                    cachedItem.DisplayDetailsText = detailsString;
                                });
                            }
                        );

                        await DeltaDownloadHelper
                            .ApplyDeltaUsingBlockmapAsync(
                                http,
                                file.Url,
                                destinationPath,
                                file.BlockmapUrl!,
                                file.BlockmapCabFileDigest,
                                attemptToken,
                                deltaProgress
                            )
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var downloadConfig = new DownloadConfiguration
                        {
                            ReserveStorageSpaceBeforeStartingDownload =
                                config.ReserveStorageSpaceBeforeStartingDownload,
                            ParallelDownload = false,
                            ChunkCount = 1,
                            ParallelCount = 1,
                            BufferBlockSize = config.BufferBlockSize,
                            MaximumBytesPerSecond = config.MaximumBytesPerSecond,

                            // Cap the in-memory write-ahead buffer. Without this the library's
                            // memory queue is unbounded, so a fast network races ahead of the disk
                            // writer and accumulates most of the file in RAM — a spike that never
                            // drops after the download (lands on the LOH). 16 MB is ample headroom
                            // for a sequential, single-chunk download and keeps the footprint flat.
                            MaximumMemoryBufferBytes = 16 * 1024 * 1024,

                            // Do NOT enable live streaming: it makes the library retain the whole
                            // downloaded payload in memory to expose it as a MemoryStream. We only
                            // write to disk and read byte *counts* for progress — the live stream is
                            // never consumed, so enabling it would just hold ~the entire file in RAM.
                            EnableLiveStreaming = false,
                        };

                        await DownloadAsync(
                                downloadConfig,
                                file.Url,
                                destinationPath,
                                attemptToken,
                                totalBytes =>
                                {
                                    if (totalBytes > 0)
                                    {
                                        cachedItem.TotalBytes = totalBytes;
                                        downloadManager.UpdateDownloadBytes(
                                            productId,
                                            cachedItem.ReceivedBytes,
                                            cachedItem.TotalBytes
                                        );
                                    }
                                },
                                receivedBytes =>
                                {
                                    cachedItem.ReceivedBytes = receivedBytes;
                                    downloadManager.UpdateDownloadBytes(
                                        productId,
                                        cachedItem.ReceivedBytes,
                                        cachedItem.TotalBytes
                                    );
                                },
                                progress =>
                                {
                                    long tickNow = Environment.TickCount64;
                                    int wholePercent = (int)Math.Clamp(progress, 0, 100);
                                    bool isComplete = wholePercent == 100;

                                    if (
                                        !isComplete
                                        && tickNow - Volatile.Read(ref lastUIUpdateMs)
                                            < UI_THROTTLE_MS
                                    )
                                        return;

                                    lastWholePercent = wholePercent;
                                    Volatile.Write(ref lastUIUpdateMs, tickNow);
                                    Interlocked.Exchange(
                                        ref lastProgressTicks,
                                        tickNow - startTicks
                                    );

                                    var receivedText = FormatBytes(cachedItem.ReceivedBytes ?? 0);
                                    var totalText = FormatBytes(cachedItem.TotalBytes ?? 0);
                                    var detailsString =
                                        $"{wholePercent}% • {receivedText} / {totalText}";

                                    downloadManager.UpdateDownloadProgress(productId, wholePercent);
                                    downloadManager.UpdateDownloadDetailsText(
                                        productId,
                                        detailsString
                                    );
                                }
                            )
                            .ConfigureAwait(false);
                    }

                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    // Only persist the hash now that the file is confirmed on disk.
                    // Storing it earlier risks writing to JSON before completion,
                    // which would cause the integrity-cache check to skip
                    // re-downloading a partially written file.
                    var confirmedHash = isUnpackaged ? file.Sha256 : file.Digest;
                    downloadManager.AddDownloadedFile(productId, destinationPath, confirmedHash);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Stall timeout / per-attempt cancel: retry.
                    if (attempt < MAX_RETRIES_PER_FILE)
                    {
                        var delayMs = GetRetryDelayMs(null, attempt, MAX_BACKOFF_MS);
                            downloadManager.UpdateDownloadDetailsText(
                                productId,
                                string.Format("Download_Status_RestartingIn".GetLocalized(), delayMs / 1000.0)
                            );
                            await Task.Delay(delayMs, token).ConfigureAwait(false);
                            continue;
                        }

                    hadError = true;
                    downloadManager.UpdateDownloadStatusText(
                        productId,
                        "Download_Status_Stalled".GetLocalized()
                    );
                    break;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (WebException ex) when (attempt < MAX_RETRIES_PER_FILE)
                {
                    var delayMs = GetRetryDelayMs(ex, attempt, MAX_BACKOFF_MS);
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        string.Format("Download_Status_NetworkIssue".GetLocalized(), delayMs / 1000.0)
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MAX_RETRIES_PER_FILE)
                {
                    var delayMs = GetRetryDelayMs(null, attempt, MAX_BACKOFF_MS);
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        string.Format("Download_Status_RetryError".GetLocalized(), ex.Message, delayMs / 1000.0)
                    );
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    hadError = true;
                    downloadManager.UpdateDownloadStatusText(productId, string.Format("Download_Status_Error".GetLocalized(), ex.Message));
                    downloadManager.UpdateDownloadDetailsText(
                        productId,
                        "Download_Status_NetworkError".GetLocalized()
                    );
                    break;
                }
            }

            if (cancelled || hadError)
                break;

            if (!downloaded)
            {
                hadError = true;
                downloadManager.UpdateDownloadStatusText(
                    productId,
                    "Download_Status_FailedRetries".GetLocalized()
                );
                break;
            }
        }

        // End of batch: finalize UI
        updateService.StopStatusAnimation();
        animator.Stop(downloadItem);

        // Clear details so next phase (install) starts clean
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        if (cancelled)
        {
            downloadManager.UpdateDownloadStatusText(productId, "Download_Status_Canceled".GetLocalized());
            try
            {
                downloadManager.UpdateDownloadBytes(productId, null, null);
            }
            catch { }
            downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Cancelled);
            return;
        }

        if (hadError)
        {
            try
            {
                downloadManager.UpdateDownloadBytes(productId, null, null);
            }
            catch { }
            return;
        }

        // Mark download phase complete
        downloadManager.UpdateDownloadProgress(productId, 100);
        downloadManager.UpdateDownloadDetailsText(productId, string.Empty);

        // Persist the final download state.
        downloadManager.SaveDownloadsThrottled(force: true);

        // Download-only mode for packaged apps: skip install phase.
        if (downloadOnly || isUnpackaged)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, "Download_Status_Completed".GetLocalized());
            downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Completed);
            return;
        }

        // Begin install phase and reflect it in Downloads page.
        downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Installing);
        downloadManager.UpdateDownloadProgress(productId, 0);
        downloadManager.UpdateDownloadStatusText(productId, "Status_Installing".GetLocalized());
        try
        {
            downloadManager.UpdateDownloadBytes(productId, null, null);
        }
        catch { }
        updateService.StartStatusAnimation("Status_Installing".GetLocalized());

        // Animated dots in the Downloads list during install
        animator.Start(downloadItem, "Status_Installing".GetLocalized());

        var mainPackagePath = downloadItem
            .DownloadedFiles.Select(f => f.Path)
            .FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p)
                && string.Equals(
                    Path.GetFileName(p),
                    Path.GetFileName(entry.FileName),
                    StringComparison.OrdinalIgnoreCase
                )
            );

        if (string.IsNullOrWhiteSpace(mainPackagePath) || !File.Exists(mainPackagePath))
        {
            // Can't locate main package on disk; mark failed.
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(
                productId,
                "Download_Status_MissingPackage".GetLocalized()
            );
            downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Failed);
            return;
        }

        var depPaths = (downloadItem?.DownloadedFiles ?? [])
            .Select(f => f.Path)
            .Where(p =>
                !string.IsNullOrWhiteSpace(p)
                && File.Exists(p)
                && !string.Equals(p, mainPackagePath, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        try
        {
            // Throttle install progress updates similar to download progress
            int lastInstallPercent = -1;
            long lastInstallProgressMs = 0;
            const int INSTALL_PROGRESS_THROTTLE_MS = 100;

            var installProgress = new Progress<AppPackageInstaller.InstallProgress>(p =>
            {
                var percent = (int)Math.Clamp(p.Percent, 0, 100);

                // Throttle updates unless we hit 100%
                var now = Environment.TickCount64;
                if (percent != 100 && percent == lastInstallPercent)
                    return;
                if (percent != 100 && now - lastInstallProgressMs < INSTALL_PROGRESS_THROTTLE_MS)
                    return;

                lastInstallPercent = percent;
                lastInstallProgressMs = now;

                downloadManager.UpdateDownloadProgress(productId, percent);
            });

            await AppPackageInstaller.InstallAsync(
                mainPackagePath,
                dependencyPackagePaths: depPaths,
                progress: installProgress,
                cancellationToken: token
            );

            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            downloadManager.UpdateDownloadStatusText(productId, null);
            downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            var packageFamilyName = downloadItem.PackageFamilyName;
            if (IsPackageInstalled(packageFamilyName))
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(
                    productId,
                    Raven.Models.DownloadStatus.Completed
                );
            }
            else
            {
                downloadManager.UpdateDownloadStatusText(productId, null);
                downloadManager.UpdateDownloadStatus(
                    productId,
                    Raven.Models.DownloadStatus.Cancelled
                );
            }
        }
        catch (Exception ex)
        {
            animator.Stop(downloadItem);
            updateService.StopStatusAnimation();
            installLogger.LogError(
                ex,
                "Install failed in download flow | ProductId={ProductId} | MainPackage={MainPackagePath} | Dependencies={DependencyCount}",
                productId,
                mainPackagePath,
                depPaths.Count
            );
            downloadItem.LastInstallError = ex;
            downloadManager.UpdateDownloadStatusText(productId, string.Format("Download_Status_InstallFailed".GetLocalized(), ex.Message));
            downloadManager.UpdateDownloadStatus(productId, Raven.Models.DownloadStatus.Failed);
        }
    }

    private static bool IsPackageInstalled(string? packageFamilyName)
    {
        return PackagedAppDiscovery.IsInstalled(packageFamilyName);
    }

    private static int GetRetryDelayMs(WebException? webEx, int attempt, int maxBackoffMs)
    {
        int baseDelayMs = (int)Math.Min(maxBackoffMs, 1000 * Math.Pow(2, attempt - 1));

        if (webEx?.Response is HttpWebResponse resp)
        {
            // Honor Retry-After when present (common for 429/503).
            var retryAfter = resp.Headers["Retry-After"];
            if (int.TryParse(retryAfter, out var seconds) && seconds > 0)
                return (int)Math.Min(maxBackoffMs, seconds * 1000);
        }

        // Add small jitter to avoid thundering herd.
        return baseDelayMs + Random.Shared.Next(0, 500);
    }
}
