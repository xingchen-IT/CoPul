using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CoPul.App.Controls;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 任务栏模式（需求第 7 章）。把六+二格指标画进 Windows 任务栏左侧的空白区。
///
/// <para>
/// <b>嵌入方式</b>：<c>SetParent</c> 到 <c>Shell_TrayWnd</c> 并加 <c>WS_CHILD</c>（7.1 的 B 方案）。
/// 实测 Win11 build 26200 上这条路可行：子窗口被任务栏接受、能正常绘制、坐标相对任务栏客户区。
/// 屏幕上"看不见"过一次，但那是全屏游戏盖住了任务栏，不是挂载失败 ——
/// 用 <c>PrintWindow</c> 直接向任务栏要绘制结果就能看到自己那块。
/// </para>
///
/// <para>
/// <b>为什么不改变任务栏布局</b>：这仍然是"占位"而不是"插入"。它只会占用
/// 开始按钮左边的空白，图标挤过来时会被盖住 —— 7.1 已经把这条列为必须告知的限制。
/// </para>
///
/// <para>
/// 数据不自己采：与大窗口、悬浮窗共用同一份 <see cref="DashboardState"/>。
/// 每多一份 CollectorHost 就多一个 PresentMon 会话，FPS 会互相踩（M2 已踩过）。
/// </para>
/// </summary>
public partial class TaskbarWindow : Window
{
    /// <summary>文字取色策略（7.5：文字必须跟任务栏明暗走，否则深底黑字直接不可读）。</summary>
    public enum InkMode
    {
        /// <summary>读 <c>SystemUsesLightTheme</c> 自动决定。</summary>
        Auto,
        /// <summary>强制浅色字（任务栏是深底）。</summary>
        Light,
        /// <summary>强制深色字（任务栏是浅底）。</summary>
        Dark
    }

    /// <summary>左边距，物理像素。7.3 写的是 8px，任务栏是物理像素坐标系，直接照用。</summary>
    private const int LeftInset = 8;

    /// <summary>组间距（DIP）。必须与 XAML 里各列的 <c>Margin="0,0,7,0"</c> 一致。</summary>
    private const double CellGapDip = 7;

    /// <summary>两端内边距（DIP）。必须与 XAML 里 Shell 的 <c>Padding="6,0"</c> 一致。</summary>
    private const double PaddingDip = 6;

    private readonly DashboardState _state;
    private readonly AppConfig _config;

    private IntPtr _bar;
    private bool _attached;
    private InkMode _ink = InkMode.Auto;

    /// <summary>点击是否穿透（7.6 / Q-12）。true 时 WM_NCHITTEST 一律返回 HTTRANSPARENT。</summary>
    private bool _clickThrough = true;

    /// <summary>当前实际生效的毛玻璃方式，由 <see cref="WindowGlass.Apply"/> 返回，用于诊断输出。</summary>
    public string GlassMode { get; private set; } = "未设置";

    /// <summary>Auto 模式下最近一次成功测得的"任务栏是浅底吗"。采样失败时沿用这个值。</summary>
    private bool _autoIsLight;

    /// <summary>接收 <c>TaskbarCreated</c> 广播的隐藏顶层窗口。子窗口收不到广播，见 <see cref="InstallTaskbarWatcher"/>。</summary>
    private HwndSource? _watcher;

    private int _taskbarCreatedMsg;

    /// <summary>
    /// 存活巡检（每秒一次）。为什么不能只靠 <c>TaskbarCreated</c> 广播：
    /// 本机实测广播<b>根本没到</b> —— 日志里只有 <c>attach</c> 和 <c>watcher</c> 两行，
    /// 没有任何 <c>TaskbarCreated 收到</c>。最可能的原因是 UIPI：explorer 是中完整性，
    /// 若本进程提权，跨完整性级别的窗口消息会被直接丢弃。
    /// 广播是"快"（重启瞬间就能知道），巡检是"准"（不依赖任何消息），两个都要。
    /// </summary>
    private DispatcherTimer? _watchdog;

    /// <summary>正在恢复中，挡住巡检重入（重建会同步 Close 本窗口）。</summary>
    private bool _recovering;

    /// <summary>已经交给 App 重建，本实例不再管任何事。</summary>
    private bool _handedOff;

    /// <summary>
    /// 自己的 HWND，在挂载时缓存下来。
    ///
    /// <para>
    /// 不能拿 <see cref="Handle"/> 做存活判断：窗口被父窗口连带销毁后，
    /// <c>WindowInteropHelper.Handle</c> 会返回 <c>IntPtr.Zero</c>，
    /// 巡检里 `if (hwnd == 0) return` 会把它当成"还没初始化"直接跳过 ——
    /// 于是明明窗口已经没了，巡检却什么都不做（第一次就是这么静默失败的）。
    /// </para>
    /// </summary>
    private IntPtr _hwnd;

    /// <summary>双击或被菜单要求"回大窗口"。窗口之间不互相引用，由 App 接线。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>切回悬浮窗（任务栏条上无法做复杂设置，菜单里给一条出口）。</summary>
    public event EventHandler? FloatRequested;

    /// <summary>
    /// 本窗口已被系统销毁，需要外部重建一个新的（explorer 重启时会发生）。
    /// App 收到后负责关掉旧实例、new 一个新的并 Show。
    /// </summary>
    public event EventHandler? RecreateRequested;

