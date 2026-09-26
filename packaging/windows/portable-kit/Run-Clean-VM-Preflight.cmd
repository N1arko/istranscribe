@echo off
setlocal
title isTranscribe clean VM preflight
REM @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Invoke-AcceptanceKit.ps1" -Mode CleanVmPreflight -KitRoot "%~dp0" -EvidenceRoot "%~dp0..\isTranscribe-clean-vm-preflight-evidence"
set "ISTRANSCRIBE_EXIT=%ERRORLEVEL%"
echo.
if "%ISTRANSCRIBE_EXIT%"=="0" (echo Preflight passed. Review the external evidence directory.) else (echo Preflight stopped. Review the error and raw evidence.)
pause
exit /b %ISTRANSCRIBE_EXIT%
