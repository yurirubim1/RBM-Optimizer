using System.Globalization;
using System.Text;

namespace Rbm.Gaming.Frames;

/// <summary>Opções de parse.</summary>
/// <param name="ProcessName">Processo-alvo (ex.: "game.exe"); null = o processo com mais quadros (ignora "&lt;unknown&gt;").</param>
/// <param name="SwapChain">Swap chain específica; null = a com mais quadros do processo.</param>
public sealed record PresentMonParseOptions(string? ProcessName = null, string? SwapChain = null);

/// <summary>
/// Parser de CSV do PresentMon 1.x e 2.x. O layout é detectado pelo cabeçalho (nomes case-insensitive):
/// <list type="bullet">
/// <item>V1 (1.x / --v1_metrics): msBetweenPresents, msBetweenDisplayChange, msUntilDisplayed, Dropped, TimeInSeconds.</item>
/// <item>V2Metrics (2.0–2.2 / --v2_metrics): FrameTime, CPUBusy, CPUWait, GPUBusy, GPUWait, DisplayLatency, DisplayedTime, CPUStartTime.</item>
/// <item>V2Default (2.3+): MsBetweenPresents, MsBetweenDisplayChange, MsUntilDisplayed, MsCPUBusy, MsCPUWait, MsGPUBusy, MsGPUWait, TimeInMs...</item>
/// </list>
/// "NA" e campos vazios viram NaN. O CSV costuma misturar vários processos; o parser escolhe o processo e a
/// swap chain principal e lista as demais em <see cref="ParsedFrameCapture.OtherTargets"/>.
/// </summary>
public static class PresentMonCsvParser
{
    private sealed class Cols
    {
        private readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);
        public Cols(string[] header) { for (int i = 0; i < header.Length; i++) _map.TryAdd(header[i].Trim(), i); }
        public int Find(params string[] names) { foreach (var n in names) if (_map.TryGetValue(n, out var i)) return i; return -1; }
        public bool Has(string n) => _map.ContainsKey(n);
    }

    public static ParsedFrameCapture ParseFile(string path, PresentMonParseOptions? options = null)
    {
        using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(sr, options);
    }

    public static ParsedFrameCapture ParseText(string csv, PresentMonParseOptions? options = null) => Parse(new StringReader(csv), options);

    /// <summary>Detecta o layout a partir dos nomes de coluna.</summary>
    public static PresentMonCsvLayout DetectLayout(IReadOnlyCollection<string> header)
    {
        var set = new HashSet<string>(header.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        if (set.Contains("FrameTime") && (set.Contains("CPUBusy") || set.Contains("CPUStartTime") || set.Contains("CPUStartQPC"))) return PresentMonCsvLayout.V2Metrics;
        if (set.Contains("MsBetweenPresents") && (set.Contains("MsCPUBusy") || set.Contains("MsBetweenAppStart") || set.Contains("PresentRuntime") || set.Contains("MsBetweenSimulationStart")))
            return PresentMonCsvLayout.V2Default;
        if (set.Contains("msBetweenPresents")) return PresentMonCsvLayout.V1;
        return PresentMonCsvLayout.Unknown;
    }

    public static ParsedFrameCapture Parse(TextReader reader, PresentMonParseOptions? options = null)
    {
        options ??= new PresentMonParseOptions();
        string? headerLine = reader.ReadLine();
        while (headerLine is not null && string.IsNullOrWhiteSpace(headerLine)) headerLine = reader.ReadLine();
        if (headerLine is null) throw new FormatException("CSV vazio.");
        var header = SplitCsv(headerLine.TrimStart('﻿'));
        var layout = DetectLayout(header);
        if (layout == PresentMonCsvLayout.Unknown)
            throw new FormatException("Cabeçalho não reconhecido como CSV do PresentMon (faltam msBetweenPresents/FrameTime).");
        var c = new Cols(header);

        int iApp = c.Find("Application");
        int iPid = c.Find("ProcessID");
        int iSc = c.Find("SwapChainAddress");
        int iRuntime = c.Find("PresentRuntime", "Runtime");
        int iSync = c.Find("SyncInterval");
        int iTear = c.Find("AllowsTearing");
        int iMode = c.Find("PresentMode");
        int iDropped = c.Find("Dropped");
        string ftName;
        int iFt;
        if ((iFt = c.Find("MsBetweenPresents")) >= 0) ftName = header[iFt];
        else if ((iFt = c.Find("FrameTime")) >= 0) ftName = header[iFt];
        else { iFt = c.Find("MsBetweenAppStart"); ftName = iFt >= 0 ? header[iFt] : ""; }
        if (iFt < 0) throw new FormatException("Coluna de tempo de quadro não encontrada.");

        int iDisp = c.Find("MsBetweenDisplayChange", "DisplayedTime");
        int iUntil = c.Find("MsUntilDisplayed");
        int iCpuBusy = c.Find("MsCPUBusy", "CPUBusy");
        int iCpuWait = c.Find("MsCPUWait", "CPUWait");
        int iGpuBusy = c.Find("MsGPUBusy", "GPUBusy");
        int iGpuWait = c.Find("MsGPUWait", "GPUWait");
        int iDispLat = c.Find("DisplayLatency");
        int iClick = c.Find("MsClickToPhotonLatency", "ClickToPhotonLatency");

        // Coluna de tempo: (índice, escala conhecida em ms por unidade ou NaN = autodetectar, relativa ao início?)
        (int idx, double scale, bool relative, string name) time = (-1, double.NaN, true, "");
        foreach (var (n, s, rel) in new (string, double, bool)[]
                 {
                     ("TimeInSeconds", 1000, true), ("TimeInMs", 1, true), ("CPUStartTimeInMs", 1, true), ("CPUStartTimeInSeconds", 1000, true),
                     ("CPUStartTime", double.NaN, true), ("CPUStartQPCTime", 1, false), ("CPUStartQPCTimeInMs", 1, false),
                     ("TimeInQPC", double.NaN, false), ("CPUStartQPC", double.NaN, false), ("QPCTime", double.NaN, false),
                 })
        {
            int i = c.Find(n);
            if (i >= 0) { time = (i, s, rel, header[i]); break; }
        }

        // 1ª passada: agrupa linhas por (app, pid, swapchain)
        var groups = new Dictionary<(string app, int pid, string sc), List<string[]>>();
        int skipped = 0;
        string? line;
        int maxIdx = new[] { iApp, iPid, iSc, iFt }.Max();
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsv(line);
            if (f.Length <= maxIdx || f.Length < header.Length - 8) { skipped++; continue; }
            string app = iApp >= 0 ? f[iApp] : "";
            int pid = iPid >= 0 && int.TryParse(f[iPid], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            string sc = iSc >= 0 ? NormalizeSwapChain(f[iSc]) : "";
            var key = (app, pid, sc);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<string[]>();
            list.Add(f);
        }
        if (groups.Count == 0) throw new FormatException("O CSV não contém quadros.");

        var warnings = new List<string>();
        var candidates = groups.AsEnumerable();
        if (options.ProcessName is { Length: > 0 } pn)
        {
            string want = pn.Trim();
            candidates = candidates.Where(g => string.Equals(g.Key.app, want, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(Path.GetFileNameWithoutExtension(g.Key.app), Path.GetFileNameWithoutExtension(want), StringComparison.OrdinalIgnoreCase));
            if (!candidates.Any()) throw new FormatException($"O processo '{want}' não aparece no CSV.");
        }
        else
        {
            var known = candidates.Where(g => !g.Key.app.Equals("<unknown>", StringComparison.OrdinalIgnoreCase) && !g.Key.app.Equals("<error>", StringComparison.OrdinalIgnoreCase));
            if (known.Any()) candidates = known;
        }
        if (options.SwapChain is { Length: > 0 } wantSc)
        {
            var nsc = NormalizeSwapChain(wantSc);
            var bySc = candidates.Where(g => g.Key.sc == nsc).ToList();
            if (bySc.Count > 0) candidates = bySc; else warnings.Add($"Swap chain {wantSc} não encontrada; usando a principal.");
        }
        // Processo com mais quadros, depois sua swap chain com mais quadros.
        var target = candidates
            .GroupBy(g => (g.Key.app, g.Key.pid))
            .OrderByDescending(pg => pg.Sum(x => x.Value.Count))
            .First()
            .OrderByDescending(x => x.Value.Count)
            .First();

        var others = groups.Where(g => !g.Key.Equals(target.Key))
            .Select(g => new CsvTargetInfo(g.Key.app, g.Key.pid, g.Key.sc, g.Value.Count))
            .OrderByDescending(o => o.Frames).ToList();
        int sameProcOtherSc = others.Count(o => o.Application == target.Key.app && o.ProcessId == target.Key.pid);
        if (sameProcOtherSc > 0)
            warnings.Add($"O processo tem {sameProcOtherSc + 1} swap chains; analisada a com mais quadros ({target.Value.Count}).");

        var rows = target.Value;
        var raw = new FrameRecord[rows.Count];
        var rawTime = new double[rows.Count];
        string? runtime = null;
        for (int r = 0; r < rows.Count; r++)
        {
            var f = rows[r];
            runtime ??= Get(f, iRuntime);
            double ft = Num(f, iFt);
            double disp = Num(f, iDisp);
            double until = Num(f, iUntil);
            bool? displayed = null;
            if (iDropped >= 0)
            {
                var d = Get(f, iDropped);
                if (d is "1") displayed = false; else if (d is "0") displayed = true;
            }
            else if (layout == PresentMonCsvLayout.V2Metrics && iDisp >= 0) displayed = double.IsFinite(disp);
            else if (iUntil >= 0) displayed = double.IsFinite(until);

            bool? tear = Get(f, iTear) switch { "1" => true, "0" => false, _ => null };
            int? sync = int.TryParse(Get(f, iSync), NumberStyles.Integer, CultureInfo.InvariantCulture, out var si) ? si : null;
            string? mode = Get(f, iMode);
            if (mode is not null) mode = string.Intern(mode);
            // v1: msBetweenDisplayChange = 0 para quadros descartados → NaN
            if (displayed == false) disp = double.NaN;
            raw[r] = new FrameRecord(double.NaN, ft, disp, until, Num(f, iCpuBusy), Num(f, iCpuWait), Num(f, iGpuBusy), Num(f, iGpuWait),
                Num(f, iDispLat), Num(f, iClick), displayed, mode, tear, sync);
            rawTime[r] = time.idx >= 0 ? Num(f, time.idx) : double.NaN;
        }

        string timeSource = AssignTimes(raw, rawTime, time.scale, time.relative, time.name, warnings);
        return new ParsedFrameCapture(layout, target.Key.app, target.Key.pid, target.Key.sc, runtime, raw, others, skipped, ftName, timeSource, warnings);
    }

    /// <summary>
    /// Converte a coluna de tempo para ms relativos. A escala é verificada contra a soma dos frametimes; se a coluna
    /// faltar ou não bater, usa a soma acumulada dos frametimes (documentado em TimeSource).
    /// </summary>
    private static string AssignTimes(FrameRecord[] frames, double[] rawTime, double knownScale, bool relative, string colName, List<string> warnings)
    {
        int n = frames.Length;
        double sumFt = 0;
        for (int i = 1; i < n; i++) if (double.IsFinite(frames[i].FrameTimeMs)) sumFt += frames[i].FrameTimeMs;
        int first = Array.FindIndex(rawTime, double.IsFinite);
        int last = Array.FindLastIndex(rawTime, double.IsFinite);
        bool monotonic = first >= 0 && last > first;
        if (monotonic)
            for (int i = first + 1; i <= last; i++)
                if (double.IsFinite(rawTime[i]) && rawTime[i] < rawTime[i - 1] - 1e-9 && double.IsFinite(rawTime[i - 1])) { monotonic = false; break; }

        double scale = double.NaN;
        if (monotonic && sumFt > 0)
        {
            double span = rawTime[last] - rawTime[first];
            double observed = span > 0 ? sumFt / span : double.NaN;
            double[] options = double.IsFinite(knownScale) ? [knownScale] : [1000, 1, 1e-4, 1e-6];
            foreach (var s in options)
                if (double.IsFinite(observed) && observed / s is > 0.67 and < 1.5) { scale = s; break; }
            if (double.IsNaN(scale) && double.IsFinite(knownScale) && n < 5) scale = knownScale; // poucos quadros: confia no nome
        }

        if (double.IsFinite(scale))
        {
            double origin = relative ? 0 : rawTime[first];
            double lastT = double.NaN;
            for (int i = 0; i < n; i++)
            {
                double t = double.IsFinite(rawTime[i]) ? (rawTime[i] - origin) * scale
                    : double.IsFinite(lastT) && double.IsFinite(frames[i].FrameTimeMs) ? lastT + frames[i].FrameTimeMs : lastT;
                frames[i] = frames[i] with { TimeMs = t };
                lastT = t;
            }
            return $"coluna {colName}";
        }

        if (colName.Length > 0) warnings.Add($"A coluna de tempo '{colName}' não é consistente com os frametimes; usando soma acumulada dos frametimes.");
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsFinite(frames[i].FrameTimeMs)) acc += frames[i].FrameTimeMs;
            frames[i] = frames[i] with { TimeMs = acc };
        }
        return "soma acumulada dos frametimes";
    }

    private static string NormalizeSwapChain(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        return s;
    }

    private static string? Get(string[] f, int i) => i >= 0 && i < f.Length ? f[i].Trim() : null;

    private static double Num(string[] f, int i)
    {
        if (i < 0 || i >= f.Length) return double.NaN;
        var s = f[i].AsSpan().Trim();
        if (s.Length == 0 || s.Equals("NA", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : double.NaN;
    }

    /// <summary>Divide uma linha CSV (suporta campos entre aspas com "" escapado).</summary>
    public static string[] SplitCsv(string line)
    {
        if (line.IndexOf('"') < 0) return line.Split(',');
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else q = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }
}
