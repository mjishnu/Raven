using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using Raven.Activation;
using Raven.Contracts.Services;
using Raven.Helpers;
using Raven.Models;
using Raven.Services;
using Raven.ViewModels;
using Raven.Views;
using Serilog;
using Serilog.Events;

namespace Raven;

public partial class App : Application
{
    public IHost Host
    {
        get;
    }

    private readonly ILogger<App> _logger;

    public static IServiceProvider Services
    {
        get; private set;
    }

    public static WindowEx MainWindow { get; private set; } = null!;

    public static T GetService<T>()
        where T : class
    {
        if (Services is null)
        {
            throw new InvalidOperationException("The application services have not been initialized yet.");
        }

        if (Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException(
                $"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs."
            );
        }

        return service;
    }

    public static UIElement? AppTitlebar
    {
        get; set;
    }

    public App()
    {
        InitializeComponent();

        Host = BuildHost();
        Services = Host.Services;
        _logger = Host.Services.GetRequiredService<ILogger<App>>();

        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static IHost BuildHost()
    {
        var builder = new HostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration(cfg =>
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            );

        builder.UseSerilog((_, _, loggerConfiguration) => ConfigureLogging(loggerConfiguration));

        return builder
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
                    services.AddSingleton<ILocaleService, LocaleService>();
                    services.AddSingleton<IArchitectureSelectorService, ArchitectureSelectorService>();
                    services.AddTransient<INavigationViewService, NavigationViewService>();

                    services.AddSingleton<IActivationService, ActivationService>();
                    services.AddSingleton<IPageService, PageService>();
                    services.AddSingleton<INavigationService, NavigationService>();

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
                    services.AddSingleton<Advanced_SearchViewModel>();
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

                    services.AddSingleton<IStoreService, StoreService>();
                    services.AddSingleton<GitHubUpdaterService>();
                    services.AddSingleton<AppUpdatePromptService>();

                    // Configuration
                    services.Configure<LocalSettingsOptions>(
                        context.Configuration.GetSection(nameof(LocalSettingsOptions))
                    );
                }
            )
            .Build();
    }

    private static bool IsInstallLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue))
            return false;

        if (sourceContextValue is not ScalarValue { Value: string sourceContext })
            return false;

        return sourceContext.Contains("Install", StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureLogging(LoggerConfiguration loggerConfiguration)
    {
        const string outputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext();

        try
        {
            AppLogPaths.EnsureLogDirectory();

            loggerConfiguration
                .WriteTo.File(
                    AppLogPaths.RuntimeLogFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: outputTemplate
                )
                .WriteTo.Logger(lc =>
                    lc.Filter
                        .ByIncludingOnly(IsInstallLogEvent)
                        .WriteTo.File(
                            AppLogPaths.InstallLogFilePath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 14,
                            shared: true,
                            outputTemplate: outputTemplate
                        )
                )
                .WriteTo.Logger(lc =>
                    lc.Filter
                        .ByIncludingOnly(e =>
                            e.Level >= LogEventLevel.Error && !IsInstallLogEvent(e)
                        )
                        .WriteTo.File(
                            AppLogPaths.CrashLogFilePath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 30,
                            shared: true,
                            outputTemplate: outputTemplate
                        )
                );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] File logging unavailable: {ex}");
        }
    }

    private void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e
    )
    {
        _logger.LogError(e.Exception, "Unhandled UI exception");

        Debug.WriteLine($"[App] UNHANDLED EXCEPTION: {e.Exception?.GetType().FullName}");
        Debug.WriteLine($"[App] Message   : {e.Exception?.Message}");
        Debug.WriteLine($"[App] Inner     : {e.Exception?.InnerException?.Message}");
        Debug.WriteLine($"[App] StackTrace:\n{e.Exception?.StackTrace}");
    }

    private void CurrentDomain_UnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        _logger.LogCritical(e.ExceptionObject as Exception, "Unhandled domain exception");
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e
    )
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        base.OnLaunched(args);

        MainWindow = new MainWindow();

        LogStartupDetails();

        // Initialize DownloadManagerService with the dispatcher queue
        DownloadManagerService.Instance.Initialize(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
        );

        await App.GetService<IActivationService>().ActivateAsync(args);
    }

    private void LogStartupDetails()
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var osVersion = SystemInfo.GetExactWindowsVersion();

                _logger.LogInformation(
                    "Startup | AppVersion={AppVersion} | Runtime={Runtime} | OS={OSVersion} | Arch={Architecture} | BaseDir={BaseDir} | LogsDir={LogsDir}",
                    assemblyVersion?.ToString() ?? "unknown",
                    RuntimeInformation.FrameworkDescription,
                    osVersion,
                    RuntimeInformation.OSArchitecture,
                    AppContext.BaseDirectory,
                    AppLogPaths.LogDirectory
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Failed to write startup details: {ex}");
        }
    }
}
