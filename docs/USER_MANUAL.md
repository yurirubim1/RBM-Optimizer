# RBM Optimizer — Manual do usuário

## Instalação

- **Instalador (recomendado):** execute `RBM-Optimizer-Setup.msi`. O RBM é instalado em `C:\Program Files\RBM Optimizer` e cria atalhos no Menu Iniciar e na Área de Trabalho.
- **Portátil:** copie a pasta `dist\app` e execute `RBMOptimizer.exe`. O agente elevado (`RBM.Agent.exe`) precisa ficar na mesma pasta.
- **Requisitos:** Windows 10 (2004+) ou Windows 11, x64. O runtime .NET já está incluído, não é preciso instalar nada.

## Conceitos importantes

- **O RBM roda sem privilégios.** Quando uma ação exige administrador, o Windows mostra o UAC e o RBM inicia o *agente elevado*. O estado aparece na barra inferior ("Modo padrão" ou "Agente elevado conectado"); clique ali para conectar ou encerrar o agente.
- **Nada é aplicado sem confirmação.** A confirmação cresce com o risco: ajustes de risco alto e experimentais exigem digitar **APLICAR**.
- **Toda alteração é reversível:**
  - o estado anterior é salvo antes de qualquer escrita;
  - a alteração é verificada por releitura;
  - se algo falhar, o que já foi aplicado é desfeito automaticamente;
  - você pode reverter a qualquer momento em **Histórico e Restauração**.
- **Evidência acima de promessa.** Cada ajuste tem um grau:
  - **A**: benefício demonstrado;
  - **B**: plausível, depende do sistema;
  - **C**: experimental;
  - **D**: sem benefício demonstrado;
  - **E**: obsoleto ou contraindicado. É só documentado e não pode ser aplicado.

  Só A e B recebem recomendação.
- **Sem dados falsos.** Quando um sensor não está disponível, o RBM mostra "indisponível" e o motivo, nunca um número inventado.

## Páginas

O menu começa no **modo simples** (Painel, Otimizar em 1 clique, Saúde do PC, Jogos, Limpeza, Espaço, Histórico e Configurações). Ligue **Todas as ferramentas** no rodapé da barra lateral para ver as demais páginas.

