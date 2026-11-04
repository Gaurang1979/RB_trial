; Inno Setup 6 script for the Vision System line PC package. build-package.ps1 runs it:
;   ISCC /DAppVersion=1.0.0 /DSourceDir=<dist\VisionSystem-...-win-x64> /DOutputDir=<dist> VisionSystem.iss
; Installs to C:\Program Files\VisionSystem and offers the first-run setup at the end.
; Not signed: Windows SmartScreen asks "Run anyway" on the first start.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #error SourceDir (the package folder) is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6A2D4C1E-6F3B-4C7A-9E51-7D0F2B9A4E10}
AppName=Vision System
AppVersion={#AppVersion}
AppPublisher=Sundaram Tech
DefaultDirName={autopf}\VisionSystem
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Vision System
OutputDir={#OutputDir}
OutputBaseFilename=VisionSystem-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayName=Vision System {#AppVersion}

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "\vs-runtime\vs-runtime.json,\teach\appsettings.json,\prereqs\*"; Flags: recursesubdirs ignoreversion
; the pre-install check runs from {tmp} before anything is copied (PrepareToInstall below)
Source: "{#SourceDir}\preflight.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#SourceDir}\preflight-rules.psm1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#SourceDir}\VERSION.txt"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#SourceDir}\prereqs\*"; DestDir: "{tmp}\prereqs"; Flags: dontcopy skipifsourcedoesntexist
; settings the first-run setup changes are kept on an update
Source: "{#SourceDir}\vs-runtime\vs-runtime.json"; DestDir: "{app}\vs-runtime"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#SourceDir}\teach\appsettings.json"; DestDir: "{app}\teach"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\Vision Studio"; Filename: "{app}\vs-studio\vs-studio.exe"
Name: "{group}\Teach (setup)"; Filename: "{app}\Teach.bat"
Name: "{group}\Self-test"; Filename: "{app}\Self-test.bat"
Name: "{group}\Collect report"; Filename: "{app}\Collect-report.bat"
Name: "{group}\First-run setup"; Filename: "{app}\Setup.bat"
Name: "{group}\Install and test guide"; Filename: "{app}\WINDOWS-INSTALL-AND-TEST.md"

[InstallDelete]
Type: filesandordirs; Name: "{app}\vs-runtime\*.dll"
Type: filesandordirs; Name: "{app}\vs-studio"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\first-run-setup.ps1"" -PreflightDone"; \
  Description: "Run the first-run setup now (service, firewall, network cards, power, self-test)"; \
  Flags: postinstall runascurrentuser waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\first-run-setup.ps1"" -Uninstall"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
// Owner, 2026-10-30: supporting software (with versions) and the hardware are checked, and missing
// software installed, before the vision software is installed. preflight.ps1 does both and writes
// C:\ProgramData\VisionSystem\data\install-report.txt; a FAIL stops the install here.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  rc: Integer;
begin
  Result := '';
  ExtractTemporaryFile('preflight.ps1');
  ExtractTemporaryFile('preflight-rules.psm1');
  ExtractTemporaryFile('VERSION.txt');
  try
    ExtractTemporaryFiles('{tmp}\prereqs\*');
  except
    Log('no prereqs in this installer');
  end;
  if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\preflight.ps1') +
      '" -PrereqDir "' + ExpandConstant('{tmp}\prereqs') + '" -Pause', '', SW_SHOW, ewWaitUntilTerminated, rc) then
    Result := 'The pre-install check could not start (Windows PowerShell).'
  else if rc <> 0 then
    Result := 'The pre-install check found a problem. Nothing was installed. See C:\ProgramData\VisionSystem\data\install-report.txt, fix the FAIL lines and run Setup again.';
end;
