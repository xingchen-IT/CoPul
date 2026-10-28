using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CoPul.Collector.Interop;

/// <summary>一帧呈现记录（F01/F03/F04 的原料）。</summary>
public readonly record struct PresentFrame(int Pid, string App, double BetweenMs);

/// <summary>
/// PresentMon 子进程会话：负责起进程、读 stdout 的 CSV 流、把每行解析成 <see cref="PresentFrame"/>。
///
/// 为什么必须外挂 PresentMon：Windows 没有任何 API 直接给帧率。唯一口径是 ETW 的
/// DXGI/D3D9 Present 事件流，自己实现消费者要维护 manifest、时间戳对齐、丢帧补偿，
/// 等于重写半个 PresentMon；Q-07 已批准随包附带官方二进制（MIT，约 1MB）。
///
/// **权限**：ETW 会话需要 SeSystemProfilePrivilege。未提权时 PresentMon 直接报
/// <c>failed to start trace session: access denied</c> 并退出（实测 rc=6），
/// 这里必须把这句话原样带回去，否则用户只会看到 FPS 六格恒为 `—`。
///
/// **重启**：游戏切独占全屏、显卡驱动重置、用户手动杀掉进程都会让会话断掉。
/// 看门狗每 3 秒检查一次，断了就重连（连续 5 次失败才判定不可用），
/// 不能让"打开一次游戏就把 FPS 监控搞死"。
/// </summary>
public sealed class PresentMonSession : IDisposable
{
    /// <summary>PresentMon 的 CSV 是「每帧一行」，高帧率下每秒几百行。用 8KB 行缓冲足够。</summary>
    private const int MaxLineLength = 8 * 1024;

    private const int RestartIntervalMs = 3000;
    private const int MaxConsecutiveRestarts = 5;

    private readonly object _gate = new();
    private Process? _proc;
    private Thread? _reader;
    private Thread? _watchdog;
    private volatile bool _disposed;
    private volatile bool _running;

    private int _consecutiveRestarts;
    private long _frameCount;
    private long _parseErrors;

    /// <summary>最近一次失败原因（stderr 原文，或"找不到 exe"），直接进状态栏。</summary>
    public string? LastError { get; private set; }

    /// <summary>CSV 表头是否已解析成功。表头没到之前收到的行一律丢弃。</summary>
    public bool HeaderParsed { get; private set; }

    public bool IsRunning => _running;

    /// <summary>累计解析出的帧数（含所有进程）。诊断用：判断"没数据"是没采到还是没进程。</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    public long ParseErrors => Interlocked.Read(ref _parseErrors);

    /// <summary>所有进程的 v1 CSV 列名（用于确认 PresentMon 版本没有改列名）。</summary>
    public string HeaderColumns { get; private set; } = "";

    /// <summary>每解析出一帧回调一次。回调运行在读数线程，必须极快且不许抛。</summary>
    public Action<PresentFrame>? Frame;

    /// <summary>会话意外断开（进程退出）。UI 侧用它把状态翻成降级。</summary>
    public Action<string>? Faulted;

    /// <summary>
    /// 启动会话。<paramref name="exePath"/> 指向随包的 PresentMon.exe。
    /// 返回 false 时 <see cref="LastError"/> 一定是人话。
    ///
    /// <paramref name="requireElevation"/>：是否要求当前进程已提权。默认 true。
    /// 保留这个开关是因为 FpsProvider 需要在"未提权"时给出面向用户的解释（并直接不进
    /// Start），而离线自测夹具跑的是不需要 ETW 的假实现，得能绕过 —— 判据留在调用方，
    /// 会话层只负责执行。
    /// </summary>
    public bool Start(string exePath, bool requireElevation = true)
    {
        if (_disposed) return false;

        if (!System.IO.File.Exists(exePath))
        {
            LastError = $"未找到 PresentMon：{exePath}";
            return false;
        }

        if (requireElevation && !Win32.IsAdministrator())
        {
            // 不进 StartProcess：它必然失败，还白起一个进程等它报错。
            LastError = "FPS 需要管理员权限（PresentMon 的 ETW 会话被拒绝访问）";
            return false;
        }

        if (!StartProcess(exePath)) return false;

        _watchdog = new Thread(() => WatchdogLoop(exePath))
        {
            IsBackground = true,
            Name = "CoPul.PresentMon.Watch",
            Priority = ThreadPriority.BelowNormal
        };
        _watchdog.Start();
        return true;
    }

