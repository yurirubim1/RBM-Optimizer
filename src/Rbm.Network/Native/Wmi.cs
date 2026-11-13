using System.Management;

namespace Rbm.Network.Native;

/// <summary>Acesso WMI/CIM com captura de erro por classe (uma classe falhando não derruba a coleta).</summary>
internal static class Wmi
{
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string Cimv2 = @"root\cimv2";

    public sealed record QueryResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, string? Error);

    public static QueryResult Query(string scope, string wql, CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(30) });
            using var col = searcher.Get();
            foreach (ManagementBaseObject mo in col)
            {
                using (mo)
                {
                    ct.ThrowIfCancellationRequested();
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in mo.Properties) d[p.Name] = p.Value;
                    rows.Add(d);
                }
            }
            return new QueryResult(rows, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (ManagementException ex)
        {
            return new QueryResult(rows, $"{ex.ErrorCode}: {ex.Message.Trim()}");
        }
        catch (Exception ex)
        {
            return new QueryResult(rows, ex.Message.Trim());
        }
    }

    /// <summary>Escapa uma string para literal WQL entre aspas simples.</summary>
    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    public static string? Str(this IReadOnlyDictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v is not null ? v.ToString() : null;

    public static long? Long(this IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
    }

    public static ulong? ULong(this IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        try { return Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return null; }
    }

    public static bool? Bool(this IReadOnlyDictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v is bool b ? b : null;

    public static string[] StrArray(this IReadOnlyDictionary<string, object?> r, string k) =>
        r.TryGetValue(k, out var v) && v is Array a ? a.Cast<object?>().Select(x => x?.ToString() ?? "").ToArray() : Array.Empty<string>();

    /// <summary>Converte data CIM (DMTF "yyyymmddHHMMSS.mmmmmmsUUU") ou DateTime.</summary>
    public static DateTime? Date(this IReadOnlyDictionary<string, object?> r, string k)
    {
        if (!r.TryGetValue(k, out var v) || v is null) return null;
        if (v is DateTime dt) return dt;
        var s = v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { }
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }
}
