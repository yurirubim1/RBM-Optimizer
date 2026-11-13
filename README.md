# RBM Optimizer

Diagnóstico, monitoramento, limpeza e otimização para Windows 10/11 (64 bits), com interface em português.

**Tudo é real e reversível:** cada ajuste mostra o que muda, é conferido depois de aplicado e pode ser desfeito com um clique. Nada é apresentado como ganho sem medição.

## Baixar e instalar
1. Abra a página **[Releases](../../releases/latest)** e baixe `RBM-Optimizer-Setup.msi`.
2. Execute o instalador. Como o instalador não tem assinatura digital paga, o Windows pode mostrar "O Windows protegeu o computador": clique em **Mais informações → Executar assim mesmo**.
3. Abra o **RBM Optimizer** pelo menu Iniciar.

Requisitos: Windows 10 ou 11, 64 bits. Não precisa instalar .NET (já vem junto).

## Otimizar em 1 clique
Na página **Otimizar em 1 clique**, escolha um nível. Antes de confirmar, o app mostra a lista exata do que vai mudar no seu PC.

| Nível | O que faz |
|---|---|
| **Leve** | Apaga temporários e relatórios de falha, limpa o cache de miniaturas, liga a manutenção do SSD e o Modo de Jogo, desliga a gravação automática do Xbox Game Bar. Não precisa reiniciar. |
| **Média** (recomendada) | Tudo da Leve + cria ponto de restauração, impede launchers de jogos e atualizadores (Steam, Epic, Adobe, Spotify…) de abrirem com o Windows, limpa temporários do sistema e aplica ajustes com benefício comprovado. |
| **Pesada** | Tudo da Média + plano de energia Alto desempenho (em desktops), menos economia de energia em USB/PCIe e ajustes para jogos competitivos. Em notebooks mantém o plano do fabricante. |

Ao final, o botão **Desfazer esta otimização** volta tudo como estava (exceto os arquivos temporários apagados). O histórico completo fica em **Histórico e desfazer**.

O menu começa no modo simples; ligue **Todas as ferramentas** na barra lateral para ver monitoramento, processos, drivers, rede, latência, testes de desempenho e o catálogo completo de ajustes.

## Desenvolver
```powershell
# Pré-requisito: SDK .NET 10 (o build usa %LOCALAPPDATA%\Microsoft\dotnet)
powershell -ExecutionPolicy Bypass -File build/build.ps1          # catálogo + testes + publicação + MSI
```
Git Bash: `source build/env.sh && dotnet build Rbm.slnx`. Testes: rode o executável `Rbm.Tests.exe` (xUnit v3).

A saída de build fica em `%LOCALAPPDATA%\RbmOptimizer.Build`. Builds de desenvolvimento abrem a janela fora da tela; para vê-la, defina `RBM_DEV_VISIBLE=1`.

## Documentação
[Manual do usuário](docs/USER_MANUAL.md) · [Arquitetura](docs/ARCHITECTURE.md) · [Funcionalidades](docs/FEATURES.md) · [Limitações](docs/LIMITATIONS.md) · [Pesquisa de técnicas](docs/TWEAK_RESEARCH.md) · [Catálogo](docs/TWEAK_CATALOG.md)
