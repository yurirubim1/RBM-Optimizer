using System.Text;
using Rbm.Core;
using Rbm.Gaming.Library;
using Xunit;

namespace Rbm.Tests.Gaming;

public class GameLibraryTests
{
    // Trecho real de appmanifest_714010.acf
    private const string AimlabsAcf = """
        "AppState"
        {
        	"appid"		"714010"
        	"universe"		"1"
        	"LauncherPath"		"C:\\Program Files (x86)\\Steam\\steam.exe"
        	"name"		"Aimlabs"
        	"StateFlags"		"4"
        	"installdir"		"Aim Lab"
        	"SizeOnDisk"		"22529243614"
        	"InstalledDepots"
        	{
        		"714011"
        		{
        			"manifest"		"1068866008862010251"
        			"size"		"22529243614"
        		}
        	}
        }
        """;

    [Fact]
    public void Parses_steam_appmanifest()
    {
        var m = SteamLibraryScanner.ParseAppManifest(AimlabsAcf);
        Assert.NotNull(m);
        Assert.Equal("714010", m!.AppId);
        Assert.Equal("Aimlabs", m.Name);
        Assert.Equal("Aim Lab", m.InstallDir);
        Assert.Equal(22529243614L, m.SizeOnDisk);
        Assert.Equal(4, m.StateFlags);
        Assert.False(SteamLibraryScanner.IsNonGame(m));
        Assert.True(SteamLibraryScanner.IsNonGame(m with { AppId = "228980", Name = "Steamworks Common Redistributables" }));
        Assert.Null(SteamLibraryScanner.ParseAppManifest("\"AppState\" { \"name\" \"x\" }"));
    }

