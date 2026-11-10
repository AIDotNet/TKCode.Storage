; Inno Setup script for O_O (Windows). Built by build/windows/package.ps1 with /DAppVersion, /DArch, /DSourceDir, /DOutputDir, /DIconFile.
; Per-user install (no administrator rights), Start menu entry, optional start with Windows, uninstaller.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6B0F3E8A-2C1D-4E5F-9A7B-0D3C5E1F2A40}
AppName=O_O
AppVersion={#AppVersion}
AppPublisher=AIDotNet
AppPublisherURL=https://github.com/AIDotNet/TKCode.Storage
DefaultDirName={localappdata}\Programs\O_O
DefaultGroupName=O_O
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=O_O-{#AppVersion}-windows-{#Arch}-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\O_O.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes

[Languages]
; Inno Setup ships Simplified Chinese as an unofficial translation: package.ps1 downloads it next to this script.
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "开机时启动 O_O"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\O_O"; Filename: "{app}\O_O.exe"
Name: "{userstartup}\O_O"; Filename: "{app}\O_O.exe"; Tasks: startup

[Run]
Filename: "{app}\O_O.exe"; Description: "启动 O_O"; Flags: nowait postinstall skipifsilent
