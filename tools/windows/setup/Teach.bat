@echo off
rem The teach (setup) app: make and change jobs, set the cameras, the PLC page, test with images.
rem The cameras and port 502 belong to one program at a time, so this stops the vs-runtime service,
rem opens the teach app at http://127.0.0.1:5090 (this PC only), and starts the service again when
rem you close the teach app's black window.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'" & exit /b)
sc stop vs-runtime >nul 2>&1
timeout /t 3 /nobreak >nul
powershell -NoProfile -Command "$p = Start-Process -PassThru -WorkingDirectory '%~dp0teach' -FilePath '%~dp0teach\VisionStudio.exe'; Start-Sleep 4; Start-Process 'http://127.0.0.1:5090'; $p.WaitForExit()"
sc start vs-runtime >nul 2>&1
echo vs-runtime started again.
timeout /t 3 >nul
