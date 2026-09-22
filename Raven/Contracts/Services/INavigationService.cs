using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Raven.Contracts.Services;

public interface INavigationService
{
    event NavigatedEventHandler Navigated;

    void NavigateToProductDetails(string productId);

    bool CanGoBack { get; }

    Frame? Frame { get; set; }

    bool NavigateTo(string pageKey, object? parameter = null, bool clearNavigation = false);

    void RefreshCurrentPage();

    bool GoBack();
}
