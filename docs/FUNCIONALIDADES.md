# Funcionalidades do Curupira

Documento completo do que a plataforma faz, módulo por módulo.

![Mapa de funções](images/mapa-de-funcoes.svg)

---

## Índice

- [Painel](#painel)
- [Segurança](#segurança)
- [Limpeza](#limpeza)
- [Desempenho](#desempenho)
- [Sistema](#sistema)
- [O que o Curupira deliberadamente NÃO faz](#o-que-o-curupira-deliberadamente-não-faz)

---

## Painel

![Painel](images/painel.svg)

### Pontuação de saúde (0–100)

Um número único que resume o estado da máquina, com anel colorido e rótulo.

| Faixa | Rótulo | Cor |
|---|---|---|
| 90–100 | Excelente | verde |
| 75–89 | Bom | verde-menta |
| 55–74 | Requer atenção | âmbar |
| 0–54 | Crítico | vermelho |

**Como é calculado** — parte de 100 e desconta:

| Fator | Desconto | Teto |
|---|---|---|
| Vulnerabilidade crítica | −8 cada | −35 |
| Demais vulnerabilidades | −1 cada | −10 |
| Update de segurança pendente | −5 cada | −20 |
| Update comum pendente | −2 cada | −8 |
| App desatualizado | −0,5 cada | −8 |
| Disco recuperável > 10 GB | −10 | — |
| RAM acima de 90% | −10 | — |
| Mais de 20 apps na inicialização | −6 | — |
| Drives com > 10% de fragmentação | −6 | — |
| Build defasada do catálogo oficial | −8 | — |

### Cards explicativos

Cada uma das 8 áreas responde três perguntas, em vez de mostrar só um número:

1. **O que foi encontrado** — o valor e a severidade
2. **O que é** — a explicação técnica
3. **O que melhora no computador** — o benefício concreto

Cada card tem um botão que leva direto à seção correspondente.

---

## Segurança

### Vulnerabilidades

Duas fontes combinadas numa única lista:

**CVEs da Microsoft (MSRC)**
- Consulta a API oficial `api.msrc.microsoft.com/cvrf/v3.0`
- Últimos 3 meses de publicações
- Extrai severidade, CVSS BaseScore e a KB que corrige
- Filtra pelo seu produto (Windows 10 ou 11)
- Cruza com `Get-HotFix` — mostra só o que ainda falta
- Cache de 30 minutos para não refazer o download

**Auditoria local — 12 verificações nativas**

| # | Verificação | O que detecta |
|---|---|---|
| 1 | Windows Defender | Antivírus desligado, proteção em tempo real off, Tamper Protection off, assinaturas com mais de 7 dias |
| 2 | Firewall | Perfis Domínio, Privado ou Público desativados |
| 3 | BitLocker | Drive de sistema sem criptografia |
| 4 | LSA Protection | `RunAsPPL` desativado — permite dump de credenciais |
| 5 | Credential Guard | VBS inativo |
| 6 | SMBv1 | Habilitado — vetor de EternalBlue e WannaCry |
| 7 | LM Hash | `NoLmHash` não configurado |
| 8 | RDP + NLA | RDP exposto sem Network Level Authentication (BlueKeep) |
| 9 | AutoLogon | Credenciais em texto claro no registro |
| 10 | Conta Guest | Habilitada |
| 11 | Secure Boot e TPM | Desativados |
| 12 | PowerShell v2 | Instalado — permite burlar o script-block logging |

Extras quando o [Sysinternals](https://learn.microsoft.com/sysinternals/downloads/sysinternals-suite) está no PATH:
- **Sigcheck** — binários sem assinatura em `System32`
- **Autorunsc** — enumeração completa de inicialização com detecção de não-assinados

**Correção em lote**
- Caixa de seleção por linha
- Seleção rápida: todas · só críticas · corrigíveis automaticamente
- Aplica em sequência com estado visual por linha: `Na fila → Aplicando → Corrigido / Falhou / Manual`
- Distingue KB real (instala sozinho via `PSWindowsUpdate`) de configuração local (mostra o cmdlet para você executar)
- Overlay animado durante a aplicação, com alvo atual e progresso

### Windows Update

- Lista KBs pendentes com título, categoria e tamanho
- Destaca as de segurança
- Instala com `-AcceptAll -IgnoreReboot -Confirm:$false` — sem prompts
- Instalação por KB específica, para correção pontual

### Catálogo Microsoft

- Lê a build instalada de `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` (`CurrentBuild` + `UBR`)
- Baixa e interpreta a página oficial de release-health da Microsoft
- Compara e informa se você está atrasado
- Link direto para o Microsoft Update Catalog

Útil porque o Windows Update às vezes demora semanas para entregar uma atualização em máquinas específicas.

---

## Limpeza

### Limpeza de disco

Alvos mapeados automaticamente:

| Categoria | Caminhos |
|---|---|
| Temporários | `%TEMP%`, `C:\Windows\Temp`, Prefetch |
| Windows Update | `SoftwareDistribution\Download`, `Windows.old` |
| Navegadores | Cache de Brave, Chrome e Edge |
| Desenvolvimento | npm, pip, NuGet, Go modules |
| Sistema | Delivery Optimization Cache |

Depois executa `cleanmgr /sagerun` com 26 presets ativados.

**Proteção contra symlink** — a enumeração ignora reparse points. Sem isso, uma junction plantada dentro de `%TEMP%` apontando para a pasta Documentos faria o app, rodando elevado, apagar arquivos legítimos.

### Regras da comunidade (winapp2)

Suporte à base [winapp2.ini](https://github.com/MoscaDotTo/Winapp2) — milhares de regras de limpeza mantidas há mais de 15 anos. **O CCleaner 7 abandonou esse formato.**

Como funciona:

```
1. Baixa a base oficial (~4 MB, milhares de entradas)
2. DETECTA quais se aplicam a esta máquina
     Detect=HKCU\Software\...       (a chave existe?)
     DetectFile=%LocalAppData%\...  (o caminho existe?)
   → milhares de regras viram algumas dezenas relevantes
3. MEDE só as que passaram, em paralelo
4. Você escolhe o que limpar
```

A etapa 2 é o que torna isso viável — sem ela seriam milhares de varreduras de disco inúteis.

Recursos do parser:
- Expansão de 22 variáveis (`%LocalAppData%`, `%CommonAppData%`, `%WinDir%`…)
- Curingas no meio do caminho (`User Data\*\Cache`)
- Flags `RECURSE` e `REMOVESELF`
- Categorias traduzidas via `LangSecRef`
- Avisos declarados pela própria entrada

### Registro do Windows

7 verificações, todas com **backup `.reg` obrigatório antes de qualquer remoção**:

| Categoria | O que detecta | Severidade |
|---|---|---|
| Regra de firewall órfã | Libera um `.exe` que não existe mais | **Segurança** |
| Inicialização quebrada | `Run`/`RunOnce` chamando arquivo ausente | Desperdício |
| Caminhos de aplicativo | `App Paths` apontando para `.exe` inexistente | Resíduo |
| Programa desinstalado | Entrada fantasma em Adicionar/Remover | Resíduo |
| Histórico de uso | `RunMRU` e `TypedPaths` (privacidade) | Resíduo |
| Cache de nomes | `MUICache` com referências mortas | Resíduo |
| Extensão sem destino | `.ext` apontando para ProgID não registrado | Resíduo |

**Se o backup falhar, a remoção é abortada.** Restaurar é duplo clique no `.reg` salvo em `%LOCALAPPDATA%\Curupira\registry-backups\`.

---

## Desempenho

### Memória RAM

**Liberação**
- `EmptyWorkingSet` em cada processo — devolve páginas retidas
- Purge da standby list via `NtSetSystemInformation`, com `SeProfileSingleProcessPrivilege`
- Pula processos críticos do sistema para não causar travamento

**Gerenciador por aplicativo**
- Top 25 por consumo, **agrupados por nome** — navegadores abrem dezenas de processos
- **Compactar** — devolve memória sem fechar o aplicativo
- **Encerrar** — `CloseMainWindow()` primeiro, permitindo salvar; `Kill` após 2,5 s
- Processos protegidos (`csrss`, `lsass`, `wininit`, `services`, `dwm`, `explorer`) com o botão desabilitado — encerrar qualquer um derruba a sessão ou causa tela azul

### Discos

- `Optimize-Volume -Analyze` por unidade fixa
- HDD recebe desfragmentação; SSD recebe TRIM
- Data da última otimização lida do registro

### Inicialização

- Lê `HKCU` e `HKLM`, chaves `Run` e `RunOnce`, incluindo `WOW6432Node`
- Cruza com `StartupApproved` para saber o que está ativo
- Alternar sem abrir o `regedit`

---

## Sistema

### Aplicativos (winget)

- `winget upgrade` para listar o que tem versão nova
- `winget upgrade --all --silent --accept-source-agreements --accept-package-agreements`

### Pré-requisitos

Verifica tudo que o app precisa e instala o que for instalável:

| Componente | Instalável pelo Curupira |
|---|---|
| Provedor NuGet | Sim |
| Repositório PSGallery (confiável) | Sim |
| PSWindowsUpdate 2.2.1.5 (versão fixada) | Sim |
| Winget | Não — vem da Microsoft Store |
| Cmdlets nativos do Windows | Não — dependem da edição |

Quando um cmdlet não existe na edição instalada (BitLocker no Windows Home, por exemplo), o app **avisa e desliga a função correspondente**, em vez de deixá-la falhar em silêncio depois.

Também roda automaticamente durante a instalação.

### Varredura ao vivo

Cada módulo mostra em tempo real: estado, contador de itens encontrados, barra de progresso, atividade atual, último item encontrado e a ação sugerida ao concluir.

### Logs

- Painel ao vivo com rolagem automática
- Arquivo em `%LOCALAPPDATA%\Curupira\logs\`, rotação diária, 14 dias de retenção
- Caminhos sanitizados: `C:\Users\<nome>\` vira `C:\Users\%USER%\` para você poder compartilhar logs sem expor identidade

---

## O que o Curupira deliberadamente NÃO faz

Tão importante quanto a lista de recursos:

| Não faz | Por quê |
|---|---|
| **Prometer que limpar registro acelera o Windows** | Não acelera. O registro tem milhões de chaves indexadas em memória; remover algumas centenas não muda boot nem consumo de RAM. O app declara isso na própria interface. |
| **Varrer CLSID em massa, fontes ou Shared DLLs** | É exatamente onde os "otimizadores" de mercado quebram instalações. |
| **Secure delete / sobrescrita multi-passe** | Em SSD com wear leveling e TRIM, sobrescrever não garante apagamento. É teatro de segurança. |
| **Telemetria, analytics ou crash reports** | Nenhum dado sai da máquina. As únicas chamadas de rede são para fontes públicas da Microsoft. |
| **Bundleware, upsell ou popup de "premium"** | A categoria inteira é conhecida por isso. |
| **Desativar serviços do Windows** | Ganho marginal, risco alto de quebrar funcionalidade. |

---

## Requisitos

- Windows 10 versão 20H1 ou superior, ou Windows 11
- x64
- PowerShell 5.1 (nativo)
- Privilégio de administrador — o `.exe` solicita elevação sozinho
- Internet para: MSRC, release-health, base winapp2 e instalação do PSWindowsUpdate

---

## Onde ficam os dados

Tudo em `%LOCALAPPDATA%\Curupira\`:

```
logs\                 registro de operações, rotação diária
registry-backups\     backups .reg antes de cada limpeza de registro
.ps\                  scripts temporários, com ACL restrita a Administradores
winapp2.ini           base de regras da comunidade
theme.cfg             tema escolhido
last_cleanup.txt      data da última limpeza
```

Desinstalar remove tudo.
