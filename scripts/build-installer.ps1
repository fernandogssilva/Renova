<#
    Gera o instalador do Renova (assistente Avançar > Avançar > Instalar).
    1. Publica o Renova.exe self-contained (não exige .NET instalado no cliente)
    2. Compila installer\setup.iss com o Inno Setup 6
    Saída: Renova-Setup-<versão>.exe na raiz do projeto.

    Uso:
        powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
        .\scripts\build-installer.ps1 -SkipPublish   # reaproveita o publish existente
#>
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Versão vem do .csproj, para o instalador nunca divergir do executável
[xml]$csproj = Get-Content (Join-Path $root 'src\Renova\Renova.csproj')
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -SelfContained
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup"
}

Write-Host "==> Compilando instalador $version" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\setup.iss')
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o instalador (código $LASTEXITCODE)" }

$setup = Join-Path $root "Renova-Setup-$version.exe"
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $setup).Hash
Set-Content -Path "$setup.sha256" -Value "$sha256  Renova-Setup-$version.exe" -Encoding ASCII

Write-Host ""
Write-Host "Instalador: $setup" -ForegroundColor Green
Write-Host "Tamanho:    $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB"
Write-Host "SHA-256:    $sha256" -ForegroundColor Cyan