| Página | Para que serve |
|---|---|
| **Otimizar em 1 clique** | Escolha **Leve**, **Média** ou **Pesada**. A prévia mostra exatamente o que muda neste PC (só entram ajustes compatíveis que ainda não estão certos). Inclui limpeza, ponto de restauração, retirada de launchers e atualizadores da inicialização (nunca apps de sincronização ou de mensagens) e, na Pesada em desktops, o plano Alto desempenho. O botão *Desfazer esta otimização* volta tudo. |
| **Painel** | Resumo em linguagem simples ("Tudo certo", "Atenção", "Precisa de cuidado"), o que fazer agora (com botões), atalhos, uso em tempo real, saúde por área e dados do computador. |
| **Otimizar em 1 clique** | Escolha **Leve**, **Média** ou **Pesada**. A prévia mostra exatamente o que muda neste PC (só entram ajustes compatíveis e que ainda não estão certos). Inclui limpeza, ponto de restauração, retirada de launchers/atualizadores da inicialização (nunca apps de sincronização ou mensagens) e, na Pesada em desktops, o plano Alto desempenho. O botão *Desfazer esta otimização* volta tudo. |
| **Saúde do PC** | Executa as regras sobre a telemetria dos últimos minutos. Cada achado traz evidência, causa provável, confiança, solução, riscos e o botão *Investigar*. Para resultados mais confiáveis, use o PC normalmente por alguns minutos antes. |
| **Monitor em tempo real** | Gráficos de todos os componentes, com intervalo de amostragem configurável e botão de pausa. |
| **Processador / Placa de vídeo / Memória RAM / Discos** | Detalhes de cada componente: topologia, clocks, sensores, processos que mais consomem, configuração de memória e pagefile, saúde e TRIM dos discos. |
| **Energia e Temperatura** | Planos de energia (com troca), temperaturas e indicador de limitação (throttling). |
| **Espaço em disco** | Mapa visual (treemap) das pastas que mais ocupam espaço. Clique numa pasta para entrar nela. Para pastas protegidas, use a varredura como administrador. |
| **Otimizações** | Catálogo pesquisável com o estado real lido do sistema, os detalhes técnicos completos e as opções. Por padrão mostra só os **Recomendados**; a aba **Laboratório (experimental)** traz os ajustes experimentais, aplicados um a um, sem "aplicar tudo". |
| **Perfis prontos** | Conjuntos de ajustes por cenário (Segura, Equilibrado, Competitivo, Workstation, Laptop, perfis de jogos e Personalizado), com prévia exata do que muda. O perfil **Padrão** reverte tudo o que o RBM alterou. |
| **Jogos e FPS** | Jogos detectados (Steam, Epic, GOG, Xbox e outros), preferência de GPU por jogo e captura de FPS/frametime via PresentMon (sem injeção). Mostra 1% low, stutter, overlays e comparativos de sessão. |
| **Limpeza** | Análise (simulação) e limpeza segura de temporários, caches de shader, despejos de falha e lixeira. Itens em uso são ignorados. |
| **Latência** | DPC/ISR por driver (ETW, requer agente), timer resolution, dispositivos de entrada, teste de polling rate e modo de interrupção (MSI). |
| **Rede e internet** | Adaptadores (velocidade negociada, propriedades avançadas), ping/jitter/perda em vários destinos, rota segmentada (local/roteador/provedor/remoto) e DNS. |
| **Testes de desempenho** | Testes reproduzíveis de CPU, memória, armazenamento e responsividade, com estatística (média, mediana, IC 95%, CV) e comparação antes/depois. |
| **Processos** | Gerenciador avançado (árvore, assinatura, prioridade, afinidade) e regras automáticas por executável. |
| **Inicialização e Serviços** | Programas de inicialização, serviços, tarefas agendadas e outros locais (no estilo do Autoruns). Desativa de forma reversível, nunca exclui. |
| **Drivers** | Inventário, dispositivos com problema, driver store, backup/verificação, instalação, troca de versão, links oficiais e orientação para NVIDIA/DDU. |
| **Histórico e desfazer** | Todas as alterações com valores antes e depois, reversão individual ou em lote, detecção de alterações externas, pontos de restauração, exportação/importação de backup e auditoria. |
| **Configurações** | Idioma (pt-BR/inglês), intervalo de amostragem, sensores, pontos de restauração automáticos, privacidade e licenças. |

## Fluxo recomendado para otimizar com segurança

1. Use o PC normalmente por 5–10 minutos com o RBM aberto e depois execute o **Diagnóstico**.
2. Resolva primeiro o que é **causa** (temperatura, espaço, memória, drivers, instabilidade), antes de qualquer ajuste fino.
3. Rode um **Benchmark** ou uma **captura de FPS** para ter uma linha de base.
4. Aplique ajustes individualmente ou um perfil, reinicie se for pedido e repita a medição.
5. Compare: o RBM só aponta ganho quando a diferença é estatisticamente significativa. Se não houver ganho, reverta no Histórico.

## Sensores de temperatura da CPU

As temperaturas de GPU funcionam sem nada adicional. As temperaturas e a potência da CPU (principalmente AMD) exigem o driver **PawnIO**, que você instala por conta própria se quiser, e o RBM executado com o agente elevado. O RBM não instala drivers de kernel.

## Privacidade

O RBM não tem telemetria, não exige conta e não usa servidores próprios. Ele só acessa a internet quando você executa testes de rede, baixa o PresentMon (do GitHub oficial da Intel, com verificação de assinatura) ou abre links de fabricantes.
