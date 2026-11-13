using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Core.Tweaks;
using Rbm.Optimization.Providers;

namespace Rbm.Optimization;

/// <summary>
/// Executor transacional usado tanto pela UI (alvos HKCU) quanto pelo agente (alvos privilegiados):
/// 1) resolve alvos; 2) lê e guarda o estado anterior; 3) escreve; 4) relê e verifica;
/// 5) em qualquer falha, desfaz na ordem inversa o que já foi escrito.
/// Nada é reportado como aplicado sem verificação por releitura.
/// </summary>
public sealed class TweakExecutor
{
    private readonly IReadOnlyList<ISettingProvider> _providers;

    public TweakExecutor(IEnumerable<ISettingProvider>? providers = null)
    {
        _providers = providers?.ToList() ?? new List<ISettingProvider>
        {
            new RegistrySettingProvider(), new PowerSettingProvider(), new ServiceSettingProvider(),
            new BcdSettingProvider(), new ScheduledTaskProvider(),
        };
    }

    public ISettingProvider ProviderFor(SettingTarget t) =>
        _providers.FirstOrDefault(p => p.CanHandle(t)) ?? throw new NotSupportedException($"Sem provedor para {t.GetType().Name}");

    /// <summary>Escopo: alvos que podem ser escritos sem elevação (HKCU).</summary>
    public static bool IsUserScope(SettingTarget t) => !t.WriteRequiresAdmin;
    /// <summary>Escopo: alvos que exigem o agente elevado.</summary>
    public static bool IsElevatedScope(SettingTarget t) => t.WriteRequiresAdmin;

    /// <summary>Lê o estado efetivo atual e determina a qual opção corresponde.</summary>
    public TweakStatus ReadStatus(TweakManifest m, bool elevated)
    {
        var status = new TweakStatus { TweakId = m.Id };
        var reference = m.GetOption(m.DefaultOption) ?? m.Options[0];
        var values = new Dictionary<string, SettingValue>();
        foreach (var t in reference.Targets)
        {
            if (t.ReadRequiresAdmin && !elevated)
            {
                status.Readings.Add(new TargetReading(t.IdentityKey, t.Display, SettingValue.Absent, "Leitura requer elevação."));
                status.Kind = TweakStateKind.NeedsElevationToRead;
                continue;
            }
            try
            {
                var p = ProviderFor(t);
                var v = p.Read(p.Resolve(t));
                values[t.IdentityKey] = v;
                status.Readings.Add(new TargetReading(t.IdentityKey, t.Display, v, null));
            }
            catch (Exception ex)
            {
                status.Readings.Add(new TargetReading(t.IdentityKey, t.Display, SettingValue.Absent, ex.Message));
                if (status.Kind != TweakStateKind.NeedsElevationToRead) status.Kind = ex is UnauthorizedAccessException ? TweakStateKind.NeedsElevationToRead : TweakStateKind.Error;
                status.Message = ex.Message;
            }
        }
        if (status.Kind is TweakStateKind.NeedsElevationToRead or TweakStateKind.Error) return status;

        // Serviço/tarefa inexistente nesta instalação (edição/build diferente): não aplicável.
        var missing = reference.Targets.FirstOrDefault(t => t is ServiceTarget or ScheduledTaskTarget && values.TryGetValue(t.IdentityKey, out var v) && !v.Exists);
        if (missing is not null)
        {
            status.Kind = TweakStateKind.NotApplicable;
            status.Message = $"Componente ausente nesta instalação: {missing.Display}.";
            return status;
        }

        // Prioriza a opção padrão ao desempatar (ex.: valores equivalentes).
        foreach (var o in m.Options.OrderByDescending(o => o.Key == m.DefaultOption))
        {
            if (o.Targets.All(t => values.TryGetValue(t.IdentityKey, out var v) && t.Matches(v)))
            {
                status.Kind = TweakStateKind.MatchesOption;
                status.CurrentOption = o.Key;
                return status;
            }
        }
        status.Kind = TweakStateKind.Custom;
        status.Message = "O estado atual não corresponde a nenhuma opção conhecida (alterado manualmente ou por outro programa).";
        return status;
    }

    public TweakApplyResponse Apply(TweakManifest m, string optionKey, Func<SettingTarget, bool> scope, bool elevatedContext)
    {
        var option = m.GetOption(optionKey) ?? throw new ArgumentException($"Opção '{optionKey}' não existe em {m.Id}.");
        var changes = new List<TargetChange>();
        var targets = option.Targets.Where(scope).ToList();
        try
        {
            // Fase 1: snapshot de tudo antes de escrever qualquer coisa.
            var plan = new List<(ISettingProvider p, SettingTarget resolved, SettingTarget original, SettingValue before)>();
            foreach (var t in targets)
            {
                var p = ProviderFor(t);
                var resolved = p.Resolve(t);
                plan.Add((p, resolved, t, p.Read(resolved)));
            }
            // Fase 2: escrita + verificação.
            foreach (var (p, resolved, original, before) in plan)
            {
                var change = new TargetChange { Target = resolved, Before = before, After = original.Desired, Elevated = elevatedContext };
                changes.Add(change);
                p.Write(resolved, MergeDesired(original, before));
                var after = p.Read(resolved);
                change.After = after;
                change.Verified = original.Matches(after);
                if (!change.Verified)
                    throw new InvalidOperationException($"Verificação falhou em {resolved.Display}: esperado {original.Desired}, lido {after}.");
            }
            return new TweakApplyResponse(changes, true, false, null);
        }
        catch (Exception ex)
        {
            var rollbackErrors = Rollback(changes);
            return new TweakApplyResponse(changes, false, rollbackErrors.Count == 0,
                ex.Message + (rollbackErrors.Count > 0 ? " | Falha no rollback: " + string.Join("; ", rollbackErrors) : " | Alterações parciais desfeitas."));
        }
    }

