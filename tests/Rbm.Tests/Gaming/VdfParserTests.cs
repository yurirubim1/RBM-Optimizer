using Rbm.Gaming.Library;
using Xunit;

namespace Rbm.Tests.Gaming;

public class VdfParserTests
{
    // Conteúdo real de um libraryfolders.vdf (formato novo).
    private const string LibraryFoldersNew = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"6622493963549498865"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"25674515"
        			"714010"		"0"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        		}
        	}
        }
        """;

    private const string LibraryFoldersOld = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1561832478"
        	"ContentStatsID"		"-158337411110787451"
        	"1"		"D:\\Games\\Steam"
        	"2"		"E:\\SteamLibrary"
        }
        """;

    [Fact]
    public void Parses_new_libraryfolders_format()
    {
        var libs = SteamLibraryScanner.ParseLibraryFolders(LibraryFoldersNew);
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], libs);
    }

    [Fact]
    public void Parses_old_libraryfolders_format_ignoring_non_numeric_keys()
    {
        var libs = SteamLibraryScanner.ParseLibraryFolders(LibraryFoldersOld);
        Assert.Equal([@"D:\Games\Steam", @"E:\SteamLibrary"], libs);
    }

    [Fact]
    public void Nested_lookup_is_case_insensitive_and_keeps_empty_objects()
    {
        var root = VdfParser.Parse(LibraryFoldersNew);
        var apps = root.Path("LIBRARYFOLDERS", "0", "Apps");
        Assert.NotNull(apps);
        Assert.Equal("25674515", apps!.GetValue("228980"));
        var empty = root.Path("libraryfolders", "1", "apps");
        Assert.NotNull(empty);
        Assert.True(empty!.IsObject);
        Assert.Empty(empty.Children);
    }

    [Fact]
    public void Handles_escapes_comments_unquoted_tokens_conditionals_and_duplicates()
    {
        const string text = """
            // comentário no topo
            root
            {
                "quote"   "a \"b\" c"   // comentário no fim da linha
                "tab"     "x\ty"
                unquoted  value123
                "cond"    "1" [$WIN32]
                "dup"     "first"
                "dup"     "second"
                "path"    "C:\Games\Old"
            }
            """;
        var root = VdfParser.Parse(text).Child("root")!;
        Assert.Equal("a \"b\" c", root.GetValue("quote"));
        Assert.Equal("x\ty", root.GetValue("tab"));
        Assert.Equal("value123", root.GetValue("unquoted"));
        Assert.Equal("1", root.GetValue("cond"));
        Assert.Equal("first", root.GetValue("dup"));
        Assert.Equal(2, root.Children.Count(c => c.Key == "dup"));
        Assert.Equal(@"C:\Games\Old", root.GetValue("path")); // barra que não é escape é preservada
    }

    [Fact]
    public void Tolerates_missing_final_brace_and_bom()
    {
        var root = VdfParser.Parse("\uFEFF\"AppState\"\n{\n \"appid\" \"10\"\n");
        Assert.Equal("10", root.Path("AppState", "appid")?.Value);
    }

    [Fact]
    public void Rejects_unterminated_string_and_stray_brace()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\" \"b"));
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\" \"b\" }"));
        Assert.False(VdfParser.TryParse("{", out _, out var err));
        Assert.NotNull(err);
    }
}
