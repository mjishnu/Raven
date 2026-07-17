using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Services;
using Raven.ViewModels;
using StoreListings.Library;
using Windows.System;
using Rect = Windows.Foundation.Rect;

namespace Raven.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }
    private CancellationTokenSource? suggestionCancellationTokenSource;
    private readonly ILocaleService _localeService;
    private readonly AppUpdatePromptService _appUpdatePromptService;
    private readonly ILogger<ShellPage> _logger;

    public ShellPage(ShellViewModel viewModel, AppUpdatePromptService appUpdatePromptService, ILogger<ShellPage> logger)
    {
        ViewModel = viewModel;
        _localeService = App.GetService<ILocaleService>();
        _appUpdatePromptService = appUpdatePromptService;
        _logger = logger;
        InitializeComponent();

        ViewModel.NavigationService.Frame = NavigationFrame;
        ViewModel.NavigationViewService.Initialize(NavigationViewControl);
        App.MainWindow.ExtendsContentIntoTitleBar = true;
        App.MainWindow.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        App.MainWindow.SetTitleBar(AppTitleBar);
        App.AppTitlebar = AppTitleBar;
        App.MainWindow.Activated += Window_Activated;
        AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
        AppTitleBar.Loaded += AppTitleBar_Loaded;
        Loaded += OnLoaded;
    }

    private void OnPaneDisplayModeChanged(
        NavigationView sender,
        NavigationViewDisplayModeChangedEventArgs args
    )
    {
        AppTitleBarText.Visibility =
            args.DisplayMode == NavigationViewDisplayMode.Minimal
                ? Visibility.Collapsed
                : Visibility.Visible;

        if (App.MainWindow.ExtendsContentIntoTitleBar)
        {
            SetRegionsForCustomTitleBar();
        }
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow.ExtendsContentIntoTitleBar)
        {
            SetRegionsForCustomTitleBar();
        }
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (App.MainWindow.ExtendsContentIntoTitleBar)
        {
            SetRegionsForCustomTitleBar();
        }
    }

    private void SetRegionsForCustomTitleBar()
    {
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

        var scaleAdjustment = AppTitleBar.XamlRoot.RasterizationScale;

        RightPaddingColumn.Width = new GridLength(
            App.MainWindow.AppWindow.TitleBar.RightInset / scaleAdjustment
        );
        LeftPaddingColumn.Width = new GridLength(
            App.MainWindow.AppWindow.TitleBar.LeftInset / scaleAdjustment
        );

        var transform = SearchBox.TransformToVisual(null);
        var bounds = transform.TransformBounds(
            new Rect(0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight)
        );
        var searchBoxRect = GetRect(bounds, scaleAdjustment);

        transform = PaneButton.TransformToVisual(null);
        bounds = transform.TransformBounds(
            new Rect(0, 0, PaneButton.ActualWidth, PaneButton.ActualHeight)
        );
        var paneButtonRect = GetRect(bounds, scaleAdjustment);

        transform = BackButton.TransformToVisual(null);
        bounds = transform.TransformBounds(
            new Rect(0, 0, BackButton.ActualWidth, BackButton.ActualHeight)
        );
        var backButtonRect = GetRect(bounds, scaleAdjustment);

        var rectArray = new Windows.Graphics.RectInt32[]
        {
            searchBoxRect,
            paneButtonRect,
            backButtonRect,
        };

        var nonClientInputSrc = InputNonClientPointerSource.GetForWindowId(
            App.MainWindow.AppWindow.Id
        );
        nonClientInputSrc.SetRegionRects(NonClientRegionKind.Passthrough, rectArray);
    }

    private static Windows.Graphics.RectInt32 GetRect(Rect bounds, double scale)
    {
        return new Windows.Graphics.RectInt32(
            _X: (int)Math.Round(bounds.X * scale),
            _Y: (int)Math.Round(bounds.Y * scale),
            _Width: (int)Math.Round(bounds.Width * scale),
            _Height: (int)Math.Round(bounds.Height * scale)
        );
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        TitleBarHelper.UpdateTitleBar(RequestedTheme);
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressed), true);
        RegisterBackForwardKeyboardAccelerators();

        if (NavigationViewControl.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.PointerEntered += NavItem_PointerEntered;
            settingsItem.PointerExited += NavItem_PointerExited;
        }

        if (XamlRoot is null)
            return;

        await CheckSideloadingAsync(XamlRoot);
        await _appUpdatePromptService.CheckForUpdatesOnStartupAsync(XamlRoot);
    }

    private async Task CheckSideloadingAsync(XamlRoot xamlRoot)
    {
        if (SideloadingCheckService.IsSideloadingEnabled(_logger))
            return;

        _logger.LogWarning("Sideloading is disabled on this device");

        var dialog = new ContentDialog
        {
            Title = "Sideloading_DisabledTitle".GetLocalized(),
            Content = "Sideloading_DisabledMessage".GetLocalized(),
            PrimaryButtonText = "Sideloading_EnableButton".GetLocalized(),
            CloseButtonText = "Sideloading_CancelButton".GetLocalized(),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
            await Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        var result = false;
        // Ignore button chords with the left, right, and middle buttons
        if (
            properties.IsLeftButtonPressed
            || properties.IsRightButtonPressed
            || properties.IsMiddleButtonPressed
        )
            return;

        // If back or forward are pressed (but not both) navigate appropriately
        var backPressed = properties.IsXButton1Pressed;
        var forwardPressed = properties.IsXButton2Pressed;
        if (backPressed ^ forwardPressed)
        {
            if (backPressed)
            {
                result = TryGoBack();
            }
            if (forwardPressed)
            {
                result = TryGoForward();
            }
        }
        e.Handled = result;
    }

    private void RegisterBackForwardKeyboardAccelerators()
    {
        // Back navigation accelerators
        KeyboardAccelerators.Add(
            BuildKeyboardAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu)
        );
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoBack));

        // Forward navigation accelerators
        KeyboardAccelerators.Add(
            BuildKeyboardAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu)
        );
        KeyboardAccelerators.Add(BuildKeyboardAccelerator(VirtualKey.GoForward));
    }

    private static KeyboardAccelerator BuildKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers? modifiers = null
    )
    {
        var keyboardAccelerator = new KeyboardAccelerator() { Key = key };

        if (modifiers.HasValue)
        {
            keyboardAccelerator.Modifiers = modifiers.Value;
        }

        keyboardAccelerator.Invoked += OnKeyboardAcceleratorInvoked;

        return keyboardAccelerator;
    }

    private static void OnKeyboardAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args
    )
    {
        var result = false;

        // Check which key was pressed and navigate accordingly
        if (
            sender.Key == VirtualKey.GoBack
            || (sender.Key == VirtualKey.Left && sender.Modifiers == VirtualKeyModifiers.Menu)
        )
        {
            // Back navigation
            result = TryGoBack();
        }
        else if (
            sender.Key == VirtualKey.GoForward
            || (sender.Key == VirtualKey.Right && sender.Modifiers == VirtualKeyModifiers.Menu)
        )
        {
            // Forward navigation
            result = TryGoForward();
        }

        args.Handled = result;
    }

    private static bool TryGoBack()
    {
        var navigationService = App.GetService<INavigationService>();
        if (navigationService.Frame?.CanGoBack == true)
        {
            navigationService.Frame.GoBack();
            return true;
        }

        return false;
    }

    private static bool TryGoForward()
    {
        var navigationService = App.GetService<INavigationService>();
        if (navigationService.Frame?.CanGoForward == true)
        {
            navigationService.Frame.GoForward();
            return true;
        }

        return false;
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            VisualStateManager.GoToState(this, "Deactivated", true);
        }
        else
        {
            VisualStateManager.GoToState(this, "Activated", true);
        }
    }

    private void PaneButton_Click(object sender, RoutedEventArgs args)
    {
        NavigationViewControl.IsPaneOpen = !NavigationViewControl.IsPaneOpen;
    }

    private void TitleBar_BackClick(object sender, RoutedEventArgs args)
    {
        if (NavigationFrame.CanGoBack)
        {
            NavigationFrame.GoBack();
        }
    }

    private void SearchBox_QuerySubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args
    )
    {
        // Cancel any pending suggestions
        suggestionCancellationTokenSource?.Cancel();

        // If a suggestion was explicitly chosen:
        if (args.ChosenSuggestion != null)
        {
            if (args.ChosenSuggestion is string suggestionText)
            {
                // Navigate to SearchPage with the string suggestion text.
                NavigationFrame.Navigate(typeof(SearchPage), suggestionText);
            }
            else if (args.ChosenSuggestion is Card cardSuggestion)
            {
                // Navigate to AppPage with the card object.
                NavigationFrame.Navigate(typeof(SearchPage), cardSuggestion);
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            // No suggestion was chosen; use the typed query for searching.
            NavigationFrame.Navigate(typeof(SearchPage), args.QueryText);
        }

        // Clear the items and text of the AutoSuggestBox.
        sender.ItemsSource = null;
        sender.Text = string.Empty;

        // Close the suggestion list.
        sender.IsSuggestionListOpen = false;
    }

    private void CtrlF_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void SearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args
    )
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        var query = sender.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            // Cancel any in-flight request so its stale result can't repopulate the cleared list.
            suggestionCancellationTokenSource?.Cancel();
            sender.ItemsSource = null;
            return;
        }

        // Cancel any previous request. Capture THIS request's token before any await: after
        // resuming, the field may already point to a newer keystroke's CTS, and checking the
        // field would let a stale (cancelled) request blank or overwrite the newer suggestions.
        suggestionCancellationTokenSource?.Cancel();
        suggestionCancellationTokenSource?.Dispose();
        suggestionCancellationTokenSource = new CancellationTokenSource();
        var token = suggestionCancellationTokenSource.Token;

        // Debounce: collapse a burst of keystrokes into one HTTP request instead of one per
        // keystroke. A newer keystroke cancels this delay, so only the pause after typing fires.
        try
        {
            await Task.Delay(200, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Call your API that returns card suggestions.
        var suggestions = await GetCardSuggestionsAsync(query, token);

        // Only update UI if not cancelled
        if (!token.IsCancellationRequested)
        {
            sender.ItemsSource = suggestions;
        }
    }

    private async Task<List<object>> GetCardSuggestionsAsync(
        string query,
        CancellationToken cancellationToken
    )
    {
        DeviceFamily deviceFamily = DeviceFamily.Desktop;
        Market market = _localeService.Market;
        Lang language = _localeService.Language;
        var combined = new List<object>();

        try
        {
            Result<StoreEdgeFDSuggestions> result =
                await StoreEdgeFDSuggestions.GetSearchSuggestion(
                    query,
                    deviceFamily,
                    market,
                    language,
                    cancellationToken
                );

            if (result.IsSuccess)
            {
                combined.AddRange(result.Value.Suggestions);
                combined.AddRange(result.Value.Cards);
            }
            else
            {
                throw result.Exception;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
        return combined;
    }

    private void InstallationsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigationService.NavigateTo(typeof(InstallationsViewModel).FullName!);
    }

    private void NavItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Icon is UIElement icon)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;
            
            visual.CenterPoint = new System.Numerics.Vector3((float)(icon.ActualSize.X / 2.0), (float)(icon.ActualSize.Y / 2.0), 0);
            
            var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            spring.FinalValue = new System.Numerics.Vector3(1.08f, 1.08f, 1.0f);
            spring.DampingRatio = 0.8f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            
            visual.StartAnimation("Scale", spring);
        }
    }

    private void NavItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is NavigationViewItem item && item.Icon is UIElement icon)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;
            
            var spring = compositor.CreateSpringVector3Animation();
            spring.Target = "Scale";
            spring.FinalValue = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f);
            spring.DampingRatio = 0.8f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            
            visual.StartAnimation("Scale", spring);
        }
    }
}

public partial class SuggestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }
    public DataTemplate? CardTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is string)
        {
            return StringTemplate;
        }
        else if (item is Card)
        {
            return CardTemplate;
        }
        return base.SelectTemplateCore(item, container);
    }
}