    /// <summary>自测时不改写用户的配置。</summary>
    public bool PersistToConfig { get; set; } = true;

    /// <summary>诊断串，供截图 sidecar 记录。</summary>
    public string PlacementDiag { get; private set; } = string.Empty;

    /// <summary>当前 HWND；窗口未初始化时返回 <c>IntPtr.Zero</c>。</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 调试日志。任务栏重挂涉及 4 个异步环节（广播 → 重试 → 检查存活 → 重建），
    /// 中间任何一环断了，表面现象都只是"条没了"，靠猜会连错三次。
    /// </summary>
    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "copul-tray.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不能影响主流程 */ }
    }

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(TaskbarWindow),
            new PropertyMetadata(null));

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public TaskbarWindow(DashboardState state, AppConfig config)
    {
        InitializeComponent();
        DataContext = state;

        _state = state;
        _config = config;

        // 字段表必须在第一次 Reposition 之前建好（Attach 里就会调）
        InitFields();
        RefreshFieldMenu();

        if (Enum.TryParse(config.TaskbarInk, out InkMode saved)) _ink = saved;
        _clickThrough = config.TaskbarClickThrough;

        SourceInitialized += (_, _) =>
        {
            // 先拿掉 Alt+Tab 里的条目，再挂到任务栏位置：顺序反了会先闪一个空壳
            WindowGlass.HideFromAltTab(this);

            // 自己不需要 DWM 的整窗 accent —— 7.5 默认「跟随系统」= 完全透明，
            // 底色由任务栏自己提供。这里只把 WPF 的窗口底刷成透明。
            //
            // --tray-opaque 是排障开关：窗口表面带 alpha 时，DWM 会把它和父窗口
            // 的表面做混合，混合结果可能被永久留在父窗口那边。关掉透明可以验证
            // "残影是不是 alpha 混合造成的"。
            if (!Environment.GetCommandLineArgs().Contains("--tray-opaque"))
                WindowGlass.MakeClientTransparent(this);

            Attach();

            // 点击穿透（7.6 / Q-12 采纳方案 2）。
            //
            // 用 WM_NCHITTEST 返回 HTTRANSPARENT，而不是给窗口加 WS_EX_TRANSPARENT：
            // 后者在 WPF 上要配 WS_EX_LAYERED 才跨进程生效，而 layered 会让 WPF
            // 换成软件合成路径，毛玻璃和文字渲染都会变（M2 试过整窗 alpha 的代价）。
            // HTTRANSPARENT 只影响命中测试，不碰渲染 —— 画面一个像素都不变。
            //
            // 穿透之后条上收不到任何鼠标事件，右键菜单也就没了。这是 Q-12 选方案 2
            // 的已知代价：全部操作走托盘右键。需要临时调文字颜色时，用托盘菜单
            // 里的「任务栏条可点击」把穿透关掉。
            HwndSource.FromHwnd(Handle)?.AddHook(HitTestHook);

            // 注意：WM_SETTINGCHANGE 挂在 InstallTaskbarWatcher 建的那个**顶层**窗口上，
            // 不能挂这里。条自己是 WS_CHILD，而系统广播只投递给顶层窗口 ——
            // 挂在自己身上等于从来没装上过（换主题、挪任务栏都不会有反应）。

            InstallTaskbarWatcher();
            StartWatchdog();
            StartHoverWatch();
        };

        // 放在 ContentRendered：Auto 模式要采样任务栏亮度，必须先有 _bar
        ContentRendered += (_, _) => ApplyInk();
    }

    /// <summary>
    /// 挂到任务栏上并定位。explorer 重启后要重来一遍，所以独立成方法。
    /// </summary>
    public void Attach()
    {
        // 先缓存自己的 HWND：后面巡检要靠它判断"我还在不在"，而 WindowInteropHelper
        // 在窗口被销毁后会返回 0（见 _hwnd 的注释）
        IntPtr hwnd = Handle;
        if (hwnd != IntPtr.Zero) _hwnd = hwnd;

        _bar = Taskbar.Find();
        if (_bar == IntPtr.Zero)
        {
            // 程序可能比任务栏先起来（开机自启、或正赶上 explorer 重启），等一会儿再试
            PlacementDiag = "attach: Shell_TrayWnd 未找到";
            ScheduleReattach(0);
            return;
        }

        if (hwnd == IntPtr.Zero) return;

        _attached = Taskbar.AttachAsTopLevel(hwnd);
        Trace($"attach(top-level): bar=0x{_bar:X} ok={_attached} hwnd=0x{hwnd:X}");
        Reposition();
    }

    /// <summary>上次摆位时的关键量。<see cref="Reposition"/> 现在被 1 Hz 巡检调用，必须能空转。</summary>
    private string _lastSig = string.Empty;

    /// <summary>按任务栏当前矩形重新摆位。任务栏高度/位置变化（缩放、移动）时也要调。</summary>
    public void Reposition()
    {
        if (!_attached || _bar == IntPtr.Zero) return;
        if (!Taskbar.TryGetRect(_bar, out Win32Window.RECT barRect)) return;

        Taskbar.Edge edge = Taskbar.EdgeOf(_bar);
        int freeWidth = Taskbar.FreeLeftWidth(_bar);
        uint dpi = Monitors.DpiForWindow(_bar);
        bool autoHide = Taskbar.IsAutoHide(_bar);

        // 巡检每秒都会调一次这里，几何没变就必须什么都不做 ——
        // 哪怕用同样的数值再 SetWindowPos 一次，也会引发 WM_WINDOWPOSCHANGED + WPF 重新布局，
        // 变成一个每秒一次的免费掉帧。任务栏搬动/改高度/改 DPI 时这几个量才会变。
        // 字段开关（7.4）也会改所需宽度，所以可见字段集合必须进签名。
        string fieldSig = string.Join(",", _config.TaskbarHiddenFields ?? Array.Empty<string>());
        string sig = $"{edge}|{barRect.Left},{barRect.Top},{barRect.Right},{barRect.Bottom}|{freeWidth}|{dpi}|{fieldSig}";
        if (sig == _lastSig) return;
        _lastSig = sig;

        // 7.7 / Q-13 任务栏位置：只做"底部"和"顶部"这两种横向任务栏。
        // 竖向（左/右）任务栏宽度只有 72 px 左右，八格横排放不下 —— 与其硬塞出一条
        // 被压扁的怪窗口，不如直接不画（用户在设置里把任务栏挪回上下就自动恢复）。
        // 检测走 ABM_GETTASKBARPOS 的 uEdge，不用矩形比较：自动隐藏时整条滑出屏幕，
        // 矩形反推会得出"既不在上也不在下"。
        bool horizontal = edge is Taskbar.Edge.Bottom or Taskbar.Edge.Top;

        if (!horizontal)
        {
            Suppress("竖向任务栏暂不支持");
            return;
        }

        // 自动隐藏：任务栏整条滑出屏幕时跟着一起藏。
        // 子窗口时代这是白送的（父窗口裁剪），顶层窗口必须自己处理，
        // 否则条会孤零零地浮在屏幕边缘，任务栏都收起来了它还在。
        if (Monitors.TryPrimaryMonitorBounds(out Win32Window.RECT screen) &&
            (barRect.Bottom <= screen.Top || barRect.Top >= screen.Bottom))
        {
            Suppress("任务栏已自动隐藏");
            return;
        }

        // 高度跟随任务栏（7.3）。宽度取开始按钮左侧的空白，最多用到那里。
        int barHeight = barRect.Bottom - barRect.Top;
        if (barHeight <= 0 || freeWidth <= LeftInset)
        {
            Suppress("任务栏左侧没有可用空白");
            return;
        }

        Unsuppress();

        int width = freeWidth - LeftInset;

        // 内容需要的宽度：按 7.4 的可见字段算，再按 7.7 在空间不够时自动让位
        // （组间距 7 + 两端内边距 6+6，与 TaskbarWindow.xaml 对齐）。
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        List<(string Key, FloatField Field, FrameworkElement Host)> visible = VisibleCells();
        int neededPx = NeededPx(visible, scale);

        // 7.7「任务栏已满」：可用空白放不下时按优先级从右向左砍字段
        // （先功耗、再延迟、再网络速率，FPS 留到最后）。砍掉的字段只是这次不画，
        // **不改配置** —— 任务栏图标挤走之后条要能自己长回来。
        var dropped = new List<string>();
        if (neededPx > width)
        {
            foreach (string key in TrayFields.AutoDropOrder)
            {
                if (neededPx <= width || visible.Count <= 1) break;

                int index = visible.FindIndex(c => c.Key == key);
                if (index < 0) continue;

                dropped.Add(key);
                visible.RemoveAt(index);
                neededPx = NeededPx(visible, scale);
            }
        }

        ApplyCellLayout(visible);

        int finalWidth = Math.Min(width, Math.Max(neededPx, 1));

        // A 方案：窗口是独立顶层窗口，坐标是**屏幕物理像素**，直接贴着任务栏那一行摆。
        // （B 方案时这里传的是相对任务栏客户区的 (LeftInset, 0)，语义完全不同。）
        //
        // topmost 是必须的：任务栏本身在 topmost 组里，普通顶层窗口会被它盖住。
        Monitors.SetBounds(_hwnd, barRect.Left + LeftInset, barRect.Top, finalWidth, barHeight,
                           topmost: true);

        PlacementDiag = $"bar=({barRect.Left},{barRect.Top})-({barRect.Right},{barRect.Bottom}) " +
                        $"edge={edge} free={freeWidth} need={neededPx} used={finalWidth} " +
                        $"scale={scale:F2} autohide={autoHide} " +
                        $"fields={visible.Count}/{TrayFields.Keys.Length} dropped=[{string.Join(",", dropped)}]";
    }

    /// <summary>当前是不是被主动藏起来了（竖向任务栏 / 没有空白）。</summary>
    private bool _suppressed;

    private void Suppress(string reason)
    {
        PlacementDiag = "suppressed: " + reason;
        if (_suppressed) return;

        _suppressed = true;
        Taskbar.SetChildVisible(_hwnd, false);
        Trace($"suppress: {reason}");
    }

    private void Unsuppress()
    {
        if (!_suppressed) return;

        _suppressed = false;
        Taskbar.SetChildVisible(_hwnd, true);
        Trace("unsuppress: 恢复显示");
    }

    /// <summary>八个字段，按 7.4 的顺序。</summary>
    private FloatField[] AllFields => new[]
    {
        _state.FloatFps, _state.FloatCpu, _state.FloatGpu, _state.FloatRam,
        _state.FloatDisk, _state.FloatIo, _state.FloatLat, _state.FloatPwr,
    };

    /// <summary>
    /// 字段 key / 数据 / XAML 容器三件套（7.4 / Q-03）。
    ///
    /// <para>
    /// 为什么要有这张表：开关一个字段要同时动三处 —— 布局宽度、WPF 容器的
    /// <c>Visibility</c>、菜单勾选。按名字逐个写八个 if 迟早会漏一个，
    /// 而漏掉的表现是"点了没反应"或"关掉了但右边留一段空白"，很难对上号。
    /// </para>
    /// </summary>
    private readonly List<(string Key, FloatField Field, FrameworkElement Host)> _cells = new();

    /// <summary>条右键菜单里的八个勾选项。穿透开着时够不着，同款开关在托盘菜单里。</summary>
    private readonly List<(string Key, MenuItem Item)> _fieldMenuItems = new();

    /// <summary>在 <c>InitializeComponent</c> 之后调用一次，把 XAML 里的命名容器与数据接起来。</summary>
    private void InitFields()
    {
        _cells.Clear();
        _cells.Add(("fps", _state.FloatFps, FpsCell));
        _cells.Add(("cpu", _state.FloatCpu, CpuCell));
        _cells.Add(("gpu", _state.FloatGpu, GpuCell));
        _cells.Add(("ram", _state.FloatRam, RamCell));
        _cells.Add(("disk", _state.FloatDisk, DiskCell));
        _cells.Add(("io", _state.FloatIo, IoCell));
        _cells.Add(("lat", _state.FloatLat, LatCell));
        _cells.Add(("pwr", _state.FloatPwr, PwrCell));

        _fieldMenuItems.Clear();
        _fieldMenuItems.Add(("fps", FieldFpsMenu));
        _fieldMenuItems.Add(("cpu", FieldCpuMenu));
        _fieldMenuItems.Add(("gpu", FieldGpuMenu));
        _fieldMenuItems.Add(("ram", FieldRamMenu));
        _fieldMenuItems.Add(("disk", FieldDiskMenu));
        _fieldMenuItems.Add(("io", FieldIoMenu));
        _fieldMenuItems.Add(("lat", FieldLatMenu));
        _fieldMenuItems.Add(("pwr", FieldPwrMenu));
    }

    /// <summary>按配置选出可见字段。配置若被手改成"全关"，这里兜住最后一列，不留 1 px 的窗口。</summary>
    private List<(string Key, FloatField Field, FrameworkElement Host)> VisibleCells()
    {
        var list = new List<(string Key, FloatField Field, FrameworkElement Host)>(_cells.Count);
        foreach (var cell in _cells)
        {
            if (TrayFields.IsVisible(_config, cell.Key)) list.Add(cell);
        }

        if (list.Count == 0 && _cells.Count > 0) list.Add(_cells[^1]);
        return list;
    }

    /// <summary>
    /// 这些字段横排需要多宽（物理像素）。
    /// 7.4 / Q-03 写的是「关闭后宽度自适应收缩，不保留空位」→ 最后一个可见列不带右间距。
    /// </summary>
    private static int NeededPx(List<(string Key, FloatField Field, FrameworkElement Host)> cells, double scale)
    {
        double dip = PaddingDip * 2;
        for (int i = 0; i < cells.Count; i++)
        {
            dip += cells[i].Field.Width;
            if (i < cells.Count - 1) dip += CellGapDip;
        }

        return (int)Math.Ceiling(dip * scale);
    }

    /// <summary>把可见性 / 间距同步到 XAML。不可见的设 <c>Collapsed</c>（不占位，不是 <c>Hidden</c>）。</summary>
    private void ApplyCellLayout(List<(string Key, FloatField Field, FrameworkElement Host)> visible)
    {
        foreach (var cell in _cells)
        {
            int index = -1;
            for (int v = 0; v < visible.Count; v++)
            {
                if (ReferenceEquals(visible[v].Host, cell.Host)) { index = v; break; }
            }

            if (index < 0)
            {
                if (cell.Host.Visibility != Visibility.Collapsed)
                    cell.Host.Visibility = Visibility.Collapsed;
                continue;
            }

            if (cell.Host.Visibility != Visibility.Visible)
                cell.Host.Visibility = Visibility.Visible;

            Thickness margin = index == visible.Count - 1
                ? new Thickness(0)
                : new Thickness(0, 0, CellGapDip, 0);

            if (cell.Host.Margin != margin) cell.Host.Margin = margin;
        }
    }

    /// <summary>
    /// 切换一个字段的显示（7.4 / Q-03）。条上的右键菜单与托盘菜单共用这一条路径。
    /// </summary>
    public void ToggleField(string key)
    {
        if (!TrayFields.Toggle(_config, key))
        {
            // 只剩这一个字段时拒绝关闭：全关会让条缩成 1 px 的竖线，比不给点更让人困惑
            Trace($"field: 拒绝关闭 {key}（这是最后一个可见字段）");
            RefreshFieldMenu();
            return;
        }

        if (PersistToConfig) ConfigStore.SaveCurrent();
        ReloadFields();

        Trace($"field: {key} → {(TrayFields.IsVisible(_config, key) ? "显示" : "隐藏")} " +
              $"hidden=[{string.Join(",", _config.TaskbarHiddenFields ?? Array.Empty<string>())}]");
    }

    /// <summary>配置被外部改动后重新布一次（App 的托盘菜单会调）。</summary>
    public void ReloadFields()
    {
        _lastSig = string.Empty;    // 强制下一次 Reposition 真算，不走几何签名短路
        RefreshFieldMenu();
        Reposition();
    }

    /// <summary>把勾选与置灰同步到条上的右键菜单。</summary>
    private void RefreshFieldMenu()
    {
        int visibleCount = TrayFields.VisibleCount(_config);

        foreach (var (key, item) in _fieldMenuItems)
        {
            bool on = TrayFields.IsVisible(_config, key);
            item.IsChecked = on;

            // 只剩它一个可见时，这一项置灰 —— 让"不能再关"成为看得见的事实，
            // 而不是点了没反应
            item.IsEnabled = !(on && visibleCount <= 1);
        }
    }

    /// <summary>
    /// 菜单每次弹出前同步一次勾选。
    ///
    /// <para>
    /// <c>MenuItem</c> 自身没有 <c>Opened</c> 事件，只能挂在 <c>ContextMenu</c> 上。
    /// 必须刷新而不能只靠点击时更新：托盘菜单也能改字段开关，
    /// 而条这边的勾选状态停在旧值上，会出现"菜单里勾着、条上却没有"。
    /// </para>
    /// </summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e) => RefreshFieldMenu();

    private void Field_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key }) ToggleField(key);
    }

    /// <summary>
    /// 7.5 的文字颜色。Auto 采样任务栏的**实际亮度**，不读
    /// <c>SystemUsesLightTheme</c>：实测那个说"浅色"而任务栏是深蓝灰（跟随壁纸），
    /// 按注册表判断会给深底配深字，什么都看不见。
    /// </summary>
    public void ApplyInk()
    {
        bool taskbarIsLight;
        switch (_ink)
        {
            case InkMode.Light:
                taskbarIsLight = false;
                break;
            case InkMode.Dark:
                taskbarIsLight = true;
                break;
            default:
                double luma = Taskbar.SampleLuma(_bar);

                // luma < 0 = 一点有效像素都没取到（自动隐藏时任务栏整条滑出屏幕）。
                // 这时**保持上一次的判断**。早先的实现把"没采到"当 128 处理，
                // 再拿 > 128 一比就翻成浅底 → 深色字，任务栏滑回来是深底深字，一片糊。
                if (luma >= 0) _autoIsLight = luma > 128;
                taskbarIsLight = _autoIsLight;
                break;
        }

        // 走 OnLightAlt 而不是 DashboardState.SetFloatTheme：那个会翻所有字段的
        // OnLight，把悬浮窗的颜色一起改掉。两个视图共用一份字段，主题必须各管各的。
        foreach (FloatField f in AllFields) f.OnLightAlt = taskbarIsLight;

        LabelBrush = taskbarIsLight
            ? new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63))
            : new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xCE));

        // 给条一层淡底。为什么不能纯透明（7.5 的「跟随系统」）：
        // 实测任务栏是"跟随壁纸"的半透明，壁纸很亮时（现场是一张橙红星球）深色字
        // 直接糊成一团。垫一层 40% 的对比色后，字在任何壁纸上都能读，
        // 视觉上也还是一个"任务栏上的小组件"，不破坏一致性。
        Shell.Background = taskbarIsLight
            ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        // A 方案：条从"任务栏的子窗口"变成了独立顶层窗口，不能再指望"父窗口透出来"当背景
        // —— WPF 非 layered 窗口会丢弃 alpha，纯透明只会变成一块黑。
        // 用与悬浮窗同一套 DWM accent 亚克力（A-35/A-36：必须 ACRYLICBLURBEHIND
        // 且 AccentFlags=0，否则 tint 的 alpha 会被整个忽略），深浅跟随任务栏实际亮度。
        if (_attached)
        {
            GlassMode = WindowGlass.Apply(this, WindowGlass.Backdrop.Acrylic,
                                         dark: !taskbarIsLight, tintOpacity: 0.10);
        }

        AutoInkMenu.IsChecked = _ink == InkMode.Auto;
        LightInkMenu.IsChecked = _ink == InkMode.Light;
        DarkInkMenu.IsChecked = _ink == InkMode.Dark;
    }

    /// <summary>
    /// 装 <c>TaskbarCreated</c> 监听（7.7 的 explorer 重启）。
    ///
    /// <para>
    /// 关键点：这条消息是 <c>HWND_BROADCAST</c> 发出去的，而广播<b>只投递给顶层窗口</b>。
    /// 我们的条本身是任务栏的子窗口，收不到。必须另开一个不显示的顶层窗口专门收。
    /// message-only 窗口（<c>HWND_MESSAGE</c>）同样收不到广播，所以不能用它。
    /// </para>
    /// </summary>
    private void InstallTaskbarWatcher()
    {
        _taskbarCreatedMsg = Taskbar.RegisterMessage("TaskbarCreated");
        Trace($"watcher: msg={_taskbarCreatedMsg}");
        if (_taskbarCreatedMsg == 0) return;

        var p = new HwndSourceParameters("CoPulTaskbarWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                    // 不带 WS_VISIBLE → 不显示
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW → 不进 Alt+Tab
        };

        try
        {
            _watcher = new HwndSource(p);
            _watcher.AddHook(BroadcastHook);
            Trace($"watcher: hwnd=0x{_watcher.Handle:X}");
        }
        catch
        {
            // 装不上就退化成"重启 explorer 后手动重开"，不影响其余功能
            _watcher = null;
        }
    }

    /// <summary>
    /// 点击穿透开关（7.6 / Q-12）。
    ///
    /// <para>
    /// 改这个不需要重建窗口：<c>WM_NCHITTEST</c> 每次命中测试都读当前字段，
    /// 关掉之后条立刻恢复可点（能弹右键菜单），打开就恢复穿透。
    /// </para>
    /// </summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;
            Trace($"click-through={value}");

            if (!PersistToConfig) return;
            _config.TaskbarClickThrough = value;
            ConfigStore.SaveCurrent();
        }
    }

    /// <summary>
    /// 命中测试穿透。返回 <c>HTTRANSPARENT</c> 后系统会把鼠标消息继续往下层窗口投递，
    /// 条本身收不到点击 —— 这正是 7.6 要的"不抢占"。
    ///
    /// <para>
    /// 为什么不用 <c>WS_EX_TRANSPARENT</c>：顶层窗口要真正跨进程穿透得配
    /// <c>WS_EX_LAYERED</c>，而 layered 会把 WPF 换成软件合成路径，
    /// 毛玻璃和文字渲染都会变样（M2 为整窗 alpha 付过这个代价）。
    /// 命中测试只影响鼠标，一个像素都不动。
    /// </para>
    /// </summary>
    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (msg == WM_NCHITTEST && _clickThrough)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    private IntPtr BroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {        if (msg == _taskbarCreatedMsg)
        {
            Trace("TaskbarCreated 收到");
            ScheduleReattach(0);
            return IntPtr.Zero;
        }

        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DISPLAYCHANGE = 0x007E;

        // 主题 / DPI / 显示器 / 任务栏位置变化都会发这两条，而且来得非常密。
        // 排到 Dispatcher 队尾做（窗口过程里不能改窗口尺寸），并用一个标志合并突发的一串。
        if (msg == WM_SETTINGCHANGE || msg == WM_DISPLAYCHANGE)
        {
            if (_settingsQueued) return IntPtr.Zero;
            _settingsQueued = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _settingsQueued = false;
                if (_handedOff) return;
                ApplyInk();
                Reposition();
            }));
        }

        return IntPtr.Zero;
    }

    private bool _settingsQueued;

    /// <summary>
    /// 每秒检查一次"条还在不在、任务栏还是不是原来那个"。
    /// 只做两次 <c>IsWindow</c>，不进 <c>FindWindow</c>，开销可以忽略。
    /// </summary>
    private void StartWatchdog()
    {
        if (_watchdog is not null) return;

        Trace($"watchdog: 启动 elevated={Taskbar.IsElevated()}");

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => Poll();
        _watchdog.Start();
    }

    // ---------------- 7.6 hover tooltip ----------------

    /// <summary>悬停提示的轮询定时器（2 Hz）。</summary>
    private DispatcherTimer? _hoverWatch;

    private FieldTooltip? _tooltip;

    /// <summary>
    /// 启动悬停轮询。
    ///
    /// <para>
    /// <b>为什么是轮询而不是 <c>MouseEnter</c></b>：条默认点击穿透（Q-12 方案 2），
    /// <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c> 之后鼠标消息根本不投给它，
    /// 所有 WPF 鼠标事件都不会来。7.6 又明确要求悬停显示 tooltip ——
    /// 两者只能靠"自己问系统光标在哪"来调和。
    /// </para>
    ///
    /// <para>
    /// 开销：条不可见时第一行就 return，只剩一次 <c>IsVisible</c> 判断；
    /// 可见时每次一个 <c>GetCursorPos</c> + 一次 <c>GetWindowRect</c>（微秒级）。
    /// 相比 1 Hz 巡检并不可观，也不可能进入 A-26 的量级。
    /// </para>
    /// </summary>
    private void StartHoverWatch()
    {
        if (_hoverWatch is not null) return;

        _hoverWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverWatch.Tick += (_, _) => PollHover();
        _hoverWatch.Start();
    }

    private void PollHover()
    {
        // 切到别的视图时 App 会把条 Hide 掉，但定时器还在跑 —— 这时必须把提示收起来，
        // 否则屏幕上会留一张"没人认领"的浮层（它是独立顶层窗口，不随条隐藏）。
        if (_handedOff || !_attached || _suppressed || !IsVisible || _hwnd == IntPtr.Zero)
        {
            HideTooltip();
            return;
        }

        Win32Window.POINT cursor;
        if (Taskbar.HoverOverride is { } forced) cursor = forced;
        else if (!Win32Window.TryGetCursor(out cursor)) { HideTooltip(); return; }

        if (!Monitors.TryGetRect(_hwnd, out Win32Window.RECT bar))
        {
            HideTooltip();
            return;
        }

        bool inside = cursor.X >= bar.Left && cursor.X < bar.Right &&
                      cursor.Y >= bar.Top && cursor.Y < bar.Bottom;

        if (!inside)
        {
            HideTooltip();
            return;
        }

        ShowTooltip(cursor, bar);
    }

    private void ShowTooltip(Win32Window.POINT cursor, Win32Window.RECT bar)
    {
        _tooltip ??= new FieldTooltip();

        _tooltip.SetTone(_autoIsLight);
        _tooltip.SetText(BuildTooltipText());

        if (!_tooltip.IsVisible)
        {
            _tooltip.Show();
            Trace($"tooltip: show cursor=({cursor.X},{cursor.Y})");
        }

        // Show 之后才有布局尺寸（SizeToContent 在 Show 前量不出来）
        _tooltip.UpdateLayout();

        IntPtr hwnd = new WindowInteropHelper(_tooltip).Handle;
        if (hwnd == IntPtr.Zero) return;

        double scale = Monitors.ScaleForWindow(hwnd);
        if (scale <= 0) scale = 1;

        int w = (int)Math.Ceiling(_tooltip.ActualWidth * scale);
        int h = (int)Math.Ceiling(_tooltip.ActualHeight * scale);
        if (w <= 0 || h <= 0) return;

        if (!Monitors.TryPrimaryMonitorBounds(out Win32Window.RECT screen)) return;

        // 水平跟着光标居中，但不许把自己顶出屏幕
        int x = cursor.X - w / 2;
        x = Math.Clamp(x, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - w - 8));

        // 默认贴在条上方（底部任务栏）；任务栏在顶部时改放下方
        int y = bar.Top - h - 10;
        if (y < screen.Top + 8) y = bar.Bottom + 10;

        Monitors.SetBounds(hwnd, x, y, w, h, topmost: true);
    }

    private void HideTooltip()
    {
        if (_tooltip is not { IsVisible: true }) return;

        _tooltip.Hide();
        Trace("tooltip: hide");
    }

    /// <summary>
    /// 提示文本。数据直接取条上那份 <see cref="FloatField"/> 的字符串 ——
    /// 不重新格式化，否则会出现"条上写着 65%、提示里说 66%"这种对不上的情况。
    /// </summary>
    private string BuildTooltipText()
    {
        var sb = new StringBuilder();

        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (!TrayFields.IsVisible(_config, cell.Key)) continue;

            if (sb.Length > 0) sb.AppendLine();

            string top = cell.Field.Top ?? string.Empty;
            string bottom = cell.Field.Bottom ?? string.Empty;

            // Bottom 有两种含义：次值（GPU 的温度、内存的已用/总量、上下行速率），
            // 或者只是把字段名再写一遍（FPS / PWR / LAT）。后者跳过，
            // 否则会打出 "FPS：—   FPS"。
            string value = string.IsNullOrWhiteSpace(bottom) || bottom == cell.Field.Key
                ? top
                : top + "   " + bottom;

            sb.Append(TrayFields.Labels[i]).Append("：").Append(value);

            // 7.6 特别点名"硬盘温度对应哪块盘"：条上只有两行数字，
            // 盘型号/序号只能在这里补
            if (cell.Key == "disk") AppendDiskLines(sb);
        }

        return sb.ToString();
    }

    private void AppendDiskLines(StringBuilder sb)
    {
        foreach (var disk in _state.Disks)
        {
            string model = disk.Model ?? string.Empty;
            if (model.Length > 34) model = model[..33] + "…";

            sb.AppendLine();
            sb.Append("　　盘").Append(disk.Index + 1);
            if (model.Length > 0) sb.Append(' ').Append(model);

            // 没管理员权限时 NVMe 温度读不到（R8），这时只是多一个没意义的 "—"，
            // 不显示比显示更能让人明白"这项没数据"
            string temp = disk.Temp ?? string.Empty;
            if (temp.Length > 0 && temp != Fmt.Dash) sb.Append("  ").Append(temp);
        }
    }

    private void Poll()
    {
        if (_handedOff || _recovering) return;

        // 还没挂上（启动时任务栏没就绪）——交给 Attach 里的重试链，这里不插手
        if (!_attached) return;

        // 自己的窗口没了。A 方案下窗口是独立顶层窗口，explorer 重启**不会**再
        // 连带销毁它（这正是选 A 的原因之一），所以这条只给"桌面重建 / 会话切换"兜底。
        if (!Taskbar.IsAlive(_hwnd))
        {
            Recover(rebuild: true);
            return;
        }

        // 任务栏换了句柄（explorer 重启）。窗口不用重建，换个锚点重新定位就行。
        IntPtr bar = Taskbar.Find();
        if (bar != _bar)
        {
            Trace($"watchdog: 任务栏换了（0x{_bar:X} → 0x{bar:X}）");
            _bar = bar;
            _lastSig = string.Empty;    // 强制重算几何
            if (_bar == IntPtr.Zero) { ScheduleReattach(0); return; }
        }

        // 广播在提权运行时收不到（见 InstallTaskbarWatcher 的说明），
        // 所以"任务栏被挪到别的边 / 换了高度 / 换了主题壁纸"只能靠巡检兜底。
        // Reposition 内部有几何签名短路，没变时只是几次比较，不会真的去 SetWindowPos。
        Reposition();

        if (_ink == InkMode.Auto)
        {
            double luma = Taskbar.SampleLuma(_bar);

            // 加 10 的死区：任务栏是带模糊的半透明层，壁纸动画/视频会让采样值每秒小幅抖动。
            // 卡着 128 翻来翻去的话字色会一秒一变，比不跟还糟。
            if (luma >= 0 && (luma > 128) != _autoIsLight && Math.Abs(luma - 128) > 10)
                ApplyInk();
        }
    }

    private void Recover(bool rebuild)
    {
        _recovering = true;
        try
        {
            if (rebuild)
            {
                Trace($"watchdog: 条 hwnd=0x{_hwnd:X} 已被销毁 → 重建");
                _handedOff = true;
                _watchdog?.Stop();
                RecreateRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // A 方案下窗口不随 explorer 重启消失，换个锚点重新定位即可，不需要 Detach
            Trace($"watchdog: 任务栏换了（旧 0x{_bar:X}）→ 重新定位");
            _bar = Taskbar.Find();
            _lastSig = string.Empty;
            Attach();
            ApplyInk();
        }
        finally
        {
            _recovering = false;
        }
    }

    /// <summary>
    /// explorer 重启后重挂。必须重试 —— 任务栏窗口不是 explorer 一起来就存在的，
    /// 刚广播完那一下 <c>FindWindow("Shell_TrayWnd")</c> 实测常常还是空。
    /// </summary>
    private void ScheduleReattach(int attempt)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) =>
        {
            t.Stop();

            IntPtr found = Taskbar.Find();
            Trace($"reattach#{attempt}: bar=0x{found:X}");
            if (found == IntPtr.Zero)
            {
                // 20 次 × 700ms ≈ 14 秒。explorer 重启到任务栏真正就绪实测要 2–5 秒，
                // 早先只留 8 次（5.6 秒）在慢机器上会直接放弃。
                if (attempt < 20) ScheduleReattach(attempt + 1);
                return;
            }

            // A 方案下窗口是独立顶层窗口，explorer 重启不会把它带走，
            // 所以这里只需要重新定位，不再需要 Detach / 重建。
            // 判活仍用缓存的 _hwnd：窗口真的没了（会话切换）时 Handle 会返回 0。
            if (!Taskbar.IsAlive(_hwnd))
            {
                Trace($"reattach#{attempt}: alive=False hwnd=0x{_hwnd:X} → 触发重建");
                _handedOff = true;
                _watchdog?.Stop();
                RecreateRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Trace($"reattach#{attempt}: alive=True hwnd=0x{_hwnd:X}");

            _bar = found;
            _lastSig = string.Empty;
            _attached = false;
            Attach();
            ApplyInk();
        };
        t.Start();
    }

    /// <summary>
    /// A 方案下这里不需要做任何特殊处理。
    ///
    /// <para>
    /// B 方案（`SetParent` 到任务栏当子窗口）时代这里有一大段收尾代码，
    /// 因为跨进程子窗口消失后 DWM 不会重绘任务栏那一块，退出必留一张冻结的画面。
    /// 试过 <c>Hide</c>、<c>Hide+Detach</c>、<c>DwmFlush</c>、移出父客户区、
    /// 销毁前 <c>Opacity=0</c>、关掉透明表面六种手段，全部无效。
    /// 换成顶层窗口后这个问题从根上不存在了 —— 顶层窗口有完整的 DWM 表面，
    /// 销毁时 DWM 正常合成，任务栏那一块会被正常重绘。
    /// </para>
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _attached = false;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Trace("窗口关闭");

        _watchdog?.Stop();
        _watchdog = null;

        // 提示框是独立顶层窗口，不随条关闭自动消失 —— 不显式关掉会留在屏幕上
        _hoverWatch?.Stop();
        _hoverWatch = null;
        _tooltip?.Close();
        _tooltip = null;

        // 监听窗口是独立的顶层窗口，不随本窗口关闭自动销毁，必须显式释放，
        // 否则重建实例时会多留一个空窗口在后台
        _watcher?.Dispose();
        _watcher = null;
        base.OnClosed(e);
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Shell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 菜单由 XAML 的 ContextMenu 自动弹出，这里只需要把事件标记为已处理，
        // 否则点击会穿透到任务栏，触发任务栏自己的右键菜单（与 7.6 的"不抢占"冲突）
        e.Handled = true;
    }

    private void ShowMain_Click(object sender, RoutedEventArgs e)
        => MainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void ToFloat_Click(object sender, RoutedEventArgs e)
        => FloatRequested?.Invoke(this, EventArgs.Empty);

    private void InkAuto_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Auto);
    private void InkLight_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Light);
    private void InkDark_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Dark);

    /// <summary>
    /// 改任务栏条的文字取色（G-08 设置页）。<see cref="SetInk"/> 原本只给右键菜单用，
    /// 设置页要和它走同一条路径 —— 否则"只改一个，另一处还停在旧值"。
    /// </summary>
    public void SetInkMode(InkMode mode) => SetInk(mode);

    private void SetInk(InkMode mode)
    {
        _ink = mode;
        ApplyInk();

        if (!PersistToConfig) return;
        _config.TaskbarInk = mode.ToString();
        ConfigStore.SaveCurrent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
