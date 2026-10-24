@echo off
rem Build the fast pagefile scanner (requires .NET Framework csc).
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /unsafe /optimize+ /target:exe /out:bin\FastScan.exe gui\FastScan.cs
if errorlevel 1 exit /b 1
echo built bin\FastScan.exe
