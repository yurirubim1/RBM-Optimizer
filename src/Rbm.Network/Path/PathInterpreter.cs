namespace Rbm.Network.Path;

/// <summary>
/// Interpretação pura das medições por salto. Regra central: roteadores intermediários tratam ICMP com baixa
/// prioridade e limitam a taxa de respostas "TTL excedido". Perda (ou atraso) que aparece em um salto mas NÃO
/// aparece nos saltos seguintes não é perda real do tráfego — é o roteador economizando CPU. Só é perda real
/// a que começa em um salto e persiste até o destino.
/// </summary>
public static class PathInterpreter
{
    /// <summary>Atraso mínimo (ms) acima dos saltos seguintes para classificar como resposta lenta do roteador.</summary>
    public const double ControlPlaneDelayMinMs = 15.0;

    public static PathAnalysis Interpret(string target, string? destinationAddress, bool reached, int rounds,
        IReadOnlyList<HopMeasurement> hops, string? gatewayAddress, int ispPublicHops = 2, double lossThresholdPercent = 2.0)
    {
        var ordered = hops.OrderBy(h => h.Ttl).ToList();
        var segments = HopClassifier.Classify(ordered.Select(h => (h.Address, h.HostName, h.IsDestination)).ToList(), gatewayAddress, ispPublicHops);
        var notes = new List<string>();

        bool Responsive(HopMeasurement h) => h.Stats.Received > 0;
        double Loss(HopMeasurement h) => double.IsNaN(h.Stats.LossPercent) ? 100 : h.Stats.LossPercent;

        var last = ordered.LastOrDefault(Responsive);
        double endLoss = last is null ? 100 : Loss(last);
        if (!reached) notes.Add("O destino não respondeu ao traceroute; a perda fim a fim é a do último salto que respondeu.");

        var interp = new HopInterpretation[ordered.Count];
        var notesPerHop = new string[ordered.Count];
        bool originFound = false;

        for (int i = 0; i < ordered.Count; i++)
        {
            var h = ordered[i];
            var later = ordered.Skip(i + 1).Where(Responsive).ToList();
            if (!Responsive(h))
            {
                interp[i] = HopInterpretation.NoIcmpResponse;
                notesPerHop[i] = later.Count > 0
                    ? "Não responde a ICMP, mas os saltos seguintes respondem: comportamento comum, não indica problema."
                    : "Sem resposta (roteadores finais ou destino podem bloquear ICMP).";
                continue;
            }

            double loss = Loss(h);
            bool lossNotPropagated = later.Any(l => Loss(l) < loss / 2 && Loss(l) < loss - lossThresholdPercent / 2);

            if (loss >= lossThresholdPercent && lossNotPropagated)
            {
                interp[i] = HopInterpretation.IcmpRateLimitedLoss;
                notesPerHop[i] = $"Perda de {loss:0.#}% só neste salto (não se repete adiante): limitação de ICMP no roteador, não é perda real.";
                continue;
            }

            if (loss >= lossThresholdPercent && endLoss >= lossThresholdPercent)
            {
                interp[i] = originFound ? HopInterpretation.RealLossPropagated : HopInterpretation.RealLossOrigin;
                notesPerHop[i] = originFound
                    ? $"Perda de {loss:0.#}% herdada de salto anterior."
                    : h.IsDestination && later.Count == 0
                        ? $"Perda de {loss:0.#}% aparece só no destino: perda real perto do servidor ou o servidor limita ICMP."
                        : $"Perda de {loss:0.#}% começa aqui e persiste até o fim: origem provável de perda real.";
                originFound = true;
                continue;
            }

            if (later.Count > 0 && !double.IsNaN(h.Stats.MedianMs))
            {
                double minLater = later.Select(l => l.Stats.MedianMs).Where(m => !double.IsNaN(m)).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(minLater) && h.Stats.MedianMs - minLater >= ControlPlaneDelayMinMs && h.Stats.MedianMs > minLater * 1.3)
                {
                    interp[i] = HopInterpretation.ControlPlaneDelay;
                    notesPerHop[i] = $"Atraso só neste salto ({h.Stats.MedianMs:0} ms vs {minLater:0} ms adiante): o roteador responde ICMP devagar; não afeta o tráfego que passa por ele.";
                    continue;
                }
            }

            interp[i] = HopInterpretation.Normal;
            notesPerHop[i] = loss > 0 && loss < lossThresholdPercent ? $"Perda pequena ({loss:0.#}%), dentro da variação normal da amostra." : "";
        }

        var analyzed = ordered.Select((h, i) => new AnalyzedHop(h, HopClassifier.ScopeOf(h.Address), segments[i], interp[i], notesPerHop[i])).ToList();

