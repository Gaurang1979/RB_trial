# Rules of the pre-install check (preflight.ps1): pure functions that turn facts about the PC into
# results. No Windows calls here, so tools/windows/tests/preflight-rules.Tests.ps1 runs them with
# made-up facts on any PC. Windows PowerShell 5.1 syntax.
#
# A result: Group, Check, Found, Required, Verdict (PASS, WARN, FAIL, INFO), Note.
# FAIL stops the install; WARN is shown and written to the report; INFO is for the record.

# The line PC minimum and recommendation (owner, 2026-10-30). Estimates until the 225 per minute
# timing is measured on a Windows line PC: docs/WINDOWS-INSTALL-AND-TEST.md "Line PC to get".
$script:Limits = @{
    WindowsMinBuild     = 17763        # Windows 10 1809 / LTSC 2019
    WindowsTestedBuilds = '19045 (Windows 10 22H2), 22631 (Windows 11 23H2)'
    CpuMinCores         = 4            # physical cores, refused below
    CpuRecCores         = 8            # warned below
    RamMinGb            = 8
    RamRecGb            = 16
    DiskMinGb           = 15           # install 2 GB + NG images 5 GB + results.db, logs, updates 8 GB
    DiskRecGb           = 100
    ScreenMinW          = 1366; ScreenMinH = 768
    ScreenRecW          = 1920; ScreenRecH = 1080
    JumboMin            = 9000
    LinkMinMbps         = 1000
    MvsMinVersion       = '4.8.2'      # camera SDK the package is built against (third_party/hikrobot-mvs-sdk)
}

function Get-PreflightLimits { return $script:Limits }

function New-CheckResult {
    param([string]$Group, [string]$Check, $Found, $Required, [ValidateSet('PASS', 'WARN', 'FAIL', 'INFO')][string]$Verdict, [string]$Note = '')
    return [pscustomobject]@{ Group = $Group; Check = $Check; Found = "$Found"; Required = "$Required"; Verdict = $Verdict; Note = $Note }
}

# Compares two version strings by their numbers ("4.8.2.1", "V5.1.0 build 20260909" -> 5.1.0.20260909).
# Returns -1, 0 or 1; a missing or number-free version sorts below every real one.
function Compare-Version {
    param([string]$A, [string]$B)
    $x = @([regex]::Matches("$A", '\d+') | ForEach-Object { [long]$_.Value })
    $y = @([regex]::Matches("$B", '\d+') | ForEach-Object { [long]$_.Value })
    if ($x.Count -eq 0 -and $y.Count -eq 0) { return 0 }
    if ($x.Count -eq 0) { return -1 }
    if ($y.Count -eq 0) { return 1 }
    $n = [Math]::Max($x.Count, $y.Count)
    for ($i = 0; $i -lt $n; $i++) {
        $p = if ($i -lt $x.Count) { $x[$i] } else { 0 }
        $q = if ($i -lt $y.Count) { $y[$i] } else { 0 }
        if ($p -lt $q) { return -1 }
        if ($p -gt $q) { return 1 }
    }
    return 0
}

# A supporting component with a minimum version: missing or older is FAIL (or WARN when not required).
function Test-Component {
    param([string]$Group = 'Software', [string]$Name, [string]$Installed, [string]$Minimum, [string]$Tested = '', [bool]$Required = $true)
    $need = "$Minimum or newer$(if ($Tested) { ", tested $Tested" })"
    $bad = if ($Required) { 'FAIL' } else { 'WARN' }
    if ([string]::IsNullOrWhiteSpace($Installed)) { return New-CheckResult $Group $Name 'not installed' $need $bad }
    if ((Compare-Version $Installed $Minimum) -lt 0) { return New-CheckResult $Group $Name $Installed $need $bad 'older than the minimum' }
    return New-CheckResult $Group $Name $Installed $need 'PASS'
}

