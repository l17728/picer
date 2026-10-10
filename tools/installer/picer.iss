; Built with pinned Inno Setup 6.7.3. Installer engine: https://jrsoftware.org/
; Payload is self-contained; user data and original media are never install targets.
#ifndef ProductKind
  #error ProductKind is required
#endif
#if ProductKind == "gui"
  #define ProductTitle "Picer 照片与视频管理"
  #define ProductFolder "Picer"
  #define ProductId "Picer.Desktop"
  #define PrimaryExe "Picer.App.exe"
  #define AssetPrefix "Picer"
#else
  #define ProductTitle "Picer 媒体驻留代理"
  #define ProductFolder "Picer MediaAgent"
  #define ProductId "Picer.MediaAgent"
  #define PrimaryExe "Picer.MediaAgent.exe"
  #define AssetPrefix "Picer.MediaAgent"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductTitle}
AppVersion={#ProductVersion}
AppPublisher=Picer
AppPublisherURL=https://github.com/l17728/picer
AppSupportURL=https://github.com/l17728/picer/issues
AppUpdatesURL=https://github.com/l17728/picer/releases
VersionInfoVersion={#ProductVersion}
DefaultDirName={localappdata}\Programs\{#ProductFolder}
DefaultGroupName={#ProductTitle}
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#PrimaryExe}
UninstallDisplayName={#ProductTitle}
SetupIconFile=..\..\src\Picer.App\Assets\Picer.ico
WizardStyle=modern
Compression=lzma2/fast
SolidCompression=yes
OutputDir={#InstallerOutput}
OutputBaseFilename={#AssetPrefix}-v{#ProductVersion}-win-x64-setup
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
ChangesAssociations=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Messages]
ReadyLabel1=点击“安装”即可完成程序和依赖部署。
#if ProductKind == "gui"
ReadyLabel2a=安装包已包含 .NET 运行时和媒体代理。远程 GUI 不需要 Python、CUDA 或模型包。首次启动时通过“连接索引服务”完成配对。%n%n升级保留索引、设置和原始照片/视频；卸载只移除已安装的程序文件。
#else
ReadyLabel2a=安装包已包含 .NET、FFmpeg 和 FFprobe，无需另行下载。默认创建桌面入口并登录自启。首次启动时填写服务连接信息，并选择允许访问的照片/视频目录。%n%n升级保留连接设置；卸载不删除设置、索引或原始媒体。
#endif

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: checkedonce
#if ProductKind == "agent"
Name: "autostart"; Description: "登录 Windows 后自动运行驻留代理"; Flags: checkedonce
#endif

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ProductTitle}"; Filename: "{app}\{#PrimaryExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductTitle}"; Filename: "{app}\{#PrimaryExe}"; WorkingDir: "{app}"; Tasks: desktopicon
#if ProductKind == "agent"
Name: "{userstartup}\Picer 媒体驻留代理"; Filename: "{app}\{#PrimaryExe}"; WorkingDir: "{app}"; Tasks: autostart
#endif

[Run]
Filename: "{app}\{#PrimaryExe}"; Description: "启动 {#ProductTitle}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function NumericVersionPart(var Value: String): Integer;
var Position: Integer; Part: String;
begin
  Position := Pos('.', Value);
  if Position = 0 then begin Part := Value; Value := ''; end
  else begin Part := Copy(Value, 1, Position - 1); Delete(Value, 1, Position); end;
  Result := StrToIntDef(Part, 0);
end;

function IsNewerVersion(Installed: String; Candidate: String): Boolean;
var Index, OldPart, NewPart: Integer;
begin
  Result := False;
  for Index := 1 to 3 do begin
    OldPart := NumericVersionPart(Installed);
    NewPart := NumericVersionPart(Candidate);
    if OldPart > NewPart then begin Result := True; Exit; end;
    if OldPart < NewPart then Exit;
  end;
end;

function InitializeSetup(): Boolean;
var InstalledVersion: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1',
    'DisplayVersion', InstalledVersion) and IsNewerVersion(InstalledVersion, '{#ProductVersion}') then begin
    MsgBox('已安装更新版本 ' + InstalledVersion + '。请使用相同或更新版本的安装包，避免不兼容的降级。', mbError, MB_OK);
    Result := False;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { Default install is a single Install click. /DIR and /TASKS remain available to administrators. }
  Result := (PageID = wpSelectDir) or (PageID = wpSelectTasks);
end;
