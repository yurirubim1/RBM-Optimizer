using Rbm.Core;
using Rbm.Network.Adapters;
using Rbm.Network.Diagnostics;
using Rbm.Network.Latency;
using Xunit;

namespace Rbm.Tests.Network;

public class AdvancedPropertyAndDiagnosticsTests
{
    private const string Guid1 = "{954571B5-1AA9-4E20-9CD8-8FE64F889591}";

    private static AdvancedProperty Enum(string kw, string current, params (string Reg, string Disp)[] opts) => new()
    {
        InterfaceGuid = Guid1, AdapterName = "Ethernet", InstanceId = $"{Guid1}::{kw}", RegistryKeyword = kw, DisplayName = kw,
        DisplayValue = opts.FirstOrDefault(o => o.Reg == current).Disp, RegistryValue = new[] { current }, DefaultRegistryValue = opts[0].Reg,
        ValueKind = AdvancedValueKind.Enumeration, ValidOptions = opts.Select(o => new AdvancedPropertyOption(o.Reg, o.Disp)).ToList(),
        Known = AdvancedKeywords.Lookup(kw),
    };

    private static readonly AdvancedProperty SpeedDuplex = Enum("*SpeedDuplex", "0",
        ("0", "Auto Negociação"), ("1", "10 Mbps Half Duplex"), ("2", "10 Mbps Full Duplex"), ("3", "100 Mbps Half Duplex"),
        ("4", "100 Mbps Full Duplex"), ("6", "1.0 Gbps Full Duplex"));

    private static readonly AdvancedProperty ReceiveBuffers = new()
    {
        InterfaceGuid = Guid1, AdapterName = "Ethernet", InstanceId = $"{Guid1}::*ReceiveBuffers", RegistryKeyword = "*ReceiveBuffers",
        DisplayName = "Receive Buffers", RegistryValue = new[] { "512" }, DefaultRegistryValue = "512",
        ValueKind = AdvancedValueKind.Numeric, Range = new NumericRange(32, 512, 8, 10),
    };

    [Fact]
    public void ValidateShape_RejectsInjectionAndBlockedKeywords()
    {
        Assert.True(AdvancedPropertyValidator.ValidateShape(new(Guid1, "*EEE", "0")).Success);
        Assert.False(AdvancedPropertyValidator.ValidateShape(new("not-a-guid", "*EEE", "0")).Success);
        Assert.False(AdvancedPropertyValidator.ValidateShape(new(Guid1, "*EEE' OR 1=1", "0")).Success);
        Assert.False(AdvancedPropertyValidator.ValidateShape(new(Guid1, "*EEE", "0; Remove-Item")).Success);
        Assert.Equal("blocked_keyword", AdvancedPropertyValidator.ValidateShape(new(Guid1, "NetworkAddress", "001122334455")).ErrorCode);
        Assert.Equal("blocked_keyword", AdvancedPropertyValidator.ValidateShape(new(Guid1, "RegVlanid", "0")).ErrorCode);
        Assert.False(AdvancedPropertyValidator.ValidateShape(null).Success);
    }

    [Fact]
    public void ValidateValue_EnumerationAcceptsOnlyDriverValues()
    {
        Assert.True(AdvancedPropertyValidator.ValidateValue(SpeedDuplex, "6").Success);
        var bad = AdvancedPropertyValidator.ValidateValue(SpeedDuplex, "5");
        Assert.False(bad.Success);
        Assert.Equal("value_not_allowed", bad.ErrorCode);
    }

    [Theory]
    [InlineData("32", true)]
    [InlineData("256", true)]
    [InlineData("512", true)]
    [InlineData("33", false)]   // fora do passo
    [InlineData("520", false)]  // acima do máximo
    [InlineData("16", false)]   // abaixo do mínimo
    [InlineData("abc", false)]
    public void ValidateValue_NumericRangeAndStep(string v, bool ok) =>
        Assert.Equal(ok, AdvancedPropertyValidator.ValidateValue(ReceiveBuffers, v).Success);

    [Fact]
    public void ValidateValue_FreeTextIsBlocked()
    {
        var p = ReceiveBuffers with { ValueKind = AdvancedValueKind.FreeText, Range = null, DefaultRegistryValue = null };
        Assert.Equal("free_text_blocked", AdvancedPropertyValidator.ValidateValue(p, "1").ErrorCode);
    }

    [Fact]
    public void MaxSpeedFromSpeedDuplex_UsesCodesAndText()
    {
        Assert.Equal(1_000_000_000, AdvancedKeywords.MaxSpeedFromSpeedDuplex(SpeedDuplex));
        var p25 = Enum("*SpeedDuplex", "0", ("0", "Auto"), ("4", "100 Mbps Full"), ("12", "2,5 Gbps Full Duplex"));
        Assert.Equal(2_500_000_000, AdvancedKeywords.MaxSpeedFromSpeedDuplex(p25));
        Assert.Null(AdvancedKeywords.MaxSpeedFromSpeedDuplex(null));
    }

    [Fact]
    public void RollbackRequest_UsesPreviousValueAndExpectsNew()
    {
        var r = new AdvancedPropertySetResponse(true, Guid1, "Ethernet", "*EEE", "1", "Activado", "0", "Desactivado", true, "ok", null);
        Assert.Equal(new AdvancedPropertySetRequest(Guid1, "*EEE", "1", "0"), r.RollbackRequest);
        Assert.Null((r with { Success = false }).RollbackRequest);
    }

    // ---------------------------------------------------------------- Diagnósticos

