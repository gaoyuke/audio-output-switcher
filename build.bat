@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4.x compiler not found.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:2 ^
  /out:"%~dp0AudioSwitch.exe" ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Core.dll ^
  "%~dp0AudioSwitch.cs"
if errorlevel 1 (
  echo [ERROR] Build failed.
  exit /b 1
)
echo [OK] Built: %~dp0AudioSwitch.exe
endlocal
