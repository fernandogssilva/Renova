# Instalador alternativo (sem Inno Setup): copia o publish para %ProgramFiles%
# e cria atalho + registro de desinstalação. Rode como Administrador.

#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\Renova"
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$publish   = Join-Path $root 'src\Renova\bin\publish'
$exeName   = 'Renova.exe'

if (-not (Test-Path (Join-Path $publish $exeName))) {
    Write-Host "Publish ausente. Rodando publish primeiro..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

Write-Host "Instalando em $InstallDir..." -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null }
Copy-Item -Path (Join-Path $publish '*') -Destination $InstallDir -Recurse -Force

# Atalhos: Menu Iniciar + Desktop do usuário
$startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Renova"
if (-not (Test-Path $startMenu)) { New-Item -ItemType Directory -Force -Path $startMenu | Out-Null }

$WshShell = New-Object -ComObject WScript.Shell

# Menu Iniciar
$lnkStart = $WshShell.CreateShortcut((Join-Path $startMenu 'Renova.lnk'))
$lnkStart.TargetPath       = Join-Path $InstallDir $exeName
$lnkStart.WorkingDirectory = $InstallDir
$lnkStart.IconLocation     = Join-Path $InstallDir $exeName
$lnkStart.Description      = 'Renova - Seu Windows em dia'
$lnkStart.Save()

# Desktop (perfil do usuário que executou o instalador; se elevado, usa o Public)
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not (Test-Path $desktop)) { $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory') }
$lnkDesk = $WshShell.CreateShortcut((Join-Path $desktop 'Renova.lnk'))
$lnkDesk.TargetPath       = Join-Path $InstallDir $exeName
$lnkDesk.WorkingDirectory = $InstallDir
$lnkDesk.IconLocation     = Join-Path $InstallDir $exeName
$lnkDesk.Description      = 'Renova - Seu Windows em dia'
$lnkDesk.Save()
Write-Host "Atalho no desktop: $desktop\Renova.lnk" -ForegroundColor Cyan

# Registro de desinstalação
$regBase = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Renova'
if (-not (Test-Path $regBase)) { New-Item -Path $regBase -Force | Out-Null }
Set-ItemProperty $regBase 'DisplayName'     'Renova'
Set-ItemProperty $regBase 'DisplayVersion'  '2.0.0'
Set-ItemProperty $regBase 'Publisher'       'CyberThrust'
Set-ItemProperty $regBase 'InstallLocation' $InstallDir
Set-ItemProperty $regBase 'UninstallString' "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty $regBase 'NoModify'        1
Set-ItemProperty $regBase 'NoRepair'        1

# Uninstall script
@"
#Requires -RunAsAdministrator
Remove-Item -Recurse -Force '$InstallDir' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force '$startMenu' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force '$env:LOCALAPPDATA\Renova' -ErrorAction SilentlyContinue
Remove-Item -Path '$regBase' -Recurse -Force -ErrorAction SilentlyContinue
"@ | Set-Content -Path (Join-Path $InstallDir 'uninstall.ps1') -Encoding UTF8

# ===========================================================
#  Pré-requisitos: instala o que o Renova precisa para rodar
# ===========================================================
Write-Host ""
Write-Host "Verificando pre-requisitos..." -ForegroundColor Cyan

$pinnedPSWU = '2.2.1.5'

# --- Provedor NuGet ---
if (-not (Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue)) {
    Write-Host "  Instalando provedor NuGet..." -ForegroundColor Yellow
    try {
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers -ErrorAction Stop | Out-Null
        Write-Host "    OK" -ForegroundColor Green
    } catch { Write-Host "    FALHA: $($_.Exception.Message)" -ForegroundColor Red }
} else { Write-Host "  Provedor NuGet: ja presente" -ForegroundColor DarkGray }

# --- PSGallery confiavel ---
try {
    if (-not (Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue)) {
        Register-PSRepository -Default -ErrorAction Stop
    }
    if ((Get-PSRepository -Name PSGallery).InstallationPolicy -ne 'Trusted') {
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction Stop
        Write-Host "  PSGallery marcado como confiavel" -ForegroundColor Green
    } else { Write-Host "  PSGallery: ja confiavel" -ForegroundColor DarkGray }
} catch { Write-Host "  FALHA PSGallery: $($_.Exception.Message)" -ForegroundColor Red }

# --- PSWindowsUpdate na versao fixada ---
$have = Get-Module -ListAvailable -Name PSWindowsUpdate |
        Where-Object { $_.Version -eq [Version]$pinnedPSWU }
if (-not $have) {
    Write-Host "  Instalando PSWindowsUpdate $pinnedPSWU (pode demorar)..." -ForegroundColor Yellow
    try {
        Install-Module -Name PSWindowsUpdate -RequiredVersion $pinnedPSWU -Force -Scope AllUsers `
            -AllowClobber -Confirm:$false -ErrorAction Stop | Out-Null
        Write-Host "    OK" -ForegroundColor Green
    } catch { Write-Host "    FALHA: $($_.Exception.Message)" -ForegroundColor Red }
} else { Write-Host "  PSWindowsUpdate ${pinnedPSWU}: ja presente" -ForegroundColor DarkGray }

# --- Cmdlets nativos: so reporta, nao ha como instalar ---
Write-Host ""
Write-Host "Cmdlets do sistema:" -ForegroundColor Cyan
$cmdlets = [ordered]@{
    'Get-MpComputerStatus'    = 'Auditoria do Windows Defender'
    'Get-NetFirewallProfile'  = 'Auditoria de firewall'
    'Get-BitLockerVolume'     = 'Verificacao de criptografia'
    'Optimize-Volume'         = 'Desfragmentacao e TRIM'
    'Get-Tpm'                 = 'Verificacao de TPM'
    'Get-WindowsOptionalFeature' = 'Deteccao de SMBv1 e PowerShell v2'
    'Get-NetTCPConnection'    = 'Portas expostas'
    'Confirm-SecureBootUEFI'  = 'Secure Boot'
}
foreach ($c in $cmdlets.GetEnumerator()) {
    if (Get-Command $c.Key -ErrorAction SilentlyContinue) {
        Write-Host ("  [OK]      {0,-28} {1}" -f $c.Key, $c.Value) -ForegroundColor DarkGray
    } else {
        Write-Host ("  [AUSENTE] {0,-28} {1} - funcao sera pulada" -f $c.Key, $c.Value) -ForegroundColor Yellow
    }
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "  [AVISO] winget ausente. Instale o App Installer pela Microsoft Store" -ForegroundColor Yellow
    Write-Host "          para habilitar a atualizacao de aplicativos." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Instalado. Atalho: $startMenu" -ForegroundColor Green
Write-Host "Executável: $(Join-Path $InstallDir $exeName)"
