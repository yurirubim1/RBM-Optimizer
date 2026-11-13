using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Core.Tweaks;

namespace Rbm.Optimization.Providers;

/// <summary>
/// Elementos BCD do carregador atual ({current}). Allow-list estrita de elementos e valores —
/// nada relacionado a hypervisor/VBS/assinatura/integridade é acessível.
/// Leitura e escrita exigem elevação (bcdedit).
/// </summary>
public sealed partial class BcdSettingProvider : ISettingProvider
{
    public static readonly IReadOnlyDictionary<string, string[]> AllowedElements = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["useplatformclock"] = new[] { "yes", "no" },
        ["useplatformtick"] = new[] { "yes", "no" },
        ["disabledynamictick"] = new[] { "yes", "no" },
        ["tscsyncpolicy"] = new[] { "default", "legacy", "enhanced" },
    };

    public bool CanHandle(SettingTarget target) => target is BcdTarget;

    public static void Validate(string element, string? value)
    {
        if (!AllowedElements.TryGetValue(element, out var allowed)) throw new ArgumentException($"Elemento BCD não permitido: {element}");
        if (value is not null && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Valor BCD não permitido para {element}: {value}");
    }

    public SettingValue Read(SettingTarget target)
    {
        var t = (BcdTarget)target;
        Validate(t.Element, null);
        var r = ProcessRunner.Run(ProcessRunner.System32("bcdedit.exe"), new[] { "/enum", "{current}" }, TimeSpan.FromSeconds(15), ProcessRunner.OemEncoding);
        if (!r.Success) throw new UnauthorizedAccessException("bcdedit requer privilégios administrativos para leitura: " + (r.StdErr + r.StdOut).Trim());
        return ParseElement(r.StdOut, t.Element);
    }

    /// <summary>Extrai um elemento da saída de "bcdedit /enum". Nomes de elementos não são localizados; valores booleanos podem ser.</summary>
    public static SettingValue ParseElement(string output, string element)
    {
        foreach (var line in output.Split('\n'))
        {
            var m = LineRegex().Match(line.TrimEnd('\r'));
            if (!m.Success || !string.Equals(m.Groups[1].Value, element, StringComparison.OrdinalIgnoreCase)) continue;
            return new SettingValue(true, "bcd", Normalize(m.Groups[2].Value.Trim()));
        }
        return SettingValue.Absent;
    }

    private static string Normalize(string v) => v.ToLowerInvariant() switch
    {
        "yes" or "sim" or "on" or "true" or "1" or "sí" or "oui" or "ja" => "yes",
        "no" or "não" or "nao" or "off" or "false" or "0" or "non" or "nein" => "no",
        var other => other,
    };

    public void Write(SettingTarget target, SettingValue value)
    {
        var t = (BcdTarget)target;
        var v = value.Exists ? value.Data?.ToLowerInvariant() : null;
        Validate(t.Element, v);
        var args = v is null
            ? new[] { "/deletevalue", "{current}", t.Element }
            : new[] { "/set", "{current}", t.Element, v };
        var r = ProcessRunner.Run(ProcessRunner.System32("bcdedit.exe"), args, TimeSpan.FromSeconds(15), ProcessRunner.OemEncoding);
        // /deletevalue de elemento inexistente retorna erro: tratamos como sucesso se a releitura confirmar ausência.
        if (!r.Success && !(v is null && !Read(t).Exists))
            throw new InvalidOperationException($"bcdedit falhou ({r.ExitCode}): {(r.StdErr + r.StdOut).Trim()}");
    }

    [GeneratedRegex(@"^([a-z0-9]+)\s{2,}(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();
}
