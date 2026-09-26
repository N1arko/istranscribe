@echo off
setlocal
title isTranscribe Windows Sandbox acceptance
REM @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\New-WindowsSandboxConfiguration.ps1" -KitRoot "%~dp0" -Launch -Force
set "ISTRANSCRIBE_EXIT=%ERRORLEVEL%"
if not "%ISTRANSCRIBE_EXIT%"=="0" pause
exit /b %ISTRANSCRIBE_EXIT%
