using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using StoreListings.Library;
using test.Helpers;
using test.Models;

namespace test.Services;

public static class UpdateCheckService
{
    private const int LOOKUP_BATCH_SIZE = 5;
    private const int INSTALL_BATCH_SIZE = 5;

    public static async Task<List<UpdateItem>> CheckForUpdatesAsync(
        IProgress<(int completed, int total)>? progress,
        CancellationToken ct
    )
    {
        var installedApps = PackagedAppDiscovery.GetAllInstalledStoreApps();

        var pfns = installedApps
            .Select(a => a.PackageFamilyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<UpdateItem>();
        int completed = 0;
        int total = pfns.Count;

        var batches = pfns.Select((pfn, i) => (pfn, i))
            .GroupBy(x => x.i / LOOKUP_BATCH_SIZE)
            .Select(g => g.Select(x => x.pfn).ToList());

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            Result<List<StoreEdgeFDProduct>> batchResult;
            try
            {
                batchResult = await StoreEdgeFDProduct.GetProductsByIdTypeAsync(
                    batch,
                    StoreIdType.PackageFamilyName,
                    DeviceFamily.Desktop,
                    Market.US,
                    Lang.en,
                    ct
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCheckService] Batch lookup failed: {ex.Message}");
                completed += batch.Count;
                progress?.Report((completed, total));
                continue;
            }

            if (!batchResult.IsSuccess)
            {
                completed += batch.Count;
                progress?.Report((completed, total));
                continue;
            }

            var products = batchResult.Value;
            int processedInBatch = 0;

            foreach (var product in products)
            {
                ct.ThrowIfCancellationRequested();

                if (product.InstallerType != InstallerType.Packaged)
                {
                    processedInBatch++;
                    completed++;
                    progress?.Report((completed, total));
                    continue;
                }

                string? storeVersion = null;
                try
                {
                    storeVersion = await VersionCheckService.GetLatestVersionAsync(
                        product.ProductId,
                        product.InstallerType,
                        ct
                    );
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[UpdateCheckService] Version check failed for {product.ProductId}: {ex.Message}"
                    );
                }

                string? pfn = product.PackageFamilyName;
                string? installedVersion = !string.IsNullOrWhiteSpace(pfn)
                    ? PackagedAppDiscovery.GetInstalledVersion(pfn)
                    : null;

                if (VersionComparison.IsStoreNewer(storeVersion, installedVersion))
                {
                    results.Add(
                        new UpdateItem
                        {
                            PackageFamilyName = pfn ?? string.Empty,
                            ProductId = product.ProductId,
                            Title = product.Title,
                            LogoUrl = product.Logo?.Url,
                            PublisherName = product.PublisherName,
                            InstalledVersion = installedVersion,
                            StoreVersion = storeVersion!,
                            RevisionId = product.RevisionId,
                            IsBundle = product.IsBundle,
                        }
                    );
                }

                processedInBatch++;
                completed++;
                progress?.Report((completed, total));
            }

            // Account for PFNs in the batch that returned no product
            int unmatched = batch.Count - processedInBatch;
            if (unmatched > 0)
            {
                completed += unmatched;
                progress?.Report((completed, total));
            }
        }

