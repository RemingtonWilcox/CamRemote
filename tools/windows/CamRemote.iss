[Setup]
AppName=CamRemote
AppVersion=1.0
AppPublisher=CamRemote
DefaultDirName={autopf}\CamRemote
DefaultGroupName=CamRemote
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist
OutputBaseFilename=CamRemote-Windows-Setup
SetupIconFile=..\..\app\CamRemote\CamRemote.ico
UninstallDisplayIcon={app}\CamRemote.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\..\build\win-publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\CamRemote"; Filename: "{app}\CamRemote.exe"
Name: "{autodesktop}\CamRemote"; Filename: "{app}\CamRemote.exe"

[Run]
Filename: "{app}\CamRemote.exe"; Description: "Open CamRemote"; Flags: nowait postinstall skipifsilent
