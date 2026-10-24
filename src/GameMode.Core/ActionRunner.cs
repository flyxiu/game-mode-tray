using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameMode.Core;

/// <summary>关闭时记下关了谁，恢复时照着这个来。</summary>
public sealed class SessionState
{
    public DateTimeOffset StartedAt { get; set; }
    public List<string> ClosedIds { get; set; } = new();
    public long FreedMB { get; set; }
}

/// <summary>执行动作，并维护状态文件。不碰 UI。</summary>
public sealed class ActionRunner(string configDir)
{
    private string StatePath => Path.Combine(configDir, "tray-state.json");

    /// <summary>
    /// 关掉名单里在跑的应用。返回本次关掉了哪些 id。
    /// </summary>
    public async Task<(SessionState State, List<string> Log)> CloseAsync(
        IReadOnlyList<AppEntry> apps,
        IReadOnlyList<StopCommand> stopCommands,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var state = new SessionState { StartedAt = DateTimeOffset.Now };
        var log = new List<string>();
        var before = GetAvailableMB();

        foreach (var item in DecisionEngine.Effective(DecisionEngine.PlanClose(apps)))
        {
            if (cancel.IsCancellationRequested) break;

            var app = item.App;
            progress?.Report(app.Name);

            try
            {
                // WSL 必须先处理：它的内存落在 vmmemWSL 里，
                // 那是系统进程杀不掉，只能整条 wsl --shutdown。
                if (app.IsWsl)
                {
                    await RunCommandAsync("wsl.exe --shutdown", cancel);
                    log.Add($"{app.Name}：已执行 wsl --shutdown");
                }
                // 其他应用看有没有配 stopCommand
                else if (stopCommands.FirstOrDefault(c =>
                    string.Equals(c.Process, Path.GetFileNameWithoutExtension(app.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase)) is { } cmd)
                {
                    await RunCommandAsync(cmd.Command, cancel);
                    log.Add($"{app.Name}：执行 {cmd.Command}");
                }
                else
                {
                    await KillAsync(app, cancel);
                    log.Add($"{app.Name}：已结束");
                }

                state.ClosedIds.Add(app.Id);
            }
            catch (Exception ex)
            {
                log.Add($"{app.Name}：失败，{ex.Message}");
            }
        }

        // 给系统一点时间回收，再读一次可用内存
        await Task.Delay(800, cancel).ContinueWith(_ => { }, TaskScheduler.Default);
        state.FreedMB = GetAvailableMB() - before;

        await SaveStateAsync(state, cancel);
        return (state, log);
    }

    /// <summary>
    /// 关掉单个应用。右键菜单里的「关闭这个应用」用，不影响会话状态文件——
    /// 那不是「开始游戏」，结束后不该被「结束游戏」重新拉起来。
    /// </summary>
    public async Task<(bool Ok, string Message)> CloseOneAsync(AppEntry app, CancellationToken cancel)
    {
        try
        {
            if (app.IsWsl)
            {
                await RunCommandAsync("wsl.exe --shutdown", cancel);
                return (true, "WSL 已关闭");
            }

            await KillAsync(app, cancel);
            return (true, $"{app.Name} 已关闭");
        }
        catch (Exception ex)
        {
            return (false, $"关闭失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动单个应用。右键菜单里的「启动这个应用」用。
    /// 启动后同样做静默处理，不让窗口弹到桌面上。
    /// </summary>
    public async Task<(bool Ok, string Message)> LaunchOneAsync(AppEntry app, CancellationToken cancel)
    {
        try
        {
            // WSL 没有「启动」的概念，跑一条空命令把默认发行版拉起来
            if (app.IsWsl)
            {
                await RunCommandAsync("wsl.exe -- true", cancel);
                return (true, "WSL 已启动");
            }

            var proc = Process.Start(new ProcessStartInfo(app.ExecutablePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });

            await MinimizeWhenReadyAsync(proc, app, cancel);
            return (true, $"{app.Name} 已启动");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>按状态文件恢复。返回启动成功的名字和需要手动处理的。</summary>
    public async Task<(List<string> Started, List<string> Manual, List<string> Log)> RestoreAsync(
        IReadOnlyList<AppEntry> apps,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var started = new List<string>();
        var manual = new List<string>();
        var log = new List<string>();

        var state = await LoadStateAsync(cancel);
        if (state is null) return (started, manual, log);

        var closedIds = state.ClosedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in DecisionEngine.Effective(DecisionEngine.PlanRestore(apps, closedIds)))
        {
            if (cancel.IsCancellationRequested) break;
            progress?.Report(item.App.Name);

            try
            {
                var proc = Process.Start(new ProcessStartInfo(item.App.ExecutablePath)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized,
                });

                started.Add(item.App.Name);
                log.Add($"{item.App.Name}：已启动");

                // 静默启动：等主窗口出来就收进托盘，不让它弹在桌面上。
                // UseShellExecute 的 WindowStyle 对不少程序不生效（尤其是
                // 自己决定初始显示方式的 Electron / MSIX 应用），所以再补一刀。
                await MinimizeWhenReadyAsync(proc, item.App, cancel);
            }
            catch (Exception ex)
            {
                log.Add($"{item.App.Name}：启动失败，{ex.Message}");
            }
        }

        // 被关掉但没有启动路径的（WSL 这类）提示手动处理
        foreach (var id in closedIds)
        {
            var app = apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (app is not null && !app.CanRestore) manual.Add(app.Name);
        }

        try { File.Delete(StatePath); } catch { }

        return (started, manual, log);
    }

    /// <summary>
    /// 等应用的主窗口出现后把它最小化。
    /// 不是所有程序都会马上建窗口，所以轮询一小段时间；找不到就算了，
    /// 不能因为最小化失败就让恢复流程报错。
    /// </summary>
    private static async Task MinimizeWhenReadyAsync(Process? started, AppEntry app, CancellationToken cancel)
    {
        const int SwMinimize = 6;

        // 不用 started.MainWindowHandle：多进程应用里启动的那个往往不是有窗口的。
        // 按 pid 集合轮询更可靠。
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (cancel.IsCancellationRequested) return;
            await Task.Delay(150, cancel).ContinueWith(_ => { }, TaskScheduler.Default);

            foreach (int pid in app.ProcessIds.DefaultIfEmpty(started?.Id ?? 0))
            {
                if (pid == 0) continue;

                try
                {
                    using var p = Process.GetProcessById(pid);
                    IntPtr hWnd = p.MainWindowHandle;
                    if (hWnd == IntPtr.Zero) continue;

                    ShowWindow(hWnd, SwMinimize);
                    return;   // 收到就应该收手，别反复折腾用户的窗口
                }
                catch
                {
                    // 进程已经退出或者拿不到句柄，继续试下一个
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static async Task KillAsync(AppEntry app, CancellationToken cancel)
    {
        // 先温和关闭，给用户留保存的机会
        foreach (var pid in app.ProcessIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.CloseMainWindow();
            }
            catch { }
        }

        await Task.Delay(700, cancel).ContinueWith(_ => { }, TaskScheduler.Default);

        // 一秒后还活着的强杀
        foreach (var pid in app.ProcessIds)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private static async Task RunCommandAsync(string command, CancellationToken cancel)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        if (p is null) return;
        await p.WaitForExitAsync(cancel);
    }

    /// <summary>
    /// 系统当前可用物理内存。
    ///
    /// 不能用 GC.GetGCMemoryInfo().TotalAvailableMemoryBytes——那说的是
    /// .NET 自己还能申请多少（受容器/GC 限制影响），不是系统剩余多少，
    /// 所以关完应用算出来永远是 0，通知里一直显示"释放 0 MB"。
    /// </summary>
    private static long GetAvailableMB()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return 0;

        return (long)(status.ullAvailPhys / 1024 / 1024);
    }

    [StructLayout(LayoutKind.Sequential)]
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
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private async Task SaveStateAsync(SessionState state, CancellationToken cancel)
    {
        var json = JsonSerializer.Serialize(state, GameModeConfig.JsonOptions);
        await File.WriteAllTextAsync(StatePath, json, cancel);
    }

    private async Task<SessionState?> LoadStateAsync(CancellationToken cancel)
    {
        if (!File.Exists(StatePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(StatePath, cancel);
            return JsonSerializer.Deserialize<SessionState>(json);
        }
        catch
        {
            return null;
        }
    }
}
