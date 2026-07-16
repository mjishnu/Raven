using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using StoreListings.Library;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.layouts;
using Windows.Foundation;

namespace Raven.Views.Shared;

public sealed partial class CardViewControl : UserControl
{
    #region Fields
    private Compositor _compositor;
    private SpringVector3NaturalMotionAnimation _springAnimation;
    private bool filterBtnActive = false;
    private bool isLoadingMore = false;
    private CancellationTokenSource? _loadingDotsCts;
    private CancellationTokenSource? _navigateCts;
    private readonly ILocaleService _localeService;
    private readonly ILogger<CardViewControl> _logger;

    // True while the list is scrolled to the bottom; lets a resize keep the end pinned.
    private bool _atEnd;

    // Suppresses scroll-position saves while we programmatically restore/anchor the view.
    private bool _restorePending;
    private bool _cleaned;

    private const double EndThreshold = 4.0;
    private const double LoadMoreThreshold = 200.0;
    #endregion

    #region Constructor
    public CardViewControl()
    {
        _localeService = App.GetService<ILocaleService>();
        _logger = App.GetService<ILogger<CardViewControl>>();
        InitializeComponent();
        // Classic ScrollViewer is available immediately after InitializeComponent (no deferred
        // template hookup like ItemsView.ScrollView), so subscribe directly.
        Scroller.ViewChanged += Scroller_ViewChanged;
        Scroller.SizeChanged += Scroller_SizeChanged;
        Loaded += CardViewControl_Loaded;
        Unloaded += CardViewControl_Unloaded;
    }
    #endregion

    #region Delegates
    // Delegate type for the LoadCards method
    public delegate Task LoadCardsDelegate();
    #endregion

    #region Dependency Properties

