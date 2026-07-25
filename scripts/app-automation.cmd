@echo off
setlocal
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0AppAutomation.ps1" %*
exit /b %ERRORLEVEL%