        // Possível NAT duplo: salto privado RFC1918 logo após o roteador.
        var privAfterRouter = analyzed.Skip(1).TakeWhile(a => a.Segment <= PathSegment.Isp)
            .FirstOrDefault(a => a.Scope == AddressScope.Private);
        if (privAfterRouter is not null)
            notes.Add($"Salto privado {privAfterRouter.Hop.Address} após o roteador: pode ser rede interna do provedor ou NAT duplo (modem e roteador ambos roteando).");
        if (analyzed.Any(a => a.Scope == AddressScope.Cgnat))
            notes.Add("Endereço CGNAT (100.64.0.0/10) no caminho: o provedor compartilha IP público entre clientes (CGNAT).");
        notes.Add("A divisão provedor/internet é heurística (primeiros saltos públicos e domínio do DNS reverso); sem consulta de ASN.");

        var summaries = BuildSegments(analyzed);
        var originHop = analyzed.FirstOrDefault(a => a.Interpretation == HopInterpretation.RealLossOrigin);
        PathSegment? originSeg = originHop?.Segment;
        string verdict = Verdict(originHop, endLoss, lossThresholdPercent, analyzed);
        return new PathAnalysis(target, destinationAddress, reached, rounds, analyzed, summaries, originSeg, endLoss, verdict, notes);
    }

    private static IReadOnlyList<SegmentSummary> BuildSegments(IReadOnlyList<AnalyzedHop> hops)
    {
        var result = new List<SegmentSummary>();
        double prevMedian = 0;
        foreach (var seg in new[] { PathSegment.Router, PathSegment.Isp, PathSegment.Remote, PathSegment.Destination })
        {
            var inSeg = hops.Where(h => h.Segment == seg).ToList();
            if (inSeg.Count == 0) continue;
            var responsive = inSeg.Where(h => h.Hop.Stats.Received > 0).ToList();
            // Representativo: último salto "confiável" (não limitado/lento); senão, o último que respondeu.
            var rep = responsive.LastOrDefault(h => h.Interpretation is not (HopInterpretation.IcmpRateLimitedLoss or HopInterpretation.ControlPlaneDelay))
                      ?? responsive.LastOrDefault();
            if (rep is null)
            {
                result.Add(new SegmentSummary(seg, HopClassifier.SegmentLabel(seg), inSeg[0].Hop.Ttl, inSeg[^1].Hop.Ttl, null,
                    double.NaN, double.NaN, double.NaN, double.NaN, false, "Nenhum salto deste segmento respondeu a ICMP."));
                continue;
            }
            var st = rep.Hop.Stats;
            double inc = st.MedianMs - prevMedian;
            string note = rep.Interpretation switch
            {
                HopInterpretation.IcmpRateLimitedLoss => "Único salto disponível limita ICMP: a perda mostrada não é real.",
                HopInterpretation.ControlPlaneDelay => "Único salto disponível responde ICMP devagar: o RTT mostrado é pessimista.",
                _ => "",
            };
            if (inc < 0) { note = (note + " RTT menor que o segmento anterior (variação/ICMP do salto anterior); acréscimo tratado como 0.").Trim(); inc = 0; }
            bool real = rep.Interpretation is HopInterpretation.RealLossOrigin or HopInterpretation.RealLossPropagated;
            result.Add(new SegmentSummary(seg, HopClassifier.SegmentLabel(seg), inSeg[0].Hop.Ttl, inSeg[^1].Hop.Ttl, rep.Hop.Ttl,
                st.MedianMs, inc, st.JitterMs, st.LossPercent, real, note));
            if (!double.IsNaN(st.MedianMs)) prevMedian = Math.Max(prevMedian, st.MedianMs);
        }
        return result;
    }

    private static string Verdict(AnalyzedHop? origin, double endLoss, double threshold, IReadOnlyList<AnalyzedHop> hops)
    {
        bool anyFake = hops.Any(h => h.Interpretation is HopInterpretation.IcmpRateLimitedLoss or HopInterpretation.ControlPlaneDelay);
        if (origin is null)
        {
            return endLoss < threshold
                ? "Sem perda real no caminho." + (anyFake ? " Perdas/atrasos vistos em saltos intermediários são limitação de ICMP nos roteadores e não afetam seu tráfego." : "")
                : "Perda no fim do caminho, mas sem origem clara (dados insuficientes).";
        }
        return origin.Segment switch
        {
            PathSegment.Router => $"Perda real começa já no seu roteador (salto {origin.Hop.Ttl}): problema na rede local (Wi-Fi, cabo, roteador ou placa de rede).",
            PathSegment.Isp => $"Perda real começa na rede do provedor (salto {origin.Hop.Ttl}, {origin.Hop.Address}): a sua rede local está limpa; o problema está no provedor.",
            PathSegment.Remote => $"Perda real começa fora do provedor (salto {origin.Hop.Ttl}, {origin.Hop.Address}): trânsito/internet, fora do seu controle.",
            PathSegment.Destination => "Perda aparece só no servidor de destino: perda perto do servidor ou o servidor limita ICMP (teste também via TCP).",
            _ => $"Perda real a partir do salto {origin.Hop.Ttl} (segmento indeterminado).",
        };
    }
}
