using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CoPul.App.Sdk;

/// <summary>
/// 最终用户许可协议（EULA）的文本与同意状态（发布合规项，2026-11-04）。
///
/// <para>
/// 为什么需要这个类：仓库里有 <c>LICENSE</c> 只说明"作者主张什么权利"，但**没有用户的
/// 同意动作**。中国法院认可点击合同，前提是「条款可获取 + 同意有记录」—— 双击 exe 就能用，
/// 起纠纷时对方可以主张合同未成立，那么"禁止二次分发"和免责声明这两段最关键的条款都用不上。
/// 所以首次启动必须把条款摆到用户面前，并把同意结果记进配置。
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
    /// 只认大版本整数：条款的细微措辞调整不该频繁打扰用户，实质性变更才重新征求同意。
    /// </summary>
    public const string Version = "1";

    /// <summary>csproj 里 &lt;EmbeddedResource LogicalName&gt; 指定的资源名。</summary>
    private const string ResourceName = "CoPul.App.EULA.txt";

    /// <summary>协议首次生效日期，只用于弹窗抬头。</summary>
    public const string EffectiveDate = "2026-11-04";

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
            Interop.Trace.Log("eula", "同意状态写盘失败 → 下次启动仍会询问");
    }

    /// <summary>弹窗里显示的全文：抬头的说明 + 要点 + 完整协议正文。</summary>
    public static string Text => Header + Body();

    private const string Header =
        "CoPul 最终用户许可协议（EULA）\n" +
        "版本 1 · 2026-11-04 生效\n" +
        "\n" +
        "使用本软件前，请完整阅读以下条款。点击「同意并继续」表示你已阅读、理解并接受全部\n" +
        "条款；不接受请点击「退出」，本软件将不会运行。\n" +
        "\n" +
        "要点提示（仅为方便阅读，不构成对正文的限缩，一切以正文为准）：\n" +
        "  · 允许在你自己拥有或控制的设备上个人非商业使用；\n" +
        "  · 禁止任何形式的二次分发（转载、镜像、网盘、打包进整合包等）；\n" +
        "  · 允许阅读源码并提交 Issue / Pull Request，但不得发布修改版；\n" +
        "  · 软件按「现状」提供，作者不承担使用后果。\n" +
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
    /// 兜底正文。<b>不是</b>为了替代 LICENSE：它只保证"即使资源丢了，禁止二次分发与免责
    /// 这两条也还在用户眼前"。看到它出现就该修 csproj，而不是当成正常路径。
    /// </summary>
    private const string Fallback =
        "CoPul 专有软件许可协议（摘要）\n" +
        "\n" +
        "Copyright (c) 2026 CoPul 作者（xingchen）。保留所有权利。\n" +
        "\n" +
        "一、允许：从作者发布的官方渠道下载，在你拥有或控制的设备上安装、运行，用于个人\n" +
        "非商业用途；为备份保存一份副本；阅读源码并提交 Issue / Pull Request；在文章、视频、\n" +
        "评测中引用截图并注明出处。\n" +
        "\n" +
        "二、禁止（未经作者事先书面许可）：以任何形式二次分发（转载、镜像、网盘、论坛、\n" +
        "应用商店、下载站、打包进整合包/启动器/系统镜像、通过任何介质提供副本）；发布修改版\n" +
        "或衍生版；商业利用；移除版权与作者标识；反编译与逆向工程。\n" +
        "\n" +
        "三、第三方组件（PresentMon MIT、LibreHardwareMonitorLib MPL-2.0、.NET 运行时 MIT、\n" +
        "PawnIO 由用户自行安装）各自遵循其原始许可；本协议不授予任何人将本软件与上述组件\n" +
        "作为整体再次分发的权利。\n" +
        "\n" +
        "四、免责：本软件按「现状」提供，不附带任何明示或暗示的担保。在任何情况下，作者\n" +
        "均不对因使用或无法使用本软件而产生的任何直接、间接、偶然、特殊或后果性损害承担\n" +
        "责任，包括但不限于数据丢失、系统损坏、硬件损伤或业务中断。\n" +
        "\n" +
        "五、违反任一条款，你获得的一切授权立即自动终止，应立即停止使用并删除全部副本。\n" +
        "\n" +
        "六、适用中华人民共和国法律。\n" +
        "\n" +
        "完整条款见随包的 LICENSE.txt，或仓库根目录的 LICENSE 文件。\n";
}
