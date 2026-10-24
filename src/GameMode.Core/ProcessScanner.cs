using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;

namespace GameMode.Core;

/// <summary>
/// 扫描正在运行的进程，聚合成 AppEntry。
///
/// 聚合口径：按 exe 所在目录归并，也就是"一个应用 = 一个安装目录"。
/// 一个应用的多个进程（zen.exe 与 crashpad_handler、PowerToys 那一堆子 exe）合成一条。
/// </summary>
public sealed class ProcessScanner
{
    /// <summary>系统目录一律不进列表。</summary>
    private static readonly string[] ExcludedRoots =
    {
        @"C:\Windows\",
        @"C:\Program Files\dotnet\",
        @"C:\ProgramData\",
    };

    /// <summary>临时目录：安装包残留、更新器缓存，都不是常驻应用。</summary>
    private static readonly string[] ExcludedFragments =
    {
        @"\Temp\",
        @"\$Recycle.Bin\",
    };

    /// <summary>辅助进程，自己不代表一个应用。</summary>
    private static readonly HashSet<string> HelperExecutables =
        new(new[] { "crashpad_handler.exe", "WerFault.exe" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描系统托盘里的应用。
    ///
    /// 判定方式见 <see cref="TrayEnumerator"/>：向系统反查每个 (隐藏窗口, 图标编号)
    /// 是不是一个真实存在的托盘图标。这是 Win11 上唯一有效的方法。
    /// </summary>
    /// <param name="protect">保护名单里的进程名（不含 .exe）。</param>
    /// <param name="extraTracked">用户额外登记的 exe（比如 Zen，关掉就是真关、不驻留托盘）。</param>
    public Task<List<AppEntry>> ScanAsync(IReadOnlyCollection<string> protect,
        IReadOnlyCollection<string>? extraTracked = null, CancellationToken cancel = default)
        => Task.Run(() => Scan(protect, extraTracked, cancel), cancel);

    public List<AppEntry> Scan(IReadOnlyCollection<string> protect,
        IReadOnlyCollection<string>? extraTracked = null, CancellationToken cancel = default)
    {
        var protectSet = new HashSet<string>(protect, StringComparer.OrdinalIgnoreCase);

        // 先给所有进程拍一次快照：路径、内存、进程名都取一次。
        // 托盘枚举也要用这些路径，重复取会让每轮扫描多花一倍时间。
        var snapshot = TakeSnapshot(cancel);

        // 托盘图标的宿主进程 id -> 注册表里的 exe 路径（可能为 null）
        var trayHosts = TrayEnumerator.GetTrayHosts(cancel, snapshot);

        var extraSet = new HashSet<string>(extraTracked ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, List<RawProcess>>(StringComparer.OrdinalIgnoreCase);

        // 自己也有托盘图标，会被自己的扫描检测到，必须排除
        int selfId = Environment.ProcessId;
        string? selfPath = Environment.ProcessPath;

        // 直接吃快照，不再重复枚举进程
        foreach (var (id, info) in snapshot)
        {
            if (cancel.IsCancellationRequested) break;
            if (id == selfId) continue;

            {
                var raw = new RawProcess(
                    Id: id,
                    ProcessName: info.Name,
                    MemoryMB: info.MemoryMB,
                    ExecutablePath: info.Path,
                    HasWindow: info.HasWindow);

                // 拿不到路径时，用托盘枚举从注册表带回来的路径补
                if (string.IsNullOrEmpty(raw.ExecutablePath)
                    && trayHosts.TryGetValue(raw.Id, out var hinted)
                    && !string.IsNullOrEmpty(hinted))
                {
                    raw = raw with { ExecutablePath = hinted };
                }

                if (string.IsNullOrEmpty(raw.ExecutablePath)) continue;
                if (IsExcluded(raw.ExecutablePath)) continue;
                if (selfPath is not null
                    && raw.ExecutablePath.Equals(selfPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 收两类：在托盘里有图标的，或用户手动登记的
                bool inTray = trayHosts.ContainsKey(raw.Id);
                bool tracked = extraSet.Contains(raw.ExecutablePath);
                if (!inTray && !tracked) continue;

                string key = GetGroupKey(raw);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<RawProcess>();
                list.Add(raw);
            }
        }

        var apps = new List<AppEntry>(groups.Count);
        foreach (var (_, members) in groups)
        {
            // 只对留下的应用补取「有没有主窗口」，
            // 数量从上百个降到十几个，这一步几乎不花时间。
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].HasWindow) continue;
                try
                {
                    using var p = Process.GetProcessById(members[i].Id);
                    members[i] = members[i] with { HasWindow = p.MainWindowHandle != IntPtr.Zero };
                }
                catch { }
            }

            var main = members.Where(m => m.HasWindow).MaxBy(m => m.MemoryMB)
                       ?? members.MaxBy(m => m.MemoryMB)!;

            var entry = new AppEntry
            {
                Id = Path.GetDirectoryName(main.ExecutablePath!)!.ToLowerInvariant(),
                Name = GetDisplayName(main.ExecutablePath!, main.ProcessName),
                ExecutablePath = main.ExecutablePath!,
                IsRunning = true,
                MemoryMB = members.Sum(m => m.MemoryMB),
                CanRestore = true,
            };

            entry.IsProtected = members.Any(m => protectSet.Contains(m.ProcessName));
            foreach (var m in members) entry.ProcessIds.Add(m.Id);
            apps.Add(entry);
        }

        // 按内存从大到小：用户最想先看到吃内存的
        return apps.OrderByDescending(a => a.MemoryMB).ToList();
    }

    /// <summary>
    /// 取进程的 exe 全路径。
    ///
    /// 不用 Process.MainModule：它要求 PROCESS_QUERY_INFORMATION 权限，
    /// 对 32 位进程、高权限进程经常返回 null 或直接抛异常。
    /// 实测 MaaEnd、Steamcommunity_302、sunshine 这三个就因此读不到路径，
    /// 表现为「进程在跑但列表里没有」。
    ///
    /// QueryFullProcessImageName 只需要 PROCESS_QUERY_LIMITED_INFORMATION，
    /// 覆盖范围大得多。
    /// </summary>
    internal static string? GetProcessPath(Process process)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            // 退回到 MainModule，能拿到就用
            try { return process.MainModule?.FileName; }
            catch { return null; }
        }

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
            return buffer.ToString(0, size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, int flags,
        StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>一次把进程信息取全，供扫描和托盘枚举共用。</summary>
    internal static Dictionary<int, (string? Path, string Name, long MemoryMB, bool HasWindow)>
        TakeSnapshot(CancellationToken cancel)
    {
        var map = new Dictionary<int, (string?, string, long, bool)>();

        foreach (var process in Process.GetProcesses())
        {
            if (cancel.IsCancellationRequested) break;

            using (process)
            {
                try
                {
                    // 先不取 MainWindowHandle：它对每个进程都要枚举一遍窗口，
                    // 94 个进程就是 94 轮，占整轮扫描一半以上时间。
                    // 先记 false，等确定了归属分组再对少量候选补取。
                    map[process.Id] = (
                        GetProcessPath(process),
                        process.ProcessName,
                        process.WorkingSet64 / 1024 / 1024,
                        false);
                }
                catch
                {
                    // 已退出或权限不足，跳过
                }
            }
        }

        return map;
    }

    private static bool IsExcluded(string path)
    {
        foreach (var root in ExcludedRoots)
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var fragment in ExcludedFragments)
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;

        return HelperExecutables.Contains(Path.GetFileName(path));
    }

    /// <summary>归组键：同一产品的多个可执行文件归一组。</summary>
    private static string GetGroupKey(RawProcess raw)
    {
        if (raw.ProcessName.StartsWith("WeChat", StringComparison.OrdinalIgnoreCase))
            return "product:wechat";

        if (raw.ProcessName.StartsWith("wetype_", StringComparison.OrdinalIgnoreCase))
            return "product:wetype";

        if (raw.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase))
            return "product:powertoys";

        // 网易 UU：uu.exe 与 GameViewer.exe 同属远程产品
        if (raw.ProcessName is "GameViewer" or "uu")
            return "product:netease-uu";

        return "dir:" + Path.GetDirectoryName(raw.ExecutablePath)!;
    }

