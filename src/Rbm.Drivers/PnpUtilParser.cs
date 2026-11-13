using System.Xml;
using System.Xml.Linq;

namespace Rbm.Drivers;

/// <summary>
/// Interpreta a saída de <c>pnputil /enum-drivers</c>.
/// <list type="bullet">
/// <item>XML (<c>/format xml</c>, disponível no Windows 11 recente — verificado no build 26200): estrutura estável e independente de idioma.</item>
/// <item>Texto (Windows 10/11 antigos): os rótulos são LOCALIZADOS ("Nome Publicado", "Published Name"...), então o parser
/// ignora os rótulos e identifica os campos pela FORMA do valor e pela ordem fixa dos campos.</item>
/// </list>
/// </summary>
public static class PnpUtilParser
{
    public static IReadOnlyList<DriverPackage> ParseXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using var sr = new StringReader(xml);
        using var xr = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(xr);
        var list = new List<DriverPackage>();
        foreach (var d in doc.Root?.Elements("Driver") ?? Enumerable.Empty<XElement>())
        {
            string? published = (string?)d.Attribute("DriverName");
            if (!DriverStoreRules.IsPublishedOemInf(published)) continue;
            string? E(string n) => d.Element(n)?.Value.Trim() is { Length: > 0 } v ? v : null;
            DriverVersionMath.TryParseDateAndVersion(E("DriverVersion"), out var date, out var ver);
            var devices = d.Element("Devices")?.Elements("Device")
                .Select(x => new PackageDeviceRef(((string?)x.Attribute("InstanceId") ?? "").Trim(),
                    x.Element("DeviceDescription")?.Value.Trim(), x.Element("Status")?.Value.Trim()))
                .Where(x => x.InstanceId.Length > 0).ToArray() ?? Array.Empty<PackageDeviceRef>();
            var attrs = d.Element("CatalogAttributes")?.Elements("Attribute").Select(a => a.Value.Trim()).Where(a => a.Length > 0).ToArray()
                ?? Array.Empty<string>();
            list.Add(new DriverPackage(
                published!.ToLowerInvariant(), E("OriginalName"), E("ProviderName"), E("ClassName"), ParseGuid(E("ClassGuid")),
                ParseGuid(E("ExtensionId")), ver, DriverVersion.ParseOrNull(ver), date, E("SignerName"), attrs,
                E("DriverPackageId"), E("FamilyId"), devices));
        }
        return list;
    }

    /// <summary>
    /// Parser estrutural do formato texto. Para cada bloco "rótulo: valor" iniciado por um valor "oemNN.inf":
    /// próximo ".inf" = nome original; 1º GUID = classe, 2º GUID = extensão; "mm/dd/aaaa x.y.z.w" = versão;
    /// textos livres antes da versão = provedor, classe; o 1º texto livre após a versão = assinante.
    /// </summary>
    public static IReadOnlyList<DriverPackage> ParseText(string text)
    {
        var list = new List<DriverPackage>();
        var values = new List<string>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n').Append(""))
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (line.Length == 0 || colon <= 0)
            {
                if (line.Length == 0) { Flush(); }
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            if (DriverStoreRules.IsPublishedOemInf(value)) Flush(); // novo bloco mesmo sem linha em branco
            values.Add(value);
        }
        return list;

        void Flush()
        {
            if (values.Count == 0) return;
            var v = values.ToArray();
            values.Clear();
            int start = Array.FindIndex(v, DriverStoreRules.IsPublishedOemInf);
            if (start < 0) return;
            string published = v[start].ToLowerInvariant();
            string? original = null, provider = null, className = null, signer = null, ver = null;
            Guid? classGuid = null, extId = null;
            DateTime? date = null;
            bool versionSeen = false;
            for (int i = start + 1; i < v.Length; i++)
            {
                var s = v[i];
                if (s.Length == 0) continue;
                if (original is null && !versionSeen && s.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) && !s.Contains(' ')) { original = s; continue; }
                if (ParseGuid(s) is { } g)
                {
                    if (classGuid is null) classGuid = g; else extId ??= g;
                    continue;
                }
                if (!versionSeen && DriverVersionMath.TryParseDateAndVersion(s, out var d, out var vv)) { date = d; ver = vv; versionSeen = true; continue; }
                if (!versionSeen)
                {
                    if (provider is null) provider = s;
                    else className ??= s;
                }
                else if (signer is null) signer = s;
            }
            list.Add(new DriverPackage(published, original, provider, className, classGuid, extId, ver, DriverVersion.ParseOrNull(ver),
                date, signer, Array.Empty<string>(), null, null, Array.Empty<PackageDeviceRef>()));
        }
    }

    private static Guid? ParseGuid(string? s) =>
        s is not null && s.StartsWith('{') && Guid.TryParse(s, out var g) ? g : null;
}
