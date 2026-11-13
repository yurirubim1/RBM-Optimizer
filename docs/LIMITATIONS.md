# RBM Optimizer — Limitações conhecidas (v0.1.0)

Lista honesta do que **não** está pronto, do que **não** foi exercitado e do que não é possível fazer.

## 1. Caminhos elevados ainda não executados de ponta a ponta

Durante o desenvolvimento, o shell não estava elevado e o UAC não foi aprovado. Por isso, as operações abaixo foram implementadas e tiveram a validação e as recusas testadas por testes unitários, mas **ainda não rodaram com privilégio real**:

- aplicar e reverter tweaks de HKLM, energia, serviços, BCD e tarefas pelo agente;
- criar ponto de restauração;
- captura DPC/ISR por ETW e requisições de timer (`powercfg /energy`);
- instalar, trocar e exportar drivers pelo agente;
- encerrar processos elevados e definir sua prioridade; alterar inicialização em HKLM, serviços e tarefas;
- limpeza das categorias administrativas (WER, `C:\Windows\Temp`);
- varredura de espaço elevada;
- escrita de propriedades avançadas do adaptador de rede.

Os procedimentos de teste manual estão nos READMEs de cada módulo (`src/Rbm.*/README.md`).

## 2. Sensores e hardware

- **Temperatura e potência da CPU** ficam indisponíveis: exigem o driver PawnIO (o RBM não instala drivers de kernel) **e** leitura elevada. Os sensores rodam no processo da UI, que não é elevado; levar a leitura de sensores para o agente ainda não foi implementado.
- **SMART/NVMe detalhado** (operação `storage.health`) não foi implementado. Fica disponível só a saúde agregada do Windows (MSFT_PhysicalDisk).
- **Memória comprimida** não é exibida (não há contador PDH padrão).
- **Uso de GPU por processo** é somado entre todos os adaptadores.
- **Wi-Fi** não foi testado em hardware real (esta máquina não tem Wi-Fi). O parser e a API WLAN foram testados com fixtures.
- **Afinidade de processos** cobre apenas o grupo de processadores 0 (até 64 threads lógicas).

## 3. Motor de otimizações

- **Opção "padrão" de energia:** usa os valores genéricos do Windows. Planos de fabricantes (OEM) podem ter padrões diferentes, e então o estado aparece como "personalizado". A reversão continua exata, porque usa o snapshot real e não a opção "padrão".
- **`DirectXUserGlobalSettings`:** a escrita grava a string inteira. Outros tokens que o usuário tenha nessa string são restaurados apenas na reversão.
- **Fora do catálogo aplicável:** técnicas que dependem de comandos (netsh, MMAgent, `powercfg -h`, fsutil por volume) ou de caminhos por dispositivo/interface (modo MSI, Nagle por interface) estão **só documentadas** em `docs/TWEAK_RESEARCH.md`. O modo MSI tem inspeção somente leitura na página Latência; as propriedades de placa de rede são editadas na página Rede.
- **Preferências de interface** (ex.: atraso de menu) só entram em vigor depois de sair e entrar na conta (não há broadcast de `SystemParametersInfo`).

## 4. Agente elevado

- **Uma operação por vez.** Operações longas, como a varredura de espaço elevada ou a captura ETW, bloqueiam outras chamadas ao agente até terminar.
- **Cancelar uma chamada encerra o agente** para manter o canal consistente. A próxima operação pede o UAC de novo.
- **Sem progresso detalhado** em operações elevadas longas; só o tempo decorrido é exibido.

## 5. Funcionalidades fora do escopo ou limitadas por design

- **Benchmark sintético de GPU:** não implementado, porque não há metodologia multi-fabricante confiável sem um motor de renderização próprio. Para medir GPU, use a captura de FPS via PresentMon (página Gaming).
- **Latência física "mouse-to-photon":** impossível medir por software. Exige equipamento externo (NVIDIA LDAT, Reflex Analyzer ou câmera de alta velocidade).
- **Overclock, undervolt e curvas de ventoinha:** não implementados. O RBM direciona para as ferramentas oficiais (AMD Software, NVIDIA App, Intel).
- **Atualização de drivers:** não baixa drivers automaticamente. Indica a fonte oficial do fabricante e a busca por Hardware ID no Microsoft Update Catalog.
- **NVCleanstall e DDU:** só orientação e análise somente leitura de pacotes. Nunca modifica pacotes assinados e nunca executa remoções profundas.
- **Cache do Delivery Optimization:** não há API nativa documentada, então a página abre Configurações › Armazenamento.
- **Análise de espaço:** não exclui arquivos (por segurança). Para remoção segura, use a página Limpeza.

## 6. Interface e distribuição

- **Inglês parcial:** navegação, cabeçalhos e textos comuns. O conteúdo técnico (catálogo, diagnósticos e módulos) está em pt-BR. Mudar o idioma exige reiniciar o app.
- **Código e instalador sem assinatura:** sem certificado de assinatura de código, o Windows SmartScreen pode avisar na primeira execução.
- **Tamanho:** a distribuição self-contained ocupa ~164 MB, porque o runtime .NET vai embutido.
- **Pontos de restauração:** o Windows limita a 1 a cada 24 h por padrão. O RBM detecta e informa quando isso acontece.
- **Nome da pasta do repositório:** ainda é "Apex Optimizer", mas o código, os executáveis e os dados já usam "RBM".
