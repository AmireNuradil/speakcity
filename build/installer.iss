#ifndef PayloadDir
  #define PayloadDir "..\out\app"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\out\release"
#endif
#ifndef BootstrapDir
  #define BootstrapDir "..\out\bootstrap"
#endif
[Setup]
AppId={{78504A9B-391F-4F72-9BDC-FAE4568512E4}
AppName=SPEAKCITY AI
AppVersion=0.2.0
DefaultDirName={localappdata}\Programs\SPEAKCITY AI
DefaultGroupName=SPEAKCITY AI
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#ReleaseDir}
OutputBaseFilename=SPEAKCITY-AI-Setup-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\SpeakCity.exe
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BootstrapDir}\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Icons]
Name: "{autoprograms}\SPEAKCITY AI"; Filename: "{app}\SpeakCity.exe"
Name: "{autodesktop}\SPEAKCITY AI"; Filename: "{app}\SpeakCity.exe"; Tasks: desktopicon
[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the official Microsoft WebView2 prerequisite..."; Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\SpeakCity.exe"; Description: "Open SPEAKCITY AI"; Flags: nowait postinstall skipifsilent
[Code]
function HasVersion(Root: Integer; Key: String): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;
function NeedsWebView2: Boolean;
begin
  Result := not (
    HasVersion(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    HasVersion(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'));
end;
