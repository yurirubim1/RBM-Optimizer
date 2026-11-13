using System.Diagnostics;
using System.IO;
using Rbm.Core;
using Rbm.Hardware;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

public sealed record LicenseInfo(string Component, string License, string Use);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _settings;
    private readonly ITelemetryService _telemetry;
    private readonly LhmSensorProvider _lhm;
    private bool _loading;
    public override string Key => "settings";

    public string[] Languages { get; } = { "Português (Brasil)", "English" };
    public string[] Intervals { get; } = { "1 s", "2 s", "5 s" };

    [ObservableProperty] private string _language = "Português (Brasil)";
    [ObservableProperty] private string _interval = "1 s";
    [ObservableProperty] private bool _sensorsEnabled = true;
    [ObservableProperty] private bool _restoreBeforeRisky = true;
    [ObservableProperty] private bool _restoreBeforeProfile = true;
    [ObservableProperty] private string _sensorStatus = "";
    public string DataFolder => RbmPaths.DataRoot;
    public string DatabasePath => RbmPaths.Database;
    public string AuditFolder => RbmPaths.AgentAudit;
    public string VersionText => $"RBM Optimizer {App.Version} · .NET {Environment.Version} · Windows {Environment.OSVersion.Version}";

    public IReadOnlyList<LicenseInfo> Licenses { get; } = new List<LicenseInfo>
    {
        new("LibreHardwareMonitorLib", "MPL-2.0", "Sensores de GPU/CPU/armazenamento (opcional)"),
        new("Microsoft.Diagnostics.Tracing.TraceEvent", "MIT", "Captura ETW de DPC/ISR"),
        new("Microsoft.Data.Sqlite / SQLite", "MIT / domínio público", "Banco local de histórico"),
        new("CommunityToolkit.Mvvm", "MIT", "Infraestrutura MVVM"),
        new("Serilog + Sinks.File", "Apache-2.0", "Logs estruturados"),
        new("Microsoft.Extensions.*", "MIT", "Injeção de dependências e logging"),
        new("PresentMon (Intel, baixado sob demanda)", "MIT", "Captura de frames via ETW, sem injeção"),
    };

    public SettingsViewModel(ISettingsStore settings, ITelemetryService telemetry, LhmSensorProvider lhm)
    {
        _settings = settings; _telemetry = telemetry; _lhm = lhm;
    }

    public override Task OnNavigatedToAsync()
    {
        _loading = true;
        Language = _settings.Get("language", "pt-BR").StartsWith("en") ? "English" : "Português (Brasil)";
        Interval = $"{(int)_telemetry.Interval.TotalSeconds} s";
        SensorsEnabled = _settings.Get("sensors.lhm", true);
        RestoreBeforeRisky = _settings.Get("restore.pointBeforeRisky", true);
        RestoreBeforeProfile = _settings.Get("restore.pointBeforeProfile", true);
        SensorStatus = _lhm.StatusMessage == "Não inicializado." ? "Os sensores começam a funcionar alguns segundos depois que o app abre." : _lhm.StatusMessage + (_lhm.PawnIoInstalled ? "" : " (O RBM não instala drivers no sistema.)");
        _loading = false;
        return Task.CompletedTask;
    }

    partial void OnLanguageChanged(string value)
    {
        if (_loading) return;
        _settings.Set("language", value == "English" ? "en" : "pt-BR");
        App.Notify.Info("Idioma", "Reinicie o RBM para aplicar o idioma em toda a interface.");
    }

    partial void OnIntervalChanged(string value)
    {
        if (_loading) return;
        var secs = int.Parse(value.Split(' ')[0]);
        _telemetry.Interval = TimeSpan.FromSeconds(secs);
        _settings.Set("telemetry.interval", secs);
    }

    partial void OnSensorsEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.Set("sensors.lhm", value);
        App.Notify.Info("Sensores", "Reinicie o RBM para aplicar.");
    }

    partial void OnRestoreBeforeRiskyChanged(bool value) { if (!_loading) _settings.Set("restore.pointBeforeRisky", value); }
    partial void OnRestoreBeforeProfileChanged(bool value) { if (!_loading) _settings.Set("restore.pointBeforeProfile", value); }

    [RelayCommand]
    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(dir)) { App.Notify.Info("Pasta inexistente", $"{dir} ainda não foi criada (é criada no primeiro uso)."); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