function Test-WindowsVersion {
    param([int]$Build, [string]$Caption, [bool]$Is64Bit)
    $L = $script:Limits
    $r = @()
    $r += if (-not $Is64Bit) { New-CheckResult 'Software' 'Windows 64-bit' 'no' 'x64' 'FAIL' }
          else { New-CheckResult 'Software' 'Windows 64-bit' 'yes' 'x64' 'PASS' }
    $need = "build $($L.WindowsMinBuild) or newer, tested $($L.WindowsTestedBuilds)"
    $r += if ($Build -lt $L.WindowsMinBuild) { New-CheckResult 'Software' 'Windows build' $Build $need 'FAIL' 'Windows 10 1809 or newer is needed' }
          else { New-CheckResult 'Software' 'Windows build' $Build $need 'PASS' }
    $ed = 'Windows 10/11 IoT Enterprise LTSC (primary), Pro or Enterprise'
    $r += if ($Caption -match 'Home') { New-CheckResult 'Software' 'Windows edition' $Caption $ed 'WARN' 'Home edition: updates and restarts cannot be held back on a line PC' }
          elseif ($Caption -match 'IoT|Enterprise|Pro') { New-CheckResult 'Software' 'Windows edition' $Caption $ed 'PASS' }
          else { New-CheckResult 'Software' 'Windows edition' $Caption $ed 'WARN' 'not a tested edition' }
    return $r
}

function Test-Cpu {
    param([string]$Name, [int]$Cores, [int]$Threads)
    $L = $script:Limits
    $need = "$($L.CpuMinCores) cores minimum, $($L.CpuRecCores) recommended"
    $found = "$Name, $Cores cores / $Threads threads"
    if ($Cores -lt $L.CpuMinCores) { return New-CheckResult 'Hardware' 'CPU' $found $need 'FAIL' 'too few cores for two cameras at 225 per minute' }
    if ($Cores -lt $L.CpuRecCores) { return New-CheckResult 'Hardware' 'CPU' $found $need 'WARN' 'below the recommendation: check the cycle time at full speed' }
    return New-CheckResult 'Hardware' 'CPU' $found $need 'PASS'
}

function Test-Ram {
    param([double]$Gb)
    $L = $script:Limits
    $need = "$($L.RamMinGb) GB minimum, $($L.RamRecGb) GB recommended"
    $found = '{0:N1} GB' -f $Gb
    if ($Gb -lt $L.RamMinGb - 0.5) { return New-CheckResult 'Hardware' 'Memory' $found $need 'FAIL' }
    if ($Gb -lt $L.RamRecGb - 0.5) { return New-CheckResult 'Hardware' 'Memory' $found $need 'WARN' }
    return New-CheckResult 'Hardware' 'Memory' $found $need 'PASS'
}

function Test-Disk {
    param([string]$Drive, [double]$FreeGb, [string]$MediaType)
    $L = $script:Limits
    $r = @()
    $need = "$($L.DiskMinGb) GB free minimum (program 2 GB, NG images 5 GB, results.db, logs and updates), $($L.DiskRecGb) GB recommended"
    $found = '{0:N1} GB free on {1}' -f $FreeGb, $Drive
    $r += if ($FreeGb -lt $L.DiskMinGb) { New-CheckResult 'Hardware' 'Disk space' $found $need 'FAIL' 'free some space or use a bigger disk' }
          elseif ($FreeGb -lt $L.DiskRecGb) { New-CheckResult 'Hardware' 'Disk space' $found $need 'WARN' }
          else { New-CheckResult 'Hardware' 'Disk space' $found $need 'PASS' }
    $r += if ($MediaType -match 'HDD') { New-CheckResult 'Hardware' 'Disk type' $MediaType 'SSD' 'WARN' 'a hard disk slows image saving' }
          elseif ($MediaType -match 'SSD') { New-CheckResult 'Hardware' 'Disk type' $MediaType 'SSD' 'PASS' }
          else { New-CheckResult 'Hardware' 'Disk type' $(if ($MediaType) { $MediaType } else { 'unknown' }) 'SSD' 'INFO' 'Windows does not say; check it is an SSD' }
    return $r
}

