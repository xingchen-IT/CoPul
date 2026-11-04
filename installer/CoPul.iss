; CoPul 芯脉 —— Inno Setup 安装脚本（M4.48）
;
; 设计要点（与 0.1.0 的发布路线一致）：
;   1. 用户级安装：装到 %LOCALAPPDATA%\Programs\CoPul，全程不弹 UAC。
;      绿色软件的用户不该为了装一个监控条去点"允许此应用对你的设备进行更改"。
;   2. 不在安装向导里显示许可协议：协议由程序首启时弹（同一份正文，出自同一份 LICENSE）。
;      两处都弹等于让人连读两遍，反而容易被跳过。
;   3. 卸载时**问**用户要不要连配置一起删，默认"否"（MB_DEFBUTTON2）。
;      静默卸载不会弹窗，改为读环境变量 COPUL_PURGE=1 才删——批量卸载和自动化测试都靠它。
;
; 编译：tools\build-installer.ps1（会先确认 artifacts 里的发布目录存在）

#define MyAppName        "CoPul 芯脉"
#define MyAppPublisher   "CoPul"
#define MyAppExeName     "CoPul.exe"

; 版本号只有一个来源：csproj 的 <Version>。build-installer.ps1 读出来用 /D 传进来，
; 这里的默认值只是为了让 ISCC 能单独跑（不带 /D 时用）。
#ifndef MyAppVersion
#define MyAppVersion     "0.1.0"
#endif

#define StageDir         "..\artifacts\CoPul-" + MyAppVersion + "-win-x64"
#define OutDir           "..\artifacts"

[Setup]
; AppId 一旦定下就不能改：卸载项、升级覆盖全靠它认人。
AppId={{7A3F1C2E-9B4D-4E81-A6C5-3D2F8B0E5A17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} 安装程序

; {localappdata} = %LOCALAPPDATA%，与 PrivilegesRequired=lowest 配套。
DefaultDirName={localappdata}\Programs\CoPul
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; lowest = 只装当前用户，不请求管理员。不要再加 PrivilegesRequiredOverridesAllowed，
; 那会让安装器在某些环境下又去争取提权，与"双击即装"矛盾。
PrivilegesRequired=lowest

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir={#OutDir}
OutputBaseFilename=CoPul-{#MyAppVersion}-setup-x64
SetupIconFile=..\src\CoPul.App\assets\copul.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; 压缩用 lzma2/max：165 MB 压到 57 MB，安装 12 秒。
; **SolidCompression 必须关**：实测 solid=yes 时安装器启动后 37 秒 exit=1，目标目录一个
; 文件都没有、连 /LOG 指定的日志都还没创建；同一份内容改成 solid=no，11.7 秒装完 485 个文件。
; 代价只是产物大 6 MB（57 vs 51），换来的是一次装成功。
Compression=lzma2/max
SolidCompression=no
WizardStyle=modern

; 安装/卸载时若程序正在运行，交给 Restart Manager 关掉，而不是拷到一半报"文件被占用"。
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
; 整个发布目录原样铺开。使用说明.txt 是给绿色压缩包写的（"解压到任意目录"），
; 装完还留着会误导，排除掉。
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "使用说明.txt"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载后残留的空目录（Inno 会删已知文件，但安装期间程序自己生成的临时文件不在记录里）
Type: filesandordirs; Name: "{app}"

[Code]
{ 注意：这段文字**不能**写成 const。
  Pascal Script 的 const 段只接受简单字面量，'...' + #13#10 + '...' 这种拼接虽然编译能过，
  但会让整个 [Code] 段在安装器启动时初始化失败——实测表现是 setup.exe 0.2 秒退出、exit=1、
  连 /LOG 指定的日志都没创建，看起来完全不像脚本问题。改成函数在运行时拼就正常。 }
function PurgePrompt(): String;
begin
  Result := '是否同时删除你的设置和日志？' + #13#10#13#10 +
            '设置：%APPDATA%\CoPul\config.json' + #13#10 +
            '日志：%LOCALAPPDATA%\CoPul' + #13#10#13#10 +
            '选「是」一并删除，机器上不留痕迹；选「否」保留，重装后设置还在。';
end;

procedure PurgeUserData();
begin
  DelTree(ExpandConstant('{userappdata}\CoPul'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\CoPul'), True, True, True);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  { 静默卸载不能弹窗（会卡在无人值守的脚本里），改看环境变量。
    没设就按"保留"处理——删数据这种事，默认必须是安全的那一侧。 }
  if UninstallSilent() then
  begin
    if GetEnv('COPUL_PURGE') = '1' then
      PurgeUserData();
    Exit;
  end;

  if MsgBox(PurgePrompt(), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    PurgeUserData();
end;
