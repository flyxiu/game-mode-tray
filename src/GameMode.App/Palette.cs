using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GameMode.App;

/// <summary>视觉参数，和 design/ui.html 里那套令牌一一对应。</summary>
internal static class Palette
{
    // 文字
    public static readonly Color Text = Color.FromArgb(240, 240, 244);
    public static readonly Color TextDim = Color.FromArgb(150, 150, 160);
    public static readonly Color TextFaint = Color.FromArgb(105, 105, 115);

    // 强调：嘉然粉 → 紫
    public static readonly Color AccentA = Color.FromArgb(244, 154, 193);
    public static readonly Color AccentB = Color.FromArgb(168, 130, 255);

    // 状态
    public static readonly Color Running = Color.FromArgb(120, 214, 160);
    public static readonly Color Off = Color.FromArgb(70, 72, 80);
    public static readonly Color Track = Color.FromArgb(58, 61, 68);
    public static readonly Color Warn = Color.FromArgb(245, 180, 90);
    public static readonly Color Danger = Color.FromArgb(240, 110, 110);

    // 面
    public static readonly Color WindowBg = Color.FromArgb(18, 19, 24);
    public static readonly Color BinBg = Color.FromArgb(38, 40, 46);
    public static readonly Color TileHover = Color.FromArgb(255, 255, 255);
    public static readonly Color SafeBg = Color.FromArgb(120, 214, 160);

    // 布局：所有左右边距都从 Margin 派生，避免按钮和分区各自算一套造成错位
    public const int WindowW = 500;
    public const int WindowH = 520;
    public const int Margin = 16;
    public const int ColGap = 10;

    public const int Tile = 46;
    public const int TileGap = 6;
    public const int RadiusWindow = 14;
    public const int RadiusBin = 12;
    public const int RadiusTile = 10;

    private static readonly Dictionary<(int, FontStyle), Font> Fonts = new();
    private static readonly Dictionary<int, SolidBrush> Brushes = new();
    private static readonly Dictionary<int, Pen> Pens = new();

    /// <summary>
    /// 字体与画刷缓存。两类对象都持有 GDI 句柄，必须缓存复用——
    /// 每次 Paint 都 new 会在几秒内耗光句柄池，表现为界面卡死。
    ///
    /// size 是像素，不是磅。用磅在 150% 缩放下会得到 13.5px 这种半像素字号，
    /// GDI 只能取整对齐，小字就发虚。
    /// </summary>
    public static Font F(int pixelSize, FontStyle style = FontStyle.Regular)
    {
        var key = (pixelSize, style);
        if (Fonts.TryGetValue(key, out var f)) return f;

        try { f = new Font("Segoe UI Variable Display", pixelSize, style, GraphicsUnit.Pixel); }
        catch { f = new Font("Segoe UI", pixelSize, style, GraphicsUnit.Pixel); }

        Fonts[key] = f;
        return f;
    }

    public static SolidBrush B(Color c)
    {
        int key = c.ToArgb();
        if (Brushes.TryGetValue(key, out var b)) return b;
        b = new SolidBrush(c);
        Brushes[key] = b;
        return b;
    }

    public static Pen P(Color c, float width = 1f)
    {
        int key = HashCode.Combine(c.ToArgb(), width);
        if (Pens.TryGetValue(key, out var p)) return p;
        p = new Pen(c, width);
        Pens[key] = p;
        return p;
    }

    /// <summary>圆角矩形路径。调用方负责 Dispose。</summary>
    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>窗口效果相关的 Win32 调用。</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentEnableBlurBehind = 3;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    /// <summary>
    /// 磨砂玻璃。系统不支持 Acrylic 时退到普通模糊，再不行就当纯色窗口。
    /// 参数是 ABGR 顺序，这点和 RGB 直觉相反，写错会得到错误的色调。
    /// </summary>
    public static void EnableAcrylic(IntPtr hwnd, byte alpha, Color tint)
    {
        int gradient = (alpha << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

        foreach (var state in new[] { AccentEnableAcrylicBlurBehind, AccentEnableBlurBehind })
        {
            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,
                GradientColor = gradient,
            };

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };

            IntPtr buffer = Marshal.AllocHGlobal(data.SizeOfData);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                data.Data = buffer;
                if (SetWindowCompositionAttribute(hwnd, ref data) != 0) return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Win11 的圆角。Win10 上调用会失败，忽略即可。</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int pref = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }

    /// <summary>无边框窗口要让阴影正常显示，需要把边距扩出去。</summary>
    public static void ExtendFrame(IntPtr hwnd)
    {
        var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    public static void DragWindow(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, HtCaption, 0);
    }
}
