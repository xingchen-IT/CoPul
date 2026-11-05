# 构建 Windows 安装包（Inno Setup）
#
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#
# 前置：artifacts\CoPul-<版本>-win-x64 必须存在（由 tools\publish.ps1 产出）。
#       不存在时本脚本会自动调用 publish.ps1 -NoZip 先生成目录。
# 产物：artifacts\CoPul-<版本>-setup-x64.exe  +  .sha256
#
# 为什么安装器单独一个脚本、不塞进 publish.ps1：
#   绿色包是"必产出"，安装包是"可选产出"。混在一起会让改一次版本号跑两遍 165 MB 的
#   自包含发布，而且安装器依赖外部工具（ISCC），缺它时不该拖累绿色包的构建。

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root = Split-Path -Parent $PSScriptRoot          # 仓库根
$iss  = Join-Path $root "installer\CoPul.iss"
$art  = Join-Path $root "artifacts"

# ---------------------------------------------------------------------------
# 1. 版本号：从 csproj 读，避免脚本、csproj、BUILD-INFO 三处各写一个
# ---------------------------------------------------------------------------
$csproj = Join-Path $root "src\CoPul.App\CoPul.App.csproj"
$m = Select-String -Path $csproj -Pattern "<Version>([^<]+)</Version>"
if (-not $m) { throw "csproj 里找不到 <Version>，没法确定安装包版本。" }
$version = $m.Matches[0].Groups[1].Value.Trim()

$stage = Join-Path $art "CoPul-$version-win-x64"
$exe   = Join-Path $art "CoPul-$version-setup-x64.exe"

# ---------------------------------------------------------------------------
# 2. 找 ISCC（Inno Setup 编译器）
#    本机是按用户级装的（无需管理员），所以优先 %LOCALAPPDATA%\Programs。
#    这条顺序不能反：先查 Program Files 会在"没装全机版"时报一堆权限错。
# ---------------------------------------------------------------------------
function Resolve-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        "C:\Program Files\Inno Setup 7\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command ISCC -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "找不到 ISCC.exe（Inno Setup 编译器）。装法见 installer\README.md。"
}

# ---------------------------------------------------------------------------
# 3. 发布目录不存在就先做一次（不带 zip，安装器只要目录）
# ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $stage "CoPul.exe"))) {
    Write-Host "发布目录不存在，先跑一次 publish.ps1 -NoZip …" -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "publish.ps1") -NoZip
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 失败（exit $LASTEXITCODE），安装包没法做。" }
}

$fileCount = (Get-ChildItem $stage -Recurse -File).Count

# ---------------------------------------------------------------------------
# 产物形态校验（M4.50 起是单文件布局，这里从"数文件个数"改成"看形态"）
#
# 原来是 `文件数 -lt 400 → 报错`，因为多文件发布正常有 480+ 个。单文件之后整个目录
# 只剩 8 个文件，那条判据会把**正常产物**判成半份包。改成两条各自能说明问题的：
#   · exe 太小 → 自包含运行时没打进去（装上必崩）；
#   · 外部文件缺 → 这些是 ExcludeFromSingleFile 的，运行时按路径去找，缺了图标就空白。
# ---------------------------------------------------------------------------
$exeMB = [math]::Round((Get-Item (Join-Path $stage "CoPul.exe")).Length / 1MB, 1)
if ($exeMB -lt 100) {
    throw "发布目录里的 CoPul.exe 只有 $exeMB MB，自包含运行时没打进去（正常 140+）。先重跑 publish.ps1。"
}

$mustExist = @(
    "LICENSE.txt", "THIRD-PARTY-NOTICES.txt",
    "presentmon\PresentMon.exe", "presentmon\LICENSE.txt",
    "assets\copul.ico", "assets\copul-tray-dark.ico", "assets\copul-tray-light.ico"
)
$missing = @($mustExist | Where-Object { -not (Test-Path (Join-Path $stage $_)) })
if ($missing.Count -gt 0) {
    throw "发布目录缺外部文件：$($missing -join '、')。先重跑 publish.ps1。"
}

# ---------------------------------------------------------------------------
# 4. 编译
# ---------------------------------------------------------------------------
$iscc = Resolve-Iscc
Write-Host ""
Write-Host "Inno Setup : $iscc"
Write-Host "版本       : $version"
Write-Host "源目录     : $stage（$fileCount 个文件）"
Write-Host "输出       : $exe"
Write-Host ""

if (Test-Path $exe) { Remove-Item $exe -Force }

& $iscc "/Qp" "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）。" }
if (-not (Test-Path $exe)) { throw "ISCC 返回成功但没有产出 $exe。" }

# ---------------------------------------------------------------------------
# 5. SHA-256（与绿色包同一格式：大写字串 + 文件名，可直接给 sha256sum -c）
# ---------------------------------------------------------------------------
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$name = Split-Path $exe -Leaf
$line = "$hash  $name"
[System.IO.File]::WriteAllText("$exe.sha256", "$line`n", (New-Object System.Text.UTF8Encoding($false)))

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "安装包就绪：$name（$mb MB）" -ForegroundColor Green
Write-Host "SHA-256   ：$hash"
Write-Host ""
Write-Host "静默安装： $exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
Write-Host "静默卸载： %LOCALAPPDATA%\Programs\CoPul\unins000.exe /VERYSILENT"
Write-Host "连配置一起卸：先设环境变量 COPUL_PURGE=1，再执行上面的卸载命令"

# ---------------------------------------------------------------------------
# 6. 完整性标签兜底
#
#    本仓库所在的 D:\CoPul开发 带 Mandatory Label\Low（沙箱留下的），artifacts\ 与
#    刚生成的 exe 都继承这个标签。用户双击带 Low 标签的 setup.exe 时，进程以 Low IL
#    启动，写不进 Medium 的 %TEMP%，报：
#        「无法创建目录 %TEMP%\is-XXXX.tmp。错误 5：拒绝访问。」
#    0.2 秒 exit=1，连 /LOG 都写不出来——看着像脚本坏了，其实字节完全没问题。
#
#    试过 icacls /setintegritylevel Medium，对单文件和对 artifacts\ 都返回 exit=5：
#    同目录那条 Everyone:(CI)(DENY)(DC) 显式 ACE 把它挡住了。
#    所以不动工作区安全设置，改成「额外复制一份到 Downloads」——复制按**目标**目录
#    取标签，落到 Medium 目录后就是干净的，双击即装。
# ---------------------------------------------------------------------------
$labelLine = icacls $exe 2>$null | Select-String -Pattern 'Low Mandatory Level'
if ($labelLine) {
    $dl = Join-Path $env:USERPROFILE 'Downloads'
    if (Test-Path $dl) {
        $copy = Join-Path $dl $name
        Copy-Item $exe $copy -Force
        $copyLabel = icacls $copy 2>$null | Select-String -Pattern 'Low Mandatory Level'
        Write-Host ""
        Write-Host "注意：artifacts\ 带「低完整性标签」，原地那份双击会报「错误 5：拒绝访问」。" -ForegroundColor Yellow
        if ($copyLabel) {
            Write-Host "      Downloads 里的副本也继承了标签，请改用 D:\ 根目录之类的位置。" -ForegroundColor Yellow
        } else {
            Write-Host "      已额外输出一份可直接双击的副本：" -ForegroundColor Yellow
            Write-Host "      $copy" -ForegroundColor Green
        }
    }
}
