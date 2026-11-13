namespace Rbm.Core.Hardware;

/// <summary>Inventário estático do computador, coletado ao iniciar o aplicativo.</summary>
public sealed class SystemProfile
{
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.Now;
    public CpuInfo Cpu { get; init; } = new();
    public List<GpuInfo> Gpus { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public BoardInfo Board { get; init; } = new();
    public OsInfo Os { get; init; } = new();
    public List<DiskInfo> Disks { get; init; } = new();
    public List<VolumeInfo> Volumes { get; init; } = new();
    public PowerInfo Power { get; init; } = new();
    public List<DisplayInfo> Displays { get; init; } = new();
    public FormFactor FormFactor { get; init; }
    /// <summary>Avisos de coleta (fontes indisponíveis, falhas de WMI etc.).</summary>
    public List<string> CollectionWarnings { get; init; } = new();

    public GpuInfo? PrimaryGpu => Gpus.Where(g => !g.IsVirtual).OrderByDescending(g => g.IsDiscrete).ThenByDescending(g => g.DedicatedVideoMemoryBytes).FirstOrDefault();
}

public sealed class CpuInfo
{
    public string Name { get; set; } = "Desconhecido";
    public CpuVendor Vendor { get; set; }
    public string Manufacturer { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public int BaseClockMHz { get; set; }
    public int MaxClockMHz { get; set; }
    public long L2CacheKB { get; set; }
    public long L3CacheKB { get; set; }
    public string Socket { get; set; } = "";
    public string Family { get; set; } = "";
    public bool VirtualizationFirmwareEnabled { get; set; }
    /// <summary>CPU híbrida (P-cores + E-cores) detectada via EfficiencyClass.</summary>
    public bool IsHybrid { get; set; }
    public int PerformanceCores { get; set; }
    public int EfficiencyCores { get; set; }
    /// <summary>Número de domínios de cache L3 (aprox. CCDs/CCXs em AMD).</summary>
    public int L3Domains { get; set; }
    public List<CoreInfo> Cores { get; set; } = new();
}

public sealed record CoreInfo(int CoreIndex, int Group, ulong Mask, byte EfficiencyClass, int L3Domain, int[] LogicalProcessors);

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public GpuVendor Vendor { get; set; }
    public string PnpDeviceId { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public long DedicatedVideoMemoryBytes { get; set; }
    public long SharedSystemMemoryBytes { get; set; }
    public bool IsDiscrete { get; set; }
    public bool IsVirtual { get; set; }
    /// <summary>LUID do adaptador DXGI (usado para casar contadores "GPU Engine").</summary>
    public long AdapterLuid { get; set; }
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public string CurrentResolution { get; set; } = "";
    public int CurrentRefreshRate { get; set; }
}

public sealed class MemoryInfo
{
    public long TotalPhysicalBytes { get; set; }
    public List<MemoryModule> Modules { get; set; } = new();
    public int SlotsTotal { get; set; }
    /// <summary>Estimativa de canais ocupados (heurística baseada em BankLabel/DeviceLocator). Null = indeterminado.</summary>
    public int? EstimatedChannels { get; set; }
    public string ChannelEvidence { get; set; } = "";
    /// <summary>Velocidade configurada (MT/s) — reflete XMP/EXPO quando ativo.</summary>
    public int ConfiguredSpeedMTs => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredSpeedMTs);
    /// <summary>Velocidade nominal máxima declarada pelo SPD (MT/s).</summary>
    public int RatedSpeedMTs => Modules.Count == 0 ? 0 : Modules.Min(m => m.SpeedMTs);
    public string MemoryType { get; set; } = "";
}

public sealed record MemoryModule(string Manufacturer, string PartNumber, long CapacityBytes, int SpeedMTs, int ConfiguredSpeedMTs,
    string DeviceLocator, string BankLabel, int SmbiosMemoryType, string FormFactor);

public sealed class BoardInfo
{
    public string Manufacturer { get; set; } = "";
    public string Product { get; set; } = "";
    public string SystemManufacturer { get; set; } = "";
    public string SystemModel { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public bool? UefiMode { get; set; }
    public bool? SecureBoot { get; set; }
}

public sealed class OsInfo
{
    public string Caption { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public int Build { get; set; }
    public int Ubr { get; set; }
    public string Edition { get; set; } = "";
    public string Architecture { get; set; } = "";
    public DateTime? InstallDate { get; set; }
    public DateTime LastBoot { get; set; }
    public TimeSpan Uptime => DateTime.Now - LastBoot;
    public bool IsWindows11 => Build >= 22000;
    public string MachineName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public bool HvciEnabled { get; set; }
    public bool? VbsRunning { get; set; }
}

public sealed class DiskInfo
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public long SizeBytes { get; set; }
    public StorageMediaType MediaType { get; set; }
    public StorageBusType BusType { get; set; }
    public string HealthStatus { get; set; } = "";
    public string OperationalStatus { get; set; } = "";
    public bool IsSystemDisk { get; set; }
    public string DeviceId { get; set; } = "";
}

public sealed class VolumeInfo
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    public int? DiskNumber { get; set; }
    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
}

public sealed class PowerInfo
{
    public bool HasBattery { get; set; }
    public bool OnAcPower { get; set; } = true;
    public int? BatteryPercent { get; set; }
    public string ActiveSchemeName { get; set; } = "";
    public Guid ActiveSchemeGuid { get; set; }
    public bool? ModernStandby { get; set; }
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public int BitsPerPixel { get; set; }
    public bool IsPrimary { get; set; }
}
