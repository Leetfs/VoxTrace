@echo off
rem Example: run the live extractor from the command line.
rem   scripts\run_live.cmd <capture.txt> <key-hex> <out-dir>
setlocal
cd /d "%~dp0.."
if "%~3"=="" (
  echo usage: run_live.cmd capture.txt key-hex out-dir
  exit /b 1
)
python -m voxtrace live --capture "%~1" --key %~2 --out "%~3"
