<#
    Renova - Launcher PowerShell
    Auto-elevação. Localiza o EXE em bin\publish ou em %ProgramFiles%.
    Uso:
        .\Renova.ps1
        .\Renova.ps1 -Build      # publica antes de executar
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
    (Join-Path $root 'src\Renova\bin\publish\Renova.exe'),
    "$env:ProgramFiles\Renova\Renova.exe",
    "$env:LOCALAPPDATA\Programs\Renova\Renova.exe"
)

$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    throw "Renova.exe não encontrado. Rode: .\Renova.ps1 -Build"
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
