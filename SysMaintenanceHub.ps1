<#
    SysMaintenanceHub - Launcher PowerShell
    Auto-elevação. Localiza o EXE em bin\publish ou em %ProgramFiles%.
    Uso:
        .\SysMaintenanceHub.ps1
        .\SysMaintenanceHub.ps1 -Build      # publica antes de executar
#>

param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Build) {
    Write-Host "==> Publicando (self-contained)..." -ForegroundColor Cyan
    & (Join-Path $root 'scripts\publish.ps1') -SelfContained
}

$candidates = @(
    (Join-Path $root 'src\SysMaintenanceHub\bin\publish\SysMaintenanceHub.exe'),
    "$env:ProgramFiles\SysMaintenanceHub\SysMaintenanceHub.exe",
    "$env:LOCALAPPDATA\Programs\SysMaintenanceHub\SysMaintenanceHub.exe"
)

$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    throw "SysMaintenanceHub.exe não encontrado. Rode: .\SysMaintenanceHub.ps1 -Build"
}

Write-Host "Executando: $exe" -ForegroundColor Green

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Elevando via UAC..." -ForegroundColor Yellow
    Start-Process -FilePath $exe -Verb RunAs
} else {
    Start-Process -FilePath $exe
}