    private static NetworkAdapterInfo Ethernet(long speed) => new()
    {
        InterfaceGuid = Guid1, Name = "Ethernet", Description = "Realtek PCIe GbE Family Controller", Kind = AdapterKind.Ethernet,
        IsHardware = true, Status = AdapterStatus.Up, LinkSpeedBps = speed, IsDefaultRouteInterface = true,
        Driver = new AdapterDriverInfo("Realtek", null, "10.79.50.1003", new DateTime(2025, 10, 3), "Realtek", true, null, "oem22.inf"),
    };

    private static NetworkInventory Inv(params NetworkAdapterInfo[] a) => new(DateTimeOffset.Now, a, 12, Array.Empty<string>());

    private static AdapterAdvancedSettings Adv(params AdvancedProperty[] props) => new(Guid1, "Ethernet", props,
        new PowerManagementInfo(PowerSavingState.Enabled, "Registro", null, null, null, null, null),
        new OffloadSummary(null, null, null, null, null, null, null, null, null, null, null, null, null, null), Array.Empty<string>());

    private static TargetLatencyResult Lat(TargetRole role, string host, params double?[] rtts) =>
        new(new LatencyTarget(host, host, role), host, LatencyMath.Compute(rtts), Array.Empty<LatencySample>(), null);

    [Fact]
    public void Rule_LinkBelowCapability_FiresOn100MbpsGigabit()
    {
        var f = NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput { Inventory = Inv(Ethernet(100_000_000)), Advanced = new[] { Adv(SpeedDuplex) } });
        var x = Assert.Single(f, y => y.RuleId == "net.link.below-capability");
        Assert.Equal(Severity.Medium, x.Severity);
        Assert.Equal(Confidence.High, x.Confidence);
        Assert.DoesNotContain(NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput { Inventory = Inv(Ethernet(1_000_000_000)), Advanced = new[] { Adv(SpeedDuplex) } }),
            y => y.RuleId == "net.link.below-capability");
    }

    [Fact]
    public void Rule_LossAtGateway_IsLocal()
    {
        var gw = Lat(TargetRole.Gateway, "192.168.0.1", Enumerable.Range(0, 20).Select(i => i % 5 == 0 ? (double?)null : 1).ToArray());
        var f = NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput { Inventory = Inv(Ethernet(1_000_000_000)), Latency = new[] { gw } });
        var x = Assert.Single(f, y => y.RuleId == "net.loss.local");
        Assert.Equal(Severity.High, x.Severity); // 20%
    }

    [Fact]
    public void Rule_RemoteOnlyLoss_RequiresMoreThanOneRemoteTarget()
    {
        var clean = Enumerable.Repeat<double?>(1, 20).ToArray();
        var lossy = Enumerable.Range(0, 20).Select(i => i < 2 ? (double?)null : 20).ToArray();
        var gw = Lat(TargetRole.Gateway, "192.168.0.1", clean);
        var oneLossy = new NetworkDiagnosticInput { Latency = new[] { gw, Lat(TargetRole.PublicDns, "1.1.1.1", lossy), Lat(TargetRole.PublicDns, "8.8.8.8", clean) } };
        Assert.DoesNotContain(NetworkDiagnostics.Evaluate(oneLossy), y => y.RuleId == "net.loss.remote-only");
        var twoLossy = new NetworkDiagnosticInput { Latency = new[] { gw, Lat(TargetRole.PublicDns, "1.1.1.1", lossy), Lat(TargetRole.PublicDns, "8.8.8.8", lossy) } };
        Assert.Contains(NetworkDiagnostics.Evaluate(twoLossy), y => y.RuleId == "net.loss.remote-only");
    }

    [Fact]
    public void Rule_LanJitter()
    {
        var jittery = Enumerable.Range(0, 30).Select(i => (double?)(i % 2 == 0 ? 2 : 20)).ToArray();
        var f = NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput { Latency = new[] { Lat(TargetRole.Gateway, "192.168.0.1", jittery) } });
        Assert.Contains(f, y => y.RuleId == "net.jitter.lan");
    }

    [Fact]
    public void Rule_PowerSaving_OnlyOnDesktop()
    {
        var eee = Enum("*EEE", "1", ("0", "Desativado"), ("1", "Ativado"));
        var input = new NetworkDiagnosticInput { Inventory = Inv(Ethernet(1_000_000_000)), Advanced = new[] { Adv(eee) }, FormFactor = FormFactor.Desktop };
        var x = Assert.Single(NetworkDiagnostics.Evaluate(input), y => y.RuleId == "net.power.saving-desktop");
        Assert.Equal(ImpactLevel.Unknown, x.ExpectedImpact);
        Assert.DoesNotContain(NetworkDiagnostics.Evaluate(input with { FormFactor = FormFactor.Laptop }), y => y.RuleId == "net.power.saving-desktop");
    }

    [Fact]
    public void Rule_ErrorsIncreasing_AndDriverAge()
    {
        var delta = new AdapterCounterDelta(TimeSpan.FromMinutes(5), 50, 0, 0, 0, 10_000, 10_000, false);
        var old = Ethernet(1_000_000_000) with { Driver = AdapterDriverInfo.Unknown with { Date = new DateTime(2018, 1, 1), Provider = "Realtek", IsSigned = false } };
        var f = NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput
        {
            Inventory = Inv(old), Now = new DateTime(2026, 11, 9),
            CounterDeltas = new Dictionary<string, AdapterCounterDelta> { [Guid1] = delta },
        });
        Assert.Contains(f, y => y.RuleId == "net.errors.increasing" && y.Severity == Severity.High);
        Assert.Contains(f, y => y.RuleId == "net.driver.age");
        Assert.Contains(f, y => y.RuleId == "net.driver.unsigned");
    }

    [Fact]
    public void Evaluate_NoData_NoFindings() => Assert.Empty(NetworkDiagnostics.Evaluate(new NetworkDiagnosticInput()));
}
