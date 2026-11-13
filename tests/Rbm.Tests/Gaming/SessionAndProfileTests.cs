using Rbm.Gaming.Frames;
using Rbm.Gaming.Library;
using Rbm.Gaming.Profiles;
using Rbm.Gaming.Sessions;
using Xunit;

namespace Rbm.Tests.Gaming;

public class SessionAndProfileTests
{
    private static FrameSessionSummary Summary(IReadOnlyList<double> perSecond, string? resolution = "1920x1080", string? settings = "Alto", IReadOnlyList<string>? tweaks = null) =>
        new(Guid.NewGuid(), "steam:1", "game.exe", DateTimeOffset.Now, TimeSpan.FromSeconds(perSecond.Count),
            perSecond.Count * 100, perSecond.Average(), perSecond.Average(), 80, 70, 75, 12.5, 2, 2.0 * 60 / perSecond.Count,
            "Hardware: Independent Flip", BoundKind.GpuBound, "Provavelmente limitado pela GPU",
            new SessionConditions("Equilibrado", null, tweaks ?? [], resolution, settings, "benchmark"), perSecond, null);

    private static List<double> Series(double center, int n, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => center + (rng.NextDouble() - 0.5) * 4).ToList();
    }

    [Fact]
    public void Refuses_short_or_flagged_or_mismatched_sessions()
    {
        var ok = Summary(Series(100, 30, 1));
        Assert.Equal("too_short", SessionComparer.Compare(Summary(Series(100, 15, 2)), ok).ErrorCode);
        Assert.Equal("different_settings", SessionComparer.Compare(ok, ok, new SessionComparisonOptions(UserFlaggedDifferentSettings: true)).ErrorCode);
        Assert.Equal("different_resolution", SessionComparer.Compare(ok, Summary(Series(100, 30, 3), resolution: "2560x1440")).ErrorCode);
        Assert.Equal("different_settings", SessionComparer.Compare(ok, Summary(Series(100, 30, 3), settings: "Ultra")).ErrorCode);
        Assert.True(SessionComparer.Compare(ok, Summary(Series(100, 30, 3), resolution: "1920 x 1080")).Success); // normaliza espaços
    }

    [Fact]
    public void Detects_real_improvement_and_no_change()
    {
        var before = Summary(Series(100, 60, 4));
        var after = Summary(Series(120, 60, 5), tweaks: ["power.highperf"]);
        var r = SessionComparer.Compare(before, after);
        Assert.True(r.Success);
        Assert.True(r.Value!.FpsComparison.Significant);
        Assert.True(r.Value.FpsComparison.DeltaPercent > 15);
        Assert.Contains("Melhora", r.Value.VerdictPt);
        Assert.Contains(r.Value.Deltas, d => d.NamePt == "FPS médio");
        Assert.Contains(r.Value.Caveats, c => c.Contains("autocorrelacionado"));

        var same = SessionComparer.Compare(before, Summary(Series(100, 60, 6)));
        Assert.False(same.Value!.FpsComparison.Significant);
    }

    [Fact]
    public void Session_is_built_from_real_analysis()
    {
        var cap = PresentMonCsvParser.ParseFile(GamingTestUtil.Fixture("pm26_default.csv"), new("Code.exe"));
        var a = FrameAnalyzer.Analyze(cap);
        var s = FrameSession.FromAnalysis("manual:x", "VS Code", DateTimeOffset.Now, a, new SessionConditions(AppliedTweakIds: ["t1"]), "c.csv");
        var sum = s.ToSummary();
        Assert.Equal(a.Metrics.AverageFps, sum.AverageFps);
        Assert.Equal("Code.exe", sum.ProcessName);
        Assert.Equal(["t1"], sum.Conditions.TweakIds);
        Assert.Equal(a.PerSecondFps.Count, sum.PerSecondFps.Count);
    }

    [Fact]
    public async Task Profile_store_roundtrip_with_atomic_replace_and_corruption_quarantine()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = GamingTestUtil.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "games.json");
            var store = new GameProfileStore(path);
            Assert.Empty((await store.LoadAsync(ct)).Games);

            var exe = new ExecutableResolution(@"C:\G\g.exe", Rbm.Core.Confidence.High, "x", []);
            var profile = GameProfile.FromDetected(new DetectedGame("steam:1", "Jogo", GameSource.Steam, "1", @"C:\G", exe, null, null, [], []))
                with { Notes = "anotação", PreferredGpu = GpuPreference.HighPerformance, AppliedChangeIds = ["c1"] };
            await store.UpsertAsync(profile, ct);
            await store.UpsertAsync(profile with { Name = "Jogo 2" }, ct); // substitui, não duplica

            var sess = Summary(Series(100, 30, 7)) with { Low01PercentFps = double.NaN };
            Assert.True((await store.AddSessionAsync("steam:1", sess, ct: ct)).Success);
            Assert.False((await store.AddSessionAsync("nope", sess, ct: ct)).Success);
            Assert.True(File.Exists(path + ".bak"));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            var reloaded = await new GameProfileStore(path).LoadAsync(ct);
            var g = Assert.Single(reloaded.Games);
            Assert.Equal("Jogo 2", g.Name);
            Assert.Equal(GpuPreference.HighPerformance, g.PreferredGpu);
            var s = Assert.Single(g.Sessions);
            Assert.True(double.IsNaN(s.Low01PercentFps));
            Assert.Equal(sess.PerSecondFps, s.PerSecondFps);
            Assert.Equal(TimeSpan.FromSeconds(30), s.Duration);

            await File.WriteAllTextAsync(path, "{ quebrado", ct);
            var store2 = new GameProfileStore(path);
            Assert.Empty((await store2.LoadAsync(ct)).Games);
            Assert.NotNull(store2.LastLoadWarning);
            Assert.Single(Directory.GetFiles(dir, "games.json.corrupt-*"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Gpu_preference_value_parsing_preserves_other_pairs_and_validates()
    {
        Assert.Equal(GpuPreference.HighPerformance, GpuPreferenceStore.ParsePreference("GpuPreference=2;"));
        Assert.Equal(GpuPreference.PowerSaving, GpuPreferenceStore.ParsePreference("SwapEffectUpgradeEnable=1;GpuPreference=1;"));
        Assert.Null(GpuPreferenceStore.ParsePreference("GpuPreference=7;"));
        Assert.Null(GpuPreferenceStore.ParsePreference(null));
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;AutoHDREnable=1;",
            GpuPreferenceStore.BuildValue("SwapEffectUpgradeEnable=1;GpuPreference=0;AutoHDREnable=1;", GpuPreference.HighPerformance));
        Assert.Equal("GpuPreference=0;", GpuPreferenceStore.BuildValue(null, GpuPreference.Default));

        Assert.False(GpuPreferenceStore.ValidateExecutablePath("game.exe", mustExist: false).Success);
        Assert.False(GpuPreferenceStore.ValidateExecutablePath(@"C:\x\game.txt", mustExist: false).Success);
        Assert.False(GpuPreferenceStore.ValidateExecutablePath(@"C:\x\ga;me.exe", mustExist: false).Success);
        Assert.False(GpuPreferenceStore.ValidateExecutablePath(@"C:\x\DirectXUserGlobalSettings", mustExist: false).Success);
        Assert.True(GpuPreferenceStore.ValidateExecutablePath(@"C:\Jogos\Meu Jogo\jogo.exe", mustExist: false).Success);
        Assert.False(GpuPreferenceStore.ValidateExecutablePath(@"C:\definitely\missing\jogo.exe").Success);
        Assert.False(GpuPreferenceStore.Set(@"C:\definitely\missing\jogo.exe", GpuPreference.HighPerformance).Success); // nada é gravado
    }
}
