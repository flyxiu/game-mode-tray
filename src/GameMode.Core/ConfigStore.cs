using System.Text.Json;

namespace GameMode.Core;

/// <summary>
/// 配置读写。绑定到一个具体目录（通常是 exe 所在目录），
/// 这样程序搬到哪配置跟到哪，不用写注册表也不用 %APPDATA%。
/// </summary>
public sealed class ConfigStore(string directory)
{
    private string Path => System.IO.Path.Combine(directory, "tray-config.json");

    public GameModeConfig Current { get; private set; } = new();

    /// <summary>读配置。文件不存在时生成一份默认的，损坏时用默认值继续。</summary>
    public GameModeConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<GameModeConfig>(json, GameModeConfig.JsonOptions);
                if (cfg is not null) Current = cfg;
            }
            else
            {
                // 第一次运行：落一份默认配置，用户能直接看到文件在哪、长什么样
                Save();
            }
        }
        catch
        {
            // 配置坏了不能阻止程序启动，用默认值继续
        }

        return Current;
    }

    /// <summary>写回。写失败只记日志，不影响内存里的状态。</summary>
    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, GameModeConfig.JsonOptions);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path, overwrite: true);   // 先写临时文件再替换，避免写一半断电
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把一个应用放进指定名单。互斥由这里保证：进一个就出另一个。
    /// 同时记下显示名，否则应用关掉后界面没法显示它叫什么。
    /// </summary>
    public void SetList(string appId, AppList list, string? displayName = null, string? exePath = null)
    {
        var id = appId.ToLowerInvariant();
        Current.Close.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Current.Safe.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        var entry = new AppRef(
            id,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            string.IsNullOrWhiteSpace(exePath) ? null : exePath.ToLowerInvariant());
        switch (list)
        {
            case AppList.Close: Current.Close.Add(entry); break;
            case AppList.Safe: Current.Safe.Add(entry); break;
        }

        Save();
    }

    /// <summary>把一个 exe 登记到托盘区。已存在则忽略。</summary>
    public bool AddTracked(string exePath)
    {
        var normalized = exePath.Trim().ToLowerInvariant();
        if (Current.Tracked.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return false;

        Current.Tracked.Add(normalized);
        Save();
        return true;
    }

    /// <summary>从托盘区移除，同时清掉它在关闭区/安全区的记录。</summary>
    public void RemoveTracked(string exePath)
    {
        var normalized = exePath.Trim().ToLowerInvariant();
        Current.Tracked.RemoveAll(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase));

        string dir = System.IO.Path.GetDirectoryName(normalized) ?? "";
        Current.Close.RemoveAll(x => x.Id.Equals(dir, StringComparison.OrdinalIgnoreCase));
        Current.Safe.RemoveAll(x => x.Id.Equals(dir, StringComparison.OrdinalIgnoreCase));

        Save();
    }

    /// <summary>查询某个应用在哪个名单里。</summary>
    public AppList GetList(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (Current.Close.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return AppList.Close;
        if (Current.Safe.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return AppList.Safe;
        return AppList.None;
    }
}
