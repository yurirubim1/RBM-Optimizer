using System.Globalization;
using System.Text.RegularExpressions;

namespace Rbm.Drivers;

/// <summary>
/// Versão de driver no formato do INF (<c>DriverVer = mm/dd/yyyy, w.x.y.z</c>): até 4 partes de 0 a 65535.
/// Comparação numérica parte a parte (partes ausentes valem 0), portanto "25.30.0.0001" == "25.30.0.1"
/// e "10.0.9" &lt; "10.0.10" (ao contrário de comparação de texto).
/// </summary>
public readonly record struct DriverVersion(int Major, int Minor, int Build, int Revision) : IComparable<DriverVersion>
{
    public int CompareTo(DriverVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Build.CompareTo(other.Build);
        return c != 0 ? c : Revision.CompareTo(other.Revision);
    }

    public static bool operator <(DriverVersion a, DriverVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(DriverVersion a, DriverVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(DriverVersion a, DriverVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DriverVersion a, DriverVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";

    /// <summary>Interpreta "w.x.y.z" (1 a 4 partes, cada uma 0..65535). Retorna false para texto inválido.</summary>
    public static bool TryParse(string? text, out DriverVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 4) return false;
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0 || p.Length > 5 || !p.All(char.IsAsciiDigit)) return false;
            int n = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
            if (n > 65535) return false;
            v[i] = n;
        }
        version = new DriverVersion(v[0], v[1], v[2], v[3]);
        return true;
    }

    public static DriverVersion? ParseOrNull(string? text) => TryParse(text, out var v) ? v : null;

    /// <summary>Converte o DWORDLONG de SP_DRVINFO_DATA.DriverVersion (16 bits por parte).</summary>
    public static DriverVersion FromPacked(ulong packed) =>
        new((int)((packed >> 48) & 0xFFFF), (int)((packed >> 32) & 0xFFFF), (int)((packed >> 16) & 0xFFFF), (int)(packed & 0xFFFF));
}

/// <summary>Comparação de drivers (versão primeiro, depois data) e interpretação de formatos textuais.</summary>
public static partial class DriverVersionMath
{
    /// <summary>
    /// Compara dois drivers: versão (numérica, 4 partes) e, em empate ou versão ilegível, data.
    /// Retorna &lt;0 se A é mais antigo, 0 se equivalente/indeterminado, &gt;0 se A é mais novo.
    /// É a mesma ordem usada pelo PnP para desempatar drivers de mesmo rank (data, depois versão),
    /// mas aqui a versão vem primeiro porque é o que o usuário compara — ver <see cref="CompareLikePnp"/>.
    /// </summary>
    public static int Compare(DriverVersion? versionA, DateTime? dateA, DriverVersion? versionB, DateTime? dateB)
    {
        if (versionA is { } va && versionB is { } vb)
        {
            int c = va.CompareTo(vb);
            if (c != 0) return c;
        }
        if (dateA is { } da && dateB is { } db) return da.Date.CompareTo(db.Date);
        return 0;
    }

    /// <summary>Ordem do PnP entre drivers de mesmo rank: data mais recente vence; empate → maior versão.</summary>
    public static int CompareLikePnp(DriverVersion? versionA, DateTime? dateA, DriverVersion? versionB, DateTime? dateB)
    {
        if (dateA is { } da && dateB is { } db && da.Date != db.Date) return da.Date.CompareTo(db.Date);
        if (versionA is { } va && versionB is { } vb) return va.CompareTo(vb);
        return 0;
    }

    [GeneratedRegex(@"^\s*(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})\s*,?\s*(?<v>\d{1,5}(?:\.\d{1,5}){0,3})\s*$")]
    private static partial Regex DateVersionRegex();

    /// <summary>
    /// Interpreta "MM/dd/yyyy w.x.y.z" (pnputil) ou "MM/dd/yyyy, w.x.y.z" (DriverVer do INF).
    /// O formato da data é sempre o americano, independentemente do idioma do Windows.
    /// </summary>
    public static bool TryParseDateAndVersion(string? text, out DateTime? date, out string? version)
    {
        date = null; version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = DateVersionRegex().Match(text);
        if (!m.Success) return false;
        int mo = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (mo is < 1 or > 12 || y < 1980 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return false;
        date = new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);
        version = m.Groups["v"].Value;
        return true;
    }

    /// <summary>Indica se o texto parece "data + versão" (usado pelo parser estrutural do pnputil).</summary>
    public static bool LooksLikeDateAndVersion(string? text) => TryParseDateAndVersion(text, out _, out _);
}
