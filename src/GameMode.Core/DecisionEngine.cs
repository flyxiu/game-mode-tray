namespace GameMode.Core;

/// <summary>对一个应用要做的动作。</summary>
public enum AppAction
{
    /// <summary>不动。</summary>
    Skip,
    /// <summary>结束进程。</summary>
    Kill,
    /// <summary>启动它。</summary>
    Start,
}

/// <summary>一次执行计划里的一项。</summary>
public sealed record PlannedAction(AppEntry App, AppAction Action, string Reason);

/// <summary>
/// 判定引擎：只做纯逻辑，不碰进程、不碰磁盘。
/// 好处是这套规则能用单元测试锁住——比如"关闭名单里但没在跑的应用，开始游戏时跳过"。
/// </summary>
public static class DecisionEngine
{
    /// <summary>
    /// 开始游戏：只关"在关闭名单里、正在跑、不受保护"的应用。
    /// 安全区的应用哪怕在跑也不碰；没在跑的名单项直接跳过。
    /// </summary>
    public static List<PlannedAction> PlanClose(IEnumerable<AppEntry> apps)
    {
        var plan = new List<PlannedAction>();

        foreach (var app in apps)
        {
            if (app.IsProtected)
            {
                plan.Add(new(app, AppAction.Skip, "受保护"));
                continue;
            }

            switch (app.List)
            {
                case AppList.Safe:
                    plan.Add(new(app, AppAction.Skip, "在安全区"));
                    break;

                case AppList.Close when !app.IsRunning:
                    plan.Add(new(app, AppAction.Skip, "没在跑"));
                    break;

                case AppList.Close:
                    plan.Add(new(app, AppAction.Kill, "关闭名单且在跑"));
                    break;

                default:
                    plan.Add(new(app, AppAction.Skip, "不在名单里"));
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// 结束游戏：只开本次真关掉的应用。
    /// closedIds 来自状态文件，不是当前名单——中途改了名单也不影响这次恢复。
    /// </summary>
    public static List<PlannedAction> PlanRestore(IEnumerable<AppEntry> apps, IReadOnlySet<string> closedIds)
    {
        var plan = new List<PlannedAction>();

        foreach (var app in apps)
        {
            if (!closedIds.Contains(app.Id))
            {
                plan.Add(new(app, AppAction.Skip, "本次没关"));
                continue;
            }

            if (app.IsRunning)
            {
                plan.Add(new(app, AppAction.Skip, "已经在跑"));
                continue;
            }

            if (!app.CanRestore)
            {
                plan.Add(new(app, AppAction.Skip, "没有启动路径"));
                continue;
            }

            plan.Add(new(app, AppAction.Start, "本次被关掉"));
        }

        return plan;
    }

    /// <summary>计划里真正会执行的动作。</summary>
    public static IEnumerable<PlannedAction> Effective(IEnumerable<PlannedAction> plan)
        => plan.Where(p => p.Action != AppAction.Skip);
}
