namespace CoPul.App.Sdk;

/// <summary>
/// 任务栏条的八个字段（7.4 / Q-03）。
///
/// <para>
/// 抽成独立表是因为有三个地方要按同一份顺序与同一套 key 说话：
/// <see cref="CoPul.App.TaskbarWindow"/> 的布局与右键菜单、<c>App</c> 的托盘菜单、
/// 以及落盘的 <see cref="AppConfig.TaskbarHiddenFields"/>。分散写三份必然错位。
/// </para>
///
/// <para>
/// 这里的顺序就是 7.4 表格的顺序，也是条上从左到右的排列顺序，不接受重排 ——
/// 用户记位置是按"第三个是 GPU"记的。
/// </para>
/// </summary>
public static class TrayFields
{
    /// <summary>字段 key，与 <see cref="AppConfig.TaskbarHiddenFields"/> 里的值对应。</summary>
    public static readonly string[] Keys =
    {
        "fps", "cpu", "gpu", "ram", "disk", "io", "lat", "pwr",
    };

    /// <summary>菜单里显示的名字。</summary>
    public static readonly string[] Labels =
    {
        "FPS", "CPU", "GPU", "内存", "硬盘温度", "上传 / 下载", "网络延迟", "整机功耗",
    };

    /// <summary>
    /// 7.7「任务栏已满」时自动让位的顺序：先砍功耗、再砍延迟、再砍网络速率。
    /// FPS 不在表里 —— 它是 7.4 里唯一"原始需求明确要求可开关"的字段，
    /// 也是用户放任务栏上最想看到的那个，留到最后。
    /// </summary>
    public static readonly string[] AutoDropOrder =
    {
        "pwr", "lat", "io", "disk", "ram", "gpu", "cpu",
    };

    /// <summary>该字段当前是否显示（即用户没有关掉它）。</summary>
    public static bool IsVisible(AppConfig config, string key)
    {
        string[] hidden = config.TaskbarHiddenFields ?? Array.Empty<string>();
        foreach (string h in hidden)
        {
            if (string.Equals(h, key, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>当前可见字段数（按配置算，不含"空间不够自动让位"）。</summary>
    public static int VisibleCount(AppConfig config)
    {
        int n = 0;
        foreach (string key in Keys)
        {
            if (IsVisible(config, key)) n++;
        }
        return n;
    }

    /// <summary>
    /// 切换一个字段的显示，返回 true = 已改变，false = 被拒绝。
    ///
    /// <para>
    /// 为什么拒绝"关掉最后一个"：全关之后 <c>Reposition</c> 算出的内容宽度是 0，
    /// 窗口会被夹到 1 px —— 屏幕上留一根发丝长的竖线，用户只会以为程序坏了。
    /// 与其画一条 1 px 的窗口，不如让这一项置灰、点不动。
    /// </para>
    /// </summary>
    public static bool Toggle(AppConfig config, string key)
    {
        var hidden = new List<string>(config.TaskbarHiddenFields ?? Array.Empty<string>());

        int removed = hidden.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            // 要关掉它，但它是最后一个可见的 —— 拒绝
            if (VisibleCount(config) <= 1) return false;
            hidden.Add(key);
        }

        config.TaskbarHiddenFields = hidden.ToArray();
        return true;
    }
}
