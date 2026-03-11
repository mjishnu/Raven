using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

using test.Activation;
using test.Contracts.Services;
using test.Core.Contracts.Services;
using test.Core.Services;
using test.Models;
using test.Services;
using test.ViewModels;
using test.Views;

namespace test;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host { get; }

    public static IServiceProvider Services { get; private set; }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException(
                $"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs."
            );
        }

        return service;
    }

    public static WindowEx MainWindow { get; } = new MainWindow();

    public static UIElement? AppTitlebar { get; set; }

    public App()
    {
        InitializeComponent();

        Host = Microsoft
            .Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureServices(
                (context, services) =>
                {
                    // Default Activation Handler
                    services.AddTransient<
                        ActivationHandler<LaunchActivatedEventArgs>,
                        DefaultActivationHandler
                    >();

                    // Other Activation Handlers

                    // Services
                    services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
                    services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
                    services.AddTransient<INavigationViewService, NavigationViewService>();

                    services.AddSingleton<IActivationService, ActivationService>();
                    services.AddSingleton<IPageService, PageService>();
                    services.AddSingleton<INavigationService, NavigationService>();

                    // Core Services
                    services.AddSingleton<IFileService, FileService>();

                    // Views and ViewModels
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<SettingsPage>();
                    services.AddTransient<BundlesViewModel>();
                    services.AddTransient<BundlesPage>();
                    services.AddTransient<AppViewModel>();
                    services.AddTransient<AppPage>();
                    services.AddTransient<ShellPage>();
                    services.AddTransient<ShellViewModel>();

                    // TemplateStudio: Added Advanced Search View and ViewModel
                    services.AddTransient<Advanced_SearchViewModel>();
                    services.AddTransient<Advanced_SearchPage>();

                    services.AddTransient<MainPage>();
                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<SearchPage>();
                    services.AddSingleton<SearchViewModel>();
                    services.AddTransient<DownloadsPage>();
                    services.AddSingleton<DownloadsViewModel>();

                    services.AddTransient<InstallationsPage>();
                    services.AddSingleton<InstallationsViewModel>();
                    services.AddTransient<UpdatesPage>();
                    services.AddSingleton<UpdatesViewModel>();

                    // Configuration
                    services.Configure<LocalSettingsOptions>(
                        context.Configuration.GetSection(nameof(LocalSettingsOptions))
                    );
                }
            )
            .Build();

        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e
    )
    {
        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception?.GetType().FullName}");
        Debug.WriteLine($"[App] Message   : {e.Exception?.Message}");
        Debug.WriteLine($"[App] Inner     : {e.Exception?.InnerException?.Message}");
        Debug.WriteLine($"[App] StackTrace:\n{e.Exception?.StackTrace}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        // Initialize DownloadManagerService with the dispatcher queue
        DownloadManagerService.Instance.Initialize(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
        );

        await App.GetService<IActivationService>().ActivateAsync(args);
        // Add other services
        var services = new ServiceCollection();
        services.AddSingleton<IStoreService, StoreService>();
        Services = services.BuildServiceProvider();
    }
}
