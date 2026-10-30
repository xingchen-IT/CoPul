using System.Collections.Generic;
using System.Text;

namespace CoPul.App.Sdk;

/// <summary>解析后的热键：修饰键位 + 虚拟键码 + 规范写法。</summary>
/// <param name="Modifiers">Win32 的 <c>MOD_*</c> 位组合（不含 <c>MOD_NOREPEAT</c>）。</param>
/// <param name="VirtualKey">虚拟键码（<c>VK_*</c>）。</param>
/// <param name="Text">规范化后的写法，如 <c>Ctrl+Alt+C</c>。落盘与显示都用它。</param>
public readonly record struct HotkeyCombo(uint Modifiers, uint VirtualKey, string Text);

/// <summary>
/// 热键字符串的解析与格式化（G-03）。
///
/// <para>
/// <b>为什么不直接把配置串丢给 <c>RegisterHotKey</c>：</b>那个 API 要的是
/// 「修饰键位 + 虚拟键码」两个整数，而配置里存的是人能读懂的 <c>Ctrl+Alt+C</c>。
/// 这层转换必须自己做，而且要做得**能报错** —— 用户手改配置写成 <c>Ctrl+Q+Z</c> 时，
/// 静默注册不上（任务栏条不弹、热键无反应）比直接说"不认识的按键"难查得多。
/// </para>
///
/// <para>
/// 全部是纯函数，不碰 Win32：注册失败的原因（被别的程序占用）和写法错误
/// 必须能被分开描述，否则用户看到"热键无效"完全不知道该改什么。
/// </para>
/// </summary>
public static class HotkeyGesture
{
    /// <summary>G-03 规定的默认值。</summary>
    public const string Default = "Ctrl+Alt+C";

    // Win32 MOD_* 常量
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// <c>MOD_NOREPEAT</c>（Win7+）。**必须带上**：按住不放时系统会每 30ms 发一次
    /// <c>WM_HOTKEY</c>，三视图会被连按般地来回切，松手时停在一个不可预测的位置。
    /// </summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>VK_F12。Windows 保留给调试器，注册必然失败，提前拦下比让用户猜好。</summary>
    private const uint VkF12 = 0x7B;

    private static readonly (uint Vk, string Name, string Alias)[] SpecialKeys =
    {
        (0x20, "Space", "spacebar"),
        (0x09, "Tab", ""),
        (0x0D, "Enter", "return"),
        (0x1B, "Esc", "escape"),
        (0x08, "Backspace", "back"),
        (0x2E, "Delete", "del"),
        (0x2D, "Insert", "ins"),
        (0x24, "Home", ""),
        (0x23, "End", ""),
        (0x21, "PageUp", "pgup"),
        (0x22, "PageDown", "pgdn"),
        (0x25, "Left", "leftarrow"),
        (0x26, "Up", "uparrow"),
        (0x27, "Right", "rightarrow"),
        (0x28, "Down", "downarrow"),
    };

    /// <summary>OEM 标点。<c>VK_OEM_*</c> 的码位是键盘布局无关的固定值，可以直接硬编码。</summary>
    private static readonly (char Ch, uint Vk)[] OemKeys =
    {
        ('`', 0xC0), ('-', 0xBD), ('=', 0xBB),
        ('[', 0xDB), (']', 0xDD), ('\\', 0xDC),
        (';', 0xBA), ('\'', 0xDE), (',', 0xBC),
        ('.', 0xBE), ('/', 0xBF),
    };

    // 查表用。静态构造一次，解析只在用户改热键时发生，不急
    private static readonly Dictionary<string, uint> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, string> ByVk = new();

    static HotkeyGesture()
    {
        foreach ((uint vk, string name, string alias) in SpecialKeys)
        {
            ByName[name] = vk;
            if (alias.Length > 0) ByName[alias] = vk;
            ByVk[vk] = name;
        }

        for (int n = 1; n <= 24; n++)
        {
            uint vk = (uint)(0x70 + n - 1);     // VK_F1 = 0x70
            string name = "F" + n;
            ByName[name] = vk;
            ByVk[vk] = name;
        }

        foreach ((char ch, uint vk) in OemKeys)
        {
            ByName[ch.ToString()] = vk;
            ByVk[vk] = ch.ToString();
        }
    }

