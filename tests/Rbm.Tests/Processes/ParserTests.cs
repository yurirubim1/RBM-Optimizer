using System.Buffers.Binary;
using System.Text;
using Rbm.Processes;
using Xunit;

namespace Rbm.Tests.Processes;

public class CommandLineParserTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\App Folder\app.exe",
        @"C:\Tools\tool.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"),
    };
    private static bool Exists(string p) => Files.Contains(p);

    [Fact]
    public void Quoted_WithArgs()
    {
        var p = CommandLineParser.Parse("\"C:\\Program Files\\App Folder\\app.exe\" --minimized -x", Exists);
        Assert.Equal(@"C:\Program Files\App Folder\app.exe", p.ExecutablePath);
        Assert.Equal("--minimized -x", p.Arguments);
        Assert.True(p.ExecutableExists);
    }

    [Fact]
    public void Unquoted_PathWithSpaces_UsesCreateProcessRule()
    {
        var p = CommandLineParser.Parse(@"C:\Program Files\App Folder\app.exe /background", Exists);
        Assert.Equal(@"C:\Program Files\App Folder\app.exe", p.ExecutablePath);
        Assert.Equal("/background", p.Arguments);
    }

    [Fact]
    public void Unquoted_MissingExtension_IsResolved()
    {
        var p = CommandLineParser.Parse(@"C:\Tools\tool -a", Exists);
        Assert.Equal(@"C:\Tools\tool.exe", p.ExecutablePath);
        Assert.Equal("-a", p.Arguments);
    }

    [Fact]
    public void Rundll32_ExposesHostedDll()
    {
        var p = CommandLineParser.Parse("rundll32.exe shell32.dll,Control_RunDLL", Exists);
        Assert.EndsWith("rundll32.exe", p.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("shell32.dll", p.HostedModulePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(p.HostedModulePath, p.EffectivePath);
    }

    [Fact]
    public void Rundll32_SkipsSwitchesBeforeDll()
    {
        var p = CommandLineParser.Parse("rundll32.exe /d shell32.dll,PerformAutochkOperations", Exists);
        Assert.EndsWith("shell32.dll", p.HostedModulePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForwardSlashDrivePath_IsNormalized() =>
        Assert.Equal(@"C:\Program Files\App Folder\app.exe", CommandLineParser.Parse("\"C:/Program Files/App Folder/app.exe\" -x", Exists).ExecutablePath);

    [Fact]
    public void Empty_IsNull()
    {
        var p = CommandLineParser.Parse("  ", Exists);
        Assert.Null(p.ExecutablePath);
        Assert.False(p.ExecutableExists);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\disk.sys", @"System32\drivers\disk.sys")]
    [InlineData(@"System32\drivers\disk.sys", @"System32\drivers\disk.sys")]
    [InlineData(@"\??\C:\Drivers\x.sys", null)]
    public void NormalizeImagePath_DriverForms(string raw, string? relToWindows)
    {
        var n = CommandLineParser.NormalizeImagePath(raw);
        if (relToWindows is null) Assert.Equal(@"C:\Drivers\x.sys", n);
        else Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), relToWindows), n, ignoreCase: true);
    }

    [Fact]
    public void Tokenize_RespectsQuotes() =>
        Assert.Equal(["/s", @"C:\a b\c.dll", "x"], CommandLineParser.Tokenize("/s \"C:\\a b\\c.dll\" x"));
}

public class GlobAndGpuParserTests
{
    [Theory]
    [InlineData("*.exe", "game.exe", true)]
    [InlineData("game*", "GameClient.exe", true)]
    [InlineData("g?me.exe", "game.exe", true)]
    [InlineData("game.exe", "games.exe", false)]
    [InlineData(@"C:\Games\*\launcher.exe", @"c:\games\x\Launcher.exe", true)]
    public void Glob_Matches(string pattern, string text, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, text));

    [Fact]
    public void GpuInstance_Parses()
    {
        Assert.True(GpuProcessUsageReader.TryParseInstance("pid_1234_luid_0x00000000_0x0000D1E6_phys_0_eng_3_engtype_3D", out int pid, out string t));
        Assert.Equal(1234, pid);
        Assert.Equal("3D", t);
        Assert.False(GpuProcessUsageReader.TryParseInstance("_Total", out _, out _));
    }

    [Fact]
    public void GpuAggregate_SumsAndTracksMax()
    {
        var r = GpuProcessUsageReader.Aggregate(
        [
            ("pid_10_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 40),
            ("pid_10_luid_0x0_0x1_phys_0_eng_1_engtype_VideoDecode", 15),
            ("pid_10_luid_0x0_0x1_phys_0_eng_2_engtype_Copy", 5),
            ("pid_20_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 2),
        ]);
        Assert.Equal(60, r[10].SumPercent, 3);
        Assert.Equal(40, r[10].MaxEnginePercent, 3);
        Assert.Equal(15, r[10].ByEngineType["VideoDecode"], 3);
        Assert.Equal(2, r[20].SumPercent, 3);
    }
}

public class LnkParserTests
{
    private static byte[] BuildLnk(string localBasePath, string args)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // ShellLinkHeader (76 bytes)
        w.Write(0x4C);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(0x02u | 0x20u | 0x80u); // HasLinkInfo | HasArguments | IsUnicode
        w.Write(new byte[76 - 24]);
        // LinkInfo
        byte[] volume = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(volume, 16);
        byte[] path = Encoding.Latin1.GetBytes(localBasePath + "\0");
        int header = 0x1C;
        int volOff = header, baseOff = volOff + volume.Length, suffixOff = baseOff + path.Length;
        int size = suffixOff + 1;
        w.Write(size); w.Write(header); w.Write(1); w.Write(volOff); w.Write(baseOff); w.Write(0); w.Write(suffixOff);
        w.Write(volume); w.Write(path); w.Write((byte)0);
        // StringData: COMMAND_LINE_ARGUMENTS
        w.Write((ushort)args.Length);
        w.Write(Encoding.Unicode.GetBytes(args));
        return ms.ToArray();
    }

    [Fact]
    public void Parse_LocalBasePathAndArguments()
    {
        var t = LnkParser.Parse(BuildLnk(@"C:\Games\Game.exe", "-silent"));
        Assert.NotNull(t);
        Assert.Equal(@"C:\Games\Game.exe", t!.TargetPath);
        Assert.Equal("-silent", t.Arguments);
    }

    [Fact]
    public void Parse_RejectsGarbageAndTruncated()
    {
        Assert.Null(LnkParser.Parse(new byte[10]));
        Assert.Null(LnkParser.Parse(new byte[100]));
        var good = BuildLnk(@"C:\x.exe", "");
        Assert.Null(LnkParser.Parse(good.AsSpan(0, 90))); // LinkInfo truncado
    }
}
