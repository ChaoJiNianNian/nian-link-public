#ifndef AppVersion
#error AppVersion is required.
#endif
#ifndef BundleDir
#error BundleDir is required.
#endif
#ifndef ReleaseDir
#error ReleaseDir is required.
#endif
#ifndef BetaId
#error BetaId is required.
#endif
#if !FileExists(BundleDir + "\App\NianLink.DiscoveryPortable.exe")
#error Beta candidate application is missing.
#endif
#if !FileExists(BundleDir + "\Keyboard\deskflow-core.exe")
#error Candidate keyboard engine is missing.
#endif

[Setup]
AppId=NianLink-MultiDevice-Beta-{#BetaId}
AppName=年年互联（多设备测试版）
AppVersion={#AppVersion}
AppPublisher=年年互联
DefaultDirName={code:BetaInstallDir}
DefaultGroupName=年年互联（多设备测试版）-{#BetaId}
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dynamic
SetupIconFile=../assets/brand/NianLink.ico
UninstallDisplayIcon={app}\App\NianLink.DiscoveryPortable.exe
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=NianLink-Multi-Beta-{#AppVersion}-win-x64-TEST-Setup
VersionInfoVersion={#AppVersion}
VersionInfoDescription=年年互联多设备测试安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Excludes: "笔记本-D盘启动.cmd,台式机-H盘启动.cmd"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "languages\ChineseSimplified.LICENSE.txt"; DestDir: "{app}\THIRD-PARTY-NOTICES"; Flags: ignoreversion
Source: "languages\README.md"; DestDir: "{app}\THIRD-PARTY-NOTICES"; DestName: "ChineseSimplified-来源.md"; Flags: ignoreversion

[Icons]
Name: "{group}\年年互联（多设备测试版）"; Filename: "{app}\App\NianLink.DiscoveryPortable.exe"; Parameters: "--beta-portable"; WorkingDir: "{app}\App"
Name: "{group}\卸载年年互联（多设备测试版）"; Filename: "{uninstallexe}"; IconFilename: "{app}\App\NianLink.DiscoveryPortable.exe"
Name: "{group}\测试版使用说明"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\使用说明.txt"""
Name: "{autodesktop}\年年互联（多设备测试版）-{#BetaId}"; Filename: "{app}\App\NianLink.DiscoveryPortable.exe"; Parameters: "--beta-portable"; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\NianLink.DiscoveryPortable.exe"; Parameters: "--beta-portable"; WorkingDir: "{app}\App"; Description: "打开多设备测试版"; Flags: nowait postinstall skipifsilent

[Code]
function GetFileAttributes(Path: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GetDriveType(Path: String): Cardinal;
  external 'GetDriveTypeW@kernel32.dll stdcall';

function BetaInstallDir(Param: String): String;
begin
  if GetDriveType('D:\') = 3 then Result := 'D:\Projects\nian-link-beta-installed'
  else if GetDriveType('H:\') = 3 then Result := 'H:\Projects\nian-link-beta-installed'
  else Result := ExpandConstant('{localappdata}\Programs\NianLinkMultiDeviceBeta');
  Result := AddBackslash(Result) + 'test-{#BetaId}';
end;

function HasLinkedAncestor(Path: String): Boolean;
var Attr: Cardinal; Parent: String;
begin
  Result := False;
  while Path <> '' do begin
    Attr := GetFileAttributes(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin Result := True; exit; end;
    Parent := ExtractFileDir(RemoveBackslashUnlessRoot(Path));
    if (Parent = '') or (CompareText(Parent, Path) = 0) then exit;
    Path := Parent;
  end;
end;

function HasRuntimeVersion(RootKey: Integer): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(RootKey, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
end;

function InitializeSetup: Boolean;
var Installed: Cardinal;
begin
  Result := HasRuntimeVersion(HKLM32) or HasRuntimeVersion(HKCU32) or HasRuntimeVersion(HKCU64);
  if not Result then begin MsgBox('需要 Microsoft Edge WebView2 Runtime，请先从微软官方网站安装。', mbInformation, MB_OK); exit; end;
  Result := RegQueryDWordValue(HKLM64, 'Software\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1);
  if not Result then MsgBox('需要 Microsoft Visual C++ x64 运行库，请先从微软官方网站安装。', mbInformation, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Target: String;
begin
  Target := ExpandConstant('{app}');
  Result := '';
  if CompareText(RemoveBackslashUnlessRoot(Target), RemoveBackslashUnlessRoot(BetaInstallDir(''))) <> 0 then
    Result := '测试版只能安装在专用目录，不能指定其他程序的目录。'
  else if DirExists(Target) or FileExists(Target) then
    Result := '专用安装目录已经存在。为保护现有文件，请先检查，不会覆盖。'
  else if HasLinkedAncestor(Target) then
    Result := '安装目录包含链接，无法安全安装。';
end;