function Test-Screen {
    param([int]$Width, [int]$Height)
    $L = $script:Limits
    $need = "$($L.ScreenMinW)x$($L.ScreenMinH) minimum, $($L.ScreenRecW)x$($L.ScreenRecH) recommended"
    if ($Width -le 0 -or $Height -le 0) { return New-CheckResult 'Hardware' 'Screen' 'none found' $need 'WARN' 'Vision Studio needs a screen; the runtime works without one' }
    $found = "${Width}x${Height}"
    if ($Width -lt $L.ScreenMinW -or $Height -lt $L.ScreenMinH) { return New-CheckResult 'Hardware' 'Screen' $found $need 'WARN' 'Vision Studio does not fit: parts of the screen are cut off' }
    return New-CheckResult 'Hardware' 'Screen' $found $need 'PASS'
}

# Adapters: wired physical cards, each @{ Name; Description; Up (bool); SpeedMbps; JumboMax; DriverProvider; DriverVersion }.
function Test-Network {
    param([object[]]$Adapters, [int]$GigeCameras, [bool]$PlcPort = $true)
    $L = $script:Limits
    $r = @()
    $Adapters = @($Adapters | Where-Object { $_ })
    $want = $GigeCameras + $(if ($PlcPort) { 1 } else { 0 })
    $need = "$want ($GigeCameras for GigE cameras, one each$(if ($PlcPort) { ', 1 for the PLC' }))"
    $r += if ($want -eq 0) { New-CheckResult 'Hardware' 'Network ports' $Adapters.Count 'none needed' 'PASS' }
          elseif ($Adapters.Count -ge $want) { New-CheckResult 'Hardware' 'Network ports' $Adapters.Count $need 'PASS' }
          elseif ($Adapters.Count -ge 1) { New-CheckResult 'Hardware' 'Network ports' $Adapters.Count $need 'WARN' 'cameras and PLC must share a card through a switch; one card per GigE camera is safer at 225 per minute' }
          else { New-CheckResult 'Hardware' 'Network ports' 0 $need 'FAIL' 'no wired network port' }
    foreach ($a in $Adapters) {
        $check = "Network port $($a.Name)"
        $found = "$($a.Description), driver $($a.DriverProvider) $($a.DriverVersion), " +
            $(if ($a.Up) { "$($a.SpeedMbps) Mbit/s" } else { 'no link' }) + ", jumbo up to $(if ($a.JumboMax) { $a.JumboMax } else { 'none' })"
        $notes = @()
        $verdict = 'PASS'
        if ($GigeCameras -gt 0 -and (-not $a.JumboMax -or $a.JumboMax -lt $L.JumboMin)) { $verdict = 'WARN'; $notes += 'no jumbo frames: not for a GigE camera' }
        if ($a.Up -and $a.SpeedMbps -lt $L.LinkMinMbps) { $verdict = 'WARN'; $notes += "link below $($L.LinkMinMbps) Mbit/s" }
        if (-not $a.Up) { $notes += 'link speed checked when a cable is in' }
        if ($a.Description -notmatch 'Intel') { $notes += 'Intel cards are recommended for GigE cameras' }
        $r += New-CheckResult 'Hardware' $check $found "$($L.LinkMinMbps) Mbit/s, jumbo $($L.JumboMin)+" $verdict ($notes -join '; ')
    }
    return $r
}

function Test-Usb {
    param([int]$Usb3Controllers, [int]$UsbCameras)
    $need = if ($UsbCameras -gt 0) { "one USB3 controller per camera ($UsbCameras)" } else { 'none needed' }
    if ($UsbCameras -le 0) { return New-CheckResult 'Hardware' 'USB3 controllers' $Usb3Controllers $need 'INFO' }
    if ($Usb3Controllers -eq 0) { return New-CheckResult 'Hardware' 'USB3 controllers' 0 $need 'FAIL' 'no USB3 host controller for the USB3 cameras' }
    if ($Usb3Controllers -lt $UsbCameras) { return New-CheckResult 'Hardware' 'USB3 controllers' $Usb3Controllers $need 'WARN' 'cameras share a controller; add a USB3 card if frames are missed' }
    return New-CheckResult 'Hardware' 'USB3 controllers' $Usb3Controllers $need 'PASS'
}

