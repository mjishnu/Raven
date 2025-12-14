using System.Diagnostics;
using Downloader;
using test.Models;
using test.Services;

namespace test.Helpers;

public sealed class DownloadHelper
{
    public static async Task StartDownloadAsync(
        FileEntry entry,
        string productId,
        CancellationToken token,
        UIUpdateService updateService
    )
    {
        const int THROTTLE_MS = 500;
        var reporter = updateService.GetReporter();
        var downloadManager = DownloadManagerService.Instance;

        // Per-file progress tracking state
        int lastWholePercent = -1;
        long lastReportTicks = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var config = new DownloadConfiguration
        {
            ChunkCount = 4, // Reduced from 8 - some servers throttle too many connections
            ParallelDownload = true,
            Timeout = 30000, // 60 seconds - longer timeout to prevent premature chunk failures
            ParallelCount = 2, // Reduced to 2 for better server compatibility
            BufferBlockSize = 8192, // 8KB buffer - standard size for better compatibility
            MaximumBytesPerSecond = 0, // No speed limit
            MinimumSizeOfChunking = 1024, // 1KB minimum - create chunks only for larger files
            ReserveStorageSpaceBeforeStartingDownload = true, // Pre-allocate disk space
        };

        var svc = new DownloadService(config);

        // Bridge external cancellation into the download service
        using var cancellationRegistration = token.Register(() =>
        {
            try
            {
                updateService.StartStatusAnimation("Cancelling");
                svc.CancelAsync();
            }
            catch
            {
                // ignore
            }
        });

        // Only handle progress here; reset state per file in the loop below
        svc.DownloadProgressChanged += (s, e) =>
        {
            int whole = (int)e.ProgressPercentage;
            long now = stopwatch.ElapsedMilliseconds;

            // Only emit when progress moves forward and throttle the UI updates
            if (whole > lastWholePercent)
            {
                if (now - lastReportTicks < THROTTLE_MS && whole != 100)
                    return;
                lastWholePercent = whole;
                lastReportTicks = now;

                double receivedMB = e.ReceivedBytesSize / (1024.0 * 1024.0);
                double totalMB = e.TotalBytesToReceive / (1024.0 * 1024.0);
                // Instrumentation: show instantaneous speed and active chunk count if available
                double mbPerSec = e.BytesPerSecondSpeed / (1024.0 * 1024.0);
                int activeChunks = e.ActiveChunks;

                reporter.Report(
                    new UIUpdate(
                        Progress: e.ProgressPercentage,
                        Details: $"{whole}% • {receivedMB:F1} / {totalMB:F0} MB"
                    )
                );

                // Update DownloadItem progress directly
                downloadManager.UpdateDownloadProgress(productId, e.ProgressPercentage);
            }
        };

        // Track per-file cancellation via completion event (some libs don't throw on cancel)
        bool currentFileCancelled = false;
        svc.DownloadFileCompleted += (s, e) =>
        {
            if (e.Cancelled)
                currentFileCancelled = true;
        };

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

        // Batch header: show total count and start a single continuous animation
        int totalFiles = flattened.Count;
        updateService.StartStatusAnimation(
            $"Downloading {totalFiles} file{(totalFiles == 1 ? string.Empty : "s")}"
        );
        reporter.Report(new UIUpdate(Progress: 0, Details: "Initializing..."));

        bool cancelled = false;
        bool hadError = false;

        for (int i = 0; i < flattened.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            updateService.StartStatusAnimation(
                $"Downloading ({i + 1}/{totalFiles}) file{(totalFiles == 1 ? string.Empty : "s")}"
            );

            var file = flattened[i];

            string destinationPath = Path.Combine(
                AppContext.BaseDirectory,
                "downloads",
                Path.GetFileName(file.FileName)
            );

            Debug.WriteLine(destinationPath);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                Directory.CreateDirectory(destinationDir);

            // Reset per-file progress tracking before starting each file
            lastWholePercent = -1;
            lastReportTicks = 0;
            stopwatch.Restart();
            currentFileCancelled = false;
            reporter.Report(new UIUpdate(Progress: 0));

            try
            {
                await svc.DownloadFileTaskAsync(file.Url, destinationPath, token);

                // If cancellation was requested during the download, stop the batch
                if (token.IsCancellationRequested || currentFileCancelled)
                {
                    cancelled = true;
                    break;
                }

                // Register the downloaded file path for tracking
                downloadManager.AddDownloadedFilePath(productId, destinationPath);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                hadError = true;
                reporter.Report(
                    new UIUpdate(
                        Status: $"Error: {ex.Message}",
                        Details: "Check network or disk space."
                    )
                );
                break;
            }
        }

        // End of batch: finalize UI
        updateService.StopStatusAnimation();

        if (cancelled)
        {
            reporter.Report(new UIUpdate(Status: "Download canceled.", Details: "User canceled."));
        }
        else if (!hadError)
        {
            reporter.Report(
                new UIUpdate(
                    Status: "All downloads completed successfully!",
                    Progress: 100,
                    Details: "Files saved."
                )
            );
        }
    }
}
