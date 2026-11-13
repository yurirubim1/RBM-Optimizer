namespace Rbm.Latency;

/// <summary>Faixa de endereços de um módulo de kernel carregado.</summary>
/// <param name="Approximate">true quando o tamanho foi inferido (ex.: EnumDeviceDrivers não informa tamanho).</param>
public sealed record KernelModuleRange(ulong Base, ulong Size, string FileName, string? ImagePath, bool Approximate = false)
{
    public ulong End => Base + Size;
    public bool Contains(ulong address) => address >= Base && address < End;
}

/// <summary>
/// Mapa endereço → módulo de kernel. Alimentado por eventos ETW Image DCStart/Load (faixa exata: ImageBase + ImageSize)
/// e, como reserva, por EnumDeviceDrivers (só base; tamanho aproximado pela próxima base).
/// Faixas exatas têm prioridade sobre aproximadas. Não é thread-safe (usar a partir de uma única thread de processamento).
/// </summary>
public sealed class KernelModuleMap
{
    private readonly Dictionary<ulong, KernelModuleRange> _byBase = new();
    private KernelModuleRange[]? _sorted;

    public int Count => _byBase.Count;
    public IEnumerable<KernelModuleRange> Modules => _byBase.Values;

    /// <summary>Endereços de kernel em x64 ficam na metade canônica superior.</summary>
    public static bool IsKernelAddress(ulong address) => address >= 0xFFFF800000000000UL;

    public void Add(ulong imageBase, ulong size, string? imagePath, bool approximate = false)
    {
        if (imageBase == 0 || size == 0) return;
        var name = FileNameOf(imagePath);
        if (string.IsNullOrEmpty(name)) return;
        if (_byBase.TryGetValue(imageBase, out var existing) && !existing.Approximate && approximate) return;
        _byBase[imageBase] = new KernelModuleRange(imageBase, size, name, imagePath, approximate);
        _sorted = null;
    }

    public void Remove(ulong imageBase)
    {
        if (_byBase.Remove(imageBase)) _sorted = null;
    }

    /// <summary>Resolve um endereço de rotina para o módulo que o contém; null se desconhecido.</summary>
    public KernelModuleRange? Resolve(ulong address)
    {
        if (_byBase.Count == 0) return null;
        _sorted ??= _byBase.Values.OrderBy(r => r.Base).ToArray();
        int lo = 0, hi = _sorted.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_sorted[mid].Base <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // Faixas aproximadas podem se sobrepor a exatas: examina as bases anteriores mais próximas,
        // preferindo uma faixa exata que contenha o endereço.
        KernelModuleRange? approx = null;
        for (int i = found; i >= 0 && i >= found - 8; i--)
        {
            var r = _sorted[i];
            if (!r.Contains(address)) continue;
            if (!r.Approximate) return r;
            approx ??= r;
        }
        return approx;
    }

    /// <summary>Nome do arquivo a partir de caminhos de kernel (\SystemRoot\..., \??\C:\..., C:\...).</summary>
    public static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().TrimEnd('\0');
        int idx = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return idx >= 0 ? p[(idx + 1)..] : p;
    }

    /// <summary>Monta faixas aproximadas a partir de bases (EnumDeviceDrivers): tamanho = distância até a próxima base (limitado).</summary>
    public void AddApproximateFromBases(IReadOnlyList<(ulong Base, string Name)> modules, ulong maxSize = 64UL * 1024 * 1024)
    {
        var sorted = modules.Where(m => m.Base != 0).OrderBy(m => m.Base).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            ulong size = i + 1 < sorted.Length ? Math.Min(sorted[i + 1].Base - sorted[i].Base, maxSize) : maxSize;
            Add(sorted[i].Base, size, sorted[i].Name, approximate: true);
        }
    }
}
