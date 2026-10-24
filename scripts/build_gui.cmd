@echo off
rem Build the WinForms GUI with the .NET Framework compiler (no SDK required).
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /out:bin\VoxTrace.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  gui\VoxTrace.cs
if errorlevel 1 exit /b 1
echo built bin\VoxTrace.exe
