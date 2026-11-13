using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Core.Tweaks;
using Rbm.Processes;
using Rbm.Processes.Agent;
using Rbm.Security;
using Xunit;

namespace Rbm.Tests.Processes;

public class ProcessTreeTests
{
    [Fact]
    public void ResolveParents_HandlesOrphansAndPidReuse()
    {
        var items = new List<(int, int, long)>
        {
            (100, 0, 1000),   // raiz
            (200, 100, 2000), // filho de 100
            (300, 999, 3000), // pai inexistente → órfão
            (400, 500, 1500), // "pai" 500 criado depois → PID reutilizado → raiz
            (500, 100, 5000),
            (600, 600, 6000), // aponta para si mesmo → raiz
        };
        var p = ProcessTreeBuilder.ResolveParents(items);
        Assert.Equal(-1, p[0]);
        Assert.Equal(0, p[1]);
        Assert.Equal(-1, p[2]);
        Assert.Equal(-1, p[3]);
        Assert.Equal(0, p[4]);
        Assert.Equal(-1, p[5]);
    }

    [Fact]
    public void ResolveParents_BreaksCycles()
    {
        var p = ProcessTreeBuilder.ResolveParents(new List<(int, int, long)> { (1, 2, 0), (2, 1, 0) });
        Assert.Contains(-1, p);
    }

    [Fact]
    public void GetDescendants_IsPostOrder()
    {
        RawProcessInfo R(int pid, int ppid, long ct) => new(pid, ppid, $"p{pid}.exe", ct, 1, 1, 1, 8, 0, 0, 0, 0, 0, 0, 0, 0, true);
        var list = new List<RawProcessInfo> { R(10, 1, 100), R(11, 10, 200), R(12, 11, 300), R(13, 10, 250), R(14, 99, 50) };
        var d = ProcessTreeBuilder.GetDescendants(list, 10, 100).Select(x => x.Pid).ToList();
        Assert.Equal(3, d.Count);
        Assert.True(d.IndexOf(12) < d.IndexOf(11)); // neto antes do filho
        Assert.DoesNotContain(14, d);
        Assert.Empty(ProcessTreeBuilder.GetDescendants(list, 10, 999)); // hora de criação diferente = outro processo
    }
}

public class RateCalculatorTests
{
    private static RawProcessInfo Raw(int pid, long create, long cpu100ns, long read) =>
        new(pid, 1, "x.exe", create, 1, 1, 1, 8, 1000, 1000, 1000, cpu100ns, 0, read, 0, 0, true);

    [Fact]
    public void FirstSample_HasNoRates()
    {
        var e = ProcessRateCalculator.Compute([Raw(10, 5, 1000, 0)], new Dictionary<ProcessKey, RawProcessInfo>(), null, 0, 100, 12);
        Assert.Null(e[0].CpuPercent);
        Assert.Null(e[0].IoReadBytesPerSec);
    }

    [Fact]
    public void Cpu_IsNormalizedByLogicalProcessors()
    {
        var prev = Raw(10, 5, 0, 0);
        // 1 s de intervalo, 1 s de CPU consumido em 12 lógicos → 8,33%
        var cur = Raw(10, 5, TimeSpan.TicksPerSecond, 1_000_000);
        var e = ProcessRateCalculator.Compute([cur], new Dictionary<ProcessKey, RawProcessInfo> { [prev.Key] = prev },
            TimeSpan.FromSeconds(1), 1, 2, 12);
        Assert.Equal(100.0 / 12, e[0].CpuPercent!.Value, 3);
        Assert.Equal(1_000_000, e[0].IoReadBytesPerSec!.Value, 3);
    }

    [Fact]
    public void PidReuse_IsTreatedAsNewProcess()
    {
        var prev = Raw(10, 5, 50_000_000, 0);
        long prevTime = 1_000_000, now = prevTime + TimeSpan.TicksPerSecond;
        var cur = Raw(10, prevTime + TimeSpan.TicksPerSecond / 2, TimeSpan.TicksPerSecond / 4, 0); // criado há 0,5 s
        var e = ProcessRateCalculator.Compute([cur], new Dictionary<ProcessKey, RawProcessInfo> { [prev.Key] = prev },
            TimeSpan.FromSeconds(1), prevTime, now, 1);
        Assert.Equal(50, e[0].CpuPercent!.Value, 3); // 0,25 s de CPU em 0,5 s de vida
    }

