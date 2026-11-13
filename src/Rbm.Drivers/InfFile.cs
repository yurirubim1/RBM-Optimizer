using System.Text;

namespace Rbm.Drivers;

/// <summary>Cabeçalho [Version] de um INF, com tokens %STRING% resolvidos pela seção [Strings].</summary>
public sealed record InfHeader(
    string? Class,
    Guid? ClassGuid,
    string? Provider,
    string? DriverVerRaw,
    DateTime? Date,
    string? Version,
    DriverVersion? ParsedVersion,
    // Catálogo aplicável a x64 (CatalogFile.NTamd64 > CatalogFile.NT > CatalogFile).
    string? CatalogFile,
    IReadOnlyDictionary<string, string> AllCatalogEntries);

/// <summary>Leitor mínimo de INF (somente leitura; nada é executado). Suporta ANSI/UTF-8/UTF-16 com BOM.</summary>
public static class InfFile
{
    public const long MaxInfBytes = 16 * 1024 * 1024;

    public static InfHeader ReadHeader(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("INF não encontrado.", path);
        if (fi.Length > MaxInfBytes) throw new InvalidDataException("INF maior que o limite de 16 MB.");
        string text;
        using (var sr = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true))
            text = sr.ReadToEnd();
        return ParseHeader(text);
    }

    public static InfHeader ParseHeader(string text)
    {
        var sections = ParseSections(text, "version", "strings");
        var version = sections.GetValueOrDefault("version") ?? new();
        var strings = sections.GetValueOrDefault("strings") ?? new();

        string? Get(string key) => version.TryGetValue(key, out var v) ? Resolve(v, strings) : null;

        string? driverVer = Get("DriverVer");
        DateTime? date = null; string? ver = null;
        if (driverVer is not null) DriverVersionMath.TryParseDateAndVersion(driverVer, out date, out ver);

        var cats = version.Where(kv => kv.Key.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key, kv => Resolve(kv.Value, strings), StringComparer.OrdinalIgnoreCase);
        string? cat = cats.GetValueOrDefault("CatalogFile.NTamd64") ?? cats.GetValueOrDefault("CatalogFile.NT") ?? cats.GetValueOrDefault("CatalogFile");

        Guid? cg = Get("ClassGuid") is { } g && Guid.TryParse(g, out var gg) ? gg : null;
        return new InfHeader(Get("Class"), cg, Get("Provider"), driverVer, date, ver, DriverVersion.ParseOrNull(ver), cat, cats);
    }

    /// <summary>Lê as seções pedidas como chave → valor (primeira ocorrência), removendo comentários e aspas.</summary>
    internal static Dictionary<string, Dictionary<string, string>> ParseSections(string text, params string[] wanted)
    {
        var want = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                string name = end > 1 ? line[1..end].Trim() : "";
                if (want.Contains(name))
                {
                    if (!result.TryGetValue(name, out current)) result[name] = current = new(StringComparer.OrdinalIgnoreCase);
                }
                else current = null;
                continue;
            }
            if (current is null) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim().Trim('"');
            string value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\"\"", "\"");
            current.TryAdd(key, value);
        }
        return result;
    }

    private static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ';' && !inQuote) return line[..i];
        }
        return line.TrimEnd('\r');
    }

    private static string Resolve(string value, Dictionary<string, string> strings)
    {
        if (value.Length > 2 && value[0] == '%' && value[^1] == '%' && value.IndexOf('%', 1) == value.Length - 1)
            return strings.TryGetValue(value[1..^1], out var s) ? s : value;
        return value;
    }
}
