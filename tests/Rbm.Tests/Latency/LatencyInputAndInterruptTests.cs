using Rbm.Core;
using Rbm.Latency;
using Xunit;

namespace Rbm.Tests.Latency;

public class PollingRateEstimatorTests
{
    private const long Freq = 10_000_000; // QPC típico (10 MHz)

    /// <summary>Gera timestamps com período fixo + jitter determinístico.</summary>
    private static IEnumerable<long> Series(long start, double periodMs, int count, double jitterMs = 0, int seed = 1)
    {
        var rnd = new Random(seed);
        double t = start;
        for (int i = 0; i < count; i++)
        {
            double j = jitterMs == 0 ? 0 : (rnd.NextDouble() * 2 - 1) * jitterMs;
            yield return (long)Math.Round(t + j * Freq / 1000.0);
            t += periodMs * Freq / 1000.0;
        }
    }

    [Theory]
    [InlineData(8.0, 125)]
    [InlineData(4.0, 250)]
    [InlineData(2.0, 500)]
    [InlineData(1.0, 1000)]
    [InlineData(0.5, 2000)]
    [InlineData(0.25, 4000)]
    [InlineData(0.125, 8000)]
    public void EstimatesStandardRates(double periodMs, int expectedHz)
    {
        var est = new PollingRateEstimator(Freq);
        foreach (var ts in Series(1_000_000, periodMs, 1000, jitterMs: periodMs * 0.05)) est.Add(7, ts);
        var e = est.GetEstimate(7)!;
        Assert.Equal(expectedHz, e.NearestStandardRateHz);
        Assert.InRange(e.EstimatedRateHz, expectedHz * 0.97, expectedHz * 1.03);
        Assert.InRange(e.MedianIntervalMs, periodMs * 0.97, periodMs * 1.03);
        Assert.True(e.StabilityPercent > 95);
        Assert.Equal(Confidence.High, e.Confidence);
    }

    [Fact]
    public void PausesAboveThreshold_AreIgnored()
    {
        var est = new PollingRateEstimator(Freq);
        long t = 0;
        for (int burst = 0; burst < 5; burst++)
        {
            for (int i = 0; i < 200; i++) { est.Add(1, t); t += Freq / 1000; } // 1 kHz
            t += Freq / 2; // pausa de 500 ms (mouse parado)
        }
        var e = est.GetEstimate(1)!;
        Assert.Equal(4, e.PausesIgnored);
        Assert.Equal(995, e.IntervalCount);
        Assert.Equal(1000, e.NearestStandardRateHz);
        Assert.Equal(1.0, e.MedianIntervalMs, 6);
        Assert.Equal(1.0, e.MeanIntervalMs, 6); // pausas não contaminam a média
    }

    [Fact]
    public void MissedReports_DoNotMoveTheMedian()
    {
        // 1000 Hz com 20% de relatórios ausentes (movimento lento): intervalos de 2 ms ocasionais
        var est = new PollingRateEstimator(Freq);
        long t = 0; var rnd = new Random(3);
        for (int i = 0; i < 2000; i++) { t += Freq / 1000 * (rnd.NextDouble() < 0.2 ? 2 : 1); est.Add(2, t); }
        var e = est.GetEstimate(2)!;
        Assert.Equal(1000, e.NearestStandardRateHz);
        Assert.True(e.MeanIntervalMs > e.MedianIntervalMs);
        Assert.Equal(2.0, e.P99IntervalMs, 6);
        Assert.Equal(1.0, e.P1IntervalMs, 6);
    }

    [Fact]
    public void Devices_AreTrackedSeparately_AndOutOfOrderIgnored()
    {
        var est = new PollingRateEstimator(Freq);
        foreach (var ts in Series(0, 1.0, 300)) est.Add(new IntPtr(10), ts);
        foreach (var ts in Series(0, 8.0, 300)) est.Add(new IntPtr(20), ts);
        est.Add(10, 5); // muito antigo: fora de ordem
        Assert.Equal(1000, est.GetEstimate(10)!.NearestStandardRateHz);
        Assert.Equal(125, est.GetEstimate(20)!.NearestStandardRateHz);
        Assert.Equal(2, est.GetAllEstimates().Count);
        est.Reset(20);
        Assert.Null(est.GetEstimate(20));
    }

    [Fact]
    public void TooFewIntervals_ReturnsNull_ThenLowConfidence()
    {
        var est = new PollingRateEstimator(Freq);
        foreach (var ts in Series(0, 1.0, 5)) est.Add(1, ts);
        Assert.Null(est.GetEstimate(1));
        foreach (var ts in Series(Freq, 1.0, 30)) est.Add(1, ts);
        var e = est.GetEstimate(1)!;
        Assert.Equal(Confidence.Low, e.Confidence);
    }

