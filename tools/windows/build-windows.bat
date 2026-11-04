@echo off
rem One-click build of the Vision System line PC package on a Windows 10/11 x64 PC.
rem Double-click this file (or run it from any command prompt) in the repository folder.
rem It checks Visual Studio Build Tools (C++), Git and the .NET 10 SDK and offers to install what is
rem missing, builds the engine (the first time 20-40 minutes for OpenCV), runs the tests, and writes:
rem   dist\VisionSystem-Setup-<version>.exe   installer (when Inno Setup 6 is installed)
rem   dist\VisionSystem-<version>-win-x64.zip the same as a zip
rem   dist\build-<time>.log                    the full log; send it back if the build fails
rem Options are passed on: build-windows.bat -SkipTests   build-windows.bat -Yes
rem Steps and what each file is: docs\WINDOWS-INSTALL-AND-TEST.md, part 1.
cd /d "%~dp0..\.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-package.ps1" %*
set RC=%ERRORLEVEL%
echo.
if not "%RC%"=="0" echo The build stopped with an error. See the log in the dist folder.
pause
exit /b %RC%
