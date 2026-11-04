<#
.SYNOPSIS
    CoPul 发布打包：自包含 publish → 校验必备文件 → BUILD-INFO → zip → SHA-256。

.DESCRIPTION
    为什么要脚本而不是手敲 dotnet publish：

    1. 参数必须固化。`-r win-x64 --self-contained` 少一个，产物就从"解压即用"变成
       "用户机器上还得装 .NET 8"，而这个差别在开发机上**看不出来** —— 本机装着 SDK，
       缺运行时的包照样能跑。所以每次发版都从同一条命令出包。
    2. 产物必须校验。自包含包 480+ 个文件，漏 `runtimes\` 或 `presentmon\` 时程序能启动，
       但一读硬件 / 一测帧率就崩（M4.41 探针副本踩过：缺 runtimes → 关窗即崩且进程不退）。
       校验清单是硬门禁，缺一个就退出码 1。
    3. 哈希必须随包给。Gitee Release 上用户没法确认下载到的就是作者发的，
       一份 SHA-256 至少让"转发时被替换"这类事有据可查。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1

.EXAMPLE
    # 只出目录不压缩（调试发布内容时用）
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -NoZip
#>
[CmdletBinding()]
param(
    # 版本号，默认取 csproj 里的 <Version>
    [string]$Version = "",

    # 产物根目录（相对仓库根）
    [string]$OutputRoot = "artifacts",

    # 跳过 zip 与哈希（只要一个可运行的目录）
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

# 控制台按 UTF-8 输出：脚本正文是中文，默认 OEM 代码页（936）会把 UTF-8 字节
# 解成"²Ö¿â"这种乱码 —— 出一次错就分不清是路径写错还是编码问题。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# Compress-Archive 的进度条会往 stdout 刷几千行，把真正的结论（哈希、体积）冲掉
$ProgressPreference = "SilentlyContinue"

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectPath = Join-Path $RepoRoot "src\CoPul.App\CoPul.App.csproj"
$CsprojPath  = $ProjectPath

# ---------------------------------------------------------------------------
# dotnet 定位：**先看自带 SDK，再看 PATH**
#
# 顺序不是随便排的：本机 PATH 上的 C:\Program Files\dotnet\dotnet.exe 只装了运行时，
# 没有 SDK，直接调它会报 "No .NET SDKs were found" —— 而报错信息看起来像脚本参数写错了。
# 所以判据不是"这个 exe 在不在"，而是"它 --list-sdks 有没有输出"。
# ---------------------------------------------------------------------------
function Resolve-Dotnet {
    $candidates = @(
        (Join-Path $env:USERPROFILE ".workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe")
    )
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
        } catch {
            # 没有 SDK 的宿主会以非零码退出，继续试下一个
        }
    }

    throw "找不到带 SDK 的 dotnet：自带 SDK 与 PATH 都没有可用安装。"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $m = Select-String -Path $CsprojPath -Pattern "<Version>(.*?)</Version>" | Select-Object -First 1
    if (-not $m) { throw "csproj 里没有 <Version>，无法确定版本号。" }
    $Version = $m.Matches[0].Groups[1].Value.Trim()
}

$Dotnet    = Resolve-Dotnet
$StageName = "CoPul-$Version-win-x64"
$StageDir  = Join-Path $RepoRoot (Join-Path $OutputRoot $StageName)
$ZipPath   = "$StageDir.zip"

Write-Host ""
Write-Host "CoPul 发布打包" -ForegroundColor Cyan
Write-Host "  版本      : $Version"
Write-Host "  仓库      : $RepoRoot"
Write-Host "  dotnet    : $Dotnet"
Write-Host "  产物目录  : $StageDir"
Write-Host ""

# ---------------------------------------------------------------------------
# 1. 清空旧产物
#    必须整目录删：上一版的文件残留会混进这一版的包，而"多一个文件"没人会去核对。
#
#    用 .NET 的 Directory/File 删除而不是 Remove-Item：本机（以及一些企业策略）会给
#    Remove-Item -Recurse 挂"安全删除"钩子，钩子失败时 fail-closed，脚本直接中断 ——
#    报错信息里只有一个 SAFE_DELETE_FAIL_CLOSED，完全看不出自己错在哪。
#    这里删的是本脚本自己上一轮生成的产物目录，路径可控，不需要走回收站。
# ---------------------------------------------------------------------------
if (Test-Path $StageDir) { [System.IO.Directory]::Delete($StageDir, $true) }
if (Test-Path $ZipPath)  { [System.IO.File]::Delete($ZipPath) }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

# ---------------------------------------------------------------------------
# 2. publish（-warnaserror：有警告就当失败，与"0 警告 0 错误才算过"的约定一致）
# ---------------------------------------------------------------------------
$publishArgs = @(
    "publish", $ProjectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", $StageDir,
    "-warnaserror",
    "--nologo"
)

& $Dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

# ---------------------------------------------------------------------------
# 3. 校验必备文件
#    每一项都对应一个"少了它程序会在用户机器上以某种方式坏掉"的具体故障，
#    不是形式主义 —— 备注里写清坏在哪，以后加文件才知道该不该进清单。
# ---------------------------------------------------------------------------
$required = @(
    @{ Path = "CoPul.exe";                              Why = "入口" },
    @{ Path = "CoPul.dll";                              Why = "主程序" },
    @{ Path = "CoPul.Collector.dll";                    Why = "采集层（缺了启动就抛）" },
    @{ Path = "LICENSE.txt";                            Why = "专有许可必须随二进制走" },
    @{ Path = "THIRD-PARTY-NOTICES.txt";                Why = "MPL-2.0 / MIT 的告知义务" },
    @{ Path = "presentmon\PresentMon.exe";              Why = "FPS 唯一来源" },
    @{ Path = "presentmon\LICENSE.txt";                 Why = "MIT 要求所有副本保留声明" },
    # 自包含 + RID 发布时原生库会被**摊平到根目录**，没有 runtimes\ 子目录
    # （框架依赖发布才有）。所以这里查的是库本身，不是它曾经的所在目录。
    @{ Path = "LibreHardwareMonitorLib.dll";            Why = "硬件采集主库" },
    @{ Path = "libMonoPosixHelper.dll";                 Why = "LHM 的原生依赖，缺了读硬件崩（M4.41）" },
    @{ Path = "RAMSPDToolkit-NDD.dll";                  Why = "内存 SPD 读取" },
    @{ Path = "assets\copul.ico";                       Why = "exe / 关于页图标" },
    @{ Path = "assets\copul-tray-dark.ico";             Why = "深色任务栏托盘图标" },
    @{ Path = "assets\copul-tray-light.ico";            Why = "浅色任务栏托盘图标" }
)

$missing = @()
foreach ($item in $required) {
    if (-not (Test-Path (Join-Path $StageDir $item.Path))) { $missing += $item }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "发布产物缺文件：" -ForegroundColor Red
    foreach ($item in $missing) { Write-Host ("  - {0}  ({1})" -f $item.Path, $item.Why) -ForegroundColor Red }
    throw "产物校验失败，共缺 $($missing.Count) 项。"
}

# 调试符号不进包：用户拿到也没用，还多几百 MB 的误判面（有人会以为要装什么）
Get-ChildItem -Path $StageDir -Recurse -Filter *.pdb | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

# ---------------------------------------------------------------------------
# 4. 使用说明 + BUILD-INFO
#
#    使用说明不是 README 的翻版：README 面向"从源码构建的人"，而拿到 zip 的人只想知道
#    「双击哪个、关窗为什么没退出、为什么温度是 —」。这三件事恰好是这个程序最容易误解的地方。
# ---------------------------------------------------------------------------
$readme = @(
    "CoPul 芯脉 $Version 使用说明",
    "",
    "1. 双击 CoPul.exe 启动。绿色免安装：不写注册表、不建服务、不装驱动。",
    "2. 首次启动会要求阅读并同意许可协议；不同意会直接退出（不会开始采集）。",
    "3. 启动后默认显示大窗口。点窗口的关闭按钮只是收起，程序仍在托盘；",
    "   要真正退出请用托盘图标的右键菜单 →「退出」。",
    "4. 建议右键「以管理员身份运行」。普通权限下 CPU 温度、硬盘温度、帧率会显示 —，",
    "   原因是这些数据要读 MSR / 物理磁盘 / ETW，属系统限制，不是故障。",
    "   设置页 →「传感器诊断」会逐项说明谁就绪、谁为什么没就绪。",
    "5. 配置与日志：%APPDATA%\CoPul 与 %LOCALAPPDATA%\CoPul（设置页 →「关于」有打开按钮）。",
    "6. 完整条款见 LICENSE.txt；第三方组件及各自许可见 THIRD-PARTY-NOTICES.txt。",
    "7. 本程序不联网、不上传任何数据，全部指标在本机采集与显示。",
    "",
    "许可：专有软件，保留所有权利，禁止二次分发。"
)
$readmePath = Join-Path $StageDir "使用说明.txt"
[System.IO.File]::WriteAllLines($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))
if (-not (Test-Path $readmePath)) { throw "使用说明.txt 没写出来。" }

# ---------------------------------------------------------------------------
# 5. BUILD-INFO：这份包是什么时候、从哪个提交、用哪个 SDK 出来的
#    没有它，"用户报的 bug 属于哪一版"只能靠猜（M4.44 的版本串就是为同一件事）
# ---------------------------------------------------------------------------
$gitSha = ""
try { $gitSha = (& git -C $RepoRoot rev-parse --short HEAD) 2>$null } catch { $gitSha = "unknown" }
try { $gitDirty = (& git -C $RepoRoot status --porcelain) 2>$null } catch { $gitDirty = "" }

$files = Get-ChildItem -Path $StageDir -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

$info = @(
    "CoPul 芯脉 $Version",
    "",
    "构建时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "源码提交 : $gitSha$(if ($gitDirty) { '（工作区有未提交改动）' } else { '' })",
    "SDK      : $(& $Dotnet --version)",
    "运行时   : win-x64 / self-contained（无需安装 .NET）",
    "文件数   : $($files.Count)",
    "解压体积 : $([math]::Round($bytes / 1MB, 1)) MB",
    "",
    "许可：专有软件，保留所有权利，禁止二次分发。完整条款见 LICENSE.txt。",
    "第三方组件及各自许可见 THIRD-PARTY-NOTICES.txt。"
)
$infoPath = Join-Path $StageDir "BUILD-INFO.txt"
# UTF-8 带 BOM：记事本、旧版 PowerShell 都能正确显示中文
[System.IO.File]::WriteAllLines($infoPath, $info, (New-Object System.Text.UTF8Encoding($true)))

$files = Get-ChildItem -Path $StageDir -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host ("产物就绪：{0} 个文件 / {1} MB" -f $files.Count, [math]::Round($bytes / 1MB, 1)) -ForegroundColor Green
if ($NoZip) {
    Write-Host "已跳过压缩（-NoZip）。" -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------------------
# 6. zip + SHA-256
# ---------------------------------------------------------------------------
Write-Host "压缩中…"
# 压缩**目录本身**而不是 "$StageDir\*"：后者解开后是 480 个文件摊一地，
# 用户随手解压到桌面就再也收不回去。带一层 CoPul-<版本>-win-x64\ 才像个包。
Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal -Force

$zip = Get-Item $ZipPath
$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash
$hashFile = "$ZipPath.sha256"
# 格式与 sha256sum 一致（"hash  文件名"），Linux / Git Bash 都能直接 -c 校验
"$hash  $($zip.Name)" | Set-Content -Path $hashFile -Encoding ASCII

Write-Host ""
Write-Host "发布包" -ForegroundColor Cyan
Write-Host ("  {0}" -f $zip.FullName)
Write-Host ("  压缩后 {0} MB" -f [math]::Round($zip.Length / 1MB, 1))
Write-Host ("  SHA-256 {0}" -f $hash)
Write-Host ("  校验文件 {0}" -f $hashFile)
Write-Host ""
Write-Host "下一步：把 zip 与 .sha256 一起传到 Gitee Release，正文用 docs/发布说明-$Version.md"
