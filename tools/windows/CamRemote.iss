[Setup]
; AppId stays CamRemote, the old name, so Setup updates an existing install instead of adding a second one.
AppId=CamRemote
AppName=RemoteCam
AppVersion=1.0.13
AppPublisher=Remington Wilcox
DefaultDirName={autopf}\CamRemote
DefaultGroupName=RemoteCam
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

[InstallDelete]
; The shortcuts from before the app was called RemoteCam.
Type: files; Name: "{autoprograms}\CamRemote.lnk"
Type: files; Name: "{autodesktop}\CamRemote.lnk"

[Icons]
Name: "{autoprograms}\RemoteCam"; Filename: "{app}\CamRemote.exe"
Name: "{autodesktop}\RemoteCam"; Filename: "{app}\CamRemote.exe"

[Run]
Filename: "{app}\CamRemote.exe"; Description: "Open RemoteCam"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.old"
Type: files; Name: "{app}\CrAdapter\*.old"

[Code]
// CamRemote 1.0.8 and older, closed while still looking for or connecting to the camera, can leave a copy
// that Windows can't finish closing, which holds its files until a restart. Windows still lets those files
// be renamed, so move them aside and install the new ones in their place. The app removes them later.
function InUse(const Path: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := False;
  try
    Stream := TFileStream.Create(Path, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := True;
  end;
end;

procedure MoveAside(const Dir: String);
var
  Find: TFindRec;
  Path: String;
begin
  if FindFirst(Dir + '\*', Find) then
  try
    repeat
      Path := Dir + '\' + Find.Name;
      if (Find.Name <> '.') and (Find.Name <> '..') then
      begin
        if Find.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          MoveAside(Path)
        else if CompareText(ExtractFileExt(Path), '.old') = 0 then
          DeleteFile(Path)
        else if InUse(Path) then
        begin
          DeleteFile(Path + '.old');
          if RenameFile(Path, Path + '.old') then Log('Moved aside a file still in use: ' + Path);
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

// By now Setup has already asked any CamRemote that's open to close, so what's still in use is stuck.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and DirExists(ExpandConstant('{app}')) then MoveAside(ExpandConstant('{app}'));
end;
