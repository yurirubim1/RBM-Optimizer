using System.Globalization;
using System.Management;
using System.Security.Principal;
using Rbm.Network.Native;
using Microsoft.Win32;

namespace Rbm.Network.Adapters;

/// <summary>
/// Leitura (sem elevação) e escrita (elevada) de propriedades avançadas de adaptadores de rede via CIM
/// (root\StandardCimv2, MSFT_NetAdapterAdvancedPropertySettingData) — equivalente a Get/Set-NetAdapterAdvancedProperty,
/// sem PowerShell e sem montagem de linha de comando.
/// </summary>
public static class AdvancedPropertyService
{
    private const string NetClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Lê propriedades avançadas, energia e offloads de todos os adaptadores visíveis.</summary>
    public static Task<IReadOnlyList<AdapterAdvancedSettings>> ReadAllAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadCore(null, ct), ct);

    /// <summary>Lê as configurações de um adaptador (GUID "{...}"). Null se o adaptador não existir.</summary>
    public static Task<AdapterAdvancedSettings?> ReadAsync(string interfaceGuid, CancellationToken ct = default)
    {
        if (!Guid.TryParse(interfaceGuid, out var g)) return Task.FromResult<AdapterAdvancedSettings?>(null);
        return Task.Run(() => ReadCore(g.ToString("B").ToUpperInvariant(), ct).FirstOrDefault(), ct);
    }

    private static IReadOnlyList<AdapterAdvancedSettings> ReadCore(string? onlyGuid, CancellationToken ct)
    {
        string filter = onlyGuid is null ? "" : $" WHERE InstanceID LIKE '{Wmi.Escape(onlyGuid)}::%'";
        var adv = Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData" + filter, ct);
        var props = adv.Rows.Select(MapProperty).Where(p => p is not null).Select(p => p!).ToList();

        string adapterFilter = onlyGuid is null ? "" : $" WHERE InterfaceGuid = '{Wmi.Escape(onlyGuid)}'";
        var adapters = Wmi.Query(Wmi.StandardCimv2, "SELECT Name, InterfaceGuid FROM MSFT_NetAdapter" + adapterFilter, ct);
        var rss = Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterRssSettingData", ct);
        var rsc = Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterRscSettingData", ct);
        var pm = Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterPowerManagementSettingData", ct);

        var guids = adapters.Rows.Select(r => (Guid: Norm(r.Str("InterfaceGuid")), Name: r.Str("Name") ?? ""))
            .Where(x => x.Guid is not null)
            .Concat(props.Select(p => (Guid: (string?)p.InterfaceGuid, Name: p.AdapterName)))
            .GroupBy(x => x.Guid!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Guid: g.Key, Name: g.First().Name));

        var result = new List<AdapterAdvancedSettings>();
        foreach (var (guid, name) in guids)
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            if (adv.Error is not null) warnings.Add($"Propriedades avançadas indisponíveis: {adv.Error}");
            var mine = props.Where(p => string.Equals(p.InterfaceGuid, guid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.RegistryKeyword, StringComparer.OrdinalIgnoreCase).ToList();

            var rssRow = rss.Rows.FirstOrDefault(r => string.Equals(r.Str("Name"), name, StringComparison.OrdinalIgnoreCase));
            var rscRow = rsc.Rows.FirstOrDefault(r => string.Equals(r.Str("Name"), name, StringComparison.OrdinalIgnoreCase));
            var pmRow = pm.Rows.FirstOrDefault(r => string.Equals(r.Str("Name"), name, StringComparison.OrdinalIgnoreCase));

            var power = BuildPower(guid, pmRow, pm.Error);
            var summary = BuildSummary(mine, rssRow, rscRow);
            result.Add(new AdapterAdvancedSettings(guid, name, mine, power, summary, warnings));
        }
        return result;
    }

    private static string? Norm(string? g) => g is not null && Guid.TryParse(g, out var x) ? x.ToString("B").ToUpperInvariant() : null;

    internal static AdvancedProperty? MapProperty(IReadOnlyDictionary<string, object?> r)
    {
        var instanceId = r.Str("InstanceID");
        var keyword = r.Str("RegistryKeyword");
        if (instanceId is null || keyword is null) return null;
        int sep = instanceId.IndexOf("::", StringComparison.Ordinal);
        var guid = Norm(sep > 0 ? instanceId[..sep] : null);
        if (guid is null) return null;

        var validReg = r.StrArray("ValidRegistryValues");
        var validDisp = r.StrArray("ValidDisplayValues");
        var options = validReg.Select((v, i) => new AdvancedPropertyOption(v, i < validDisp.Length ? validDisp[i] : v)).ToList();

        NumericRange? range = null;
        long? min = r.Long("NumericParameterMinValue"), max = r.Long("NumericParameterMaxValue");
        if (options.Count == 0 && min is not null && max is not null)
            range = new NumericRange(min.Value, max.Value, Math.Max(1, r.Long("NumericParameterStepValue") ?? 1), (int)(r.Long("NumericParameterBaseValue") ?? 10));

        var kind = options.Count > 0 ? AdvancedValueKind.Enumeration : range is not null ? AdvancedValueKind.Numeric : AdvancedValueKind.FreeText;
        return new AdvancedProperty
        {
            InterfaceGuid = guid,
            AdapterName = r.Str("Name") ?? "",
            InstanceId = instanceId,
            RegistryKeyword = keyword,
            DisplayName = r.Str("DisplayName") ?? keyword,
            DisplayValue = r.Str("DisplayValue"),
            RegistryValue = r.StrArray("RegistryValue"),
            DefaultRegistryValue = r.Str("DefaultRegistryValue"),
            ValueKind = kind,
            ValidOptions = options,
            Range = range,
            Known = AdvancedKeywords.Lookup(keyword),
        };
    }

    private static string? Describe(AdvancedProperty? p) => p is null ? null : $"{p.DisplayValue ?? p.CurrentRegistryValue} [{p.CurrentRegistryValue}]";

    private static OffloadSummary BuildSummary(List<AdvancedProperty> p, IReadOnlyDictionary<string, object?>? rss, IReadOnlyDictionary<string, object?>? rsc)
    {
        AdvancedProperty? F(string k) => p.FirstOrDefault(x => string.Equals(x.RegistryKeyword, k, StringComparison.OrdinalIgnoreCase));
        bool? On(AdvancedProperty? x) => x?.CurrentRegistryValue is { } v ? v != "0" : null;

        bool? rssEnabled = rss?.Bool("Enabled") ?? On(F(AdvancedKeywords.Rss));
        int? queues = (int?)rss?.Long("NumberOfReceiveQueues");
        if (queues is null && int.TryParse(F(AdvancedKeywords.NumRssQueues)?.CurrentRegistryValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int q)) queues = q;

        bool? rsc4 = rsc?.Bool("IPv4Enabled") ?? On(F(AdvancedKeywords.RscIPv4));
        bool? rsc6 = rsc?.Bool("IPv6Enabled") ?? On(F(AdvancedKeywords.RscIPv6));
        string? rscNote = rsc is null && F(AdvancedKeywords.RscIPv4) is null && F(AdvancedKeywords.RscIPv6) is null
            ? "RSC não suportado/declarado por este driver." : null;

        return new OffloadSummary(rssEnabled, queues, rsc4, rsc6, rscNote,
            Describe(F(AdvancedKeywords.InterruptModeration)), Describe(F(AdvancedKeywords.FlowControl)), Describe(F(AdvancedKeywords.Eee)),
            Describe(F("EnableGreenEthernet")), Describe(F(AdvancedKeywords.LsoV2IPv4)), Describe(F(AdvancedKeywords.LsoV2IPv6)),
            Describe(F(AdvancedKeywords.ReceiveBuffers)), Describe(F(AdvancedKeywords.TransmitBuffers)), Describe(F(AdvancedKeywords.SpeedDuplex)));
    }

    private static string? SettingName(long? v) => v switch
    {
        0 => "Não suportado",
        1 => "Desabilitado",
        2 => "Habilitado",
        3 => "Inativo",
        null => null,
        _ => v.ToString(),
    };

    private static PowerManagementInfo BuildPower(string guid, IReadOnlyDictionary<string, object?>? pm, string? pmError)
    {
        if (pm is not null)
        {
            var state = pm.Long("AllowComputerToTurnOffDevice") switch
            {
                0 => PowerSavingState.Unsupported,
                1 => PowerSavingState.Disabled,
                2 => PowerSavingState.Enabled,
                _ => PowerSavingState.Unknown,
            };
            return new PowerManagementInfo(state, "MSFT_NetAdapterPowerManagementSettingData", ReadPnPCapabilities(guid).Raw,
                SettingName(pm.Long("SelectiveSuspend")), SettingName(pm.Long("DeviceSleepOnDisconnect")), SettingName(pm.Long("WakeOnMagicPacket")), null);
        }

        // Fallback: registro da classe de rede (PnPCapabilities). Ausente/0 = padrão (o Windows pode desligar o dispositivo).
        var (found, raw) = ReadPnPCapabilities(guid);
        if (!found)
            return new PowerManagementInfo(PowerSavingState.Unknown, "indisponível", null, null, null, null,
                pmError is null ? "Dados de energia não encontrados." : $"WMI de energia indisponível ({pmError}) e chave do driver não encontrada.");
        PowerSavingState st = raw switch
        {
            null or 0 => PowerSavingState.Enabled,
            int v when (v & 0x18) == 0x18 => PowerSavingState.Disabled,
            _ => PowerSavingState.Unknown,
        };
        return new PowerManagementInfo(st, raw is null ? "Registro (PnPCapabilities ausente = padrão do Windows: permitido)" : "Registro (PnPCapabilities)",
            raw, null, null, null, pmError is null ? null : $"WMI de energia indisponível sem elevação ou não suportado: {pmError}");
    }

    private static (bool Found, int? Raw) ReadPnPCapabilities(string guid)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(NetClassKey);
            if (cls is null) return (false, null);
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("NetCfgInstanceId") is string id && string.Equals(Norm(id), guid, StringComparison.OrdinalIgnoreCase))
                        return (true, k.GetValue("PnPCapabilities") is int v ? v : null);
                }
                catch { /* subchave protegida */ }
            }
        }
        catch { }
        return (false, null);
    }

    // ------------------------------------------------------------------ escrita (elevada)

    /// <summary>
    /// Altera UMA propriedade avançada. Requer elevação — executado via RBM.Agent.
    /// Valida formato, existência, palavra-chave e o valor contra a lista/faixa declarada pelo driver;
    /// registra o valor anterior (para rollback) e confirma por releitura.
    /// ATENÇÃO: o driver reinicia o adaptador ao aplicar — a conexão cai por alguns segundos.
    /// </summary>
    public static Task<AdvancedPropertySetResponse> SetAsync(AdvancedPropertySetRequest request, CancellationToken ct = default) =>
        Task.Run(() => SetCore(request, ct), ct);

    public const string ResetWarning =
        "Ao aplicar, o driver reinicia o adaptador: a rede fica indisponível por alguns segundos (jogos/chamadas em andamento podem cair).";

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static AdvancedPropertySetResponse SetCore(AdvancedPropertySetRequest req, CancellationToken ct)
    {
        AdvancedPropertySetResponse Fail(string msg, string code, AdvancedProperty? p = null) =>
            new(false, req?.InterfaceGuid ?? "", p?.AdapterName, req?.RegistryKeyword ?? "", p?.CurrentRegistryValue, p?.DisplayValue, null, null, false, msg, code);

        var shape = AdvancedPropertyValidator.ValidateShape(req);
        if (!shape.Success) return Fail(shape.Message, shape.ErrorCode ?? "invalid_request");
        if (!IsElevated()) return Fail("Requer elevação — executado via RBM.Agent.", "not_elevated");

        string guid = Guid.Parse(req.InterfaceGuid).ToString("B").ToUpperInvariant();
        string instanceId = $"{guid}::{req.RegistryKeyword}";
        string wql = $"SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE InstanceID = '{Wmi.Escape(instanceId)}'";

        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Wmi.StandardCimv2), new ObjectQuery(wql));
            using var col = searcher.Get();
            var mo = col.Cast<ManagementObject>().FirstOrDefault(o =>
                string.Equals(o["RegistryKeyword"]?.ToString(), req.RegistryKeyword, StringComparison.OrdinalIgnoreCase));
            if (mo is null) return Fail("Propriedade não encontrada neste adaptador.", "not_found");
            using (mo)
            {
                var row = mo.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
                var prop = MapProperty(row);
                if (prop is null) return Fail("Resposta do driver inválida.", "driver_error");

                if (req.ExpectedCurrentValue is not null && !string.Equals(prop.CurrentRegistryValue, req.ExpectedCurrentValue, StringComparison.Ordinal))
                    return Fail($"O valor atual ({prop.CurrentRegistryValue ?? "vazio"}) difere do esperado ({req.ExpectedCurrentValue}); nada foi alterado.", "drift", prop);

                var valid = AdvancedPropertyValidator.ValidateValue(prop, req.NewRegistryValue);
                if (!valid.Success) return Fail(valid.Message, valid.ErrorCode ?? "value_not_allowed", prop);

                if (string.Equals(prop.CurrentRegistryValue, req.NewRegistryValue, StringComparison.Ordinal))
                    return new AdvancedPropertySetResponse(true, guid, prop.AdapterName, prop.RegistryKeyword, prop.CurrentRegistryValue, prop.DisplayValue,
                        prop.CurrentRegistryValue, prop.DisplayValue, true, "O valor já era o solicitado; nada foi alterado.", null);

                ct.ThrowIfCancellationRequested();
                mo["RegistryValue"] = new[] { req.NewRegistryValue };
                string? newDisplay = prop.ValueKind == AdvancedValueKind.Enumeration ? prop.DisplayFor(req.NewRegistryValue) : req.NewRegistryValue;
                if (newDisplay is not null) mo["DisplayValue"] = newDisplay;
                mo.Put(new PutOptions { Type = PutType.UpdateOnly });

                // Releitura para confirmar.
                var verify = Wmi.Query(Wmi.StandardCimv2, wql, CancellationToken.None).Rows.Select(MapProperty).FirstOrDefault(p => p is not null);
                bool verified = verify is not null && string.Equals(verify.CurrentRegistryValue, req.NewRegistryValue, StringComparison.Ordinal);
                return new AdvancedPropertySetResponse(true, guid, prop.AdapterName, prop.RegistryKeyword, prop.CurrentRegistryValue, prop.DisplayValue,
                    req.NewRegistryValue, verify?.DisplayValue ?? newDisplay, verified,
                    verified ? $"Alterado de \"{prop.DisplayValue}\" para \"{verify!.DisplayValue}\". {ResetWarning}"
                             : $"Alteração enviada, mas a releitura não confirmou o novo valor. {ResetWarning}", null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return Fail("Acesso negado pelo WMI. Requer elevação — executado via RBM.Agent.", "access_denied");
        }
        catch (Exception ex)
        {
            return Fail($"Falha ao aplicar: {ex.Message.Trim()}", "wmi_error");
        }
    }
}
