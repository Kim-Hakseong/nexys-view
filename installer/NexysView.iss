; Nexys VIEW — Inno Setup script (beta.md §2). Build: ISCC.exe installer\NexysView.iss
; Input: ..\app\dist\app\* (dotnet publish, self-contained win-x64), bootstrapper MicrosoftEdgeWebview2Setup.exe in installer\redist\
#define MyAppName "Nexys VIEW"
#define MyAppVersion "1.1.0-beta.2"
#define MyAppVersionText "1.1.0 beta 2"
#define MyAppPublisher "Nexys"
#define MyAppExeName "NexysView.exe"
#define MyAppUrl "https://github.com/Kim-Hakseong/nexys-view"

[Setup]
AppId={{7E2C6D1A-5B34-4F8E-9C21-NEXYSVIEW090}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersionText}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersionText}
OutputDir=..\app\dist
OutputBaseFilename=NexysView-{#MyAppVersion}-setup
SetupIconFile=..\app\NexysView.App\Assets\brand\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
ChangesAssociations=yes
LicenseFile=..\installer\LICENSE.txt
VersionInfoVersion=1.1.0.0
VersionInfoProductTextVersion={#MyAppVersionText}
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_tdms"; Description: ".tdms 파일을 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked
Name: "assoc_tdm"; Description: ".tdm 파일을 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked
Name: "assoc_dxd"; Description: ".dxd 파일을 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked
Name: "assoc_mf4"; Description: ".mf4 / .mdf 파일을 {#MyAppName}으로 열기"; GroupDescription: "파일 연결 (선택):"; Flags: unchecked

[Files]
Source: "..\app\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\installer\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\installer\redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; file associations (optional tasks, default off)
Root: HKA; Subkey: "Software\Classes\.tdms\OpenWithProgids"; ValueType: string; ValueName: "NexysView.tdms"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_tdms
Root: HKA; Subkey: "Software\Classes\NexysView.tdms"; ValueType: string; ValueName: ""; ValueData: "TDMS measurement file"; Flags: uninsdeletekey; Tasks: assoc_tdms
Root: HKA; Subkey: "Software\Classes\NexysView.tdms\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_tdms
Root: HKA; Subkey: "Software\Classes\NexysView.tdms\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_tdms
Root: HKA; Subkey: "Software\Classes\.tdm\OpenWithProgids"; ValueType: string; ValueName: "NexysView.tdm"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_tdm
Root: HKA; Subkey: "Software\Classes\NexysView.tdm"; ValueType: string; ValueName: ""; ValueData: "TDM measurement file"; Flags: uninsdeletekey; Tasks: assoc_tdm
Root: HKA; Subkey: "Software\Classes\NexysView.tdm\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_tdm
Root: HKA; Subkey: "Software\Classes\NexysView.tdm\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_tdm
Root: HKA; Subkey: "Software\Classes\.dxd\OpenWithProgids"; ValueType: string; ValueName: "NexysView.dxd"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_dxd
Root: HKA; Subkey: "Software\Classes\NexysView.dxd"; ValueType: string; ValueName: ""; ValueData: "DXD measurement file"; Flags: uninsdeletekey; Tasks: assoc_dxd
Root: HKA; Subkey: "Software\Classes\NexysView.dxd\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_dxd
Root: HKA; Subkey: "Software\Classes\NexysView.dxd\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_dxd
Root: HKA; Subkey: "Software\Classes\.mf4\OpenWithProgids"; ValueType: string; ValueName: "NexysView.mf4"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mf4"; ValueType: string; ValueName: ""; ValueData: "MDF measurement file"; Flags: uninsdeletekey; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mf4\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mf4\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\.mdf\OpenWithProgids"; ValueType: string; ValueName: "NexysView.mdf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mdf"; ValueType: string; ValueName: ""; ValueData: "MDF measurement file"; Flags: uninsdeletekey; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc_mf4
Root: HKA; Subkey: "Software\Classes\NexysView.mdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_mf4

[Run]
; WebView2 Evergreen runtime: installed silently when missing (bootstrapper downloads the current runtime)
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft Edge WebView2 런타임 설치 중…"; Check: not WebView2Installed; Flags: waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Evergreen runtime detection (Microsoft documented keys; 'pv' present and not 0.0.0.0)
function WebView2Installed: Boolean;
var
  pv: string;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
    Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
      Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
      Result := (pv <> '') and (pv <> '0.0.0.0');
end;
