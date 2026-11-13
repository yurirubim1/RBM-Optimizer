using Rbm.UI.Infrastructure;
using Rbm.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.Views;

/// <summary>
/// Registro central das páginas (ordem = ordem da barra lateral). Ícones: Segoe Fluent Icons / MDL2.
/// Páginas marcadas como avançadas só aparecem na barra lateral com "Ferramentas avançadas" ligado,
/// mas continuam acessíveis por links (ex.: "Investigar" no diagnóstico).
/// </summary>
public static class PageRegistry
{
    public static readonly IReadOnlyList<NavItem> Items = new List<NavItem>
    {
        // Início
        new("dashboard", "", "home", typeof(DashboardViewModel)),
        new("recommend", "", "home", typeof(RecommendationsViewModel)),
        new("diagnostics","", "home", typeof(DiagnosticsViewModel)),
        // Otimizar
        new("optimizations", "", "optimize", typeof(OptimizationsViewModel)),
        new("profiles", "", "optimize", typeof(ProfilesViewModel)),
        new("gaming", "", "optimize", typeof(GamingViewModel)),
        // Limpar
        new("cleanup", "", "clean", typeof(CleanupViewModel)),
        new("space", "", "clean", typeof(SpaceViewModel)),
        // Meu PC
        new("cpu", "", "mypc", typeof(CpuViewModel)),
        new("gpu", "", "mypc", typeof(GpuViewModel)),
        new("memory", "", "mypc", typeof(MemoryViewModel)),
        new("storage", "", "mypc", typeof(StorageViewModel)),
        new("power", "", "mypc", typeof(PowerViewModel)),
        // Ferramentas avançadas
        new("monitoring", "", "tools", typeof(MonitoringViewModel)),
        new("processes", "", "tools", typeof(ProcessesViewModel)),
        new("startup", "", "tools", typeof(StartupViewModel)),
        new("drivers", "", "tools", typeof(DriversViewModel)),
        new("network", "", "tools", typeof(NetworkViewModel)),
        new("latency", "", "tools", typeof(LatencyViewModel)),
        new("benchmarks", "", "tools", typeof(BenchmarksViewModel)),
        // Sistema
        new("history", "", "system", typeof(HistoryViewModel)),
        new("settings", "", "system", typeof(SettingsViewModel)),
    };

    public static void Register(IServiceCollection sc)
    {
        foreach (var i in Items) sc.AddSingleton(i.ViewModelType);
        sc.AddSingleton<DiagnosticsState>();
    }
}
