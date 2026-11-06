@echo off
rem Builds PCMonitorPanel.exe with the C# compiler included in .NET Framework 4.x (present on every Windows 10/11 install).
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:PCMonitorPanel.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  /resource:panel.default.json,panel.default.json ^
  PCMonitorPanel.cs
if errorlevel 1 (echo Build failed & exit /b 1)
echo Built PCMonitorPanel.exe
