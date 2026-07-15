using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Raven;

public static partial class Program
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var argsList = new List<string>(args);
        var pidIndex = argsList.IndexOf("--wait-for-pid");
        if (pidIndex >= 0 && pidIndex + 1 < argsList.Count)
        {
            if (int.TryParse(argsList[pidIndex + 1], out int pid))
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.WaitForExit(10000);
                }
                catch
                {
                    // Ignore, process might have already exited
                }
            }
        }

        var isRedirect = DecideRedirection();
        if (isRedirect)
            return;

        var mutex = new Mutex(true, "Raven_SingleInstance_Mutex", out var isNewInstance);
        if (!isNewInstance)
        {
            _ = MessageBoxW(
                0,
                "Another instance of Raven is already running.\nPlease close it before launching this one.",
                "Raven",
                MB_OK | MB_ICONWARNING);
            return;
        }

        try
        {
            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Handles single-instance redirection. Must run synchronously BEFORE
    /// Application.Start so the STA thread is not yet running a message loop.
    /// Returns true if this process is a secondary instance and should exit.
    /// </summary>
    private static bool DecideRedirection()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey("raven_main_instance");

        if (!keyInstance.IsCurrent)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            keyInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            return true;
        }

        keyInstance.Activated += OnActivated;
        return false;
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
