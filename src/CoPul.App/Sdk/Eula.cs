using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CoPul.App.Sdk;

/// <summary>
/// 许可协议（GPL-3.0）的文本与阅读确认状态（2026-11-04 立，2026-11-05 随协议切换重写）。
///
/// <para>
/// 为什么还留着这个窗：GPL-3.0 第 9 条写明「运行本程序不需要接受本许可」，所以它**不是**
/// 点击合同，也不该再声称"同意才能用"。它现在的职责只有一个 —— 把许可全文和「这是自由软件、
/// 可以再分发」这件事摆到用户眼前一次，免得有人以为自己拿到的是专有软件。
/// </para>
///
/// <para>
/// 界面上的措辞因此全改成「已阅读」而不是「同意」：勾选只表示看过，不构成对 GPL 授予
/// 权利的任何限缩（GPL 第 7 条禁止附加限制，写成"同意"反而给人留下可争议的抓手）。
/// 阅读记录仍然保留，因为它同时是「这个用户装过哪一版」的证据。
/// </para>
///
/// <para>
/// 正文<b>以嵌入资源</b>的形式随程序集走，而不是运行时去读 <c>LICENSE.txt</c>：
/// 后者一旦被删（用户清理目录、精简部署）就会退化成"弹窗里只有一句摘要"，
/// 而那正是最需要完整条款的时刻。<c>LICENSE.txt</c> 仍然复制到输出目录，给人读。
/// </para>
/// </summary>
public static class Eula
{
    /// <summary>
    /// 协议版本。**改动协议正文就必须 +1**，否则老用户不会被重新询问。
    /// 只认大版本整数：条款的细微措辞调整不该频繁打扰用户，实质性变更才重新告知。
    ///
    /// <para>v1 = 专有 EULA（2026-11-04）；v2 = 切换为 GPL-3.0（2026-11-05）。</para>
    /// </summary>
    public const string Version = "2";

    /// <summary>csproj 里 &lt;EmbeddedResource LogicalName&gt; 指定的资源名。</summary>
    private const string ResourceName = "CoPul.App.EULA.txt";

    /// <summary>本版协议生效日期，只用于弹窗抬头。</summary>
    public const string EffectiveDate = "2026-11-05";

    /// <summary>配置里记录的版本是否就是当前版本。</summary>
    public static bool IsAccepted(AppConfig config)
        => string.Equals(config.LicenseAcceptedVersion?.Trim(), Version, StringComparison.Ordinal);

    /// <summary>记下同意：版本 + 本地时间（带时区）。**必须立刻落盘**，否则崩溃/强杀就白同意了。</summary>
    public static void Accept(AppConfig config)
    {
        config.LicenseAcceptedVersion = Version;
        config.LicenseAcceptedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");

        // 落盘失败不阻断使用（同意已经发生，只是记不下来），但必须留痕：
        // 表现是"每次都问一遍"，不知情会当成 bug 去翻 UI 代码。
        if (!ConfigStore.SaveCurrent())
            Interop.Trace.Log("eula", "阅读记录写盘失败 → 下次启动仍会显示");
    }

    /// <summary>弹窗里显示的全文：抬头的说明 + 要点 + 完整协议正文。</summary>
    public static string Text => Header + Body();

    private const string Header =
        "CoPul 许可协议 · GNU 通用公共许可证第 3 版（GPL-3.0）\n" +
        "版本 2 · 2026-11-05 生效\n" +
        "\n" +
        "CoPul 是自由软件：你可以自由运行、研究、修改和再分发它，包括用于商业目的。\n" +
        "再分发时须保留本许可全文、标注你对文件的修改，并让衍生作品同样以 GPL-3.0\n" +
        "发布并提供完整对应源码。\n" +
        "\n" +
        "要点提示（仅为方便阅读，不构成对许可证的限缩，一切以正文为准）：\n" +
        "  · 允许运行、复制、修改、再分发，含商业用途；\n" +
        "  · 再分发须附 GPL-3.0 全文，并提供对应源码的获取方式；\n" +
        "  · 修改过的版本必须显著标注「已修改」，且不得使用「CoPul」名称与图标；\n" +
        "  · 软件按「现状」提供，不附带任何担保。\n" +
        "\n" +
        "GPL-3.0 第 9 条：运行本程序不需要接受本许可。下面的勾选只表示你已阅读。\n" +
        "\n" +
        "————————————————————————\n" +
        "\n";

    /// <summary>读嵌入的协议正文。资源缺失（改过 csproj、裁剪过程序集）时退回摘要并留痕。</summary>
    private static string Body()
    {
        try
        {
            Assembly asm = typeof(Eula).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Tidy(reader.ReadToEnd());
            }
        }
        catch (Exception ex)
        {
            Interop.Trace.Log("eula", "读嵌入协议失败: " + ex.Message);
        }

        Interop.Trace.Log("eula", $"嵌入资源 {ResourceName} 不存在 → 使用内嵌摘要");
        return Fallback;
    }

    /// <summary>
    /// 把 LICENSE 里那种 <c>======</c> 的 ASCII 下划线换成一条细横线。
    ///
    /// <para>
    /// 只动**纯等号行**，一个字都不改：LICENSE 是给人看和给工具读的纯文本，
    /// 而弹窗里一长串等号看起来像漏了格式的代码。文字内容完全保持原样，
    /// 用户看到的与随包文件是同一条款。
    /// </para>
    /// </summary>
    private static string Tidy(string body)
        => Regex.Replace(body, @"(?m)^=+\s*$", "────────────────────────");

    /// <summary>
    /// 兜底正文。<b>不是</b>为了替代 LICENSE：它只保证"即使嵌入资源丢了，用户仍看得到
    /// 「这是 GPL-3.0 自由软件、可以再分发、无担保」这三件最关键的事"。
    /// 看到它出现就该修 csproj，而不是当成正常路径。
    /// </summary>
    private const string Fallback =
        "CoPul 许可协议（摘要）· GNU 通用公共许可证第 3 版（GPL-3.0）\n" +
        "\n" +
        "Copyright (C) 2026 xingchen\n" +
        "\n" +
        "本程序是自由软件：你可以依据自由软件基金会发布的 GNU 通用公共许可证第 3 版\n" +
        "重新分发和/或修改它。\n" +
        "\n" +
        "本程序按「现状」提供，不附带任何明示或暗示的担保，包括但不限于适销性与特定\n" +
        "用途适用性的担保。详见 GPL-3.0 第 15、16 条。\n" +
        "\n" +
        "你应当已随本程序收到一份 GPL-3.0 的副本；若没有，见\n" +
        "https://www.gnu.org/licenses/gpl-3.0.txt 。\n" +
        "\n" +
        "第三方组件（PresentMon MIT、LibreHardwareMonitorLib MPL-2.0、HidSharp\n" +
        "Apache-2.0、.NET 运行时 MIT、PawnIO 由用户自行安装）各自遵循其原始许可，\n" +
        "与本程序的 GPL-3.0 授权并存，互不取代。\n" +
        "\n" +
        "完整条款见随包的 LICENSE.txt，或仓库根目录的 LICENSE 文件。\n" +
        "对应源码（GPL-3.0 第 6 条）：https://gitee.com/stardust-xingchenya/corevessel-co-pul\n";
}
