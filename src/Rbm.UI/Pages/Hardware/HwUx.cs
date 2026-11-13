using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.Pages.Hardware;

/// <summary>Nível de status exibido em chips e indicadores (linguagem simples).</summary>
public enum UxLevel { Unknown, Ok, Info, Warn, Danger }

public static class UxLevels
{
    public static UxLevel Worst(params UxLevel[] levels)
    {
        var w = UxLevel.Unknown;
        foreach (var l in levels)
            if (Rank(l) > Rank(w)) w = l;
        return w;
    }

    private static int Rank(UxLevel l) => l switch { UxLevel.Danger => 4, UxLevel.Warn => 3, UxLevel.Info => 2, UxLevel.Ok => 1, _ => 0 };

    public static Brush Fg(UxLevel l) => Hw.B(l switch
    {
        UxLevel.Ok => "B.Success", UxLevel.Info => "B.Info", UxLevel.Warn => "B.Warning", UxLevel.Danger => "B.Danger", _ => "B.TextMuted",
    });

    public static Brush Bg(UxLevel l) => Hw.B(l switch
    {
        UxLevel.Ok => "B.SuccessSoft", UxLevel.Info => "B.AccentSoft", UxLevel.Warn => "B.WarningSoft", UxLevel.Danger => "B.DangerSoft", _ => "B.Surface3",
    });

    public static string Glyph(UxLevel l) => l switch
    {
        UxLevel.Ok => "", UxLevel.Info => "", UxLevel.Warn => "", UxLevel.Danger => "", _ => "",
    };
}

/// <summary>Palavras e limiares usados no resumo das páginas (mesmos critérios do Painel e do Diagnóstico).</summary>
public static class Words
{
    /// <summary>Carga (CPU/GPU): mesmos limites do Painel.</summary>
    public static string Load(double pct) => pct < 30 ? "tranquilo" : pct < 70 ? "uso moderado" : pct < 90 ? "uso alto" : "uso intenso";
    /// <summary>Frase "está …" para o resumo (masculino: processador; feminino: placa de vídeo).</summary>
    public static string LoadState(double pct, bool fem = false) => pct < 30 ? (fem ? "tranquila" : "tranquilo") : pct < 70 ? "em uso moderado" : pct < 90 ? "em uso alto" : "trabalhando a todo vapor";

    /// <summary>Ocupação de memória: mesmos limites do Painel.</summary>
    public static string Mem(double pct) => pct < 60 ? "folgada" : pct < 80 ? "confortável" : pct < 90 ? "ficando cheia" : "quase cheia";
    public static UxLevel MemLevel(double pct) => pct < 85 ? UxLevel.Ok : pct < 95 ? UxLevel.Warn : UxLevel.Danger;

    /// <summary>Temperatura de CPU/GPU: normal &lt; 70, morna &lt; 85, quente ≥ 85 (diagnóstico alerta a partir de 85–90 °C).</summary>
    public static string Temp(double c) => c < 70 ? "normal" : c < 85 ? "morna" : c < 95 ? "quente" : "muito quente";
    public static UxLevel CpuTempLevel(double c) => c < 90 ? UxLevel.Ok : c < 100 ? UxLevel.Warn : UxLevel.Danger;
    public static UxLevel GpuTempLevel(double c) => c < 85 ? UxLevel.Ok : c < 90 ? UxLevel.Warn : UxLevel.Danger;
    public static UxLevel HotspotLevel(double c) => c < 100 ? UxLevel.Ok : c < 110 ? UxLevel.Warn : UxLevel.Danger;

    /// <summary>Temperatura de SSD/HD: até ~60 °C é normal; acima de 70 °C muitos SSDs reduzem velocidade.</summary>
    public static string DiskTemp(double c) => c < 60 ? "normal" : c < 70 ? "morna" : "quente";
}

/// <summary>Estado do banner de resumo (Hero) de uma página.</summary>
public sealed partial class HeroState : ObservableObject
{
    [ObservableProperty] private string _headline = "Coletando dados…";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _context = "";
    [ObservableProperty] private string _chipTip = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText), nameof(ChipForeground), nameof(ChipBackground), nameof(ChipGlyph))]
    private UxLevel _level = UxLevel.Unknown;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChipText))]
    private string? _chipOverride;

    public string ChipText => ChipOverride ?? Level switch
    {
        UxLevel.Ok => "Tudo certo", UxLevel.Info => "Informação", UxLevel.Warn => "Atenção", UxLevel.Danger => "Problema", _ => "Indisponível",
    };
    public Brush ChipForeground => UxLevels.Fg(Level);
    public Brush ChipBackground => UxLevels.Bg(Level);
    public string ChipGlyph => UxLevels.Glyph(Level);

    public void Set(UxLevel level, string headline, string detail, string? chip = null)
    {
        Level = level; Headline = headline; Detail = detail; ChipOverride = chip;
    }
}

/// <summary>Métrica em destaque: rótulo + valor + significado (com cor do nível).</summary>
public sealed partial class Fact : ObservableObject
{
    public Fact(string label, string tip = "") { Label = label; Tip = tip; }

    public string Label { get; }
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _note = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoteBrush))]
    private UxLevel _level = UxLevel.Unknown;

    public Brush NoteBrush => UxLevels.Fg(Level);

    public void Set(string value, string note, UxLevel level)
    {
        Value = value; Note = note; Level = level;
    }

    public void Unavailable(string note = "sem leitura")
    {
        Value = Hw.Unavailable; Note = note; Level = UxLevel.Unknown;
    }
}

/// <summary>Navegação entre páginas a partir dos botões de ação (chaves do PageRegistry).</summary>
public static class HwNav
{
    public static IRelayCommand<string> Go { get; } = new RelayCommand<string>(k =>
    {
        if (!string.IsNullOrWhiteSpace(k)) Hw.Main.Navigate(k);
    });
}
