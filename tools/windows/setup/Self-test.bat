@echo off
rem Checks the engine DLLs, AI models, folders, cameras, the job's camera serials and port 502.
rem   Self-test.bat            quick check (the service keeps running)
rem   Self-test.bat grab 300   also fires each job camera 300 times and prints frame times and frames
rem                            missed; the service is stopped for it and started again afterwards
rem Result: on screen, in C:\ProgramData\VisionSystem\selftest.txt and data\selftest.json
net session >nul 2>&1 || (powershell -NoProfile -Command "$a = '%*'.Trim(); if ($a) { Start-Process -Verb RunAs -FilePath '%~f0' -ArgumentList $a } else { Start-Process -Verb RunAs -FilePath '%~f0' }" & exit /b)
setlocal
set OUT=%ProgramData%\VisionSystem\selftest.txt
if /i "%~1"=="grab" (
  sc stop vs-runtime >nul 2>&1
  timeout /t 3 /nobreak >nul
  "%~dp0vs-runtime\vs-runtime.exe" self-test --grab %~2 --json "%ProgramData%\VisionSystem\data\selftest.json" > "%OUT%"
  sc start vs-runtime >nul 2>&1
) else (
  "%~dp0vs-runtime\vs-runtime.exe" self-test --json "%ProgramData%\VisionSystem\data\selftest.json" > "%OUT%"
)
type "%OUT%"
pause