    /// <summary>Para energia com AC/DC parcial, preserva a metade não especificada.</summary>
    private static SettingValue MergeDesired(SettingTarget t, SettingValue before)
    {
        if (t is PowerSettingTarget p && before.Exists && before.Data is not null)
        {
            var (bac, bdc) = PowerValue.Parse(before.Data);
            return new SettingValue(true, "power", PowerValue.Format(p.Ac ?? bac, p.Dc ?? bdc));
        }
        return t.Desired;
    }

    private List<string> Rollback(List<TargetChange> changes)
    {
        var errors = new List<string>();
        for (int i = changes.Count - 1; i >= 0; i--)
        {
            var c = changes[i];
            try
            {
                var p = ProviderFor(c.Target);
                // Só restaura o que de fato mudou (ex.: a escrita que falhou pode não ter alterado nada).
                if (p.Read(c.Target).SemanticEquals(c.Before)) continue;
                p.Write(c.Target, c.Before);
                if (!p.Read(c.Target).SemanticEquals(c.Before)) errors.Add($"{c.Target.Display}: releitura após rollback diverge do estado original");
            }
            catch (Exception ex) { errors.Add($"{c.Target.Display}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>
    /// Reverte alterações para os valores "Before". Detecta divergência (valor atual ≠ valor escrito
    /// pelo RBM) e, sem <paramref name="force"/>, não sobrescreve alterações externas.
    /// </summary>
    public TweakRevertResponse Revert(IEnumerable<TargetChange> changes, bool force)
    {
        var list = changes.ToList();
        var drift = new List<DriftReport>();
        foreach (var c in list)
        {
            try
            {
                var current = ProviderFor(c.Target).Read(c.Target);
                if (!current.SemanticEquals(c.After) && !current.SemanticEquals(c.Before))
                    drift.Add(new DriftReport(c.Target.IdentityKey, c.Target.Display, c.After, current));
            }
            catch (Exception ex) { return new TweakRevertResponse(false, drift, ex.Message); }
        }
        if (drift.Count > 0 && !force)
            return new TweakRevertResponse(false, drift, "Configuração alterada externamente após a aplicação. Confirme para sobrescrever.");

        var errors = new List<string>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var c = list[i];
            try
            {
                var p = ProviderFor(c.Target);
                p.Write(c.Target, c.Before);
                var after = p.Read(c.Target);
                if (!after.SemanticEquals(c.Before)) errors.Add($"{c.Target.Display}: releitura {after} ≠ {c.Before}");
            }
            catch (Exception ex) { errors.Add($"{c.Target.Display}: {ex.Message}"); }
        }
        return new TweakRevertResponse(errors.Count == 0, drift, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    /// <summary>Compara o estado atual com o valor gravado pelo RBM (divergências externas).</summary>
    public List<DriftReport> DetectDrift(IEnumerable<TargetChange> changes, bool elevated)
    {
        var list = new List<DriftReport>();
        foreach (var c in changes)
        {
            if (c.Target.ReadRequiresAdmin && !elevated) continue;
            try
            {
                var cur = ProviderFor(c.Target).Read(c.Target);
                if (!cur.SemanticEquals(c.After)) list.Add(new DriftReport(c.Target.IdentityKey, c.Target.Display, c.After, cur));
            }
            catch { /* leitura indisponível: sem conclusão */ }
        }
        return list;
    }
}

/// <summary>Avalia compatibilidade de um tweak com o hardware/sistema detectado.</summary>
public static class CompatibilityEvaluator
{
    public static (bool Compatible, string Reason) Evaluate(TweakManifest m, SystemProfile? p)
    {
        if (p is null) return (true, "Perfil do sistema ainda não coletado.");
        var c = m.Compatibility;
        if (c.MinBuild > 0 && p.Os.Build < c.MinBuild) return (false, $"Requer build {c.MinBuild}+ (atual {p.Os.Build}).");
        if (c.MaxBuild > 0 && p.Os.Build > c.MaxBuild) return (false, $"Obsoleto após a build {c.MaxBuild} (atual {p.Os.Build}).");
        if (c.CpuVendor is { } cv && cv != CpuVendor.Unknown && p.Cpu.Vendor != cv) return (false, $"Aplica-se somente a CPUs {cv}.");
        if (c.GpuVendor is { } gv && gv != GpuVendor.Unknown && !p.Gpus.Any(g => g.Vendor == gv)) return (false, $"Aplica-se somente a GPUs {gv}.");
        if (c.FormFactor is { } ff && ff != FormFactor.Unknown && p.FormFactor != FormFactor.Unknown && p.FormFactor != ff) return (false, ff == FormFactor.Laptop ? "Aplica-se a notebooks." : "Aplica-se a desktops.");
        if (c.RequiresHybridCpu && !p.Cpu.IsHybrid) return (false, "Requer CPU híbrida (P-cores/E-cores).");
        if (c.RequiresBattery && !p.Power.HasBattery) return (false, "Requer bateria.");
        return (true, "Compatível.");
    }
}
