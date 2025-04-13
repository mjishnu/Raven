using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StoreListings.Library;
using test.Models;
using test.ViewModels;

namespace test.Views;

public sealed partial class AppPage : Page
{
    public AppViewModel ViewModel { get; }

    public AppPage()
    {
        ViewModel = App.GetService<AppViewModel>();
        InitializeComponent();
    }

    public AppInfo AppData { get; set; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StoreEdgeFDProduct ProductInfo)
        {
            LoadData(ProductInfo);
        }
    }

    private void LoadData(StoreEdgeFDProduct ProductInfo)
    {
        {
            DisplayItem.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;

            AppData.SetValues(
                ProductInfo.Logo,
                ProductInfo.Screenshots,
                ProductInfo.RevisionId,
                ProductInfo.Title,
                ProductInfo.PublisherName,
                ProductInfo.Description,
                ProductInfo.Rating,
                ProductInfo.RatingCount,
                ProductInfo.Size
            );

            LoadingOverlay.Visibility = Visibility.Collapsed;
            DisplayItem.Visibility = Visibility.Visible;
        }
    }

    private void LeftArrowButton_Click(object sender, RoutedEventArgs e)
    {
        // Scroll left by a fixed amount or to the previous item
        double offset = ScreenshotsScrollViewer.HorizontalOffset - 654; // Width + spacing
        ScreenshotsScrollViewer.ChangeView(Math.Max(0, offset), null, null);
    }

    private void RightArrowButton_Click(object sender, RoutedEventArgs e)
    {
        // Scroll right by a fixed amount or to the next item
        double offset = ScreenshotsScrollViewer.HorizontalOffset + 654; // Width + spacing
        ScreenshotsScrollViewer.ChangeView(
            Math.Min(ScreenshotsScrollViewer.ScrollableWidth, offset),
            null,
            null
        );
    }
}
