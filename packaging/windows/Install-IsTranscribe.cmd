@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-IsTranscribe.ps1" -BundleRoot "%~dp0" %*
exit /b %errorlevel%
