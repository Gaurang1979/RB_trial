<#
.SYNOPSIS
  Pre-install check of a Vision System line PC (owner, 2026-10-30): supporting software with
  versions, then the hardware, before anything of the vision software is copied.

.DESCRIPTION
  1. Supporting software, each with its minimum and tested version: Windows edition and build,
     PowerShell, the Hikrobot MVS camera SDK runtime and its GenTL producers, and the components the
     package carries itself (.NET 10 runtime, Visual C++ runtime; ONNX models run on OpenCV, no
     ONNX Runtime). A missing or old MVS is installed from prereqs\ (the installer must carry a valid
     Hikrobot signature, and match prereqs\SHA256SUMS.txt when that lists it), then checked again.
  2. Hardware: CPU, memory, GPU (not used), free space for the program and the working files, disk
     type, network ports (one per GigE camera plus one for the PLC, 1 Gbit/s, jumbo frames), USB3
     controllers for USB3 cameras, screen resolution.
  3. Writes C:\ProgramData\VisionSystem\data\install-report.txt and .json. First-run setup adds the
     PLC and self-test results (cameras found) to the same report.
  Exit code: 0 passed (warnings allowed), 1 a check failed: nothing may be installed.

  Run by the installer before it copies files, and by Setup.bat. The rules and limits are in
  preflight-rules.psm1.
#>
[CmdletBinding()]
param(
    [int]$GigeCameras = -1,      # -1 = ask
    [int]$UsbCameras = -1,
    [string]$PrereqDir = '',     # default: prereqs next to this script
    [string]$ReportBase = "$env:ProgramData\VisionSystem\data\install-report",
    [switch]$NoInstall,          # report a missing MVS, do not install it
    [switch]$Pause               # wait for Enter at the end (the installer's window)
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'preflight-rules.psm1') -Force
$L = Get-PreflightLimits
if (-not $PrereqDir) { $PrereqDir = Join-Path $PSScriptRoot 'prereqs' }
$results = @()

function Done($code) {
    if ($Pause) { Read-Host 'Press Enter to close this window' | Out-Null }
    exit $code
}
function Ask-Count($q, $default) {
    $a = Read-Host "$q [$default]"
    if ($a -match '^\d+$') { return [int]$a } else { return $default }
}

Write-Host 'Vision System pre-install check' -ForegroundColor Cyan
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run it as Administrator.' -ForegroundColor Red
    Done 1
}
if ($GigeCameras -lt 0) { $GigeCameras = Ask-Count 'How many GigE cameras?' 2 }
if ($UsbCameras -lt 0) { $UsbCameras = Ask-Count 'How many USB3 cameras?' 0 }

# ---------------------------------------------------------------- 1. supporting software
$os = Get-CimInstance Win32_OperatingSystem
$results += Test-WindowsVersion -Build ([int]$os.BuildNumber) -Caption $os.Caption -Is64Bit ([Environment]::Is64BitOperatingSystem)
$results += Test-Component -Name 'Windows PowerShell' -Installed "$($PSVersionTable.PSVersion)" -Minimum '5.1' -Tested '5.1'

$pkgVersion = ''
$versionFile = Join-Path $PSScriptRoot 'VERSION.txt'
if (Test-Path $versionFile) { $pkgVersion = (Get-Content $versionFile -TotalCount 1) }
$results += New-CheckResult 'Software' '.NET 10 runtime' 'carried by the package (self-contained)' 'nothing to install' 'INFO'
$crt = Get-ChildItem (Join-Path $PSScriptRoot 'vs-runtime') -Filter 'vcruntime140.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
$results += New-CheckResult 'Software' 'Visual C++ runtime' $(if ($crt) { "carried by the package, $($crt.VersionInfo.FileVersion)" } else { 'carried by the package' }) 'nothing to install' 'INFO'
$results += New-CheckResult 'Software' 'ONNX Runtime' 'not used: the AI models run on OpenCV DNN inside the engine DLLs' 'nothing to install' 'INFO'

