; Per-user installer: no administrator rights, nothing outside the user's profile.
; Built by build-installer.ps1, which passes AppVersion, PublishDir and OutputDir.
; SmokeTest builds an installer that leaves no trace outside its folder, so it can be tried on a working machine.

#ifndef AppVersion
  #error Build this with build-installer.ps1
#endif

[Setup]
AppId={{6C2F4C57-1B7E-4E0B-9D35-3B4B7E8A2F11}
AppName=ScreenSorter
AppVersion={#AppVersion}
AppPublisher=AVitrum
AppPublisherURL=https://github.com/AVitrum/shot-sort
DefaultDirName={localappdata}\Programs\ScreenSorter
DefaultGroupName=ScreenSorter
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
SetupIconFile=..\src\ScreenSorter.App\Assets\app.ico
UninstallDisplayIcon={app}\ScreenSorter.App.exe
OutputDir={#OutputDir}
OutputBaseFilename=ScreenSorter-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
#ifdef SmokeTest
CreateUninstallRegKey=no
DisableDirPage=no
#ifdef SmokeMutex
AppMutex=Local\ScreenSorter.SingleInstance
#endif
#else
AppMutex=Local\ScreenSorter.SingleInstance
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.lib,DirectML.Debug.dll"; Flags: recursesubdirs ignoreversion

[Code]
// The app's own update passes /UPDATE=1, and nobody is there to start the new version.
function StartAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataFolder: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    exit;
#ifdef SmokeTest
  DataFolder := ExpandFileName(ExpandConstant('{app}\..\data-smoke'));
#else
  DataFolder := ExpandConstant('{localappdata}\ScreenSorter');
#endif
  if not DirExists(DataFolder) then
    exit;
  if MsgBox('Also delete your ScreenSorter data?' + #13#10#13#10 +
      'This removes your settings, sorting history, learned examples, logs and the downloaded image model. ' +
      'Your screenshots stay where they are.' + #13#10#13#10 +
      'Keep the data if you may install ScreenSorter again.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataFolder, True, True, True);
end;

#ifdef SmokeTest
[Run]
; Really starts the new version, as a real update does, but every path it uses is beside the install folder, so this copy
; can never sort the real screenshots folder.
#define SmokeData "{app}\..\data-smoke"
Filename: "{app}\ScreenSorter.App.exe"; Parameters: "--Paths:WatchFolders:0=""{#SmokeData}\shots"" --Paths:SortedRoot=""{#SmokeData}\shots"" --Paths:DatabasePath=""{#SmokeData}\test.db"" --Paths:LogFolder=""{#SmokeData}\logs"" --Paths:UserSettingsPath=""{#SmokeData}\settings.json"" --Clip:ModelPath=""{#SmokeData}\none.onnx"" --Clip:ModelUrl=http://127.0.0.1:9/none --LmStudio:Enabled=false --Updates:Enabled=false"; Flags: nowait; Check: StartAfterUpdate
#else
[Tasks]
Name: "autostart"; Description: "Start ScreenSorter with Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{autoprograms}\ScreenSorter"; Filename: "{app}\ScreenSorter.App.exe"
Name: "{autodesktop}\ScreenSorter"; Filename: "{app}\ScreenSorter.App.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ScreenSorter"; ValueData: """{app}\ScreenSorter.App.exe"""; Tasks: autostart
; The app's own setting writes the same value, so uninstalling removes it however it got there.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ScreenSorter"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\ScreenSorter.App.exe"; Description: "Start ScreenSorter"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ScreenSorter.App.exe"; Flags: nowait; Check: StartAfterUpdate
#endif