    [Fact]
    public void RealSystem_EnumeratesCurrentProcessQuickly()
    {
        using var sampler = new ProcessSampler();
        sampler.Capture();
        Thread.Sleep(200);
        var snap = sampler.Capture();
        Assert.Equal(ProcessSampler.NativeSource, snap.Source);
        Assert.True(snap.RatesAvailable);
        var me = Assert.Single(snap.Processes, p => p.Pid == Environment.ProcessId);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe", me.Name, ignoreCase: true);
        Assert.True(me.WorkingSetBytes > 0);
        Assert.NotNull(me.ImagePath);
        Assert.Contains(snap.Processes, p => p.Pid == 4 && p.Name == "System");
        Assert.True(snap.CollectionMilliseconds < 500, $"Amostragem levou {snap.CollectionMilliseconds} ms");
    }
}

public class ProtectionAndValidationTests
{
    [Theory]
    [InlineData("RealTime")]
    [InlineData("realtime")]
    [InlineData("256")]
    [InlineData("Turbo")]
    [InlineData("")]
    public void Priority_RejectsRealtimeAndUnknown(string value) =>
        Assert.False(ProcessPriorityMap.TryParseAllowed(value, out _, out _));

    [Fact]
    public void Priority_AcceptsAllowList()
    {
        Assert.True(ProcessPriorityMap.TryParseAllowed("High", out var p, out _));
        Assert.Equal(ProcessPriority.High, p);
        Assert.False(ProcessPriorityMap.IsAllowedToSet(ProcessPriority.RealTime, out _));
    }

    [Fact]
    public void AffinityMask_Validation()
    {
        Assert.False(ProcessGuard.ValidateAffinityMask(0, 0xFFF, out _));
        Assert.False(ProcessGuard.ValidateAffinityMask(0x1000, 0xFFF, out _));
        Assert.True(ProcessGuard.ValidateAffinityMask(0x0F0, 0xFFF, out _));
    }

    [Fact]
    public void Guard_BlocksCriticalAndRequiresConfirmationForManualOnly()
    {
        Assert.Equal(ProcessErrorCodes.Protected, ProcessGuard.Check(1234, "lsass.exe", "encerrar", true)!.ErrorCode);
        Assert.Equal(ProcessErrorCodes.Protected, ProcessGuard.Check(4, "System", "encerrar", true)!.ErrorCode);
        Assert.Equal(ProcessErrorCodes.ConfirmationRequired, ProcessGuard.Check(1234, "explorer.exe", "encerrar", false)!.ErrorCode);
        Assert.Null(ProcessGuard.Check(1234, "explorer.exe", "encerrar", true));
        Assert.Null(ProcessGuard.Check(1234, "notepad.exe", "encerrar", false));
        Assert.NotNull(ProcessGuard.Check(Environment.ProcessId, "self.exe", "encerrar", true));
        Assert.False(ProcessGuard.IsAutomationAllowed(1234, "explorer.exe"));
        Assert.False(ProcessGuard.IsAutomationAllowed(1234, "csrss.exe"));
    }

    [Fact]
    public void AgentHandlers_RejectBadPayloadsBeforeTouchingTheSystem()
    {
        Assert.False(ProcessesAgentHandlers.ProcessSetPriority(new ProcessPriorityRequest(4, "High", null)).Success);
        Assert.False(ProcessesAgentHandlers.ProcessSetPriority(new ProcessPriorityRequest(12345, "RealTime", null)).Success);
        Assert.False(ProcessesAgentHandlers.ProcessSetPriority(new ProcessPriorityCommand(12345, null, 0)).Success);
        Assert.False(ProcessesAgentHandlers.ProcessKill(new ProcessKillRequest(0, false)).Success);
        var tree = ProcessesAgentHandlers.ProcessKill(new ProcessKillCommand(Environment.ProcessId, Tree: true, TreeConfirmed: false));
        Assert.Equal(ProcessErrorCodes.ConfirmationRequired, tree.ErrorCode);
    }