function Get-Mvs {
    # Hikrobot MVS: the camera SDK runtime (MvCameraControl.dll) and the GigE / USB3 drivers.
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $app = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match '^MVS|Machine Vision Software|Industrial Camera SDK' -and $_.Publisher -match 'Hikrobot|Hikvision' } |
        Select-Object -First 1
    $dirs = @()
    if ($env:MVCAM_COMMON_RUNENV) { $dirs += Join-Path $env:MVCAM_COMMON_RUNENV 'Win64_x64' }
    $dirs += "${env:ProgramFiles(x86)}\Common Files\MVS\Runtime\Win64_x64", "$env:ProgramFiles\Common Files\MVS\Runtime\Win64_x64"
    $dll = $dirs | ForEach-Object { Join-Path $_ 'MvCameraControl.dll' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    return [pscustomobject]@{
        App        = if ($app) { "$($app.DisplayName) $($app.DisplayVersion)" } else { '' }
        Dll        = $dll
        DllVersion = if ($dll) { (Get-Item $dll).VersionInfo.FileVersion } else { '' }
    }
}

$cameraCount = $GigeCameras + $UsbCameras
$mvs = Get-Mvs
$mvsCheck = Test-Component -Name 'Hikrobot MVS camera SDK runtime (MvCameraControl.dll)' -Installed $mvs.DllVersion -Minimum $L.MvsMinVersion -Tested '4.8.2 (MVS 5.1.0)' -Required ($cameraCount -gt 0)
if ($mvsCheck.Verdict -ne 'PASS' -and -not $NoInstall) {
    Write-Host "MVS camera runtime: $($mvsCheck.Found). Looking for its installer in $PrereqDir ..."
    $sums = @{}
    $sumFile = Join-Path $PrereqDir 'SHA256SUMS.txt'
    if (Test-Path $sumFile) {
        foreach ($line in Get-Content $sumFile) { if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1].ToUpper() } }
    }
    $installer = Get-ChildItem $PrereqDir -Filter '*.exe' -ErrorAction SilentlyContinue | Where-Object Name -match 'MVS|Hik|SDK' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $installer) {
        $mvsCheck.Note = "no installer in ${PrereqDir}: download MVS for Windows from the Hikrobot download centre and put the .exe there, or install it, then run this again"
    } else {
        $sig = Get-AuthenticodeSignature $installer.FullName
        $hash = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash
        $trust = Test-InstallerTrust -File $installer.FullName -SignatureStatus "$($sig.Status)" -Signer "$($sig.SignerCertificate.Subject)" `
            -SignerPattern 'Hikrobot|Hikvision' -Sha256 $hash -ExpectedSha256 $(if ($sums.ContainsKey($installer.Name)) { $sums[$installer.Name] } else { '' })
        $results += $trust
        if ($trust.Verdict -eq 'PASS') {
            Write-Host "Installing $($installer.Name) (signed by $($sig.SignerCertificate.Subject)). Follow its screens; this window waits." -ForegroundColor Yellow
            $p = Start-Process -FilePath $installer.FullName -Wait -PassThru
            Write-Host "installer exit code $($p.ExitCode)"
            $env:MVCAM_COMMON_RUNENV = [Environment]::GetEnvironmentVariable('MVCAM_COMMON_RUNENV', 'Machine')
            $mvs = Get-Mvs
            $mvsCheck = Test-Component -Name 'Hikrobot MVS camera SDK runtime (MvCameraControl.dll)' -Installed $mvs.DllVersion -Minimum $L.MvsMinVersion -Tested '4.8.2 (MVS 5.1.0)' -Required ($cameraCount -gt 0)
            $mvsCheck.Note = "checked again after installing $($installer.Name)"
        }
    }
}
if ($mvs.App) { $mvsCheck.Found = "$($mvsCheck.Found) ($($mvs.App))" }
$results += $mvsCheck

$gentl = @()
foreach ($d in ("$([Environment]::GetEnvironmentVariable('GENICAM_GENTL64_PATH', 'Machine'))" -split ';' | Where-Object { $_ })) {
    $gentl += Get-ChildItem $d -Filter '*.cti' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) $($_.VersionInfo.FileVersion)" }
}
$results += New-CheckResult 'Software' 'GenICam GenTL producers' $(if ($gentl) { $gentl -join ', ' } else { 'none' }) 'not used by this build (cameras open through the MVS SDK)' 'INFO'

# ---------------------------------------------------------------- 2. hardware
$cpu = Get-CimInstance Win32_Processor
$results += Test-Cpu -Name (($cpu | Select-Object -First 1).Name.Trim()) -Cores (($cpu | Measure-Object NumberOfCores -Sum).Sum) -Threads (($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
$results += Test-Ram -Gb ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name })
$results += New-CheckResult 'Hardware' 'GPU' ($gpus -join ', ') 'not used by this build (AI runs on the CPU)' 'INFO'

$drive = $env:SystemDrive.TrimEnd(':')
$vol = Get-Volume -DriveLetter $drive
$media = ''
try { $media = "$((Get-Partition -DriveLetter $drive | Get-Disk | Get-PhysicalDisk -ErrorAction Stop).MediaType)" } catch { $media = '' }
if (-not $media) {
    try { $num = (Get-Partition -DriveLetter $drive).DiskNumber; $media = "$((Get-PhysicalDisk | Where-Object DeviceId -eq "$num").MediaType)" } catch { $media = '' }
}
$results += Test-Disk -Drive "${drive}:" -FreeGb ($vol.SizeRemaining / 1GB) -MediaType $media

$nics = @()
foreach ($n in Get-NetAdapter -Physical | Where-Object { $_.MediaType -eq '802.3' -and $_.InterfaceDescription -notmatch 'Wi-?Fi|Wireless|Bluetooth' }) {
    $j = Get-NetAdapterAdvancedProperty -Name $n.Name -RegistryKeyword '*JumboPacket' -ErrorAction SilentlyContinue
    $jmax = 0
    if ($j) {
        $vals = @($j.ValidRegistryValues | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
        if ($vals.Count) { $jmax = ($vals | Measure-Object -Maximum).Maximum } elseif ($j.NumericParameterMaxValue) { $jmax = [int]$j.NumericParameterMaxValue }
    }
    $nics += [pscustomobject]@{
        Name = $n.Name; Description = $n.InterfaceDescription; Up = ($n.Status -eq 'Up')
        SpeedMbps = [int]($n.ReceiveLinkSpeed / 1e6); JumboMax = $jmax
        DriverProvider = $n.DriverProvider; DriverVersion = $n.DriverVersion
    }
}
$results += Test-Network -Adapters $nics -GigeCameras $GigeCameras -PlcPort $true

$usb3 = @(Get-CimInstance Win32_USBController | Where-Object { $_.Name -match 'xHCI|eXtensible|USB ?3' }).Count
$results += Test-Usb -Usb3Controllers $usb3 -UsbCameras $UsbCameras

$screen = Get-CimInstance Win32_VideoController | Where-Object CurrentHorizontalResolution | Sort-Object CurrentHorizontalResolution -Descending | Select-Object -First 1
$results += Test-Screen -Width ([int]$screen.CurrentHorizontalResolution) -Height ([int]$screen.CurrentVerticalResolution)

# ---------------------------------------------------------------- 3. report
$meta = @{
    'Computer' = $env:COMPUTERNAME
    'Checked at' = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    'Package' = $(if ($pkgVersion) { $pkgVersion } else { 'unknown' })
    'Cameras planned' = "$GigeCameras GigE, $UsbCameras USB3"
}
$overall = Write-CheckReport -BasePath $ReportBase -Results $results -Meta $meta
Write-Host ''
Get-Content "$ReportBase.txt" | ForEach-Object {
    $c = if ($_ -match '^FAIL') { 'Red' } elseif ($_ -match '^WARN') { 'Yellow' } else { 'Gray' }
    Write-Host $_ -ForegroundColor $c
}
Write-Host ''
Write-Host "Report: $ReportBase.txt (and .json)"
if ($overall -eq 'FAIL') {
    Write-Host 'Pre-install check FAILED: nothing of the vision software was installed. Fix the FAIL lines and run it again.' -ForegroundColor Red
    Done 1
}
Write-Host "Pre-install check $(if ($overall -eq 'WARN') { 'passed with warnings' } else { 'passed' })." -ForegroundColor Green
Done 0
