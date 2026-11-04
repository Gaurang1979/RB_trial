@echo off
rem Measures read rate and time on this PC.
rem usage: run-bench.bat "C:\path\to\Barcode Images"
rem Writes bench-result.txt and bench-result.csv next to this file; send both back.

if "%~1"=="" (
  echo usage: run-bench.bat "C:\path\to\Barcode Images"
  exit /b 2
)

cd /d "%~dp0"
echo PC: > bench-result.txt
wmic cpu get name,numberofcores,numberoflogicalprocessors,maxclockspeed >> bench-result.txt
bench.exe "%~1" samples.csv --runs 3 --csv bench-result.csv >> bench-result.txt
type bench-result.txt
