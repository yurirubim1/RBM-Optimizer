using Rbm.Core;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Rbm.Hardware;

/// <summary>
/// Sensores via LibreHardwareMonitorLib (MPL-2.0).
/// • GPU AMD (ADL) / NVIDIA (NVAPI) / Intel: temperatura, hotspot, clocks, potência, ventoinha — em modo usuário.
/// • CPU (temperatura/potência) e SMART exigem o driver PawnIO instalado pelo usuário E processo elevado;
///   sem isso esses sensores ficam "indisponíveis" (nunca estimados).
/// O RBM não instala drivers de kernel.
/// </summary>
public sealed class LhmSensorProvider : ISensorProvider
{
    private Computer? _computer;
    private readonly object _gate = new();
    private SystemProfile? _profile;
    private DateTime _lastUpdate = DateTime.MinValue;

    public string Name => "LibreHardwareMonitor";
    public bool IsAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Não inicializado.";
    public bool PawnIoInstalled { get; private set; }
    /// <summary>Intervalo mínimo entre atualizações (as leituras de sensores têm custo).</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMilliseconds(900);

    public void Initialize(SystemProfile profile)
    {
        _profile = profile;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
            PawnIoInstalled = k is not null;
        try
        {
            var c = new Computer { IsGpuEnabled = true, IsCpuEnabled = true, IsStorageEnabled = true, IsMotherboardEnabled = false, IsMemoryEnabled = false, IsControllerEnabled = false, IsNetworkEnabled = false, IsPsuEnabled = false, IsBatteryEnabled = false };
            c.Open();
            _computer = c;
            IsAvailable = true;
            var hasCpuTemp = c.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).SelectMany(h => { h.Update(); return h.Sensors; }).Any(s => s.SensorType == SensorType.Temperature && s.Value is > 1 and < 150);
            StatusMessage = hasCpuTemp ? "Sensores de GPU e CPU ativos."
                : PawnIoInstalled ? "Sensores de GPU ativos. Temperatura da CPU requer execução elevada (driver PawnIO detectado)."
                : "Sensores de GPU ativos. Temperatura/potência da CPU indisponíveis: exigem o driver PawnIO (não instalado) e execução elevada.";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusMessage = "LibreHardwareMonitor indisponível: " + ex.Message;
        }
    }

    public void Enrich(TelemetrySnapshot s)
    {
        var c = _computer;
        if (c is null) return;
        lock (_gate)
        {
            bool refresh = DateTime.UtcNow - _lastUpdate >= MinInterval;
            foreach (var hw in c.Hardware)
            {
                try
                {
                    if (refresh) hw.Update();
                    switch (hw.HardwareType)
                    {
                        case HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel:
                            EnrichGpu(hw, s); break;
                        case HardwareType.Cpu:
                            EnrichCpu(hw, s); break;
                        case HardwareType.Storage:
                            EnrichStorage(hw, s); break;
                    }
                }
                catch { /* sensor individual falhou: segue */ }
            }
            if (refresh) _lastUpdate = DateTime.UtcNow;
        }
    }

    private static float? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Value is not null);
            if (s is not null) return s.Value;
        }
        return null;
    }

    private static Reading<double> R(float? v, string src) => v is { } f && float.IsFinite(f) ? Reading<double>.Of(f, src) : Reading<double>.None;

    /// <summary>
    /// Temperaturas fora de 1..150 °C são tratadas como indisponíveis: sem acesso ao hardware
    /// (ex.: CPU sem PawnIO) o LHM expõe o sensor com valor 0, o que seria um dado falso.
    /// </summary>
    private static Reading<double> T(float? v, string src) => v is { } f && float.IsFinite(f) && f > 1 && f < 150 ? Reading<double>.Of(f, src) : Reading<double>.None;

    /// <summary>Potência/clock zero com hardware ativo também indica ausência de acesso.</summary>
    private static Reading<double> P(float? v, string src) => v is { } f && float.IsFinite(f) && f > 0 ? Reading<double>.Of(f, src) : Reading<double>.None;

    private static void EnrichGpu(IHardware hw, TelemetrySnapshot s)
    {
        var target = s.Gpus.FirstOrDefault(g => Similar(g.Name, hw.Name)) ?? (s.Gpus.Count(g => !string.IsNullOrEmpty(g.Name)) == 1 ? s.Gpus[0] : null);
        if (target is null) return;
        target.TemperatureC = T(Find(hw, SensorType.Temperature, "GPU Core", "GPU Temperature", "GPU Edge"), "LHM");
        target.HotspotC = T(Find(hw, SensorType.Temperature, "GPU Hot Spot", "GPU Hotspot", "GPU Junction"), "LHM");
        target.CoreClockMHz = R(Find(hw, SensorType.Clock, "GPU Core", "GPU Shader"), "LHM");
        target.MemoryClockMHz = R(Find(hw, SensorType.Clock, "GPU Memory"), "LHM");
        target.PowerW = R(Find(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU PPT", "GPU Core", "GPU Total"), "LHM");
        target.FanRpm = R(Find(hw, SensorType.Fan, "GPU Fan", "GPU Fan 1", "GPU"), "LHM");
    }

    private static void EnrichCpu(IHardware hw, TelemetrySnapshot s)
    {
        s.CpuTemperatureC = T(Find(hw, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Average", "Core Max"), "LHM");
        s.CpuPackagePowerW = P(Find(hw, SensorType.Power, "Package", "CPU Package"), "LHM");
    }

    private void EnrichStorage(IHardware hw, TelemetrySnapshot s)
    {
        var t = Find(hw, SensorType.Temperature, "Temperature", "Composite Temperature", "Temperature 1");
        if (t is null || _profile is null) return;
        var disk = _profile.Disks.FirstOrDefault(d => Similar(d.Model, hw.Name));
        if (disk is null) return;
        var tel = s.Disks.FirstOrDefault(d => d.Instance.StartsWith(disk.Number + " ", StringComparison.Ordinal) || d.Instance == disk.Number.ToString());
        if (tel is not null) tel.TemperatureC = T(t, "LHM");
    }

    private static bool Similar(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string N(string x) => new string(x.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var na = N(a); var nb = N(b);
        return na.Contains(nb) || nb.Contains(na);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
