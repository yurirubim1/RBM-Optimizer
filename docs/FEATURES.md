# RBM Optimizer — Relatório de funcionalidades (v0.1.0)

Legenda: ✅ implementado e verificado nesta máquina · 🧪 implementado e testado por unidade, mas sem execução elevada real · ❌ não implementado (ver [LIMITATIONS.md](LIMITATIONS.md))

## Plataforma
- ✅ Solução .NET 10 com 16 projetos + testes. **577 testes automatizados passando.**
- ✅ UI WPF com tema próprio (dark grafite, azul elétrico, ciano e violeta), 21 páginas, gráficos `OnRender`, toasts, modais de confirmação proporcionais ao risco e barra de status (monitoramento, privilégio, tarefas, reinício pendente).
- ✅ SQLite local (histórico, benchmarks, sessões, diagnósticos, snapshots, rede, auditoria) e logs Serilog.
- 🧪 Agente elevado com allow-list, canal autenticado (ACL + PID + diretório + token) e auditoria.
- ✅ Build Release self-contained e instalador **MSI** (WiX 5): `dist/RBM-Optimizer-Setup.msi`.

## Por seção do escopo
| Seção | Estado |
|---|---|
| 3. Dashboard (identificação, tempo real, índice com metodologia/pesos/confiança) | ✅ |
| 4. Diagnóstico (22 regras + drivers; os 10 campos por achado; associação ≠ causa) | ✅ |
| 5. Catálogo: 75 técnicas com manifesto completo, leitura real, aplicação, verificação e reversão | ✅ leitura · 🧪 aplicação elevada |
| 5.6 Advanced Tweaks Lab (individual, sem "aplicar tudo", grau E bloqueado) | ✅ |
| 6. Gaming (bibliotecas, perfil por jogo, preferência de GPU, PresentMon, 1%/0,1% low, stutter, correlação, overlays) | ✅ (captura validada pelo subagente com PresentMon 2.6.0) |
| 7. Latência (DPC/ISR via ETW, indicadores, timer, entrada, polling rate, MSI) · rede | 🧪 ETW · ✅ demais |
| 8. Drivers (inventário SetupAPI, problemas, driver store, backup/verificação, instalação/troca, links oficiais, NVIDIA/DDU orientação) | ✅ leitura/backup · 🧪 instalação |
| 9. Processos (lista/árvore, assinatura, prioridade/afinidade, regras por executável, impacto) | ✅ · 🧪 ações elevadas |
| 10. Inicialização e serviços (Run/RunOnce, pastas, serviços, tarefas, shell extensions, drivers de boot; desativação reversível) | ✅ · 🧪 HKLM/serviços |
| 11. Benchmarks (CPU ST/MT, memória, armazenamento, responsividade; estatística; antes/depois) | ✅ (executados uma vez em tamanho reduzido) |
| 12. Perfis (Padrão, Segura, Balanceada, Competitivo, Workstation, Notebook, perfis de jogo, Personalizado; prévia de diferenças) | ✅ prévia · 🧪 aplicação elevada |
| 13. Restauração e histórico (journal, lote, divergência, backup exportar/importar validado, pontos de restauração) | ✅ · 🧪 ponto de restauração |
| 14. Recomendações (impacto, confiança, risco, esforço, reversibilidade, compatibilidade) | ✅ |
| 15. Pesquisa de técnicas (graus A–E, fontes) | ✅ `docs/TWEAK_RESEARCH.md` |
| Extra: Análise de espaço (treemap squarified, navegação por pastas, varredura elevada opcional) | ✅ · 🧪 elevada |
| Extra: Limpeza segura (simulação, categorias com risco, nunca segue junctions) | ✅ · 🧪 categorias admin |
| SMART/NVMe detalhado, sensores de CPU sem PawnIO, benchmark sintético de GPU, OC/undervolt | ❌ |

## Documentação
`docs/ARCHITECTURE.md` · `docs/USER_MANUAL.md` · `docs/TWEAK_RESEARCH.md` · `docs/TWEAK_CATALOG.md` · `docs/LIMITATIONS.md` · `src/Rbm.*/README.md` (por módulo)
