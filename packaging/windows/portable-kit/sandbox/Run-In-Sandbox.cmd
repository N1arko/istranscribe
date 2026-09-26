@echo off
setlocal
title isTranscribe Sandbox lifecycle
REM @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\isTranscribeAcceptance\kit\tools\Invoke-AcceptanceKit.ps1" -Mode SandboxLifecycle -KitRoot "C:\isTranscribeAcceptance\kit" -EvidenceRoot "C:\isTranscribeAcceptance\evidence"
set "ISTRANSCRIBE_EXIT=%ERRORLEVEL%"
echo.
if "%ISTRANSCRIBE_EXIT%"=="0" (echo Sandbox lifecycle passed. Close Sandbox after reviewing staging evidence.) else (echo Sandbox lifecycle stopped. Preserve raw evidence and close Sandbox.)
pause
exit /b %ISTRANSCRIBE_EXIT%
