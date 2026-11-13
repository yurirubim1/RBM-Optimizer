using System.Numerics;
using System.Runtime.InteropServices;
using Rbm.Core.Hardware;

namespace Rbm.Hardware;

/// <summary>
/// Topologia de CPU via GetLogicalProcessorInformationEx: núcleos físicos, EfficiencyClass
/// (P-core vs E-core em CPUs híbridas) e domínios de L3 (aproxima CCDs/CCXs em AMD).
/// </summary>
public static class CpuTopology
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    public static (List<CoreInfo> cores, int l3Domains) Read()
    {
        var cores = new List<CoreInfo>();
        var l3Masks = new List<(ushort group, ulong mask)>();

        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF /* RelationAll */, IntPtr.Zero, ref len);
        if (len == 0) return (cores, 0);
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return (cores, 0);
            int offset = 0;
            var rawCores = new List<(ushort group, ulong mask, byte eff)>();
            while (offset < len)
            {
                IntPtr p = buf + offset;
                int relationship = Marshal.ReadInt32(p);
                int size = Marshal.ReadInt32(p, 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore)
                {
                    // PROCESSOR_RELATIONSHIP: Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GroupMask[...]
                    byte eff = Marshal.ReadByte(p, 8 + 1);
                    ushort groupCount = (ushort)Marshal.ReadInt16(p, 8 + 22);
                    // GROUP_AFFINITY alinhado a 8: começa em 8 + 24
                    for (int g = 0; g < groupCount; g++)
                    {
                        IntPtr ga = p + 8 + 24 + g * 16;
                        ulong mask = (ulong)Marshal.ReadInt64(ga);
                        ushort group = (ushort)Marshal.ReadInt16(ga, 8);
                        rawCores.Add((group, mask, eff));
                    }
                }
                else if (relationship == RelationCache)
                {
                    // CACHE_RELATIONSHIP: Level(1) Associativity(1) LineSize(2) CacheSize(4) Type(4) Reserved(18) GroupCount(2) GroupMask
                    byte level = Marshal.ReadByte(p, 8);
                    if (level == 3)
                    {
                        ushort gc = (ushort)Marshal.ReadInt16(p, 8 + 30);
                        int count = Math.Max((int)gc, 1);
                        for (int g = 0; g < count; g++)
                        {
                            IntPtr ga = p + 8 + 32 + g * 16;
                            l3Masks.Add(((ushort)Marshal.ReadInt16(ga, 8), (ulong)Marshal.ReadInt64(ga)));
                        }
                    }
                }
                offset += size;
            }

            int idx = 0;
            foreach (var (group, mask, eff) in rawCores)
            {
                int l3 = l3Masks.FindIndex(m => m.group == group && (m.mask & mask) != 0);
                var lps = Enumerable.Range(0, 64).Where(b => (mask & (1UL << b)) != 0).Select(b => group * 64 + b).ToArray();
                cores.Add(new CoreInfo(idx++, group, mask, eff, l3, lps));
            }
            return (cores, l3Masks.Count);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Preenche campos de topologia em <paramref name="cpu"/>.</summary>
    public static void Apply(CpuInfo cpu)
    {
        var (cores, l3) = Read();
        cpu.Cores = cores;
        cpu.L3Domains = l3;
        if (cores.Count > 0)
        {
            var classes = cores.Select(c => c.EfficiencyClass).Distinct().ToList();
            cpu.IsHybrid = classes.Count > 1;
            if (cpu.IsHybrid)
            {
                byte max = classes.Max();
                cpu.PerformanceCores = cores.Count(c => c.EfficiencyClass == max);
                cpu.EfficiencyCores = cores.Count - cpu.PerformanceCores;
            }
            else cpu.PerformanceCores = cores.Count;
        }
    }

    public static int PopCount(ulong mask) => BitOperations.PopCount(mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
