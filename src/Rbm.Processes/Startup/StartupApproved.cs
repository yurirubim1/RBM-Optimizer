using System.Buffers.Binary;

namespace Rbm.Processes;

/// <summary>Estado decodificado de um valor StartupApproved.</summary>
public readonly record struct StartupApprovedState(bool Present, bool Enabled, DateTime? DisabledAtUtc, byte Flag);

/// <summary>
/// Codificação usada pelo Gerenciador de Tarefas em
/// HK??\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}:
/// valor binário de 12 bytes; byte 0 = 0x02/0x06 habilitado, 0x03/0x07 desabilitado (bit 0 = desabilitado);
/// bytes 4..11 = FILETIME (UTC) do momento em que foi desabilitado (zeros quando habilitado).
/// Valor ausente = habilitado. Alternar é reversível e não apaga a entrada original.
/// </summary>
public static class StartupApproved
{
    public const int ValueLength = 12;
    public const string KeyBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static StartupApprovedState Decode(byte[]? data)
    {
        if (data is null || data.Length == 0) return new StartupApprovedState(false, true, null, 0);
        byte flag = data[0];
        bool enabled = (flag & 0x01) == 0;
        DateTime? when = null;
        if (!enabled && data.Length >= ValueLength)
        {
            long ft = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));
            if (ft > 0)
            {
                try { when = DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { }
            }
        }
        return new StartupApprovedState(true, enabled, when, flag);
    }

    /// <summary>
    /// Gera o valor binário. Preserva o bit 0x04 do valor anterior (variantes 0x06/0x07 usadas pelo Windows).
    /// </summary>
    public static byte[] Encode(bool enabled, DateTime? disabledAtUtc = null, byte[]? previous = null)
    {
        var data = new byte[ValueLength];
        byte keep = previous is { Length: > 0 } ? (byte)(previous[0] & 0x04) : (byte)0;
        data[0] = (byte)(0x02 | keep | (enabled ? 0 : 1));
        if (!enabled)
        {
            var when = (disabledAtUtc ?? DateTime.UtcNow).ToUniversalTime();
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4, 8), when.ToFileTimeUtc());
        }
        return data;
    }
}
