using System.Windows;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Data;
using Rbm.Diagnostics;
using Rbm.Hardware;
using Rbm.Monitoring;
using Rbm.Optimization;
using Rbm.Restore;
using Rbm.Security;
using Rbm.UI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Rbm.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static Notifier Notify { get; } = new();
    public static DialogService Dialogs { get; } = new();
    public static TaskTracker Tasks { get; } = new();
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Log(Exception ex, string context) => Serilog.Log.Error(ex, "{Context}", context);

    protected override void OnStartup(StartupEventArgs e)
    {
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(System.IO.Path.Combine(RbmPaths.Logs, "rbm-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Serilog.Log.Information("RBM Optimizer {Version} iniciando. Build Windows {Build}", Version, Environment.OSVersion.Version);

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Serilog.Log.Fatal(a.ExceptionObject as Exception, "Exceção não tratada");
        TaskScheduler.UnobservedTaskException += (_, a) => { Serilog.Log.Error(a.Exception, "Tarefa não observada"); a.SetObserved(); };

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.AddSerilog(dispose: false));
        ConfigureServices(sc);
        Services = sc.BuildServiceProvider();

        var settings = Services.GetRequiredService<ISettingsStore>();
        Loc.Language = settings.Get("language", "pt-BR");
        Services.GetRequiredService<ITelemetryService>().Interval = TimeSpan.FromSeconds(Math.Clamp(settings.Get("telemetry.interval", 1), 1, 10));

        base.OnStartup(e);
        // Diagnóstico usa duas amostras de processos (1 s) para obter CPU% real por processo.
        Services.GetRequiredService<ViewModels.DiagnosticsState>().ProcessSource = () =>
        {
            var sampler = new Rbm.Processes.ProcessSampler();
            sampler.Capture();
            Thread.Sleep(1000);
            return sampler.Capture().ToProcessSamples();
        };
        var window = new MainWindow { DataContext = Services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection sc)
    {
        sc.AddSingleton<RbmDatabase>();
        sc.AddSingleton<IChangeJournal, ChangeJournal>();
        sc.AddSingleton<ISettingsStore, SettingsStore>();
        sc.AddSingleton<SystemInventory>();
        sc.AddSingleton<ISystemInventory>(sp => sp.GetRequiredService<SystemInventory>());
        sc.AddSingleton<LhmSensorProvider>();
        sc.AddSingleton<ITelemetryService>(sp => new TelemetryService(sp.GetRequiredService<ISystemInventory>(),
            sp.GetRequiredService<ISettingsStore>().Get("sensors.lhm", true) ? new ISensorProvider[] { sp.GetRequiredService<LhmSensorProvider>() } : Array.Empty<ISensorProvider>(),
            sp.GetService<ILogger<TelemetryService>>()));
        sc.AddSingleton<LiveTelemetry>();
        sc.AddSingleton<AgentClient>();
        sc.AddSingleton<IAgentClient>(sp => sp.GetRequiredService<AgentClient>());
        sc.AddSingleton(_ => TweakCatalog.LoadEmbedded());
        sc.AddSingleton(_ => new TweakExecutor());
        sc.AddSingleton<ChangeOrchestrator>();
        sc.AddSingleton<Rbm.Core.Diagnostics.IDiagnosticRule, Rbm.Drivers.DriverDiagnosticRule>();
        sc.AddSingleton<DiagnosticEngine>();
        sc.AddSingleton<Rbm.Processes.ProcessSampler>();
        sc.AddSingleton<MainViewModel>();
        Views.PageRegistry.Register(sc);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Exceção na UI");
        Notify.Error("Erro inesperado", e.Exception.Message);
        e.Handled = true; // mantém o app aberto; o erro fica registrado no log
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services.GetService<ITelemetryService>()?.Dispose();
            Services.GetService<AgentClient>()?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        Serilog.Log.Information("RBM Optimizer encerrado.");
        Serilog.Log.CloseAndFlush();
        base.OnExit(e);
    }
}
