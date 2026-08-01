@echo off
cd /d "%~dp0"
if not exist "napcat-monitor-v1.0.6.exe" call "build.bat"
if not exist "napcat-monitor-v1.0.6.exe" exit /b 1
"napcat-monitor-v1.0.6.exe"
pause
