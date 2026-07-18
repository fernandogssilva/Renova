# Instalador alternativo (sem Inno Setup): copia o publish para %ProgramFiles%
# e cria atalho + registro de desinstalação. Rode como Administrador.

#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\SysMaintenanceHub"
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$publish   = Join-Path $root 'src\SysMaintenanceHub\bin\publish'
$exeName   = 'SysMaintenanceHub.exe'

if (-not (Test-Path (Join-Path $publish $exeName))) {
    Write-Host "Publish ausente. Rodando publish primeiro..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

Write-Host "Instalando em $InstallDir..." -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null }
Copy-Item -Path (Join-Path $publish '*') -Destination $InstallDir -Recurse -Force

# Atalhos: Menu Iniciar + Desktop do usuário
$startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\SysMaintenanceHub"
if (-not (Test-Path $startMenu)) { New-Item -ItemType Directory -Force -Path $startMenu | Out-Null }

$WshShell = New-Object -ComObject WScript.Shell

# Menu Iniciar
$lnkStart = $WshShell.CreateShortcut((Join-Path $startMenu 'Wintal.lnk'))
$lnkStart.TargetPath       = Join-Path $InstallDir $exeName
$lnkStart.WorkingDirectory = $InstallDir
$lnkStart.IconLocation     = Join-Path $InstallDir $exeName
$lnkStart.Description      = 'Wintal - A saude do seu Windows'
$lnkStart.Save()

# Desktop (perfil do usuário que executou o instalador; se elevado, usa o Public)
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not (Test-Path $desktop)) { $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory') }
$lnkDesk = $WshShell.CreateShortcut((Join-Path $desktop 'Wintal.lnk'))
$lnkDesk.TargetPath       = Join-Path $InstallDir $exeName
$lnkDesk.WorkingDirectory = $InstallDir
$lnkDesk.IconLocation     = Join-Path $InstallDir $exeName
$lnkDesk.Description      = 'Wintal - A saude do seu Windows'
$lnkDesk.Save()
Write-Host "Atalho no desktop: $desktop\Wintal.lnk" -ForegroundColor Cyan

# Registro de desinstalação
$regBase = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SysMaintenanceHub'
if (-not (Test-Path $regBase)) { New-Item -Path $regBase -Force | Out-Null }
Set-ItemProperty $regBase 'DisplayName'     'SysMaintenanceHub'
Set-ItemProperty $regBase 'DisplayVersion'  '1.0.0'
Set-ItemProperty $regBase 'Publisher'       'DataSec'
Set-ItemProperty $regBase 'InstallLocation' $InstallDir
Set-ItemProperty $regBase 'UninstallString' "powershell.exe -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty $regBase 'NoModify'        1
Set-ItemProperty $regBase 'NoRepair'        1

# Uninstall script
@"
#Requires -RunAsAdministrator
Remove-Item -Recurse -Force '$InstallDir' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force '$startMenu' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force '$env:LOCALAPPDATA\SysMaintenanceHub' -ErrorAction SilentlyContinue
Remove-Item -Path '$regBase' -Recurse -Force -ErrorAction SilentlyContinue
"@ | Set-Content -Path (Join-Path $InstallDir 'uninstall.ps1') -Encoding UTF8

Write-Host ""
Write-Host "Instalado. Atalho: $startMenu" -ForegroundColor Green
Write-Host "Executável: $(Join-Path $InstallDir $exeName)"
