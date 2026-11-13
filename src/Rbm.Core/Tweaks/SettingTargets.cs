using System.Text.Json.Serialization;

namespace Rbm.Core.Tweaks;

/// <summary>
/// Um alvo de configuração com valor desejado. Tipos são fechados (allow-list):
/// o agente privilegiado só sabe ler/escrever estes tipos, e só para alvos presentes no catálogo compilado.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RegistryTarget), "registry")]
[JsonDerivedType(typeof(PowerSettingTarget), "power")]
[JsonDerivedType(typeof(ServiceTarget), "service")]
[JsonDerivedType(typeof(BcdTarget), "bcd")]
[JsonDerivedType(typeof(ScheduledTaskTarget), "task")]
public abstract class SettingTarget
{
    /// <summary>Identidade do alvo (independente do valor desejado).</summary>
    [JsonIgnore] public abstract string IdentityKey { get; }
    /// <summary>Descrição legível do alvo.</summary>
    [JsonIgnore] public abstract string Display { get; }
    /// <summary>Valor desejado por esta opção.</summary>
    [JsonIgnore] public abstract SettingValue Desired { get; }
    /// <summary>Requer privilégio administrativo para escrita.</summary>
    [JsonIgnore] public abstract bool WriteRequiresAdmin { get; }
    /// <summary>Requer privilégio administrativo para leitura.</summary>
    [JsonIgnore] public virtual bool ReadRequiresAdmin => false;

    public virtual bool Matches(SettingValue current) => Desired.SemanticEquals(current);
}

public enum RegistryHive { HKLM, HKCU }

public enum RegistryValueKind2 { DWord, QWord, String, ExpandString, MultiString, Binary }

public sealed class RegistryTarget : SettingTarget
{
    public RegistryHive Hive { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public RegistryValueKind2 Kind { get; set; } = RegistryValueKind2.DWord;
    /// <summary>Valor como string (decimal para DWORD/QWORD, hex para Binary, '\n' separando MultiString). Null = valor ausente/removido.</summary>
    public string? Value { get; set; }
    /// <summary>
    /// Outros valores que também contam como "este estado" na leitura (ex.: ausente ≡ 1 quando o
    /// Windows trata a falta do valor como ativado). Null na lista = valor ausente.
    /// </summary>
    public List<string?>? Equivalents { get; set; }

    public override string IdentityKey => $"reg:{Hive}\\{Path.TrimEnd('\\')}\\{Name}".ToUpperInvariant();
    public override string Display => $"{Hive}\\{Path}\\{(string.IsNullOrEmpty(Name) ? "(Padrão)" : Name)}";
    public override SettingValue Desired => Value is null ? SettingValue.Absent : new SettingValue(true, Kind.ToString(), Value);
    public override bool WriteRequiresAdmin => Hive == RegistryHive.HKLM;

    public override bool Matches(SettingValue current)
    {
        if (base.Matches(current)) return true;
        if (Equivalents is null) return false;
        foreach (var e in Equivalents)
        {
            var alt = e is null ? SettingValue.Absent : new SettingValue(true, Kind.ToString(), e);
            if (alt.SemanticEquals(current)) return true;
        }
        return false;
    }
}

public sealed class PowerSettingTarget : SettingTarget
{
    /// <summary>"active" = esquema ativo (resolvido no momento da aplicação) ou GUID explícito.</summary>
    public string Scheme { get; set; } = "active";
    public Guid Subgroup { get; set; }
    public Guid Setting { get; set; }
    public uint? Ac { get; set; }
    public uint? Dc { get; set; }
    public string? FriendlyName { get; set; }

    public override string IdentityKey => $"power:{Scheme}:{Subgroup}:{Setting}".ToUpperInvariant();
    public override string Display => $"Energia: {FriendlyName ?? Setting.ToString()} (esquema {Scheme})";
    public override SettingValue Desired => new(true, "power", PowerValue.Format(Ac, Dc));
    // Alterar índices de esquemas do sistema exige admin na maioria das builds; tratamos como privilegiado.
    public override bool WriteRequiresAdmin => true;

    public override bool Matches(SettingValue current)
    {
        if (!current.Exists || current.Data is null) return false;
        var (ac, dc) = PowerValue.Parse(current.Data);
        return (Ac is null || Ac == ac) && (Dc is null || Dc == dc);
    }
}

public static class PowerValue
{
    public static string Format(uint? ac, uint? dc) => $"ac={(ac?.ToString() ?? "*")};dc={(dc?.ToString() ?? "*")}";
    public static (uint? ac, uint? dc) Parse(string s)
    {
        uint? ac = null, dc = null;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !uint.TryParse(kv[1], out var v)) continue;
            if (kv[0] == "ac") ac = v; else if (kv[0] == "dc") dc = v;
        }
        return (ac, dc);
    }
}

public enum ServiceStartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

public sealed class ServiceTarget : SettingTarget
{
    public string ServiceName { get; set; } = "";
    public ServiceStartMode StartMode { get; set; }

    public override string IdentityKey => $"svc:{ServiceName}".ToUpperInvariant();
    public override string Display => $"Serviço {ServiceName}: inicialização";
    public override SettingValue Desired => new(true, "start", StartMode.ToString());
    public override bool WriteRequiresAdmin => true;
}

public sealed class BcdTarget : SettingTarget
{
    /// <summary>Elemento BCD (somente elementos da allow-list do provedor são aceitos).</summary>
    public string Element { get; set; } = "";
    /// <summary>Valor; null = elemento removido (padrão do Windows).</summary>
    public string? Value { get; set; }

    public override string IdentityKey => $"bcd:{{current}}:{Element}".ToUpperInvariant();
    public override string Display => $"BCD {{current}} {Element}";
    public override SettingValue Desired => Value is null ? SettingValue.Absent : new SettingValue(true, "bcd", Value);
    public override bool WriteRequiresAdmin => true;
    public override bool ReadRequiresAdmin => true;
}

public sealed class ScheduledTaskTarget : SettingTarget
{
    public string TaskPath { get; set; } = "";
    public bool Enabled { get; set; }

    public override string IdentityKey => $"task:{TaskPath}".ToUpperInvariant();
    public override string Display => $"Tarefa agendada {TaskPath}";
    public override SettingValue Desired => new(true, "task", Enabled ? "Enabled" : "Disabled");
    public override bool WriteRequiresAdmin => true;
}

/// <summary>Valor serializado de um alvo, usado em leitura, snapshot e reversão.</summary>
public sealed record SettingValue(bool Exists, string? Kind, string? Data)
{
    public static readonly SettingValue Absent = new(false, null, null);

    public bool SemanticEquals(SettingValue other)
    {
        if (!Exists && !other.Exists) return true;
        if (Exists != other.Exists) return false;
        if (string.Equals(Data, other.Data, StringComparison.OrdinalIgnoreCase)) return true;
        // Comparação numérica tolerante (ex.: "0x26" vs "38").
        if (TryNum(Data, out var a) && TryNum(other.Data, out var b)) return a == b;
        return false;
    }

    private static bool TryNum(string? s, out ulong v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        if (long.TryParse(s, out var l)) { v = unchecked((ulong)l); return true; }
        return ulong.TryParse(s, out v);
    }

    public override string ToString() => Exists ? (Data ?? "(vazio)") : "(ausente)";
}
