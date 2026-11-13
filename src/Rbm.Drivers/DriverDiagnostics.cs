using Rbm.Core;
using Rbm.Core.Diagnostics;

namespace Rbm.Drivers;

/// <summary>
/// Achados de diagnóstico derivados SOMENTE do inventário real (códigos de problema, assinatura, idade, pacotes duplicados).
/// Retorna <see cref="Finding"/> do Rbm.Core para integrar ao motor de diagnóstico.
/// </summary>
public static class DriverDiagnostics
{
    public const string Category = "Drivers";
    public const int OldDriverYears = 5;

    public static IReadOnlyList<Finding> Analyze(DeviceInventory inventory, DriverStoreInventory? store = null, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var findings = new List<Finding>();

        // 1) Dispositivos sem driver
        foreach (var d in inventory.Devices.Where(d => d.IsUnknown))
        {
            var hw = d.PrimaryHardwareId;
            findings.Add(new Finding
            {
                RuleId = "drivers.unknown-device",
                Category = Category,
                Title = $"Dispositivo sem driver: {d.Name}",
                Description = "O Windows não tem driver instalado para este dispositivo.",
                Evidence = Evidence(d, $"Fabricante pelo ID: {hw?.VendorName ?? (hw?.VendorId is { } v ? $"ID {v} (não catalogado)" : "não identificado")}"),
                ProbableCause = "Driver não disponível no Windows Update ou dispositivo que requer pacote do fabricante.",
                Confidence = Confidence.High,
                Severity = Severity.Medium,
                ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Use os IDs de hardware para buscar no Catálogo do Microsoft Update ou no site oficial do fabricante (links na página Drivers).",
                Risks = "Instale somente drivers de fontes oficiais.",
                Effort = EffortLevel.Medium,
                NavigateTo = Category,
            });
        }

        // 2) Códigos de problema (exceto desativados e "desconhecidos" já cobertos)
        foreach (var d in inventory.Devices.Where(d => d.HasProblem && !d.IsDisabled && !d.IsUnknown))
        {
            var p = d.Problem ?? ProblemCodes.Describe(d.ProblemCode)!;
            findings.Add(new Finding
            {
                RuleId = "drivers.problem-code",
                Category = Category,
                Title = $"{d.Name}: código {p.Code}",
                Description = p.DescriptionPt,
                Evidence = Evidence(d, $"Código {p.Code} ({p.Symbol})"),
                ProbableCause = p.DescriptionPt,
                Confidence = Confidence.High,
                Severity = p.Severity,
                ExpectedImpact = p.Severity >= Severity.High ? ImpactLevel.High : ImpactLevel.Unknown,
                Solution = p.SuggestedActionPt,
                Risks = "Troca de driver pode exigir reinício; faça backup (exportação) antes.",
                Restart = p.Code == ProblemCodes.NeedRestart ? RestartRequirement.Reboot : RestartRequirement.None,
                Effort = EffortLevel.Medium,
                NavigateTo = Category,
            });
        }

        // 3) Desativados (informativo, agregado)
        var disabled = inventory.Devices.Where(d => d.IsPresent && d.IsDisabled).ToArray();
        if (disabled.Length > 0)
            findings.Add(new Finding
            {
                RuleId = "drivers.disabled",
                Category = Category,
                Title = $"{disabled.Length} dispositivo(s) desativado(s)",
                Description = "Dispositivos desativados manualmente ou por software.",
                Evidence = disabled.Select(d => $"{d.Name} [{d.InstanceId}]").ToList(),
                Confidence = Confidence.High,
                Severity = Severity.Info,
                Solution = "Se não foi intencional, ative-os no Gerenciador de Dispositivos.",
                NavigateTo = Category,
            });

        // 4) Assinatura
        var badSig = inventory.Devices
            .Where(d => d.IsPresent && d.Driver?.Signature.State is DriverSignatureState.Unsigned or DriverSignatureState.Invalid)
            .GroupBy(d => d.Driver!.InfName, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var g in badSig)
        {
            var sig = g.First().Driver!.Signature;
            bool unsigned = sig.State == DriverSignatureState.Unsigned;
            findings.Add(new Finding
            {
                RuleId = "drivers.signature",
                Category = Category,
                Title = unsigned ? $"Driver não assinado: {g.Key}" : $"Assinatura inválida: {g.Key}",
                Description = unsigned
                    ? "O pacote de driver em uso não tem assinatura digital verificável."
                    : "A assinatura do pacote de driver em uso não pôde ser validada.",
                Evidence = g.Select(d => $"{d.Name} — {d.Driver!.InfName} v{d.Driver.Version} ({d.Driver.Provider})")
                    .Append($"Detalhe: {sig.Detail ?? sig.SignerKindPt}").ToList(),
                ProbableCause = "Driver instalado com a verificação de assinatura desativada, modo de teste, ou pacote alterado.",
                Confidence = Confidence.Medium,
                Severity = unsigned ? Severity.High : Severity.Medium,
                ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Substitua por um driver assinado obtido da fonte oficial. Não mantenha a assinatura de drivers desativada.",
                Risks = "Drivers não assinados podem causar instabilidade e são vetor de ataque (kernel).",
                Effort = EffortLevel.Medium,
                NavigateTo = Category,
            });
        }

        // 5) Drivers muito antigos (nota suave; só terceiros em dispositivos ativos)
        var cutoff = today.AddYears(-OldDriverYears);
        var old = inventory.Devices
            .Where(d => d.IsStarted && d.Driver is { IsThirdParty: true, Date: { } dt } && dt < cutoff)
            .OrderBy(d => d.Driver!.Date).ToArray();
        if (old.Length > 0)
            findings.Add(new Finding
            {
                RuleId = "drivers.old",
                Category = Category,
                Title = $"{old.Length} driver(s) de terceiros com mais de {OldDriverYears} anos",
                Description = "Data do driver antiga em dispositivos ativos. Isso NÃO indica problema por si só — muitos drivers simplesmente não precisam de atualização.",
                Evidence = old.Select(d => $"{d.Name} — {d.Driver!.InfName} v{d.Driver.Version} ({d.Driver.Date:dd/MM/yyyy}, {d.Driver.Provider})").ToList(),
                Confidence = Confidence.High,
                Severity = Severity.Info,
                ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Verifique no site oficial do fabricante se há versão mais nova somente se houver problema com o dispositivo. " + OfficialSources.NewestNotAlwaysBestPt,
                NavigateTo = Category,
                CorrelationOnly = true,
            });

        // 6) Pacotes duplicados no Driver Store
        if (store is not null && store.Packages.Count > 0)
        {
            var analysis = DriverStoreService.Analyze(inventory, store);
            if (analysis.Duplicates.Count > 0)
            {
                var ev = new List<string>();
                foreach (var g in analysis.Duplicates)
                    ev.Add($"{g.OriginalName}: " + string.Join("; ", g.Packages.Select(p =>
                        $"{p.PublishedName} v{p.Version} ({p.Date:dd/MM/yyyy}){(g.InUsePublishedNames.Contains(p.PublishedName, StringComparer.OrdinalIgnoreCase) ? " [em uso]" : "")}")));
                findings.Add(new Finding
                {
                    RuleId = "drivers.duplicate-packages",
                    Category = Category,
                    Title = $"{analysis.Duplicates.Count} driver(s) com várias versões no Driver Store",
                    Description = "Versões anteriores ficam no Driver Store após atualizações. Elas permitem \"Reverter driver\" e ocupam pouco espaço na maioria dos casos.",
                    Evidence = ev,
                    Confidence = Confidence.High,
                    Severity = Severity.Low,
                    ExpectedImpact = ImpactLevel.Negligible,
                    Solution = "Nenhuma ação é necessária. O RBM nunca remove pacotes automaticamente. Para liberar espaço, faça backup e remova manualmente " +
                               "apenas pacotes antigos que NÃO estejam em uso (pnputil /delete-driver oemNN.inf, sem /force).",
                    Risks = "Remover a versão anterior impede reverter o driver; remover um pacote em uso pode deixar o dispositivo sem driver.",
                    Reversible = false,
                    Effort = EffortLevel.Low,
                    NavigateTo = Category,
                });
            }
        }

        // 7) Dispositivos não presentes com drivers de terceiros (informativo)
        var ghosts = inventory.Devices.Where(d => !d.IsPresent && d.Driver is { IsThirdParty: true }).ToArray();
        if (ghosts.Length > 0)
            findings.Add(new Finding
            {
                RuleId = "drivers.not-present",
                Category = Category,
                Title = $"{ghosts.Length} dispositivo(s) não conectado(s) com driver de terceiro registrado",
                Description = "Registros de dispositivos removidos ou desconectados (ex.: periféricos USB, GPU antiga). Normalmente inofensivo.",
                Evidence = ghosts.Take(50).Select(d => $"{d.Name} — {d.Driver!.InfName} v{d.Driver.Version}" +
                    (d.LastArrival is { } la ? $" (última conexão {la:dd/MM/yyyy})" : "")).ToList(),
                Confidence = Confidence.High,
                Severity = Severity.Info,
                Solution = "Nenhuma ação necessária. Após troca de GPU de outro fabricante, considere a orientação de remoção limpa (DDU).",
                NavigateTo = Category,
            });

        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static List<string> Evidence(DeviceRecord d, params string[] extra)
    {
        var ev = new List<string> { $"Instância: {d.InstanceId}" };
        if (d.ClassName is not null) ev.Add($"Classe: {d.ClassName}");
        if (d.HardwareIds.Count > 0) ev.Add($"ID de hardware: {d.HardwareIds[0]}");
        if (d.Driver is { } drv) ev.Add($"Driver: {drv.InfName} v{drv.Version ?? "?"} ({drv.Date:dd/MM/yyyy}) — {drv.Provider}");
        ev.AddRange(extra);
        return ev;
    }
}

/// <summary>Regra para o motor de diagnóstico: coleta inventário + Driver Store e devolve <see cref="DriverDiagnostics.Analyze"/>.</summary>
public sealed class DriverDiagnosticRule : IDiagnosticRule
{
    public string Id => "drivers";
    public string Category => DriverDiagnostics.Category;

    public async Task<IEnumerable<Finding>> EvaluateAsync(DiagnosticContext context, CancellationToken ct)
    {
        var inv = context.Extra.TryGetValue(nameof(DeviceInventory), out var o) && o is DeviceInventory di
            ? di : await new DeviceInventoryService().CollectAsync(ct).ConfigureAwait(false);
        DriverStoreInventory? store = context.Extra.TryGetValue(nameof(DriverStoreInventory), out var s) && s is DriverStoreInventory ds
            ? ds : await new DriverStoreService().EnumerateAsync(ct).ConfigureAwait(false);
        return DriverDiagnostics.Analyze(inv, store);
    }
}
