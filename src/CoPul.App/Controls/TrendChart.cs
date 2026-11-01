using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CoPul.App.Controls;

/// <summary>
/// 使用率折线图。自绘控件，不引第三方图表库。
///
/// 为什么自己画：
/// - 每个刷新档位就要重画一次（默认 0.5s，见 <c>AppConfig.SampleIntervalMs</c>），
///   图表库的通用布局（坐标轴、刻度、图例、动画）每一项都是白花；
/// - 只需要「两条折线 + 三条网格线 + 五个纵轴刻度」，用 DrawingContext 直接画约 0.1ms；
/// - 引库会把体积从几百 KB 抬到几 MB，而 v1 只有一个图。
///
/// 两个刻意的设计：
/// - **纵轴固定 0–100%**：占用率本来就有上界，做自动缩放会让 5% 的抖动看起来像满载；
/// - **数据不足时从右侧画起**：新点永远贴右边，旧数据从左侧长出来，
///   否则启动时折线会从左边慢慢爬，视觉上像"卡住了"。
///
/// 纵轴：左侧 38px 是刻度区（量程的四等分点），折线只画在剩下的区域里，
/// 标签因此永远不会被数据盖住。顶/底两个标签贴边时会向内 clamp，避免被裁掉半行。
/// 量程与单位由 <see cref="MaxValue"/> / <see cref="Unit"/> 决定：
/// 占用率图是 0–100%、温度图是 0–100°C，同一个控件两种用法。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    /// <summary>左侧纵轴刻度区宽度（px）。按最宽的标签 "100°C" 留的余量。</summary>
    private const double AxisW = 38;

    private static readonly Typeface TickFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush? _gridBrush;
    private Brush? _axisBrush;
    private Brush? _tickBrush;
    private Brush? _cpuBrush;
    private Brush? _gpuBrush;
    private Pen? _gridPen;
    private Pen? _axisPen;
    private Pen? _cpuPen;
    private Pen? _gpuPen;
    private Brush? _cpuFill;

    /// <summary>数据源。由窗口构造时直接赋值（不走绑定：本控件没有需要双向同步的状态）。</summary>
    public Sdk.TrendSeries? Series { get; set; }

    /// <summary>
    /// 纵轴上限（A-30）。占用率图 100、温度图 100（摄氏度）。
    /// 两个图都写死量程，不做自动缩放：自动缩放会把 5% 的抖动画成满载，
    /// 也会让温度曲线的纵轴每次跳动，看不出"是不是在爬升"。
    /// </summary>
    public double MaxValue { get; set; } = 100;

    /// <summary>刻度文字后缀：<c>"%"</c> 或 <c>"°C"</c>。</summary>
    public string Unit { get; set; } = "%";

    /// <summary>数据变了调一次。内部只置脏，真正的绘制由 WPF 合并到下一次渲染。</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = RenderSize.Width;
        double h = RenderSize.Height;
        double plotW = w - AxisW;
        if (plotW <= 0 || h <= 0) return;

        double max = MaxValue > 0 ? MaxValue : 100;

        // 值 → 像素。折线和刻度共用这一个映射，保证 50% 的标签正好落在 50% 的线上。
        double Y(double v) => h - Math.Clamp(v, 0, max) / max * (h - 2) - 1;

        // 网格线用 BrushGrid 而不是 BrushTrack（M4.28）：后者在卡片底上是一排能单独看见的横线，
        // 三条线一拉，整张图先入眼的是"表格"而不是曲线。这里只留够暗示量程的浓度。
        var grid = Pen(ref _gridPen, ref _gridBrush, "BrushGrid", 1.0);
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(Y(max * i / 4.0)) + 0.5;
            dc.DrawLine(grid, new Point(AxisW, y), new Point(w, y));
        }

        // 纵轴：一条竖线 + 底部的 0% 基线
        var axis = Pen(ref _axisPen, ref _axisBrush, "BrushDivider", 1.0);
        dc.DrawLine(axis, new Point(AxisW + 0.5, 0), new Point(AxisW + 0.5, h));
        dc.DrawLine(grid, new Point(AxisW, Math.Round(Y(0)) + 0.5), new Point(w, Math.Round(Y(0)) + 0.5));

        DrawAxisLabels(dc, h, max, Y);

        var series = Series;
        if (series is null || series.Count == 0) return;

        int n = series.Count;
        int cap = series.Capacity;

        // 最新点贴右边缘；没写满时左侧留空
        double X(int i) => AxisW + (i + (cap - n)) * plotW / (cap - 1);

        Draw(dc, series, n, X, Y, false);   // GPU 先画，CPU 压在上面
        Draw(dc, series, n, X, Y, true);
    }

    /// <summary>画纵轴刻度文字。量程的四等分点，每帧 5 个 FormattedText，开销可以忽略。</summary>
    private void DrawAxisLabels(DrawingContext dc, double h, double max, Func<double, double> y)
    {
        var brush = Brush(ref _tickBrush, "BrushTextTertiary");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i <= 4; i++)
        {
            double t = max * (4 - i) / 4.0;     // 从顶到底：max、3/4、1/2、1/4、0
            var ft = new FormattedText(t.ToString("F0") + Unit, CultureInfo.CurrentUICulture,
                                       FlowDirection.LeftToRight, TickFace, 9.5, brush, dpi);

            // 右对齐贴着轴线；顶/底两个标签往里收，别被裁掉半行
            double ty = Math.Clamp(y(t) - ft.Height / 2, 0, Math.Max(0, h - ft.Height));
            double tx = Math.Max(0, AxisW - 6 - ft.Width);
            dc.DrawText(ft, new Point(tx, ty));
        }
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
