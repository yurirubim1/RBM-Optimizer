using System.Runtime.InteropServices;
using System.Text;
using Rbm.Core;
using Rbm.Network.Native;

namespace Rbm.Network.Adapters;

/// <summary>Resultado da consulta Wi-Fi: dicionário por GUID "{GUID}" maiúsculo e erro global (se a consulta toda falhou).</summary>
public sealed record WifiQueryResult(IReadOnlyDictionary<string, WifiDetails> ByInterfaceGuid, string? Error);

/// <summary>
/// Lê dados da conexão Wi-Fi pela Native Wifi API (preferida) com fallback para <c>netsh wlan show interfaces</c>.
/// Não requer elevação. No Windows 11 24H2+ o acesso a SSID/BSSID exige a permissão de Localização
/// para aplicativos de área de trabalho; sem ela o motivo é retornado explicitamente.
/// </summary>
public static class WifiInspector
{
    public const string LocationHint =
        "O Windows negou acesso aos dados de Wi-Fi. No Windows 11 24H2 ou superior, ative " +
        "Configurações > Privacidade e segurança > Localização > \"Permitir que aplicativos da área de trabalho acessem sua localização\".";

    public static async Task<WifiQueryResult> QueryAllAsync(CancellationToken ct = default)
    {
        var api = await Task.Run(QueryViaApi, ct).ConfigureAwait(false);
        if (api.Error is null) return api;

        try
        {
            var r = await ProcessRunner.RunAsync(ProcessRunner.System32("netsh.exe"), new[] { "wlan", "show", "interfaces" },
                TimeSpan.FromSeconds(10), ct, ProcessRunner.OemEncoding).ConfigureAwait(false);
            if (r.Success)
            {
                var parsed = NetshWlanParser.Parse(r.StdOut).Where(p => p.Guid is not null).ToList();
                if (parsed.Count > 0)
                    return new WifiQueryResult(parsed
                        .GroupBy(p => NormalizeGuid(p.Guid!), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Details, StringComparer.OrdinalIgnoreCase), null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* mantém o erro da API */ }

        return api;
    }

    internal static string NormalizeGuid(string g) => Guid.TryParse(g, out var x) ? x.ToString("B").ToUpperInvariant() : g.ToUpperInvariant();

    private static WifiQueryResult QueryViaApi()
    {
        var results = new Dictionary<string, WifiDetails>(StringComparer.OrdinalIgnoreCase);
        IntPtr handle = IntPtr.Zero;
        try
        {
            uint rc = WlanNative.WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (rc == WlanNative.ErrorServiceNotActive)
                return new(results, "Serviço WlanSvc não está em execução: não há Wi-Fi ativo.");
            if (rc != WlanNative.ErrorSuccess)
                return new(results, $"WlanOpenHandle falhou (código {rc}).");

            rc = WlanNative.WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list);
            if (rc != WlanNative.ErrorSuccess) return new(results, $"WlanEnumInterfaces falhou (código {rc}).");
            try
            {
                int count = Marshal.ReadInt32(list, 0);
                int size = Marshal.SizeOf<WlanNative.WLAN_INTERFACE_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanNative.WLAN_INTERFACE_INFO>(list + 8 + i * size);
                    results[info.InterfaceGuid.ToString("B").ToUpperInvariant()] = QueryInterface(handle, info);
                }
            }
            finally { WlanNative.WlanFreeMemory(list); }
            return new(results, null);
        }
        catch (DllNotFoundException)
        {
            return new(results, "wlanapi.dll não disponível (componente de Wi-Fi ausente nesta edição do Windows).");
        }
        catch (Exception ex)
        {
            return new(results, $"Falha ao consultar a API de Wi-Fi: {ex.Message}");
        }
        finally
        {
            if (handle != IntPtr.Zero) WlanNative.WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static WifiDetails QueryInterface(IntPtr handle, WlanNative.WLAN_INTERFACE_INFO info)
    {
        var guid = info.InterfaceGuid;
        string state = WifiFormat.InterfaceStateName(info.isState);
        if (info.isState != 1)
            return WifiDetails.Unavailable($"Interface Wi-Fi não conectada ({state}).", "wlanapi") with { State = state };

        uint rc = WlanNative.WlanQueryInterface(handle, ref guid, WlanNative.OpcodeCurrentConnection, IntPtr.Zero, out _, out IntPtr data, IntPtr.Zero);
        if (rc == WlanNative.ErrorAccessDenied)
        {
            int? rssiOnly = QueryInt(handle, guid, WlanNative.OpcodeRssi);
            int? chOnly = QueryInt(handle, guid, WlanNative.OpcodeChannelNumber);
            return WifiDetails.Unavailable(LocationHint, "wlanapi") with
            {
                State = state, RssiDbm = rssiOnly, Channel = chOnly, Band = WifiFormat.BandFromChannel(chOnly),
            };
        }
        if (rc != WlanNative.ErrorSuccess || data == IntPtr.Zero)
            return WifiDetails.Unavailable($"WlanQueryInterface falhou (código {rc}).", "wlanapi") with { State = state };

        WlanNative.WLAN_CONNECTION_ATTRIBUTES ca;
        try { ca = Marshal.PtrToStructure<WlanNative.WLAN_CONNECTION_ATTRIBUTES>(data); }
        finally { WlanNative.WlanFreeMemory(data); }

        var assoc = ca.wlanAssociationAttributes;
        int ssidLen = (int)Math.Min(assoc.dot11Ssid.uSSIDLength, 32u);
        string ssid = Encoding.UTF8.GetString(assoc.dot11Ssid.ucSSID, 0, ssidLen);
        string bssid = WifiFormat.FormatMac(assoc.dot11Bssid);

        int? rssi = QueryInt(handle, guid, WlanNative.OpcodeRssi);
        int? channel = QueryInt(handle, guid, WlanNative.OpcodeChannelNumber);
        double? freqMhz = QueryFrequencyMHz(handle, guid, assoc.dot11Bssid);

        string? band = WifiFormat.BandFromFrequencyMHz(freqMhz) ?? WifiFormat.BandFromChannel(channel, assoc.dot11PhyType);
        var sec = ca.wlanSecurityAttributes;
        return new WifiDetails(true, null, state, ssid, bssid, (int)assoc.wlanSignalQuality, rssi, channel, freqMhz, band,
            WifiFormat.PhyName(assoc.dot11PhyType), assoc.ulRxRate / 1000.0, assoc.ulTxRate / 1000.0,
            WifiFormat.AuthName(sec.dot11AuthAlgorithm), WifiFormat.CipherName(sec.dot11CipherAlgorithm),
            string.IsNullOrEmpty(ca.strProfileName) ? null : ca.strProfileName, "wlanapi");
    }

    private static int? QueryInt(IntPtr handle, Guid guid, int opcode)
    {
        uint rc = WlanNative.WlanQueryInterface(handle, ref guid, opcode, IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero);
        if (rc != WlanNative.ErrorSuccess || data == IntPtr.Zero) return null;
        try { return size >= 4 ? Marshal.ReadInt32(data) : null; }
        finally { WlanNative.WlanFreeMemory(data); }
    }

    /// <summary>Frequência central do BSS conectado (lista em cache do driver; não dispara varredura).</summary>
    private static double? QueryFrequencyMHz(IntPtr handle, Guid guid, byte[] bssid)
    {
        uint rc = WlanNative.WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 1, false, IntPtr.Zero, out IntPtr list);
        if (rc != WlanNative.ErrorSuccess || list == IntPtr.Zero) return null;
        try
        {
            int count = Marshal.ReadInt32(list, 4);
            int size = Marshal.SizeOf<WlanNative.WLAN_BSS_ENTRY>();
            for (int i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<WlanNative.WLAN_BSS_ENTRY>(list + 8 + i * size);
                if (e.dot11Bssid.AsSpan().SequenceEqual(bssid)) return e.ulChCenterFrequency / 1000.0;
            }
            return null;
        }
        finally { WlanNative.WlanFreeMemory(list); }
    }
}
