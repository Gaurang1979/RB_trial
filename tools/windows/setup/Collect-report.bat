@echo off
rem Puts what the developer needs after a test in one zip on the desktop (no product images).
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-report.ps1"
pause
