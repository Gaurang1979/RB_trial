@echo off
rem Removes the vs-runtime service and the "Vision System" firewall rules. Jobs and data stay in
rem C:\ProgramData\VisionSystem; delete the program folder yourself afterwards if you want.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0first-run-setup.ps1" -Uninstall
pause
