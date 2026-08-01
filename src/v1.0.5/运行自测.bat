@echo off
cd /d "%~dp0"
if not exist "napcat-monitor-v1.0.5.exe" call "build.bat"
if not exist "napcat-monitor-v1.0.5.exe" exit /b 1
"napcat-monitor-v1.0.5.exe" --self-test
pause
