; CoPul 芯脉 —— Inno Setup 安装脚本（M4.48）
;
; 设计要点（与后续版本的发布路线一致）：
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
#define MyAppVersion     "0.1.1"
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

; 目标位置页**必须显示**。默认值 auto 会在「本机已装过」时把这一页藏起来，
; 于是升级/重装的用户根本没机会改路径——这正是「找不到自定义安装路径」的原因。
; 显示之后由 [Code] 段做合法性校验（系统目录、盘符根、写权限、路径长度）。
DisableDirPage=no
; 目录已存在且非空时仍由 Inno 自己提示「是否仍要安装」，不额外拦截。
DirExistsWarning=auto
; 记住用户上次选的路径，下次打开默认就是它。
UsePreviousAppDir=yes

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

[Messages]
; 目录页顶部说明。默认文案没告诉用户「这里能改」，也没说明为什么不能选 Program Files。
; 注意别在这写 %LOCALAPPDATA%——Messages 里的 % 是占位符语法（%n 换行、%1 参数），
; 路径改由下方的目录框自己显示，正文只讲规则。
SelectDirDesc=安装程序会把 [name] 装到下面的文件夹。想装到别处，点「浏览」另选一个空文件夹。%n%n本安装为「仅当前用户」，全程不请求管理员权限，所以不能装进 C:\Program Files 这类系统目录——请选一个你自己有写权限的目录。

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
; 只删「空了才删」的目录。原来写 filesandordirs 是安全的——那时路径固定在
; %LOCALAPPDATA%\Programs\CoPul，一定是独占目录；现在允许自定义路径，用户可能
; 装到 D:\Tools 这种与别的软件共用的目录，递归删会连别人的文件一起带走。
; 程序自己不往安装目录写东西（配置在 %APPDATA%\CoPul、日志在 %LOCALAPPDATA%\CoPul），
; 所以「空目录」判据足够把 484 个文件清理干净。
Type: dirifempty; Name: "{app}"

[Code]
{ ============================================================
  自定义安装路径（M4.49）
  目录页本身由 DisableDirPage=no 打开，这里负责校验：用户级安装写不进系统目录，
  所以不能只把「浏览」按钮露出来就完事，否则用户会选到 Program Files 再装失败。
  ============================================================ }

var
  DirPageInitDir: String;
  DirPageCaptured: Boolean;

