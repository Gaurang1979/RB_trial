<#
.SYNOPSIS
  First-run setup of a Vision System line PC (Windows 10/11 x64). Run as Administrator; Setup.bat
  does that for you. Safe to run again: each step checks what is there first.

.DESCRIPTION
  0. The pre-install check (preflight.ps1): supporting software with versions (MVS installed from
     prereqs\ when missing), then the hardware. A FAIL stops here, before anything is copied.
  1. Copies the package to the install folder (default C:\Program Files\VisionSystem).
  2. Makes C:\ProgramData\VisionSystem\jobs and \data, writable by the service.
  3. Writes the settings: those folders, camera source, licence check OFF (owner's test PC;
     Studio:LicenceRequired=false), the teach app on this PC only (http://127.0.0.1:5090).
  4. Network cards: the PLC card and the camera cards, an optional fixed IP for each; on the camera
     cards jumbo frames and the largest receive buffers; power saving off on all of them.
  5. Windows Firewall (rule group "Vision System"): Modbus TCP port 502 in, only on the PLC card;
     the GigE camera stream (UDP) in, only on the camera cards and only for the Vision programs.
  6. Power: High performance, no sleep, USB selective suspend off, USB hubs and PCI Express kept
     powered (USB3 cameras drop out otherwise).
  7. The self-test (engine DLLs, AI models, cameras, port 502).
  8. A Vision Studio user (name and PIN) if none exists.
  9. The vs-runtime Windows service (automatic start, LocalService account, restarts on failure).
  10. Desktop shortcuts: Vision Studio, Teach (setup), Self-test, Collect report.
  A log of what was done: C:\ProgramData\VisionSystem\setup-<date>.log. Every check with its
  versions and result: C:\ProgramData\VisionSystem\data\install-report.txt and .json.

.PARAMETER Uninstall
  Stops and removes the service and the firewall rules. Jobs and data stay in C:\ProgramData\VisionSystem.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\VisionSystem",
    [string]$DataRoot = "$env:ProgramData\VisionSystem",
    [string]$PlcNic = "",          # network card name, e.g. "Ethernet 2"; empty = ask
    [string]$PlcIp = "",           # the PLC's own address; port 502 then accepts only it
    [string[]]$CameraNics = @(),   # e.g. "Ethernet 3","Ethernet 4"; empty = ask
    [switch]$NoNetwork,            # leave network cards and firewall as they are
    [switch]$NoPower,              # leave power settings as they are
    [switch]$NoService,            # do not install the service (console mode only)
    [switch]$PreflightDone,        # the installer already ran the pre-install check
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$Service = 'vs-runtime'
$FwGroup = 'Vision System'

# ---- run as Administrator
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'This needs Administrator rights: right-click Setup.bat and choose "Run as administrator".' -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Force $DataRoot | Out-Null
$Log = Join-Path $DataRoot "setup-$(Get-Date -Format yyyyMMdd-HHmmss).log"
Start-Transcript -Path $Log | Out-Null

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ask($q, $default = '') {
    $a = Read-Host "$q$(if ($default) { " [$default]" })"
    if ([string]::IsNullOrWhiteSpace($a)) { return $default } else { return $a.Trim() }
}
function YesNo($q, $default = 'Y') { return (Ask "$q (Y/N)" $default) -match '^[Yy]' }
function Stop-Runtime {
    $s = Get-Service $Service -ErrorAction SilentlyContinue
    if ($s -and $s.Status -ne 'Stopped') { Stop-Service $Service -Force; (Get-Service $Service).WaitForStatus('Stopped', '00:00:30') }
}

if ($Uninstall) {
    Step 'Removing the vs-runtime service and the firewall rules'
    Stop-Runtime
    if (Get-Service $Service -ErrorAction SilentlyContinue) { & sc.exe delete $Service | Out-Null }
    Get-NetFirewallRule -Group $FwGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Done. Jobs and data are kept in $DataRoot."
    Stop-Transcript | Out-Null
    exit 0
}

$Source = (Resolve-Path $PSScriptRoot).Path
Write-Host "Vision System first-run setup. Log: $Log"
if (Test-Path (Join-Path $Source 'VERSION.txt')) { Get-Content (Join-Path $Source 'VERSION.txt') }
Import-Module (Join-Path $Source 'preflight-rules.psm1') -Force
$Report = Join-Path $DataRoot 'data\install-report'

# ---------------------------------------------------------------- 0. pre-install check
if (-not $PreflightDone) {
    Step 'Pre-install check: supporting software and hardware'
    & (Join-Path $Source 'preflight.ps1') -ReportBase $Report
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Setup stopped: the pre-install check failed (report: $Report.txt). Nothing of the vision software was installed." -ForegroundColor Red
        Stop-Transcript | Out-Null
        exit 1
    }
}

