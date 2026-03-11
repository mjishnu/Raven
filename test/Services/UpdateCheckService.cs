using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
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

        var batchList = pfns
            .Select((pfn, i) => (pfn, i))
            .GroupBy(x => x.i / LOOKUP_BATCH_SIZE)
            .Select(g => g.Select(x => x.pfn).ToList())
            .ToList();

        // Bounded channel (capacity 1): the producer stays at most one StoreEdgeFD fetch
        // ahead of the consumer, so the next batch's network call overlaps with the current
        // batch's DCAT lookup and concurrent version checks.
        var channel = Channel.CreateBounded<(List<string> Batch, Result<List<StoreEdgeFDProduct>> Result)>(1);

        // Producer: issues one StoreEdgeFD lookup per PFN batch and writes the result to
        // the channel. Runs concurrently with the consumer.
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var batch in batchList)
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
                            Debug.WriteLine(
                                $"[UpdateCheckService] Batch lookup failed: {ex.Message}"
                            );
                            batchResult = Result<List<StoreEdgeFDProduct>>.Failure(ex);
                        }

                        await channel.Writer.WriteAsync((batch, batchResult), ct);
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            ct
        );

        // Consumer: reads each StoreEdgeFD result from the channel, performs a single DCAT
        // batch fetch for all packaged products, then does a soft version check (DCAT
        // AppVersion vs installed) — while the producer is already fetching the next batch.
        try
        {
            await foreach (var (batch, batchResult) in channel.Reader.ReadAllAsync(ct))
            {
                if (!batchResult.IsSuccess)
                {
                    completed += batch.Count;
                    progress?.Report((completed, total));
                    continue;
                }

                var products = batchResult.Value;

                // Batch-fetch DCAT packages for all packaged products in one request.
                var packagedIds = products
                    .Where(p => p.InstallerType == InstallerType.Packaged)
                    .Select(p => p.ProductId)
                    .ToList();

                var dcatLookup = new Dictionary<string, IEnumerable<StoreListings.Library.DCATPackage>>(
                    StringComparer.OrdinalIgnoreCase
                );
                if (packagedIds.Count > 0)
                {
                    try
                    {
                        var dcatResult =
                            await StoreListings.Library.DCATPackage.GetMultiplePackagesAsync(
                                packagedIds,
                                Market.US,
                                Lang.en,
                                true,
                                ct
                            );
                        if (dcatResult.IsSuccess)
                        {
                            foreach (var dcatProduct in dcatResult.Value)
                                dcatLookup[dcatProduct.ProductId] = dcatProduct.Packages;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[UpdateCheckService] DCAT batch lookup failed: {ex.Message}"
                        );
                    }
                }

                // Version checks are sequential to avoid overwhelming the FE3 endpoint.
                // Per-product progress is reported as each check completes so the UI
                // stays responsive. Non-packaged products are counted inline.
                foreach (var product in products)
                {
                    ct.ThrowIfCancellationRequested();

                    if (product.InstallerType != InstallerType.Packaged)
                    {
                        completed++;
                        progress?.Report((completed, total));
                        continue;
                    }

                    // Soft check: read the version directly from the DCAT catalog data
                    // that was already fetched above — no FE3 / Windows Update calls.
                    string? storeVersion = null;
                    if (dcatLookup.TryGetValue(product.ProductId, out var pkgs))
                    {
                        storeVersion = pkgs
                            .Where(p => p.AppVersion.HasValue)
                            .OrderByDescending(p => p.AppVersion!.Value)
                            .FirstOrDefault()
                            ?.AppVersion
                            ?.ToString();
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

                    completed++;
                    progress?.Report((completed, total));
                }

                // Account for PFNs in the batch that returned no product.
                int unmatched = batch.Count - products.Count;
                if (unmatched > 0)
                {
                    completed += unmatched;
                    progress?.Report((completed, total));
                }
            }
        }
        finally
        {
            // If the consumer exits early (e.g. cancellation), unblock any producer WriteAsync
            // and always observe the producer task to prevent unobserved exceptions.
            channel.Writer.TryComplete();
            try
            {
                await producer;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCheckService] Producer faulted: {ex.Message}");
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
                if (!downloadManager.IsCancellationRequested(p.ProductId))
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

        // Only show Pending and start the animator when the item hasn't already been
        // cancelled (e.g. cancel was clicked before this batch started).
        DownloadItemStatusAnimator? pendingAnimator = null;
        if (!downloadManager.IsCancellationRequested(item.ProductId))
        {
            downloadManager.UpdateDownloadStatus(item.ProductId, DownloadStatus.Pending);
            pendingAnimator = new DownloadItemStatusAnimator(dispatcher);
            pendingAnimator.Start(downloadItem, "Fetching download URLs");
        }

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
            pendingAnimator?.Stop(downloadItem);

            if (fileEntry == null)
            {
                downloadManager.UpdateDownloadStatus(item.ProductId, DownloadStatus.Failed);
                downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Failed);
            }
            else
            {
                var uiUpdateService = new UIUpdateService(dispatcher);
                await DownloadHelper.StartDownloadAsync(
                    fileEntry,
                    item.ProductId,
                    itemCts.Token,
                    uiUpdateService
                );
            }
        }
        catch (OperationCanceledException)
        {
            downloadManager.UpdateDownloadStatus(item.ProductId, DownloadStatus.Cancelled);
            downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[UpdateCheckService] Update failed for {item.ProductId}: {ex.Message}"
            );
            downloadManager.UpdateDownloadStatus(item.ProductId, DownloadStatus.Failed);
            downloadManager.RunOnUIThread(() => item.Status = DownloadStatus.Failed);
        }
        finally
        {
            pendingAnimator?.Stop(downloadItem); // no-op if already stopped above
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
