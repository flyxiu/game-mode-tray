namespace GameMode.Core;

/// <summary>应用在哪个名单里。两者互斥，一个应用最多属于一个。</summary>
public enum AppList
{
    /// <summary>没进任何名单，游戏时保持原样。</summary>
    None,
    /// <summary>关闭名单：开始游戏时关掉，结束游戏时开回来。</summary>
    Close,
    /// <summary>安全区：任何情况下都不碰。</summary>
    Safe,
}

/// <summary>
/// 一个可管理的应用。按 exe 全路径聚合——同一个 exe 的多个进程算一个应用，
/// 比如 Zen 有十几个子进程，界面上只出现一个。
/// </summary>
public sealed class AppEntry
{
    /// <summary>配置里的稳定标识，取 exe 全路径的小写形式。不随显示名变化。</summary>
    public required string Id { get; init; }

    /// <summary>显示名。优先取 exe 的文件描述，取不到时用文件名。</summary>
    public required string Name { get; init; }

    /// <summary>exe 全路径。拖进名单时由它自动填充，不用手写。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>同名的所有进程 id，用于结束进程。</summary>
    public List<int> ProcessIds { get; } = new();

    /// <summary>合计工作集，MB。</summary>
    public long MemoryMB { get; set; }

    /// <summary>此刻在不在跑。不在跑的名单项显示为灰，执行时跳过。</summary>
    public bool IsRunning { get; set; }

    /// <summary>在哪个名单里。</summary>
    public AppList List { get; set; } = AppList.None;

    /// <summary>
    /// 是否落在系统级保护名单里（explorer、svchost、火绒这类）。
    /// 保护的应用不能被拖进关闭名单，也不能被结束进程。
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>
    /// 关掉之后能不能自动开回来。false 表示只有关闭动作、没有启动路径，
    /// 比如 WSL 走 wsl --shutdown，下次用到时由系统自己拉起。
    /// </summary>
    public bool CanRestore { get; set; }

    /// <summary>
    /// 这个是 WSL，不是普通应用。它的内存落在 vmmemWSL 里，
    /// 关闭要走 `wsl --shutdown` 而不是结束进程（vmmem 是系统进程，杀不掉）。
    /// 界面里固定排第一，没运行也显示，标灰。
    /// </summary>
    public bool IsWsl { get; set; }

    public override string ToString() => $"{Name} ({MemoryMB} MB, {List}, {(IsRunning ? "运行中" : "未运行")})";
}
