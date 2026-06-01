using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class Advanced_SearchPage : Page
{
    public Advanced_SearchViewModel ViewModel { get; }
    private readonly ILocaleService _localeService;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _loadingDotsCts;

    [GeneratedRegex(@".+/([^/?]+)(?:\?|$)")]
    private static partial Regex ProductIdFromUrlRegex();

    public Advanced_SearchPage()
    {
        ViewModel = App.GetService<Advanced_SearchViewModel>();
        _localeService = App.GetService<ILocaleService>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.ShowInfoCards = true;
        ViewModel.HasError = false;
        ViewModel.IsLoading = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Cancel any in-flight search and the loading-dots loop. The dots loop re-enqueues a
        // closure onto the DispatcherQueue every 500ms, which keeps this page alive until
        // cancelled — navigation does not otherwise cancel it.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = null;

        // Sever this transient page's x:Bind subscriptions to the singleton ViewModel,
        // which would otherwise root the page forever. Matches the MainPage/SearchPage pattern.
        Bindings.StopTracking();
    }

    private void SearchTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box)
            ViewModel.SelectedTypeIndex = box.SelectedIndex;

        ViewModel.HasError = false;
        ViewModel.ErrorMessage = string.Empty;
        ViewModel.ShowInfoCards = true;
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _ = ExecuteSearchAsync();
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExecuteSearchAsync();
    }

    private async Task ExecuteSearchAsync()
    {
        string query = QueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ViewModel.ShowInfoCards = false;
            ViewModel.HasError = true;
            ViewModel.ErrorMessage = "AdvancedSearch_Error_EmptyQuery".GetLocalized();
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        ViewModel.IsLoading = true;
        ViewModel.HasError = false;
        ViewModel.ShowInfoCards = false;

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = new CancellationTokenSource();
        LoadingDotsText.Text = ".";
        _ = AnimateLoadingDotsAsync(_loadingDotsCts.Token);

        try
        {
            string? productId = await ResolveProductIdAsync(query, ViewModel.SelectedSearchType, token);
            if (productId == null)
                return;

            var product = await Utils.ProductOrBundle(productId, InstallerType.Unknown, token, _localeService.Market, _localeService.Language);
            var frame = App.GetService<INavigationService>().Frame;
            QueryBox.Text = string.Empty;
            if (product.IsBundle)
                frame?.Navigate(typeof(BundlesPage), (product.ProductInfo, product.BundleInfo));
            else
                frame?.Navigate(typeof(AppPage), product.ProductInfo);
        }
        catch (OperationCanceledException)
        {
            // silently ignored
        }
        catch (Exception ex)
        {
            ViewModel.HasError = true;
            ViewModel.ErrorMessage = ex.Message;
        }
        finally
        {
            _loadingDotsCts?.Cancel();
            _loadingDotsCts?.Dispose();
            _loadingDotsCts = null;

            if (!token.IsCancellationRequested)
                ViewModel.IsLoading = false;
        }
    }

    private async Task AnimateLoadingDotsAsync(CancellationToken ct)
    {
        string[] frames = [".", "..", "..."];
        int i = 1;
        try
        {
            while (true)
            {
                await Task.Delay(500, ct);
                var frame = frames[i % frames.Length];
                DispatcherQueue.TryEnqueue(() => LoadingDotsText.Text = frame);
                i++;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string?> ResolveProductIdAsync(string query, SearchType type, CancellationToken token)
    {
        switch (type)
        {
            case SearchType.Url:
            {
                var match = ProductIdFromUrlRegex().Match(query);
                if (!match.Success)
                {
                    ViewModel.IsLoading = false;
                    ViewModel.HasError = true;
                    ViewModel.ErrorMessage = "AdvancedSearch_Error_InvalidUrl".GetLocalized();
                    return null;
                }
                return match.Groups[1].Value;
            }

            case SearchType.ProductId:
                return query;

            case SearchType.PackageFamilyName:
            {
                var localeService = _localeService;
                var result = await StoreEdgeFDProduct.GetProductsByIdTypeAsync(
                    [query],
                    StoreIdType.PackageFamilyName,
                    DeviceFamily.Desktop,
                    localeService.Market,
                    localeService.Language,
                    token
                );

                if (!result.IsSuccess || result.Value.Count == 0)
                {
                    ViewModel.IsLoading = false;
                    ViewModel.HasError = true;
                    ViewModel.ErrorMessage = result.IsSuccess
                        ? "AdvancedSearch_Error_NoPfnResult".GetLocalized()
                        : string.Format("AdvancedSearch_Error_FailedLookup".GetLocalized(), result.Exception.Message);
                    return null;
                }

                return result.Value[0].ProductId;
            }

            default:
                return query;
        }
    }
}
