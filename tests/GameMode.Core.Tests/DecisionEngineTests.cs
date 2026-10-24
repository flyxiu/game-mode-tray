using GameMode.Core;
using Xunit;

namespace GameMode.Core.Tests;

/// <summary>
/// 判定规则的护栏。这些用例对应的是 docs/设计.md 里那张状态表，
/// 改规则时必须同步改这里，否则测试会挡住。
/// </summary>
public class DecisionEngineTests
{
    private static AppEntry Make(string id, AppList list, bool running = true,
        bool isProtected = false, bool canRestore = true) => new()
    {
        Id = id,
        Name = id,
        ExecutablePath = $@"D:\Apps\{id}.exe",
        List = list,
        IsRunning = running,
        IsProtected = isProtected,
        CanRestore = canRestore,
        MemoryMB = running ? 100 : 0,
    };

    [Fact]
    public void 关闭名单里且在跑的应用会被关掉()
    {
        var app = Make("zen", AppList.Close);
        var plan = DecisionEngine.PlanClose([app]);

        Assert.Equal(AppAction.Kill, plan.Single().Action);
    }

    [Fact]
    public void 关闭名单里但没在跑的应用跳过()
    {
        var app = Make("mumu", AppList.Close, running: false);
        var plan = DecisionEngine.PlanClose([app]);

        Assert.Equal(AppAction.Skip, plan.Single().Action);
        Assert.Equal("没在跑", plan.Single().Reason);
    }

    [Fact]
    public void 安全区的应用在跑也不碰()
    {
        var app = Make("weixin", AppList.Safe);
        var plan = DecisionEngine.PlanClose([app]);

        Assert.Equal(AppAction.Skip, plan.Single().Action);
        Assert.Equal("在安全区", plan.Single().Reason);
    }

    [Fact]
    public void 不在名单里的应用不碰()
    {
        var app = Make("nv", AppList.None);
        var plan = DecisionEngine.PlanClose([app]);

        Assert.Equal(AppAction.Skip, plan.Single().Action);
    }

    [Fact]
    public void 受保护的应用即使进了关闭名单也不关()
    {
        var app = Make("huorong", AppList.Close, isProtected: true);
        var plan = DecisionEngine.PlanClose([app]);

        Assert.Equal(AppAction.Skip, plan.Single().Action);
        Assert.Equal("受保护", plan.Single().Reason);
    }

    [Fact]
    public void 恢复时只开本次真关掉的()
    {
        var zen = Make("zen", AppList.Close, running: false);
        var weixin = Make("weixin", AppList.Safe, running: false);

        var plan = DecisionEngine.PlanRestore([zen, weixin], new HashSet<string> { "zen" });

        Assert.Equal(AppAction.Start, plan.Single(p => p.App.Id == "zen").Action);
        Assert.Equal(AppAction.Skip, plan.Single(p => p.App.Id == "weixin").Action);
    }

    [Fact]
    public void 恢复时没有启动路径的跳过()
    {
        var wsl = Make("vmmemWSL", AppList.Close, running: false, canRestore: false);
        var plan = DecisionEngine.PlanRestore([wsl], new HashSet<string> { "vmmemWSL" });

        Assert.Equal(AppAction.Skip, plan.Single().Action);
        Assert.Equal("没有启动路径", plan.Single().Reason);
    }

    [Fact]
    public void 恢复时已经跑着的不重复启动()
    {
        var app = Make("zen", AppList.Close, running: true);
        var plan = DecisionEngine.PlanRestore([app], new HashSet<string> { "zen" });

        Assert.Equal(AppAction.Skip, plan.Single().Action);
        Assert.Equal("已经在跑", plan.Single().Reason);
    }

    [Fact]
    public void 完整链路_关闭只包含交集_恢复只包含本次()
    {
        var apps = new[]
        {
            Make("zen",   AppList.Close, running: true),
            Make("mumu",  AppList.Close, running: false),
            Make("weixin",AppList.Safe,  running: true),
            Make("nv",    AppList.None,  running: true),
            Make("huorong", AppList.Close, running: true, isProtected: true),
        };

        var close = DecisionEngine.Effective(DecisionEngine.PlanClose(apps)).ToList();
        Assert.Single(close);
        Assert.Equal("zen", close[0].App.Id);
    }
}
