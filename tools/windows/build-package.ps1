<#
.SYNOPSIS
  Builds the Vision System line PC package on a Windows 10/11 x64 PC, in one go.

.DESCRIPTION
  Checks the tools (Visual Studio Build Tools with C++, Git, .NET 10 SDK; Inno Setup is optional)
  and offers to install what is missing with winget; builds the C++ engine (codereader, vs_ocr,
  vs_locate with OpenCV, zxing-cpp and libdmtx from vcpkg); runs the engine tests and the .NET
  tests of the line PC programs; publishes vs-runtime (the runtime service), vs-studio (Vision
  Studio) and the teach app self-contained for win-x64 (no .NET install needed on the line PC);
  and puts it all in dist\:
    dist\VisionSystem-<version>-win-x64\        the package folder
    dist\VisionSystem-<version>-win-x64.zip      the same, zipped
    dist\VisionSystem-Setup-<version>.exe        an installer, when Inno Setup 6 is installed

  Easiest: double-click tools\windows\build-windows.bat. The first build compiles OpenCV and
  takes 20-40 minutes; later builds reuse it and take a few minutes.

.PARAMETER SkipTests
  Do not run the engine and .NET tests (faster; only for a rebuild of code already tested).

.PARAMETER Yes
  Install missing tools without asking.

.PARAMETER MvsSdkDir
  Another Hikrobot MVS SDK folder for dotnet\HikCamera (default: third_party\hikrobot-mvs-sdk).
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$Yes,
    [string]$MvsSdkDir = ""
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Compress-Archive and Invoke-WebRequest are much faster without it

# vcpkg at the commit engine\vcpkg.json and .github\workflows\windows-build.yml use
$VcpkgCommit = 'a073c28e52d920cb2465a17512324b3b6684d03a'
$Triplet = 'x64-windows-static-md'   # static libraries, dynamic C runtime: the engine DLLs need no OpenCV DLLs
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Version = '1.0.0'
$Started = Get-Date

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail($text) {
    Write-Host ''
    Write-Host "BUILD FAILED: $text" -ForegroundColor Red
    Write-Host "The full log is in $Log; send it back if the cause is not clear." -ForegroundColor Red
    try { Stop-Transcript | Out-Null } catch { Write-Verbose 'no transcript' }
    exit 1
}
function Run($what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { Fail "$what (exit code $LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force (Join-Path $Root 'dist') | Out-Null
$Log = Join-Path $Root "dist\build-$(Get-Date -Format yyyyMMdd-HHmmss).log"
Start-Transcript -Path $Log | Out-Null
Set-Location $Root

# ---------------------------------------------------------------- 1. prerequisites
Step 'Checking the tools'
if (-not [Environment]::Is64BitOperatingSystem) { Fail 'this needs 64-bit Windows 10 or 11' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
function Find-VS {
    if (-not (Test-Path $vswhere)) { return $null }
    $p = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($p) { return ($p | Select-Object -First 1) } else { return $null }
}
function Has-DotNet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    try { return [bool](& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }) } catch { return $false }
}
function Find-Iscc {
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

$missing = @()
if (-not (Find-VS)) {
    $missing += [pscustomobject]@{ Name = 'Visual Studio 2022 Build Tools with "Desktop development with C++"'
        Id = 'Microsoft.VisualStudio.2022.BuildTools'
        Args = @('--override', '--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended') }
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    $missing += [pscustomobject]@{ Name = 'Git'; Id = 'Git.Git'; Args = @() }
}
if (-not (Has-DotNet10)) {
    $missing += [pscustomobject]@{ Name = '.NET 10 SDK'; Id = 'Microsoft.DotNet.SDK.10'; Args = @() }
}
$iscc = Find-Iscc
if (-not $iscc) {
    $missing += [pscustomobject]@{ Name = 'Inno Setup 6 (optional: makes the Setup .exe; without it you get the zip)'; Id = 'JRSoftware.InnoSetup'; Args = @() }
}

if ($missing.Count -gt 0) {
    Write-Host 'Missing:'
    $missing | ForEach-Object { Write-Host "  - $($_.Name)" }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail 'winget is not on this PC; install the tools above by hand (README section "Windows build"), then run this again'
    }
    $go = $Yes
    if (-not $go) { $go = (Read-Host 'Install them now with winget? This needs Administrator rights and can take 15 minutes (Y/N)') -match '^[Yy]' }
    if (-not $go) { Fail 'tools missing' }
    foreach ($m in $missing) {
        Write-Host "Installing $($m.Name) ..."
        & winget install --id $m.Id -e --accept-package-agreements --accept-source-agreements @($m.Args)
        # 0, or "already installed"
        if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) { Write-Host "winget exit code $LASTEXITCODE for $($m.Name)" -ForegroundColor Yellow }
    }
    # pick up the new PATH entries without a new window
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $iscc = Find-Iscc
}

