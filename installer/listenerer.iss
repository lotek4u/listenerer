; Listenerer installer — bot + config tool + prerequisites.
; Deliberately asks for NO Entra credentials: configuration happens afterwards
; in the Listenerer Config tool (offered at the end of setup).
; Build: ISCC listenerer.iss   (publish/bot and publish/config must exist — see LISTENERER.md)

#define AppVersion "0.1.1"

[Setup]
AppId={{7E1B9A44-52C3-4E8F-9D06-listenerer01}
AppName=Listenerer
AppVersion={#AppVersion}
AppPublisher=Nick Barnett
DefaultDirName={autopf}\Listenerer
DefaultGroupName=Listenerer
OutputDir=out
OutputBaseFilename=ListenererSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\ListenererConfig.exe
WizardStyle=modern

[Files]
; Both apps are self-contained .NET publishes — no .NET runtime prerequisite.
; They share identical runtime DLLs, so merging into one folder is safe.
Source: "..\publish\bot\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\publish\config\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\src\RecordingBot.Console\.env-template"; DestDir: "{app}"; Flags: ignoreversion
Source: "vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\Listenerer Config"; Filename: "{app}\ListenererConfig.exe"
Name: "{group}\Listenerer Bot (console)"; Filename: "{app}\RecordingBot.Console.exe"
Name: "{group}\Recordings folder"; Filename: "{app}"

[Run]
; VC++ 2015-2022 runtime — required by the Microsoft.Skype.Bots.Media native libs
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing Visual C++ runtime (media SDK prerequisite)..."; Check: VCRedistNeeded
; Default firewall openings for signaling + media (adjust if ports are changed in config)
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Listenerer Signaling"" dir=in action=allow protocol=TCP localport=9441"; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Listenerer Media"" dir=in action=allow protocol=TCP localport=8445"; Flags: runhidden
; Configuration happens HERE, not during install
Filename: "{app}\ListenererConfig.exe"; Description: "Open Listenerer Config now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Listenerer Signaling"""; Flags: runhidden; RunOnceId: "fw1"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Listenerer Media"""; Flags: runhidden; RunOnceId: "fw2"

[Code]
function VCRedistNeeded: Boolean;
var
  Installed: Cardinal;
begin
  Result := True;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
                        'Installed', Installed) then
    Result := Installed <> 1;
end;
