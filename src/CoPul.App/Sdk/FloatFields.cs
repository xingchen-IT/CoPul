namespace CoPul.App.Sdk;

/// <summary>
/// 悬浮窗的六个字段（M4.14：字段可独立开关）。
///
/// <para>
/// 与 <see cref="TrayFields"/> 同构，但**分成两套**：宽度规则完全不同 —— 任务栏是九格里挤，
/// 悬浮窗是宽高都由内容撑着。两处的"关掉之后怎么收缩"不是同一回事，
/// 合成一张表只会让两边互相迁就。
///
/// <para>
/// M4.18 起补齐到与任务栏同样的九项（多出硬盘温度 / 上传下载 / 整机功耗），
/// 但**顺序不与任务栏对齐**：新三项一律追加在末尾。老用户已经记住"第六个是网络延迟"，
/// 把它挪到第八位等于白记一次。
/// </para>
/// </para>
///
/// <para>
/// 顺序就是条上（横向从左到右 / 竖向从上到下）的顺序，不接受重排：
/// 用户记位置是按"竖着第四个是内存"记的。
/// </para>
/// </summary>
public static class FloatFields
{
    /// <summary>字段 key，与 <see cref="AppConfig.FloatHiddenFields"/> 里的值对应。</summary>
    public static readonly string[] Keys =
    {
        "fps", "cpu", "gpu", "ram", "vram", "net", "disk", "io", "pwr",
    };

    /// <summary>菜单与设置页里显示的名字。</summary>
    public static readonly string[] Labels =
    {
        "FPS", "CPU", "GPU", "内存", "显存", "网络延迟", "硬盘温度", "上传 / 下载", "整机功耗",
    };

    /// <summary>
    /// M4.18 后加的三项默认**关闭**。老用户升级上来条子不该突然变长 ——
    /// 想要哪条自己勾开，勾了才占位置。由 <see cref="ConfigStore"/> 在首次加载时播种。
    /// </summary>
    public static readonly string[] DefaultHidden =
    {
        "disk", "io", "pwr",
    };

    /// <summary>该字段当前是否显示（即用户没有关掉它）。</summary>
    public static bool IsVisible(AppConfig config, string key)
    {
        string[] hidden = config.FloatHiddenFields ?? Array.Empty<string>();
        foreach (string h in hidden)
        {
            if (string.Equals(h, key, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>当前可见字段数。</summary>
    public static int VisibleCount(AppConfig config)
    {
        int n = 0;
        foreach (string key in Keys)
        {
            if (IsVisible(config, key)) n++;
        }
        return n;
    }

    /// <summary>字段的中文/英文名，找不到时回原 key（日志与探针里用）。</summary>
    public static string LabelOf(string key)
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase)) return Labels[i];
        }
        return key;
    }

    /// <summary>
    /// 切换一个字段的显示，返回 true = 已改变，false = 被拒绝。
    ///
    /// <para>
    /// 拒绝"关掉最后一个"的理由与任务栏那边一样：全关之后宽度归零，
    /// 窗口会被夹到几像素宽 —— 屏幕上留一根发丝，用户只会以为程序坏了。
    /// </para>
    /// </summary>
    public static bool Toggle(AppConfig config, string key)
    {
        var hidden = new List<string>(config.FloatHiddenFields ?? Array.Empty<string>());

        int removed = hidden.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            if (VisibleCount(config) <= 1) return false;
            hidden.Add(key);
        }

        config.FloatHiddenFields = hidden.ToArray();
        return true;
    }
}
