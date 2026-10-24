using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameMode.Core;

/// <summary>
/// 配置。字段名刻意保持简短，因为它就是磁盘上的 JSON。
///
/// 名单里同时存 Id（exe 所在目录，小写）和 Name（记录时的显示名）。
/// 名字必须存：应用关掉后列表里要显示它，而那时候进程已经没了，
/// 没法再从 exe 上读 FileVersionInfo。早先只存路径，结果界面只能从路径
/// 最后一段猜名字，像 ...\WindowsApps\xxx\app 这种会显示成 "app"。
/// </summary>
public sealed class GameModeConfig
{
    /// <summary>schema 版本，将来改结构时用来做迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 保护名单：进程名（不含 .exe），命中即永不关闭。
    /// 默认为空——是否保护由用户在界面上自己决定，
    /// 预置一份"看似安全"的清单反而会让用户以为自己选过。
    /// </summary>
    public List<string> Protect { get; set; } = new();

    /// <summary>
    /// 托盘区：用户手动登记的应用，存 exe 全路径。
    /// 不再自动扫描系统托盘——Win11 的托盘改成 XAML 实现后，
    /// TB_BUTTONCOUNT、溢出面板 UIA、NotifyIconSettings 注册表都读不准，
    /// 与其猜，不如让用户自己指定要管哪些。
    /// </summary>
    public List<string> Tracked { get; set; } = new();

    /// <summary>关闭名单：exe 所在目录（小写）+ 记录时的显示名。</summary>
    public List<AppRef> Close { get; set; } = new();

    /// <summary>安全区：同上。</summary>
    public List<AppRef> Safe { get; set; } = new();

    /// <summary>执行关闭时额外跑的 shell 命令，目前只有 WSL 用。</summary>
    public List<StopCommand> StopCommands { get; set; } = new();

    /// <summary>
    /// 全局快捷键，按一下把界面叫到前面。默认未绑定。
    /// 存的是 Win32 的虚拟键码与修饰键位，不是字符串——
    /// 字符串要解析，解析错就是热键静默失效。
    /// </summary>
    public HotkeySetting Hotkey { get; set; } = new();

    /// <summary>开机自启。改了会同步写注册表的 Run 键。</summary>
    public bool AutoStart { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new AppRefConverter() },
    };
}

/// <summary>全局快捷键。两个都为 0 表示未绑定。</summary>
public sealed class HotkeySetting
{
    /// <summary>修饰键，Win32 的 MOD_* 位掩码：1=Alt 2=Ctrl 4=Shift 8=Win。</summary>
    public uint Modifiers { get; set; }

    /// <summary>虚拟键码，比如 F9=120。0 表示未绑定。</summary>
    public uint Key { get; set; }

    /// <summary>计算属性，不该写进 JSON——写进去将来会被当成存储字段读回来。</summary>
    [JsonIgnore]
    public bool IsBound => Key != 0;

    /// <summary>给人看的写法，比如 Ctrl+Alt+G。</summary>
    public string Describe()
    {
        if (!IsBound) return "未绑定";

        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 8) != 0) parts.Add("Win");

        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",          // F1~F24
        >= 0x30 and <= 0x39 => ((char)key).ToString(),     // 0~9
        >= 0x41 and <= 0x5A => ((char)key).ToString(),     // A~Z
        0x20 => "空格",
        0x0D => "回车",
        0x1B => "Esc",
        0x09 => "Tab",
        _ => $"#{key}",
    };
}

public sealed class StopCommand
{
    /// <summary>进程名（不含 .exe），命中时才执行命令。</summary>
    public required string Process { get; set; }

    /// <summary>要执行的命令，比如 "wsl.exe --shutdown"。</summary>
    public required string Command { get; set; }
}

/// <summary>
/// 名单里的一条记录：路径 + 记录时的显示名。
/// 名字只是给人看的快照，应用升级改名了也不影响匹配（匹配只看 Id）。
/// </summary>
public sealed class AppRef
{
    /// <summary>exe 所在目录，小写。匹配用这个。</summary>
    public string Id { get; set; } = "";

    /// <summary>记录时的显示名。为 null 时界面退回从路径猜。</summary>
    public string? Name { get; set; }

    /// <summary>
    /// 启动用的 exe 全路径。
    /// Id 是目录（匹配用），直接拿目录去 Process.Start 只会弹出资源管理器——
    /// 早先恢复失败就是这个原因。老记录没有这个字段，恢复时从目录里找。
    /// </summary>
    public string? Exe { get; set; }

    public AppRef() { }

    public AppRef(string id, string? name = null, string? exe = null)
    {
        Id = id;
        Name = name;
        Exe = exe;
    }
}

/// <summary>
/// 让名单既能读旧格式（纯字符串数组），也能读新格式（对象数组）。
/// 旧配置文件是 ["c:\\path"]，新的是 [{"Id":"...","Name":"..."}]，
/// 两种都要能读，否则用户升级一次就丢配置。
/// </summary>
public sealed class AppRefConverter : JsonConverter<AppRef>
{
    public override AppRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string id = reader.GetString() ?? "";
            return new AppRef(id.ToLowerInvariant());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            string id = root.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? "" : "";
            string? name = root.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;

            // Exe 必须一起读回来。漏了这一步，写盘时明明有 Exe，
            // 下次加载却变成 null，恢复时又退回拿目录去 Process.Start，
            // 于是又变成"只弹出资源管理器"。
            string? exe = root.TryGetProperty("Exe", out var exeProp) ? exeProp.GetString() : null;

            return new AppRef(id.ToLowerInvariant(), name,
                string.IsNullOrWhiteSpace(exe) ? null : exe);
        }

        throw new JsonException($"名单项格式不认识：{reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, AppRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        if (!string.IsNullOrEmpty(value.Name)) writer.WriteString("Name", value.Name);
        if (!string.IsNullOrEmpty(value.Exe)) writer.WriteString("Exe", value.Exe);
        writer.WriteEndObject();
    }
}