    private static string GetDisplayName(string path, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            string? desc = info.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
        }
        catch { }

        return fallback;
    }

    private sealed record RawProcess(int Id, string ProcessName, long MemoryMB, string? ExecutablePath, bool HasWindow);
}

/// <summary>
/// 枚举系统托盘里真实存在的图标。
///
/// 原理：应用调用 Shell_NotifyIcon(NIM_ADD) 注册托盘图标时必须提供标识——
/// 要么 (窗口句柄, 图标编号 uID)，要么一个 GUID。
/// 系统提供 Shell_NotifyIconGetRect 查询"这个标识对应的图标现在在屏幕哪里"。
/// 于是反过来枚举：拿系统里每个窗口 × 一串 uID 去问，
/// 返回成功并给出坐标的，就是真实存在的托盘图标。
///
/// 为什么不用别的办法（本机都实测过）：
///   TB_BUTTONCOUNT           —— Win11 托盘由 XAML 渲染，ToolbarWindow32 是 0x0 空壳，返回 0
///   UIA 树                   —— 托盘图标不进可访问性树，整个任务栏只剩两个空 Pane
///   注册表 NotifyIconSettings —— 129 条里 93 条无 IconGuid，是历史残留；
///                               其中的 IconGuid 是 Windows 生成的设置键，不是 API 注册标识
/// </summary>
public static class TrayEnumerator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Shell_NotifyIconGetRect 的正式导出位置是 shell32.dll。
    // 绑到 taskbar.dll 会抛 EntryPointNotFoundException。
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// 每个窗口最多试到第几个图标编号。
    /// 多数应用用 0~5，但有些（比如 PowerToys 的多个工具）会用更大的编号，
    /// 这里放宽到 64，代价是每个窗口多问 30 次、单次调用微秒级，可接受。
    /// </summary>
    private const int MaxIconId = 64;

    /// <summary>
    /// 拿所有托盘图标宿主进程的 id，以及注册表里记录的 exe 路径。
    /// 路径是给「拿不到进程路径」的情况兜底的——比如 sunshine 以高权限运行，
    /// OpenProcess 直接失败，此时只能信注册表里那条记录。
    /// </summary>
    public static Dictionary<int, string?> GetTrayHosts(
        CancellationToken cancel = default,
        Dictionary<int, (string? Path, string Name, long MemoryMB, bool HasWindow)>? snapshot = null)
    {
        // 增量缓存：上一次已知的托盘宿主。
        // 完整枚举一轮约 2.5 秒（所有窗口 × 65 个编号），
        // 但绝大多数进程每次结果都一样。缓存起来，只探测新出现的进程，
        // 这样 3 秒一次的存活检查才能跑得起。
        var result = new Dictionary<int, string?>();
        var fresh = new HashSet<int>();

        // 先建立「进程 -> 它的窗口列表」缓存，三个路径共用
        var windowsByPid = CollectWindowsByProcess(cancel);

        // 进程 id 对应的真实 exe 路径，用来判断缓存是否还能用
        var pathByPid = snapshot is not null
            ? snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.Path)
            : ProcessScanner.TakeSnapshot(cancel).ToDictionary(kv => kv.Key, kv => kv.Value.Path);

        // 能命中缓存的就直接下结论（含"不是托盘"这种否定结论），不必再探测
        var next = new Dictionary<int, (string? Path, bool IsTray, string? Hint)>();
        foreach (var (pid, cached) in _probed)
        {
            pathByPid.TryGetValue(pid, out string? nowPath);
            if (!string.Equals(nowPath, cached.Path, StringComparison.OrdinalIgnoreCase)) continue;  // pid 复用了

            next[pid] = cached;
            fresh.Add(pid);
            if (cached.IsTray) result[pid] = cached.Hint;
        }

        // 路径一：注册表里的 UID 精确探测（主力）
        // HKCU\Control Panel\NotifyIconSettings 每条记录带 ExecutablePath 和 UID，
        // UID 就是应用注册图标时用的图标编号。拿它去问，命中率高且快。
        // 实测能覆盖 NVIDIA(1051)、火绒(420/421)、Wallpaper Engine(1000) 这些大编号。
        ProbeByRegistryUid(windowsByPid, result, cancel, fresh);

        // 路径三：GUID 注册的图标（先于暴力路径，因为暴力路径对它无效）
        // 有些应用不用 (hWnd, uID)，而是给 Shell_NotifyIcon 传一个 GUID，
        // 此时 hWnd 必须为 0。典型例子是 Lyricify。
        // GUID 来自注册表同一条记录的 IconGuid 值——对这类应用它恰好是有效的。
        ProbeByGuid(windowsByPid, result, cancel, fresh, snapshot ?? ProcessScanner.TakeSnapshot(cancel));

        // 路径二：暴力试小编号兜底
        // 有些应用没在注册表留 UID 记录（比如 Ekko Studio，它用 uID=3），
        // 对全部窗口试 0..64 能补上这类。
        ProbeByBruteForce(windowsByPid, result, cancel, fresh);

        // 回写缓存：命中的照抄，新探测的连否定结果一起记下来
        foreach (var pid in windowsByPid.Keys.Select(p => (int)p))
        {
            if (next.ContainsKey(pid)) continue;
            pathByPid.TryGetValue(pid, out string? path);
            next[pid] = (path, result.ContainsKey(pid), result.GetValueOrDefault(pid));
        }

        _probed = next;
        return result;
    }

    /// <summary>
    /// 探测结果缓存：进程 id -> (exe 路径, 是否托盘宿主, 注册表路径)。
    ///
    /// 关键点是**否定结果也要缓存**。只缓存"是托盘"的话，剩下的几十个
    /// 非托盘进程每轮都要重新试 65 个图标编号，实测占 1.2～1.9 秒。
    /// 带上否定结果之后，第二轮起整轮扫描降到几十毫秒。
    ///
    /// 用 exe 路径做校验：pid 会被系统复用，路径变了就说明是新进程，重新探测。
    /// </summary>
    private static Dictionary<int, (string? Path, bool IsTray, string? Hint)> _probed = new();

    /// <summary>清掉缓存。改配置或想强制重算时用。</summary>
    public static void InvalidateCache() => _probed = new();

    /// <summary>枚举全部窗口，按所属进程分组。</summary>
    private static Dictionary<uint, List<IntPtr>> CollectWindowsByProcess(CancellationToken cancel)
    {
        var roots = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            roots.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        var all = new List<IntPtr>(roots);
        foreach (var parent in roots)
        {
            if (cancel.IsCancellationRequested) break;
            EnumChildWindows(parent, (hWnd, _) =>
            {
                all.Add(hWnd);
                return true;
            }, IntPtr.Zero);
        }

        var map = new Dictionary<uint, List<IntPtr>>();
        foreach (var hWnd in all.Distinct())
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == 0) continue;

            if (!map.TryGetValue(pid, out var list))
                map[pid] = list = new List<IntPtr>();
            list.Add(hWnd);
        }

        return map;
    }

    /// <summary>
    /// 路径一：读注册表的 UID 记录，逐个精确探测。
    ///
    /// 注册表 HKCU\Control Panel\NotifyIconSettings 下每个子键代表一个图标设置，
    /// 有 ExecutablePath（exe 全路径）和 UID（注册时用的图标编号）。
    /// 注意 UID 可能很大——NVIDIA 用 1051、Wallpaper Engine 用 1000、
    /// 火绒用 420/421，暴力试 0..64 完全够不着，所以这条路径不可省。
    /// </summary>
    private static void ProbeByRegistryUid(Dictionary<uint, List<IntPtr>> windowsByPid,
        Dictionary<int, string?> result, CancellationToken cancel, HashSet<int> skip)
    {
        List<(string Path, int Uid)> records;
        try
        {
            records = ReadRegistryUidRecords();
        }
        catch
        {
            return;   // 注册表读不到不影响暴力路径
        }

        if (records.Count == 0) return;

        // 先把「exe 全路径 -> pid 列表」建好，
        // 否则要对每条记录遍历全部进程，129 条 x 数百进程会明显卡顿。
        var pidsByPath = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            if (cancel.IsCancellationRequested) break;

            using (process)
            {
                string? path = ProcessScanner.GetProcessPath(process);
                if (path is null) continue;

                if (!pidsByPath.TryGetValue(path, out var list))
                    pidsByPath[path] = list = new List<uint>();
                list.Add((uint)process.Id);
            }
        }

        // 文件名兜底表：有些进程（sunshine、以服务方式运行的安全软件）
        // 连 PROCESS_QUERY_LIMITED_INFORMATION 都拿不到，OpenProcess 直接返回 0，
        // 于是完整路径永远匹配不上。这类按文件名再试一次。
        // 同名不同路径的风险由「图标探测必须命中」这个条件兜住——
        // 即便撞上同名进程，IsTrayIcon 不成功也不会误判。
        var pidsByFileName = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, pids) in pidsByPath)
        {
            string fileName = Path.GetFileName(path);
            if (!pidsByFileName.TryGetValue(fileName, out var list))
                pidsByFileName[fileName] = list = new List<uint>();
            foreach (uint pid in pids)
                if (!list.Contains(pid)) list.Add(pid);
        }

        // 再按进程名补一遍：sunshine 这类高权限进程连受限查询都拿不到，
        // 只能靠进程名（= exe 名去掉扩展名）对回去。
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string exeGuess;
                try { exeGuess = process.ProcessName + ".exe"; }
                catch { continue; }

                uint pid = (uint)process.Id;
                if (!pidsByFileName.TryGetValue(exeGuess, out var list))
                    pidsByFileName[exeGuess] = list = new List<uint>();
                if (!list.Contains(pid)) list.Add(pid);
            }
        }

        bool debug = Environment.GetEnvironmentVariable("GAMEMODE_TRAY_DEBUG") == "1";
        if (debug)
        {
            Console.WriteLine($"[debug] 注册表记录 {records.Count} 条，有路径的进程 {pidsByPath.Count} 个，"
                            + $"文件名索引 {pidsByFileName.Count} 个，窗口表 {windowsByPid.Count} 个进程");
        }

        foreach (var (exePath, uid) in records)
        {
            if (cancel.IsCancellationRequested) break;

            string resolved = ExpandKnownFolder(exePath);
            string fileName = Path.GetFileName(resolved);

            // 先按完整路径找，找不到再按文件名找
            bool byPath = pidsByPath.ContainsKey(resolved);
            if (!pidsByPath.TryGetValue(resolved, out var pids))
            {
                if (!pidsByFileName.TryGetValue(fileName, out pids))
                {
                    if (debug) Console.WriteLine($"[debug] 未匹配 {fileName} (uid={uid})");
                    continue;
                }
            }

            if (debug)
            {
                var withWindows = pids.Count(p => windowsByPid.ContainsKey(p));
                Console.WriteLine($"[debug] {fileName} uid={uid} 匹配 {pids.Count} 个进程" +
                                  $"(有窗口的 {withWindows} 个) 按{(byPath ? "路径" : "文件名")}");
            }

            foreach (uint pid in pids)
            {
                if (skip.Contains((int)pid)) continue;
                if (result.ContainsKey((int)pid)) continue;
                if (!windowsByPid.TryGetValue(pid, out var windows)) continue;

                bool found = false;
                foreach (var hWnd in windows)
                {
                    if (!IsTrayIcon(hWnd, uid)) continue;
                    result[(int)pid] = resolved;   // 记下注册表里的路径，供兜底
                    found = true;
                    break;
                }

                if (debug && !found)
                    Console.WriteLine($"[debug]   pid={pid} 有 {windows.Count} 个窗口，uid={uid} 全部未命中");
            }
        }
    }

    /// <summary>
    /// 路径三：GUID 注册的图标。
    /// 用 hWnd=0 + guidItem=注册表 IconGuid 去问系统。
    /// 这类图标无法靠 (hWnd, uID) 找到——它的宿主窗口上任何 uID 都探测不到。
    /// </summary>
    private static void ProbeByGuid(Dictionary<uint, List<IntPtr>> windowsByPid,
        Dictionary<int, string?> result, CancellationToken cancel, HashSet<int> skip,
        Dictionary<int, (string? Path, string Name, long MemoryMB, bool HasWindow)> snapshot)
    {
        List<(string Path, int Uid, string? Guid)> records;
        try
        {
            records = ReadRegistryRecordsWithGuid();
        }
        catch
        {
            return;
        }

        if (records.Count == 0) return;

        // 先建一次「路径 -> pid」索引。
        // 原先是每条记录都遍历一遍进程列表，129 条 × 94 个进程 = 一万多次
        // GetProcessPath，光这一步就要 400ms。
        var pidsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pidsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, info) in snapshot)
        {
            if (cancel.IsCancellationRequested) break;

            if (info.Path is not null) pidsByPath.TryAdd(info.Path, pid);
            pidsByName.TryAdd(info.Name + ".exe", pid);
        }

        foreach (var (exePath, _, guidText) in records)
        {
            if (cancel.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(guidText)) continue;
            if (!Guid.TryParse(guidText, out var guid)) continue;

            string resolved = ExpandKnownFolder(exePath);
            string fileName = Path.GetFileName(resolved);

            if (!pidsByPath.TryGetValue(resolved, out int pid)
                && !pidsByName.TryGetValue(fileName, out pid))
                continue;

            if (skip.Contains(pid)) continue;
            if (result.ContainsKey(pid)) continue;

            if (!IsTrayIconByGuid(guid)) continue;
            result[pid] = resolved;
        }
    }

    /// <summary>用 GUID 问系统：这个图标现在在不在。</summary>
    private static bool IsTrayIconByGuid(Guid guid)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = IntPtr.Zero,
            uID = 0,
            guidItem = guid,
        };

        try
        {
            return Shell_NotifyIconGetRect(ref id, out _) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读注册表记录，连 IconGuid 一起带出来。</summary>
    private static List<(string Path, int Uid, string? Guid)> ReadRegistryRecordsWithGuid()
    {
        var records = new List<(string, int, string?)>();

        using var root = Registry.CurrentUser.OpenSubKey(
            @"Control Panel\NotifyIconSettings", writable: false);
        if (root is null) return records;

        foreach (string name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name, writable: false);
            if (sub is null) continue;

            if (sub.GetValue("ExecutablePath") is not string path) continue;
            if (string.IsNullOrWhiteSpace(path)) continue;

            int uid = sub.GetValue("UID") is int u ? u : -1;
            string? guid = sub.GetValue("IconGuid") as string;

            records.Add((path, uid, guid));
        }

        return records;
    }

    /// <summary>路径二：对所有窗口试小编号，补注册表里没有记录的应用。</summary>
    private static void ProbeByBruteForce(Dictionary<uint, List<IntPtr>> windowsByPid,
        Dictionary<int, string?> result, CancellationToken cancel, HashSet<int> skip)
    {
        foreach (var (pid, windows) in windowsByPid)
        {
            if (cancel.IsCancellationRequested) break;
            if (skip.Contains((int)pid)) continue;        // 缓存里已确认
            if (result.ContainsKey((int)pid)) continue;   // 路径一已确认

            // 托盘宿主的窗口一定不可见。可见窗口不可能是托盘图标宿主，
            // 跳过它们能省掉大部分调用。
            bool hit = false;
            foreach (var hWnd in windows)
            {
                if (IsWindowVisible(hWnd)) continue;

                for (int iconId = 0; iconId <= MaxIconId; iconId++)
                {
                    if (!IsTrayIcon(hWnd, iconId)) continue;
                    result[(int)pid] = null;   // 暴力路径拿不到可靠路径
                    hit = true;
                    break;
                }

                if (hit) break;
            }
        }
    }

    /// <summary>读注册表里所有带 UID 的托盘图标记录。</summary>
    private static List<(string Path, int Uid)> ReadRegistryUidRecords()
    {
        var records = new List<(string, int)>();

        using var root = Registry.CurrentUser.OpenSubKey(
            @"Control Panel\NotifyIconSettings", writable: false);
        if (root is null) return records;

        foreach (string name in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(name, writable: false);
            if (sub is null) continue;

            if (sub.GetValue("ExecutablePath") is not string path) continue;
            if (sub.GetValue("UID") is not int uid) continue;
            if (string.IsNullOrWhiteSpace(path)) continue;

            records.Add((path, uid));
        }

        return records;
    }

    /// <summary>
    /// 展开路径开头的已知文件夹 GUID。
    /// 注册表里 NVIDIA 的路径长这样：
    /// {6D809377-6AF0-444B-8957-A3773F02200E}\NVIDIA Corporation\NvContainer\nvcontainer.exe
    /// 不展开就和进程的真实路径对不上。
    /// </summary>
    private static string ExpandKnownFolder(string path)
    {
        if (path.Length < 38 || path[0] != '{') return path;

        string guidPart = path[..38];
        if (!Guid.TryParse(guidPart, out var guid)) return path;

        try
        {
            string? basePath = KnownFolderPath(guid);
            if (basePath is null) return path;
            return Path.Combine(basePath, path[39..]);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>把 KNOWNFOLDERID 翻成真实路径。</summary>
    private static string? KnownFolderPath(Guid guid)
    {
        IntPtr ptr;
        int hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out ptr);
        if (hr != 0) return null;

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags,
        IntPtr token, out IntPtr path);

    /// <summary>问系统：这个 (窗口, 编号) 是不是一个现存的托盘图标。</summary>
    private static bool IsTrayIcon(IntPtr hWnd, int iconId)
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = hWnd,
            uID = iconId,
            guidItem = Guid.Empty,
        };

        try
        {
            // 返回 0（S_OK）说明图标存在，rect 给出它在屏幕上的位置
            return Shell_NotifyIconGetRect(ref id, out _) == 0;
        }
        catch
        {
            return false;
        }
    }
}