    [Fact]
    public void Window_KeepsOnlyMostRecentIntervals()
    {
        var est = new PollingRateEstimator(Freq, windowSize: 100);
        long t = 0;
        for (int i = 0; i < 500; i++) { est.Add(1, t); t += Freq / 125; }   // 125 Hz antigo
        for (int i = 0; i < 101; i++) { est.Add(1, t); t += Freq / 1000; }  // 1000 Hz recente
        var e = est.GetEstimate(1)!;
        Assert.Equal(100, e.IntervalCount);
        Assert.Equal(1000, e.NearestStandardRateHz);
    }

    [Fact]
    public void BunchedEvents_AreFlagged()
    {
        var est = new PollingRateEstimator(Freq);
        long t = 0;
        for (int i = 0; i < 300; i++)
        {
            t += Freq / 1000; est.Add(1, t);
            if (i % 3 == 0) est.Add(1, t + 10); // 1 µs depois: agrupamento da fila de mensagens
        }
        var e = est.GetEstimate(1)!;
        Assert.Contains(e.Notes, n => n.Contains("agrupados"));
    }

    [Theory]
    [InlineData(130, 125)]
    [InlineData(700, 500)]
    [InlineData(750, 1000)]
    [InlineData(960, 1000)]
    [InlineData(3000, 4000)]
    [InlineData(12000, 8000)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, 0)]
    public void NearestStandardRate_UsesLogDistance(double hz, int expected) =>
        Assert.Equal(expected, PollingRateEstimator.NearestStandardRate(hz));

    [Fact]
    public void Notes_StateWhatIsMeasured()
    {
        var e = PollingRateEstimator.Estimate(1, Enumerable.Repeat(1.0, 200).ToArray(), 0)!;
        Assert.Contains(e.Notes, n => n.Contains("não a latência de clique"));
    }
}

public class SelectiveSuspendInterpreterTests
{
    [Fact]
    public void NoValues_IsNotConfigured() =>
        Assert.Equal(SelectiveSuspendState.NotConfigured, SelectiveSuspendInterpreter.Interpret(null, null, null).State);

    [Fact]
    public void HidSelectiveSuspendDisabled_WinsOverEnhancedPowerManagement()
    {
        var r = SelectiveSuspendInterpreter.Interpret(0, 1, 1);
        Assert.Equal(SelectiveSuspendState.Disabled, r.State);
        Assert.Contains("SelectiveSuspendEnabled=0", r.Interpretation);
        Assert.Contains("AllowIdleIrpInD3=1", r.Interpretation);
    }

    [Fact]
    public void SelectiveSuspendEnabled_IsEnabled() =>
        Assert.Equal(SelectiveSuspendState.Enabled, SelectiveSuspendInterpreter.Interpret(1, null, null).State);

    [Fact]
    public void Contradictory_IsMixed() =>
        Assert.Equal(SelectiveSuspendState.Mixed, SelectiveSuspendInterpreter.Interpret(0, null, null, selectiveSuspendOn: 1).State);

    [Theory]
    [InlineData(1, SelectiveSuspendState.Enabled)]
    [InlineData(0, SelectiveSuspendState.Disabled)]
    public void EnhancedPowerManagementOnly(int epm, SelectiveSuspendState expected) =>
        Assert.Equal(expected, SelectiveSuspendInterpreter.Interpret(null, epm, null).State);

    [Fact]
    public void ToInt_HandlesRegistryShapes()
    {
        Assert.Equal(1, SelectiveSuspendInterpreter.ToInt(1));
        Assert.Equal(1, SelectiveSuspendInterpreter.ToInt(1L));
        Assert.Equal(1, SelectiveSuspendInterpreter.ToInt(new byte[] { 1 }));          // REG_BINARY de 1 byte (comum em HID)
        Assert.Equal(0, SelectiveSuspendInterpreter.ToInt(new byte[] { 0, 0, 0, 0 }));
        Assert.Equal(7, SelectiveSuspendInterpreter.ToInt(" 7 "));
        Assert.Null(SelectiveSuspendInterpreter.ToInt(null));
        Assert.Null(SelectiveSuspendInterpreter.ToInt(Array.Empty<byte>()));
        Assert.Null(SelectiveSuspendInterpreter.ToInt(new[] { "a" }));
    }

