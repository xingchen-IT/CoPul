using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace CoPul.App.Sdk;

/// <summary>
/// 悬浮窗的一个单元格（需求 6.3）：上行主值 13px 粗、下行次值 10px。
///
/// 为什么不直接把大窗口的 <see cref="MetricCell"/> 绑过来：两者的格式不一样。
/// 大窗口写 `4.9 / 8.0 GB`（带空格、带单位），悬浮窗要 `v4.9/8.0G`——
/// 34px 高的条上多一个空格就少一位有效数字。所以这里单独持一份"紧凑格式"的字符串，
/// 由 <see cref="DashboardState"/> 在每次 Apply 时顺带写好，格式化逻辑仍集中在一处。
///
/// <see cref="Width"/> 来自 6.3 的列表格（34/44/44/74/78/44），
/// 固定列宽是为了让 2Hz 刷新时数字不会左右漂——比"按内容自适应"更稳。
/// </summary>
public sealed class FloatField : INotifyPropertyChanged
{
    private string _top = "—";
    private string _bottom = "";
    private Brush _lightBrush = Brushes.Gray;
    private Brush _darkBrush = Brushes.Gray;
    private bool _onLight = true;

    /// <summary>第二视图主题；null = 跟随 <see cref="OnLight"/>。</summary>
    private bool? _onLightAlt;
    private double _width;

    public FloatField(string key, double width)
    {
        Key = key;
        BaseWidth = width;
        _width = width;
    }

    /// <summary>字段名，仅用于自测输出与 XAML 可读性。</summary>
    public string Key { get; }

    /// <summary>6.3 表格里的基准列宽（100% 档），缩放时以它为底。</summary>
    public double BaseWidth { get; }

    /// <summary>
    /// 当前实际列宽。会随 <see cref="ApplyScale"/> 变化，所以是普通的可通知属性。
    ///
    /// <para>
    /// 为什么列宽要随档位变，而不是让整个窗口套一层 Viewbox：Viewbox 是"先按原尺寸布局
    /// 再整体缩放"，文字会被当成图片拉伸，在 150% DPI × 1.5 档（2.25 倍像素比）下边缘发虚。
    /// 改列宽则是让 WPF 用目标尺寸<b>重新排版</b>，矢量文字始终锐利。
    /// </para>
    /// </summary>
    public double Width
    {
        get => _width;
        private set
        {
            if (Math.Abs(_width - value) < 0.01) return;
            _width = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Width)));
        }
    }

    /// <summary>按档位乘数重算列宽。乘数来自 <see cref="CoPul.App.FloatWindow"/> 的 Q-05 档位。</summary>
    public void ApplyScale(double scale) => Width = BaseWidth * scale;

    public string Top => _top;
    public string Bottom => _bottom;

    /// <summary>
    /// 主值颜色，跟着阈值走（与 <see cref="MetricCell.ValueBrush"/> 同源）：
    /// 浅色玻璃上取压暗版，深色玻璃上取原色。两套都常驻，切换主题只是换索引。
    /// </summary>
    public Brush TopBrush => _onLight ? _lightBrush : _darkBrush;

    /// <summary>
    /// 第二个视图的明暗主题（任务栏模式用）。
    ///
    /// <para>
    /// 为什么需要：悬浮窗和任务栏共用同一批字段，而两者的底色可能相反 ——
    /// 悬浮窗是浅色玻璃（要深字）时，任务栏可能正处在深色模式（要浅字）。
    /// 只用一个 <see cref="OnLight"/> 会互相踩。留一个可空的第二开关，
    /// 未设置时跟随 <see cref="OnLight"/>，所以现有视图的行为完全不变。
    /// </para>
    /// </summary>
    public bool OnLightAlt
    {
        get => _onLightAlt ?? _onLight;
        set
        {
            if (_onLightAlt == value) return;
            _onLightAlt = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopBrushAlt)));
        }
    }

    /// <summary>任务栏用的主值颜色，与 <see cref="TopBrush"/> 互不影响。</summary>
    public Brush TopBrushAlt => OnLightAlt ? _lightBrush : _darkBrush;

    /// <summary>当前用哪套画刷。切浅色/深色时由 <see cref="DashboardState"/> 统一翻。</summary>
    public bool OnLight
    {
        get => _onLight;
        set
        {
            if (_onLight == value) return;
            _onLight = value;

            // 第二视图跟随本值时也要重发，否则任务栏那边颜色停在旧值上
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopBrush)));
            if (_onLightAlt is null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopBrushAlt)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Set(string top, string bottom, Brush lightBrush, Brush darkBrush)
    {
        bool changed = false;

        if (_top != top) { _top = top; changed = true; }
        if (_bottom != bottom) { _bottom = bottom; changed = true; }
        if (!ReferenceEquals(_lightBrush, lightBrush)) { _lightBrush = lightBrush; changed = true; }
        if (!ReferenceEquals(_darkBrush, darkBrush)) { _darkBrush = darkBrush; changed = true; }

        if (changed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
