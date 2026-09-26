@echo off
setlocal
title isTranscribe clean VM lifecycle
REM @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Invoke-AcceptanceKit.ps1" -Mode CleanVmLifecycle -KitRoot "%~dp0" -EvidenceRoot "%~dp0..\isTranscribe-clean-vm-lifecycle-evidence"
set "ISTRANSCRIBE_EXIT=%ERRORLEVEL%"
echo.
if "%ISTRANSCRIBE_EXIT%"=="0" (echo Lifecycle passed. Review the external evidence directory.) else (echo Lifecycle stopped. Revert the disposable VM after preserving raw evidence.)
pause
exit /b %ISTRANSCRIBE_EXIT%
