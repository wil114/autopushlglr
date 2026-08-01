@echo off
cd /d "%~dp0"
if exist "UninstallQQMonitor.exe" (
  start "" "UninstallQQMonitor.exe"
) else (
  echo UninstallQQMonitor.exe not found.
  pause
)
