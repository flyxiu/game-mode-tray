using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GameMode.Core;

namespace GameMode.App;

/// <summary>
/// 主窗口。三区布局：
///   左上 托盘区（系统托盘里的应用，按内存从大到小）
///   右上 关闭名单（永久记录）
///   下   安全区（永久记录，横贯两区）
///
/// 全部自绘。子控件做不出磨砂玻璃上的半透明效果，
/// 拖拽时也要自己画跟随缩略图和落点高亮，用控件反而不自由。
///
/// 布局规则：左右边距统一从 Palette.Margin 派生，分区和按钮共用同一套计算，
/// 保证左右对称、上下对齐。尺寸写 96 DPI 逻辑像素，绘制前乘缩放比。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ConfigStore _config;
    private readonly ProcessScanner _scanner = new();
    private readonly ActionRunner _runner;
    private readonly IconCache _icons = new();

    private List<AppEntry> _apps = new();
    private CancellationTokenSource? _scanCancel;
    private bool _busy;
    private string _busyText = "";

    private AppEntry? _hoverApp;

    // 拖拽不走 OLE DoDragDrop：它会接管消息循环，全自绘窗口拖动时不再重绘。
    private AppEntry? _dragApp;
    private Point _dragPoint;
    private Point _dragStart;
    private bool _dragging;
    private AppList? _dropTarget;

    private int _scrollTray;
    private int _scrollClose;
    private int _scrollSafe;

    private long _totalMB = 16384;
    private long _availMB;

    // ---------- 布局常量（96 DPI 逻辑像素） ----------
    // 竖向节奏：标题 16 / 副标题 38 / 内存数字 62 / 进度条 104 / 按钮 120
    //          分区 170 / 安全区 396
    internal const int BaseW = 500;
    internal const int BaseH = 506;

    private const int TitleY = 16;
    private const int SubtitleY = 40;
    private const int MemValueY = 64;
    private const int BarY = 104;
    private const int ActionsY = 120;
    private const int ActionsH = 36;
    private const int BinsY = 170;
    private const int BinsH = 210;
    private const int SafeY = 396;
    private const int SafeH = 92;

    private const int GridCols = 4;
    private const int VisibleRows = 3;

    private float UiScale => DeviceDpi / 96f;

    private int S(int value) => (int)Math.Round(value * UiScale);

    private Rectangle Scaled(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    /// <summary>字号：写逻辑像素，乘缩放比后取整，避免半像素字号发糊。</summary>
    private Font UiFont(int logicalPx, FontStyle style = FontStyle.Regular)
        => Palette.F(Math.Max(9, S(logicalPx)), style);

    /// <summary>两栏宽度。左右分区和两个按钮共用，保证对齐。</summary>
    private static int ColumnWidth => (BaseW - Palette.Margin * 2 - Palette.ColGap) / 2;

    private static int RightColumnX => Palette.Margin + ColumnWidth + Palette.ColGap;

    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _memTimer = new();

    /// <summary>3 秒定时器的计数，用来控制完整重扫频率。</summary>
    private int _refreshTicks;

    // 动效：所有状态变化都插值过渡，不用瞬变。
    // 只有存在未完成的动画时才跑定时器，静止时零开销。
    private readonly System.Windows.Forms.Timer _animTimer = new();
    private float _hoverFade;
    private float _tipFade;
    private float _menuFade;

    // 两个大按钮的悬停与按下进度（1=开始游戏，2=结束游戏）
    private int _hoverButton;      // 0=没有
    private int _pressedButton;
    private readonly Dictionary<int, float> _buttonHover = new();
    private readonly Dictionary<int, float> _buttonPress = new();

    private readonly List<(Rectangle Rect, AppEntry App, AppList Kind)> _hitAreas = new();

    // 悬停气泡：显示应用名、内存、归属
    private string _tipTitle = "";
    private string _tipSub = "";
    private Point _tipAt;

    // 自绘右键菜单：不用 ContextMenuStrip，因为它跟随系统主题，和玻璃面板不搭
    private readonly List<(Rectangle Rect, string Text, Action Action, bool Enabled, bool Danger)> _menuItems = new();
    private bool _menuOpen;
    private Point _menuAt;
    private Rectangle _menuBox;
    private int _menuHover = -1;

    /// <summary>把尺寸与鼠标事件写进 size-trace.txt。capture 模式自动开，也可用环境变量开。</summary>
    public static bool TraceSize { get; set; } =
        Environment.GetEnvironmentVariable("GAMEMODE_DEBUG") == "1";

    public MainForm()
    {
        string dir = AppContext.BaseDirectory;
        _config = new ConfigStore(dir);
        _runner = new ActionRunner(dir);

        // 窗口图标。不设的话任务栏和 Alt+Tab 显示的是 .NET 默认图标，
        // 只有系统托盘那一处用了 LoadAppIcon 的结果。
        Icon = LoadAppIcon();

        // 允许从文件资源管理器拖 exe 进来登记到托盘区
        AllowDrop = true;
        DragEnter += OnFileDragEnter;
        DragDrop += OnFileDrop;

        AutoScaleMode = AutoScaleMode.None;
        BackColor = Palette.WindowBg;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = "游戏模式";
        ShowInTaskbar = true;
        KeyPreview = true;

        ClientSize = new Size(S(BaseW), S(BaseH));

        _config.Load();
        BuildTray();

        // 只在窗口可见时刷新内存，隐藏时不空转
        _animTimer.Interval = 16;   // 约 60fps
        _animTimer.Tick += (_, _) => StepAnimation();

        _memTimer.Interval = 1000;
        _memTimer.Tick += (_, _) =>
        {
            if (!Visible) return;

            // 手动退出的应用要从托盘区消失
            DropDeadApps();

            UpdateMemory();
            Invalidate();

            // 每 8 秒完整重扫一次，发现新启动的应用。
            // 托盘枚举有增量缓存，第二轮起只探测新出现的进程，不再每次 2.5 秒。
            if (++_refreshTicks % 8 == 0) _ = RescanAsync();
        };
    }

    private static void Dbg(string message)
    {
        if (!TraceSize) return;
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "size-trace.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ClientSize = new Size(S(BaseW), S(BaseH));
        Native.RoundCorners(Handle);
        Native.ExtendFrame(Handle);
        Native.EnableAcrylic(Handle, 0xD8, Color.FromArgb(20, 22, 28));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size(S(BaseW), S(BaseH));
        Invalidate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DPI 切换会按系统建议矩形重设窗口，无边框窗口会被塞进非客户区，
        // 这里和 OnShown 各钉一次尺寸。
        ClientSize = new Size(S(BaseW), S(BaseH));
        Dbg($"[Load] ClientSize={ClientSize} Dpi={DeviceDpi}");
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClientSize = new Size(S(BaseW), S(BaseH));
        Dbg($"[Shown] Dpi={DeviceDpi} ClientSize={ClientSize}");
        _memTimer.Start();
        await RescanAsync();
        _scanReady = true;
    }

    /// <summary>首次扫描完成前，其他入口不再重复触发，否则会互相取消。</summary>
    private bool _scanReady;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        // 窗口第一次显示时 OnShown 会扫一次；这里再扫会把那次取消掉，
        // 表现是托盘区空一片。所以等首扫完成后再允许从这里触发。
        if (Visible && _scanReady) _ = RescanAsync();
    }

    // ---------- 数据 ----------

    /// <summary>
    /// 移除所有进程都已消失的应用。
    ///
    /// 扫描只在启动和窗口激活时跑，用户手动关掉某个应用后，
    /// 它还会留在托盘区——这就是要修的问题。
    /// 这里每个定时器周期做一次轻量核对：只看进程还在不在，
    /// 不重新做托盘探测（那个要 2.5 秒，太贵）。
    /// </summary>
    /// <summary>推进一步动画。没有动画在进行时把定时器停掉，省 CPU。</summary>
    private void StepAnimation()
    {
        bool busy = false;

        busy |= Advance(ref _hoverFade, _hoverApp is not null ? 1f : 0f, 0.16f);
        busy |= Advance(ref _tipFade, ShowTipTarget(), 0.20f);
        busy |= Advance(ref _menuFade, _menuOpen ? 1f : 0f, 0.22f);

        foreach (int id in new[] { 1, 2 })
        {
            float h = _buttonHover.GetValueOrDefault(id);
            float p = _buttonPress.GetValueOrDefault(id);

            if (Advance(ref h, _hoverButton == id ? 1f : 0f, 0.18f)) busy = true;
            if (Advance(ref p, _pressedButton == id ? 1f : 0f, 0.35f)) busy = true;

            _buttonHover[id] = h;
            _buttonPress[id] = p;
        }

        Invalidate();
        if (!busy) _animTimer.Stop();
    }

    /// <summary>把 current 朝 target 推进一步。返回是否还在路上。</summary>
    private static bool Advance(ref float current, float target, float step)
    {
        if (Math.Abs(current - target) < 0.01f)
        {
            current = target;
            return false;
        }

        current += current < target ? step : -step;
        current = Math.Clamp(current, 0f, 1f);
        return true;
    }

    private float ButtonHover(int id) => _buttonHover.GetValueOrDefault(id);
    private float ButtonPress(int id) => _buttonPress.GetValueOrDefault(id);

    /// <summary>鼠标在哪个按钮上（0=不在）。</summary>
    private int HitButton(Point p)
    {
        if (Scaled(Palette.Margin, ActionsY, ColumnWidth, ActionsH).Contains(p)) return 1;
        if (Scaled(RightColumnX, ActionsY, ColumnWidth, ActionsH).Contains(p)) return 2;
        return 0;
    }

    /// <summary>气泡该不该显示（0 或 1）。</summary>
    private float ShowTipTarget()
    {
        bool wanted = !_menuOpen && !_dragging && !string.IsNullOrEmpty(_tipTitle);
        return wanted ? 1f : 0f;
    }

    /// <summary>启动动画定时器。状态变化的地方调它。</summary>
    private void KickAnimation()
    {
        if (!_animTimer.Enabled) _animTimer.Start();
    }

    /// <summary>
    /// capture 模式用：手动驱动一次存活检查。
    /// capture 走的是 DoEvents 循环，WM_TIMER 不被派发，定时器不会 Tick，
    /// 所以截图验证时要显式调这个。
    /// </summary>
    public void PumpLiveState()
    {
        DropDeadApps();
        UpdateMemory();
        Invalidate();
    }

    private void DropDeadApps()
    {
        if (_apps.Count == 0) return;

        var alive = new HashSet<int>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            alive.Add(process.Id);
            process.Dispose();
        }

        int removed = _apps.RemoveAll(app => !app.ProcessIds.Any(alive.Contains));
        if (removed == 0) return;

        // 悬停指针可能指着刚移除的应用
        if (_hoverApp is not null && !_apps.Contains(_hoverApp))
        {
            _hoverApp = null;
            _tipTitle = "";
            _tipSub = "";
        }

        // 命中区是上一帧算的，清掉等下次重算，避免点到已移除的图标
        _hitAreas.Clear();
    }

    private async Task RescanAsync()
    {
        _scanCancel?.Cancel();
        _scanCancel?.Dispose();
        _scanCancel = new CancellationTokenSource();
        var token = _scanCancel.Token;

        try
        {
            var protect = _config.Current.Protect;
            var tracked = _config.Current.Tracked;
            var scanned = await _scanner.ScanAsync(protect, tracked, token);
            if (token.IsCancellationRequested) return;

            foreach (var app in scanned)
                app.List = _config.GetList(app.Id);

            _apps = scanned;
            _scrollTray = 0;
            UpdateMemory();
            Invalidate();
        }
        catch (OperationCanceledException)
        {
            // 正常：被下一次扫描顶掉了
        }
        catch (Exception ex)
        {
            _busyText = "扫描失败：" + ex.Message;
            Invalidate();
        }
    }

    private void UpdateMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX();
            status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(status);
            if (GlobalMemoryStatusEx(ref status))
            {
                _totalMB = (long)(status.ullTotalPhys / 1024 / 1024);
                _availMB = (long)(status.ullAvailPhys / 1024 / 1024);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailableExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---------- 三区列表 ----------

    private List<AppEntry> TrayApps => _apps;

    private List<AppEntry> CloseApps => WithGhosts(AppList.Close, _config.Current.Close);
    private List<AppEntry> SafeApps => WithGhosts(AppList.Safe, _config.Current.Safe);

    /// <summary>
    /// 名单区 = 在跑的（带图标和内存）+ 名单里有但没跑的（灰显占位）。
    /// 用 id 去重，避免同一个应用出现两次。
    /// </summary>
    private List<AppEntry> WithGhosts(AppList kind, List<AppRef> refs)
    {
        var running = _apps.Where(a => a.List == kind).ToList();
        var seen = running.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ghosts = refs.Where(r => !seen.Contains(r.Id))
                         .Select(r => MakeGhost(r, kind))
                         .ToList();
        return running.Concat(ghosts).ToList();
    }

    /// <summary>
    /// 名单里有、但当前没在运行的应用。
    /// 名字优先用记录时存下来的——这时进程已经没了，没法从 exe 读版本信息。
    /// 老配置没存名字，只能从路径猜，猜不出就显示路径末段。
    /// </summary>
    private static AppEntry MakeGhost(AppRef reference, AppList kind) => new()
    {
        Id = reference.Id,
        Name = string.IsNullOrWhiteSpace(reference.Name) ? GuessNameFromPath(reference.Id) : reference.Name!,

        // 必须是可执行文件，不能是目录——恢复时拿目录去 Process.Start 只会弹出资源管理器。
        // 老记录没有 Exe 字段，就从目录里找现存的 exe。
        ExecutablePath = !string.IsNullOrWhiteSpace(reference.Exe)
            ? reference.Exe!
            : FindExecutable(reference.Id),

        List = kind,          // 必须设：否则气泡会把归属显示成"不参与"，右键菜单也会判断错
        IsRunning = false,
        CanRestore = true,
    };

    /// <summary>从应用目录里找主 exe。找不到就返回目录本身。</summary>
    private static string FindExecutable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return dir;

            var candidates = Directory.EnumerateFiles(dir, "*.exe").ToList();
            if (candidates.Count == 0) return dir;

            // 优先取和应用目录同名的那个，否则取体积最大的——通常是主程序
            string dirName = Path.GetFileName(dir.TrimEnd('\\'));
            var match = candidates.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(dirName, StringComparison.OrdinalIgnoreCase));

            return match ?? candidates.OrderByDescending(f => new FileInfo(f).Length).First();
        }
        catch
        {
            return dir;
        }
    }

    /// <summary>没有记录名字时，从路径猜一个。优先取目录下现存的 exe。</summary>
    private static string GuessNameFromPath(string id)
    {
        try
        {
            if (Directory.Exists(id))
            {
                var exe = Directory.EnumerateFiles(id, "*.exe").FirstOrDefault();
                if (exe is not null)
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                        return info.FileDescription.Trim();
                    return Path.GetFileNameWithoutExtension(exe);
                }
            }

            // 目录也没了：取路径末段。若是 app/bin/lib 这类无意义的名字，
            // 往上一层找，像 ...\Claude_2.31\app 就退回成 Claude_2.31
            string name = Path.GetFileName(id.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) return id;

            if (GenericDirNames.Contains(name))
            {
                string? parent = Path.GetFileName(Path.GetDirectoryName(id.TrimEnd('\\')));
                if (!string.IsNullOrEmpty(parent)) name = parent;
            }

            return name;
        }
        catch
        {
            return id;
        }
    }

    /// <summary>把一个名字缩成能塞进图标格子的两三个字符。</summary>
    private static string Abbreviate(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length <= 2) return trimmed;

        // 中文取前两个字
        if (trimmed[0] > 0x2E80) return trimmed[..2];

        // 英文取前两个字母并大写
        return trimmed[..2].ToUpperInvariant();
    }

    private static readonly HashSet<string> GenericDirNames =
        new(new[] { "app", "apps", "bin", "lib", "release", "debug", "x64", "x86" },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>格式化内存占用：超过 1 GB 用 GB，否则用 MB。</summary>
    private static string FormatMB(long mb)
        => mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb} MB";

    // ---------- 托盘 ----------

    private void BuildTray()
    {
        _tray.Icon = LoadAppIcon();
        _tray.Text = "游戏模式";
        _tray.Visible = true;

        // 系统托盘的右键菜单只能用 WinForms 原生控件（托盘菜单无法自绘），
        // 这里只做深色配色，尽量和面板一致
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(30, 32, 40),
            ForeColor = Palette.Text,
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = UiFont(11),
        };
        menu.Items.Add("开始游戏", null, async (_, _) => await CloseAllAsync());
        menu.Items.Add("结束游戏", null, async (_, _) => await RestoreAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开窗口", null, (_, _) => ShowWindow());
        menu.Items.Add("退出", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return SystemIcons.Application;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _ = RescanAsync();
    }

    // ---------- 动作 ----------

    private async Task CloseAllAsync()
    {
        if (_busy) return;

        _busy = true;
        _busyText = "正在关闭…";
        Invalidate();

        try
        {
            var progress = new Progress<string>(name => { _busyText = "关闭 " + name; Invalidate(); });
            var (state, _) = await _runner.CloseAsync(_apps, _config.Current.StopCommands, progress);

            await RescanAsync();
            Toast($"已关闭 {state.ClosedIds.Count} 项，释放 {state.FreedMB} MB");
        }
        catch (Exception ex)
        {
            Toast("关闭失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _busyText = "";
            Invalidate();
        }
    }

    private async Task RestoreAllAsync()
    {
        if (_busy) return;

        _busy = true;
        _busyText = "正在恢复…";
        Invalidate();

        try
        {
            var progress = new Progress<string>(name => { _busyText = "恢复 " + name; Invalidate(); });

            // 必须把名单里记录过、但当前没在跑的应用也带上。
            // _apps 只装正在运行的应用，被关掉的那个不在里面，
            // 直接传 _apps 的话恢复永远找不到它——这就是"关得掉、开不回来"。
            var targets = WithRecorded();
            var (started, manual, _) = await _runner.RestoreAsync(targets, progress);

            await RescanAsync();

            string message = $"已恢复 {started.Count} 项";
            if (manual.Count > 0) message += $"，{manual.Count} 项需手动打开";
            Toast(message);
        }
        catch (Exception ex)
        {
            Toast("恢复失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _busyText = "";
            Invalidate();
        }
    }

    /// <summary>当前在跑的应用 + 名单里记录过但没在跑的应用。</summary>
    private List<AppEntry> WithRecorded()
    {
        var all = new List<AppEntry>(_apps);
        var seen = _apps.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in _config.Current.Close.Concat(_config.Current.Safe))
        {
            if (seen.Contains(reference.Id)) continue;

            string exe = !string.IsNullOrWhiteSpace(reference.Exe)
                ? reference.Exe!
                : FindExecutable(reference.Id);

            all.Add(new AppEntry
            {
                Id = reference.Id,
                Name = string.IsNullOrWhiteSpace(reference.Name)
                    ? GuessNameFromPath(reference.Id) : reference.Name!,
                ExecutablePath = exe,
                List = _config.GetList(reference.Id),
                IsRunning = false,
                CanRestore = true,
            });
        }

        return all;
    }

    /// <summary>
    /// 系统托盘气泡。Win11 会把它转成系统通知，标题栏已经显示应用名，
    /// 这里不再重复写"游戏模式"，正文只留动作结果。
    /// </summary>
    private void Toast(string text)
    {
        _tray.BalloonTipTitle = "";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(2500);
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 磨砂背景上半透明，ClearType（子像素）算不准底色，小字会糊。
        // 灰度抗锯齿不依赖背景色，在玻璃上更清楚。
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 磨砂玻璃要求背景透明，不铺底色
        DrawHeader(g);
        DrawActions(g);
        DrawBin(g, Scaled(Palette.Margin, BinsY, ColumnWidth, BinsH),
            "托盘里在跑的", TrayApps, AppList.None, _scrollTray);
        DrawBin(g, Scaled(RightColumnX, BinsY, ColumnWidth, BinsH),
            "游戏时关闭", CloseApps, AppList.Close, _scrollClose);
        DrawSafe(g);
        DrawDragGhost(g);
        DrawHoverTip(g);
        DrawMenu(g);
    }

    /// <summary>
    /// 悬停气泡。画在鼠标上方，贴边时翻到下方。
    /// 不用 ToolTip 控件：它是系统主题，和玻璃面板不搭。
    /// </summary>
    private void DrawHoverTip(Graphics g)
    {
        if (_tipFade <= 0.01f) return;           // 完全透明时不画

        var titleFont = UiFont(11, FontStyle.Bold);
        var subFont = UiFont(10);

        SizeF titleSize = g.MeasureString(_tipTitle, titleFont);
        SizeF subSize = g.MeasureString(_tipSub, subFont);

        int padX = S(9), padY = S(7);
        int width = (int)Math.Max(titleSize.Width, subSize.Width) + padX * 2;
        int height = (int)(titleSize.Height + subSize.Height) + padY * 2;

        // 默认在指针右下方，超出窗口就翻到左侧/上方
        int x = _tipAt.X + S(14);
        int y = _tipAt.Y + S(18);
        if (x + width > ClientSize.Width - S(4)) x = _tipAt.X - width - S(14);
        if (y + height > ClientSize.Height - S(4)) y = _tipAt.Y - height - S(12);

        var box = new Rectangle(x, y, width, height);

        byte boxAlpha = (byte)(238 * _tipFade);
        byte borderAlpha = (byte)(46 * _tipFade);

        using (var path = Palette.RoundRect(box, S(8)))
        {
            g.FillPath(Palette.B(Color.FromArgb(boxAlpha, 26, 28, 34)), path);
            g.DrawPath(Palette.P(Color.FromArgb(borderAlpha, 255, 255, 255), 1f * UiScale), path);
        }

        g.DrawString(_tipTitle, titleFont,
            Palette.B(Color.FromArgb((byte)(255 * _tipFade), Palette.Text)),
            box.X + padX, box.Y + padY);
        g.DrawString(_tipSub, subFont,
            Palette.B(Color.FromArgb((byte)(255 * _tipFade), Palette.TextDim)),
            box.X + padX, box.Y + padY + titleSize.Height - S(2));
    }

    /// <summary>
    /// 自绘右键菜单。不用 ContextMenuStrip：它跟随系统主题，
    /// 在磨砂面板上会出现浅色块，和整体风格不一致。
    /// </summary>
    private void DrawMenu(Graphics g)
    {
        if (_menuFade <= 0.01f) return;

        int padX = S(10), padY = S(5), rowH = S(28);
        var font = UiFont(11);

        int width = 0;
        foreach (var item in _menuItems)
        {
            SizeF size = g.MeasureString(item.Text, font);
            width = Math.Max(width, (int)size.Width);
        }
        width += padX * 2 + S(26);

        int height = padY * 2 + _menuItems.Count * rowH;
        var box = new Rectangle(_menuAt.X, _menuAt.Y, width, height);

        // 贴边时收进窗口内
        if (box.Right > ClientSize.Width - S(6)) box.X = ClientSize.Width - S(6) - box.Width;
        if (box.Bottom > ClientSize.Height - S(6)) box.Y = ClientSize.Height - S(6) - box.Height;
        if (box.X < S(6)) box.X = S(6);
        if (box.Y < S(6)) box.Y = S(6);

        byte menuBg = (byte)(246 * _menuFade);
        byte menuBorder = (byte)(48 * _menuFade);

        using (var path = Palette.RoundRect(box, S(9)))
        {
            g.FillPath(Palette.B(Color.FromArgb(menuBg, 30, 32, 40)), path);
            g.DrawPath(Palette.P(Color.FromArgb(menuBorder, 255, 255, 255), 1f * UiScale), path);
        }

        // 回写实际位置，供命中测试用
        _menuBox = box;

        for (int i = 0; i < _menuItems.Count; i++)
        {
            var item = _menuItems[i];
            var row = new Rectangle(box.X + S(4), box.Y + padY + i * rowH, box.Width - S(8), rowH);

            bool hover = _menuHover == i && item.Enabled;
            if (hover)
            {
                using var path = Palette.RoundRect(row, S(6));
                g.FillPath(Palette.B(Color.FromArgb((byte)(28 * _menuFade), 255, 255, 255)), path);
            }

            var color = !item.Enabled
                ? Palette.TextFaint
                : item.Danger ? Color.FromArgb(240, 130, 130) : Palette.Text;

            SizeF size = g.MeasureString(item.Text, font);
            g.DrawString(item.Text, font,
                Palette.B(Color.FromArgb((byte)(255 * _menuFade), color)),
                row.X + padX, row.Y + (row.Height - size.Height) / 2);
        }
    }

    private void DrawHeader(Graphics g)
    {
        g.DrawString("游戏模式", UiFont(15, FontStyle.Bold), Palette.B(Palette.Text),
            S(Palette.Margin), S(TitleY));
        g.DrawString("打游戏前腾内存", UiFont(10), Palette.B(Palette.TextFaint),
            S(Palette.Margin), S(SubtitleY));

        // 关闭按钮，右边距与分区对齐
        var close = Scaled(BaseW - Palette.Margin - 25, TitleY + 2, 25, 25);
        using (var path = Palette.RoundRect(close, S(8)))
            g.FillPath(Palette.B(Color.FromArgb(42, 44, 51)), path);

        using (var pen = new Pen(Palette.TextDim, 1.5f * UiScale))
        {
            g.DrawLine(pen, close.X + S(8), close.Y + S(8), close.Right - S(8), close.Bottom - S(8));
            g.DrawLine(pen, close.Right - S(8), close.Y + S(8), close.X + S(8), close.Bottom - S(8));
        }

        // 内存数字，与进度条拉开 12px 以上
        string value = (_availMB / 1024.0).ToString("0.0");
        var valueFont = UiFont(24, FontStyle.Bold);
        g.DrawString(value, valueFont, Palette.B(Palette.Text), S(Palette.Margin), S(MemValueY));

        SizeF valueSize = g.MeasureString(value, valueFont);
        g.DrawString("GB 可用", UiFont(11), Palette.B(Palette.TextDim),
            S(Palette.Margin) + valueSize.Width - S(2), S(MemValueY + 12));

        int pct = _totalMB > 0 ? (int)(100 - _availMB * 100.0 / _totalMB) : 0;
        string rest = $"/ {_totalMB / 1024.0:0.0} GB · 已用 {pct}%";

        // 忙碌时把进度文字放在这一行，不额外占版面
        string right = _busy ? _busyText : rest;
        var rightFont = UiFont(10);
        SizeF rightSize = g.MeasureString(right, rightFont);
        g.DrawString(right, rightFont, Palette.B(_busy ? Palette.AccentA : Palette.TextFaint),
            ClientSize.Width - S(Palette.Margin) - rightSize.Width, S(MemValueY + 14));

        var track = Scaled(Palette.Margin, BarY, BaseW - Palette.Margin * 2, 5);
        using (var path = Palette.RoundRect(track, S(2)))
            g.FillPath(Palette.B(Palette.Track), path);

        int fillWidth = (int)(track.Width * Math.Clamp(pct / 100.0, 0, 1));
        if (fillWidth > 2)
        {
            var fill = new Rectangle(track.X, track.Y, fillWidth, track.Height);
            Color color = pct > 88 ? Palette.Danger : pct > 70 ? Palette.Warn : Palette.AccentA;
            using var path = Palette.RoundRect(fill, S(2));
            g.FillPath(Palette.B(color), path);
        }
    }

    private void DrawActions(Graphics g)
    {
        DrawButton(g, Scaled(Palette.Margin, ActionsY, ColumnWidth, ActionsH), "开始游戏", primary: true);
        DrawButton(g, Scaled(RightColumnX, ActionsY, ColumnWidth, ActionsH), "结束游戏", primary: false);
    }

    private void DrawButton(Graphics g, Rectangle r, string text, bool primary)
    {
        // 悬停与按下都有过渡，不是瞬间切换
        int id = primary ? 1 : 2;
        float hover = ButtonHover(id);
        float press = ButtonPress(id);

        // 按下时整体下沉 1px，视觉上"被按进去"
        var box = press > 0.01f
            ? new Rectangle(r.X, r.Y + (int)(S(1) * press), r.Width, r.Height)
            : r;

        using (var path = Palette.RoundRect(box, S(9)))
        {
            if (primary)
            {
                // 悬停时渐变提亮
                var a = Blend(Palette.AccentA, Color.White, 0.18f * hover);
                var b = Blend(Palette.AccentB, Color.White, 0.18f * hover);
                using var brush = new LinearGradientBrush(box, a, b, 0f);
                g.FillPath(brush, path);
            }
            else
            {
                // 从 46 提亮到 62
                int v = (int)(46 + 16 * hover);
                g.FillPath(Palette.B(Color.FromArgb(v, v + 2, v + 9)), path);
            }
        }

        var font = UiFont(12, FontStyle.Bold);
        var color = primary ? Color.FromArgb(32, 20, 30) : Palette.Text;
        SizeF size = g.MeasureString(text, font);
        g.DrawString(text, font, Palette.B(color),
            box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2);
    }

    /// <summary>把两个颜色按比例混合。</summary>
    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    /// <summary>画一个图标分区。bounds 已是物理像素。</summary>
    private void DrawBin(Graphics g, Rectangle bounds, string title,
        List<AppEntry> apps, AppList kind, int scroll)
    {
        bool active = _dropTarget == kind && _dragging;

        using (var path = Palette.RoundRect(bounds, S(Palette.RadiusBin)))
            g.FillPath(Palette.B(Color.FromArgb(60, Palette.BinBg)), path);

        // 每区有自己的标识色：关闭名单粉、托盘区白、安全区绿。
        // 拖拽经过时加粗并提亮，这是"能放进来"的唯一提示。
        var accent = kind switch
        {
            AppList.Close => Color.FromArgb(150, Palette.AccentA),
            AppList.Safe => Color.FromArgb(130, Palette.Running),
            _ => Color.FromArgb(34, 255, 255, 255),
        };

        var borderColor = active
            ? (kind == AppList.Close ? Palette.AccentA : Palette.Running)
            : accent;

        using (var path = Palette.RoundRect(bounds, S(Palette.RadiusBin)))
            g.DrawPath(Palette.P(borderColor, (active ? 1.8f : 1f) * UiScale), path);

        g.DrawString(title, UiFont(10), Palette.B(Palette.TextDim),
            bounds.X + S(11), bounds.Y + S(9));

        // 右侧：条数 + 合计内存。原型里有，之前漏了
        long totalMB = apps.Where(a => a.IsRunning).Sum(a => a.MemoryMB);
        string summary = apps.Count == 0 ? "" : $"{apps.Count} 条 · {FormatMB(totalMB)}";
        var summaryFont = UiFont(9);
        SizeF summarySize = g.MeasureString(summary, summaryFont);
        g.DrawString(summary, summaryFont, Palette.B(Palette.TextFaint),
            bounds.Right - S(11) - summarySize.Width, bounds.Y + S(9));

        int tileW = S(Palette.Tile);
        int gap = S(Palette.TileGap);
        int gridWidth = GridCols * tileW + (GridCols - 1) * gap;
        int startX = bounds.X + (bounds.Width - gridWidth) / 2;
        int startY = bounds.Y + S(30);

        int firstIndex = scroll * GridCols;
        int capacity = GridCols * VisibleRows;

        for (int slot = 0; slot < capacity; slot++)
        {
            int index = firstIndex + slot;
            if (index >= apps.Count) break;

            int col = slot % GridCols, row = slot / GridCols;
            var tile = new Rectangle(startX + col * (tileW + gap), startY + row * (tileW + gap), tileW, tileW);
            DrawTile(g, tile, apps[index], kind);
        }

        int totalRows = (apps.Count + GridCols - 1) / GridCols;
        if (totalRows > VisibleRows)
            DrawScrollHint(g, bounds, scroll, totalRows - VisibleRows);
    }

    private void DrawScrollHint(Graphics g, Rectangle bounds, int scroll, int maxScroll)
    {
        int barW = Math.Max(2, S(2));
        int trackX = bounds.Right - S(5);
        int trackY = bounds.Y + S(30);
        int trackH = bounds.Height - S(42);

        using (var track = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
            g.FillRectangle(track, trackX, trackY, barW, trackH);

        int totalRows = maxScroll + VisibleRows;
        int thumbH = Math.Max(S(16), trackH * VisibleRows / totalRows);
        int thumbY = trackY + (trackH - thumbH) * scroll / Math.Max(1, maxScroll);

        using var thumb = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
        g.FillRectangle(thumb, trackX, thumbY, barW, thumbH);
    }

    private void DrawTile(Graphics g, Rectangle tile, AppEntry app, AppList kind)
    {
        bool hover = ReferenceEquals(_hoverApp, app) && !_dragging;
        bool beingDragged = _dragging && ReferenceEquals(_dragApp, app);
        bool dim = !app.IsRunning && kind != AppList.None;

        using (var path = Palette.RoundRect(tile, S(Palette.RadiusTile)))
        {
            // 悬停底色随动画渐变，不是瞬变
            byte alpha = (byte)(12 + 22 * (hover ? _hoverFade : 0f));
            g.FillPath(Palette.B(Color.FromArgb(alpha, 255, 255, 255)), path);
        }

        // 名单描边。
        // 在名单区里：显示该区的颜色（粉=关闭，绿=安全）。
        // 在托盘区里：显示它当前归属的那个名单的颜色——这是"已被管理"的唯一提示。
        Color? ring = kind switch
        {
            AppList.Close => Palette.AccentA,
            AppList.Safe => Palette.Running,
            _ => app.List switch
            {
                AppList.Close => Palette.AccentA,
                AppList.Safe => Palette.Running,
                _ => null,
            },
        };

        if (ring is not null)
        {
            using var path = Palette.RoundRect(tile, S(Palette.RadiusTile));
            g.DrawPath(Palette.P(Color.FromArgb(150, ring.Value), 1.2f * UiScale), path);
        }

        int iconPad = S(7);
        int iconSize = S(32);

        // 源图是 256px 裁边后的，缩放质量由 IconCache 保证
        var bitmap = _icons.Get(app.ExecutablePath, iconSize);

        if (bitmap is not null)
        {
            var dest = new Rectangle(tile.X + iconPad, tile.Y + iconPad, iconSize, iconSize);

            if (beingDragged)
            {
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.3f });
                g.DrawImage(bitmap, dest, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
            }
            else if (dim)
            {
                // 名单里有但没在跑：灰度 + 降透明
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix(new[]
                {
                    new[] { 0.3f, 0.3f, 0.3f, 0f, 0f },
                    new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                    new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0.45f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f },
                }));
                g.DrawImage(bitmap, dest, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
            }
            else
            {
                g.DrawImage(bitmap, dest);
            }
        }
        else
        {
            // 真取不到图标才画名字缩写。
            // 取两个字符而不是一个：单个字母认不出是哪个应用（"c" 到底是谁），
            // 46px 的格子塞得下两个中文字或两三个字母。
            string initial = string.IsNullOrEmpty(app.Name) ? "?" : Abbreviate(app.Name);
            var font = UiFont(13, FontStyle.Bold);
            SizeF size = g.MeasureString(initial, font);
            var color = dim ? Color.FromArgb(70, Palette.Text) : Palette.Text;
            g.DrawString(initial, font, Palette.B(color),
                tile.X + (tile.Width - size.Width) / 2, tile.Y + (tile.Height - size.Height) / 2);
        }

        // 运行状态点
        if (app.IsRunning)
        {
            using var brush = new SolidBrush(Palette.Running);
            g.FillEllipse(brush, tile.Right - S(9), tile.Bottom - S(9), S(5), S(5));
        }
    }

    private void DrawSafe(Graphics g)
    {
        var bounds = Scaled(Palette.Margin, SafeY, BaseW - Palette.Margin * 2, SafeH);
        bool active = _dropTarget == AppList.Safe && _dragging;

        using (var path = Palette.RoundRect(bounds, S(Palette.RadiusBin)))
            g.FillPath(Palette.B(Color.FromArgb(12, Palette.SafeBg)), path);

        // 安全区常驻绿描边：alpha 41 几乎看不见，抬到 130 才和其它两区一样能分辨。
        var borderColor = active ? Palette.Running : Color.FromArgb(130, Palette.SafeBg);
        using (var path = Palette.RoundRect(bounds, S(Palette.RadiusBin)))
            g.DrawPath(Palette.P(borderColor, (active ? 1.5f : 1f) * UiScale), path);

        using (var brush = new SolidBrush(Palette.Running))
            g.FillEllipse(brush, bounds.X + S(11), bounds.Y + S(13), S(5), S(5));

        g.DrawString("安全区 · 永不关闭", UiFont(10),
            Palette.B(Color.FromArgb(217, Palette.Running)), bounds.X + S(21), bounds.Y + S(8));

        var apps = SafeApps;
        long totalMB = apps.Where(a => a.IsRunning).Sum(a => a.MemoryMB);
        string summary = apps.Count == 0 ? "" : $"{apps.Count} 条 · {FormatMB(totalMB)}";
        var summaryFont = UiFont(9);
        SizeF summarySize = g.MeasureString(summary, summaryFont);
        g.DrawString(summary, summaryFont, Palette.B(Palette.TextFaint),
            bounds.Right - S(11) - summarySize.Width, bounds.Y + S(9));

        int tileW = S(Palette.Tile);
        int gap = S(Palette.TileGap);
        int startX = bounds.X + S(10);
        int startY = bounds.Y + S(26);

        int capacity = SafeCapacity(bounds);
        int first = _scrollSafe * capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            int index = first + slot;
            if (index >= apps.Count) break;

            var tile = new Rectangle(startX + slot * (tileW + gap), startY, tileW, tileW);
            DrawTile(g, tile, apps[index], AppList.Safe);
        }
    }

    private int SafeCapacity(Rectangle safeBounds)
        => Math.Max(1, (safeBounds.Width - S(20)) / (S(Palette.Tile) + S(Palette.TileGap)));

    /// <summary>拖动时跟随鼠标的半透明缩略图。</summary>
    private void DrawDragGhost(Graphics g)
    {
        if (!_dragging || _dragApp is null) return;

        int size = S(Palette.Tile);
        var ghost = new Rectangle(_dragPoint.X - size / 2, _dragPoint.Y - size / 2, size, size);

        using (var path = Palette.RoundRect(ghost, S(Palette.RadiusTile)))
        {
            g.FillPath(Palette.B(Color.FromArgb(150, 40, 42, 50)), path);
            using var pen = new Pen(Color.FromArgb(200, Palette.AccentA), 1.5f * UiScale);
            g.DrawPath(pen, path);
        }

        int iconSize = S(32);
        var bitmap = _icons.Get(_dragApp.ExecutablePath, iconSize);
        if (bitmap is null) return;

        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f });
        var dest = new Rectangle(ghost.X + (size - iconSize) / 2, ghost.Y + (size - iconSize) / 2, iconSize, iconSize);
        g.DrawImage(bitmap, dest, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
    }

    /// <summary>生成悬停气泡的文案。没有可显示的内容时清空，气泡就不画。</summary>
    private void UpdateHoverTip(AppEntry? app, AppList? kind)
    {
        if (app is null)
        {
            _tipTitle = "";
            _tipSub = "";
            KickAnimation();
            return;
        }

        _tipTitle = app.Name;

        string memory = app.IsRunning ? FormatMB(app.MemoryMB) : "未运行";
        string state = app.List switch
        {
            AppList.Close => "游戏时关闭",
            AppList.Safe => "永不关闭",
            _ => "不参与",
        };

        // 右键菜单上浮出来的时候不叠气泡，避免两个面板打架
        _tipSub = $"{memory} · {state}";
        _ = kind;
        KickAnimation();
    }

    /// <summary>从资源管理器拖 exe 进来时给出可放手的光标。</summary>
    private void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// 松手时把 exe 登记到托盘区。也支持把快捷方式拖进来——这里按 .lnk 解析目标。
    /// </summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        foreach (string file in files)
        {
            string exe = file;
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                exe = ResolveShortcut(file);
            }

            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(exe)) continue;
            if (_config.AddTracked(exe)) added++;
        }

        if (added == 0) return;

        _ = RescanAsync();
        Toast($"已登记 {added} 个应用");
    }

    /// <summary>解析快捷方式指向的目标路径。取不到就返回原路径。</summary>
    private static string ResolveShortcut(string lnkPath)
    {
        try
        {
            // WScript.Shell 在 .NET 8 里要通过 COM 互操作调用
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return lnkPath;

            object shell = Activator.CreateInstance(shellType)!;
            object shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath })!;

            string target = (string)shortcut.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null)!;

            return string.IsNullOrEmpty(target) ? lnkPath : target;
        }
        catch
        {
            return lnkPath;
        }
    }

    // ---------- 命中测试 ----------

    private void RebuildHitAreas()
    {
        _hitAreas.Clear();
        CollectGrid(Palette.Margin, BinsY, ColumnWidth, BinsH, TrayApps, AppList.None, _scrollTray);
        CollectGrid(RightColumnX, BinsY, ColumnWidth, BinsH, CloseApps, AppList.Close, _scrollClose);

        var safeBounds = Scaled(Palette.Margin, SafeY, BaseW - Palette.Margin * 2, SafeH);
        var safeApps = SafeApps;
        int tileW = S(Palette.Tile);
        int gap = S(Palette.TileGap);
        int startX = safeBounds.X + S(10);
        int startY = safeBounds.Y + S(26);
        int capacity = SafeCapacity(safeBounds);
        int first = _scrollSafe * capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            int index = first + slot;
            if (index >= safeApps.Count) break;
            _hitAreas.Add((new Rectangle(startX + slot * (tileW + gap), startY, tileW, tileW),
                safeApps[index], AppList.Safe));
        }
    }

    private void CollectGrid(int x, int y, int w, int h, List<AppEntry> apps, AppList kind, int scroll)
    {
        var bounds = Scaled(x, y, w, h);
        int tileW = S(Palette.Tile);
        int gap = S(Palette.TileGap);
        int gridWidth = GridCols * tileW + (GridCols - 1) * gap;
        int startX = bounds.X + (bounds.Width - gridWidth) / 2;
        int startY = bounds.Y + S(30);
        int firstIndex = scroll * GridCols;
        int capacity = GridCols * VisibleRows;

        for (int slot = 0; slot < capacity; slot++)
        {
            int index = firstIndex + slot;
            if (index >= apps.Count) break;

            int col = slot % GridCols, row = slot / GridCols;
            _hitAreas.Add((new Rectangle(startX + col * (tileW + gap), startY + row * (tileW + gap),
                tileW, tileW), apps[index], kind));
        }
    }

    private (AppEntry App, AppList Kind)? HitTest(Point p)
    {
        foreach (var (rect, app, kind) in _hitAreas)
            if (rect.Contains(p)) return (app, kind);
        return null;
    }

    /// <summary>鼠标落在哪个名单区上，用于拖拽落点判断。</summary>
    private AppList? RegionAt(Point p)
    {
        if (Scaled(RightColumnX, BinsY, ColumnWidth, BinsH).Contains(p)) return AppList.Close;
        if (Scaled(Palette.Margin, SafeY, BaseW - Palette.Margin * 2, SafeH).Contains(p)) return AppList.Safe;

        // 拖回托盘区 = 从名单里移出。返回 None 表示"不参与"。
        if (Scaled(Palette.Margin, BinsY, ColumnWidth, BinsH).Contains(p)) return AppList.None;

        return null;
    }

    // ---------- 交互 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragApp is not null)
        {
            _dragPoint = e.Location;

            if (!_dragging && (Math.Abs(e.X - _dragStart.X) > S(8) || Math.Abs(e.Y - _dragStart.Y) > S(8)))
                _dragging = true;

            if (_dragging)
            {
                var target = RegionAt(e.Location);
                if (target != _dropTarget) _dropTarget = target;

                Cursor = Cursors.Hand;
                Invalidate();
                return;
            }
        }

        if (_menuOpen)
        {
            int padY = S(5), rowH = S(28);
            int index = _menuBox.Contains(e.Location)
                ? (e.Location.Y - _menuBox.Y - padY) / Math.Max(1, rowH)
                : -1;

            if (index < 0 || index >= _menuItems.Count) index = -1;
            if (index != _menuHover)
            {
                _menuHover = index;
                Invalidate();
            }
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            return;
        }

        // 按钮悬停
        int btn = HitButton(e.Location);
        if (btn != _hoverButton)
        {
            _hoverButton = btn;
            KickAnimation();
        }

        RebuildHitAreas();
        var hit = HitTest(e.Location);
        var app = hit?.App;

        if (!ReferenceEquals(app, _hoverApp))
        {
            _hoverApp = app;
            _hoverFade = app is null ? _hoverFade : 0f;   // 新进场的从 0 渐入
            Cursor = app is null ? Cursors.Default : Cursors.Hand;
            UpdateHoverTip(app, hit?.Kind);
            KickAnimation();
            Invalidate();
        }
        else if (app is not null)
        {
            // 指针在同一个图标上移动时，气泡跟着走
            int dx = Math.Abs(e.X - _tipAt.X);
            int dy = Math.Abs(e.Y - _tipAt.Y);
            if (dx > S(2) || dy > S(2))
            {
                _tipAt = e.Location;
                Invalidate();
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        _pressedButton = HitButton(e.Location);
        if (_pressedButton != 0) KickAnimation();

        // 菜单打开时，点击只作用于菜单
        if (_menuOpen)
        {
            if (e.Button == MouseButtons.Left) ClickMenu(e.Location);
            else CloseMenu();
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            RebuildHitAreas();
            var hit = HitTest(e.Location);
            if (hit is not null) ShowAppMenu(hit.Value.App, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        if (Scaled(BaseW - Palette.Margin - 25, TitleY + 2, 25, 25).Contains(e.Location))
        {
            Hide();
            return;
        }

        if (Scaled(Palette.Margin, ActionsY, ColumnWidth, ActionsH).Contains(e.Location))
        {
            _ = CloseAllAsync();
            return;
        }
        if (Scaled(RightColumnX, ActionsY, ColumnWidth, ActionsH).Contains(e.Location))
        {
            _ = RestoreAllAsync();
            return;
        }

        RebuildHitAreas();
        var tile = HitTest(e.Location);
        if (tile is not null)
        {
            if (tile.Value.App.IsProtected) return;

            _dragApp = tile.Value.App;
            _dragStart = e.Location;
            _dragPoint = e.Location;
            _dragging = false;

            // 捕获鼠标：拖出窗口范围后仍能收到移动和松开，不设的话拖到边缘就断线
            Capture = true;
            return;
        }

        if (e.Y < S(56)) Native.DragWindow(Handle);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left || _dragApp is null)
        {
            if (Capture) Capture = false;
            return;
        }

        var app = _dragApp;
        bool wasDragging = _dragging;
        var target = wasDragging ? RegionAt(e.Location) : null;

        _pressedButton = 0;
        _dragApp = null;
        _dragging = false;
        _dropTarget = null;
        if (Capture) Capture = false;

        if (wasDragging)
        {
            // 拖到某个名单区：切过去。互斥由 ConfigStore 保证
            if (target is not null && app.List != target)
                SetList(app.Id, target.Value, app.Name, app.ExecutablePath);
        }
        else
        {
            // 单击语义：名单区里单击 = 移出，托盘区里单击 = 加进关闭区。
            // 拖拽判定阈值放宽到 8px，避免手抖把单击误判成拖拽。
            SetList(app.Id,
                app.List == AppList.None ? AppList.Close : AppList.None,
                app.Name, app.ExecutablePath);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // 拖动中离开窗口是正常的（鼠标已捕获），不能取消拖拽
        if (_dragApp is null)
        {
            _hoverApp = null;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        int direction = e.Delta > 0 ? -1 : 1;

        if (Scaled(Palette.Margin, BinsY, ColumnWidth, BinsH).Contains(e.Location))
        {
            _scrollTray = Math.Clamp(_scrollTray + direction, 0, MaxScroll(TrayApps.Count));
            Invalidate();
        }
        else if (Scaled(RightColumnX, BinsY, ColumnWidth, BinsH).Contains(e.Location))
        {
            _scrollClose = Math.Clamp(_scrollClose + direction, 0, MaxScroll(CloseApps.Count));
            Invalidate();
        }
        else if (Scaled(Palette.Margin, SafeY, BaseW - Palette.Margin * 2, SafeH).Contains(e.Location))
        {
            var safe = Scaled(Palette.Margin, SafeY, BaseW - Palette.Margin * 2, SafeH);
            int capacity = SafeCapacity(safe);
            int pages = (SafeApps.Count + capacity - 1) / capacity;
            _scrollSafe = Math.Clamp(_scrollSafe + direction, 0, Math.Max(0, pages - 1));
            Invalidate();
        }
    }

    private static int MaxScroll(int count)
    {
        int rows = (count + GridCols - 1) / GridCols;
        return Math.Max(0, rows - VisibleRows);
    }

    /// <summary>
    /// 打开自绘右键菜单。菜单项每次按应用状态重建，
    /// 因为"加入/移出"的可选项取决于它当前在哪个名单里。
    /// </summary>
    private void ShowAppMenu(AppEntry app, Point at)
    {
        _menuItems.Clear();
        _menuAt = at;
        _menuHover = -1;
        _menuOpen = true;

        void Add(string text, Action action, bool enabled = true, bool danger = false)
            => _menuItems.Add((Rectangle.Empty, text, action, enabled, danger));

        if (app.IsProtected)
        {
            Add("在保护名单里，不能关闭", () => { }, enabled: false);
        }
        else
        {
            Add("加入「游戏时关闭」", () => SetList(app.Id, AppList.Close, app.Name, app.ExecutablePath),
                enabled: app.List != AppList.Close);
            Add("加入「安全区」", () => SetList(app.Id, AppList.Safe, app.Name, app.ExecutablePath),
                enabled: app.List != AppList.Safe);
            Add("从名单移出", () => SetList(app.Id, AppList.None),
                enabled: app.List != AppList.None, danger: true);
        }

        Add("打开文件位置", () =>
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{app.ExecutablePath}\"");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        });

        // 托盘区的条目可以彻底取消登记
        if (app.List == AppList.None && _config.Current.Tracked.Any(t =>
                t.Equals(app.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            Add("取消登记", () =>
            {
                _config.RemoveTracked(app.ExecutablePath);
                _ = RescanAsync();
            }, danger: true);
        }

        // 必须重置淡入进度并启动动画，否则 _menuFade 停在 0，
        // DrawMenu 直接 return 什么都不画，而 _menuOpen 已经是 true，
        // 界面看着就像卡死了。
        _menuFade = 0f;
        _menuBox = Rectangle.Empty;
        KickAnimation();
        Invalidate();
    }

    private void CloseMenu()
    {
        if (!_menuOpen) return;
        _menuOpen = false;
        _menuHover = -1;
        _menuItems.Clear();
        KickAnimation();   // 让 _menuFade 归零的过程中还能继续画到全透明
        Invalidate();
    }

    /// <summary>菜单打开时，判断点到了哪一项。</summary>
    private void ClickMenu(Point p)
    {
        if (!_menuBox.Contains(p)) { CloseMenu(); return; }

        int padY = S(5), rowH = S(28);
        int index = (p.Y - _menuBox.Y - padY) / Math.Max(1, rowH);
        if (index < 0 || index >= _menuItems.Count) { CloseMenu(); return; }

        var item = _menuItems[index];
        CloseMenu();
        if (item.Enabled) item.Action();
    }

    private void SetList(string appId, AppList list, string? displayName = null, string? exePath = null)
    {
        _config.SetList(appId, list, displayName, exePath);

        foreach (var app in _apps)
            if (app.Id.Equals(appId, StringComparison.OrdinalIgnoreCase))
                app.List = list;

        Invalidate();
    }

    /// <summary>capture 模式用：把某个图标的悬停气泡显示出来。</summary>
    public void ShowDemoTip(bool ghost = false)
    {
        // 默认拿托盘区第一个；ghost=true 时拿名单区里没在跑的那个，
        // 用来核对未运行条目的气泡显示
        var app = ghost
            ? CloseApps.Concat(SafeApps).FirstOrDefault(a => !a.IsRunning)
            : TrayApps.FirstOrDefault();
        if (app is null) return;

        _hoverApp = app;
        UpdateHoverTip(app, AppList.None);
        _tipAt = new Point(S(120), S(300));

        // capture 走 DoEvents 循环，动画定时器不 Tick，
        // 直接设成最终状态，否则截图里什么都看不到
        _hoverFade = 1f;
        _tipFade = 1f;
        Invalidate();
    }

    /// <summary>capture 模式用：把右键菜单显示出来。</summary>
    public void ShowDemoMenu()
    {
        var app = TrayApps.FirstOrDefault();
        if (app is null) return;

        ShowAppMenu(app, new Point(S(120), S(300)));

        // 同上：跳过动画，直接显示最终状态
        _menuFade = 1f;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            if (_menuOpen) { CloseMenu(); return; }
            Hide();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memTimer.Dispose();
            _scanCancel?.Cancel();
            _scanCancel?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
