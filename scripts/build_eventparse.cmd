@echo off
rem Build the optional Protocol18 helper used for automatic player names.
rem Requires the .NET SDK 8 and a PhotonClient.dll from the Photon .NET SDK.
rem   set PHOTON_CLIENT_DLL=C:\path\to\PhotonClient.dll
setlocal
cd /d "%~dp0.."
if "%PHOTON_CLIENT_DLL%"=="" (
  echo PHOTON_CLIENT_DLL is not set; skipping eventparse build.
  echo Set it to the Photon .NET SDK PhotonClient.dll path.
  exit /b 1
)
if not exist bin mkdir bin
dotnet build gui\eventparse -c Release -o bin\eventparse -p:PhotonClientPath="%PHOTON_CLIENT_DLL%"
if errorlevel 1 exit /b 1
echo built bin\eventparse\eventparse.exe
