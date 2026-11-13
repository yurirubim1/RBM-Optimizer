using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Rbm.Drivers.Native;
using static Rbm.Drivers.Native.NativeMethods;

namespace Rbm.Drivers;

/// <summary>
/// Inventário de dispositivos PnP e seus drivers. Fonte principal: SetupAPI/CfgMgr32 (rápido, sem elevação);
/// fallback: WMI (Win32_PnPEntity + Win32_PnPSignedDriver). Seguro para chamar fora da thread de UI
/// (todo o trabalho roda em <see cref="Task.Run(Action)"/>).
/// </summary>
public sealed class DeviceInventoryService
{
    // Cache de assinatura por caminho de INF + data de modificação (SetupVerifyInfFile consulta catálogos e é o passo mais caro).
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, DriverSignature Sig)> SignatureCache = new(StringComparer.OrdinalIgnoreCase);

    public Task<DeviceInventory> CollectAsync(CancellationToken ct = default) => CollectAsync(InventoryOptions.Default, ct);

    public Task<DeviceInventory> CollectAsync(InventoryOptions options, CancellationToken ct = default) =>
        Task.Run(() => Collect(options, ct), ct);

    /// <summary>Versão síncrona (para o agente ou testes). Não chame na thread de UI.</summary>
    public DeviceInventory Collect(InventoryOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        try
        {
            var devices = CollectSetupApi(options, warnings, ct);
            return new DeviceInventory(DateTimeOffset.Now, devices, "SetupAPI", sw.Elapsed, warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (options.AllowWmiFallback)
        {
            warnings.Add($"SetupAPI indisponível ({ex.Message}); usando WMI como alternativa (mais lento, sem flags DN_*).");
            var devices = CollectWmi(options, warnings, ct);
            return new DeviceInventory(DateTimeOffset.Now, devices, "WMI", sw.Elapsed, warnings);
        }
    }

    /// <summary>Relê apenas um dispositivo (usado na verificação pós-instalação). Null se não existir.</summary>
    public DeviceRecord? CollectOne(string instanceId, bool resolveSignature = true)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 400) return null;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == InvalidHandle) return null;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            // DIOD_INHERIT_CLASSDRVS = 0x2 não é necessário; 0 abre também dispositivos não presentes.
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) return null;
            var reader = new PropertyReader(set);
            return ReadDevice(reader, ref data, instanceId, resolveSignature, out _);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ------------------------------------------------------------------ SetupAPI

    private static List<DeviceRecord> CollectSetupApi(InventoryOptions options, List<string> warnings, CancellationToken ct)
    {
        uint flags = DIGCF_ALLCLASSES | (options.IncludeNotPresent ? 0 : DIGCF_PRESENT);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, flags);
        if (set == InvalidHandle)
            throw new InvalidOperationException($"SetupDiGetClassDevs falhou (Win32 {Marshal.GetLastWin32Error()}).");
        var result = new List<DeviceRecord>(1024);
        int sigFailures = 0;
        try
        {
            var reader = new PropertyReader(set);
            var idBuf = new char[1024];
            for (uint i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested();
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_ITEMS) break;
                    throw new InvalidOperationException($"SetupDiEnumDeviceInfo falhou (Win32 {err}).");
                }
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, (uint)idBuf.Length, out uint req))
                {
                    warnings.Add($"Dispositivo #{i}: ID de instância ilegível (Win32 {Marshal.GetLastWin32Error()}).");
                    continue;
                }
                string instanceId = new string(idBuf, 0, (int)Math.Max(0, req - 1)).TrimEnd('\0');
                var rec = ReadDevice(reader, ref data, instanceId, options.ResolveSignatures, out bool sigFailed);
                if (sigFailed) sigFailures++;
                result.Add(rec);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        if (sigFailures > 0)
            warnings.Add($"Assinatura não verificável para {sigFailures} dispositivo(s) (INF ausente ou erro do SetupVerifyInfFile).");
        result.Sort((a, b) => string.Compare(a.ClassName ?? "~", b.ClassName ?? "~", StringComparison.OrdinalIgnoreCase) is var c && c != 0
            ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static DeviceRecord ReadDevice(PropertyReader r, ref SP_DEVINFO_DATA data, string instanceId, bool resolveSignature, out bool sigFailed)
    {
        sigFailed = false;
        string? desc = r.String(ref data, ref NativeMethods.DeviceDesc);
        string? friendly = r.String(ref data, ref NativeMethods.FriendlyName);
        string? className = r.String(ref data, ref NativeMethods.Class);
        Guid? classGuid = r.Guid(ref data, ref NativeMethods.ClassGuid);
        bool? presentProp = r.Bool(ref data, ref NativeMethods.IsPresent);

        uint cr = CM_Get_DevNode_Status(out uint status, out uint problem, data.DevInst, 0);
        bool present = cr == CR_SUCCESS ? (presentProp ?? true) : false;
        int problemCode;
        uint? statusFlags = null;
        if (cr == CR_SUCCESS)
        {
            statusFlags = status;
            problemCode = (status & 0x400) != 0 ? (int)problem : 0; // DN_HAS_PROBLEM
        }
        else
        {
            problemCode = r.UInt32(ref data, ref NativeMethods.ProblemCode) is { } pc ? (int)pc : 0;
        }

        InstalledDriver? driver = null;
        string? infName = r.String(ref data, ref NativeMethods.DriverInfPath);
        if (!string.IsNullOrWhiteSpace(infName))
        {
            string infPath = DriverStoreRules.InstalledInfPath(infName) ?? Path.Combine(DriverStoreRules.InfDirectory, Path.GetFileName(infName));
            string? ver = r.String(ref data, ref NativeMethods.DriverVersion);
            DriverSignature sig = DriverSignature.NotChecked;
            if (resolveSignature)
            {
                sig = VerifyInfSignature(infPath);
                sigFailed = sig.State == DriverSignatureState.Unavailable;
            }
            driver = new InstalledDriver(
                infName, infPath, ver, DriverVersion.ParseOrNull(ver),
                r.FileTime(ref data, ref NativeMethods.DriverDate)?.Date,
                r.String(ref data, ref NativeMethods.DriverProvider),
                r.String(ref data, ref NativeMethods.DriverDesc),
                r.String(ref data, ref NativeMethods.DriverInfSection),
                r.String(ref data, ref NativeMethods.MatchingDeviceId),
                r.UInt32(ref data, ref NativeMethods.DriverRank),
                sig);
        }

        return new DeviceRecord(
            instanceId,
            friendly ?? desc ?? instanceId,
            desc,
            className,
            classGuid,
            r.String(ref data, ref NativeMethods.Manufacturer),
            r.StringList(ref data, ref NativeMethods.HardwareIds),
            r.StringList(ref data, ref NativeMethods.CompatibleIds),
            r.String(ref data, ref NativeMethods.Service),
            r.String(ref data, ref NativeMethods.EnumeratorName),
            r.String(ref data, ref NativeMethods.LocationInfo),
            present,
            statusFlags,
            problemCode,
            ProblemCodes.Describe(problemCode),
            driver,
            r.FileTime(ref data, ref NativeMethods.LastArrivalDate)?.ToLocalTime());
    }

    /// <summary>
    /// Verifica a assinatura de um INF instalado via SetupVerifyInfFile (catálogo do sistema; sem rede).
    /// Resultado em cache por caminho + data de modificação.
    /// </summary>
    public static DriverSignature VerifyInfSignature(string infPath)
    {
        try
        {
            if (!File.Exists(infPath))
                return new DriverSignature(DriverSignatureState.Unavailable, null, null, null, "Arquivo INF não encontrado.");
            var stamp = File.GetLastWriteTimeUtc(infPath);
            if (SignatureCache.TryGetValue(infPath, out var c) && c.Stamp == stamp) return c.Sig;

            var info = new SP_INF_SIGNER_INFO_V2 { cbSize = (uint)Marshal.SizeOf<SP_INF_SIGNER_INFO_V2>() };
            bool ok = SetupVerifyInfFileW(infPath, IntPtr.Zero, ref info);
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            string? signer = string.IsNullOrWhiteSpace(info.DigitalSigner) ? null : info.DigitalSigner;
            string? cat = string.IsNullOrWhiteSpace(info.CatalogFile) ? null : info.CatalogFile;
            uint? score = info.SignerScore == 0 ? null : info.SignerScore;
            DriverSignature sig = ok
                ? new DriverSignature(DriverSignatureState.Signed, signer, cat, score, null)
                : MapVerifyError(err, signer, cat, score);
            SignatureCache[infPath] = (stamp, sig);
            return sig;
        }
        catch (Exception ex)
        {
            return new DriverSignature(DriverSignatureState.Unavailable, null, null, null, ex.Message);
        }
    }

    private static DriverSignature MapVerifyError(int err, string? signer, string? cat, uint? score)
    {
        uint u = unchecked((uint)err);
        return u switch
        {
            0x800B0100 => new(DriverSignatureState.Unsigned, null, cat, score, "Sem assinatura (TRUST_E_NOSIGNATURE)."),
            0xE000022F => new(DriverSignatureState.Unsigned, null, cat, score, "INF de terceiro sem catálogo (ERROR_NO_CATALOG_FOR_OEM_INF)."),
            0x800B0109 => new(DriverSignatureState.Invalid, signer, cat, score, "Raiz não confiável (CERT_E_UNTRUSTEDROOT)."),
            0x800B0101 => new(DriverSignatureState.Invalid, signer, cat, score, "Certificado expirado (CERT_E_EXPIRED)."),
            0x80096010 => new(DriverSignatureState.Invalid, signer, cat, score, "Hash não confere (TRUST_E_BAD_DIGEST)."),
            0xE0000242 => new(DriverSignatureState.Invalid, signer, cat, score, "Hash do arquivo não está no catálogo (ERROR_FILE_HASH_NOT_IN_CATALOG)."),
            2 or 3 => new(DriverSignatureState.Unavailable, null, null, null, $"INF inacessível (Win32 {err})."),
            _ => new(DriverSignatureState.Invalid, signer, cat, score, $"Verificação falhou (0x{u:X8})."),
        };
    }

    /// <summary>Leitor de DEVPKEYs com buffer reutilizado (um por enumeração; não é thread-safe).</summary>
    private sealed class PropertyReader(IntPtr set)
    {
        private byte[] _buf = new byte[4096];

        private bool Get(ref SP_DEVINFO_DATA d, ref DEVPROPKEY key, out uint type, out int size)
        {
            while (true)
            {
                if (SetupDiGetDevicePropertyW(set, ref d, ref key, out type, _buf, (uint)_buf.Length, out uint req, 0))
                {
                    size = (int)req;
                    return true;
                }
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_INSUFFICIENT_BUFFER && req > _buf.Length && req < 1 << 20) { _buf = new byte[req]; continue; }
                size = 0;
                return false;
            }
        }

        public string? String(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k)
        {
            if (!Get(ref d, ref k, out uint t, out int n) || t != DEVPROP_TYPE_STRING || n < 2) return null;
            var s = Encoding.Unicode.GetString(_buf, 0, n).TrimEnd('\0').Trim();
            return s.Length == 0 ? null : s;
        }

        public IReadOnlyList<string> StringList(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k)
        {
            if (!Get(ref d, ref k, out uint t, out int n) || t != DEVPROP_TYPE_STRING_LIST || n < 2) return Array.Empty<string>();
            return Encoding.Unicode.GetString(_buf, 0, n).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public uint? UInt32(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k) =>
            Get(ref d, ref k, out uint t, out int n) && t == DEVPROP_TYPE_UINT32 && n >= 4 ? BitConverter.ToUInt32(_buf, 0) : null;

        public bool? Bool(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k) =>
            Get(ref d, ref k, out uint t, out int n) && t == DEVPROP_TYPE_BOOLEAN && n >= 1 ? _buf[0] != 0 : null;

        public Guid? Guid(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k) =>
            Get(ref d, ref k, out uint t, out int n) && t == DEVPROP_TYPE_GUID && n >= 16 ? new System.Guid(_buf.AsSpan(0, 16)) : null;

        public DateTime? FileTime(ref SP_DEVINFO_DATA d, ref DEVPROPKEY k)
        {
            if (!Get(ref d, ref k, out uint t, out int n) || t != DEVPROP_TYPE_FILETIME || n < 8) return null;
            long ft = BitConverter.ToInt64(_buf, 0);
            if (ft <= 0) return null;
            try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    // ------------------------------------------------------------------ WMI (fallback)

    private static List<DeviceRecord> CollectWmi(InventoryOptions options, List<string> warnings, CancellationToken ct)
    {
        var drivers = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2",
                "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName, Signer, IsSigned, Description FROM Win32_PnPSignedDriver");
            foreach (var o in s.Get())
                if (o["DeviceID"] is string id) drivers[id] = o;
        }
        catch (Exception ex) { warnings.Add($"Win32_PnPSignedDriver indisponível: {ex.Message}"); }

        var list = new List<DeviceRecord>();
        using var searcher = new ManagementObjectSearcher(@"root\cimv2",
            "SELECT PNPDeviceID, Name, Description, PNPClass, ClassGuid, Manufacturer, HardwareID, CompatibleID, Service, Present, ConfigManagerErrorCode FROM Win32_PnPEntity");
        foreach (var o in searcher.Get())
        {
            ct.ThrowIfCancellationRequested();
            if (o["PNPDeviceID"] is not string id) continue;
            bool present = o["Present"] is bool b ? b : true;
            if (!present && !options.IncludeNotPresent) continue;
            int problem = o["ConfigManagerErrorCode"] is uint u ? (int)u : 0;
            InstalledDriver? drv = null;
            if (drivers.TryGetValue(id, out var d) && d["InfName"] is string inf && inf.Length > 0)
            {
                string? ver = d["DriverVersion"] as string;
                DateTime? date = null;
                if (d["DriverDate"] is string dd && dd.Length >= 8)
                    try { date = ManagementDateTimeConverter.ToDateTime(dd).Date; } catch { }
                bool? signed = d["IsSigned"] as bool?;
                var sig = signed switch
                {
                    true => new DriverSignature(DriverSignatureState.Signed, d["Signer"] as string, null, null, "Fonte: WMI"),
                    false => new DriverSignature(DriverSignatureState.Unsigned, null, null, null, "Fonte: WMI"),
                    _ => new DriverSignature(DriverSignatureState.Unavailable, null, null, null, "WMI não informou a assinatura."),
                };
                drv = new InstalledDriver(inf, DriverStoreRules.InstalledInfPath(inf) ?? inf, ver, DriverVersion.ParseOrNull(ver), date,
                    d["DriverProviderName"] as string, d["Description"] as string, null, null, null, sig);
            }
            Guid? cg = o["ClassGuid"] is string g && System.Guid.TryParse(g, out var gg) ? gg : null;
            int slash = id.IndexOf('\\');
            list.Add(new DeviceRecord(id, (o["Name"] as string) ?? id, o["Description"] as string, o["PNPClass"] as string, cg,
                o["Manufacturer"] as string, (o["HardwareID"] as string[]) ?? Array.Empty<string>(), (o["CompatibleID"] as string[]) ?? Array.Empty<string>(),
                o["Service"] as string, slash > 0 ? id[..slash] : null, null, present, null, problem, ProblemCodes.Describe(problem), drv, null));
        }
        return list;
    }
}