    [Fact]
    public void Services_ValidationRejectsProtectedAndDriverModes()
    {
        Assert.Equal(ProcessErrorCodes.Protected, ServiceManager.ValidateStartModeRequest(new ServiceStartRequest("WinDefend", ServiceStartMode.Disabled)).ErrorCode);
        Assert.False(ServiceManager.ValidateStartModeRequest(new ServiceStartRequest("SysMain", ServiceStartMode.Boot)).Success);
        Assert.False(ServiceManager.ValidateStartModeRequest(new ServiceStartRequest(@"..\x", ServiceStartMode.Manual)).Success);
        Assert.True(ServiceManager.ValidateStartModeRequest(new ServiceStartRequest("SysMain", ServiceStartMode.Manual)).Success);
        Assert.False(ServiceManager.ValidateControlRequest(new ServiceControlRequest("RpcSs", ServiceControlAction.Stop)).Success);
        Assert.True(ServiceManager.ValidateControlRequest(new ServiceControlRequest("RpcSs", ServiceControlAction.Start)).Success);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true)]
    [InlineData(@"\GoogleUpdateTaskMachineUA", true)]
    [InlineData(@"Microsoft\Windows\x", false)]
    [InlineData(@"\a\..\b", false)]
    [InlineData(@"\a/b", false)]
    [InlineData(@"\folder\", false)]
    [InlineData(@"\", false)]
    public void TaskPath_Validation(string path, bool ok) => Assert.Equal(ok, ScheduledTaskManager.ValidateTaskPath(path).Success);

    [Fact]
    public void Tasks_ProtectedCannotBeDisabled()
    {
        var r = ScheduledTaskManager.SetEnabledElevated(new TaskToggleRequest(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start", false));
        Assert.Equal(ProcessErrorCodes.Protected, r.ErrorCode);
    }
}

public class RulesTests
{
    private static readonly IReadOnlyList<CoreInfo> Hybrid =
    [
        new(0, 0, 0b0011, 1, 0, [0, 1]), // P-core com SMT
        new(1, 0, 0b1100, 1, 0, [2, 3]), // P-core com SMT
        new(2, 0, 0b10000, 0, 0, [4]),   // E-core
        new(3, 0, 0b100000, 0, 0, [5]),  // E-core
    ];
    private static readonly IReadOnlyList<CoreInfo> Ryzen5500 =
        Enumerable.Range(0, 6).Select(i => new CoreInfo(i, 0, 0b11UL << (i * 2), 0, 0, [i * 2, i * 2 + 1])).ToList();

    [Fact]
    public void Matcher_NameWithOrWithoutExe_AndExactBeforeWildcard()
    {
        var rules = new List<ProcessRule>
        {
            new() { Id = "wild", ExecutableName = "game*", Priority = ProcessPriority.AboveNormal },
            new() { Id = "exact", ExecutableName = "game", Priority = ProcessPriority.High },
        };
        Assert.Equal("exact", ProcessRuleMatcher.Match(rules, 1234, "Game.exe", null)!.Id);
        Assert.Equal("wild", ProcessRuleMatcher.Match(rules, 1234, "GameLauncher.exe", null)!.Id);
        Assert.Null(ProcessRuleMatcher.Match(rules, 1234, "other.exe", null));
    }

    [Fact]
    public void Matcher_ExceptionsAndProtection()
    {
        var rules = new List<ProcessRule>
        {
            new() { ExecutableName = "app.exe", Priority = ProcessPriority.High, Exceptions = [@"C:\Program Files\App\Updater\"] },
            new() { ExecutableName = "*host*", Priority = ProcessPriority.Idle },
        };
        Assert.Null(ProcessRuleMatcher.Match(rules, 1234, "app.exe", @"C:\Program Files\App\Updater\app.exe"));
        Assert.NotNull(ProcessRuleMatcher.Match(rules, 1234, "app.exe", @"C:\Program Files\App\app.exe"));
        Assert.Null(ProcessRuleMatcher.Match(rules, 1234, "app.exe", null)); // caminho desconhecido + exceções → não aplica
        Assert.Null(ProcessRuleMatcher.Match(rules, 1234, "svchost.exe", null)); // crítico nunca casa
    }

    [Fact]
    public void Validator_RejectsDangerousRules()
    {
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "lsass.exe", Priority = ProcessPriority.Idle }));
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "*.exe", Priority = ProcessPriority.Idle }));
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "game.exe", Priority = ProcessPriority.RealTime }));
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = @"C:\x\game.exe", Priority = ProcessPriority.High }));
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "game.exe" }));
        Assert.NotEmpty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "game.exe", Affinity = AffinityPreset.CustomMask }));
        Assert.Empty(ProcessRuleValidator.Validate(new ProcessRule { ExecutableName = "game.exe", Priority = ProcessPriority.High }));
    }

    [Fact]
    public void AffinityPresets_Hybrid()
    {
        Assert.Equal(0b001111UL, AffinityPresets.Compute(AffinityPreset.PerformanceCoresOnly, Hybrid, 0x3F).Mask);
        Assert.Equal(0b001111UL, AffinityPresets.Compute(AffinityPreset.ExcludeEfficiencyCores, Hybrid, 0x3F).Mask);
        Assert.Equal(0b110101UL, AffinityPresets.Compute(AffinityPreset.OneThreadPerCore, Hybrid, 0x3F).Mask);
    }

    [Fact]
    public void AffinityPresets_NonHybridEqualsAllCoresWithNote()
    {
        var c = AffinityPresets.Compute(AffinityPreset.PerformanceCoresOnly, Ryzen5500, 0xFFF);
        Assert.Equal(0xFFFUL, c.Mask);
        Assert.Contains("sem núcleos híbridos", c.Note);
        Assert.Equal(0x555UL, AffinityPresets.Compute(AffinityPreset.OneThreadPerCore, Ryzen5500, 0xFFF).Mask);
        Assert.Null(AffinityPresets.Compute(AffinityPreset.PerformanceCoresOnly, [], 0xFFF).Mask);
        Assert.Null(AffinityPresets.Compute(AffinityPreset.CustomMask, Ryzen5500, 0xFFF, 0x1000).Mask);
    }

    [Fact]
    public void Store_RoundTripsAtomically_AndDisablesInvalidRules()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rbm-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProcessRuleStore(Path.Combine(dir, "process-rules.json"));
            Assert.True(store.Load().Success);
            var set = new ProcessRuleSet { Rules = [new ProcessRule { ExecutableName = "game.exe", Priority = ProcessPriority.High, Affinity = AffinityPreset.OneThreadPerCore }] };
            Assert.True(store.Save(set).Success);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
            var loaded = store.Load();
            Assert.True(loaded.Success);
            Assert.Equal(set.Rules[0] with { Exceptions = loaded.Value!.Rules[0].Exceptions }, loaded.Value.Rules[0]);

            Assert.False(store.Save(new ProcessRuleSet { Rules = [new ProcessRule { ExecutableName = "csrss.exe", Priority = ProcessPriority.High }] }).Success);

            File.WriteAllText(store.FilePath, """{"version":1,"rules":[{"id":"a","executableName":"x.exe","enabled":true,"priority":"RealTime","exceptions":null}]}""");
            var hand = store.Load();
            Assert.True(hand.Success);
            Assert.False(hand.Value!.Rules[0].Enabled);

            File.WriteAllText(store.FilePath, "{ not json");
            Assert.False(store.Load().Success);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class ImpactAndAdvisorTests
{
    private static ProcessSnapshot Snap(DateTimeOffset t, params ProcessEntry[] p) => new(t, p, TimeSpan.FromSeconds(30), 12, "test", 1, null);
    private static ProcessEntry E(int pid, string name, double cpu, long priv, double io = 0) =>
        new(pid, 1, name, 1000 + pid, 1, 1, 1, 8, priv, priv, priv, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, cpu, io, 0, 0, null);

    [Fact]
    public void Analyzer_FlagsMonotonicGrowthAndRanksCpu()
    {
        var t0 = DateTimeOffset.Now;
        var snaps = Enumerable.Range(0, 12).Select(i => Snap(t0.AddSeconds(30 * i),
            E(10, "leaky.exe", 1, 100L * 1048576 + i * 20L * 1048576),
            E(20, "busy.exe", 25, 50L * 1048576),
            E(30, "io.exe", 0.5, 30L * 1048576, 5 * 1048576))).ToList();
        var r = BackgroundImpactAnalyzer.Analyze(snaps);
        var leaky = r.ByImpact.Single(p => p.Name == "leaky.exe");
        Assert.True(leaky.PossibleLeak);
        Assert.Equal(40 * 1048576.0, leaky.PrivateBytesSlopeBytesPerMin, 0);
        Assert.Equal("busy.exe", r.TopByAverageCpu[0].Name);
        Assert.Equal("io.exe", r.TopByIo[0].Name);
        Assert.False(r.ByImpact.Single(p => p.Name == "busy.exe").PossibleLeak);
    }

    [Fact]
    public void Analyzer_EmptyWindow() => Assert.NotEmpty(BackgroundImpactAnalyzer.Analyze([]).Notes);

    private static PublisherInfo Pub(string path, SignatureStatus status, string? signer) =>
        new(path, true, null, null, null, null, new SignatureInfo(status, signer, false, null));

    [Fact]
    public void Advisor_Rules()
    {
        AutostartRecommendationKind K(string path, PublisherInfo? pub, bool exists = true, bool prot = false) =>
            AutostartAdvisor.Recommend(new AutostartFacts(AutostartKind.StartupEntry, "x", path, path, exists, pub, prot)).Kind;

        Assert.Equal(AutostartRecommendationKind.Keep, K(@"C:\Windows\System32\SecurityHealthSystray.exe",
            Pub(@"C:\Windows\System32\SecurityHealthSystray.exe", SignatureStatus.Valid, "Microsoft Windows")));
        Assert.Equal(AutostartRecommendationKind.OptionalSafeToDisable, K(@"C:\Program Files (x86)\Steam\steam.exe",
            Pub(@"C:\Program Files (x86)\Steam\steam.exe", SignatureStatus.Valid, "Valve Corp.")));
        // OneDrive é da Microsoft, mas é opcional na inicialização.
        Assert.Equal(AutostartRecommendationKind.OptionalSafeToDisable, K(@"C:\Users\u\AppData\Local\Microsoft\OneDrive\OneDrive.exe",
            Pub("x", SignatureStatus.Valid, "Microsoft Corporation")));
        // Nome conhecido sem assinatura = possível imitação → investigar.
        Assert.Equal(AutostartRecommendationKind.InvestigateUnsigned, K(@"C:\Temp\steam.exe", Pub("x", SignatureStatus.Unsigned, null)));
        Assert.Equal(AutostartRecommendationKind.InvestigateUnsigned, K(@"C:\Gone\app.exe", null, exists: false));
        Assert.Equal(AutostartRecommendationKind.Unknown, K(@"C:\Vendor\tool.exe", Pub("x", SignatureStatus.Valid, "Vendor Inc.")));
        Assert.Equal(AutostartRecommendationKind.Unknown, K(@"C:\Vendor\tool.exe", null));
        Assert.Equal(AutostartRecommendationKind.Keep, K(@"C:\Vendor\tool.exe", null, prot: true));
    }

    [Fact]
    public void WhqlSigner_IsNotMicrosoft()
    {
        Assert.False(PublisherInfo.IsMicrosoftSigner("Microsoft Windows Hardware Compatibility Publisher"));
        Assert.True(PublisherInfo.IsMicrosoftSigner("Microsoft Windows"));
        Assert.True(PublisherInfo.IsMicrosoftSigner("Microsoft Corporation"));
        Assert.False(PublisherInfo.IsMicrosoftSigner("Microsofty Ltd"));
        Assert.False(PublisherInfo.IsMicrosoftSigner(null));
    }
}
