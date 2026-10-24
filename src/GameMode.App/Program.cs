namespace GameMode.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 调试用：--capture <目录> 把界面渲染成 PNG 后退出。
        // 不依赖屏幕截图，改样式时可以直接比对，也不会受别的窗口遮挡。
        SetupDpi();

        if (args.Length >= 2 && args[0] == "--capture")
        {
            string demo = args.Length >= 3 ? args[2] : "";
            RunCapture(args[1], demo);
            return;
        }

        // 调试用：--list 打印扫描结果，用来核对过滤规则
        if (args.Length >= 1 && args[0] == "--list")
        {
            RunList(args);
            return;
        }

        // 不开窗口直接执行。可以拿来做桌面快捷方式或开机脚本
        if (args.Length >= 1 && args[0] is "--close" or "--restore")
        {
            args_debug = args.Length >= 2 && args[1] == "debug";
            RunHeadless(args[0] == "--close").GetAwaiter().GetResult();
            return;
        }

        // 全局异常兜底：winexe 崩溃时什么都看不到，至少落一份日志
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) => CrashLog(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 单实例：重复启动只是把已有窗口叫出来
        const string mutexName = @"Global\GameModeTray_SingleInstance";
        using var mutex = new Mutex(true, mutexName, out bool created);

        if (!created)
        {
            MessageBox.Show("游戏模式已经在运行了，看系统托盘。", "游戏模式",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>
    /// 不开窗口执行一次关闭或恢复。
    /// 恢复的坑：名字里的"启动"必须用 exe 全路径，拿应用目录去 Process.Start
    /// 只会弹出资源管理器——这就是早先"关得掉、开不回来"的原因。
    /// </summary>
    private static bool args_debug;

    private static async Task RunHeadless(bool close)
    {
        string dir = AppContext.BaseDirectory;
        var store = new GameMode.Core.ConfigStore(dir);
        var cfg = store.Load();
        var scanner = new GameMode.Core.ProcessScanner();
        var runner = new GameMode.Core.ActionRunner(dir);

        var apps = await scanner.ScanAsync(cfg.Protect, cfg.Tracked);
        foreach (var app in apps) app.List = store.GetList(app.Id);

        // 名单里有、但没在跑的应用也要进来，否则恢复时找不到它
        if (!close)
        {
            apps = MergeWithRecorded(apps, cfg, store);
        }

        if (close)
        {
            if (args_debug)
            {
                Console.WriteLine($"[debug] 配置 Close 有 {cfg.Close.Count} 条");
                foreach (var r in cfg.Close)
                    Console.WriteLine($"[debug]   记录 Id=[{r.Id}] Name=[{r.Name}] Exe=[{r.Exe}]");
            }

            if (args_debug)
                foreach (var a in apps)
                    Console.WriteLine($"[debug] {a.Name} id={a.Id} list={a.List} running={a.IsRunning} prot={a.IsProtected}");

            var (state, log) = await runner.CloseAsync(apps, cfg.StopCommands);
            Console.WriteLine($"关闭 {state.ClosedIds.Count} 个，释放 {state.FreedMB} MB");
            foreach (var line in log) Console.WriteLine("  " + line);
        }
        else
        {
            var (started, manual, log) = await runner.RestoreAsync(apps);
            Console.WriteLine($"启动 {started.Count} 个");
            foreach (var line in log) Console.WriteLine("  " + line);
            foreach (var name in manual) Console.WriteLine($"  需手动处理：{name}");
        }
    }

    /// <summary>把名单里记录过、但当前没在运行的应用也拼进列表，供恢复使用。</summary>
    private static List<GameMode.Core.AppEntry> MergeWithRecorded(
        List<GameMode.Core.AppEntry> running, GameMode.Core.GameModeConfig cfg,
        GameMode.Core.ConfigStore store)
    {
        var seen = running.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = new List<GameMode.Core.AppEntry>(running);

        foreach (var reference in cfg.Close.Concat(cfg.Safe))
        {
            if (seen.Contains(reference.Id)) continue;

            string exe = !string.IsNullOrWhiteSpace(reference.Exe)
                ? reference.Exe!
                : FindExecutable(reference.Id);

            all.Add(new GameMode.Core.AppEntry
            {
                Id = reference.Id,
                Name = reference.Name ?? Path.GetFileNameWithoutExtension(exe),
                ExecutablePath = exe,
                List = store.GetList(reference.Id),
                IsRunning = false,
                CanRestore = true,
            });
        }

        return all;
    }

    /// <summary>目录里找主 exe。</summary>
    private static string FindExecutable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return dir;
            var files = Directory.EnumerateFiles(dir, "*.exe").ToList();
            if (files.Count == 0) return dir;

            string name = Path.GetFileName(dir.TrimEnd('\\'));
            return files.FirstOrDefault(f =>
                       Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? files.OrderByDescending(f => new FileInfo(f).Length).First();
        }
        catch
        {
            return dir;
        }
    }

    /// <summary>打印扫描结果，核对过滤规则用。</summary>
    private static void RunList(string[] args)
    {
        string dir = AppContext.BaseDirectory;
        var store = new GameMode.Core.ConfigStore(dir);
        var cfg = store.Load();
        var scanner = new GameMode.Core.ProcessScanner();

        // --list debug 打开托盘枚举的诊断输出
        if (args.Any(a => a == "debug"))
            Environment.SetEnvironmentVariable("GAMEMODE_TRAY_DEBUG", "1");

        // 自动枚举托盘图标 + 用户额外登记的应用
        var apps = scanner.Scan(cfg.Protect, cfg.Tracked);

        // --list bench：同一进程内连扫三次，看增量缓存的效果
        if (args.Length >= 2 && args[1] == "bench")
        {
            for (int i = 1; i <= 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var again = scanner.Scan(cfg.Protect, cfg.Tracked);
                sw.Stop();
                Console.WriteLine($"第 {i} 次：{again.Count} 项，{sw.ElapsedMilliseconds} ms");
            }
        }

        // --list mem：核对可用内存口径（应和系统"可用物理内存"一致）
        if (args.Any(a => a == "mem"))
        {
            var runnerType = typeof(GameMode.Core.ActionRunner);
            var method = runnerType.GetMethod("GetAvailableMB",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            object? value = method?.Invoke(null, null);
            Console.WriteLine($"程序算出的可用内存：{value} MB");
        }

        Console.WriteLine($"共 {apps.Count} 项（额外登记 {cfg.Tracked.Count} 个）");
        foreach (var app in apps)
            Console.WriteLine($"{app.MemoryMB,7} MB  {app.Name,-28} {System.IO.Path.GetFileName(app.ExecutablePath)}");
    }

    /// <summary>把窗口内容渲染成图片，供样式核对使用。</summary>
    private static void RunCapture(string outDir, string demo = "")
    {
        SetupDpi();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(outDir);

        MainForm.TraceSize = true;
        using var form = new MainForm();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        File.AppendAllText(Path.Combine(outDir, "size-trace.txt"),
            $"{DateTime.Now:HH:mm:ss.fff} [BeforeShow] ClientSize={form.ClientSize} Size={form.Size} Dpi={form.DeviceDpi}\r\n");
        form.Show();

        // 等扫描和首帧绘制完成
        // 托盘枚举要遍历全部窗口 × 64 个 uID，比纯进程扫描慢，
        // 这里等够时间再截，否则拍到的是空列表。
        for (int i = 0; i < 150; i++)
        {
            Application.DoEvents();
            form.PumpLiveState();   // 定时器在 DoEvents 循环里不 Tick，手动驱动
            Thread.Sleep(100);
        }

        File.AppendAllText(Path.Combine(outDir, "size-trace.txt"),
            $"{DateTime.Now:HH:mm:ss.fff} [AfterShow] ClientSize={form.ClientSize} Size={form.Size} Dpi={form.DeviceDpi}\r\n");

        // 演示用的界面状态：气泡或菜单，便于静态核对
        if (demo == "tip") form.ShowDemoTip();
        else if (demo == "ghost") form.ShowDemoTip(ghost: true);
        else if (demo == "menu") form.ShowDemoMenu();

        for (int i = 0; i < 6; i++)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));

        // 磨砂窗口本身是透明的，垫一层渐变模拟桌面透过来的效果
        using var composed = new Bitmap(bitmap.Width, bitmap.Height);
        using (var g = Graphics.FromImage(composed))
        {
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, composed.Width, composed.Height),
                Color.FromArgb(48, 40, 74),
                Color.FromArgb(18, 30, 46),
                45f))
            {
                g.FillRectangle(bg, 0, 0, composed.Width, composed.Height);
            }
            g.DrawImage(bitmap, 0, 0);
        }

        string path = Path.Combine(outDir, "window.png");
        composed.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        // 顺带把尺寸信息写出来，排查布局问题时不用再猜
        File.WriteAllText(Path.Combine(outDir, "size.txt"),
            $"DeviceDpi={form.DeviceDpi}\r\n" +
            $"ClientSize={form.ClientSize}\r\n" +
            $"Form.Width/Height={form.Width}x{form.Height}\r\n" +
            $"预期物理={MainForm.BaseW * form.DeviceDpi / 96f:0}x{MainForm.BaseH * form.DeviceDpi / 96f:0}");

        Console.WriteLine(path);

        form.Hide();
    }

    /// <summary>
    /// 在所有窗口创建之前声明 DPI 感知。
    /// 少了这一步，150% 缩放下 Windows 会把整个窗口当位图拉大，
    /// 表现为文字发虚、圆角有锯齿、颜色边缘出彩边。
    /// 必须早于 Application.Run，也早于任何 Graphics 调用。
    /// </summary>
    private static void SetupDpi()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            // 老系统上不支持就退到系统级感知，至少不会整体拉伸
            try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }
        }
    }

    private static void CrashLog(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"""
                [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}
                {ex.StackTrace}

                """);
        }
        catch
        {
            // 连日志都写不了就只能放弃
        }
    }
}
