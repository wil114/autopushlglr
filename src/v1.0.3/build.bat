@echo off
cd /d "%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
  echo C# compiler not found: %CSC%
  exit /b 1
)

"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 ^
  /out:"napcat-monitor-v1.0.3.exe" ^
  /reference:"%REF%\System.Web.Extensions.dll" ^
  "Program.cs"

if errorlevel 1 exit /b 1
echo Build succeeded: napcat-monitor-v1.0.3.exe
