using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Core.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace Rbm.Hardware;

/// <summary>
/// Inventário real do sistema via WMI/CIM, Registro, DXGI e APIs Win32.
/// Cada fonte é isolada: falhas viram avisos em <see cref="SystemProfile.CollectionWarnings"/>, nunca dados inventados.
/// </summary>
public sealed partial class SystemInventory : ISystemInventory
{
    private readonly ILogger _log;
    public SystemProfile? Current { get; private set; }

    public SystemInventory(ILogger<SystemInventory>? log = null) => _log = (ILogger?)log ?? NullLogger.Instance;

    public Task<SystemProfile> CollectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var warnings = new List<string>();
        var cpu = Safe(ReadCpu, new CpuInfo(), "CPU", warnings);
        var gpus = Safe(ReadGpus, new List<GpuInfo>(), "GPU", warnings);
        var mem = Safe(ReadMemory, new MemoryInfo(), "Memória", warnings);
        var board = Safe(ReadBoard, new BoardInfo(), "Placa-mãe/BIOS", warnings);
        var os = Safe(ReadOs, new OsInfo(), "Sistema operacional", warnings);
        var disks = Safe(ReadDisks, new List<DiskInfo>(), "Discos", warnings);
        var vols = Safe(() => ReadVolumes(disks), new List<VolumeInfo>(), "Volumes", warnings);
        var power = Safe(ReadPower, new PowerInfo(), "Energia", warnings);
        var displays = Safe(ReadDisplays, new List<DisplayInfo>(), "Monitores", warnings);
        var ff = Safe(() => ReadFormFactor(power), FormFactor.Unknown, "Formato", warnings);
        ct.ThrowIfCancellationRequested();

