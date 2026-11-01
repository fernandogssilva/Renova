@echo off
setlocal
title Renova - Atualizador
color 0B

echo.
echo  ============================================
echo   Renova - Atualizador para %ProgramFiles%
echo  ============================================
echo.

:: Verifica se e admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo  [!] Este script precisa ser executado como Administrador.
    echo      Clique com botao direito neste arquivo e escolha:
    echo      "Executar como administrador"
    echo.
    pause
    exit /b 1
)

set "SRC=%~dp0src\Renova\bin\publish"
set "DST=%ProgramFiles%\Renova"

if not exist "%SRC%\Renova.exe" (
    echo  [!] Arquivo de publish nao encontrado em:
    echo      %SRC%
    echo.
    echo  Rode antes: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained
    pause
    exit /b 1
)

echo  [1/3] Fechando Renova se estiver aberto...
taskkill /IM Renova.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul

echo  [2/3] Copiando arquivos para %DST%...
xcopy "%SRC%\*" "%DST%\" /E /Y /Q >nul
if %errorlevel% neq 0 (
    echo  [ERRO] Falha na copia.
    pause
    exit /b 1
)

echo  [3/3] Abrindo Renova atualizado...
start "" "%DST%\Renova.exe"

echo.
echo  Concluido!
echo.
timeout /t 3 /nobreak >nul
exit /b 0
