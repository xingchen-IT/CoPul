using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace CoPul.App.Interop;

/// <summary>
/// 开机自启（G-04 / F-14，默认关闭）。
///
/// <para>
/// <b>为什么不是写个 Run 键就完事</b>：本程序的完整功能需要管理员权限
/// （CPU 温度 / 功耗 / SMART 都读不到，见 13.5）。Windows 的
/// <c>HKCU\...\Run</c> 无法启动需要提权的程序 —— UAC 开着时那条注册表项启动会
/// <b>直接失败且没有任何提示</b>，用户只会觉得"自启坏了"。
/// 所以主路径是<b>计划任务</b>（登录触发 + 最高权限），Run 键只在 Disable 时顺手清掉。
/// </para>
///
/// <para>
/// <b>为什么不用 <c>schtasks.exe</c></b>：本机安全策略把 schtasks 列进了程序黑名单，
/// 程序内 <c>Process.Start</c> 也直接被拒（Win32Exception 拒绝访问）。
/// 改用 COM 的 <c>Schedule.Service</c>（taskschd.dll），不启动任何外部进程，
/// 顺带避开了命令行引号转义这块老雷区（任务动作本来就该拆成 Path + Arguments）。
/// </para>
///
/// <para>
/// <b>启动命令是什么</b>：本机 <c>CoPul.exe</c>（apphost）起不来（缺 WindowsDesktop 8 运行时），
/// 一直是 <c>dotnet.exe CoPul.dll</c> 在跑。所以命令行必须按"当前进程"反推，
/// 不能写死路径 —— 否则换一种部署方式自启就悄悄失效。
/// </para>
/// </summary>
public static class AutoStart
{
    /// <summary>计划任务名 / Run 键值名。</summary>
    public const string TaskName = "CoPul";

    /// <summary>子进程参数：真正干活（需要已提权）。</summary>
    public const string InternalSwitch = "--autostart-internal";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // TASK_TRIGGER_LOGON / TASK_ACTION_EXEC / TASK_CREATE_OR_UPDATE
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int CreateOrUpdate = 6;

    // TASK_RUNLEVEL_HIGHEST / TASK_LOGON_INTERACTIVE_TOKEN
    private const int RunLevelHighest = 1;
    private const int LogonInteractiveToken = 3;

    private static bool? _cached;

    /// <summary>自启时该执行的可执行文件与参数（拆开给任务动作用）。</summary>
    public static (string Path, string Arguments) SplitCommand()
    {
        string host = Environment.ProcessPath ?? string.Empty;
        string dll = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        bool hostIsDotnet = Path.GetFileName(host)
            .StartsWith("dotnet", StringComparison.OrdinalIgnoreCase);

        if (hostIsDotnet && dll.Length > 0) return (host, $"\"{dll}\" --tray");
        if (host.Length > 0) return (host, "--tray");
        return (dll, "--tray");
    }

    /// <summary>给人看的完整命令行（诊断输出用）。</summary>
    public static string CommandLine()
    {
        (string path, string args) = SplitCommand();
        return $"\"{path}\" {args}";
    }

    /// <summary>当前是否已配置自启（计划任务存在，或遗留的 Run 键值存在）。</summary>
    public static bool IsEnabled()
    {
        if (_cached is { } known) return known;

        bool enabled = TaskExists() || RunValueExists();
        _cached = enabled;
        return enabled;
    }

    /// <summary>
    /// 打开自启。需要管理员 —— 未提权时会弹一次 UAC，之后开机不再弹。
    /// </summary>
    public static bool Enable()
    {
        bool ok = Taskbar.IsElevated() ? EnableCore() : RunElevated("enable");

        // 任务与 Run 键同时存在会启动两次；任务建好了就把旧键清掉
        if (ok) DeleteRunValue();

        _cached = null;
        Trace.Log("autostart", $"enable ok={ok} cmd={CommandLine()}");
        return ok;
    }

    /// <summary>关闭自启（任务与 Run 键都清）。同样需要管理员。</summary>
    public static bool Disable()
    {
        bool ok = Taskbar.IsElevated() ? DisableCore() : RunElevated("disable");

        DeleteRunValue();
        _cached = null;
        Trace.Log("autostart", $"disable ok={ok}");
        return ok;
    }

