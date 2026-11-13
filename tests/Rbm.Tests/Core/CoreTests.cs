using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Core.Tweaks;
using Rbm.Data;
using Rbm.Diagnostics;
using Rbm.Hardware;
using Rbm.Optimization;
using Rbm.Optimization.Providers;
using Rbm.Security;
using Xunit;

namespace Rbm.Tests.Core;

public class StatisticsTests
{
    [Fact]
    public void Summarize_ComputesPercentilesAndCi()
    {
        var s = Stats.Summarize(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Assert.Equal(10, s.Count);
        Assert.Equal(5.5, s.Mean, 6);
        Assert.Equal(5.5, s.Median, 6);
        Assert.Equal(9.91, s.P99, 2);
        Assert.True(s.Ci95Low < s.Mean && s.Ci95High > s.Mean);
    }

    [Fact]
    public void Summarize_EmptyIsNaN() => Assert.True(double.IsNaN(Stats.Summarize(Array.Empty<double>()).Mean));

    [Fact]
    public void Compare_NoiseIsNotSignificant()
    {
        var a = new double[] { 100, 101, 99, 100.5, 99.5 };
        var b = new double[] { 100.4, 99.8, 100.9, 99.6, 100.2 };
        var r = Stats.Compare(a, b, higherIsBetter: true);
        Assert.False(r.Significant);
        Assert.Contains("Sem diferença", r.Verdict);
    }

    [Fact]
    public void Compare_ClearGainIsSignificant()
    {
        var a = new double[] { 100, 101, 99, 100, 100 };
        var b = new double[] { 120, 121, 119, 120, 120 };
        var r = Stats.Compare(a, b, higherIsBetter: true);
        Assert.True(r.Significant);
        Assert.True(r.DeltaPercent > 19);
        Assert.Contains("Melhora", r.Verdict);
    }

    [Fact]
    public void Compare_LowerIsBetterDetectsRegression()
    {
        var r = Stats.Compare(new double[] { 10, 10.1, 9.9, 10 }, new double[] { 15, 15.2, 14.8, 15 }, higherIsBetter: false);
        Assert.True(r.Significant);
        Assert.Contains("Piora", r.Verdict);
    }

    [Theory]
    [InlineData(1, 12.706)]
    [InlineData(10, 2.228)]
    [InlineData(100, 1.984)]
    public void TCritical_MatchesTable(int df, double expected) => Assert.Equal(expected, Stats.TCritical95(df), 2);
}

public class SettingValueTests
{
    [Fact]
    public void SemanticEquals_HexAndDecimal() => Assert.True(new SettingValue(true, "DWord", "0x26").SemanticEquals(new SettingValue(true, "DWord", "38")));

    [Fact]
    public void SemanticEquals_AbsentVsPresent() => Assert.False(SettingValue.Absent.SemanticEquals(new SettingValue(true, "DWord", "0")));

    [Fact]
    public void RegistryTarget_Equivalents()
    {
        var t = new RegistryTarget { Hive = RegistryHive.HKCU, Path = "Software\\X", Name = "V", Kind = RegistryValueKind2.DWord, Value = "1", Equivalents = new List<string?> { null } };
        Assert.True(t.Matches(SettingValue.Absent));
        Assert.True(t.Matches(new SettingValue(true, "DWord", "1")));
        Assert.False(t.Matches(new SettingValue(true, "DWord", "0")));
    }

    [Fact]
    public void PowerTarget_PartialMatch()
    {
        var t = new PowerSettingTarget { Subgroup = Guid.NewGuid(), Setting = Guid.NewGuid(), Ac = 100, Dc = null };
        Assert.True(t.Matches(new SettingValue(true, "power", "ac=100;dc=5")));
        Assert.False(t.Matches(new SettingValue(true, "power", "ac=50;dc=5")));
    }

    [Fact]
    public void Unsigned_Dword_LargeValue()
    {
        var t = new RegistryTarget { Hive = RegistryHive.HKLM, Path = "SYSTEM\\X", Name = "NtfsDisableLastAccessUpdate", Value = "2147483650" };
        Assert.True(t.Matches(new SettingValue(true, "DWord", "0x80000002")));
    }
}

public class CatalogTests
{
    [Fact]
    public void EmbeddedCatalog_LoadsWithoutValidationErrors()
    {
        var c = TweakCatalog.LoadEmbedded();
        Assert.True(c.Tweaks.Count >= 60, $"Catálogo com {c.Tweaks.Count} itens");
        Assert.True(c.ValidationErrors.Count == 0, string.Join("\n", c.ValidationErrors));
        Assert.NotEmpty(c.Profiles);
    }

