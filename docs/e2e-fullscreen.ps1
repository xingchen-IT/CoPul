# 端到端验收：任务栏模式 + 真实全屏 → 自动切悬浮窗 → 退全屏回落
#
# 用法（提权，因为任务栏条需要提权才稳定）：
#   1. 保持一个全屏游戏在跑（或按 Win+Tab 进全屏），
#   2. 双击本脚本，
#   3. 看 D:\CoPulRun\e2e-*.txt 与 copul-trace.log。
#
# 为什么不能自动跑：真全屏需要独占显示器，脚本里造不出来（造出来也是假的）。
# 现实做法是"人开游戏 + 脚本采集证据"，与 M3 的独占全屏取舍一致。

$ErrorActionPreference = 'Continue'
$run    = 'D:\CoPulRun'
$dll    = Join-Path $run 'CoPul.dll'
$sdk    = "$env:USERPROFILE\.workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe"
$trace  = Join-Path $env:TEMP 'copul-trace.log'
$probe  = Join-Path $env:TEMP 'copul-fs-e2e.txt'

Write-Host '=== CoPul 全屏自动切换 端到端验收 ===' -ForegroundColor Cyan
Write-Host ''

# 1) 当前是不是全屏（先记一笔，用于对照）
Write-Host '[1/4] 记录当前全屏状态...' -ForegroundColor Yellow
& $sdk $dll --fullscreen-probe $probe --fs-polls 2 *> $null
if (Test-Path $probe) {
    $now = (Get-Content $probe -Encoding UTF8 | Select-String 'full=' | Select-Object -First 1).Line
    Write-Host "      当前: $($now.Trim())" -ForegroundColor Gray
}

# 2) 清掉旧 trace，便于只看本轮
Write-Host '[2/4] 清空 trace...' -ForegroundColor Yellow
if (Test-Path $trace) { Remove-Item $trace -Force -ErrorAction SilentlyContinue }

# 3) 以任务栏模式启动（不自动 Play，靠用户已经开着的全屏游戏触发）
Write-Host '[3/4] 以 --tray 启动，等待 12 秒观察自动切换...' -ForegroundColor Yellow
$p = Start-Process -FilePath $sdk -ArgumentList @($dll, '--tray', '--no-shadow') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 12

# 4) 收集证据
Write-Host '[4/4] 收集证据...' -ForegroundColor Yellow
$out = Join-Path $run ("e2e-" + (Get-Date -Format 'HHmmss') + '.txt')
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("=== CoPul 全屏自动切换 端到端验收 ===")
[void]$sb.AppendLine("time : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("pid  : $($p.Id)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("== trace（本轮）==")
if (Test-Path $trace) {
    Get-Content $trace -Encoding UTF8 | ForEach-Object { [void]$sb.AppendLine($_) }
} else {
    [void]$sb.AppendLine("(无 trace —— 检查提权与 TEMP 权限)")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("== 结论指引 ==")
[void]$sb.AppendLine("有 'fullscreen enter-full: Taskbar→Float(auto)' 且视图真的换了 = 通过")
[void]$sb.AppendLine("只有 'enter-full: view=... 不动作' = 当前不是任务栏模式，或没检测到全屏")
[void]$sb.AppendLine("完全没有 fullscreen 行 = 定时器没跑起来（查启动参数）")

Set-Content -Path $out -Value $sb.ToString() -Encoding UTF8
Write-Host "      报告: $out" -ForegroundColor Green
Write-Host ''

# 别把用户进程留着：它还会继续跑采集，验收完就关掉
try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
Write-Host '=== 完成。请打开上面那个报告文件查看 ===' -ForegroundColor Cyan
Start-Sleep -Seconds 3
