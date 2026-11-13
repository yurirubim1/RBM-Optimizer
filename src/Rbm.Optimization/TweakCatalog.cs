using System.Reflection;
using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Tweaks;
using Rbm.Optimization.Providers;
using Rbm.Security;

namespace Rbm.Optimization;

/// <summary>
/// Catálogo de tweaks compilado no assembly (recursos embutidos Catalog/*.json).
/// Por ser compilado, é a mesma fonte de verdade para a UI e para o agente elevado:
/// o agente só executa tweaks (e alvos) que constem aqui.
/// </summary>
public sealed class TweakCatalog
{
    private readonly Dictionary<string, TweakManifest> _byId = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<TweakManifest> Tweaks { get; }
    public IReadOnlyList<TweakProfile> Profiles { get; }
    /// <summary>Erros de validação (tweaks inválidos são excluídos do catálogo).</summary>
    public IReadOnlyList<string> ValidationErrors { get; }

    private TweakCatalog(List<TweakManifest> tweaks, List<TweakProfile> profiles, List<string> errors)
    {
        foreach (var t in tweaks) _byId[t.Id] = t;
        Tweaks = tweaks;
        Profiles = profiles;
        ValidationErrors = errors;
    }

    public TweakManifest? Get(string id) => _byId.GetValueOrDefault(id);

    private static TweakCatalog? _embedded;
    private static readonly object Gate = new();

    /// <summary>Catálogo embutido (singleton, carregado uma vez).</summary>
    public static TweakCatalog LoadEmbedded()
    {
        lock (Gate)
        {
            if (_embedded is not null) return _embedded;
            var asm = typeof(TweakCatalog).Assembly;
            var docs = new List<(string name, string json)>();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.Contains(".Catalog.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                using var s = asm.GetManifestResourceStream(res)!;
                using var rd = new StreamReader(s);
                docs.Add((res, rd.ReadToEnd()));
            }
            _embedded = Load(docs);
            return _embedded;
        }
    }

    public static TweakCatalog Load(IEnumerable<(string name, string json)> documents)
    {
        var tweaks = new List<TweakManifest>();
        var profiles = new List<TweakProfile>();
        var errors = new List<string>();
        foreach (var (name, json) in documents)
        {
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("tweaks", out var arr))
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        try
                        {
                            var m = el.Deserialize<TweakManifest>(AgentJson.Options);
                            if (m is null) continue;
                            var errs = CatalogValidator.Validate(m);
                            if (errs.Count > 0) { errors.AddRange(errs.Select(e => $"{name}: {m.Id}: {e}")); continue; }
                            if (tweaks.Any(t => string.Equals(t.Id, m.Id, StringComparison.OrdinalIgnoreCase))) { errors.Add($"{name}: ID duplicado {m.Id}"); continue; }
                            tweaks.Add(m);
                        }
                        catch (Exception ex) { errors.Add($"{name}: item inválido: {ex.Message}"); }
                    }
                }
                if (doc.RootElement.TryGetProperty("profiles", out var parr))
                    foreach (var el in parr.EnumerateArray())
                    {
                        try { if (el.Deserialize<TweakProfile>(AgentJson.Options) is { } p) profiles.Add(p); }
                        catch (Exception ex) { errors.Add($"{name}: perfil inválido: {ex.Message}"); }
                    }
            }
            catch (Exception ex) { errors.Add($"{name}: JSON inválido: {ex.Message}"); }
        }
        // Perfis só podem referenciar tweaks A/B existentes.
        foreach (var p in profiles)
            p.Entries.RemoveAll(e =>
            {
                var t = tweaks.FirstOrDefault(x => x.Id == e.TweakId);
                bool bad = t is null || t.Grade > EvidenceGrade.B || t.GetOption(e.Option) is null;
                if (bad) errors.Add($"Perfil {p.Id}: entrada inválida {e.TweakId}/{e.Option}");
                return bad;
            });
        return new TweakCatalog(tweaks, profiles, errors);
    }
}

public sealed class TweakProfile
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new("");
    public LocalizedText Description { get; set; } = new("");
    /// <summary>"system" ou "gaming".</summary>
    public string Kind { get; set; } = "system";
    public List<ProfileEntry> Entries { get; set; } = new();
    public ProfileConditions? Conditions { get; set; }
}

public sealed class ProfileEntry
{
    public string TweakId { get; set; } = "";
    public string Option { get; set; } = "";
    public LocalizedText? Rationale { get; set; }
}

public sealed class ProfileConditions
{
    public FormFactor? FormFactor { get; set; }
}