    public static readonly DependencyProperty LoadCardsMethodProperty = DependencyProperty.Register(
        nameof(LoadCardsMethod),
        typeof(LoadCardsDelegate),
        typeof(CardViewControl),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel),
        typeof(ICardViewModel),
        typeof(CardViewControl),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(CardViewControl),
        new PropertyMetadata(null)
    );

    public static readonly DependencyProperty ItemSourceFilter1Property =
        DependencyProperty.Register(
            nameof(ItemSourceFilter1),
            typeof(List<string>),
            typeof(CardViewControl),
            new PropertyMetadata(null)
        );

    public static readonly DependencyProperty ItemSourceFilter2Property =
        DependencyProperty.Register(
            nameof(ItemSourceFilter2),
            typeof(List<string>),
            typeof(CardViewControl),
            new PropertyMetadata(null)
        );

    public static readonly DependencyProperty SelectedFilter1Property = DependencyProperty.Register(
        nameof(SelectedFilterIndex1),
        typeof(int),
        typeof(CardViewControl),
        new PropertyMetadata(0)
    );

    public static readonly DependencyProperty SelectedFilter2Property = DependencyProperty.Register(
        nameof(SelectedFilterIndex2),
        typeof(int),
        typeof(CardViewControl),
        new PropertyMetadata(0)
    );

    public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register(
        nameof(HeaderText),
        typeof(string),
        typeof(CardViewControl),
        new PropertyMetadata(null)
    );
    #endregion

    #region Properties
    // Property for the LoadCards method
    public LoadCardsDelegate LoadCardsMethod
    {
        get => (LoadCardsDelegate)GetValue(LoadCardsMethodProperty);
        set => SetValue(LoadCardsMethodProperty, value);
    }
    public ICardViewModel ViewModel
    {
        get => (ICardViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public IEnumerable ItemsSource
    {
        get => (IEnumerable)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public List<string> ItemSourceFilter1
    {
        get => (List<string>)GetValue(ItemSourceFilter1Property);
        set => SetValue(ItemSourceFilter1Property, value);
    }

    public List<string> ItemSourceFilter2
    {
        get => (List<string>)GetValue(ItemSourceFilter2Property);
        set => SetValue(ItemSourceFilter2Property, value);
    }

    public int SelectedFilterIndex1
    {
        get => (int)GetValue(SelectedFilter1Property);
        set => SetValue(SelectedFilter1Property, value);
    }

    public int SelectedFilterIndex2
    {
        get => (int)GetValue(SelectedFilter2Property);
        set => SetValue(SelectedFilter2Property, value);
    }

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }
    #endregion

    #region Event Handlers
    private void Element_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Ensure compositor is initialized
        if (_compositor == null)
        {
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        }

        Utils.HandlePointerEntered(sender, e, ref _springAnimation, _compositor);
    }

    private void Element_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Ensure compositor is initialized
        if (_compositor == null)
        {
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        }

        Utils.HandlePointerExited(sender, e, ref _springAnimation, _compositor);
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is Card card)
        {
            NavigateToProductOrBundle(card.ProductId, card.InstallerType);
        }
        else
        {
            Debug.WriteLine("Failed to get card for navigation");
        }
    }

    private void FilterButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && filterBtnActive == false)
        {
            // Apply border without animation
            button.BorderThickness = new Thickness(1);
        }
    }

    private void FilterButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && filterBtnActive == false)
        {
            // Remove border without animation
            button.BorderThickness = new Thickness(0);
        }
    }

    #endregion


    public async Task InitialLoadCards()
    {
        Scroller.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;
        ViewModel.CurrentSkipItem = 0;
        ViewModel.FirstVisibleIndex = 0;
        _atEnd = false;
        ScrollToOffset(0);

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = new CancellationTokenSource();
        _ = AnimateLoadingDotsAsync(_loadingDotsCts.Token);

        var success = true;
        var errorText = "";
        Exception loadException = null;
        isLoadingMore = true;
        try
        {
            ViewModel.Cards.Clear();
            await LoadCardsMethod();
        }
        catch (Exception ex)
        {
            success = false;
            errorText = ex.Message;
            loadException = ex;
            _logger.LogError(
                ex,
                "Failed to load cards | ExceptionType={ExceptionType} | HResult=0x{HResult:X8} | Header={Header}",
                ex.GetType().FullName,
                ex.HResult,
                HeaderText
            );
        }
        finally
        {
            _loadingDotsCts?.Cancel();
            _loadingDotsCts?.Dispose();
            _loadingDotsCts = null;
        }

        // Control may have been unloaded while the await was in progress
        if (ViewModel == null)
        {
            isLoadingMore = false;
            return;
        }

        if (success == true)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            NoResultsPanel.Visibility =
                (ViewModel.Cards.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            
            if (loadException != null && IsNetworkError(loadException))
            {
                ErrorPanel.Glyph = "\uF384";
                ErrorPanel.Title = "No Internet Connection";
                ErrorPanel.Subtitle = "Please check your network settings and try again.";
            }
            else
            {
                ErrorPanel.Glyph = "\uEBA0";
                ErrorPanel.Title = "CardView_Error_LoadFailed".GetLocalizedFormat(errorText);
                ErrorPanel.Subtitle = string.Empty;
            }
        }
        isLoadingMore = false;
    }

    public async Task LoadMoreCards()
    {
        if (isLoadingMore || !ViewModel.HasMoreItems)
        {
            return;
        }

        isLoadingMore = true;
        LoadMoreIndicator.Visibility = Visibility.Visible;
        ViewModel.CurrentSkipItem += 25;
        try
        {
            await LoadCardsMethod();
        }
        catch (Exception ex)
        {
            // Handle error silently or show a small notification
            Debug.WriteLine($"Error loading more cards: {ex.Message}");
        }
        finally
        {
            LoadMoreIndicator.Visibility = Visibility.Collapsed;
            isLoadingMore = false;
        }
    }

    private void FilterBtn_Click(object sender, RoutedEventArgs e)
    {
        // Toggle FilterGrid visibility
        FilterPanel.Visibility =
            FilterPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

        filterBtnActive = !filterBtnActive;

        if (filterBtnActive == true)
        {
            FilterBtn.Background = (SolidColorBrush)Resources["SimpleCardBackgroundBrush"];

            FilterBtn.BorderThickness = new Thickness(1);

            // Create rotation animation for the arrow
            var rotateAnimation = new DoubleAnimation
            {
                From = 0,
                To = 180,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            };

            // Ensure the transform is set up
            if (FilterBtnArrow.RenderTransform is not RotateTransform)
            {
                FilterBtnArrow.RenderTransform = new RotateTransform();
                FilterBtnArrow.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            // Start the animation
            Storyboard.SetTarget(rotateAnimation, FilterBtnArrow);
            Storyboard.SetTargetProperty(
                rotateAnimation,
                "(UIElement.RenderTransform).(RotateTransform.Angle)"
            );

            var storyboard = new Storyboard();
            storyboard.Children.Add(rotateAnimation);
            storyboard.Begin();

            // Change arrow to point upward when filter is active
            FilterBtnArrow.Glyph = "\uE972"; // Keep the same glyph, just rotate it
        }
        else
        {
            FilterBtn.BorderThickness = new Thickness(0);
            FilterBtn.Background = new SolidColorBrush(Colors.Transparent);

            // Create rotation animation for the arrow
            var rotateAnimation = new DoubleAnimation
            {
                From = 180,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            };

            // Ensure the transform is set up
            if (FilterBtnArrow.RenderTransform is not RotateTransform)
            {
                FilterBtnArrow.RenderTransform = new RotateTransform();
                FilterBtnArrow.RenderTransformOrigin = new Point(0.5, 0.5);
            }

            // Start the animation
            Storyboard.SetTarget(rotateAnimation, FilterBtnArrow);
            Storyboard.SetTargetProperty(
                rotateAnimation,
                "(UIElement.RenderTransform).(RotateTransform.Angle)"
            );

            var storyboard = new Storyboard();
            storyboard.Children.Add(rotateAnimation);
            storyboard.Begin();

            // Use the same glyph, animation will handle visual change
            FilterBtnArrow.Glyph = "\uE972";
        }
    }

    private void CardViewControl_Loaded(object sender, RoutedEventArgs e)
    {
        // Returning to a cached page: restore the previous scroll position by item index.
        if (ViewModel?.HasCachedResults == true)
        {
            RestoreScrollPosition();
        }
    }

    // Repeater's top within the scroll content (= header height, incl. expanded filter panel).
    // The ScrollViewer scrolls header + cards, so every card-row offset is shifted by this.
    private double GetRepeaterTop()
    {
        try
        {
            return CardRepeater.TransformToVisual(ScrollContent).TransformPoint(new Point(0, 0)).Y;
        }
        catch
        {
            return 0;
        }
    }

    private async void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_restorePending || ViewModel == null)
            return;

        double offset = Scroller.VerticalOffset;
        double scrollable = Scroller.ScrollableHeight;

        _atEnd = scrollable > 0 && offset >= scrollable - EndThreshold;

        // Persist the position only when the view settles: FirstVisibleIndex is consumed
        // on navigation restore and resize anchoring, never per frame, and SaveScrollPosition
        // does TransformToVisual interop + DP reads that are wasted on intermediate ticks.
        // No offset>0 guard: settling at the exact top must save index 0, or a stale deep
        // index would be restored on the next visit (and re-anchored to on window resize).
        if (!e.IsIntermediate && !isLoadingMore)
        {
            SaveScrollPosition();
        }

        if (
            !isLoadingMore
            && ViewModel.HasMoreItems
            && scrollable > 0
            && offset >= scrollable - LoadMoreThreshold
        )
        {
            await LoadMoreCards();
            // If the fling settled while the load was in flight, the settled ViewChanged was
            // swallowed by the isLoadingMore gate above; capture the final position now.
            // (SaveScrollPosition no-ops if the control was cleaned up during the await.)
            if (!_restorePending && ViewModel != null && Scroller.VerticalOffset > 0)
                SaveScrollPosition();
        }
    }

    // Persist the first visible item's index rather than a pixel offset, so the position stays
    // correct after the column count reflows (window resize, snap, fullscreen).
    private void SaveScrollPosition()
    {
        if (ViewModel == null || CardRepeater.Layout is not VirtualGridLayout layout)
            return;

        double rowPitch = layout.RowPitch;
        if (rowPitch <= 0)
            return;

        int columns =
            layout.LastColumnCount > 0
                ? layout.LastColumnCount
                : layout.GetColumnCountForWidth(Scroller.ViewportWidth);
        // Subtract the header band: rows are measured from the repeater's top, not the viewport's.
        int firstRow = Math.Max(0, (int)((Scroller.VerticalOffset - GetRepeaterTop()) / rowPitch));
        ViewModel.FirstVisibleIndex = firstRow * Math.Max(1, columns);
    }

    private void RestoreScrollPosition()
    {
        if (ViewModel == null || ViewModel.FirstVisibleIndex <= 0)
            return;

        int index = ViewModel.FirstVisibleIndex;
        _restorePending = true;
        int attempts = 0;

        void Apply()
        {
            if (ViewModel == null)
            {
                _restorePending = false;
                return;
            }

            if (CardRepeater.Layout is not VirtualGridLayout layout || layout.RowPitch <= 0)
            {
                if (attempts++ < 10)
                {
                    DispatcherQueue.TryEnqueue(Apply);
                    return;
                }
                _restorePending = false;
                return;
            }

            int columns =
                layout.LastColumnCount > 0
                    ? layout.LastColumnCount
                    : Math.Max(1, layout.GetColumnCountForWidth(Scroller.ViewportWidth));
            double target = GetRepeaterTop() + (index / columns) * layout.RowPitch;

            // The uniform layout reports its full extent after the first measure; wait for it
            // so the target offset is reachable rather than being clamped short.
            if (Scroller.ScrollableHeight + 0.5 < target && attempts++ < 10)
            {
                DispatcherQueue.TryEnqueue(Apply);
                return;
            }

            ScrollToOffset(target);
            _restorePending = false;
        }

        DispatcherQueue.TryEnqueue(Apply);
    }

    // Keep the scroll anchored across responsive reflow: the column count (and therefore the
    // pixel offset of any item) changes with width, so a saved offset would drift. Re-derive it.
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel == null || CardRepeater.ItemsSource == null)
            return;

        bool wasAtEnd = _atEnd;
        int index = ViewModel.FirstVisibleIndex;
        if (!wasAtEnd && index <= 0)
            return; // already at the top — nothing to preserve

        _restorePending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (wasAtEnd)
            {
                // Was at the bottom before the resize: stay pinned to the (new) end.
                ScrollToOffset(Scroller.ScrollableHeight);
            }
            else if (CardRepeater.Layout is VirtualGridLayout layout && layout.RowPitch > 0)
            {
                int columns =
                    layout.LastColumnCount > 0
                        ? layout.LastColumnCount
                        : Math.Max(1, layout.GetColumnCountForWidth(Scroller.ViewportWidth));
                ScrollToOffset(GetRepeaterTop() + (index / columns) * layout.RowPitch);
            }

            DispatcherQueue.TryEnqueue(() => _restorePending = false);
        });
    }

    private void ScrollToOffset(double verticalOffset)
    {
        Scroller.ChangeView(null, verticalOffset, null, disableAnimation: true);
    }

    private void CardViewControl_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

    /// <summary>
    /// Idempotent teardown. Called from <see cref="CardViewControl_Unloaded"/> AND from the host
    /// page's OnNavigatedFrom. The page path is the reliable one: WinUI does not guarantee that
    /// Unloaded fires on every navigation, and when it is skipped the repeater stays subscribed
    /// to the singleton ViewModel's Cards.CollectionChanged, which roots this control and leaks it.
    /// </summary>
    public void Cleanup()
    {
        if (_cleaned)
            return;
        _cleaned = true;

        Scroller.ViewChanged -= Scroller_ViewChanged;
        Scroller.SizeChanged -= Scroller_SizeChanged;
        Loaded -= CardViewControl_Loaded;
        Unloaded -= CardViewControl_Unloaded;

        // Saves now happen only on settled scrolls, so navigating away mid-gesture would
        // restore a stale pre-gesture position. Snapshot the live offset here, while the
        // geometry is still intact. offset>0 guard: a spurious Unloaded could read 0 and
        // erase a legitimate saved index.
        if (!_restorePending && !isLoadingMore && Scroller.VerticalOffset > 0)
            SaveScrollPosition();

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = null;

        // Cancel any in-flight card-click product fetch: its continuation would otherwise
        // root this torn-down control until the HTTP call completes and then perform a
        // stale Navigate the user no longer wants.
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();
        _navigateCts = null;

        // Detach the repeater from the (singleton-owned) collection immediately.
        // The repeater binds ItemsSource OneTime, so nulling the ItemsSource dependency
        // property below does NOT propagate to it. Without this explicit detach, the
        // torn-down repeater stays subscribed to the shared ObservableCollection's
        // CollectionChanged event; a later mutation on a fresh page then delivers the
        // event to this disconnected native peer and throws COMException 0x80004005.
        CardRepeater.ItemsSource = null;

        LoadCardsMethod = null;
        ViewModel = null;
        ItemsSource = null;
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

    public async void NavigateToProductOrBundle(string productId, InstallerType installerType)
    {
        Scroller.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = new CancellationTokenSource();
        _ = AnimateLoadingDotsAsync(_loadingDotsCts.Token);

        // Fresh CTS per click: a newer click supersedes an in-flight fetch, and Cleanup()
        // cancels it on teardown so the continuation can't stale-Navigate from a dead page.
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();
        _navigateCts = new CancellationTokenSource();
        var navToken = _navigateCts.Token;

        var NavigationFrame = App.GetService<INavigationService>().Frame;
        try
        {
            var product = await Utils.ProductOrBundle(productId, installerType, navToken, market: _localeService.Market, language: _localeService.Language);

            // The token only guards the network awaits; a cancel landing after they complete
            // resumes here without an OCE. Don't navigate on behalf of a torn-down page.
            if (navToken.IsCancellationRequested)
                return;

            LoadingOverlay.Visibility = Visibility.Collapsed;

            if (product.IsBundle)
            {
                NavigationFrame?.Navigate(
                    typeof(BundlesPage),
                    (product.ProductInfo, product.BundleInfo)
                );
            }
            else
            {
                NavigationFrame?.Navigate(typeof(AppPage), product.ProductInfo);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or torn down mid-fetch: nothing to show, nothing to navigate to.
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            
            if (IsNetworkError(ex))
            {
                ErrorPanel.Glyph = "\uF384";
                ErrorPanel.Title = "No Internet Connection";
                ErrorPanel.Subtitle = "Please check your network settings and try again.";
            }
            else
            {
                ErrorPanel.Glyph = "\uEBA0";
                ErrorPanel.Title = "CardView_Error_OpenFailed".GetLocalizedFormat(ex.Message);
                ErrorPanel.Subtitle = string.Empty;
            }
            Debug.WriteLine($"Failed to load product: {ex.Message}");
        }
        finally
        {
            _loadingDotsCts?.Cancel();
            _loadingDotsCts?.Dispose();
            _loadingDotsCts = null;
        }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = ApplyFilters();
    }

    public async Task ApplyFilters()
    {
        ViewModel.Filter1 = SelectedFilterIndex1;
        ViewModel.Filter2 = SelectedFilterIndex2;
        await InitialLoadCards();
    }

    private bool IsNetworkError(Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException || ex is System.Net.Sockets.SocketException)
            return true;
        if (ex.InnerException != null)
            return IsNetworkError(ex.InnerException);
        return false;
    }
}
