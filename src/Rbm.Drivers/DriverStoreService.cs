using System.Text;
using Rbm.Core;

namespace Rbm.Drivers;

/// <summary>
/// Pacotes de terceiros no Driver Store via <c>pnputil /enum-drivers</c> (funciona SEM elevação — verificado no build 26200).
/// Usa <c>/format xml /devices</c> quando disponível; caso contrário, cai para o formato texto (parser estrutural).
/// </summary>
public sealed class DriverStoreService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public static string PnpUtilPath => ProcessRunner.System32("pnputil.exe");

    public async Task<DriverStoreInventory> EnumerateAsync(CancellationToken ct = default)
    {
        var warnings = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), $"rbm-pnputil-{Guid.NewGuid():N}.xml");
        try
        {
            // Saída em arquivo: o XML é gravado em UTF-8 conforme declarado, sem depender da página de código do console.
            var r = await ProcessRunner.RunAsync(PnpUtilPath,
                ["/enum-drivers", "/format", "xml", "/ids", "/devices", "/output-file", tmp], Timeout, ct).ConfigureAwait(false);
            if (r.Success && File.Exists(tmp))
            {
                string xml = await File.ReadAllTextAsync(tmp, ct).ConfigureAwait(false);
                if (xml.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) || xml.TrimStart().StartsWith("<PnpUtil", StringComparison.Ordinal))
                {
                    var pk = PnpUtilParser.ParseXml(xml);
                    return new DriverStoreInventory(DateTimeOffset.Now, pk, "pnputil-xml", warnings);
                }
            }
            warnings.Add(r.TimedOut ? "pnputil /format xml excedeu o tempo limite." : "pnputil desta versão do Windows não suporta /format xml; usando o formato texto.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add($"Falha no modo XML do pnputil: {ex.Message}"); }
        finally { TryDelete(tmp); }

        var t = await ProcessRunner.RunAsync(PnpUtilPath, ["/enum-drivers"], Timeout, ct, ProcessRunner.OemEncoding).ConfigureAwait(false);
        if (!t.Success)
        {
            warnings.Add($"pnputil /enum-drivers falhou (código {t.ExitCode}{(t.TimedOut ? ", tempo esgotado" : "")}).");
            return new DriverStoreInventory(DateTimeOffset.Now, Array.Empty<DriverPackage>(), "indisponível", warnings);
        }
        var packages = PnpUtilParser.ParseText(t.StdOut);
        warnings.Add("Formato texto: sem pasta do pacote no store (DriverPackageId) nem lista de dispositivos por pacote.");
        return new DriverStoreInventory(DateTimeOffset.Now, packages, "pnputil-text", warnings);
    }

    /// <summary>
    /// Junta inventário de dispositivos e pacotes: dispositivos por pacote (pelo oemNN.inf instalado — inclui não presentes),
    /// grupos com mais de uma versão do mesmo driver e pacotes sem dispositivo associado.
    /// </summary>
    public static DriverStoreAnalysis Analyze(DeviceInventory inventory, DriverStoreInventory store)
    {
        var byPkg = new Dictionary<string, List<DeviceRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in inventory.Devices)
            if (d.Driver is { IsThirdParty: true } drv)
            {
                if (!byPkg.TryGetValue(drv.InfName, out var l)) byPkg[drv.InfName] = l = new();
                l.Add(d);
            }
        // Complementa com o mapeamento do pnputil /devices (pacotes de extensão aparecem aqui, não no DEVPKEY_Device_DriverInfPath).
        foreach (var p in store.Packages)
            foreach (var pd in p.Devices)
                if (inventory.Find(pd.InstanceId) is { } dev)
                {
                    if (!byPkg.TryGetValue(p.PublishedName, out var l)) byPkg[p.PublishedName] = l = new();
                    if (!l.Contains(dev)) l.Add(dev);
                }

        var duplicates = store.Packages
            .GroupBy(p => p.FamilyKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(p => p, PackageNewestFirst.Instance).ToArray();
                var inUse = ordered.Where(p => byPkg.ContainsKey(p.PublishedName)).Select(p => p.PublishedName).ToArray();
                return new DuplicatePackageGroup(g.Key, ordered[0].OriginalName ?? ordered[0].PublishedName, ordered, inUse);
            })
            .OrderBy(g => g.OriginalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var unreferenced = store.Packages.Where(p => !byPkg.ContainsKey(p.PublishedName)).ToArray();
        return new DriverStoreAnalysis(
            byPkg.ToDictionary(k => k.Key, v => (IReadOnlyList<DeviceRecord>)v.Value, StringComparer.OrdinalIgnoreCase),
            duplicates, unreferenced);
    }

    /// <summary>Ordena pacotes por versão e depois data (crescente; use OrderByDescending para "mais novo primeiro").</summary>
    public sealed class PackageNewestFirst : IComparer<DriverPackage>
    {
        public static readonly PackageNewestFirst Instance = new();
        public int Compare(DriverPackage? x, DriverPackage? y)
        {
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
            int c = DriverVersionMath.Compare(x.ParsedVersion, x.Date, y.ParsedVersion, y.Date);
            return c != 0 ? c : string.Compare(x.PublishedName, y.PublishedName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
