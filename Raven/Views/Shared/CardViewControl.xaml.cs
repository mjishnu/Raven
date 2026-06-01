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
    private readonly ILocaleService _localeService;
    private readonly ILogger<CardViewControl> _logger;

    // Inner ScrollView of the self-scrolling ItemsView (resolved once its template applies).
    private ScrollView? _scrollView;

    // True while the list is scrolled to the bottom; lets a resize keep the end pinned.
    private bool _atEnd;

    // Suppresses scroll-position saves while we programmatically restore/anchor the view.
    private bool _restorePending;
    private int _hookAttempts;
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
        CardItemsView.Loaded += CardItemsView_Loaded;
        CardItemsView.SizeChanged += CardItemsView_SizeChanged;
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
        DisplayItem.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
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
            DisplayItem.Visibility = Visibility.Visible;
            NoResultsPanel.Visibility =
                (ViewModel.Cards.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Visible;
            ErrorTextBlock.Text = string.Format(
                "CardView_Error_LoadFailed".GetLocalized(),
                errorText
            );
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

    private void CardItemsView_Loaded(object sender, RoutedEventArgs e)
    {
        TryHookScrollView();
    }

    // The ItemsView owns the scrolling ScrollView, which only exists after its template applies.
    private void TryHookScrollView()
    {
        if (_scrollView != null)
            return;

        _scrollView = CardItemsView.ScrollView;
        if (_scrollView != null)
        {
            _scrollView.ViewChanged += ScrollView_ViewChanged;
            // Returning to a cached page: restore the previous scroll position by item index.
            if (ViewModel?.HasCachedResults == true)
            {
                RestoreScrollPosition();
            }
        }
        else if (_hookAttempts++ < 10)
        {
            DispatcherQueue.TryEnqueue(TryHookScrollView);
        }
    }

    private async void ScrollView_ViewChanged(ScrollView sender, object args)
    {
        if (_restorePending || ViewModel == null)
            return;

        double offset = sender.VerticalOffset;
        double scrollable = sender.ScrollableHeight;

        _atEnd = scrollable > 0 && offset >= scrollable - EndThreshold;

        if (offset > 0 && !isLoadingMore)
        {
            SaveScrollPosition(sender);
        }

        if (
            !isLoadingMore
            && ViewModel.HasMoreItems
            && scrollable > 0
            && offset >= scrollable - LoadMoreThreshold
        )
        {
            await LoadMoreCards();
        }
    }

    // Persist the first visible item's index rather than a pixel offset, so the position stays
    // correct after the column count reflows (window resize, snap, fullscreen).
    private void SaveScrollPosition(ScrollView sender)
    {
        if (ViewModel == null || CardItemsView.Layout is not VirtualGridLayout layout)
            return;

        double rowPitch = layout.RowPitch;
        if (rowPitch <= 0)
            return;

        int columns =
            layout.LastColumnCount > 0
                ? layout.LastColumnCount
                : layout.GetColumnCountForWidth(sender.ViewportWidth);
        int firstRow = (int)(sender.VerticalOffset / rowPitch);
        ViewModel.FirstVisibleIndex = firstRow * Math.Max(1, columns);
    }

    private void RestoreScrollPosition()
    {
        if (_scrollView == null || ViewModel == null || ViewModel.FirstVisibleIndex <= 0)
            return;

        int index = ViewModel.FirstVisibleIndex;
        _restorePending = true;
        int attempts = 0;

        void Apply()
        {
            if (_scrollView == null || ViewModel == null)
            {
                _restorePending = false;
                return;
            }

            if (CardItemsView.Layout is not VirtualGridLayout layout || layout.RowPitch <= 0)
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
                    : Math.Max(1, layout.GetColumnCountForWidth(_scrollView.ViewportWidth));
            double target = (index / columns) * layout.RowPitch;

            // The uniform layout reports its full extent after the first measure; wait for it
            // so the target offset is reachable rather than being clamped short.
            if (_scrollView.ScrollableHeight + 0.5 < target && attempts++ < 10)
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
    private void CardItemsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged fires after layout, so the inner ScrollView is reliably available here.
        // Acts as the fallback hook point if it wasn't ready when Loaded fired.
        TryHookScrollView();

        if (_scrollView == null || ViewModel == null || CardItemsView.ItemsSource == null)
            return;

        bool wasAtEnd = _atEnd;
        int index = ViewModel.FirstVisibleIndex;
        if (!wasAtEnd && index <= 0)
            return; // already at the top — nothing to preserve

        _restorePending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_scrollView == null)
            {
                _restorePending = false;
                return;
            }

            if (wasAtEnd)
            {
                // Was at the bottom before the resize: stay pinned to the (new) end.
                ScrollToOffset(_scrollView.ScrollableHeight);
            }
            else if (CardItemsView.Layout is VirtualGridLayout layout && layout.RowPitch > 0)
            {
                int columns = layout.LastColumnCount > 0 ? layout.LastColumnCount : 1;
                ScrollToOffset((index / columns) * layout.RowPitch);
            }

            DispatcherQueue.TryEnqueue(() => _restorePending = false);
        });
    }

    private void ScrollToOffset(double verticalOffset)
    {
        _scrollView?.ScrollTo(
            0,
            verticalOffset,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)
        );
    }

    private void CardViewControl_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

    /// <summary>
    /// Idempotent teardown. Called from <see cref="CardViewControl_Unloaded"/> AND from the host
    /// page's OnNavigatedFrom. The page path is the reliable one: WinUI does not guarantee that
    /// Unloaded fires on every navigation, and when it is skipped the ItemsView stays subscribed
    /// to the singleton ViewModel's Cards.CollectionChanged, which roots this control and leaks it.
    /// </summary>
    public void Cleanup()
    {
        if (_cleaned)
            return;
        _cleaned = true;

        if (_scrollView != null)
        {
            _scrollView.ViewChanged -= ScrollView_ViewChanged;
            _scrollView = null;
        }
        CardItemsView.Loaded -= CardItemsView_Loaded;
        CardItemsView.SizeChanged -= CardItemsView_SizeChanged;
        Unloaded -= CardViewControl_Unloaded;

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = null;

        // Detach the ItemsView from the (singleton-owned) collection immediately.
        // The inner ItemsView binds ItemsSource OneTime, so nulling the ItemsSource
        // dependency property below does NOT propagate to it. Without this explicit
        // detach, the torn-down ItemsView stays subscribed to the shared
        // ObservableCollection's CollectionChanged event; a later mutation on a fresh
        // page then delivers the event to this disconnected native peer and throws
        // COMException 0x80004005 ("Unspecified error").
        CardItemsView.ItemsSource = null;

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
        DisplayItem.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = new CancellationTokenSource();
        _ = AnimateLoadingDotsAsync(_loadingDotsCts.Token);

        var NavigationFrame = App.GetService<INavigationService>().Frame;
        try
        {
            var product = await Utils.ProductOrBundle(productId, installerType, market: _localeService.Market, language: _localeService.Language);

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
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Visible;
            ErrorTextBlock.Text = string.Format(
                "CardView_Error_OpenFailed".GetLocalized(),
                ex.Message
            );
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
}