# ---------------------------------------------------------------- 1. files
Step "Program files in $InstallDir"
if ((Resolve-Path $Source).Path.TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    Stop-Runtime
    Get-Process vs-studio, VisionStudio -ErrorAction SilentlyContinue | Stop-Process -Force
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    # /MIR keeps the folder equal to the package; settings are written again in step 3
    & robocopy $Source $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy to $InstallDir failed (robocopy $LASTEXITCODE)" }
    Write-Host 'copied'
} else { Write-Host 'already in place' }
$Runtime = Join-Path $InstallDir 'vs-runtime\vs-runtime.exe'
$Teach = Join-Path $InstallDir 'teach\VisionStudio.exe'
$Studio = Join-Path $InstallDir 'vs-studio\vs-studio.exe'
if (-not (Test-Path $Runtime)) { throw "vs-runtime.exe not found in $InstallDir\vs-runtime" }

# ---------------------------------------------------------------- 2. data folders
Step "Jobs and data in $DataRoot"
$Jobs = Join-Path $DataRoot 'jobs'
$Data = Join-Path $DataRoot 'data'
New-Item -ItemType Directory -Force $Jobs, $Data | Out-Null
# the service (LocalService) and the person setting up the line may change them; nobody else
& icacls $DataRoot /grant '*S-1-5-19:(OI)(CI)M' "$($env:USERDOMAIN)\$($env:USERNAME):(OI)(CI)M" /T /Q | Out-Null
Write-Host 'ok'

# ---------------------------------------------------------------- 3. settings
Step 'Settings (licence check OFF for this test PC)'
function Set-Json($file, [scriptblock]$change) {
    $j = if (Test-Path $file) { Get-Content $file -Raw | ConvertFrom-Json } else { New-Object psobject }
    & $change $j
    $j | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $file
}
function Put($obj, $section, $key, $value) {
    if (-not $obj.PSObject.Properties[$section]) { $obj | Add-Member -NotePropertyName $section -NotePropertyValue (New-Object psobject) }
    $s = $obj.$section
    if ($s.PSObject.Properties[$key]) { $s.$key = $value } else { $s | Add-Member -NotePropertyName $key -NotePropertyValue $value }
}
foreach ($f in @((Join-Path $InstallDir 'vs-runtime\vs-runtime.json'), (Join-Path $InstallDir 'teach\appsettings.json'))) {
    if (-not (Test-Path (Split-Path $f))) { continue }
    Set-Json $f {
        param($j)
        Put $j 'Studio' 'JobsFolder' $Jobs
        Put $j 'Studio' 'DataFolder' $Data
        Put $j 'Studio' 'LicenceRequired' $false
    }
}
Set-Json (Join-Path $InstallDir 'vs-runtime\vs-runtime.json') { param($j) Put $j 'Runtime' 'Source' 'camera' }
# the teach app answers on this PC only; it is never a line program
if (Test-Path (Join-Path $InstallDir 'teach\appsettings.json')) {
    Set-Json (Join-Path $InstallDir 'teach\appsettings.json') { param($j) $j.Urls = 'http://127.0.0.1:5090' }
}
Write-Host "jobs $Jobs, data $Data, licence check off"

