# SysMaintenanceHub

Dashboard visual para manutenção e atualização do Windows — construído em **WPF / .NET 8**, com temas dark/light, execução silenciosa (sem prompts intermediários) e logs em tempo real.

> Interface reativa, MVVM, single-file executable. Toda ação decidida pelo usuário no painel; a partir daí o app roda sem perguntar mais nada.

---

## Sumário

- [Recursos](#recursos)
- [Screenshots do dashboard](#screenshots-do-dashboard)
- [Como rodar (3 opções)](#como-rodar-3-opções)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Módulos e o que cada um faz](#módulos-e-o-que-cada-um-faz)
- [Compilar do código-fonte](#compilar-do-código-fonte)
- [Onde ficam os logs e a configuração](#onde-ficam-os-logs-e-a-configuração)
- [Solução de problemas](#solução-de-problemas)
- [Roadmap](#roadmap)
- [Licença](#licença)

---

## Recursos

| Recurso | Detalhes |
|---|---|
| **Windows Update** | Lista KBs pendentes (via `PSWindowsUpdate`) e destaca os de segurança. Instala com `-AcceptAll -Confirm:$false` — sem prompts. |
| **Winget** | `winget upgrade --all --silent --accept-source-agreements --accept-package-agreements` |
| **Limpeza de disco** | TEMP, Prefetch, Windows Update, caches de dev (npm/pip/NuGet/Go/Yarn), caches de navegador (Brave/Chrome/Edge) + `cleanmgr /sagerun` com todos os presets ativados. |
| **Desfragmentação / TRIM** | `Optimize-Volume -Analyze` para relatório e `-Defrag` para executar. |
| **Startup Apps** | Lê registros `HKCU/HKLM Run` + `StartupApproved`, mostra ativos vs. desativados, permite alternar. |
| **Logs** | UI ao vivo (aba **Logs**) + arquivo Serilog rotativo diário. |
| **Tema Dark / Light** | Alterna e persiste em `%LOCALAPPDATA%\SysMaintenanceHub\theme.cfg`. |
| **Auto-elevação** | Manifesto `requireAdministrator` — UAC único no início. |

---

## Screenshots do dashboard

O dashboard mostra 6 cartões-KPI acima e uma área com abas:

```
┌─────────────────────────────────────────────────────────────────────────┐
│ ⚡ SysMaintenanceHub                            [Executar tudo] [Tema] │
│    Pronto.                                              ┌─────────────┐│
│ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░ 62%          │             ││
├─────────────────────────────────────────────────────────┴─────────────┤│
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐    ││
│ │ Windows  │ │ Segurança│ │ Apps     │ │ Limpeza  │ │ Startup  │    ││
│ │ Update   │ │          │ │ (winget) │ │ estimada │ │          │    ││
│ │   12     │ │    4     │ │   23     │ │ 8.4 GB   │ │   17     │    ││
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘    ││
├───────────────────────────────────────────────────────────────────────┤│
│ [Windows Update] [Apps (winget)] [Limpeza] [Startup] [Discos] [Logs]  ││
│                                                                        ││
│  KB       Título                        Categoria    Tamanho          ││
│  KB5051987 2026-06 Cumulative Update... Security     342.5 MB          ││
│  ...                                                                   ││
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Como rodar (3 opções)

O projeto entrega **três formas** de rodar, dependendo da sua preferência:

### 1. Duplo clique — `SysMaintenanceHub.bat`
Wrapper batch com auto-elevação (UAC). Localiza o EXE em `bin\publish\` ou em `%ProgramFiles%`.

```
Duplo clique em SysMaintenanceHub.bat
```

### 2. PowerShell — `SysMaintenanceHub.ps1`
Mesmo comportamento, mas com opção `-Build` para compilar antes:

```powershell
.\SysMaintenanceHub.ps1                  # roda o EXE existente
.\SysMaintenanceHub.ps1 -Build           # publica primeiro e depois roda
```

### 3. EXE direto — `SysMaintenanceHub.exe`
Após publicar, o executável fica em:
```
src\SysMaintenanceHub\bin\publish\SysMaintenanceHub.exe
```
Duplo clique dispara UAC automaticamente.

---

## Requisitos

- **Windows 10 20H1+ ou Windows 11**
- Se rodar o EXE `-SelfContained`: **nenhum runtime extra** (já vem embutido, ~75 MB)
- Se rodar framework-dependent: **.NET 8 Desktop Runtime** ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **PowerShell 5.1+** (nativo no Windows)
- **Winget** (nativo no Windows 11, instalável via Microsoft Store no Windows 10)
- Módulo **PSWindowsUpdate** — instalado automaticamente na primeira execução (requer internet)

---

## Instalação

### Opção A — Instalador Inno Setup (recomendado para distribuir)

Requer [Inno Setup 6](https://jrsoftware.org/isdl.php) instalado.

```powershell
# 1. Publique o EXE self-contained
.\scripts\publish.ps1 -SelfContained

# 2. Compile o instalador
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\setup.iss

# 3. Distribua o setup gerado
# installer\output\SysMaintenanceHub-Setup-1.0.0.exe
```

O instalador oferece:
- Escolha do diretório (`%ProgramFiles%\SysMaintenanceHub` por padrão)
- Ícone no menu iniciar / desktop (opcional)
- Iniciar no login do Windows (opcional)
- Registro em **Adicionar/Remover Programas**
- Desinstalador com limpeza do `%LOCALAPPDATA%\SysMaintenanceHub`

### Opção B — Instalação sem Inno (PowerShell)

```powershell
# como Administrador
.\scripts\install-local.ps1
```

Faz o mesmo trabalho: copia para `%ProgramFiles%\SysMaintenanceHub`, cria atalho e registra a desinstalação.

### Opção C — Portable (sem instalar)

Basta rodar o `SysMaintenanceHub.exe` diretamente da pasta `bin\publish\`. Nada é gravado fora de `%LOCALAPPDATA%\SysMaintenanceHub\`.

---

## Estrutura do projeto

```
SysMaintenanceHub/
├── SysMaintenanceHub.sln
├── SysMaintenanceHub.bat            # launcher .bat
├── SysMaintenanceHub.ps1            # launcher .ps1
├── README.md                        # este manual
├── .gitignore
│
├── src/SysMaintenanceHub/
│   ├── SysMaintenanceHub.csproj     # .NET 8 WPF, single-file, publish self-contained
│   ├── app.manifest                 # UAC requireAdministrator + PerMonitorV2
│   ├── App.xaml(.cs)                # bootstrap Serilog + tema
│   ├── MainWindow.xaml(.cs)         # dashboard
│   │
│   ├── Themes/
│   │   ├── DarkTheme.xaml
│   │   ├── LightTheme.xaml
│   │   └── Common.xaml              # cards, botões, progress bar
│   │
│   ├── ViewModels/
│   │   └── MainViewModel.cs         # ObservableProperty + RelayCommand
│   │
│   ├── Services/
│   │   ├── PowerShellRunner.cs      # exec PS com stream de stdout
│   │   ├── WindowsUpdateService.cs  # PSWindowsUpdate silencioso
│   │   ├── WingetService.cs         # winget upgrade
│   │   ├── CleanupService.cs        # TEMP + caches + cleanmgr
│   │   ├── DefragService.cs         # Optimize-Volume
│   │   ├── StartupAppsService.cs    # Registry Run + StartupApproved
│   │   ├── ThemeManager.cs          # Dark/Light persistente
│   │   └── AdminGuard.cs
│   │
│   ├── Models/Models.cs             # records para os dados exibidos
│   ├── Converters/Converters.cs     # BoolToVisibility, MB→GB, etc.
│   └── Assets/                      # ícones e imagens
│
├── installer/
│   └── setup.iss                    # Inno Setup 6 (PT-BR + EN)
│
├── scripts/
│   ├── publish.ps1                  # dotnet publish (framework/self-contained)
│   └── install-local.ps1            # instalador PowerShell alternativo
│
└── docs/
```

---

## Módulos e o que cada um faz

### 1. Windows Update (`WindowsUpdateService.cs`)
- Garante instalação silenciosa do módulo `PSWindowsUpdate` na primeira execução.
- `Get-WindowsUpdate -MicrosoftUpdate` para listar (KB, título, categoria, tamanho).
- `Install-WindowsUpdate -MicrosoftUpdate -AcceptAll -IgnoreReboot -Confirm:$false` para instalar tudo.
- Sem popup, sem "y/n". Deixa o usuário decidir sobre reboot depois.

### 2. Aplicativos (`WingetService.cs`)
- `winget upgrade` com parser regex do output tabular do winget.
- `winget upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity --include-unknown` para atualizar tudo.

### 3. Limpeza (`CleanupService.cs`)
Enumera automaticamente estes alvos (se existirem):
- User `%TEMP%`, `C:\Windows\Temp`, `C:\Windows\Prefetch`
- `C:\Windows\SoftwareDistribution\Download`, `C:\Windows.old`
- Cache do Brave, Chrome, Edge
- `%USERPROFILE%\.nuget`, `%LOCALAPPDATA%\npm-cache`, `%LOCALAPPDATA%\pip\Cache`, `%USERPROFILE%\go\pkg`
- Delivery Optimization Cache

Depois roda `cleanmgr /sagerun:99` com **26 presets** ativados (Update Cleanup, Delivery Optimization Files, Windows Defender, etc.).

Grava data da última limpeza em `%LOCALAPPDATA%\SysMaintenanceHub\last_cleanup.txt`.

### 4. Discos (`DefragService.cs`)
- Para cada drive fixo, roda `Optimize-Volume -DriveLetter X -Analyze` e extrai o `% fragmentado`.
- `Optimize-Volume -Defrag` para HDD tradicional; SSDs recebem TRIM (comportamento nativo do cmdlet).
- Lê data do último defrag do registro `HKLM\SOFTWARE\Microsoft\Dfrg\Statistics`.

### 5. Startup Apps (`StartupAppsService.cs`)
- Enumera 5 chaves do registro: HKCU/HKLM `Run` e `RunOnce`, incluindo `WOW6432Node`.
- Cruza com `Explorer\StartupApproved\Run` para descobrir quais estão ativos.
- Botão **Alternar** grava byte 0x02 (ativo) ou 0x03 (desativo) sem precisar `regedit`.

### 6. Logs (`Serilog` + `App.xaml.cs`)
- Console + arquivo rotativo diário (14 dias de retenção).
- Arquivo em `%LOCALAPPDATA%\SysMaintenanceHub\logs\app-YYYYMMDD.log`.
- Formato: `[timestamp INF] SourceContext :: mensagem`.
- Captura exceções não tratadas do AppDomain e do dispatcher WPF.
- UI: aba **Logs** com auto-scroll, mantém últimas 2.000 linhas em memória.

### 7. Tema (`ThemeManager.cs`)
- Dark é o default. Alternar via botão 🌓 no header.
- Cores em `DarkTheme.xaml` / `LightTheme.xaml` (paleta Tailwind-like).
- Persiste em `%LOCALAPPDATA%\SysMaintenanceHub\theme.cfg`.

---

## Compilar do código-fonte

```powershell
# clone
git clone https://github.com/fernandogssilva/SysMaintenanceHub.git
cd SysMaintenanceHub

# restore + build
dotnet restore
dotnet build -c Release

# publish (opção A: EXE portátil, sem dependência)
.\scripts\publish.ps1 -SelfContained
# saída: src\SysMaintenanceHub\bin\publish\SysMaintenanceHub.exe  (~75 MB)

# publish (opção B: menor, precisa .NET 8 Desktop Runtime no destino)
.\scripts\publish.ps1
# saída: src\SysMaintenanceHub\bin\publish\SysMaintenanceHub.exe  (~10 MB)
```

---

## Onde ficam os logs e a configuração

Tudo em `%LOCALAPPDATA%\SysMaintenanceHub\`:

```
%LOCALAPPDATA%\SysMaintenanceHub\
├── logs\
│   ├── app-20260718.log
│   ├── app-20260717.log
│   └── ...
├── theme.cfg               # "Dark" ou "Light"
└── last_cleanup.txt        # ISO-8601 da última limpeza executada
```

**Nada é gravado fora dessa pasta.** Desinstalar remove tudo.

---

## Solução de problemas

### O EXE abre e fecha na hora
Provavelmente falha do .NET Desktop Runtime (se usou publish framework-dependent). Verifique:
```powershell
dotnet --list-runtimes | Select-String "WindowsDesktop.App 8"
```
Instale o [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) ou use `.\scripts\publish.ps1 -SelfContained`.

### "PSWindowsUpdate não pôde ser instalado"
Verifique conexão + permissão. Instale manualmente uma vez:
```powershell
Install-Module PSWindowsUpdate -Force -AllowClobber -Scope AllUsers
```

### "winget não é reconhecido como comando"
Instale o **App Installer** pela Microsoft Store, ou baixe o `.msixbundle` do [github/microsoft/winget-cli/releases](https://github.com/microsoft/winget-cli/releases).

### Fragmentação sempre 0%
No Windows moderno, SSDs reportam 0% e recebem TRIM automaticamente — é o comportamento correto. HDDs vão mostrar valores reais.

### Aba Startup não mostra tudo
Apps modernos (Store apps, tarefas agendadas de startup) não usam Registry `Run`. Para inspeção completa, cruze com **Task Scheduler** e `shell:startup`.

---

## Roadmap

- [ ] Ícone `.ico` customizado
- [ ] Gráfico histórico de espaço em disco (LiveCharts)
- [ ] Suporte a tarefas agendadas do Task Scheduler no Startup Apps
- [ ] Exportar relatório em HTML/PDF
- [ ] Notificações do Windows quando updates críticos aparecerem
- [ ] Assinatura de código para distribuição corporativa

---

## Licença

MIT © 2026 [Fernando Silva](https://github.com/fernandogssilva) / DataSec
