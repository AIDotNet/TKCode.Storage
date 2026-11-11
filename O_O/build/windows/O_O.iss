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

[Code]
// 数据目录 (GitHub issue #1): a fresh install may keep O_O's data elsewhere than %APPDATA%\O_O, e.g. on another drive.
// The choice goes into settings.json, which always stays in %APPDATA%\O_O; later O_O moves the data itself
// (控制台 → 关于 → 数据目录). Updates and reinstalls keep the existing settings and skip the page.
var
  DataDirPage: TInputDirWizardPage;

function DefaultDataDir: String;
begin
  Result := ExpandConstant('{userappdata}\O_O');
end;

function SettingsFile: String;
begin
  Result := DefaultDataDir + '\settings.json';
end;

procedure InitializeWizard;
begin
  DataDirPage := CreateInputDirPage(wpSelectDir, '选择数据目录', 'O_O 把插件、插件数据和偏好保存在哪里？',
    '默认保存在当前用户的应用数据文件夹。可以改到其他磁盘；以后也能在 O_O 的“控制台 → 关于 → 数据目录”中更改，数据会一起迁移。',
    False, 'O_O');
  DataDirPage.Add('');
  DataDirPage.Values[0] := DefaultDataDir;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = DataDirPage.ID) and FileExists(SettingsFile);
end;

function IsEmptyDir(Dir: String): Boolean;
var
  Rec: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function JsonEscape(S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
  Lines: TArrayOfString;
begin
  if (CurStep <> ssPostInstall) or FileExists(SettingsFile) then
    Exit;
  Dir := RemoveBackslashUnlessRoot(Trim(DataDirPage.Values[0]));
  if (Dir = '') or (CompareText(Dir, DefaultDataDir) = 0) then
    Exit;
  // Like the app: a folder that already holds other files gets an O_O folder inside it.
  if DirExists(Dir) and not IsEmptyDir(Dir) then
    Dir := AddBackslash(Dir) + 'O_O';
  if not ForceDirectories(Dir) then
    Exit;
  ForceDirectories(DefaultDataDir);
  SetArrayLength(Lines, 1);
  Lines[0] := '{ "DataDirectory": "' + JsonEscape(Dir) + '" }';
  SaveStringsToUTF8File(SettingsFile, Lines, False);
end;