/// <summary>Validação estrutural e de segurança de manifestos.</summary>
public static class CatalogValidator
{
    public static List<string> Validate(TweakManifest m)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(m.Id) || !m.Id.All(c => char.IsLower(c) || char.IsDigit(c) || c is '.' or '-' or '_')) e.Add("ID inválido (use minúsculas, dígitos, '.', '-', '_').");
        if (string.IsNullOrWhiteSpace(m.Name.Pt)) e.Add("Nome ausente.");
        if (m.Options.Count == 0) e.Add("Nenhuma opção definida.");
        if (m.GetOption(m.DefaultOption) is null) e.Add($"defaultOption '{m.DefaultOption}' não existe.");
        if (m.RecommendedOption is not null && m.GetOption(m.RecommendedOption) is null) e.Add($"recommendedOption '{m.RecommendedOption}' não existe.");
        if (m.RecommendedOption is not null && m.Grade > EvidenceGrade.B) e.Add("Tweaks C/D/E não podem ter opção recomendada.");
        if (m.Options.Select(o => o.Key).Distinct().Count() != m.Options.Count) e.Add("Chaves de opção duplicadas.");

        HashSet<string>? identity = null;
        foreach (var o in m.Options)
        {
            if (o.Targets.Count == 0) { e.Add($"Opção '{o.Key}' sem alvos."); continue; }
            var ids = o.Targets.Select(t => t.IdentityKey).ToHashSet();
            if (ids.Count != o.Targets.Count) e.Add($"Opção '{o.Key}' tem alvos repetidos.");
            if (identity is null) identity = ids;
            else if (!identity.SetEquals(ids)) e.Add($"Opção '{o.Key}' não cobre o mesmo conjunto de alvos das demais.");
            foreach (var t in o.Targets) e.AddRange(ValidateTarget(t));
        }
        return e;
    }

    public static IEnumerable<string> ValidateTarget(SettingTarget t)
    {
        switch (t)
        {
            case RegistryTarget r:
                if (string.IsNullOrWhiteSpace(r.Path) || r.Path.Contains("..") || r.Path.StartsWith('\\')) yield return $"Caminho de Registro inválido: {r.Path}";
                if (r.Hive == RegistryHive.HKLM && r.Path.StartsWith(@"SYSTEM\CurrentControlSet\Services\", StringComparison.OrdinalIgnoreCase))
                {
                    var svc = r.Path.Split('\\')[3];
                    if (Protection.IsProtectedService(svc)) yield return $"Registro de serviço protegido: {svc}";
                }
                if (r.Path.Contains(@"\Policies\Microsoft\Windows Defender", StringComparison.OrdinalIgnoreCase)) yield return "Alterações em políticas do Defender não são permitidas.";
                break;
            case ServiceTarget s:
                if (!ServiceConfig.IsValidName(s.ServiceName)) yield return $"Nome de serviço inválido: {s.ServiceName}";
                if (Protection.IsProtectedService(s.ServiceName) && s.StartMode == ServiceStartMode.Disabled) yield return $"Serviço protegido: {s.ServiceName}";
                if (s.StartMode is ServiceStartMode.Boot or ServiceStartMode.System) yield return "Modo Boot/System não permitido.";
                break;
            case BcdTarget b:
                string? err = null;
                try { BcdSettingProvider.Validate(b.Element, b.Value?.ToLowerInvariant()); } catch (Exception ex) { err = ex.Message; }
                if (err is not null) yield return err;
                break;
            case ScheduledTaskTarget st:
                string? terr = null;
                try { ScheduledTaskProvider.Validate(st.TaskPath); } catch (Exception ex) { terr = ex.Message; }
                if (terr is not null) yield return terr;
                break;
            case PowerSettingTarget p:
                if (p.Subgroup == Guid.Empty || p.Setting == Guid.Empty) yield return "GUID de energia ausente.";
                if (!string.Equals(p.Scheme, "active", StringComparison.OrdinalIgnoreCase) && !Guid.TryParse(p.Scheme, out _)) yield return "Esquema de energia inválido.";
                break;
            default:
                yield return $"Tipo de alvo não suportado: {t.GetType().Name}";
                break;
        }
    }

    /// <summary>
    /// Valida, no agente, que alvos recebidos para reversão pertencem ao manifesto (impede que a UI
    /// envie caminhos arbitrários). Para energia, o esquema resolvido (GUID) deve existir.
    /// </summary>
    public static string? ValidateRevertTargets(TweakManifest m, IEnumerable<TargetChange> changes)
    {
        var allowed = m.AllTargets().Select(NeutralKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes)
        {
            if (c.Target is null) return "Alvo ausente.";
            if (!allowed.Contains(NeutralKey(c.Target))) return $"Alvo não pertence ao tweak {m.Id}: {c.Target.Display}";
            if (c.Target is PowerSettingTarget p && !string.Equals(p.Scheme, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(p.Scheme, out var g) || !PowerApi.SchemeExists(g)) return "Esquema de energia inexistente.";
            }
            var v = c.Before;
            if (v.Data is { Length: > 8192 }) return "Valor muito grande.";
            if (c.Target is ServiceTarget st && v.Exists)
            {
                if (!Enum.TryParse<ServiceStartMode>(v.Data, true, out var mode)) return "Modo de serviço inválido.";
                if (mode == ServiceStartMode.Disabled && Protection.IsProtectedService(st.ServiceName)) return "Serviço protegido.";
            }
            if (c.Target is BcdTarget bt)
            {
                try { BcdSettingProvider.Validate(bt.Element, v.Exists ? v.Data?.ToLowerInvariant() : null); }
                catch (Exception ex) { return ex.Message; }
            }
        }
        return null;
    }

    /// <summary>Chave de identidade que ignora o esquema de energia resolvido.</summary>
    public static string NeutralKey(SettingTarget t) => t is PowerSettingTarget p ? $"power:{p.Subgroup}:{p.Setting}".ToUpperInvariant() : t.IdentityKey;
}
