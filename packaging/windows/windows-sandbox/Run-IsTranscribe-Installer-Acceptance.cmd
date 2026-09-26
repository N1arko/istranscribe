@echo off
setlocal
title isTranscribe installer acceptance
REM @spec spec://modules/platform/INFRA-005.A-production-windows-x64-installer#verification
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\isTranscribeAcceptance\tools\windows-sandbox\Invoke-WindowsSandboxAcceptance.ps1"
set "ISTRANSCRIBE_ACCEPTANCE_EXIT=%ERRORLEVEL%"
echo.
if "%ISTRANSCRIBE_ACCEPTANCE_EXIT%"=="0" (
  echo Acceptance finished. Review the two JSON files in C:\isTranscribeAcceptance\evidence.
) else (
  echo Acceptance stopped. Read the error above and start a fresh disposable run after fixing it.
)
echo Close Windows Sandbox when review is complete.
pause
exit /b %ISTRANSCRIBE_ACCEPTANCE_EXIT%
