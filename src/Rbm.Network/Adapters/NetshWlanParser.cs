using System.Globalization;
using System.Text;

namespace Rbm.Network.Adapters;

/// <summary>
/// Parser robusto de <c>netsh wlan show interfaces</c> (inglês e pt-BR; tolera acentos ausentes ou corrompidos
/// por codificação de console). Usado apenas como fallback da Native Wifi API.
/// </summary>
public static class NetshWlanParser
{
    public sealed record Entry(string? Name, string? Guid, WifiDetails Details);

    private enum Field
    {
        None, Name, Description, Guid, State, Ssid, Bssid, NetworkType, RadioType, Authentication, Cipher,
        ConnectionMode, Band, Channel, RxRate, TxRate, Signal, Rssi, Profile,
    }

    public static IReadOnlyList<Entry> Parse(string output)
    {
        var entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(output)) return entries;

        Dictionary<Field, string>? cur = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int sep = line.IndexOf(" : ", StringComparison.Ordinal);
            if (sep < 0) { sep = line.IndexOf(':'); if (sep < 0) continue; }
            string key = line[..sep].Trim();
            string value = line[(sep + (line.AsSpan(sep).StartsWith(" : ") ? 3 : 1))..].Trim();
            if (key.Length == 0) continue;

            var field = Classify(key);
            if (field == Field.None) continue;
            if (field == Field.Name)
            {
                if (cur is not null) entries.Add(Build(cur));
                cur = new Dictionary<Field, string>();
            }
            cur ??= new Dictionary<Field, string>();
            cur.TryAdd(field, value);
        }
        if (cur is not null) entries.Add(Build(cur));
        return entries;
    }

    private static Entry Build(Dictionary<Field, string> f)
    {
        string? Get(Field k) => f.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

        string? state = Get(Field.State);
        bool connected = state is not null && Normalize(state).StartsWith("conect", StringComparison.Ordinal) ||
                         state is not null && Normalize(state).StartsWith("connected", StringComparison.Ordinal);
        int? signal = ParseInt(Get(Field.Signal)?.TrimEnd('%', ' '));
        int? rssi = ParseInt(Get(Field.Rssi));
        int? channel = ParseInt(Get(Field.Channel));
        string? band = Get(Field.Band);
        if (band is not null) band = band.Replace('.', ',');
        band ??= WifiFormat.BandFromChannel(channel);

        var details = connected
            ? new WifiDetails(true, null, state, Get(Field.Ssid), Get(Field.Bssid)?.ToLowerInvariant(), signal, rssi, channel, null, band,
                Get(Field.RadioType), ParseDouble(Get(Field.RxRate)), ParseDouble(Get(Field.TxRate)),
                Get(Field.Authentication), Get(Field.Cipher), Get(Field.Profile), "netsh")
            : WifiDetails.Unavailable($"Interface Wi-Fi não conectada ({state ?? "estado desconhecido"}).", "netsh") with { State = state };
        return new Entry(Get(Field.Name), Get(Field.Guid), details);
    }

    private static Field Classify(string key)
    {
        string k = Normalize(key);
        // Ordem importa: "ap bssid"/"bssid" antes de "ssid"; "taxa de recepcao" antes de genéricos.
        if (k is "name" or "nome") return Field.Name;
        if (k.StartsWith("descri")) return Field.Description;
        if (k == "guid") return Field.Guid;
        if (k is "state" or "estado") return Field.State;
        if (k.EndsWith("bssid")) return Field.Bssid;
        if (k == "ssid") return Field.Ssid;
        if (k is "network type" or "tipo de rede") return Field.NetworkType;
        if (k is "radio type" or "tipo de radio" || k.StartsWith("tipo de r") && k.EndsWith("dio")) return Field.RadioType;
        if (k.StartsWith("authentication") || k.StartsWith("autentica")) return Field.Authentication;
        if (k is "cipher" or "cifra" || k.StartsWith("codifica") || k.StartsWith("criptografia")) return Field.Cipher;
        if (k is "connection mode" or "modo de conexao" || k.StartsWith("modo de conex")) return Field.ConnectionMode;
        if (k is "band" or "banda") return Field.Band;
        if (k is "channel" or "canal") return Field.Channel;
        if (k.StartsWith("receive rate") || k.StartsWith("taxa de recep") || k.StartsWith("taxa de rece")) return Field.RxRate;
        if (k.StartsWith("transmit rate") || k.StartsWith("taxa de transmiss")) return Field.TxRate;
        if (k is "signal" or "sinal") return Field.Signal;
        if (k == "rssi") return Field.Rssi;
        if (k is "profile" or "perfil") return Field.Profile;
        return Field.None;
    }

    /// <summary>Minúsculas, sem acentos; caracteres não ASCII (mojibake) são descartados.</summary>
    internal static string Normalize(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c > 127) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static int? ParseInt(string? s)
    {
        if (s is null) return null;
        var digits = new string(s.Trim().TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    private static double? ParseDouble(string? s)
    {
        if (s is null) return null;
        var t = s.Trim().Replace(',', '.');
        var num = new string(t.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
