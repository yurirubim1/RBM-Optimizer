using System.Text.RegularExpressions;

namespace Rbm.Drivers;

/// <summary>Regras/caminhos do Driver Store e validações simples de nomes (usadas também no agente elevado).</summary>
public static partial class DriverStoreRules
{
    public static string WindowsDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string InfDirectory { get; } = Path.Combine(WindowsDirectory, "INF");
    public static string FileRepository { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemInfRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]{1,120}\.inf_[A-Za-z0-9]{2,10}_[0-9a-fA-F]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\. ]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeFileNameRegex();

    /// <summary>"oemNN.inf" (nome publicado de pacote de terceiro).</summary>
    public static bool IsPublishedOemInf(string? name) => name is not null && OemInfRegex().IsMatch(name);

    /// <summary>Nome de pasta do FileRepository (ex.: "amdafd.inf_amd64_d6709807244bfcf6").</summary>
    public static bool IsSafePackageId(string? id) => id is not null && PackageIdRegex().IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>Nome de arquivo simples (sem separadores, sem "..").</summary>
    public static bool IsSafeFileName(string? name) => name is not null && SafeFileNameRegex().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    public static string FamilyKey(string originalName, Guid? classGuid, Guid? extensionId) =>
        $"{originalName.ToLowerInvariant()}|{classGuid?.ToString() ?? "-"}|{extensionId?.ToString() ?? "-"}";

    /// <summary>Caminho completo de um INF instalado (%windir%\INF\name). Null se o nome não for um arquivo simples.</summary>
    public static string? InstalledInfPath(string? infName) =>
        IsSafeFileName(infName) ? Path.Combine(InfDirectory, infName!) : null;
}
