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

    public FloatField(string key, double width)
    {
        Key = key;
        Width = width;
    }

    /// <summary>字段名，仅用于自测输出与 XAML 可读性。</summary>
    public string Key { get; }

    public double Width { get; }

    public string Top => _top;
    public string Bottom => _bottom;

    /// <summary>
    /// 主值颜色，跟着阈值走（与 <see cref="MetricCell.ValueBrush"/> 同源）：
    /// 浅色玻璃上取压暗版，深色玻璃上取原色。两套都常驻，切换主题只是换索引。
    /// </summary>
    public Brush TopBrush => _onLight ? _lightBrush : _darkBrush;

    /// <summary>当前用哪套画刷。切浅色/深色时由 <see cref="DashboardState"/> 统一翻。</summary>
    public bool OnLight
    {
        get => _onLight;
        set
        {
            if (_onLight == value) return;
            _onLight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopBrush)));
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
