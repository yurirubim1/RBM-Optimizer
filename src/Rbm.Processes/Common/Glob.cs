namespace Rbm.Processes;

/// <summary>Curinga simples (<c>*</c> e <c>?</c>), sem regex, sem diferenciar maiúsculas — usado por regras e exceções.</summary>
public static class Glob
{
    public static bool HasWildcards(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    public static bool IsMatch(string pattern, string text)
    {
        ReadOnlySpan<char> p = pattern, t = text;
        int pi = 0, ti = 0, starP = -1, starT = 0;
        while (ti < t.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || char.ToUpperInvariant(p[pi]) == char.ToUpperInvariant(t[ti]))) { pi++; ti++; }
            else if (pi < p.Length && p[pi] == '*') { starP = pi++; starT = ti; }
            else if (starP >= 0) { pi = starP + 1; ti = ++starT; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}