function NormSlashes(const S: String): String;
begin
  { 命令行 /DIR= 过来的可能是 C:/Program Files/... （正斜杠），
    不统一成反斜杠就匹配不上 Program Files 这类常量，校验会被绕过。
    注意：注释里别写 Inno 常量的花括号原文，Pascal 的注释不嵌套，会被提前截断。 }
  Result := S;
  StringChangeEx(Result, '/', '\', True);
end;

function StripTrailingSlash(S: String): String;
begin
  { 保留 "C:\" 这种长度 3 的根路径，其余剥掉尾部反斜杠，便于统一比较。
    这里刻意不用 "while (Length>3) and (S[Length]='\')"：Inno 的 and 不保证短路，
    空串时 S[0] 会越界报错。拆成两步。 }
  Result := S;
  while Length(Result) > 3 do
  begin
    if Result[Length(Result)] <> '\' then
      Break;
    Delete(Result, Length(Result), 1);
  end;
end;

function IsUnderPath(const Dir, Root: String): Boolean;
var
  D, R: String;
begin
  Result := False;
  R := Lowercase(StripTrailingSlash(Root));
  if R = '' then
    Exit;
  D := Lowercase(StripTrailingSlash(Dir));
  { 必须带上分隔符再比，否则 C:\Win 会被当成 C:\Windows 的子目录。 }
  Result := (D = R) or (Pos(R + '\', D) = 1);
end;

function IsDriveRoot(const Dir: String): Boolean;
var
  D: String;
begin
  D := StripTrailingSlash(Dir);
  Result := False;
  if Length(D) <> 3 then
    Exit;
  Result := (D[2] = ':') and (D[3] = '\');
end;

function CanWriteToDir(const Dir: String): Boolean;
var
  Probe: String;
begin
  { 目录还不存在时不做判断：那是安装时 CreateDir 的事，提前建一个反而留下垃圾。 }
  Result := True;
  if not DirExists(Dir) then
    Exit;
  Probe := AddBackslash(StripTrailingSlash(Dir)) + '.copul-write-probe';
  Result := SaveStringToFile(Probe, 'probe', False);
  if Result then
    DeleteFile(Probe);
end;

function CheckTargetDir(const Dir: String; var Reason: String): Boolean;
var
  Norm: String;
begin
  Result := False;
  Norm := StripTrailingSlash(NormSlashes(Dir));

  if Norm = '' then
  begin
    Reason := '安装路径不能为空。';
    Exit;
  end;

  if Pos('"', Norm) > 0 then
  begin
    Reason := '安装路径里不能包含双引号。';
    Exit;
  end;

  { 自包含发布里有 System.Private.Xml.resources.dll 这类长文件名，再叠深子目录就到 MAX_PATH 了。 }
  if Length(Norm) > 180 then
  begin
    Reason := '安装路径太长（' + IntToStr(Length(Norm)) + ' 个字符，上限 180）。' + #13#10 +
              '程序里有较深的子目录和上百个长文件名，路径过长会让部分文件装不进去。';
    Exit;
  end;

  if IsUnderPath(Norm, ExpandConstant('{win}')) or
     IsUnderPath(Norm, ExpandConstant('{sys}')) or
     IsUnderPath(Norm, ExpandConstant('{pf}')) or
     IsUnderPath(Norm, ExpandConstant('{pf32}')) or
     IsUnderPath(Norm, ExpandConstant('{pf64}')) or
     IsUnderPath(Norm, ExpandConstant('{commonappdata}')) or
     IsUnderPath(Norm, ExpandConstant('{commonprograms}')) or
     IsUnderPath(Norm, ExpandConstant('{commonstartmenu}')) then
  begin
    Reason := '这个位置需要管理员权限，装不进去。' + #13#10#13#10 +
              '本安装程序是「仅当前用户」安装，全程不请求管理员，只能装到你的个人目录。' + #13#10 +
              '换一个自己的目录即可，例如：' + #13#10 +
              ExpandConstant('{localappdata}') + '\Programs\CoPul';
    Exit;
  end;

  if IsDriveRoot(Norm) then
  begin
    Reason := '不要直接装到盘符根目录，那里会被塞进几百个文件。' + #13#10#13#10 +
              '建议在它下面建一个子目录，例如：' + #13#10 +
              Norm + 'Programs\CoPul';
    Exit;
  end;

  if not CanWriteToDir(Norm) then
  begin
    Reason := '没有往这个目录写文件的权限：' + #13#10 + Norm + #13#10#13#10 +
              '换一个你能写的目录，或者换回默认位置：' + #13#10 +
              ExpandConstant('{localappdata}') + '\Programs\CoPul';
    Exit;
  end;

  Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { 记下目录页刚打开时的值（可能是上次安装的路径），后面用它判断用户有没有真改过。 }
  if (CurPageID = wpSelectDir) and (not DirPageCaptured) then
  begin
    DirPageCaptured := True;
    DirPageInitDir := WizardDirValue;
  end;
end;

{ 调用方带 /DIR="..." 时安装向导会跳过目录页，NextButtonClick 根本不执行。
  所以在最早的地方补一道。

  静默模式（/SILENT、/VERYSILENT）：只写日志 + 返回 False，**绝不弹框**。
  Inno 自己遇到无权限目录时会弹一个 /SUPPRESSMSGBOXES 抑制不掉的框，实测把无人值守
  安装挂死 120 秒以上——比装失败更糟，批量部署会全部超时。InitializeSetup 返回 False
  则是静默退出，干净利落。

  注意 WizardSilent 在 InitializeSetup 阶段还没初始化（返回 False），只能看命令行。 }
function InitializeSetup(): Boolean;
var
  DirParam, Reason, Tail: String;
begin
  Result := True;

  DirParam := ExpandConstant('{param:DIR|}');
  if DirParam = '' then
    Exit;
  if CheckTargetDir(DirParam, Reason) then
    Exit;

  Log('拒绝安装到 "' + DirParam + '"：' + Reason);
  Result := False;

  Tail := Uppercase(GetCmdTail());
  if (Pos('/VERYSILENT', Tail) = 0) and (Pos('/SILENT', Tail) = 0) then
    MsgBox(Reason, mbError, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir, Reason, OldExe: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  Dir := StripTrailingSlash(WizardDirValue);

  if not CheckTargetDir(Dir, Reason) then
  begin
    MsgBox(Reason, mbError, MB_OK);
    Result := False;
    Exit;
  end;

  { 换了位置、而旧位置确实有本程序 → 提醒旧目录不会自动搬迁。只是提醒，不拦。 }
  if DirPageCaptured and (Lowercase(Dir) <> Lowercase(StripTrailingSlash(DirPageInitDir))) then
  begin
    OldExe := AddBackslash(StripTrailingSlash(DirPageInitDir)) + '{#MyAppExeName}';
    if FileExists(OldExe) then
      MsgBox('旧版本装在：' + #13#10 + DirPageInitDir + #13#10#13#10 +
             '这次会装到新位置，旧目录里的文件不会自动搬走，卸载时也只清理新位置。' + #13#10 +
             '不想留两份的话，装完可以手动删掉旧目录。', mbInformation, MB_OK);
  end;
end;

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
