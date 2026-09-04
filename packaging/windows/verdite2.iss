; Inno Setup script for Verdite2.
;
; Run packaging/windows/build-windows.ps1 first: this installs an already
; published tree and does not build one.
;
; The installer deliberately does NOT ask for the disc. The game is built on
; first launch, from the image the player picks in the app itself, into
; %LOCALAPPDATA%\Verdite2 -- so the install directory stays read-only, an
; uninstall leaves saves alone, and reinstalling does not force a rebuild.

#define AppName    "Verdite2"
#define AppVersion GetEnv("VERDITE2_VERSION")
#if AppVersion == ""
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{9F1F0C1E-6A3E-4C69-9C2A-9E5F2B8D4A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Voicedrew11
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\Verdite2.exe
OutputDir=..\..\dist
OutputBaseFilename=Verdite2-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
LicenseFile=..\..\LICENSE
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Verdite2.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Verdite2.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Verdite2.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Saves, settings and the built game live in %LOCALAPPDATA%\Verdite2 and are NOT
; removed: an uninstall should not delete somebody's save file. The built game
; assembly goes with them, which costs a rebuild on reinstall and is the right
; way round -- a lost save cannot be rebuilt.
