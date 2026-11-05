using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CoPul.App.Sdk;

/// <summary>
/// 关于页要展示的事实（2026-11-04）：版本、构建时间、安装目录、第三方组件清单。
///
/// <para>
/// 集中在这里而不是散在设置页里，是为了让**探针也能拿到同一份值** ——
/// 关于页最容易出的错不是排版，而是"界面上写着 0.1.0，实际跑的是另一个 dll"。
/// 诊断串与界面读同一个属性，才能对上。
/// </para>
/// </summary>
public static class AboutInfo
{
    /// <summary>csproj 里 &lt;EmbeddedResource LogicalName&gt; 指定的资源名。</summary>
    private const string NoticesResource = "CoPul.App.THIRD-PARTY-NOTICES.txt";

    /// <summary>
    /// 版本串，形如 <c>0.1.0 (049bedd)</c>。
    ///
    /// <para>
    /// 取 <c>AssemblyInformationalVersion</c> 而不是 <c>AssemblyVersion</c>：后者是四段
    /// <c>0.1.0.0</c>，用户报问题时看不出是哪一次构建。加号后面那截是构建时由 SDK 写进去的
    /// 源码版本号，取前 7 位就是对得上 git 提交的短 sha。
    /// </para>
    /// </summary>
    public static string Version
    {
        get
        {
            try
            {
                Assembly asm = typeof(AboutInfo).Assembly;
                string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                                  .InformationalVersion
                              ?? asm.GetName().Version?.ToString()
                              ?? "未知";

                int plus = info.IndexOf('+');
                if (plus <= 0 || plus >= info.Length - 1) return info;

                string sha = info[(plus + 1)..];
                if (sha.Length > 7) sha = sha[..7];
                return $"{info[..plus]} ({sha})";
            }
            catch (Exception ex)
            {
                Interop.Trace.Log("about", "读版本失败: " + ex.Message);
                return "未知";
            }
        }
    }

    /// <summary>
    /// 产物写盘时间，当作构建时间。**不是**编译时刻：换过二进制之后它就是那一份的时间，
    /// 而这恰好是排查"改了源码但部署没生效"时该看的东西。
    ///
    /// <para>
    /// 不问 <c>Assembly.Location</c>：单文件发布时它恒为空（IL3000 警告），
    /// 退到 <c>Environment.ProcessPath</c> —— 单文件下 exe 的写盘时间就是打包完成时刻，
    /// 比 dll 时间更贴近"这份产物何时出的"。多文件模式仍优先取 dll，语义不变。
    /// </para>
    /// </summary>
    public static string BuildTime
    {
        get
        {
            try
            {
                string dll = Path.Combine(AppContext.BaseDirectory, "CoPul.dll");
                string? path = File.Exists(dll) ? dll : Environment.ProcessPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "未知";
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch (Exception ex)
            {
                Interop.Trace.Log("about", "读构建时间失败: " + ex.Message);
                return "未知";
            }
        }
    }

    /// <summary>程序所在目录。单文件发布时也指向 exe 所在目录。</summary>
    public static string AppDirectory => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>
    /// 第三方组件清单正文（嵌入资源）。与随包的 <c>THIRD-PARTY-NOTICES.txt</c> 同源，
    /// 不复制第二份 —— 两份清单迟早会不一致，而组件清单不一致就是合规瑕疵。
    /// </summary>
    public static string Notices
    {
        get
        {
            try
            {
                using Stream? stream = typeof(AboutInfo).Assembly
                    .GetManifestResourceStream(NoticesResource);

                if (stream is not null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    // 只把纯等号行换成细横线，一个字都不改：清单里那种 ====== 下划线
                    // 在界面上看起来像漏了格式的代码。与 Eula.Tidy 同一处理。
                    return Regex.Replace(reader.ReadToEnd().TrimEnd(), @"(?m)^=+\s*$", "────────────────────────");
                }
            }
            catch (Exception ex)
            {
                Interop.Trace.Log("about", "读组件清单失败: " + ex.Message);
            }

            // 资源没了要**说出来**，不能安静地显示一块空白：MPL-2.0 要求告知源码获取方式，
            // 关于页空着就等于没告知。看到这行就该修 csproj。
            Interop.Trace.Log("about", $"嵌入资源 {NoticesResource} 不存在");
            return "组件清单未随程序提供。请查阅随包的 THIRD-PARTY-NOTICES.txt，" +
                   "或联系作者取得。";
        }
    }
}
