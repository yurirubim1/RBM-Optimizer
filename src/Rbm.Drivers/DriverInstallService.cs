using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Drivers.Native;
using static Rbm.Drivers.Native.NativeMethods;

namespace Rbm.Drivers;

public sealed record InstallValidation(
    bool Ok,
    string? InfPath,
    InfHeader? Header,
    PackageSignatureCheck? Signature,
    IReadOnlyList<string> Messages);

/// <summary>
/// Resultado de <c>pnputil /add-driver /install</c>. <c>DeviceChanges</c> vem da re-enumeração antes/depois (dados reais),
/// não da saída textual do pnputil.
/// </summary>
public sealed record DriverInstallResult(
    bool Success,
    bool RebootRequired,
    int ExitCode,
    string? PublishedName,
    IReadOnlyList<DriverChange> DeviceChanges,
    IReadOnlyList<string> Messages);

/// <summary>Nó de driver que o PnP selecionaria para o dispositivo a partir de um INF específico.</summary>
public sealed record DriverNodeInfo(string Description, string Manufacturer, string Provider, DriverVersion Version, DateTime? Date);

/// <summary>
/// Versão alternativa (normalmente anterior) do driver, mantida no Driver Store e compatível com o dispositivo.
/// <c>CompareToCurrent</c>: &lt;0 candidato mais antigo que o atual; &gt;0 mais novo; 0 equivalente.
/// </summary>
public sealed record RollbackCandidate(
    string InstanceId,
    string DeviceName,
    InstalledDriver? Current,
    DriverPackage Package,
    DriverNodeInfo Node,
    int CompareToCurrent);

public sealed record SwitchPreview(bool Possible, DriverNodeInfo? Node, string MessagePt);

public sealed record DriverSwitchResult(
    bool Success,
    bool RebootRequired,
    InstalledDriver? Before,
    InstalledDriver? After,
    IReadOnlyList<string> Messages);

