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
#else
AppMutex=Local\ScreenSorter.SingleInstance
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.lib,DirectML.Debug.dll"; Flags: recursesubdirs ignoreversion

#ifndef SmokeTest
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
#endif