$vs = Find-VS
if (-not $vs) { Fail 'Visual Studio Build Tools with C++ not found after install; restart the PC and run this again' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'Git not found; open a new window and run this again' }
if (-not (Has-DotNet10)) { Fail '.NET 10 SDK not found; open a new window and run this again' }
Write-Host "Visual Studio: $vs"
Write-Host ".NET SDK:      $((& dotnet --version))"
Write-Host "Inno Setup:    $(if ($iscc) { $iscc } else { 'not installed (zip only)' })"

# The x64 compiler, cmake and the redistributable folder from the Visual Studio developer shell
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
Set-Location $Root
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { Fail 'cmake not found in the Visual Studio shell (add "C++ CMake tools for Windows" in the Visual Studio Installer)' }

# ---------------------------------------------------------------- 2. engine
Step 'Submodules (third_party/remote-service: the remote service agent vs-runtime builds with)'
Run 'git submodule update' { git -C $Root submodule update --init --recursive }

Step 'vcpkg (OpenCV, zxing-cpp, libdmtx)'
$Vcpkg = Join-Path $Root '.vcpkg'
if (-not (Test-Path (Join-Path $Vcpkg 'vcpkg.exe'))) {
    if (-not (Test-Path $Vcpkg)) { Run 'git clone vcpkg' { git clone https://github.com/microsoft/vcpkg.git $Vcpkg } }
    Run 'git fetch vcpkg' { git -C $Vcpkg fetch --quiet origin }
    Run 'git checkout vcpkg' { git -C $Vcpkg checkout --quiet $VcpkgCommit }
    Run 'bootstrap vcpkg' { & (Join-Path $Vcpkg 'bootstrap-vcpkg.bat') -disableMetrics }
}

Step 'Engine: configure and build (the first time builds OpenCV: 20-40 minutes)'
$toolchain = Join-Path $Vcpkg 'scripts\buildsystems\vcpkg.cmake'
Run 'cmake configure' { cmake -S engine -B engine\build -A x64 "-DCMAKE_TOOLCHAIN_FILE=$toolchain" "-DVCPKG_TARGET_TRIPLET=$Triplet" }
Run 'cmake build' { cmake --build engine\build --config Release --parallel }
$Rel = Join-Path $Root 'engine\build\Release'
foreach ($f in 'codereader.dll', 'vs_ocr.dll', 'vs_locate.dll', 'bench.exe', 'ocr_bench.exe') {
    if (-not (Test-Path (Join-Path $Rel $f))) { Fail "engine build made no $f" }
}

if (-not $SkipTests) {
    Step 'Engine tests'
    # every engine test program the engine registers (engine/CMakeLists.txt add_test: reader, OCR,
    # locate and each tool DLL's own), the same list on every PC
    Run 'engine tests (ctest)' { ctest --test-dir engine\build -C Release --output-on-failure --parallel $env:NUMBER_OF_PROCESSORS }
    Step '.NET tests (line PC programs)'
    foreach ($t in 'CodeReader.Tests', 'VisionCore.Tests', 'StationGuard.Tests', 'VisionStudio.Tests', 'RuntimeContract.Tests', 'Runtime.Host.Tests', 'Studio.Tests') {
        Run "dotnet test $t" { dotnet test "dotnet\$t" -c Release --nologo }
    }
}

# ---------------------------------------------------------------- 3. .NET programs
Step 'Publishing vs-runtime, vs-studio and the teach app (self-contained, win-x64)'
$Pub = Join-Path $Root 'out\publish'
if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
$sdk = @()
if ($MvsSdkDir) { $sdk = @("-p:MvsSdkDir=$MvsSdkDir") }
Run 'publish vs-runtime' { dotnet publish dotnet\Runtime.Host -c Release -r win-x64 --self-contained true --nologo -o "$Pub\vs-runtime" @sdk }
Run 'publish vs-studio' { dotnet publish dotnet\Studio -c Release -r win-x64 --self-contained true --nologo -o "$Pub\vs-studio" }
Run 'publish teach app' { dotnet publish dotnet\VisionStudio -c Release -r win-x64 --self-contained true --nologo -o "$Pub\teach" @sdk }

# ---------------------------------------------------------------- 4. package
Step 'Making the package'
$Name = "VisionSystem-$Version-win-x64"
$Pkg = Join-Path $Root "dist\$Name"
if (Test-Path $Pkg) { Remove-Item -Recurse -Force $Pkg }
New-Item -ItemType Directory -Force $Pkg | Out-Null

# The C runtime the engine DLLs need (dynamic CRT), copied next to them (app-local, allowed by the
# Visual C++ redistributable licence), so the line PC needs no VC++ runtime install.
$crt = Get-ChildItem (Join-Path $env:VCToolsRedistDir 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $crt) { Fail "no x64 C runtime under $env:VCToolsRedistDir" }
$models = Get-ChildItem (Join-Path $Root 'ml\models') -File | Where-Object { $_.Extension -eq '.onnx' -or $_.Name -eq 'ocr_charset.txt' }

foreach ($app in 'vs-runtime', 'teach') {
    $dst = Join-Path $Pkg $app
    Copy-Item -Recurse "$Pub\$app" $dst
    # every engine DLL the build made (codereader, vs_ocr, vs_locate and tool DLLs added later, such as vs_measure)
    Copy-Item (Join-Path $Rel '*.dll') $dst -Force
    Copy-Item (Join-Path $crt.FullName '*.dll') $dst -Force
    New-Item -ItemType Directory -Force (Join-Path $dst 'models') | Out-Null
    $models | Copy-Item -Destination (Join-Path $dst 'models') -Force
}
# vs-runtime reads vs-runtime.json only; the teach app's program and web settings it carries along are not used
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $Pkg 'vs-runtime\VisionStudio.exe'), (Join-Path $Pkg 'vs-runtime\appsettings.json'), (Join-Path $Pkg 'vs-runtime\appsettings.Development.json')
Copy-Item -Recurse "$Pub\vs-studio" (Join-Path $Pkg 'vs-studio')

$tools = Join-Path $Pkg 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null
Copy-Item (Join-Path $Rel 'codereader.dll'), (Join-Path $Rel 'bench.exe'), (Join-Path $Rel 'vs_ocr.dll'), (Join-Path $Rel 'ocr_bench.exe') $tools
Copy-Item (Join-Path $crt.FullName '*.dll') $tools -Force
Copy-Item (Join-Path $Root 'truth\samples.csv'), (Join-Path $Root 'tools\windows\run-bench.bat') $tools
New-Item -ItemType Directory -Force (Join-Path $tools 'models') | Out-Null
$models | Copy-Item -Destination (Join-Path $tools 'models') -Force

Copy-Item (Join-Path $Root 'tools\windows\setup\*') $Pkg -Recurse -Force
# Supporting installers for the line PC (the Hikrobot MVS installer, and a SHA256SUMS.txt for them):
# put them in third_party\installers (not in git); the pre-install check verifies and runs them.
$inst = Join-Path $Root 'third_party\installers'
if (Test-Path $inst) {
    New-Item -ItemType Directory -Force (Join-Path $Pkg 'prereqs') | Out-Null
    Copy-Item (Join-Path $inst '*') (Join-Path $Pkg 'prereqs') -Force
    Write-Host "prereqs: $((Get-ChildItem (Join-Path $Pkg 'prereqs')).Name -join ', ')"
} else { Write-Host 'prereqs: none (third_party\installers missing): the line PC must have MVS installed, or get its installer in prereqs\' -ForegroundColor Yellow }
Copy-Item (Join-Path $Root 'docs\WINDOWS-INSTALL-AND-TEST.md') $Pkg
$commit = (git rev-parse --short HEAD)
# Owner's test build (requirements section 11 INS-06): the customer-release protection of section 18
# (one dispatcher DLL, encrypted models, Native AOT / single-file, signing) is not applied here.
@("Vision System $Version, commit $commit, built $(Get-Date -Format 'yyyy-MM-dd HH:mm') on $env:COMPUTERNAME",
  'Owner test build, not for customer PCs (requirements section 11 INS-06; licence check off).') | Set-Content (Join-Path $Pkg 'VERSION.txt')

# the package's own check: the engine DLLs load with the copied C runtime, the models load
Step 'Checking the package (vs-runtime self-test, without cameras)'
& (Join-Path $Pkg 'vs-runtime\vs-runtime.exe') self-test "--Studio:JobsFolder=$env:TEMP\vs-selftest\jobs" "--Studio:DataFolder=$env:TEMP\vs-selftest\data" | Tee-Object -Variable st | Out-Host
$bad = $st | Where-Object { $_ -match '^FAIL' -and $_ -notmatch 'camera|job ' }
if ($bad) { Fail "package self-test: $($bad -join '; ')" }

$Zip = Join-Path $Root "dist\$Name.zip"
if (Test-Path $Zip) { Remove-Item -Force $Zip }
Compress-Archive -Path $Pkg -DestinationPath $Zip

$Setup = $null
if ($iscc) {
    Step 'Installer (Inno Setup)'
    Run 'ISCC' { & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$Pkg" "/DOutputDir=$Root\dist" (Join-Path $Root 'tools\windows\VisionSystem.iss') }
    $Setup = Join-Path $Root "dist\VisionSystem-Setup-$Version.exe"
}

$mins = [int]((Get-Date) - $Started).TotalMinutes
Write-Host ''
Write-Host "BUILD OK in $mins min (commit $commit)" -ForegroundColor Green
Write-Host '  Owner test build, not for customer PCs (requirements section 11 INS-06).' -ForegroundColor Yellow
if ($Setup) { Write-Host "  Installer: $Setup" }
Write-Host "  Zip:       $Zip"
Write-Host "  Folder:    $Pkg"
Write-Host 'Next: docs\WINDOWS-INSTALL-AND-TEST.md, part 2 (install on the line PC).'
Stop-Transcript | Out-Null
exit 0
