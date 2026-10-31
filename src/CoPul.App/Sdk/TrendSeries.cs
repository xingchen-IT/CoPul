namespace CoPul.App.Sdk;

/// <summary>
/// 使用率历史的环形缓冲。定长数组 + 写指针，不产生垃圾。
///
/// 容量 120 × 0.5s 挡位 = 近 60 秒。为什么不做无限增长：一来内存无上界，
/// 二来折线图上 60 秒以外的历史肉眼已经看不出趋势，留着只是让每帧多画几百个点。
///
/// <para>
/// M4.24：容量是<b>点数</b>不是时长，所以刷新档位调慢后折线覆盖的时间按比例变长
/// （3s 档 ≈ 6 分钟、5s 档 ≈ 10 分钟）。这是刻意保留的 —— 慢档位下看的就是长趋势，
/// 真要固定 60 秒就得按档位重建数组，代价是切档位时历史被清空。
/// </para>
/// </summary>
public sealed class TrendSeries
{
    private readonly double[] _cpu;
    private readonly double[] _gpu;
    private int _head;
    private int _count;

    public TrendSeries(int capacity)
    {
        Capacity = capacity;
        _cpu = new double[capacity];
        _gpu = new double[capacity];
        _cpu.AsSpan().Fill(double.NaN);
        _gpu.AsSpan().Fill(double.NaN);
    }

    public int Capacity { get; }

    /// <summary>已写入的点数（写满后恒等于 Capacity）。</summary>
    public int Count => _count;

    /// <summary>写入一个采样点。NaN 表示这一项没读到，画的时候断开，不能当 0。</summary>
    public void Push(double cpu, double gpu)
    {
        _cpu[_head] = cpu;
        _gpu[_head] = gpu;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>按「从最旧到最新」的顺序取第 i 个点。画图按这个顺序即可。</summary>
    public double Cpu(int i) => At(_cpu, i);

    public double Gpu(int i) => At(_gpu, i);

    private double At(double[] buf, int i)
    {
        if (i < 0 || i >= _count) return double.NaN;
        int start = (_head - _count + Capacity) % Capacity;
        return buf[(start + i) % Capacity];
    }

    public void Clear()
    {
        _cpu.AsSpan().Fill(double.NaN);
        _gpu.AsSpan().Fill(double.NaN);
        _head = 0;
        _count = 0;
    }
}
