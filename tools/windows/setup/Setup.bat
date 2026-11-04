@echo off
rem First-run setup of the line PC: right-click, "Run as administrator" (it asks for it if needed).
rem What it does: first-run-setup.ps1 (service, firewall, network cards, power, self-test, user).
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
cd /d "%~dp0"
rem files from a downloaded zip are marked as from the internet; PowerShell would refuse them
powershell -NoProfile -Command "Get-ChildItem -Recurse -File '%~dp0' | Unblock-File"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0first-run-setup.ps1" %*
pause