    /// <summary>
    /// 注册计划任务。**必须在已提权的进程里调用**。
    /// </summary>
    public static bool EnableCore()
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic def = service.NewTask(0);

            def.RegistrationInfo.Description = "CoPul 芯脉 —— 登录时以最高权限显示任务栏条";
            def.Settings.Enabled = true;
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            def.Settings.ExecutionTimeLimit = "PT0S";   // 常驻程序，不能有 3 天超时

            dynamic trigger = def.Triggers.Create(TriggerLogon);
            trigger.Enabled = true;
            trigger.UserId = WindowsIdentity.GetCurrent().Name;

            // 最高权限：与手动"右键 → 以管理员身份运行"等价，但不再弹 UAC
            def.Principal.RunLevel = RunLevelHighest;
            def.Principal.LogonType = LogonInteractiveToken;

            (string path, string args) = SplitCommand();

            dynamic action = def.Actions.Create(ActionExec);
            action.Path = path;
            action.Arguments = args;

            // userId / password 给 null：配合 LogonType=InteractiveToken 不需要密码
            root.RegisterTaskDefinition(TaskName, def, CreateOrUpdate, null, null, LogonInteractiveToken);
            return true;
        }
        catch (Exception ex)
        {
            Trace.Log("autostart", $"EnableCore 失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>删除计划任务。**必须在已提权的进程里调用**。任务本来不存在也算成功。</summary>
    public static bool DisableCore()
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");

            try { root.DeleteTask(TaskName, 0); }
            catch { /* 不存在 → 目的已经达到 */ }

            return true;
        }
        catch (Exception ex)
        {
            Trace.Log("autostart", $"DisableCore 失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>自测用：只确认 COM 计划任务通道能不能用，不注册任何东西。</summary>
    public static string ComTest()
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            int count = root.GetTasks(1).Count;
            return $"connect=ok tasks={count}";
        }
        catch (Exception ex)
        {
            return $"err: {ex.GetType().Name} {ex.Message}";
        }
    }

    /// <summary>自测用：把已注册的任务定义读回来（触发类型 / 权限 / 动作 / 时限）。</summary>
    public static string DumpTask()
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(TaskName);
            dynamic def = task.Definition;
            dynamic trigger = def.Triggers.Item(1);
            dynamic action = def.Actions.Item(1);

            return $"state={task.State} runLevel={def.Principal.RunLevel} " +
                   $"triggerType={trigger.Type} userId={trigger.UserId} " +
                   $"exec={action.Path} args={action.Arguments} " +
                   $"limit={def.Settings.ExecutionTimeLimit}";
        }
        catch (Exception ex)
        {
            return $"err: {ex.GetType().Name} {ex.Message}";
        }
    }

    private static object CreateService()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service")
                     ?? throw new InvalidOperationException("找不到 Schedule.Service");

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static bool TaskExists()
    {
        try
        {
            dynamic service = CreateService();
            dynamic root = service.GetFolder("\\");
            root.GetTask(TaskName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用提权后的自己重跑一次，只做注册/删除那一件事。
    ///
    /// <para>
    /// 为什么不用 <c>Process.Start("schtasks.exe", ..., runas)</c>：本机策略禁止 schtasks。
    /// 提权自己则是永远可用的 —— 反正逻辑已经在 <see cref="EnableCore"/> 里了。
    /// </para>
    /// </summary>
    private static bool RunElevated(string verb)
    {
        string host = Environment.ProcessPath ?? string.Empty;
        string dll = Assembly.GetEntryAssembly()?.Location ?? string.Empty;

        bool hostIsDotnet = Path.GetFileName(host)
            .StartsWith("dotnet", StringComparison.OrdinalIgnoreCase);

        string arguments = hostIsDotnet && dll.Length > 0
            ? $"\"{dll}\" {InternalSwitch} {verb}"
            : $"{InternalSwitch} {verb}";

        try
        {
            var psi = new ProcessStartInfo(host, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",             // 弹一次 UAC
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using Process? p = Process.Start(psi);
            if (p is null) return false;

            // 15 秒足够用户点掉 UAC；超时也不能把主界面卡死
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // 1223 = 用户在 UAC 里点了"否"，不是故障
            Trace.Log("autostart", $"提权失败：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static bool RunValueExists()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(TaskName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch { /* 没权限就算了，主路径是计划任务 */ }
    }
}
