@echo off
rem Installs the web dashboard as a Windows service that starts with the PC.
rem Run as Administrator from the Dashboard folder. Edit appsettings.json first (camera serials,
rem replay folder, API key), and create users with:  Dashboard.exe add-user <name> "<full name>" Operator,Engineer,QA
rem Then open http://<line-pc>:5080 from any browser on the plant network.

cd /d "%~dp0"
sc.exe create CodeReaderDashboard binPath= "\"%~dp0Dashboard.exe\"" start= delayed-auto DisplayName= "Code Reader Dashboard" || exit /b 1
sc.exe description CodeReaderDashboard "AI code reader: live view, product master, tuning, audit trail (port 5080)"
sc.exe failure CodeReaderDashboard reset= 86400 actions= restart/5000/restart/5000/restart/30000
netsh advfirewall firewall add rule name="Code Reader Dashboard" dir=in action=allow protocol=TCP localport=5080
sc.exe start CodeReaderDashboard
