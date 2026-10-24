using System.Text.Json;
using GameMode.Core;
using Xunit;

namespace GameMode.Core.Tests;

/// <summary>
/// 名单持久化的护栏。Exe 一旦在读取时丢掉，恢复就只能拿目录去
/// Process.Start，结果只是弹出资源管理器——这三个用例挡住这条退路。
/// </summary>
public class ConfigStoreTests
{
    [Fact]
    public void 旧格式纯字符串仍能读()
    {
        const string json = """
        { "Version": 1, "Close": ["C:\\Apps\\Foo"] }
        """;

        var cfg = JsonSerializer.Deserialize<GameModeConfig>(json, GameModeConfig.JsonOptions)!;

        var entry = Assert.Single(cfg.Close);
        Assert.Equal("c:\\apps\\foo", entry.Id);
        Assert.Null(entry.Name);
        Assert.Null(entry.Exe);
    }

    [Fact]
    public void 新格式读回Exe()
    {
        const string json = """
        { "Version": 1, "Close": [{ "Id": "C:\\Apps\\Foo", "Name": "Foo", "Exe": "C:\\Apps\\Foo\\foo.exe" }] }
        """;

        var cfg = JsonSerializer.Deserialize<GameModeConfig>(json, GameModeConfig.JsonOptions)!;

        var entry = Assert.Single(cfg.Close);
        Assert.Equal("c:\\apps\\foo", entry.Id);
        Assert.Equal("Foo", entry.Name);
        Assert.Equal("C:\\Apps\\Foo\\foo.exe", entry.Exe);
    }

    [Fact]
    public void 加载后再保存Exe不丢()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gamemode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // 模拟用户已有的配置：带 Exe。旧代码 Load 时会把它丢掉，
            // 紧接着任何一次 Save 都会把丢掉的版本写回磁盘。
            File.WriteAllText(Path.Combine(dir, "tray-config.json"), """
            { "Version": 1, "Close": [{ "Id": "c:\\apps\\foo", "Name": "Foo", "Exe": "c:\\apps\\foo\\foo.exe" }] }
            """);

            var store = new ConfigStore(dir);
            store.Load();
            store.Save();

            var entry = Assert.Single(new ConfigStore(dir).Load().Close);
            Assert.Equal(@"c:\apps\foo\foo.exe", entry.Exe);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 存盘再读Exe不丢()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gamemode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var store = new ConfigStore(dir);
            store.Load();
            store.SetList(@"C:\Program Files\WindowsApps\Claude_2.31\app", AppList.Close,
                "Claude", @"C:\Program Files\WindowsApps\Claude_2.31\app\claude.exe");

            var reloaded = new ConfigStore(dir).Load();
            var entry = Assert.Single(reloaded.Close);

            Assert.Equal("c:\\program files\\windowsapps\\claude_2.31\\app", entry.Id);
            Assert.Equal("Claude", entry.Name);
            Assert.Equal(@"c:\program files\windowsapps\claude_2.31\app\claude.exe", entry.Exe);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
