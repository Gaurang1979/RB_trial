# Pester 5 tests of the pre-install check rules with made-up PC facts; they run on any PC:
#   pwsh -c "Invoke-Pester tools/windows/tests"
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\setup\preflight-rules.psm1') -Force
    function Nic($name, $desc = 'Intel(R) Ethernet Controller I210', $up = $true, $speed = 1000, $jumbo = 9014) {
        [pscustomobject]@{ Name = $name; Description = $desc; Up = $up; SpeedMbps = $speed; JumboMax = $jumbo; DriverProvider = 'Intel'; DriverVersion = '12.19.2.45' }
    }
}

Describe 'Compare-Version' {
    It 'compares by numbers, not text' {
        Compare-Version '4.10.0' '4.8.2' | Should -Be 1
        Compare-Version '4.8.2' '4.8.10' | Should -Be -1
    }
    It 'treats missing parts as zero' {
        Compare-Version '4.8' '4.8.0.0' | Should -Be 0
        Compare-Version '4.8.2.1' '4.8.2' | Should -Be 1
    }
    It 'reads the numbers out of a product text' {
        Compare-Version 'V5.1.0 build 20260909' '5.1.0' | Should -Be 1
    }
    It 'puts a missing version below any real one' {
        Compare-Version '' '0.1' | Should -Be -1
        Compare-Version 'none' '' | Should -Be 0
    }
}

Describe 'Test-Component' {
    It 'fails a required component that is missing or too old' {
        (Test-Component -Name 'MVS' -Installed '' -Minimum '4.8.2').Verdict | Should -Be 'FAIL'
        (Test-Component -Name 'MVS' -Installed '4.7.9' -Minimum '4.8.2').Verdict | Should -Be 'FAIL'
    }
    It 'only warns for an optional one' {
        (Test-Component -Name 'x' -Installed '' -Minimum '1.0' -Required $false).Verdict | Should -Be 'WARN'
    }
    It 'passes the minimum and newer' {
        (Test-Component -Name 'MVS' -Installed '4.8.2.0' -Minimum '4.8.2' -Tested '4.8.2').Verdict | Should -Be 'PASS'
        (Test-Component -Name 'MVS' -Installed '4.9.0' -Minimum '4.8.2').Verdict | Should -Be 'PASS'
    }
}

Describe 'Test-WindowsVersion' {
    It 'refuses 32-bit and builds before 1809' {
        $r = Test-WindowsVersion -Build 17134 -Caption 'Microsoft Windows 10 Pro' -Is64Bit $false
        ($r | Where-Object Check -eq 'Windows 64-bit').Verdict | Should -Be 'FAIL'
        ($r | Where-Object Check -eq 'Windows build').Verdict | Should -Be 'FAIL'
    }
    It 'passes IoT Enterprise and warns on Home' {
        (Test-WindowsVersion -Build 19045 -Caption 'Microsoft Windows 10 IoT Enterprise LTSC' -Is64Bit $true | Where-Object Verdict -ne 'PASS') | Should -BeNullOrEmpty
        (Test-WindowsVersion -Build 22631 -Caption 'Microsoft Windows 11 Home' -Is64Bit $true | Where-Object Check -eq 'Windows edition').Verdict | Should -Be 'WARN'
    }
}

Describe 'CPU, memory, disk and screen limits' {
    It 'refuses below the minimum, warns below the recommendation' {
        (Test-Cpu -Name 'i3' -Cores 2 -Threads 4).Verdict | Should -Be 'FAIL'
        (Test-Cpu -Name 'i5' -Cores 6 -Threads 12).Verdict | Should -Be 'WARN'
        (Test-Cpu -Name 'i7' -Cores 8 -Threads 16).Verdict | Should -Be 'PASS'
        (Test-Ram -Gb 4).Verdict | Should -Be 'FAIL'
        (Test-Ram -Gb 7.8).Verdict | Should -Be 'WARN'    # an 8 GB PC reports a little less
        (Test-Ram -Gb 15.7).Verdict | Should -Be 'PASS'
    }
    It 'refuses too little working space and warns on a hard disk' {
        $r = Test-Disk -Drive 'C:' -FreeGb 10 -MediaType 'HDD'
        ($r | Where-Object Check -eq 'Disk space').Verdict | Should -Be 'FAIL'
        ($r | Where-Object Check -eq 'Disk type').Verdict | Should -Be 'WARN'
        (Test-Disk -Drive 'C:' -FreeGb 40 -MediaType 'SSD' | Where-Object Check -eq 'Disk space').Verdict | Should -Be 'WARN'
        (Test-Disk -Drive 'C:' -FreeGb 400 -MediaType 'SSD' | Where-Object Verdict -ne 'PASS') | Should -BeNullOrEmpty
        (Test-Disk -Drive 'C:' -FreeGb 400 -MediaType '' | Where-Object Check -eq 'Disk type').Verdict | Should -Be 'INFO'
    }
    It 'warns for a screen smaller than 1366x768 or none' {
        (Test-Screen -Width 1024 -Height 768).Verdict | Should -Be 'WARN'
        (Test-Screen -Width 0 -Height 0).Verdict | Should -Be 'WARN'
        (Test-Screen -Width 1366 -Height 768).Verdict | Should -Be 'PASS'
        (Test-Screen -Width 1920 -Height 1080).Verdict | Should -Be 'PASS'
    }
}

