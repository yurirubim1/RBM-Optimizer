using Rbm.Core.Agent;
using Rbm.Gaming.Overlays;
using Rbm.Gaming.PresentMon;
using Xunit;

namespace Rbm.Tests.Gaming;

public class PresentMonAndOverlayTests
{
    // Trecho da resposta real de GET /repos/GameTechDev/PresentMon/releases/latest (v2.6.0).
    private const string ReleaseJson = """
        {
          "tag_name": "v2.6.0", "name": "v2.6.0", "prerelease": false, "published_at": "2026-09-21T17:30:36Z",
          "assets": [
            { "name": "PresentMon-2.6.0-x64.exe", "size": 980320,
              "digest": "sha256:b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af",
              "browser_download_url": "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe" },
            { "name": "PresentMon-2.6.0.msi", "size": 169672704,
              "browser_download_url": "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0.msi" },
            { "name": "ReleaseSymbols.zip", "size": 54167987,
              "browser_download_url": "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/ReleaseSymbols.zip" }
          ]
        }
        """;

    [Fact]
    public void Parses_release_and_selects_console_x64_asset()
    {
        var rel = PresentMonDownloader.ParseRelease(ReleaseJson);
        Assert.Equal("v2.6.0", rel.TagName);
        Assert.Equal(3, rel.Assets.Count);
        var a = PresentMonDownloader.SelectConsoleAsset(rel);
        Assert.NotNull(a);
        Assert.Equal("PresentMon-2.6.0-x64.exe", a!.Name);
        Assert.Equal("B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF", a.Sha256);
        Assert.Equal(980320, a.Size);
    }

    [Fact]
    public void Rejects_assets_from_other_repositories_or_insecure_urls()
    {
        var evil = PresentMonDownloader.ParseRelease("""
            { "tag_name": "v9", "assets": [
              { "name": "PresentMon-9.0.0-x64.exe", "size": 1, "browser_download_url": "https://github.com/evil/PresentMon/releases/download/v9/PresentMon-9.0.0-x64.exe" },
              { "name": "PresentMon-9.0.1-x64.exe", "size": 1, "browser_download_url": "http://github.com/GameTechDev/PresentMon/releases/download/v9/PresentMon-9.0.1-x64.exe" } ] }
            """);
        Assert.Null(PresentMonDownloader.SelectConsoleAsset(evil));
        Assert.True(PresentMonDownloader.IsAllowedDownloadUrl("https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe"));
    }

    [Fact]
    public void Builds_arguments_for_2x_and_1x()
    {
        var v2 = PresentMonLocator.BuildCaptureArguments(2, "game.exe", @"C:\out\a.csv", 60, "S");
        Assert.Equal(["--process_name", "game.exe", "--output_file", @"C:\out\a.csv", "--timed", "60", "--terminate_after_timed",
                      "--terminate_on_proc_exit", "--stop_existing_session", "--no_console_stats", "--session_name", "S"], v2);
        var v1 = PresentMonLocator.BuildCaptureArguments(1, "game.exe", @"C:\out\a.csv", 60, "S");
        Assert.Contains("-no_top", v1);
        Assert.All(v1.Where(x => x.StartsWith('-')), x => Assert.False(x.StartsWith("--", StringComparison.Ordinal)));
        Assert.Equal(new Version(2, 6, 0), PresentMonLocator.VersionFromFileName("PresentMon-2.6.0-x64.exe"));
        Assert.Equal(new Version(1, 9, 2), PresentMonLocator.VersionFromFileName(@"C:\t\PresentMon-1.9.2-x64.exe"));
        Assert.Null(PresentMonLocator.VersionFromFileName("PresentMon.exe"));
    }