    [Fact]
    public async Task Steam_scanner_reads_all_libraries_and_resolves_exe()
    {
        var root = GamingTestUtil.NewTempDir();
        var lib2 = GamingTestUtil.NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{root.Replace("\\", "\\\\")}\" }} \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }} }}");
            Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
            File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_714010.acf"), AimlabsAcf);
            File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_228980.acf"),
                "\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }");
            var game = Path.Combine(lib2, "steamapps", "common", "Aim Lab");
            GamingTestUtil.WriteBytes(Path.Combine(game, "AimLab_tb.exe"), 600_000);
            Directory.CreateDirectory(Path.Combine(game, "AimLab_tb_Data"));
            GamingTestUtil.WriteBytes(Path.Combine(game, "UnityCrashHandler64.exe"), 1_200_000);

            var outp = await new SteamLibraryScanner(root).ScanAsync(TestContext.Current.CancellationToken);
            var g = Assert.Single(outp.Games);
            Assert.Equal("steam:714010", g.Id);
            Assert.Equal(GameSource.Steam, g.Source);
            Assert.Equal("AimLab_tb.exe", g.ProcessName);
            Assert.Equal(22529243614L, g.SizeOnDiskBytes);
        }
        finally { Directory.Delete(root, true); Directory.Delete(lib2, true); }
    }

    [Fact]
    public void Parses_epic_manifest_and_flags_non_games()
    {
        const string json = """
            {
              "FormatVersion": 0,
              "bIsIncompleteInstall": false,
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteBootstrapper.exe",
              "DisplayName": "Fortnite",
              "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
              "AppName": "Fortnite",
              "bIsApplication": true,
              "AppCategories": [ "public", "games", "applications" ],
            }
            """;
        var m = EpicLibraryScanner.ParseManifest(json);
        Assert.NotNull(m);
        Assert.Equal("Fortnite", m!.DisplayName);
        Assert.Equal(@"FortniteGame\Binaries\Win64\FortniteBootstrapper.exe", m.LaunchExecutable);
        Assert.False(m.IsNonGame);
        var engine = EpicLibraryScanner.ParseManifest("""{"DisplayName":"Unreal Engine","InstallLocation":"C:\\UE","AppCategories":["engines"]}""");
        Assert.True(engine!.IsNonGame);
        Assert.Null(EpicLibraryScanner.ParseManifest("""{"DisplayName":"x"}"""));
    }

    [Fact]
    public async Task Epic_scanner_prefers_real_game_process_over_launcher()
    {
        var manifests = GamingTestUtil.NewTempDir();
        var install = GamingTestUtil.NewTempDir();
        try
        {
            GamingTestUtil.WriteBytes(Path.Combine(install, "Binaries", "Win64", "Launcher.exe"), 79_184);
            GamingTestUtil.WriteBytes(Path.Combine(install, "Binaries", "Win64", "RocketLeague.exe"), 3_900_000);
            GamingTestUtil.WriteBytes(Path.Combine(install, "Binaries", "Win64", "RocketLeague_EAC.exe"), 400_000);
            GamingTestUtil.WriteBytes(Path.Combine(install, "Binaries", "Win64", "EasyAntiCheat", "EasyAntiCheat_EOS_Setup.exe"), 900_000);
            File.WriteAllText(Path.Combine(manifests, "A.item"),
                $$"""{"DisplayName":"Rocket League®","InstallLocation":"{{install.Replace("\\", "\\\\")}}","LaunchExecutable":"Binaries/Win64/Launcher.exe","AppName":"Sugar","bIsApplication":true}""");

            var outp = await new EpicLibraryScanner(manifests).ScanAsync(TestContext.Current.CancellationToken);
            var g = Assert.Single(outp.Games);
            Assert.Equal("epic:Sugar", g.Id);
            Assert.Equal("RocketLeague.exe", g.ProcessName);
            Assert.EndsWith("Launcher.exe", g.LaunchExecutablePath);
            Assert.Contains("launcher", g.Executable.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(manifests, true); Directory.Delete(install, true); }
    }

    [Fact]
    public void Resolver_picks_unreal_shipping_exe()
    {
        // Conjunto real da pasta do Fortnite
        var c = new List<ExeCandidate>
        {
            new(@"Engine\Binaries\Win64\CrashReportClient.exe", 25_991_608),
            new(@"Engine\Binaries\Win64\EpicWebHelper.exe", 2_708_920),
            new(@"Engine\Binaries\Win64\UninstallHelper-Win64-Shipping.exe", 2_255_288),
            new(@"FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", 985_896),
            new(@"FortniteGame\Binaries\Win64\FortniteBootstrapper.exe", 3_155_896),
            new(@"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", 507_366_048),
            new(@"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping_EAC_EOS.exe", 4_102_072),
            new(@"FortniteGame\Binaries\Win64\FortniteLauncher.exe", 7_602_104),
        };
        var r = ExecutableResolver.Choose(@"C:\Program Files\Epic Games\Fortnite", c, "Fortnite");
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", r.Path);
        Assert.Equal(Confidence.Medium, r.Confidence);
    }

    [Fact]
    public void Resolver_handles_unity_and_reports_none_when_only_launchers()
    {
        var unity = ExecutableResolver.Choose(@"D:\Games\Aim Lab", [
            new("AimLab_tb.exe", 666_624, HasUnityDataFolder: true),
            new(@"AimLab_tb_Data\StreamingAssets\DeepLinking\AimLabLinksHandler.exe", 23_901_474),
            new("UnityCrashHandler64.exe", 1_185_200)], "Aimlabs");
        Assert.EndsWith("AimLab_tb.exe", unity.Path);

        var none = ExecutableResolver.Choose(@"D:\X", [new("unins000.exe", 3_000_000), new(@"_CommonRedist\vcredist_x64.exe", 9_000_000), new("Launcher.exe", 100)]);
        Assert.False(none.IsResolved);

        Assert.True(ExecutableResolver.IsExcludedName("UnityCrashHandler64.exe"));
        Assert.True(ExecutableResolver.IsExcludedName("RocketLeague_EAC.exe"));
        Assert.True(ExecutableResolver.IsExcludedName("FortniteBootstrapper.exe"));
        Assert.False(ExecutableResolver.IsExcludedName("RocketLeague.exe"));
        Assert.True(ExecutableResolver.IsInExcludedDirectory(@"Binaries\Win64\EasyAntiCheat\x.exe"));
    }

    [Fact]
    public void Manifest_pointing_only_to_uninstaller_is_not_reported_as_game_exe()
    {
        var dir = GamingTestUtil.NewTempDir();
        try
        {
            var uninst = Path.Combine(dir, "Uninstall.exe");
            GamingTestUtil.WriteBytes(uninst, 2_000_000);
            var r = ExecutableResolver.ResolveWithManifest(uninst, dir, "Ubisoft Connect", "X", TestContext.Current.CancellationToken);
            Assert.False(r.IsResolved);
            Assert.Contains("Uninstall.exe", r.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parses_gaming_root_file_and_microsoft_game_config()
    {
        // Bytes reais do C:\.GamingRoot ("RGBX", 1, "XboxGames")
        var bytes = new byte[] { 0x52, 0x47, 0x42, 0x58, 1, 0, 0, 0 }.Concat(Encoding.Unicode.GetBytes("XboxGames\0")).ToArray();
        Assert.Equal(["XboxGames"], XboxLibraryScanner.ParseGamingRoot(bytes));
        Assert.Empty(XboxLibraryScanner.ParseGamingRoot("nope"u8));

        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="Microsoft.MinecraftUWP" Publisher="CN=Microsoft Corporation" Version="1.26.6301.0" />
              <StoreId>9NBLGGH2JHXJ</StoreId>
              <ShellVisuals DefaultDisplayName="Minecraft for Windows" PublisherDisplayName="Microsoft Studios" />
              <ExecutableList>
                <Executable Name="Helper.exe" TargetDeviceFamily="PC" />
                <Executable Name="Minecraft.Windows.exe" TargetDeviceFamily="PC" Id="Game" />
              </ExecutableList>
            </Game>
            """;
        var cfg = XboxLibraryScanner.ParseMicrosoftGameConfig(xml);
        Assert.NotNull(cfg);
        Assert.Equal("Minecraft for Windows", cfg!.DisplayName);
        Assert.Equal("Microsoft.MinecraftUWP", cfg.IdentityName);
        Assert.Equal("Minecraft.Windows.exe", cfg.Executables[0]); // Id="Game" tem prioridade
        Assert.Equal("9NBLGGH2JHXJ", cfg.StoreId);
    }

    [Fact]
    public void Classifies_uninstall_entries_by_store()
    {
        Assert.Equal(GameSource.EaApp, UninstallRegistryScanner.Classify(new("x", "Battlefield 2042", "Electronic Arts", @"C:\EA\BF", null, @"C:\Program Files\Common Files\EAInstaller\Battlefield\Cleanup.exe")));
        Assert.Equal(GameSource.UbisoftConnect, UninstallRegistryScanner.Classify(new("Uplay Install 5059", "Rainbow Six Siege", "Ubisoft", @"C:\Ubi\R6", null, "\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\Uplay.exe\" uplay://uninstall/635")));
        Assert.Equal(GameSource.BattleNet, UninstallRegistryScanner.Classify(new("Overwatch", "Overwatch", "Blizzard Entertainment", @"C:\BN\OW", null, "\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\"")));
        // Jogo da EA comprado no Steam: não é EA app
        Assert.Null(UninstallRegistryScanner.Classify(new("Steam App 1172470", "Rbm Legends", "Electronic Arts", @"C:\Steam\Rbm", null, "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/1172470")));
        // O próprio launcher é ignorado
        Assert.Null(UninstallRegistryScanner.Classify(new("{x}", "EA app", "Electronic Arts", @"C:\EA", null, "msiexec")));
        Assert.Null(UninstallRegistryScanner.Classify(new("y", "7-Zip", "Igor Pavlov", @"C:\7z", null, "u.exe")));
        // Componente real encontrado neste PC: não é jogo
        Assert.Null(UninstallRegistryScanner.Classify(new("Ubisoft-Sentinel", "Ubisoft Anti-Cheat", "Ubisoft", @"C:\Ubi\AC", null, @"C:\Ubi\AC\Uninstall.exe")));

        Assert.Equal(@"C:\Games\X\x.exe", UninstallRegistryScanner.ExeFromDisplayIcon("\"C:\\Games\\X\\x.exe\",0"));
        Assert.Null(UninstallRegistryScanner.ExeFromDisplayIcon(@"C:\Games\X\icon.ico"));
    }

    [Fact]
    public void Deduplicates_by_executable_and_keeps_best_source()
    {
        var exe = new ExecutableResolution(@"C:\Games\G\g.exe", Confidence.High, "manual", []);
        var steam = new DetectedGame("steam:1", "Game", GameSource.Steam, "1", @"C:\Games\G", exe with { Confidence = Confidence.Medium }, null, 10, [], []);
        var ea = new DetectedGame("ea:k", "Game (EA)", GameSource.EaApp, "k", @"C:\Games\G\", exe with { Confidence = Confidence.Medium }, null, null, [], []);
        var manual = new DetectedGame("manual:abc", "g", GameSource.Manual, null, @"C:\Games\G", exe, null, null, [], []);
        var other = new DetectedGame("gog:2", "Other", GameSource.Gog, "2", @"C:\Games\O", ExecutableResolution.None("x"), null, null, [], []);

        var list = GameLibraryService.Deduplicate([manual, ea, steam, other]);
        Assert.Equal(2, list.Count);
        var g = list.Single(x => x.Id == "steam:1");
        Assert.Equal(GameSource.Steam, g.Source);
        Assert.Contains(GameSource.EaApp, g.AlsoFoundIn);
        Assert.Contains(GameSource.Manual, g.AlsoFoundIn);
        Assert.Equal(Confidence.High, g.Executable.Confidence); // herdou o executável de maior confiança
    }

    [Fact]
    public void Manual_entry_is_validated()
    {
        Assert.False(GameLibraryService.CreateManualEntry("relative\\game.exe").Success);
        Assert.False(GameLibraryService.CreateManualEntry(@"C:\definitely\missing\game.exe").Success);
        var dir = GamingTestUtil.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "MyGame.exe");
            GamingTestUtil.WriteBytes(p, 10);
            var r = GameLibraryService.CreateManualEntry(p);
            Assert.True(r.Success);
            Assert.StartsWith("manual:", r.Value!.Id);
            Assert.Equal("MyGame", r.Value.Name);
            Assert.Equal(r.Value.Id, GameLibraryService.CreateManualEntry(p.ToUpperInvariant()).Value!.Id); // id estável
        }
        finally { Directory.Delete(dir, true); }
    }
}
