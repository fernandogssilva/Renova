# Publica o SysMaintenanceHub como single-file self-contained ou framework-dependent.
# Rode: powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 [-SelfContained]
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\SysMaintenanceHub\SysMaintenanceHub.csproj'
$out  = Join-Path $root 'src\SysMaintenanceHub\bin\publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$common = @(
    'publish', $proj,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $out,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true'
)
if ($SelfContained) {
    $common += @('--self-contained', 'true')
} else {
    $common += @('--self-contained', 'false')
}

Write-Host "==> dotnet $($common -join ' ')" -ForegroundColor Cyan
& dotnet @common
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar (código $LASTEXITCODE)" }

Write-Host ""
Write-Host "Publicado em: $out" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, @{N='SizeMB';E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
