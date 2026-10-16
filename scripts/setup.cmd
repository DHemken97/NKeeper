@echo off
rem NKeeper setup for Windows: runs setup.ps1 without changing PowerShell's execution policy.
rem Double-click it, or run "scripts\setup.cmd -Mode Iis" (as administrator) etc.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set code=%errorlevel%
if "%~1"=="" pause
exit /b %code%