    [Fact]
    public void GlobalInterpretation_MentionsBothPowerSources()
    {
        var s = SelectiveSuspendInterpreter.InterpretGlobal(0, 1);
        Assert.Contains("na tomada: Desativada", s);
        Assert.Contains("na bateria: Ativada", s);
    }

    [Theory]
    [InlineData("Mouse", "Mouse compatível com HID", InputDeviceCategory.Mouse)]
    [InlineData("Keyboard", "Dispositivo de teclado HID", InputDeviceCategory.Keyboard)]
    [InlineData("XnaComposite", "Xbox 360 Controller for Windows", InputDeviceCategory.GameController)]
    [InlineData("HIDClass", "Controlador de jogo compatível com HID", InputDeviceCategory.GameController)]
    [InlineData("HIDClass", "HID-compliant consumer control device", InputDeviceCategory.OtherHid)]
    public void Categorize_InputDevices(string cls, string name, InputDeviceCategory expected) =>
        Assert.Equal(expected, InputDeviceInspector.Categorize(cls, name));
}

public class InterruptPolicyInterpreterTests
{
    [Theory]
    [InlineData(4294967293UL, -3L)]
    [InlineData(4294967290UL, -6L)]
    [InlineData(39UL, 39L)]
    [InlineData(0UL, 0L)]
    public void NormalizeIrq_MapsMsiToNegative(ulong raw, long expected) =>
        Assert.Equal(expected, InterruptPolicyInterpreter.NormalizeIrq(raw));

    [Fact]
    public void ParseAllocatedResources_GroupsIrqsByDevice()
    {
        var refs = new[]
        {
            (@"\\PC\root\cimv2:Win32_IRQResource.IRQNumber=4294967293", @"\\PC\root\cimv2:Win32_PnPEntity.DeviceID=""PCI\\VEN_1022&DEV_1634&SUBSYS_7C961462&REV_00\\3&11583659&0&11"""),
            (@"\\PC\root\cimv2:Win32_IRQResource.IRQNumber=4294967292", @"\\PC\root\cimv2:Win32_PnPEntity.DeviceID=""PCI\\VEN_1022&DEV_1634&SUBSYS_7C961462&REV_00\\3&11583659&0&11"""),
            (@"\\PC\root\cimv2:Win32_IRQResource.IRQNumber=39", @"\\PC\root\cimv2:Win32_PnPEntity.DeviceID=""PCI\\VEN_1002&DEV_1637&SUBSYS_9C961462&REV_00\\4&EB91FFF&0&0141"""),
            (@"\\PC\root\cimv2:Win32_DeviceMemoryAddress.StartingAddress=4026531840", @"\\PC\root\cimv2:Win32_PnPEntity.DeviceID=""PCI\\X\\Y"""),
        };
        var map = InterruptPolicyInterpreter.ParseAllocatedResources(refs);
        Assert.Equal(2, map.Count);
        var port = map[@"pci\ven_1022&dev_1634&subsys_7c961462&rev_00\3&11583659&0&11"]; // sem diferenciar maiúsculas
        Assert.Equal(new long[] { -3, -4 }, port.ToArray());
        Assert.Equal(InterruptMode.Msi, InterruptPolicyInterpreter.EffectiveMode(port));
        Assert.Equal(InterruptMode.LineBased, InterruptPolicyInterpreter.EffectiveMode(map[@"PCI\VEN_1002&DEV_1637&SUBSYS_9C961462&REV_00\4&EB91FFF&0&0141"]));
        Assert.Equal(InterruptMode.Unknown, InterruptPolicyInterpreter.EffectiveMode(Array.Empty<long>()));
    }

    [Fact]
    public void ParseAffinityMask_ReadsBinaryLittleEndian()
    {
        Assert.Equal(0x0000000000000105UL, InterruptPolicyInterpreter.ParseAffinityMask(new byte[] { 0x05, 0x01, 0, 0, 0, 0, 0, 0 }));
        Assert.Equal(0x4UL, InterruptPolicyInterpreter.ParseAffinityMask(4));
        Assert.Equal(0xFFFFFFFFUL, InterruptPolicyInterpreter.ParseAffinityMask(-1));
        Assert.Null(InterruptPolicyInterpreter.ParseAffinityMask(null));
        Assert.Equal("0, 2, 8", InterruptPolicyInterpreter.MaskToCpuList(0x105));
    }

    [Theory]
    [InlineData(0, "IrqPolicyMachineDefault")]
    [InlineData(4, "IrqPolicySpecifiedProcessors")]
    [InlineData(5, "IrqPolicySpreadMessagesAcrossAllProcessors")]
    public void DevicePolicyNames(int v, string expected) =>
        Assert.StartsWith(expected, InterruptPolicyInterpreter.DevicePolicyName(v));

