; Inno Setup script for NotchHUD. Built by build-release.ps1:
;   ISCC.exe /DMyVersion=1.2.0 /DSourceDir=<self-contained publish folder> /DOutputDir=<dist> installer\NotchHUD.iss

#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir must point at the self-contained publish folder
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6B0F3E7A-2C4D-4E7B-9A51-3D2F8C1E9B40}
AppName=NotchHUD
AppVersion={#MyVersion}
AppVerName=NotchHUD {#MyVersion}
AppPublisher=cagatay-softgineer
AppPublisherURL=https://github.com/cagatay-softgineer/NotchHUD
AppSupportURL=https://github.com/cagatay-softgineer/NotchHUD/issues
AppUpdatesURL=https://github.com/cagatay-softgineer/NotchHUD/releases
DefaultDirName={autopf}\NotchHUD
DefaultGroupName=NotchHUD
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=NotchHUD-{#MyVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\NotchHUD.exe
UninstallDisplayName=NotchHUD
VersionInfoVersion={#MyVersion}
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start NotchHUD when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NotchHUD"; Filename: "{app}\NotchHUD.exe"
Name: "{autodesktop}\NotchHUD"; Filename: "{app}\NotchHUD.exe"; Tasks: desktopicon

[Run]
; Same scheduled task NotchHUD's own "Start with Windows" setting creates (runs elevated at sign-in, no UAC prompt).
Filename: "schtasks.exe"; Parameters: "/Create /TN ""NotchHUD"" /TR ""\""{app}\NotchHUD.exe\"""" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden; Tasks: autostart
Filename: "{app}\NotchHUD.exe"; Description: "Start NotchHUD now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""NotchHUD"" /F"; Flags: runhidden; RunOnceId: "RemoveTask"
Filename: "taskkill.exe"; Parameters: "/IM NotchHUD.exe /F"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
// Ask a running NotchHUD to quit (it saves the current session), then make sure it is gone.
procedure StopNotchHUD();
var
  ResultCode, I: Integer;
begin
  SaveStringToFile(ExpandConstant('{localappdata}\NotchHUD\command.txt'), 'quit', False);
  for I := 1 to 20 do
  begin
    Sleep(250);
    Exec('cmd.exe', '/C tasklist /FI "IMAGENAME eq NotchHUD.exe" | find /I "NotchHUD.exe" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then Exit;
  end;
  Exec('taskkill.exe', '/IM NotchHUD.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopNotchHUD();
  Result := '';
end;