# An installer for a supporting component, checked before it runs: Authenticode signature valid and
# from the expected maker, and, when a SHA256SUMS.txt lists it, the same checksum.
function Test-InstallerTrust {
    param([string]$File, [string]$SignatureStatus, [string]$Signer, [string]$SignerPattern, [string]$Sha256 = '', [string]$ExpectedSha256 = '')
    $name = Split-Path $File -Leaf
    if ($ExpectedSha256 -and ($Sha256 -ne $ExpectedSha256)) { return New-CheckResult 'Software' "Installer $name" "SHA-256 $Sha256" "SHA-256 $ExpectedSha256" 'FAIL' 'checksum differs: the file is damaged or not the one listed' }
    if ($SignatureStatus -ne 'Valid') { return New-CheckResult 'Software' "Installer $name" "signature $SignatureStatus" 'valid signature' 'FAIL' 'not signed or the signature is broken' }
    if ($Signer -notmatch $SignerPattern) { return New-CheckResult 'Software' "Installer $name" "signed by $Signer" "signed by $SignerPattern" 'FAIL' 'signed by someone else' }
    return New-CheckResult 'Software' "Installer $name" "signed by $Signer$(if ($ExpectedSha256) { ', checksum matches' })" 'valid signature' 'PASS'
}

function Get-OverallVerdict {
    param([object[]]$Results)
    if (@($Results | Where-Object Verdict -eq 'FAIL').Count -gt 0) { return 'FAIL' }
    if (@($Results | Where-Object Verdict -eq 'WARN').Count -gt 0) { return 'WARN' }
    return 'PASS'
}

function ConvertTo-ReportText {
    param([object[]]$Results, [hashtable]$Meta)
    $lines = @()
    foreach ($k in ($Meta.Keys | Sort-Object)) { $lines += "{0}: {1}" -f $k, $Meta[$k] }
    $lines += "Overall: $(Get-OverallVerdict $Results)"
    foreach ($g in ($Results | Select-Object -ExpandProperty Group -Unique)) {
        $lines += ''
        $lines += "== $g"
        foreach ($x in ($Results | Where-Object Group -eq $g)) {
            $line = '{0,-5} {1}: {2}' -f $x.Verdict, $x.Check, $x.Found
            if ($x.Required) { $line += "  (needs $($x.Required))" }
            if ($x.Note) { $line += " - $($x.Note)" }
            $lines += $line
        }
    }
    return ($lines -join [Environment]::NewLine)
}

# Writes <base>.json and <base>.txt; with -Append the results are added to what the report holds
# (setup adds the PLC and self-test results to the pre-install check's report).
function Write-CheckReport {
    param([string]$BasePath, [object[]]$Results, [hashtable]$Meta = @{}, [switch]$Append)
    $all = @()
    $m = @{}
    if ($Append -and (Test-Path "$BasePath.json")) {
        $old = Get-Content "$BasePath.json" -Raw | ConvertFrom-Json
        $all += @($old.results)
        foreach ($p in $old.meta.PSObject.Properties) { $m[$p.Name] = $p.Value }
    }
    foreach ($k in $Meta.Keys) { $m[$k] = $Meta[$k] }
    $all += @($Results)
    $doc = [pscustomobject]@{ overall = (Get-OverallVerdict $all); meta = $m; results = $all }
    $dir = Split-Path $BasePath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $doc | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 "$BasePath.json"
    ConvertTo-ReportText $all $m | Set-Content -Encoding UTF8 "$BasePath.txt"
    return $doc.overall
}

Export-ModuleMember -Function Get-PreflightLimits, New-CheckResult, Compare-Version, Test-Component, Test-WindowsVersion, Test-Cpu, Test-Ram, Test-Disk, Test-Screen, Test-Network, Test-Usb, Test-InstallerTrust, Get-OverallVerdict, ConvertTo-ReportText, Write-CheckReport