        return results;
    }

    public static async Task UpdateAppsAsync(
        IReadOnlyList<UpdateItem> items,
        DispatcherQueue dispatcher,
        CancellationToken ct,
        Action<UpdateItem> onItemCompleted
    )
    {
        var downloadManager = DownloadManagerService.Instance;
        var queue = items.ToList();

        for (int batchStart = 0; batchStart < queue.Count; batchStart += INSTALL_BATCH_SIZE)
        {
            ct.ThrowIfCancellationRequested();

            var batch = queue.Skip(batchStart).Take(INSTALL_BATCH_SIZE).ToList();

            // Mark items not yet started as Pending
            var pending = queue.Skip(batchStart + INSTALL_BATCH_SIZE).ToList();
            foreach (var p in pending)
            {
                downloadManager.RunOnUIThread(() => p.Status = DownloadStatus.Pending);
            }

            await Task.WhenAll(
                batch.Select(item =>
                    ProcessItemAsync(item, dispatcher, ct, downloadManager, onItemCompleted)
                )
            );
        }
    }

    private static async Task ProcessItemAsync(
        UpdateItem item,
        DispatcherQueue dispatcher,
        CancellationToken ct,
        DownloadManagerService downloadManager,
        Action<UpdateItem> onItemCompleted
    )
    {
        var productData = new ProductData
        {
            ProductId = item.ProductId,
            Title = item.Title,
            Description = string.Empty,
            PublisherName = item.PublisherName,
            Logo = new StoreListings.Library.Image(
                item.LogoUrl ?? string.Empty,
                "Transparent",
                0,
                0
            ),
            Screenshots = [],
            Rating = 0,
            RatingCount = 0,
            InstallerType = InstallerType.Packaged,
            RevisionId = item.RevisionId,
            PackageFamilyName = item.PackageFamilyName,
            IsBundle = item.IsBundle,
        };

        var downloadItem = downloadManager.AddDownload(productData);

        var unsubscribeDownloadItem = SubscribeToDownloadItem(item, downloadItem, downloadManager);

        // Show indeterminate bar with animated dots while fetching the download URL.
        // DownloadHelper.StartDownloadAsync switches status to Downloading and starts
        // its own animator once bytes start flowing.
        downloadManager.UpdateDownloadStatus(item.ProductId, DownloadStatus.Pending);
        var pendingAnimator = new DownloadItemStatusAnimator(dispatcher);
        pendingAnimator.Start(downloadItem, "Fetching download URLs");

        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        downloadManager.RegisterCancellationToken(item.ProductId, itemCts);

        try
        {
            var fileEntry = await GetDownloadUrl.fetch(
                item.ProductId,
                InstallerType.Packaged,
                itemCts.Token
            );

            // Stop pending animator before StartDownloadAsync starts its own.
            pendingAnimator.Stop(downloadItem);

            if (fileEntry == null)
            {
                downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Failed);
                return;
            }

            var uiUpdateService = new UIUpdateService(dispatcher);
            await DownloadHelper.StartDownloadAsync(
                fileEntry,
                item.ProductId,
                itemCts.Token,
                uiUpdateService
            );
        }
        catch (OperationCanceledException)
        {
            downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[UpdateCheckService] Update failed for {item.ProductId}: {ex.Message}"
            );
            downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Failed);
        }
        finally
        {
            pendingAnimator.Stop(downloadItem); // no-op if already stopped above
            downloadManager.UnregisterCancellationToken(item.ProductId);
            itemCts.Dispose();
            unsubscribeDownloadItem();
        }

        // Enqueue the completion callback on the UI thread so that item.Status is guaranteed
        // to be current — all prior RunOnUIThread status updates are FIFO-queued before this one.
        downloadManager.RunOnUIThread(() => onItemCompleted(item));
    }

    private static Action SubscribeToDownloadItem(
        UpdateItem updateItem,
        DownloadItem downloadItem,
        DownloadManagerService downloadManager
    )
    {
        PropertyChangedEventHandler handler = (sender, e) =>
        {
            if (sender is not DownloadItem di)
                return;

            switch (e.PropertyName)
            {
                case nameof(DownloadItem.Status):
                    downloadManager.RunOnUIThread(() => updateItem.Status = di.Status);
                    break;
                case nameof(DownloadItem.Progress):
                    downloadManager.RunOnUIThread(() => updateItem.Progress = di.Progress);
                    break;
                case nameof(DownloadItem.StatusTextOverride):
                    downloadManager.RunOnUIThread(() =>
                        updateItem.StatusTextOverride = di.StatusTextOverride
                    );
                    break;
                case nameof(DownloadItem.DisplayDetailsText):
                    downloadManager.RunOnUIThread(() =>
                        updateItem.DisplayDetailsText = di.DisplayDetailsText
                    );
                    break;
            }
        };

        downloadItem.PropertyChanged += handler;
        return () => downloadItem.PropertyChanged -= handler;
    }
}
