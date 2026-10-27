using System.Windows;
using System.Windows.Media;

namespace CoPul.App.Controls;

/// <summary>
/// 使用率折线图。自绘控件，不引第三方图表库。
///
/// 为什么自己画：
/// - 每 0.5 秒就要重画一次，图表库的通用布局（坐标轴、刻度、图例、动画）每一项都是白花；
/// - 只需要「两条折线 + 三条网格线」，用 DrawingContext 直接画约 0.1ms；
/// - 引库会把体积从几百 KB 抬到几 MB，而 v1 只有一个图。
///
/// 两个刻意的设计：
/// - **纵轴固定 0–100%**：占用率本来就有上界，做自动缩放会让 5% 的抖动看起来像满载；
/// - **数据不足时从右侧画起**：新点永远贴右边，旧数据从左侧长出来，
///   否则启动时折线会从左边慢慢爬，视觉上像"卡住了"。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    private Brush? _gridBrush;
    private Brush? _cpuBrush;
    private Brush? _gpuBrush;
    private Pen? _gridPen;
    private Pen? _cpuPen;
    private Pen? _gpuPen;
    private Brush? _cpuFill;

    /// <summary>数据源。由窗口构造时直接赋值（不走绑定：本控件没有需要双向同步的状态）。</summary>
    public Sdk.TrendSeries? Series { get; set; }

    /// <summary>数据变了调一次。内部只置脏，真正的绘制由 WPF 合并到下一次渲染。</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        var grid = Pen(ref _gridPen, ref _gridBrush, "BrushTrack", 1.0);
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4.0) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }

        var series = Series;
        if (series is null || series.Count == 0) return;

        int n = series.Count;
        int cap = series.Capacity;

        // 最新点贴右边缘；没写满时左侧留空
        double X(int i) => (i + (cap - n)) * w / (cap - 1);
        double Y(double v) => h - Math.Clamp(v, 0, 100) / 100.0 * (h - 2) - 1;

        Draw(dc, series, n, X, Y, false);   // GPU 先画，CPU 压在上面
        Draw(dc, series, n, X, Y, true);
    }

    private void Draw(DrawingContext dc, Sdk.TrendSeries series, int n,
                      Func<int, double> x, Func<double, double> y, bool cpu)
    {
        var pts = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double v = cpu ? series.Cpu(i) : series.Gpu(i);
            if (double.IsNaN(v)) continue;
            pts.Add(new Point(x(i), y(v)));
        }

        if (pts.Count == 0) return;

        Brush brush;
        Pen pen;
        if (cpu)
        {
            brush = Brush(ref _cpuBrush, "AccentCpu");
            pen = Pen(ref _cpuPen, ref _cpuBrush, "AccentCpu", 1.6);
        }
        else
        {
            brush = Brush(ref _gpuBrush, "AccentGpu");
            pen = Pen(ref _gpuPen, ref _gpuBrush, "AccentGpu", 1.6);
        }

        // 面积填充只给 CPU：两条都填会糊成一片，GPU 走纯线条更好读
        if (cpu && pts.Count > 1)
        {
            var fill = _cpuFill ??= MakeFill();

            // 折线首尾各补一个底边点，闭合成面积块
            var poly = new List<Point>(pts)
            {
                new(pts[^1].X, RenderSize.Height),
                new(pts[0].X, RenderSize.Height)
            };

            var figure = new PathFigure { StartPoint = poly[0], IsClosed = true, IsFilled = true };
            figure.Segments.Add(new PolyLineSegment(poly.Skip(1), true));

            var geo = new PathGeometry(new[] { figure });
            geo.Freeze();
            dc.DrawGeometry(fill, null, geo);
        }

        if (pts.Count == 1)
        {
            dc.DrawEllipse(brush, null, pts[0], 1.5, 1.5);
            return;
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private static Brush Brush(ref Brush? cache, string key)
    {
        if (cache is not null) return cache;
        var b = Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        if (b.CanFreeze) b.Freeze();
        cache = b;
        return b;
    }

    private static Pen Pen(ref Pen? penCache, ref Brush? brushCache, string key, double thickness)
    {
        if (penCache is not null) return penCache;
        var pen = new Pen(Brush(ref brushCache, key), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze) pen.Freeze();
        penCache = pen;
        return pen;
    }

    private static Brush MakeFill()
    {
        var b = new SolidColorBrush(Color.FromArgb(38, 0x5B, 0x8F, 0xF9));
        b.Freeze();
        return b;
    }
}
