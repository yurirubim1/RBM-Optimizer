namespace Rbm.Latency;

/// <summary>Textos pt-BR compartilhados (limitações e esclarecimentos exibidos junto aos resultados).</summary>
public static class LatencyTexts
{
    /// <summary>Esclarecimento central: o que DPC/ISR é e o que NÃO é.</summary>
    public const string NotInputLag =
        "DPC/ISR medem quanto tempo drivers passam em rotinas de kernel de alta prioridade. Isso NÃO é a latência de entrada " +
        "(clique-até-imagem). Software rodando no próprio PC não consegue medir a latência mouse-até-fóton: isso exige hardware " +
        "externo (ex.: NVIDIA LDAT, monitor com Reflex Latency Analyzer ou câmera de alta velocidade filmando mouse e tela).";

    public static readonly IReadOnlyList<string> CaptureLimitations = new[]
    {
        NotInputLag,
        "A duração de cada execução vem dos eventos ETW de kernel (timestamp do evento − InitialTime); a resolução é a do relógio da sessão (QPC, < 1 µs).",
        "O módulo é identificado pelo endereço da rotina DPC/ISR. Drivers que usam o framework KMDF (Wdf01000.sys) ou timers do kernel (ntoskrnl.exe) podem aparecer no lugar do driver realmente responsável.",
        "Relacionar um driver a picos é associação, não causa confirmada. Confirme comparando capturas com o dispositivo desativado ou o driver atualizado.",
        "A própria captura ETW adiciona uma pequena sobrecarga; picos no primeiro segundo podem ser causados pelo rundown inicial da sessão.",
        "A latência de agendamento de threads (interrupt-to-process, reportada pelo LatencyMon) não é medida aqui.",
        "Resultados variam com a carga: capture durante o uso que apresenta o problema (ex.: áudio estalando, jogo travando).",
    };

    public static readonly IReadOnlyList<string> CounterLimitations = new[]
    {
        "Contadores de desempenho são médias por intervalo: não mostram picos individuais nem qual driver é responsável. Use a captura DPC/ISR (requer elevação) para isso.",
        "Valores de referência são indicativos; variam com hardware, carga e atividade de rede/áudio/vídeo.",
        NotInputLag,
    };

    public const string PollingRateNote =
        "Mede a taxa de relatórios entregues ao Windows enquanto o dispositivo está em movimento/uso (WM_INPUT), não a latência de clique. " +
        "Mouses só enviam relatórios quando há movimento: mova o mouse de forma contínua e rápida durante a medição.";

    public const string RawInputTimingNote =
        "Os timestamps são obtidos (QueryPerformanceCounter) quando a mensagem WM_INPUT é processada; atrasos da fila de mensagens da UI " +
        "adicionam variação. Para taxas ≥ 2000 Hz, processe WM_INPUT em uma thread dedicada (janela message-only) para evitar agrupamento.";
}