# ---------------------------------------------------------------- 4 + 5. network and firewall
function Show-Nics {
    $i = 0
    $script:nics = @(Get-NetAdapter -Physical | Sort-Object Name)
    foreach ($n in $script:nics) {
        $i++
        $ip = (Get-NetIPAddress -InterfaceIndex $n.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
        Write-Host ("  {0}. {1,-22} {2,-12} {3,-10} {4}  {5}" -f $i, $n.Name, $n.Status, $n.LinkSpeed, $ip, $n.InterfaceDescription)
    }
}
function Pick($text) {
    $a = Ask $text
    if (-not $a) { return @() }
    $out = @()
    foreach ($p in $a -split '[, ]+') {
        if ($p -match '^\d+$' -and [int]$p -ge 1 -and [int]$p -le $script:nics.Count) { $out += $script:nics[[int]$p - 1].Name }
        elseif (Get-NetAdapter -Name $p -ErrorAction SilentlyContinue) { $out += $p }
        else { Write-Host "  no card '$p'" -ForegroundColor Yellow }
    }
    return $out
}
function Set-FixedIp($nic) {
    $cur = (Get-NetIPAddress -InterfaceAlias $nic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1)
    $now = if ($cur) { "$($cur.IPAddress)/$($cur.PrefixLength)" } else { 'none' }
    $want = Ask "  Fixed IP for '$nic' as address/prefix, e.g. 192.168.10.1/24 (empty = keep $now)"
    if (-not $want) { return }
    if ($want -notmatch '^(\d{1,3}(\.\d{1,3}){3})/(\d{1,2})$') { Write-Host '  not address/prefix; kept' -ForegroundColor Yellow; return }
    $addr = $Matches[1]; $len = [int]$Matches[3]
    Set-NetIPInterface -InterfaceAlias $nic -Dhcp Disabled -ErrorAction SilentlyContinue
    Get-NetIPAddress -InterfaceAlias $nic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
    New-NetIPAddress -InterfaceAlias $nic -IPAddress $addr -PrefixLength $len | Out-Null
    Write-Host "  $nic = $addr/$len"
}
function Set-Advanced($nic, $keyword, [scriptblock]$choose, $label) {
    $p = Get-NetAdapterAdvancedProperty -Name $nic -RegistryKeyword $keyword -ErrorAction SilentlyContinue
    if (-not $p) { Write-Host "  $label`: this card has no such setting"; return }
    $v = & $choose $p
    if ($null -eq $v) { return }
    if ("$($p.RegistryValue)" -ne "$v") {
        Set-NetAdapterAdvancedProperty -Name $nic -RegistryKeyword $keyword -RegistryValue $v -NoRestart:$false
        Write-Host "  $label = $v"
    } else { Write-Host "  $label already $v" }
}
function No-PowerSaving($nic) {
    try { Disable-NetAdapterPowerManagement -Name $nic -ErrorAction Stop; Write-Host '  power saving off' } catch { Write-Host '  power saving: not settable on this card' }
    Set-Advanced $nic '*EEE' { param($p) if ($p.ValidRegistryValues -contains '0') { '0' } } 'Energy Efficient Ethernet'
}

if (-not $NoNetwork) {
    Step 'Network cards'
    Show-Nics
    if (-not $PlcNic) { $PlcNic = (Pick 'Which card goes to the PLC? number (empty = no PLC yet)') | Select-Object -First 1 }
    if ($CameraNics.Count -eq 0) { $CameraNics = Pick 'Which card(s) go to GigE cameras? numbers, e.g. 3,4 (empty = USB3 cameras only)' }
    if ($PlcNic -and ($CameraNics -contains $PlcNic)) { Write-Host 'The PLC and a camera share one card: allowed, but a separate card is safer at 225 per minute.' -ForegroundColor Yellow }

    if ($PlcNic) {
        Write-Host "PLC card: $PlcNic"
        Set-FixedIp $PlcNic
        No-PowerSaving $PlcNic
        if (-not $PlcIp) { $PlcIp = Ask "  The PLC's IP address, so port 502 answers only the PLC (empty = any device on that card)" }
        if ($PlcIp) {
            $ok = Test-Connection -ComputerName $PlcIp -Count 2 -Quiet -ErrorAction SilentlyContinue
            $v = if ($ok) { 'PASS' } else { 'WARN' }
            $note = if ($ok) { '' } else { 'no answer: PLC off, cable, or the card and PLC are in different subnets' }
            Write-Host "  PLC $PlcIp $(if ($ok) { 'answers' } else { 'does not answer' })"
            Write-CheckReport -BasePath $Report -Results @(New-CheckResult 'Setup' 'PLC reachable' "$PlcIp on $PlcNic" 'answers ping' $v $note) -Append | Out-Null
        }
    }
    foreach ($c in $CameraNics) {
        Write-Host "Camera card: $c"
        Set-FixedIp $c
        No-PowerSaving $c
        # the largest jumbo frame the card offers (9014 on most Intel cards); the camera takes the
        # largest packet the path carries when it opens (GevSCPSPacketSize)
        Set-Advanced $c '*JumboPacket' { param($p) ($p.ValidRegistryValues | ForEach-Object { [int]$_ } | Measure-Object -Maximum).Maximum } 'Jumbo frame'
        Set-Advanced $c '*ReceiveBuffers' { param($p) if ($p.NumericParameterMaxValue) { $p.NumericParameterMaxValue } } 'Receive buffers'
        Set-Advanced $c '*InterruptModeration' { param($p) if ($p.ValidRegistryValues -contains '1') { '1' } } 'Interrupt moderation'
    }

    Step "Windows Firewall (group '$FwGroup')"
    Get-NetFirewallRule -Group $FwGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($PlcNic) {
        $r = @{ DisplayName = 'Vision System - PLC Modbus TCP 502'; Group = $FwGroup; Direction = 'Inbound'; Action = 'Allow'
            Protocol = 'TCP'; LocalPort = 502; InterfaceAlias = $PlcNic; Program = $Runtime; Profile = 'Any' }
        if ($PlcIp) { $r.RemoteAddress = $PlcIp }
        New-NetFirewallRule @r | Out-Null
        Write-Host "port 502 in: only on $PlcNic$(if ($PlcIp) { ", only from $PlcIp" }), only vs-runtime"
    }
    if ($CameraNics.Count -gt 0) {
        foreach ($prog in @($Runtime, $Teach) | Where-Object { Test-Path $_ }) {
            New-NetFirewallRule -DisplayName "Vision System - GigE camera stream ($(Split-Path $prog -Leaf))" -Group $FwGroup -Direction Inbound -Action Allow `
                -Protocol UDP -InterfaceAlias $CameraNics -Program $prog -Profile Any | Out-Null
        }
        Write-Host "GigE camera stream (UDP) in: only on $($CameraNics -join ', '), only vs-runtime and the teach app"
    }
    Write-Host 'Camera IP addresses: set each GigE camera to a fixed IP in its card''s subnet with the MVS client (docs\WINDOWS-INSTALL-AND-TEST.md, step 2.4).'
}

# ---------------------------------------------------------------- 6. power
if (-not $NoPower) {
    Step 'Power settings'
    # powercfg reports a missing plan on stderr, which Windows PowerShell would turn into a stop
    $ErrorActionPreference = 'Continue'
    $hp = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'   # High performance
    & powercfg /setactive $hp 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host '  High performance plan not on this PC; using the current plan' }
    & powercfg /change standby-timeout-ac 0
    & powercfg /change hibernate-timeout-ac 0
    & powercfg /change disk-timeout-ac 0
    # USB selective suspend off; PCI Express link state power management off
    & powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_PCIEXPRESS ASPM 0
    & powercfg /setactive SCHEME_CURRENT
    # "Allow the computer to turn off this device to save power" off on every USB hub and controller
    $n = 0
    Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceName -match '^USB' -and $_.Enable } |
        ForEach-Object { Set-CimInstance -InputObject $_ -Property @{ Enable = $false } -ErrorAction SilentlyContinue; $n++ }
    Write-Host "  no sleep, USB selective suspend off, PCIe power saving off, power-off allowed on $n USB device(s) -> off"
    $ErrorActionPreference = 'Stop'
}

# ---------------------------------------------------------------- 7. self-test
Step 'Self-test'
Stop-Runtime
$stJson = Join-Path $Data 'selftest.json'
& $Runtime self-test --json $stJson | Tee-Object -Variable st | Out-Host
$st | Set-Content (Join-Path $DataRoot 'selftest.txt')
# the self-test's checks (DLLs, models, cameras found, job serials) go into the install report too
if (Test-Path $stJson) {
    $overall = Write-CheckReport -BasePath $Report -Results @((Get-Content $stJson -Raw | ConvertFrom-Json).results) -Meta @{ 'Self-test at' = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') } -Append
    Write-Host "Install report: $Report.txt (overall $overall)"
}
if ($st -match '^FAIL') { Write-Host 'Some checks failed: see docs\WINDOWS-INSTALL-AND-TEST.md, "When the self-test fails". Setup goes on.' -ForegroundColor Yellow }

# ---------------------------------------------------------------- 8. a user
Step 'Vision Studio user'
$users = Join-Path $Data 'runtime-users.json'
if (Test-Path $users) { Write-Host 'users exist (add more with: vs-runtime add-user ...)' }
elseif (YesNo 'Create an Engineer + Admin user now (needed to start the line from Vision Studio)?') {
    $u = Ask '  Short name' 'engineer'
    $full = Ask '  Full name' 'Line Engineer'
    do { $pin = Ask '  PIN (4 or more digits)' } until ($pin -match '^\d{4,}$')
    & $Runtime add-user $u $full 'Operator,Supervisor,Engineer,Admin,QA' --pin $pin
}

# ---------------------------------------------------------------- 9. service
if (-not $NoService) {
    Step "Windows service $Service"
    # New-Service and Win32_Service.Change take the quoted path as it is (sc.exe would lose the quotes)
    $bin = "`"$Runtime`""
    if (-not (Get-Service $Service -ErrorAction SilentlyContinue)) {
        New-Service -Name $Service -BinaryPathName $bin -DisplayName 'Vision System runtime' -StartupType Automatic `
            -Description 'Vision System runtime: cameras, jobs, PLC link (Modbus TCP 502), results. Vision Studio connects to it.' | Out-Null
    }
    $r = Get-CimInstance Win32_Service -Filter "Name='$Service'" |
        Invoke-CimMethod -MethodName Change -Arguments @{ PathName = $bin; StartMode = 'Automatic'; StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' }
    if ($r.ReturnValue -ne 0) { throw "setting the service account failed (Win32_Service.Change $($r.ReturnValue))" }
    # restart after 5 s, 5 s, then 30 s; the PLC sees the heartbeat stop meanwhile and makes the parts NG
    & sc.exe failure $Service reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    Start-Service $Service
    Start-Sleep -Seconds 3
    Write-Host "service: $((Get-Service $Service).Status)"
}

# ---------------------------------------------------------------- 10. shortcuts
Step 'Desktop shortcuts'
$desk = [Environment]::GetFolderPath('CommonDesktopDirectory')
$sh = New-Object -ComObject WScript.Shell
function Link($name, $target, $arguments = '', $dir = '') {
    $l = $sh.CreateShortcut((Join-Path $desk "$name.lnk"))
    $l.TargetPath = $target; $l.Arguments = $arguments
    $l.WorkingDirectory = if ($dir) { $dir } else { Split-Path $target }
    $l.Save()
}
Link 'Vision Studio' $Studio
if (Test-Path $Teach) { Link 'Vision System - Teach (setup)' (Join-Path $InstallDir 'Teach.bat') }
Link 'Vision System - Self-test' (Join-Path $InstallDir 'Self-test.bat')
Link 'Vision System - Collect report' (Join-Path $InstallDir 'Collect-report.bat')
Write-Host 'ok'

Write-Host ''
Write-Host 'Setup finished.' -ForegroundColor Green
Write-Host "Log: $Log"
Write-Host 'Next: docs\WINDOWS-INSTALL-AND-TEST.md, part 3 (make the job) and part 4 (live test).'
Stop-Transcript | Out-Null
