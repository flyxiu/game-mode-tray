using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameMode.App;

/// <summary>
/// 开机自启。写 HKCU 的 Run 键——只影响当前用户，不需要管理员权限。
/// 不用任务计划：那是给需要提权或延迟启动的场景，这里是托盘小工具，Run 键够用。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameModeTray";

    /// <summary>现在是不是已经设了自启。以注册表为准，不信配置文件。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置或取消自启。返回是否真的改成功了。</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                // 数字外面加引号，路径里有空格也不会被拆开
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[自启] 写注册表失败：{ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// 全局快捷键。用 RegisterHotKey 注册，系统级生效——窗口没在前台也能按。
/// 同一时刻只能有一个，换绑前先注销旧的。
/// </summary>
internal static class Hotkey
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>把 WinForms 的修饰键换成 Win32 的位掩码。</summary>
    public static uint ToModifiers(Keys key)
    {
        uint m = 0;
        if (key.HasFlag(Keys.Control)) m |= MOD_CONTROL;
        if (key.HasFlag(Keys.Alt)) m |= MOD_ALT;
        if (key.HasFlag(Keys.Shift)) m |= MOD_SHIFT;
        return m;
    }

    /// <summary>
    /// 注册。返回 true 表示成功；false 通常是被别的程序占了。
    /// 失败不要抛异常，界面上提示一下就行——为个热键崩掉不值得。
    /// </summary>
    public static bool Register(IntPtr handle, int id, uint modifiers, uint key)
    {
        if (key == 0) return false;

        // Alt 单独按是系统菜单，Win 组合键大多被系统占用，两种都不推荐。
        // 这里不拦，交给 RegisterHotKey 自己判断能不能注册。
        try
        {
            if (!RegisterHotKey(handle, id, modifiers, key))
            {
                Debug.WriteLine($"[热键] 注册失败，错误码 {Marshal.GetLastWin32Error()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[热键] 注册异常：{ex.Message}");
            return false;
        }
    }

    public static void Unregister(IntPtr handle, int id)
    {
        if (id == 0) return;
        try { UnregisterHotKey(handle, id); }
        catch { /* 窗口都销毁了，注销失败无所谓 */ }
    }

    /// <summary>这条消息是不是热键触发的。</summary>
    public static bool IsHotkeyMessage(ref Message m) => m.Msg == WM_HOTKEY;
}

/// <summary>Win32 的虚拟键码或 WinForms 键值转成存放格式。</summary>
internal static class KeyCode
{
    /// <summary>这些键只当修饰键用，不能单独成为热键主体。</summary>
    public static bool IsModifierOnly(Keys key) => key is
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or
        Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or
        Keys.Menu or Keys.LMenu or Keys.RMenu or
        Keys.LWin or Keys.RWin;
}
