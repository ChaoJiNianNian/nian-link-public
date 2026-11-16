#ifndef AppVersion
#error AppVersion is required.
#endif
#ifndef BundleDir
#error BundleDir is required.
#endif
#ifndef ReleaseDir
#error ReleaseDir is required.
#endif
#if !FileExists(BundleDir + "\App\NianLink.MultiDevice.exe")
#error Candidate application is missing.
#endif
#if !FileExists(BundleDir + "\Keyboard\deskflow-core.exe")
#error Candidate keyboard engine is missing.
#endif

[Setup]
AppId={{854CF652-A75D-470C-965F-BE9D56712B02}
AppName=年年互联（多设备内测）
AppVersion={#AppVersion}
AppPublisher=年年互联
DefaultDirName={localappdata}\Programs\NianLinkMultiDeviceTest
DefaultGroupName=年年互联（多设备内测）
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dynamic
SetupIconFile=../assets/brand/NianLink.ico
UninstallDisplayIcon={app}\App\NianLink.MultiDevice.exe
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=NianLink-Multi-{#AppVersion}-win-x64-TEST-Setup
VersionInfoVersion={#AppVersion}
VersionInfoDescription=年年互联多设备内测安装程序

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#BundleDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "languages\ChineseSimplified.LICENSE.txt"; DestDir: "{app}\THIRD-PARTY-NOTICES"; Flags: ignoreversion
Source: "languages\README.md"; DestDir: "{app}\THIRD-PARTY-NOTICES"; DestName: "ChineseSimplified-来源.md"; Flags: ignoreversion

[Icons]
Name: "{group}\年年互联（多设备内测）"; Filename: "{app}\App\NianLink.MultiDevice.exe"; Parameters: """{localappdata}\NianLinkMultiDeviceCandidate"" ""{app}\Keyboard\deskflow-core.exe"""; WorkingDir: "{app}\App"
Name: "{group}\卸载年年互联（多设备内测）"; Filename: "{uninstallexe}"; IconFilename: "{app}\App\NianLink.MultiDevice.exe"
Name: "{group}\年年互联使用说明"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\使用说明.txt"""
Name: "{autodesktop}\年年互联（多设备内测）"; Filename: "{app}\App\NianLink.MultiDevice.exe"; Parameters: """{localappdata}\NianLinkMultiDeviceCandidate"" ""{app}\Keyboard\deskflow-core.exe"""; WorkingDir: "{app}\App"; Tasks: desktopicon

[Run]
Filename: "{app}\App\NianLink.MultiDevice.exe"; Parameters: """{localappdata}\NianLinkMultiDeviceCandidate"" ""{app}\Keyboard\deskflow-core.exe"""; WorkingDir: "{app}\App"; Description: "打开年年互联"; Flags: nowait postinstall skipifsilent

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
  if not Result then MsgBox('需要 Microsoft Edge WebView2 Runtime。请从微软官方网站安装后再运行此内测程序。', mbInformation, MB_OK);
end;
