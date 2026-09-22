using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Raven.Contracts.Services;
using Raven.Contracts.ViewModels;
using Raven.Helpers;
using Raven.ViewModels;
using Raven.Views;

namespace Raven.Services;

// For more information on navigation between pages see
// https://github.com/microsoft/TemplateStudio/blob/main/docs/WinUI/navigation.md
public class NavigationService : INavigationService
{
    private readonly IPageService _pageService;
    private object? _lastParameterUsed;
    private Frame? _frame;

    public event NavigatedEventHandler? Navigated;

    public Frame? Frame
    {
        get
        {
            if (_frame == null)
            {
                _frame = App.MainWindow.Content as Frame;
                RegisterFrameEvents();
            }

            return _frame;
        }
        set
        {
            UnregisterFrameEvents();
            _frame = value;
            RegisterFrameEvents();
        }
    }

    public void NavigateToProductDetails(string productId)
    {
        _frame?.Navigate(typeof(AppPage), productId);
    }

    [MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
    public bool CanGoBack => Frame != null && Frame.CanGoBack;

    public NavigationService(IPageService pageService)
    {
        _pageService = pageService;
    }

    private void RegisterFrameEvents()
    {
        if (_frame != null)
        {
            _frame.Navigated += OnNavigated;
        }
    }

    private void UnregisterFrameEvents()
    {
        if (_frame != null)
        {
            _frame.Navigated -= OnNavigated;
        }
    }

    public bool GoBack()
    {
        if (CanGoBack)
        {
            var vmBeforeNavigation = _frame.GetPageViewModel();
            _frame.GoBack();
            if (vmBeforeNavigation is INavigationAware navigationAware)
            {
                navigationAware.OnNavigatedFrom();
            }

            return true;
        }

        return false;
    }

    public bool NavigateTo(string pageKey, object? parameter = null, bool clearNavigation = false)
    {
        var pageType = _pageService.GetPageType(pageKey);

        if (
            _frame != null
            && (
                _frame.Content?.GetType() != pageType
                || (parameter != null && !parameter.Equals(_lastParameterUsed))
            )
        )
        {
            _frame.Tag = clearNavigation;
            var vmBeforeNavigation = _frame.GetPageViewModel();
            var navigated = _frame.Navigate(pageType, parameter);
            if (navigated)
            {
                _lastParameterUsed = parameter;
                if (vmBeforeNavigation is INavigationAware navigationAware)
                {
                    navigationAware.OnNavigatedFrom();
                }
            }
            return navigated;
        }
        return false;
    }

    public void RefreshCurrentPage()
    {
        if (_frame?.Content is null || _frame.CurrentSourcePageType is null)
            return;

        var pageType = _frame.CurrentSourcePageType;
        var parameter = _lastParameterUsed;
        var backStackCount = _frame.BackStack.Count;
        _frame.Tag = false;

        if (_frame.Navigate(pageType, parameter))
        {
            // Refreshing must not add a second copy of the old page to the back stack.
            while (_frame.BackStack.Count > backStackCount)
                _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (sender is Frame frame)
        {
            _lastParameterUsed = e.Parameter;
            var clearNavigation = frame.Tag is bool shouldClear && shouldClear;
            if (clearNavigation)
            {
                frame.BackStack.Clear();
            }

            if (frame.GetPageViewModel() is INavigationAware navigationAware)
            {
                navigationAware.OnNavigatedTo(e.Parameter);
            }

            Navigated?.Invoke(sender, e);
        }
    }
}
