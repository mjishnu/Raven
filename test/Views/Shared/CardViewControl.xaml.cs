using System.Collections;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using StoreListings.Library;
using test.Contracts.Services;
using test.Helpers;
using Windows.Foundation;

namespace test.Views.Shared;

public sealed partial class CardViewControl : UserControl
{
    #region Fields
    private Compositor _compositor;
    private SpringVector3NaturalMotionAnimation _springAnimation;
    private bool filterBtnActive = false;
    private bool isLoadingMore = false;
    private CancellationTokenSource? _loadingDotsCts;
    #endregion

    #region Constructor
    public CardViewControl()
    {
        InitializeComponent();
        scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
        scrollViewer.Loaded += ScrollViewer_Loaded;
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
            Debug.WriteLine(errorText);
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
            ErrorTextBlock.Text = errorText;
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

    private async void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && scrollViewer.VerticalOffset > 0)
        {
            ViewModel.ScrollPosition = scrollViewer.VerticalOffset;
        }

        if (
            !isLoadingMore
            && ViewModel.HasMoreItems
            && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 200
        )
        {
            await LoadMoreCards();
        }
    }

    private void ScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HasCachedResults)
        {
            if (ViewModel.ScrollPosition <= 0)
                return;

            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(50);
                scrollViewer.ChangeView(null, ViewModel.ScrollPosition, null, false);

                await Task.Delay(150);
                scrollViewer.ChangeView(null, ViewModel.ScrollPosition, null, false);
            });
        }
    }

    private void CardViewControl_Unloaded(object sender, RoutedEventArgs e)
    {
        scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
        scrollViewer.Loaded -= ScrollViewer_Loaded;
        Unloaded -= CardViewControl_Unloaded;

        _loadingDotsCts?.Cancel();
        _loadingDotsCts?.Dispose();
        _loadingDotsCts = null;

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
            var product = await Utils.ProductOrBundle(productId, installerType);

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
            ErrorTextBlock.Text = ex.Message;
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
