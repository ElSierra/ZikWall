@echo off

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-setup-exe.ps1" %*
exit /b %errorlevel%
