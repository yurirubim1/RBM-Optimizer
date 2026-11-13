using System.Text;

namespace Rbm.Gaming.Library;

/// <summary>
/// Nó de um documento KeyValues (VDF texto da Valve). Um nó tem valor (folha) OU filhos (objeto).
/// Chaves duplicadas são preservadas na ordem; buscas são case-insensitive (como no Steam).
/// </summary>
public sealed record VdfNode(string Key, string? Value, IReadOnlyList<VdfNode> Children)
{
    public bool IsObject => Value is null;

    /// <summary>Primeiro filho com a chave (case-insensitive) ou null.</summary>
    public VdfNode? Child(string key)
    {
        foreach (var c in Children)
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Valor do primeiro filho-folha com a chave, ou null.</summary>
    public string? GetValue(string key) => Child(key) is { Value: { } v } ? v : null;

    /// <summary>Caminho de chaves (ex.: "AppState", "appid").</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? n = this;
        foreach (var k in keys) { n = n?.Child(k); if (n is null) return null; }
        return n;
    }
}

public sealed class VdfParseException(string message, int line) : FormatException($"VDF inválido (linha {line}): {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// Parser robusto de KeyValues texto (libraryfolders.vdf, appmanifest_*.acf, config.vdf ...).
/// Suporta: strings entre aspas com escapes (\" \\ \n \t), tokens sem aspas, comentários "//",
/// condicionais "[$WIN32]" (ignoradas), #include/#base (tratados como pares comuns), BOM,
/// chaves duplicadas, e tolera chave final sem valor e '}' faltando no fim do arquivo.
/// </summary>
public static class VdfParser
{
    private enum TokKind { String, Open, Close, End }
    private readonly record struct Tok(TokKind Kind, string Text, int Line);

    /// <summary>Faz o parse e retorna um nó raiz (Key = "") cujos filhos são os objetos de nível superior.</summary>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var toks = Tokenize(text);
        int pos = 0;
        var children = ParseObjectBody(toks, ref pos, topLevel: true);
        return new VdfNode("", null, children);
    }

    public static bool TryParse(string text, out VdfNode? root, out string? error)
    {
        try { root = Parse(text); error = null; return true; }
        catch (VdfParseException ex) { root = null; error = ex.Message; return false; }
    }

    private static List<VdfNode> ParseObjectBody(List<Tok> toks, ref int pos, bool topLevel)
    {
        var list = new List<VdfNode>();
        while (pos < toks.Count)
        {
            var t = toks[pos];
            if (t.Kind == TokKind.End) { if (!topLevel) { /* '}' ausente no fim: tolerado */ } return list; }
            if (t.Kind == TokKind.Close)
            {
                if (topLevel) throw new VdfParseException("'}' sem '{' correspondente", t.Line);
                pos++;
                return list;
            }
            if (t.Kind == TokKind.Open) throw new VdfParseException("'{' sem chave", t.Line);

            string key = t.Text; pos++;
            var next = toks[pos];
            switch (next.Kind)
            {
                case TokKind.String:
                    pos++;
                    list.Add(new VdfNode(key, next.Text, []));
                    break;
                case TokKind.Open:
                    pos++;
                    var kids = ParseObjectBody(toks, ref pos, topLevel: false);
                    list.Add(new VdfNode(key, null, kids));
                    break;
                case TokKind.Close:
                case TokKind.End:
                    // chave sem valor (arquivo truncado): ignora a chave.
                    break;
            }
        }
        return list;
    }

    private static List<Tok> Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0, line = 1, n = s.Length;
        if (n > 0 && s[0] == '﻿') i = 1;
        var sb = new StringBuilder();
        while (i < n)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n') i++;
                continue;
            }
            if (c == '{') { toks.Add(new(TokKind.Open, "{", line)); i++; continue; }
            if (c == '}') { toks.Add(new(TokKind.Close, "}", line)); i++; continue; }
            if (c == '[')
            {
                // condicional de plataforma, ex. [$WIN32] / [!$X360] — ignorada
                int close = s.IndexOf(']', i);
                if (close < 0) throw new VdfParseException("condicional '[' sem ']'", line);
                i = close + 1;
                continue;
            }
            if (c == '"')
            {
                int startLine = line;
                i++;
                sb.Clear();
                bool closed = false;
                while (i < n)
                {
                    char d = s[i];
                    if (d == '\\' && i + 1 < n)
                    {
                        char e = s[i + 1];
                        switch (e)
                        {
                            case '"': sb.Append('"'); i += 2; continue;
                            case '\\': sb.Append('\\'); i += 2; continue;
                            case 'n': sb.Append('\n'); i += 2; continue;
                            case 't': sb.Append('\t'); i += 2; continue;
                            default: sb.Append(d); i++; continue; // barra literal (ex.: caminhos antigos)
                        }
                    }
                    if (d == '"') { closed = true; i++; break; }
                    if (d == '\n') line++;
                    sb.Append(d);
                    i++;
                }
                if (!closed) throw new VdfParseException("string sem aspas de fechamento", startLine);
                toks.Add(new(TokKind.String, sb.ToString(), startLine));
                continue;
            }
            // token sem aspas
            sb.Clear();
            while (i < n && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"'))
            {
                if (s[i] == '/' && i + 1 < n && s[i + 1] == '/') break;
                sb.Append(s[i]); i++;
            }
            toks.Add(new(TokKind.String, sb.ToString(), line));
        }
        toks.Add(new(TokKind.End, "", line));
        return toks;
    }
}
