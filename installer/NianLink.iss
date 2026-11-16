#ifndef AppVersion
#error AppVersion is required.
#endif
#ifndef PublishDir
#error PublishDir is required.
#endif
#ifndef ReleaseDir
#error ReleaseDir is required.
#endif
#if !FileExists(PublishDir + "\NianLink.exe")
#error NianLink.exe was not found.
#endif

[Setup]
AppId={{E7A5D94B-8C38-43D9-A962-F258DFE981CB}
AppName=年年互联（测试版）
AppVersion={#AppVersion}
AppPublisher=年年互联
DefaultDirName={localappdata}\Programs\NianLinkStandalone
DefaultGroupName=年年互联（测试版）
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dynamic
SetupIconFile=../assets/brand/NianLink.ico
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\NianLink.exe
Compression=lzma2
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=NianLink-{#AppVersion}-win-x64-TEST-Setup
VersionInfoVersion={#AppVersion}
VersionInfoDescription=年年互联测试版安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\年年互联（测试版）"; Filename: "{app}\NianLink.exe"; WorkingDir: "{app}"
Name: "{group}\卸载年年互联（测试版）"; Filename: "{uninstallexe}"; IconFilename: "{app}\NianLink.exe"
Name: "{autodesktop}\年年互联（测试版）"; Filename: "{app}\NianLink.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function HasRuntimeVersion(RootKey: Integer): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(RootKey, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
end;
function InitializeSetup: Boolean;
begin
  Result := HasRuntimeVersion(HKLM32) or HasRuntimeVersion(HKCU32) or HasRuntimeVersion(HKCU64);
  if not Result then MsgBox('需要 Microsoft Edge WebView2 Runtime。请从微软官方网站安装后再运行此安装程序。', mbInformation, MB_OK);
end;
