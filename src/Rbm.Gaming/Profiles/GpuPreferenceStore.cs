using System.Globalization;
using Rbm.Core;
using Microsoft.Win32;

namespace Rbm.Gaming.Profiles;

/// <summary>Preferência de GPU por aplicativo (Configurações → Sistema → Tela → Gráficos).</summary>
public enum GpuPreference
{
    /// <summary>"Deixar o Windows decidir" (GpuPreference=0).</summary>
    Default = 0,
    /// <summary>"Economia de energia" (GpuPreference=1) — normalmente a GPU integrada.</summary>
    PowerSaving = 1,
    /// <summary>"Alto desempenho" (GpuPreference=2) — normalmente a GPU dedicada.</summary>
    HighPerformance = 2,
}

/// <summary>Leitura da preferência de GPU de um executável.</summary>
/// <param name="ExecutablePath">Caminho do executável (nome do valor no registro).</param>
/// <param name="Exists">Há valor gravado para este executável.</param>
/// <param name="Preference">Preferência lida (null = sem valor ou valor desconhecido).</param>
/// <param name="RawValue">Texto bruto (ex.: "GpuPreference=2;SwapEffectUpgradeEnable=1;").</param>
public sealed record GpuPreferenceReading(string ExecutablePath, bool Exists, GpuPreference? Preference, string? RawValue);

/// <summary>
/// Lê/grava HKCU\Software\Microsoft\DirectX\UserGpuPreferences (valor = caminho do .exe, dado = "GpuPreference=N;").
/// Operação real e SEM elevação (HKCU). Outros pares do mesmo texto (ex.: SwapEffectUpgradeEnable, AutoHDREnable)
/// são preservados. O valor anterior é devolvido para permitir desfazer.
/// </summary>
public static class GpuPreferenceStore
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ReservedGlobalValue = "DirectXUserGlobalSettings";

    /// <summary>Valida o caminho do executável (absoluto, .exe, sem caracteres inválidos, ≤ 1024 caracteres).</summary>
    public static OperationResult<string> ValidateExecutablePath(string? exePath, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return OperationResult<string>.Fail("Caminho do executável vazio.", "invalid_path");
        string p = exePath.Trim();
        if (p.Length > 1024) return OperationResult<string>.Fail("Caminho muito longo.", "invalid_path");
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.Contains('"') || p.Contains(';') || p.Contains('='))
            return OperationResult<string>.Fail("Caminho contém caracteres inválidos.", "invalid_path");
        if (!Path.IsPathFullyQualified(p)) return OperationResult<string>.Fail("O caminho precisa ser absoluto (ex.: C:\\Jogos\\jogo.exe).", "invalid_path");
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return OperationResult<string>.Fail($"Caminho inválido: {ex.Message}", "invalid_path"); }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return OperationResult<string>.Fail("O arquivo precisa ser um .exe.", "invalid_path");
        if (string.Equals(Path.GetFileName(full), ReservedGlobalValue, StringComparison.OrdinalIgnoreCase)) return OperationResult<string>.Fail("Nome reservado.", "invalid_path");
        if (mustExist && !File.Exists(full)) return OperationResult<string>.Fail("Executável não encontrado.", "not_found");
        return OperationResult<string>.Ok(full);
    }

    /// <summary>Faz o parse de "k=v;k2=v2;" (função pura). Chaves case-insensitive, ordem preservada.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParsePairs(string? raw)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            list.Add(new(part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }
        return list;
    }

    /// <summary>Extrai a preferência de um texto bruto (função pura).</summary>
    public static GpuPreference? ParsePreference(string? raw)
    {
        foreach (var kv in ParsePairs(raw))
            if (kv.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 2)
                return (GpuPreference)n;
        return null;
    }

    /// <summary>Monta o novo texto definindo GpuPreference e preservando os outros pares (função pura).</summary>
    public static string BuildValue(string? existingRaw, GpuPreference preference)
    {
        var pairs = ParsePairs(existingRaw).Where(kv => !kv.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)).ToList();
        pairs.Insert(0, new("GpuPreference", ((int)preference).ToString(CultureInfo.InvariantCulture)));
        return string.Concat(pairs.Select(kv => $"{kv.Key}={kv.Value};"));
    }

    public static GpuPreferenceReading Read(string exePath)
    {
        // O nome do valor no registro é o caminho exato: normaliza (barras, ".."), como em Set.
        if (ValidateExecutablePath(exePath, mustExist: false) is { Success: true, Value: { } norm }) exePath = norm;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k?.GetValue(exePath) is string raw) return new GpuPreferenceReading(exePath, true, ParsePreference(raw), raw);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new GpuPreferenceReading(exePath, false, null, null);
    }

    /// <summary>Lista todas as preferências gravadas (exceto o valor global).</summary>
    public static IReadOnlyList<GpuPreferenceReading> ReadAll()
    {
        var list = new List<GpuPreferenceReading>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (k is null) return list;
            foreach (var name in k.GetValueNames())
            {
                if (string.Equals(name, ReservedGlobalValue, StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValue(name) is string raw) list.Add(new GpuPreferenceReading(name, true, ParsePreference(raw), raw));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    /// <summary>
    /// Define a preferência (HKCU, sem elevação). Retorna a leitura ANTERIOR para desfazer via <see cref="Restore"/>.
    /// O jogo precisa ser reiniciado para a mudança valer.
    /// </summary>
    public static OperationResult<GpuPreferenceReading> Set(string exePath, GpuPreference preference)
    {
        if (!Enum.IsDefined(preference)) return OperationResult<GpuPreferenceReading>.Fail("Preferência inválida.", "invalid_value");
        var v = ValidateExecutablePath(exePath);
        if (!v.Success) return OperationResult<GpuPreferenceReading>.Fail(v.Message, v.ErrorCode);
        string path = v.Value!;
        try
        {
            var previous = Read(path);
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            k.SetValue(path, BuildValue(previous.RawValue, preference), RegistryValueKind.String);
            return OperationResult<GpuPreferenceReading>.Ok(previous, $"Preferência de GPU definida como {Describe(preference)}. Reinicie o jogo para aplicar.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return OperationResult<GpuPreferenceReading>.Fail($"Falha ao gravar no registro: {ex.Message}", "registry_error"); }
    }

    /// <summary>Restaura um estado lido anteriormente (recria o valor bruto ou remove-o se não existia).</summary>
    public static OperationResult Restore(GpuPreferenceReading previous)
    {
        var v = ValidateExecutablePath(previous.ExecutablePath, mustExist: false);
        if (!v.Success) return OperationResult.Fail(v.Message, v.ErrorCode);
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (previous.Exists && previous.RawValue is not null) k.SetValue(previous.ExecutablePath, previous.RawValue, RegistryValueKind.String);
            else k.DeleteValue(previous.ExecutablePath, throwOnMissingValue: false);
            return OperationResult.Ok("Preferência de GPU restaurada.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return OperationResult.Fail($"Falha ao gravar no registro: {ex.Message}", "registry_error"); }
    }

    public static string Describe(GpuPreference p) => p switch
    {
        GpuPreference.PowerSaving => "Economia de energia",
        GpuPreference.HighPerformance => "Alto desempenho",
        _ => "Deixar o Windows decidir",
    };
}