    [Fact]
    public void Interpret_ExplainsRegistryVersusAllocation()
    {
        var a = InterruptPolicyInterpreter.Interpret(1, null, InterruptMode.LineBased, new long[] { 16 }, null, null, null);
        Assert.Contains("MSISupported = 1", a);
        Assert.Contains("pode não suportar MSI", a);

        var b = InterruptPolicyInterpreter.Interpret(null, null, InterruptMode.Msi, new long[] { -3 }, 4, 0x3, 3);
        Assert.Contains("MSISupported ausente", b);
        Assert.Contains("pci.sys", b);
        Assert.Contains("CPUs atribuídas: 0, 1", b);
        Assert.Contains("IrqPriorityHigh", b);
        Assert.Contains("Somente leitura", b);

        var c = InterruptPolicyInterpreter.Interpret(0, 8, InterruptMode.LineBased, new long[] { 39 }, null, null, null);
        Assert.Contains("MSI desabilitado", c);
        Assert.Contains("Limite de mensagens: 8", c);
    }

    [Theory]
    [InlineData("Display", "amdkmdag", "AMD Radeon RX 6600", PciDeviceCategory.Gpu)]
    [InlineData("Net", "rt640x64", "Realtek PCIe GbE", PciDeviceCategory.Network)]
    [InlineData("SCSIAdapter", "stornvme", "Controladora NVMe", PciDeviceCategory.Storage)]
    [InlineData("HDC", "storahci", "Standard SATA AHCI Controller", PciDeviceCategory.Storage)]
    [InlineData("USB", "USBXHCI", "USB xHCI Compliant Host Controller", PciDeviceCategory.UsbController)]
    [InlineData("System", "HDAudBus", "High Definition Audio Controller", PciDeviceCategory.Audio)]
    [InlineData("System", "pci", "PCI Express Root Port", PciDeviceCategory.Bridge)]
    [InlineData("System", "amdpsp", "AMD PSP 10.0 Device", PciDeviceCategory.Other)]
    public void Categorize_PciDevices(string cls, string svc, string name, PciDeviceCategory expected) =>
        Assert.Equal(expected, InterruptModeInspector.Categorize(cls, svc, name));

    [Theory]
    [InlineData("{4d36e968-e325-11ce-bfc1-08002be10318}", "Display")]
    [InlineData("{4D36E972-E325-11CE-BFC1-08002BE10318}", "Net")]
    [InlineData("{36fc9e60-c465-11cf-8056-444553540000}", "USB")]
    [InlineData("{00000000-0000-0000-0000-000000000000}", null)]
    [InlineData("not-a-guid", null)]
    public void ClassFromGuid_MapsKnownSetupClasses(string guid, string? expected) =>
        Assert.Equal(expected, InterruptModeInspector.ClassFromGuid(guid));
}

public class TimerResolutionTests
{
    [Theory]
    [InlineData(156250u, 15.625)]
    [InlineData(5000u, 0.5)]
    [InlineData(10000u, 1.0)]
    public void ToMs_Converts100nsUnits(uint units, double ms) =>
        Assert.Equal(ms, TimerResolutionInspector.ToMs(units), 9);

    [Fact]
    public void Query_ReturnsConsistentValues_WithoutElevation()
    {
        var r = TimerResolutionInspector.Query();
        Assert.True(r.FinestMs > 0);
        Assert.True(r.FinestMs <= r.CurrentMs + 1e-9);
        Assert.True(r.CurrentMs <= r.CoarsestMs + 1e-9);
        Assert.Contains("2004", r.Explanation);
        Assert.Contains(r.References, x => x.Contains("learn.microsoft.com"));
    }

