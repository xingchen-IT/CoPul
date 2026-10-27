using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace CoPul.App.Sdk;

/// <summary>阈值等级。对应需求 10.3「阈值色覆盖语义色，优先级最高」。</summary>
public enum Level
{
    /// <summary>未到阈值，用区块语义色。</summary>
    Normal = 0,
    /// <summary>温度 66–80°C 或占用 71–90%。</summary>
    Warn = 1,
    /// <summary>温度 &gt; 80°C 或占用 &gt; 90%。</summary>
    Danger = 2,
    /// <summary>本机没有这个指标（`—`），用次文字色。</summary>
    Muted = 3
}

/// <summary>
/// 一个数值格。UI 只绑它，不碰 <c>MetricSnapshot</c>——快照是采集层的结构，
/// 直接绑到 XAML 会让「显示格式」「阈值配色」这些纯展示逻辑散进视图里。
/// </summary>
public sealed class MetricCell : INotifyPropertyChanged
{
    private string _value = Fmt.Dash;
    private string _sub = "";
    private double _bar;
    private Visibility _barVisibility = Visibility.Collapsed;
    private Level _level = Level.Muted;

    public MetricCell(string title, string unit, string accentKey)
    {
        Title = title;
        Unit = unit;
        AccentKey = accentKey;
    }

    public string Title { get; }
    public string Unit { get; }
    public string AccentKey { get; }

    public string Value => _value;
    public string Sub => _sub;
    public double Bar => _bar;
    public Visibility BarVisibility => _barVisibility;
    public Visibility SubVisibility => string.IsNullOrEmpty(_sub) ? Visibility.Collapsed : Visibility.Visible;

    public Brush ValueBrush => Palette.Brush(BrushKeyFor(_level, AccentKey));
    public Brush BarBrush => ValueBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 刷新一格。bar 传 null 表示不画进度条（温度、频率这类没有"满量程"的指标）。
    /// </summary>
    public void Apply(string value, Level level, string? sub = null, double? bar = null)
    {
        bool changed = false;

        if (_value != value) { _value = value; changed = true; }
        if (_level != level) { _level = level; changed = true; }

        string newSub = sub ?? "";
        if (_sub != newSub) { _sub = newSub; changed = true; }

        if (bar.HasValue)
        {
            double b = Math.Clamp(bar.Value, 0, 100);
            if (Math.Abs(_bar - b) > 0.05) { _bar = b; changed = true; }
            if (_barVisibility != Visibility.Visible) { _barVisibility = Visibility.Visible; changed = true; }
        }
        else if (_barVisibility != Visibility.Collapsed)
        {
            _barVisibility = Visibility.Collapsed;
            changed = true;
        }

        if (!changed) return;

        Raise(nameof(Value));
        Raise(nameof(Sub));
        Raise(nameof(SubVisibility));
        Raise(nameof(Bar));
        Raise(nameof(BarVisibility));
        Raise(nameof(ValueBrush));
        Raise(nameof(BarBrush));
    }

    /// <summary>标记为不可用。文案统一为 `—`，不要写"0"，A-13 明确禁止把无值当 0。</summary>
    public void Unavailable(string? sub = null) => Apply(Fmt.Dash, Level.Muted, sub);

    private static string BrushKeyFor(Level level, string accentKey) => level switch
    {
        Level.Warn => "BrushWarn",
        Level.Danger => "BrushDanger",
        Level.Muted => "BrushTextSecondary",
        _ => accentKey
    };

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>从应用资源里取画刷。资源在 App.xaml 里集中定义，视图与代码共用一套配色。</summary>
internal static class Palette
{
    private static readonly Dictionary<string, Brush> Cache = new();

    public static Brush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var brush = Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        if (brush.CanFreeze) brush.Freeze();
        Cache[key] = brush;
        return brush;
    }
}
