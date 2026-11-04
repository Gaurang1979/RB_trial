<#
.SYNOPSIS
  Collects what the developer needs after a line test into one zip on the desktop:
  the version, a fresh self-test, the install report (pre-install check, PLC, self-test), setup logs, the results database (results.db), the PLC and line
  settings, the job files (without their pictures), the vs-runtime messages from the Windows event
  log, console logs, bench results, and the PC, network card, power and firewall settings.
  No product images and no user or token files are taken.
#>
$ErrorActionPreference = 'Continue'
$Install = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataRoot = "$env:ProgramData\VisionSystem"
$Data = Join-Path $DataRoot 'data'
$Jobs = Join-Path $DataRoot 'jobs'
$stamp = Get-Date -Format yyyyMMdd-HHmm
$Work = Join-Path $env:TEMP "VisionSystem-report-$env:COMPUTERNAME-$stamp"
$Zip = Join-Path ([Environment]::GetFolderPath('Desktop')) "VisionSystem-report-$env:COMPUTERNAME-$stamp.zip"
New-Item -ItemType Directory -Force $Work | Out-Null

Write-Host 'Collecting the report ...'
Copy-Item (Join-Path $Install 'VERSION.txt') $Work -ErrorAction SilentlyContinue

# results.db is written all the time while the line runs: a clean copy needs the service stopped
$svc = Get-Service vs-runtime -ErrorAction SilentlyContinue
$wasRunning = $svc -and $svc.Status -eq 'Running'
if ($wasRunning) {
    $a = Read-Host 'Stop the vs-runtime service for a few seconds for a clean copy of results.db? Only when the line is stopped (Y/N) [Y]'
    if ($a -notmatch '^[Nn]') { Stop-Service vs-runtime -Force; (Get-Service vs-runtime).WaitForStatus('Stopped', '00:00:30') } else { $wasRunning = $false }
}
New-Item -ItemType Directory -Force (Join-Path $Work 'data') | Out-Null
Get-ChildItem $Data -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^results\.db' -or $_.Extension -in '.log', '.csv', '.txt' -or $_.Name -in 'plc.json', 'line.json', 'last-job.txt', 'install-report.json', 'selftest.json' } |
    Copy-Item -Destination (Join-Path $Work 'data')
& (Join-Path $Install 'vs-runtime\vs-runtime.exe') self-test 2>&1 | Set-Content (Join-Path $Work 'selftest.txt')
if ($wasRunning) { Start-Service vs-runtime }

New-Item -ItemType Directory -Force (Join-Path $Work 'jobs') | Out-Null
Get-ChildItem $Jobs -Filter *.json -File -ErrorAction SilentlyContinue | Copy-Item -Destination (Join-Path $Work 'jobs')
Get-ChildItem $DataRoot -Filter 'setup-*.log' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $Work
Get-ChildItem (Join-Path $Install 'tools') -Filter 'bench-result*' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $Work

# vs-runtime writes warnings and errors to the Application event log; crashes come from .NET Runtime / Application Error
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-3) } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in 'vs-runtime', '.NET Runtime', 'Application Error', 'VisionStudio' } |
    Select-Object TimeCreated, ProviderName, LevelDisplayName, Id, Message |
    Export-Csv (Join-Path $Work 'events.csv') -NoTypeInformation -Encoding UTF8
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = (Get-Date).AddDays(-3) } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'vs-runtime|Vision System' } |
    Select-Object TimeCreated, LevelDisplayName, Id, Message |
    Export-Csv (Join-Path $Work 'service-events.csv') -NoTypeInformation -Encoding UTF8

$pc = Join-Path $Work 'pc.txt'
& {
    '== PC'
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, OSArchitecture, LastBootUpTime | Format-List | Out-String
    Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed | Format-List | Out-String
    "Memory GB: {0:N1}" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
    '== Service'
    Get-Service vs-runtime -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType | Format-List | Out-String
    '== Network cards'
    Get-NetAdapter -Physical | Select-Object Name, Status, LinkSpeed, InterfaceDescription | Format-Table -AutoSize | Out-String
    Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress, PrefixLength | Format-Table -AutoSize | Out-String
    Get-NetAdapter -Physical | ForEach-Object {
        Get-NetAdapterAdvancedProperty -Name $_.Name -RegistryKeyword '*JumboPacket', '*ReceiveBuffers', '*InterruptModeration', '*EEE' -ErrorAction SilentlyContinue
    } | Select-Object Name, DisplayName, DisplayValue | Format-Table -AutoSize | Out-String
    '== Firewall (Vision System)'
    Get-NetFirewallRule -Group 'Vision System' -ErrorAction SilentlyContinue | ForEach-Object {
        $r = $_; $p = $r | Get-NetFirewallPortFilter; $i = $r | Get-NetFirewallInterfaceFilter; $a = $r | Get-NetFirewallAddressFilter
        "{0}: {1} {2} port {3}, card {4}, from {5}" -f $r.DisplayName, $r.Direction, $p.Protocol, $p.LocalPort, ($i.InterfaceAlias -join ','), ($a.RemoteAddress -join ',')
    }
    '== Power'
    powercfg /getactivescheme
    powercfg /query SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226
} *> $pc

if (Test-Path $Zip) { Remove-Item $Zip -Force }
Compress-Archive -Path (Join-Path $Work '*') -DestinationPath $Zip
Remove-Item -Recurse -Force $Work
Write-Host ''
Write-Host "Report: $Zip" -ForegroundColor Green
Write-Host 'Send this file back. It holds the test results (results.db) but no product images.'