    [Theory]
    [InlineData("game.exe", true)]
    [InlineData("Cyberpunk2077.exe", true)]
    [InlineData("FortniteClient-Win64-Shipping.exe", true)]
    [InlineData("Ação & Aventura (x64).exe", true)]
    [InlineData(@"C:\Games\game.exe", false)]
    [InlineData("../game.exe", false)]
    [InlineData("-stop_existing_session.exe", false)]
    [InlineData("game", false)]
    [InlineData("ga*me.exe", false)]
    [InlineData("ga\"me.exe", false)]
    [InlineData(".exe", false)]
    [InlineData("", false)]
    public void Validates_process_name(string name, bool ok) =>
        Assert.Equal(ok, PresentMonCapture.ValidateProcessName(name).Success);

    [Fact]
    public void Validates_capture_request()
    {
        Assert.Equal("invalid_duration", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 0, @"C:\t\PresentMon.exe")).ErrorCode);
        Assert.Equal("invalid_duration", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 601, @"C:\t\PresentMon.exe")).ErrorCode);
        Assert.Equal("invalid_path", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 10, @"tools\PresentMon.exe")).ErrorCode);
        Assert.Equal("invalid_path", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 10, @"C:\Windows\System32\cmd.exe")).ErrorCode);
        Assert.Equal("invalid_path", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 10, @"\\server\share\PresentMon.exe")).ErrorCode);
        Assert.Equal("not_found", PresentMonCapture.ValidateRequest(new FrameCaptureRequest("game.exe", 10, @"C:\missing\PresentMon-2.6.0-x64.exe")).ErrorCode);
        Assert.Equal("invalid_process", PresentMonCapture.ValidateRequest(new FrameCaptureRequest(@"x\game.exe", 10, @"C:\t\PresentMon.exe")).ErrorCode);
        Assert.False(PresentMonCapture.ValidateRequest(null).Success);
    }

    [Fact]
    public async Task Capture_rejects_unsigned_presentmon()
    {
        var dir = GamingTestUtil.NewTempDir();
        try
        {
            var fake = Path.Combine(dir, "PresentMon-2.6.0-x64.exe");
            GamingTestUtil.WriteBytes(fake, 4096);
            var r = await PresentMonCapture.CaptureAsync(new FrameCaptureRequest("game.exe", 1, fake), dir, TestContext.Current.CancellationToken);
            Assert.False(r.Success);
            Assert.Equal("bad_signature", r.ErrorCode);
            Assert.Empty(Directory.GetFiles(dir, "*.csv"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Capability_check_does_not_throw()
    {
        var c = PresentMonCapture.GetCapability();
        Assert.Equal(c.IsElevated || c.IsPerformanceLogUser, c.CanCaptureUnelevated);
        Assert.False(string.IsNullOrWhiteSpace(c.MessagePt));
    }

    [Fact]
    public void Matches_known_overlays_case_insensitively()
    {
        var hits = OverlayDetector.Match([("Discord", 10), ("discord", 11), ("gameoverlayui64.exe", 20), ("notepad", 30), ("RTSS", 40)]);
        Assert.Equal(3, hits.Count);
        var discord = hits.Single(h => h.Product.StartsWith("Discord"));
        Assert.Equal([10, 11], discord.Pids);
        Assert.Contains("Teste", discord.TestSuggestionPt);
        Assert.Contains(hits, h => h.Category == OverlayCategory.Monitoring);
        Assert.Contains("não uma correção garantida", OverlayDetector.DisclaimerPt);
    }

    [Fact]
    public void Interprets_game_dvr_state()
    {
        Assert.Contains("desativada", OverlayDetector.Interpret(0, 0, 1, null));
        Assert.Contains("Modo de Jogo ativado", OverlayDetector.Interpret(0, 0, 1, null));
        Assert.Contains("habilitada", OverlayDetector.Interpret(1, 1, null, null));
        Assert.Contains("política", OverlayDetector.Interpret(1, 1, null, 0));
        Assert.Contains("padrão", OverlayDetector.Interpret(null, null, null, null));
        _ = OverlayDetector.ReadGameDvrState(); // somente leitura, não lança
    }
}