/// <summary>
/// Instalação, verificação pós-instalação e troca/reversão de versão de drivers.
/// <para>Métodos marcados "Requer elevação — executado via RBM.Agent" validam TODAS as entradas
/// (podem receber payloads originados na UI). Nada aqui remove drivers.</para>
/// </summary>
public sealed partial class DriverInstallService
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);
    private readonly DriverStoreService _store = new();
    private readonly DeviceInventoryService _inventory = new();

    /// <summary>
    /// Pré-validação (não elevada) de um INF antes da instalação: caminho local seguro, extensão .inf, cabeçalho legível,
    /// catálogo declarado e presente, catálogo com assinatura Authenticode confiável e INF presente no catálogo.
    /// </summary>
    public static InstallValidation ValidateForInstall(string? infPath)
    {
        var msgs = new List<string>();
        var path = DriverInputValidator.ValidateInstallInf(infPath);
        if (!path.Success) return new(false, null, null, null, [path.Message]);
        InfHeader header;
        try { header = InfFile.ReadHeader(path.Value!); }
        catch (Exception ex) { return new(false, path.Value, null, null, [$"INF ilegível: {ex.Message}"]); }

        var sig = CatalogVerifier.VerifyPackage(path.Value!);
        msgs.Add(sig.MessagePt);
        if (header.ParsedVersion is null) msgs.Add("Aviso: DriverVer ausente ou ilegível no INF.");
        if (header.Class is null) msgs.Add("Aviso: classe do dispositivo não declarada no INF.");
        return new(sig.IsTrusted, path.Value, header, sig, msgs);
    }

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (AgentOps.DriverInstall).
    /// Executa <c>pnputil /add-driver &lt;inf&gt; /install</c> após <see cref="ValidateForInstall"/>; depois re-enumera os
    /// dispositivos e compara com o estado anterior para mostrar quais drivers mudaram (verificação real).
    /// </summary>
    public async Task<DriverInstallResult> InstallAsync(string? infPath, CancellationToken ct = default)
    {
        var v = ValidateForInstall(infPath);
        if (!v.Ok) return new(false, false, -1, null, [], v.Messages);
        var msgs = new List<string>(v.Messages);

        var opts = new InventoryOptions { ResolveSignatures = false };
        var before = DriverSnapshot.FromInventory(await _inventory.CollectAsync(opts, ct).ConfigureAwait(false));

        var r = await ProcessRunner.RunAsync(DriverStoreService.PnpUtilPath, ["/add-driver", v.InfPath!, "/install"], InstallTimeout, ct, ProcessRunner.OemEncoding)
            .ConfigureAwait(false);
        string? published = OemNameRegex().Matches(r.StdOut).Select(m => m.Value.ToLowerInvariant()).LastOrDefault();

        var after = DriverSnapshot.FromInventory(await _inventory.CollectAsync(opts, ct).ConfigureAwait(false));
        var changes = DriverSnapshotComparer.Diff(before, after).Changes;

        bool reboot = r.ExitCode == 3010;
        bool success;
        switch (r.ExitCode)
        {
            case 0 or 3010:
                success = true;
                msgs.Add(changes.Count > 0
                    ? $"Driver instalado. {changes.Count} dispositivo(s) com driver alterado."
                    : "Pacote adicionado ao Driver Store; nenhum dispositivo mudou de driver (o atual é igual/melhor ou não há dispositivo compatível conectado).");
                break;
            case 259: // ERROR_NO_MORE_ITEMS: pacote adicionado, sem dispositivos atualizados
                success = true;
                msgs.Add("Pacote adicionado ao Driver Store, mas nenhum dispositivo foi atualizado (driver atual igual ou com prioridade maior, ou dispositivo ausente).");
                break;
            default:
                success = false;
                msgs.Add(r.TimedOut ? "pnputil excedeu o tempo limite." : $"pnputil falhou (código {r.ExitCode}).");
                break;
        }
        if (reboot) msgs.Add("É necessário reiniciar para concluir a instalação.");
        return new DriverInstallResult(success, reboot, r.ExitCode, published, changes, msgs);
    }

    /// <summary>
    /// Versões alternativas do driver de um dispositivo que estão no Driver Store (base do "Reverter driver" do Windows).
    /// Candidatos: pacotes da mesma classe; cada um é testado com a correspondência REAL do PnP (SetupDiBuildDriverInfoList
    /// com DI_ENUMSINGLEINF no INF do store). Não exige elevação.
    /// </summary>
    public Task<IReadOnlyList<RollbackCandidate>> FindAlternativeVersionsAsync(DeviceRecord device, DriverStoreInventory store, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<RollbackCandidate>>(() =>
        {
            var list = new List<RollbackCandidate>();
            if (!device.IsPresent) return list;
            var current = device.Driver;
            foreach (var p in store.Packages)
            {
                ct.ThrowIfCancellationRequested();
                if (p.IsExtension || p.StoreInfPath is null) continue;
                if (current is not null && string.Equals(p.PublishedName, current.InfName, StringComparison.OrdinalIgnoreCase)) continue;
                if (device.ClassGuid is { } cg && p.ClassGuid is { } pg && cg != pg) continue;
                var preview = PreviewSwitch(device.InstanceId, p.StoreInfPath);
                if (!preview.Possible || preview.Node is null) continue;
                int cmp = current is null ? 0 : DriverVersionMath.Compare(preview.Node.Version, preview.Node.Date, current.ParsedVersion, current.Date);
                list.Add(new RollbackCandidate(device.InstanceId, device.Name, current, p, preview.Node, cmp));
            }
            return list.OrderByDescending(c => c.Node.Version).ThenByDescending(c => c.Node.Date).ToList();
        }, ct);

    /// <summary>
    /// Verifica (sem elevação) se o INF do Driver Store tem um nó de driver compatível com o dispositivo, usando a correspondência
    /// do próprio PnP. Não altera nada.
    /// </summary>
    public static SwitchPreview PreviewSwitch(string instanceId, string storeInfPath)
    {
        var id = DriverInputValidator.ValidateInstanceId(instanceId);
        if (!id.Success) return new(false, null, id.Message);
        var inf = DriverInputValidator.ValidateStoreInf(storeInfPath);
        if (!inf.Success) return new(false, null, inf.Message);
        return WithDriverNode(id.Value!, inf.Value!, install: false, out _, out var node, out var msg)
            ? new(true, node, msg) : new(false, null, msg);
    }

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (proposta: AgentOps "driver.switch").
    /// Troca o driver de UM dispositivo para um pacote já existente no Driver Store (ex.: versão anterior).
    /// Validações: ID de instância, nome publicado oemNN.inf existente no store (o caminho NÃO vem da UI — é resolvido aqui),
    /// INF dentro do FileRepository, catálogo assinado e confiável, nó compatível pelo PnP.
    /// <para>Usa DiInstallDevice no nó escolhido em vez de <c>pnputil /add-driver oemNN.inf /install</c>: o pnputil só instala se
    /// o pacote for "melhor" pelo ranking do PnP, portanto NÃO consegue voltar para uma versão mais antiga quando a nova
    /// continua no store. DiInstallDevice aplica o nó escolhido somente a este dispositivo.</para>
    /// </summary>
    public async Task<DriverSwitchResult> SwitchToPackageAsync(string? instanceId, string? publishedName, CancellationToken ct = default)
    {
        var id = DriverInputValidator.ValidateInstanceId(instanceId);
        if (!id.Success) return new(false, false, null, null, [id.Message]);
        var name = DriverInputValidator.ValidatePublishedName(publishedName, allowAll: false);
        if (!name.Success) return new(false, false, null, null, [name.Message]);

        var store = await _store.EnumerateAsync(ct).ConfigureAwait(false);
        var pkg = store.Find(name.Value!);
        if (pkg is null) return new(false, false, null, null, [$"Pacote {name.Value} não existe no Driver Store."]);
        if (pkg.StoreInfPath is null)
            return new(false, false, null, null, ["Local do pacote no Driver Store desconhecido (pnputil sem suporte a XML neste Windows)."]);
        var inf = DriverInputValidator.ValidateStoreInf(pkg.StoreInfPath);
        if (!inf.Success) return new(false, false, null, null, [inf.Message]);
        var sig = CatalogVerifier.VerifyPackage(inf.Value!);
        if (!sig.IsTrusted) return new(false, false, null, null, [sig.MessagePt]);

        var beforeDev = _inventory.CollectOne(id.Value!, resolveSignature: false);
        if (beforeDev is null || !beforeDev.IsPresent) return new(false, false, null, null, ["Dispositivo não encontrado ou não conectado."]);

        var (ok, rb, msg) = await Task.Run(() =>
        {
            bool s = WithDriverNode(id.Value!, inf.Value!, install: true, out bool reboot, out _, out string m);
            return (s, reboot, m);
        }, ct).ConfigureAwait(false);
        var afterDev = _inventory.CollectOne(id.Value!, resolveSignature: true);
        var msgs = new List<string> { msg };
        if (ok && afterDev?.Driver is { } a)
            msgs.Add($"Driver atual do dispositivo: {a.InfName} v{a.Version} ({a.Date:dd/MM/yyyy}).");
        return new DriverSwitchResult(ok, ok && rb, beforeDev.Driver, afterDev?.Driver, msgs);
    }

    /// <summary>Abre o dispositivo, monta a lista de drivers somente do INF indicado e (opcionalmente) instala o melhor nó.</summary>
    private static bool WithDriverNode(string instanceId, string infPath, bool install, out bool reboot, out DriverNodeInfo? node, out string message)
    {
        reboot = false; node = null;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == InvalidHandle) { message = "SetupDiCreateDeviceInfoList falhou."; return false; }
        var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
        try
        {
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
            { message = $"Dispositivo não encontrado (Win32 {Marshal.GetLastWin32Error()})."; return false; }

            var p = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DEVINSTALL_PARAMS>(), DriverPath = "" };
            if (!SetupDiGetDeviceInstallParamsW(set, ref data, ref p))
            { message = $"SetupDiGetDeviceInstallParams falhou (Win32 {Marshal.GetLastWin32Error()})."; return false; }
            p.Flags |= DI_ENUMSINGLEINF;
            p.DriverPath = infPath;
            if (!SetupDiSetDeviceInstallParamsW(set, ref data, ref p))
            { message = $"SetupDiSetDeviceInstallParams falhou (Win32 {Marshal.GetLastWin32Error()})."; return false; }
            if (!SetupDiBuildDriverInfoList(set, ref data, SPDIT_COMPATDRIVER))
            { message = $"Não foi possível ler os drivers do INF (Win32 {Marshal.GetLastWin32Error()})."; return false; }
            try
            {
                var best = default(SP_DRVINFO_DATA_V2);
                bool found = false;
                for (uint i = 0; i < 4096; i++)
                {
                    var d = new SP_DRVINFO_DATA_V2 { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA_V2>() };
                    if (!SetupDiEnumDriverInfoW(set, ref data, SPDIT_COMPATDRIVER, i, ref d)) break;
                    // O PnP ordena por rank; o primeiro nó é o de melhor correspondência dentro deste INF.
                    if (!found) { best = d; found = true; }
                }
                if (!found) { message = "O pacote não contém driver compatível com este dispositivo."; return false; }
                DateTime? date = best.DriverDate > 0 ? DateTime.FromFileTimeUtc(best.DriverDate).Date : null;
                node = new DriverNodeInfo(best.Description ?? "", best.MfgName ?? "", best.ProviderName ?? "", DriverVersion.FromPacked(best.DriverVersion), date);
                if (!install) { message = $"Compatível: {node.Description} v{node.Version} ({node.Date:dd/MM/yyyy})."; return true; }

                if (!DiInstallDevice(IntPtr.Zero, set, ref data, ref best, DIIDFLAG_NOFINISHINSTALLUI, out reboot))
                {
                    int err = Marshal.GetLastWin32Error();
                    message = err == 5 ? "Acesso negado: a troca de driver requer elevação (RBM.Agent)." : $"DiInstallDevice falhou (Win32 {err}).";
                    return false;
                }
                message = $"Driver trocado para {node.Description} v{node.Version}." + (reboot ? " Reinicie para concluir." : "");
                return true;
            }
            finally { SetupDiDestroyDriverInfoList(set, ref data, SPDIT_COMPATDRIVER); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [GeneratedRegex(@"\boem\d{1,5}\.inf\b", RegexOptions.IgnoreCase)]
    private static partial Regex OemNameRegex();
}
