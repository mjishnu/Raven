using Microsoft.UI.Xaml.Media;
using Raven.Helpers;
using Windows.UI.ViewManagement;

namespace Raven;

// Code-only (no MainWindow.xaml): as a XAML root, WindowEx made the XAML compiler generate
// metadata for WinUIEx's obsolete Icon type, which surfaced as a CS0618 warning.
public sealed partial class MainWindow : WindowEx
{
    private Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue;

    private UISettings settings;

    public MainWindow()
    {
        MinWidth = 500;
        MinHeight = 500;
        PersistenceId = "MainWindow";
        SystemBackdrop = new MicaBackdrop();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/Raven.ico"));
        Content = null;
        Title = "AppDisplayName".GetLocalized();
        dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        settings = new UISettings();
        settings.ColorValuesChanged += Settings_ColorValuesChanged;
    }

    // this handles updating the caption button colors correctly when indows system theme is changed
    // while the app is open
    private void Settings_ColorValuesChanged(UISettings sender, object args)
    {
        // This calls comes off-thread, hence we will need to dispatch it to current app's thread
        dispatcherQueue.TryEnqueue(() =>
        {
            TitleBarHelper.ApplySystemThemeToCaptionButtons();
        });
    }
}
