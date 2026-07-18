@echo off
:: ===============================================================
::  SysMaintenanceHub - Launcher (.bat)
::  Auto-elevação via UAC. Executa o EXE já publicado.
:: ===============================================================
setlocal EnableExtensions

set "EXE=%~dp0src\SysMaintenanceHub\bin\publish\SysMaintenanceHub.exe"
if not exist "%EXE%" set "EXE=%ProgramFiles%\SysMaintenanceHub\SysMaintenanceHub.exe"
if not exist "%EXE%" (
    echo [ERRO] SysMaintenanceHub.exe nao encontrado.
    echo Execute scripts\publish.ps1 -SelfContained primeiro
    echo ou instale via scripts\install-local.ps1
    pause
    exit /b 1
)

:: Elevação automática (UAC único)
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -Verb RunAs"
    exit /b 0
)
start "" "%EXE%"
