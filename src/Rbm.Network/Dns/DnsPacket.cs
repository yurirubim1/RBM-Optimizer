using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Rbm.Network.Dns;

public static class DnsType
{
    public const ushort A = 1;
    public const ushort NS = 2;
    public const ushort CNAME = 5;
    public const ushort SOA = 6;
    public const ushort AAAA = 28;
}

public sealed record DnsQuestion(string Name, ushort Type, ushort Class);

/// <summary>Registro de resposta. <see cref="Data"/> é o valor decodificado (IP para A/AAAA, nome para CNAME/NS) ou hex.</summary>
public sealed record DnsRecord(string Name, ushort Type, ushort Class, uint Ttl, string Data);

public sealed record DnsMessage(
    ushort Id,
    bool IsResponse,
    int Opcode,
    bool Authoritative,
    bool Truncated,
    bool RecursionDesired,
    bool RecursionAvailable,
    int ResponseCode,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsRecord> Answers,
    int AuthorityCount,
    int AdditionalCount);

/// <summary>Codificação/decodificação mínima de mensagens DNS (RFC 1035), com proteção contra ponteiros em laço.</summary>
public static class DnsPacket
{
    public const int HeaderSize = 12;

    /// <summary>Monta uma consulta padrão (QDCOUNT = 1, RD conforme parâmetro). Nomes IDN são convertidos para punycode.</summary>
    public static byte[] BuildQuery(ushort id, string name, ushort qtype = DnsType.A, bool recursionDesired = true)
    {
        var labels = EncodeName(name);
        var buf = new byte[HeaderSize + labels.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), id);
        buf[2] = (byte)(recursionDesired ? 0x01 : 0x00); // QR=0, OPCODE=0, AA=0, TC=0, RD
        buf[3] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1); // QDCOUNT
        labels.CopyTo(buf, HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(HeaderSize + labels.Length), qtype);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(HeaderSize + labels.Length + 2), 1); // IN
        return buf;
    }

    /// <summary>Converte um nome em labels DNS. Lança <see cref="ArgumentException"/> se inválido.</summary>
    public static byte[] EncodeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Nome vazio.", nameof(name));
        string ascii;
        try { ascii = new IdnMapping().GetAscii(name.Trim().TrimEnd('.')); }
        catch (ArgumentException) { throw new ArgumentException($"Nome inválido: {name}", nameof(name)); }
        if (ascii.Length > 253) throw new ArgumentException("Nome excede 253 caracteres.", nameof(name));

        var ms = new List<byte>(ascii.Length + 2);
        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63) throw new ArgumentException($"Label inválido em \"{name}\".", nameof(name));
            foreach (char c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException($"Caractere inválido em \"{name}\".", nameof(name));
            ms.Add((byte)label.Length);
            ms.AddRange(Encoding.ASCII.GetBytes(label));
        }
        ms.Add(0);
        return ms.ToArray();
    }

    /// <summary>Decodifica uma mensagem. Retorna false (com motivo) se estiver malformada.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage? message, out string? error)
    {
        message = null; error = null;
        try
        {
            if (data.Length < HeaderSize) { error = "Mensagem menor que o cabeçalho DNS."; return false; }
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(data);
            byte f1 = data[2], f2 = data[3];
            int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            int an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            int ns = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
            int ar = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            bool tc = (f1 & 0x02) != 0;

            int pos = HeaderSize;
            var questions = new List<DnsQuestion>(qd);
            for (int i = 0; i < qd; i++)
            {
                string qn = ReadName(data, ref pos);
                Need(data, pos, 4);
                questions.Add(new DnsQuestion(qn, BinaryPrimitives.ReadUInt16BigEndian(data[pos..]), BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..])));
                pos += 4;
            }

            var answers = new List<DnsRecord>(an);
            for (int i = 0; i < an; i++)
            {
                if (pos >= data.Length && tc) break; // truncada: respostas parciais são aceitáveis
                string rn = ReadName(data, ref pos);
                Need(data, pos, 10);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                ushort cls = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
                int rdlen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
                pos += 10;
                Need(data, pos, rdlen);
                string value = type switch
                {
                    DnsType.A when rdlen == 4 => new IPAddress(data.Slice(pos, 4)).ToString(),
                    DnsType.AAAA when rdlen == 16 => new IPAddress(data.Slice(pos, 16)).ToString(),
                    DnsType.CNAME or DnsType.NS => ReadNameAt(data, pos),
                    _ => Convert.ToHexString(data.Slice(pos, rdlen)),
                };
                answers.Add(new DnsRecord(rn, type, cls, ttl, value));
                pos += rdlen;
            }

            message = new DnsMessage(id, (f1 & 0x80) != 0, (f1 >> 3) & 0x0F, (f1 & 0x04) != 0, tc, (f1 & 0x01) != 0,
                (f2 & 0x80) != 0, f2 & 0x0F, questions, answers, ns, ar);
            return true;
        }
        catch (FormatException ex) { error = ex.Message; return false; }
    }

    private static void Need(ReadOnlySpan<byte> data, int pos, int len)
    {
        if (pos < 0 || len < 0 || pos + len > data.Length) throw new FormatException("Mensagem DNS truncada/malformada.");
    }

    private static string ReadNameAt(ReadOnlySpan<byte> data, int pos) => ReadName(data, ref pos);

    /// <summary>Lê um nome com compressão. Ponteiros devem apontar para trás (impede laços); máximo 255 bytes.</summary>
    public static string ReadName(ReadOnlySpan<byte> data, ref int pos)
    {
        var sb = new StringBuilder();
        int cur = pos, jumps = 0, total = 0;
        bool jumped = false;
        while (true)
        {
            Need(data, cur, 1);
            byte len = data[cur];
            if (len == 0)
            {
                if (!jumped) pos = cur + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                Need(data, cur, 2);
                int target = ((len & 0x3F) << 8) | data[cur + 1];
                if (target >= cur) throw new FormatException("Ponteiro de compressão DNS inválido (para frente/laço).");
                if (++jumps > 64) throw new FormatException("Excesso de ponteiros de compressão DNS.");
                if (!jumped) pos = cur + 2;
                jumped = true;
                cur = target;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("Tipo de label DNS não suportado.");
            Need(data, cur + 1, len);
            total += len + 1;
            if (total > 255) throw new FormatException("Nome DNS excede 255 bytes.");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(data.Slice(cur + 1, len)));
            cur += len + 1;
        }
        return sb.ToString();
    }

    public static string RCodeName(int rcode) => rcode switch
    {
        0 => "NOERROR",
        1 => "FORMERR",
        2 => "SERVFAIL",
        3 => "NXDOMAIN",
        4 => "NOTIMP",
        5 => "REFUSED",
        _ => $"RCODE {rcode}",
    };
}
