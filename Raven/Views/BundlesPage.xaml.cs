using System.Collections.ObjectModel;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using Raven.Contracts.Views;
using Raven.Helpers;
using Raven.Models;
using Raven.ViewModels;

namespace Raven.Views;

public sealed partial class BundlesPage : Page, IViewModelPage<BundlesViewModel>
{
    public AppInfo AppData { get; set; } = new AppInfo();

    public ObservableCollection<Card> BundleCards { get; set; } = [];

    private readonly Compositor _compositor;
    private SpringVector3NaturalMotionAnimation? _springAnimation;
    private CancellationTokenSource? _navigateCts;

    public BundlesViewModel ViewModel { get; }

    public BundlesPage()
    {
        ViewModel = App.GetService<BundlesViewModel>();
        _compositor = App.MainWindow.Compositor;
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is (ProductData productInfo, StoreEdgeFDQuery bundleInfo))
        {
            LoadData(productInfo, bundleInfo);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Cancel any in-flight card-click product fetch: its continuation would otherwise
        // root this dead page until the HTTP call completes and then perform a stale Navigate.
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();
        _navigateCts = null;
    }

    private void LoadData(ProductData productInfo, StoreEdgeFDQuery bundleInfo)
    {
        DisplayItem.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;

        var success = true;

        AppData.SetValues(
            productInfo.ProductId,
            productInfo.Logo,
            productInfo.Screenshots,
            productInfo.RevisionId,
            productInfo.Version,
            productInfo.Title,
            productInfo.PublisherName,
            productInfo.Description,
            productInfo.Rating,
            productInfo.RatingCount,
            productInfo.Size
        );

        if (bundleInfo.Cards.Count == 0)
        {
            success = false;
        }
        for (var i = 0; i < bundleInfo.Cards.Count; i++)
        {
            var card = bundleInfo.Cards[i];
            BundleCards.Add(card);
        }

        if (success == true)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DisplayItem.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Visible;
        }
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Fresh CTS per click: a newer click supersedes an in-flight fetch, and
        // OnNavigatedFrom cancels it on teardown.
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();
        _navigateCts = new CancellationTokenSource();

        Utils.HandleCardTapped(
            sender as FrameworkElement,
            Frame,
            DisplayItem,
            ErrorIcon,
            LoadingOverlay,
            ErrorIconText,
            _navigateCts.Token
        );
    }

    private void element_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Utils.HandlePointerEntered(sender, e, ref _springAnimation, _compositor);
    }

    private void element_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        Utils.HandlePointerExited(sender, e, ref _springAnimation, _compositor);
    }
}