    [Fact]
    public void EmbeddedCatalog_HonestyRules()
    {
        var c = TweakCatalog.LoadEmbedded();
        Assert.All(c.Tweaks, t => Assert.True(t.Grade <= EvidenceGrade.B || t.RecommendedOption is null, $"{t.Id} C/D/E com recomendação"));
        Assert.All(c.Profiles.SelectMany(p => p.Entries), e => Assert.True(c.Get(e.TweakId)!.Grade <= EvidenceGrade.B, e.TweakId));
        Assert.Equal(c.Tweaks.Count, c.Tweaks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Validator_RejectsProtectedServiceAndForbiddenBcd()
    {
        var bad = new TweakManifest
        {
            Id = "x.bad", Name = new("x"), DefaultOption = "default",
            Options =
            {
                new TweakOption { Key = "default", Targets = { new ServiceTarget { ServiceName = "WinDefend", StartMode = ServiceStartMode.Disabled }, new BcdTarget { Element = "hypervisorlaunchtype", Value = "off" } } },
            },
        };
        var errs = CatalogValidator.Validate(bad);
        Assert.Contains(errs, e => e.Contains("protegido"));
        Assert.Contains(errs, e => e.Contains("BCD"));
    }

    [Fact]
    public void Validator_RejectsOptionsWithDifferentTargets()
    {
        var m = new TweakManifest
        {
            Id = "x.diff", Name = new("x"), DefaultOption = "a",
            Options =
            {
                new TweakOption { Key = "a", Targets = { new RegistryTarget { Hive = RegistryHive.HKCU, Path = "Software\\A", Name = "X", Value = "1" } } },
                new TweakOption { Key = "b", Targets = { new RegistryTarget { Hive = RegistryHive.HKCU, Path = "Software\\B", Name = "X", Value = "1" } } },
            },
        };
        Assert.Contains(CatalogValidator.Validate(m), e => e.Contains("mesmo conjunto"));
    }

    [Fact]
    public void RevertValidation_RejectsForeignTarget()
    {
        var m = new TweakManifest
        {
            Id = "x.ok", Name = new("x"), DefaultOption = "a",
            Options = { new TweakOption { Key = "a", Targets = { new RegistryTarget { Hive = RegistryHive.HKLM, Path = "SOFTWARE\\A", Name = "X", Value = "1" } } } },
        };
        var foreign = new TargetChange { Target = new RegistryTarget { Hive = RegistryHive.HKLM, Path = "SYSTEM\\CurrentControlSet\\Services\\WinDefend", Name = "Start", Value = "4" }, Before = new SettingValue(true, "DWord", "4") };
        Assert.NotNull(CatalogValidator.ValidateRevertTargets(m, new[] { foreign }));
    }

    [Fact]
    public void Json_PolymorphicTargets_RoundTrip()
    {
        var rec = new TweakChangeRecord { TweakId = "x", ToOption = "on", Changes = { new TargetChange { Target = new PowerSettingTarget { Scheme = Guid.NewGuid().ToString(), Subgroup = Guid.NewGuid(), Setting = Guid.NewGuid(), Ac = 1 }, Before = new SettingValue(true, "power", "ac=0;dc=0") } } };
        var json = JsonSerializer.Serialize(rec, AgentJson.Options);
        var back = JsonSerializer.Deserialize<TweakChangeRecord>(json, AgentJson.Options)!;
        Assert.IsType<PowerSettingTarget>(back.Changes[0].Target);
        Assert.Equal("ac=0;dc=0", back.Changes[0].Before.Data);
    }
}

/// <summary>Provedor em memória para testar o executor transacional sem tocar o sistema.</summary>
internal sealed class FakeProvider : ISettingProvider
{
    public readonly Dictionary<string, SettingValue> Store = new();
    public string? FailWriteOn;
    public string? CorruptWriteOn;
    public bool CanHandle(SettingTarget t) => t is RegistryTarget;
    public SettingValue Read(SettingTarget t) => Store.TryGetValue(t.IdentityKey, out var v) ? v : SettingValue.Absent;
    public void Write(SettingTarget t, SettingValue v)
    {
        if (FailWriteOn == t.IdentityKey) throw new UnauthorizedAccessException("negado");
        // Simula um sistema que "ignora" o valor aplicado (ex.: política sobrescrevendo), mas aceita a restauração.
        Store[t.IdentityKey] = CorruptWriteOn == t.IdentityKey && v.Exists && v.Data == "1" ? new SettingValue(true, "DWord", "999") : v;
    }
}

public class ExecutorTests
{
    private static TweakManifest Manifest() => new()
    {
        Id = "t.test", Name = new("Teste"), DefaultOption = "off", RecommendedOption = "on", Grade = EvidenceGrade.B,
        Options =
        {
            new TweakOption { Key = "off", Targets = { R("A", "0"), R("B", "0") } },
            new TweakOption { Key = "on", Targets = { R("A", "1"), R("B", "1") } },
        },
    };
    private static RegistryTarget R(string name, string v) => new() { Hive = RegistryHive.HKCU, Path = "Software\\RbmTest", Name = name, Value = v };

    [Fact]
    public void Apply_SnapshotsWritesAndVerifies()
    {
        var p = new FakeProvider();
        p.Store[R("A", "0").IdentityKey] = new SettingValue(true, "DWord", "0");
        var ex = new TweakExecutor(new[] { p });
        var r = ex.Apply(Manifest(), "on", _ => true, false);
        Assert.True(r.Success);
        Assert.All(r.Changes, c => Assert.True(c.Verified));
        Assert.Equal("0", r.Changes[0].Before.Data);
        Assert.False(r.Changes[1].Before.Exists);
        Assert.Equal(TweakStateKind.MatchesOption, ex.ReadStatus(Manifest(), false).Kind);
        Assert.Equal("on", ex.ReadStatus(Manifest(), false).CurrentOption);
    }

    [Fact]
    public void Apply_FailureRollsBackPartialWrites()
    {
        var p = new FakeProvider { FailWriteOn = R("B", "1").IdentityKey };
        p.Store[R("A", "0").IdentityKey] = new SettingValue(true, "DWord", "0");
        var r = new TweakExecutor(new[] { p }).Apply(Manifest(), "on", _ => true, false);
        Assert.False(r.Success);
        Assert.True(r.RolledBack);
        Assert.Equal("0", p.Store[R("A", "0").IdentityKey].Data);
    }

    [Fact]
    public void Apply_VerificationMismatchRollsBack()
    {
        var p = new FakeProvider { CorruptWriteOn = R("A", "1").IdentityKey };
        var r = new TweakExecutor(new[] { p }).Apply(Manifest(), "on", _ => true, false);
        Assert.False(r.Success);
        Assert.Contains("Verificação falhou", r.Error);
        Assert.False(p.Store[R("A", "1").IdentityKey].Exists);
    }

    [Fact]
    public void Revert_DetectsExternalDriftAndRequiresForce()
    {
        var p = new FakeProvider();
        var ex = new TweakExecutor(new[] { p });
        var r = ex.Apply(Manifest(), "on", _ => true, false);
        p.Store[R("A", "1").IdentityKey] = new SettingValue(true, "DWord", "7"); // alteração externa
        var rv = ex.Revert(r.Changes, force: false);
        Assert.False(rv.Success);
        Assert.Single(rv.Drift);
        var forced = ex.Revert(r.Changes, force: true);
        Assert.True(forced.Success);
        Assert.False(p.Store[R("A", "1").IdentityKey].Exists);
    }

    [Fact]
    public void ReadStatus_CustomWhenNoOptionMatches()
    {
        var p = new FakeProvider();
        p.Store[R("A", "1").IdentityKey] = new SettingValue(true, "DWord", "1");
        p.Store[R("B", "1").IdentityKey] = new SettingValue(true, "DWord", "0");
        Assert.Equal(TweakStateKind.Custom, new TweakExecutor(new[] { p }).ReadStatus(Manifest(), false).Kind);
    }
}

public class ParserTests
{
    [Fact]
    public void Bcd_ParsesLocalizedBooleans()
    {
        const string output = "Carregador de Inicialização do Windows\n-------------------------------\nidentificador           {current}\nuseplatformclock        Sim\ndisabledynamictick      No\n";
        Assert.Equal("yes", BcdSettingProvider.ParseElement(output, "useplatformclock").Data);
        Assert.Equal("no", BcdSettingProvider.ParseElement(output, "disabledynamictick").Data);
        Assert.False(BcdSettingProvider.ParseElement(output, "useplatformtick").Exists);
    }

    [Fact]
    public void Bcd_RejectsElementsOutsideAllowList() => Assert.Throws<ArgumentException>(() => BcdSettingProvider.Validate("nx", "AlwaysOff"));

    [Fact]
    public void ScheduledTask_ParsesEnabledFlag()
    {
        Assert.False(ScheduledTaskProvider.ParseEnabled("<?xml version=\"1.0\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings><Enabled>false</Enabled></Settings></Task>"));
        Assert.True(ScheduledTaskProvider.ParseEnabled("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings></Settings></Task>"));
    }

    [Fact]
    public void ScheduledTask_RejectsInjection() => Assert.Throws<ArgumentException>(() => ScheduledTaskProvider.Validate("\\Microsoft\\X\" & calc"));

    [Fact]
    public void Channels_FromBankLabels()
    {
        var mods = new List<MemoryModule>
        {
            new("K", "P", 16L << 30, 3600, 3600, "DIMM 0", "P0 CHANNEL A", 26, "DIMM"),
            new("K", "P", 16L << 30, 3600, 3600, "DIMM 0", "P0 CHANNEL B", 26, "DIMM"),
        };
        Assert.Equal(2, SystemInventory.EstimateChannels(mods).Item1);
        Assert.Null(SystemInventory.EstimateChannels(new List<MemoryModule> { mods[0] with { BankLabel = "BANK 0", DeviceLocator = "A1" }, mods[1] with { BankLabel = "BANK 1", DeviceLocator = "B1" } }).Item1);
    }

    [Fact]
    public void GpuCounterInstance_Parses()
    {
        var r = Rbm.Monitoring.TelemetryService.ParseGpuInstance("pid_1234_luid_0x00000000_0x0000EC32_phys_0_eng_3_engtype_3D");
        Assert.NotNull(r);
        Assert.Equal(0xEC32, r!.Value.luid);
        Assert.Equal("3D", r.Value.engType);
    }

    [Theory]
    [InlineData(25, 25, 5, 100)]
    [InlineData(5, 25, 5, 0)]
    [InlineData(15, 25, 5, 50)]
    public void HealthIndex_LinearScoring(double v, double good, double bad, double expected) =>
        Assert.Equal(expected, HealthIndexCalculator.Linear(v, good, bad), 3);
}

public class DataTests
{
    [Fact]
    public async Task Journal_RoundTripAndActiveChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rbm-test-{Guid.NewGuid():N}.db");
        try
        {
            var db = new RbmDatabase(path);
            var j = new ChangeJournal(db);
            var a = new TweakChangeRecord { TweakId = "t1", ToOption = "on", Status = ChangeStatus.Applied, At = DateTimeOffset.Now.AddMinutes(-2) };
            var b = new TweakChangeRecord { TweakId = "t1", ToOption = "off", Status = ChangeStatus.Reverted, At = DateTimeOffset.Now };
            var c = new TweakChangeRecord { TweakId = "t2", ToOption = "on", Status = ChangeStatus.PendingRestart };
            await j.SaveAsync(a); await j.SaveAsync(b); await j.SaveAsync(c);
            var active = await j.ActiveChangesAsync();
            Assert.Single(active);
            Assert.Equal("t2", active[0].TweakId);
            Assert.Equal(3, (await j.ListAsync()).Count);
            var settings = new SettingsStore(db);
            settings.Set("x", 42);
            Assert.Equal(42, new SettingsStore(db).Get("x", 0));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public void NaN_IsSerializable() => Assert.Contains("NaN", JsonSerializer.Serialize(new { v = double.NaN }, AgentJson.Options));
}

public class ChannelTests
{
    [Fact]
    public async Task Framing_RoundTrip()
    {
        using var ms = new MemoryStream();
        var req = new AgentRequest("1", AgentOps.Ping, JsonSerializer.SerializeToElement(new { a = 1 }), "tok");
        await AgentFraming.WriteAsync(ms, req, CancellationToken.None);
        ms.Position = 0;
        var back = await AgentFraming.ReadAsync<AgentRequest>(ms, CancellationToken.None);
        Assert.Equal(AgentOps.Ping, back!.Op);
        Assert.Equal("tok", back.Token);
    }

    [Fact]
    public async Task Framing_RejectsOversizedLength()
    {
        using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFraming.ReadAsync<AgentRequest>(ms, CancellationToken.None));
    }

    [Fact]
    public void Token_ConstantTimeCompare()
    {
        var t = ChannelSecurity.NewToken();
        Assert.Equal(64, t.Length);
        Assert.True(ChannelSecurity.TokenEquals(t, t));
        Assert.False(ChannelSecurity.TokenEquals(t, ChannelSecurity.NewToken()));
    }

    [Fact]
    public void AllowList_ContainsOnlyKnownOps() => Assert.DoesNotContain("exec", AgentOps.All);

    [Fact]
    public void Protection_BlocksCriticalProcessesAndRealtime()
    {
        Assert.True(Protection.IsCriticalProcess("lsass.exe"));
        Assert.True(Protection.IsProtectedService("WinDefend"));
        Assert.DoesNotContain("RealTime", Protection.AllowedPriorities);
    }
}
