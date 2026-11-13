using System.Text.Json;
using System.Text.Json.Serialization;
using Rbm.Core;

namespace Rbm.Processes;

/// <summary>
/// Persistência das regras em JSON (padrão: %LOCALAPPDATA%\RbmOptimizer\process-rules.json).
/// Escrita atômica: arquivo temporário + flush + substituição (MoveFileEx REPLACE_EXISTING).
/// </summary>
public sealed class ProcessRuleStore
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly object _lock = new();

    public ProcessRuleStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(RbmPaths.DataRoot, "process-rules.json");
    }

    public string FilePath { get; }

    /// <summary>Carrega as regras. Arquivo ausente = conjunto vazio. Arquivo corrompido = falha (o arquivo não é alterado).</summary>
    public OperationResult<ProcessRuleSet> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return OperationResult<ProcessRuleSet>.Ok(new ProcessRuleSet(), "Nenhuma regra salva.");
                var json = File.ReadAllText(FilePath);
                var set = JsonSerializer.Deserialize<ProcessRuleSet>(json, JsonOptions) ?? new ProcessRuleSet();
                // JSON editado à mão pode trazer null em listas/strings.
                set = set with
                {
                    Rules = (set.Rules ?? Array.Empty<ProcessRule>()).Where(r => r is not null)
                        .Select(r => r with { ExecutableName = r.ExecutableName ?? "", Exceptions = r.Exceptions ?? Array.Empty<string>(), Id = r.Id ?? Guid.NewGuid().ToString("N") })
                        .ToList(),
                };
                var invalid = set.Rules.Where(r => ProcessRuleValidator.Validate(r).Count > 0).ToList();
                // Regras inválidas (editadas à mão) são carregadas DESABILITADAS para não serem aplicadas.
                if (invalid.Count > 0)
                    set = set with { Rules = set.Rules.Select(r => invalid.Contains(r) ? r with { Enabled = false } : r).ToList() };
                return OperationResult<ProcessRuleSet>.Ok(set, invalid.Count > 0 ? $"{invalid.Count} regra(s) inválida(s) foram desabilitadas." : "OK");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return OperationResult<ProcessRuleSet>.Fail($"Arquivo de regras inválido ({ex.Message}). Corrija ou remova {FilePath}.", ProcessErrorCodes.InvalidArgument);
            }
            catch (Exception ex)
            {
                return OperationResult<ProcessRuleSet>.Fail("Falha ao ler as regras: " + ex.Message, ProcessErrorCodes.Failed);
            }
        }
    }

    /// <summary>Valida e grava atomicamente.</summary>
    public OperationResult Save(ProcessRuleSet set)
    {
        var errors = set.Rules.SelectMany(r => ProcessRuleValidator.Validate(r).Select(e => $"{r.ExecutableName}: {e}")).ToList();
        if (set.Rules.GroupBy(r => r.Id).Any(g => g.Count() > 1)) errors.Add("IDs de regra duplicados.");
        if (errors.Count > 0) return OperationResult.Fail("Regras inválidas: " + string.Join(" | ", errors), ProcessErrorCodes.InvalidArgument);

        lock (_lock)
        {
            string tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(set, JsonOptions);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, FilePath, overwrite: true);
                return OperationResult.Ok($"{set.Rules.Count} regra(s) salva(s).");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return OperationResult.Fail("Falha ao salvar as regras: " + ex.Message, ProcessErrorCodes.Failed);
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
