using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

using WinUIEx;

namespace Raven;

public static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;

    [STAThread]
    static async Task<int> Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var keyInstance = AppInstance.FindOrRegisterForKey("raven_main_instance");

        if (!keyInstance.IsCurrent)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            await keyInstance.RedirectActivationToAsync(activationArgs);
            return 0;
        }

        var mutex = new Mutex(true, "Raven_SingleInstance_Mutex", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBoxW(
                0,
                "Another instance of Raven is already running.\nPlease close it before launching this one.",
                "Raven",
                MB_OK | MB_ICONWARNING);
            return 0;
        }

        try
        {
            keyInstance.Activated += OnActivated;

            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        if (App.MainWindow is not null)
        {
            _ = App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                WindowExtensions.Restore(App.MainWindow);
                WindowExtensions.SetForegroundWindow(App.MainWindow);
            });
        }
    }
}