    private const string SampleReport = """
        <html><head><style>td { color: red; }</style><script>var x = "<td>noise</td>";</script></head><body>
        <table>
        <tr><td class="nameBold"><span>Platform Timer Resolution:Platform Timer Resolution</span></td></tr>
        <tr><td colspan="2">The default platform timer resolution is 15.6ms (15625000ns) and should be used whenever the system is idle.</td></tr>
        <tr><td class="info"><table><tr><td class="name">Current Timer Resolution (100ns units)</td><td>10000</td></tr></table></td></tr>
        <tr><td class="nameBold"><span>Platform Timer Resolution:Outstanding Timer Request</span></td></tr>
        <tr><td colspan="2">A program or service has requested a timer resolution smaller than the platform maximum timer resolution.</td></tr>
        <tr><td class="info"><table>
          <tr><td class="name">Requested Period</td><td>10000</td></tr>
          <tr><td class="name">Requesting Process ID</td><td>4321</td></tr>
          <tr><td class="name">Requesting Process Path</td><td>\Device\HarddiskVolume3\Program Files\Game\game.exe</td></tr>
        </table></td></tr>
        <tr><td class="nameBold"><span>Platform Timer Resolution:Outstanding Timer Request</span></td></tr>
        <tr><td colspan="2">A program or service has requested a timer resolution smaller than the platform maximum timer resolution.</td></tr>
        <tr><td class="info"><table>
          <tr><td class="name">Requested Period</td><td>5000</td></tr>
          <tr><td class="name">Requesting Process ID</td><td>888</td></tr>
          <tr><td class="name">Requesting Process Path</td><td>\Device\HarddiskVolume3\Windows\System32\audiodg.exe</td></tr>
        </table></td></tr>
        <tr><td class="nameBold"><span>Platform Timer Resolution:Timer Request Stack</span></td></tr>
        <tr><td class="info"><table><tr><td class="name">Calling Module Stack</td><td>\Device\HarddiskVolume3\Windows\System32\ntdll.dll</td></tr></table></td></tr>
        <tr><td class="nameBold"><span>USB Suspend:USB Device not Entering Suspend</span></td></tr>
        <tr><td class="info"><table><tr><td class="name">Device Name</td><td>USB Root Hub</td></tr></table></td></tr>
        </table></body></html>
        """;

    [Fact]
    public void EnergyReportParser_ExtractsOutstandingRequests()
    {
        var r = EnergyReportTimerParser.Parse(SampleReport);
        Assert.Equal(1.0, r.CurrentResolutionMs);
        Assert.Equal(2, r.Requests.Count);
        Assert.Equal(4321, r.Requests[0].ProcessId);
        Assert.Equal(10000u, r.Requests[0].RequestedPeriod100ns);
        Assert.Equal(1.0, r.Requests[0].RequestedPeriodMs);
        Assert.EndsWith("game.exe", r.Requests[0].ProcessPath);
        Assert.Equal(888, r.Requests[1].ProcessId);
        Assert.Equal(0.5, r.Requests[1].RequestedPeriodMs);
    }

    [Fact]
    public void EnergyReportParser_HandlesPortugueseLabels()
    {
        const string html = """
            <table><tr><td>Resolução do Timer da Plataforma:Solicitação de Timer Pendente</td></tr>
            <tr><td>Período Solicitado</td><td>10000</td></tr>
            <tr><td>ID do Processo Solicitante</td><td>77</td></tr>
            <tr><td>Caminho do Processo Solicitante</td><td>\Device\HarddiskVolume3\app.exe</td></tr></table>
            """;
        var r = EnergyReportTimerParser.Parse(html);
        var q = Assert.Single(r.Requests);
        Assert.Equal(77, q.ProcessId);
        Assert.Equal(10000u, q.RequestedPeriod100ns);
    }

    [Fact]
    public void EnergyReportParser_EmptyOrUnrelated_ReturnsNothing()
    {
        Assert.Empty(EnergyReportTimerParser.Parse("").Requests);
        Assert.Empty(EnergyReportTimerParser.Parse("<table><tr><td>USB Suspend:USB Device not Entering Suspend</td></tr></table>").Requests);
    }
}

public class DriverHintsTests
{
    [Theory]
    [InlineData("ndis.sys", DriverHints.Network)]
    [InlineData("Netwtw10.sys", DriverHints.Network)]
    [InlineData("rt640x64.sys", DriverHints.Network)]
    [InlineData("amdkmdag.sys", DriverHints.Graphics)]
    [InlineData("nvlddmkm.sys", DriverHints.Graphics)]
    [InlineData("dxgkrnl.sys", DriverHints.Graphics)]
    [InlineData("storport.sys", DriverHints.Storage)]
    [InlineData("HDAudBus.sys", DriverHints.Audio)]
    [InlineData("RTKVHD64.sys", DriverHints.Audio)]
    [InlineData("USBXHCI.SYS", DriverHints.UsbInput)]
    [InlineData("ACPI.sys", DriverHints.Platform)]
    [InlineData("Wdf01000.sys", DriverHints.Framework)]
    [InlineData("ntoskrnl.exe", DriverHints.Kernel)]
    [InlineData("foo.sys", "")]
    public void Categorize_KnownModules(string module, string expected) =>
        Assert.Equal(expected, DriverHints.Categorize(module));

    [Fact]
    public void Wdf_HintExplainsAttributionLimit() =>
        Assert.Contains("não é identificável", DriverHints.Hint("Wdf01000.sys"));
}
