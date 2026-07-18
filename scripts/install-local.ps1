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

# Atalho no menu iniciar
$startMenu = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\SysMaintenanceHub"
if (-not (Test-Path $startMenu)) { New-Item -ItemType Directory -Force -Path $startMenu | Out-Null }
$WshShell = New-Object -ComObject WScript.Shell
$shortcut = $WshShell.CreateShortcut((Join-Path $startMenu 'SysMaintenanceHub.lnk'))
$shortcut.TargetPath       = Join-Path $InstallDir $exeName
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation     = Join-Path $InstallDir $exeName
$shortcut.Save()

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