    private bool StartProcess(string exePath)
    {
        // v1 列集（PresentMon 1.x 口径）：字段少、列名十年没动过，解析风险最低。
        // --no_console_stats：否则它会在 stdout 上画动态统计表，和 CSV 混在一起。
        // --session_name：固定名字，免得和用户自己开着的 PresentMon 抢默认的 "PresentMon" 会话。
        // --stop_existing_session：ETW 实时会话不会随进程退出而销毁，上次 CoPul 崩掉会留下
        //   一个同名残留会话，不加这个参数下次就再也起不来（表现为"访问被拒绝"但不提原因）。
        // 不加 --process_id：前台进程随时会变，每换一次就重启一次会话等于每秒重建 ETW，
        // 所以采全部进程，由上层按"当前目标"筛选。
        string args =
            "--output_stdout --no_console_stats --v1_metrics " +
            "--session_name CoPulFps --stop_existing_session " +
            "--exclude PresentMon.exe --exclude CoPul.exe --exclude dwm.exe";

        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? "."
        };

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");
        }
        catch (Exception ex)
        {
            LastError = "启动 PresentMon 失败：" + ex.GetBaseException().Message;
            return false;
        }

        lock (_gate)
        {
            _proc = proc;
            HeaderParsed = false;
        }
        _running = true;

        _reader = new Thread(() => ReadLoop(proc))
        {
            IsBackground = true,
            Name = "CoPul.PresentMon.Read",
            Priority = ThreadPriority.BelowNormal
        };
        _reader.Start();
        return true;
    }

    /// <summary>
    /// 读 CSV 流。用 <see cref="StreamReader.ReadLine"/> 而不是自己切缓冲：
    /// PresentMon 的行是完整写出的（每行一次 fflush），StreamReader 的阻塞读正好，
    /// 而且它只在换行处返回，省掉半行拼接。
    /// </summary>
    private void ReadLoop(Process proc)
    {
        var err = new StringBuilder();
        // stderr 也要读走，否则管道写满会把 PresentMon 卡死（经典死锁）。只在退出时用它。
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (err) { if (err.Length < 4096) err.AppendLine(e.Data); }
        };
        proc.BeginErrorReadLine();

        int iApp = -1, iPid = -1, iPresent = -1, iDisplay = -1;

        try
        {
            using var reader = proc.StandardOutput;
            string? line;
            int headerChecks = 0;

            while (!_disposed && (line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;

                if (!HeaderParsed)
                {
                    // 表头之前可能有 PresentMon 的启动提示。按"含 Application 且含 ProcessID"
                    // 认表头，认错一次不会死，最多多跳几行。
                    if (line.StartsWith('\uFEFF')) line = line[1..];
                    if (line.Contains("Application", StringComparison.Ordinal) &&
                        line.Contains("ProcessID", StringComparison.Ordinal))
                    {
                        var cols = line.Split(',');
                        HeaderColumns = line;
                        iApp = FindCol(cols, "Application");
                        iPid = FindCol(cols, "ProcessID");
                        // v1：msBetweenPresents 是帧生成间隔，msBetweenDisplayChange 是上屏间隔。
                        // FPS 取前者——和 RTSS / 游戏内计数器同一口径；后者在无边框窗口
                        // 常被 vsync 量化成 16.7ms，会把 200 FPS 的游戏显示成 60。
                        // **列名必须大小写不敏感**：2.x 的 --v1_metrics 走的是 1.x 兼容写出器，
                        // 真实表头是小写 msBetweenPresents（见 CsvOutput.cpp: WriteCsvHeader<FrameMetrics1>），
                        // 而 2.x 自己的宽表用大写 MsBetweenPresents，两边都要认。
                        iPresent = FindCol(cols, "msBetweenPresents");
                        iDisplay = FindCol(cols, "msBetweenDisplayChange");

                        HeaderParsed = iApp >= 0 && iPid >= 0 && (iPresent >= 0 || iDisplay >= 0);
                        if (!HeaderParsed)
                        {
                            LastError = "PresentMon 输出列不符合预期：" + line;
                            break;
                        }
                        continue;
                    }

                    if (++headerChecks > 40)
                    {
                        LastError = "PresentMon 未输出 CSV 表头（前 40 行均非表头）";
                        break;
                    }
                    continue;
                }

                ParseRow(line, iApp, iPid, iPresent, iDisplay);
            }
        }
        catch (Exception ex)
        {
            LastError = "读取 PresentMon 输出失败：" + ex.GetBaseException().Message;
        }

        _running = false;

        string? reason = null;
        lock (err)
        {
            if (err.Length > 0) reason = err.ToString().Trim();
        }
        // 进程正常退出（用户关了游戏不影响我们，会话不该退出）也算断开，交给看门狗重连。
        if (!_disposed && !string.IsNullOrEmpty(reason)) LastError = reason;
        if (!_disposed) Faulted?.Invoke(LastError ?? "PresentMon 会话已断开");
    }

    private void ParseRow(string line, int iApp, int iPid, int iPresent, int iDisplay)
    {
        // 手工切分而不是 Split(',')：每帧一次，高帧率下这是每秒几百次的分配点。
        Span<Range> ranges = stackalloc Range[32];
        if (!TrySplit(line, ranges, out int n) || n <= Math.Max(iApp, iPid)) { Interlocked.Increment(ref _parseErrors); return; }

        if (!int.TryParse(line[ranges[iPid]], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {
            Interlocked.Increment(ref _parseErrors);
            return;
        }

        double ms = double.NaN;
        if (iPresent >= 0 && iPresent < n) TryParseMs(line[ranges[iPresent]], out ms);
        if (double.IsNaN(ms) && iDisplay >= 0 && iDisplay < n) TryParseMs(line[ranges[iDisplay]], out ms);
        if (double.IsNaN(ms) || ms <= 0) { Interlocked.Increment(ref _parseErrors); return; }

        Interlocked.Increment(ref _frameCount);
        Frame?.Invoke(new PresentFrame(pid, line[ranges[iApp]], ms));
    }

    /// <summary>按列名找下标，大小写不敏感（1.x 兼容表头用小写 m，2.x 宽表用大写 M）。</summary>
    private static int FindCol(string[] cols, string name)
    {
        for (int i = 0; i < cols.Length; i++)
        {
            // 表头可能带 BOM 或首尾空白；只 Trim 一次，别在每帧的热路径上做
            if (string.Equals(cols[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>按逗号切分（PresentMon 的 Application 列不含逗号，不需要处理引号转义）。</summary>
    private static bool TrySplit(string line, Span<Range> ranges, out int count)
    {
        count = 0;
        int start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i != line.Length && line[i] != ',') continue;

            if (count >= ranges.Length) return false;
            ranges[count++] = new Range(start, i);
            start = i + 1;
        }
        return count > 0;
    }

    private static bool TryParseMs(ReadOnlySpan<char> text, out double ms)
    {
        // 列值可能是 "NA"（该帧没有对应时间戳），double.TryParse 会失败，正好走回退分支。
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
    }

    private void WatchdogLoop(string exePath)
    {
        while (!_disposed)
        {
            Thread.Sleep(RestartIntervalMs);
            if (_disposed) return;

            bool alive;
            lock (_gate) alive = _running && _proc is { HasExited: false };
            if (alive)
            {
                _consecutiveRestarts = 0;
                continue;
            }

            if (++_consecutiveRestarts > MaxConsecutiveRestarts)
            {
                LastError ??= "PresentMon 连续重连失败，FPS 已停采";
                return;
            }

            KillProcess();
            if (!StartProcess(exePath)) return;
        }
    }

    private void KillProcess()
    {
        lock (_gate)
        {
            try
            {
                if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退出 / 权限不足，忽略：下一轮会重新 StartProcess
            }
            _proc = null;
        }
        _reader = null;
    }

    public void Stop()
    {
        _disposed = true;
        _running = false;
        KillProcess();
    }

    public void Dispose() => Stop();
}