        var profile = new SystemProfile
        {
            Cpu = cpu, Gpus = gpus, Memory = mem, Board = board, Os = os, Disks = disks, Volumes = vols,
            Power = power, Displays = displays, FormFactor = ff, CollectionWarnings = warnings,
        };
        Current = profile;
        return profile;
    }, ct);

    private T Safe<T>(Func<T> f, T fallback, string area, List<string> warnings)
    {
        try { return f(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao coletar {Area}", area);
            warnings.Add($"{area}: {ex.Message}");
            return fallback;
        }
    }

    public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        searcher.Options.Timeout = TimeSpan.FromSeconds(20);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    private static string S(ManagementBaseObject o, string p) => (o[p]?.ToString() ?? "").Trim();
    private static long L(ManagementBaseObject o, string p) => o[p] is null ? 0 : Convert.ToInt64(o[p]);
    private static int I(ManagementBaseObject o, string p) => o[p] is null ? 0 : Convert.ToInt32(o[p]);

    private static DateTime? WmiDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }

    // ---------------- CPU ----------------
    private static CpuInfo ReadCpu()
    {
        var cpu = new CpuInfo();
        foreach (var o in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, Caption, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            cpu.Name = Regex.Replace(S(o, "Name"), @"\s+", " ");
            cpu.Manufacturer = S(o, "Manufacturer");
            cpu.PhysicalCores += I(o, "NumberOfCores");
            cpu.LogicalProcessors += I(o, "NumberOfLogicalProcessors");
            cpu.BaseClockMHz = I(o, "MaxClockSpeed");
            cpu.L2CacheKB = L(o, "L2CacheSize");
            cpu.L3CacheKB = L(o, "L3CacheSize");
            cpu.Socket = S(o, "SocketDesignation");
            cpu.Family = S(o, "Caption");
            cpu.VirtualizationFirmwareEnabled = o["VirtualizationFirmwareEnabled"] is bool b && b;
        }
        cpu.Vendor = cpu.Manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Intel
                   : cpu.Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? CpuVendor.Amd
                   : CpuVendor.Other;
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            if (k?.GetValue("~MHz") is int mhz && cpu.BaseClockMHz == 0) cpu.BaseClockMHz = mhz;
        CpuTopology.Apply(cpu);
        return cpu;
    }

    // ---------------- GPU ----------------
    private static List<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();
        foreach (var o in Query("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var g = new GpuInfo
            {
                Name = S(o, "Name"),
                PnpDeviceId = S(o, "PNPDeviceID"),
                DriverVersion = S(o, "DriverVersion"),
                DriverDate = WmiDate(S(o, "DriverDate")),
                CurrentResolution = o["CurrentHorizontalResolution"] is null ? "" : $"{I(o, "CurrentHorizontalResolution")}×{I(o, "CurrentVerticalResolution")}",
                CurrentRefreshRate = I(o, "CurrentRefreshRate"),
            };
            var m = VenDevRegex().Match(g.PnpDeviceId);
            if (m.Success)
            {
                g.VendorId = Convert.ToUInt32(m.Groups[1].Value, 16);
                g.DeviceId = Convert.ToUInt32(m.Groups[2].Value, 16);
            }
            g.Vendor = VendorFrom(g.VendorId, g.Name);
            g.IsVirtual = !g.PnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);
            gpus.Add(g);
        }

        // DXGI: VRAM real (64 bits) e LUID; casa por vendor/device.
        try
        {
            foreach (var a in Dxgi.EnumerateAdapters().Where(a => !a.IsSoftware))
            {
                var g = gpus.FirstOrDefault(x => x.VendorId == a.VendorId && x.DeviceId == a.DeviceId && x.AdapterLuid == 0)
                        ?? gpus.FirstOrDefault(x => x.Name.Equals(a.Description, StringComparison.OrdinalIgnoreCase) && x.AdapterLuid == 0);
                if (g is null) continue;
                g.AdapterLuid = a.Luid;
                g.DedicatedVideoMemoryBytes = (long)a.DedicatedVideoMemory;
                g.SharedSystemMemoryBytes = (long)a.SharedSystemMemory;
            }
        }
        catch { /* DXGI indisponível (sessão remota/serviço) */ }

        foreach (var g in gpus)
            g.IsDiscrete = !g.IsVirtual && (g.Vendor == GpuVendor.Nvidia
                || (g.Vendor == GpuVendor.Amd && g.DedicatedVideoMemoryBytes > 1L << 30)
                || (g.Vendor == GpuVendor.Intel && g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)));
        return gpus;
    }

    public static GpuVendor VendorFrom(uint vendorId, string name) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x1414 => GpuVendor.Microsoft,
        0 when name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => GpuVendor.Nvidia,
        0 when name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase) => GpuVendor.Amd,
        0 when name.Contains("Intel", StringComparison.OrdinalIgnoreCase) => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    [GeneratedRegex(@"VEN_([0-9A-F]{4}).*DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VenDevRegex();

    [GeneratedRegex(@"(?:CHANNEL\s*|Channel|\bCH\s*)([A-H])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();

    // ---------------- Memória ----------------
    private static MemoryInfo ReadMemory()
    {
        var mem = new MemoryInfo();
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) mem.TotalPhysicalBytes = (long)ms.ullTotalPhys;

        foreach (var o in Query("SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, DeviceLocator, BankLabel, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
        {
            mem.Modules.Add(new MemoryModule(S(o, "Manufacturer"), S(o, "PartNumber"), L(o, "Capacity"), I(o, "Speed"), I(o, "ConfiguredClockSpeed"),
                S(o, "DeviceLocator"), S(o, "BankLabel"), I(o, "SMBIOSMemoryType"), I(o, "FormFactor") == 12 ? "SODIMM" : "DIMM"));
        }
        foreach (var o in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
            mem.SlotsTotal += I(o, "MemoryDevices");

        int type = mem.Modules.FirstOrDefault()?.SmbiosMemoryType ?? 0;
        mem.MemoryType = type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 30 => "LPDDR4", 35 => "LPDDR5", 0 => "", _ => $"Tipo SMBIOS {type}" };
        (mem.EstimatedChannels, mem.ChannelEvidence) = EstimateChannels(mem.Modules);
        return mem;
    }

    /// <summary>
    /// Estima canais ocupados a partir de BankLabel/DeviceLocator (formatos variam por fabricante).
    /// Retorna null quando não é possível concluir — nunca chuta.
    /// </summary>
    public static (int?, string) EstimateChannels(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return (null, "Nenhum módulo reportado pelo SMBIOS.");
        var channels = new HashSet<string>();
        foreach (var m in modules)
        {
            var cm = ChannelRegex().Match($"{m.BankLabel} {m.DeviceLocator}");
            if (cm.Success) channels.Add(cm.Groups[1].Value.ToUpperInvariant());
        }
        if (channels.Count > 0)
            return (channels.Count, $"Rótulos SMBIOS indicam canal(is): {string.Join(", ", channels.OrderBy(c => c))} ({modules.Count} módulo(s)).");
        if (modules.Count == 1)
            return (1, "Apenas um módulo instalado: operação em canal único (em DDR5 cada módulo tem 2 subcanais de 32 bits, mas a largura total equivale a um canal de 64 bits).");
        return (null, $"{modules.Count} módulos, mas os rótulos SMBIOS ('{modules[0].BankLabel}', '{modules[0].DeviceLocator}') não identificam canais. Consulte o manual da placa-mãe para os slots recomendados.");
    }

    // ---------------- Placa-mãe / BIOS ----------------
    private static BoardInfo ReadBoard()
    {
        var b = new BoardInfo();
        foreach (var o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard")) { b.Manufacturer = S(o, "Manufacturer"); b.Product = S(o, "Product"); }
        foreach (var o in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem")) { b.SystemManufacturer = S(o, "Manufacturer"); b.SystemModel = S(o, "Model"); }
        foreach (var o in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS")) { b.BiosVendor = S(o, "Manufacturer"); b.BiosVersion = S(o, "SMBIOSBIOSVersion"); b.BiosDate = WmiDate(S(o, "ReleaseDate")); }
        if (GetFirmwareType(out int fw)) b.UefiMode = fw == 2;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
            if (k?.GetValue("UEFISecureBootEnabled") is int sb) b.SecureBoot = sb == 1;
        return b;
    }

    // ---------------- SO ----------------
    private static OsInfo ReadOs()
    {
        var os = new OsInfo();
        foreach (var o in Query("SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            os.Caption = S(o, "Caption");
            os.Build = int.TryParse(S(o, "BuildNumber"), out var b) ? b : Environment.OSVersion.Version.Build;
            os.Architecture = S(o, "OSArchitecture");
            os.InstallDate = WmiDate(S(o, "InstallDate"));
            os.LastBoot = WmiDate(S(o, "LastBootUpTime")) ?? DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            os.DisplayVersion = k?.GetValue("DisplayVersion")?.ToString() ?? k?.GetValue("ReleaseId")?.ToString() ?? "";
            os.Ubr = k?.GetValue("UBR") is int u ? u : 0;
            os.Edition = k?.GetValue("EditionID")?.ToString() ?? "";
        }
        if (os.Build >= 22000 && os.Caption.Contains("Windows 10")) os.Caption = os.Caption.Replace("Windows 10", "Windows 11");
        try
        {
            foreach (var o in Query("SELECT SecurityServicesRunning, VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard"))
            {
                if (o["SecurityServicesRunning"] is uint[] running) os.HvciEnabled = running.Contains(2u);
                if (o["VirtualizationBasedSecurityStatus"] is uint vbs) os.VbsRunning = vbs == 2;
            }
        }
        catch { /* DeviceGuard indisponível */ }
        return os;
    }

    // ---------------- Discos ----------------
    private static List<DiskInfo> ReadDisks()
    {
        var list = new List<DiskInfo>();
        foreach (var o in Query("SELECT DeviceId, FriendlyName, SerialNumber, FirmwareVersion, Size, MediaType, BusType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
        {
            var d = new DiskInfo
            {
                DeviceId = S(o, "DeviceId"),
                Number = int.TryParse(S(o, "DeviceId"), out var n) ? n : -1,
                Model = S(o, "FriendlyName"),
                Serial = S(o, "SerialNumber"),
                Firmware = S(o, "FirmwareVersion"),
                SizeBytes = L(o, "Size"),
                MediaType = I(o, "MediaType") switch { 3 => StorageMediaType.Hdd, 4 => StorageMediaType.Ssd, 5 => StorageMediaType.Scm, _ => StorageMediaType.Unknown },
                BusType = I(o, "BusType") switch { 11 => StorageBusType.Sata, 17 => StorageBusType.Nvme, 7 => StorageBusType.Usb, 10 => StorageBusType.Sas, 8 => StorageBusType.Raid, 14 or 15 => StorageBusType.Virtual, 0 => StorageBusType.Unknown, _ => StorageBusType.Other },
                HealthStatus = I(o, "HealthStatus") switch { 0 => "Saudável", 1 => "Atenção", 2 => "Não saudável", _ => "Desconhecido" },
                OperationalStatus = o["OperationalStatus"] is ushort[] st ? string.Join(", ", st.Select(OpStatus)) : "",
            };
            if (d.MediaType == StorageMediaType.Unknown && d.BusType == StorageBusType.Nvme) d.MediaType = StorageMediaType.Ssd;
            list.Add(d);
        }
        return list.OrderBy(d => d.Number).ToList();
    }

    private static string OpStatus(ushort s) => s switch
    {
        2 => "OK", 3 => "Degradado", 5 => "Falha prevista", 6 => "Erro", 10 => "Parado", 11 => "Em serviço", 13 => "Perda de comunicação", 0xD00D => "Em manutenção",
        _ => s.ToString(),
    };

    private static List<VolumeInfo> ReadVolumes(List<DiskInfo> disks)
    {
        var map = new Dictionary<char, int>();
        try
        {
            foreach (var o in Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
                if (o["DriveLetter"] is char c && c != '\0') map[char.ToUpperInvariant(c)] = I(o, "DiskNumber");
        }
        catch { }
        var sysLetter = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var letter = char.ToUpperInvariant(d.Name[0]);
            list.Add(new VolumeInfo
            {
                Letter = $"{letter}:", Label = d.VolumeLabel, FileSystem = d.DriveFormat, TotalBytes = d.TotalSize, FreeBytes = d.AvailableFreeSpace,
                IsSystem = letter == sysLetter, DiskNumber = map.TryGetValue(letter, out var n) ? n : null,
            });
        }
        foreach (var v in list.Where(v => v.IsSystem && v.DiskNumber is not null))
        {
            var disk = disks.FirstOrDefault(x => x.Number == v.DiskNumber);
            if (disk is not null) disk.IsSystemDisk = true;
        }
        return list;
    }

    // ---------------- Energia ----------------
    private static PowerInfo ReadPower()
    {
        var p = new PowerInfo();
        if (GetSystemPowerStatus(out var sps))
        {
            p.OnAcPower = sps.ACLineStatus != 0;
            p.HasBattery = (sps.BatteryFlag & 128) == 0 && sps.BatteryFlag != 255;
            if (p.HasBattery && sps.BatteryLifePercent <= 100) p.BatteryPercent = sps.BatteryLifePercent;
        }
        try
        {
            var g = PowerSchemes.Active();
            p.ActiveSchemeGuid = g;
            p.ActiveSchemeName = PowerSchemes.Name(g);
        }
        catch { }
        return p;
    }

    private static FormFactor ReadFormFactor(PowerInfo power)
    {
        foreach (var o in Query("SELECT PCSystemType FROM Win32_ComputerSystem"))
        {
            var t = I(o, "PCSystemType");
            if (t == 2) return FormFactor.Laptop;
        }
        foreach (var o in Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            if (o["ChassisTypes"] is ushort[] ct)
            {
                if (ct.Any(c => c is 8 or 9 or 10 or 14 or 30 or 31 or 32)) return FormFactor.Laptop;
                if (ct.Any(c => c is 3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 35 or 36)) return FormFactor.Desktop;
            }
        return power.HasBattery ? FormFactor.Laptop : FormFactor.Desktop;
    }

    // ---------------- Monitores ----------------
    private static List<DisplayInfo> ReadDisplays()
    {
        var list = new List<DisplayInfo>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 1) == 0) continue; // não anexado à área de trabalho
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref dm)) continue;
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string friendly = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : dd.DeviceString;
            list.Add(new DisplayInfo
            {
                DeviceName = dd.DeviceName, FriendlyName = friendly, Width = dm.dmPelsWidth, Height = dm.dmPelsHeight,
                RefreshRate = dm.dmDisplayFrequency, BitsPerPixel = dm.dmBitsPerPel, IsPrimary = (dd.StateFlags & 4) != 0,
            });
        }
        return list;
    }

    #region Interop
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out int firmwareType);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    #endregion
}
