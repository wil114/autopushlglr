@echo off
cd /d "%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "REF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

if not exist "%CSC%" (
  echo C# compiler not found: %CSC%
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /out:"qq-monitor-ui.exe" ^
  /reference:"%REF%\System.Web.Extensions.dll" ^
  /reference:"%REF%\System.Windows.Forms.dll" ^
  /reference:"%REF%\System.Drawing.dll" ^
  "UiProgram.cs"

if errorlevel 1 exit /b 1
echo Build succeeded: qq-monitor-ui.exe