    /// <summary>
    /// 解析配置里的写法。失败时 <paramref name="error"/> 是给用户看的中文句子。
    /// </summary>
    public static bool TryParse(string? gesture, out HotkeyCombo combo, out string error)
    {
        combo = default;
        error = string.Empty;

        string raw = (gesture ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "热键不能为空";
            return false;
        }

        // 不用 RemoveEmptyEntries 的宽容写法：Ctrl++A 这种多敲一个加号的串
        // 应该报错而不是被猜成 Ctrl+A —— 猜错了用户会以为程序在乱来
        string[] parts = raw.Split('+');
        uint mods = 0;
        uint vk = 0;

        foreach (string part in parts)
        {
            string token = part.Trim();
            if (token.Length == 0)
            {
                error = $"「{raw}」写法不完整（有多余的 +）";
                return false;
            }

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= ModControl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
                case "win":
                case "windows":
                case "meta": mods |= ModWin; continue;
            }

            if (vk != 0)
            {
                error = $"「{raw}」里出现了两个主键";
                return false;
            }

            if (!TryParseKey(token, out vk))
            {
                error = $"不认识的按键「{token}」";
                return false;
            }
        }

        if (vk == 0)
        {
            error = "缺少主键（如 C / F9 / Space）";
            return false;
        }

        if (mods == 0)
        {
            // 无修饰键的全局热键会独占整个系统里的那个键（连在记事本里打字都会被吞），
            // 这不是"用户想要的功能"，是"用户没想到的后果"
            error = "至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        if (vk == VkF12)
        {
            error = "F12 被系统保留给调试器，换一个键";
            return false;
        }

        combo = new HotkeyCombo(mods, vk, Format(mods, vk));
        return true;
    }

    /// <summary>把修饰键位与虚拟键码拼成规范写法。顺序固定为 Ctrl→Alt→Shift→Win。</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var sb = new StringBuilder(16);
        sb.Append(FormatModifiers(modifiers));

        if (ByVk.TryGetValue(vk, out string? name)) sb.Append(name);
        else if (vk is >= 'A' and <= 'Z') sb.Append((char)vk);
        else if (vk is >= '0' and <= '9') sb.Append((char)vk);
        else sb.Append('?').Append(vk);       // 不认识的键，至少别丢掉码位

        return sb.ToString();
    }

    /// <summary>只拼修饰键部分，末尾带 <c>+</c>；没有修饰键时返回空串。</summary>
    public static string FormatModifiers(uint modifiers)
    {
        var sb = new StringBuilder(12);

        if ((modifiers & ModControl) != 0) sb.Append("Ctrl+");
        if ((modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((modifiers & ModShift) != 0) sb.Append("Shift+");
        if ((modifiers & ModWin) != 0) sb.Append("Win+");

        return sb.ToString();
    }

    /// <summary>这个虚拟键码认不认识（设置页录键时用来挡掉媒体键之类的冷门键）。</summary>
    public static bool IsKnownKey(uint vk)
        => ByVk.ContainsKey(vk) || vk is >= 'A' and <= 'Z' or >= '0' and <= '9';

    /// <summary>
    /// 校验一组已经拼好的修饰键与键码，成功时给出规范写法。
    /// 设置页录键走这条：它拿到的已经是 <c>ModifierKeys</c> + <c>KeyInterop</c> 的结果，
    /// 不需要再经过字符串解析。
    /// </summary>
    public static bool TryCompose(uint modifiers, uint vk, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        if (vk == 0)
        {
            error = "没有识别到按键";
            return false;
        }

        if (!IsKnownKey(vk))
        {
            error = "这个键不支持作为全局热键";
            return false;
        }

        if ((modifiers & (ModControl | ModAlt | ModShift | ModWin)) == 0)
        {
            error = "至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        if (vk == VkF12)
        {
            error = "F12 被系统保留给调试器，换一个键";
            return false;
        }

        text = Format(modifiers, vk);
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            char upper = char.ToUpperInvariant(token[0]);
            if (upper is >= 'A' and <= 'Z') { vk = upper; return true; }
            if (upper is >= '0' and <= '9') { vk = upper; return true; }
        }

        return ByName.TryGetValue(token, out vk);
    }
}
