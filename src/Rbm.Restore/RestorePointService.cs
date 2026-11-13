using System.Management;
using Rbm.Core;

namespace Rbm.Restore;

public sealed record RestorePointInfo(uint SequenceNumber, string Description, DateTime CreatedAt, uint Type);

/// <summary>
/// Pontos de restauração do sistema (WMI root\default:SystemRestore).
/// Observações honestas: o Windows limita a criação a 1 ponto a cada 24 h por padrão
/// (SystemRestorePointCreationFrequency) e a Proteção do Sistema pode estar desativada.
/// Pontos de restauração NÃO substituem backups completos.
/// </summary>
public static class RestorePointService
{
    public static IReadOnlyList<RestorePointInfo> List()
    {
        var list = new List<RestorePointInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore");
            foreach (ManagementObject o in s.Get())
            {
                DateTime created = DateTime.MinValue;
                try { created = ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString() ?? ""); } catch { }
                list.Add(new RestorePointInfo(Convert.ToUInt32(o["SequenceNumber"]), o["Description"]?.ToString() ?? "", created, Convert.ToUInt32(o["RestorePointType"])));
            }
        }
        catch { /* sem permissão ou proteção desativada */ }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>Requer elevação — executado via Rbm.Agent.</summary>
    public static OperationResult<RestorePointInfo> Create(string description)
    {
        description = new string((description ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
        if (string.IsNullOrWhiteSpace(description)) description = "RBM Optimizer";
        var before = List();
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12u;  // MODIFY_SETTINGS
            inParams["EventType"] = 100u;        // BEGIN_SYSTEM_CHANGE
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
            if (rv != 0)
                return OperationResult<RestorePointInfo>.Fail(rv == 1058
                    ? "A Proteção do Sistema (serviço de restauração) está desativada."
                    : $"Falha ao criar ponto de restauração (código {rv}). Verifique se a Proteção do Sistema está ativa na unidade do sistema.");
        }
        catch (ManagementException ex)
        {
            return OperationResult<RestorePointInfo>.Fail("Falha ao criar ponto de restauração: " + ex.Message);
        }
        var after = List();
        var created = after.FirstOrDefault(a => before.All(b => b.SequenceNumber != a.SequenceNumber));
        if (created is null)
        {
            var last = after.FirstOrDefault();
            return OperationResult<RestorePointInfo>.Fail(last is not null && (DateTime.Now - last.CreatedAt).TotalHours < 24
                ? $"O Windows não criou um novo ponto: limite de 1 ponto a cada 24 h. Último ponto: \"{last.Description}\" em {last.CreatedAt:g}."
                : "O Windows aceitou a solicitação, mas nenhum novo ponto foi encontrado. Verifique a Proteção do Sistema.", "THROTTLED");
        }
        return OperationResult<RestorePointInfo>.Ok(created, $"Ponto de restauração criado: {created.Description}");
    }
}