Describe 'Test-Network' {
    It 'wants one port per GigE camera and one for the PLC' {
        $r = Test-Network -Adapters @((Nic 'E1'), (Nic 'E2'), (Nic 'E3')) -GigeCameras 2
        ($r | Where-Object Check -eq 'Network ports').Verdict | Should -Be 'PASS'
        ($r | Where-Object Verdict -ne 'PASS') | Should -BeNullOrEmpty
    }
    It 'warns when cameras and PLC must share, fails with no port' {
        (Test-Network -Adapters @((Nic 'E1')) -GigeCameras 2 | Where-Object Check -eq 'Network ports').Verdict | Should -Be 'WARN'
        (Test-Network -Adapters @() -GigeCameras 2 | Where-Object Check -eq 'Network ports').Verdict | Should -Be 'FAIL'
        (Test-Network -Adapters @() -GigeCameras 0 -PlcPort $false | Where-Object Check -eq 'Network ports').Verdict | Should -Be 'PASS'
    }
    It 'warns on a card without jumbo frames or below 1 Gbit/s' {
        $r = Test-Network -Adapters @((Nic 'E1' -jumbo 0), (Nic 'E2' -speed 100), (Nic 'E3' -desc 'Realtek PCIe GbE')) -GigeCameras 2
        ($r | Where-Object Check -eq 'Network port E1').Verdict | Should -Be 'WARN'
        ($r | Where-Object Check -eq 'Network port E2').Verdict | Should -Be 'WARN'
        ($r | Where-Object Check -eq 'Network port E3').Note | Should -Match 'Intel'
    }
    It 'does not judge the speed of a card without a cable' {
        (Test-Network -Adapters @((Nic 'E1' -up $false -speed 0)) -GigeCameras 0 -PlcPort $true | Where-Object Check -eq 'Network port E1').Verdict | Should -Be 'PASS'
    }
}

Describe 'Test-Usb' {
    It 'needs a USB3 controller for USB3 cameras' {
        (Test-Usb -Usb3Controllers 0 -UsbCameras 2).Verdict | Should -Be 'FAIL'
        (Test-Usb -Usb3Controllers 1 -UsbCameras 2).Verdict | Should -Be 'WARN'
        (Test-Usb -Usb3Controllers 2 -UsbCameras 2).Verdict | Should -Be 'PASS'
        (Test-Usb -Usb3Controllers 0 -UsbCameras 0).Verdict | Should -Be 'INFO'
    }
}

Describe 'Test-InstallerTrust' {
    It 'runs only a validly signed installer from the expected maker' {
        (Test-InstallerTrust -File 'p\MVS.exe' -SignatureStatus 'Valid' -Signer 'CN=Hangzhou Hikrobot Co., Ltd.' -SignerPattern 'Hikrobot|Hikvision').Verdict | Should -Be 'PASS'
        (Test-InstallerTrust -File 'p\MVS.exe' -SignatureStatus 'NotSigned' -Signer '' -SignerPattern 'Hikrobot').Verdict | Should -Be 'FAIL'
        (Test-InstallerTrust -File 'p\MVS.exe' -SignatureStatus 'Valid' -Signer 'CN=Someone Else' -SignerPattern 'Hikrobot').Verdict | Should -Be 'FAIL'
    }
    It 'fails a checksum that differs from SHA256SUMS.txt' {
        (Test-InstallerTrust -File 'MVS.exe' -SignatureStatus 'Valid' -Signer 'Hikrobot' -SignerPattern 'Hikrobot' -Sha256 'AA' -ExpectedSha256 'BB').Verdict | Should -Be 'FAIL'
        (Test-InstallerTrust -File 'MVS.exe' -SignatureStatus 'Valid' -Signer 'Hikrobot' -SignerPattern 'Hikrobot' -Sha256 'AA' -ExpectedSha256 'AA').Verdict | Should -Be 'PASS'
    }
}

Describe 'Report' {
    It 'is FAIL when any check failed, WARN when one warned' {
        Get-OverallVerdict @((New-CheckResult 'A' 'a' 1 1 'PASS'), (New-CheckResult 'A' 'b' 1 1 'WARN')) | Should -Be 'WARN'
        Get-OverallVerdict @((New-CheckResult 'A' 'a' 1 1 'INFO'), (New-CheckResult 'A' 'b' 1 1 'FAIL')) | Should -Be 'FAIL'
        Get-OverallVerdict @((New-CheckResult 'A' 'a' 1 1 'INFO')) | Should -Be 'PASS'
    }
    It 'writes text and JSON, and appends later results' {
        $base = Join-Path $TestDrive 'data/install-report'
        Write-CheckReport -BasePath $base -Results @(Test-Ram -Gb 32) -Meta @{ Package = '1.0.0' } | Should -Be 'PASS'
        Write-CheckReport -BasePath $base -Results @(New-CheckResult 'Self-test' 'cameras' 'none' 'two' 'FAIL') -Meta @{ SelfTest = 'run' } -Append | Should -Be 'FAIL'
        $j = Get-Content "$base.json" -Raw | ConvertFrom-Json
        $j.overall | Should -Be 'FAIL'
        @($j.results).Count | Should -Be 2
        $j.meta.Package | Should -Be '1.0.0'
        $t = Get-Content "$base.txt" -Raw
        $t | Should -Match 'Overall: FAIL'
        $t | Should -Match '== Self-test'
        $t | Should -Match 'PASS  Memory: 32.0 GB'
    }
}
