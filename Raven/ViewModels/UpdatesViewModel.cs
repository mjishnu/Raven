using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Models;
using Raven.Services;
using Raven.Views;
using StoreListings.Library;

namespace Raven.ViewModels;

public partial class UpdatesViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ILocaleService _localeService;

    public DispatcherQueue? DispatcherQueue { get; set; }

    [ObservableProperty]
    private ObservableCollection<UpdateItem> _availableUpdates = [];

    [ObservableProperty]
    private ObservableCollection<UpdateItem> _completedUpdates = [];

    [ObservableProperty]
    private ObservableCollection<DownloadItem> _failedUpdates = [];

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isUpdating;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _checkingProgressBase = string.Empty;

    [ObservableProperty]
    private string _checkingProgressDots = string.Empty;

    [ObservableProperty]
    private double _checkingProgress;

    public bool HasUpdates => AvailableUpdates.Count > 0;
    public bool HasCompletedUpdates => CompletedUpdates.Count > 0;
    public bool HasFailedUpdates => FailedUpdates.Count > 0;
    public bool ShowUpdatesList => (HasUpdates || HasCompletedUpdates || HasFailedUpdates) && !IsChecking;
    public bool ShowEmptyState => !HasUpdates && !HasCompletedUpdates && !IsChecking && !IsUpdating;
    public bool IsAllSelected
    {
        get
        {
            var selectable = AvailableUpdates.Where(x => !x.IsProgressVisible).ToList();
            return selectable.Count > 0 && selectable.All(x => x.IsSelected);
        }
    }

    public string ButtonText =>
        IsChecking ? "Updates_ButtonChecking".GetLocalized()
        : SelectedCount > 0 ? "Updates_ButtonUpdate".GetLocalizedFormat(SelectedCount)
        : "Updates_ButtonCheck".GetLocalized();

    public bool ButtonEnabled => !IsChecking && (!IsUpdating || SelectedCount > 0);

    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _updateCts;
    private volatile string _checkingBaseText = "Updates_CheckingBase".GetLocalized();

    private static readonly string CompletedUpdatesPath = Path.Combine(
        Path.GetTempPath(),
        "raven_completed_updates.json"
    );

    public UpdatesViewModel(INavigationService navigationService, ILocaleService localeService)
    {
        _navigationService = navigationService;
        _localeService = localeService;

        AvailableUpdates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(IsAllSelected));
            OnPropertyChanged(nameof(ShowUpdatesList));
        };

        CompletedUpdates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCompletedUpdates));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowUpdatesList));
        };

        FailedUpdates.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFailedUpdates));
            OnPropertyChanged(nameof(ShowUpdatesList));
            OnPropertyChanged(nameof(ShowEmptyState));
        };

        DownloadManagerService.Instance.Downloads.CollectionChanged += OnDownloadsChanged;
        foreach (var download in DownloadManagerService.Instance.Downloads)
        {
            download.PropertyChanged += OnDownloadItemPropertyChanged;
            if (download.LastInstallError != null && download.IsFromUpdateFlow)
                FailedUpdates.Add(download);
        }
    }

    private void OnDownloadsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (DownloadItem item in e.NewItems)
            {
                item.PropertyChanged += OnDownloadItemPropertyChanged;
                if (item.LastInstallError != null && item.IsFromUpdateFlow && !FailedUpdates.Contains(item))
                    DispatcherQueue?.TryEnqueue(() => FailedUpdates.Add(item));
            }
        }
        if (e.OldItems != null)
        {
            foreach (DownloadItem item in e.OldItems)
            {
                item.PropertyChanged -= OnDownloadItemPropertyChanged;
                DispatcherQueue?.TryEnqueue(() => FailedUpdates.Remove(item));
            }
        }
    }

    private void OnDownloadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DownloadItem item && e.PropertyName == nameof(DownloadItem.LastInstallError))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (item.LastInstallError != null && item.IsFromUpdateFlow && !FailedUpdates.Contains(item))
                    FailedUpdates.Add(item);
                else if (item.LastInstallError == null)
                    FailedUpdates.Remove(item);
            });
        }
    }

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonEnabled));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowUpdatesList));
    }

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonEnabled));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonEnabled));
    }

    [RelayCommand]
    private async Task CheckForUpdatesOrUpdate()
    {
        if (SelectedCount > 0 && !IsChecking)
            await UpdateSelectedAsync();
        else if (!IsUpdating)
            await CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void CancelCheck()
    {
        _checkCts?.Cancel();
    }

    [RelayCommand]
    private void ClearAllFailedUpdates()
    {
        var items = FailedUpdates.ToList();
        foreach (var item in items)
        {
            DownloadManagerService.Instance.ClearDownloadError(item.ProductId);
        }
    }

    [RelayCommand]
    private void ClearFailedUpdate(DownloadItem item)
    {
        DownloadManagerService.Instance.ClearDownloadError(item.ProductId);
    }

    public void ClearFailedUpdateByProductId(string productId)
    {
        DownloadManagerService.Instance.ClearDownloadError(productId);
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool allSelected = IsAllSelected;
        foreach (var item in AvailableUpdates.Where(x => !x.IsProgressVisible))
            item.IsSelected = !allSelected;
        RecalculateSelectedCount();
    }

    private async Task CheckForUpdatesAsync()
    {
        Debug.WriteLine("[Updates] CheckForUpdatesAsync START");
        
        foreach (var item in AvailableUpdates)
            item.PropertyChanged -= OnUpdateItemPropertyChanged;
        AvailableUpdates.Clear();

        // Clear the Failed Updates banner visually without removing the persisted errors
        // By setting IsFromUpdateFlow to false, they are automatically removed from FailedUpdates
        foreach (var failedItem in FailedUpdates.ToList())
        {
            failedItem.IsFromUpdateFlow = false;
        }
        FailedUpdates.Clear();

        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();
        var cts = _checkCts;
        IsChecking = true;

        // Set text immediately — no waiting for the first HTTP response.
        // Dots initialise with punctuation spaces (U+2008) so the width is
        // already the same as "..." before the first tick fires.
        _checkingBaseText = "Updates_CheckingBase".GetLocalized();
        CheckingProgressBase = "Updates_CheckingBase".GetLocalized();
        CheckingProgressDots = ".";
        var dotsCts = CancellationTokenSource.CreateLinkedTokenSource(_checkCts.Token);
        _ = AnimateCheckingDotsAsync(dotsCts.Token);

        var progress = new Progress<(int completed, int total)>(p =>
        {
            // Update base text immediately; animation keeps running with dots.
            _checkingBaseText = "Updates_CheckingProgress".GetLocalizedFormat(p.completed, p.total);
            CheckingProgressBase = _checkingBaseText;
            CheckingProgress = p.total > 0 ? (double)p.completed / p.total * 100.0 : 0;
        });

        List<UpdateItem> updates = [];

        try
        {
            updates = await UpdateCheckService.CheckForUpdatesAsync(progress, _checkCts.Token, _localeService.Market, _localeService.Language);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Updates] Check cancelled");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updates] Check EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            dotsCts.Cancel();
            dotsCts.Dispose();

            for (int i = 0; i < updates.Count; i++)
            {
                var item = updates[i];
                AvailableUpdates.Add(item);
                item.PropertyChanged += OnUpdateItemPropertyChanged;
            }

            IsChecking = false;
            CheckingProgressBase = string.Empty;
            CheckingProgressDots = string.Empty;
            RecalculateSelectedCount();
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(ShowEmptyState));
        });
    }

    private async Task AnimateCheckingDotsAsync(CancellationToken ct)
    {
        // The dots TextBlock has a fixed Width in XAML so the layout slot never
        // changes size — plain dot strings are sufficient, no width-padding tricks needed.
        string[] frames = [".", "..", "..."];
        int i = 1; // frame 0 "." is already shown synchronously before this runs
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                var frame = frames[i % frames.Length];
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (IsChecking)
                        CheckingProgressDots = frame;
                });
                i++;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task UpdateSelectedAsync()
    {
        // Only queue items that are not already being processed.
        var toUpdate = AvailableUpdates.Where(x => x.IsSelected && !x.IsProgressVisible).ToList();
        if (toUpdate.Count == 0)
            return;

        foreach (var updateItem in toUpdate)
        {
            ClearFailedUpdateByProductId(updateItem.ProductId);
        }

        bool freshStart = !IsUpdating;
        IsUpdating = true;

        if (freshStart)
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource();
        }
        else
        {
            // Reuse the existing CTS so new items join the current cancellation scope
            // without interrupting already-running downloads.
            _updateCts ??= new CancellationTokenSource();
        }

        try
        {
            await UpdateCheckService.UpdateAppsAsync(
                toUpdate,
                DispatcherQueue!,
                _updateCts.Token,
                OnUpdateItemCompleted,
                _localeService.Market,
                _localeService.Language
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            // Reset any items stuck as Pending.
            foreach (var item in toUpdate.Where(x => x.Status == DownloadStatus.Pending))
            {
                item.Status = null;
                item.Progress = 0;
                item.StatusTextOverride = null;
                item.DisplayDetailsText = string.Empty;
                item.IsSelected = false;
            }

            RecalculateSelectedCount();

            // Clear the updating flag only once no items remain active.
            if (!AvailableUpdates.Any(x => x.IsProgressVisible))
            {
                IsUpdating = false;
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    private void OnUpdateItemCompleted(UpdateItem item)
    {
        // Called on the UI thread via RunOnUIThread in ProcessItemAsync.
        if (item.Status == DownloadStatus.Completed)
        {
            item.PropertyChanged -= OnUpdateItemPropertyChanged;
            AvailableUpdates.Remove(item);
            CompletedUpdates.Insert(0, item);
            PersistCompletedUpdates();
        }
        else
        {
            // Cancelled or Failed: reset state and keep in the available list for retry.
            item.Status = null;
            item.Progress = 0;
            item.StatusTextOverride = null;
            item.DisplayDetailsText = string.Empty;
            item.IsSelected = false;
            RecalculateSelectedCount();
        }
    }

    private void OnUpdateItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateItem.IsSelected) &&
            e.PropertyName != nameof(UpdateItem.IsProgressVisible))
            return;

        // When an item transitions to a terminal non-progress state (Cancelled/Failed)
        // its checkbox re-enables before OnUpdateItemCompleted resets IsSelected.
        // Pre-emptively uncheck here so the checkbox never briefly shows
        // as enabled+checked, and so RecalculateSelectedCount below gets the right tally.
        if (sender is UpdateItem { IsSelected: true } item &&
            !item.IsProgressVisible &&
            item.Status is DownloadStatus.Cancelled or DownloadStatus.Failed)
        {
            item.IsSelected = false;
        }

        RecalculateSelectedCount();
    }

    private void RecalculateSelectedCount()
    {
        SelectedCount = AvailableUpdates.Count(x => x.IsSelected && !x.IsProgressVisible);
        OnPropertyChanged(nameof(IsAllSelected));
    }

    private void LoadCompletedUpdates()
    {
        CompletedUpdates.Clear();

        try
        {
            if (!File.Exists(CompletedUpdatesPath))
                return;

            var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            var fileTime = File.GetLastWriteTime(CompletedUpdatesPath);
            if (fileTime < processStart)
            {
                File.Delete(CompletedUpdatesPath);
                return;
            }

            var json = File.ReadAllText(CompletedUpdatesPath);
            var entries = JsonSerializer.Deserialize<List<CompletedUpdateEntry>>(json);
            if (entries == null)
                return;

            foreach (var entry in entries)
            {
                CompletedUpdates.Add(
                    new UpdateItem
                    {
                        ProductId = entry.ProductId,
                        Title = entry.Title,
                        LogoUrl = entry.LogoUrl,
                        InstalledVersion = entry.InstalledVersion,
                        StoreVersion = entry.StoreVersion,
                        Status = DownloadStatus.Completed,
                    }
                );
            }
        }
        catch { }
    }

    private void PersistCompletedUpdates()
    {
        try
        {
            var entries = CompletedUpdates
                .Select(i => new CompletedUpdateEntry
                {
                    ProductId = i.ProductId,
                    Title = i.Title,
                    LogoUrl = i.LogoUrl,
                    InstalledVersion = i.InstalledVersion,
                    StoreVersion = i.StoreVersion,
                })
                .ToList();

            var json = JsonSerializer.Serialize(entries);
            File.WriteAllText(CompletedUpdatesPath, json);
        }
        catch { }
    }

    private sealed class CompletedUpdateEntry
    {
        public string ProductId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? LogoUrl { get; set; }
        public string InstalledVersion { get; set; } = string.Empty;
        public string StoreVersion { get; set; } = string.Empty;
    }
}
